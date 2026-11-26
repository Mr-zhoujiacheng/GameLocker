using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 设置主页：左右侧边栏单列，居中/全屏双列分类卡片（常规 / 外观 / 顶栏 / 控制 / 抽屉 / 关于）。
/// 云端备份不在此列出，只能从「游戏存档小组件」编辑页进入 CloudPage。
/// 手柄：方向跟随列数移动光标，A=进入子页，View(Select)/B=返回主页。
/// </summary>
public sealed partial class SettingsPage : Page, ISettingsGamepad
{
    /// <summary>卡片顺序与 OpenCategory 的分支编号一致（0=常规 … 7=底栏）</summary>
    private Control[] _cards = [];
    private int _index;
    private bool _ready;
    private bool _twoColumn = true;
    private SelectionGlow? _glow;

    public SettingsPage()
    {
        InitializeComponent();
        // 分类卡片用手绘白框当光标：子控件拿到焦点时不再画系统焦点框（避免两个框）
        FocusRing.Attach(this);
        if (Content is Panel root) _glow = SelectionGlow.Attach(root);
        _cards = [GeneralCard, AppearanceCard, SystemCard, ControlCard, DrawerCard, AboutCard];
        // 选中上浮作用在 RenderTransform 上，不影响卡片排布
        foreach (var c in _cards) c.RenderTransform = new CompositeTransform();
        // 卡片网格宽度跟随可用宽度（窄侧边栏/宽悬浮都对齐），避免两列被窗口裁掉
        CategoryScroll.SizeChanged += (_, _) => LayoutCategories();
        Loaded += (_, _) =>
        {
            LayoutCategories();
            UpdateCursor();
        };
    }

    /// <summary>分类网格宽度 = min(720, 可用宽度)；左右侧边栏固定单列，居中/全屏保留双列。</summary>
    private void LayoutCategories()
    {
        if (CategoryPanel is null || CategoryScroll is null) return;
        var avail = CategoryScroll.ViewportWidth;   // 已扣除 ScrollViewer 的 Padding
        if (avail <= 0) return;
        _twoColumn = App.Main?.IsWideOverlay != false;
        CategoryPanel.ColumnDefinitions[1].Width = _twoColumn
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        CategoryPanel.ColumnSpacing = _twoColumn ? 12 : 0;
        var rowCount = _twoColumn ? (_cards.Length + 1) / 2 : _cards.Length;
        while (CategoryPanel.RowDefinitions.Count < rowCount)
            CategoryPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (CategoryPanel.RowDefinitions.Count > rowCount)
            CategoryPanel.RowDefinitions.RemoveAt(CategoryPanel.RowDefinitions.Count - 1);
        for (var i = 0; i < _cards.Length; i++)
        {
            Grid.SetRow(_cards[i], _twoColumn ? i / 2 : i);
            Grid.SetColumn(_cards[i], _twoColumn ? i % 2 : 0);
        }
        CategoryPanel.Width = Math.Min(720, avail);
        CategoryPanel.VerticalAlignment = _twoColumn && avail >= 640
            ? VerticalAlignment.Center
            : VerticalAlignment.Top;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        LayoutCategories();
        UpdateCursor();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;

        // 单列上下逐项移动；双列左右换列、上下换行。
        var col = _index % 2;
        if (_twoColumn && buttons.HasFlag(GamepadButtons.DPadLeft) && col == 1) Move(-1);
        else if (_twoColumn && buttons.HasFlag(GamepadButtons.DPadRight) && col == 0) Move(1);
        else if (buttons.HasFlag(GamepadButtons.DPadUp)) Move(_twoColumn ? -2 : -1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) Move(_twoColumn ? 2 : 1);
        else if (buttons.HasFlag(GamepadButtons.A)) OpenCategory(_index);
    }

    private void Move(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _cards.Length || next == _index) return;
        _index = next;
        UpdateCursor();
    }

    /// <summary>鼠标/触摸点击卡片：先选中，再进入该分类</summary>
    private void OnCategoryCardClick(object sender, RoutedEventArgs e)
    {
        var idx = Array.IndexOf(_cards, sender);
        if (idx < 0) return;
        _index = idx;
        UpdateCursor();
        OpenCategory(idx);
    }

    /// <summary>光标：选中卡片白框 + 上浮 3px（自绘，不用系统焦点框）</summary>
    private void UpdateCursor()
    {
        if (_cards.Length == 0) return;
        for (var i = 0; i < _cards.Length; i++)
        {
            var on = i == _index;
            _cards[i].BorderBrush = on ? SelectionGlow.CursorBrush
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AnimateLift(_cards[i], on ? -3 : 0);
        }
        _glow?.Show(_cards[_index], 10);
        FocusRing.HideCurrent(XamlRoot);
        if (_ready) _cards[_index].StartBringIntoView();
    }

    private static void AnimateLift(Control c, double to)
    {
        if (c.RenderTransform is not CompositeTransform t) return;
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, t);
        Storyboard.SetTargetProperty(anim, "TranslateY");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void OpenCategory(int index)
    {
        var page = index switch
        {
            1 => typeof(AppearancePage),
            2 => typeof(StatusBarSettingsPage),
            3 => typeof(KeybindPage),
            4 => typeof(DrawerSettingsPage),
            5 => typeof(AboutPage),
            _ => typeof(GeneralPage)
        };
        App.Main?.NavigateSettings(page);
    }

    // 分类主页无按键捕获
    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
