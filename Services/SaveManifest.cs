using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace GameLocker.Services;

/// <summary>清单里一款游戏的存档相关信息（精简后）</summary>
public sealed class SaveManifestEntry
{
    /// <summary>命中 <c>tags: [save]</c> 且 Windows 可用的存档路径（含占位符，未展开）</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>安装目录名（用于按 exe 所在目录反查游戏）</summary>
    public List<string> InstallDirs { get; set; } = [];

    /// <summary>Steam appid（清单 steam.id）</summary>
    public long? SteamId { get; set; }

    /// <summary>额外 Steam appid（清单 id.steamExtra，部分游戏有多个版本）</summary>
    public List<long> SteamExtra { get; set; } = [];
}

/// <summary>精简后的存档位置索引（游戏名 → 条目）</summary>
public sealed class SaveManifestIndex
{
    public Dictionary<string, SaveManifestEntry> Games { get; set; } = [];
}

/// <summary>
/// ludusavi 存档位置清单（1.9 万+ 游戏，明文 YAML，MIT 许可）的下载 / 流式解析 / 缓存 / 查询。
/// 17.7MB 的 YAML 不做全量反序列化（掌机内存吃紧），用 YamlDotNet 的事件级 Parser 边解析边只抽取
/// files(tags 含 save) / installDir / steam.id / id.steamExtra，落成精简 JSON 缓存；
/// 缓存 7 天内直接用，过期后台刷新，失败静默回退（让上层走启发式兜底）。
/// </summary>
public static class SaveManifest
{
    /// <summary>清单地址：必须走 jsDelivr（raw.githubusercontent.com 在本机实测不可达，勿改回）</summary>
    private const string ManifestUrl =
        "https://cdn.jsdelivr.net/gh/mtkennerly/ludusavi-manifest@master/data/manifest.yaml";

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLocker", "save-manifest.json");

    private static readonly string MetaFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLocker", "save-manifest.meta.json");

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 浏览器 UA 降低被拦概率；17.7MB 下载给足超时（30 秒对慢速网络不够）
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        catch { /* UA 设置失败不影响使用 */ }
        return http;
    }

    /// <summary>缓存元数据（ETag / 抓取时间）</summary>
    private sealed class ManifestMeta
    {
        public string? ETag { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
    }

    private static SaveManifestIndex? _index;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// 确保索引可用：内存命中即返回；否则读缓存（7 天内直接用）；过期或缺失则下载刷新，
    /// 刷新失败且有旧缓存时退化为旧缓存，都没有则用空索引（上层走启发式）。
    /// <param name="note">刷新过程中的提示文案（在后台线程调用，订阅方自行 marshal）</param>
    /// </summary>
    public static async Task EnsureLoadedAsync(Action<string>? note = null)
    {
        if (_index is not null) return;
        await Gate.WaitAsync();
        try
        {
            if (_index is not null) return;

            var meta = LoadMeta();
            var hasCache = File.Exists(CacheFile) && meta is not null;
            if (hasCache && DateTimeOffset.UtcNow - meta!.FetchedAt < MaxAge)
            {
                _index = LoadCache();
                if (_index is not null) return;
            }

            note?.Invoke("正在更新存档位置数据库…");
            try
            {
                var parsed = await DownloadAndParseAsync(meta?.ETag, note);
                if (parsed is null) return; // 未变更（304）：沿用旧缓存，下面兜底读取
                _index = parsed;
                SaveCache(_index);
            }
            catch
            {
                // 下载/解析失败：静默回退，读旧缓存或空索引
            }

            if (_index is not null) return;
            _index = hasCache ? LoadCache() : null;
            _index ??= new SaveManifestIndex();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>下载清单并流式解析成精简索引；服务端返回 304（未变更）时返回 null。</summary>
    private static async Task<SaveManifestIndex?> DownloadAndParseAsync(string? etag, Action<string>? note)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        if (!string.IsNullOrEmpty(etag))
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode == HttpStatusCode.NotModified) return null;
        resp.EnsureSuccessStatusCode();

        var tmp = Path.Combine(Path.GetTempPath(), "gamelocker-manifest-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            note?.Invoke("正在解析存档位置数据库…");
            var index = ParseManifest(tmp);
            SaveMeta(new ManifestMeta { ETag = resp.Headers.ETag?.Tag, FetchedAt = DateTimeOffset.UtcNow });
            return index;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 临时文件清理失败无害 */ }
        }
    }

    // ---- 流式解析 ----

    /// <summary>YAML 容器帧（追踪"当前是键还是值"与当前键名）</summary>
    private sealed class Frame
    {
        public bool IsMap;
        public bool ExpectKey = true;
        public string? Key;
    }

    /// <summary>
    /// 事件级流式解析清单：只处理顶层「游戏名 → 各段」结构，
    /// 抽取 files(tags 含 save 且 Windows 可用) / installDir / steam.id / id.steamExtra。
    /// 其余段（registry/cloud/launch 等）与不支持的语法一并忽略，不报错。
    /// </summary>
    private static SaveManifestIndex ParseManifest(string yamlPath)
    {
        var index = new SaveManifestIndex();
        var keys = new string?[64];     // 按深度记录当前键路径
        var frames = new List<Frame>();
        var current = new SaveManifestEntry();
        var section = "";
        var pendingPath = "";
        var pendingSave = false;
        var sawWhen = false;
        var whenWindows = false;
        var inTags = false;   // 当前条目内已进入 tags 段
        var inWhen = false;   // 当前条目内已进入 when 段

        using var reader = new StreamReader(yamlPath, Encoding.UTF8);
        var parser = new Parser(reader);
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart:
                    frames.Add(new Frame { IsMap = true, ExpectKey = true });
                    break;

                case SequenceStart:
                    frames.Add(new Frame { IsMap = false, ExpectKey = false });
                    break;

                case MappingEnd:
                case SequenceEnd:
                    if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    // 离开 files 条目回到 files 映射层（深度 3）：提交上一个路径
                    if (frames.Count == 3 && section == "files")
                    {
                        if (pendingPath.Length > 0 && pendingSave && (!sawWhen || whenWindows))
                            current.Files.Add(pendingPath);
                        pendingPath = "";
                        pendingSave = false;
                        sawWhen = false;
                        whenWindows = false;
                        inTags = false;
                        inWhen = false;
                    }
                    break;

                case Scalar sc:
                    HandleScalar(sc.Value);
                    break;
            }
        }
        return index;

        void HandleScalar(string value)
        {
            var depth = frames.Count;
            if (depth == 0) return;
            var frame = frames[^1];

            if (!frame.IsMap)
            {
                // 序列项：tags 里的 "save"，或 id.steamExtra 的 appid
                if (depth == 5 && section == "files" && inTags)
                {
                    if (string.Equals(value, "save", StringComparison.OrdinalIgnoreCase)) pendingSave = true;
                }
                else if (depth == 4 && section == "id" && keys[3] == "steamExtra")
                {
                    if (long.TryParse(value, out var extra)) current.SteamExtra.Add(extra);
                }
                return;
            }

            if (frame.ExpectKey)
            {
                frame.Key = value;
                frame.ExpectKey = false;
                if (depth < keys.Length) keys[depth] = value;

                if (depth == 1)
                {
                    // 顶层键 = 游戏名
                    current = new SaveManifestEntry();
                    index.Games[value] = current;
                    section = "";
                }
                else if (depth == 2)
                {
                    section = value is "files" or "installDir" or "steam" or "id" ? value : "";
                }
                else if (depth == 3)
                {
                    if (section == "files")
                    {
                        pendingPath = value;
                        pendingSave = false;
                        sawWhen = false;
                        whenWindows = false;
                        inTags = false;
                        inWhen = false;
                    }
                    else if (section == "installDir" && value.Length > 0)
                    {
                        current.InstallDirs.Add(value);
                    }
                }
                else if (depth == 4 && section == "files")
                {
                    if (value == "tags") inTags = true;
                    else if (value == "when") { sawWhen = true; inWhen = true; }
                }
            }
            else
            {
                frame.ExpectKey = true;
                if (depth == 3 && section == "steam" && frame.Key == "id")
                {
                    if (long.TryParse(value, out var id)) current.SteamId = id;
                }
                else if (depth == 6 && section == "files" && inWhen && frame.Key == "os")
                {
                    if (value.Contains("windows", StringComparison.OrdinalIgnoreCase)) whenWindows = true;
                }
            }
        }
    }

    // ---- 查询 ----

    /// <summary>
    /// 查清单：优先 appid 精确命中 → 再安装目录名相等（忽略大小写）→ 再游戏名归一化相等/互相包含。
    /// 索引不可用（未加载/加载失败）时返回 null，由上层走启发式兜底。
    /// </summary>
    public static SaveManifestEntry? TryFind(string? gameName, IEnumerable<string>? installDirs, long? appId)
    {
        var idx = _index;
        if (idx is null || idx.Games.Count == 0) return null;

        if (appId is { } id)
        {
            foreach (var e in idx.Games.Values)
                if (e.SteamId == id || e.SteamExtra.Contains(id)) return e;
        }

        if (installDirs is not null)
        {
            var dirs = installDirs.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (dirs.Count > 0)
            {
                foreach (var kv in idx.Games)
                    if (dirs.Any(d => kv.Value.InstallDirs.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))))
                        return kv.Value;
            }
        }

        var norm = Normalize(gameName);
        if (norm.Length > 0)
        {
            foreach (var kv in idx.Games)
                if (Normalize(kv.Key) == norm) return kv.Value;
            foreach (var kv in idx.Games)
            {
                var k = Normalize(kv.Key);
                if (k.Length >= 4 && (k.Contains(norm, StringComparison.Ordinal) || norm.Contains(k, StringComparison.Ordinal)))
                    return kv.Value;
            }
        }
        return null;
    }

    /// <summary>归一化：仅保留字母数字并转小写（用于游戏名模糊比对）</summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ---- 缓存读写 ----

    private static SaveManifestIndex? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var idx = JsonSerializer.Deserialize<SaveManifestIndex>(File.ReadAllText(CacheFile));
            return idx?.Games.Count > 0 ? idx : null;
        }
        catch { return null; /* 缓存损坏：当作无缓存 */ }
    }

    private static void SaveCache(SaveManifestIndex index)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(index));
            if (LoadMeta() is null)
                SaveMeta(new ManifestMeta { FetchedAt = DateTimeOffset.UtcNow });
        }
        catch { /* 落盘失败不影响本次使用（下次再拉） */ }
    }

    private static ManifestMeta? LoadMeta()
    {
        try
        {
            if (!File.Exists(MetaFile)) return null;
            return JsonSerializer.Deserialize<ManifestMeta>(File.ReadAllText(MetaFile));
        }
        catch { return null; }
    }

    private static void SaveMeta(ManifestMeta meta)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetaFile)!);
            File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta));
        }
        catch { }
    }
}
