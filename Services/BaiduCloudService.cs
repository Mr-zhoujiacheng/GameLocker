using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameLocker.Services;

public sealed record BaiduAccount(string Bduss, string Stoken, string Ptoken, string User);
/// <summary>百度网盘文件条目；Href 为 fsId|sign|timestamp，下载时据此换取直链。</summary>
public sealed record BaiduEntry(string Href, string Name, DateTimeOffset Modified, long Size);

/// <summary>
/// 百度网盘网页版文件操作：列举 / 建目录 / 上传（预创建 + 分片 + 提交）/ 下载 / 删除。
/// 全部走网页接口（pan.baidu.com），凭据为扫码得到的 BDUSS/STOKEN/PTOKEN。
/// </summary>
public static class BaiduCloudService
{
    private const string Pan = "https://pan.baidu.com";
    private const string Common = "channel=chunlei&app_id=250528&web=1&clienttype=0";
    private const long SliceSize = 4L * 1024 * 1024;
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly HttpClient Api = new(new HttpClientHandler { UseCookies = false, UseProxy = false })
    { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly HttpClient Transfer = new(new HttpClientHandler { UseCookies = false, UseProxy = false })
    { Timeout = TimeSpan.FromHours(12) };

    public static BaiduAccount? CurrentAccount()
    {
        try
        {
            var secret = SecretProtector.Load("baidu");
            if (string.IsNullOrWhiteSpace(secret)) return null;
            var account = JsonSerializer.Deserialize<BaiduAccount>(secret, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return account is not null && !string.IsNullOrWhiteSpace(account.Bduss) &&
                !string.IsNullOrWhiteSpace(account.Stoken) ? account : null;
        }
        catch { return null; }
    }

    public static async Task<string?> TestAsync(BaiduAccount account)
    {
        try
        {
            _ = await SessionInfoAsync(account);
            _ = await ReadDirAsync(account, "/");
            return null;
        }
        catch (Exception ex)
        {
            return ex is IOException ? "连接百度网盘失败：" + ex.Message : "连接百度网盘失败，请检查网络和登录凭据。";
        }
    }

    public static async Task EnsureFolderAsync(BaiduAccount account, string path)
    {
        var current = "/";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") throw new IOException("云端目录路径不能包含 . 或 ..。");
            var listing = await ReadDirAsync(account, current);
            var exists = listing.Folders.Any(f => f.Name == part);
            var next = current.TrimEnd('/') + "/" + part;
            if (!exists)
            {
                var token = (await SessionInfoAsync(account)).Token;
                await PostFormAsync(account, Pan + "/api/create?a=commit&" + Common + "&bdstoken=" + Uri.EscapeDataString(token),
                    new Dictionary<string, string>
                    {
                        ["path"] = next, ["isdir"] = "1", ["block_list"] = "[]", ["async"] = "0"
                    }, "创建百度网盘目录");
                if (!(await ReadDirAsync(account, current)).Folders.Any(f => f.Name == part))
                    throw new IOException("创建目录后未能确认目录存在。");
            }
            current = next;
        }
    }

    public static async Task<IReadOnlyList<BaiduEntry>> ListAsync(BaiduAccount account, string path) =>
        (await ReadDirAsync(account, Normalize(path))).Files;

    public static async Task UploadAsync(BaiduAccount account, string folder, string fileName, string localPath)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0)
            throw new IOException("上传文件名无效。");
        var dir = Normalize(folder);
        var remote = dir.TrimEnd('/') + "/" + fileName;
        var token = (await SessionInfoAsync(account)).Token;

        await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var partCount = Math.Max(1L, (input.Length + SliceSize - 1) / SliceSize);
        var buffer = new byte[SliceSize];
        var blocks = new List<string>();
        for (var i = 0; i < partCount; i++)
        {
            var length = (int)Math.Min(SliceSize, input.Length - input.Position);
            await input.ReadExactlyAsync(buffer.AsMemory(0, length));
            blocks.Add(Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, length))));
        }
        var blockList = "[\"" + string.Join("\",\"", blocks) + "\"]";

        var pre = await PostFormAsync(account,
            Pan + "/api/precreate?channel=chunlei&app_id=250528&web=1&bdstoken=" + Uri.EscapeDataString(token),
            new Dictionary<string, string>
            {
                ["path"] = remote, ["size"] = Number(input.Length), ["isdir"] = "0",
                ["autoinit"] = "1", ["block_list"] = blockList, ["rtype"] = "1"
            }, "初始化百度网盘上传");
        var uploadId = First(pre, "uploadid");
        var rapid = First(pre, "return_type") == "2";

        if (!rapid)
        {
            input.Position = 0;
            for (var part = 0; part < partCount; part++)
            {
                var length = (int)Math.Min(SliceSize, input.Length - input.Position);
                await input.ReadExactlyAsync(buffer.AsMemory(0, length));
                await UploadPartAsync(account, token, remote, uploadId, buffer, length, part, fileName);
            }
        }

        await PostFormAsync(account,
            Pan + "/api/create?channel=chunlei&app_id=250528&web=1&bdstoken=" + Uri.EscapeDataString(token),
            new Dictionary<string, string>
            {
                ["path"] = remote, ["size"] = Number(input.Length), ["isdir"] = "0",
                ["block_list"] = blockList, ["uploadid"] = uploadId, ["target_path"] = dir, ["rtype"] = "1"
            }, "提交百度网盘上传");

        var uploaded = await ListAsync(account, dir);
        if (!uploaded.Any(f => f.Name == fileName && f.Size == input.Length))
            throw new IOException("上传已提交，但云端列表尚未确认备份文件，请检查百度网盘后重试。");
    }

    private static async Task UploadPartAsync(BaiduAccount account, string token, string remote, string uploadId,
        byte[] buffer, int length, int part, string fileName)
    {
        if (uploadId.Length == 0) throw new IOException("百度预上传未返回上传任务编号。");
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(buffer, 0, length);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://d.pcs.baidu.com/rest/2.0/pcs/superfile2?method=upload&type=tmpfile&app_id=250528&channel=chunlei&clienttype=0&web=1&bdstoken=" +
            Uri.EscapeDataString(token) + "&path=" + Uri.EscapeDataString(remote) +
            "&uploadid=" + Uri.EscapeDataString(uploadId) + "&partseq=" + Number(part)) { Content = content };
        Prepare(request, account);
        var payload = await SendJsonAsync(request, "上传百度网盘分片");
        EnsureOk(payload, "上传百度网盘分片");
        if (!First(payload, "md5").Equals(Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, length))),
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("百度网盘分片上传未返回匹配的校验值。");
    }

    public static async Task DownloadAsync(BaiduAccount account, string href, string localPath)
    {
        var parts = href.Split('|');
        if (parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit))
            throw new IOException("云端文件标识无效，请重新打开备份列表。");
        var token = (await SessionInfoAsync(account)).Token;
        using var signRequest = new HttpRequestMessage(HttpMethod.Get,
            Pan + "/api/gettemplatevariable?" + Common + "&fields=" +
            Uri.EscapeDataString("[\"sign1\",\"sign3\",\"timestamp\"]"));
        Prepare(signRequest, account);
        var signPayload = await SendJsonAsync(signRequest, "获取百度网盘下载签名");
        EnsureOk(signPayload, "获取百度网盘下载签名");
        var signData = signPayload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? result : signPayload;
        var sign1 = First(signData, "sign1");
        var sign3 = First(signData, "sign3");
        var timestamp = First(signData, "timestamp");
        if (sign1.Length == 0 || sign3.Length == 0 || timestamp.Length == 0)
            throw new IOException("百度网盘未返回下载签名参数，请重新登录后重试。");
        var sign = DownloadSign(sign3, sign1);
        var url = Pan + "/api/download?sign=" + Uri.EscapeDataString(sign) +
            "&timestamp=" + Uri.EscapeDataString(timestamp) + "&type=dlink&fidlist=" +
            Uri.EscapeDataString("[" + parts[0] + "]") + "&bdstoken=" + Uri.EscapeDataString(token) + "&" + Common;
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, url);
        Prepare(apiRequest, account);
        var payload = await SendJsonAsync(apiRequest, "获取百度网盘下载地址");
        EnsureOk(payload, "获取百度网盘下载地址");
        var dlink = "";
        if (payload.TryGetProperty("dlink", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (First(item, "dlink").Length > 0) { dlink = First(item, "dlink"); break; }
        if (dlink.Length == 0) throw new IOException("百度网盘未返回下载地址，请重新登录或稍后重试。");

        if (!Uri.TryCreate(dlink, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new IOException("百度网盘返回的下载地址无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Cookie", CookieHeader(account));
        request.Headers.TryAddWithoutValidation("User-Agent", "pan.baidu.com");
        request.Headers.Referrer = new Uri(Pan + "/");
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(12));
        try
        {
            using var response = await Transfer.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            EnsureHttp(response, "下载百度网盘文件");
            await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await response.Content.CopyToAsync(output, timeout.Token);
        }
        catch (HttpRequestException) { throw new IOException("下载百度网盘文件失败，请检查网络连接。"); }
        catch (OperationCanceledException) { throw new IOException("下载超时，请稍后重试。"); }
    }

    public static async Task DeleteAsync(BaiduAccount account, string folder, string href)
    {
        var parts = href.Split('|');
        if (parts.Length == 0 || parts[0].Length == 0) throw new IOException("云端文件标识无效。");
        var dir = Normalize(folder);
        var target = (await ReadDirAsync(account, dir)).Files.FirstOrDefault(f => f.Href.Split('|')[0] == parts[0]);
        if (target is null) return;
        var remote = dir.TrimEnd('/') + "/" + target.Name;
        var token = (await SessionInfoAsync(account)).Token;
        await PostFormAsync(account,
            Pan + "/api/filemanager?opera=delete&async=0&onnest=newcopy&" + Common + "&bdstoken=" + Uri.EscapeDataString(token),
            new Dictionary<string, string> { ["filelist"] = "[\"" + remote.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]" },
            "删除百度网盘文件");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1));
            if (!(await ReadDirAsync(account, dir)).Files.Any(f => f.Href.Split('|')[0] == parts[0])) return;
        }
        throw new IOException("删除请求已提交，但尚未确认文件已删除；请稍后检查网盘，不能视为删除成功。");
    }

    private sealed record Folder(string Name);
    private sealed record DirListing(List<Folder> Folders, List<BaiduEntry> Files);

    private static async Task<DirListing> ReadDirAsync(BaiduAccount account, string dir)
    {
        var token = (await SessionInfoAsync(account)).Token;
        var folders = new List<Folder>();
        var files = new List<BaiduEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 100; page++)
        {
            var url = Pan + "/api/list?order=time&desc=1&showempty=0&web=1&page=" + page +
                "&num=100&dir=" + Uri.EscapeDataString(dir) + "&bdstoken=" + Uri.EscapeDataString(token) + "&" + Common;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            Prepare(request, account);
            var payload = await SendJsonAsync(request, "列举百度网盘目录");
            EnsureOk(payload, "列举百度网盘目录");
            if (!payload.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) break;
            var count = 0;
            var added = 0;
            foreach (var item in list.EnumerateArray())
            {
                count++;
                var name = First(item, "server_filename", "filename");
                if (name.Length == 0) throw new IOException("百度网盘目录条目缺少名称。");
                var isDir = First(item, "isdir") == "1";
                var id = First(item, "fs_id");
                if (isDir)
                {
                    if (seen.Add("d:" + name)) { folders.Add(new Folder(name)); added++; }
                    continue;
                }
                if (id.Length == 0) throw new IOException("百度网盘文件条目缺少标识。");
                if (!seen.Add("f:" + id)) continue;
                _ = long.TryParse(First(item, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                var modified = DateTimeOffset.MinValue;
                if (long.TryParse(First(item, "server_mtime"), out var seconds))
                {
                    try { modified = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                    catch (ArgumentOutOfRangeException) { }
                }
                var sign = First(item, "sign");
                var stamp = First(item, "timestamp");
                files.Add(new BaiduEntry(id + "|" + sign + "|" + stamp, name, modified, Math.Max(0, size)));
                added++;
            }
            if (count < 100 || added == 0) break;
        }
        return new DirListing(folders, files);
    }

    private static async Task<(string Token, string User)> SessionInfoAsync(BaiduAccount account)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            Pan + "/api/gettemplatevariable?clienttype=0&app_id=250528&web=1&fields=" +
            Uri.EscapeDataString("[\"bdstoken\",\"username\"]"));
        Prepare(request, account);
        var payload = await SendJsonAsync(request, "获取百度网盘会话");
        EnsureOk(payload, "获取百度网盘会话");
        var data = payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? result : payload;
        var token = First(data, "bdstoken");
        if (token.Length == 0) throw new IOException("百度网盘未返回会话令牌，登录可能已失效。");
        return (token, First(data, "username"));
    }

    private static void Prepare(HttpRequestMessage request, BaiduAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Bduss) || string.IsNullOrWhiteSpace(account.Stoken))
            throw new IOException("百度网盘登录凭据不完整，请重新登录。");
        foreach (var value in new[] { account.Bduss, account.Stoken, account.Ptoken })
            if (value.Contains('\r') || value.Contains('\n')) throw new IOException("百度网盘登录凭据格式无效，请重新登录。");
        request.Headers.Add("Cookie", CookieHeader(account));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri(Pan + "/disk/home");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
    }

    private static string CookieHeader(BaiduAccount account)
    {
        var value = "BDUSS=" + account.Bduss + "; STOKEN=" + account.Stoken;
        return account.Ptoken.Length == 0 ? value : value + "; PTOKEN=" + account.Ptoken;
    }

    private static async Task<JsonElement> PostFormAsync(BaiduAccount account, string url,
        Dictionary<string, string> form, string action)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new FormUrlEncodedContent(form) };
        Prepare(request, account);
        var payload = await SendJsonAsync(request, action);
        EnsureOk(payload, action);
        return payload;
    }

    private static async Task<JsonElement> SendJsonAsync(HttpRequestMessage request, string action)
    {
        try
        {
            using var response = await Api.SendAsync(request);
            EnsureHttp(response, action);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException(action + "返回格式异常。");
            return document.RootElement.Clone();
        }
        catch (HttpRequestException) { throw new IOException(action + "网络请求失败，请检查网络连接。"); }
        catch (OperationCanceledException) { throw new IOException(action + "请求超时，请稍后重试。"); }
        catch (JsonException) { throw new IOException(action + "返回非 JSON 数据，登录可能已失效。"); }
    }

    private static void EnsureHttp(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        throw new IOException(action + (code is 401 or 403
            ? "失败：登录已失效或无权限，请重新登录。"
            : $"失败：服务器返回 HTTP {code}。"));
    }

    private static void EnsureOk(JsonElement payload, string action)
    {
        var errno = First(payload, "errno", "error_code");
        if (errno.Length == 0 || errno == "0") return;
        throw new IOException(action + (errno switch
        {
            "-6" or "-9" => "失败：登录已失效，请重新登录。",
            "-8" => "失败：百度网盘空间不足。",
            _ => "失败：网盘拒绝了请求（errno=" + errno + "），请检查登录状态、权限或剩余空间。"
        }));
    }

    private static string Normalize(string path)
    {
        var text = (path ?? "").Replace('\\', '/').Trim();
        if (text.Length == 0) return "/";
        return "/" + string.Join("/", text.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string First(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return "";
    }

    private static string DownloadSign(string key, string text)
    {
        var state = Enumerable.Range(0, 256).ToArray();
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        var output = new byte[text.Length];
        var x = 0;
        j = 0;
        for (var n = 0; n < text.Length; n++)
        {
            x = (x + 1) & 255;
            j = (j + state[x]) & 255;
            (state[x], state[j]) = (state[j], state[x]);
            output[n] = (byte)(text[n] ^ state[(state[x] + state[j]) & 255]);
        }
        return Convert.ToBase64String(output);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
