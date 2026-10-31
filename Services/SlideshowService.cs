using GameLocker.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameLocker.Services;

/// <summary>
/// 图片组小组件的轮播服务（全局静态）：按文件夹扫描图片，按各自速度档位定时轮换当前图片。
/// 每个文件夹独立一条轮播序列（同一文件夹的多个磁贴共享当前图，不同文件夹互不影响）。
/// 扫描在后台线程执行；当前图变化通过 CurrentChanged 回调（UI 线程）通知刷新。
/// </summary>
public static class SlideshowService
{
    /// <summary>支持的图片扩展名</summary>
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    /// <summary>轮播速度档位 → 间隔秒数</summary>
    public static int IntervalOf(PhotoSpeed speed) => speed switch
    {
        PhotoSpeed.Slow => 180,  // 慢：3 分钟
        PhotoSpeed.Art => 1800,  // 艺术风格：30 分钟
        _ => 30                  // 快：30 秒
    };

    private sealed record FolderState(List<string> Files, int Index, int IntervalSeconds,
        int Elapsed = 0, bool Scanned = false);

    private static readonly Dictionary<string, FolderState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已解码图片缓存（key=文件路径；切图时避免重复解码）</summary>
    private static readonly Dictionary<string, ImageSource?> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    private static DispatcherTimer? _timer;
    private static DispatcherQueue? _uiQueue;

    /// <summary>任一文件夹的当前图片发生变化（UI 线程触发）</summary>
    public static event Action? CurrentChanged;

    /// <summary>启动轮播定时器（幂等，主页 OnNavigatedTo 调用；需在 UI 线程）。
    /// 固定 1 秒 tick，按各文件夹自己的间隔秒数累计推进，支持每磁贴不同速度</summary>
    public static void Start()
    {
        _uiQueue ??= DispatcherQueue.GetForCurrentThread();
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickAll();
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

    /// <summary>注册一个轮播文件夹（首次注册时后台扫描；已注册则只更新速度档位，主页每次排布调用）</summary>
    public static void Register(string folder, int intervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        if (_states.TryGetValue(folder, out var st))
        {
            // 速度档位可能在编辑里改过：同步间隔并清零计时，使新速度立即生效
            if (st.IntervalSeconds != intervalSeconds)
                _states[folder] = st with { IntervalSeconds = intervalSeconds, Elapsed = 0 };
            return;
        }
        _states[folder] = new FolderState([], 0, intervalSeconds);
        _ = ScanAsync(folder);
    }

    /// <summary>该文件夹是否已完成扫描（false=尚未注册或扫描中；磁贴占位文案据此区分）</summary>
    public static bool IsScanned(string folder) =>
        _states.TryGetValue(folder, out var st) && st.Scanned;

    /// <summary>文件夹对应的当前图片（未扫描到/目录为空返回 null）</summary>
    public static ImageSource? CurrentImage(string folder)
    {
        if (!_states.TryGetValue(folder, out var st) || st.Files.Count == 0) return null;
        var file = st.Files[Math.Clamp(st.Index, 0, st.Files.Count - 1)];
        return GetImage(file);
    }

    /// <summary>强制重新扫描（编辑更换文件夹后调用）</summary>
    public static void Rescan(string folder, int intervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        _states[folder] = new FolderState([], 0, intervalSeconds);
        _ = ScanAsync(folder);
    }

    /// <summary>手动切换当前选中磁贴的图片（主页 A/LB/RB）：dir=1 下一张，-1 上一张</summary>
    public static void Skip(string folder, int dir)
    {
        if (!_states.TryGetValue(folder, out var st) || st.Files.Count == 0) return;
        var n = st.Files.Count;
        _states[folder] = st with { Index = ((st.Index + dir) % n + n) % n, Elapsed = 0 };
        CurrentChanged?.Invoke();
    }

    private static void TickAll()
    {
        var changed = false;
        foreach (var (folder, st) in _states)
        {
            if (st.Files.Count == 0) continue;
            var elapsed = st.Elapsed + 1;
            if (elapsed < st.IntervalSeconds)
            {
                _states[folder] = st with { Elapsed = elapsed };
                continue;
            }
            _states[folder] = st with { Elapsed = 0, Index = (st.Index + 1) % st.Files.Count };
            changed = true;
        }
        if (changed) CurrentChanged?.Invoke();
    }

    private static async Task ScanAsync(string folder)
    {
        List<string> files;
        try
        {
            files = await Task.Run(() =>
            {
                var list = new List<string>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(folder))
                        if (ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                            && !IsHidden(f))
                            list.Add(f);
                }
                catch { /* 无权限：保持空列表 */ }
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            });
        }
        catch { return; }

        // 期间文件夹被移除则丢弃
        if (!_states.TryGetValue(folder, out var cur)) return;
        _states[folder] = cur with { Files = files, Index = 0, Elapsed = 0, Scanned = true };
        _uiQueue?.TryEnqueue(() => CurrentChanged?.Invoke());
    }

    private static bool IsHidden(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
        catch { return false; }
    }

    private static ImageSource? GetImage(string file)
    {
        if (_imageCache.TryGetValue(file, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            if (new FileInfo(file).Length > 0)
            {
                img = new BitmapImage { DecodePixelWidth = 320, UriSource = new Uri(file) };
            }
        }
        catch { img = null; }
        if (_imageCache.Count >= 32) _imageCache.Remove(_imageCache.Keys.First());
        _imageCache[file] = img;
        return img;
    }
}