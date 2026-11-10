using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 隐藏窗口接收 WM_POWERBROADCAST：睡眠/休眠唤醒后触发 <see cref="Resumed"/>（在后台线程执行），
/// 用于把固件可能已复位的 CPU 功耗限制重新写回。
/// </summary>
internal static class PowerResumeWatcher
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMECRITICAL = 0x0006;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const string ClassName = "GameLocker.PowerWatch";
    private static readonly WndProcDelegate WindowProc = WndProc;
    private static Thread? _thread;

    public static event Action? Resumed;

    public static void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "GameLocker.PowerResume" };
        _thread.Start();
    }

    private static void MessageLoop()
    {
        var instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = WindowProc,
            hInstance = instance,
            lpszClassName = ClassName
        };
        RegisterClassExW(ref windowClass);
        var hwnd = CreateWindowExW(0, ClassName, "GameLocker Power", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
        DestroyWindow(hwnd);
        UnregisterClassW(ClassName, instance);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_POWERBROADCAST)
        {
            var code = (int)wParam;
            if (code is PBT_APMRESUMECRITICAL or PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC)
            {
                try { Resumed?.Invoke(); }
                catch (Exception ex) { DebugLog.Write("[电源] 唤醒处理失败：" + ex.Message); }
            }
            return new IntPtr(1);
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessageW(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
