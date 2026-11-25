using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>呼出手势所在屏幕边缘（Bottom：任务栏在屏幕下方时，从底边向上滑呼出）</summary>
public enum SummonEdge { Left, Right, Top, Bottom }

/// <summary>
/// 平板边缘手势呼出：双通道监听"从屏幕边缘向中心"的滑动即触发呼出。
/// ① 全局低级鼠标钩子（WH_MOUSE_LL）：捕获鼠标拖动及触摸合成的鼠标消息（桌面/无触摸处理的前台时有效）；
/// ② <see cref="TouchInputHook"/> 提供原始触摸按下和抬起，不受窗口命中测试影响。
/// 触摸坐标投递到鼠标钩子线程，两通道共用 600ms 冷却。事件在该线程触发，订阅方需 marshal 回 UI 线程。
/// 仅监听手势；系统边缘滑动策略由设置开关通过独立提权助手管理。
/// 诊断：边缘手势事件写入 %APPDATA%\GameLocker\edge.log。
/// </summary>
public sealed class EdgeGestureService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TOUCH_DOWN = 0x8001;
    private const uint WM_TOUCH_UP = 0x8002;

    /// <summary>按下点距边缘在此范围内（物理像素）才视为边缘手势起点</summary>
    private const int EdgeZone = 70;
    /// <summary>向内拖动触发呼出的最小距离</summary>
    private const int Threshold = 60;
    /// <summary>朝边缘方向快滑触发收起的最小距离（比呼出更大：避免与列表滚动、磁贴拖拽混淆）</summary>
    private const int DismissThreshold = 120;

    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile SummonEdge _edge = SummonEdge.Left;
    private readonly HookProcDelegate _hookProc; // 防 GC 回收
    private IntPtr _hook = 0;
    private int _downX, _downY;
    private long _downAt;
    private volatile bool _tracking;
    private bool _downNearEdge;     // 本次按下起点是否在边缘感应带内（决定抬起时判呼出还是收起）
    private long _lastTrigger;
    private long _lastDismiss;
    private long _lastBack;
    /// <summary>压制"向下滑=返回"截止时间（TickCount64）：窗口侧发现本次下滑起点在已滚动的内容里时设置，
    /// 该手势期间原始输入通道不再把它当返回，交给列表正常滚动</summary>
    public long BackSuppressedUntil { get; set; }

    // HID 触摸手势状态（单指即可判定边缘滑动）
    private bool _tDown;
    private int _tStartX, _tStartY;
    private long _tDownAt;

    /// <summary>边缘向内滑动被识别 → 请求呼出</summary>
    public event Action? SummonTriggered;

    /// <summary>从屏幕边缘起手、朝边缘外快滑被识别 → 请求最小化（中间横滑不再触发，
    /// 主页中间横滑由 MainPage 用于切换副页）。
    /// 走 Raw Input 触摸通道：不受页面命中测试/列表框触摸捕获影响，任何页面都能触发</summary>
    public event Action? DismissTriggered;

    /// <summary>从窗口内部向下快滑被识别 → 请求返回上一级（主页由上层决定改为最小化）。
    /// 同样走 Raw Input 触摸通道</summary>
    public event Action? BackTriggered;

    /// <summary>手指刚落在呼出边缘带（抬起前就触发）：用于提前封锁系统开始菜单，
    /// 避免上滑动作完成前原版开始菜单先闪出来。</summary>
    public event Action? EdgeTouched;

    private void NotifyEdgeTouched()
    {
        try { EdgeTouched?.Invoke(); }
        catch { /* 订阅方异常不影响手势判定 */ }
    }

    public EdgeGestureService()
    {
        _hookProc = HookCallback;
    }

    /// <summary>当前是否已启用（钩子运行中）</summary>
    public bool IsRunning => _running;

    /// <summary>启动本应用手势监听（幂等）。
    /// 关键：每次新建 Thread——已退出的 Thread 再 Start() 会抛 ThreadStateException，
    /// 在 XAML 事件回调里变成 stowed 异常 0xc000027b 崩 CoreMessagingXP（开关关→开即闪退的根因）。</summary>
    public void Start()
    {
        if (_disposed || _running || _thread?.IsAlive == true) return;
        _running = true;
        _tracking = _tDown = false;
        var t = new Thread(Run) { IsBackground = true, Name = "EdgeGesture" };
        // 钩子线程必须 MTA：STA 与 WinUI3 CoreMessaging/COM 公寓冲突（WH_MOUSE_LL 只需消息泵，不需要 STA）
        t.SetApartmentState(ApartmentState.MTA);
        _thread = t;
        t.Start();
    }

    /// <summary>停止本应用手势监听（幂等）</summary>
    public void Stop()
    {
        _running = false;
        var id = _threadId;
        if (id != 0) PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        var thread = _thread;
        if (thread != null && thread != Thread.CurrentThread && !thread.Join(3000))
            Log("gesture thread stop timed out");
    }

    /// <summary>切换呼出边缘（停靠侧/全屏变化时调用）</summary>
    public void SetEdge(SummonEdge edge)
    {
        _edge = edge;
    }

    /// <summary>兼容旧接口：原始触摸监听不遮挡输入，无需显隐控制。</summary>
    public void SetStripVisible(bool visible) { }

    private void Run()
    {
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
            Log($"hook installed, handle={_hook}");
            if (_hook == 0)
            {
                Log($"SetWindowsHookEx failed err={Marshal.GetLastWin32Error()}");
                return;
            }
            TouchInputHook.Down += QueueTouchDown;
            TouchInputHook.Up += QueueTouchUp;
            TouchInputHook.Start();

            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                switch (msg.message)
                {
                    case WM_TOUCH_DOWN:
                        OnTouchDown(msg.wParam.ToInt32(), msg.lParam.ToInt32());
                        break;
                    case WM_TOUCH_UP:
                        OnTouchUp(msg.wParam.ToInt32(), msg.lParam.ToInt32());
                        break;
                    default:
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                        break;
                }
            }
        }
        catch (Exception ex) { Log($"gesture thread EX: {ex}"); }
        finally
        {
            _running = false;
            TouchInputHook.Down -= QueueTouchDown;
            TouchInputHook.Up -= QueueTouchUp;
            UnhookWindowsHookEx(_hook);
            _hook = 0;
            _threadId = 0;
            Log("hook removed, thread exit");
        }
    }

    private void QueueTouchDown(int x, int y) => QueueTouch(WM_TOUCH_DOWN, x, y);
    private void QueueTouchUp(int x, int y) => QueueTouch(WM_TOUCH_UP, x, y);

    private void QueueTouch(uint message, int x, int y)
    {
        var id = _threadId;
        if (_running && id != 0) PostThreadMessage(id, message, (IntPtr)x, (IntPtr)y);
    }

    private void OnTouchDown(int x, int y)
    {
        _tDown = true;
        _tStartX = x;
        _tStartY = y;
        _tDownAt = Environment.TickCount64;
        var near = NearEdge(x, y);
        if (near) NotifyEdgeTouched();
        Log($"touch down ({x},{y}) nearEdge={near}");
    }

    private void OnTouchUp(int x, int y)
    {
        if (!_tDown) return;
        _tDown = false;
        var elapsed = Environment.TickCount64 - _tDownAt;
        // 游戏内右上/底部边缘：交给顶栏/底栏手势，启动器这次不再响应
        if (GameSwipeService.TryHandleGesture(_tStartX, _tStartY, x, y, elapsed))
        {
            Log("game swipe consumed (touch)");
            return;
        }
        var inward = _edge switch
        {
            SummonEdge.Left => x - _tStartX,
            SummonEdge.Right => _tStartX - x,
            SummonEdge.Bottom => _tStartY - y,
            _ => y - _tStartY
        };
        var nearEdge = NearEdge(_tStartX, _tStartY);
        Log($"touch up ({x},{y}) inward={inward} nearEdge={nearEdge} ms={elapsed}");
        var cross = _edge is SummonEdge.Left or SummonEdge.Right
            ? Math.Abs(y - _tStartY) : Math.Abs(x - _tStartX);
        if (nearEdge && inward >= Threshold && cross < inward && elapsed < 800 &&
            (Environment.TickCount64 - _lastTrigger) > 600)
        {
            _lastTrigger = Environment.TickCount64;
            try { SummonTriggered?.Invoke(); Log("touch summon dispatched"); }
            catch (Exception ex) { Log($"summon invoke EX: {ex.GetType().Name}: {ex.Message}"); }
        }
        else if (!nearEdge && _edge == SummonEdge.Top && -inward >= DismissThreshold &&
            cross < 80 && elapsed < 500 && Environment.TickCount64 - _lastDismiss > 600)
        {
            _lastDismiss = Environment.TickCount64;
            try { DismissTriggered?.Invoke(); Log("touch dismiss dispatched (top)"); }
            catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
        }
        // 向下滑：起点在窗口内部、竖直向下快滑（横向偏移小，避开横向拖拽）→ 返回上一级
        else if (!nearEdge && (y - _tStartY) >= DismissThreshold && Math.Abs(x - _tStartX) < 80 &&
            elapsed < 500 && (Environment.TickCount64 - _lastBack) > 600 &&
            Environment.TickCount64 > BackSuppressedUntil)
        {
            _lastBack = Environment.TickCount64;
            try { BackTriggered?.Invoke(); Log("touch back dispatched"); }
            catch (Exception ex) { Log($"back invoke EX: {ex.GetType().Name}: {ex.Message}"); }
        }
        // 滑到屏幕左/右边缘（终点在边缘带内、横向快滑且位移够大）→ 最小化；
        // 起点在中间、停在中间的横滑不触发（留给主页切副页）
        else if (!nearEdge && NearSideEdge(x) && Math.Abs(x - _tStartX) >= DismissThreshold &&
            Math.Abs(y - _tStartY) < 80 && elapsed < 500 &&
            (Environment.TickCount64 - _lastDismiss) > 600)
        {
            _lastDismiss = Environment.TickCount64;
            try { DismissTriggered?.Invoke(); Log("touch dismiss dispatched"); }
            catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _running)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msgId = (int)wParam;

            if (msgId == WM_LBUTTONDOWN)
            {
                // 每次都记起点：抬起时按起点是否在边缘带内，判呼出或收起
                _downX = info.ptX; _downY = info.ptY;
                _downAt = Environment.TickCount64;
                _downNearEdge = NearEdge(info.ptX, info.ptY);
                _tracking = true;
                if (_downNearEdge)
                {
                    NotifyEdgeTouched();
                    Log($"down near edge {_edge} at ({info.ptX},{info.ptY})");
                }
            }
            else if (msgId == WM_LBUTTONUP && _tracking)
            {
                _tracking = false;
                // 游戏内右上/底部边缘：交给顶栏/底栏手势，启动器这次不再响应
                if (GameSwipeService.TryHandleGesture(_downX, _downY, info.ptX, info.ptY,
                        Environment.TickCount64 - _downAt))
                {
                    Log("game swipe consumed (mouse)");
                    return CallNextHookEx(_hook, nCode, wParam, lParam);
                }
                var inward = Inward(info.ptX, info.ptY);
                var elapsed = Environment.TickCount64 - _downAt;
                var cross = _edge is SummonEdge.Left or SummonEdge.Right
                    ? Math.Abs(info.ptY - _downY) : Math.Abs(info.ptX - _downX);
                if (_downNearEdge)
                {
                    Log($"up at ({info.ptX},{info.ptY}) inward={inward}");
                    // 触摸会合成重复的鼠标消息，600ms 冷却去重，防双触发
                    if (inward >= Threshold && cross < inward && elapsed < 800 && (Environment.TickCount64 - _lastTrigger) > 600)
                    {
                        _lastTrigger = Environment.TickCount64;
                        try { SummonTriggered?.Invoke(); Log("summon dispatched"); }
                        catch (Exception ex) { Log($"summon invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                    }
                    // 从屏幕边缘朝外滑 → 最小化（与从边缘内滑呼出相反方向）
                    else if (-inward >= DismissThreshold && (Environment.TickCount64 - _lastDismiss) > 600)
                    {
                        _lastDismiss = Environment.TickCount64;
                        try { DismissTriggered?.Invoke(); Log("dismiss dispatched"); }
                        catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
                else if (elapsed < 500 && (info.ptY - _downY) >= DismissThreshold && Math.Abs(info.ptX - _downX) < 80 &&
                    (Environment.TickCount64 - _lastBack) > 600 &&
                    Environment.TickCount64 > BackSuppressedUntil)
                {
                    _lastBack = Environment.TickCount64;
                    try { BackTriggered?.Invoke(); Log("back dispatched"); }
                    catch (Exception ex) { Log($"back invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                }
                // 滑到屏幕左/右边缘 → 最小化（与触摸通道同一规则）
                else if (elapsed < 500 && NearSideEdge(info.ptX) && Math.Abs(info.ptX - _downX) >= DismissThreshold &&
                    Math.Abs(info.ptY - _downY) < 80 &&
                    (Environment.TickCount64 - _lastDismiss) > 600)
                {
                    _lastDismiss = Environment.TickCount64;
                    try { DismissTriggered?.Invoke(); Log("dismiss dispatched (edge end)"); }
                    catch (Exception ex) { Log($"dismiss invoke EX: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>抬起点是否落在屏幕左/右边缘带内（与呼出边无关：最小化统一看"滑到左右屏幕边缘"）</summary>
    private bool NearSideEdge(int x)
    {
        var sw = GetSystemMetrics(0);
        return x >= 0 && x < sw && (x <= EdgeZone || x >= sw - EdgeZone);
    }

    /// <summary>按下点是否落在当前呼出边缘的感应带内</summary>
    private bool NearEdge(int x, int y)
    {
        var sw = GetSystemMetrics(0);
        var sh = GetSystemMetrics(1);
        if (x < 0 || x >= sw || y < 0 || y >= sh) return false;
        return _edge switch
        {
            SummonEdge.Right => x >= sw - EdgeZone,
            SummonEdge.Top => y <= EdgeZone,
            SummonEdge.Bottom => y >= sh - EdgeZone,
            _ => x <= EdgeZone
        };
    }

    /// <summary>抬起点相对按下点的"向屏幕中心"位移</summary>
    private int Inward(int upX, int upY) => _edge switch
    {
        SummonEdge.Left => upX - _downX,   // 向右（向中心）
        SummonEdge.Right => _downX - upX,  // 向左（向中心）
        SummonEdge.Bottom => _downY - upY, // 向上（向中心）
        _ => upY - _downY                  // 向下（向中心）
    };

    /// <summary>诊断日志（便于真机排查触摸是否到达）</summary>
    private static void Log(string text)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            File.AppendAllText(Path.Combine(dir, "GameLocker", "edge.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>窗口层诊断写入：与手势日志同一文件，便于对照"窗口内指针通道"与"原始输入通道"</summary>
    internal static void Trace(string text) => Log(text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ---- P/Invoke ----
    private delegate IntPtr HookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int ptX, ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
