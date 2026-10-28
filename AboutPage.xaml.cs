using GameLocker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace GameLocker;

/// <summary>
/// 关于页：软件信息 + 更新设置（检查/下载 GitHub 最新版本、启动自动检查）
/// + 运行日志（默认关闭，开启后复现问题再导出到桌面）+ 作者入口。
/// 手柄：方向=选择条目，A=执行（检查/安装/切换/打开），X=导出日志，View(Select)/B=返回设置主页。
/// </summary>
public sealed partial class AboutPage : Page, ISettingsGamepad
{
    private int _index;
    private bool _ready;

    /// <summary>可导航卡片：检查更新 / 启动检查 / 日志 / GitHub / QQ / 打赏</summary>
    private Button[] _cards = [];

    /// <summary>点在卡片内的「导出」按钮上时，抑制卡片本身的一次点击</summary>
    private bool _suppressCardClick;

    private static readonly SolidColorBrush CursorBrush =
        new(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ClearBrush =
        new(Microsoft.UI.Colors.Transparent);

    /// <summary>本轮会话中找到的新版本（null=已是最新或尚未检查）</summary>
    private UpdateInfo? _update;

    /// <summary>正在检查或下载，防止重复触发</summary>
    private bool _busy;

    /// <summary>正在导出日志，防止重复触发</summary>
    private bool _exporting;

    /// <summary>下载进度轮询：下载在 UpdateService 里后台跑，页面重建后仍能接上进度</summary>
    private DispatcherTimer? _downloadWatch;

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

        AutoCheckToggle.IsOn = SettingsService.Current.AutoCheckUpdates;
        LogToggle.IsOn = SettingsService.Current.DiagnosticLog;

        _cards =
        [
            UpdateCard, AutoCheckCard, LogCard, GithubCard, QqCard, DonateCard
        ];

        // 「导出」按钮宽度跟右侧开关一致（还原），高度比开关低一点，视觉上更紧凑
        LogToggle.SizeChanged += (_, _) =>
        {
            ExportButton.Width = LogToggle.ActualWidth;
            ExportButton.Height = Math.Max(22, LogToggle.ActualHeight - 6);
        };

        // 卡片网格：宽窗口两列并整块居中，侧边栏窄窗口回成一列
        ContentScroll.SizeChanged += (_, _) => LayoutCards();
        Loaded += (_, _) => { LayoutCards(); UpdateCursor(_index); };

        ShowKnownUpdate();
        RestoreDownloadState();
    }

    /// <summary>可否排两列的最小可用宽度</summary>
    private const double TwoColumnMinWidth = 560;

    /// <summary>按可用宽度重排：宽=两列 + 上下居中；窄=单列 + 靠上</summary>
    private void LayoutCards()
    {
        if (LinkGrid is null || ContentScroll is null) return;
        var avail = ContentScroll.ViewportWidth;
        if (avail <= 0) return;
        var two = avail >= TwoColumnMinWidth;

        LinkGrid.ColumnDefinitions[1].Width = two
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        for (var i = 0; i < _cards.Length; i++)
        {
            Grid.SetRow(_cards[i], two ? i / 2 : i);
            Grid.SetColumn(_cards[i], two ? i % 2 : 0);
        }
    }

    /// <summary>未配置的入口显示为提示文字，避免空行</summary>
    private static string Text(string url) => string.IsNullOrWhiteSpace(url) ? "（未配置）" : url;

    /// <summary>把启动时自动检查的结果同步到界面（无需再次联网）</summary>
    private void ShowKnownUpdate()
    {
        if (UpdateService.LatestFound is { } info)
        {
            ApplyUpdate(info);
            return;
        }
        if (UpdateService.AutoChecked)
            UpdateSub.Text = $"已是最新版本 v{FormatVersion(UpdateService.CurrentVersion)}";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        _index = 0;
        FocusIndex(0);

        ShowKnownUpdate();
        // 页面可能被重建：下载中则恢复进度显示，而不是回到"下载并安装"
        RestoreDownloadState();
        // 启动自动检查关闭或尚未完成时，进入关于页补一次静默检查
        if (_update is null && !UpdateService.AutoChecked && SettingsService.Current.AutoCheckUpdates)
            _ = CheckAsync(silent: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ready = false;
        _downloadWatch?.Stop();
    }

    public void HandleGamepad(GamepadButtons buttons)
    {
        if (!_ready) return;

        if (buttons.HasFlag(GamepadButtons.DPadUp)) MoveBy(0, -1);
        else if (buttons.HasFlag(GamepadButtons.DPadDown)) MoveBy(0, 1);
        else if (buttons.HasFlag(GamepadButtons.DPadLeft)) MoveBy(-1, 0);
        else if (buttons.HasFlag(GamepadButtons.DPadRight)) MoveBy(1, 0);
        else if (buttons.HasFlag(GamepadButtons.X))
            _ = ExportAsync();
        else if (buttons.HasFlag(GamepadButtons.A))
            Open(_cards[_index].Tag as string);
    }

    /// <summary>宽窗口两列时左右换列、上下换行；单列时四个方向都是逐项移动</summary>
    private void MoveBy(int dx, int dy)
    {
        var two = LinkGrid.ColumnDefinitions[1].Width.Value > 0;
        if (!two)
        {
            var step = dx != 0 ? dx : dy;
            var nextSingle = Math.Clamp(_index + step, 0, _cards.Length - 1);
            if (nextSingle != _index) FocusIndex(nextSingle);
            return;
        }

        var row = _index / 2;
        var col = _index % 2;
        if (dx != 0)
        {
            var nc = col + dx;
            if (nc < 0 || nc > 1) return;
            var target = row * 2 + nc;
            if (target < _cards.Length) FocusIndex(target);
            return;
        }

        var nr = row + dy;
        var rows = (_cards.Length + 1) / 2;
        if (nr < 0 || nr >= rows) return;
        FocusIndex(Math.Min(nr * 2 + col, _cards.Length - 1));
    }

    /// <summary>移动光标：白框 + 必要时滚动到可见</summary>
    private void FocusIndex(int index)
    {
        _index = index;
        UpdateCursor(index);

        var c = _cards[index];
        try
        {
            var p = c.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var vh = ContentScroll.ViewportHeight;
            if (p.Y < 0)
                ContentScroll.ChangeView(null, Math.Max(0, ContentScroll.VerticalOffset + p.Y - 8), null);
            else if (p.Y + c.ActualHeight > vh)
                ContentScroll.ChangeView(null, ContentScroll.VerticalOffset + p.Y + c.ActualHeight - vh + 8, null);
        }
        catch { }
    }

    /// <summary>给当前卡片画白框</summary>
    private void UpdateCursor(int index)
    {
        for (var i = 0; i < _cards.Length; i++)
            _cards[i].BorderBrush = i == index ? CursorBrush : ClearBrush;
    }

    /// <summary>鼠标/触摸点击卡片直接执行</summary>
    private void OnLinkCardClick(object sender, RoutedEventArgs e)
    {
        // 点在卡片内的「导出」按钮上时，不再执行卡片本身（避免顺带切换日志开关）
        if (_suppressCardClick)
        {
            _suppressCardClick = false;
            return;
        }
        if (sender is not Button card) return;
        var idx = Array.IndexOf(_cards, card);
        if (idx >= 0) FocusIndex(idx);
        Open(card.Tag as string);
    }

    /// <summary>按卡片 Tag 执行对应动作</summary>
    private void Open(string? tag)
    {
        switch (tag)
        {
            case "update":
                _ = CheckOrInstallAsync();
                break;
            case "autocheck":
                ToggleAutoCheck();
                break;
            case "log":
                ToggleLog();
                break;
            case "qq":
                OpenUrl(AboutInfo.QqGroupUrl);
                break;
            case "donate":
                OpenUrl(AboutInfo.DonateUrl);
                break;
            default:
                OpenUrl(AboutInfo.GithubUrl);
                break;
        }
    }

    /// <summary>更新条目：无新版本时检查，有新版本时下载并安装</summary>
    private async Task CheckOrInstallAsync()
    {
        if (_busy) return;
        if (_update is { } info) Install(info);
        else await CheckAsync();
    }

    /// <summary>检查 GitHub 最新版本；silent=true 时不显示进度动画、失败也不改状态文字</summary>
    private async Task CheckAsync(bool silent = false)
    {
        if (_busy) return;
        _busy = true;
        if (!silent)
        {
            UpdateTitle.Text = "检查更新";
            UpdateSub.Text = "正在检查…";
            UpdateProgress.Value = 0;
            UpdateProgress.IsIndeterminate = true;
            UpdateProgress.Visibility = Visibility.Visible;
        }
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                _update = null;
                UpdateTitle.Text = "检查更新";
                UpdateSub.Text = $"已是最新版本 v{FormatVersion(UpdateService.CurrentVersion)}";
            }
            else
            {
                ApplyUpdate(info);
            }
        }
        catch (Exception ex)
        {
            if (!silent) UpdateSub.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyUpdate(UpdateInfo info)
    {
        _update = info;
        UpdateTitle.Text = $"下载并安装 v{FormatVersion(info.Version)}";
        UpdateSub.Text = FirstLine(info.Notes)
                         ?? $"发现新版本（当前 v{FormatVersion(UpdateService.CurrentVersion)}），点按下载并安装";
    }

    /// <summary>
    /// 下载安装包并静默启动安装。下载任务由 UpdateService 单例持有：
    /// 中途切出关于页不会重置进度，也不会重复下载同一个安装包。
    /// </summary>
    private void Install(UpdateInfo info)
    {
        _update = info;
        _busy = true;
        UpdateTitle.Text = $"正在下载 v{FormatVersion(info.Version)}";
        UpdateSub.Text = "开始下载…";
        UpdateProgress.Value = 0;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var task = UpdateService.StartDownloadAsync(info);
            // 错误通过 UpdateService.LastDownloadError 在轮询里显示，这里只避免任务异常未观察
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _busy = false;
            ShowDownloadFailed(info, ex.Message);
            return;
        }
        StartDownloadWatch();
    }

    /// <summary>重新进入关于页时：如果还在下载就恢复进度显示，而不是重置成"下载并安装"</summary>
    private void RestoreDownloadState()
    {
        if (UpdateService.DownloadingInfo is { } info && UpdateService.IsDownloading)
        {
            _update = info;
            _busy = true;
            UpdateTitle.Text = $"正在下载 v{FormatVersion(info.Version)}";
            UpdateProgress.Value = UpdateService.DownloadProgress * 100;
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateSub.Text = $"正在下载… {UpdateService.DownloadProgress * 100:0}%";
            StartDownloadWatch();
            return;
        }
        if (UpdateService.LastDownloadError is { } error && _update is { } failed)
            ShowDownloadFailed(failed, error);
    }

    /// <summary>轮询 UpdateService 的下载进度（页面可能被重建，进度仍保存在服务里）</summary>
    private void StartDownloadWatch()
    {
        _downloadWatch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _downloadWatch.Tick -= OnDownloadWatchTick;
        _downloadWatch.Tick += OnDownloadWatchTick;
        _downloadWatch.Start();
    }

    private void OnDownloadWatchTick(object? sender, object e)
    {
        if (UpdateService.IsDownloading)
        {
            UpdateProgress.Value = UpdateService.DownloadProgress * 100;
            UpdateSub.Text = $"正在下载… {UpdateService.DownloadProgress * 100:0}%";
            return;
        }

        _downloadWatch?.Stop();
        if (UpdateService.LastDownloadError is { } error)
        {
            _busy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            if (_update is { } info) ShowDownloadFailed(info, error);
            return;
        }
        // 下载完成、安装程序已启动（安装结束后程序会自动重启），保持 busy 防止重复触发
        UpdateProgress.Value = 100;
        UpdateSub.Text = "下载完成，正在启动安装程序…";
    }

    private void ShowDownloadFailed(UpdateInfo info, string error)
    {
        _busy = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateTitle.Text = $"重试下载并安装 v{FormatVersion(info.Version)}";
        UpdateSub.Text = "更新失败：" + error;
    }

    /// <summary>切换“启动时检查更新”并立即保存</summary>
    private void ToggleAutoCheck()
    {
        var on = !AutoCheckToggle.IsOn;
        AutoCheckToggle.IsOn = on;
        SettingsService.Update(s => s.AutoCheckUpdates = on);
        if (on && !UpdateService.AutoChecked) _ = CheckAsync(silent: true);
    }

    /// <summary>
    /// 切换「记录运行日志」并立即保存。打开时清空旧日志（导出即本次复现的内容），
    /// 关闭时先写一行再禁用，便于日志里看到用户何时停用。
    /// </summary>
    private void ToggleLog()
    {
        var on = !LogToggle.IsOn;
        LogToggle.IsOn = on;
        if (!on) DebugLog.Write("日志已关闭（用户手动关闭）");
        SettingsService.Update(s => s.DiagnosticLog = on);
        if (on) DebugLog.Reset();
        ExportSub.Text = on
            ? "正在记录，复现问题后点「导出」保存"
            : "默认关闭，复现问题后点「导出」保存";
    }

    /// <summary>点击卡片内的「导出」按钮：只导出，不切换日志开关</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        _suppressCardClick = true;
        // 卡片 Click 会紧随其后同步触发，本轮事件结束后再放开抑制
        DispatcherQueue.TryEnqueue(() => _suppressCardClick = false);
        _ = ExportAsync();
    }

    /// <summary>把运行日志 + 显卡/温度诊断导出成一个 txt 到桌面（默认位置），结果显示在条目说明里</summary>
    private async Task ExportAsync()
    {
        if (_exporting) return;
        _exporting = true;
        ExportSub.Text = "正在导出…";
        try
        {
            var path = await Task.Run(DebugLog.Export);
            ExportSub.Text = path is null
                ? "导出失败：桌面目录不可写"
                : $"已导出到桌面：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ExportSub.Text = "导出失败：" + ex.Message;
        }
        finally
        {
            _exporting = false;
        }
    }

    /// <summary>release 说明取第一行非空文本，去掉 markdown 符号并限长</summary>
    private static string? FirstLine(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        foreach (var raw in notes.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', ' ').Trim();
            if (line.Length == 0) continue;
            return line.Length > 90 ? line[..90] + "…" : line;
        }
        return null;
    }

    private static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";

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
