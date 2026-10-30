using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// Windows 通知中心：打开通知中心（Win+N）与"有没有新通知"检测。
/// 未打包（非 MSIX）应用拿不到 UserNotificationListener，所以新通知的判断改为读
/// 注册表里各应用记录的最后一条通知时间（HKCU\...\Notifications\Settings\&lt;AppId&gt;\LastNotificationAddedTime），
/// 取全局最大值即可知道"最近有没有新通知到达"。
/// </summary>
public static class NotificationService
{
    private const string SettingsRoot = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>最近一条通知的到达时间（本地时间）；读不到返回 null</summary>
    public static DateTime? LastAddedTime()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsRoot);
            if (root is null) return null;

            DateTime? latest = null;
            foreach (var appId in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(appId);
                if (app?.GetValue("LastNotificationAddedTime") is not long ticks || ticks <= 0) continue;
                try
                {
                    var time = DateTime.FromFileTimeUtc(ticks).ToLocalTime();
                    if (latest is null || time > latest) latest = time;
                }
                catch { /* 个别应用写的时间戳异常，跳过 */ }
            }
            return latest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开 Windows 通知中心（等同按下 Win+N）</summary>
    public static void OpenCenter() => KeySendService.Send(0x4E, KeySendService.ModWin); // 'N'
}
