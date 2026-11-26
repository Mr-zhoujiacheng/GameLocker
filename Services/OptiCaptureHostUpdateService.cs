using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLocker.Services;

internal sealed record OptiCaptureHostRelease(Version Version, string Tag, Uri DownloadUri, long Size, string Sha256)
{
    /// <summary>api.github.com 的资产端点（会 302 到 release-assets.githubusercontent.com）。
    /// 部分网络能访问 API 却连不上 github.com，下载时优先用它，直链只作兜底。</summary>
    internal Uri? ApiAssetUri { get; init; }
}

/// <summary>Downloads and activates official OptiCaptureHost runtimes without changing an existing runtime in place.</summary>
internal sealed class OptiCaptureHostUpdateService
{
    private const string ReleaseRoot = "https://github.com/tegraultra/OptiCaptureHost/releases/download/";
    private const string AssetApiRoot = "https://api.github.com/repos/tegraultra/OptiCaptureHost/releases/assets/";

    /// <summary>
    /// 上游全量包资产名。上游改过命名（<c>OptiCaptureHost-2.10.80_Full.Pack.rar</c> →
    /// <c>OptiCaptureHost_v2.12.00_Full.Pack.rar</c> → <c>OptiCaptureHost_v2.12.68_HotFIX_Full.Pack.rar</c>），
    /// 所以这里只认「版本号 + Full.Pack + 压缩后缀」，不绑定前缀分隔符和 HotFIX 标记。
    /// </summary>
    private static readonly Regex FullPackageName = new(
        @"^OptiCaptureHost[-_]v?\.?(?<version>\d+\.\d+\.\d+)(?:_HotFIX)?_Full\.Pack\.(?:rar|zip|7z)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string ActiveFile = "active-runtime.txt";
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private const int MaxMetadataBytes = 1024 * 1024;
    private static readonly Version LegacyVersion = new(2, 10, 80);
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/tegraultra/OptiCaptureHost/releases/latest");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _root;
    private readonly HttpClient _http;

    internal OptiCaptureHostUpdateService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLocker", "OptiCaptureHost"), Http)
    {
    }

    // The supplied test client must not follow redirects automatically.
    internal OptiCaptureHostUpdateService(string root, HttpClient http)
    {
        _root = Path.GetFullPath(root);
        _http = http;
    }

    internal string? GetInstalledDirectory() => GetInstalled()?.Directory;
    internal Version? GetInstalledVersion() => GetInstalled()?.Version;
    internal bool IsFullRuntimeInstalled() => GetInstalled() is { } installed && OptiCaptureHostPackage.HasFullRuntime(installed.Directory);

    internal async Task<OptiCaptureHostRelease> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = CreateRequest(LatestReleaseUri, "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxMetadataBytes)
            throw new InvalidDataException("OptiCaptureHost release metadata is too large.");
        await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var payload = new MemoryStream();
        await ReadBoundedAsync(responseStream, payload, MaxMetadataBytes, null, timeout.Token).ConfigureAwait(false);
        payload.Position = 0;
        using var metadata = await JsonDocument.ParseAsync(payload, cancellationToken: timeout.Token).ConfigureAwait(false);
        return ReadRelease(metadata.RootElement);
    }

    internal async Task<string> InstallAsync(OptiCaptureHostRelease release, Action<long, long?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        // Held for the whole transaction, including the installed-version check. A second writer cannot downgrade us.
        using var installLock = new FileStream(Path.Combine(_root, ".install.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var installed = GetInstalled();
        if (installed is { } current && (current.Version > release.Version ||
            (current.Version == release.Version && OptiCaptureHostPackage.HasFullRuntime(current.Directory))))
            return current.Directory;

        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(_root, ".staging-" + id);
        var pointerTemp = Path.Combine(stage, "activate.tmp");
        var installedName = release.Tag + "-" + id;
        var destination = Path.Combine(_root, installedName);
        var published = false;
        try
        {
            Directory.CreateDirectory(stage);
            var archive = Path.Combine(stage, "download" + Path.GetExtension(release.DownloadUri.AbsolutePath));
            await DownloadAsync(release, archive, onProgress, cancellationToken).ConfigureAwait(false);
            var unpack = Path.Combine(stage, "unpack");
            var runtime = await Task.Run(() => OptiCaptureHostPackage.Prepare(archive, unpack, installed?.Directory,
                release.Version, cancellationToken), cancellationToken).ConfigureAwait(false);

            File.WriteAllText(pointerTemp, installedName);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(runtime, destination);
            published = true;
            // Both moves are within _root; the old pointer remains intact up to this commit point.
            File.Move(pointerTemp, Path.Combine(_root, ActiveFile), overwrite: true);
            published = false;
            return destination;
        }
        finally
        {
            if (published) TryDeleteDirectory(destination);
            TryDeleteDirectory(stage);
        }
    }

    private (string Directory, Version Version)? GetInstalled()
    {
        try
        {
            using var file = new FileStream(Path.Combine(_root, ActiveFile), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (file.Length is > 0 and <= 128)
            {
                using var reader = new StreamReader(file);
                var name = reader.ReadToEnd();
                var split = name.IndexOf('-');
                if (split > 0 && Guid.TryParseExact(name[(split + 1)..], "N", out _) &&
                    TryParseVersion(name[..split], out var version) && version >= LegacyVersion)
                {
                    var directory = Path.Combine(_root, name);
                    if (OptiCaptureHostService.IsRuntimePresent(directory)) return (directory, version);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var legacy = Path.Combine(_root, "v" + LegacyVersion);
        return OptiCaptureHostService.IsRuntimePresent(legacy) ? (legacy, LegacyVersion) : null;
    }

    private async Task DownloadAsync(OptiCaptureHostRelease release, string archive, Action<long, long?>? onProgress,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        foreach (var start in DownloadStarts(release))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                await DownloadFromAsync(start, release, archive, onProgress, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException &&
                                          !cancellationToken.IsCancellationRequested)
            {
                // 网络问题换个端点再试一次；内容校验失败（InvalidDataException）不重试。
                failure = error;
                TryDeleteFile(archive);
            }
        }
        throw failure ?? new InvalidDataException("The OptiCaptureHost download could not be started.");
    }

    /// <summary>下载入口顺序：api.github.com 资产端点优先，github.com 直链兜底。
    /// 部分网络能访问 API 却连不上 github.com，只用直链会直接报“更新不可用”。</summary>
    private static IEnumerable<Uri> DownloadStarts(OptiCaptureHostRelease release)
    {
        if (release.ApiAssetUri is not null) yield return release.ApiAssetUri;
        yield return release.DownloadUri;
    }

    private async Task DownloadFromAsync(Uri start, OptiCaptureHostRelease release, string archive,
        Action<long, long?>? onProgress, CancellationToken cancellationToken)
    {
        var url = start;
        for (var redirects = 0; ; redirects++)
        {
            using var request = CreateRequest(url, "application/octet-stream");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (redirects >= 4 || location is null || !Uri.TryCreate(url, location, out var next) ||
                    !IsDownloadRedirect(next))
                    throw new InvalidDataException("Unexpected OptiCaptureHost download redirect.");
                url = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK ||
                (response.Content.Headers.ContentLength is { } length && length != release.Size))
                throw new InvalidDataException("OptiCaptureHost download length differs from GitHub release metadata.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            var lastReport = Environment.TickCount64;
            await ReadBoundedAsync(source, destination, release.Size, (buffer, read) =>
            {
                hash.AppendData(buffer, 0, read);
                copied += read;
                if (Environment.TickCount64 - lastReport < 200 || copied == release.Size) return;
                lastReport = Environment.TickCount64;
                onProgress?.Invoke(copied, release.Size);
            }, cancellationToken).ConfigureAwait(false);
            if (copied != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OptiCaptureHost download failed SHA-256 or length verification.");
            onProgress?.Invoke(copied, release.Size);
            return;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task ReadBoundedAsync(Stream source, Stream destination, long limit, Action<byte[], int>? report,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var length = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0) return;
            total += length;
            if (total > limit) throw new InvalidDataException("OptiCaptureHost download exceeded its expected size.");
            await destination.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            report?.Invoke(buffer, length);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("GameLocker-OptiCaptureHost");
        request.Headers.Accept.ParseAdd(accept);
        return request;
    }

    private static bool IsDownloadRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = null!;
        if (tag is null || tag.Length > 33 || !tag.StartsWith('v')) return false;
        var parts = tag[1..].Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9'))) return false;
        if (!Version.TryParse(tag[1..], out var parsed) || tag != "v" + parsed.ToString(3)) return false;
        version = parsed;
        return true;
    }

    private static OptiCaptureHostRelease ReadRelease(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object ||
            !json.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !json.TryGetProperty("prerelease", out var pre) || pre.ValueKind != JsonValueKind.False ||
            !TryParseVersion(ReadString(json, "tag_name"), out var version) || version < LegacyVersion ||
            !json.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned an unsupported OptiCaptureHost release.");

        var tag = "v" + version.ToString(3);
        OptiCaptureHostRelease? release = null;
        var releaseIsHotFix = false;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (name is null || !IsFullPackage(name, version)) continue;
            var digest = ReadString(asset, "digest");
            if (ReadString(asset, "state") != "uploaded" ||
                !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out var length) || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                ReadString(asset, "browser_download_url") != ReleaseRoot + tag + "/" + name)
                throw new InvalidDataException("GitHub returned invalid OptiCaptureHost full-pack metadata.");
            var hotFix = name.Contains("HotFIX", StringComparison.OrdinalIgnoreCase);
            var candidate = new OptiCaptureHostRelease(version, tag, new Uri(ReleaseRoot + tag + "/" + name), length,
                digest[7..])
            {
                ApiAssetUri = AssetApiUri(asset)
            };
            ValidateRelease(candidate);
            // 同一版本同时挂普通包和 HotFIX 包时取 HotFIX（修正版）；同类重复视为元数据异常。
            if (release is null || (hotFix && !releaseIsHotFix))
            {
                release = candidate;
                releaseIsHotFix = hotFix;
            }
            else if (hotFix == releaseIsHotFix)
            {
                throw new InvalidDataException("GitHub returned multiple OptiCaptureHost full packs for the same version.");
            }
        }
        return release ?? throw new InvalidDataException("The official release does not contain a supported OptiCaptureHost full pack.");
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>资产 id → api.github.com 下载端点；没有 id 时返回 null（只用直链）。</summary>
    private static Uri? AssetApiUri(JsonElement asset)
    {
        if (!asset.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var value) || value <= 0) return null;
        return new Uri(AssetApiRoot + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsFullPackage(string? name, Version version) =>
        name is not null && FullPackageName.Match(name) is { Success: true } match &&
        match.Groups["version"].Value == version.ToString(3);

    private static void ValidateRelease(OptiCaptureHostRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!TryParseVersion(release.Tag, out var version) || version != release.Version || version < LegacyVersion ||
            release.Size <= 0 || release.Size > MaxArchiveBytes || release.Sha256.Length != 64 ||
            !release.Sha256.All(Uri.IsHexDigit) || !release.DownloadUri.IsAbsoluteUri ||
            !IsFullPackage(release.DownloadUri.Segments[^1], version) ||
            release.DownloadUri.OriginalString != ReleaseRoot + release.Tag + "/" + release.DownloadUri.Segments[^1])
            throw new InvalidDataException("The OptiCaptureHost release descriptor is invalid.");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
