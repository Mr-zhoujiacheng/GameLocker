using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace GameLocker.Services;

public sealed record TianyiAccount(string Cookies, string SessionKey, string User);
public sealed record TianyiEntry(string Href, string Name, DateTimeOffset Modified, long Size);

public static class TianyiCloudService
{
    private const string WebUrl = "https://cloud.189.cn";
    private const string AppKey = "600100422";
    private const string RootId = "-11";
    private const int SliceSize = 10 * 1024 * 1024;
    private static readonly HttpClient Api = new(TianyiHttp.CreateHandler()) { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly HttpClient Transfer = new(TianyiHttp.CreateHandler()) { Timeout = TimeSpan.FromHours(12) };

    public static TianyiAccount? CurrentAccount()
    {
        try
        {
            var secret = SecretProtector.Load();
            if (string.IsNullOrWhiteSpace(secret)) return null;
            var account = JsonSerializer.Deserialize<TianyiAccount>(secret, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return account is not null && !string.IsNullOrWhiteSpace(account.Cookies) &&
                !string.IsNullOrWhiteSpace(account.SessionKey) ? account : null;
        }
        catch { return null; }
    }

    public static async Task<string?> TestAsync(TianyiAccount account)
    {
        try
        {
            await ReadFolderAsync(account, RootId);
            return null;
        }
        catch (Exception ex)
        {
            return ex is IOException ? "连接天翼云盘失败：" + ex.Message : "连接天翼云盘失败，请检查网络和登录凭据。";
        }
    }

    public static async Task EnsureFolderAsync(TianyiAccount account, string path) =>
        _ = await ResolveFolderAsync(account, path, true);

    public static async Task<IReadOnlyList<TianyiEntry>> ListAsync(TianyiAccount account, string path)
    {
        var folderId = await ResolveFolderAsync(account, path, false);
        return (await ReadFolderAsync(account, folderId)).Files;
    }

    public static async Task UploadAsync(TianyiAccount account, string path, string fileName, string localPath)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0)
            throw new IOException("上传文件名无效。");
        var folderId = await ResolveFolderAsync(account, path, true);
        await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var keyRequest = NewApiRequest(account, WebUrl + "/api/security/generateRsaKey.action?noCache=" + Timestamp());
        var keyPayload = await SendJsonAsync(keyRequest, "获取上传公钥");
        CheckSuccess(keyPayload, "获取上传公钥");
        var keyData = Data(keyPayload);
        var publicKey = First(keyPayload, "pubKey");
        if (publicKey.Length == 0) publicKey = First(keyData, "pubKey");
        var pkId = First(keyPayload, "pkId");
        if (pkId.Length == 0) pkId = First(keyData, "pkId");
        if (publicKey.Length == 0 || pkId.Length == 0) throw new IOException("上传公钥响应缺少 pubKey/pkId。");
        using var rsa = RSA.Create();
        try
        {
            if (publicKey.Contains("-----BEGIN", StringComparison.Ordinal)) rsa.ImportFromPem(publicKey);
            else rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new IOException("天翼云盘返回的上传公钥格式无效。"); }

        var init = await UploadControlAsync(account, rsa, pkId, "/person/initMultiUpload", new()
        {
            ["parentFolderId"] = folderId, ["fileName"] = Uri.EscapeDataString(fileName),
            ["fileSize"] = Number(input.Length), ["sliceSize"] = Number(SliceSize), ["lazyCheck"] = "1"
        });
        var uploadId = First(Data(init), "uploadFileId");
        if (uploadId.Length == 0) uploadId = First(init, "uploadFileId");
        if (uploadId.Length == 0) throw new IOException("初始化上传未返回 uploadFileId。");
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var partHashes = new List<string>();
        var buffer = new byte[SliceSize];
        var partCount = Math.Max(1L, (input.Length + SliceSize - 1) / SliceSize);
        for (long part = 1; part <= partCount; part++)
        {
            var length = (int)Math.Min(SliceSize, input.Length - input.Position);
            await input.ReadExactlyAsync(buffer.AsMemory(0, length));
            var partHash = MD5.HashData(buffer.AsSpan(0, length));
            partHashes.Add(Convert.ToHexString(partHash));
            fileHash.AppendData(buffer, 0, length);
            var urls = await UploadControlAsync(account, rsa, pkId, "/person/getMultiUploadUrls", new()
            {
                ["partInfo"] = Number(part) + "-" + Convert.ToBase64String(partHash), ["uploadFileId"] = uploadId
            });
            var uploadUrls = Property(urls, "uploadUrls");
            if (uploadUrls.ValueKind != JsonValueKind.Object) uploadUrls = Property(Data(urls), "uploadUrls");
            var details = Property(uploadUrls, "partNumber_" + Number(part));
            var url = First(details, "requestURL");
            using var request = new HttpRequestMessage(HttpMethod.Put, TransferUri(url));
            request.Content = new ByteArrayContent(buffer, 0, length);
            var headers = Uri.UnescapeDataString(First(details, "requestHeader"));
            foreach (var pair in headers.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                var name = pair[..separator];
                var value = pair[(separator + 1)..];
                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
            await SendTransferAsync(request, "上传第 " + Number(part) + " 个分片");
        }
        var md5 = Hex(fileHash.GetHashAndReset());
        var sliceMd5 = input.Length <= SliceSize ? md5 : Md5(string.Join("\n", partHashes));
        await UploadControlAsync(account, rsa, pkId, "/person/commitMultiUploadFile", new()
        {
            ["uploadFileId"] = uploadId, ["fileMd5"] = md5, ["sliceMd5"] = sliceMd5,
            ["lazyCheck"] = "1", ["opertype"] = "3"
        });
        var uploaded = (await ReadFolderAsync(account, folderId)).Files;
        if (!uploaded.Any(f => f.Name == fileName && f.Size == input.Length))
            throw new IOException("上传已提交，但云端列表尚未确认备份文件，请检查天翼云盘后重试。");
    }

    public static async Task DownloadAsync(TianyiAccount account, string fileId, string localPath)
    {
        ValidateId(fileId);
        var payload = await OpenAsync(account, "/api/open/file/getFileDownloadUrl.action", new()
        { ["fileId"] = fileId, ["dt"] = "1" }, "获取下载地址");
        var url = First(payload, "fileDownloadUrl", "downloadUrl", "url");
        if (url.Length == 0) url = First(Data(payload), "fileDownloadUrl", "downloadUrl", "url");
        using var request = new HttpRequestMessage(HttpMethod.Get, TransferUri(url));
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(12));
        try
        {
            using var response = await Transfer.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            CheckHttp(response, "下载文件");
            await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await response.Content.CopyToAsync(output, timeout.Token);
        }
        catch (HttpRequestException) { throw new IOException("下载文件失败，请检查网络连接。"); }
        catch (OperationCanceledException) { throw new IOException("下载文件超时，请稍后重试。"); }
    }

    public static async Task DeleteAsync(TianyiAccount account, string path, string fileId)
    {
        ValidateId(fileId);
        var parentId = await ResolveFolderAsync(account, path, false);
        var target = (await ReadFolderAsync(account, parentId)).Files.FirstOrDefault(f => f.Href == fileId);
        if (target is null) return;
        var taskInfos = JsonSerializer.Serialize(new[] { new { fileId, fileName = target.Name, isFolder = 0 } });
        var result = await OpenAsync(account, "/api/open/batch/createBatchTask.action", new()
        { ["type"] = "DELETE", ["taskInfos"] = taskInfos }, "删除云端文件", true);
        RequireSuccessCode(result, "删除云端文件");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1));
            if (!(await ReadFolderAsync(account, parentId)).Files.Any(f => f.Href == fileId)) return;
        }
        throw new IOException("删除请求已提交，但尚未确认文件已删除；请稍后检查云盘，不能视为删除成功。");
    }

    private sealed record Folder(string Id, string Name);
    private sealed record FolderListing(List<Folder> Folders, List<TianyiEntry> Files);

    private static async Task<string> ResolveFolderAsync(TianyiAccount account, string path, bool create)
    {
        var current = RootId;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") throw new IOException("云端目录路径不能包含 . 或 ..。");
            var listing = await ReadFolderAsync(account, current);
            var folder = listing.Folders.FirstOrDefault(f => f.Name == part);
            if (folder is null && create)
            {
                await OpenAsync(account, "/api/open/file/createFolder.action", new()
                { ["folderName"] = part, ["parentFolderId"] = current }, "创建云端目录", true);
                folder = (await ReadFolderAsync(account, current)).Folders.FirstOrDefault(f => f.Name == part);
            }
            if (folder is null) throw new IOException(create ? "创建云端目录后未能确认目录存在。" : "云端目录不存在。");
            current = folder.Id;
        }
        return current;
    }

    private static async Task<FolderListing> ReadFolderAsync(TianyiAccount account, string folderId)
    {
        var folders = new List<Folder>();
        var files = new List<TianyiEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            var payload = await OpenAsync(account, "/api/open/file/listFiles.action", new()
            {
                ["pageSize"] = "60", ["pageNum"] = Number(page), ["mediaType"] = "0", ["folderId"] = folderId,
                ["iconOption"] = "5", ["orderBy"] = "lastOpTime", ["descending"] = "true"
            }, "列举云端目录");
            var listing = Property(payload, "fileListAO");
            JsonElement folderArray, fileArray;
            if (listing.ValueKind == JsonValueKind.Object)
            {
                folderArray = Property(listing, "folderList");
                fileArray = Property(listing, "fileList");
            }
            else
            {
                // XML 形态：<listFiles><fileList><count>…</count><folder>…</folder><file>…</file></fileList></listFiles>
                listing = Property(payload, "fileList");
                if (listing.ValueKind != JsonValueKind.Object) throw new IOException("云端目录响应缺少文件列表，无法读取。");
                folderArray = Property(listing, "folder");
                fileArray = Property(listing, "file");
            }
            if (folderArray.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object) &&
                fileArray.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            {
                // 空目录的响应不会带 folderList/fileList，只给 count=0，属正常情况
                var totalItems = 0L;
                _ = long.TryParse(First(listing, "count", "totalCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out totalItems);
                if (totalItems > 0) throw new IOException("云端目录响应缺少目录和文件列表。");
            }
            var count = 0;
            var added = 0;
            foreach (var item in Items(folderArray))
            {
                count++;
                var id = First(item, "id", "folderId", "fileId", "fileID");
                var name = First(item, "name", "folderName", "fileName");
                if (id.Length == 0 || name.Length == 0) throw new IOException("云端目录条目缺少名称或标识。");
                if (seen.Add("d:" + id)) { folders.Add(new Folder(id, name)); added++; }
            }
            foreach (var item in Items(fileArray))
            {
                count++;
                var id = First(item, "id", "fileId", "fileID");
                var name = First(item, "name", "fileName");
                if (id.Length == 0 || name.Length == 0) throw new IOException("云端文件条目缺少名称或标识。");
                if (seen.Add("f:" + id))
                {
                    _ = long.TryParse(First(item, "size", "fileSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    files.Add(new TianyiEntry(id, name, Modified(item), Math.Max(0, size)));
                    added++;
                }
            }
            _ = long.TryParse(First(listing, "count", "totalCount"), out var total);
            if (count == 0)
            {
                if (total > seen.Count) throw new IOException("云端目录分页提前结束，文件列表不完整。");
                break;
            }
            if (added == 0) throw new IOException("云端目录分页重复，无法确认完整文件列表。");
            if (total > 0 && seen.Count >= total) break;
            if (count < 60 && total <= 0) break;
        }
        return new FolderListing(folders, files);
    }

    private static DateTimeOffset Modified(JsonElement item)
    {
        var text = First(item, "lastOpTime", "lastUpdateTime", "modifyDate", "createDate");
        if (long.TryParse(text, out var timestamp))
        {
            try { return timestamp > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : DateTimeOffset.FromUnixTimeSeconds(timestamp); }
            catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
        }
        // 网页端的无时区时间为北京时间，不能随本机时区改变。
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) && local.Kind == DateTimeKind.Unspecified)
            return new DateTimeOffset(local, TimeSpan.FromHours(8));
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : DateTimeOffset.MinValue;
    }

    private static async Task<JsonElement> OpenAsync(TianyiAccount account, string endpoint,
        Dictionary<string, string> parameters, string action, bool post = false)
    {
        var timestamp = Timestamp();
        var signed = new Dictionary<string, string>(parameters) { ["Timestamp"] = timestamp, ["AppKey"] = AppKey };
        var signature = Md5(string.Join("&", signed.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)));
        var query = post ? new Dictionary<string, string>() : new Dictionary<string, string>(parameters);
        query["noCache"] = timestamp;
        query["sessionKey"] = account.SessionKey;
        using var request = NewApiRequest(account, WebUrl + endpoint + "?" + Query(query));
        request.Method = post ? HttpMethod.Post : HttpMethod.Get;
        request.Headers.Add("Sign-Type", "1");
        request.Headers.Add("Signature", signature);
        request.Headers.Add("Timestamp", timestamp);
        request.Headers.Add("AppKey", AppKey);
        if (post) request.Content = new FormUrlEncodedContent(parameters);
        var payload = await SendJsonAsync(request, action);
        CheckSuccess(payload, action);
        return payload;
    }

    private static async Task<JsonElement> UploadControlAsync(TianyiAccount account, RSA rsa, string pkId,
        string uri, Dictionary<string, string> parameters)
    {
        var seed = Guid.NewGuid().ToString("N")[..24];
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(seed[..16]);
        var form = string.Join("&", parameters.Select(p => p.Key + "=" + p.Value));
        var encrypted = Hex(aes.EncryptEcb(Encoding.UTF8.GetBytes(form), PaddingMode.PKCS7));
        var timestamp = Timestamp();
        var signText = $"SessionKey={account.SessionKey}&Operate=GET&RequestURI={uri}&Date={timestamp}&params={encrypted}";
        var signature = Hex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(seed), Encoding.UTF8.GetBytes(signText)));
        using var request = NewApiRequest(account, "https://upload.cloud.189.cn" + uri + "?params=" + encrypted);
        request.Headers.Add("SessionKey", account.SessionKey);
        request.Headers.Add("Signature", signature);
        request.Headers.Add("X-Request-Date", timestamp);
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        request.Headers.Add("EncryptionText", Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(seed), RSAEncryptionPadding.Pkcs1)));
        request.Headers.Add("PkId", pkId);
        var payload = await SendJsonAsync(request, "上传控制请求");
        if (First(payload, "code") != "SUCCESS") throw new IOException("上传控制请求未返回 SUCCESS，上传未完成。");
        return payload;
    }

    private static HttpRequestMessage NewApiRequest(TianyiAccount account, string url)
    {
        if (string.IsNullOrWhiteSpace(account.Cookies) || string.IsNullOrWhiteSpace(account.SessionKey))
            throw new IOException("天翼云盘登录凭据不完整，请重新登录。");
        if (account.Cookies.Contains('\r') || account.Cookies.Contains('\n') || account.SessionKey.Contains('\r') || account.SessionKey.Contains('\n'))
            throw new IOException("天翼云盘登录凭据格式无效，请重新登录。");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", account.Cookies);
        // 云端接口对非浏览器请求很敏感：对齐官方网页的 UA / Accept / Browser-Id，避免被当成爬虫返回 HTML
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        request.Headers.Referrer = new Uri(WebUrl + "/web/main/");
        request.Headers.Accept.ParseAdd("application/json;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("Origin", WebUrl);
        request.Headers.TryAddWithoutValidation("Browser-Id", BrowserId);
        return request;
    }

    private static readonly string BrowserId = Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> SendJsonAsync(HttpRequestMessage request, string action)
    {
        try
        {
            using var response = await Api.SendAsync(request);
            CheckHttp(response, action);
            var text = await response.Content.ReadAsStringAsync();
            if (text.TrimStart().StartsWith('<'))
            {
                // 网页登录态下的云端接口（如 listFiles/createFolder）实际按 XML 返回
                JsonElement payload;
                try { payload = XmlPayload(text); }
                catch (Exception ex) when (ex is System.Xml.XmlException or JsonException)
                { throw new IOException(action + "返回无法解析的内容（HTTP " + (int)response.StatusCode + "）。"); }
                if (payload.ValueKind != JsonValueKind.Object) throw new IOException(action + "返回格式异常。");
                return payload;
            }
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException(action + "返回格式异常。");
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                var head = text.Length <= 200 ? text : text[..200];
                DebugLog.Write($"云端接口 {action} 返回非 JSON：HTTP {(int)response.StatusCode} " +
                    response.Content.Headers.ContentType + " " + head.Replace("\r", "\\r").Replace("\n", "\\n"));
                throw new IOException(action + "返回非 JSON 数据（HTTP " + (int)response.StatusCode + "），登录可能已失效。");
            }
        }
        catch (HttpRequestException) { throw new IOException(action + "网络请求失败，请检查网络连接。"); }
        catch (OperationCanceledException) { throw new IOException(action + "请求超时，请稍后重试。"); }
    }

    /// <summary>把根元素展开成顶层字段，XML 与网页 JSON 的读取代码就能共用一套字段名。</summary>
    private static JsonElement XmlPayload(string text)
    {
        var root = XDocument.Parse(text).Root ?? throw new JsonException("XML 内容为空");
        var node = XmlNode(root);
        if (node is JsonValue) node = new JsonObject { [root.Name.LocalName] = node };
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode XmlNode(XElement element)
    {
        var children = element.Elements().ToList();
        var obj = new JsonObject();
        foreach (var attribute in element.Attributes())
            if (!obj.ContainsKey(attribute.Name.LocalName)) obj[attribute.Name.LocalName] = attribute.Value;
        if (children.Count == 0)
            return obj.Count == 0 ? JsonValue.Create(element.Value.Trim()) : obj;
        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var items = group.Select(XmlNode).ToList();
            obj[group.Key] = items.Count == 1 ? items[0] : new JsonArray(items.ToArray());
        }
        return obj;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().ToArray(),
        JsonValueKind.Object => [element],
        _ => []
    };

    private static async Task SendTransferAsync(HttpRequestMessage request, string action)
    {
        try
        {
            using var response = await Transfer.SendAsync(request);
            CheckHttp(response, action);
        }
        catch (HttpRequestException) { throw new IOException(action + "网络请求失败，请重试。"); }
        catch (OperationCanceledException) { throw new IOException(action + "超时，请重试。"); }
    }

    private static void CheckHttp(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        throw new IOException(action + (code is 401 or 403 ? "失败：登录已失效或无权限，请重新登录。" : $"失败：服务器返回 HTTP {code}。"));
    }

    private static void CheckSuccess(JsonElement payload, string action)
    {
        var code = First(payload, "res_code", "resCode", "code", "errorCode");
        if (code.Length > 0 && code is not ("0" or "SUCCESS" or "success"))
            throw new IOException(action + "失败：天翼云盘拒绝了请求，请检查登录状态、权限或剩余空间。");
        if (Property(payload, "success").ValueKind == JsonValueKind.False)
            throw new IOException(action + "失败：天翼云盘返回操作失败。");
    }

    private static void RequireSuccessCode(JsonElement payload, string action)
    {
        CheckSuccess(payload, action);
        var code = First(payload, "res_code", "resCode", "code", "errorCode");
        // XML 形态的批量任务接口用 message/SUCCESS 或 batchTaskId 表示已受理
        var message = First(payload, "message", "msg", "res_message");
        var taskId = First(payload, "batchTaskId", "taskId");
        if (code.Length == 0 && message.Length == 0 && taskId.Length == 0)
            throw new IOException(action + "未返回可验证的成功状态，不能确认操作已接受。");
        if (message.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            throw new IOException(action + "失败：天翼云盘返回操作失败。");
    }

    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    private static JsonElement Data(JsonElement item) => Property(item, "data");
    private static string First(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Property(item, name);
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return "";
    }

    private static Uri TransferUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new IOException("天翼云盘返回的文件传输地址无效。");
        return uri;
    }

    private static void ValidateId(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId)) throw new IOException("云端文件标识不能为空。");
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Timestamp() => Number(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static string Md5(string text) => Hex(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Query(Dictionary<string, string> values) => string.Join("&",
        values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
}

/// <summary>
/// 天翼云把登录会话和登录时的出口 IP 绑定：登录走 IPv4，后续请求若落到 IPv6 会报
/// "check ip error"。这里统一强制 IPv4 解析与连接，保证前后一致。
/// </summary>
internal static class TianyiHttp
{
    public static SocketsHttpHandler CreateHandler(CookieContainer? cookies = null, bool allowAutoRedirect = true) => new()
    {
        UseProxy = false,
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        AllowAutoRedirect = allowAutoRedirect,
        ConnectCallback = ConnectIpv4Async
    };

    private static async ValueTask<Stream> ConnectIpv4Async(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new IOException("无法解析天翼云服务器地址。");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
