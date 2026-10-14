using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 小组件页：列出可添加的小组件（当前为音乐控制器），磁贴预览完全复用主页磁贴样式。
/// 手柄：方向=移动光标，A/X=添加到主页，B/View=返回主页。
/// </summary>
public sealed partial class WidgetPage : Page
{
    private Control[] _formControls = [];
    private int _formIndex;

    /// <summary>预览用固定音乐组件项（Target 稳定哈希配色，与主页添加后的磁贴一致；固定 2×2 大磁贴）</summary>
    private static readonly LaunchItem PreviewItem = new()
    {
        Name = "音乐控制器",
        Target = "widget:music",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Music,
        Size = TileSize.Large
    };

    /// <summary>预览用固定图片组项（未定向文件夹，磁贴显示占位提示）</summary>
    private static readonly LaunchItem PhotoPreviewItem = new()
    {
        Name = "图片组",
        Target = "widget:photos",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.Photos,
        Size = TileSize.Large
    };

    /// <summary>预览用固定磁贴集项（集合 glyph 标准磁贴）</summary>
    private static readonly LaunchItem TileSetPreviewItem = new()
    {
        Name = "磁贴集",
        Target = "widget:tileset",
        Kind = LaunchKind.Widget,
        WidgetKind = WidgetKind.TileSet,
        Size = TileSize.Large
    };

    public WidgetPage()
    {
        InitializeComponent();
        _formControls = [MusicWidgetCard, PhotoWidgetCard, TileSetWidgetCard];
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 磁贴预览：与主页同一套样式构建（按钮传 null → 预览中禁用；图片组固定居中裁切）
        var (w, h) = (2 * TileStyle.Unit + TileStyle.Gap, 2 * TileStyle.Unit + TileStyle.Gap);
        TilePreviewHost.Content = TileStyle.BuildMusicTile(
            PreviewItem, w, h, TileStyle.AccentFor(PreviewItem), TileStyle.IsNoColor(PreviewItem),
            null, null, null).Tile;
        PhotoPreviewHost.Content = TileStyle.BuildPhotoTile(
            PhotoPreviewItem, w, h, null, "图片组\n按 R3 选择图片文件夹").Tile;
        TileSetPreviewHost.Content = TileStyle.BuildStandardTile(
            TileSetPreviewItem, null, w, h, TileStyle.AccentFor(TileSetPreviewItem),
            TileStyle.IsNoColor(TileSetPreviewItem));
        _formIndex = 0;
        FocusForm(0);
    }

    // ---- 手柄 ----

    public void HandleGamepad(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp)) MoveRow(-1);
        else if (b.HasFlag(GamepadButtons.DPadDown)) MoveRow(1);
        else if (b.HasFlag(GamepadButtons.DPadLeft)) MoveCol(-1);
        else if (b.HasFlag(GamepadButtons.DPadRight)) MoveCol(1);

        if (b.HasFlag(GamepadButtons.A) || b.HasFlag(GamepadButtons.X)) ActivateForm();
        else if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View))
            App.Main?.ShowMainPage();
    }

    private void ActivateForm()
    {
        var c = _formControls[_formIndex];
        if (ReferenceEquals(c, PhotoWidgetCard)) AddPhotoWidget();
        else if (ReferenceEquals(c, TileSetWidgetCard)) AddTileSetWidget();
        else AddMusicWidget();
    }

    // 行感知导航（与 AddItemPage 同一套模式）
    private readonly List<List<(int idx, double x, double y)>> _rows = [];

    private void BuildRows()
    {
        _rows.Clear();
        var pts = new List<(int idx, double x, double y)>();
        for (var i = 0; i < _formControls.Length; i++)
        {
            if (_formControls[i].Visibility != Visibility.Visible) continue;
            if (!TryCenter(_formControls[i], out var x, out var y)) continue;
            pts.Add((i, x, y));
        }
        pts.Sort((a, b) => a.y.CompareTo(b.y));
        const double tol = 40;
        for (var k = 0; k < pts.Count;)
        {
            var rowY = pts[k].y;
            var row = new List<(int idx, double x, double y)>();
            while (k < pts.Count && pts[k].y - rowY <= tol) row.Add(pts[k++]);
            row.Sort((a, b) => a.x.CompareTo(b.x));
            _rows.Add(row);
        }
    }

    private (int row, int col) LocateCurrent()
    {
        for (var r = 0; r < _rows.Count; r++)
            for (var c = 0; c < _rows[r].Count; c++)
                if (_rows[r][c].idx == _formIndex) return (r, c);
        return (-1, -1);
    }

    private void MoveRow(int delta)
    {
        BuildRows();
        var (ri, ci) = LocateCurrent();
        if (ri < 0) { MoveForm(delta); return; }
        var target = ri + delta;
        if (target < 0 || target >= _rows.Count) return;
        var row = _rows[target];
        var curX = _rows[ri][ci].x;
        var best = row[0];
        var bd = Math.Abs(row[0].x - curX);
        foreach (var p in row)
        {
            var d = Math.Abs(p.x - curX);
            if (d < bd) { bd = d; best = p; }
        }
        _formIndex = best.idx;
        FocusForm(best.idx);
    }

    private void MoveCol(int delta)
    {
        BuildRows();
        var (ri, ci) = LocateCurrent();
        if (ri < 0) { MoveForm(delta); return; }
        var row = _rows[ri];
        var nc = ci + delta;
        if (nc < 0 || nc >= row.Count) return;
        _formIndex = row[nc].idx;
        FocusForm(row[nc].idx);
    }

    private void MoveForm(int delta)
    {
        var next = Math.Clamp(_formIndex + delta, 0, _formControls.Length - 1);
        if (next == _formIndex) return;
        _formIndex = next;
        FocusForm(next);
    }

    private bool TryCenter(Control c, out double x, out double y)
    {
        x = y = 0;
        if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return false;
        try
        {
            var r = c.TransformToVisual(FormPanel)
                      .TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            x = r.X + r.Width / 2;
            y = r.Y + r.Height / 2;
            return true;
        }
        catch { return false; }
    }

    private void FocusForm(int i)
    {
        var c = _formControls[i];
        UpdateCursor(c);
        // RadioButton：只移动光标高亮，不聚焦——WinUI 键盘聚焦会自动勾选，
        // 导致"移动即变更"；按 A 才真正切换选中（与 AddItemPage 同模式）
        if (c is not RadioButton)
            c.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        EnsureVisible(c);
    }

    /// <summary>高亮光标所在控件（描边）</summary>
    private void UpdateCursor(Control? cursor)
    {
        foreach (var ctl in _formControls)
            ctl.BorderBrush = ReferenceEquals(ctl, cursor)
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void EnsureVisible(Control c)
    {
        // 三列等宽布局：光标控件超出可视区时自动垂直滚入
        try
        {
            var p = c.TransformToVisual(FormScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = FormScroll.ViewportHeight;
            if (p.Y < 0)
                FormScroll.ChangeView(null, FormScroll.VerticalOffset + p.Y - 8, null);
            else if (p.Y + c.ActualHeight > vh)
                FormScroll.ChangeView(null, FormScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    // ---- 添加到主页 ----

    private void AddMusicWidget()
    {
        var items = ConfigService.Load();
        if (items.Any(it => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Music))
        {
            PageTitle.Text = "音乐控制器已在主页";
            return;
        }
        items.Add(new LaunchItem
        {
            Name = "音乐控制器",
            Target = "widget:music",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Music,
            Size = TileSize.Large
        });
        ConfigService.Save(items);
        App.Main?.ShowMainPage();
    }

    private void AddPhotoWidget()
    {
        var items = ConfigService.Load();
        if (items.Any(it => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Photos))
        {
            PageTitle.Text = "图片组已在主页";
            return;
        }
        items.Add(new LaunchItem
        {
            Name = "图片组",
            Target = "widget:photos",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Photos,
            Size = TileSize.Large
        });
        ConfigService.Save(items);
        App.Main?.ShowMainPage();
    }

    /// <summary>磁贴集：复制当前主页磁贴为快照（可添加多个；快照仅排除既有磁贴集避免递归，设置磁贴一并复制）</summary>
    private void AddTileSetWidget()
    {
        var items = ConfigService.Load();
        var snapshot = items
            .Where(it => !(it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.TileSet))
            .Select(it => it.Clone())
            .ToList();
        items.Add(new LaunchItem
        {
            Name = "磁贴集",
            Target = "widget:tileset:" + Guid.NewGuid().ToString("N"),
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.TileSet,
            Size = TileSize.Large,
            Snapshot = snapshot
        });
        ConfigService.Save(items);
        App.Main?.ShowMainPage();
    }

    // ---- 鼠标 ----

    private void OnBackClick(object sender, RoutedEventArgs e) => App.Main?.ShowMainPage();

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: "photo" }) AddPhotoWidget();
        else if (sender is Button { Tag: "tileset" }) AddTileSetWidget();
        else AddMusicWidget();
    }
}
