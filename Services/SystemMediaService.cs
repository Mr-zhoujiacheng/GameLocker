using Windows.Media;
using Windows.Media.Control;

namespace GameLocker.Services;

/// <summary>
/// 系统媒体控制服务（全局静态）：通过 SMTC（GlobalSystemMediaTransportControls）
/// 读取并控制设备当前正在播放的媒体会话（任意播放器：浏览器、音乐 App 等）。
/// 不创建自己的播放会话、不管理音乐文件。
/// 状态变化通过 StateChanged 回调通知 UI（事件已在服务层 marshal 回 UI 线程后触发）。
/// </summary>
public static class SystemMediaService
{
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static GlobalSystemMediaTransportControlsSession? _session;
    private static Task? _init;

    /// <summary>UI 线程调度器（InitAsync 在 UI 线程调用时捕获）。
    /// SMTC 事件可能在后台线程触发，而元数据读取与 BitmapImage.SetSourceAsync 必须在 UI 线程执行，
    /// 故所有事件处理统一 marshal 回 UI 线程（也顺带消除并发竞态）。</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    private static void EnqueueUI(Action a)
    {
        if (_uiQueue == null || !_uiQueue.TryEnqueue(() => a())) a(); // 未就绪则就地执行
    }

    // 曲目信息（WinRT 投影为异步方法，缓存后同步读取）
    private static string _title = "";
    private static string _artist = "";

    /// <summary>会话/曲目/播放状态变化（UI 层自行 marshal 后刷新）</summary>
    public static event Action? StateChanged;

    public static bool HasSession => _session != null;

    /// <summary>当前曲目名（无会话为空）</summary>
    public static string Title => _title;

    /// <summary>当前艺术家（无会话为空）</summary>
    public static string Artist => _artist;

    public static bool IsPlaying
    {
        get
        {
            var info = GetPlaybackInfo();
            return info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
    }

    /// <summary>播放进度（秒）；无会话为 0</summary>
    public static double PositionSeconds
    {
        get
        {
            try { return Math.Clamp(_session?.GetTimelineProperties()?.Position.TotalSeconds ?? 0, 0, 86400); }
            catch { return 0; }
        }
    }

    /// <summary>曲目总时长（秒）；无会话/未知为 0</summary>
    public static double DurationSeconds
    {
        get
        {
            try { return Math.Clamp(_session?.GetTimelineProperties()?.EndTime.TotalSeconds ?? 0, 0, 86400); }
            catch { return 0; }
        }
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackInfo? GetPlaybackInfo()
    {
        try { return _session?.GetPlaybackInfo(); }
        catch { return null; }
    }

    /// <summary>请求管理器并订阅会话切换（幂等，重复调用复用同一 Task）</summary>
    public static Task InitAsync() => _init ??= DoInitAsync();

    private static async Task DoInitAsync()
    {
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => EnqueueUI(() => { AttachSession(); Notify(); });
            AttachSession();
        }
        catch
        {
            // SMTC 不可用（异常环境）：保持无会话状态，磁贴显示"未在播放"
        }
    }

    /// <summary>切换到当前活动会话并重新挂接事件</summary>
    private static void AttachSession()
    {
        if (_session != null)
        {
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.MediaPropertiesChanged -= OnMediaChanged;
        }
        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.MediaPropertiesChanged += OnMediaChanged;
            // 属性+封面异步刷新完成后再广播，避免 UI 拿到旧 Title 导致封面误判
            _ = RefreshAndNotifyAsync();
        }
        else
        {
            _title = "";
            _artist = "";
            ClearCover();
            Notify();
        }
    }

    /// <summary>
    /// 读取曲目元数据缓存，并按需拉取封面，全部完成后才广播。
    /// 必须等属性刷新完再广播：否则 UI 读到旧 Title|Artist，封面判定相同而跳过更新。
    /// </summary>
    private static async Task RefreshAndNotifyAsync()
    {
        var s = _session;
        if (s == null) return;
        Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef = null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(s, _session)) return; // 期间已切换会话，丢弃过期结果
            _title = props.Title ?? "";
            _artist = props.Artist ?? "";
            thumbRef = props.Thumbnail;
        }
        catch { _title = ""; _artist = ""; }

        await LoadCoverAsync(s, $"{_title}|{_artist}", thumbRef);
        Notify();
    }

    // ---- 封面缓存：服务层在曲目变化后主动拉取，UI 只读缓存，无并发竞态 ----
    private static string _coverKey = "";
    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _coverImage;
    private static bool _coverLoading;

    /// <summary>当前封面（无封面或未就绪为 null）</summary>
    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? CoverImage => _coverImage;

    private static void ClearCover()
    {
        _coverKey = "";
        _coverImage = null;
        _coverLoading = false;
    }

    /// <summary>
    /// 拉取封面到缓存。去重规则：同 key 且（正在拉取 或 已有图）才跳过；
    /// 若同 key 但缓存无图，说明封面是晚到的（部分播放器先发标题事件、封面就绪后再发一次），需重试。
    /// 切歌时旧请求结果靠 key 校验自动丢弃。
    /// </summary>
    private static async Task LoadCoverAsync(
        GlobalSystemMediaTransportControlsSession s, string key,
        Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef)
    {
        if (key == _coverKey && (_coverLoading || _coverImage != null)) return;
        _coverKey = key;
        _coverImage = null;
        if (thumbRef == null) return;
        _coverLoading = true;
        try
        {
            var ras = await thumbRef.OpenReadAsync();
            if (!ReferenceEquals(s, _session) || key != _coverKey) return; // 会话/曲目已变，丢弃
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.DecodePixelWidth = 160; // 缩略图级别，降低解码开销
            await bmp.SetSourceAsync(ras);
            if (!ReferenceEquals(s, _session) || key != _coverKey) return;
            _coverImage = bmp;
            Notify(); // 封面晚于标题就绪时立即通知 UI
        }
        catch { /* 解码失败：保持无封面 */ }
        finally { _coverLoading = false; }
    }

    // 播放状态变化才广播；进度（TimelinePropertiesChanged）高频触发，
    // 由主页秒级轮询读取，不广播避免刷屏
    private static void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object e) =>
        EnqueueUI(Notify);

    private static void OnMediaChanged(GlobalSystemMediaTransportControlsSession s,
        MediaPropertiesChangedEventArgs e) => EnqueueUI(() => _ = RefreshAndNotifyAsync());

    /// <summary>播放/暂停切换（无会话时忽略）</summary>
    public static void TogglePlay()
    {
        var s = _session;
        if (s == null) return;
        _ = IsPlaying ? s.TryPauseAsync() : s.TryPlayAsync();
    }

    /// <summary>下一曲</summary>
    public static void Next() => _ = _session?.TrySkipNextAsync();

    /// <summary>上一曲</summary>
    public static void Prev() => _ = _session?.TrySkipPreviousAsync();

    /// <summary>异步读取当前封面为 ImageSource（无会话/无封面/失败返回 null）</summary>
    public static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> GetThumbnailAsync()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(s, _session)) return null; // 期间已切换会话
            var streamRef = props.Thumbnail;
            if (streamRef == null) return null;
            var ras = await streamRef.OpenReadAsync();
            if (!ReferenceEquals(s, _session)) return null;
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.DecodePixelWidth = 128; // 缩略图级别，降低解码开销
            await bmp.SetSourceAsync(ras);
            return bmp;
        }
        catch { return null; }
    }

    private static void Notify() => StateChanged?.Invoke();
}
