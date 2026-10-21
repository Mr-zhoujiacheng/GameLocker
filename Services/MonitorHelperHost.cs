using System.Diagnostics;
using System.Runtime.InteropServices;

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

        using var etw = new PresentEtwListener();
        if (!etw.Start())
        {
            Log($"etw 启动失败 err={etw.StartError} {etw.StartDiagnostic}，退出");
            stop?.Dispose();
            return;
        }
        Log("etw=ok");

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
                etw.TargetPid = shared.ReadTargetPid();
                var (fps, frameMs, history) = etw.GetStats();
                shared.Publish(fps, frameMs, history);
            }
        }
        finally
        {
            KillTimer(IntPtr.Zero, new UIntPtr(TimerId));
            etw.Dispose();
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
