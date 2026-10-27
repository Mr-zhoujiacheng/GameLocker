using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>屏幕键盘的输入方式：全键拼音 / 九键拼音 / 五笔（86 版）</summary>
internal enum KeyboardMode
{
    Full,
    NineKey,
    Wubi
}

/// <summary>
/// 屏幕键盘主件：独立 Win32 窗口（置顶 / 不抢焦点 / 不激活），贴在屏幕左或右侧边缘，
/// 竖向窄条内分三列（字母 / 数字符号 / 功能），每列可滚动，适合掌机竖屏边缘点按打字。
/// 因为窗口带 WS_EX_NOACTIVATE，点按键不会夺走前台应用的焦点，
/// 字符通过 SendInput(KEYEVENTF_UNICODE) 直接落到当前前台应用里。
/// </summary>
internal sealed class OnScreenKeyboardWindow : IDisposable
{
    private const string ClassName = "GameLockerOnScreenKeyboard";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;
    private const int SPI_GETWORKAREA = 0x0030;
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DESTROY = 0x0002;
    private const int LWA_ALPHA = 0x00000002;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int DT_CENTER = 0x0001;
    private const int DT_VCENTER = 0x0004;
    private const int DT_SINGLELINE = 0x0020;
    private const int TRANSPARENT = 1;
    private const uint GRADIENT_FILL_RECT_V = 0;

    /// <summary>按键动作类型</summary>
    private enum KeyAction
    {
        Insert,     // 插入一个字符
        Backspace,
        Enter,
        Space,
        Tab,
        Escape,
        Caps,       // 大小写切换
        Chinese,    // 中/英（拼音）模式切换
        NineDigit,  // 九键数字键（中文拼拼音、英文连按循环字母）
        Close       // 关闭键盘
    }

    /// <summary>一个按键：文字、字符、动作、命中矩形（客户区坐标）</summary>
    private sealed class KeyDef
    {
        public string Label = "";
        public char Character;
        public char Digit;          // 九键：该键的数字
        public string Letters = ""; // 九键：该数字对应的字母（abc / def …）
        public KeyAction Action;
        public RECT Rect;
    }

    private static WndProcDelegate? _wndProc; // 防止回调被 GC
    private static bool _registered;

    private IntPtr _hwnd;
    private IntPtr _instance;
    private double _scale = 1;
    private int _width;
    private int _height;
    private bool _dockRight;

    private IntPtr _memDc;
    private IntPtr _memBitmap;
    private IntPtr _oldBitmap;
    private IntPtr _fontKey;
    private IntPtr _fontSmall;
    private IntPtr _bgBrush;
    private IntPtr _colBrush;
    private IntPtr _shadowBrush;    // 键帽投影
    private IntPtr _keySideBrush;   // 键帽侧壁（深色基座）
    private IntPtr _highlightPen;   // 键帽顶部受光的高光细线
    private IntPtr _iconPen;

    private readonly List<KeyDef> _keys = [];
    private int _scrollY;        // 整体滚动偏移（内容放不下时才生效）
    private int _contentH;       // 全部按键内容高度
    private int _viewH;          // 可用高度

    private KeyDef? _pressed;
    private bool _dragging;
    private POINT _dragStart;
    private int _dragLastY;
    private bool _caps;
    private bool _visible;
    private bool _qwerty;                        // 字母按 QWERTY 排列（false=a–z 顺序）
    private KeyboardMode _mode = KeyboardMode.Full;
    private bool _chinese;                        // 中文拼音模式
    private readonly System.Text.StringBuilder _pinyin = new(); // 拼音/五笔/九键缓冲
    private KeyDef? _lastNineKey;                 // 九键英文连按循环用
    private int _nineTap;
    private long _nineTapAt;
    private List<string> _candidates = [];        // 当前候选
    private readonly List<(RECT Rect, string Text)> _candHits = []; // 候选按钮命中区
    private int _headerH;                         // 表头高度（含候选行）

    public bool IsVisible => _visible;

    /// <summary>创建窗口（不显示）</summary>
    public bool Create()
    {
        try
        {
            _instance = GetModuleHandle(null);
            if (!_registered)
            {
                _wndProc = WndProc;
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = _instance,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName
                };
                RegisterClassExW(ref wc);
                _registered = true;
            }

            _hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                ClassName, "GameLocker Keyboard", WS_POPUP,
                0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;

            SetLayeredWindowAttributes(_hwnd, 0, 236, LWA_ALPHA);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>贴哪一侧（true=屏幕右侧）</summary>
    public void SetDock(bool dockRight)
    {
        if (_dockRight == dockRight) return;
        _dockRight = dockRight;
        if (_visible) Reposition();
    }

    /// <summary>字母排列：false=a–z 顺序，true=QWERTY</summary>
    public void SetQwerty(bool qwerty)
    {
        if (_qwerty == qwerty) return;
        _qwerty = qwerty;
        if (_visible)
        {
            LayoutKeys();
            Invalidate();
        }
    }

    /// <summary>输入方式：全键拼音 / 九键 / 五笔</summary>
    public void SetMode(KeyboardMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _pinyin.Clear();
        _candidates = [];
        if (_visible)
        {
            LayoutKeys();
            Invalidate();
        }
    }

    /// <summary>显示（不激活、不抢焦点）</summary>
    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        Reposition();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _visible = true;
        KeyboardClickSound.Open(); // 提前打开音频设备，第一次按键也不会有启动延迟
        Invalidate();
    }

    /// <summary>隐藏键盘</summary>
    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_HIDE);
        _visible = false;
    }

    public void Dispose()
    {
        DestroyResources();
        KeyboardClickSound.Close();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _visible = false;
    }

    // ---- 布局 ----

    /// <summary>按当前屏幕工作区与 DPI 重算窗口位置与列/键布局</summary>
    private void Reposition()
    {
        var work = GetWorkArea();
        _scale = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);
        var s = _scale;

        _width = (int)Math.Round(Math.Clamp((work.Right - work.Left) * 0.23, 330 * s, 430 * s));
        _height = work.Bottom - work.Top;
        var x = _dockRight ? work.Right - _width : work.Left;
        var y = work.Top;

        _scrollY = 0;
        // 先准备好后台缓冲与按键布局，再改窗口尺寸：SetWindowPos 会同步派发 WM_PAINT，
        // 若此时后台缓冲还没建好会被跳过绘制，尺寸也可能对不上
        RecreateBackBuffer();
        LayoutKeys();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, _width, _height, SWP_NOACTIVATE);
    }

    /// <summary>
    /// 生成按键：字母每行 5 键（a–y 共 5 行，末行 z + 中/Aa/⌫/✕）→ 数字与标点 4×4 → 功能键一行 4 个。
    /// 行高按可用高度自适应（30~62），内容放不下时整体上下滚动。
    /// </summary>
    private void LayoutKeys()
    {
        var s = _scale;
        var pad = (int)Math.Round(10 * s);
        var hintH = HintHeight();
        // 候选行高度始终预留：切换中英文时按键位置和整体高度都保持不变
        _headerH = hintH + CandBarHeight() + (int)Math.Round(6 * s);
        var rowGap = (int)Math.Round(6 * s);
        var blockGap = (int)Math.Round(10 * s);
        var colGap = (int)Math.Round(5 * s);

        const int letterCols = 5;   // 字母每行 5 键
        const int letterRows = 6;   // a–y 占 5 行，末行是 z + 4 个功能键
        const int digitRows = 4;    // 数字标点 4 行 × 4 列
        const int funcRows = 1;     // 功能键一行 4 个
        var rows = letterRows + digitRows + funcRows;

        var top = pad + _headerH;
        _viewH = _height - pad - top;
        if (_mode == KeyboardMode.NineKey)
        {
            LayoutNineKey(pad, top, rowGap, blockGap, colGap);
            return;
        }

        var keyH = Math.Clamp((_viewH - (rows - 1) * rowGap - 2 * blockGap) / rows,
            (int)Math.Round(30 * s), (int)Math.Round(62 * s));

        _contentH = rows * keyH + (rows - 1) * rowGap + 2 * blockGap;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _contentH - _viewH));

        _keys.Clear();
        var y = top - _scrollY;

        // ---- 字母：前 5 行 a–y（或 QWERTY 顺序的前 25 个），每行 5 键 ----
        var letters = _qwerty ? "qwertyuiopasdfghjklzxcvbnm" : "abcdefghijklmnopqrstuvwxyz";
        var letterW = (_width - pad * 2 - (letterCols - 1) * colGap) / letterCols;
        for (var row = 0; row < letterRows - 1; row++)
        {
            for (var i = 0; i < letterCols; i++)
            {
                var ch = letters[row * letterCols + i];
                AddKey(new KeyDef { Label = ch.ToString(), Character = ch, Action = KeyAction.Insert },
                    pad + i * (letterW + colGap), y, letterW, keyH);
            }
            y += keyH + rowGap;
        }

        // ---- 末行：第 26 个字母后面接 中 / Aa / 退格 / 关闭（正好 5 键） ----
        var lastRow = new[]
        {
            new KeyDef { Label = letters[25].ToString(), Character = letters[25], Action = KeyAction.Insert },
            new KeyDef { Label = "中", Action = KeyAction.Chinese },
            new KeyDef { Label = "Aa", Action = KeyAction.Caps },
            new KeyDef { Label = "←", Action = KeyAction.Backspace },
            new KeyDef { Label = "×", Action = KeyAction.Close }
        };
        var lastX = pad + (letterCols - lastRow.Length) * (letterW + colGap) / 2;
        for (var i = 0; i < lastRow.Length; i++)
            AddKey(lastRow[i], lastX + i * (letterW + colGap), y, letterW, keyH);
        y += keyH + rowGap + blockGap;

        // ---- 数字与标点：4 列 × 4 行（16 个最常用） ----
        var digitW = (_width - pad * 2 - 3 * colGap) / 4;
        const string digitKeys = "1234567890.,?!-_";
        for (var i = 0; i < digitKeys.Length; i++)
        {
            var row = i / 4;
            var col = i % 4;
            AddKey(new KeyDef { Label = digitKeys[i].ToString(), Character = digitKeys[i], Action = KeyAction.Insert },
                pad + col * (digitW + colGap), y + row * (keyH + rowGap), digitW, keyH);
        }
        y += digitRows * (keyH + rowGap) + blockGap;

        // ---- 功能键：一行 4 个（关闭已移到字母末行） ----
        var funcCols = 4;
        var funcW = (_width - pad * 2 - (funcCols - 1) * colGap) / funcCols;
        var funcs = new[]
        {
            new KeyDef { Label = "空格", Action = KeyAction.Space },
            new KeyDef { Label = "回车", Action = KeyAction.Enter },
            new KeyDef { Label = "Tab", Action = KeyAction.Tab },
            new KeyDef { Label = "Esc", Action = KeyAction.Escape }
        };
        for (var i = 0; i < funcs.Length; i++)
            AddKey(funcs[i], pad + i * (funcW + colGap), y, funcW, keyH);

        BuildCandidateRects(pad, colGap);
    }

    /// <summary>
    /// 九键布局：3×3 数字字母键（2 abc … 9 wxyz）+ 中/Aa/退格/关闭 + 功能键一行。
    /// 只去掉字母键区，数字键本身就是 3×3，不再重复铺数字标点块。
    /// </summary>
    private void LayoutNineKey(int pad, int top, int rowGap, int blockGap, int colGap)
    {
        var s = _scale;
        const int gridRows = 3;
        var rows = gridRows + 2;   // 九键 + 控制行 + 功能行
        var keyH = Math.Clamp((_viewH - (rows - 1) * rowGap - blockGap) / rows,
            (int)Math.Round(56 * s), (int)Math.Round(120 * s));

        _contentH = rows * keyH + (rows - 1) * rowGap + blockGap;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _contentH - _viewH));

        _keys.Clear();
        var y = top - _scrollY;
        var w = _width - pad * 2;
        var keyW = (w - 2 * colGap) / 3;

        var cells = new (string Label, char Digit, string Letters)[]
        {
            ("1", '1', ""),
            ("2 abc", '2', "abc"),
            ("3 def", '3', "def"),
            ("4 ghi", '4', "ghi"),
            ("5 jkl", '5', "jkl"),
            ("6 mno", '6', "mno"),
            ("7 pqrs", '7', "pqrs"),
            ("8 tuv", '8', "tuv"),
            ("9 wxyz", '9', "wxyz")
        };
        for (var i = 0; i < cells.Length; i++)
        {
            var (label, digit, letters) = cells[i];
            AddKey(new KeyDef
            {
                Label = label,
                Character = digit,
                Digit = digit,
                Letters = letters,
                Action = letters.Length > 0 ? KeyAction.NineDigit : KeyAction.Insert
            }, pad + i % 3 * (keyW + colGap), y + i / 3 * (keyH + rowGap), keyW, keyH);
        }
        y += gridRows * (keyH + rowGap) + blockGap;

        // 控制行：中 / Aa / 退格 / 关闭（九键没有字母键，z 那格换成这排）
        var ctrl = new[]
        {
            new KeyDef { Label = "中", Action = KeyAction.Chinese },
            new KeyDef { Label = "Aa", Action = KeyAction.Caps },
            new KeyDef { Label = "←", Action = KeyAction.Backspace },
            new KeyDef { Label = "×", Action = KeyAction.Close }
        };
        var ctrlW = (w - 3 * colGap) / 4;
        for (var i = 0; i < ctrl.Length; i++)
            AddKey(ctrl[i], pad + i * (ctrlW + colGap), y, ctrlW, keyH);
        y += keyH + rowGap + blockGap;

        // 功能行：空格 / 回车 / Tab / Esc
        var funcs = new[]
        {
            new KeyDef { Label = "空格", Action = KeyAction.Space },
            new KeyDef { Label = "回车", Action = KeyAction.Enter },
            new KeyDef { Label = "Tab", Action = KeyAction.Tab },
            new KeyDef { Label = "Esc", Action = KeyAction.Escape }
        };
        var funcW = (w - 3 * colGap) / 4;
        for (var i = 0; i < funcs.Length; i++)
            AddKey(funcs[i], pad + i * (funcW + colGap), y, funcW, keyH);

        BuildCandidateRects(pad, colGap);
    }

    private void AddKey(KeyDef key, int left, int top, int width, int height)
    {
        key.Rect = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
        _keys.Add(key);
    }

    /// <summary>顶部提示行高度</summary>
    private int HintHeight() => (int)Math.Round(24 * _scale);

    /// <summary>候选行高度（矮一些，候选字横排点按）</summary>
    private int CandBarHeight() => (int)Math.Round(32 * _scale);

    /// <summary>按当前候选数把一行候选均分到窗口宽度，生成可点击命中区</summary>
    private void BuildCandidateRects(int pad, int gap)
    {
        _candHits.Clear();
        if (!_chinese || _candidates.Count == 0) return;

        // 候选行紧贴在提示行下方，尽量靠近按键
        var top = pad + HintHeight();
        var height = CandBarHeight();
        // 窄条时最多显示几个候选：每个候选（最多两个字）按 46 像素起算，保证点得准
        var maxFit = Math.Max(3, (_width - pad * 2 + gap) / ((int)Math.Round(46 * _scale) + gap));
        var n = Math.Min(_candidates.Count, maxFit);
        var itemW = (_width - pad * 2 - (n - 1) * gap) / n;
        for (var i = 0; i < n; i++)
        {
            var left = pad + i * (itemW + gap);
            _candHits.Add((new RECT { Left = left, Top = top, Right = left + itemW, Bottom = top + height },
                _candidates[i]));
        }
    }

    // ---- 绘制 ----

    private void RecreateBackBuffer()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memDc, _oldBitmap);
            if (_memBitmap != IntPtr.Zero) DeleteObject(_memBitmap);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            _memBitmap = IntPtr.Zero;
            _oldBitmap = IntPtr.Zero;
        }
        var screen = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screen);
        _memBitmap = CreateCompatibleBitmap(screen, _width, _height);
        _oldBitmap = SelectObject(_memDc, _memBitmap);
        ReleaseDC(IntPtr.Zero, screen);
    }

    private void EnsureResources()
    {
        if (_fontKey != IntPtr.Zero) return;
        var s = _scale;
        _fontKey = CreateFontW(-(int)Math.Round(20 * s), 0, 0, 0, 500, 0, 0, 0,
            (uint)FontCharSet.Unicode, 0, 0, 0, 0, "Microsoft YaHei UI");
        _fontSmall = CreateFontW(-(int)Math.Round(12 * s), 0, 0, 0, 400, 0, 0, 0,
            (uint)FontCharSet.Unicode, 0, 0, 0, 0, "Microsoft YaHei UI");

        _bgBrush = CreateSolidBrush(Rgb(22, 22, 26));
        _colBrush = CreateSolidBrush(Rgb(38, 38, 44));
        _shadowBrush = CreateSolidBrush(Rgb(0, 0, 0));
        _keySideBrush = CreateSolidBrush(Rgb(16, 16, 20));
        _highlightPen = CreatePen(0, 1, Rgb(132, 132, 150));
        // 退格箭头 / 关闭叉号直接用线条画，任何 DPI 下都清楚
        _iconPen = CreatePen(0, Math.Max(2, (int)Math.Round(2 * s)), Rgb(236, 236, 240));
    }

    private void DestroyResources()
    {
        foreach (var h in new[] { _fontKey, _fontSmall, _bgBrush, _colBrush, _shadowBrush, _keySideBrush, _highlightPen, _iconPen })
            if (h != IntPtr.Zero) DeleteObject(h);
        _fontKey = _fontSmall = IntPtr.Zero;
        _bgBrush = _colBrush = _shadowBrush = _keySideBrush = _highlightPen = _iconPen = IntPtr.Zero;
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _memDc == IntPtr.Zero) return;
        EnsureResources();
        var s = _scale;
        var pad = (int)Math.Round(10 * s);
        var header = _headerH;
        var hintH = HintHeight();

        var full = new RECT { Left = 0, Top = 0, Right = _width, Bottom = _height };
        FillRect(_memDc, ref full, _bgBrush);

        // 顶部提示 + 关闭提示
        SelectObject(_memDc, _fontSmall);
        SetBkMode(_memDc, TRANSPARENT);
        SetTextColor(_memDc, Rgb(150, 150, 160));
        var tip = new RECT { Left = pad, Top = pad, Right = _width - pad, Bottom = pad + hintH };
        var tipText = !_chinese
            ? "点目标应用后直接打字"
            : _pinyin.Length > 0
                ? _pinyin.ToString()
                : _mode switch
                {
                    KeyboardMode.Wubi => "五笔输入",
                    KeyboardMode.NineKey => "九键拼音",
                    _ => "拼音输入"
                };
        DrawTextW(_memDc, tipText, -1, ref tip, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        // 中文模式：一行候选字，点一下就把该字注入前台应用
        for (var i = 0; i < _candHits.Count; i++)
        {
            var (rect, text) = _candHits[i];
            var face = DrawKeycap(rect, false, false);
            SelectObject(_memDc, _fontKey);
            SetTextColor(_memDc, Rgb(236, 236, 240));
            DrawTextW(_memDc, text, -1, ref face, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        }

        // 按键：统一绘制（滚动偏移已在布局阶段扣掉），裁到按键区
        var clip = new RECT
        {
            Left = pad,
            Top = pad + header,
            Right = _width - pad,
            Bottom = _height - pad
        };
        var saved = SaveDC(_memDc);
        IntersectClipRect(_memDc, clip.Left, clip.Top, clip.Right, clip.Bottom);

        foreach (var k in _keys)
        {
            if (k.Rect.Bottom < clip.Top || k.Rect.Top > clip.Bottom) continue;
            var pressed = ReferenceEquals(k, _pressed);
            var active = pressed || (k.Action == KeyAction.Chinese && _chinese);
            var face = DrawKeycap(k.Rect, pressed, active);

            if (k.Action is KeyAction.Backspace or KeyAction.Close)
            {
                DrawGlyphIcon(k, face);
                continue;
            }

            var text = k.Action == KeyAction.Insert && _caps && char.IsLetter(k.Character)
                ? char.ToUpperInvariant(k.Character).ToString()
                : k.Label;
            SelectObject(_memDc, _fontKey);
            SetTextColor(_memDc, active ? Rgb(255, 255, 255) : Rgb(236, 236, 240));
            DrawTextW(_memDc, text, -1, ref face, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        }
        RestoreDC(_memDc, saved);

        // 内容超出可用高度时，底部提示还能上下滑
        if (_contentH > _viewH)
        {
            SelectObject(_memDc, _fontSmall);
            SetTextColor(_memDc, Rgb(120, 120, 130));
            var tipRect = new RECT
            {
                Left = pad,
                Top = _height - pad - (int)Math.Round(16 * s),
                Right = _width - pad,
                Bottom = _height - pad
            };
            DrawTextW(_memDc, _scrollY + _viewH < _contentH ? "▼" : "▲", -1, ref tipRect,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        }

        var hdc = GetDC(_hwnd);
        BitBlt(hdc, 0, 0, _width, _height, _memDc, 0, 0, 0x00CC0020);
        ReleaseDC(_hwnd, hdc);
    }

    /// <summary>
    /// 画一颗机械键帽：底面投影 + 深色侧壁 + 垂直渐变顶面；按下时顶面下沉、侧壁变薄。
    /// 返回顶面矩形，文字与图标都画在顶面上，视觉上才像嵌在键帽里。
    /// </summary>
    private RECT DrawKeycap(RECT cell, bool pressed, bool accent)
    {
        var s = _scale;
        var radius = (int)Math.Round(8 * s);
        var inset = (int)Math.Round(2 * s);
        var depth = Math.Max(3, (int)Math.Round(5 * s));   // 侧壁可见高度

        // 键帽不描边，靠投影和侧壁区分边界
        SelectObject(_memDc, GetStockObject(8));

        if (!pressed)
        {
            // 投影稍向右下偏移，让键帽有浮起感
            var off = Math.Max(2, (int)Math.Round(4 * s));
            var shadow = new RECT
            {
                Left = cell.Left + off / 2,
                Top = cell.Top + off,
                Right = cell.Right + off,
                Bottom = cell.Bottom + off
            };
            SelectObject(_memDc, _shadowBrush);
            RoundRect(_memDc, shadow.Left, shadow.Top, shadow.Right, shadow.Bottom, radius, radius);
        }

        // 侧壁（键帽基座）：整格先铺深色，顶面再盖上去露出下沿
        SelectObject(_memDc, _keySideBrush);
        RoundRect(_memDc, cell.Left, cell.Top, cell.Right, cell.Bottom, radius, radius);

        var sink = pressed ? depth : 0;   // 按下时顶面下沉，侧壁只剩一点点
        var face = new RECT
        {
            Left = cell.Left + inset,
            Top = cell.Top + inset + sink,
            Right = cell.Right - inset,
            Bottom = cell.Bottom - inset - depth + sink
        };

        int topColor, bottomColor;
        if (accent)
        {
            topColor = pressed ? Rgb(100, 150, 242) : Rgb(124, 174, 255);
            bottomColor = pressed ? Rgb(52, 86, 170) : Rgb(66, 104, 198);
        }
        else
        {
            topColor = pressed ? Rgb(66, 66, 78) : Rgb(96, 96, 112);
            bottomColor = pressed ? Rgb(40, 40, 48) : Rgb(52, 52, 62);
        }
        FillVerticalGradient(face, topColor, bottomColor, Math.Max(2, radius - inset));

        // 顶面靠上的一条细高光，模拟键帽倒角受光；按下时不画，才有沉下去的感觉
        if (!pressed)
        {
            var rx = Math.Min(radius * 2, (face.Right - face.Left) / 2);
            SelectObject(_memDc, _highlightPen);
            MoveToEx(_memDc, face.Left + rx, face.Top + 1, IntPtr.Zero);
            LineTo(_memDc, face.Right - rx, face.Top + 1);
            SelectObject(_memDc, GetStockObject(8));
        }
        return face;
    }

    /// <summary>圆角区域内做垂直渐变（GDI GradientFill + 圆角剪裁区域）</summary>
    private void FillVerticalGradient(RECT rect, int topColor, int bottomColor, int radius)
    {
        var rgn = CreateRoundRectRgn(rect.Left, rect.Top, rect.Right + 1, rect.Bottom + 1, radius * 2, radius * 2);
        var saved = SaveDC(_memDc);
        SelectClipRgn(_memDc, rgn);

        var vertex = new TRIVERTEX[2];
        vertex[0].X = rect.Left;
        vertex[0].Y = rect.Top;
        vertex[0].Red = (ushort)((topColor & 0xFF) << 8);
        vertex[0].Green = (ushort)(((topColor >> 8) & 0xFF) << 8);
        vertex[0].Blue = (ushort)(((topColor >> 16) & 0xFF) << 8);
        vertex[0].Alpha = 0xFFFF;
        vertex[1].X = rect.Right;
        vertex[1].Y = rect.Bottom;
        vertex[1].Red = (ushort)((bottomColor & 0xFF) << 8);
        vertex[1].Green = (ushort)(((bottomColor >> 8) & 0xFF) << 8);
        vertex[1].Blue = (ushort)(((bottomColor >> 16) & 0xFF) << 8);
        vertex[1].Alpha = 0xFFFF;

        var mesh = new[] { new GRADIENT_RECT { UpperLeft = 0, LowerRight = 1 } };
        GradientFill(_memDc, vertex, 2, mesh, 1, GRADIENT_FILL_RECT_V);

        RestoreDC(_memDc, saved);
        DeleteObject(rgn);
    }

    /// <summary>退格画左箭头、关闭画叉号：线条绘制，不依赖字体里有没有这些字形</summary>
    private void DrawGlyphIcon(KeyDef key, RECT face)
    {
        var s = _scale;
        var cx = (face.Left + face.Right) / 2;
        var cy = (face.Top + face.Bottom) / 2;
        SelectObject(_memDc, _iconPen);
        if (key.Action == KeyAction.Backspace)
        {
            var half = (int)Math.Round(12 * s);
            var head = (int)Math.Round(5 * s);
            MoveToEx(_memDc, cx - half, cy, IntPtr.Zero);
            LineTo(_memDc, cx + half, cy);
            MoveToEx(_memDc, cx - half, cy, IntPtr.Zero);
            LineTo(_memDc, cx - half + head, cy - head);
            MoveToEx(_memDc, cx - half, cy, IntPtr.Zero);
            LineTo(_memDc, cx - half + head, cy + head);
        }
        else
        {
            var half = (int)Math.Round(9 * s);
            MoveToEx(_memDc, cx - half, cy - half, IntPtr.Zero);
            LineTo(_memDc, cx + half, cy + half);
            MoveToEx(_memDc, cx - half, cy + half, IntPtr.Zero);
            LineTo(_memDc, cx + half, cy - half);
        }
        // 画完图标把画笔换回空画笔，否则后面的键帽会被粗白线描边
        SelectObject(_memDc, GetStockObject(8));
    }

    private void Invalidate()
    {
        if (_hwnd == IntPtr.Zero) return;
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // ---- 命中与交互 ----

    /// <summary>找出客户区坐标下的按键</summary>
    private KeyDef? HitTest(int x, int y)
    {
        var pad = (int)Math.Round(10 * s);
        if (y < pad || y > _height - pad) return null;
        foreach (var k in _keys)
            if (x >= k.Rect.Left && x < k.Rect.Right && y >= k.Rect.Top && y < k.Rect.Bottom)
                return k;
        return null;
    }

    private double s => _scale;

    /// <summary>整体滚动（内容高于可用高度时才有效果）</summary>
    private void ScrollContent(int delta)
    {
        var max = Math.Max(0, _contentH - _viewH);
        var next = Math.Clamp(_scrollY + delta, 0, max);
        if (next == _scrollY) return;
        _scrollY = next;
        LayoutKeys();
        Invalidate();
    }

    private void Execute(KeyDef key)
    {
        switch (key.Action)
        {
            case KeyAction.Caps:
                _caps = !_caps;
                Invalidate();
                break;
            case KeyAction.Chinese:
                _chinese = !_chinese;
                _pinyin.Clear();
                _candidates = [];
                LayoutKeys();
                Invalidate();
                break;
            case KeyAction.Close:
                Hide();
                break;
            case KeyAction.Insert:
                if (_chinese && char.IsLetter(key.Character))
                {
                    // 拼音模式：字母只进缓冲，查出候选后再注入汉字
                    _pinyin.Append(char.ToLowerInvariant(key.Character));
                    RefreshCandidates();
                }
                else
                {
                    KeySendService.TypeChar(_caps && char.IsLetter(key.Character)
                        ? char.ToUpperInvariant(key.Character) : key.Character);
                }
                break;
            case KeyAction.NineDigit:
                if (_chinese)
                {
                    // 九键拼音：数字先进缓冲，再按手机键盘的可能音节出候选
                    _pinyin.Append(key.Digit);
                    RefreshCandidates();
                }
                else
                {
                    InsertNineLetter(key);
                }
                break;
            case KeyAction.Backspace:
                if (_chinese && _pinyin.Length > 0)
                {
                    _pinyin.Length--;
                    RefreshCandidates();
                }
                else
                {
                    KeySendService.Send(0x08, 0);
                }
                break;
            case KeyAction.Enter:
                if (_chinese && _candidates.Count > 0) CommitCandidate(_candidates[0]);
                else KeySendService.Send(0x0D, 0);
                break;
            case KeyAction.Space:
                if (_chinese && _candidates.Count > 0) CommitCandidate(_candidates[0]);
                else KeySendService.TypeChar(' ');
                break;
            case KeyAction.Tab:
                KeySendService.Send(0x09, 0);
                break;
            case KeyAction.Escape:
                KeySendService.Send(0x1B, 0);
                break;
        }
    }

    /// <summary>拼音/五笔/九键缓冲变化后重新查候选并重排</summary>
    private void RefreshCandidates()
    {
        var buffer = _pinyin.ToString();
        _candidates = [.. (_mode switch
        {
            KeyboardMode.Wubi when _chinese => WubiDictionary.Query(buffer),
            KeyboardMode.NineKey when _chinese => PinyinDictionary.QueryDigits(buffer),
            _ => PinyinDictionary.Query(buffer)
        })];
        LayoutKeys();
        Invalidate();
    }

    /// <summary>九键英文输入：同一键连按在 abc/def… 里循环，和手机键盘一致</summary>
    private void InsertNineLetter(KeyDef key)
    {
        var letters = key.Letters;
        if (letters.Length == 0) return;

        var now = Environment.TickCount64;
        if (ReferenceEquals(_lastNineKey, key) && now - _nineTapAt < 900)
        {
            _nineTap = (_nineTap + 1) % letters.Length;
            KeySendService.Send(0x08, 0);   // 删掉上一次循环出的字母
        }
        else
        {
            _nineTap = 0;
        }
        _lastNineKey = key;
        _nineTapAt = now;

        var ch = letters[_nineTap];
        KeySendService.TypeChar(_caps ? char.ToUpperInvariant(ch) : ch);
    }

    /// <summary>把候选字（1~2 个汉字）逐个注入前台应用，并清空拼音缓冲</summary>
    private void CommitCandidate(string text)
    {
        foreach (var ch in text) KeySendService.TypeChar(ch);
        _pinyin.Clear();
        _candidates = [];
        LayoutKeys();
        Invalidate();
    }

    // ---- 窗口过程 ----

    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "keyboard.log");

    /// <summary>回调日志：窗口过程里的异常若穿到原生消息分发栈会直接崩进程，必须先兜住</summary>
    private static void LogError(uint msg, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} msg=0x{msg:X4} {ex}\r\n");
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return WndProcCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            LogError(msg, ex);
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private IntPtr WndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                PAINTSTRUCT ps;
                BeginPaint(hWnd, out ps);
                try { Render(); }
                finally { EndPaint(hWnd, ref ps); }
                return IntPtr.Zero;
            }
            case WM_ERASEBKGND:
                return (IntPtr)1;
            case WM_LBUTTONDOWN:
            {
                var x = (short)((long)lParam & 0xFFFF);
                var y = (short)(((long)lParam >> 16) & 0xFFFF);
                foreach (var (rect, text) in _candHits)
                {
                    if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
                    {
                        KeyboardClickSound.Play();
                        CommitCandidate(text);
                        return IntPtr.Zero;
                    }
                }
                var key = HitTest(x, y);
                if (key is not null)
                {
                    KeyboardClickSound.Play();
                    _pressed = key;
                    _dragging = false;
                    _dragStart = new POINT { X = x, Y = y };
                    _dragLastY = y;
                    SetCapture(hWnd);
                    Invalidate();
                }
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                if (_pressed is null) return IntPtr.Zero;
                var x = (short)((long)lParam & 0xFFFF);
                var y = (short)(((long)lParam >> 16) & 0xFFFF);
                var dy = y - _dragStart.Y;
                if (!_dragging && Math.Abs(dy) * 2 > Math.Round(18 * _scale))
                {
                    _dragging = true;
                    _pressed = null;
                }
                if (_dragging)
                {
                    var step = y - _dragLastY;
                    ScrollContent(-step);
                    _dragLastY = y;
                }
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                var key = _pressed;
                var wasDragging = _dragging;
                _pressed = null;
                _dragging = false;
                ReleaseCapture();
                Invalidate();
                if (key is not null && !wasDragging) Execute(key);
                return IntPtr.Zero;
            }
            case WM_MOUSEWHEEL:
            {
                var delta = ((short)(((long)wParam >> 16) & 0xFFFF)) / 120;
                ScrollContent(-delta * (int)Math.Round(60 * _scale));
                return IntPtr.Zero;
            }
            case WM_RBUTTONUP:
                Hide();
                return IntPtr.Zero;
            case WM_DPICHANGED:
                if (_visible)
                {
                    DestroyResources(); // 字号随 DPI 重建
                    Reposition();
                }
                return IntPtr.Zero;
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ---- Win32 ----

    private static RECT GetWorkArea()
    {
        var r = new RECT();
        SystemParametersInfoW(SPI_GETWORKAREA, 0, ref r, 0);
        return r;
    }

    private static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

    private enum FontCharSet : uint { Ansi = 0, Unicode = 1 }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    /// <summary>GradientFill 的顶点：颜色用 16 位通道（8 位值 &lt;&lt; 8）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TRIVERTEX
    {
        public int X;
        public int Y;
        public ushort Red;
        public ushort Green;
        public ushort Blue;
        public ushort Alpha;
    }

    /// <summary>GradientFill 的矩形网格：指向两个顶点下标</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GRADIENT_RECT
    {
        public uint UpperLeft;
        public uint LowerRight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, int colorKey, byte alpha, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(int action, int param, ref RECT rect, int winIni);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    // 必须声明 CharSet.Unicode：否则字符串按 ANSI(GBK) 封送后交给宽字符版 DrawTextW，
    // 中文与双字符标签会两两字节错位，显示成乱码
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int length, ref RECT rect, int format);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int style, int width, int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr oldPoint);

    [DllImport("gdi32.dll")]
    private static extern bool LineTo(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern int SaveDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool RestoreDC(IntPtr hdc, int saved);

    [DllImport("gdi32.dll")]
    private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int SelectClipRgn(IntPtr hdc, IntPtr rgn);

    [DllImport("msimg32.dll")]
    private static extern bool GradientFill(IntPtr hdc, [In] TRIVERTEX[] vertex, uint vertexCount,
        [In] GRADIENT_RECT[] mesh, uint meshCount, uint mode);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int rop);
}

/// <summary>屏幕键盘的生命周期管理：同一时刻只有一个窗口，再次触发即关闭</summary>
internal static class OnScreenKeyboard
{
    private static OnScreenKeyboardWindow? _window;

    /// <summary>键盘是否正在显示</summary>
    public static bool IsVisible => _window is { IsVisible: true };

    /// <summary>最近一次失败原因（诊断用）</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>显示/隐藏键盘；dockRight=true 贴屏幕右侧，qwerty=true 字母按 QWERTY 排列</summary>
    public static bool Toggle(bool dockRight, bool qwerty, KeyboardMode mode)
    {
        try
        {
            if (_window is null)
            {
                var w = new OnScreenKeyboardWindow();
                if (!w.Create())
                {
                    LastError = "创建键盘窗口失败";
                    return false;
                }
                _window = w;
            }
            if (_window.IsVisible)
            {
                _window.Hide();
                return true;
            }
            _window.SetDock(dockRight);
            _window.SetQwerty(qwerty);
            _window.SetMode(mode);
            _window.Show();
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>编辑页改了贴边方向后实时生效（键盘打开时立刻换边）</summary>
    public static void ApplyDock(bool dockRight)
    {
        if (_window is { IsVisible: true }) _window.SetDock(dockRight);
    }

    /// <summary>编辑页改了字母排列后实时生效（键盘打开时立刻换排列）</summary>
    public static void ApplyLayout(bool qwerty)
    {
        if (_window is { IsVisible: true }) _window.SetQwerty(qwerty);
    }

    /// <summary>编辑页改了输入方式（全键/九键/五笔）后实时生效</summary>
    public static void ApplyMode(KeyboardMode mode)
    {
        if (_window is { IsVisible: true }) _window.SetMode(mode);
    }
}
