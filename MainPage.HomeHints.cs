using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameLocker;

/// <summary>
/// 主页顶部提示条（双区 HUD）：左侧动态区按当前选中项刷新，右侧 2×2 固定区常驻。
/// 数据来自 GetBoardLegend()：主页态取 Home 行，编辑态取 Edit 行 —— 黑板移除后，
/// 这片提示条就是唯一的手柄操作说明。
/// </summary>
public sealed partial class MainPage
{
    /// <summary>动态区最近一次写入的按键配置；相同则跳过重建，避免光标移动时整条闪烁</summary>
    private string _homeHintSpec = "";

    /// <summary>上一次刷新时是否处于编辑态：变模式时播"提示换了一套"的过渡动画</summary>
    private bool _hintEditing;

    /// <summary>按当前选中项刷新动态区（A 启动/播放、Y 最小化、LB/RB 切歌…）。
    /// 固定区（选择·添加·编辑·返回）在主页态不动，进入编辑态才换成编辑键位。</summary>
    private void RefreshHomeHints(bool force = false)
    {
        try
        {
            RefreshHomeHintsCore(force);
        }
        catch (Exception ex)
        {
            // 提示条只是辅助：动画/重建出错不能把整个界面崩掉（UI 线程异常会直接闪退）
            DebugLog.Write("[提示条] 刷新异常：" + ex);
        }
    }

    private void RefreshHomeHintsCore(bool force)
    {
        var editing = _moving;
        var modeChanged = editing != _hintEditing;
        _hintEditing = editing;
        var (edit, home) = GetBoardLegend();
        ApplyFixedHintChips(editing);

        // 固定区已经占掉的键不再进动态区，避免同一个键出现两种说法。
        var rows = editing ? edit : home;
        var items = rows
            .Where(row => row.Key is not ("L3" or "B") && !(editing && row.Key == "X"))
            .Take(3)
            .ToList();

        var spec = string.Join("|", items.Select(row => $"{row.Key}:{row.Text}"));
        var describe = items.Count == 0
            ? "当前磁贴没有专属快捷操作"
            : string.Join("，", items.Select(row => $"{KeySpoken(row.Key)} {row.Text}"));
        if (!force && !modeChanged && spec == _homeHintSpec) return;

        var wasEmpty = _homeHintSpec.Length == 0;
        _homeHintSpec = spec;

        // 读屏/自动化：整条只报一次，内容是"当前能做什么 + 固定键"
        AutomationProperties.SetName(HomeHintPlate,
            $"{(editing ? "编辑" : "主页")}手柄提示：{describe}。固定：方向键选择、{(editing ? "X 标记、B 或 L3 退出" : "X 添加、L3 编辑、B 返回")}。");

        var hasDynamic = spec.Length > 0;
        HomeHintDynamicHost.Visibility = hasDynamic ? Visibility.Visible : Visibility.Collapsed;
        HomeHintDivider.Visibility = hasDynamic ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDynamic) return;

        HomeHintDynamic.Hints = spec;
        if (modeChanged && !wasEmpty && !force)
        {
            // 进入/退出编辑态：整条换了一套键位，给一个更明显的过渡
            PlayHomeHintModeChange();
            return;
        }
        if (force || wasEmpty)
        {
            // 首次出现/强制刷新不做动效，免得启动或切模式时闪一下
            HomeHintDynamicSlide.X = 0;
            HomeHintDynamicHost.Opacity = 1;
            return;
        }
        PlayHomeHintChange();
    }

    /// <summary>固定区 2×2 的四个键位：主页态是"添加/返回"，编辑态是"标记/退出"</summary>
    private void ApplyFixedHintChips(bool editing)
    {
        var x = editing ? "X:标记" : "X:添加";
        var b = editing ? "B:退出" : "B:返回";
        var l3 = editing ? "L3:退出" : "L3:编辑";
        if (HomeHintChipX.Hints != x) HomeHintChipX.Hints = x;
        if (HomeHintChipB.Hints != b) HomeHintChipB.Hints = b;
        if (HomeHintChipL3.Hints != l3) HomeHintChipL3.Hints = l3;
    }

    /// <summary>把键位标识读成口语，供 AutomationProperties 朗读</summary>
    private static string KeySpoken(string key) => key.ToUpperInvariant() switch
    {
        "DPAD" => "方向键",
        "LB/RB" => "左右肩键",
        "UD" => "上下键",
        "LR" => "左右键",
        "VIEW" => "视图键",
        "MENU" => "菜单键",
        _ => $"{key} 键"
    };

    /// <summary>切换磁贴时只让动态区动：向左 -6px 滑入 + 淡入 120ms；固定区一帧都不动，位置稳定本身就是可读性</summary>
    private void PlayHomeHintChange()
    {
        if (!HeaderMotionEnabled)
        {
            HomeHintDynamicSlide.X = 0;
            HomeHintDynamicHost.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var timeline = new Storyboard();

        var slide = new DoubleAnimation
        {
            // 动态区在左、向左扩展，所以从左侧 -6px 滑入更自然
            From = -6,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = ease
        };
        Storyboard.SetTarget(slide, HomeHintDynamicSlide);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));
        timeline.Children.Add(slide);

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = ease
        };
        Storyboard.SetTarget(fade, HomeHintDynamicHost);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        timeline.Children.Add(fade);

        timeline.Begin();
    }

    /// <summary>
    /// 进入/退出编辑态时整条提示条的过渡：动态区从左侧滑入淡入、固定键位盘回弹放大、
    /// 整条轻微放大后回落——一眼能看出"操作提示换了一套"。
    /// </summary>
    private void PlayHomeHintModeChange()
    {
        HomeHintDynamicSlide.X = 0;
        HomeHintDynamicHost.Opacity = 1;

        if (!HeaderMotionEnabled)
        {
            SnapHomeHintMotion();
            return;
        }

        var plateWidth = HomeHintPlate.ActualWidth;
        var plateHeight = HomeHintPlate.ActualHeight;
        if (plateWidth > 0 && plateHeight > 0)
            HomeHintPlate.CenterPoint = new System.Numerics.Vector3((float)(plateWidth / 2), (float)(plateHeight / 2), 0);
        var fixedWidth = HomeHintFixed.ActualWidth;
        var fixedHeight = HomeHintFixed.ActualHeight;
        if (fixedWidth > 0 && fixedHeight > 0)
            HomeHintFixed.CenterPoint = new System.Numerics.Vector3((float)(fixedWidth / 2), (float)(fixedHeight / 2), 0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var timeline = new Storyboard();

        // 动态区：比"换磁贴"幅度更大，配合淡入
        timeline.Children.Add(HintAnimation(HomeHintDynamicSlide, nameof(TranslateTransform.X), -14, 0, 220, ease));
        timeline.Children.Add(HintAnimation(HomeHintDynamicHost, nameof(UIElement.Opacity), 0, 1, 180, ease));

        // 固定键位盘：0.9 → 1 回弹 + 淡入
        HomeHintFixedScale.ScaleX = 0.9;
        HomeHintFixedScale.ScaleY = 0.9;
        timeline.Children.Add(HintAnimation(HomeHintFixedScale, nameof(ScaleTransform.ScaleX), 0.9, 1, 260, pop));
        timeline.Children.Add(HintAnimation(HomeHintFixedScale, nameof(ScaleTransform.ScaleY), 0.9, 1, 260, pop));
        timeline.Children.Add(HintAnimation(HomeHintFixed, nameof(UIElement.Opacity), 0.25, 0.9, 200, ease));

        // 整条：轻轻放大再回落，像"刷新了一下"
        timeline.Children.Add(HintAnimation(HomeHintPlateScale, nameof(ScaleTransform.ScaleX), 1.02, 1, 240, ease));
        timeline.Children.Add(HintAnimation(HomeHintPlateScale, nameof(ScaleTransform.ScaleY), 1.02, 1, 240, ease));

        timeline.Begin();
    }

    private static DoubleAnimation HintAnimation(DependencyObject target, string property,
        double from, double to, int milliseconds, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = easing
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private void SnapHomeHintMotion()
    {
        HomeHintDynamicSlide.X = 0;
        HomeHintDynamicHost.Opacity = 1;
        HomeHintFixedScale.ScaleX = 1;
        HomeHintFixedScale.ScaleY = 1;
        HomeHintFixed.Opacity = 0.9;
        HomeHintPlateScale.ScaleX = 1;
        HomeHintPlateScale.ScaleY = 1;
    }

    // ── 猫娘躲提示条 ───────────────────────────────────────────────────────────
    /// <summary>下缩深度：正好让脸沉到磁贴后面、耳朵尖还露在上面（精灵图里耳朵占顶部约 20 DIP）</summary>
    private const double MascotDuckDepth = 32;

    /// <summary>提示条距标题区底边的距离（三种窗口模式共用，改这一处全模式同步）</summary>
    private const double HomeHintBottom = 22;
    private bool _mascotDucked;
    private DispatcherTimer? _duckTimer;
    private double _duckFrom, _duckTo;
    private long _duckStartedAt;
    private int _duckDuration;

    /// <summary>第一块磁贴的上沿（窗口坐标）；猫娘藏身时以它为"桌面"，露出部分裁在这条线以上</summary>
    private double _firstTileEdgeY = double.NaN;

    private void OnHomeHintSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMascotDuck();

    /// <summary>提示条压到猫娘头上时，猫把脑袋缩到磁贴后面，只留耳朵尖；遮挡消失再冒出来。</summary>
    private void UpdateMascotDuck()
    {
        if (MascotLayer is null || HomeHintPlate is null) return;
        if (MascotLayer.Visibility != Visibility.Visible || HomeHintShell.Visibility != Visibility.Visible
            || HomeHintPlate.ActualWidth <= 0 || MascotLayer.ActualWidth <= 0)
        {
            SetMascotDuck(false);
            return;
        }

        var bar = HomeHintPlate.TransformToVisual(this)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, HomeHintPlate.ActualWidth, HomeHintPlate.ActualHeight));
        var cat = MascotLayer.TransformToVisual(this)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, MascotLayer.ActualWidth, MascotLayer.ActualHeight));

        // 只按"头部"判：左右各内缩 6，纵向只取上面七成，耳朵尖轻蹭不算被挡
        var headLeft = cat.X + 6;
        var headRight = cat.X + cat.Width - 6;
        var headTop = cat.Y + 6;
        var headBottom = cat.Y + cat.Height * 0.7;
        var overlapped = bar.X < headRight && bar.X + bar.Width > headLeft
                         && bar.Y < headBottom && bar.Y + bar.Height > headTop;
        SetMascotDuck(overlapped);
    }

    private void SetMascotDuck(bool duck)
    {
        if (_mascotDucked == duck)
        {
            // 位置或尺寸变了（磁贴滚动、窗口缩放），按当前值重算裁剪
            if (duck) ApplyDuckFrame(MascotDuckSlide.Y);
            return;
        }
        _mascotDucked = duck;

        // 缩下去时禁止拖拽猫娘；万一手柄/鼠标正抓着它，先松手
        if (duck && IsMascotPointerCaptured) Mascot.CancelMovement();
        MascotLayer.IsHitTestVisible = !duck;

        StartDuckTween(duck ? MascotDuckDepth : 0, duck ? 200 : 280);
    }

    /// <summary>下缩/起身的补间：位移与裁剪同一条缓动曲线，看起来是"沉到磁贴后面"而不是"瞬间切换"</summary>
    private void StartDuckTween(double to, int durationMs)
    {
        var timer = EnsureDuckTimer();

        _duckFrom = MascotDuckSlide.Y;
        _duckTo = to;
        _duckDuration = durationMs;
        _duckStartedAt = Environment.TickCount64;

        if (!HeaderMotionEnabled || Math.Abs(to - _duckFrom) < 0.5)
        {
            timer.Stop();
            ApplyDuckFrame(to);
            return;
        }
        timer.Start();
    }

    private DispatcherTimer EnsureDuckTimer()
    {
        if (_duckTimer is null)
        {
            _duckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            _duckTimer.Tick += OnDuckTick;
        }
        return _duckTimer;
    }

    private void OnDuckTick(object? sender, object e)
    {
        var t = _duckDuration <= 0 ? 1 : Math.Clamp((Environment.TickCount64 - _duckStartedAt) / (double)_duckDuration, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        ApplyDuckFrame(_duckFrom + (_duckTo - _duckFrom) * eased);
        if (t >= 1)
        {
            _duckTimer?.Stop();
            ApplyDuckFrame(_duckTo);
        }
    }

    /// <summary>按当前下缩量同时更新位移和裁剪：露出高度从整只猫平滑收窄到只剩耳朵</summary>
    private void ApplyDuckFrame(double offset)
    {
        MascotDuckSlide.Y = offset;
        if (offset <= 0.5)
        {
            MascotLayer.Clip = null;
            return;
        }

        var fullHeight = MascotLayer.Height;
        // 磁贴上沿每次都现算：窗口缩放/滚动后缓存值可能过期，算不出来就按"精灵图底边 8 行是手指"估
        var keepAtFull = FirstTileEdgeY() - MascotLayer.Margin.Top;
        if (!double.IsFinite(keepAtFull) || keepAtFull <= 0) keepAtFull = fullHeight - 8;
        keepAtFull = Math.Clamp(keepAtFull, 16, fullHeight);

        var keepAtEnd = Math.Max(8, keepAtFull - MascotDuckDepth);        // 缩到底时只留耳朵
        var progress = Math.Clamp(offset / MascotDuckDepth, 0, 1);
        var visible = fullHeight + (keepAtEnd - fullHeight) * progress;
        if (visible >= fullHeight - 0.5)
        {
            MascotLayer.Clip = null;
            return;
        }
        MascotLayer.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, MascotLayer.Width, Math.Max(4, visible))
        };
    }

    /// <summary>第一块磁贴上沿的窗口坐标；没有磁贴时返回 NaN</summary>
    private double FirstTileEdgeY()
    {
        if (_placedTiles.Count > 0)
        {
            var (tile, _, _) = _placedTiles[0];
            var top = Microsoft.UI.Xaml.Controls.Canvas.GetTop(tile) - TileScroll.VerticalOffset;
            if (double.IsFinite(top)) return MascotSpacer.Height + TileScroll.Padding.Top + top;
        }
        return double.IsFinite(_firstTileEdgeY) ? _firstTileEdgeY : double.NaN;
    }
}
