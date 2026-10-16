using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace GameLocker.Services;

/// <summary>WebDAV 连接参数：服务器地址 + 账号 + 应用密码（应用密码经 DPAPI 加密存于本机）</summary>
public readonly record struct WebDavAccount(string BaseUrl, string User, string Password);

/// <summary>WebDAV 上的一个文件（Href 为可直接请求的绝对 URL，删除时直接用）</summary>
public readonly record struct WebDavEntry(string Href, DateTimeOffset Modified, long Size);

/// <summary>
/// 云端备份的 WebDAV 实现（默认坚果云 https://dav.jianguoyun.com/dav/，也兼容群晖 / Nextcloud 等自建网盘）。
/// 只用 HTTP Basic 认证 + 五个标准方法：MKCOL 建目录、PUT 上传、GET 下载、PROPFIND 列举、DELETE 删除；
/// 不需要注册任何开发者应用，也不依赖第三方 SDK。
/// </summary>
public static class WebDavService
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>PROPFIND 请求体：只要最后修改时间、大小与资源类型</summary>
    private const string PropFindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:getlastmodified/><d:getcontentlength/><d:resourcetype/>" +
        "</d:prop></d:propfind>";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // 部分服务端会拒绝无 User-Agent 的请求，统一带上标识
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GameLocker/1.0");
        return c;
    }

    /// <summary>按「地址 + 账号 + 密码」组装参数（自动补地址结尾的 /）；任意一项为空返回 null</summary>
    public static WebDavAccount? BuildAccount(string? url, string? user, string? password)
    {
        var u = url?.Trim() ?? "";
        var n = user?.Trim() ?? "";
        if (u.Length == 0 || n.Length == 0 || string.IsNullOrEmpty(password)) return null;
        if (!u.EndsWith('/')) u += "/";
        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return null;
        return new WebDavAccount(u, n, password);
    }

    /// <summary>读取当前设置 + 本机加密凭据组装连接参数；未配置完整返回 null</summary>
    public static WebDavAccount? CurrentAccount()
    {
        var s = SettingsService.Current;
        return BuildAccount(s.WebDavUrl, s.WebDavUser, SecretProtector.Load());
    }

    /// <summary>测试连接：PROPFIND 服务器根地址；返回 null 表示可用，否则为中文错误文案</summary>
    public static async Task<string?> TestAsync(WebDavAccount acct)
    {
        try
        {
            using var req = NewRequest(new HttpMethod("PROPFIND"), acct.BaseUrl, acct);
            req.Headers.Add("Depth", "0");
            req.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
            using var resp = await Http.SendAsync(req);
            if (IsOk(resp) || (int)resp.StatusCode == 207) return null;
            return "连接失败：" + Reason(resp);
        }
        catch (Exception ex)
        {
            return "连接失败：" + ex.Message;
        }
    }

    /// <summary>逐段创建目录（已存在视为成功）</summary>
    public static async Task EnsureFolderAsync(WebDavAccount acct, string path)
    {
        var cur = "";
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur.Length == 0 ? seg : cur + "/" + seg;
            using var req = NewRequest(new HttpMethod("MKCOL"), FolderUrl(acct, cur), acct);
            using var resp = await Http.SendAsync(req);
            var code = (int)resp.StatusCode;
            // 201=新建成功；405/409=目录已存在；301/302=地址被规范化重定向，均视为成功
            if (IsOk(resp) || code is 301 or 302 or 405 or 409) continue;
            throw Fail(resp, $"创建云端目录 {seg} 失败");
        }
    }

    /// <summary>上传本地文件到 path/fileName（PUT，同名覆盖）</summary>
    public static async Task UploadAsync(WebDavAccount acct, string path, string fileName, string localFile)
    {
        await using var fs = File.OpenRead(localFile);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var req = NewRequest(HttpMethod.Put, FileUrl(acct, path, fileName), acct);
        req.Content = content;
        using var resp = await Http.SendAsync(req);
        if (IsOk(resp)) return;
        throw Fail(resp, "上传失败");
    }

    /// <summary>下载云端文件到本地（GET）；文件不存在等失败时抛出带中文原因的异常</summary>
    public static async Task DownloadAsync(WebDavAccount acct, string href, string localFile)
    {
        using var req = NewRequest(HttpMethod.Get, href, acct);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!IsOk(resp)) throw Fail(resp, "下载失败");
        await using var fs = File.Create(localFile);
        await resp.Content.CopyToAsync(fs);
    }

    /// <summary>列举目录下的文件（不含目录自身与子目录），用于按保留份数裁剪旧备份</summary>
    public static async Task<List<WebDavEntry>> ListAsync(WebDavAccount acct, string path)
    {
        using var req = NewRequest(new HttpMethod("PROPFIND"), FolderUrl(acct, path), acct);
        req.Headers.Add("Depth", "1");
        req.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
        using var resp = await Http.SendAsync(req);
        if ((int)resp.StatusCode != 207 && !IsOk(resp))
            throw Fail(resp, "列举云端目录失败");

        var list = new List<WebDavEntry>();
        XNamespace d = "DAV:";
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var r in doc.Descendants(d + "response"))
        {
            var prop = r.Descendants(d + "prop").FirstOrDefault();
            if (prop is null) continue;
            if (prop.Element(d + "resourcetype")?.Element(d + "collection") is not null) continue; // 目录跳过

            var href = r.Element(d + "href")?.Value ?? "";
            if (href.Length == 0) continue;

            var modified = DateTimeOffset.MinValue;
            if (DateTimeOffset.TryParse(prop.Element(d + "getlastmodified")?.Value, out var t)) modified = t;
            _ = long.TryParse(prop.Element(d + "getcontentlength")?.Value, out var size);

            list.Add(new WebDavEntry(Absolute(acct, href), modified, size));
        }
        return list;
    }

    /// <summary>删除（按 href 直接删；404 视为已删除）</summary>
    public static async Task DeleteAsync(WebDavAccount acct, string href)
    {
        using var req = NewRequest(HttpMethod.Delete, href, acct);
        using var resp = await Http.SendAsync(req);
        if (IsOk(resp) || (int)resp.StatusCode == 404) return;
        throw Fail(resp, "删除云端旧备份失败");
    }

    // ---- 内部工具 ----

    private static bool IsOk(HttpResponseMessage resp) =>
        (int)resp.StatusCode is >= 200 and < 300;

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, WebDavAccount acct)
    {
        var req = new HttpRequestMessage(method, url);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{acct.User}:{acct.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return req;
    }

    /// <summary>目录 URL（结尾带 /）</summary>
    private static string FolderUrl(WebDavAccount acct, string path) => Build(acct, path) + "/";

    /// <summary>文件 URL（结尾不带 /）</summary>
    private static string FileUrl(WebDavAccount acct, string path, string fileName) =>
        Build(acct, path) + "/" + Uri.EscapeDataString(fileName);

    private static string Build(WebDavAccount acct, string path)
    {
        var b = acct.BaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path)) return b;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                       .Select(Uri.EscapeDataString);
        return b + "/" + string.Join('/', segs);
    }

    /// <summary>把 PROPFIND 返回的 href（可能是绝对 URL、也可能只有路径）转成可请求的绝对 URL</summary>
    private static string Absolute(WebDavAccount acct, string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var abs)
            ? abs.ToString()
            : new Uri(new Uri(acct.BaseUrl), href).ToString();

    /// <summary>把 HTTP 状态码翻译成用户可读的中文原因</summary>
    private static string Reason(HttpResponseMessage resp) => (int)resp.StatusCode switch
    {
        401 => "认证失败（401）：账号或应用密码不正确",
        403 => "无权限（403）：该账号不允许此操作",
        404 => "路径不存在（404）：请检查服务器地址是否正确",
        405 => "服务端不支持该操作（405）：可能不是 WebDAV 地址",
        409 => "目录冲突（409）",
        507 => "网盘空间不足（507）",
        _ => $"服务器返回 {(int)resp.StatusCode}"
    };

    private static IOException Fail(HttpResponseMessage resp, string action) =>
        new($"{action}：{Reason(resp)}");
}
