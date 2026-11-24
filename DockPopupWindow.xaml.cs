using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using GameLocker.Models;
using GameLocker.Services;
using Windows.Graphics;

namespace GameLocker;

/// <summary>弹层里的一项：可能是启动器条目，也可能是一个文件路径（文件夹展开）。</summary>
public sealed record DockPopupEntry(string Label, LaunchItem? Item, string Target, string? IconPath, bool IsFolder = false, Action? Run = null);

/// <summary>
/// 底栏的弹出面板：应用组宫格、文件夹内容展开都走这里。
/// 与底栏不同，这个窗口是**可激活**的普通窗口——弹层里的按钮需要正常接收鼠标，
/// 而底栏是 WS_EX_NOACTIVATE、WinUI 指针事件进不来（见 DockWindow 的说明）。
/// </summary>
public sealed partial class DockPopupWindow : Window
{
    private const int ItemSize = 56;
    private const int Gap = 10;

    private static DockPopupWindow? _current;

    private readonly List<DockPopupEntry> _entries = [];
    private bool _isArc;
    private bool _isMenu;    private bool _isStartMenu;

    // ---- 手柄导航状态（弹层是可激活普通窗口，但 WinUI 不认手柄，这里自己维护选中项）----
    private int _padIndex = -1;
    private int _padColumns = 1;
    private readonly List<Button> _padButtons = [];
    private readonly List<Action> _padActions = [];
    private ListView? _padList;                 // 开始菜单列表（另一种布局）
    private List<(string Name, string Path)>? _padShortcuts;
    private Action<string>? _padPick;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public DockPopupWindow()
    {
        InitializeComponent();
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Title = "Game Locker Dock Popup";
        ApplyChrome();

        // _current 要在窗口自行关闭时清掉，否则 IsOpen 会一直为 true，手柄/鼠标判定都会错
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };

        // 不靠"失焦自动关闭"：底栏是 NOACTIVATE 窗口，弹层激活后很容易被别的窗口
        // 抢走焦点而瞬间消失（实测 2 秒内就没了）。改成由底栏的全局鼠标钩子判断
        // "点击落在弹层外部"再关闭。
    }

    /// <summary>该屏幕坐标是否落在弹层窗口内（物理像素）。</summary>
    public static bool Contains(int screenX, int screenY)
    {
        if (_current is null) return false;
        try
        {
            if (!GetWindowRect(_current.Hwnd, out var rect)) return false;
            return screenX >= rect.Left && screenX <= rect.Right
                && screenY >= rect.Top && screenY <= rect.Bottom;
        }
        catch { return false; }
    }

    public static bool IsOpen => _current is not null;

    /// <summary>宫格布局（应用组）。</summary>
    public static void ShowGrid(string title, IReadOnlyList<DockPopupEntry> entries, int anchorX, int screenBottom)
        => Show(title, entries, anchorX, screenBottom, arc: false, arcAngle: 0);

    /// <summary>弧形布局（文件夹展开）；angle 为扇形张开角度，0 表示竖排一列。</summary>
    public static void ShowArc(string title, IReadOnlyList<DockPopupEntry> entries, int anchorX, int screenBottom, int arcAngle)
        => Show(title, entries, anchorX, screenBottom, arc: true, arcAngle: arcAngle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>
    /// 竖排菜单（右键菜单用它）。
    /// 不用 MenuFlyout：底栏是 NOACTIVATE 窗口，WinUI 的 flyout 在这里弹不出来（实测抛异常）。
    /// </summary>
    public static void ShowMenu(string title, IReadOnlyList<DockPopupEntry> entries, int anchorX, int screenBottom)
    {
        try
        {
            _current?.Close();
            if (entries.Count == 0) return;

            var window = new DockPopupWindow { _isMenu = true };
            window._entries.AddRange(entries);
            _current = window;

            window.TitleText.Text = title;
            var (width, height) = window.LayoutMenu();
            window.MoveTo(anchorX - width / 2, screenBottom - height, width, height);
            window.Activate();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 菜单打开失败：" + ex.Message); }
    }

    /// <summary>竖排菜单（贴着某个上边缘向下展开，顶栏用它）。</summary>
    public static void ShowMenuBelow(string title, IReadOnlyList<DockPopupEntry> entries, int anchorX, int topY)
    {
        try
        {
            _current?.Close();
            if (entries.Count == 0) return;

            var window = new DockPopupWindow { _isMenu = true };
            window._entries.AddRange(entries);
            _current = window;

            window.TitleText.Text = title;
            var (width, height) = window.LayoutMenu();
            window.MoveTo(anchorX - width / 2, topY + 6, width, height);
            window.Activate();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 菜单打开失败：" + ex.Message); }
    }

    /// <summary>竖排菜单布局：每项一行，宽度固定。</summary>
    private (int Width, int Height) LayoutMenu()
    {
        const int rowHeight = 34;
        const int menuWidth = 210;

        ItemsCanvas.Children.Clear();
        _padButtons.Clear();
        _padActions.Clear();
        _padColumns = 1;

        for (var i = 0; i < _entries.Count; i++)
            PlaceMenuRow(i, i * rowHeight, menuWidth, rowHeight - 4);

        ItemsCanvas.Width = menuWidth;
        ItemsCanvas.Height = _entries.Count * rowHeight;

        var scale = Scale;
        return ((int)Math.Round((menuWidth + 28) * scale),
                (int)Math.Round((_entries.Count * rowHeight + 28 + 34) * scale));
    }

    private void PlaceMenuRow(int index, int y, int width, int height)
    {
        var entry = _entries[index];

        var button = new Button
        {
            Width = width,
            Height = height,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Content = new TextBlock { Text = entry.Label, FontSize = 13 }
        };

        var item = index;
        button.Click += async (_, _) => await ActivateEntryAsync(item);
        _padButtons.Add(button);
        _padActions.Add(() => { _ = ActivateEntryAsync(item); });

        Canvas.SetLeft(button, 0);
        Canvas.SetTop(button, y);
        ItemsCanvas.Children.Add(button);
    }

    /// <summary>
    /// 从开始菜单挑选应用：带搜索框的列表。
    /// 开始菜单里通常有几百个快捷方式，所以不做成分页宫格，而是"输入即过滤"的列表。
    /// </summary>
    public static void ShowStartMenu(int anchorX, int screenBottom, Action<string> onPick)
    {
        try
        {
            _current?.Close();

            var shortcuts = StartMenuShortcuts();
            if (shortcuts.Count == 0)
            {
                DebugLog.Write("[底栏] 开始菜单里没有找到快捷方式");
                return;
            }

            var window = new DockPopupWindow { _isStartMenu = true };
            _current = window;

            window.TitleText.Text = $"从开始菜单添加（{shortcuts.Count} 项）";
            var (width, height) = window.LayoutStartMenu(shortcuts, onPick);
            window.MoveTo(anchorX - width / 2, screenBottom - height, width, height);
            window.Activate();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 打开开始菜单列表失败：" + ex.Message); }
    }

    /// <summary>枚举开始菜单（公共 + 用户）里的 .lnk。</summary>
    private static List<(string Name, string Path)> StartMenuShortcuts()
    {
        var list = new List<(string, string)>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrWhiteSpace(name)) list.Add((name, file));
                }
            }
            catch (Exception ex) { DebugLog.Write("[底栏] 枚举开始菜单失败：" + ex.Message); }
        }

        return list
            .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Item1, StringComparer.CurrentCulture)
            .ToList();
    }

    private (int Width, int Height) LayoutStartMenu(List<(string Name, string Path)> shortcuts, Action<string> onPick)
    {
        const int panelWidth = 360;
        const int listHeight = 420;

        ItemsCanvas.Children.Clear();

        var search = new TextBox
        {
            Width = panelWidth,
            Height = 34,
            PlaceholderText = "输入名称过滤…",
            FontSize = 13
        };

        var list = new ListView
        {
            Width = panelWidth,
            Height = listHeight,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true
        };

        var names = new System.Collections.ObjectModel.ObservableCollection<string>();
        void Refill(string? filter)
        {
            names.Clear();
            foreach (var (name, _) in shortcuts)
            {
                if (string.IsNullOrWhiteSpace(filter)
                    || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                    names.Add(name);
            }
        }
        Refill(null);
        list.ItemsSource = names;

        search.TextChanged += (_, _) => Refill(search.Text);

        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not string label) return;
            var match = shortcuts.FirstOrDefault(s => s.Name == label);
            if (string.IsNullOrEmpty(match.Path)) return;

            Close();
            try { onPick(match.Path); }
            catch (Exception ex) { DebugLog.Write("[底栏] 添加开始菜单项失败：" + ex.Message); }
        };

        Canvas.SetLeft(search, 0);
        Canvas.SetTop(search, 0);
        Canvas.SetLeft(list, 0);
        Canvas.SetTop(list, 42);
        ItemsCanvas.Children.Add(search);
        ItemsCanvas.Children.Add(list);

        ItemsCanvas.Width = panelWidth;
        ItemsCanvas.Height = listHeight + 42;

        _padList = list;
        _padShortcuts = shortcuts;
        _padPick = onPick;

        var scale = Scale;
        return ((int)Math.Round((panelWidth + 28) * scale),
                (int)Math.Round((listHeight + 42 + 28 + 34) * scale));
    }

    public static void HideCurrent()
    {
        try { _current?.Close(); } catch { }
        _current = null;
    }

    /// <summary>
    /// 手柄按键：当前弹层自己导航（XInput 不会进 WinUI，弹层按钮收不到焦点/按键）。
    /// 宫格/弧形/菜单：←→ 或 LB/RB 逐项、↑↓ 按行跳、A 执行、B 关闭；
    /// 开始菜单列表：↑↓ 选择、A 添加、B 关闭。返回是否消费。
    /// </summary>
    public static bool HandleGamepad(GamepadButtons buttons)
    {
        var current = _current;
        if (current is null) return false;
        // 手柄事件来自轮询线程：切到弹出的 UI 线程再动可视树
        current._dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_current, current)) return;
            try { current.HandleGamepadCore(buttons); }
            catch (Exception ex) { DebugLog.Write("[底栏] 弹层手柄导航失败：" + ex.Message); }
        });
        return true;
    }

    private bool HandleGamepadCore(GamepadButtons buttons)
    {
        if (buttons == GamepadButtons.None) return false;

        // B / View：关闭弹层（与鼠标点到外面一致）
        if (buttons.HasFlag(GamepadButtons.B) || buttons.HasFlag(GamepadButtons.View))
        {
            HideCurrent();
            return true;
        }

        if (_padList is not null) return HandleStartMenuGamepad(buttons);

        if (_padButtons.Count == 0) return true; // 空弹层只允许关闭

        if (buttons.HasFlag(GamepadButtons.A))
        {
            if (_padIndex < 0) { SelectPad(0); return true; }
            try { _padActions[_padIndex](); } catch (Exception ex) { DebugLog.Write("[底栏] 弹层手柄执行失败：" + ex.Message); }
            return true;
        }

        var delta = 0;
        if (buttons.HasFlag(GamepadButtons.DPadLeft) || buttons.HasFlag(GamepadButtons.LeftShoulder)) delta = -1;
        else if (buttons.HasFlag(GamepadButtons.DPadRight) || buttons.HasFlag(GamepadButtons.RightShoulder)) delta = 1;
        else if (buttons.HasFlag(GamepadButtons.DPadUp)) delta = -_padColumns;
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) delta = _padColumns;

        if (delta == 0) return true; // 其余键在弹层内不做事，也不再传给底栏
        MovePad(delta);
        return true;
    }

    private void MovePad(int delta)
    {
        var count = _padButtons.Count;
        if (count == 0) return;
        var next = _padIndex < 0 ? 0 : (_padIndex + delta) % count;
        if (next < 0) next += count;
        SelectPad(next);
    }

    /// <summary>选中第 index 项：白描边 + 保持可见（宫格按行跳时不会跳空）。</summary>
    private void SelectPad(int index)
    {
        if (index < 0 || index >= _padButtons.Count) return;

        if (_padIndex >= 0 && _padIndex < _padButtons.Count)
        {
            var old = _padButtons[_padIndex];
            old.BorderThickness = new Thickness(0);
            old.BorderBrush = null;
        }

        _padIndex = index;
        var button = _padButtons[index];
        button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        button.BorderThickness = new Thickness(2);
    }

    private bool HandleStartMenuGamepad(GamepadButtons buttons)
    {
        var list = _padList;
        if (list is null) return false;
        var count = list.Items.Count;
        if (count == 0) return true;

        if (buttons.HasFlag(GamepadButtons.A))
        {
            if (list.SelectedIndex < 0) SelectStartItem(0);
            else PickStartItem(list.SelectedIndex);
            return true;
        }

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.LeftShoulder))
            SelectStartItem(list.SelectedIndex < 0 ? 0 : Math.Max(0, list.SelectedIndex - 1));
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.RightShoulder))
            SelectStartItem(list.SelectedIndex < 0 ? 0 : Math.Min(count - 1, list.SelectedIndex + 1));

        return true;
    }

    private void SelectStartItem(int index)
    {
        if (_padList is null || index < 0 || index >= _padList.Items.Count) return;
        _padList.SelectedIndex = index;
        _padList.ScrollIntoView(_padList.Items[index]);
    }

    private void PickStartItem(int index)
    {
        if (_padList is null || _padShortcuts is null || _padPick is null) return;
        if (index < 0 || index >= _padList.Items.Count) return;
        if (_padList.Items[index] is not string label) return;

        var match = _padShortcuts.FirstOrDefault(s => s.Name == label);
        if (string.IsNullOrEmpty(match.Path)) return;

        Close();
        try { _padPick(match.Path); }
        catch (Exception ex) { DebugLog.Write("[底栏] 添加开始菜单项失败：" + ex.Message); }
    }

    private static void Show(
        string title, IReadOnlyList<DockPopupEntry> entries, int anchorX, int screenBottom,
        bool arc, int arcAngle)
    {
        try
        {
            _current?.Close();
            if (entries.Count == 0) return;

            var window = new DockPopupWindow { _isArc = arc };
            window._entries.AddRange(entries);
            _current = window;

            window.TitleText.Text = title;
            var (width, height) = window.Layout(entries.Count, arcAngle);

            // 先定位再激活：首帧就按最终尺寸渲染，避免内容按默认尺寸布局后再重排
            window.MoveTo(anchorX - width / 2, screenBottom - height, width, height);
            window.Activate();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 弹层打开失败：" + ex.Message); }
    }

    /// <summary>
    /// 按内容排布并返回窗口需要的尺寸（物理像素）。
    /// 注意：XAML 的 Width/Height 是 DIP，所以布局全部用 DIP 计算，
    /// 只有最后的窗口尺寸才乘 DPI 缩放换算成物理像素。
    /// </summary>
    private (int Width, int Height) Layout(int count, int arcAngle)
    {
        const int size = ItemSize;   // DIP
        const int gap = Gap;         // DIP

        ItemsCanvas.Children.Clear();
        _padButtons.Clear();
        _padActions.Clear();
        _padColumns = _isArc ? 1 : 5;

        int gridWidth, gridHeight;

        if (_isArc && arcAngle > 0)
        {
            // 弧形：以底部中心为圆心向上张开
            var count2 = Math.Min(count, 7);
            // 半径按"相邻图标刚好不压住"估算：弦长 ≈ 2R·sin(Δ/2) ≥ 图标间距
            var radius = (int)((size + gap) * 2.4);
            var sweep = Math.Clamp(arcAngle, 20, 180) * Math.PI / 180.0;

            var cx = radius + size / 2.0;
            var cy = radius + size / 2.0;

            for (var i = 0; i < count2; i++)
            {
                var t = count2 == 1 ? 0.5 : (double)i / (count2 - 1);
                // 以正上方（90°）为中心左右对称张开
                var angle = Math.PI / 2 + sweep / 2 - sweep * t;
                var x = cx + radius * Math.Cos(angle) - size / 2.0;
                var y = cy - radius * Math.Sin(angle) - size / 2.0;
                PlaceItem(i, (int)x, (int)y, size);
            }

            gridWidth = (int)(cx + radius + size / 2.0);
            gridHeight = (int)(cy + size / 2.0);
        }
        else
        {
            // 宫格：最多 5 列
            var columns = Math.Min(count, 5);
            var rows = (count + 4) / 5;

            for (var i = 0; i < count; i++)
            {
                var column = i % 5;
                var row = i / 5;
                PlaceItem(i, column * (size + gap), row * (size + gap), size);
            }

            gridWidth = columns * size + (columns - 1) * gap;
            gridHeight = rows * size + (rows - 1) * gap;
        }

        ItemsCanvas.Width = gridWidth;
        ItemsCanvas.Height = gridHeight;

        // 卡片内边距 + 标题行（DIP），最后统一换算成物理像素
        const int padding = 14 * 2;
        const int titleBand = 20 + 10 + 6; // 标题高 + StackPanel.Spacing + 余量

        var scale = Scale;
        return ((int)Math.Round((gridWidth + padding) * scale),
                (int)Math.Round((gridHeight + padding + titleBand) * scale));
    }

    private void PlaceItem(int index, int x, int y, int size)
    {
        var entry = _entries[index];

        var button = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(button, entry.Label);

        if (entry.IconPath is not null)
        {
            button.Content = new Image
            {
                Source = new BitmapImage(new Uri(entry.IconPath)),
                Stretch = Stretch.Uniform,
                Width = size * 0.62,
                Height = size * 0.62
            };
        }
        else
        {
            button.Content = new FontIcon
            {
                Glyph = entry.IsFolder || entry.Item?.Kind == LaunchKind.Folder ? "\uE8B7"
                    : entry.Item?.Kind == LaunchKind.Url ? "\uE774"
                    : entry.Item?.Kind == LaunchKind.Folder ? "\uE8B7"
                    : "\uE8A5",
                FontSize = size * 0.4,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
            };
        }

        // 文件/文件夹条目支持拖出到资源管理器（1.12）。
        // 弹层是可激活窗口，XAML 拖放在这里可用（底栏本体不行）。
        if (entry.Item is null && !string.IsNullOrEmpty(entry.Target)
            && (File.Exists(entry.Target) || Directory.Exists(entry.Target)))
        {
            button.CanDrag = true;
            button.DragStarting += async (_, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                    var items = new List<Windows.Storage.IStorageItem>();

                    if (Directory.Exists(entry.Target))
                        items.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(entry.Target));
                    else if (File.Exists(entry.Target))
                        items.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(entry.Target));

                    if (items.Count > 0) e.Data.SetStorageItems(items);
                }
                catch (Exception ex) { DebugLog.Write("[底栏] 拖出文件失败：" + ex.Message); }
                finally { deferral.Complete(); }
            };
        }

        var item = index;
        button.Click += async (_, _) => await ActivateEntryAsync(item);
        _padButtons.Add(button);
        _padActions.Add(() => { _ = ActivateEntryAsync(item); });

        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, y);
        ItemsCanvas.Children.Add(button);
    }

    /// <summary>
    /// 激活弹层里的第 index 项（鼠标点击与手柄 A 共用）：先关弹层，再按条目类型启动/执行。
    /// </summary>
    private async Task ActivateEntryAsync(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        var entry = _entries[index];
        Close();
        try
        {
            if (entry.Run is not null)
            {
                entry.Run();
                return;
            }

            if (entry.Item is not null)
                await LaunchService.LaunchOrActivateAsync(entry.Item, CancellationToken.None,
                    SettingsService.Current.DockTrayWake);
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = entry.Target,
                    UseShellExecute = true
                });
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 弹层启动失败：" + ex.Message); }
    }

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private double Scale => GetDpiForWindow(Hwnd) / 96.0;

    private void MoveTo(int x, int y, int width, int height)
    {
        var hwnd = Hwnd;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, Math.Max(1, width), Math.Max(1, height),
            SWP_NOACTIVATE);
    }

    private void ApplyChrome()
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);

        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

    }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : (IntPtr)SetWindowLong32(hwnd, index, value.ToInt32());

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
