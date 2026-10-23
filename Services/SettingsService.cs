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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时回退默认
        }
        var def = new AppSettings();
        Save(def);
        return def;
    }

    public static void Save(AppSettings settings)
    {
        Current = settings;
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
