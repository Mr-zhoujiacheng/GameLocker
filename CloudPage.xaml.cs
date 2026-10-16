using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 云端备份设置：WebDAV（默认坚果云）的服务器地址、账号、应用密码、云端根目录名，含保存与测试连接。
/// 手柄：方向=移动焦点，A=聚焦输入框/按下按钮，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class CloudPage : Page, ISettingsGamepad
{
    /// <summary>坚果云官方教程：第三方应用授权 WebDAV 开启方法（含添加应用密码的截图）</summary>
    private const string NutstoreHelpUrl = "https://help.jianguoyun.com/?p=2064";

    /// <summary>地址留空时的兜底值</summary>
    private const string DefaultUrl = "https://dav.jianguoyun.com/dav/";

    private const string DefaultRoot = "GameLocker";

    private readonly Control[] _controls;
    private int _index;
    private bool _ready;
    private bool _busy;

    public CloudPage()
    {
        InitializeComponent();

        _controls = [UrlBox, UserBox, PasswordInput, RootBox, SaveButton, TestButton, OpenNutstoreButton];

        UrlBox.Text = SettingsService.Current.WebDavUrl;
        UserBox.Text = SettingsService.Current.WebDavUser;
        RootBox.Text = SettingsService.Current.WebDavRootFolder;
        RefreshUi();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = 0;
        FocusIndex(0);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft)) Move(-1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight)) Move(1);
        else if (buttons.HasFlag(GamepadButtons.A)) ActivateCurrent();
    }

    private void Move(int delta)
    {
        var next = Math.Clamp(_index + delta, 0, _controls.Length - 1);
        if (next == _index) return;
        _index = next;
        FocusIndex(next);
    }

    private void FocusIndex(int i)
    {
        var c = _controls[i];
        c.Focus(FocusState.Keyboard);
        EnsureVisible(c);
    }

    private void ActivateCurrent()
    {
        switch (_controls[_index])
        {
            case TextBox tb:
                tb.Focus(FocusState.Keyboard); // 唤出系统输入法后可键盘输入
                break;
            case PasswordBox pb:
                pb.Focus(FocusState.Keyboard);
                break;
            case Button btn when ReferenceEquals(btn, SaveButton):
                SaveConfig();
                break;
            case Button btn when ReferenceEquals(btn, TestButton):
                _ = TestAsync();
                break;
            case Button btn when ReferenceEquals(btn, OpenNutstoreButton):
                OpenNutstoreHelp();
                break;
        }
    }

    /// <summary>窄窗口下焦点控件自动滚入可视区（同 AppearancePage 的手动 ChangeView 实现）</summary>
    private void EnsureVisible(Control c)
    {
        try
        {
            if (ReferenceEquals(c, _controls[0]))
            {
                ContentScroll.ChangeView(null, 0, null);
                return;
            }
            if (ReferenceEquals(c, _controls[^1]))
            {
                ContentScroll.ChangeView(null, ContentScroll.ScrollableHeight, null);
                return;
            }

            var p = c.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = ContentScroll.ViewportHeight;
            if (p.Y < 44)
                ContentScroll.ChangeView(null, Math.Max(0, ContentScroll.VerticalOffset + p.Y - 44), null);
            else if (p.Y + c.ActualHeight > vh)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { /* 未连接视觉树时忽略 */ }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => App.Main?.ShowSettingsHub();

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveConfig();
    private void OnTestClick(object sender, RoutedEventArgs e) => _ = TestAsync();
    private void OnOpenNutstoreClick(object sender, RoutedEventArgs e) => OpenNutstoreHelp();

    /// <summary>把四个输入框写回设置；应用密码留空则保留本机已保存的那份</summary>
    private void SaveConfig()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) url = DefaultUrl;
        if (!url.EndsWith('/')) url += "/";

        var user = UserBox.Text.Trim();
        var root = RootBox.Text.Trim();
        if (root.Length == 0) root = DefaultRoot;

        SettingsService.Update(s =>
        {
            s.WebDavUrl = url;
            s.WebDavUser = user;
            s.WebDavRootFolder = root;
        });

        var pwd = PasswordInput.Password;
        if (pwd.Length > 0)
        {
            SecretProtector.Save(pwd);
            PasswordInput.Password = "";
        }

        UrlBox.Text = url;
        RootBox.Text = root;
        RefreshUi();
        StatusText.Text = string.IsNullOrEmpty(SecretProtector.Load())
            ? "已保存，但还缺应用密码（无法上传）" : "已保存配置";
    }

    /// <summary>用当前输入框的值（密码留空则用本机已保存的）测试连接</summary>
    private async Task TestAsync()
    {
        if (_busy) return;

        var pwd = PasswordInput.Password;
        if (pwd.Length == 0) pwd = SecretProtector.Load() ?? "";

        var acct = WebDavService.BuildAccount(UrlBox.Text, UserBox.Text, pwd);
        if (acct is null)
        {
            StatusText.Text = "请先填写服务器地址、账号与应用密码";
            return;
        }

        _busy = true;
        RefreshUi();
        StatusText.Text = "正在测试连接…";

        string? err;
        try { err = await WebDavService.TestAsync(acct.Value); }
        catch (Exception ex) { err = "连接失败：" + ex.Message; }
        finally { _busy = false; }

        RefreshUi();
        StatusText.Text = err ?? "连接成功，可以上传存档了";
    }

    /// <summary>交给系统默认浏览器打开坚果云教程；失败静默</summary>
    private static async void OpenNutstoreHelp()
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(NutstoreHelpUrl)); }
        catch { }
    }

    private void RefreshUi()
    {
        PasswordStateText.Text = string.IsNullOrEmpty(SecretProtector.Load())
            ? "未设置（无法上传）" : "已保存（本机加密存储）";
        SaveButton.IsEnabled = !_busy;
        TestButton.IsEnabled = !_busy;
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
