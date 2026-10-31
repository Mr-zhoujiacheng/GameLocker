using System.Net;
using System.Text.Json.Nodes;

namespace GameLocker.Services;

public sealed class TianyiLoginSession : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _poll = new();
    private string _loginUrl = "";
    private string _requestId = "";
    private string _lt = "";
    /// <summary>登录成功后跳转地址（官方网页从这里读取 sessionKey）</summary>
    private string _loginRedirect = "";
    private string _loginSessionKey = "";

    public TianyiLoginSession()
    {
        _http = new HttpClient(TianyiHttp.CreateHandler(_cookies))
        { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    private async Task<JsonNode> PostAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://open.e.189.cn/api/logbox/oauth2/" + endpoint);
        request.Headers.Referrer = new Uri(_loginUrl);
        request.Headers.TryAddWithoutValidation("reqId", _requestId);
        request.Headers.TryAddWithoutValidation("lt", _lt);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new IOException("天翼云登录返回为空");
    }

    private static string Value(JsonNode? node, string name, string fallback = "") =>
        node?[name]?.ToString() ?? fallback;

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]),
            p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace("+", " ")) : "");

    public async Task<byte[]> CreateQrAsync(CancellationToken ct)
    {
        using var page = await _http.GetAsync("https://cloud.189.cn/api/portal/loginUrl.action?redirectURL=https%3A%2F%2Fcloud.189.cn%2Fweb%2Fmain%2F&pageId=1", ct);
        page.EnsureSuccessStatusCode();
        var uri = page.RequestMessage!.RequestUri!;
        _loginUrl = uri.AbsoluteUri;
        var query = Query(uri);
        if (!query.TryGetValue("appId", out var appId) || !query.TryGetValue("lt", out _lt!) || !query.TryGetValue("reqId", out _requestId!))
            throw new IOException("无法获取天翼云登录参数，请稍后重试");
        var config = await PostAsync("appConf.do", new() { ["version"] = "2.0", ["appKey"] = appId }, ct);
        var data = config["data"] ?? throw new IOException("天翼云登录配置无效");
        var qr = await PostAsync("getUUID.do", new() { ["appId"] = appId }, ct);
        if (Value(qr, "result") != "0" || Value(qr, "uuid").Length == 0 || Value(qr, "encodeuuid").Length == 0 || Value(qr, "encryuuid").Length == 0)
            throw new IOException("获取天翼云二维码失败");
        _poll.Clear();
        _poll["appId"] = appId;
        _poll["uuid"] = Value(qr, "uuid");
        _poll["encryuuid"] = Value(qr, "encryuuid");
        foreach (var name in new[] { "returnUrl", "state", "paramId" }) _poll[name] = Value(data, name);
        _poll["returnUrl"] = Uri.EscapeDataString(Value(data, "returnUrl"));
        _poll["clientType"] = "1";
        _poll["cb_SaveName"] = "1";
        _poll["isOauth2"] = Value(data, "isOauth2").Equals("false", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
        // 官方直接拼接 encodeuuid（该值本身已含百分号编码），再编码会扫出错误的地址
        var imageUrl = "https://open.e.189.cn/api/logbox/oauth2/image.do?uuid=" + Value(qr, "encodeuuid") +
            "&REQID=" + _requestId;
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        imageRequest.Headers.Referrer = uri;
        imageRequest.Headers.TryAddWithoutValidation("reqId", _requestId);
        imageRequest.Headers.TryAddWithoutValidation("lt", _lt);
        using var image = await _http.SendAsync(imageRequest, ct);
        image.EnsureSuccessStatusCode();
        if (image.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            throw new IOException("天翼云未返回有效二维码，请重试");
        return await image.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<TianyiAccount> WaitForLoginAsync(Action<string> status, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1600, ct);
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            _poll["date"] = DateTime.Now.ToString("yyyy-MM-ddHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + Random.Shared.Next(24);
            _poll["timeStamp"] = stamp;
            var result = await PostAsync("qrcodeLoginState.do", _poll, ct);
            var code = Value(result, "result") == "-20099" ? "-20099" : Value(result, "status");
            if (code == "0")
            {
                foreach (var name in new[] { "sessionKey", "sessionkey", "ssKey" })
                {
                    var value = Value(result, name, Value(result["data"], name));
                    if (value.Length > 0) { _loginSessionKey = value; break; }
                }
                var redirect = new[] { "redirectUrl", "redirectURL", "url", "targetUrl", "jumpUrl" }
                    .Select(k => Value(result, k, Value(result["data"], k))).FirstOrDefault(v => v.Length > 0);
                if (redirect is not null)
                {
                    var callback = new Uri(redirect);
                    if (callback.Scheme != "https" || !(callback.Host == "189.cn" || callback.Host.EndsWith(".189.cn", StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("天翼云返回的登录跳转地址无效");
                    using var response = await _http.GetAsync(callback, ct);
                    response.EnsureSuccessStatusCode();
                    var final = response.RequestMessage?.RequestUri;
                    _loginRedirect = final?.ToString() ?? callback.ToString();
                    if (_loginSessionKey.Length == 0)
                    {
                        var fromFinal = final is null ? "" : PickSessionKey(final);
                        _loginSessionKey = fromFinal.Length > 0 ? fromFinal : PickSessionKey(callback);
                    }
                }
                using var main = await _http.GetAsync("https://cloud.189.cn/web/main/", ct);
                main.EnsureSuccessStatusCode();
                DebugLog.Write("天翼云登录跳转：" + _loginRedirect +
                    "；重定向 sessionKey=" + (_loginSessionKey.Length > 0 ? "有" : "无"));
                status("登录成功，正在验证云端会话…");
                return await ReadAccountAsync(ct);
            }
            if (code is "-11001" or "-20099") throw new IOException("二维码已失效，请重新扫码");
            if (code == "-134") throw new IOException("账号需要额外验证，请先在天翼云官方应用完成验证后重试");
            status(code == "-11002" ? "已扫码，请在天翼云 App 确认登录" : "请使用天翼云盘 App 扫码登录");
        }
        throw new IOException("二维码已过期，请重新扫码");
    }

    private static string PickSessionKey(Uri uri)
    {
        foreach (var text in new[] { uri.Query, uri.Fragment.TrimStart('#') })
        {
            if (text.Length == 0) continue;
            foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length < 2) continue;
                var name = Uri.UnescapeDataString(parts[0]);
                if (name is not ("sessionKey" or "sessionkey" or "ssKey")) continue;
                var value = Uri.UnescapeDataString(parts[1].Replace("+", " "));
                if (value.Length > 0) return value;
            }
        }
        return "";
    }

    /// <summary>
    /// 云端文件接口只需要 cloud.189.cn 这一域的登录 cookie；把登录页/其它域的同名 cookie
    /// 一起塞进 Cookie 头会撞车，导致服务端认不出登录态。
    /// </summary>
    private string CookieHeader()
    {
        var header = _cookies.GetCookieHeader(new Uri("https://cloud.189.cn"));
        if (header.Length == 0) header = _cookies.GetCookieHeader(new Uri("https://h5.cloud.189.cn"));
        return header;
    }

    private async Task<TianyiAccount> ReadAccountAsync(CancellationToken ct)
    {
        string user = "";
        var briefSession = "";
        try
        {
            var briefText = await _http.GetStringAsync("https://cloud.189.cn/api/portal/v2/getUserBriefInfo.action?noCache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
            var brief = JsonNode.Parse(briefText);
            var resCode = Value(brief, "res_code", Value(brief, "resCode"));
            user = Find(brief, "userAccount", "nickName", "name");
            briefSession = Find(brief, "sessionKey", "session_key");
            DebugLog.Write($"天翼云账号信息：res_code={resCode} user={user} briefSessionKey={briefSession.Length}");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            DebugLog.Write($"天翼云账号信息读取失败：{ex.GetType().Name}: {ex.Message}");
        }
        var session = _loginSessionKey.Length > 0 ? _loginSessionKey : briefSession;
        if (session.Length == 0)
        {
            using var noRedirect = new HttpClient(TianyiHttp.CreateHandler(_cookies, allowAutoRedirect: false)) { Timeout = TimeSpan.FromSeconds(30) };
            var target = new Uri("https://api.cloud.189.cn/open/oauth2/ssoH5.action");
            var token = "";
            for (var i = 0; i < 12; i++)
            {
                if (Query(target).TryGetValue("accessToken", out token!) && token.Length > 0) break;
                using var response = await noRedirect.GetAsync(target, ct);
                if (response.Headers.Location is not Uri location) break;
                target = new Uri(target, location);
            }
            if (!string.IsNullOrEmpty(token))
            {
                foreach (var appId in new[] { "600100422", "8025431004" })
                {
                    var url = "https://api.cloud.189.cn/getSessionForPC.action?appId=" + appId + "&clientType=TELEPC&version=6.2&channelId=web_cloud.189.cn&rand=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "&accessToken=" + Uri.EscapeDataString(token);
                    foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
                    {
                        using var request = new HttpRequestMessage(method, url);
                        request.Headers.Accept.ParseAdd("application/json");
                        using var response = await _http.SendAsync(request, ct);
                        var text = await response.Content.ReadAsStringAsync(ct);
                        if (!response.IsSuccessStatusCode) continue;
                        if (text.TrimStart().StartsWith('<'))
                        {
                            var xml = System.Xml.Linq.XDocument.Parse(text);
                            session = xml.Descendants().FirstOrDefault(e => e.Name.LocalName is "sessionKey" or "session_key")?.Value ?? "";
                        }
                        else session = Find(JsonNode.Parse(text), "sessionKey", "session_key");
                        if (session.Length > 0) break;
                    }
                    if (session.Length > 0) break;
                }
            }
        }
        var cookies = CookieHeader();
        var names = new List<string>();
        foreach (Cookie cookie in _cookies.GetCookies(new Uri("https://cloud.189.cn"))) names.Add(cookie.Name);
        DebugLog.Write($"天翼云会话来源：{( _loginSessionKey.Length > 0 ? "重定向" : briefSession.Length > 0 ? "账号信息" : "getSessionForPC")}；cookie={string.Join(",", names)}");
        if (session.Length == 0 || cookies.Length == 0) throw new IOException("登录已确认，但未获取到天翼云文件会话，请重新扫码");
        return new TianyiAccount(cookies, session, user.Length == 0 ? "天翼云账号" : user);
    }

    private static string Find(JsonNode? node, params string[] names)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in names) if (obj[name] is JsonValue value && value.ToString().Length > 0) return value.ToString();
            foreach (var pair in obj) { var found = Find(pair.Value, names); if (found.Length > 0) return found; }
        }
        else if (node is JsonArray array)
            foreach (var item in array) { var found = Find(item, names); if (found.Length > 0) return found; }
        return "";
    }

    public void Dispose() => _http.Dispose();
}
