using System.Net;
using System.Text.Json.Nodes;

namespace GameLocker.Services;

/// <summary>
/// 百度网盘网页版扫码登录：getqrcode 取二维码 → 轮询 unicast 频道，
/// 确认后兑换频道票据，从响应 Cookie 中取得 BDUSS/STOKEN/PTOKEN。
/// 只依赖网页公开接口，不调用需要申请应用的开放平台 OAuth。
/// </summary>
public sealed class BaiduLoginSession : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private string _sign = "";
    private string _gid = "";

    public BaiduLoginSession()
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    private static string Value(JsonNode? node, string name) => node?[name]?.ToString() ?? "";

    private async Task<HttpResponseMessage> GetAsync(string url, string referer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(referer);
        return await _http.SendAsync(request, ct);
    }

    public async Task<byte[]> CreateQrAsync(CancellationToken ct)
    {
        _gid = Guid.NewGuid().ToString("N");
        using var response = await GetAsync(
            "https://passport.baidu.com/v2/api/getqrcode?lp=pc&qrloginfrom=pc&gid=" + _gid + "&tpl=netdisk",
            "https://pan.baidu.com/", ct);
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new IOException("百度网盘登录返回为空");
        if (Value(node, "errno") != "0") throw new IOException("获取百度网盘二维码失败");
        var sign = Value(node, "sign");
        var imageUrl = Value(node, "imgurl");
        if (sign.Length == 0 || imageUrl.Length == 0) throw new IOException("百度网盘二维码参数无效");
        _sign = sign;
        if (!imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) imageUrl = "https://" + imageUrl;

        using var image = await GetAsync(imageUrl, "https://pan.baidu.com/", ct);
        image.EnsureSuccessStatusCode();
        if (image.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            throw new IOException("百度网盘未返回有效二维码，请重试");
        return await image.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<BaiduAccount> WaitForLoginAsync(Action<string> status, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = await PollAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 长轮询到点未返回，属于正常空闲，不是失效
                status("请使用百度网盘 App 扫码登录");
                continue;
            }

            var payload = ParseCallback(text);
            if (payload is null) { await Task.Delay(800, ct); continue; }
            // errno=1 是长轮询闲置超时（服务端 30 秒无状态变化时的正常返回），不能当作二维码失效
            if (Value(payload, "errno") != "0")
            {
                status("请使用百度网盘 App 扫码登录");
                continue;
            }

            var channelText = Value(payload, "channel_v");
            if (channelText.Length == 0)
            {
                status("请使用百度网盘 App 扫码登录");
                continue;
            }
            JsonNode? channel;
            try { channel = JsonNode.Parse(channelText); } catch { channel = null; }
            if (channel is null) continue;

            var scanStatus = Value(channel, "status");
            DebugLog.Write("百度扫码状态：status=" + scanStatus);
            switch (scanStatus)
            {
                case "0":
                    status("已确认，正在获取百度网盘会话…");
                    return await CompleteAsync(channel, ct);
                case "1":
                    status("已扫码，请在百度网盘 App 确认登录");
                    break;
                case "2":
                    throw new IOException("已在手机上取消登录，请重新扫码");
                default:
                    status("请使用百度网盘 App 扫码登录");
                    break;
            }
        }
        throw new IOException("二维码已过期，请重新扫码");
    }

    private async Task<string> PollAsync(CancellationToken ct)
    {
        var url = "https://passport.baidu.com/channel/unicast?channel_id=" + Uri.EscapeDataString(_sign) +
            "&tpl=netdisk&gid=" + Uri.EscapeDataString(_gid) + "&callback=cb&_=" +
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await GetAsync(url, "https://pan.baidu.com/", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>去掉 JSONP 包装：cb({...})</summary>
    private static JsonNode? ParseCallback(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith("cb", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('(');
            var end = trimmed.LastIndexOf(')');
            if (start < 0 || end <= start) return null;
            trimmed = trimmed[(start + 1)..end];
        }
        try
        {
            return JsonNode.Parse(NormalizeCallback(trimmed), documentOptions: new()
            { AllowTrailingCommas = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            DebugLog.Write($"百度回调解析失败：长度={trimmed.Length} 行={ex.LineNumber} 字节={ex.BytePositionInLine}");
            return null;
        }
    }

    private static string NormalizeCallback(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var quote = text[i];
            if (quote is not ('\'' or '"')) { output.Append(quote); continue; }
            var value = new System.Text.StringBuilder();
            var closed = false;
            while (++i < text.Length)
            {
                var ch = text[i];
                if (ch == quote) { closed = true; break; }
                if (ch != '\\') { value.Append(ch); continue; }
                if (++i >= text.Length) break;
                ch = text[i];
                switch (ch)
                {
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'v': value.Append('\v'); break;
                    case '0': value.Append('\0'); break;
                    case '\n': break;
                    case '\r':
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        break;
                    case 'x':
                    case 'u':
                        var digits = ch == 'x' ? 2 : 4;
                        if (i + digits >= text.Length || !ushort.TryParse(text.AsSpan(i + 1, digits),
                            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code))
                            throw new System.Text.Json.JsonException("Invalid callback escape");
                        value.Append((char)code);
                        i += digits;
                        break;
                    default: value.Append(ch); break;
                }
            }
            if (!closed) throw new System.Text.Json.JsonException("Unterminated callback string");
            output.Append(System.Text.Json.JsonSerializer.Serialize(value.ToString()));
        }
        return output.ToString();
    }

    private async Task<BaiduAccount> CompleteAsync(JsonNode channel, CancellationToken ct)
    {
        var ticket = Value(channel, "v");
        if (ticket.Length == 0) throw new IOException("百度扫码确认后未返回登录票据，请重新扫码");
        var url = "https://passport.baidu.com/v3/login/main/qrbdusslogin?bduss=" + Uri.EscapeDataString(ticket) +
            "&u=" + Uri.EscapeDataString("https://pan.baidu.com/disk/home") +
            "&loginVersion=v4&qrcode=1&tpl=netdisk&callback=cb&v=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await GetAsync(url, "https://pan.baidu.com/", ct);
        response.EnsureSuccessStatusCode();
        var result = ParseCallback(await response.Content.ReadAsStringAsync(ct))
            ?? throw new IOException("百度登录票据兑换返回格式异常");
        var code = Value(result["errInfo"], "no");
        if (code.Length == 0) code = Value(result["data"], "errno");
        DebugLog.Write("百度扫码票据兑换：code=" + code);
        if (code != "0")
            throw new IOException("百度登录确认失败（" + code + "）：" + Value(result["errInfo"], "msg"));

        var target = Value(result["data"], "u");
        if (target.Length > 0)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var redirect) || redirect.Scheme != "https" ||
                !(redirect.Host.Equals("baidu.com", StringComparison.OrdinalIgnoreCase) ||
                  redirect.Host.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("百度返回的登录跳转地址无效");
            using var callback = await GetAsync(redirect.AbsoluteUri, "https://passport.baidu.com/", ct);
            callback.EnsureSuccessStatusCode();
        }
        using var home = await GetAsync("https://pan.baidu.com/disk/home", "https://pan.baidu.com/", ct);
        home.EnsureSuccessStatusCode();
        var cookies = _cookies.GetCookies(new Uri("https://pan.baidu.com/"));
        var bduss = cookies["BDUSS"]?.Value ?? "";
        var stoken = cookies["STOKEN"]?.Value ?? "";
        var ptoken = cookies["PTOKEN"]?.Value ?? "";
        DebugLog.Write($"百度扫码会话：BDUSS={bduss.Length > 0} STOKEN={stoken.Length > 0}");
        if (bduss.Length == 0 || stoken.Length == 0)
            throw new IOException("百度登录已确认，但未取得网盘会话，请重新扫码");

        var user = "";
        try { user = await FetchUserAsync(ct); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException) { }
        return new BaiduAccount(bduss, stoken, ptoken,
            user.Length == 0 ? "百度网盘账号" : user);
    }

    private async Task<string> FetchUserAsync(CancellationToken ct)
    {
        var url = "https://pan.baidu.com/api/gettemplatevariable?clienttype=0&app_id=250528&web=1&fields=" +
            Uri.EscapeDataString("[\"bdstoken\",\"username\"]");
        using var response = await GetAsync(url, "https://pan.baidu.com/disk/home", ct);
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return Value(node?["result"] as JsonObject ?? node, "username");
    }

    public void Dispose() => _http.Dispose();
}
