using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 托盘常驻 + 全局热键（默认 Ctrl+Alt+G），纯 Win32 实现，运行在独立线程消息循环上。
/// 事件在消息线程触发，订阅方需自行 marshal 回 UI 线程。
/// </summary>
public sealed class TrayHotkeyService : IDisposable
{
    // ---- Win32 常量 ----
    private const int WM_USER = 0x0400;
    private const int WM_HOTKEY = 0x0400;
    private const int TrayCallback = WM_USER + 1; // 托盘回调消息
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
    private const uint VK_G = 0x47;
    private const int HOTKEY_ID = 0x1;
    private const int IDM_SHOW = 0x2001, IDM_EXIT = 0x2002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int NIM_ADD = 0x00, NIM_DELETE = 0x0002, NIM_MODIFY = 0x01;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04;
    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int SM_CXSMICON = 49;
    private const int LR_SHARED = 0x00008000;

    private readonly Thread _thread;
    private IntPtr _hwnd;
    private volatile bool _disposed;
    private readonly WndProcDelegate _wndProc; // 防止被 GC 回收
    private NOTIFYICONDATA _nid;

    /// <summary>热键被按下 / 托盘双击 / 选择"显示启动器"</summary>
    public event Action? ShowRequested;
    /// <summary>托盘菜单选择退出</summary>
    public event Action? ExitRequested;

    public TrayHotkeyService()
    {
        _wndProc = WndProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "TrayHotkey" };
        _thread.Start();
    }

    private void Run()
    {
        var hInstance = GetModuleHandle(null);
        var className = "GameLockerTrayWnd";

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className,
            hIcon = IconLoader.Large,
            hIconSm = IconLoader.Small
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, className, "Game Locker", 0,
            0, 0, 0, 0, 0, 0, hInstance, 0);

        var ok = RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_G);

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_USER + 1,
            hIcon = IconLoader.Small,
            szTip = "Game Locker（Ctrl+Alt+G 唤起）"
        };
        Shell_NotifyIcon(NIM_ADD, ref _nid);

        // 消息循环
        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                if (wParam.ToInt32() == HOTKEY_ID) ShowRequested?.Invoke();
                return 0;

            case WM_USER + 1: // 托盘回调
                var mouseMsg = (uint)(long)lParam;
                if (mouseMsg == WM_LBUTTONDBLCLK) ShowRequested?.Invoke();
                else if (mouseMsg == WM_RBUTTONUP) ShowTrayMenu();
                return 0;

            case WM_COMMAND:
                var id = (wParam.ToInt32() & 0xFFFF);
                if (id == IDM_SHOW) ShowRequested?.Invoke();
                else if (id == IDM_EXIT) ExitRequested?.Invoke();
                return 0;

            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        var hMenu = CreatePopupMenu();
        AppendMenu(hMenu, MF_STRING, IDM_SHOW, "显示启动器");
        AppendMenu(hMenu, MF_SEPARATOR, 0, null);
        AppendMenu(hMenu, MF_STRING, IDM_EXIT, "退出");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(hMenu);

        if (cmd == IDM_SHOW) ShowRequested?.Invoke();
        else if (cmd == IDM_EXIT) ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            PostMessage(_hwnd, WM_CLOSE, 0, 0);
        }
    }

    // ---- P/Invoke 声明 ----
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("shell32.dll")]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(int hInstance, int lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
}
