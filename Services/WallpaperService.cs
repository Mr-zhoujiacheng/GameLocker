using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameLocker.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameLocker.Services;

/// <summary>
/// 壁纸小组件的在线壁纸服务（全局静态）：从 Bing 每日壁纸 / Picsum 随机壁纸拉取图片，
/// 下载到本地缓存目录后按设置速度轮番切换；收藏的壁纸出现得更频繁，排除的壁纸不再出现。
/// 同时负责把当前壁纸真正设为桌面壁纸（SystemParametersInfo）或锁屏壁纸（PersonalizationCSP 注册表）。
/// </summary>
public static class WallpaperService
{
    /// <summary>本地壁纸缓存目录（%APPDATA%\GameLocker\wallpapers）</summary>
    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "wallpapers");

    /// <summary>支持的图片扩展名</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>每个来源最多保留的本地壁纸数量（收藏的不清理）</summary>
    private const int MaxCache = 24;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>单个壁纸小组件的运行状态（按 Target 区分）</summary>
    private sealed class State
    {
        public string Source = "bing";
        public int IntervalSeconds = 600;
        public int Elapsed;
        public List<string> Files = [];
        public int Index;
        public HashSet<string> Liked = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase);
        public bool Busy;
        public string LastError = "";
        public DateTime RetryAt;
    }

    private static readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    private static DispatcherTimer? _timer;
    private static DispatcherQueue? _uiQueue;

    /// <summary>当前壁纸发生变化 / 下载状态变化（UI 线程触发；主页据此刷新磁贴）</summary>
    public static event Action? Changed;

    /// <summary>轮播速度档位 → 间隔秒数（壁纸偏慢：快 1 分钟 / 慢 10 分钟 / 长 30 分钟）</summary>
    public static int IntervalOf(PhotoSpeed speed) => speed switch
    {
        PhotoSpeed.Fast => 60,
        PhotoSpeed.Slow => 600,
        _ => 1800
    };

    private static string KeyOf(LaunchItem item) =>
        string.IsNullOrWhiteSpace(item.Target) ? "widget:wallpaper" : item.Target;

    private static string SourceOf(LaunchItem item) =>
        string.Equals(item.WallpaperSource, "picsum", StringComparison.OrdinalIgnoreCase) ? "picsum" : "bing";

    /// <summary>来源显示名（磁贴信息条使用）</summary>
    public static string SourceName(LaunchItem item) =>
        SourceOf(item) == "picsum" ? "Picsum 随机壁纸" : "Bing 每日壁纸";

    /// <summary>启动轮播定时器（幂等，主页 OnNavigatedTo 调用；需在 UI 线程）</summary>
    public static void Start()
    {
        _uiQueue ??= DispatcherQueue.GetForCurrentThread();
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>停止定时器（离开主页时调用，避免后台空转）</summary>
    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>释放已解码图片缓存（需在 UI 线程调用）</summary>
    public static void ReleaseImageCache() => _imageCache.Clear();

    /// <summary>注册一个壁纸小组件（主页每次排布 / 编辑保存后调用）：同步来源与速度，必要时补下载</summary>
    public static void Register(LaunchItem item)
    {
        var key = KeyOf(item);
        if (!_states.TryGetValue(key, out var st))
        {
            st = new State();
            _states[key] = st;
        }
        var source = SourceOf(item);
        var interval = IntervalOf(item.SlideshowSpeed);
        var sourceChanged = st.Source != source;
        var prefsChanged = !st.Liked.SetEquals(item.WallpaperLiked)
            || !st.Blocked.SetEquals(item.WallpaperBlocked);
        st.Source = source;
        st.IntervalSeconds = interval;
        st.Liked = new HashSet<string>(item.WallpaperLiked, StringComparer.OrdinalIgnoreCase);
        st.Blocked = new HashSet<string>(item.WallpaperBlocked, StringComparer.OrdinalIgnoreCase);
        if (sourceChanged || prefsChanged) Rescan(st);
        if (st.Files.Count == 0 && !st.Busy) _ = FillAsync(st);
        else if (!st.Busy && st.Files.Count - st.Index <= 1) _ = FillAsync(st);
    }

    /// <summary>当前轮播到的壁纸文件（没有缓存返回 null）</summary>
    public static string? CurrentFile(LaunchItem item)
    {
        var st = _states.GetValueOrDefault(KeyOf(item));
        if (st is null || st.Files.Count == 0) return null;
        return st.Files[Math.Clamp(st.Index, 0, st.Files.Count - 1)];
    }

    /// <summary>当前壁纸解码后的图片（磁贴/预览使用）</summary>
    public static ImageSource? CurrentImage(LaunchItem item) => ToImage(CurrentFile(item));

    /// <summary>无图时的占位文案（有图返回空串）</summary>
    public static string Placeholder(LaunchItem item)
    {
        var st = _states.GetValueOrDefault(KeyOf(item));
        if (st is null) return "壁纸\n正在准备…";
        if (st.Files.Count > 0) return "";
        if (st.LastError.Length > 0) return "壁纸获取失败\n" + st.LastError;
        return st.Busy ? "壁纸\n正在下载…" : "壁纸\n还没有本地壁纸";
    }

    /// <summary>当前壁纸的信息（磁贴选中时显示：来源 · 第几张/共几张 · 本地缓存数）</summary>
    public static string InfoOf(LaunchItem item)
    {
        var st = _states.GetValueOrDefault(KeyOf(item));
        if (st is null) return SourceName(item);
        var total = st.Files.Count;
        if (total == 0)
            return st.LastError.Length > 0 ? "获取失败：" + st.LastError : "正在下载壁纸…";
        return $"{SourceName(item)} · 第 {Math.Clamp(st.Index + 1, 1, total)}/{total} 张 · 本地 {CachedCount(item)} 张";
    }

    /// <summary>该小组件本地已缓存的壁纸数量（按当前来源统计）</summary>
    public static int CachedCount(LaunchItem item)
    {
        var source = SourceOf(item);
        try
        {
            if (!Directory.Exists(CacheDir)) return 0;
            return Directory.GetFiles(CacheDir, source + "_*")
                .Count(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    /// <summary>"下载到本地"：按设置补下载若干张新壁纸（返回新增数量与结果说明）</summary>
    public static async Task<(int Added, string Message)> DownloadMoreAsync(LaunchItem item, int count)
    {
        Register(item);
        var key = KeyOf(item);
        var st = _states.GetValueOrDefault(key);
        if (st is null) return (0, "壁纸小组件未初始化");
        if (st.Busy) return (0, "正在下载，请稍后…");

        st.Busy = true;
        RaiseChanged();
        var added = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var file = await FetchAsync(st);
                if (file is null) break;
                added++;
                st.LastError = "";
            }
        }
        finally
        {
            st.Busy = false;
            Rescan(st);
            RaiseChanged();
        }
        var total = CachedCount(item);
        if (added > 0) return (added, $"已下载 {added} 张，本地共 {total} 张");
        return (0, st.LastError.Length > 0 ? st.LastError : "没有可下载的新壁纸");
    }

    /// <summary>"清除缓存"：删除本地缓存目录里的壁纸，清空轮播池后重新拉取（收藏/排除名单保留）</summary>
    public static (int Removed, string Message) ClearCache(LaunchItem item)
    {
        var removed = 0;
        try
        {
            if (Directory.Exists(CacheDir))
                foreach (var f in Directory.GetFiles(CacheDir))
                {
                    if (!ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        File.Delete(f);
                        _imageCache.Remove(f);
                        removed++;
                    }
                    catch { /* 被占用：跳过这一张 */ }
                }
        }
        catch (Exception ex)
        {
            return (removed, "清除缓存失败：" + ex.Message);
        }

        foreach (var st in _states.Values)
        {
            st.Files = [];
            st.Index = 0;
            st.Elapsed = 0;
            st.RetryAt = default;
            st.LastError = "";
        }
        RaiseChanged();
        var key = KeyOf(item);
        var state = _states.GetValueOrDefault(key);
        if (state is not null && !state.Busy) _ = FillAsync(state);
        return (removed, $"已清除 {removed} 张缓存壁纸");
    }

    /// <summary>把指定图片设为桌面壁纸或锁屏壁纸（桌面走 SPI，锁屏走 HKCU PersonalizationCSP）</summary>
    public static (bool Ok, string Message) Apply(string file, bool lockScreen)
    {
        try
        {
            if (!File.Exists(file)) return (false, "壁纸文件不存在，请稍后再试");
            return lockScreen ? SetLockScreen(file) : SetDesktop(file);
        }
        catch (Exception ex)
        {
            return (false, "设置壁纸失败：" + ex.Message);
        }
    }

    /// <summary>定时推进：按每个小组件自己的速度换下一张，并在快用完时补下载</summary>
    private static void Tick()
    {
        var changed = false;
        foreach (var st in _states.Values)
        {
            if (st.Files.Count == 0) continue;
            st.Elapsed++;
            if (st.Elapsed < st.IntervalSeconds) continue;
            st.Elapsed = 0;
            st.Index = (st.Index + 1) % st.Files.Count;
            changed = true;
            if (st.Files.Count - st.Index <= 1 && !st.Busy) _ = FillAsync(st);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>补下载：池子里不足 6 张时再取几张（最多 4 次），失败后 60 秒内不再重试</summary>
    private static async Task FillAsync(State st)
    {
        if (st.Busy || DateTime.UtcNow < st.RetryAt) return;
        st.Busy = true;
        RaiseChanged();
        var added = 0;
        try
        {
            for (var i = 0; i < 4 && st.Files.Count + added < 6; i++)
            {
                var file = await FetchAsync(st);
                if (file is null) break;
                added++;
            }
            if (st.LastError.Length > 0) st.RetryAt = DateTime.UtcNow.AddSeconds(60);
        }
        finally
        {
            st.Busy = false;
            Rescan(st);
            RaiseChanged();
        }
    }

    /// <summary>按来源扫描本地缓存，过滤排除项，并让收藏项在池子里出现两次</summary>
    private static void Rescan(State st)
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(CacheDir))
                foreach (var f in Directory.GetFiles(CacheDir, st.Source + "_*"))
                {
                    var name = Path.GetFileName(f);
                    if (!ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                    if (st.Blocked.Contains(name)) continue;
                    list.Add(f);
                }
        }
        catch { /* 无权限：保持空列表 */ }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        var pool = new List<string>(list);
        foreach (var f in list)
            if (st.Liked.Contains(Path.GetFileName(f))) pool.Add(f);
        st.Files = pool;
        if (st.Index >= pool.Count) st.Index = 0;
    }

    /// <summary>取候选列表并下载第一张尚未缓存的图片，成功返回本地路径</summary>
    private static async Task<string?> FetchAsync(State st)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var candidates = await GetCandidatesAsync(st);
            if (candidates.Count == 0)
            {
                if (st.LastError.Length == 0) st.LastError = "没有可用的新壁纸";
                return null;
            }
            foreach (var (url, name) in candidates)
            {
                var dest = Path.Combine(CacheDir, st.Source + "_" + name);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                using var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) { st.LastError = $"HTTP {resp.StatusCode}"; continue; }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0) continue;
                var tmp = dest + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, dest, true);
                TrimCache(st);
                return dest;
            }
            if (st.LastError.Length == 0) st.LastError = "没有可用的新壁纸";
            return null;
        }
        catch (Exception ex)
        {
            st.LastError = ex.InnerException?.Message ?? ex.Message;
            return null;
        }
    }

    /// <summary>候选壁纸列表（URL + 本地文件名）；Bing 取每日榜单，Picsum 随机分页取图</summary>
    private static async Task<List<(string Url, string Name)>> GetCandidatesAsync(State st)
    {
        var list = new List<(string, string)>();
        if (st.Source == "picsum")
        {
            // Picsum（Lorem Picsum）：分页随机取一组图片 ID，再按 1920×1080 下载
            var page = Random.Shared.Next(1, 21);
            var url = $"https://picsum.photos/v2/list?page={page}&limit=20";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) { st.LastError = $"HTTP {resp.StatusCode}"; return list; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    list.Add(($"https://picsum.photos/id/{id}/1920/1080", id + ".jpg"));
                }
            }
        }
        else
        {
            var url = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=zh-CN";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) { st.LastError = $"HTTP {resp.StatusCode}"; return list; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in images.EnumerateArray())
                {
                    if (!el.TryGetProperty("urlbase", out var ub)) continue;
                    var urlbase = ub.GetString();
                    if (string.IsNullOrWhiteSpace(urlbase)) continue;
                    var raw = urlbase.Contains("id=") ? urlbase[(urlbase.IndexOf("id=") + 3)..] : urlbase.Trim('/');
                    var name = Sanitize(raw) + "_UHD.jpg";
                    list.Add(("https://www.bing.com" + urlbase + "_UHD.jpg", name));
                }
            }
        }
        return list;
    }

    /// <summary>清理超出缓存上限的旧壁纸（收藏的不清理）</summary>
    private static void TrimCache(State st)
    {
        try
        {
            var files = Directory.GetFiles(CacheDir, st.Source + "_*");
            if (files.Length <= MaxCache) return;
            var excess = files.OrderBy(File.GetLastWriteTime).Take(files.Length - MaxCache);
            foreach (var f in excess)
                if (!st.Liked.Contains(Path.GetFileName(f)))
                    try
                    {
                        File.Delete(f);
                        _imageCache.Remove(f);
                    }
                    catch { }
        }
        catch { }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace('?', '_').Replace('&', '_').Replace('/', '_').Replace('\\', '_');
    }

    private static ImageSource? ToImage(string? file)
    {
        if (file is null) return null;
        if (_imageCache.TryGetValue(file, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            if (new FileInfo(file).Length > 0)
                img = new BitmapImage { DecodePixelWidth = 480, UriSource = new Uri(file) };
        }
        catch { img = null; }
        if (_imageCache.Count >= 32) _imageCache.Remove(_imageCache.Keys.First());
        _imageCache[file] = img;
        return img;
    }

    private static void RaiseChanged()
    {
        if (_uiQueue is null) { Changed?.Invoke(); return; }
        _uiQueue.TryEnqueue(() => Changed?.Invoke());
    }

    // ---- 系统壁纸设置 ----

    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    private static (bool, string) SetDesktop(string file)
    {
        // 填充模式 = 铺满（WallpaperStyle 10 + 不平铺）
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                   @"Control Panel\Desktop", writable: true))
        {
            key?.SetValue("WallpaperStyle", "10", Microsoft.Win32.RegistryValueKind.String);
            key?.SetValue("TileWallpaper", "0", Microsoft.Win32.RegistryValueKind.String);
        }
        if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, file, SpifUpdateIniFile | SpifSendChange))
            return (false, "设置桌面壁纸失败");
        return (true, "已设为桌面壁纸");
    }

    private static (bool, string) SetLockScreen(string file)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\PersonalizationCSP");
        if (key is null) return (false, "无法写入锁屏壁纸设置");
        key.SetValue("LockScreenImagePath", file, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("LockScreenImageUrl", file, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("LockScreenImageStatus", 1, Microsoft.Win32.RegistryValueKind.DWord);
        return (true, "已设为锁屏壁纸");
    }
}
