using System.Runtime.InteropServices;
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
    /// <summary>
    /// 统计"自 since 之后有新通知"的应用数量。
    /// 未打包应用拿不到 UserNotificationListener，只能按注册表里各应用的
    /// LastNotificationAddedTime 判断，因此这是"有新通知的应用数"，不是精确条数。
    /// </summary>
    public static int CountSince(DateTime since)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsRoot);
            if (root is null) return 0;

            var count = 0;
            foreach (var appId in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(appId);
                if (app?.GetValue("LastNotificationAddedTime") is not long ticks || ticks <= 0) continue;
                try
                {
                    if (DateTime.FromFileTimeUtc(ticks).ToLocalTime() > since) count++;
                }
                catch { /* 忽略异常时间戳 */ }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
    /// <summary>
    /// 最近一条通知来自哪个应用（注册表里的 AppId）。
    /// 用于"点击通知直达应用窗口"：拿不到单条通知内容，但能拿到来源应用。
    /// </summary>
    public static string? LatestAppId()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsRoot);
            if (root is null) return null;

            string? bestId = null;
            long bestTicks = 0;

            foreach (var appId in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(appId);
                if (app?.GetValue("LastNotificationAddedTime") is not long ticks || ticks <= 0) continue;
                if (ticks <= bestTicks) continue;
                bestTicks = ticks;
                bestId = appId;
            }

            return bestId;
        }
        catch
        {
            return null;
        }
    }

    public static void OpenCenter() => KeySendService.Send(0x4E, KeySendService.ModWin); // 'N'

    /// <summary>通知中心是否在前台（用于再次点击时关闭）。</summary>
    public static bool IsCenterForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;

        var cls = ClassName(hwnd);
        var title = WindowTitle(hwnd);
        return cls.Contains("XamlExplorerHostIsland", StringComparison.OrdinalIgnoreCase)
            || title.Contains("通知", StringComparison.Ordinal)
            || title.Contains("Notification", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>关闭前台的通知中心/浮层。</summary>
    public static void CloseCenter() => KeySendService.Send(0x1B, 0); // Esc

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(128);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(256);
        var n = GetWindowText(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
