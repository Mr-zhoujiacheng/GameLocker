using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLocker.Services;

/// <summary>存档定位结果：路径列表 + 命中方式（用于提示与 meta 记录）</summary>
public readonly record struct SaveLocation(IReadOnlyList<string> Paths, string Method);

/// <summary>
/// 存档路径定位：清单（ludusavi）优先 + Steam 直取 + 引擎启发式兜底。
/// 占位符展开支持 <c>&lt;key|alt1|alt2&gt;</c> 备选语法与 <c>*</c> 目录通配；
/// 不支持的语法整条跳过，不报错。结果过滤掉不存在 / 空目录 / 安装根目录本身。
/// </summary>
public static class SavePathResolver
{
    /// <summary>一个待探测的候选路径（原始含占位符）→ 展开后的候选</summary>
    private static readonly Regex PlaceholderRegex = new("<([^<>]+)>", RegexOptions.Compiled);

    /// <summary>
    /// 定位当前游戏的存档路径。
    /// <param name="exePath">当前游戏进程的可执行文件完整路径（可为空 → 只用启发式）</param>
    /// <param name="gameName">当前游戏显示名（窗口标题 / 进程名）</param>
    /// <param name="exeName">当前游戏进程名（不含扩展名）</param>
    /// <returns>命中方式为「清单(…)」「Steam 云存档」「启发式」；未命中时 Paths 为空</returns>
    public static SaveLocation Resolve(string? exePath, string? gameName, string? exeName)
    {
        var installRoot = "";
        var installDirName = "";
        string? steamRoot = null;
        if (!string.IsNullOrEmpty(exePath))
        {
            installRoot = Path.GetDirectoryName(exePath) ?? "";
            (steamRoot, installDirName) = DetectSteam(exePath);
        }

        var appId = ResolveAppId(steamRoot, installDirName);
        // 游戏安装根：Steam 游戏取 steamapps\common\<目录名>（对应清单的 <base>），否则取 exe 所在目录
        var baseDir = steamRoot is not null && installDirName.Length > 0
            ? Path.Combine(steamRoot, "steamapps", "common", installDirName)
            : installRoot;
        var installDirs = new List<string>();
        if (installDirName.Length > 0) installDirs.Add(installDirName);
        // 非 Steam：exe 所在目录名也作为安装目录名候选
        if (installDirs.Count == 0 && installRoot.Length > 0)
            installDirs.Add(Path.GetFileName(installRoot));

        // ① 清单命中 → 展开 files
        DebugLog.Write($"存档定位: 输入 进程='{exeName}' 标题='{gameName}' exe路径='{exePath ?? "(无)"}' " +
            $"→ steam='{steamRoot ?? "(无)"}' 安装目录='{installDirName}' appId={appId?.ToString() ?? "(无)"}");

        var entry = SaveManifest.TryFind(gameName, installDirs, appId);
        DebugLog.Write($"存档定位: 清单命中={(entry is not null)} 条目路径数={entry?.Files.Count ?? 0}");
        if (entry is not null && entry.Files.Count > 0)
        {
            var paths = new List<string>();
            foreach (var raw in entry.Files) paths.AddRange(Expand(raw, baseDir, steamRoot));
            var filtered = Filter(paths, baseDir);
            if (filtered.Count > 0)
            {
                var how = appId is not null && entry.SteamId == appId ? "Steam"
                    : installDirs.Count > 0 ? "目录名" : "名称";
                return new SaveLocation(filtered, $"清单({how})");
            }
            DebugLog.Write("存档定位: 清单条目展开后无有效目录，候选 " + string.Join(" | ", paths.Distinct().Take(20)));
        }

        // ② Steam 云存档直取（清单未命中或清单路径都不存在时）
        if (steamRoot is not null && appId is not null)
        {
            var remote = FindSteamUserdata(steamRoot, appId.Value);
            if (remote.Count > 0) return new SaveLocation(remote, "Steam 云存档");
        }

        // ③ 引擎启发式兜底
        var candidates = HeuristicCandidates(installDirName, gameName, exeName).Distinct().ToList();
        var heuristic = Filter(candidates, baseDir);
        if (heuristic.Count == 0)
            DebugLog.Write("存档定位: 清单未命中且启发式无结果，已尝试候选 " + string.Join(" | ", candidates));
        return heuristic.Count > 0
            ? new SaveLocation(heuristic, "启发式")
            : new SaveLocation([], "未找到");
    }

    // ---- Steam 探测 ----

    /// <summary>从 exe 路径识别 Steam 安装根与安装目录名（形如 &lt;steam&gt;\steamapps\common\&lt;dir&gt;\…）</summary>
    private static (string? SteamRoot, string InstallDirName) DetectSteam(string exePath)
    {
        const string marker = @"\steamapps\common\";
        var idx = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (null, "");
        var root = exePath[..idx];
        var rest = exePath[(idx + marker.Length)..];
        var name = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return (root, name);
    }

    /// <summary>反查 appid：优先 appmanifest_*.acf 里 installdir 与安装目录名匹配的那条</summary>
    private static long? ResolveAppId(string? steamRoot, string installDirName)
    {
        if (steamRoot is null || installDirName.Length == 0) return null;
        var apps = Path.Combine(steamRoot, "steamapps");
        try
        {
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var m = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (!m.Success || !string.Equals(m.Groups[1].Value, installDirName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = Regex.Match(text, "\"appid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
                if (id.Success && long.TryParse(id.Groups[1].Value, out var v)) return v;
            }
        }
        catch { /* 目录不存在 / 无权限：忽略 */ }
        return null;
    }

    /// <summary>
    /// Steam userdata 直取：&lt;根&gt;\userdata\&lt;accountId&gt;\&lt;appid&gt;\remote（多账号取最近修改的）。
    /// 注意这里的「根」有两种：客户端安装根（userdata 真正所在）与库目录（兜底），见 UserdataRoots。
    /// </summary>
    private static List<string> FindSteamUserdata(string? steamRoot, long appId)
    {
        var result = new List<string>();
        var candidates = new List<(string Path, DateTime Time)>();
        foreach (var root in UserdataRoots(steamRoot).ToList())
        {
            var userdata = Path.Combine(root, "userdata");
            try
            {
                foreach (var acct in Directory.EnumerateDirectories(userdata))
                {
                    var remote = Path.Combine(acct, appId.ToString(), "remote");
                    if (Directory.Exists(remote) && HasEntries(remote))
                    {
                        try { candidates.Add((remote, Directory.GetLastWriteTime(remote))); }
                        catch { candidates.Add((remote, DateTime.MinValue)); }
                    }
                }
            }
            catch { /* userdata 不存在：试下一个根 */ }
        }
        if (candidates.Count > 0)
            result.Add(candidates.OrderByDescending(c => c.Time).First().Path); // 多账号取最近修改
        return result;
    }

    // ---- 占位符展开 ----

    /// <summary>把含占位符的原始路径展开为若干候选绝对路径（先展开 &lt;…|…&gt; 备选，再展开 * 通配）</summary>
    private static IEnumerable<string> Expand(string raw, string installRoot, string? steamRoot)
    {
        var alts = new List<string> { raw };
        for (var guard = 0; guard < 8 && alts.Any(s => PlaceholderRegex.IsMatch(s)); guard++)
        {
            var next = new List<string>();
            foreach (var s in alts)
            {
                var m = PlaceholderRegex.Match(s);
                if (!m.Success) { next.Add(s); continue; }
                foreach (var opt in m.Groups[1].Value.Split('|'))
                {
                    foreach (var val in SubstituteAll(opt.Trim(), installRoot, steamRoot))
                        next.Add(s[..m.Index] + val + s[(m.Index + m.Length)..]);
                }
            }
            alts = next;
            if (alts.Count > 64) break;
        }

        var final = new List<string>();
        foreach (var s in alts)
        {
            if (s.Contains('<')) continue; // 残留未识别占位符：整条跳过
            final.AddRange(ExpandWildcard(s));
        }
        return final;
    }

    /// <summary>
    /// 单个占位符的全部取值；不支持的占位符返回空集合（调用方据此丢弃该分支）。
    /// 之所以返回集合：&lt;root&gt; 在清单里既有「Steam 客户端安装根」用法（userdata），也有「库目录」用法
    /// （steamapps/common），分盘时两者不同，只能都给出候选，再交由 Filter 按目录是否真实存在筛掉。
    /// </summary>
    private static IEnumerable<string> SubstituteAll(string key, string installRoot, string? steamRoot)
    {
        switch (key.ToLowerInvariant())
        {
            case "base":
                return installRoot.Length > 0 ? new[] { installRoot } : Array.Empty<string>();
            case "home":
                return [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
            case "winlocalappdata":
                return [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)];
            case "winlocalappdatalow":
                return [Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")];
            case "winappdata":
                return [Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)];
            case "winprogramdata":
                return [Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)];
            case "windocuments":
                return [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)];
            case "osusername":
                return [Environment.UserName];
            case "root":
                return RootCandidates(steamRoot);
            case "storeuserid":
                return StoreUserIds(steamRoot);
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>&lt;root&gt; 候选：Steam 客户端安装根（userdata 所在），外加由 exe 推出的库目录兜底</summary>
    private static List<string> RootCandidates(string? steamRoot)
    {
        var list = new List<string>();
        if (SteamClientRoot() is { } client) list.Add(client);
        if (!string.IsNullOrEmpty(steamRoot) && !list.Contains(steamRoot, StringComparer.OrdinalIgnoreCase))
            list.Add(steamRoot);
        return list;
    }

    /// <summary>&lt;storeUserId&gt; 候选：Steam userdata 下的账号目录名（清单里也常直接嵌在文件名中）</summary>
    private static List<string> StoreUserIds(string? steamRoot)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in UserdataRoots(steamRoot).ToList())
        {
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(Path.Combine(root, "userdata")).ToList(); }
            catch { continue; }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.Length > 0 && seen.Add(name)) list.Add(name);
            }
        }
        return list;
    }

    /// <summary>可能存放 Steam userdata 的根目录：客户端安装根优先，库目录兜底（二者同盘时相同）</summary>
    private static IEnumerable<string> UserdataRoots(string? steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SteamClientRoot() is { } client && seen.Add(client)) yield return client;
        if (!string.IsNullOrEmpty(steamRoot) && seen.Add(steamRoot!)) yield return steamRoot!;
    }

    private static string? _steamClientRoot;
    private static bool _steamClientProbed;

    /// <summary>
    /// Steam 客户端安装根。exe 路径只能看出「库目录」（steamapps\common 前那段），而 Steam 把 userdata 固定
    /// 放在客户端安装根 —— 这正是「只在默认安装（客户端与库同盘）才碰巧可用」的原因，故必须另从注册表取。
    /// </summary>
    private static string? SteamClientRoot()
    {
        if (_steamClientProbed) return _steamClientRoot;
        _steamClientProbed = true;
        foreach (var (key, name) in SteamRegistryKeys)
        {
            try
            {
                if (Registry.GetValue(key, name, null) is not string raw || raw.Length == 0) continue;
                var path = Path.GetFullPath(raw.Replace('/', '\\')).TrimEnd('\\');
                if (Directory.Exists(path)) { _steamClientRoot = path; break; }
            }
            catch { /* 键不存在 / 无权限：试下一个 */ }
        }
        DebugLog.Write($"存档定位: Steam 客户端根={_steamClientRoot ?? "(未探测到)"}");
        return _steamClientRoot;
    }

    private static readonly (string Key, string Name)[] SteamRegistryKeys =
    [
        (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
        (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
    ];

    /// <summary>把含 * 的目录模式按目录枚举逐段展开（无 * 时原样返回）</summary>
    private static IEnumerable<string> ExpandWildcard(string pattern)
    {
        if (!pattern.Contains('*')) return [pattern];

        var normalized = pattern.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return [];

        var current = new List<string> { segments[0] + "\\" }; // 盘符或 UNC 段
        foreach (var seg in segments.Skip(1))
        {
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (seg.Contains('*'))
                {
                    List<string> dirs;
                    try { dirs = Directory.EnumerateDirectories(dir, seg).ToList(); }
                    catch { dirs = []; }
                    if (dirs.Count > 0)
                    {
                        foreach (var sub in dirs) next.Add(sub + "\\");
                        continue;
                    }
                    // 该段其实是「文件名模式」（如 user_*.dat / *.sav，清单里非常常见）：匹配到的是文件而非目录，
                    // 而备份要的是它所在的目录，故整段退化为父目录本身（后面 Filter 还会再校验存在性）。
                    try
                    {
                        if (Directory.EnumerateFiles(dir, seg).Any()) next.Add(dir);
                    }
                    catch { /* 无权限 / 目录不存在：该分支无结果 */ }
                }
                else
                {
                    next.Add(Path.Combine(dir, seg));
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        return current.Select(p => p.TrimEnd('\\')).Where(p => p.Length > 0);
    }

    // ---- 引擎启发式 ----

    /// <summary>按引擎惯例生成候选：Unreal / Unity / Godot / 常见「文档\My Games」目录</summary>
    private static List<string> HeuristicCandidates(
        string installDirName, string? gameName, string? exeName)
    {
        var names = new List<string>();
        void Add(string? n)
        {
            n = n?.Trim();
            if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase))
                names.Add(n!);
        }
        Add(installDirName);
        Add(exeName);
        Add(gameName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

        var candidates = new List<string>();
        foreach (var n in names)
        {
            // Unreal：Saved\SaveGames；Unity：LocalLow\<公司>\<产品>（公司名未知，用通配）
            candidates.Add(Path.Combine(local, n, "Saved", "SaveGames"));
            candidates.Add(Path.Combine(local, n, "Saved"));
            candidates.AddRange(ExpandWildcard(Path.Combine(localLow, "*", n)));
            candidates.Add(Path.Combine(roaming, n));
            candidates.Add(Path.Combine(docs, "My Games", n));
            candidates.Add(Path.Combine(docs, n));
        }
        return candidates;
    }

    // ---- 结果过滤 ----

    /// <summary>保留存在且非空、且不等于安装根/盘符根的目录；去重</summary>
    private static List<string> Filter(IEnumerable<string> candidates, string installRoot)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            string full;
            try { full = Path.GetFullPath(c).TrimEnd('\\'); }
            catch { continue; }
            if (full.Length == 0) continue;
            // 清单里不少条目直接指向单个文件（如 .../KillHouseGames/ActionSquad/userdata.bin）。
            // 备份以目录为单位，且同目录下的兄弟文件通常同属该游戏数据，故改取其父目录。
            try { if (File.Exists(full)) full = (Path.GetDirectoryName(full) ?? full).TrimEnd('\\'); }
            catch { /* 路径异常：保持原样，后面按目录校验失败即丢弃 */ }
            if (full.Length == 0 || seen.Contains(full)) continue;
            if (installRoot.Length > 0 && string.Equals(full, installRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue; // 不能把整个安装目录打进包里
            if (Path.GetPathRoot(full) is { } root && string.Equals(full, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue; // 盘符根
            try { if (!Directory.Exists(full) || !HasEntries(full)) continue; }
            catch { continue; }
            seen.Add(full);
            result.Add(full);
        }
        return result;
    }

    /// <summary>目录内是否有条目（防空目录）</summary>
    private static bool HasEntries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }
}
