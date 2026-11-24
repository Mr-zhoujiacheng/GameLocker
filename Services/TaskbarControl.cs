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
    private static bool _watchdogActive;

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
        mode = mode == 2 ? 2 : 0;
        lock (Gate)
        {
            if (mode == _applied) return;
        }

        try
        {
            var tray = FindWindow(TrayClass, null);
            if (tray == IntPtr.Zero)
            {
                lock (Gate) _applied = mode;
                return;
            }

            lock (Gate) CaptureOriginal(tray);

            if (mode == 2)
            {
                // 自动隐藏态让资源管理器自己把工作区扩到整屏；再隐藏任务栏窗口。
                // 不再反复 SPI_SETWORKAREA，避免与 Shell 互相覆盖导致最大化窗口跳动。
                SetAutoHide(tray, true);
                HideTaskbars();
                lock (Gate)
                {
                    _hidden = true;
                    _applied = 2;
                }
                StartWatchdog();
                DebugLog.Write("[任务栏] 已彻底隐藏");
            }
            else
            {
                StopWatchdog();
                SetAutoHide(tray, (_originalState & ABS_AUTOHIDE) != 0);
                ShowTaskbars();
                lock (Gate)
                {
                    _hidden = false;
                    _applied = 0;
                }
                DebugLog.Write("[任务栏] 已恢复显示");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 任务栏控制失败：" + ex.Message);
        }
    }

    /// <summary>退出前务必调用：恢复任务栏显示状态并停止看门狗。</summary>
    public static void Shutdown()
    {
        try
        {
            if (IsHidden)
            {
                var tray = FindWindow(TrayClass, null);
                StopWatchdog();
                if (tray != IntPtr.Zero)
                {
                    SetAutoHide(tray, (_originalState & ABS_AUTOHIDE) != 0);
                    ShowTaskbars();
                }
            }
        }
        catch { }

        StopWatchdog();
        lock (Gate)
        {
            _applied = -1;
            _hidden = false;
            _originalCaptured = false;
        }
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private static void CaptureOriginal(IntPtr tray)
    {
        if (_originalCaptured) return;
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        _originalState = (int)SHAppBarMessage(ABM_GETSTATE, ref data);
        _originalCaptured = true;
    }

    private static void StartWatchdog()
    {
        lock (Gate)
        {
            _watchdogActive = true;
        }
        _watchdog ??= new Timer(_ => WatchdogTick(), null, 100, 100);
    }

    private static void StopWatchdog()
    {
        lock (Gate)
        {
            _watchdogActive = false;
        }
    }

    /// <summary>看门狗：任务栏被任何外部操作重新显示时立刻隐藏。</summary>
    private static void WatchdogTick()
    {
        lock (Gate)
        {
            if (!_watchdogActive || !_hidden) return;
        }

        try
        {
            HideTaskbars();
        }
        catch { }
    }

    private static void HideTaskbars()
    {
        EnumWindows((hwnd, _) =>
        {
            var cls = ClassName(hwnd);
            if (cls is not (TrayClass or SecondaryTrayClass)) return true;
            if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_HIDE);
            return true;
        }, IntPtr.Zero);
    }

    private static void ShowTaskbars()
    {
        EnumWindows((hwnd, _) =>
        {
            var cls = ClassName(hwnd);
            if (cls is TrayClass or SecondaryTrayClass)
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            return true;
        }, IntPtr.Zero);
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(128);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    private static void SetAutoHide(IntPtr tray, bool enabled)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = tray,
            lParam = enabled ? ABS_AUTOHIDE : ABS_ALWAYSONTOP
        };
        SHAppBarMessage(ABM_SETSTATE, ref data);
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
