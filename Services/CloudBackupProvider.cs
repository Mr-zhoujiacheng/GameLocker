using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>云端备份的网盘统一入口：按设置里的当前网盘分发到天翼云或百度网盘。</summary>
public sealed record CloudFileEntry(string Href, string Name, DateTimeOffset Modified, long Size);

public static class CloudBackupProvider
{
    public static CloudProvider Current => SettingsService.Current.CloudProvider;

    public static string DisplayName => Current == CloudProvider.Baidu ? "百度网盘" : "天翼云";

    public static string RootFolder
    {
        get
        {
            var raw = Current == CloudProvider.Baidu
                ? SettingsService.Current.BaiduRootFolder
                : SettingsService.Current.TianyiRootFolder;
            return string.IsNullOrWhiteSpace(raw) ? "GameLocker" : raw;
        }
    }

    public static bool IsSignedIn => Current == CloudProvider.Baidu
        ? BaiduCloudService.CurrentAccount() is not null
        : TianyiCloudService.CurrentAccount() is not null;

    public static string AccountName
    {
        get
        {
            if (Current == CloudProvider.Baidu) return BaiduCloudService.CurrentAccount()?.User ?? "";
            return TianyiCloudService.CurrentAccount()?.User ?? "";
        }
    }

    public static async Task EnsureFolderAsync(string path)
    {
        if (Current == CloudProvider.Baidu)
        {
            var account = BaiduCloudService.CurrentAccount()
                ?? throw new IOException("百度网盘未登录，请扫码登录。");
            await BaiduCloudService.EnsureFolderAsync(account, path);
        }
        else
        {
            var account = TianyiCloudService.CurrentAccount()
                ?? throw new IOException("天翼云未登录，请扫码登录。");
            await TianyiCloudService.EnsureFolderAsync(account, path);
        }
    }

    public static async Task<IReadOnlyList<CloudFileEntry>> ListAsync(string path)
    {
        if (Current == CloudProvider.Baidu)
        {
            var account = BaiduCloudService.CurrentAccount()
                ?? throw new IOException("百度网盘未登录，请扫码登录。");
            var list = await BaiduCloudService.ListAsync(account, path);
            return list.Select(e => new CloudFileEntry(e.Href, e.Name, e.Modified, e.Size)).ToArray();
        }
        else
        {
            var account = TianyiCloudService.CurrentAccount()
                ?? throw new IOException("天翼云未登录，请扫码登录。");
            var list = await TianyiCloudService.ListAsync(account, path);
            return list.Select(e => new CloudFileEntry(e.Href, e.Name, e.Modified, e.Size)).ToArray();
        }
    }

    public static async Task UploadAsync(string path, string fileName, string localPath)
    {
        if (Current == CloudProvider.Baidu)
        {
            var account = BaiduCloudService.CurrentAccount()
                ?? throw new IOException("百度网盘未登录，请扫码登录。");
            await BaiduCloudService.UploadAsync(account, path, fileName, localPath);
        }
        else
        {
            var account = TianyiCloudService.CurrentAccount()
                ?? throw new IOException("天翼云未登录，请扫码登录。");
            await TianyiCloudService.UploadAsync(account, path, fileName, localPath);
        }
    }

    public static async Task DownloadAsync(string href, string localPath)
    {
        if (Current == CloudProvider.Baidu)
        {
            var account = BaiduCloudService.CurrentAccount()
                ?? throw new IOException("百度网盘未登录，请扫码登录。");
            await BaiduCloudService.DownloadAsync(account, href, localPath);
        }
        else
        {
            var account = TianyiCloudService.CurrentAccount()
                ?? throw new IOException("天翼云未登录，请扫码登录。");
            await TianyiCloudService.DownloadAsync(account, href, localPath);
        }
    }

    public static async Task DeleteAsync(string path, string href)
    {
        if (Current == CloudProvider.Baidu)
        {
            var account = BaiduCloudService.CurrentAccount()
                ?? throw new IOException("百度网盘未登录，请扫码登录。");
            await BaiduCloudService.DeleteAsync(account, path, href);
        }
        else
        {
            var account = TianyiCloudService.CurrentAccount()
                ?? throw new IOException("天翼云未登录，请扫码登录。");
            await TianyiCloudService.DeleteAsync(account, path, href);
        }
    }
}
