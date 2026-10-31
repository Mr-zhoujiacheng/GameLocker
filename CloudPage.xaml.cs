using System.Text.Json;
using GameLocker.Models;
using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;

namespace GameLocker;

public sealed partial class CloudPage : Page, ISettingsGamepad
{
    private readonly Control[] _controls;
    private int _index;
    private bool _ready;
    private bool _busy;
    private CancellationTokenSource? _loginCancellation;

    public CloudPage()
    {
        InitializeComponent();
        _controls = [TianyiProviderRadio, BaiduProviderRadio, LoginButton, LogoutButton, RootBox, SaveButton, TestButton];
        if (SettingsService.Current.CloudProvider == CloudProvider.Baidu) BaiduProviderRadio.IsChecked = true;
        else TianyiProviderRadio.IsChecked = true;
        RootBox.Text = CurrentRootFolder;
        RefreshUi();
    }

    private CloudProvider Provider => BaiduProviderRadio.IsChecked == true ? CloudProvider.Baidu : CloudProvider.Tianyi;

    private string CurrentRootFolder => Provider == CloudProvider.Baidu
        ? SettingsService.Current.BaiduRootFolder
        : SettingsService.Current.TianyiRootFolder;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = 0;
        FocusIndex();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ready = false;
        _loginCancellation?.Cancel();
        base.OnNavigatedFrom(e);
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;
        var delta = buttons.HasFlag(GamepadButtons.DPadUp) || buttons.HasFlag(GamepadButtons.DPadLeft) ? -1 :
            buttons.HasFlag(GamepadButtons.DPadDown) || buttons.HasFlag(GamepadButtons.DPadRight) ? 1 : 0;
        if (delta != 0)
        {
            var next = _index + delta;
            while (next >= 0 && next < _controls.Length && !_controls[next].IsEnabled) next += delta;
            if (next >= 0 && next < _controls.Length) { _index = next; FocusIndex(); }
        }
        else if (buttons.HasFlag(GamepadButtons.A) && _controls[_index].IsEnabled)
        {
            if (_index == 0) TianyiProviderRadio.IsChecked = true;
            else if (_index == 1) BaiduProviderRadio.IsChecked = true;
            else if (_index == 2) _ = LoginAsync();
            else if (_index == 3) Logout();
            else if (_index == 4) FocusIndex();
            else if (_index == 5) SaveConfig();
            else if (_index == 6) _ = TestAsync();
        }
    }

    private void FocusIndex()
    {
        var control = _controls[_index];
        FocusRing.FocusWithRing(control);
        try
        {
            var point = control.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point());
            if (point.Y < 0) ContentScroll.ChangeView(null, Math.Max(0, ContentScroll.VerticalOffset + point.Y), null);
            else if (point.Y + control.ActualHeight > ContentScroll.ViewportHeight)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + point.Y + control.ActualHeight - ContentScroll.ViewportHeight + 8, null);
        }
        catch { }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => App.Main?.ShowSettingsHub();
    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveConfig();
    private void OnTestClick(object sender, RoutedEventArgs e) => _ = TestAsync();
    private void OnLoginClick(object sender, RoutedEventArgs e) => _ = LoginAsync();
    private void OnLogoutClick(object sender, RoutedEventArgs e) => Logout();

    private void OnProviderChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SettingsService.Update(s => s.CloudProvider = Provider);
        _loginCancellation?.Cancel();
        QrImage.Source = null;
        QrImage.Visibility = Visibility.Collapsed;
        RootBox.Text = CurrentRootFolder;
        StatusText.Text = "已切换到" + CloudBackupProvider.DisplayName;
        RefreshUi();
    }

    private void SaveConfig()
    {
        var root = RootBox.Text.Trim();
        if (root.Length == 0) root = "GameLocker";
        if (root.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || root is "." or "..")
        {
            StatusText.Text = "请输入单个有效的云端目录名";
            return;
        }
        var baidu = Provider == CloudProvider.Baidu;
        SettingsService.Update(s =>
        {
            if (baidu) s.BaiduRootFolder = root;
            else s.TianyiRootFolder = root;
        });
        RootBox.Text = root;
        StatusText.Text = "云端目录已保存";
    }

    private async Task LoginAsync()
    {
        _loginCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        var ct = cancellation.Token;
        var baidu = Provider == CloudProvider.Baidu;
        var name = baidu ? "百度网盘" : "天翼云";
        StatusText.Text = "正在获取" + name + "二维码…";
        QrImage.Visibility = Visibility.Collapsed;
        try
        {
            if (baidu) await LoginBaiduAsync(ct);
            else await LoginTianyiAsync(ct);
            QrImage.Visibility = Visibility.Collapsed;
            RefreshUi();
            StatusText.Text = name + "登录成功，可以备份存档了";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                DebugLog.Write($"云端登录（{name}）失败：{ex.GetType().Name}: {ex.Message}");
                QrImage.Source = null;
                QrImage.Visibility = Visibility.Collapsed;
                StatusText.Text = "登录失败：" + ex.Message;
            }
        }
        finally { if (ReferenceEquals(_loginCancellation, cancellation)) _loginCancellation = null; }
    }

    private async Task LoginTianyiAsync(CancellationToken ct)
    {
        using var login = new TianyiLoginSession();
        var bytes = await login.CreateQrAsync(ct);
        await ShowQrAsync(bytes, ct);
        StatusText.Text = "请使用天翼云盘 App 扫码登录";
        var account = await login.WaitForLoginAsync(
            text => { if (!ct.IsCancellationRequested) StatusText.Text = text; }, ct);
        ct.ThrowIfCancellationRequested();
        var error = await TianyiCloudService.TestAsync(account);
        ct.ThrowIfCancellationRequested();
        if (error is not null) throw new IOException(error);
        SecretProtector.Save(JsonSerializer.Serialize(account));
    }

    private async Task LoginBaiduAsync(CancellationToken ct)
    {
        using var login = new BaiduLoginSession();
        var bytes = await login.CreateQrAsync(ct);
        await ShowQrAsync(bytes, ct);
        StatusText.Text = "请使用百度网盘 App 扫码登录";
        var account = await login.WaitForLoginAsync(
            text => { if (!ct.IsCancellationRequested) StatusText.Text = text; }, ct);
        ct.ThrowIfCancellationRequested();
        var error = await BaiduCloudService.TestAsync(account);
        ct.ThrowIfCancellationRequested();
        if (error is not null) throw new IOException(error);
        SecretProtector.Save(JsonSerializer.Serialize(account), "baidu");
    }

    private async Task ShowQrAsync(byte[] bytes, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        ct.ThrowIfCancellationRequested();
        QrImage.Source = image;
        QrImage.Visibility = Visibility.Visible;
    }

    private void Logout()
    {
        _loginCancellation?.Cancel();
        try
        {
            var baidu = Provider == CloudProvider.Baidu;
            SecretProtector.Clear(baidu ? "baidu" : "tianyi");
            QrImage.Source = null;
            QrImage.Visibility = Visibility.Collapsed;
            RefreshUi();
            StatusText.Text = "已退出" + (baidu ? "百度网盘" : "天翼云") + "登录，云端已有备份保留";
        }
        catch (Exception ex) { StatusText.Text = "退出登录失败：" + ex.Message; }
    }

    private async Task TestAsync()
    {
        if (_busy) return;
        var baidu = Provider == CloudProvider.Baidu;
        var name = baidu ? "百度网盘" : "天翼云";
        var tianyi = baidu ? null : TianyiCloudService.CurrentAccount();
        var baiduAccount = baidu ? BaiduCloudService.CurrentAccount() : null;
        if (tianyi is null && baiduAccount is null) { StatusText.Text = "请先扫码登录" + name; return; }
        _busy = true;
        RefreshUi();
        StatusText.Text = "正在测试" + name + "连接…";
        try
        {
            var error = baidu
                ? await BaiduCloudService.TestAsync(baiduAccount!)
                : await TianyiCloudService.TestAsync(tianyi!);
            StatusText.Text = error ?? "连接成功，可以上传存档了";
        }
        catch (Exception ex) { StatusText.Text = "连接失败：" + ex.Message; }
        finally { _busy = false; RefreshUi(); }
    }

    private void RefreshUi()
    {
        var baidu = Provider == CloudProvider.Baidu;
        var name = baidu ? "百度网盘" : "天翼云";
        IntroText.Text = $"使用{(baidu ? "百度网盘" : "天翼云盘")} App 扫码登录，存档按游戏分目录上传。登录会话加密保存在本机。";
        AccountTitle.Text = name + "账号";
        RootHint.Text = $"存档会保存到{(baidu ? "百度网盘" : "天翼云盘")} /该目录/游戏名/ 下。";

        var user = baidu ? BaiduCloudService.CurrentAccount()?.User : TianyiCloudService.CurrentAccount()?.User;
        AccountText.Text = user is null ? "未登录" : "已登录：" + user;
        LogoutButton.IsEnabled = user is not null;
        TestButton.IsEnabled = !_busy && user is not null;
    }

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}
