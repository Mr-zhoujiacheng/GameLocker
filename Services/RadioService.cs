using Windows.Devices.Radios;

namespace GameLocker.Services;

/// <summary>
/// Wi-Fi / 蓝牙无线电开关。走 WinRT 的 Radio API（无需管理员权限、无需 MSIX 打包），
/// 失败时返回 null，由界面退化为打开对应的系统设置页。
/// </summary>
public static class RadioService
{
    /// <summary>读取无线电当前是否开启；找不到无线电或没有权限时返回 null</summary>
    public static async Task<bool?> IsOnAsync(RadioKind kind)
    {
        try
        {
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            return radio is null ? null : radio.State == RadioState.On;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>切换无线电开关，返回切换后的状态；找不到无线电或没有权限时返回 null</summary>
    public static async Task<bool?> ToggleAsync(RadioKind kind)
    {
        try
        {
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            if (radio is null) return null;

            var target = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
            var status = await radio.SetStateAsync(target);
            return status == RadioAccessStatus.Allowed ? target == RadioState.On : null;
        }
        catch
        {
            return null;
        }
    }
}
