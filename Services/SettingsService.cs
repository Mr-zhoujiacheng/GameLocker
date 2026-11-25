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

    private static readonly object Gate = new();
    private static bool _useInMemorySettings;
    internal static bool IsRecovering => Volatile.Read(ref _useInMemorySettings);

    public static AppSettings Current { get; private set; } = Load();

    private static AppSettings Load()
    {
        string? original;
        try
        {
            original = ConfigurationFile.Read(FilePath);
            if (original is null)
            {
                var defaults = new AppSettings { DockSummonComboMigrated = true, PowerProfilesMigrated = true };
                TryWriteOnLoad(defaults, original);
                return defaults;
            }

            using var document = JsonDocument.Parse(original);
            var loaded = ParseSettings(document.RootElement);
            var changed = false;
            // 旧默认（单 Start）升级为 Start+Select，避免老配置继续用旧快捷键
            if (loaded.FullscreenCombo == GamepadButtons.Menu)
            {
                loaded.FullscreenCombo = GamepadButtons.Menu | GamepadButtons.View;
                changed = true;
            }
            // 旧版把底栏呼出组合键写到了启动器。一次性还原各自的组合键。
            if (!loaded.DockSummonComboMigrated)
            {
                loaded.DockSummonComboMigrated = true;
                changed = true;
                if (loaded.SummonCombo == (GamepadButtons.LeftThumb | GamepadButtons.RightThumb))
                {
                    loaded.SummonCombo = GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger;
                    loaded.DockSummonCombo = GamepadButtons.LeftThumb | GamepadButtons.RightThumb;
                    DebugLog.Write("[设置] 已把启动器呼出键还原为 LT+RT，LS+RS 挪给底栏呼出键");
                }
            }
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.CpuPowerTakeover), out _) && loaded.CpuPowerPl1 > 0)
            {
                loaded.CpuPowerTakeover = true;
                changed = true;
            }
            if (!loaded.PowerProfilesMigrated)
            {
                MigratePowerProfiles(loaded);
                changed = true;
            }
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.SharedProfile), out _))
            {
                loaded.SharedProfile = (PowerProfileService.OnAc ? loaded.AcProfile : loaded.BatteryProfile).Copy();
                changed = true;
            }
            DebugLog.Enabled = loaded.DiagnosticLog;
            // 条高按图标推导，旧配置里的 DockHeight 不再使用。
            var beforeIcon = loaded.DockIconSize;
            loaded.NormalizeDockGeometry();
            // 旧的“自动隐藏”(1) 已移除。
            var beforeTaskbarMode = loaded.DockTaskbarMode;
            if (loaded.DockTaskbarMode != 2) loaded.DockTaskbarMode = 0;
            if (changed || loaded.DockIconSize != beforeIcon || loaded.DockTaskbarMode != beforeTaskbarMode)
                TryWriteOnLoad(loaded, original);
            return loaded;
        }
        catch (Exception ex)
        {
            StartRecovery(ex);
            return new AppSettings
            {
                TakeOverSystemKeyboard = false,
                EdgeGestureSummon = false,
                StartMenuMode = false,
                AutoCheckUpdates = false
            };
        }
    }

    internal static AppSettings? ReadSavedSettings()
    {
        var saved = ConfigurationFile.Read(FilePath);
        if (saved is null) return null;
        using var document = JsonDocument.Parse(saved);
        return ParseSettings(document.RootElement);
    }

    private static AppSettings ParseSettings(JsonElement root)
    {
        var settings = root.Deserialize<AppSettings>(Options);
        if (settings is null || settings.AcProfile is null || settings.BatteryProfile is null
            || settings.SharedProfile is null || settings.CoreParkingOriginals is null)
            throw new JsonException("设置结构无效");
        return settings;
    }

    private static void TryWriteOnLoad(AppSettings settings, string? original)
    {
        try { ConfigurationFile.Write(FilePath, JsonSerializer.Serialize(settings, Options), original); }
        catch (Exception ex) { StartRecovery(ex, "初始化或迁移写入失败"); }
    }

    private static void StartRecovery(Exception error, string reason = "读取失败")
    {
        _useInMemorySettings = true;
        ConfigurationFile.Report(FilePath, $"{reason}，保留原文件，本次运行的更改仅保存在内存", error);
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
        lock (Gate)
        {
            Current = settings;
            DebugLog.Enabled = settings.DiagnosticLog;
            if (_useInMemorySettings) return;

            string? original;
            try
            {
                original = ConfigurationFile.Read(FilePath);
                if (original is not null)
                {
                    using var document = JsonDocument.Parse(original);
                    ParseSettings(document.RootElement);
                }
            }
            catch (Exception ex)
            {
                StartRecovery(ex);
                return;
            }

            ConfigurationFile.Write(FilePath, JsonSerializer.Serialize(settings, Options), original);
            ApplyAutoStart(settings.AutoStart);
        }
    }

    /// <summary>
    /// 只更新当前设置的指定字段并落盘（各设置子页共用，避免整对象覆盖丢失其他页的值）。
    /// </summary>
    public static void Update(Action<AppSettings> mutate)
    {
        lock (Gate)
        {
            mutate(Current);
            // 底栏条高必须容得下放大后的图标（含倒影），在这里兜底，任何入口改尺寸都不会存下坏组合
            Current.NormalizeDockGeometry();
            Save(Current);
        }
    }

    private static string? AutoStartExecutable()
    {
        using var install = Registry.CurrentUser.OpenSubKey(@"Software\GameLocker");
        if (install?.GetValue("InstallPath") is string installedExe &&
            Path.IsPathFullyQualified(installedExe) && File.Exists(installedExe))
            return installedExe;

        // 兼容尚未写入 InstallPath 的 Inno Setup 版本和自定义安装目录。
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7F3C4E2-9B21-4E7D-8C5A-6D4E3F1B2A90}_is1";
        using var previous = Registry.CurrentUser.OpenSubKey(uninstallKey);
        if (previous?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location))
        {
            var previousExe = Path.Combine(location, "GameLocker.exe");
            if (File.Exists(previousExe)) return previousExe;
        }
        return Environment.ProcessPath;
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
                if (AutoStartExecutable() is not { } executable) return;
                var command = $"\"{executable}\"";
                if (!string.Equals(run.GetValue("GameLocker") as string, command, StringComparison.OrdinalIgnoreCase))
                    run.SetValue("GameLocker", command);
            }
            else
                run.DeleteValue("GameLocker", false);
        }
        catch (Exception ex)
        {
            DebugLog.Write("开机自启项更新失败：" + ex.Message);
        }
    }

    /// <summary>在主实例启动时校正已启用的自启项。</summary>
    public static void EnsureAutoStartPath()
    {
        lock (Gate)
        {
            if (!_useInMemorySettings) ApplyAutoStart(Current.AutoStart);
        }
    }
}
