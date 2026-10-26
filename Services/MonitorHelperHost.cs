using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 提权监控助手（GameLocker.exe --monitor-helper &lt;父进程PID&gt;）。
/// 帧率只能在管理员权限下用 ETW 采集，主进程不提权时以 runas 拉起本助手：
/// 助手在独立进程运行 ETW，把数据写入共享内存；主进程退出或发出停止事件后自动结束。
/// 不创建任何窗口，仅在主线程跑一个 SetTimer 消息泵。
/// </summary>
internal static class MonitorHelperHost
{
    private const int WM_QUIT = 0x0012;
    private const int WM_TIMER = 0x0113;
    private const uint PM_NOREMOVE = 0x0000;
    private const int TimerId = 1;

    /// <summary>助手入口：成功返回时表示已收到退出请求，调用方应终止进程</summary>
    public static void Run(int parentPid)
    {
        Log($"start parent={parentPid}");

        using var shared = MonitorShared.Open();
        if (shared is null)
        {
            Log("shared=null，退出");
            return;
        }
        Log("shared=ok");

        EventWaitHandle? stop = null;
        try { stop = EventWaitHandle.OpenExisting(MonitorShared.StopEventName); }
        catch { }
        Log($"stopEvent={(stop is null ? "缺失" : "ok")}");

        // CPU 封装温度：PawnIO/MSR（首次会静默安装随包附带的驱动，失败则保持不可用）
        // GPU 温度：厂商 MSI ACPI WMI（Get_Thermal[3]），由下面的定时器每 200ms 取一次（内部缓存 2 秒）
        // 放到后台任务里做，避免驱动安装耗时拖住 ETW 启动与采样
        _ = Task.Run(() =>
        {
            var ready = PawnIoMsr.EnsureReady(installIfMissing: true);
            Log($"pawnio={(ready ? "ok" : PawnIoMsr.Status)}");

            var wmiReady = MsiWmiProbe.EnsureReady();
            Log($"msi-wmi={(wmiReady ? "ok：" + MsiWmiProbe.MethodList : MsiWmiProbe.Status)}");

            WriteVendorStatus(wmiReady);
        });

        PresentEtwListener? etw = null;
        try
        {
            etw = new PresentEtwListener();
            if (etw.Start()) Log("etw=ok");
            else
            {
                Log($"etw 启动失败 err={etw.StartError} {etw.StartDiagnostic}（继续只读温度）");
                etw.Dispose();
                etw = null;
            }
        }
        catch (Exception ex)
        {
            Log("etw 异常：" + ex.Message);
            etw?.Dispose();
            etw = null;
        }

        // 先创建线程消息队列，否则看门狗的 PostThreadMessage 可能失败
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        var threadId = GetCurrentThreadId();

        var watchdog = new Thread(() => Watch(parentPid, stop, threadId))
        {
            IsBackground = true,
            Name = "GameLocker.Monitor.HelperWatchdog"
        };
        watchdog.Start();

        SetTimer(IntPtr.Zero, new UIntPtr(TimerId), 200, IntPtr.Zero);
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_QUIT) break;
                if (msg.message != WM_TIMER) continue;
                double fps = 0, frameMs = 0;
                double[] history = [];
                if (etw is not null)
                {
                    etw.TargetPid = shared.ReadTargetPid();
                    (fps, frameMs, history) = etw.GetStats();
                }
                var cpuTemp = PawnIoMsr.Available ? PawnIoMsr.ReadTemperature() : double.NaN;
                // 厂商 WMI 的 GPU 温度（内部缓存 2 秒，不会每次 200ms 都调固件）
                var gpuTemp = MsiWmiProbe.ReadGpuTemperature();
                shared.Publish(fps, frameMs, history, cpuTemp, gpuTemp);
            }
        }
        finally
        {
            KillTimer(IntPtr.Zero, new UIntPtr(TimerId));
            etw?.Dispose();
            PawnIoMsr.Close();
            stop?.Dispose();
            Log("exit");
        }
    }

    /// <summary>助手诊断日志（%APPDATA%\GameLocker\monitor-helper.log）</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "monitor-helper.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");

    /// <summary>
    /// 厂商传感器采集状态（%APPDATA%\GameLocker\monitor-vendor.txt），导出日志时附在「厂商传感器」段开头，
    /// 这样即使采集失败，用户导出的日志也能说明失败原因。
    /// </summary>
    private static void WriteVendorStatus(bool wmiReady)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine($"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"MSI WMI：{(wmiReady ? "就绪（" + MsiWmiProbe.MethodList + "）" : MsiWmiProbe.Status)}");
            if (wmiReady)
            {
                // 首轮结果直接写进状态文件：方法调用失败时这里会带出确切原因
                var probe = MsiWmiProbe.Capture();
                if (probe.Length > 0)
                    sb.AppendLine("MSI WMI 首轮：" + (probe.Length > 220 ? probe[..220] + "…" : probe));
                else if (MsiWmiProbe.LastError.Length > 0)
                    sb.AppendLine("MSI WMI 首轮失败：" + MsiWmiProbe.LastError);
                // Get_Thermal[3] 第 8 字节（浮层 GPU 温度的来源）
                sb.AppendLine($"MSI WMI GPU 温度：{Num(MsiWmiProbe.ReadGpuTemperature(), "0")} ℃");
            }
            File.WriteAllText(Path.Combine(Dir, "monitor-vendor.txt"), sb.ToString());
        }
        catch { }
    }

    private static string Num(double value, string format) =>
        double.IsNaN(value) ? "—" : value.ToString(format);

    /// <summary>看门狗：父进程消失或主进程发停止事件时，给助手线程投递 WM_QUIT</summary>
    private static void Watch(int parentPid, EventWaitHandle? stop, uint threadId)
    {
        while (ParentAlive(parentPid))
        {
            if (stop is null)
            {
                Thread.Sleep(500);
                continue;
            }
            if (stop.WaitOne(500)) break;
        }
        PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool ParentAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
