using System.Text.Json;
using GameLocker.Models;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// 应用设置读写（JSON 存于 %APPDATA%\GameLocker\settings.json），
/// 并负责把 AutoStart 落到 HKCU Run 注册表键。
/// </summary>
public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static AppSettings Current { get; private set; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                // 旧默认（单 Start）升级为 Start+Select，避免老配置继续用旧快捷键
                if (loaded.FullscreenCombo == GamepadButtons.Menu)
                    loaded.FullscreenCombo = GamepadButtons.Menu | GamepadButtons.View;
                // 上一版把「底栏呼出组合键」错接到启动器 SummonCombo 上，默认值也顺手从 LT+RT 改成 LS+RS；
                // 存过那份配置的机器上启动器呼出键被顶成 LS+RS、LT+RT 直接失效。这里一次性还原。
                var summonRestored = false;
                if (!loaded.DockSummonComboMigrated)
                {
                    loaded.DockSummonComboMigrated = true;
                    if (loaded.SummonCombo == (GamepadButtons.LeftThumb | GamepadButtons.RightThumb))
                    {
                        loaded.SummonCombo = GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger;
                        loaded.DockSummonCombo = GamepadButtons.LeftThumb | GamepadButtons.RightThumb;
                        summonRestored = true;
                        DebugLog.Write("[设置] 已把启动器呼出键还原为 LT+RT，LS+RS 挪给底栏呼出键");
                    }
                }
                MigratePowerProfiles(loaded);
                using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (!document.RootElement.TryGetProperty(nameof(AppSettings.SharedProfile), out _))
                    loaded.SharedProfile = (PowerProfileService.OnAc ? loaded.AcProfile : loaded.BatteryProfile).Copy();
                DebugLog.Enabled = loaded.DiagnosticLog;
                // 底栏图标尺寸兜底（条高已改为按图标自动推导，旧配置里的 DockHeight 直接忽略）
                var beforeIcon = loaded.DockIconSize;
                loaded.NormalizeDockGeometry();
                // 「任务栏处理」已改为「任务栏隐藏」开关：旧的“自动隐藏”(1) 不再有意义，统一归零
                var beforeTaskbarMode = loaded.DockTaskbarMode;
                if (loaded.DockTaskbarMode != 2) loaded.DockTaskbarMode = 0;
                if (loaded.DockIconSize != beforeIcon || loaded.DockTaskbarMode != beforeTaskbarMode || summonRestored)
                {
                    try { Save(loaded); } catch { }
                }
                return loaded;
            }
        }
        catch
        {
            // 配置损坏时回退默认
        }
        var def = new AppSettings();
        Save(def);
        return def;
    }

    /// <summary>旧配置只有一套全局功耗/核心设置：首次升级时复制成插电与电池两套参数，
    /// 其余面板参数保持“未保存”，等用户在面板里保存一次后生效。</summary>
    private static void MigratePowerProfiles(AppSettings settings)
    {
        if (settings.PowerProfilesMigrated) return;
        settings.PowerProfilesMigrated = true;
        Seed(settings.AcProfile);
        Seed(settings.BatteryProfile);
        Seed(settings.SharedProfile);

        void Seed(PowerProfile profile)
        {
            if (settings.CpuPowerTakeover && settings.CpuPowerPl1 > 0)
            {
                profile.CpuPowerEnabled = true;
                profile.CpuPowerPl1 = settings.CpuPowerPl1;
                profile.CpuPowerPl2 = settings.CpuPowerPl2;
                profile.CpuPowerFppt = settings.CpuPowerFppt;
            }
            if (settings.GameCoreMode != GameCoreMode.Auto)
            {
                profile.CoreMode = settings.GameCoreMode;
                profile.CoreModeCaptured = true;
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        Current = settings;
        DebugLog.Enabled = settings.DiagnosticLog;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        ApplyAutoStart(settings.AutoStart);
    }

    /// <summary>
    /// 只更新当前设置的指定字段并落盘（各设置子页共用，避免整对象覆盖丢失其他页的值）。
    /// </summary>
    public static void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        // 底栏条高必须容得下放大后的图标（含倒影），在这里兜底，任何入口改尺寸都不会存下坏组合
        Current.NormalizeDockGeometry();
        Save(Current);
    }

    /// <summary>开机自启：写/删 HKCU\Software\Microsoft\Windows\CurrentVersion\Run</summary>
    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run == null) return;

            if (enable)
            {
                // 加引号：路径含空格时 Run 项按空格截断，重启后就启动不了本程序
                var exe = $"\"{Environment.ProcessPath}\"";
                if (!string.Equals(run.GetValue("GameLocker") as string, exe, StringComparison.OrdinalIgnoreCase))
                    run.SetValue("GameLocker", exe);
            }
            else
                run.DeleteValue("GameLocker", false);
        }
        catch
        {
            // 注册表不可写时忽略，不影响其余设置
        }
    }

    /// <summary>启动时校正自启项：程序被移动/重命名后 Run 值仍指向旧路径，开机就起不来。</summary>
    public static void EnsureAutoStartPath()
    {
        ApplyAutoStart(Current.AutoStart);
        DebugLog.Write($"开机自启：{Current.AutoStart} → {Environment.ProcessPath}");
    }
}
