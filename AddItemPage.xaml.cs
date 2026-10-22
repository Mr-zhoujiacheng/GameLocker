using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
public sealed partial class AddItemPage : Page, ISettingsGamepad
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

    /// <summary>本次会话中已提取/选择的图标文件名（null=未改动）</summary>
    private string? _pendingIcon;

    /// <summary>本次会话中磁贴背景图片的改动（null=未改动；""=清除）</summary>
    private string? _pendingCover;

    /// <summary>图片组：本次会话选定的文件夹（null=未改动）</summary>
    private string? _pendingPhotoFolder;

    /// <summary>游戏存档：本次会话选定的备份文件夹（null=未改动）</summary>
    private string? _pendingBackupFolder;

    /// <summary>磁贴抽屉：本次会话编辑中的抽屉列表（含名称）</summary>
    private List<DrawerPage> _pendingDrawers = [];

    /// <summary>磁贴抽屉：当前正在编辑的抽屉下标</summary>
    private int _drawerCursor;

    /// <summary>当前浏览是否为"图片组选文件夹"（区别于普通 Folder 类型项选路径）</summary>
    private bool _pickingPhotoFolder;

    /// <summary>当前浏览是否为"游戏存档选文件夹"</summary>
    private bool _pickingBackupFolder;

    /// <summary>文件传输：本次会话选定的可发送/接收目录（null=未改动；""=恢复默认下载文件夹）</summary>
    private string? _pendingFtSendFolder;
    private string? _pendingFtRecvFolder;

    /// <summary>当前浏览是在给"文件传输"选可发送目录还是接收目录</summary>
    private bool _pickingFtSendFolder;
    private bool _pickingFtRecvFolder;

    /// <summary>系统小组件：启用的面板项胶囊按钮（下标 = SystemPanelService.Order 的序号）</summary>
    private ToggleButton[] _sysOptBoxes = [];

    /// <summary>系统小组件：胶囊右侧的勾/叉状态图标（与 _sysOptBoxes 同下标）</summary>
    private FontIcon[] _sysOptMarks = [];

    // 胶囊选中/未选视觉（代码直设，不走 VisualState）
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SysChipOnBrush = MakeBrush(0x33, 0x2E, 0x6B, 0xE6);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SysChipOffBrush = MakeBrush(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SysChipOnTextBrush = MakeBrush(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SysChipOffTextBrush = MakeBrush(0xE6, 0xEE, 0xF9);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SysChipOnMarkBrush = MakeBrush(0xBB, 0xD8, 0xFF);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SysChipOffMarkBrush = MakeBrush(0x59, 0xFF, 0xFF, 0xFF);

    // ---- 映射小组件的按键捕获状态（实现 ISettingsGamepad，MainWindow 据此转发原始手柄按键） ----

    /// <summary>是否处于映射按键捕获模式</summary>
    public bool IsCapturing => _mappingCapturing;
    private bool _mappingCapturing;
    /// <summary>本次捕获/编辑得到的手柄键（0=未映射手柄键）</summary>
    private int _pendingPad;
    /// <summary>本次捕获/编辑得到的键盘虚拟键码（0=未映射键盘键）</summary>
    private int _pendingKey;
    /// <summary>本次捕获/编辑得到的键盘修饰键标志</summary>
    private int _pendingMods;
    /// <summary>捕获过程中实时累积的修饰键状态（键盘按键按住期间）</summary>
    private int _liveMods;

    // ---- 手柄映射小组件：可视化手柄编辑状态 ----

    /// <summary>捕获模式：区分映射小组件捕获与手柄映射的目标捕获</summary>
    private enum CaptureMode { Mapping, MapTargetKey, MapTargetPad }
    private CaptureMode _captureMode = CaptureMode.Mapping;
    /// <summary>当前正在捕获的映射（手柄映射捕获时用；映射小组件捕获为 null）</summary>
    private GamepadMapping? _captureMapping;

    /// <summary>本次会话编辑的映射列表（深拷贝；保存时写回 item.GamepadMappings）</summary>
    private List<GamepadMapping>? _pendingGamepadMappings;

    /// <summary>手柄图上当前选中的源手柄键（None=未选中，此时编辑面板收起）</summary>
    private GamepadButtons _selectedMapSource = GamepadButtons.None;

    /// <summary>手柄图上的一个可点击热区（对应一个手柄键，既可点击也可用手柄 A 键确认）</summary>
    private sealed class MapZoneUi
    {
        public GamepadButtons Button;
        public Button Hit = null!;
        public Microsoft.UI.Xaml.Shapes.Shape Visual = null!;
        public TextBlock Label = null!;
    }

    private readonly List<MapZoneUi> _mapZones = [];

    /// <summary>手柄图底图在画布中的尺寸（Assets/gamepad-base.png 为 460×311.6，1:1 铺满画布）</summary>
    private const double PadCanvasWidth = 460;

    private const double PadCanvasHeight = 311.6;

    /// <summary>手柄热区布局：键 / 相对画布的左边距 / 上边距 / 宽 / 高 / 标注文字 / 是否圆形。
    /// 坐标是把参考实现（YeManCC）GamepadVisualizer 的手柄按键位置，经底图裁剪映射换算到 460×311.6 画布后得到，
    /// 因此热区与底图上的按键轮廓一一对应。</summary>
    private static readonly (GamepadButtons Btn, double X, double Y, double W, double H, string Text, bool Round)[] ZoneLayout =
    [
        // 顶部扳机（机身肩部凸起的上半）与肩键（其下方的弧带）
        (GamepadButtons.LeftTrigger,   71.75, 3.60, 65.8, 30.0, "LT", false),
        (GamepadButtons.RightTrigger,  324.35, 3.60, 65.8, 30.0, "RT", false),
        (GamepadButtons.LeftShoulder,  58.31, 37.61, 86.8, 36.0, "LB", false),
        (GamepadButtons.RightShoulder, 316.79, 37.61, 86.8, 36.0, "RB", false),
        // 西瓜键：热区正好套住底图中心的 Xbox 标志，故不写字（标志本身即说明） / 视图 / 菜单
        (GamepadButtons.Guide,         209.90, 60.10, 40.0, 40.0, "", true),
        (GamepadButtons.View,          180.61, 123.75, 23.7, 23.4, "Select", true),
        (GamepadButtons.Menu,          251.47, 123.75, 23.7, 23.4, "Start", true),
        // 左摇杆按下 / 右摇杆按下
        (GamepadButtons.LeftThumb,     74.13, 105.28, 61.1, 60.3, "L3", true),
        (GamepadButtons.RightThumb,    257.42, 175.28, 67.2, 66.4, "R3", true),
        // 十字键：四块按同一中心（168.57, 209.25）严丝合缝拼成一个十字，中间不留空洞
        (GamepadButtons.DPadUp,        154.71, 170.44, 27.7, 38.81, "↑", false),
        (GamepadButtons.DPadDown,      154.71, 209.25, 27.7, 38.76, "↓", false),
        (GamepadButtons.DPadLeft,      125.03, 194.10, 43.54, 30.3, "←", false),
        (GamepadButtons.DPadRight,     168.57, 194.10, 43.54, 30.3, "→", false),
        // 右侧 ABXY（菱形排布，与真实手柄按键间距一致）
        (GamepadButtons.Y,             342.20, 93.18, 33.2, 32.8, "Y", true),
        (GamepadButtons.B,             373.00, 120.57, 33.2, 32.8, "B", true),
        (GamepadButtons.A,             342.20, 151.01, 33.2, 32.8, "A", true),
        (GamepadButtons.X,             311.39, 120.57, 33.2, 32.8, "X", true),
    ];

    /// <summary>热区外的额外命中留白（触及目标更大；顶部扳机/肩键用更小的值以免互相压盖）</summary>
    private const double ZoneHitPadding = 10;

    private const double ShoulderHitPadding = 4;

    /// <summary>鼠标目标循环顺序（目标类型=鼠标时，点击"切换鼠标键"依次轮换）</summary>
    private static readonly int[] MouseOrder =
    [
        KeySendService.MouseLeft, KeySendService.MouseRight, KeySendService.MouseMiddle,
        KeySendService.MouseX1, KeySendService.MouseX2, KeySendService.MouseWheelUp, KeySendService.MouseWheelDown
    ];

    // 手柄图配色（底图浅灰线稿 + 按键半透明填充：未映射=淡蓝灰、映射=强调蓝、当前选中=琥珀描边）。
    // 实例字段：保证画刷在 UI 线程构造时创建
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush ZoneFillBrush = MakeBrush(0x66, 0x2A, 0x34, 0x46);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush ZoneStrokeBrush = MakeBrush(0x8F, 0xA3, 0xBF);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush MappedFillBrush = MakeBrush(0xE0, 0x2E, 0x6B, 0xE6);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush MappedStrokeBrush = MakeBrush(0xBB, 0xD8, 0xFF);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedStrokeBrush = MakeBrush(0xFF, 0xD1, 0x66);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush LabelBrush = MakeBrush(0xE6, 0xEE, 0xF9);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush LabelMappedBrush = MakeBrush(0xFF, 0xFF, 0xFF);

    // 手柄导航光标描边（白色 0xCC，与全局卡片式控件的光标高亮一致）
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush CursorBrush = MakeBrush(0xCC, 0xFF, 0xFF, 0xFF);

    private static Microsoft.UI.Xaml.Media.SolidColorBrush MakeBrush(byte r, byte g, byte b)
        => new(Windows.UI.Color.FromArgb(255, r, g, b));

    private static Microsoft.UI.Xaml.Media.SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
        => new(Windows.UI.Color.FromArgb(a, r, g, b));

    public AddItemPage()
    {
        InitializeComponent();
        _sysOptBoxes = [SysOptSettings, SysOptThisPc, SysOptVolume, SysOptBrightness, SysOptWifi, SysOptBluetooth, SysOptHotspot, SysOptNightLight];
        _sysOptMarks = [SysOptSettingsMark, SysOptThisPcMark, SysOptVolumeMark, SysOptBrightnessMark, SysOptWifiMark, SysOptBluetoothMark, SysOptHotspotMark, SysOptNightLightMark];
        for (var i = 0; i < _sysOptBoxes.Length; i++)
        {
            var chip = _sysOptBoxes[i];
            var mark = _sysOptMarks[i];
            chip.Checked += (_, _) => ApplySysChipVisual(chip, mark, true);
            chip.Unchecked += (_, _) => ApplySysChipVisual(chip, mark, false);
        }
        BuildGamepadMapDiagram();
        // OptiScaler 下拉框选项来自服务层数组，保证与写入配置的值一致
        FillOptiCombo(OptiDllCombo, OptiScalerService.ProxyOptions);
        // 版本渠道下拉项带"已下载到的版本号"后缀，故每次进页面都重新灌一次；默认选"正式版"
        FillOptiCombo(OptiVersionCombo, OptiScalerService.GetVersionChannelOptions());
        // 具体版本先只放"最新版"，进编辑页后异步补上 GitHub 上可下载的版本列表
        FillOptiCombo(OptiVersionTagCombo, [("", "最新版（自动）")]);
        FillOptiCombo(OptiFsr4VersionCombo, [("", "最新版（自动）")]);
        _suppressOptiTagEvent = true;
        OptiVersionTagCombo.SelectedIndex = 0;
        OptiFsr4VersionCombo.SelectedIndex = 0;
        _suppressOptiTagEvent = false;
        OptiVersionCombo.SelectedIndex = 0;
        FillOptiCombo(OptiUpscalerCombo, OptiScalerService.UpscalerOptions);
        FillOptiCombo(OptiFgInputCombo, OptiScalerService.FgInputOptions);
        FillOptiCombo(OptiFgOutputCombo, OptiScalerService.FgOutputOptions);
        FillOptiCombo(OptiFgMultiplierCombo, OptiScalerService.FgMultiplierOptions);
        FillOptiCombo(OptiFsr4PresetCombo, OptiScalerService.Fsr4PresetOptions);
        FillOptiCombo(OptiFsr4FilesCombo, OptiScalerService.Fsr4FilesOptions);
        UpdateOptiFsr4Row();  // 画质提升默认"自动" → 先收起 FSR4 画质档
        UpdateOptiFgRows();   // 帧生成默认关闭 → 先收起输入/输出/倍数三项（内部会重建手柄导航序列）

        // 排版自适应：窗口宽度变化（侧边栏 ↔ 全屏/居中）时重算表单宽度并居中，三种模式排版一致
        FormScroll.SizeChanged += (_, _) => LayoutForm();
        Loaded += (_, _) => LayoutForm();
    }

    /// <summary>表单最大宽度：宽窗口（全屏/居中）下不铺满，保持与侧边栏下一致的阅读宽度</summary>
    private const double FormMaxWidth = 640;

    /// <summary>按视口宽度设置外层容器宽度、表单限宽居中（不依赖 ScrollViewer 的内容对齐，
    /// 在 WinUI 里 HorizontalContentAlignment 常不生效导致内容靠左）</summary>
    private void LayoutForm()
    {
        if (FormHost is null || FormPanel is null || FormScroll is null) return;
        var avail = FormScroll.ViewportWidth;   // 已扣除 ScrollViewer 的 Padding
        if (avail <= 0) return;
        FormHost.Width = avail;
        FormPanel.Width = Math.Min(FormMaxWidth, avail);
        // 保存按钮与表单同宽居中：窄窗口下占满可用宽（与原样式一致的大按钮），宽窗口下不超过表单宽
        SaveButton.Width = FormPanel.Width;
    }

    /// <summary>八个磁贴颜色单选（Tag=#RRGGBB）+ 无颜色（Tag=none）</summary>
    private RadioButton[] ColorRadios =>
    [
        ColorBlueRadio, ColorRedRadio, ColorGreenRadio,
        ColorPurpleRadio, ColorNoneRadio, ColorTealRadio,
        ColorYellowRadio, ColorPinkRadio, ColorIndigoRadio
    ];

    /// <summary>当前已展开下拉的 OptiScaler 下拉框（非空时方向键在其选项内移动、A 确认、B 取消）</summary>
    private ComboBox? _openCombo;

    /// <summary>展开下拉前的已选索引（B 取消时回滚）</summary>
    private int _comboBackupIndex;

    /// <summary>程序内回填"具体版本 / FSR4 版本"下拉时，抑制 SelectionChanged 触发的后台下载</summary>
    private bool _suppressOptiTagEvent;

    /// <summary>编辑初始化时要回填的版本标签（各自用一次）</summary>
    private string _savedOptiTag = "";
    private string _savedFsr4Tag = "";

    /// <summary>版本列表请求代次：渠道/变体快速切换时只采纳最后一次请求的结果</summary>
    private int _optiVersionGen;
    private int _fsr4VersionGen;

    /// <summary>编辑初始化时程序内回填下拉，抑制 SelectionChanged 的副作用（加载/下载）</summary>
    private bool _suppressOptiVersionEvent;
    private bool _suppressFsr4FilesEvent;

    /// <summary>把 OptiScalerService 里的选项数组灌进下拉框（Tag=写入配置的值）</summary>
    private static void FillOptiCombo(ComboBox cb, (string Value, string Label)[] options)
    {
        cb.Items.Clear();
        foreach (var o in options)
            cb.Items.Add(new ComboBoxItem { Content = o.Label, Tag = o.Value });
    }

    /// <summary>下拉框当前项对应的配置值（空串=自动）</summary>
    private static string OptiComboValue(ComboBox cb) =>
        (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>按配置值反查下拉框索引并选中（找不到或空值=第一项"自动"）</summary>
    private static void SelectOptiCombo(ComboBox cb, (string Value, string Label)[] options, string? value)
    {
        var want = value ?? "";
        var idx = Array.FindIndex(options, o => string.Equals(o.Value, want, StringComparison.OrdinalIgnoreCase));
        cb.SelectedIndex = idx < 0 ? 0 : idx;
    }

    /// <summary>表单导航序列：只纳入"有效可见"的控件（自身与所属分组容器都 Visible），
    /// 并按页面从上到下的视觉顺序排列。手柄映射编辑时标题/路径/类型/图标整块被隐藏，
    /// 若仍按控件自身 Visibility 入列，方向键会走进看不见的 TextBox/单选——
    /// 表现就是"进手柄映射编辑后光标看不见、往下走了再往上回不到图区"。</summary>
    private void RebuildFormControls()
    {
        var vis = Microsoft.UI.Xaml.Visibility.Visible;
        var list = new List<Control>();
        if (NameBox.Visibility == vis) list.Add(NameBox);
        // 路径 + 浏览（手柄映射/小组件/磁贴组编辑时整块隐藏，按容器判定）
        if (TargetRow.Visibility == vis) { list.Add(TargetBox); list.Add(BrowseButton); }
        // 图片组定向文件夹 / 轮播速度与动画
        if (PhotoFolderRow.Visibility == vis) list.Add(PhotoFolderButton);
        if (PhotoSettingsPanel.Visibility == vis)
        {
            list.Add(SpeedFastRadio);
            list.Add(SpeedSlowRadio);
            list.Add(SpeedArtRadio);
            list.Add(AnimationToggle);
        }
        // 文件传输：可发送目录 / 接收目录
        if (FileTransferRow.Visibility == vis)
        {
            list.Add(FtSendFolderButton);
            list.Add(FtRecvFolderButton);
        }
        // 映射小组件：按键捕获按钮
        if (MappingRow.Visibility == vis) list.Add(MappingCaptureButton);
        // 修改器小组件：打开已下载修改器目录按钮
        if (TrainerRow.Visibility == vis) list.Add(OpenTrainerFolderButton);
        // 性能监控：悬浮条位置（六选一）/ 大小 / 不透明度
        if (MonitorRow.Visibility == vis)
        {
            list.Add(MonSizeSmall);
            list.Add(MonSizeNormal);
            list.Add(MonSizeLarge);
            list.Add(MonOpaque100);
            list.Add(MonOpaque50);
            list.Add(MonOpaque0);
            list.Add(MonPosTopLeft);
            list.Add(MonPosTopCenter);
            list.Add(MonPosTopRight);
            list.Add(MonPosBottomLeft);
            list.Add(MonPosBottomCenter);
            list.Add(MonPosBottomRight);
        }
        // 游戏存档小组件：备份方式 / 保留份数，以及按方式显示的本地面板或云端面板
        if (BackupRow.Visibility == vis)
        {
            list.Add(BackupLocalRadio);
            list.Add(BackupCloudRadio);
            if (BackupLocalPanel.Visibility == vis)
            {
                list.Add(BackupFolderButton);
                list.Add(OpenBackupFolderButton);
            }
            if (BackupCloudPanel.Visibility == vis) list.Add(CloudSettingsButton);
            list.Add(Keep3Radio);
            list.Add(Keep5Radio);
            list.Add(Keep10Radio);
        }
        // 手柄映射小组件：手柄图上的每个按键热区；选中某个键后再加入其输出类型/捕获/删除
        if (GamepadMapRow.Visibility == vis)
        {
            foreach (var zone in _mapZones)
                list.Add(zone.Hit);
            if (GamepadMapEditor.Visibility == vis)
            {
                list.Add(GamepadMapKeyRadio);
                list.Add(GamepadMapMouseRadio);
                list.Add(GamepadMapPadRadio);
                list.Add(GamepadMapCaptureButton);
                list.Add(GamepadMapDeleteButton);
            }
        }
        // OptiScaler 小组件：注入 DLL 名 / 画质提升（含 FSR4 子项）/ 版本渠道 + 具体版本 / 本地包 / 一键还原与打开目录
        if (OptiScalerRow.Visibility == vis)
        {
            list.Add(OptiDllCombo);
            list.Add(OptiUpscalerCombo);
            // 仅"画质提升"选到 FSR4 时才显示 FSR4 画质档 / 文件 / 版本 → 收起时不纳入导航
            if (OptiFsr4Cell.Visibility == vis)
            {
                list.Add(OptiFsr4PresetCombo);
                list.Add(OptiFsr4FilesCombo);
                if (OptiFsr4VersionCell.Visibility == vis) list.Add(OptiFsr4VersionCombo);
            }
            list.Add(OptiVersionCombo);
            // 本地包渠道没有网上版本可选 → "具体版本"已收起，不纳入导航
            if (OptiVersionTagCell.Visibility == vis) list.Add(OptiVersionTagCombo);
            if (OptiLocalRow.Visibility == vis) list.Add(OptiLocalFolderButton);
            list.Add(OptiFrameGenToggle);
            // 两列并排的视觉顺序：帧生成 → 输入 → 输出 → 倍数；关闭时后三格收起，不纳入导航
            if (OptiFgInputCell.Visibility == vis) list.Add(OptiFgInputCombo);
            if (OptiFgOutputCell.Visibility == vis) list.Add(OptiFgOutputCombo);
            if (OptiFgMultiplierCell.Visibility == vis) list.Add(OptiFgMultiplierCombo);
            // 一键还原：有备份时先选游戏（下拉），再按还原按钮
            if (OptiRestoreCombo.Items.Count > 0) list.Add(OptiRestoreCombo);
            list.Add(OptiRestoreButton);
            list.Add(OptiOpenFolderButton);
        }
        // 磁贴抽屉：抽屉名称 + 上一个/下一个切换 + 新增/删除
        if (DrawerRow.Visibility == vis)
        {
            list.Add(DrawerNameBox);
            list.Add(DrawerPrevButton);
            list.Add(DrawerNextButton);
            list.Add(DrawerAddButton);
            list.Add(DrawerDeleteButton);
        }
        // 组件标题（仅手柄映射，位于类型/大小/颜色行上方）/ 系统小组件的启用项 / 类型 / 磁贴大小 / 磁贴颜色（各自容器隐藏时跳过）
        if (TitleColumn.Visibility == vis) list.Add(WidgetTitleBox);
        if (SystemOptionsPanel.Visibility == vis) list.AddRange(_sysOptBoxes);
        if (KindColumn.Visibility == vis) { list.Add(AppRadio); list.Add(UrlRadio); list.Add(FolderRadio); }
        if (SizeColumn.Visibility == vis) { list.Add(SmallSizeRadio); list.Add(WideSizeRadio); list.Add(LargeSizeRadio); }
        if (ColorColumn.Visibility == vis) list.AddRange(ColorRadios);
        // 图标 / 磁贴图片
        if (IconRow.Visibility == vis) list.Add(IconButton);
        if (CoverRow.Visibility == vis)
        {
            list.Add(CoverButton);
            if (CoverClearButton.Visibility == vis) list.Add(CoverClearButton);
        }
        list.Add(SaveButton);

        // 重建后按"控件引用"回填光标下标：列表顺序变化/条目增减都不会让光标跳到别的控件
        var prev = _formControls is { Length: > 0 } && _formIndex >= 0 && _formIndex < _formControls.Length
            ? _formControls[_formIndex] : null;
        _formControls = list.ToArray();
        if (prev is not null)
        {
            var i = Array.IndexOf(_formControls, prev);
            _formIndex = i >= 0 ? i : Math.Clamp(_formIndex, 0, _formControls.Length - 1);
        }
    }

    /// <summary>系统小组件：按位掩码勾选"显示的项目"（bit = 面板项序号）</summary>
    private void CheckSystemOptions(int mask)
    {
        for (var i = 0; i < _sysOptBoxes.Length; i++)
        {
            var on = (mask & (1 << i)) != 0;
            _sysOptBoxes[i].IsChecked = on;
            ApplySysChipVisual(_sysOptBoxes[i], _sysOptMarks[i], on); // 事件在 null→false 时不触发，显式兜底
        }
    }

    /// <summary>胶囊选中视觉：底色/文字色/右侧勾(选中)或叉(未选)。
    /// 该自定义模板下 VisualState 的 Setter 实测不生效，改为代码直设。</summary>
    private static void ApplySysChipVisual(ToggleButton chip, FontIcon mark, bool on)
    {
        chip.Background = on ? SysChipOnBrush : SysChipOffBrush;
        chip.Foreground = on ? SysChipOnTextBrush : SysChipOffTextBrush;
        mark.Glyph = on ? "\uE73E" : "\uE711";
        mark.Foreground = on ? SysChipOnMarkBrush : SysChipOffMarkBrush;
    }

    /// <summary>系统小组件：把勾选结果读成位掩码（0 = 一个都没勾）</summary>
    private int SelectedSystemMask()
    {
        var mask = 0;
        for (var i = 0; i < _sysOptBoxes.Length; i++)
            if (_sysOptBoxes[i].IsChecked == true) mask |= 1 << i;
        return mask;
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

    /// <summary>编辑模式：预填表单并按类型裁剪可见区域</summary>
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
        _pendingBackupFolder = null;
        _pendingFtSendFolder = null;
        _pendingFtRecvFolder = null;
        NameBox.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        NameBox.Header = "显示名称";
        NameBox.PlaceholderText = "例如：Steam";
        NameBox.Text = item.Name;
        TargetBox.Text = item.Target;
        AppRadio.IsChecked = item.Kind == LaunchKind.App;
        UrlRadio.IsChecked = item.Kind == LaunchKind.Url;
        FolderRadio.IsChecked = item.Kind == LaunchKind.Folder;
        CheckSize(item.Size);
        CheckColor(item.TileColor);
        ShowPreview(item.IconFile);
        ShowCoverPreview(item.CoverFile);
        // 组没有"类型/路径"概念，隐藏对应区域（RebuildFormControls 按容器可见性自动跳过）
        var vis = _isGroup ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        TargetRow.Visibility = vis;
        KindColumn.Visibility = vis;
        // 小组件：隐藏路径/类型；音乐保留"名称+颜色"，
        // 图片组保留"名称+大小+图片文件夹+轮播速度/动画/展示模式"（无颜色），
        // 设置等小组件保留"名称+大小+颜色"
        if (_isWidget)
        {
            var w = Microsoft.UI.Xaml.Visibility.Collapsed;
            TargetRow.Visibility = w;
            KindColumn.Visibility = w;
            var isPhoto = item.WidgetKind == WidgetKind.Photos;
            // 图标/图片：小组件均不可换（固定样式或整块铺图）
            IconHeaderText.Visibility = w;
            IconRow.Visibility = w;
            CoverHeaderText.Visibility = w;
            CoverRow.Visibility = w;
            // 音乐固定 2×2、系统固定"长"不可改大小；其余小组件（图片组/设置/电源/天气/映射/修改器/游戏存档/手柄映射）可编辑大小。
            // 颜色：图片组不需要（整块铺图）
            SizeColumn.Visibility = isPhoto || item.WidgetKind is WidgetKind.Settings or WidgetKind.Power or WidgetKind.Weather or WidgetKind.Mapping or WidgetKind.Trainer or WidgetKind.Backup or WidgetKind.GamepadMap or WidgetKind.OptiScaler or WidgetKind.RecycleBin or WidgetKind.Foreground or WidgetKind.Monitor
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            ColorColumn.Visibility = isPhoto || TileStyle.IsWin10Theme
                ? w : Microsoft.UI.Xaml.Visibility.Visible;
            PhotoFolderRow.Visibility = isPhoto
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            PhotoSettingsPanel.Visibility = isPhoto
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            // 映射：名称、大小、颜色 + 按键捕获（无图标/图片）
            var isMapping = item.WidgetKind == WidgetKind.Mapping;
            MappingRow.Visibility = isMapping
                ? Microsoft.UI.Xaml.Visibility.Visible : w;
            // 修改器：名称、大小、颜色 + 「已下载修改器」（点击跳转存放目录）
            var isTrainer = item.WidgetKind == WidgetKind.Trainer;
            TrainerRow.Visibility = isTrainer ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isTrainer) TrainerFolderText.Text = TrainerService.TrainersDir;
            // 游戏存档：名称、大小、颜色 + 备份方式 / 备份文件夹 / 保留份数 / 打开目录
            var isBackup = item.WidgetKind == WidgetKind.Backup;
            BackupRow.Visibility = isBackup ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isBackup)
            {
                BackupCloudRadio.IsChecked = item.BackupMode == BackupMode.Cloud;
                BackupLocalRadio.IsChecked = item.BackupMode != BackupMode.Cloud;
                BackupFolderText.Text = string.IsNullOrEmpty(item.BackupFolder) ? "默认目录" : item.BackupFolder!;
                Keep3Radio.IsChecked = item.BackupKeep == 3;
                Keep10Radio.IsChecked = item.BackupKeep == 10;
                Keep5Radio.IsChecked = item.BackupKeep is not 3 and not 10; // 兜底：非 3/10 一律按 5
                UpdateBackupPanels();
            }
            // 性能监控：大小、颜色 + 悬浮条位置 / 大小 / 透明度（磁贴固定显示“性能监控”，无需自定义名称）
            var isMonitor = item.WidgetKind == WidgetKind.Monitor;
            MonitorRow.Visibility = isMonitor ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isMonitor)
            {
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "性能监控";
                NameBox.Visibility = w;
                CheckMonitorSettings(item.MonitorPos, item.MonitorScale, item.MonitorOpacity);
            }
            // 文件传输：名称、大小、颜色 + 可发送目录 / 接收目录（留空=系统下载文件夹）
            var isFileTransfer = item.WidgetKind == WidgetKind.FileTransfer;
            FileTransferRow.Visibility = isFileTransfer ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isFileTransfer)
            {
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "文件传输";
                FtSendFolderText.Text = string.IsNullOrWhiteSpace(item.FileTransferSendFolder)
                    ? "未设置（默认：下载文件夹）" : item.FileTransferSendFolder!;
                FtRecvFolderText.Text = string.IsNullOrWhiteSpace(item.FileTransferRecvFolder)
                    ? "未设置（默认：下载文件夹）" : item.FileTransferRecvFolder!;
            }
            // OptiScaler：名称、大小、颜色 + 注入 DLL 名 / ini 关键项 / 本地包 / 一键还原
            var isOptiScaler = item.WidgetKind == WidgetKind.OptiScaler;
            OptiScalerRow.Visibility = isOptiScaler ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isOptiScaler)
            {
                // 磁贴固定显示图标 + "OptiScaler"名称，无需用户自定义 → 隐藏顶部"显示名称"输入框
                // （NameBox.Text 仍保留原值，保存时不改变名称；InitForEdit 每次会把它重置为 Visible，不会残留）
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "OptiScaler";
                NameBox.Visibility = w;
                SelectOptiCombo(OptiDllCombo, OptiScalerService.ProxyOptions, item.OptiDllName);
                // 保存过的具体版本先记下，等版本列表拉回来后回填（下拉里一开始只有"最新版"）
                _savedOptiTag = item.OptiVersionTag ?? "";
                _savedFsr4Tag = item.OptiFsr4VersionTag ?? "";
                _suppressOptiVersionEvent = true;
                SelectOptiCombo(OptiVersionCombo, OptiScalerService.GetVersionChannelOptions(), item.OptiChannel);
                _suppressOptiVersionEvent = false;
                SelectOptiCombo(OptiUpscalerCombo, OptiScalerService.UpscalerOptions, item.OptiDx12Upscaler);
                SelectOptiCombo(OptiFgInputCombo, OptiScalerService.FgInputOptions, item.OptiFgInput);
                SelectOptiCombo(OptiFgOutputCombo, OptiScalerService.FgOutputOptions, item.OptiFgOutput);
                SelectOptiCombo(OptiFgMultiplierCombo, OptiScalerService.FgMultiplierOptions, item.OptiFgMultiplier);
                SelectOptiCombo(OptiFsr4PresetCombo, OptiScalerService.Fsr4PresetOptions, item.OptiFsr4Preset);
                _suppressFsr4FilesEvent = true;
                SelectOptiCombo(OptiFsr4FilesCombo, OptiScalerService.Fsr4FilesOptions, item.OptiFsr4Files);
                _suppressFsr4FilesEvent = false;
                UpdateOptiFsr4Row();  // 按"画质提升"的取值决定 FSR4 画质档是否展开
                OptiFrameGenToggle.IsOn = item.OptiFrameGen;
                UpdateOptiLocalRow(); // 版本渠道选"本地包"时展开文件夹提示
                // 版本列表：渠道/变体没变化时 SelectionChanged 不会触发，这里主动加载并按保存值回填
                _ = ReloadOptiVersionsAsync();
                var fsr4Variant = OptiComboValue(OptiFsr4FilesCombo);
                if (fsr4Variant is "int8" or "fp8")
                {
                    OptiFsr4VersionCell.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    _ = ReloadFsr4VersionsAsync(fsr4Variant);
                }
                else
                {
                    OptiFsr4VersionCell.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                OptiRestoreStatus.Text = OptiScalerService.HasBackup()
                    ? "选择上面下拉里的游戏，再按「一键还原」。" : "";
                RefreshOptiBackups();
            }
            // 手柄映射：标题、大小、颜色 + 可视化手柄图（点击手柄键 → 键盘/鼠标/手柄输出）
            var isGamepadMap = item.WidgetKind == WidgetKind.GamepadMap;
            GamepadMapRow.Visibility = isGamepadMap ? Microsoft.UI.Xaml.Visibility.Visible : w;
            // 组件标题输入框（独立一行，显示在类型/大小/颜色行的上方）仅手柄映射小组件显示
            TitleColumn.Visibility = isGamepadMap ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isGamepadMap)
            {
                // 手柄映射：拷入映射列表；每次进入编辑都从"未选中任何手柄键"开始
                _pendingGamepadMappings = item.GamepadMappings.Select(m => m.Clone()).ToList();
                _selectedMapSource = GamepadButtons.None;
                RefreshGamepadMapAll();
                // 标题走与大小/颜色并排的"组件标题"输入框，顶部名称框隐藏；留空则磁贴显示"手柄映射"
                NameBox.Visibility = w;
                WidgetTitleBox.Text = item.Name;
            }
            // 前台应用监测：名称不参与展示（磁贴固定显示"当前前台应用"的图标/进程名/状态）→ 隐藏顶部"显示名称"输入框
            // （NameBox.Text 仍保留原值，保存时若分支不改名称则不变；InitForEdit 每次会把它重置为 Visible，不会残留）
            if (item.WidgetKind == WidgetKind.Foreground)
            {
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "前台应用";
                NameBox.Visibility = w;
            }
            // 系统：名称不参与展示（磁贴按当前面板项显示"Win 设置/音量/亮度…"）→ 隐藏顶部"显示名称"输入框；
            // 大小固定"长"（SizeColumn 已隐藏），改为显示"勾选的项目"（勾选项才参与右摇杆切换）
            if (item.WidgetKind == WidgetKind.System)
            {
                if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = "系统";
                NameBox.Visibility = w;
                SystemOptionsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                CheckSystemOptions(SystemPanelService.NormalizeMask(item.SystemOptionMask));
            }
            if (isMapping)
            {
                _pendingPad = (int)item.MappingPad;
                _pendingKey = item.MappingKey;
                _pendingMods = item.MappingMods;
                _mappingCapturing = false;
                MappingText.Text = item.MappingLabel;
                // 标题与映射按键无关联，可单独填写；留空则磁贴显示映射按键名
                NameBox.Header = "标题";
                NameBox.PlaceholderText = "留空则显示映射按键名";
            }
            if (isPhoto)
            {
                PhotoFolderText.Text = string.IsNullOrEmpty(item.WidgetFolder)
                    ? "未选择" : item.WidgetFolder;
                SpeedFastRadio.IsChecked = item.SlideshowSpeed == PhotoSpeed.Fast;
                SpeedSlowRadio.IsChecked = item.SlideshowSpeed == PhotoSpeed.Slow;
                SpeedArtRadio.IsChecked = item.SlideshowSpeed == PhotoSpeed.Art;
                AnimationToggle.IsOn = item.PhotoAnimation;
            }
            // 磁贴抽屉：名称、颜色 + 抽屉列表（改名/增删）；大小固定"长"尺寸，抽屉里的磁贴在主页放入
            var isDrawer = item.WidgetKind == WidgetKind.Drawer;
            DrawerRow.Visibility = isDrawer ? Microsoft.UI.Xaml.Visibility.Visible : w;
            if (isDrawer)
            {
                _pendingDrawers = (item.Drawers ?? []).Select(d => d.Clone()).ToList();
                if (_pendingDrawers.Count == 0) _pendingDrawers.Add(new DrawerPage { Name = "抽屉1", Items = [] });
                _drawerCursor = 0;
                RefreshDrawerRow();
            }
        }
        else
        {
            TitleColumn.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            SystemOptionsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            SizeColumn.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            ColorColumn.Visibility = TileStyle.IsWin10Theme
                ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
            IconHeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            IconRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CoverHeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CoverRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PhotoFolderRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PhotoSettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            MappingRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            TrainerRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            BackupRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            GamepadMapRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            OptiScalerRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            MonitorRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            FileTransferRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _formIndex = 0;
        HintBar.SetHints("DPAD:选择|A:确认/切换|X:保存|B:返回");

        if (e.Parameter is LaunchItem item)
        {
            InitForEdit(item);
            PageTitle.Text = _isWidget
                ? (item.WidgetKind == WidgetKind.Photos ? "编辑图片组"
                    : item.WidgetKind == WidgetKind.Settings ? "编辑设置磁贴"
                    : item.WidgetKind == WidgetKind.Power ? "编辑电源磁贴"
                    : item.WidgetKind == WidgetKind.Weather ? "编辑天气小组件"
                    : item.WidgetKind == WidgetKind.Mapping ? "编辑映射小组件"
                    : item.WidgetKind == WidgetKind.Trainer ? "编辑修改器小组件"
                    : item.WidgetKind == WidgetKind.Backup ? "编辑游戏存档小组件"
                    : item.WidgetKind == WidgetKind.GamepadMap ? "编辑手柄映射小组件"
                    : item.WidgetKind == WidgetKind.OptiScaler ? "编辑 OptiScaler 小组件"
                    : item.WidgetKind == WidgetKind.RecycleBin ? "编辑回收站小组件"
                    : item.WidgetKind == WidgetKind.Foreground ? "编辑前台应用小组件"
                    : item.WidgetKind == WidgetKind.System ? "编辑系统小组件"
                    : item.WidgetKind == WidgetKind.Monitor ? "编辑性能监控小组件"
                    : item.WidgetKind == WidgetKind.FileTransfer ? "编辑文件传输小组件"
                    : item.WidgetKind == WidgetKind.Drawer ? "编辑磁贴抽屉" : "编辑音乐小组件")
                : _isGroup ? "编辑磁贴组" : "编辑快捷项";
        }
        else
        {
            // 添加模式：清空
            _editing = null;
            _isGroup = false;
            _isWidget = false;
            _pendingPhotoFolder = null;
            _pendingBackupFolder = null;
            _pendingGamepadMappings = null;
            _selectedMapSource = GamepadButtons.None;
            GamepadMapEditor.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            GamepadMapSummary.Text = "";
            PhotoFolderRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            PhotoSettingsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            MappingRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            TrainerRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            BackupRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            GamepadMapRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            OptiScalerRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            MonitorRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            FileTransferRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            DrawerRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _pendingDrawers = [];
            _drawerCursor = 0;
            TitleColumn.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            SystemOptionsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            WidgetTitleBox.Text = "";
            _pendingIcon = null;
            _pendingCover = null;
            PageTitle.Text = "添加快捷项";
            NameBox.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            NameBox.Header = "显示名称";
            NameBox.PlaceholderText = "例如：Steam";
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
        RebuildFormControls(); // 导航序列只含有效可见控件，并按页面视觉顺序排列
        _formIndex = 0;        // 最上面的第一个可聚焦控件（手柄映射编辑时=手柄图上的 LT 热区）
        FocusForm(_formIndex);
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
        // OptiScaler 下拉展开中：方向键在选项内移动，A 确认，B/Select 取消（不走表单导航）
        if (_openCombo != null) { HandleOpenCombo(b); return; }

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

    /// <summary>上下：按方向取最近控件</summary>
    private void MoveRow(int delta) => MoveDirection(0, delta);

    /// <summary>左右：按方向取最近控件</summary>
    private void MoveCol(int delta) => MoveDirection(delta, 0);

    /// <summary>
    /// 方向最近邻导航（实时取控件在页面中的实际矩形，滚动/缩放后依然准确）：
    /// 只在移动方向一侧的控件里，按"边缘推进距离 + 侧向错开量"取最近者。
    /// 不用中心点距离——新版面两列宽窄不一，中心距离会把旁边更宽的控件算远、跳过紧邻的小控件。
    /// 找不到候选时可向下落到保存按钮。
    /// </summary>
    private void MoveDirection(int dx, int dy)
    {
        if (_formControls.Length == 0) return;
        if (!TryRect(_formControls[_formIndex], out var cr)) { MoveForm(dx != 0 ? dx : dy); return; }

        var curX = cr.X + cr.Width / 2;
        var curY = cr.Y + cr.Height / 2;
        var horizontal = dx != 0;
        var sign = horizontal ? dx : dy;

        var best = -1;
        var bestScore = double.MaxValue;
        var bestPrimary = double.MaxValue;

        for (var i = 0; i < _formControls.Length; i++)
        {
            if (i == _formIndex) continue;
            if (ReferenceEquals(_formControls[i], SaveButton)) continue;
            if (!TryRect(_formControls[i], out var r)) continue; // 折叠/未布局控件自动跳过

            var cx = r.X + r.Width / 2;
            var cy = r.Y + r.Height / 2;

            // 中心必须在移动方向前方：同排/同行的控件（中心几乎对齐）不算"前方"
            var centerDelta = horizontal ? (cx - curX) * sign : (cy - curY) * sign;
            if (centerDelta < 1) continue;

            // 从当前控件边缘到候选近边的推进距离（<=0 表示紧贴或轻微重叠）
            var edge = horizontal
                ? (sign > 0 ? r.X - (cr.X + cr.Width) : cr.X - (r.X + r.Width))
                : (sign > 0 ? r.Y - (cr.Y + cr.Height) : cr.Y - (r.Y + r.Height));

            // 侧向重叠量：>0 表示正对（有交叠带），<=0 表示错开，按错开间隙惩罚
            var crossOverlap = horizontal
                ? Math.Min(cr.Y + cr.Height, r.Y + r.Height) - Math.Max(cr.Y, r.Y)
                : Math.Min(cr.X + cr.Width, r.X + r.Width) - Math.Max(cr.X, r.X);
            var crossGap = crossOverlap > 0 ? 0 : -crossOverlap;

            var score = Math.Max(0, edge) + crossGap * 3;
            if (score < bestScore - 0.001 ||
                (Math.Abs(score - bestScore) <= 0.001 && centerDelta < bestPrimary))
            {
                bestScore = score;
                bestPrimary = centerDelta;
                best = i;
            }
        }

        if (best >= 0) { _formIndex = best; FocusForm(best); return; }

        // 找不到候选：向下（含右下方）兜底到页脚保存按钮，其余方向不移动
        if (dy > 0)
        {
            var saveIdx = Array.IndexOf(_formControls, SaveButton);
            if (saveIdx >= 0 && saveIdx != _formIndex) { _formIndex = saveIdx; FocusForm(saveIdx); }
        }
    }

    private bool TryRect(Control c, out Windows.Foundation.Rect rect)
    {
        rect = default;
        if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return false;
        try
        {
            rect = c.TransformToVisual(FormPanel)
                     .TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
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

    /// <summary>按配置回填监控悬浮条的位置 / 大小 / 不透明度（旧配置无这些字段时走默认值）</summary>
    private void CheckMonitorSettings(MonitorPosition pos, double scale, double opacity)
    {
        MonPosTopLeft.IsChecked = pos == MonitorPosition.TopLeft;
        MonPosTopCenter.IsChecked = pos == MonitorPosition.TopCenter;
        MonPosTopRight.IsChecked = pos == MonitorPosition.TopRight;
        MonPosBottomLeft.IsChecked = pos == MonitorPosition.BottomLeft;
        MonPosBottomCenter.IsChecked = pos == MonitorPosition.BottomCenter;
        MonPosBottomRight.IsChecked = pos == MonitorPosition.BottomRight;

        MonSizeSmall.IsChecked = scale < 0.88;
        MonSizeLarge.IsChecked = scale > 1.15;
        MonSizeNormal.IsChecked = MonSizeSmall.IsChecked != true && MonSizeLarge.IsChecked != true;

        MonOpaque100.IsChecked = opacity >= 0.72;
        MonOpaque50.IsChecked = MonOpaque100.IsChecked != true && opacity >= 0.25;
        MonOpaque0.IsChecked = MonOpaque100.IsChecked != true && MonOpaque50.IsChecked != true;
    }

    private MonitorPosition SelectedMonitorPos()
    {
        if (MonPosTopLeft.IsChecked == true) return MonitorPosition.TopLeft;
        if (MonPosTopRight.IsChecked == true) return MonitorPosition.TopRight;
        if (MonPosBottomLeft.IsChecked == true) return MonitorPosition.BottomLeft;
        if (MonPosBottomCenter.IsChecked == true) return MonitorPosition.BottomCenter;
        if (MonPosBottomRight.IsChecked == true) return MonitorPosition.BottomRight;
        return MonitorPosition.TopCenter;
    }

    private double SelectedMonitorScale() =>
        MonSizeSmall.IsChecked == true ? 0.75 :
        MonSizeLarge.IsChecked == true ? 1.3 : 1.0;

    private double SelectedMonitorOpacity() =>
        MonOpaque100.IsChecked == true ? 1.0 :
        MonOpaque50.IsChecked == true ? 0.5 : 0.0;

    private PhotoSpeed SelectedSpeed()
    {
        if (SpeedSlowRadio.IsChecked == true) return PhotoSpeed.Slow;
        if (SpeedArtRadio.IsChecked == true) return PhotoSpeed.Art;
        return PhotoSpeed.Fast;
    }

    /// <summary>游戏存档：当前选中的保留份数</summary>
    private int SelectedBackupKeep()
    {
        if (Keep3Radio.IsChecked == true) return 3;
        if (Keep10Radio.IsChecked == true) return 10;
        return 5;
    }

    /// <summary>OptiScaler：A 键展开下拉框（记下当前索引，供 B 取消时回滚）</summary>
    private void OpenCombo(ComboBox cb)
    {
        _comboBackupIndex = cb.SelectedIndex;
        _openCombo = cb;
        cb.IsDropDownOpen = true;
    }

    /// <summary>下拉框展开中的按键：上/下移动选项（即时预览选中项），A 确认收起，B/Select 回滚并收起。
    /// 下拉列表由 WinUI 自己渲染，这里只改 SelectedIndex。</summary>
    private void HandleOpenCombo(GamepadButtons b)
    {
        var cb = _openCombo!;
        var last = cb.Items.Count - 1;
        if (b.HasFlag(GamepadButtons.DPadUp))
            cb.SelectedIndex = Math.Max(0, cb.SelectedIndex - 1);
        else if (b.HasFlag(GamepadButtons.DPadDown))
            cb.SelectedIndex = Math.Min(last, cb.SelectedIndex + 1);
        else if (b.HasFlag(GamepadButtons.A))
            CloseCombo(false);
        else if (b.HasFlag(GamepadButtons.B) || b.HasFlag(GamepadButtons.View))
            CloseCombo(true);
    }

    /// <summary>收起下拉框；rollback=true 时把选中项退回展开前的值（B 取消）</summary>
    private void CloseCombo(bool rollback)
    {
        if (_openCombo is not { } cb) return;
        if (rollback) cb.SelectedIndex = _comboBackupIndex;
        cb.IsDropDownOpen = false;
        _openCombo = null;
    }

    /// <summary>OptiScaler：当前选中的注入 DLL 名（null=自动，按 wiki 建议/默认 dxgi.dll）</summary>
    private string? SelectedOptiDll()
    {
        var v = OptiComboValue(OptiDllCombo);
        return v.Length == 0 ? null : v;
    }

    /// <summary>OptiScaler：当前选中的 Dx12Upscaler（写入 OptiScaler.ini）</summary>
    private string SelectedOptiUpscaler()
    {
        var v = OptiComboValue(OptiUpscalerCombo);
        return v.Length == 0 ? "auto" : v;
    }

    private void OnOptiFrameGenToggled(object sender, RoutedEventArgs e) => UpdateOptiFgRows();

    private void OnOptiUpscalerChanged(object sender, SelectionChangedEventArgs e) => UpdateOptiFsr4Row();

    /// <summary>
    /// 只有"画质提升"选到 FSR4 时才需要 FSR4 画质档（Fsr4Preset）→ 其余取值一律收起该子项，
    /// 避免无关选项占位；同时重建导航序列。
    /// </summary>
    private void UpdateOptiFsr4Row()
    {
        var show = OptiComboValue(OptiUpscalerCombo) == "fsr4";
        OptiFsr4Cell.Visibility = show
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        RebuildFormControls();
    }

    /// <summary>
    /// 帧生成总闸关闭时输入/输出/倍数不生效 → 三格（小标题 + 下拉框）整体收起，
    /// 行高与间距一并归零，后面的"本地包"随之上移；同时重建导航序列，避免手柄光标走到已隐藏的控件上。
    /// </summary>
    private void UpdateOptiFgRows()
    {
        var vis = OptiFrameGenToggle.IsOn
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        OptiFgMultiplierCell.Visibility = vis;
        OptiFgInputCell.Visibility = vis;
        OptiFgOutputCell.Visibility = vis;
        RebuildFormControls();
    }

    private void ActivateForm()
    {
        switch (_formControls[_formIndex])
        {
            case TextBox tb:
                tb.Focus(Microsoft.UI.Xaml.FocusState.Keyboard); // 弹出输入法后可物理键盘输入
                break;
            // OptiScaler 下拉框：A 键展开选项列表（展开后由 HandleOpenCombo 接管按键）
            case ComboBox cb:
                OpenCombo(cb);
                break;
            // 手柄映射：输出类型三选一（键盘/鼠标/手柄）——需先于通用 RadioButton 分支
            case RadioButton rb0 when ReferenceEquals(rb0, GamepadMapKeyRadio)
                || ReferenceEquals(rb0, GamepadMapMouseRadio)
                || ReferenceEquals(rb0, GamepadMapPadRadio):
                ApplyGamepadMapKind(rb0);
                break;
            // 手柄映射：手柄图上的按键热区（A 键即选中该键并展开其编辑面板）
            case Button btn0 when _mapZones.Any(z => ReferenceEquals(z.Hit, btn0)):
                SelectGamepadMapSource(_mapZones.First(z => ReferenceEquals(z.Hit, btn0)).Button);
                break;
            case RadioButton rb:
                rb.IsChecked = true;
                break;
            // 系统小组件：显示项目胶囊（A = 勾选/取消勾选）
            case ToggleButton tb:
                tb.IsChecked = !(tb.IsChecked ?? false);
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
            case Button btn when ReferenceEquals(btn, MappingCaptureButton):
                StartMappingCapture();
                break;
            // 手柄映射：捕获/切换选中键的输出目标
            case Button btn when ReferenceEquals(btn, GamepadMapCaptureButton):
                DoGamepadMapCapture();
                break;
            // 手柄映射：删除选中键的映射
            case Button btn when ReferenceEquals(btn, GamepadMapDeleteButton):
                DoGamepadMapDelete();
                break;
            case Button btn when ReferenceEquals(btn, OpenTrainerFolderButton):
                // 修改器：A 键打开已下载修改器目录
                TrainerService.OpenTrainersFolder();
                break;
            // OptiScaler：打开本地包文件夹 / 一键还原 / 打开数据目录
            case Button btn when ReferenceEquals(btn, OptiLocalFolderButton):
                OptiScalerService.OpenLocalFolder();
                break;
            case Button btn when ReferenceEquals(btn, OptiRestoreButton):
                RestoreSelectedOptiBackup();
                break;
            case Button btn when ReferenceEquals(btn, OptiOpenFolderButton):
                OptiScalerService.OpenFolder();
                break;
            // 文件传输：选择发送目录 / 接收目录
            case Button btn when ReferenceEquals(btn, FtSendFolderButton):
                _pickingFtSendFolder = true;
                EnterBrowse(BrowseMode.Folder);
                break;
            case Button btn when ReferenceEquals(btn, FtRecvFolderButton):
                _pickingFtRecvFolder = true;
                EnterBrowse(BrowseMode.Folder);
                break;
            // 磁贴抽屉：上一个 / 下一个 / 新增 / 删除
            case Button btn when ReferenceEquals(btn, DrawerPrevButton):
                MoveDrawerCursor(-1);
                break;
            case Button btn when ReferenceEquals(btn, DrawerNextButton):
                MoveDrawerCursor(1);
                break;
            case Button btn when ReferenceEquals(btn, DrawerAddButton):
                OnDrawerAddClick(btn, new RoutedEventArgs());
                break;
            case Button btn when ReferenceEquals(btn, DrawerDeleteButton):
                OnDrawerDeleteClick(btn, new RoutedEventArgs());
                break;
            case Button btn when ReferenceEquals(btn, BackupFolderButton):
                // 游戏存档：进入文件夹浏览，结果写入 _pendingBackupFolder（不改 TargetBox/类型）
                _pickingBackupFolder = true;
                EnterBrowse(BrowseMode.Folder);
                break;
            case Button btn when ReferenceEquals(btn, OpenBackupFolderButton):
                // 游戏存档：A 键打开当前备份目标目录
                OpenBackupFolder();
                break;
            case Button btn when ReferenceEquals(btn, CloudSettingsButton):
                // 游戏存档：A 键跳到 设置 → 云端备份 配置 WebDAV
                App.Main?.NavigateSettings(typeof(CloudPage));
                break;
            case Button btn when ReferenceEquals(btn, SaveButton):
                _ = SaveAsync();
                break;
        }
    }

    private void FocusForm(int i)
    {
        var c = _formControls[i];
        // 手柄图热区（Button）的"光标"用热区自身描边表达：默认焦点视觉在 BorderThickness=0 的
        // 透明按钮上看不见，必须显式画一圈白框
        UpdateZoneCursor(_mapZones.FirstOrDefault(z => ReferenceEquals(z.Hit, c))?.Hit);
        UpdateSysChipCursor(c is ToggleButton tb && !(c is RadioButton) ? tb : null);
        if (c is RadioButton rb)
        {
            // 单选按钮：移动只移动光标（高亮），不聚焦——WinUI 键盘聚焦会自动勾选，
            // 导致"移动即变更"；按 A 才真正切换选中。
            // 注意：RadioButton 继承自 ToggleButton，必须先判断 RadioButton，否则会被当成胶囊处理
            UpdateRadioCursor(rb);
            UpdateToggleCursor(null);
            UpdateComboCursor(null);
            EnsureVisible(c);
            return;
        }
        if (c is ToggleButton chip)
        {
            // 胶囊：只移动光标高亮，不聚焦——与单选按钮同理，避免 XAML 焦点机制干扰 A 键的勾选/取消
            UpdateRadioCursor(null);
            UpdateToggleCursor(null);
            UpdateComboCursor(null);
            EnsureVisible(chip);
            return;
        }
        if (c is ToggleSwitch ts)
        {
            // 开关同理：移动只高亮，按 A 才切换
            UpdateRadioCursor(null);
            UpdateToggleCursor(ts);
            UpdateComboCursor(null);
            EnsureVisible(c);
            return;
        }
        if (c is ComboBox combo)
        {
            // 下拉框同理：不聚焦（否则键盘上下键会直接改选中项），只高亮，按 A 才展开
            UpdateRadioCursor(null);
            UpdateToggleCursor(null);
            UpdateComboCursor(combo);
            EnsureVisible(c);
            return;
        }
        UpdateRadioCursor(null);
        UpdateToggleCursor(null);
        UpdateComboCursor(null);
        c.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        EnsureVisible(c); // 窄窗口下焦点控件自动滚入可视区
    }

    /// <summary>高亮光标所在单选按钮（白色 0xCC 描边，与全局卡片式样式一致）。
    /// 非光标控件清除本地 BorderBrush，让样式自带的默认描边生效（不能用 Transparent 硬覆盖）</summary>
    private void UpdateRadioCursor(RadioButton? cursor)
    {
        foreach (var ctl in _formControls)
        {
            if (ctl is not RadioButton r) continue;
            if (ReferenceEquals(r, cursor)) r.BorderBrush = CursorBrush;
            else r.ClearValue(RadioButton.BorderBrushProperty);
        }
    }

    /// <summary>高亮光标所在开关（描边）</summary>
    private void UpdateToggleCursor(ToggleSwitch? cursor)
    {
        foreach (var ctl in _formControls)
        {
            if (ctl is not ToggleSwitch t) continue;
            if (ReferenceEquals(t, cursor)) t.BorderBrush = CursorBrush;
            else t.ClearValue(ToggleSwitch.BorderBrushProperty);
        }
    }

    /// <summary>高亮光标所在下拉框（描边，与单选/开关的光标语义一致）</summary>
    private void UpdateComboCursor(ComboBox? cursor)
    {
        foreach (var ctl in _formControls)
        {
            if (ctl is not ComboBox c) continue;
            if (ReferenceEquals(c, cursor)) c.BorderBrush = CursorBrush;
            else c.ClearValue(ComboBox.BorderBrushProperty);
        }
    }

    /// <summary>高亮光标所在的"显示项目"胶囊（白色描边走 CursorRing，与选中态/焦点态互不覆盖）</summary>
    private void UpdateSysChipCursor(ToggleButton? cursor)
    {
        foreach (var b in _sysOptBoxes)
        {
            if (ReferenceEquals(b, cursor)) b.BorderBrush = CursorBrush;
            else b.ClearValue(ToggleButton.BorderBrushProperty);
        }
    }

    /// <summary>高亮光标所在的手柄图热区（白框），其余热区收起描边</summary>
    private void UpdateZoneCursor(Button? cursor)
    {
        foreach (var z in _mapZones)
        {
            var on = ReferenceEquals(z.Hit, cursor);
            z.Hit.BorderBrush = on ? CursorBrush : null;
            z.Hit.BorderThickness = new Thickness(on ? 2 : 0);
        }
    }

    /// <summary>把控件滚动到可视区，并保留上下文：上方留 44px 余量（保证分组标题/说明不在视野外），
    /// 下方留 8px；光标在首个控件时整页滚回顶部、在页脚保存按钮时滚到底（与外观页同款行为），
    /// 目标偏移钳在可滚动范围并关闭滚动动画——方向键连发时不会丢滚动请求。</summary>
    private void EnsureVisible(Control c)
    {
        try
        {
            // 首个/末个控件：直接滚到顶/底，避免"往下走了再往上回不到上方"
            if (_formIndex == 0) { FormScroll.ChangeView(null, 0, null, true); return; }
            if (_formIndex == _formControls.Length - 1)
            {
                // 末位恒为页脚固定的保存按钮（不在滚动区内）：此时把表单滚到底
                FormScroll.ChangeView(null, FormScroll.ScrollableHeight, null, true);
                return;
            }
            var p = c.TransformToVisual(FormScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = FormScroll.ViewportHeight;
            const double topMargin = 44;
            double? target;
            if (p.Y < topMargin) target = FormScroll.VerticalOffset + p.Y - topMargin;
            else if (p.Y + c.ActualHeight > vh - 8) target = FormScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8;
            else return;
            FormScroll.ChangeView(null, Math.Clamp(target.Value, 0, FormScroll.ScrollableHeight), null, true);
        }
        catch { }
    }

    private void Cancel()
    {
        // 图标文件按目标路径哈希命名，可能被多个快捷项共用，取消时不删除（孤立文件无害）
        if (App.Main is { } main) main.PendingDrawerInsert = null;   // 放弃本次新建：连带放弃"放进抽屉"的一次性请求
        App.Main?.ShowMainPage();
    }

    // ---- 磁贴抽屉：抽屉列表的切换 / 改名 / 增删（内容在主页放入）----

    /// <summary>把当前抽屉的名称与状态写进抽屉行（切换/增删后调用）</summary>
    private void RefreshDrawerRow()
    {
        if (_pendingDrawers.Count == 0)
        {
            _drawerCursor = 0;
            DrawerNameBox.Text = "";
            DrawerStatusText.Text = "暂无抽屉，点「新增抽屉」新建";
            return;
        }
        _drawerCursor = Math.Clamp(_drawerCursor, 0, _pendingDrawers.Count - 1);
        var page = _pendingDrawers[_drawerCursor];
        DrawerNameBox.Text = page.Name;
        DrawerStatusText.Text = $"第 {_drawerCursor + 1} / {_pendingDrawers.Count} 个 · 已放 {page.Items.Count} 个磁贴";
    }

    private void OnDrawerNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_drawerCursor < 0 || _drawerCursor >= _pendingDrawers.Count) return;
        _pendingDrawers[_drawerCursor].Name = DrawerNameBox.Text;
    }

    private void OnDrawerPrevClick(object sender, RoutedEventArgs e) => MoveDrawerCursor(-1);
    private void OnDrawerNextClick(object sender, RoutedEventArgs e) => MoveDrawerCursor(1);

    private void MoveDrawerCursor(int delta)
    {
        if (_pendingDrawers.Count == 0) return;
        _drawerCursor = (_drawerCursor + delta + _pendingDrawers.Count) % _pendingDrawers.Count;
        RefreshDrawerRow();
    }

    private void OnDrawerAddClick(object sender, RoutedEventArgs e)
    {
        _pendingDrawers.Add(new DrawerPage { Name = DrawerService.NextName(_pendingDrawers), Items = [] });
        _drawerCursor = _pendingDrawers.Count - 1;
        RefreshDrawerRow();
    }

    private void OnDrawerDeleteClick(object sender, RoutedEventArgs e)
    {
        // 至少保留一个抽屉：全删光会让组件没有可显示的内容
        if (_pendingDrawers.Count <= 1) { DrawerStatusText.Text = "至少保留一个抽屉"; return; }
        _pendingDrawers.RemoveAt(_drawerCursor);
        if (_drawerCursor >= _pendingDrawers.Count) _drawerCursor = _pendingDrawers.Count - 1;
        RefreshDrawerRow();
    }

    private async Task SaveAsync()
    {
        // 手柄映射小组件的标题走与大小/颜色并排的"组件标题"框，其余走顶部名称框
        var name = (_isWidget && _editing?.WidgetKind == WidgetKind.GamepadMap
            ? WidgetTitleBox.Text : NameBox.Text).Trim();
        var target = TargetBox.Text.Trim();
        // 映射小组件、手柄映射小组件标题可与按键无关且可留空（留空即显示按键名/"手柄映射"），不作必填校验
        var nameRequired = !(_isWidget && _editing?.WidgetKind is WidgetKind.Mapping or WidgetKind.GamepadMap);
        if ((nameRequired && string.IsNullOrWhiteSpace(name))
            || (!_isGroup && !_isWidget && string.IsNullOrWhiteSpace(target)))
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
            // 定位原项（按 Target + 名称，组只匹配组）；抽屉内层磁贴不在顶层列表，
            // 必须连同抽屉一起找，否则会报"未找到原快捷项"而改不动抽屉里的磁贴
            var it = DrawerService.FindAnywhere(items, x => x.Target == _origTarget && x.Name == _origName)
                  ?? DrawerService.FindAnywhere(items, x => x.Target == _origTarget && (!_isGroup || x.Kind == LaunchKind.Group));
            if (it is null) { PageTitle.Text = "未找到原快捷项"; return; }

            it.Name = name;
            // 小组件：音乐改名称与颜色；设置改名称、大小与颜色；图片组改名称、大小、文件夹与轮播设置（无颜色）
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
                else if (it.WidgetKind is WidgetKind.Settings or WidgetKind.Power)
                {
                    // 设置 / 电源：常驻磁贴，可改名称、大小与颜色（不可删除）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                }
                else if (it.WidgetKind == WidgetKind.Weather)
                {
                    // 天气：改名称、大小与颜色（数据由服务刷新，无其他配置）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                }
                else if (it.WidgetKind == WidgetKind.Monitor)
                {
                    // 性能监控：改名称、大小、颜色，以及悬浮条位置 / 大小 / 不透明度
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    it.MonitorPos = SelectedMonitorPos();
                    it.MonitorScale = SelectedMonitorScale();
                    it.MonitorOpacity = SelectedMonitorOpacity();
                }
                else if (it.WidgetKind == WidgetKind.FileTransfer)
                {
                    // 文件传输：改名称、大小、颜色，以及可发送目录 / 接收目录（空=系统下载文件夹）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    if (_pendingFtSendFolder != null)
                        it.FileTransferSendFolder = _pendingFtSendFolder.Length == 0 ? null : _pendingFtSendFolder;
                    if (_pendingFtRecvFolder != null)
                        it.FileTransferRecvFolder = _pendingFtRecvFolder.Length == 0 ? null : _pendingFtRecvFolder;
                }
                else if (it.WidgetKind == WidgetKind.Mapping)
                {
                    // 映射：改标题、大小、颜色与映射按键（手柄键或键盘键二选一）；
                    // 标题与按键无关联——标题留空时磁贴改为显示按键名（it.Name 已由通用逻辑写入）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    it.MappingPad = (GamepadButtons)_pendingPad;
                    it.MappingKey = _pendingKey;
                    it.MappingMods = _pendingMods;
                }
                else if (it.WidgetKind == WidgetKind.Trainer)
                {
                    // 修改器：改名称、大小与颜色（取哪个修改器由当前游戏进程决定，无其他配置）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                }
                else if (it.WidgetKind == WidgetKind.GamepadMap)
                {
                    // 手柄映射：改名称、大小、颜色与映射列表（源手柄键 → 键盘/鼠标/手柄输出）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    // 只保存已设好目标的条目（选中过按键但没设目标的不写入）
                    it.GamepadMappings = (_pendingGamepadMappings ?? [])
                        .Where(HasTarget).Select(m => m.Clone()).ToList();
                    // 若该磁贴的映射正开启，刷新登记引用使编辑立即生效（开关状态保持不变）
                    GamepadRemapService.Refresh(it);
                }
                else if (it.WidgetKind == WidgetKind.Backup)
                {
                    // 游戏存档：改名称、大小、颜色、备份方式与保留份数；云端直传不使用本地备份文件夹
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    it.BackupMode = BackupCloudRadio.IsChecked == true ? BackupMode.Cloud : BackupMode.Local;
                    if (it.BackupMode == BackupMode.Local)
                    {
                        if (_pendingBackupFolder != null)
                            it.BackupFolder = _pendingBackupFolder.Length == 0 ? null : _pendingBackupFolder;
                    }
                    else
                    {
                        it.BackupFolder = null;
                    }
                    it.BackupKeep = SelectedBackupKeep();
                }
                else if (it.WidgetKind == WidgetKind.OptiScaler)
                {
                    // OptiScaler：改名称、大小、颜色、注入 DLL 名、版本与 ini 关键项（空=自动/最新版）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                    it.OptiDllName = SelectedOptiDll();
                    it.OptiChannel = OptiComboValue(OptiVersionCombo);
                    // 本地包不联网，不存在"具体版本"
                    it.OptiVersionTag = it.OptiChannel == "local" ? "" : OptiComboValue(OptiVersionTagCombo);
                    it.OptiDx12Upscaler = SelectedOptiUpscaler();
                    it.OptiFrameGen = OptiFrameGenToggle.IsOn;
                    it.OptiFgInput = OptiComboValue(OptiFgInputCombo);
                    it.OptiFgOutput = OptiComboValue(OptiFgOutputCombo);
                    it.OptiFgMultiplier = OptiComboValue(OptiFgMultiplierCombo);
                    it.OptiFsr4Preset = OptiComboValue(OptiFsr4PresetCombo);
                    it.OptiFsr4Files = OptiComboValue(OptiFsr4FilesCombo);
                    it.OptiFsr4VersionTag = it.OptiFsr4Files is "int8" or "fp8"
                        ? OptiComboValue(OptiFsr4VersionCombo) : "";
                }
                else if (it.WidgetKind == WidgetKind.RecycleBin)
                {
                    // 回收站：改名称、大小与颜色（A 打开回收站、Y 清空，无其他配置）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                }
                else if (it.WidgetKind == WidgetKind.Foreground)
                {
                    // 前台应用监测：改大小与颜色（磁贴内容为"当前前台应用"，A 冻结/解冻、Y 结束，无其他配置）
                    it.Size = SelectedSize();
                    it.TileColor = SelectedColor();
                }
                else if (it.WidgetKind == WidgetKind.System)
                {
                    // 系统：大小固定"长"；可改颜色与"显示的项目"（勾选的才参与右摇杆切换）
                    var mask = SelectedSystemMask();
                    if (mask == 0)
                    {
                        PageTitle.Text = "系统小组件至少要勾选一个项目";
                        return;
                    }
                    it.Size = TileSize.Wide;
                    it.TileColor = SelectedColor();
                    it.SystemOptionMask = mask;
                    // 当前停留的项被去掉了：回落到第一个勾选项，避免磁贴停在不可切换的项上
                    it.SystemOption = SystemPanelService.NormalizeOption(mask, it.SystemOption);
                }
                else if (it.WidgetKind == WidgetKind.Drawer)
                {
                    // 磁贴抽屉：改颜色（抽屉药丸的强调色）与抽屉列表（名称/增删）；
                    // 大小固定"长"尺寸，抽屉里的磁贴在主页放入（此处不改）
                    if (_pendingDrawers.Any(d => string.IsNullOrWhiteSpace(d.Name)))
                    {
                        PageTitle.Text = "抽屉名称不能为空";
                        return;
                    }
                    it.TileColor = SelectedColor();
                    DrawerService.ApplyEdits(it, _pendingDrawers);
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
                PageIndex = App.Main?.CurrentHomePage ?? 0,   // 新建磁贴归到主页当前所在页
                Size = SelectedSize(),
                TileColor = SelectedColor(),
                IconFile = _pendingIcon,
                CoverFile = string.IsNullOrEmpty(_pendingCover) ? null : _pendingCover
            };
            if (kind != LaunchKind.Url && it.IconFile == null)
                it.IconFile = await IconExtractService.ExtractAsync(target);
            // 从主页抽屉内部按 X 发起的新建：直接放进该抽屉（放不下则报错且不落盘）
            var req = App.Main?.PendingDrawerInsert;
            if (req is not null)
            {
                App.Main!.PendingDrawerInsert = null;   // 一次性请求，消费即清
                if (!DrawerService.TryInsert(items, req, it, out var error))
                {
                    PageTitle.Text = error ?? "抽屉放不下这个磁贴";
                    return;
                }
            }
            else
            {
                items.Add(it);
            }
        }

        ConfigService.Save(items);
        MonitorService.ApplySettings(); // 监控正在运行时：位置/大小/透明度立即生效
        App.Main?.ShowMainPage();
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

    // 文件传输：可发送目录 / 接收目录
    private void OnFtSendFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _pickingFtSendFolder = true;
        EnterBrowse(BrowseMode.Folder);
    }

    private void OnFtRecvFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _pickingFtRecvFolder = true;
        EnterBrowse(BrowseMode.Folder);
    }
    private void OnCoverClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => EnterBrowse(BrowseMode.Cover);
    private void OnCoverClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearCover();
    // 修改器编辑：「已下载修改器」一键跳转到存放修改器的目录
    private void OnOpenTrainerFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => TrainerService.OpenTrainersFolder();
    // 游戏存档编辑：「备份文件夹」进入文件夹浏览 / 「打开备份目录」打开当前目标目录
    private void OnBackupFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _pickingBackupFolder = true;
        EnterBrowse(BrowseMode.Folder);
    }
    private void OnOpenBackupFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => OpenBackupFolder();

    // OptiScaler 编辑：版本渠道（正式版/测试版/本地包，本地包展开文件夹）+ 具体版本浏览下载 /
    // FSR4 版本浏览下载 / 一键还原 / 打开数据目录
    private void OnOptiVersionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (OptiLocalRow is null || OptiVersionTagCombo is null) return; // XAML 解析阶段
        UpdateOptiLocalRow();
        if (_suppressOptiVersionEvent) return;
        _ = ReloadOptiVersionsAsync();
    }

    /// <summary>版本渠道选到"本地包"时展开本地包文件夹行（紧跟版本下拉下方），并重建手柄导航序列</summary>
    private void UpdateOptiLocalRow()
    {
        var isLocal = string.Equals(OptiComboValue(OptiVersionCombo), "local", StringComparison.OrdinalIgnoreCase);
        OptiLocalRow.Visibility = isLocal ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        // 本地包没有"网上版本"可选 → 收起"具体版本"
        OptiVersionTagCell.Visibility = isLocal ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        if (isLocal) OptiLocalFolderText.Text = OptiScalerService.LocalPackageFolder;
        RebuildFormControls();
    }

    /// <summary>按当前渠道拉取 GitHub 上可下载的版本列表灌进"具体版本"下拉；失败只保留"最新版"</summary>
    private async Task ReloadOptiVersionsAsync()
    {
        if (OptiVersionCombo is null || OptiVersionTagCombo is null) return;
        var channel = OptiComboValue(OptiVersionCombo);
        var want = _savedOptiTag;   // 仅编辑初始化时非空
        _savedOptiTag = "";
        var gen = ++_optiVersionGen;

        // 当前页不是 OptiScaler 小组件：不联网，避免每开一次编辑页都打 GitHub API
        if (OptiScalerRow.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;

        if (channel == "local")
        {
            _suppressOptiTagEvent = true;
            OptiVersionTagCombo.Items.Clear();
            _suppressOptiTagEvent = false;
            OptiVersionStatus.Text = "本地包不联网，直接使用文件夹里的包";
            return;
        }

        OptiVersionStatus.Text = "正在获取可下载版本…";
        var options = await OptiScalerService.GetVersionOptionsAsync(channel);
        if (gen != _optiVersionGen) return;   // 已切换渠道，丢弃过期结果

        if (options.Length == 0) options = [("", "最新版（自动）")];
        _suppressOptiTagEvent = true;
        FillOptiCombo(OptiVersionTagCombo, options);
        SelectOptiCombo(OptiVersionTagCombo, options, want);
        _suppressOptiTagEvent = false;

        OptiVersionStatus.Text = options.Length <= 1
            ? "无法获取版本列表（请检查网络；注入时仍会尝试最新版）"
            : $"共 {options.Length - 1} 个可下载版本，选中后自动下载";
    }

    /// <summary>用户选定"具体版本"：后台预下载，注入时直接用缓存</summary>
    private async void OnOptiVersionTagChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOptiTagEvent || OptiVersionStatus is null) return;
        var channel = OptiComboValue(OptiVersionCombo);
        var tag = OptiComboValue(OptiVersionTagCombo);
        await PrefetchAsync(channel, tag, OptiVersionStatus);
    }

    /// <summary>FSR4 文件变体切换：int8/fp8 才需要选版本，并拉取该仓库可下载的 Release</summary>
    private async void OnOptiFsr4FilesChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (OptiFsr4VersionCell is null) return;
        var variant = OptiComboValue(OptiFsr4FilesCombo);
        var show = variant is "int8" or "fp8";
        OptiFsr4VersionCell.Visibility = show
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        RebuildFormControls();
        if (!show || _suppressFsr4FilesEvent) return;
        await ReloadFsr4VersionsAsync(variant);
    }

    /// <summary>按 FSR4 变体拉取可下载版本列表灌进"FSR4 版本"下拉</summary>
    private async Task ReloadFsr4VersionsAsync(string variant)
    {
        var want = _savedFsr4Tag;
        _savedFsr4Tag = "";
        var gen = ++_fsr4VersionGen;

        OptiFsr4VersionStatus.Text = "正在获取可下载版本…";
        var options = await OptiScalerService.GetFsr4VersionOptionsAsync(variant);
        if (gen != _fsr4VersionGen) return;

        if (options.Length == 0) options = [("", "最新版（自动）")];
        _suppressOptiTagEvent = true;
        FillOptiCombo(OptiFsr4VersionCombo, options);
        SelectOptiCombo(OptiFsr4VersionCombo, options, want);
        _suppressOptiTagEvent = false;

        OptiFsr4VersionStatus.Text = options.Length <= 1
            ? "无法获取版本列表（请检查网络；注入时仍会尝试最新版）"
            : $"共 {options.Length - 1} 个可下载版本，选中后自动下载";
    }

    /// <summary>用户选定 FSR4 版本：后台预下载</summary>
    private async void OnOptiFsr4VersionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOptiTagEvent || OptiFsr4VersionStatus is null) return;
        var variant = OptiComboValue(OptiFsr4FilesCombo);
        if (variant is not ("int8" or "fp8")) return;
        var tag = OptiComboValue(OptiFsr4VersionCombo);
        await PrefetchAsync(variant, tag, OptiFsr4VersionStatus);
    }

    /// <summary>后台预下载选中版本，并把服务层的下载进度写到状态文本上（channel=stable/beta，variant=int8/fp8）</summary>
    private async Task PrefetchAsync(string channel, string tag, TextBlock status)
    {
        var what = tag.Length == 0 ? "最新版" : tag;
        status.Text = $"正在后台下载 {what}…";
        var failed = false;
        void Handler(OptiScalerMessage m)
        {
            if (m.Error) failed = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrEmpty(m.Text)) status.Text = m.Text;
            });
        }

        OptiScalerService.StatusChanged += Handler;
        try
        {
            if (channel is "int8" or "fp8")
                await OptiScalerService.PrefetchFsr4Async(channel, tag);
            else
                await OptiScalerService.PrefetchAsync(channel, tag);
        }
        finally
        {
            OptiScalerService.StatusChanged -= Handler;
        }
        status.Text = failed ? "下载失败（注入时会再试一次）" : $"{what} 已就绪";
    }

    private void OnOptiLocalFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => OptiScalerService.OpenLocalFolder();

    private void OnOptiRestoreClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => RestoreSelectedOptiBackup();

    /// <summary>还原下拉里选中的那个游戏（不是"上一次注入的游戏"），完成后刷新备份列表</summary>
    private void RestoreSelectedOptiBackup()
    {
        if (OptiRestoreCombo.Items.Count == 0)
        {
            OptiRestoreStatus.Text = "没有可还原的备份";
            return;
        }
        var key = OptiComboValue(OptiRestoreCombo);
        var result = OptiScalerService.Restore(key);
        RefreshOptiBackups();
        // 还原成功后该备份目录会被删除；Refresh 在无备份时会写"暂无备份"，这里以还原结果为准
        OptiRestoreStatus.Text = OptiRestoreCombo.Items.Count == 0
            ? $"{result}（没有其他备份了）" : result;
    }

    /// <summary>刷新"已备份的游戏"下拉：按时间倒序，优先保持当前选中；没有备份时禁用下拉并给提示</summary>
    private void RefreshOptiBackups()
    {
        var options = OptiScalerService.GetBackupOptions();
        var prev = OptiComboValue(OptiRestoreCombo);
        FillOptiCombo(OptiRestoreCombo, options);
        if (options.Length > 0)
        {
            var want = options.Any(o => string.Equals(o.Value, prev, StringComparison.OrdinalIgnoreCase))
                ? prev
                : OptiScalerService.DefaultBackupKey();
            SelectOptiCombo(OptiRestoreCombo, options, want);
        }
        OptiRestoreCombo.IsEnabled = options.Length > 0;
        if (options.Length == 0) OptiRestoreStatus.Text = "暂无备份：注入一次 OptiScaler 后会自动备份被覆盖的文件";
        RebuildFormControls();
    }

    private void OnOptiOpenFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => OptiScalerService.OpenFolder();

    // 备份方式切换：本地面板 / 云端面板互换（云端面板只展示 WebDAV 配置状态）
    private void OnBackupModeChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (BackupCloudPanel is null) return; // XAML 解析阶段（IsChecked="True" 触发）此时控件尚未创建
        UpdateBackupPanels();
        RebuildFormControls();
    }

    /// <summary>按当前备份方式切换面板，并刷新云端（WebDAV）配置状态</summary>
    private void UpdateBackupPanels()
    {
        var cloud = BackupCloudRadio.IsChecked == true;
        var visible = Microsoft.UI.Xaml.Visibility.Visible;
        var collapsed = Microsoft.UI.Xaml.Visibility.Collapsed;
        BackupLocalPanel.Visibility = cloud ? collapsed : visible;
        BackupCloudPanel.Visibility = cloud ? visible : collapsed;

        var acct = WebDavService.CurrentAccount();
        CloudStateText.Text = acct is null
            ? "未配置（无法云端备份）"
            : $"已配置：{acct.Value.User} → {SettingsService.Current.WebDavRootFolder}";
    }

    // 游戏存档编辑：「打开云端备份设置」跳到 设置 → 云端备份
    private void OnCloudSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.NavigateSettings(typeof(CloudPage));

    /// <summary>打开当前编辑中的备份目标目录（按未保存的方式/文件夹选择计算，不写回配置）</summary>
    private void OpenBackupFolder()
    {
        // 云端直传没有本地目标目录：直接不打开
        if (BackupCloudRadio.IsChecked == true) return;

        var tmp = new LaunchItem
        {
            BackupMode = BackupMode.Local,
            BackupFolder = _pendingBackupFolder ?? _editing?.BackupFolder
        };
        SaveBackupService.OpenBackupsFolder(tmp);
    }
    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Cancel();

    // ---- 按键捕获（键盘在页面 KeyDown/KeyUp 捕获，手柄由 MainWindow 转发到 CaptureButtons） ----
    // 同一套捕获状态服务两处：映射小组件（源可为手柄键或键盘键）与 手柄映射（源=手柄键，目标=键盘键/手柄键/鼠标键）

    private void OnMappingCaptureClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => StartMappingCapture();

    /// <summary>映射小组件的按键捕获入口（源：手柄键或键盘键，二选一）</summary>
    private void StartMappingCapture() => StartCapture(CaptureMode.Mapping);

    /// <summary>进入按键捕获：清空待定缓冲，按模式给出提示，并把焦点收回页面以接收键盘事件</summary>
    private void StartCapture(CaptureMode mode, GamepadMapping? mapping = null)
    {
        _captureMode = mode;
        _captureMapping = mapping;
        _mappingCapturing = true;
        _pendingPad = 0;
        _pendingKey = 0;
        _pendingMods = 0;
        _liveMods = 0;
        switch (mode)
        {
            case CaptureMode.Mapping:
                MappingText.Text = "请按下手柄或键盘按键…";
                break;
            case CaptureMode.MapTargetKey:
                GamepadMapHintText.Text = "请按下要输出的键盘按键…";
                GamepadMapHintBox.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                break;
            case CaptureMode.MapTargetPad:
                GamepadMapHintText.Text = "请按下要输出的手柄按键…";
                GamepadMapHintBox.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                break;
        }
        App.Main?.KeyboardHook.SetSuspended(true); // 捕获期间暂停呼出键，避免按下呼出键把窗口收起
        Focus(FocusState.Programmatic); // 键盘事件落到页面（否则可能被按钮/文本框吞掉）
    }

    /// <summary>捕获结束：按模式定格结果并交还焦点到表单</summary>
    private void EndMappingCapture()
    {
        var mode = _captureMode;
        var mapping = _captureMapping;
        _mappingCapturing = false;
        _captureMapping = null;
        _captureMode = CaptureMode.Mapping;
        App.Main?.KeyboardHook.SetSuspended(false);
        switch (mode)
        {
            case CaptureMode.Mapping:
                MappingText.Text = KeySendService.MappingLabel(_pendingPad, _pendingKey, _pendingMods);
                break;
            case CaptureMode.MapTargetKey:
                if (mapping is not null && _pendingKey != 0)
                {
                    mapping.TargetKind = MappingTargetKind.Key;
                    mapping.TargetKey = _pendingKey;
                    mapping.TargetMods = _pendingMods;
                }
                break;
            case CaptureMode.MapTargetPad:
                if (mapping is not null && _pendingPad != 0)
                {
                    mapping.TargetKind = MappingTargetKind.Pad;
                    mapping.TargetPad = (GamepadButtons)_pendingPad;
                }
                break;
        }
        GamepadMapHintBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (mode != CaptureMode.Mapping) RefreshGamepadMapAll();
        FocusForm(_formIndex);
    }

    /// <summary>MainWindow 在捕获模式转发的原始手柄按键：键盘目标模式忽略；否则累加为手柄键（可组合）</summary>
    public void CaptureButtons(GamepadButtons buttons)
    {
        if (!_mappingCapturing) return;
        if (_captureMode == CaptureMode.MapTargetKey) return; // 键盘目标只接受键盘输入
        _pendingPad |= (int)buttons;
        _pendingKey = 0;
        _pendingMods = 0;
        if (_captureMode == CaptureMode.Mapping)
            MappingText.Text = GamepadService.Describe((GamepadButtons)_pendingPad);
    }

    /// <summary>MainWindow 在所有手柄键松开时通知：若已捕获到手柄键则结束捕获</summary>
    public void NotifyAllReleased()
    {
        if (!_mappingCapturing || _pendingPad == 0) return;
        if (_captureMode == CaptureMode.MapTargetKey) return;
        EndMappingCapture();
    }

    /// <remarks>捕获期间吞掉按键事件，避免误触发表单操作；单独按修饰键只累积不结束</remarks>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!_mappingCapturing) { base.OnKeyDown(e); return; }
        // 手柄目标模式只接受手柄输入，吞掉键盘事件避免误操作
        if (_captureMode == CaptureMode.MapTargetPad) { e.Handled = true; return; }
        var vk = (int)e.Key;
        if (KeySendService.IsModifier(vk))
        {
            _liveMods |= KeySendService.ModFlag(vk);
        }
        else
        {
            _pendingPad = 0;
            _pendingKey = vk;
            _pendingMods = _liveMods;
            if (_captureMode == CaptureMode.Mapping)
                MappingText.Text = KeySendService.Describe(vk, _liveMods);
            else if (_captureMode == CaptureMode.MapTargetKey)
                GamepadMapHintText.Text = KeySendService.Describe(vk, _liveMods);
        }
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (!_mappingCapturing) { base.OnKeyUp(e); return; }
        if (_captureMode == CaptureMode.MapTargetPad) { e.Handled = true; return; }
        var vk = (int)e.Key;
        if (KeySendService.IsModifier(vk)) { _liveMods &= ~KeySendService.ModFlag(vk); return; }
        if (_pendingKey == vk) EndMappingCapture(); // 主键抬起即定格（此时修饰键仍按住，组合已记录）
        e.Handled = true;
    }

    // ---- 手柄映射：可视化手柄图编辑（按键热区参考 YeManCC 的 GamepadVisualizer）----

    /// <summary>构建可视化手柄图：网上找的手柄线稿底图（Assets/gamepad-base.png，裁剪+调色后 1:1 铺满画布）
    /// + 每个手柄键一个贴合底图按键轮廓的可聚焦热区（点击或手柄 A 键即选中该键）</summary>
    private void BuildGamepadMapDiagram()
    {
        GamepadMapCanvas.Children.Clear();
        _mapZones.Clear();

        // 底图：手柄线稿（取自参考实现 YeManCC 同款素材，裁剪到机身范围并统一调成浅灰线稿色）
        var baseImage = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/gamepad-base.png")),
            Width = PadCanvasWidth,
            Height = PadCanvasHeight,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill
        };
        Canvas.SetLeft(baseImage, 0);
        Canvas.SetTop(baseImage, 0);
        GamepadMapCanvas.Children.Add(baseImage);

        foreach (var (btn, x, y, w, h, text, round) in ZoneLayout)
        {
            // 扳机/肩键在底图上挨得很近，留白收紧以免热区互相压盖
            var pad = btn is GamepadButtons.LeftTrigger or GamepadButtons.RightTrigger
                or GamepadButtons.LeftShoulder or GamepadButtons.RightShoulder
                ? ShoulderHitPadding
                : ZoneHitPadding;

            Microsoft.UI.Xaml.Shapes.Shape visual = round
                ? new Microsoft.UI.Xaml.Shapes.Ellipse { Width = w, Height = h }
                : new Microsoft.UI.Xaml.Shapes.Rectangle { Width = w, Height = h, RadiusX = 5, RadiusY = 5 };
            visual.StrokeThickness = 1.5;

            var label = new TextBlock
            {
                Text = text,
                // 按键标注：长词（Select/Start/Guide）缩小，方向键用箭头
                FontSize = text.Length > 4 ? 7 : text.Length > 2 ? 8 : round ? 12 : 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 热区比图形框外扩 pad，图形本身仍按 ZoneLayout 尺寸居中（坐标一一对应底图按键轮廓）
            var content = new Grid
            {
                Width = w + pad * 2,
                Height = h + pad * 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(visual);
            content.Children.Add(label);

            var hit = new Button
            {
                Content = content,
                Width = w + pad * 2,
                Height = h + pad * 2,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(round ? (w + pad * 2) / 2 : 5),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                // 光标改用热区自身白描边表达（UpdateZoneCursor），关掉系统焦点框避免两圈重叠
                UseSystemFocusVisuals = false
            };
            var zone = new MapZoneUi { Button = btn, Hit = hit, Visual = visual, Label = label };
            hit.Click += (_, _) => SelectGamepadMapSource(zone.Button);
            Canvas.SetLeft(hit, x - pad);
            Canvas.SetTop(hit, y - pad);
            GamepadMapCanvas.Children.Add(hit);
            _mapZones.Add(zone);
        }
    }

    /// <summary>手柄图 + 编辑面板 + 概要一起刷新（选中/输出/删除变化后统一调用）</summary>
    private void RefreshGamepadMapAll()
    {
        RefreshGamepadMapDiagram();
        RefreshGamepadMapEditor();
        RefreshGamepadMapSummary();
        RebuildFormControls();
    }

    /// <summary>刷新手柄图配色：已设好输出=强调蓝，当前选中=琥珀描边</summary>
    private void RefreshGamepadMapDiagram()
    {
        foreach (var zone in _mapZones)
        {
            var mapped = HasTarget(MapFor(zone.Button));
            var selected = zone.Button == _selectedMapSource;
            zone.Visual.Fill = mapped ? MappedFillBrush : ZoneFillBrush;
            zone.Visual.Stroke = selected ? SelectedStrokeBrush : mapped ? MappedStrokeBrush : ZoneStrokeBrush;
            zone.Visual.StrokeThickness = selected ? 2.6 : 1.5;
            zone.Label.Foreground = mapped ? LabelMappedBrush : LabelBrush;
        }
    }

    /// <summary>刷新输出编辑面板：仅在选中某个手柄键后显示</summary>
    private void RefreshGamepadMapEditor()
    {
        if (_selectedMapSource == GamepadButtons.None)
        {
            GamepadMapEditor.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }
        GamepadMapEditor.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        var m = MapFor(_selectedMapSource);
        GamepadMapEditorTitle.Text = $"已选手柄键：{GamepadService.Describe(_selectedMapSource)}"
            + (m is null ? "（尚未设置输出）" : $" → {TargetTextOf(m)}");
        GamepadMapKeyRadio.IsChecked = m is null || m.TargetKind == MappingTargetKind.Key;
        GamepadMapMouseRadio.IsChecked = m?.TargetKind == MappingTargetKind.Mouse;
        GamepadMapPadRadio.IsChecked = m?.TargetKind == MappingTargetKind.Pad;
        GamepadMapCaptureButton.Content = m is null
            ? "设置键盘键…"
            : m.TargetKind switch
            {
                MappingTargetKind.Mouse => "切换鼠标键",
                MappingTargetKind.Pad => "设置手柄键…",
                _ => "设置键盘键…"
            };
        GamepadMapDeleteButton.IsEnabled = m is not null;
    }

    /// <summary>刷新概要文案：列出所有已设好输出的映射</summary>
    private void RefreshGamepadMapSummary()
    {
        var parts = (_pendingGamepadMappings ?? [])
            .Where(m => HasTarget(m))
            .Select(m => $"{GamepadService.Describe(m.Source)} → {TargetTextOf(m)}")
            .ToList();
        GamepadMapSummary.Text = parts.Count == 0
            ? "尚未设置映射"
            : "已设置：" + string.Join("；", parts);
    }

    /// <summary>取某手柄键的映射（未设置返回 null）</summary>
    private GamepadMapping? MapFor(GamepadButtons btn)
        => _pendingGamepadMappings?.FirstOrDefault(m => m.Source == btn);

    /// <summary>取当前选中手柄键的映射；不存在则新建（默认键盘输出）</summary>
    private GamepadMapping? EnsureMap()
    {
        if (_selectedMapSource == GamepadButtons.None) return null;
        var m = MapFor(_selectedMapSource);
        if (m is null)
        {
            m = new GamepadMapping { Source = _selectedMapSource, TargetKind = MappingTargetKind.Key };
            _pendingGamepadMappings ??= [];
            _pendingGamepadMappings.Add(m);
        }
        return m;
    }

    /// <summary>该映射是否已设好输出（未设好的只在编辑期存在：不保存、图上也不算"已映射"）</summary>
    private static bool HasTarget(GamepadMapping? m)
    {
        if (m is null) return false;
        return m.TargetKind switch
        {
            MappingTargetKind.Mouse => m.TargetMouse != 0,
            MappingTargetKind.Pad => m.TargetPad != GamepadButtons.None,
            _ => m.TargetKey != 0
        };
    }

    /// <summary>目标文案：键盘键 / 鼠标键 / 手柄键（未设置显示对应提示）</summary>
    private static string TargetTextOf(GamepadMapping m) => m.TargetKind switch
    {
        MappingTargetKind.Mouse => KeySendService.MouseLabel(m.TargetMouse),
        MappingTargetKind.Pad => m.TargetPad == GamepadButtons.None ? "未设置手柄键" : GamepadService.Describe(m.TargetPad),
        _ => m.TargetKey == 0 ? "未设置键盘键" : KeySendService.Describe(m.TargetKey, m.TargetMods)
    };

    /// <summary>选中手柄图上的某个键：展开其输出编辑面板（映射在首次设置输出时才真正创建）</summary>
    private void SelectGamepadMapSource(GamepadButtons btn)
    {
        _selectedMapSource = btn;
        GamepadMapHintBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        RefreshGamepadMapAll();
        var zone = _mapZones.FirstOrDefault(z => z.Button == btn);
        if (zone is null) return;
        var idx = Array.IndexOf(_formControls, zone.Hit); // 手柄导航光标跟到这个热区
        if (idx >= 0) _formIndex = idx;
        FocusForm(_formIndex);
    }

    private void OnGamepadMapKindClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ApplyGamepadMapKind(sender);

    /// <summary>切换选中手柄键的输出类型（键盘/鼠标/手柄）</summary>
    private void ApplyGamepadMapKind(object sender)
    {
        var kind = ReferenceEquals(sender, GamepadMapMouseRadio) ? MappingTargetKind.Mouse
            : ReferenceEquals(sender, GamepadMapPadRadio) ? MappingTargetKind.Pad
            : MappingTargetKind.Key;
        var m = EnsureMap();
        if (m is not null) m.TargetKind = kind;
        RefreshGamepadMapAll();
    }

    private void OnGamepadMapCaptureClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => DoGamepadMapCapture();

    /// <summary>设置/切换选中手柄键的输出：鼠标输出循环切换按键，键盘/手柄输出进入按键捕获</summary>
    private void DoGamepadMapCapture()
    {
        var m = EnsureMap();
        if (m is null) return;
        switch (m.TargetKind)
        {
            case MappingTargetKind.Mouse:
                var pos = Array.IndexOf(MouseOrder, m.TargetMouse);
                m.TargetMouse = MouseOrder[(pos + 1) % MouseOrder.Length]; // pos=-1 时取第一个
                RefreshGamepadMapAll();
                break;
            case MappingTargetKind.Pad:
                StartCapture(CaptureMode.MapTargetPad, m);
                break;
            default:
                StartCapture(CaptureMode.MapTargetKey, m);
                break;
        }
    }

    private void OnGamepadMapDeleteClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => DoGamepadMapDelete();

    /// <summary>删除选中手柄键的映射</summary>
    private void DoGamepadMapDelete()
    {
        var m = MapFor(_selectedMapSource);
        if (m is null) return;
        _pendingGamepadMappings?.Remove(m);
        RefreshGamepadMapAll();
    }


    /// <remarks>切走页面时结束捕获，避免 MainWindow 仍向本页转发原始按键</remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _mappingCapturing = false;
        _captureMapping = null;
        _captureMode = CaptureMode.Mapping;
        GamepadMapHintBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        App.Main?.KeyboardHook.SetSuspended(false); // 捕获中途离开页面时恢复呼出键
    }

    // ---- 浏览模式 ----

    private void EnterBrowse(BrowseMode mode)
    {
        _browsing = true;
        _browseMode = mode;
        FormScroll.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        FooterBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; // 选文件时页脚让位给列表
        BrowsePanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        PageTitle.Text = mode switch
        {
            BrowseMode.Icon => "选择图标（exe / ico / 图片）",
            BrowseMode.Cover => "选择磁贴图片（png / jpg / bmp…）",
            BrowseMode.Folder => "选择文件夹",
            _ => "选择程序（.exe）"
        };
        HintBar.SetHints(mode is BrowseMode.Folder
            ? "DPAD:选择|A:进入/选中|B:返回"
            : "DPAD:选择|A:进入/确认|B:返回");
        NavigateTo(GetStartDir());
    }

    private void ExitBrowse()
    {
        _browsing = false;
        _pickingPhotoFolder = false; // 取消浏览时同样复位，避免影响下次普通文件夹选择
        _pickingBackupFolder = false;
        _pickingFtSendFolder = false;
        _pickingFtRecvFolder = false;
        BrowsePanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        FormScroll.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        FooterBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
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

                if (mode is BrowseMode.Folder)
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
        // 游戏存档选目标文件夹：只记录到 _pendingBackupFolder，不改 TargetBox/类型（Target 是组件标识）
        if (_pickingBackupFolder)
        {
            _pendingBackupFolder = dir;
            BackupFolderText.Text = dir;
            _pickingBackupFolder = false;
            ExitBrowse();
            return;
        }
        // 图片组选定向文件夹：只记录到 _pendingPhotoFolder，不改 TargetBox/类型（Target 是组件标识）
        if (_pickingPhotoFolder)
        {
            _pendingPhotoFolder = dir;
            PhotoFolderText.Text = dir;
            _pickingPhotoFolder = false;
            ExitBrowse();
            return;
        }
        // 文件传输选可发送目录：只记录到 _pendingFtSendFolder，不改 TargetBox/类型
        if (_pickingFtSendFolder)
        {
            _pendingFtSendFolder = dir;
            FtSendFolderText.Text = dir;
            _pickingFtSendFolder = false;
            ExitBrowse();
            return;
        }
        // 文件传输选接收目录：只记录到 _pendingFtRecvFolder，不改 TargetBox/类型
        if (_pickingFtRecvFolder)
        {
            _pendingFtRecvFolder = dir;
            FtRecvFolderText.Text = dir;
            _pickingFtRecvFolder = false;
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
