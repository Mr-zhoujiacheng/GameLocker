using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using GameLocker.Services;

namespace GameLocker;

/// <summary>
/// 底栏速记：一个轻量便签窗口，内容自动保存在本地，可导出 txt / Markdown。
/// 与底栏不同，这是普通可激活窗口（需要键盘输入）。
/// </summary>
public sealed partial class DockNoteWindow : Window
{
    private static DockNoteWindow? _current;

    private readonly TextBox _editor;
    private readonly TextBlock _status;
    private bool _dirty;
    private DispatcherTimer? _saveTimer;

    private static string NotePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameLocker", "dock-note.txt");

    public DockNoteWindow()
    {
        Title = "Game Locker 速记";
        ApplyChrome();

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "随手记点什么，关闭即自动保存…",
            FontSize = 14,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _status = new TextBlock { FontSize = 11, Opacity = 0.55 };

        var exportTxt = new Button { Content = "导出 txt", FontSize = 12 };
        exportTxt.Click += async (_, _) => await ExportAsync(".txt");

        var exportMd = new Button { Content = "导出 Markdown", FontSize = 12 };
        exportMd.Click += async (_, _) => await ExportAsync(".md");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { exportTxt, exportMd }
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x14, 0x14, 0x18)),
            Padding = new Thickness(16),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        Grid.SetRow(_editor, 0);
        Grid.SetRow(buttons, 1);
        root.Children.Add(_editor);
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { buttons, _status }
        });
        Grid.SetRow((FrameworkElement)root.Children[1], 1);

        Content = root;

        Load();
        _editor.TextChanged += (_, _) => { _dirty = true; UpdateStatus(); };
        Closed += (_, _) => { Save(); _saveTimer?.Stop(); _current = null; };
    }

    /// <summary>在底栏上方弹出速记窗口。</summary>
    public static void ShowNear(int anchorX, int dockTop)
    {
        try
        {
            if (_current is not null)
            {
                _current.Activate();
                return;
            }

            var window = new DockNoteWindow();
            _current = window;

            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
            var width = (int)Math.Round(420 * scale);
            var height = (int)Math.Round(320 * scale);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var x = anchorX - width / 2;
            var y = dockTop - height - (int)Math.Round(12 * scale);

            // 用 TOPMOST：这类小窗由点击顶栏/底栏唤起，单纯 Activate 可能抢不到前台而藏在别的窗口后面
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            window.Activate();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 打开速记失败：" + ex.Message); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(NotePath)) _editor.Text = File.ReadAllText(NotePath);
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 读取速记失败：" + ex.Message); }
        _dirty = false;
        UpdateStatus();
    }

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotePath)!);
            File.WriteAllText(NotePath, _editor.Text);
            _dirty = false;
            UpdateStatus();
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 保存速记失败：" + ex.Message); }
    }

    private void UpdateStatus()
    {
        _status.Text = _dirty ? "未保存…" : "已保存";
    }

    private async Task ExportAsync(string extension)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedFileName = "dock-note";
            picker.FileTypeChoices.Add(extension == ".md" ? "Markdown" : "文本文件", new List<string> { extension });

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var text = extension == ".md"
                ? "# 速记" + Environment.NewLine + Environment.NewLine + _editor.Text
                : _editor.Text;

            await File.WriteAllTextAsync(file.Path, text);
            DebugLog.Write($"[底栏] 速记已导出：{file.Path}");
        }
        catch (Exception ex) { DebugLog.Write("[底栏] 导出速记失败：" + ex.Message); }
    }

    private void ApplyChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME);
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
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
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
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
