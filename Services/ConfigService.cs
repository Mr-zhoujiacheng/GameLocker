using System.Text.Json;
using System.Text.Json.Serialization;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>快捷项配置读写（JSON 存于 %APPDATA%\GameLocker）</summary>
public static class ConfigService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
    private static readonly string FilePath = Path.Combine(Dir, "items.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<LaunchItem> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // 必须传 Options：Kind 以字符串序列化，默认转换器无法反序列化枚举字符串
                var items = JsonSerializer.Deserialize<List<LaunchItem>>(File.ReadAllText(FilePath), Options);
                if (items is not null)
                {
                    EnsureSettingsTile(items);
                    return items;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认项
        }
        var defaults = CreateDefaults();
        EnsureSettingsTile(defaults);
        Save(defaults);
        return defaults;
    }

    /// <summary>
    /// 保证设置磁贴常驻主页：缺失则补到列表末尾并落盘。
    /// 主页删除/组合均拦截该磁贴（可移动改位置），此处兜底防止手改配置导致丢失。
    /// </summary>
    private static void EnsureSettingsTile(List<LaunchItem> items)
    {
        if (items.Any(it => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Settings)) return;
        items.Add(new LaunchItem
        {
            Name = "设置",
            Target = "widget:settings",
            Kind = LaunchKind.Widget,
            WidgetKind = WidgetKind.Settings,
            Size = TileSize.Small
        });
        Save(items);
    }

    public static void Save(List<LaunchItem> items)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, Options));
    }

    /// <summary>首次运行的示例项（便于立刻用手柄验证）。尺寸每次安装随机分配，
    /// 因此每台机器首次打开的磁贴排布都不一样</summary>
    private static List<LaunchItem> CreateDefaults()
    {
        // 三种尺寸先随机打乱依次分配，保证前三个示例项尺寸互不相同；第四项再随机取一种
        var sizes = new[] { TileSize.Small, TileSize.Wide, TileSize.Large }
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        return
        [
            new LaunchItem { Name = "记事本", Target = "notepad.exe", Kind = LaunchKind.App, Size = sizes[0] },
            new LaunchItem { Name = "计算器", Target = "calc.exe", Kind = LaunchKind.App, Size = sizes[1] },
            new LaunchItem { Name = "Bing", Target = "https://www.bing.com", Kind = LaunchKind.Url, Size = sizes[2] },
            new LaunchItem { Name = "GitHub", Target = "https://github.com", Kind = LaunchKind.Url,
                             Size = sizes[Random.Shared.Next(sizes.Length)] }
        ];
    }
}
