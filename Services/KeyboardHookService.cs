using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）：监听"键盘呼出键"。
/// 命中（主键按下且修饰键精确匹配）时立即触发 <see cref="Triggered"/> 并吞掉该按键——无需长按。
/// 钩子必须挂在自己的消息循环线程上，故单独开后台线程安装。
/// 捕获键位期间用 <see cref="SetSuspended"/> 暂停判定，避免把正在捕获的按键吞掉。
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    /// <summary>监听的键盘虚拟键码（0=不监听）</summary>
    public int Key { get; set; } = 0x76; // F7

    /// <summary>需精确匹配的修饰键标志（Ctrl=1/Shift=2/Alt=4/Win=8，见 KeySendService.Mod*）</summary>
    public int Mods { get; set; }

    /// <summary>命中时触发（在钩子线程上，调用方需自行 marshal 到 UI 线程）</summary>
    public event Action? Triggered;

    private volatile bool _suspended;
    private volatile bool _running;
    private volatile bool _suppressUp;
    private uint _threadId;
    private IntPtr _hook;
    private HOOKPROC? _proc;
    private Thread? _thread;

    /// <summary>暂停/恢复判定（捕获键位期间暂停，避免钩子把用户正在按的键吞掉）</summary>
    public void SetSuspended(bool value) => _suspended = value;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "GameLocker.KeyHook" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        // 投递 WM_QUIT 唤醒消息循环，让钩子线程退出并卸载钩子
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback; // 保持委托存活，避免被 GC 回收导致回调跳飞
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _running = false;
            return;
        }
        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _threadId = 0;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _running && !_suspended && Key != 0)
        {
            var msg = (int)wParam;
            var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((int)data.vkCode == Key)
                {
                    if (isDown && CurrentMods() == Mods)
                    {
                        _suppressUp = true;         // 抬起也要一并吞掉，保持前后台一致
                        Triggered?.Invoke();
                        return (IntPtr)1;
                    }
                    if (isUp && _suppressUp)
                    {
                        _suppressUp = false;
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>当前按住的修饰键（左/右任一）</summary>
    private static int CurrentMods()
    {
        var m = 0;
        if (IsDown(VK_CONTROL)) m |= KeySendService.ModCtrl;
        if (IsDown(VK_SHIFT)) m |= KeySendService.ModShift;
        if (IsDown(VK_MENU)) m |= KeySendService.ModAlt;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= KeySendService.ModWin;
        return m;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ---- Win32 P/Invoke ----

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr HOOKPROC(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HOOKPROC lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
