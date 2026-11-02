using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GameLocker.Services;

/// <summary>
/// 文本翻译：免费接口同时挂多个源（腾讯 TranSmart、有道、Bing、MyMemory），
/// 每次翻译并发请求"当前最快"的两个，谁先给出有效译文就用谁，并持续记录各自的实测耗时；
/// 连续失败的接口冷却一段时间。配置了 Azure 时优先 Azure，失败也回退免费接口。
/// 仅被屏幕翻译小组件使用。
/// </summary>
public static class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static string _freeToken = "";
    private static DateTime _freeTokenAt = DateTime.MinValue;
    /// <summary>Bing 端点不可用时（如被网络拦截）在这之前不再尝试，避免每帧都白等一次超时</summary>
    private static DateTime _bingDeadUntil = DateTime.MinValue;

    /// <summary>译文缓存（菜单/提示等重复文本只翻一次，节省免费接口额度）</summary>
    private static readonly Dictionary<string, string> Cache = [];
    private static readonly Queue<string> CacheOrder = [];

    /// <summary>MyMemory 带 de 参数会走注册额度：匿名请求会被 429，带上后实测稳定（额度按邮箱计，缓存能进一步省）</summary>
    private const string MyMemoryEmail = "gamelocker@outlook.com";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    // 免费接口的健康度与实测耗时：用于"自动选择最快"与故障冷却
    private static readonly Dictionary<string, double> Latency = [];
    private static readonly Dictionary<string, int> FailStreak = [];
    private static readonly Dictionary<string, long> DeadUntil = [];
    private static readonly object EngineLock = new();

    /// <summary>语言代码转服务端标签（zh→zh-Hans）</summary>
    public static string Tag(string lang) => lang switch
    {
        "zh" => "zh-Hans",
        "en" => "en",
        "ja" => "ja",
        _ => "zh-Hans"
    };

    /// <summary>把文本翻译到目标语言；失败返回 null（不抛异常）</summary>
    public static async Task<string?> TranslateAsync(string text, string engine, string? apiKey, string? region,
        string source, string target)
    {
        text = Clip(text);
        if (text.Length == 0) return null;

        var cacheKey = Key(engine, source, target, text);
        lock (Cache)
            if (Cache.TryGetValue(cacheKey, out var hit)) return hit;

        try
        {
            var result = engine == "azure" && !string.IsNullOrWhiteSpace(apiKey)
                ? await AzureAsync(text, apiKey!, region, source, target)
                : null;
            // Azure 没配或临时失败时回退免费接口，避免因为一个引擎故障就整屏翻译失败
            if (string.IsNullOrWhiteSpace(result)) result = await FreeAsync(text, source, target);
            if (string.IsNullOrWhiteSpace(result)) return null;
            Put(cacheKey, result);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 批量逐行翻译：Azure / 腾讯 / 有道 / Bing 都支持一次请求多条文本并保持顺序，整屏几十行只发一次；
    /// 批量接口不可用时回退逐行（含 MyMemory），连续失败两次就放弃剩余行，避免整屏等上几分钟。
    /// </summary>
    public static async Task<List<string?>> TranslateLinesAsync(IReadOnlyList<string> lines, string engine,
        string? apiKey, string? region, string source, string target)
    {
        var results = new List<string?>(lines.Count);
        var missing = new List<int>();
        var texts = new List<string>();
        lock (Cache)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var text = Clip(lines[i]);
                if (text.Length == 0)
                {
                    results.Add(null);
                    continue;
                }
                var cacheKey = Key(engine, source, target, text);
                if (Cache.TryGetValue(cacheKey, out var hit))
                {
                    results.Add(hit);
                    continue;
                }
                results.Add(null);
                missing.Add(i);
                texts.Add(text);
            }
        }
        if (missing.Count == 0) return results;

        var azure = engine == "azure" && !string.IsNullOrWhiteSpace(apiKey);
        var batch = azure
            ? await AzureBatchAsync(texts, apiKey!, region, source, target)
            : new string?[texts.Count];
        // Azure 批量失败（密钥/额度问题）或未配置时，用并发的免费接口兜底
        if (batch.All(string.IsNullOrWhiteSpace))
            batch = await FreeBatchAsync(texts, source, target);

        for (var i = 0; i < missing.Count; i++)
        {
            var value = batch[i];
            if (string.IsNullOrWhiteSpace(value)) continue;
            results[missing[i]] = value;
            Put(Key(engine, source, target, texts[i]), value);
        }

        var failures = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            if (results[missing[i]] is not null) continue;
            var value = await TranslateAsync(lines[missing[i]], engine, apiKey, region, source, target);
            results[missing[i]] = value;
            failures = string.IsNullOrWhiteSpace(value) ? failures + 1 : 0;
            if (failures >= 2) break;
        }
        return results;
    }

    private static string Clip(string text)
    {
        text = text.Trim();
        return text.Length > 1200 ? text[^1200..] : text;
    }

    private static string Key(string engine, string source, string target, string text) =>
        $"{engine}|{source}|{target}|{text}";

    private static void Put(string cacheKey, string value)
    {
        lock (Cache)
        {
            if (Cache.Count >= 128 && CacheOrder.TryDequeue(out var old)) Cache.Remove(old);
            if (!Cache.ContainsKey(cacheKey))
            {
                Cache[cacheKey] = value;
                CacheOrder.Enqueue(cacheKey);
            }
        }
    }

    private static async Task<string?> AzureAsync(string text, string key, string? region, string source, string target)
    {
        var batch = await AzureBatchAsync([text], key, region, source, target);
        return batch[0];
    }

    private static async Task<string?[]> AzureBatchAsync(IReadOnlyList<string> texts, string key, string? region,
        string source, string target)
    {
        var fail = new string?[texts.Count];
        try
        {
            var url = $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Tag(target)}";
            if (source != "auto") url += $"&from={Tag(source)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(texts.Select(t => new { Text = t })),
                    Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Ocp-Apim-Subscription-Key", key);
            if (!string.IsNullOrWhiteSpace(region)) req.Headers.Add("Ocp-Apim-Subscription-Region", region.Trim());
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return fail;
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            for (var i = 0; i < texts.Count && i < root.GetArrayLength(); i++)
                fail[i] = root[i].GetProperty("translations")[0].GetProperty("text").GetString();
        }
        catch { }
        return fail;
    }

    /// <summary>单行翻译：并发免费接口（最快者优先），全部失败再回退 MyMemory</summary>
    private static async Task<string?> FreeAsync(string text, string source, string target)
    {
        var batch = await FreeBatchAsync([text], source, target);
        if (!string.IsNullOrWhiteSpace(batch[0])) return batch[0];
        return await MyMemoryAsync(text, source, target);
    }

    /// <summary>
    /// 免费接口批量翻译：按实测耗时排序，同时只发起"当前最快的两个"，先返回有效译文的胜出；
    /// 这样既不用轮询等待慢接口，也不会把免费接口打爆。
    /// </summary>
    private static async Task<string?[]> FreeBatchAsync(IReadOnlyList<string> texts, string source, string target)
    {
        var fail = new string?[texts.Count];
        if (texts.Count == 0) return fail;

        var candidates = new List<(string Name, Func<Task<string?[]>> Run)>();
        foreach (var name in RankEngines())
        {
            switch (name)
            {
                case "youdao":
                    // 有道用换行拼批：URL 太长容易失败，整批超过 1500 字就跳过它
                    if (texts.Sum(t => t.Length) <= 1500)
                        candidates.Add(("youdao", () => YoudaoBatchAsync(texts, source, target)));
                    break;
                case "tencent":
                    candidates.Add(("tencent", () => TencentBatchAsync(texts, source, target)));
                    break;
                case "bing":
                    candidates.Add(("bing", () => BingBatchAsync(texts, source, target)));
                    break;
            }
            if (candidates.Count >= 2) break;
        }
        if (candidates.Count == 0) return fail;

        var pending = candidates.Select(c => RunEngine(c.Name, c.Run)).ToList();
        while (pending.Count > 0)
        {
            Task<EngineResult> done;
            try { done = await Task.WhenAny(pending); }
            catch { break; }
            pending.Remove(done);

            EngineResult result;
            try { result = await done; }
            catch { continue; }

            var ok = result.Lines.Any(v => !string.IsNullOrWhiteSpace(v));
            Record(result.Name, result.Ms, ok);
            if (ok) return result.Lines;
        }
        return fail;
    }

    /// <summary>把一次接口调用包起来，记录耗时并保证异常不会冒出去</summary>
    private static async Task<EngineResult> RunEngine(string name, Func<Task<string?[]>> run)
    {
        var watch = Stopwatch.StartNew();
        string?[] lines;
        try { lines = await run(); }
        catch { lines = []; }
        watch.Stop();
        return new EngineResult(name, lines, watch.ElapsedMilliseconds);
    }

    /// <summary>当前可用引擎：跳过冷却中的，按实测平均耗时从快到慢排序；全部冷却时仍返回两个最快候选</summary>
    private static List<string> RankEngines()
    {
        string[] all = ["youdao", "tencent", "bing"];
        lock (EngineLock)
        {
            var now = Environment.TickCount64;
            var alive = all
                .Where(n => DeadUntil.GetValueOrDefault(n, 0) <= now)
                .OrderBy(n => Latency.GetValueOrDefault(n, SeedLatency(n)))
                .ToList();
            return alive.Count > 0 ? alive : ["youdao", "tencent"];
        }
    }

    private static double SeedLatency(string name) => name switch
    {
        "youdao" => 300,
        "tencent" => 400,
        _ => 1200
    };

    /// <summary>记录接口实测耗时（指数平滑）与连续失败；连续失败两次冷却 10 分钟</summary>
    private static void Record(string name, long ms, bool ok)
    {
        lock (EngineLock)
        {
            if (ok)
            {
                var old = Latency.GetValueOrDefault(name, SeedLatency(name));
                Latency[name] = old * 0.6 + ms * 0.4;
                FailStreak[name] = 0;
                DebugLog.Write($"翻译引擎: {name} 成功 {ms}ms");
            }
            else
            {
                var streak = FailStreak.GetValueOrDefault(name) + 1;
                FailStreak[name] = streak;
                if (streak >= 2) DeadUntil[name] = Environment.TickCount64 + 10 * 60 * 1000;
                DebugLog.Write($"翻译引擎: {name} 失败 {ms}ms（连续 {streak} 次）");
            }
        }
    }

    /// <summary>腾讯 TranSmart：一次 POST 提交多行，返回顺序一致的 auto_translation 数组</summary>
    private static async Task<string?[]> TencentBatchAsync(IReadOnlyList<string> texts, string source, string target)
    {
        var fail = new string?[texts.Count];
        try
        {
            var body = new
            {
                header = new { fn = "auto_translation", client_key = "browser-chrome-120.0.0" },
                type = "plain",
                model_category = "normal",
                source = new { lang = source, text_list = texts },
                target = new { lang = target }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return fail;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("header", out var header) &&
                header.TryGetProperty("ret_code", out var code) &&
                code.GetString() != "succ") return fail;
            if (!root.TryGetProperty("auto_translation", out var arr)) return fail;
            for (var i = 0; i < texts.Count && i < arr.GetArrayLength(); i++)
                fail[i] = arr[i].GetString();
        }
        catch { }
        return fail;
    }

    /// <summary>有道免密钥接口：用换行拼接多行，返回按换行拆分的译文，行数不一致视为失败</summary>
    private static async Task<string?[]> YoudaoBatchAsync(IReadOnlyList<string> texts, string source, string target)
    {
        var fail = new string?[texts.Count];
        try
        {
            var joined = string.Join("\n", texts.Select(t => t.Replace('\r', ' ').Replace('\n', ' ')));
            var url = "https://aidemo.youdao.com/trans?q=" + Uri.EscapeDataString(joined)
                + "&from=" + YoudaoLang(source) + "&to=" + YoudaoLang(target);
            if (url.Length > 4000) return fail;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return fail;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("errorCode", out var code) && code.GetInt32() != 0) return fail;
            if (!root.TryGetProperty("translation", out var arr) || arr.GetArrayLength() == 0) return fail;

            var parts = (arr[0].GetString() ?? "").Replace("\r", "").Split('\n');
            if (parts.Length != texts.Count) return fail; // 换行被合并/拆开时不硬塞，交给其它接口
            for (var i = 0; i < parts.Length; i++)
                fail[i] = parts[i].Trim();
        }
        catch { }
        return fail;
    }

    private static string YoudaoLang(string lang) => lang switch
    {
        "zh" => "zh-CHS",
        "ja" => "ja",
        "auto" => "auto",
        _ => "en"
    };

    /// <summary>MyMemory 回退（匿名额度有限，单次最多约 500 字节，长文本按段翻译）</summary>
    private static async Task<string?> MyMemoryAsync(string text, string source, string target)
    {
        var from = source == "auto" ? GuessSource(text) : Tag(source);
        var to = Tag(target) == "zh-Hans" ? "zh-CN" : Tag(target);
        var parts = new List<string>();
        foreach (var chunk in SplitByBytes(text, 420))
        {
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}"
                + $"&langpair={from}|{to}&de={Uri.EscapeDataString(MyMemoryEmail)}";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            if (string.IsNullOrWhiteSpace(translated)) continue;
            // 额度用尽时接口会返回一段警告文字，不能当译文显示
            if (translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
                || translated.Contains("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase)) return null;
            parts.Add(translated.Trim());
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>免费接口的源语言检测：含假名=日文，含汉字=中文，其余按英文（仅用于回退接口）</summary>
    private static string GuessSource(string text)
    {
        foreach (var c in text)
            if (c is >= '\u3040' and <= '\u30FF') return "ja";
        foreach (var c in text)
            if (c is >= '\u4E00' and <= '\u9FFF') return "zh-CN";
        return "en";
    }

    /// <summary>按 UTF-8 字节数把文本切成若干段（尽量不切断单词/句子），供有长度限制的接口使用</summary>
    private static List<string> SplitByBytes(string text, int maxBytes)
    {
        var list = new List<string>();
        var start = 0;
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));
            if (bytes + size > maxBytes && i > start)
            {
                list.Add(text[start..i]);
                start = i;
                bytes = 0;
            }
            bytes += size;
        }
        if (start < text.Length) list.Add(text[start..]);
        return list;
    }

    private static async Task<string?[]> BingBatchAsync(IReadOnlyList<string> texts, string source, string target)
    {
        var fail = new string?[texts.Count];
        var token = await BingTokenAsync();
        if (token is null) return fail;

        try
        {
            var url = $"https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Tag(target)}";
            if (source != "auto") url += $"&from={Tag(source)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(texts.Select(t => new { Text = t })),
                    Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", "Bearer " + token);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _freeToken = ""; // 强制下次刷新 token
                return fail;
            }
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            for (var i = 0; i < texts.Count && i < root.GetArrayLength(); i++)
                fail[i] = root[i].GetProperty("translations")[0].GetProperty("text").GetString();
        }
        catch
        {
            _bingDeadUntil = DateTime.Now.AddMinutes(30);
        }
        return fail;
    }

    private static async Task<string?> BingTokenAsync()
    {
        if (DateTime.Now < _bingDeadUntil) return null;
        if (DateTime.Now - _freeTokenAt > TimeSpan.FromMinutes(8) || string.IsNullOrEmpty(_freeToken))
        {
            try
            {
                var token = await Http.GetStringAsync("https://edge.microsoft.com/translate/auth");
                if (string.IsNullOrWhiteSpace(token)) { _bingDeadUntil = DateTime.Now.AddMinutes(30); return null; }
                _freeToken = token.Trim();
                _freeTokenAt = DateTime.Now;
            }
            catch
            {
                // 端点不可用（404/被拦截/超时）：30 分钟内直接走 MyMemory
                _bingDeadUntil = DateTime.Now.AddMinutes(30);
                return null;
            }
        }
        return _freeToken;
    }

    /// <summary>一次免费接口调用的结果（供并发竞速使用）</summary>
    private sealed record EngineResult(string Name, string?[] Lines, long Ms);
}
