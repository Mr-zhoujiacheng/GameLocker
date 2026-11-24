using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace GameLocker.Services;

/// <summary>
/// 底栏窗口预览：一个窗口一张卡，在底栏上方摆成和主页抽屉一样的扇形手牌。
///
/// 卡面与 <see cref="GameLocker.MainPage.BuildDrawerCard"/> 完全同一套观感：
/// 强调色三段渐变、12 DIP 圆角、半透明白描边、标题行（窗口图标 + 标题 + 关闭 ×）；
/// 卡片按抽屉手牌的方式叠压、中间高两侧低、微微倾斜，鼠标移到哪张哪张就转正、放大、抬到最上层，
/// 再点一下才激活对应窗口（和抽屉手牌“先选中，再确认”的手感一致）。
///
/// 弹层是 WS_EX_LAYERED + WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW 的逐像素透明窗口，
/// 不抢焦点、卡片外的区域完全透明。卡片里的窗口画面用 PrintWindow(PW_RENDERFULLCONTENT)
/// 抓取一张快照（不注入目标进程）；鼠标换到另一张卡时只刷新那一张，兼顾观感与开销。
/// 所有方法都必须在 UI 线程调用（窗口在 UI 线程创建）。
/// </summary>
internal static class DockPreviewHost
{
    private const string ClassName = "GameLockerDockPreview";

    // ---- 手牌几何（DIP，实际像素按 DPI 缩放；与 MainPage 抽屉手牌同源）----
    private const double CardWidthDip = 200;
    private const double CardHeightDip = 225;
    private const double HeaderDip = 38;
    private const double InnerPadDip = 8;
    private const double StepDip = 98;          // 相邻卡中心间距（最大叠压量）
    private const double EdgeDropDip = 34;      // 两侧比中间低多少
    private const double TiltDeg = 2.2;         // 每偏离中心一张的倾斜角
    private const double HotScale = 1.14;       // 选中卡放大倍率
    private const double PopGapDip = 10;        // 弹出后卡底与底栏上沿的间隙
    private const double TopMarginDip = 36;     // 弹出卡顶部到窗口上缘的余量
    private const double PushDip = 42;          // 选中卡把邻居往两侧推开的距离
    private const double CornerDip = 12;
    private const double CloseDip = 26;
    private const double IconDip = 18;
    private const double ShadowMarginXDip = 42;
    private const int MaxCards = 6;

    /// <summary>手牌动画时长与预览里抽屉手牌一致：300 ms。</summary>
    private const int AnimMs = 300;
    private const int AnimFrameMs = 16;

    private sealed class Card
    {
        public IntPtr Window;
        public string Title = "";
        public Bitmap? Snapshot;
        public Bitmap? Icon;
        /// <summary>烘焙好的卡面（渐变 + 快照 + 图标 + 标题），动画时只做一次 DrawImage，避免每帧重绘文字和图片。</summary>
        public Bitmap? Face;
        /// <summary>快照已更新但卡面还没重烘焙（动画结束后统一做，避免中途卡顿）。</summary>
        public bool FaceDirty;
        /// <summary>异步快照版本：换卡时旧任务回来要丢弃。</summary>
        public int SnapshotVersion;
        /// <summary>添加卡片（开始按钮手牌）：没有窗口，点击后回调 CommandId。</summary>
        public bool IsCommand;
        public int CommandId;
        public string Subtitle = "";
        public RectangleF Rect;   // 未选中、未旋转时的窗口坐标
        public double Tilt;
        public int Z;
        /// <summary>从当前藏匿位置抬到"底栏上方 10 DIP"所需的高度（像素，已含两侧下沉差）。</summary>
        public float HotLiftPx;

        // 动画中的当前值与起止值：切换/弹出时在 300 ms 内按抽屉手牌的缓动插值
        public float Scale = 1f;
        public float Angle;
        public float Lift;
        public float Push;
        public float StartScale = 1f;
        public float StartAngle;
        public float StartLift;
        public float StartPush;
        public float TargetScale = 1f;
        public float TargetAngle;
        public float TargetLift;
        public float TargetPush;
    }

    private static readonly List<Card> Cards = [];
    private static IntPtr _hwnd;
    private static bool _classRegistered;
    private static WndProc? _wndProc;

    /// <summary>UI 线程派发器：底栏的全局鼠标钩子线程会直接调 Hide()，GDI+ 资源必须回到 UI 线程释放。</summary>
    private static DispatcherQueue? _uiQueue;

    private static double _dpi = 1;
    private static int _windowX;
    private static int _windowY;
    private static int _windowW;
    private static int _windowH;
    private static int _hot = -1;
    private static bool _selectionMode;
    /// <summary>上一张弹出的卡：动画落下的 300 ms 内仍压在邻居上面，和抽屉手牌一致。</summary>
    private static int _lastHot = -1;
    private static int _dockTop;
    private static IntPtr _dockHwnd;
    /// <summary>弹出卡底边在窗口内的 Y（底栏上沿上方 10 DIP）。</summary>
    private static int _hotBottomLocal;
    private static Action<int>? _onCommandPick;
    private static bool _renderingHooked;
    private static DispatcherQueueTimer? _frameFallback;
    private static long _lastFrameTicks;
    private static int _snapshotDebounce;
    private static long _animStartMs;
    private static bool _animating;

    private static Color _colorTop;
    private static Color _colorMid;
    private static Color _colorBottom = Color.FromArgb(0xFF, 0x12, 0x14, 0x1A);

    private static IntPtr _memDc;
    private static IntPtr _screenDc;
    private static IntPtr _dib;
    private static IntPtr _oldBitmap;
    private static Graphics? _graphics;
    private static int _surfaceW;
    private static int _surfaceH;
    /// <summary>本次显示是否已经摆到屏幕并压到底栏后面（动画帧只更新像素，不再重复 SetWindowPos）。</summary>
    private static bool _placed;

    private static Font? _titleFont;
    private static double _fontDpi = -1;

    public static bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>手柄/键盘选卡：左右移动高亮，第一次选中会先把卡片弹出来。</summary>
    public static void SelectRelative(int delta)
    {
        if (_uiQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => SelectRelative(delta));
            return;
        }
        if (Cards.Count == 0) return;
        if (_hot < 0)
            SetHot(delta < 0 ? Cards.Count - 1 : 0);
        else
            SetHot((_hot + delta + Cards.Count) % Cards.Count);
    }

    /// <summary>手柄 A：没有选中卡时先选中第一张（弹出）；已选中时打开这张。</summary>
    public static bool ActivateSelected()
    {
        if (_uiQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => ActivateSelected());
            return true;
        }
        if (Cards.Count == 0) return false;
        if (_hot < 0)
        {
            SetHot(0);
            return true;
        }
        ActivateCard(_hot);
        return true;
    }

    private static void ActivateCard(int index)
    {
        if (index < 0 || index >= Cards.Count) return;
        var card = Cards[index];
        if (card.IsCommand)
        {
            var command = card.CommandId;
            var pick = _onCommandPick;
            Hide();
            pick?.Invoke(command);
            return;
        }
        var target = card.Window;
        Hide();
        ActivateWindow(target);
    }

    /// <summary>屏幕坐标是否落在预览层上（含卡片之间与到底栏之间的缝隙）。</summary>
    public static bool Contains(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || !IsWindowVisible(_hwnd)) return false;
        if (!GetWindowRect(_hwnd, out var rect)) return false;
        var slack = Px(6);
        if (y >= rect.Top - slack && y <= rect.Bottom + slack && x >= rect.Left - slack && x <= rect.Right + slack)
            return true;
        return y > rect.Bottom && y <= _dockTop && x >= rect.Left && x <= rect.Right;
    }

    /// <summary>
    /// 显示手牌；windows 为要展示的窗口（一个窗口 = 一张卡），anchorX 是底栏图标中心，
    /// dockTop/dockBottom 是底栏上/下沿，dockHwnd 是底栏窗口句柄（预览要排在它后面，
    /// 让底栏底板自然挡住卡片下半截，而不是把卡片裁掉）；accent 用于卡面强调色。
    /// </summary>
    public static void Show(IReadOnlyList<IntPtr> windows, int anchorX, int anchorY,
        int dockTop, int dockBottom, IntPtr dockHwnd,
        double scale = 1, global::Windows.UI.Color? accent = null, bool selectionMode = false)
    {
        if (windows.Count == 0) { Hide(); return; }

        try
        {
            _dpi = Math.Max(1, scale);
            EnsureWindow();

            var a = accent ?? global::Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
            BuildAccent(a);
            _dockTop = dockTop;
            _dockHwnd = dockHwnd;
            _onCommandPick = null;

            CleanupCards();
            foreach (var window in windows.Take(MaxCards))
            {
                Cards.Add(new Card
                {
                    Window = window,
                    Title = TitleOf(window),
                    Snapshot = CaptureWindow(window),
                    Icon = IconOf(window)
                });
            }
            if (Cards.Count == 0) { Hide(); return; }

            _selectionMode = selectionMode;
            Present(anchorX, anchorY, dockTop, dockBottom);
            DebugLog.Write($"[底栏] 预览显示 idx={_hot} windows={Cards.Count} select={selectionMode} hand={_windowW}x{_windowH}");
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 预览失败：" + ex);
            Hide();
        }
    }

    /// <summary>
    /// 开始按钮的手牌：两张添加卡片（自定义 / 小组件）。点击哪张就把 CommandId 回调给底栏。
    /// </summary>
    public static void ShowAddCards(int anchorX, int anchorY, int dockTop, int dockBottom, IntPtr dockHwnd,
        double scale = 1, global::Windows.UI.Color? accent = null, Action<int>? onPick = null, bool selectionMode = false)
    {
        try
        {
            _dpi = Math.Max(1, scale);
            EnsureWindow();

            var a = accent ?? global::Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
            BuildAccent(a);
            _dockTop = dockTop;
            _dockHwnd = dockHwnd;
            _onCommandPick = onPick;

            CleanupCards();
            Cards.Add(new Card
            {
                Title = "自定义",
                Subtitle = "程序 · 文件夹 · 网址",
                IsCommand = true,
                CommandId = 0
            });
            Cards.Add(new Card
            {
                Title = "小组件",
                Subtitle = "音乐 · 天气 · 性能…",
                IsCommand = true,
                CommandId = 1
            });

            _selectionMode = selectionMode;
            Present(anchorX, anchorY, dockTop, dockBottom);
            DebugLog.Write($"[底栏] 添加卡片显示 select={selectionMode} hand={_windowW}x{_windowH}");
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 添加卡片失败：" + ex);
            Hide();
        }
    }

    private static void Present(int anchorX, int anchorY, int dockTop, int dockBottom)
    {
        StopFrameLoop();
        ++_snapshotDebounce;
        _animating = false;
        _lastHot = -1;
        _placed = false;
        Layout(anchorX, anchorY, dockTop, dockBottom);
        foreach (var card in Cards) BuildFace(card);
        _hot = -1;   // 默认一张都不弹：全部保持缩小/倾斜的手牌状态，鼠标移到哪张才弹出哪张
        EnsureSurface(_windowW, _windowH);
        Render();
    }

    public static void Hide()
    {
        // 鼠标钩子线程、启动器/底栏切换都可能直接调到这里；GDI+ 资源只能在 UI 线程释放
        if (_uiQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(Hide);
            return;
        }
        try
        {
            StopFrameLoop();
            ++_snapshotDebounce;
            _animating = false;
            _hot = -1;
            _lastHot = -1;
            _placed = false;
            _onCommandPick = null;
            CleanupCards();
            DestroySurface();
            if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
        }
        catch { }
    }

    private static void CleanupCards()
    {
        foreach (var card in Cards)
        {
            card.Snapshot?.Dispose();
            card.Icon?.Dispose();
            card.Face?.Dispose();
        }
        Cards.Clear();
    }

    private static void Layout(int anchorX, int anchorY, int dockTop, int dockBottom)
    {
        var n = Cards.Count;
        var cardW = Px(CardWidthDip);
        var cardH = Px(CardHeightDip);
        var step = Px(StepDip);
        var mid = (n - 1) / 2.0;
        var fanW = cardW + step * Math.Max(0, n - 1);
        var marginX = Px(ShadowMarginXDip);
        var dockH = Math.Max(0, dockBottom - dockTop);

        // 窗口底边对齐底栏下沿，卡片完整画出来；预览窗口排在底栏窗口后面，
        // 让底栏底板自然盖住卡片下半截，不再用窗口边界裁切。
        _windowW = fanW + marginX * 2;
        _windowH = dockH + Px(PopGapDip) + (int)Math.Ceiling(cardH * HotScale) + Px(TopMarginDip);
        _hotBottomLocal = _windowH - dockH - Px(PopGapDip);

        var x = anchorX - _windowW / 2;
        var y = dockBottom - _windowH;

        if (MonitorFromPoint(new POINT { X = anchorX, Y = anchorY }, MONITOR_DEFAULTTONEAREST) is var monitor
            && monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var bounds = info.rcWork;
                if (x < bounds.Left) x = bounds.Left;
                if (x + _windowW > bounds.Right) x = bounds.Right - _windowW;
                if (y < bounds.Top) y = bounds.Top;
            }
        }

        _windowX = x;
        _windowY = y;

        // 单卡没有扇形下沉：卡底必须落在窗口底边，才能真正藏到一半在底栏后面
        var maxDrop = n > 1 ? Px(EdgeDropDip) : 0;
        for (var i = 0; i < n; i++)
        {
            var dist = mid > 0 ? Math.Abs(i - mid) / mid : 0;
            var drop = (int)Math.Round(Math.Pow(dist, 2) * Px(EdgeDropDip));
            var card = Cards[i];
            // 完整卡片的底边停在窗口底边（两侧/单卡）到窗口底边上方 34 DIP（多卡中心）之间；
            // 下半截被底栏挡住，卡片本身不裁切。
            var restBottom = _windowH - (maxDrop - drop);
            card.Rect = new RectangleF(
                marginX + i * step,
                restBottom - cardH,
                cardW,
                cardH);
            card.Tilt = (i - mid) * TiltDeg;
            card.HotLiftPx = restBottom - _hotBottomLocal;
            // 中间最高，越靠边越低（同抽屉手牌）
            card.Z = (int)Math.Max(1, 100 - Math.Ceiling(Math.Abs(i - mid)));

            card.Scale = 1f;
            card.Angle = (float)card.Tilt;
            card.Lift = 0f;
            card.Push = 0f;
            card.StartScale = card.TargetScale = 1f;
            card.StartAngle = card.TargetAngle = (float)card.Tilt;
            card.StartLift = card.TargetLift = 0f;
            card.StartPush = card.TargetPush = 0f;
        }
    }

    /// <summary>切换选中卡（含滑出所有卡），并启动 300 ms 手牌动画。</summary>
    private static void SetHot(int target)
    {
        if (target == _hot) return;
        _lastHot = _hot;
        _hot = target;
        // 快速扫过一排卡片时不要每张都立刻抓一次 PrintWindow，停留 70ms 再刷新
        var token = ++_snapshotDebounce;
        if (target >= 0)
        {
            _ = Task.Delay(70).ContinueWith(_ => _uiQueue?.TryEnqueue(() =>
            {
                if (token == _snapshotDebounce) RefreshHotSnapshot();
            }));
        }
        BeginAnimation();
    }

    /// <summary>
    /// 切换/弹出动画：和主页抽屉手牌同一套参数——300 ms，旋转/推挤走 CubicEaseOut，
    /// 缩放/抬起走 BackEaseOut（振幅 0.3，带一点回弹过冲）。
    /// </summary>
    private static void BeginAnimation()
    {
        if (Cards.Count == 0) return;

        var n = Cards.Count;
        var mid = (n - 1) / 2.0;
        var push = Px(PushDip);
        for (var i = 0; i < n; i++)
        {
            var card = Cards[i];
            var hot = i == _hot;
            card.StartScale = card.Scale;
            card.StartAngle = card.Angle;
            card.StartLift = card.Lift;
            card.StartPush = card.Push;

            card.TargetScale = hot ? (float)HotScale : 1f;
            card.TargetAngle = hot ? 0f : (float)card.Tilt;
            card.TargetLift = hot ? card.HotLiftPx : 0f;
            card.TargetPush = _hot < 0 || hot
                ? 0f
                : Math.Sign(i - _hot) * Math.Max(0, 3 - Math.Abs(i - _hot)) / 3f * push;
        }

        if (_graphics is null)
        {
            ApplyAnimationTargets();
            Render();
            return;
        }

        _animStartMs = Environment.TickCount64;
        _animating = true;
        StartFrameLoop();
    }

    /// <summary>跟 XAML 合成帧同步，不再靠 DispatcherQueueTimer 的 16ms 抖动。</summary>
    private static void StartFrameLoop()
    {
        if (_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnAnimationFrame;
        _renderingHooked = true;
        _lastFrameTicks = 0;

        // 兜底：某些桌面合成场景下 Rendering 不触发时，仍用 16ms 计时器把动画推完
        if (_frameFallback is null && _uiQueue is { } queue)
        {
            _frameFallback = queue.CreateTimer();
            _frameFallback.Interval = TimeSpan.FromMilliseconds(16);
            _frameFallback.IsRepeating = true;
            _frameFallback.Tick += (_, _) =>
            {
                if (!_animating) return;
                if (Environment.TickCount64 - _lastFrameTicks > 40) AnimTick();
            };
        }
        _frameFallback?.Start();
    }

    private static void StopFrameLoop()
    {
        _frameFallback?.Stop();
        if (!_renderingHooked) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnAnimationFrame;
        _renderingHooked = false;
    }

    private static void OnAnimationFrame(object? sender, object e)
    {
        _lastFrameTicks = Environment.TickCount64;
        AnimTick();
    }

    private static void AnimTick()
    {
        if (!_animating)
        {
            StopFrameLoop();
            return;
        }

        var t = (Environment.TickCount64 - _animStartMs) / (double)AnimMs;
        var done = t >= 1;
        if (t > 1) t = 1;
        var move = CubicOut(t);
        var lift = BackOut(t);

        foreach (var card in Cards)
        {
            card.Angle = Lerp(card.StartAngle, card.TargetAngle, move);
            card.Push = Lerp(card.StartPush, card.TargetPush, move);
            card.Scale = Lerp(card.StartScale, card.TargetScale, lift);
            card.Lift = Lerp(card.StartLift, card.TargetLift, lift);
        }
        Render();

        if (done)
        {
            _animating = false;
            _lastHot = -1;
            StopFrameLoop();
            // 动画结束后再补上期间延迟的卡面
            foreach (var card in Cards)
            {
                if (!card.FaceDirty) continue;
                BuildFace(card);
                card.FaceDirty = false;
            }
            Render();   // 动画结束后让上一张卡真正落回邻居下面
        }
    }

    private static void ApplyAnimationTargets()
    {
        foreach (var card in Cards)
        {
            card.Scale = card.TargetScale;
            card.Angle = card.TargetAngle;
            card.Lift = card.TargetLift;
            card.Push = card.TargetPush;
        }
    }

    private static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>WPF BackEase(EaseOut, Amplitude=0.3) 的等价缓动，会略微过冲再回弹。</summary>
    private static double BackOut(double t)
    {
        const double c1 = 0.3 * 1.70158;
        var p = t - 1;
        return 1 + (c1 + 1) * p * p * p + c1 * p * p;
    }

    private static float Lerp(float from, float to, double t) => (float)(from + (to - from) * t);

    private static void EnsureWindow()
    {
        _uiQueue ??= DispatcherQueue.GetForCurrentThread();
        if (_hwnd != IntPtr.Zero) return;

        if (!_classRegistered)
        {
            _wndProc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszClassName = ClassName
            };
            if (RegisterClassEx(ref wc) == 0)
                DebugLog.Write("[底栏] 预览窗口类注册失败 err=" + Marshal.GetLastWin32Error());
            _classRegistered = true;
        }

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "", WS_POPUP,
            0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            DebugLog.Write("[底栏] 预览窗口创建失败 err=" + Marshal.GetLastWin32Error());
    }

    // ---------------- 渲染 ----------------

    private static void EnsureSurface(int width, int height)
    {
        if (_memDc != IntPtr.Zero && _surfaceW == width && _surfaceH == height) return;
        DestroySurface();

        _memDc = CreateCompatibleDC(IntPtr.Zero);
        if (_memDc == IntPtr.Zero) return;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,       // 自上而下，GDI+ 的坐标与窗口一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            }
        };
        _dib = CreateDIBSection(_memDc, ref bmi, DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) { DestroySurface(); return; }

        _oldBitmap = SelectObject(_memDc, _dib);
        _graphics = Graphics.FromHdc(_memDc);
        _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // 卡面已按 1.3 倍烘焙，动画帧用双线性缩小即可；比每帧双三次插值更轻
        // 卡面已按 1.3 倍预烘焙，逐帧只需双线性缩放；高质量插值会把 300ms 动画拖出掉帧
        _graphics.InterpolationMode = InterpolationMode.Bilinear;
        _graphics.CompositingQuality = CompositingQuality.HighSpeed;
        _graphics.PixelOffsetMode = PixelOffsetMode.Half;
        _graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _surfaceW = width;
        _surfaceH = height;
    }

    private static void DestroySurface()
    {
        try { _graphics?.Dispose(); } catch { }
        _graphics = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memDc, _oldBitmap);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            DeleteDC(_memDc);
        }
        if (_screenDc != IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = IntPtr.Zero;
        }
        _memDc = IntPtr.Zero;
        _dib = IntPtr.Zero;
        _oldBitmap = IntPtr.Zero;
        _surfaceW = 0;
        _surfaceH = 0;
    }

    private static void Render()
    {
        if (_graphics is null || _hwnd == IntPtr.Zero) return;

        _graphics.ResetTransform();
        _graphics.Clear(Color.Transparent);

        // 中间卡最高；选中的卡永远最后画；刚落下的上一张卡动画期间仍压在邻居上面
        var order = Enumerable.Range(0, Cards.Count)
            .OrderBy(i => i == _hot ? 3000 : i == _lastHot ? 2000 : Cards[i].Z)
            .ThenBy(i => i)
            .ToList();
        foreach (var i in order) DrawCard(_graphics, Cards[i], i == _hot);

        if (_screenDc == IntPtr.Zero)
        {
            _screenDc = GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero) return;
        }
        {
            var size = new SIZE { CX = _windowW, CY = _windowH };
            var dst = new POINT { X = _windowX, Y = _windowY };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            UpdateLayeredWindow(_hwnd, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
            if (!_placed)
            {
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                // 排在底栏窗口正后面（同为置顶窗）：底栏底板盖住卡片下半截，卡片本身不裁切。
                if (_dockHwnd != IntPtr.Zero)
                    SetWindowPos(_hwnd, _dockHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                _placed = true;
            }
        }
    }

    private const float FaceScale = 1.3f;

    /// <summary>把卡面（渐变/快照/标题）烘焙成一张位图，动画期间不再逐帧重绘文字和窗口图。</summary>
    private static void BuildFace(Card card)
    {
        var w = Math.Max(1, (int)Math.Round(card.Rect.Width * FaceScale));
        var h = Math.Max(1, (int)Math.Round(card.Rect.Height * FaceScale));
        var face = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(face))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(w / card.Rect.Width, h / card.Rect.Height);
            DrawFaceContent(g, card, new RectangleF(0, 0, card.Rect.Width, card.Rect.Height));
        }
        card.Face?.Dispose();
        card.Face = face;
    }

    private static void DrawFaceContent(Graphics g, Card card, RectangleF r)
    {
        using var path = RoundRect(r, Px(CornerDip));
        using (var fill = CardBrush(r))
        {
            g.FillPath(fill, path);
        }

        // 内容区（窗口快照）先画，再画标题行，避免标题被图盖住
        var pad = Px(InnerPadDip);
        var header = Px(HeaderDip);
        var content = new RectangleF(r.Left + pad, r.Top + header, r.Width - pad * 2, r.Height - header - pad);
        var clipState = g.Save();
        try
        {
            using var contentPath = RoundRect(content, Px(8));
            g.SetClip(contentPath);
            if (card.IsCommand) DrawCommandContent(g, card, content);
            else DrawSnapshot(g, card, content);
        }
        finally { g.Restore(clipState); }

        // 标题行：窗口图标 + 标题
        var iconSize = Px(IconDip);
        var iconX = r.Left + Px(12);
        var iconY = r.Top + (header - iconSize) / 2f;
        if (card.IsCommand)
        {
            DrawPlus(g, iconX, iconY, iconSize, Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        }
        else if (card.Icon is { } icon)
        {
            g.DrawImage(icon, new RectangleF(iconX, iconY, iconSize, iconSize));
        }
        else
        {
            using var iconPen = new Pen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), Math.Max(1, Px(1.2)));
            g.DrawRectangle(iconPen, iconX + 1, iconY + 2, iconSize - 2, iconSize - 3);
        }

        // 关闭 × 由 DrawCard 动态叠加，这里始终给右上角留出位置
        var textX = iconX + iconSize + Px(8);
        var textRight = r.Right - Px(12) - Px(CloseDip) - Px(4);
        using (var sf = new StringFormat(StringFormatFlags.NoWrap)
               {
                   Trimming = StringTrimming.EllipsisCharacter,
                   Alignment = StringAlignment.Near,
                   LineAlignment = StringAlignment.Center
               })
        using (var titleBrush = new SolidBrush(Color.White))
        {
            g.DrawString(card.Title, TitleFont, titleBrush,
                new RectangleF(textX, r.Top, Math.Max(1, textRight - textX), header), sf);
        }
    }

    private static void DrawCard(Graphics g, Card card, bool hot)
    {
        var r = card.Rect;
        var cx = r.Left + r.Width / 2f;
        var by = r.Bottom;

        var state = g.Save();
        try
        {
            // 依次：推挤/抬起 → 移到底边中心 → 旋转 → 缩放 → 移回。和 WPF 手牌的
            // TransformGroup(Scale, Rotate, Translate) + RenderTransformOrigin(0.5,1) 等价。
            g.TranslateTransform(card.Push, -card.Lift);
            g.TranslateTransform(cx, by);
            g.RotateTransform(card.Angle);
            g.ScaleTransform(card.Scale, card.Scale);
            g.TranslateTransform(-cx, -by);

            // 卡面是烘焙好的位图：动画每帧只剩一次 DrawImage
            if (card.Face is { } face) g.DrawImage(face, r);
            else DrawFaceContent(g, card, r);

            using var path = RoundRect(r, Px(CornerDip));
            // 描边：普通卡很淡（#33FFFFFF），选中卡提亮并加粗（#F0FFFFFF）
            using (var pen = new Pen(
                       Color.FromArgb(hot ? 0xF0 : 0x33, 0xFF, 0xFF, 0xFF),
                       Math.Max(1, Px(hot ? 3 : 1.5))))
            {
                pen.Alignment = PenAlignment.Inset;
                g.DrawPath(pen, path);
            }

            // 关闭 ×：只给选中的窗口卡画；添加卡片没有窗口可关
            if (hot && !card.IsCommand)
            {
                var close = CloseRect(card);
                using var pen = new Pen(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF), Math.Max(1, Px(1.6)))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                var inset = close.Width * 0.32f;
                g.DrawLine(pen, close.Left + inset, close.Top + inset, close.Right - inset, close.Bottom - inset);
                g.DrawLine(pen, close.Right - inset, close.Top + inset, close.Left + inset, close.Bottom - inset);
            }
        }
        finally { g.Restore(state); }
    }

    /// <summary>添加卡片的卡面：深色底 + 大加号 + 一句说明。</summary>
    private static void DrawCommandContent(Graphics g, Card card, RectangleF content)
    {
        using (var dark = new SolidBrush(Color.FromArgb(0xFF, 0x0E, 0x0F, 0x13)))
            g.FillRectangle(dark, content);

        var box = Math.Min(content.Width, content.Height) * 0.38f;
        var boxRect = new RectangleF(
            content.Left + (content.Width - box) / 2f,
            content.Top + content.Height * 0.30f - box / 2f,
            box, box);
        using (var path = RoundRect(boxRect, box * 0.24f))
        using (var pen = new Pen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), Math.Max(1, Px(2))))
        {
            g.DrawPath(pen, path);
        }
        DrawPlus(g,
            boxRect.Left + box * 0.30f,
            boxRect.Top + box * 0.30f,
            box * 0.40f,
            Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));

        using var font = new Font(TitleFont.FontFamily, Math.Max(1, Px(12)), FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(card.Subtitle, font, brush,
            new RectangleF(content.Left + Px(8), content.Top + content.Height * 0.58f,
                content.Width - Px(16), content.Height * 0.22f), sf);
    }

    private static void DrawPlus(Graphics g, float x, float y, float size, Color color)
    {
        using var pen = new Pen(color, Math.Max(1, Px(2.4f)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var cx = x + size / 2f;
        var cy = y + size / 2f;
        g.DrawLine(pen, cx, y, cx, y + size);
        g.DrawLine(pen, x, cy, x + size, cy);
    }

    private static void DrawSnapshot(Graphics g, Card card, RectangleF content)
    {
        if (card.Snapshot is { Width: > 0, Height: > 0 } image)
        {
            // 等比铺满内容区，居中裁切
            var scale = Math.Max(content.Width / image.Width, content.Height / image.Height);
            var srcW = content.Width / scale;
            var srcH = content.Height / scale;
            var srcX = (image.Width - srcW) / 2f;
            var srcY = (image.Height - srcH) / 2f;
            g.DrawImage(image, content, new RectangleF(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);
            return;
        }

        using var dark = new SolidBrush(Color.FromArgb(0xFF, 0x0E, 0x0F, 0x13));
        g.FillRectangle(dark, content);
        var minimized = IsIconic(card.Window);
        var hung = !minimized && IsHungAppWindow(card.Window);
        var iconSize = Math.Min(content.Width, content.Height) * 0.38f;
        var iconTop = content.Top + (content.Height - iconSize) / 2f - (minimized || hung ? Px(10) : 0);
        if (card.Icon is { } icon)
        {
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.18f });
            g.DrawImage(icon,
                Rectangle.Round(new RectangleF(
                    content.Left + (content.Width - iconSize) / 2f,
                    iconTop,
                    iconSize, iconSize)),
                0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attrs);
        }
        if (minimized || hung)
        {
            using var font = new Font(TitleFont.FontFamily, Math.Max(1, Px(11)), FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(minimized ? "已最小化" : "未响应", font, brush,
                new RectangleF(content.Left, content.Top + content.Height * 0.62f, content.Width, content.Height * 0.2f), sf);
        }
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static LinearGradientBrush CardBrush(RectangleF r)
    {
        // 所有预览/添加卡片固定灰色，不再跟随条目强调色
        var brush = new LinearGradientBrush(r, Color.Black, Color.Black, LinearGradientMode.Vertical);
        brush.InterpolationColors = new ColorBlend
        {
            Colors =
            [
                Color.FromArgb(0xFF, 0x2E, 0x30, 0x36),
                Color.FromArgb(0xFF, 0x22, 0x24, 0x2A),
                Color.FromArgb(0xFF, 0x15, 0x16, 0x1A)
            ],
            Positions = [0f, 0.55f, 1f]
        };
        return brush;
    }

    private static Font TitleFont
    {
        get
        {
            if (_titleFont is not null && Math.Abs(_fontDpi - _dpi) < 0.01) return _titleFont;
            _titleFont?.Dispose();
            FontFamily family;
            try { family = new FontFamily("Segoe UI Semibold"); }
            catch { family = new FontFamily("Segoe UI"); }
            _titleFont = new Font(family, Math.Max(8, Px(15)), FontStyle.Regular, GraphicsUnit.Pixel);
            family.Dispose();
            _fontDpi = _dpi;
            return _titleFont;
        }
    }

    // ---------------- 鼠标 ----------------

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE:
                {
                    var x = SignedLoWord(lParam);
                    var y = SignedHiWord(lParam);
                    // 选卡模式下鼠标/触摸不自动弹出，必须点击一次才选中
                    if (!_selectionMode) SetHot(IndexAtFan(x, y));
                    var track = new TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = hwnd
                    };
                    TrackMouseEvent(ref track);
                    return IntPtr.Zero;
                }

            case WM_MOUSELEAVE:
                if (!_selectionMode) SetHot(-1);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                {
                    var x = SignedLoWord(lParam);
                    var y = SignedHiWord(lParam);
                    DebugLog.Write($"[底栏] 预览点击 {x},{y} hot={_hot} select={_selectionMode}");
                    var index = IndexAtFan(x, y);
                    if (_selectionMode)
                    {
                        // 长按/Y 弹出的卡片是两步：第一次点只把卡片弹出来，再点同一张才打开
                        if (index < 0)
                        {
                            Hide();
                            return IntPtr.Zero;
                        }
                        if (index != _hot)
                        {
                            SetHot(index);
                            return IntPtr.Zero;
                        }
                    }
                    if (_hot >= 0 && Cards[_hot].IsCommand)
                    {
                        var command = Cards[_hot].CommandId;
                        var pick = _onCommandPick;
                        Hide();
                        pick?.Invoke(command);
                        return IntPtr.Zero;
                    }
                    if (_hot >= 0 && HitClose(x, y))
                    {
                        var window = Cards[_hot].Window;
                        CloseWindow(window);
                        Hide();
                        return IntPtr.Zero;
                    }
                    if (index < 0)
                    {
                        Hide();
                        return IntPtr.Zero;
                    }
                    var target = Cards[index].Window;
                    Hide();
                    ActivateWindow(target);
                    return IntPtr.Zero;
                }

            case WM_MBUTTONUP:
                {
                    var index = IndexAtFan(SignedLoWord(lParam), SignedHiWord(lParam));
                    if (index >= 0)
                    {
                        CloseWindow(Cards[index].Window);
                        Hide();
                    }
                    return IntPtr.Zero;
                }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>按横坐标选中手牌里离鼠标最近的一张（叠压时也稳定；和抽屉手牌一致）。</summary>
    private static int IndexAtFan(int x, int y)
    {
        if (Cards.Count == 0) return -1;
        if (y < 0 || y > _windowH) return -1;

        // 已经弹出的卡有"粘性"：鼠标在这张卡的实际显示区域内移动时不换卡。
        // 否则从卡中间移向右上角关闭 × 的途中，横坐标会先越过邻居中心，卡一换、× 也跟着跑，
        // 永远点不到关闭。这里只认弹出后的矩形本身（略微外扩），不覆盖邻居露出来的区域。
        if (_hot >= 0 && _hot < Cards.Count)
        {
            var sticky = HotRect();
            sticky.Inflate(Px(4), Px(4));
            if (sticky.Contains(x, y)) return _hot;
        }

        var best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < Cards.Count; i++)
        {
            var r = Cards[i].Rect;
            var dist = Math.Abs(x - (r.Left + r.Width / 2));
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return bestDist <= Cards[0].Rect.Width * 0.62 ? best : -1;
    }

    private static RectangleF HotRect()
    {
        var card = Cards[_hot];
        var r = card.Rect;
        var cx = r.Left + r.Width / 2f;
        var by = _hotBottomLocal;
        var w = r.Width * (float)HotScale;
        var h = r.Height * (float)HotScale;
        return new RectangleF(cx - w / 2f, by - h, w, h);
    }

    private static RectangleF CloseRect(Card card)
    {
        var r = card.Rect;
        var size = Px(CloseDip);
        return new RectangleF(r.Right - size - Px(6), r.Top + (Px(HeaderDip) - size) / 2f, size, size);
    }

    private static bool HitClose(int x, int y)
    {
        if (_hot < 0) return false;
        // 选中卡是转正 + 以底边中心放大，关闭按钮按同样比例换算到屏幕坐标
        var card = Cards[_hot];
        var baseRect = card.Rect;
        var hotRect = HotRect();
        var close = CloseRect(card);
        var scale = (float)HotScale;
        var screenX = hotRect.Left + (close.Left - baseRect.Left) * scale;
        var screenY = hotRect.Top + (close.Top - baseRect.Top) * scale;
        var screenW = close.Width * scale;
        var screenH = close.Height * scale;
        return x >= screenX && x <= screenX + screenW && y >= screenY && y <= screenY + screenH;
    }

    /// <summary>
    /// 换卡时异步刷新当前卡的快照：PrintWindow 不再卡在 UI 线程上，
    /// 动画可以立刻开始，快照回来后再替换卡面（旧任务用版本号丢弃）。
    /// </summary>
    private static void RefreshHotSnapshot()
    {
        if (_hot < 0 || _hot >= Cards.Count) return;
        var card = Cards[_hot];
        if (card.IsCommand) return;
        var window = card.Window;
        var version = ++card.SnapshotVersion;

        _ = Task.Run(() => CaptureWindow(window)).ContinueWith(task =>
        {
            var snapshot = task.Result;
            if (snapshot is null) return;
            _uiQueue?.TryEnqueue(() =>
            {
                if (version != card.SnapshotVersion || !Cards.Contains(card))
                {
                    snapshot.Dispose();
                    return;
                }
                card.Snapshot?.Dispose();
                card.Snapshot = snapshot;
                if (_animating)
                {
                    // 动画期间绝不重绘卡面：一次 BuildFace 足够让 300ms 里掉十几帧
                    card.FaceDirty = true;
                }
                else
                {
                    BuildFace(card);
                    if (_hot >= 0 && Cards[_hot] == card) Render();
                }
            });
        }, TaskScheduler.Default);
    }

    // ---------------- 快照 / 图标 ----------------

    private static Bitmap? CaptureWindow(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return null;
            // 最小化窗口的 PrintWindow 只有一块黑底和标题栏残影，改成绘制"已最小化"占位
            if (IsIconic(window)) return null;
            // 目标卡死时 PrintWindow 会同步等它处理 WM_PRINT，直接把底栏一起拖住
            if (IsHungAppWindow(window)) return null;
            if (!GetWindowRect(window, out var rect)) return null;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return null;

            var native = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(native))
            {
                var dc = g.GetHdc();
                try
                {
                    if (!PrintWindow(window, dc, PW_RENDERFULLCONTENT))
                        PrintWindow(window, dc, 0);
                }
                finally { g.ReleaseHdc(dc); }
            }

            const int maxSide = 1400;
            if (width <= maxSide && height <= maxSide) return native;

            var scale = Math.Min((double)maxSide / width, (double)maxSide / height);
            var thumb = new Bitmap(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)),
                PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(native, 0, 0, thumb.Width, thumb.Height);
            }
            native.Dispose();
            return thumb;
        }
        catch (Exception ex)
        {
            DebugLog.Write("[底栏] 预览快照失败：" + ex.Message);
            return null;
        }
    }

    private static Bitmap? IconOf(IntPtr window)
    {
        try
        {
            var handle = WindowIconOf(window);
            if (handle == IntPtr.Zero) return null;
            using var icon = Icon.FromHandle(handle);
            return icon.ToBitmap();
        }
        catch { return null; }
    }

    private static IntPtr WindowIconOf(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return IntPtr.Zero;
            var icon = SendMessage(window, WM_GETICON, ICON_SMALL2, IntPtr.Zero);
            if (icon == IntPtr.Zero) icon = SendMessage(window, WM_GETICON, ICON_SMALL, IntPtr.Zero);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window, GCLP_HICONSM);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window, GCLP_HICON);
            return icon;
        }
        catch { return IntPtr.Zero; }
    }

    private static string TitleOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetWindowTextW(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "（无标题）";
    }

    private static void ActivateWindow(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return;
            var ok = AppWindows.BringToFront(window);
            if (!ok) AppWindows.BringToFrontSoon(window);
            DebugLog.Write($"[底栏] 预览激活 hwnd=0x{window.ToInt64():X} ok={ok}");
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 预览激活失败：" + ex.Message); }
    }

    private static void CloseWindow(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return;
            PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            DebugLog.Write($"[底栏] 预览关闭 hwnd=0x{window.ToInt64():X}");
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 预览关闭失败：" + ex.Message); }
    }

    // ---------------- 几何 / 颜色 ----------------

    private static int Px(double dip) => (int)Math.Round(dip * Math.Max(1, _dpi));

    private static void BuildAccent(global::Windows.UI.Color accent)
    {
        static byte Tone(byte value, double t, byte add) => (byte)Math.Clamp(value * t + add, 0, 255);
        _colorTop = Color.FromArgb(0xFF, Tone(accent.R, 0.46, 30), Tone(accent.G, 0.46, 30), Tone(accent.B, 0.46, 30));
        _colorMid = Color.FromArgb(0xFF, Tone(accent.R, 0.24, 18), Tone(accent.G, 0.24, 18), Tone(accent.B, 0.24, 18));
    }

    private static int SignedLoWord(IntPtr value) => (short)(value.ToInt64() & 0xFFFF);
    private static int SignedHiWord(IntPtr value) => (short)((value.ToInt64() >> 16) & 0xFFFF);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

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

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int IDC_ARROW = 32512;
    private const int WM_GETICON = 0x007F;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MBUTTONUP = 0x0208;
    private const uint WM_CLOSE = 0x0010;
    private const uint TME_LEAVE = 0x00000002;
    private const int ICON_SMALL = 0;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private const uint ULW_ALPHA = 0x00000002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
        IntPtr srcDc, ref POINT src, int colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int name);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
