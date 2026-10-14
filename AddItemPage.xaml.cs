using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>文件浏览列表项（顶层 public：供 x:Bind 的 x:DataType 引用）</summary>
public sealed record FileEntry(string Name, string Path, bool IsFolder, bool IsParent, bool IsSelectCurrent = false)
{
    public string Glyph => IsSelectCurrent ? "\uE73E" : IsParent ? "\uE74A" : IsFolder ? "\uE8B7" : "\uE7F8";
}

/// <summary>浏览模式用途</summary>
public enum BrowseMode
{
    /// <summary>选程序（.exe）</summary>
    Program,
    /// <summary>选文件夹</summary>
    Folder,
    /// <summary>选图标（exe/ico/图片）</summary>
    Icon,
    /// <summary>选磁贴背景图片（仅图片）</summary>
    Cover
}

/// <summary>
/// 添加/编辑快捷项页（独立页面，完全手柄操作）。
/// 表单模式：方向=移动焦点，A=文本框聚焦输入/切换类型/按下按钮，B/Select=返回主页。
/// 浏览模式：列出当前目录的文件夹与文件（按用途过滤），方向=选择，A=进入/选中，B=返回表单。
/// 编辑模式：OnNavigatedTo 传入 LaunchItem 时预填表单，保存时更新原项。
/// </summary>
public sealed partial class AddItemPage : Page
{
    private Control[] _formControls = [];
    private int _formIndex;
    private bool _browsing;
    private BrowseMode _browseMode;
    private string _currentDir = "";

    // 编辑模式状态
    private LaunchItem? _editing;
    private string _origTarget = "";
    private string _origName = "";

    /// <summary>编辑对象为磁贴组：隐藏"类型/路径/浏览"区，保存时不改 Kind/Target</summary>
    private bool _isGroup;

    /// <summary>编辑对象为小组件磁贴：仅可改名称与颜色，隐藏路径/类型/大小/图标/图片区</summary>
    private bool _isWidget;

    /// <summary>磁贴集副本页快照成员编辑：所属磁贴集的 Target（非空时保存写回该集 Snapshot 内成员并返回副本页）</summary>
    private string? _snapshotSetTarget;

    /// <summary>本次会话中已提取/选择的图标文件名（null=未改动）</summary>
    private string? _pendingIcon;

    /// <summary>本次会话中磁贴背景图片的改动（null=未改动；""=清除）</summary>
    private string? _pendingCover;

    /// <summary>图片组：本次会话选定的文件夹（null=未改动）</summary>
    private string? _pendingPhotoFolder;

    /// <summary>当前浏览是否为"图片组选文件夹"（区别于普通 Folder 类型项选路径）</summary>
    private bool _pickingPhotoFolder;

    public AddItemPage()
    {
        InitializeComponent();
        RebuildFormControls();
    }

    /// <summary>八个磁贴颜色单选（Tag=#RRGGBB）+ 无颜色（Tag=none）</summary>
    private RadioButton[] ColorRadios =>
    [
        ColorBlueRadio, ColorRedRadio, ColorGreenRadio,
        ColorPurpleRadio, ColorNoneRadio, ColorTealRadio,
        ColorYellowRadio, ColorPinkRadio, ColorIndigoRadio
    ];

    /// <summary>表单导航序列（清除图片按钮仅在有背景图时参与）</summary>
    private void RebuildFormControls()
    {
        var list = new List<Control>
        {
            NameBox, TargetBox, BrowseButton, AppRadio, UrlRadio, FolderRadio,
            SmallSizeRadio, WideSizeRadio, LargeSizeRadio,
            ColorBlueRadio, ColorRedRadio, ColorGreenRadio,
            ColorPurpleRadio, ColorNoneRadio, ColorTealRadio,
            ColorYellowRadio, ColorPinkRadio, ColorIndigoRadio,
            IconButton, CoverButton
        };
        if (CoverClearButton.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
            list.Add(CoverClearButton);
        if (PhotoFolderRow.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
            list.Add(PhotoFolderButton);
        // 图片组设置：轮播速度 / 切换动画
        if (PhotoSettingsPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
            list.AddRange(new Control[]
            {
                SpeedFastRadio, SpeedSlowRadio, SpeedArtRadio,
                AnimationToggle
            });
        list.Add(SaveButton);
        _formControls = list.ToArray();
    }

    /// <summary>按 #RRGGBB 勾选对应颜色块；空/不匹配则全部取消（自动哈希配色）</summary>
    private void CheckColor(string? hex)
    {
        foreach (var r in ColorRadios)
            r.IsChecked = hex != null && string.Equals((string)r.Tag, hex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前选中的磁贴颜色（无选中返回 null=自动配色）</summary>
    private string? SelectedColor()
    {
        foreach (var r in ColorRadios)
            if (r.IsChecked == true) return (string?)r.Tag;
        return null;
    }

    /// <summary>编辑模式：预填表单并按类型裁剪可见区域（主页项与磁贴集快照成员共用）</summary>
    private void InitForEdit(LaunchItem item)
    {
        _editing = item;
        _origTarget = item.Target;
        _origName = item.Name;
        _pendingIcon = null;
        _pendingCover = null;
        _isGroup = item.Kind == LaunchKind.Group;
        _isWidget = item.Kind == LaunchKind.Widget;
        _pendingPhotoFolder = null;
        NameBox.Text = item.Name;
        TargetBox.Text = item.Target;
        AppRadio.IsChecked = item.Kind == LaunchKind.App;
        UrlRadio.IsChecked = item.Kind == LaunchKind.Url;
        FolderRadio.IsChecked = item.Kind == LaunchKind.Folder;
        CheckSize(item.Size);
        CheckColor(item.TileColor);
        ShowPreview(item.IconFile);
        ShowCoverPreview(item.CoverFile);
        // 组没有"类型/路径"概念，隐藏对应区域（BuildRows 按 Visibility 自动跳过）
        var vis = _isGroup ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        TargetRow.Visibility = vis;
        KindColumn.Visibility = vis;
        // 小组件：隐藏路径/类型；音乐保留"名称+颜色"，
        // 图片组保留"名称+大小+图片文件夹+轮播速度/动画/展示模式"（无颜色），
        // 设置保留"名称+大小+颜色"，磁贴集保留"名称+大小+颜色+图标+图片"
        if (_isWidget)
        {
            var w = Microsoft.UI.Xaml.Visibility.Collapsed;
            TargetRow.Visibility = w;
            KindColumn.Visibility = w;
            var isPhoto = item.WidgetKind == WidgetKind.Photos;
            var isTileSet = item.WidgetKind == WidgetKind.TileSet;
            // 图标/图片：仅磁贴集可换（音乐/图片组/设置铺图或固定样式不适用）
            IconHeaderText.Visibility = isTileSet ? Microsoft.UI.Xaml.Visibility.Visible : w;
            IconRow.Visibility = isTileSet ? Microsoft.UI.Xaml.Visibility.Visible : w;
            CoverHeaderText.Visibility = isTileSet ? Microsoft.UI.Xaml.Visibility.Visible : w;
            CoverRow.Visibility = isTileSet ? Microsoft.UI.Xaml.Visibility.Visible : w;
            // 音乐固定 2×2 不可改大小；其余小组件可编辑大小。颜色：图片组不需要（整块铺图）
            SizeColumn.Visibility = isPhoto || item.WidgetKind is WidgetKind.Settings or WidgetKind.TileSet
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            ColorColumn.Visibility = isPhoto ? w : Microsoft.UI.Xaml.Visibility.Visible;
            PhotoFolderRow.Visibility = isPhoto
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            PhotoSettingsPanel.Visibility = isPhoto
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isPhoto)
            {
                PhotoFolderText.Text = string.IsNullOrEmpty(item.WidgetFolder)
                    ? "未选择" : item.WidgetFolder;
                SpeedFastRadio.IsChecked = item.SlideshowSpeed == PhotoSpeed.Fast;
                SpeedSlowRadio.IsChecked = item.SlideshowSpeed == PhotoSpeed.Slow;
                SpeedArtRadio.IsChecked = item.SlideshowSpeed == PhotoSpeed.Art;
                AnimationToggle.IsOn = item.PhotoAnimation;
            }
        }
        else
        {
            SizeColumn.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            ColorColumn.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            IconHeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            IconRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CoverHeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CoverRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PhotoFolderRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PhotoSettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _formIndex = 0;
        _snapshotSetTarget = null;
        HintBar.SetHints("DPAD:选择|A:确认/切换|X:保存|B:返回");

        // 磁贴集副本页快照成员编辑：参数 (磁贴集 Target, 成员项)，按普通项完整编辑，保存写回该集快照
        if (e.Parameter is ValueTuple<string, LaunchItem> snap)
        {
            _snapshotSetTarget = snap.Item1;
            InitForEdit(snap.Item2);
            PageTitle.Text = "编辑磁贴";
        }
        else if (e.Parameter is LaunchItem item)
        {
            InitForEdit(item);
            PageTitle.Text = _isWidget
                ? (item.WidgetKind == WidgetKind.Photos ? "编辑图片组"
                    : item.WidgetKind == WidgetKind.Settings ? "编辑设置磁贴"
                    : item.WidgetKind == WidgetKind.TileSet ? "编辑磁贴集" : "编辑音乐小组件")
                : _isGroup ? "编辑磁贴组" : "编辑快捷项";
        }
        else
        {
            // 添加模式：清空
            _editing = null;
            _isGroup = false;
            _isWidget = false;
            _pendingPhotoFolder = null;
            PhotoFolderRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PhotoSettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _pendingIcon = null;
            _pendingCover = null;
            PageTitle.Text = "添加快捷项";
            NameBox.Text = "";
            TargetBox.Text = "";
            AppRadio.IsChecked = true;
            CheckSize(TileSize.Wide);
            CheckColor(null);
            ShowPreview(null);
            ShowCoverPreview(null);
            TargetRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            KindColumn.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
        RebuildFormControls(); // 可见性决定导航序列（图片组含"选择文件夹"按钮）
        FocusForm(0);
    }

    /// <summary>手柄按键（MainWindow 转发，已在 UI 线程）</summary>
    public void HandleGamepad(GamepadButtons buttons)
    {
        if (_browsing) HandleBrowse(buttons);
        else HandleForm(buttons);
    }

    // ---- 表单模式 ----

    private void HandleForm(GamepadButtons b)
    {
        if (b.HasFlag(GamepadButtons.DPadUp)) MoveRow(-1);
        else if (b.HasFlag(GamepadButtons.DPadDown)) MoveRow(1);
        else if (b.HasFlag(GamepadButtons.DPadLeft)) MoveCol(-1);
        else if (b.HasFlag(GamepadButtons.DPadRight)) MoveCol(1);

        if (b.HasFlag(GamepadButtons.A)) ActivateForm();
        else if (b.HasFlag(GamepadButtons.X)) _ = SaveAsync();
        else if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View))
            Cancel();
    }

    private void MoveForm(int delta)
    {
        var next = Math.Clamp(_formIndex + delta, 0, _formControls.Length - 1);
        if (next == _formIndex) return;
        _formIndex = next;
        FocusForm(next);
    }

    // 按控件中心 Y 聚成的"行"，每行内按 X 排序（每次按键实时重建，滚动后坐标仍准确）。
    // 行感知导航取代加权最近邻：图标/图片等整行控件不会因列不对齐被跳过。
    private readonly List<List<(int idx, double x, double y)>> _rows = [];

    private void BuildRows()
    {
        _rows.Clear();
        var pts = new List<(int idx, double x, double y)>();
        for (var i = 0; i < _formControls.Length; i++)
        {
            if (_formControls[i].Visibility != Microsoft.UI.Xaml.Visibility.Visible) continue;
            if (!TryCenter(_formControls[i], out var x, out var y)) continue;
            pts.Add((i, x, y));
        }
        pts.Sort((a, b) => a.y.CompareTo(b.y));

        const double tol = 40; // 行间距 > 控件高+间隔，同行中心 Y 相同，40px 容差安全
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

    /// <summary>上下：移动到相邻行，取 X 最接近当前列的控件；到顶/到底不移动</summary>
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

    /// <summary>左右：同一行内移动；单控件行到头不移动</summary>
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

    private TileSize SelectedSize()
    {
        if (SmallSizeRadio.IsChecked == true) return TileSize.Small;
        if (LargeSizeRadio.IsChecked == true) return TileSize.Large;
        return TileSize.Wide;
    }

    private void CheckSize(TileSize size)
    {
        SmallSizeRadio.IsChecked = size == TileSize.Small;
        WideSizeRadio.IsChecked = size == TileSize.Wide;
        LargeSizeRadio.IsChecked = size == TileSize.Large;
    }

    private PhotoSpeed SelectedSpeed()
    {
        if (SpeedSlowRadio.IsChecked == true) return PhotoSpeed.Slow;
        if (SpeedArtRadio.IsChecked == true) return PhotoSpeed.Art;
        return PhotoSpeed.Fast;
    }

    private void ActivateForm()
    {
        switch (_formControls[_formIndex])
        {
            case TextBox tb:
                tb.Focus(Microsoft.UI.Xaml.FocusState.Keyboard); // 弹出输入法后可物理键盘输入
                break;
            case RadioButton rb:
                rb.IsChecked = true;
                break;
            case ToggleSwitch ts:
                ts.IsOn = !ts.IsOn;
                break;
            case Button btn when ReferenceEquals(btn, BrowseButton):
                // 按当前类型进入对应浏览：文件夹→选目录，其余→选 exe
                EnterBrowse(FolderRadio.IsChecked == true ? BrowseMode.Folder : BrowseMode.Program);
                break;
            case Button btn when ReferenceEquals(btn, IconButton):
                EnterBrowse(BrowseMode.Icon);
                break;
            case Button btn when ReferenceEquals(btn, CoverButton):
                EnterBrowse(BrowseMode.Cover);
                break;
            case Button btn when ReferenceEquals(btn, CoverClearButton):
                ClearCover();
                break;
            case Button btn when ReferenceEquals(btn, PhotoFolderButton):
                // 图片组：进入文件夹浏览，结果写入 _pendingPhotoFolder（不改 TargetBox/类型）
                _pickingPhotoFolder = true;
                EnterBrowse(BrowseMode.Folder);
                break;
            case Button btn when ReferenceEquals(btn, SaveButton):
                _ = SaveAsync();
                break;
        }
    }

    private void FocusForm(int i)
    {
        var c = _formControls[i];
        if (c is RadioButton rb)
        {
            // 单选按钮：移动只移动光标（高亮），不聚焦——WinUI 键盘聚焦会自动勾选，
            // 导致"移动即变更"；按 A 才真正切换选中。
            UpdateRadioCursor(rb);
            UpdateToggleCursor(null);
            EnsureVisible(c);
            return;
        }
        if (c is ToggleSwitch ts)
        {
            // 开关同理：移动只高亮，按 A 才切换
            UpdateRadioCursor(null);
            UpdateToggleCursor(ts);
            EnsureVisible(c);
            return;
        }
        UpdateRadioCursor(null);
        UpdateToggleCursor(null);
        c.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        EnsureVisible(c); // 窄窗口下焦点控件自动滚入可视区
    }

    /// <summary>高亮光标所在单选按钮（卡片描边）</summary>
    private void UpdateRadioCursor(RadioButton? cursor)
    {
        foreach (var ctl in _formControls)
        {
            if (ctl is not RadioButton r) continue;
            r.BorderBrush = ReferenceEquals(r, cursor)
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    /// <summary>高亮光标所在开关（描边）</summary>
    private void UpdateToggleCursor(ToggleSwitch? cursor)
    {
        foreach (var ctl in _formControls)
        {
            if (ctl is not ToggleSwitch t) continue;
            t.BorderBrush = ReferenceEquals(t, cursor)
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    /// <summary>把控件滚动到 ScrollViewer 可视区内（WinUI3 无 BringIntoView，用 ChangeView 手动实现）</summary>
    private void EnsureVisible(Control c)
    {
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

    private void Cancel()
    {
        // 图标文件按目标路径哈希命名，可能被多个快捷项共用，取消时不删除（孤立文件无害）
        // 磁贴集快照成员编辑：取消返回副本页
        if (_snapshotSetTarget != null) App.Main?.GoBackOne();
        else App.Main?.ShowMainPage();
    }

    private async Task SaveAsync()
    {
        var name = NameBox.Text.Trim();
        var target = TargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || (!_isGroup && !_isWidget && string.IsNullOrWhiteSpace(target)))
        {
            PageTitle.Text = _isGroup ? "名称不能为空"
                : _isWidget ? "名称不能为空" : "名称和路径不能为空";
            return;
        }

        var kind = _isGroup ? LaunchKind.Group
            : _isWidget ? LaunchKind.Widget
            : UrlRadio.IsChecked == true ? LaunchKind.Url
            : FolderRadio.IsChecked == true ? LaunchKind.Folder
            : LaunchKind.App;
        var items = ConfigService.Load();

        if (_editing != null)
        {
            // 定位原项：磁贴集快照成员在对应磁贴集的 Snapshot 内查找；普通项在主页列表查找
            LaunchItem? it;
            if (_snapshotSetTarget != null)
            {
                var set = items.FirstOrDefault(x => x.Target == _snapshotSetTarget &&
                    x.Kind == LaunchKind.Widget && x.WidgetKind == WidgetKind.TileSet);
                if (set is null) { PageTitle.Text = "未找到磁贴集"; return; }
                var mi = set.Snapshot.FindIndex(m => m.Target == _origTarget && m.Name == _origName);
                if (mi < 0) mi = set.Snapshot.FindIndex(m => m.Target == _origTarget);
                if (mi < 0) { PageTitle.Text = "未找到原磁贴"; return; }
                it = set.Snapshot[mi];
            }
            else
            {
                var idx = items.FindIndex(x => x.Target == _origTarget && x.Name == _origName);
                if (idx < 0) idx = items.FindIndex(x => x.Target == _origTarget && (!_isGroup || x.Kind == LaunchKind.Group));
                if (idx < 0) { PageTitle.Text = "未找到原快捷项"; return; }
                it = items[idx];
            }

            it.Name = name;
            // 小组件：音乐改名称与颜色；设置/磁贴集改名称、大小与颜色；图片组改名称、大小、文件夹与轮播设置（无颜色）
            if (_isWidget)
            {
                if (it.WidgetKind == WidgetKind.Photos)
                {
                    it.Size = SelectedSize();
                    it.SlideshowSpeed = SelectedSpeed();
                    it.PhotoAnimation = AnimationToggle.IsOn;
                    if (_pendingPhotoFolder != null)
                    {
                        it.WidgetFolder = _pendingPhotoFolder;
                        SlideshowService.Rescan(it.WidgetFolder,
                            SlideshowService.IntervalOf(it.SlideshowSpeed)); // 换文件夹后立即重扫
                    }
                }
                else if (it.WidgetKind == WidgetKind.Settings)
                {
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                }
                else if (it.WidgetKind == WidgetKind.TileSet)
                {
                    // 磁贴集：名称、大小、颜色、图标、图片均可改
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    if (_pendingIcon != null) it.IconFile = _pendingIcon;
                    if (_pendingCover != null)
                        it.CoverFile = _pendingCover.Length == 0 ? null : _pendingCover;
                }
                else
                {
                    it.TileColor = SelectedColor();
                }
            }
            else
            {
                if (!_isGroup) it.Target = target;
                it.Kind = kind;
                it.Size = SelectedSize();
                it.TileColor = SelectedColor();
                if (_pendingIcon != null) it.IconFile = _pendingIcon;
                // 程序/文件夹项且仍无图标：自动从 target 提取（组无 target，跳过）
                else if (!_isGroup && kind != LaunchKind.Url && string.IsNullOrEmpty(it.IconFile))
                    it.IconFile = await IconExtractService.ExtractAsync(target);
                if (_pendingCover != null)
                    it.CoverFile = _pendingCover.Length == 0 ? null : _pendingCover;
            }
        }
        else
        {
            var it = new LaunchItem
            {
                Name = name,
                Target = target,
                Kind = kind,
                Size = SelectedSize(),
                TileColor = SelectedColor(),
                IconFile = _pendingIcon,
                CoverFile = string.IsNullOrEmpty(_pendingCover) ? null : _pendingCover
            };
            if (kind != LaunchKind.Url && it.IconFile == null)
                it.IconFile = await IconExtractService.ExtractAsync(target);
            items.Add(it);
        }

        ConfigService.Save(items);
        // 磁贴集快照成员编辑：返回副本页（GoBack），其他编辑回主页
        if (_snapshotSetTarget != null) App.Main?.GoBackOne();
        else App.Main?.ShowMainPage();
    }

    private void OnSaveClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _ = SaveAsync();
    private void OnBrowseClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => EnterBrowse(FolderRadio.IsChecked == true ? BrowseMode.Folder : BrowseMode.Program);
    private void OnIconClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => EnterBrowse(BrowseMode.Icon);
    private void OnPhotoFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _pickingPhotoFolder = true;
        EnterBrowse(BrowseMode.Folder);
    }
    private void OnCoverClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => EnterBrowse(BrowseMode.Cover);
    private void OnCoverClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearCover();
    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Cancel();

    // ---- 浏览模式 ----

    private void EnterBrowse(BrowseMode mode)
    {
        _browsing = true;
        _browseMode = mode;
        FormScroll.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        BrowsePanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        PageTitle.Text = mode switch
        {
            BrowseMode.Icon => "选择图标（exe / ico / 图片）",
            BrowseMode.Cover => "选择磁贴图片（png / jpg / bmp…）",
            BrowseMode.Folder => "选择文件夹",
            _ => "选择程序（.exe）"
        };
        HintBar.SetHints(mode == BrowseMode.Folder
            ? "DPAD:选择|A:进入/选中|B:返回"
            : "DPAD:选择|A:进入/确认|B:返回");
        NavigateTo(GetStartDir());
    }

    private void ExitBrowse()
    {
        _browsing = false;
        _pickingPhotoFolder = false; // 取消浏览时同样复位，避免影响下次普通文件夹选择
        BrowsePanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        FormScroll.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        PageTitle.Text = _isGroup ? "编辑磁贴组" : _editing != null ? "编辑快捷项" : "添加快捷项";
        HintBar.SetHints("DPAD:选择|A:确认/切换|X:保存|B:返回");
        FocusForm(_formIndex);
    }

    private static string GetStartDir()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Directory.Exists(desktop)) return desktop;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void HandleBrowse(GamepadButtons b)
    {
        // B/View 优先处理：空目录也要能返回
        if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View))
        {
            ExitBrowse();
            return;
        }

        var count = FileList.Items.Count;
        if (count == 0) return;
        var sel = FileList.SelectedIndex < 0 ? 0 : FileList.SelectedIndex;

        if (b.HasFlag(GamepadButtons.DPadUp)) sel = Math.Max(0, sel - 1);
        else if (b.HasFlag(GamepadButtons.DPadDown)) sel = Math.Min(count - 1, sel + 1);
        FileList.SelectedIndex = sel;
        FileList.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        if (FileList.SelectedItem != null)
            FileList.ScrollIntoView(FileList.SelectedItem);

        if (b.HasFlag(GamepadButtons.A))
        {
            if (FileList.SelectedItem is FileEntry entry)
            {
                if (entry.IsSelectCurrent) SelectFolder(_currentDir);
                else if (entry.IsParent) GoParent();
                else if (entry.IsFolder)
                {
                    // 文件夹浏览模式：进入目录；其他模式：文件夹仅用于进入
                    NavigateTo(entry.Path);
                }
                else if (_browseMode == BrowseMode.Cover) _ = SelectCover(entry);
                else if (_browseMode == BrowseMode.Icon) SelectIcon(entry);
                else SelectExe(entry);
            }
        }
    }

    private async void NavigateTo(string dir)
    {
        _currentDir = dir;
        PathText.Text = dir;
        FileList.ItemsSource = null;

        var mode = _browseMode;
        // 目录枚举放后台线程，避免大目录阻塞 UI（卡死）
        var entries = await Task.Run(() =>
        {
            var list = new List<FileEntry>();
            // 各浏览用途对应的文件通配（文件夹模式只列目录）
            var patterns = mode switch
            {
                BrowseMode.Icon => new[] { "*.exe", "*.ico", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" },
                BrowseMode.Cover => new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" },
                BrowseMode.Folder => [],
                _ => new[] { "*.exe" }
            };
            try
            {
                var parent = Directory.GetParent(dir);
                if (parent != null)
                    list.Add(new FileEntry("上一级", parent.FullName, true, true));

                if (mode == BrowseMode.Folder)
                {
                    var curName = Path.GetFileName(dir);
                    list.Add(new FileEntry(
                        string.IsNullOrEmpty(curName) ? dir : $"✔ 选择当前文件夹：{curName}",
                        dir, false, false, IsSelectCurrent: true));
                }

                foreach (var d in Directory.EnumerateDirectories(dir)
                             .Where(d => !IsHidden(d))
                             .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    list.Add(new FileEntry(Path.GetFileName(d), d, true, false));

                foreach (var pattern in patterns)
                    foreach (var f in Directory.EnumerateFiles(dir, pattern)
                                 .Where(f => !IsHidden(f))
                                 .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                        list.Add(new FileEntry(Path.GetFileNameWithoutExtension(f), f, false, false));
                list = list.DistinctBy(e => e.Path).ToList();
            }
            catch { /* 无权限目录：显示已收集到的条目 */ }
            return list;
        });

        if (_currentDir != dir || _browseMode != mode) return; // 期间已切换状态，丢弃过期结果

        FileList.ItemsSource = entries;
        // 越界防护：只有存在"上一级"且还有其他条目时才选到第 2 项；
        // Items 可能尚未同步到控件，设置失败不影响浏览
        try
        {
            if (entries.Count > 0)
                FileList.SelectedIndex = entries[0].IsParent && entries.Count > 1 ? 1 : 0;
        }
        catch { }
    }

    private static bool IsHidden(string path)
    {
        // junction/特殊系统项上 GetAttributes 会抛异常，必须就地吞掉
        try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
        catch { return false; }
    }

    private void GoParent()
    {
        var parent = Directory.GetParent(_currentDir);
        if (parent == null) return;
        NavigateTo(parent.FullName);
    }

    private void SelectExe(FileEntry entry)
    {
        TargetBox.Text = entry.Path;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = entry.Name;
        AppRadio.IsChecked = true;
        ExitBrowse();
    }

    /// <summary>选文件夹：填入当前浏览目录并置类型为文件夹</summary>
    private void SelectFolder(string dir)
    {
        // 图片组选定向文件夹：只记录到 _pendingPhotoFolder，不改 TargetBox/类型（Target 是组件标识）
        if (_pickingPhotoFolder)
        {
            _pendingPhotoFolder = dir;
            PhotoFolderText.Text = dir;
            _pickingPhotoFolder = false;
            ExitBrowse();
            return;
        }
        TargetBox.Text = dir;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileName(dir) is { Length: > 0 } n ? n : dir;
        FolderRadio.IsChecked = true;
        ExitBrowse();
    }

    /// <summary>选图标：提取/复制为 PNG 并更新预览（保存时才落盘到配置）</summary>
    private async void SelectIcon(FileEntry entry)
    {
        PageTitle.Text = "正在提取图标…";
        var fileName = await IconExtractService.ExtractAsync(entry.Path);
        if (fileName != null)
        {
            _pendingIcon = fileName;
            ShowPreview(fileName);
        }
        ExitBrowse();
    }

    /// <summary>选磁贴背景图片：复制为 PNG 并更新预览（保存时才落盘到配置）</summary>
    private async Task SelectCover(FileEntry entry)
    {
        PageTitle.Text = "正在处理图片…";
        var fileName = await IconExtractService.ExtractAsync(entry.Path);
        if (fileName != null)
        {
            _pendingCover = fileName;
            ShowCoverPreview(fileName);
        }
        ExitBrowse();
    }

    /// <summary>清除磁贴背景图片（恢复图标+名称显示）</summary>
    private void ClearCover()
    {
        _pendingCover = "";
        ShowCoverPreview(null);
    }

    /// <summary>表单磁贴图片预览</summary>
    private void ShowCoverPreview(string? coverFile)
    {
        var path = coverFile == null ? null : Path.Combine(LaunchItem.IconsDir, coverFile);
        if (path != null && File.Exists(path))
        {
            try { CoverPreviewImage.Source = new BitmapImage(new Uri(path)); } catch { }
            CoverPreviewImage.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CoverPreviewGlyph.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            CoverClearButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
        else
        {
            CoverPreviewImage.Source = null;
            CoverPreviewImage.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            CoverPreviewGlyph.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CoverClearButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        RebuildFormControls();
    }

    /// <summary>表单图标预览</summary>
    private void ShowPreview(string? iconFile)
    {
        var path = iconFile == null ? null : Path.Combine(LaunchItem.IconsDir, iconFile);
        if (path != null && File.Exists(path))
        {
            PreviewImage.Source = new BitmapImage(new Uri(path));
            PreviewImage.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PreviewGlyph.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        else
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PreviewGlyph.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PreviewGlyph.Glyph = UrlRadio.IsChecked == true ? "\uE774"
                : FolderRadio.IsChecked == true ? "\uE8B7" : "\uE7F8";
        }
    }
}
