using System.Data.OleDb;
using System.Diagnostics;
using System.Reflection;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>搜索浮层里的一行结果：名称 + 说明 + 图标字形 + 启动目标</summary>
public sealed class SearchEntry
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "\uE7F8";
    public string Target { get; init; } = "";

    /// <summary>来自启动器磁贴时带上原条目，交给磁贴的启动流程（能复用"已运行就切前台"的逻辑）</summary>
    public LaunchItem? Item { get; init; }
}

/// <summary>本机应用搜索：索引开始菜单（用户级 + 机器级）与桌面上的快捷方式，供顶栏搜索图标使用。
/// 索引是懒加载的：第一次搜索时才扫目录，之后复用；扫描范围只有这几层目录，开销很小。</summary>
public static class AppSearchService
{
    private static List<SearchEntry>? _cache;

    /// <summary>按名称做不区分大小写的包含匹配：前缀命中的排前面，同等条件下名字短的靠前</summary>
    public static List<SearchEntry> Search(string query, int limit = 9)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        var all = _cache ??= BuildIndex();
        return all
            .Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name.Length)
            .Take(limit)
            .ToList();
    }

    /// <summary>用 shell 打开结果：应用走 shell:AppsFolder 解析名，其余交给默认关联，失败返回 false</summary>
    public static bool Launch(SearchEntry entry)
    {
        try
        {
            if (entry.Target.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start("explorer.exe", $"\"{entry.Target}\"");
                return true;
            }
            using var process = Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"搜索启动失败 {entry.Target}: {ex.Message}");
            return false;
        }
    }

    // ---- shell:AppsFolder 索引（Win 搜索里的“应用”结果来源：控制面板、设置、任务管理器等都在这里）----

    private static List<SearchEntry>? _shellApps;
    private static readonly object ShellAppsLock = new();

    /// <summary>按名称匹配系统应用/商店应用/控制面板项；首次调用在 STA 线程上枚举一次，之后复用缓存</summary>
    public static List<SearchEntry> SearchShellApps(string query, int limit = 9)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        var all = ShellApps();
        return all
            .Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name.Length)
            .Take(limit)
            .ToList();
    }

    private static List<SearchEntry> ShellApps()
    {
        if (_shellApps is not null) return _shellApps;
        lock (ShellAppsLock)
        {
            if (_shellApps is not null) return _shellApps;
            List<SearchEntry>? built = null;
            // Shell.Application 在 STA 上最稳（后台线程池是 MTA），枚举只做一次，阻塞一下可以接受
            var thread = new Thread(() => built = BuildShellApps()) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(8000))
            {
                DebugLog.Write("应用列表枚举超时（shell:AppsFolder）");
                built = [];
            }
            return _shellApps = built ?? [];
        }
    }

    private static List<SearchEntry> BuildShellApps()
    {
        var list = new List<SearchEntry>();
        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            var ns = Call(shell, "NameSpace", "shell:AppsFolder");
            var items = Call(ns, "Items");
            if (items is null) return list;
            var count = Convert.ToInt32(Get(items, "Count") ?? 0);
            for (var i = 0; i < count; i++)
            {
                var item = Call(items, "Item", i);
                if (item is null) continue;
                var name = Get(item, "Name") as string ?? "";
                var path = Get(item, "Path") as string ?? "";
                if (name.Length == 0 || path.Length == 0) continue;
                // 网址快捷方式按原样打开；其余（UWP/AUMID、经典程序）统一用 shell:AppsFolder 解析名启动
                var target = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(path)
                    ? path
                    : "shell:AppsFolder\\" + path;
                list.Add(new SearchEntry
                {
                    Name = name,
                    Detail = "应用",
                    Glyph = "\uE7F8",
                    Target = target
                });
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"应用列表枚举失败（shell:AppsFolder）: {ex.Message}");
        }
        DebugLog.Write($"应用列表: shell:AppsFolder 枚举到 {list.Count} 项");
        return list;
    }

    /// <summary>后期绑定调用 COM 方法（Shell.Application 没有编译期类型）</summary>
    private static object? Call(object? target, string method, params object[] args)
    {
        if (target is null) return null;
        try
        {
            return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后期绑定读 COM 属性：必须用 GetProperty，用 InvokeMethod 会读不到（返回 null 还不报错）</summary>
    private static object? Get(object? target, string property, params object[] args)
    {
        if (target is null) return null;
        try
        {
            return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, args);
        }
        catch
        {
            try
            {
                return target.GetType().InvokeMember(property, BindingFlags.InvokeMethod, null, target, args);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>用 Windows 搜索索引（SYSTEMINDEX）查本机文件：和系统搜索是同一份数据源，
    /// 覆盖用户目录、程序目录等已索引位置（文档、图片、视频、程序等都能命中）。
    /// 索引服务不可用时返回空列表，调用方回落到只显示应用/磁贴结果。</summary>
    public static async Task<List<SearchEntry>> SearchFilesAsync(string query, int limit, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        var like = q.Replace("'", "''");
        var sql =
            $"SELECT TOP {limit} System.ItemNameDisplay, System.ItemUrl, System.ItemTypeText " +
            $"FROM SYSTEMINDEX WHERE System.ItemNameDisplay LIKE '%{like}%' " +
            "ORDER BY System.ItemNameDisplay";
        try
        {
            return await Task.Run(() =>
            {
                var list = new List<SearchEntry>();
                using var conn = new OleDbConnection(
                    "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
                conn.Open();
                using var cmd = new OleDbCommand(sql, conn) { CommandTimeout = 8 };
                using var reader = cmd.ExecuteReader();
                while (!ct.IsCancellationRequested && list.Count < limit && reader.Read())
                {
                    // ItemPathDisplay 是本地化显示路径（如 C:\用户\...），磁盘上并不存在；
                    // ItemUrl 才是规范的 file: URL（C:/Users/...），去掉前缀后即可直接启动
                    var path = reader.IsDBNull(1) ? "" : PathFromUrl(reader.GetString(1));
                    if (string.IsNullOrEmpty(path)) continue;
                    var name = reader.IsDBNull(0) ? Path.GetFileName(path) : reader.GetString(0);
                    var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var folder = Path.GetDirectoryName(path) ?? "";
                    list.Add(new SearchEntry
                    {
                        Name = name,
                        Detail = string.IsNullOrEmpty(type) ? folder : $"{type} · {folder}",
                        Glyph = GlyphFor(Path.GetExtension(path)),
                        Target = path
                    });
                }
                return list;
            }, ct);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"文件搜索失败（Windows 搜索索引不可用？）: {ex.Message}");
            return [];
        }
    }

    /// <summary>把索引里的 file: URL 转成可启动的磁盘路径；非文件条目（应用 URI 等）返回空</summary>
    private static string PathFromUrl(string url)
    {
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return "";
        var path = Uri.UnescapeDataString(url[5..]).TrimStart('/').Replace('/', '\\');
        return path;
    }

    /// <summary>按扩展名给结果挑一个近似图标（文件类型太多，只分几大类）</summary>
    private static string GlyphFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".exe" or ".lnk" or ".url" or ".msi" or ".bat" or ".cmd" => "\uE7F8",  // 程序
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" => "\uEB9F", // 图片
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" => "\uE714", // 视频
        ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => "\uE8D6",          // 音频
        ".zip" or ".rar" or ".7z" or ".iso" or ".tar" or ".gz" => "\uE8DE",   // 压缩包
        _ => "\uE8A5"                                                          // 文档/其他
    };

    private static List<SearchEntry> BuildIndex()
    {
        var entries = new Dictionary<string, SearchEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            try { AddFolder(root, entries, 0); }
            catch (Exception ex) { DebugLog.Write($"搜索索引目录失败 {root}: {ex.Message}"); }
        }
        return entries.Values.OrderBy(e => e.Name, StringComparer.CurrentCulture).ToList();
    }

    private static IEnumerable<string> Roots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>递归收集快捷方式；限制深度避免异常目录结构把扫描拖长</summary>
    private static void AddFolder(string dir, Dictionary<string, SearchEntry> entries, int depth)
    {
        if (depth > 4) return;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file);
            var isUrl = ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
            if (!isUrl && !ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name)) continue;
            // 用户级与机器级常有同名条目，只保留先扫到的那条
            entries.TryAdd(name, new SearchEntry
            {
                Name = name,
                Detail = isUrl ? "网站快捷方式" : "应用快捷方式",
                Glyph = isUrl ? "\uE774" : "\uE7F8",
                Target = file
            });
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
            AddFolder(sub, entries, depth + 1);
    }
}
