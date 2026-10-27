using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 映射小组件的按键工具：键盘按键注入（SendInput，含 Ctrl/Shift/Alt/Win 组合）与按键显示名。
/// 手柄键无法向其它程序注入 XInput，改由调用方在本应用内派发（见 MainPage）。
/// </summary>
public static class KeySendService
{
    // ---- 修饰键位标志（存于 LaunchItem.MappingMods） ----
    public const int ModCtrl = 1;
    public const int ModShift = 2;
    public const int ModAlt = 4;
    public const int ModWin = 8;

    // 常用虚拟键码
    private const int VkShift = 0x10;
    private const int VkCtrl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkWin = 0x5B;

    /// <summary>Insert 键：OptiScaler 默认用它呼出游戏内的设置菜单</summary>
    public const int VkInsert = 0x2D;

    // ---- 鼠标按键编码（存于 GamepadMapping.TargetMouse） ----
    public const int MouseLeft = 1;
    public const int MouseRight = 2;
    public const int MouseMiddle = 3;
    public const int MouseX1 = 4;      // 侧键 1（后退）
    public const int MouseX2 = 5;      // 侧键 2（前进）
    public const int MouseWheelUp = 6; // 滚轮上
    public const int MouseWheelDown = 7; // 滚轮下

    /// <summary>目标鼠标按键的显示名；未知返回"未设置"</summary>
    public static string MouseLabel(int button) => button switch
    {
        MouseLeft => "鼠标左键",
        MouseRight => "鼠标右键",
        MouseMiddle => "鼠标中键",
        MouseX1 => "鼠标侧键1",
        MouseX2 => "鼠标侧键2",
        MouseWheelUp => "滚轮上",
        MouseWheelDown => "滚轮下",
        _ => "未设置"
    };

    /// <summary>该虚拟键是否为修饰键（Ctrl/Shift/Alt/Win 左右皆算）</summary>
    public static bool IsModifier(int vk) =>
        vk is VkShift or VkCtrl or VkAlt or VkWin or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5C;

    /// <summary>修饰键 → 修饰位标志；非修饰键返回 0</summary>
    public static int ModFlag(int vk) => vk switch
    {
        VkCtrl or 0xA2 or 0xA3 => ModCtrl,
        VkShift or 0xA0 or 0xA1 => ModShift,
        VkAlt or 0xA4 or 0xA5 => ModAlt,
        VkWin or 0x5C => ModWin,
        _ => 0
    };

    /// <summary>映射按键的显示名：优先手柄键，其次键盘键（含修饰键前缀），都没有则"未设置"</summary>
    public static string MappingLabel(int padFlags, int vk, int mods)
    {
        if (padFlags != 0) return GamepadService.Describe((GamepadButtons)padFlags);
        if (vk != 0) return Describe(vk, mods);
        return "未设置";
    }

    /// <summary>键盘按键显示名（"Ctrl+Shift+A" 形式；修饰键顺序 Ctrl/Shift/Alt/Win）</summary>
    public static string Describe(int vk, int mods)
    {
        var parts = new List<string>();
        if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModWin) != 0) parts.Add("Win");
        parts.Add(VkName(vk));
        return string.Join("+", parts);
    }

    /// <summary>虚拟键码 → 可读名称</summary>
    public static string VkName(int vk)
    {
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();       // 0-9
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();       // A-Z
        if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);       // Numpad0-9
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);        // F1-F24
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "空格",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "小键盘*",
            0x6B => "小键盘+",
            0x6D => "小键盘-",
            0x6E => "小键盘.",
            0x6F => "小键盘/",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "VK 0x" + vk.ToString("X2")
        };
    }

    /// <summary>需要 EXTENDEDKEY 标记的按键（方向/编辑/Home 区与右侧 Ctrl/Alt、小键盘除号等）</summary>
    private static bool IsExtended(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E => true, // PageUp/Down/End/Home/方向/Insert/Delete
        0x6F or 0x0D => true, // 小键盘除号 / 小键盘 Enter
        0xA3 or 0xA5 => true, // 右 Ctrl / 右 Alt
        _ => false
    };

    /// <summary>
    /// 把键盘按键（含修饰键组合）注入到当前前台窗口。
    /// 调用前应先把 GameLocker 窗口收起，否则按键会落在自己身上。
    /// </summary>
    public static uint Send(int vk, int mods)
    {
        if (vk == 0) return 0;

        var inputs = new List<INPUT>(8);
        void Key(int code, bool up, bool ext) => inputs.Add(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)code,
                    wScan = 0,
                    dwFlags = (uint)((up ? KEYEVENTF_KEYUP : 0) | (ext ? KEYEVENTF_EXTENDEDKEY : 0)),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });

        var ctrl = (mods & ModCtrl) != 0;
        var shift = (mods & ModShift) != 0;
        var alt = (mods & ModAlt) != 0;
        var win = (mods & ModWin) != 0;

        if (ctrl) Key(VkCtrl, false, false);
        if (shift) Key(VkShift, false, false);
        if (alt) Key(VkAlt, false, false);
        if (win) Key(VkWin, false, true);

        var ext = IsExtended(vk);
        Key(vk, false, ext);
        Key(vk, true, ext);

        if (win) Key(VkWin, true, true);
        if (alt) Key(VkAlt, true, false);
        if (shift) Key(VkShift, true, false);
        if (ctrl) Key(VkCtrl, true, false);

        var arr = inputs.ToArray();
        return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 按字符注入文本（KEYEVENTF_UNICODE）：直接把字符本身投递给前台窗口，
    /// 不受系统 CapsLock / Shift 状态影响，屏幕键盘用它输入字母、数字与符号。
    /// </summary>
    public static uint TypeChar(char ch)
    {
        if (ch == '\0') return 0;

        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE }
            }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            }
        };
        return SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 与 <see cref="Send"/> 相同的组合键注入，但逐个事件发送、事件间隔约 30ms，
    /// 更接近真人按键节奏。部分游戏/工具（如小黄鸭）对整串瞬间注入的热键反应不稳定，
    /// 拉开间隔可显著提高命中率。
    /// </summary>
    public static async Task<uint> SendAsync(int vk, int mods)
    {
        if (vk == 0) return 0;

        var ctrl = (mods & ModCtrl) != 0;
        var shift = (mods & ModShift) != 0;
        var alt = (mods & ModAlt) != 0;
        var win = (mods & ModWin) != 0;
        var ext = IsExtended(vk);

        var keys = new List<(int Code, bool Up, bool Ext)>(8);
        if (ctrl) keys.Add((VkCtrl, false, false));
        if (shift) keys.Add((VkShift, false, false));
        if (alt) keys.Add((VkAlt, false, false));
        if (win) keys.Add((VkWin, false, true));
        keys.Add((vk, false, ext));
        keys.Add((vk, true, ext));
        if (win) keys.Add((VkWin, true, true));
        if (alt) keys.Add((VkAlt, true, false));
        if (shift) keys.Add((VkShift, true, false));
        if (ctrl) keys.Add((VkCtrl, true, false));

        uint sent = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            var (code, up, extended) = keys[i];
            sent += SendOne(code, up, extended);
            if (i < keys.Count - 1) await Task.Delay(30);
        }
        return sent;
    }

    /// <summary>注入单次按键事件，返回 SendInput 是否成功（1=成功）</summary>
    private static uint SendOne(int vk, bool up, bool ext)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = (uint)((up ? KEYEVENTF_KEYUP : 0) | (ext ? KEYEVENTF_EXTENDEDKEY : 0)),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        return SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 只注入一次按键按下或抬起（不配对），并带上标记值 dwExtraInfo。
    /// 供键盘钩子在"Win 键被吞掉后补发"时使用：标记让钩子能识别自己注入的事件并放行，避免自吞。
    /// </summary>
    public static void SendKeyOnly(int vk, bool up, IntPtr extraInfo)
    {
        if (vk == 0) return;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = extraInfo
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 把鼠标按键（左/右/中/侧键/滚轮）注入到当前前台窗口（SendInput）。
    /// 与键盘一致：调用前应先把 GameLocker 窗口收起，否则事件会落在自己身上。
    /// </summary>
    public static void SendMouse(int button)
    {
        var inputs = new List<INPUT>(2);
        void M(uint flags, uint data) => inputs.Add(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = data,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });

        switch (button)
        {
            case MouseLeft: M(MOUSEEVENTF_LEFTDOWN, 0); M(MOUSEEVENTF_LEFTUP, 0); break;
            case MouseRight: M(MOUSEEVENTF_RIGHTDOWN, 0); M(MOUSEEVENTF_RIGHTUP, 0); break;
            case MouseMiddle: M(MOUSEEVENTF_MIDDLEDOWN, 0); M(MOUSEEVENTF_MIDDLEUP, 0); break;
            case MouseX1: M(MOUSEEVENTF_XDOWN, XBUTTON1); M(MOUSEEVENTF_XUP, XBUTTON1); break;
            case MouseX2: M(MOUSEEVENTF_XDOWN, XBUTTON2); M(MOUSEEVENTF_XUP, XBUTTON2); break;
            case MouseWheelUp: M(MOUSEEVENTF_WHEEL, WHEEL_DELTA); break;
            case MouseWheelDown: M(MOUSEEVENTF_WHEEL, unchecked((uint)-WHEEL_DELTA)); break;
            default: return;
        }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    // ---- SendInput P/Invoke ----

    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;
    private const int WHEEL_DELTA = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
