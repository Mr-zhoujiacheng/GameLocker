using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace GameLocker.Services;

/// <summary>移动热点与飞行模式（走 WinRT 无线/网络 API，失败时返回错误文本）。</summary>
internal static class NetworkModeService
{
    public static async Task<(bool ok, string state)> ToggleHotspotAsync()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return (false, "无网络连接");

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState == TetheringOperationalState.On)
            {
                var result = await manager.StopTetheringAsync();
                return (result.Status.ToString() == "Success", "关闭");
            }

            var started = await manager.StartTetheringAsync();
            return (started.Status.ToString() == "Success", "打开");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<bool> IsHotspotOnAsync()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return false;
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            return manager.TetheringOperationalState == TetheringOperationalState.On;
        }
        catch { return false; }
    }

    public static async Task<(bool ok, bool on, string state)> ToggleAirplaneAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return (false, false, "无权限");

            var radios = await Radio.GetRadiosAsync();
            var anyOn = radios.Any(r => r.State == RadioState.On);

            foreach (var radio in radios)
            {
                // 打开飞行模式时关掉所有无线；关闭时只恢复 Wi-Fi
                if (anyOn)
                {
                    if (radio.State == RadioState.On) await radio.SetStateAsync(RadioState.Off);
                }
                else if (radio.Kind == RadioKind.WiFi)
                {
                    await radio.SetStateAsync(RadioState.On);
                }
            }

            return (true, !anyOn, anyOn ? "关闭" : "打开");
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    public static async Task<bool> IsAirplaneOnAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            return radios.Count > 0 && radios.All(r => r.State == RadioState.Off);
        }
        catch { return false; }
    }
}
