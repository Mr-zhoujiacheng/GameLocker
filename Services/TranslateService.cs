using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GameLocker.Services;

/// <summary>
/// 文本翻译：免费接口同时挂多个源（腾讯 TranSmart、有道、Bing、MyMemory），
/// 每次翻译并发请求"当前最快"的两个，谁先给出有效译文就用谁，并持续记录各自的实测耗时；
/// 连续失败的接口冷却一段时间。Azure 模式仅请求 Azure，不会回退免费接口。
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

    public sealed record PreflightSource(string Engine, bool Success, string Reason);
    public sealed record PreflightResult(IReadOnlyList<PreflightSource> Sources)
    {
        public IReadOnlyList<string> AvailableSources => Sources.Where(s => s.Success).Select(s => s.Engine).ToArray();
        public bool Success => Sources.Any(s => s.Success);
    }

    private sealed class RequestDiagnostic(bool preflight = false)
    {
        public bool Preflight { get; } = preflight;
        public string? Reason { get; private set; }
        public void Fail(string reason)
        {
            Reason = reason;
        }
        public void Exception(Exception ex)
        {
            var reason = ex switch
            {
                OperationCanceledException => "请求超时",
                HttpRequestException http => $"HTTP 请求失败（{http.HttpRequestError}）" +
                    (http.StatusCode is { } status ? $" HTTP {(int)status}" : ""),
                JsonException => "响应解析失败",
                KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException => "响应字段缺失或格式错误",
                _ => $"请求异常（{ex.GetType().Name}）"
            };
            for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
                if (inner is SocketException socket) { reason += $" SocketErrorCode={socket.SocketErrorCode}"; break; }
            Fail(reason);
        }
    }

    public static async Task<PreflightResult> PreflightAsync(string engine, string? apiKey, string? region,
        CancellationToken cancellationToken = default, Action<PreflightSource>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] sources = engine == "free" ? ["youdao", "tencent", "bing", "mymemory"] : [engine];
        var checks = sources.Select(async name =>
        {
            var diagnostic = new RequestDiagnostic(true);
            progress?.Invoke(new PreflightSource(name, false, "检查中"));
            if (name == "azure" && string.IsNullOrWhiteSpace(apiKey)) diagnostic.Fail("Azure 未配置密钥");
            else
            {
                var lines = await SelectedBatchAsync(["Hello world"], name, apiKey, region, "en", "zh",
                    cancellationToken, null, diagnostic);
                var value = lines.FirstOrDefault()?.Trim();
                if (lines.Length == 1 && IsProbeTranslation(value) && diagnostic.Reason is null)
                {
                    var success = new PreflightSource(name, true, "可用");
                    progress?.Invoke(success);
                    return success;
                }
                if (diagnostic.Reason is null) diagnostic.Fail("未返回有效中文译文");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new PreflightSource(name, false, diagnostic.Reason!);
            DebugLog.Write($"翻译预检: {name} {result.Reason}");
            progress?.Invoke(result);
            return result;
        }).ToArray();
        var results = await Task.WhenAll(checks);
        cancellationToken.ThrowIfCancellationRequested();
        return new PreflightResult(results);
    }

    private static bool IsProbeTranslation(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 40 && value.Contains("世界") &&
        new[] { "你好", "您好", "哈啰", "哈罗", "嗨", "喂" }.Any(value.Contains);

    public static string EngineName(string name) => name switch
    {
        "free" => "自动免费",
        "youdao" => "有道",
        "tencent" => "腾讯",
        "bing" => "Bing",
        "mymemory" => "MyMemory",
        "azure" => "Azure",
        _ => name
    };

    public static string GetEngineStatus(string name, bool azureConfigured)
    {
        if (name == "azure" && !azureConfigured) return "未配置";
        lock (EngineLock)
        {
            if (name == "free")
            {
                var statuses = new[] { "youdao", "tencent", "bing", "mymemory" }
                    .Select(n => GetEngineStatus(n, azureConfigured)).ToArray();
                if (statuses.Contains("最近可用")) return "最近可用";
                if (statuses.Contains("未检测")) return "未检测";
                if (statuses.Contains("待重试")) return "待重试";
                if (statuses.Contains("最近失败")) return "最近失败";
                return "冷却中";
            }
            if (DeadUntil.GetValueOrDefault(name) > Environment.TickCount64 ||
                name == "bing" && DateTime.Now < _bingDeadUntil) return "冷却中";
            if (DeadUntil.ContainsKey(name) || name == "bing" && _bingDeadUntil != DateTime.MinValue)
                return "待重试";
            if (FailStreak.GetValueOrDefault(name) > 0) return "最近失败";
            return Latency.ContainsKey(name) ? "最近可用" : "未检测";
        }
    }

    /// <summary>语言代码转服务端标签（zh→zh-Hans）</summary>
    public static string Tag(string lang) => lang switch
    {
        "zh" => "zh-Hans",
        "en" => "en",
        "ja" => "ja",
        _ => "zh-Hans"
    };

    /// <summary>把文本翻译到目标语言；失败返回 null，调用方取消时抛出取消异常</summary>
    public static async Task<string?> TranslateAsync(string text, string engine, string? apiKey, string? region,
        string source, string target, CancellationToken cancellationToken = default, Action<string>? activity = null,
        IReadOnlyList<string>? allowedSources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        text = Clip(text);
        if (text.Length == 0 || engine == "azure" && string.IsNullOrWhiteSpace(apiKey)) return null;

        var cacheKey = Key(engine, source, target, text);
        string? cached;
        lock (Cache) Cache.TryGetValue(cacheKey, out cached);
        if (cached is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.Invoke("使用缓存（无请求）");
            return cached;
        }

        try
        {
            // Azure 与单选模式只请求指定源，不向其它接口发送文本。
            var result = engine == "free"
                ? await FreeAsync(text, source, target, cancellationToken, activity, allowedSources)
                : (await SelectedBatchAsync([text], engine, apiKey, region, source, target, cancellationToken, activity))
                    .FirstOrDefault();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result)) return null;
            Put(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// 批量逐行翻译：Azure / 腾讯 / 有道 / Bing 都支持一次请求多条文本并保持顺序，整屏几十行只发一次；
    /// 批量接口不可用时在当前模式内回退逐行；自动免费模式可回退 MyMemory，单选模式不跨源，连续失败两次放弃剩余行。
    /// </summary>
    public static async Task<List<string?>> TranslateLinesAsync(IReadOnlyList<string> lines, string engine,
        string? apiKey, string? region, string source, string target, CancellationToken cancellationToken = default,
        Action<string>? activity = null, IReadOnlyList<string>? allowedSources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (engine == "azure" && string.IsNullOrWhiteSpace(apiKey))
        {
            activity?.Invoke("Azure 未配置（无请求）");
            return Enumerable.Repeat<string?>(null, lines.Count).ToList();
        }
        var results = new List<string?>(lines.Count);
        var missing = new List<int>();
        var texts = new List<string>();
        lock (Cache)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        if (missing.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.Invoke(results.Any(value => value is not null) ? "使用缓存（无请求）" : "无待翻译文本（无请求）");
            return results;
        }

        var batch = engine == "free"
            ? await FreeBatchAsync(texts, source, target, cancellationToken, activity, allowedSources)
            : await SelectedBatchAsync(texts, engine, apiKey, region, source, target, cancellationToken, activity);
        cancellationToken.ThrowIfCancellationRequested();

        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = i < batch.Length ? batch[i] : null;
            if (string.IsNullOrWhiteSpace(value)) continue;
            results[missing[i]] = value;
            Put(Key(engine, source, target, texts[i]), value);
        }

        var failures = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results[missing[i]] is not null) continue;
            var value = await TranslateAsync(lines[missing[i]], engine, apiKey, region, source, target, cancellationToken, activity, allowedSources);
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

    private static async Task<string?[]> SelectedBatchAsync(IReadOnlyList<string> texts, string engine,
        string? apiKey, string? region, string source, string target, CancellationToken cancellationToken,
        Action<string>? activity, RequestDiagnostic? diagnostic = null)
    {
        diagnostic ??= new RequestDiagnostic();
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0 || engine == "azure" && string.IsNullOrWhiteSpace(apiKey))
            return new string?[texts.Count];
        Func<Task<string?[]>>? run = engine switch
        {
            "youdao" => () => YoudaoBatchAsync(texts, source, target, cancellationToken, diagnostic),
            "tencent" => () => TencentBatchAsync(texts, source, target, cancellationToken, diagnostic),
            "bing" => () => BingBatchAsync(texts, source, target, cancellationToken, diagnostic),
            "mymemory" => async () =>
            {
                var lines = new string?[texts.Count];
                for (var i = 0; i < texts.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lines[i] = await MyMemoryAsync(texts[i], source, target, cancellationToken, diagnostic);
                }
                return lines;
            },
            "azure" => () => AzureBatchAsync(texts, apiKey!, region, source, target, cancellationToken, diagnostic),
            _ => null
        };
        if (run is null) { diagnostic.Fail("未知翻译源"); return new string?[texts.Count]; }
        activity?.Invoke("请求：" + EngineName(engine));
        var result = await RunEngine(engine, run, cancellationToken, diagnostic);
        cancellationToken.ThrowIfCancellationRequested();
        activity?.Invoke((result.Lines.Any(v => !string.IsNullOrWhiteSpace(v)) ? "采用：" : "请求失败：") + EngineName(engine));
        return result.Lines;
    }

    private static async Task<string?[]> AzureBatchAsync(IReadOnlyList<string> texts, string key, string? region,
        string source, string target, CancellationToken cancellationToken, RequestDiagnostic? diagnostic = null)
    {
        diagnostic ??= new RequestDiagnostic();
        cancellationToken.ThrowIfCancellationRequested();
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
            using var resp = await Http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) { diagnostic.Fail($"HTTP {(int)resp.StatusCode}"); return fail; }
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetArrayLength() != texts.Count) { diagnostic.Fail("响应行数不匹配"); return fail; }
            for (var i = 0; i < texts.Count && i < root.GetArrayLength(); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fail[i] = root[i].GetProperty("translations")[0].GetProperty("text").GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Exception(ex);
            return new string?[texts.Count];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return fail;
    }

    /// <summary>单行翻译：并发免费接口（最快者优先），全部失败再回退 MyMemory</summary>
    private static async Task<string?> FreeAsync(string text, string source, string target,
        CancellationToken cancellationToken, Action<string>? activity, IReadOnlyList<string>? allowedSources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = await FreeBatchAsync([text], source, target, cancellationToken, activity, allowedSources);
        var value = batch.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (allowedSources is not null && !allowedSources.Contains("mymemory")) return null;
        return (await SelectedBatchAsync([text], "mymemory", null, null, source, target,
            cancellationToken, activity)).FirstOrDefault();
    }

    /// <summary>
    /// 免费接口批量翻译：按实测耗时排序，同时只发起"当前最快的两个"，先返回有效译文的胜出；
    /// 胜出后取消其它请求并观察其完成，主动取消不影响健康度。
    /// </summary>
    private static async Task<string?[]> FreeBatchAsync(IReadOnlyList<string> texts, string source, string target,
        CancellationToken cancellationToken, Action<string>? activity, IReadOnlyList<string>? allowedSources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fail = new string?[texts.Count];
        if (texts.Count == 0) return fail;

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var candidates = new List<(string Name, Func<Task<string?[]>> Run)>();
        foreach (var name in allowedSources ?? RankEngines())
        {
            // 有道拼批过长时跳过；所有请求复用单源路径及其隔离诊断。
            if (name == "youdao" && texts.Sum(t => t.Length) > 1500) continue;
            candidates.Add((name, () => SelectedBatchAsync(texts, name, null, null, source, target, race.Token, null)));
            if (candidates.Count >= 2) break;
        }
        if (candidates.Count == 0) return fail;

        activity?.Invoke("请求：" + string.Join(" + ", candidates.Select(c => EngineName(c.Name))));
        var tasks = candidates.Select(async c => new EngineResult(c.Name, await c.Run(), 0)).ToList();
        var pending = tasks.ToList();
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var result = await done;
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Lines.Any(v => !string.IsNullOrWhiteSpace(v)))
                {
                    race.Cancel();
                    activity?.Invoke("采用：" + EngineName(result.Name));
                    return result.Lines;
                }
            }
            return fail;
        }
        finally
        {
            // 取消败方并等待所有包装任务，确保健康记录完成且没有未观察异常。
            race.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (race.IsCancellationRequested) { }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>每个完成的请求自行记录健康度；主动取消不计失败，HTTP 超时仍按失败处理</summary>
    private static async Task<EngineResult> RunEngine(string name, Func<Task<string?[]>> run,
        CancellationToken cancellationToken, RequestDiagnostic? diagnostic = null)
    {
        diagnostic ??= new RequestDiagnostic();
        var watch = Stopwatch.StartNew();
        string?[] lines;
        try { lines = await run(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Exception(ex);
            lines = [];
        }
        watch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var ok = diagnostic.Reason is null && lines.Any(v => !string.IsNullOrWhiteSpace(v));
        if (diagnostic.Preflight && (lines.Length != 1 || !IsProbeTranslation(lines.FirstOrDefault())))
        {
            ok = false;
            if (diagnostic.Reason is null) diagnostic.Fail("未返回有效中文译文");
        }
        if (!ok && diagnostic.Reason is null) diagnostic.Fail("响应未包含有效译文");
        if (diagnostic.Reason is not null) DebugLog.Write($"翻译诊断: {name} {diagnostic.Reason}");
        Record(name, watch.ElapsedMilliseconds, ok);
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
                .Where(n => DeadUntil.GetValueOrDefault(n, 0) <= now &&
                    (n != "bing" || DateTime.Now >= _bingDeadUntil))
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
                DeadUntil.Remove(name);
                if (name == "bing") _bingDeadUntil = DateTime.MinValue;
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
    private static async Task<string?[]> TencentBatchAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken cancellationToken, RequestDiagnostic? diagnostic = null)
    {
        diagnostic ??= new RequestDiagnostic();
        cancellationToken.ThrowIfCancellationRequested();
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
            using var resp = await Http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) { diagnostic.Fail($"HTTP {(int)resp.StatusCode}"); return fail; }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("header", out var header) &&
                header.TryGetProperty("ret_code", out var code) &&
                code.GetString() != "succ") { diagnostic.Fail("服务返回失败状态"); return fail; }
            if (!root.TryGetProperty("auto_translation", out var arr)) { diagnostic.Fail("响应字段缺失"); return fail; }
            if (arr.GetArrayLength() != texts.Count) { diagnostic.Fail("响应行数不匹配"); return fail; }
            for (var i = 0; i < texts.Count && i < arr.GetArrayLength(); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fail[i] = arr[i].GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Exception(ex);
            return new string?[texts.Count];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return fail;
    }

    /// <summary>有道免密钥接口：用换行拼接多行，返回按换行拆分的译文，行数不一致视为失败</summary>
    private static async Task<string?[]> YoudaoBatchAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken cancellationToken, RequestDiagnostic? diagnostic = null)
    {
        diagnostic ??= new RequestDiagnostic();
        cancellationToken.ThrowIfCancellationRequested();
        var fail = new string?[texts.Count];
        try
        {
            var joined = string.Join("\n", texts.Select(t => t.Replace('\r', ' ').Replace('\n', ' ')));
            var url = "https://aidemo.youdao.com/trans?q=" + Uri.EscapeDataString(joined)
                + "&from=" + YoudaoLang(source) + "&to=" + YoudaoLang(target);
            if (url.Length > 4000) { diagnostic.Fail("请求长度超限"); return fail; }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) { diagnostic.Fail($"HTTP {(int)resp.StatusCode}"); return fail; }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("errorCode", out var code) && code.GetInt32() != 0) { diagnostic.Fail("服务返回失败状态"); return fail; }
            if (!root.TryGetProperty("translation", out var arr) || arr.GetArrayLength() == 0) { diagnostic.Fail("响应字段缺失"); return fail; }

            var parts = (arr[0].GetString() ?? "").Replace("\r", "").Split('\n');
            if (parts.Length != texts.Count) { diagnostic.Fail("响应行数不匹配"); return fail; } // 换行被合并/拆开时不硬塞，交给其它接口
            for (var i = 0; i < parts.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fail[i] = parts[i].Trim();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Exception(ex);
            return new string?[texts.Count];
        }
        cancellationToken.ThrowIfCancellationRequested();
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
    private static async Task<string?> MyMemoryAsync(string text, string source, string target, CancellationToken cancellationToken, RequestDiagnostic? diagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = source == "auto" ? GuessSource(text) : Tag(source);
        var to = Tag(target) == "zh-Hans" ? "zh-CN" : Tag(target);
        var parts = new List<string>();
        foreach (var chunk in SplitByBytes(text, 420, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}"
                + $"&langpair={from}|{to}&de={Uri.EscapeDataString(MyMemoryEmail)}";
            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) { diagnostic?.Fail($"HTTP {(int)response.StatusCode}"); return null; }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("responseStatus", out var status) || status.GetInt32() != 200)
            { diagnostic?.Fail("服务返回失败状态或字段缺失"); return null; }
            var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            if (string.IsNullOrWhiteSpace(translated)) { diagnostic?.Fail("响应译文字段为空"); return null; }
            // 额度用尽时接口会返回一段警告文字，不能当译文显示
            if (translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
                || translated.Contains("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase))
            { diagnostic?.Fail("服务额度或长度限制"); return null; }
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
    private static List<string> SplitByBytes(string text, int maxBytes, CancellationToken cancellationToken)
    {
        var list = new List<string>();
        var start = 0;
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static async Task<string?[]> BingBatchAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken cancellationToken, RequestDiagnostic? diagnostic = null)
    {
        diagnostic ??= new RequestDiagnostic();
        cancellationToken.ThrowIfCancellationRequested();
        var fail = new string?[texts.Count];
        var token = await BingTokenAsync(cancellationToken, diagnostic);
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
            using var resp = await Http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                diagnostic.Fail($"翻译 HTTP {(int)resp.StatusCode}");
                _freeToken = ""; // 强制下次刷新 token
                return fail;
            }
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetArrayLength() != texts.Count) { diagnostic.Fail("响应行数不匹配"); return fail; }
            for (var i = 0; i < texts.Count && i < root.GetArrayLength(); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fail[i] = root[i].GetProperty("translations")[0].GetProperty("text").GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Exception(ex);
            diagnostic.Fail("Bing 翻译 " + diagnostic.Reason + "，冷却 30 分钟");
            _bingDeadUntil = DateTime.Now.AddMinutes(30);
            return new string?[texts.Count];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return fail;
    }

    private static async Task<string?> BingTokenAsync(CancellationToken cancellationToken, RequestDiagnostic diagnostic)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!diagnostic.Preflight && DateTime.Now < _bingDeadUntil)
        { diagnostic.Fail("Bing token 失败后冷却中，可重新预检"); return null; }
        if (!diagnostic.Preflight && DateTime.Now - _freeTokenAt <= TimeSpan.FromMinutes(8) && !string.IsNullOrEmpty(_freeToken))
            return _freeToken;
        try
        {
            using var response = await Http.GetAsync("https://edge.microsoft.com/translate/auth", cancellationToken);
            if (!response.IsSuccessStatusCode)
            { diagnostic.Fail($"Bing token HTTP {(int)response.StatusCode}，冷却 30 分钟"); }
            else
            {
                var token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (token.Length > 0 && token.Split('.').Length == 3)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _freeToken = token;
                    _freeTokenAt = DateTime.Now;
                    return token;
                }
                diagnostic.Fail("Bing token 响应格式无效，冷却 30 分钟");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Exception(ex);
            diagnostic.Fail("Bing token " + diagnostic.Reason + "，冷却 30 分钟");
        }
        cancellationToken.ThrowIfCancellationRequested();
        _freeToken = "";
        _bingDeadUntil = DateTime.Now.AddMinutes(30);
        return null;
    }

    /// <summary>一次免费接口调用的结果（供并发竞速使用）</summary>
    private sealed record EngineResult(string Name, string?[] Lines, long Ms);
}
