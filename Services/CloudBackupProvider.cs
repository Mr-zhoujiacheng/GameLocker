using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>云端备份的网盘统一入口：按操作开始时捕获的账号分发，不随设置变化。</summary>
public sealed record CloudFileEntry(string Href, string Name, DateTimeOffset Modified, long Size);

internal sealed class CloudBackupSession(
    CloudProvider provider,
    string rootFolder,
    BaiduAccount? baiduAccount,
    TianyiAccount? tianyiAccount)
{
    public CloudProvider Provider { get; } = provider;
    public string RootFolder { get; } = rootFolder;
    internal BaiduAccount? BaiduAccount { get; } = baiduAccount;
    internal TianyiAccount? TianyiAccount { get; } = tianyiAccount;
    public string DisplayName => Provider == CloudProvider.Baidu ? "百度网盘" : "天翼云";
}

public static class CloudBackupProvider
{
    public static CloudProvider Current => SettingsService.Current.CloudProvider;

    public static string DisplayName => Current == CloudProvider.Baidu ? "百度网盘" : "天翼云";
    public static string RootFolder
    {
        get
        {
            var settings = SettingsService.Current;
            var raw = Current == CloudProvider.Baidu ? settings.BaiduRootFolder : settings.TianyiRootFolder;
            return string.IsNullOrWhiteSpace(raw) ? "GameLocker" : raw;
        }
    }
    public static bool IsSignedIn => Current == CloudProvider.Baidu
        ? BaiduCloudService.CurrentAccount() is not null
        : TianyiCloudService.CurrentAccount() is not null;
    public static string AccountName => Current == CloudProvider.Baidu
        ? BaiduCloudService.CurrentAccount()?.User ?? ""
        : TianyiCloudService.CurrentAccount()?.User ?? "";

    internal static CloudBackupSession CaptureSession()
    {
        var settings = SettingsService.Current;
        var provider = settings.CloudProvider;
        var rawRoot = provider == CloudProvider.Baidu ? settings.BaiduRootFolder : settings.TianyiRootFolder;
        var root = string.IsNullOrWhiteSpace(rawRoot) ? "GameLocker" : rawRoot;
        var baidu = provider == CloudProvider.Baidu ? BaiduCloudService.CurrentAccount() : null;
        var tianyi = provider == CloudProvider.Baidu ? null : TianyiCloudService.CurrentAccount();
        if (baidu is null && tianyi is null)
            throw new IOException($"{(provider == CloudProvider.Baidu ? "百度网盘" : "天翼云")}未登录，请扫码登录。");
        return new CloudBackupSession(provider, root, baidu, tianyi);
    }

    internal static Task EnsureFolderAsync(CloudBackupSession session, string path) =>
        session.Provider == CloudProvider.Baidu
            ? BaiduCloudService.EnsureFolderAsync(session.BaiduAccount!, path)
            : TianyiCloudService.EnsureFolderAsync(session.TianyiAccount!, path);

    internal static async Task<IReadOnlyList<CloudFileEntry>> ListAsync(CloudBackupSession session, string path)
    {
        if (session.Provider == CloudProvider.Baidu)
        {
            var list = await BaiduCloudService.ListAsync(session.BaiduAccount!, path);
            return list.Select(e => new CloudFileEntry(e.Href, e.Name, e.Modified, e.Size)).ToArray();
        }
        var tianyi = await TianyiCloudService.ListAsync(session.TianyiAccount!, path);
        return tianyi.Select(e => new CloudFileEntry(e.Href, e.Name, e.Modified, e.Size)).ToArray();
    }

    internal static Task UploadAsync(CloudBackupSession session, string path, string fileName, string localPath) =>
        session.Provider == CloudProvider.Baidu
            ? BaiduCloudService.UploadAsync(session.BaiduAccount!, path, fileName, localPath)
            : TianyiCloudService.UploadAsync(session.TianyiAccount!, path, fileName, localPath);

    internal static Task DownloadAsync(CloudBackupSession session, string href, string localPath) =>
        session.Provider == CloudProvider.Baidu
            ? BaiduCloudService.DownloadAsync(session.BaiduAccount!, href, localPath)
            : TianyiCloudService.DownloadAsync(session.TianyiAccount!, href, localPath);

    internal static Task DeleteAsync(CloudBackupSession session, string path, string href) =>
        session.Provider == CloudProvider.Baidu
            ? BaiduCloudService.DeleteAsync(session.BaiduAccount!, path, href)
            : TianyiCloudService.DeleteAsync(session.TianyiAccount!, path, href);
}
