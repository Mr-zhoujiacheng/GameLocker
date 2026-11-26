using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 底栏的全局鼠标 / 触摸输入接管。
/// 原因：WinUI 的内容实际托管在独立的 <c>Microsoft.UI.Content.DesktopChildSiteBridge</c> 窗口里，
/// 而且底栏是 WS_EX_NOACTIVATE、从不激活的窗口——实测鼠标消息既不进顶层窗口过程，
/// WinUI 的指针事件也不投递，XAML 拖放同样收不到。
/// 因此改用 WH_MOUSE_LL：在钩子里判断坐标是否落在底栏矩形内，命中就吞掉消息并自行处理，
/// 保证点击启动与拖拽排序在任何前台应用下都可靠。
///
/// 触摸要另走一条路：触摸帧被 WinUI 的指针管道消费后不会再提升成鼠标消息，WH_MOUSE_LL 收不到，
/// 表现为"鼠标能点、手指点不了"。这里转接 <see cref="TouchInputHook"/> 的原始输入触摸通道，
/// 把一次点按（按下→抬起、位移没超出抖动范围）合成为与鼠标完全相同的左键按下/抬起，
/// 底栏的命中判定、按压反馈、启动逻辑都复用同一套代码。
/// </summary>
internal static class DockInputHook
{
    /// <summary>左键按下；参数为屏幕物理坐标，返回 true 表示已消费（不再下发）。</summary>
    public static Func<int, int, bool>? LeftDown;

    /// <summary>鼠标移动（仅左键按下期间会被调用）；返回 true 表示已消费。</summary>
    public static Func<int, int, bool>? LeftMove;

    /// <summary>左键抬起；返回 true 表示已消费。</summary>
    public static Func<int, int, bool>? LeftUp;

    /// <summary>右键按下；返回 true 表示已消费。</summary>


    /// <summary>
    /// 按下被取消（触摸滑走、滑出底栏等）：实现方应清掉按下态与按压反馈。
    /// 鼠标不会走这里——鼠标的取消由"抬起时位移"自然判成拖拽。
    /// </summary>
    public static Action? PressCancelled;

    /// <summary>鼠标在未拖拽状态下移动（用于悬停预览）；不会消费消息。</summary>
    public static Action<int, int>? Hover;

    /// <summary>
    /// 触摸长按图标（屏幕坐标）。长按成功后本次抬起不会再触发点击，
    /// 与鼠标悬停预览/手柄 Y 共用同一张窗口卡片。
    /// </summary>
    public static Action<int, int>? LongPress;

    /// <summary>
    /// 非消费式的鼠标移动通知（屏幕 x, 屏幕 y），未拖拽时逐条触发。
    /// 顶栏用它做悬停高亮：窗口是 NOACTIVATE 且内容托管在子窗口里，拿不到 WinUI 指针事件。
    /// 观察者应自己先做矩形粗筛，避免每条移动消息都跨线程投递。
    /// </summary>
    public static event Action<int, int>? Moved;

    /// <summary>
    /// 非消费式的鼠标按下通知（消息号, 屏幕 x, 屏幕 y）。
    /// 顶栏用它做点击命中：它不夺焦点、也没有 WinUI 指针事件，只能靠这里拿到点击。
    /// 观察者不参与"是否消费"的决定。
    /// </summary>
    public static event Action<int, int, int>? Pressed;

    private static IntPtr _hook;
    private static Thread? _thread;
    private static HookProc? _proc;

    /// <summary>
    /// 自己记录"左键按下已被我们吞掉"。
    /// 不能用 GetAsyncKeyState 判断：低级钩子吞掉 LBUTTONDOWN 后，系统不再更新按键状态表，
    /// 于是 GetAsyncKeyState 会一直返回"未按下"（实测如此）。
    /// </summary>
    private static bool _dragging;

    // ---- 触摸（原始输入通道）状态 ----
    /// <summary>本次点按允许的抖动半径（物理像素）：超出即视为滑动，不触发点击。</summary>
    private const int TouchTapSlop = 32;

    /// <summary>当前有手指按着（只跟踪首指）。</summary>
    private static bool _touchDown;

    /// <summary>本次按下是否由底栏消费（没命中底栏时不做任何后续动作）。</summary>
    private static bool _touchConsumed;

    /// <summary>按下点（用于判定是否滑走）。</summary>
    private static int _touchDownX, _touchDownY;

    /// <summary>触摸长按判定时长（毫秒）：按住图标不动超过这个时间就弹出卡片。</summary>
    private const int LongPressMs = 550;

    /// <summary>长按计时器：只在本次触摸仍按着、没滑走时触发一次。</summary>
    private static Timer? _longPressTimer;

    /// <summary>每次按下/抬起/取消都递增，用来让已经排队的旧计时器回调作废。</summary>
    private static long _longPressVersion;

    /// <summary>本次触摸已经触发过长按：抬起时不再当成点击。</summary>
    private static bool _touchLongPressed;

    /// <summary>
    /// 触摸处理期间吞掉它可能"提升"出来的鼠标消息的截止时间（TickCount64）。
    /// 只在本进程确实消费了触摸时设置，避免误吞别处的真实鼠标操作。
    /// </summary>
    private static long _suppressMouseUntil;

    public static void Start()
    {
        if (_thread is not null) return;

        // 触摸通道只接一次：本方法在每块底栏创建时都会被调用
        TouchInputHook.Down += OnTouchDown;
        TouchInputHook.Move += OnTouchMove;
        TouchInputHook.Up += OnTouchUp;
        TouchInputHook.Start();

        _thread = new Thread(Run) { IsBackground = true, Name = "DockInputHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private static void Run()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            DebugLog.Write("[底栏] 鼠标钩子安装失败，err=" + Marshal.GetLastWin32Error());

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);

        var msg = wParam.ToInt32();
        if (msg != WM_LBUTTONDOWN && msg != WM_LBUTTONUP && msg != WM_MOUSEMOVE && msg != WM_RBUTTONDOWN)
            return CallNextHookEx(_hook, code, wParam, lParam);

        MSLLHOOKSTRUCT data;
        try { data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam); }
        catch { return CallNextHookEx(_hook, code, wParam, lParam); }

        // 只看真实的左键：MOUSEMOVE 也只在按键按下时进入判定
        var point = data.pt;

        // 触摸已被原始输入通道接管：触摸可能"提升"出来的重复鼠标消息一并吞掉，
        // 否则一次点按会当成两次（低级钩子吞掉按下后系统不再更新按键状态，只能靠自己记的时间窗去重）。
        if ((msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MOUSEMOVE) && TouchOwnsMouse())
            return (IntPtr)1;

        // 非消费式观察者（顶栏点击等）：异常不影响后续处理
        if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
        {
            try { Pressed?.Invoke(msg, point.X, point.Y); } catch { }
        }

        bool consumed;
        try
        {
            switch (msg)
            {
                case WM_LBUTTONDOWN:
                    consumed = LeftDown?.Invoke(point.X, point.Y) ?? false;
                    if (consumed) _dragging = true;
                    break;
                case WM_LBUTTONUP:
                    _dragging = false;
                    consumed = LeftUp?.Invoke(point.X, point.Y) ?? false;
                    break;
                case WM_RBUTTONDOWN:
                    // 右键永远放行：顺手清掉可能卡住的拖拽状态，避免后续鼠标移动被 LeftMove 吞掉
                    DebugLog.Write($"[底栏] 右键放行 dragging={_dragging} pos={point.X},{point.Y}");
                    _dragging = false;
                    consumed = false;
                    break;
                default:
                    if (_dragging)
                    {
                        consumed = LeftMove?.Invoke(point.X, point.Y) ?? false;
                    }
                    else
                    {
                        // 悬停：不消费消息，只是通知坐标变化
                        try { Hover?.Invoke(point.X, point.Y); } catch { }
                        try { Moved?.Invoke(point.X, point.Y); } catch { }
                        consumed = false;
                    }
                    break;
            }
        }
        catch
        {
            _dragging = false;
            consumed = false;
        }

        return consumed ? (IntPtr)1 : CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>触摸期间（含结束后极短时间窗）是否应由触摸通道独占鼠标消息。</summary>
    private static bool TouchOwnsMouse()
        => _touchDown ? _touchConsumed : Environment.TickCount64 < _suppressMouseUntil;

    /// <summary>
    /// 触摸按下：直接走鼠标左键按下，命中判定、按压反馈与鼠标完全一致。
    /// 按下后记录起点，用来区分"点按"和"在底栏上滑动"。
    /// </summary>
    private static void OnTouchDown(int screenX, int screenY)
    {
        if (_touchDown) return; // 只跟踪首指
        _touchDown = true;
        _touchConsumed = false;
        _touchDownX = screenX;
        _touchDownY = screenY;

        try
        {
            _touchConsumed = LeftDown?.Invoke(screenX, screenY) ?? false;
        }
        catch
        {
            _touchConsumed = false;
        }

        if (_touchConsumed)
        {
            _suppressMouseUntil = Environment.TickCount64 + 300;
            StartLongPressTimer(screenX, screenY);
        }
    }

    /// <summary>触摸移动：一旦位移超出抖动半径就取消这次点按，避免滑动页面时误启动图标。</summary>
    private static void OnTouchMove(int screenX, int screenY)
    {
        if (!_touchDown || !_touchConsumed) return;
        if (Math.Abs(screenX - _touchDownX) <= TouchTapSlop &&
            Math.Abs(screenY - _touchDownY) <= TouchTapSlop) return;

        _touchConsumed = false;
        _touchLongPressed = false;
        StopLongPressTimer();
        _suppressMouseUntil = Environment.TickCount64 + 300;
        try { PressCancelled?.Invoke(); } catch { }
    }

    /// <summary>触摸抬起：没滑走、也没触发长按时才当作一次左键点击（启动 / 切换 / 分组展开等走原有逻辑）。</summary>
    private static void OnTouchUp(int screenX, int screenY)
    {
        if (!_touchDown) return;
        _touchDown = false;
        StopLongPressTimer();

        var consumed = _touchConsumed;
        _touchConsumed = false;
        if (!consumed) return;

        _suppressMouseUntil = Environment.TickCount64 + 300;
        if (_touchLongPressed)
        {
            // 长按已经弹出卡片：这次抬起只结束按压态，不再启动图标
            _touchLongPressed = false;
            try { PressCancelled?.Invoke(); } catch { }
            return;
        }

        try
        {
            LeftUp?.Invoke(screenX, screenY);
        }
        catch { }
    }

    /// <summary>开始长按计时：按住图标不动 550ms 后回调一次，期间抬起/滑动都会取消。</summary>
    private static void StartLongPressTimer(int screenX, int screenY)
    {
        StopLongPressTimer();
        _touchLongPressed = false;
        var version = _longPressVersion;
        _longPressTimer = new Timer(_ =>
        {
            if (version != Interlocked.Read(ref _longPressVersion)) return;
            if (!_touchDown || !_touchConsumed) return;
            _touchLongPressed = true;
            _suppressMouseUntil = Environment.TickCount64 + 500;
            DebugLog.Write($"[底栏] 触摸长按触发 {screenX},{screenY}");
            try { LongPress?.Invoke(screenX, screenY); } catch { }
        }, null, LongPressMs, Timeout.Infinite);
    }

    /// <summary>取消长按计时（抬起、滑走、下一次按下都会调用）。</summary>
    private static void StopLongPressTimer()
    {
        Interlocked.Increment(ref _longPressVersion);
        var timer = _longPressTimer;
        _longPressTimer = null;
        if (timer is null) return;
        try { timer.Dispose(); } catch { }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

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
}
