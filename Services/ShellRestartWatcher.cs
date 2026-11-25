using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 监听 Explorer 重启（"TaskbarCreated" 广播）。
/// 注册在 AppBar 上的工具栏会在 explorer 重启后失效、占位丢失，
/// 必须收到广播后重新注册，否则工作区会留下 104px 空白或占位消失。
/// 用独立消息窗口接收广播，避免依赖 WinUI 窗口的 WndProc。
/// </summary>
internal static class ShellRestartWatcher
{
    /// <summary>Explorer 重启完成；在后台线程触发，订阅方需自行切回 UI 线程。</summary>
    public static event Action? TaskbarRecreated;

    private static readonly object Gate = new();
    private static bool _started;
    private static uint _taskbarCreatedMessage;

    public static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }

        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ShellRestartWatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run()
    {
        var instance = GetModuleHandle(null);
        var className = "GameLockerShellRestartWnd";

        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className
        };
        RegisterClassEx(ref wc);

        _taskbarCreatedMessage = RegisterTaskbarCreatedMessage();
        var hwnd = CreateWindowEx(0, className, "GameLockerShellRestart", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;
        AllowTaskbarCreatedMessage(hwnd, _taskbarCreatedMessage);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static WndProcDelegate? _wndProc;

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != 0 && msg == _taskbarCreatedMessage)
        {
            try { TaskbarRecreated?.Invoke(); }
            catch (Exception ex) { DebugLog.Write("[Shell] 重启回调失败：" + ex.Message); }
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    internal static uint RegisterTaskbarCreatedMessage() => RegisterWindowMessage("TaskbarCreated");

    internal static void AllowTaskbarCreatedMessage(IntPtr hwnd, uint message)
    {
        // Explorer 通常以普通权限运行，允许它向已提权的窗口发送此广播。
        if (message != 0) ChangeWindowMessageFilterEx(hwnd, message, 1, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);
}
