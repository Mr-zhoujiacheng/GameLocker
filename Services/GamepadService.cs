using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// XInput 按钮标志（低 16 位与 XINPUT_GAMEPAD.wButtons 一致；
/// 扳机为模拟量，超过阈值映射为自定义高位标志）。
/// 不依赖 Windows.Gaming.Input：unpackaged 应用无包标识时该 WinRT API 返回空集合。
/// </summary>
[Flags]
public enum GamepadButtons
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Menu = 0x0010,          // Start 键
    View = 0x0020,          // Back/Select 键
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,         // 西瓜键
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
    LeftTrigger = 0x10000,  // LT（模拟量阈值化）
    RightTrigger = 0x20000, // RT
}

/// <summary>
/// 手柄轮询服务：XInput P/Invoke，~16ms 轮询 4 个用户槽位。
/// 除边沿按钮事件外，还检测"呼出组合键长按"（默认 LT+RT 1 秒）触发 SummonTriggered。
/// </summary>
public sealed class GamepadService : IDisposable
{
    private const int MaxUsers = 4;
    private const short StickDeadzone = 8000;
    /// <summary>摇杆方向释放阈值（迟滞）：方向保持到摇杆回落到此值以内才松开</summary>
    private const short StickReleaseDeadzone = 4000;
    /// <summary>松杆过冲保护窗口：回中/换向后的这段时间内出现的反向摇杆输入判为回弹并忽略</summary>
    private const long StickReversalGuardMs = 150;
    private const byte TriggerThreshold = 30;
    private const int ERROR_SUCCESS = 0;

    /// <summary>呼出组合键需持续按住的时长</summary>
    public const int SummonHoldMs = 1000;

    private System.Timers.Timer? _timer;
    private GamepadButtons _last;
    private GamepadButtons _current;
    private bool _hasGamepad;
    private long _comboStartMs;
    private bool _comboFired;
    private long _fsStartMs;
    private bool _fsFired;
    private static bool _useLegacyDll;

    /// <summary>按键是否派发给页面：窗口隐藏到托盘/被其他应用（正在玩的游戏）盖住时置 false。
    /// 此时仍继续轮询（呼出组合键、连接状态都依赖轮询），但不派发按键/方向/摇杆事件。</summary>
    private volatile bool _routingEnabled;

    // 方向键长按连发（按住先走一格，延迟后按间隔连续走）
    private const long RepeatInitialDelayMs = 400;
    private const long RepeatIntervalMs = 130;
    private const GamepadButtons DirectionMask =
        GamepadButtons.DPadUp | GamepadButtons.DPadDown |
        GamepadButtons.DPadLeft | GamepadButtons.DPadRight;
    private GamepadButtons _heldDirs;
    private long _repeatNextMs;

    // 左摇杆方向迟滞状态（防松杆过冲触发反向，导致"下去又上来"）
    private int _yDir, _yLastSign, _xDir, _xLastSign;
    private long _yReleaseMs, _xReleaseMs;

    /// <summary>按钮按下（边沿触发，含扳机/摇杆等全部键）</summary>
    public event Action<GamepadButtons>? ButtonPressed;
    /// <summary>手柄连接状态变化</summary>
    public event Action<bool>? ConnectionChanged;
    /// <summary>所有按键松开</summary>
    public event Action? AllReleased;
    /// <summary>呼出组合键长按达到 SummonHoldMs 触发（松开前只触发一次）</summary>
    public event Action? SummonTriggered;
    /// <summary>全屏组合键长按达到 SummonHoldMs 触发（松开前只触发一次）</summary>
    public event Action? FullscreenTriggered;

    public bool HasGamepad => _hasGamepad;
    /// <summary>当前帧完整按键状态（供按键捕获等场景查询）</summary>
    public GamepadButtons Current => _current;

    /// <summary>手柄按键是否派发给页面（窗口可见且处于前台时为 true）。
    /// 从 false 恢复为 true 时重置方向连发计时：隐藏期间一直按住的方向不该在恢复瞬间被判成新按下而连发。</summary>
    public bool RoutingEnabled
    {
        get => _routingEnabled;
        set
        {
            if (_routingEnabled == value) return;
            _routingEnabled = value;
            if (value)
            {
                _heldDirs = _current & DirectionMask;
                _repeatNextMs = Environment.TickCount64 + RepeatInitialDelayMs;
            }
        }
    }

    /// <summary>呼出组合键（默认 LT+RT 同时长按 1 秒）</summary>
    public GamepadButtons SummonCombo { get; set; } =
        GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger;

    /// <summary>设置呼出组合键并复位长按计时</summary>
    public void SetSummonCombo(GamepadButtons combo)
    {
        SummonCombo = combo;
        _comboStartMs = 0;
        _comboFired = false;
    }

    /// <summary>全屏组合键（默认菜单键长按 1 秒切换全屏；实际值由设置页捕获后覆盖）</summary>
    public GamepadButtons FullscreenCombo { get; set; } = GamepadButtons.Menu;

    /// <summary>设置全屏组合键并复位长按计时</summary>
    public void SetFullscreenCombo(GamepadButtons combo)
    {
        FullscreenCombo = combo;
        _fsStartMs = 0;
        _fsFired = false;
    }

    public void Start()
    {
        _timer = new System.Timers.Timer(16) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        GamepadButtons current = GamepadButtons.None;
        var any = false;

        for (uint i = 0; i < MaxUsers; i++)
        {
            if (GetState(i, out var state) != ERROR_SUCCESS) continue;
            any = true;

            var buttons = (GamepadButtons)state.Gamepad.Buttons;

            var gp = state.Gamepad;
            if (gp.LeftTrigger > TriggerThreshold) buttons |= GamepadButtons.LeftTrigger;
            if (gp.RightTrigger > TriggerThreshold) buttons |= GamepadButtons.RightTrigger;

            // 左摇杆 → 十字键方向（死区 + 迟滞 + 松杆过冲保护）
            var ly = ReadStickAxis(gp.ThumbLY, ref _yDir, ref _yLastSign, ref _yReleaseMs);
            if (ly > 0) buttons |= GamepadButtons.DPadUp;
            else if (ly < 0) buttons |= GamepadButtons.DPadDown;
            var lx = ReadStickAxis(gp.ThumbLX, ref _xDir, ref _xLastSign, ref _xReleaseMs);
            if (lx < 0) buttons |= GamepadButtons.DPadLeft;
            else if (lx > 0) buttons |= GamepadButtons.DPadRight;

            current |= buttons;
        }

        if (any != _hasGamepad)
        {
            _hasGamepad = any;
            ConnectionChanged?.Invoke(any);
        }

        _current = current;

        // 边沿：本帧按下且上一帧未按下
        var pressed = current & ~_last;
        if (pressed != GamepadButtons.None && _routingEnabled)
            ButtonPressed?.Invoke(pressed);

        // 方向键长按连发：按住持续超过起始延迟后，按固定间隔重复触发方向按下事件
        var heldDirs = current & DirectionMask;
        if (heldDirs != GamepadButtons.None)
        {
            var nowMs = Environment.TickCount64;
            if (heldDirs != _heldDirs)
            {
                // 方向变化/新按下：重置连发计时
                _heldDirs = heldDirs;
                _repeatNextMs = nowMs + RepeatInitialDelayMs;
            }
            else if (nowMs >= _repeatNextMs)
            {
                _repeatNextMs = nowMs + RepeatIntervalMs;
                if (_routingEnabled) ButtonPressed?.Invoke(heldDirs);
            }
        }
        else
        {
            _heldDirs = GamepadButtons.None;
        }

        if (_routingEnabled && _last != GamepadButtons.None && current == GamepadButtons.None)
            AllReleased?.Invoke();

        // 呼出组合键长按检测
        if (SummonCombo != GamepadButtons.None &&
            (current & SummonCombo) == SummonCombo)
        {
            var now = Environment.TickCount64;
            if (_comboStartMs == 0) _comboStartMs = now;
            else if (!_comboFired && now - _comboStartMs >= SummonHoldMs)
            {
                _comboFired = true;
                SummonTriggered?.Invoke();
            }
        }
        else
        {
            _comboStartMs = 0;
            _comboFired = false;
        }

        // 全屏组合键长按检测
        if (FullscreenCombo != GamepadButtons.None &&
            (current & FullscreenCombo) == FullscreenCombo)
        {
            var now = Environment.TickCount64;
            if (_fsStartMs == 0) _fsStartMs = now;
            else if (!_fsFired && now - _fsStartMs >= SummonHoldMs)
            {
                _fsFired = true;
                FullscreenTriggered?.Invoke();
            }
        }
        else
        {
            _fsStartMs = 0;
            _fsFired = false;
        }

        _last = current;
    }

    /// <summary>左摇杆单轴 → 方向（+1 正侧 / -1 负侧 / 0 中位）。
    /// ① 迟滞：以 StickDeadzone 进入、StickReleaseDeadzone 退出，避免临界点抖动；
    /// ② 松杆过冲保护：刚松开（或刚换向）后 StickReversalGuardMs 内出现的反向输入判为回弹，丢弃并归零。
    /// 这是"往下摇一下光标下去又上来"的根因修复。</summary>
    private static int ReadStickAxis(short v, ref int dir, ref int lastSign, ref long releaseMs)
    {
        var now = Environment.TickCount64;
        var raw = v > StickDeadzone ? 1 : v < -StickDeadzone ? -1 : 0;

        if (raw == 0)
        {
            // 迟滞：仍在释放阈值之外则保持当前方向；回落到阈值内才算松开
            if (dir != 0 && Math.Abs((int)v) > StickReleaseDeadzone) return dir;
            if (dir != 0)
            {
                lastSign = dir;
                releaseMs = now;
            }
            dir = 0;
            return 0;
        }

        if (raw == dir) return dir;

        // 反向输入且刚松开/刚换向不久 → 松杆过冲：丢弃并让方向归零（同时停止连发）
        if (raw == -lastSign && now - releaseMs < StickReversalGuardMs)
        {
            dir = 0;
            return 0;
        }

        dir = raw;
        lastSign = raw;
        releaseMs = now;
        return dir;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }

    /// <summary>组合键的界面描述（如 "LT+RT"）</summary>
    public static string Describe(GamepadButtons combo)
    {
        if (combo == GamepadButtons.None) return "未设置";
        var names = new (GamepadButtons Flag, string Name)[]
        {
            (GamepadButtons.LeftTrigger, "LT"), (GamepadButtons.RightTrigger, "RT"),
            (GamepadButtons.LeftShoulder, "LB"), (GamepadButtons.RightShoulder, "RB"),
            (GamepadButtons.A, "A"), (GamepadButtons.B, "B"),
            (GamepadButtons.X, "X"), (GamepadButtons.Y, "Y"),
            (GamepadButtons.Menu, "Start"), (GamepadButtons.View, "Select"),
            (GamepadButtons.Guide, "Guide"),
            (GamepadButtons.LeftThumb, "左摇杆"), (GamepadButtons.RightThumb, "右摇杆"),
            (GamepadButtons.DPadUp, "十字键↑"), (GamepadButtons.DPadDown, "十字键↓"),
            (GamepadButtons.DPadLeft, "十字键←"), (GamepadButtons.DPadRight, "十字键→"),
        };
        var parts = new List<string>();
        foreach (var (flag, name) in names)
            if (combo.HasFlag(flag)) parts.Add(name);
        return parts.Count == 0 ? "未设置" : string.Join("+", parts);
    }

    // ---- XInput P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint PacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(uint user, out XINPUT_STATE state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState9(uint user, out XINPUT_STATE state);

    private static int GetState(uint user, out XINPUT_STATE state)
    {
        if (!_useLegacyDll)
        {
            try { return XInputGetState14(user, out state); }
            catch (DllNotFoundException) { _useLegacyDll = true; }
            catch (EntryPointNotFoundException) { _useLegacyDll = true; }
        }
        return XInputGetState9(user, out state);
    }
}
