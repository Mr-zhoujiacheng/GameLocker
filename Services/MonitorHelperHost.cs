using Microsoft.Win32.SafeHandles;
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

        using var initializationCancellation = new CancellationTokenSource();
        var initializationToken = initializationCancellation.Token;
        Task? cpuInitialization = null;
        Task? gpuInitialization = null;
        LosslessOutputListener? output = null;
        PresentEtwListener? etw = null;
        var framesRequested = false;
        long nextTemperatureAt = 0;

        // 先创建线程消息队列，否则看门狗的 PostThreadMessage 可能失败
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        var threadId = GetCurrentThreadId();

        var watchdog = new Thread(() => Watch(parentPid, stop, threadId))
        {
            IsBackground = true,
            Name = "GameLocker.Monitor.HelperWatchdog"
        };
        watchdog.Start();

        var interval = 500u;
        var timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, interval, IntPtr.Zero);
        // 主进程每秒消费一次，助手 500ms 发布一次已足够；温度再降一半频率并缓存上次读数
        var cpuTemp = double.NaN;
        var gpuTemp = double.NaN;
        var tick = 0;
        try
        {
            if (timer == UIntPtr.Zero) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_QUIT) break;
                if (msg.message != WM_TIMER || unchecked((ulong)msg.wParam.ToInt64()) != timer.ToUInt64()) continue;
                tick++;
                var sampling = shared.ReadSampling();
                var panelActive = shared.HasPanelDemand;
                var requestedInterval = sampling == 0 && !panelActive ? 2500u : 500u;
                if (interval != requestedInterval)
                {
                    if (SetTimer(IntPtr.Zero, timer, requestedInterval, IntPtr.Zero) != UIntPtr.Zero)
                        interval = requestedInterval;
                }
                var trackFrames = sampling.HasFlag(MonitorShared.Sampling.Frames);
                var readCpu = panelActive || sampling.HasFlag(MonitorShared.Sampling.CpuTemperature);
                var readGpu = sampling.HasFlag(MonitorShared.Sampling.GpuTemperature);
                var background = !panelActive && sampling.HasFlag(MonitorShared.Sampling.Background);
                if (readCpu && cpuInitialization is null)
                    cpuInitialization = Task.Run(() =>
                    {
                        var ready = PawnIoMsr.EnsureReady(installIfMissing: true, initializationToken);
                        Log($"pawnio={(ready ? "ok" : PawnIoMsr.Status)}");
                    });
                if (readGpu && gpuInitialization is null)
                    gpuInitialization = Task.Run(() =>
                    {
                        initializationToken.ThrowIfCancellationRequested();
                        var ready = MsiWmiProbe.EnsureReady();
                        Log($"msi-wmi={(ready ? "ok：" + MsiWmiProbe.MethodList : MsiWmiProbe.Status)}");
                        WriteVendorStatus(ready);
                    });
                if (framesRequested != trackFrames)
                {
                    framesRequested = trackFrames;
                    if (trackFrames) etw = StartEtw();
                    else
                    {
                        etw?.Dispose();
                        output?.Dispose();
                        etw = null;
                        output = null;
                    }
                }
                double fps = 0, frameMs = 0;
                double[] history = [];
                var targetPid = trackFrames ? shared.ReadTargetPid() : 0;
                var outputPid = trackFrames ? shared.ReadOutputTargetPid() : 0;
                if (outputPid > 0 && output is null)
                {
                    output = new LosslessOutputListener();
                    if (!output.Start()) Log("显示帧采集失败：" + output.LastError);
                }
                output?.SetTargets(targetPid, outputPid);
                if (outputPid > 0 && tick % 10 == 0)
                    Log($"[显示帧采集] game={targetPid} output={outputPid} fps={output?.GetFps(outputPid):0.##} {output?.Diagnostics}");
                if (etw is not null)
                {
                    etw.TargetPid = targetPid;
                    (fps, frameMs, history) = etw.GetStats();
                }
                var now = Environment.TickCount64;
                if (now >= nextTemperatureAt || (!background && nextTemperatureAt - now > 1000))
                {
                    nextTemperatureAt = now + (background ? 5000 : 1000);
                    cpuTemp = readCpu && cpuInitialization?.IsCompletedSuccessfully == true && PawnIoMsr.Available
                        ? PawnIoMsr.ReadTemperature() : double.NaN;
                    // MSI 固件参考温度，不能当作已确认的独立 GT 温度。
                    gpuTemp = readGpu && gpuInitialization?.IsCompletedSuccessfully == true
                        ? MsiWmiProbe.ReadGpuTemperature() : double.NaN;
                }
                shared.Publish(fps, frameMs, history, readCpu ? cpuTemp : double.NaN,
                    readGpu ? gpuTemp : double.NaN, targetPid,
                    etw?.GetOriginalFps() ?? double.NaN,
                    (etw?.FrameGenerationDetected ?? false) || (output?.HasGeneratedFrames(targetPid) ?? false),
                    outputPid, output?.GetFps(outputPid) ?? double.NaN,
                    output?.GetApplicationFps(outputPid) ?? double.NaN);
            }
        }
        finally
        {
            if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);
            etw?.Dispose();
            output?.Dispose();
            initializationCancellation.Cancel();
            var initialization = Task.WhenAll(cpuInitialization ?? Task.CompletedTask, gpuInitialization ?? Task.CompletedTask);
            try { initialization.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
            catch (TimeoutException) { Log("传感器初始化取消等待超时，进程退出时由系统释放设备句柄"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("传感器初始化结束异常：" + ex.Message); }
            if (cpuInitialization?.IsCompleted == true) PawnIoMsr.Close();
            stop?.Dispose();
            Log("exit");
        }
    }

    private static PresentEtwListener? StartEtw()
    {
        var etw = new PresentEtwListener();
        try
        {
            if (etw.Start()) return etw;
            Log($"ETW 启动失败 err={etw.StartError} {etw.StartDiagnostic}");
        }
        catch (Exception ex) { Log("ETW 异常：" + ex.Message); }
        etw.Dispose();
        return null;
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
        try
        {
            var handle = OpenProcess(0x00100000, false, parentPid); // SYNCHRONIZE
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return;
            }
            using var parent = new EventWaitHandle(false, EventResetMode.ManualReset);
            parent.SafeWaitHandle.Dispose();
            parent.SafeWaitHandle = handle;
            if (stop is null) parent.WaitOne();
            else WaitHandle.WaitAny(new WaitHandle[] { parent, stop });
        }
        catch (Exception ex) { Log("父进程等待失败：" + ex.Message); }
        finally { PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
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
    private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
