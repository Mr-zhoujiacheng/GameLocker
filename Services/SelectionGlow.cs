using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GameLocker.Services;

/// <summary>
/// 页面级"选中框柔光"：在页面根 Grid 上盖一层不参与布局、不接收点击的 Canvas，
/// 用多层白色圆环围着当前光标控件画出由内向外渐隐的光晕（和主页磁贴选中框同款）。
/// 页面只需要在光标变化时调用 <see cref="Show"/> / <see cref="Hide"/>。
/// </summary>
internal sealed class SelectionGlow
{
    // 与主页磁贴选中框保持同一组参数（厚度 / 透明度）
    private static readonly double[] RingThickness = { 2, 5, 9, 14, 20 };
    private static readonly byte[] RingAlpha = { 0x40, 0x22, 0x14, 0x0A, 0x05 };

    /// <summary>光标描边的统一白色（页面自己的 CursorBrush 也用它，保证一致）</summary>
    public static readonly SolidColorBrush CursorBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    /// <summary>光标描边统一粗细（和主页磁贴选中框一致）</summary>
    public const double CursorThickness = 2.5;

    /// <summary>控件原本的描边粗细：光标移走时还原，避免把普通边框也变粗</summary>
    private sealed class RestThickness
    {
        public Thickness Value;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, RestThickness>
        Rest = new();

    private static Thickness ReadThickness(FrameworkElement element) => element switch
    {
        Control c => c.BorderThickness,
        Border b => b.BorderThickness,
        _ => new Thickness(0)
    };

    private static void WriteThickness(FrameworkElement element, Thickness value)
    {
        switch (element)
        {
            case Control c: c.BorderThickness = value; break;
            case Border b: b.BorderThickness = value; break;
        }
    }

    private readonly Canvas _layer = new() { IsHitTestVisible = false, IsTabStop = false };
    private readonly Border?[] _rings = new Border?[RingThickness.Length];
    private FrameworkElement? _target;
    private double _radius = 8;

    /// <summary>在页面根 Grid 上挂一层光晕；根不是 Panel 时返回 null（页面自己判断）。</summary>
    public static SelectionGlow? Attach(Panel root)
    {
        var glow = new SelectionGlow();
        Grid.SetRowSpan(glow._layer, 99);
        Grid.SetColumnSpan(glow._layer, 99);
        Canvas.SetZIndex(glow._layer, 1000);
        root.Children.Add(glow._layer);
        glow.HookScroll(root);
        return glow;
    }

    /// <summary>列表滚动时重新贴到光标控件上（只在滚动时做一次变换计算）。</summary>
    private void HookScroll(Panel root)
    {
        root.Loaded += (_, _) =>
        {
            if (FindScrollViewer(root) is { } sv)
                sv.ViewChanged += (_, _) => Move();
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject node)
    {
        if (node is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    /// <summary>把光晕贴到控件上（cornerRadius 传该控件自己的圆角）。</summary>
    public void Show(FrameworkElement target, double cornerRadius)
    {
        if (!ReferenceEquals(_target, target))
        {
            if (_target is not null)
            {
                _target.SizeChanged -= OnTargetSizeChanged;
                _target.Loaded -= OnTargetLoaded;
            }
            _target = target;
            // 页面构造/首次布局阶段拿到的控件可能还没测量，等它 Loaded/尺寸变化后再贴
            target.SizeChanged += OnTargetSizeChanged;
            target.Loaded += OnTargetLoaded;
        }
        _radius = cornerRadius;
        // 描边统一成 2.5px（原本多细都先记下来，移走时还原）
        if (!Rest.TryGetValue(target, out var rest))
        {
            rest = new RestThickness { Value = ReadThickness(target) };
            Rest.Add(target, rest);
        }
        WriteThickness(target, new Thickness(CursorThickness));
        Move();
    }

    private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => Move();

    private void OnTargetLoaded(object sender, RoutedEventArgs e) => Move();

    public void Hide()
    {
        if (_target is not null)
        {
            _target.SizeChanged -= OnTargetSizeChanged;
            _target.Loaded -= OnTargetLoaded;
            if (Rest.TryGetValue(_target, out var rest))
                WriteThickness(_target, rest.Value);
        }
        _target = null;
        SetRingsVisible(false);
    }

    private void SetRingsVisible(bool visible)
    {
        foreach (var ring in _rings)
            if (ring is not null) ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Move()
    {
        if (_target is null) return;
        if (!_target.IsLoaded || _target.ActualWidth <= 0 || _target.ActualHeight <= 0)
        {
            SetRingsVisible(false);
            return;
        }

        Rect rect;
        try
        {
            rect = _target.TransformToVisual(_layer)
                .TransformBounds(new Rect(0, 0, _target.ActualWidth, _target.ActualHeight));
        }
        catch
        {
            return;
        }

        for (var i = 0; i < RingThickness.Length; i++)
        {
            var grow = RingThickness[i];
            var ring = _rings[i] ??= new Border
            {
                IsHitTestVisible = false,
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            if (ring.Parent is null) _layer.Children.Add(ring);

            ring.Width = rect.Width + grow * 2;
            ring.Height = rect.Height + grow * 2;
            ring.CornerRadius = new CornerRadius(_radius + grow);
            ring.BorderThickness = new Thickness(grow);
            ((SolidColorBrush)ring.BorderBrush!).Color =
                Windows.UI.Color.FromArgb(RingAlpha[i], 0xFF, 0xFF, 0xFF);
            Canvas.SetLeft(ring, rect.X - grow);
            Canvas.SetTop(ring, rect.Y - grow);
            ring.Visibility = Visibility.Visible;
        }
    }
}
