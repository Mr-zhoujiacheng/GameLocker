using System.Diagnostics;
using System.Text;
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

    private static readonly object Gate = new();
    // 读取失败后，本次会话的磁贴更改保存在内存，重启时重新尝试读取原文件。
    private static string? _recoveryItems;
    internal static bool IsRecovering => Volatile.Read(ref _recoveryItems) is not null;

    public static List<LaunchItem> Load()
    {
        lock (Gate)
        {
            if (_recoveryItems is not null) return ParseItems(_recoveryItems);

            string? original;
            List<LaunchItem> items;
            try
            {
                original = ConfigurationFile.Read(FilePath);
                items = original is null ? CreateDefaults() : ParseItems(original);
            }
            catch (Exception ex)
            {
                items = CreateDefaults();
                StartRecovery(items, ex);
                return items;
            }

            try
            {
                var changed = original is null;
                changed |= MigrateTileSets(items);
                changed |= MigrateClashName(items);
                var performanceTiles = AllItems(items).Where(it => it.Kind == LaunchKind.Widget
                    && it.WidgetKind == WidgetKind.Performance && it.Size != TileSize.Large).ToList();
                foreach (var tile in performanceTiles) tile.Size = TileSize.Large;
                changed |= performanceTiles.Count > 0;
                changed |= MigrateLegacyFrameGenEngines(items);
                changed |= EnsureSettingsTile(items);
                if (changed) ConfigurationFile.Write(FilePath, JsonSerializer.Serialize(items, Options), original);
            }
            catch (Exception ex)
            {
                if (original is not null) items = ParseItems(original);
                StartRecovery(items, ex, "初始化或迁移失败");
                return items;
            }

            // 磁贴先提交。若页数尚未写入就退出，下次读取可从已保存的磁贴补齐连续页。
            var pageCount = Math.Max(1, SettingsService.Current.HomePageCount);
            var savedPages = items.Select(it => it.PageIndex).ToHashSet();
            while (pageCount < int.MaxValue && savedPages.Contains(pageCount)) pageCount++;
            if (pageCount > SettingsService.Current.HomePageCount)
            {
                try { SettingsService.Update(s => s.HomePageCount = Math.Max(s.HomePageCount, pageCount)); }
                catch (Exception ex) { ConfigurationFile.Report(FilePath, "更新磁贴页数失败", ex); }
            }
            return items;
        }
    }

    private static List<LaunchItem> ParseItems(string json)
    {
        // Kind 以字符串序列化，须使用与写入相同的枚举转换器。
        var items = JsonSerializer.Deserialize<List<LaunchItem>>(json, Options);
        if (items is null || !HasValidItems(items)) throw new JsonException("磁贴结构无效");
        return items;
    }

    private static bool HasValidItems(List<LaunchItem> items) => items.All(item =>
        item is not null && item.Name is not null && item.Target is not null &&
        item.Members is not null && HasValidItems(item.Members) &&
        (item.Snapshot is null || HasValidItems(item.Snapshot)) &&
        (item.Drawers is null || item.Drawers.All(drawer =>
            drawer is not null && drawer.Items is not null && HasValidItems(drawer.Items))));

    private static void StartRecovery(List<LaunchItem> items, Exception error, string reason = "读取失败")
    {
        EnsureSettingsTile(items);
        _recoveryItems = JsonSerializer.Serialize(items, Options);
        ConfigurationFile.Report(FilePath, $"{reason}，保留原文件，本次运行的更改仅保存在内存", error);
    }

    /// <summary>
    /// 一次性迁移：「磁贴集」磁贴已随该功能移除。快照非空的旧磁贴集，把快照内容整体转成一个新的副页
    /// （PageIndex = 已有页数）后再移除该磁贴集磁贴；无快照的直接移除。有改动才落盘，避免每次启动重复处理。
    /// </summary>
    private static bool MigrateTileSets(List<LaunchItem> items)
    {
        var sets = items.Where(it => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.TileSet).ToList();
        if (sets.Count == 0) return false;

        var page = Math.Max(1, SettingsService.Current.HomePageCount);
        foreach (var set in sets)
        {
            if (set.Snapshot is { Count: > 0 } snapshot)
            {
                foreach (var m in snapshot)
                {
                    var copy = m.Clone();
                    copy.PageIndex = page;
                    items.Add(copy);
                }
                page++;
            }
            items.Remove(set);
        }
        return true;
    }

    /// <summary>
    /// 一次性迁移：小猫咪小组件显示名统一为 "Clash"（旧配置里存的是"小猫咪"）。
    /// 该小组件的名称现在固定不可改，这里统一纠正旧名字，有改动才落盘。
    /// </summary>
    private static bool MigrateClashName(List<LaunchItem> items)
    {
        var changed = false;
        foreach (var it in AllItems(items))
        {
            if (it.Kind != LaunchKind.Widget || it.WidgetKind != WidgetKind.Clash || it.Name == "Clash")
                continue;
            it.Name = "Clash";
            changed = true;
        }
        return changed;
    }

    /// <summary>仅迁移 FGHost 的两个旧引擎值，保留其余配置字段。</summary>
    private static bool MigrateLegacyFrameGenEngines(List<LaunchItem> items)
    {
        var changed = false;
        foreach (var item in AllItems(items))
        {
            if (item.Kind != LaunchKind.Widget || item.WidgetKind != WidgetKind.LosslessScaling)
                continue;
            var engine = item.FrameGenEngine?.Trim();
            if (!string.Equals(engine, "XESS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(engine, "FSR3", StringComparison.OrdinalIgnoreCase))
                continue;
            item.FrameGenEngine = LosslessScalingService.EngineOptiScaler;
            changed = true;
        }
        return changed;
    }

    /// <summary>顶层快捷项 + 抽屉内层磁贴（抽屉可嵌套，递归展开）</summary>
    private static IEnumerable<LaunchItem> AllItems(IEnumerable<LaunchItem> items)
    {
        foreach (var it in items)
        {
            yield return it;
            foreach (var drawer in it.Drawers ?? [])
                foreach (var inner in AllItems(drawer.Items))
                    yield return inner;
        }
    }

    /// <summary>
    /// 保证设置 / 电源磁贴常驻主页：缺失则补到列表末尾。
    /// 主页删除/组合均拦截这两个磁贴（可移动改位置），此处兜底防止手改配置导致丢失。
    /// </summary>
    private static bool EnsureSettingsTile(List<LaunchItem> items)
    {
        var changed = false;
        if (!items.Any(it => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Settings))
        {
            items.Add(new LaunchItem
            {
                Name = "设置",
                Target = "widget:settings",
                Kind = LaunchKind.Widget,
                WidgetKind = WidgetKind.Settings,
                Size = TileSize.Small
            });
            changed = true;
        }
        if (!items.Any(it => it.Kind == LaunchKind.Widget && it.WidgetKind == WidgetKind.Power))
        {
            items.Add(new LaunchItem
            {
                Name = "电源",
                Target = "widget:power",
                Kind = LaunchKind.Widget,
                WidgetKind = WidgetKind.Power,
                Size = TileSize.Small,
                TileColor = "#C42B1C" // 默认深红：一眼识别为电源入口（仍可在编辑页改色）
            });
            changed = true;
        }
        return changed;
    }

    public static void Save(List<LaunchItem> items)
    {
        lock (Gate)
        {
            if (_recoveryItems is not null)
            {
                EnsureSettingsTile(items);
                _recoveryItems = JsonSerializer.Serialize(items, Options);
                return;
            }

            string? original;
            try
            {
                original = ConfigurationFile.Read(FilePath);
                if (original is not null) ParseItems(original);
            }
            catch (Exception ex)
            {
                StartRecovery(items, ex);
                return;
            }

            ConfigurationFile.Write(FilePath, JsonSerializer.Serialize(items, Options), original);
        }
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

internal static class ConfigurationFile
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static string? Read(string path)
    {
        try { return File.ReadAllText(path, Utf8); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void Write(string path, string contents, string? original)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                using (var writer = new StreamWriter(stream, Utf8, leaveOpen: true)) writer.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (original is not null && !string.Equals(Read(path), original, StringComparison.Ordinal))
                throw new IOException("配置文件在保存前已更改");
            File.Move(temporary, path, overwrite: original is not null);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* 清理失败不掩盖写入错误 */ }
        }
    }

    internal static void Report(string path, string message, Exception error)
    {
        // 诊断日志关闭时也尽量保留恢复原因，日志只含文件名、操作和异常类型。
        var entry = $"[{DateTimeOffset.Now:O}] {Path.GetFileName(path)}：{message}（{error.GetType().Name}）";
        try { Trace.TraceError(entry); } catch { }
        try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(path)!, "config-recovery.log"), entry + Environment.NewLine); }
        catch { /* 配置目录不可写也不影响回退 */ }
    }
}
