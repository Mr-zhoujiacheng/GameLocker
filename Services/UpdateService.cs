using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLocker.Services;

/// <summary>GitHub 上找到的新版本信息</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string? Notes,
    string ReleaseUrl,
    Uri InstallerUri,
    string InstallerName,
    long Size,
    string? Sha256);

/// <summary>
/// GitHub Releases 更新器：读取最新 release、比对程序集版本，
/// 下载固定命名的 Inno Setup 安装包（校验大小与 SHA256），静默启动安装。
/// </summary>
public static class UpdateService
{
    private const string Owner = "Mr-zhoujiacheng";
    private const string Repo = "GameLocker";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>最近一次检查发现的新版本（已是最新为 null），供关于页直接展示</summary>
    public static UpdateInfo? LatestFound { get; private set; }

    /// <summary>本次运行是否已完成过自动检查（避免关于页重复触发）</summary>
    public static bool AutoChecked { get; private set; }

    /// <summary>当前程序版本（规范成 4 段，便于与 tag 比较）</summary>
    public static Version CurrentVersion =>
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GameLocker-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>启动时后台检查：任何异常都静默（网络不可用不影响启动）</summary>
    public static async Task AutoCheckAsync()
    {
        try { LatestFound = await CheckAsync(); }
        catch { LatestFound = null; }
        finally { AutoChecked = true; }
    }

    /// <summary>查询最新 release；返回 null 表示已是最新。异常向上抛出，供手动检查显示原因</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var res = await Http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var remote)) return null;
        remote = Normalize(remote);
        if (remote.CompareTo(CurrentVersion) <= 0) return null;

        // 安装包：发布时固定为 GameLocker-Setup-v{版本}.exe
        Uri? installer = null;
        var name = $"GameLocker-Setup-v{remote}.exe";
        long size = 0;
        string? sha256 = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var assetName = a.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                if (!assetName.StartsWith("GameLocker-Setup", StringComparison.OrdinalIgnoreCase)
                    || !assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                name = assetName;
                if (a.TryGetProperty("browser_download_url", out var u)
                    && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed))
                    installer = parsed;
                if (a.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes)) size = bytes;
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest
                    && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest[7..].ToLowerInvariant();
                break;
            }
        }
        if (installer is null) return null; // 没有安装包（只有源码压缩包）时不提示更新

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new UpdateInfo(remote, tag, notes, page, installer, name, size, sha256);
    }

    /// <summary>下载安装包到临时目录（自动清理旧包），返回本地路径</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLockerUpdate");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "GameLocker-Setup*.exe"))
        {
            try { File.Delete(old); } catch { }
        }

        var path = Path.Combine(dir, info.InstallerName);
        using (var res = await Http.GetAsync(info.InstallerUri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? info.Size;
            await using var input = await res.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(Math.Clamp((double)done / total, 0, 1));
            }
        }

        if (info.Size > 0 && new FileInfo(path).Length != info.Size)
        {
            File.Delete(path);
            throw new IOException("安装包大小校验失败");
        }
        if (info.Sha256 is { Length: 64 } expected)
        {
            await using var fs = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("安装包校验失败（SHA256 不匹配）");
            }
        }

        progress?.Report(1);
        return path;
    }

    /// <summary>静默启动安装包；Inno Setup 会关闭正在运行的本程序，安装完成后自动重新启动</summary>
    public static void RunInstaller(string path) =>
        Process.Start(new ProcessStartInfo(
            path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS")
        {
            UseShellExecute = true
        });

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
