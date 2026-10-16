using GameLocker.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 设置主页：分类入口（常规 / 外观 / 顶栏 / 控制 / 关于）。
/// 云端备份不在此列出，只能从「游戏存档小组件」编辑页进入 CloudPage。
/// 手柄：方向=选择分类，A=进入子页，View(Select)/B=返回主页。
/// </summary>
public sealed partial class SettingsPage : Page, ISettingsGamepad
{
    private int _index;
    private bool _ready;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = CategoryList.SelectedIndex < 0 ? 0 : CategoryList.SelectedIndex;
        CategoryList.SelectedIndex = _index;
        FocusIndex(_index);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft))
            Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight))
            Move(1);
        else if (buttons.HasFlag(GamepadButtons.A))
            OpenCategory(_index);
    }

    private void Move(int delta)
    {
        var count = CategoryList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(_index + delta, 0, count - 1);
        if (next == _index) return;
        _index = next;
        CategoryList.SelectedIndex = next;
        FocusIndex(next);
    }

    private void FocusIndex(int i)
    {
        if (CategoryList.ContainerFromIndex(i) is ListViewItem item)
            item.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
    }

    /// <summary>鼠标/触摸点击分类项进入子页（ListViewItem.Tapped 对鼠标点击与触屏轻触都可靠触发）</summary>
    private void OnCategoryTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var idx = CategoryList.Items.IndexOf(sender);
        if (idx >= 0)
        {
            _index = idx;
            CategoryList.SelectedIndex = idx;
            OpenCategory(idx);
        }
    }

    private void OpenCategory(int index)
    {
        var page = index switch
        {
            1 => typeof(AppearancePage),
            2 => typeof(TopbarPage),
            3 => typeof(KeybindPage),
            4 => typeof(AboutPage),
            _ => typeof(GeneralPage)
        };
        App.Main?.NavigateSettings(page);
    }

    // 分类主页无按键捕获
    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
