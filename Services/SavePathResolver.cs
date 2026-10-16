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
        var entry = SaveManifest.TryFind(gameName, installDirs, appId);
        if (entry is not null && entry.Files.Count > 0)
        {
            var paths = new List<string>();
            foreach (var raw in entry.Files) paths.AddRange(Expand(raw, baseDir));
            var filtered = Filter(paths, baseDir);
            if (filtered.Count > 0)
            {
                var how = appId is not null && entry.SteamId == appId ? "Steam"
                    : installDirs.Count > 0 ? "目录名" : "名称";
                return new SaveLocation(filtered, $"清单({how})");
            }
        }

        // ② Steam 云存档直取（清单未命中或清单路径都不存在时）
        if (steamRoot is not null && appId is not null)
        {
            var remote = FindSteamUserdata(steamRoot, appId.Value);
            if (remote.Count > 0) return new SaveLocation(remote, "Steam 云存档");
        }

        // ③ 引擎启发式兜底
        var heuristic = Filter(HeuristicCandidates(installDirName, gameName, exeName), baseDir);
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

    /// <summary>Steam userdata 直取：&lt;steam&gt;\userdata\&lt;accountId&gt;\&lt;appid&gt;\remote（多账号取最近修改的）</summary>
    private static List<string> FindSteamUserdata(string steamRoot, long appId)
    {
        var result = new List<string>();
        var userdata = Path.Combine(steamRoot, "userdata");
        try
        {
            var candidates = new List<(string Path, DateTime Time)>();
            foreach (var acct in Directory.EnumerateDirectories(userdata))
            {
                var remote = Path.Combine(acct, appId.ToString(), "remote");
                if (Directory.Exists(remote) && HasEntries(remote))
                {
                    try { candidates.Add((remote, Directory.GetLastWriteTime(remote))); }
                    catch { candidates.Add((remote, DateTime.MinValue)); }
                }
            }
            if (candidates.Count > 0)
                result.Add(candidates.OrderByDescending(c => c.Time).First().Path); // 多账号取最近修改
        }
        catch { /* userdata 不存在：忽略 */ }
        return result;
    }

    // ---- 占位符展开 ----

    /// <summary>把含占位符的原始路径展开为若干候选绝对路径（先展开 &lt;…|…&gt; 备选，再展开 * 通配）</summary>
    private static IEnumerable<string> Expand(string raw, string installRoot)
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
                    var val = Substitute(opt.Trim(), installRoot);
                    if (val is null) continue; // 未支持/未知的占位符：丢弃该分支
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

    /// <summary>占位符取值；未知返回 null（丢弃该分支）</summary>
    private static string? Substitute(string key, string installRoot) => key.ToLowerInvariant() switch
    {
        "base" => installRoot.Length > 0 ? installRoot : null,
        "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "winlocalappdata" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winlocalappdatalow" => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        "winappdata" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "winprogramdata" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "windocuments" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "osusername" => Environment.UserName,
        _ => null
    };

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
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir, seg))
                            next.Add(sub + "\\");
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
