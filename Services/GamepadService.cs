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

    // 方向键长按连发（按住先走一格，延迟后按间隔连续走）
    private const long RepeatInitialDelayMs = 400;
    private const long RepeatIntervalMs = 130;
    private const GamepadButtons DirectionMask =
        GamepadButtons.DPadUp | GamepadButtons.DPadDown |
        GamepadButtons.DPadLeft | GamepadButtons.DPadRight;
    private GamepadButtons _heldDirs;
    private long _repeatNextMs;

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

    /// <summary>全屏组合键（默认 LB+RB 同时长按 1 秒切换全屏）</summary>
    public GamepadButtons FullscreenCombo { get; set; } =
        GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder;

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

            // 左摇杆 → 十字键方向（带死区）
            if (gp.ThumbLY > StickDeadzone) buttons |= GamepadButtons.DPadUp;
            else if (gp.ThumbLY < -StickDeadzone) buttons |= GamepadButtons.DPadDown;
            if (gp.ThumbLX < -StickDeadzone) buttons |= GamepadButtons.DPadLeft;
            else if (gp.ThumbLX > StickDeadzone) buttons |= GamepadButtons.DPadRight;

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
        if (pressed != GamepadButtons.None)
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
                ButtonPressed?.Invoke(heldDirs);
            }
        }
        else
        {
            _heldDirs = GamepadButtons.None;
        }

        if (_last != GamepadButtons.None && current == GamepadButtons.None)
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
