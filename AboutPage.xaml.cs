using GameLocker.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 关于页：软件信息 + 作者入口（GitHub 仓库 / QQ 群聊 / 打赏支持）。
/// 手柄：方向=选择入口，A=打开，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class AboutPage : Page, ISettingsGamepad
{
    private int _index;
    private bool _ready;

    public AboutPage()
    {
        InitializeComponent();

        var v = typeof(AboutPage).Assembly.GetName().Version;
        VersionText.Text = $"版本 {v?.Major ?? 1}.{v?.Minor ?? 0}.{v?.Build ?? 0}";

        // 非打包应用按输出目录直接取图标；文件缺失或解码失败时静默不显示
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "GameLocker-256.png");
            if (File.Exists(icon)) AppIconImage.Source = new BitmapImage(new Uri(icon));
        }
        catch { }

        GithubSub.Text = Text(AboutInfo.GithubUrl);
        QqSub.Text = AboutInfo.QqGroupNo.Length > 0
            ? $"群号 {AboutInfo.QqGroupNo}"
            : Text(AboutInfo.QqGroupUrl);
        DonateSub.Text = "你的支持就是我的动力！";
        CopyrightText.Text = $"© 2026 {AboutInfo.Author}  保留所有权利";
    }

    /// <summary>未配置的入口显示为提示文字，避免空行</summary>
    private static string Text(string url) => string.IsNullOrWhiteSpace(url) ? "（未配置）" : url;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = LinkList.SelectedIndex < 0 ? 0 : LinkList.SelectedIndex;
        LinkList.SelectedIndex = _index;
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
            Open(_index);
    }

    private void Move(int delta)
    {
        var count = LinkList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(_index + delta, 0, count - 1);
        if (next == _index) return;
        _index = next;
        LinkList.SelectedIndex = next;
        FocusIndex(next);
    }

    private void FocusIndex(int i)
    {
        if (LinkList.ContainerFromIndex(i) is ListViewItem item)
            item.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
    }

    /// <summary>鼠标/触摸点击条目直接打开（Tapped 对鼠标与触屏都可靠触发）</summary>
    private void OnLinkTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var idx = LinkList.Items.IndexOf(sender);
        if (idx >= 0)
        {
            _index = idx;
            LinkList.SelectedIndex = idx;
            Open(idx);
        }
    }

    private void Open(int index)
    {
        var url = index switch
        {
            0 => AboutInfo.GithubUrl,
            1 => AboutInfo.QqGroupUrl,
            _ => AboutInfo.DonateUrl
        };
        OpenUrl(url);
    }

    /// <summary>交给系统默认浏览器/应用打开；链接未配置或非法时静默忽略</summary>
    private static async void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); }
        catch { }
    }

    private void OnBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        App.Main?.ShowSettingsHub();

    public bool IsCapturing => false;
    public void CaptureButtons(GamepadButtons pressed) { }
    public void NotifyAllReleased() { }
}

/// <summary>
/// 关于页展示的作者信息。改这里即可（留空则页面显示「（未配置）」）：
/// GitHub 仓库地址、QQ 群号与加群链接、打赏跳转链接、作者署名。
/// </summary>
internal static class AboutInfo
{
    public const string Author = "V";

    /// <summary>GitHub 仓库地址</summary>
    public const string GithubUrl = "https://github.com/Mr-zhoujiacheng/GameLocker";

    /// <summary>QQ 群号（仅展示用，可留空）</summary>
    public const string QqGroupNo = "1109469118";

    /// <summary>QQ 加群链接（点击跳转，可留空）</summary>
    public const string QqGroupUrl =
        "https://qun.qq.com/universal-share/share?ac=1" +
        "&authKey=0vDImrfZWREe3%2Bj4NBiLBrD41v1YG4JD763v1SJBX4SdZUiFJk0ByM272XbwRUBa" +
        "&busi_data=eyJncm91cENvZGUiOiIxMTA5NDY5MTE4IiwidG9rZW4iOiJhZTZDdWVIU09EK21va3hmalFqOC9aNTN6QXRrdEhpR2VEZUU3UThTdy9aQmRkd0ptcHRaUU8rcmxEdWU3dFFYIiwidWluIjoiMTI4MDMyNDIwNSJ9" +
        "&data=DJLSbt2o1eIocRPUQjMLoW6HbmHDqk4oXvij7xdehJC3e6FcsjJodTIAz4k9zjKjl1nfYzlOw9S6CXqRdtNKSw" +
        "&svctype=4&tempid=h5_group_info";

    /// <summary>打赏跳转链接（爱发电 / 赞赏码页面等）</summary>
    public const string DonateUrl = "https://afdian.com/a/MRZhou";
}
