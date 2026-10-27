using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// 接管系统屏幕键盘：关闭 Win11 触屏键盘的自动弹出，改用 GameLocker 自己的键盘（手柄热键 / 屏幕键盘小组件呼出）；
/// 独立进程的 osk.exe / TabTip 弹出时直接收起。
/// 注意：Win11 触屏键盘与输入法候选由 TextInputHost 的同一个全屏浮层（"Windows 输入体验"）承载，
/// 隐藏或关闭该窗口会连带打断输入法，所以这里不碰它，只改它的自动弹出设置。
/// </summary>
internal static class SystemKeyboardWatcher
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    /// <summary>触屏键盘设置：未连接键盘时是否自动弹出（Windows 设置 → 时间和语言 → 输入 → 触摸键盘）</summary>
    private const string TouchKeyboardKeyPath = @"Software\Microsoft\TabletTip\1.7";
    private const string AutoInvokeValue = "EnableDesktopModeAutoInvoke";

    private static WinEventDelegate? _callback; // 防止回调被 GC
    private static IntPtr _hook;
    private static long _lastHandled;           // 防抖：系统键盘连续重试弹出时不要连点

    /// <summary>接管是否已开启</summary>
    public static bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>按设置开/关接管</summary>
    public static void Apply(bool enabled)
    {
        SetAutoInvoke(!enabled);
        if (enabled) Start();
        else Stop();
    }

    /// <summary>
    /// 改注册表开/关触屏键盘自动弹出（关闭接管时恢复系统默认：自动弹出）。
    /// 没有权限时静默失败：接管退化为只收起 osk/TabTip，不影响其他功能。
    /// </summary>
    private static void SetAutoInvoke(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TouchKeyboardKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(TouchKeyboardKeyPath);
            key?.SetValue(AutoInvokeValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // 忽略：受限账户/策略不允许写时，自动弹出关不掉，但不会出错崩溃
        }
    }

    private static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _callback ??= OnShow;
        // 回调投递到安装钩子的线程（UI 线程）的消息队列，窗口操作在 UI 线程执行
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private static void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0) return;

        var now = Environment.TickCount64;
        if (now - _lastHandled < 400) return;
        if (!IsLegacyKeyboard(hwnd)) return;

        _lastHandled = now;
        // osk / TabTip 是独立进程的键盘窗口，不响应 WM_CLOSE，要发 SC_CLOSE 才会真正收起
        PostMessage(hwnd, WM_SYSCOMMAND, SC_CLOSE, IntPtr.Zero);
        OnScreenKeyboard.ShowLast();
    }

    /// <summary>独立键盘窗口：osk.exe 的 OSKMainClass、旧版触屏键盘 TabTip 的 IPTip_Main_Window</summary>
    private static bool IsLegacyKeyboard(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        var n = GetClassName(hwnd, sb, sb.Capacity);
        var cls = n > 0 ? sb.ToString(0, n) : "";
        return cls is "OSKMainClass" or "IPTip_Main_Window";
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
