using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using MediaSessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace GameLocker.Services;

/// <summary>在主页可见时观察系统媒体会话，并按需控制当前播放器。</summary>
public static class SystemMediaService
{
    // 观察与缓存由 UI 线程管理。WinRT 回调只向所属观察周期的队列投递。
    private sealed class Observation(DispatcherQueue queue, bool includeTrackDetails)
    {
        public bool IncludeTrackDetails { get; } = includeTrackDetails;
        public MediaSessionManager? Manager { get; set; }
        public MediaSession? Session { get; set; }
        public Task? Initialization { get; set; }
        public long TrackRevision { get; set; }

        public void OnCurrentSessionChanged(MediaSessionManager manager, CurrentSessionChangedEventArgs args) =>
            Enqueue(() => AttachSession(this));

        public void OnPlaybackChanged(MediaSession session, PlaybackInfoChangedEventArgs args) =>
            Enqueue(() => { if (ReferenceEquals(session, Session)) Notify(); });

        public void OnMediaChanged(MediaSession session, MediaPropertiesChangedEventArgs args) =>
            Enqueue(() => { if (ReferenceEquals(session, Session)) _ = RefreshAndNotifyAsync(this, session); });

        private void Enqueue(Action action) => queue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_observation, this)) action();
        });
    }

    private static Task<MediaSessionManager>? _managerRequest;
    private static Observation? _observation;
    private static string _title = "";
    private static string _artist = "";
    private static BitmapImage? _coverImage;
    private static MediaSession? Session => _observation?.Session;

    /// <summary>会话、曲目或播放状态在 UI 线程上通知。</summary>
    public static event Action? StateChanged;

    public static bool HasSession => Session is not null;
    public static string Title => _title;
    public static string Artist => _artist;
    public static BitmapImage? CoverImage => _coverImage;

    public static bool IsPlaying => IsSessionPlaying(Session);

    private static bool IsSessionPlaying(MediaSession? session)
    {
        try
        {
            return session?.GetPlaybackInfo()?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return false; }
    }

    /// <summary>播放进度（秒）；无会话为 0。</summary>
    public static double PositionSeconds
    {
        get
        {
            try { return Math.Clamp(Session?.GetTimelineProperties()?.Position.TotalSeconds ?? 0, 0, 86400); }
            catch { return 0; }
        }
    }

    /// <summary>曲目总时长（秒）；无会话或未知为 0。</summary>
    public static double DurationSeconds
    {
        get
        {
            try { return Math.Clamp(Session?.GetTimelineProperties()?.EndTime.TotalSeconds ?? 0, 0, 86400); }
            catch { return 0; }
        }
    }

    /// <summary>在 UI 线程开始或恢复观察。仅猫娘需要播放状态时可跳过曲目和封面。</summary>
    public static Task InitAsync(bool includeTrackDetails = true)
    {
        if (_observation is { } active && active.IncludeTrackDetails == includeTrackDetails)
            return active.Initialization ?? Task.CompletedTask;
        if (DispatcherQueue.GetForCurrentThread() is not { } queue) return Task.CompletedTask;

        Pause();
        var observation = new Observation(queue, includeTrackDetails);
        _observation = observation;
        return observation.Initialization = InitializeAsync(observation);
    }

    /// <summary>在 UI 线程暂停观察并释放缓存。播放控制命令仍可读取当前系统会话。</summary>
    public static void Pause()
    {
        if (_observation is not { } observation) return;
        _observation = null;
        ClearTrack();
        DetachSession(observation);
        if (observation.Manager is { } manager)
        {
            try { manager.CurrentSessionChanged -= observation.OnCurrentSessionChanged; }
            catch { /* 系统媒体管理器可能已经断开。 */ }
        }
    }

    private static async Task<MediaSessionManager> RequestManagerAsync() => await MediaSessionManager.RequestAsync();

    private static async Task<MediaSessionManager?> GetManagerAsync()
    {
        var request = _managerRequest ??= RequestManagerAsync();
        try { return await request; }
        catch
        {
            if (ReferenceEquals(request, _managerRequest)) _managerRequest = null;
            return null;
        }
    }

    private static async Task InitializeAsync(Observation observation)
    {
        var manager = await GetManagerAsync();
        if (!ReferenceEquals(_observation, observation)) return;
        observation.Manager = manager;
        try
        {
            if (manager is not null)
            {
                manager.CurrentSessionChanged += observation.OnCurrentSessionChanged;
                AttachSession(observation);
                return;
            }
        }
        catch { /* SMTC 不可用时维持无会话状态。 */ }
        if (!ReferenceEquals(_observation, observation)) return;
        Pause();
        Notify();
    }

    private static void AttachSession(Observation observation)
    {
        MediaSession? nextSession;
        try { nextSession = observation.Manager?.GetCurrentSession(); }
        catch { nextSession = null; }
        if (nextSession is not null && ReferenceEquals(nextSession, observation.Session)) return;

        DetachSession(observation);
        ClearTrack();
        if (nextSession is not null)
        {
            observation.Session = nextSession;
            try
            {
                nextSession.PlaybackInfoChanged += observation.OnPlaybackChanged;
                if (observation.IncludeTrackDetails)
                {
                    nextSession.MediaPropertiesChanged += observation.OnMediaChanged;
                    _ = RefreshAndNotifyAsync(observation, nextSession);
                }
            }
            catch { DetachSession(observation); }
        }
        Notify();
    }

    private static void DetachSession(Observation observation)
    {
        observation.TrackRevision++;
        if (observation.Session is not { } session) return;
        observation.Session = null;
        try { session.PlaybackInfoChanged -= observation.OnPlaybackChanged; }
        catch { /* 会话可能已经关闭。 */ }
        if (!observation.IncludeTrackDetails) return;
        try { session.MediaPropertiesChanged -= observation.OnMediaChanged; }
        catch { /* 会话可能已经关闭。 */ }
    }

    private static void ClearTrack()
    {
        _title = "";
        _artist = "";
        _coverImage = null;
    }

    private static bool IsCurrent(Observation observation, MediaSession session, long revision) =>
        ReferenceEquals(observation, _observation) && ReferenceEquals(session, observation.Session) &&
        revision == observation.TrackRevision;

    private static async Task RefreshAndNotifyAsync(Observation observation, MediaSession session)
    {
        var revision = ++observation.TrackRevision;
        string title = "", artist = "";
        IRandomAccessStreamReference? thumbnail = null;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (!IsCurrent(observation, session, revision)) return;
            title = properties?.Title ?? "";
            artist = properties?.Artist ?? "";
            thumbnail = properties?.Thumbnail;
        }
        catch { /* 无法读取曲目信息时保留空状态。 */ }
        if (!IsCurrent(observation, session, revision)) return;

        var cover = title == _title && artist == _artist ? _coverImage : null;
        if (cover is null && thumbnail is not null)
        {
            cover = await DecodeCoverAsync(thumbnail, 160, () => IsCurrent(observation, session, revision));
            if (!IsCurrent(observation, session, revision)) return;
        }
        _title = title;
        _artist = artist;
        _coverImage = cover;
        Notify();
    }

    private static async Task<BitmapImage?> DecodeCoverAsync(IRandomAccessStreamReference thumbnail, int width,
        Func<bool> isCurrent)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (!isCurrent()) return null;
            var image = new BitmapImage { DecodePixelWidth = width };
            await image.SetSourceAsync(stream);
            return isCurrent() ? image : null;
        }
        catch { return null; }
    }

    public static void TogglePlay() => _ = SendCommandAsync(session =>
        IsSessionPlaying(session) ? session.TryPauseAsync() : session.TryPlayAsync());

    public static void Next() => _ = SendCommandAsync(session => session.TrySkipNextAsync());

    public static void Prev() => _ = SendCommandAsync(session => session.TrySkipPreviousAsync());

    private static async Task SendCommandAsync(Func<MediaSession, IAsyncOperation<bool>> command)
    {
        try
        {
            var manager = await GetManagerAsync();
            if (manager?.GetCurrentSession() is { } session) await command(session);
        }
        catch { /* 播放器可能在按键后关闭，忽略已经失效的命令。 */ }
    }

    /// <summary>异步读取所观察会话的当前封面。</summary>
    public static async Task<BitmapImage?> GetThumbnailAsync()
    {
        if (_observation is not { Session: { } session } observation) return null;
        var revision = observation.TrackRevision;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (!IsCurrent(observation, session, revision) || properties?.Thumbnail is not { } thumbnail) return null;
            var image = await DecodeCoverAsync(thumbnail, 128, () => IsCurrent(observation, session, revision));
            return IsCurrent(observation, session, revision) ? image : null;
        }
        catch { return null; }
    }

    private static void Notify() => StateChanged?.Invoke();
}
