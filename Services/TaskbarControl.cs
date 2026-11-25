using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 桌面任务栏控制：开启「任务栏隐藏」后彻底隐藏系统任务栏（主屏 + 副屏）。
/// 通过把任务栏切到“自动隐藏”状态让系统自己释放工作区（避免反复强写工作区造成窗口跳动），
/// 再用后台看门狗持续压制任务栏：Win 键、下边缘、Explorer 重启等
/// 任何把任务栏重新显示出来的路径都会被立即收回，不提供临时唤出入口。
/// 关闭开关或退出程序时恢复任务栏原来的显示状态。
/// </summary>
internal static class TaskbarControl
{
    private const string TrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    private static int _applied = -1;
    private static bool _originalCaptured;
    private static int _originalState;
    private static bool _hidden;

    private static readonly object Gate = new();
    private static Timer? _watchdog;
    private static int _requestedMode;
    private static bool _shellHooked;

    /// <summary>任务栏当前是否由本程序彻底隐藏（供启动器其它逻辑避让）。</summary>
    public static bool IsHidden
    {
        get { lock (Gate) return _hidden; }
    }

    /// <summary>
    /// 按开关应用：2 = 彻底隐藏，0 = 恢复。旧的 1（自动隐藏）已废弃，按 0 处理。
    /// </summary>
    public static void Apply(int mode)
    {
        lock (Gate)
        {
            _requestedMode = mode == 2 ? 2 : 0;
            if (_requestedMode == 0 && !_originalCaptured)
                return;

            if (!_shellHooked)
            {
                ShellRestartWatcher.TaskbarRecreated += OnTaskbarRecreated;
                ShellRestartWatcher.Start();
                _shellHooked = true;
            }
            ApplyRequestedMode();
        }
    }

    // 调用者持有 Gate，避免 Explorer 的重试覆盖较新的设置或在退出后重新隐藏。
    private static void ApplyRequestedMode()
    {
        if (_requestedMode == _applied) return;
        _applied = -1;
        _hidden = false;
        if (_requestedMode == 0 && !_originalCaptured) return;

        try
        {
            var tray = FindWindow(TrayClass, null);
            if (tray == IntPtr.Zero)
            {
                DebugLog.Write("[任务栏] Explorer 尚未就绪，等待重启广播后重试");
                return;
            }

            if (_requestedMode == 2)
            {
                CaptureOriginal(tray);
                if (!SetState(tray, ABS_AUTOHIDE) || !HideTaskbars() || IsWindowVisible(tray)) return;
                _watchdog ??= new Timer(_ => WatchdogTick(), null, 100, 100);
                _hidden = true;
            }
            else
            {
                if (!SetState(tray, _originalState) || !ShowTaskbars()) return;
                _originalCaptured = false;
            }
            _applied = _requestedMode;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 任务栏控制失败：" + ex.Message);
        }
    }

    private static void OnTaskbarRecreated()
    {
        lock (Gate)
        {
            if (!_shellHooked) return;
            _applied = -1;
            ApplyRequestedMode();
        }
    }

    /// <summary>退出前务必调用：恢复任务栏显示状态并停止看门狗。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _requestedMode = 0;
            ApplyRequestedMode();
            if (_shellHooked)
            {
                ShellRestartWatcher.TaskbarRecreated -= OnTaskbarRecreated;
                _shellHooked = false;
            }
            _watchdog?.Dispose();
            _watchdog = null;
        }
    }

    private static void CaptureOriginal(IntPtr tray)
    {
        if (_originalCaptured) return;
        _originalState = ReadState(tray);
        _originalCaptured = true;
    }

    private static void WatchdogTick()
    {
        lock (Gate)
        {
            if (_requestedMode != 2 || !_hidden) return;
            try { HideTaskbars(); }
            catch (Exception ex) { DebugLog.Write("[任务栏] 隐藏检查失败：" + ex.Message); }
        }
    }

    private static bool HideTaskbars() => EnumWindows((hwnd, _) =>
    {
        var cls = ClassName(hwnd);
        if (cls is not (TrayClass or SecondaryTrayClass)) return true;
        if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_HIDE);
        return !IsWindowVisible(hwnd);
    }, IntPtr.Zero);

    private static bool ShowTaskbars() => EnumWindows((hwnd, _) =>
    {
        var cls = ClassName(hwnd);
        if (cls is TrayClass or SecondaryTrayClass)
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        return true;
    }, IntPtr.Zero);

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(128);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    private static int ReadState(IntPtr tray)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    private static bool SetState(IntPtr tray, int state)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = tray,
            lParam = (state & ABS_AUTOHIDE) != 0 ? ABS_AUTOHIDE : ABS_ALWAYSONTOP
        };
        return SHAppBarMessage(ABM_SETSTATE, ref data) != IntPtr.Zero
            && (ReadState(tray) & ABS_AUTOHIDE) == (state & ABS_AUTOHIDE)
            && FindWindow(TrayClass, null) == tray;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public AppBarHost.RECT rc;
        public IntPtr lParam;
    }

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_SETSTATE = 0x0000000A;
    private const int ABS_AUTOHIDE = 0x0000001;
    private const int ABS_ALWAYSONTOP = 0x0000002;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
