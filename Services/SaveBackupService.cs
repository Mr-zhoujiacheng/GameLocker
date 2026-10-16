using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>游戏存档备份/同步过程中的一条进度/结果提示（Error=true 时以警告样式显示）</summary>
public readonly record struct BackupMessage(string Text, bool Error);

/// <summary>
/// 游戏存档小组件服务：点磁贴 / A 键 → 定位当前正在玩的游戏的存档 → 打包为 zip；
/// Y 键 / 长按 → 把最近一份备份解压覆盖回游戏本地存档（见 Sync）。
/// 形态照 TrainerService：_busy 去重 + 后台执行 + StatusChanged 上报进度。
/// 「本地」= 写入本地目录（默认 %APPDATA%\GameLocker\backups，可在编辑页指定）；
/// 「云端」= 通过 WebDAV 上传到网盘（默认坚果云，需先在 设置 → 云端备份 填地址/账号/应用密码）。
/// </summary>
public static class SaveBackupService
{
    /// <summary>默认备份目录（未指定目标文件夹时使用）：%APPDATA%\GameLocker\backups</summary>
    public static readonly string DefaultBackupsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "backups");

    /// <summary>进度/结果提示（可能来自后台线程，订阅方自行 marshal 到 UI 线程）</summary>
    public static event Action<BackupMessage>? StatusChanged;

    private static void Report(string text, bool error = false) =>
        StatusChanged?.Invoke(new BackupMessage(text, error));

    private static bool _busy;

    /// <summary>点击 / A 键触发：备份当前游戏的存档（后台执行，进度经 StatusChanged 上报）</summary>
    public static void Activate(LaunchItem item)
    {
        if (_busy)
        {
            Report("正在备份，请稍候…", true);
            return;
        }
        _busy = true;
        _ = ActivateAsync(item);
    }

    /// <summary>Y 键 / 长按触发：把最近一份备份覆盖回游戏本地存档（后台执行，进度经 StatusChanged 上报）</summary>
    public static void Sync(LaunchItem item)
    {
        if (_busy)
        {
            Report("正在处理存档，请稍候…", true);
            return;
        }
        _busy = true;
        _ = SyncAsync(item);
    }

    private static async Task ActivateAsync(LaunchItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(TrainerService.LastExeName))
            {
                Report("未检测到游戏进程，请先运行游戏再点此磁贴", true);
                return;
            }
            if (!TrainerService.IsGameRunning())
            {
                Report($"游戏 {TrainerService.CurrentGameName()} 已退出，请先运行游戏", true);
                return;
            }

            var gameName = Sanitize(TrainerService.CurrentGameName());
            Report("正在查找存档位置…");
            await SaveManifest.EnsureLoadedAsync(t => Report(t));

            var exePath = TrainerService.CurrentGameExePath();
            var location = SavePathResolver.Resolve(exePath, TrainerService.LastWindowTitle, TrainerService.LastExeName);
            if (location.Paths.Count == 0)
            {
                Report($"未找到 {gameName} 的存档位置", true);
                return;
            }

            if (item.BackupMode == BackupMode.Cloud)
            {
                await CloudBackupAsync(item, gameName, location);
                return;
            }

            Report("正在打包存档…");
            var dir = Path.Combine(TargetDir(item), gameName);
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var baseName = $"{gameName}-{stamp}";
            var zipPath = Path.Combine(dir, baseName + ".zip");

            var bytes = await Task.Run(() => Pack(location.Paths, zipPath));
            SaveMeta(dir, baseName, location, bytes);
            Prune(dir, item.BackupKeep);

            var sizeMb = bytes / 1024d / 1024d;
            Report($"已备份到 {zipPath}（{sizeMb:0.#}MB）");
        }
        catch (Exception ex)
        {
            Report("备份失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 云端（WebDAV 上传）：打包到本地临时 zip → 上传 → 删临时文件 → 按保留份数裁剪云端旧备份。
    /// 未配置 / 上传失败都只提示错误，**不**回退写本地（避免用户误以为已上云）。
    /// </summary>
    private static async Task CloudBackupAsync(LaunchItem item, string gameName, SaveLocation location)
    {
        var acct = WebDavService.CurrentAccount();
        if (acct is null)
        {
            Report("未配置云端备份：请到「设置 → 云端备份」填写 WebDAV 地址、账号与应用密码", true);
            return;
        }
        var dav = acct.Value;

        var rootRaw = SettingsService.Current.WebDavRootFolder;
        var root = string.IsNullOrWhiteSpace(rootRaw) ? "GameLocker" : Sanitize(rootRaw);
        var folder = $"{root}/{gameName}";

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{gameName}-{stamp}.zip";
        var tmpDir = Path.Combine(DefaultBackupsDir, "_tmp");
        var tmpZip = Path.Combine(tmpDir, fileName);

        try
        {
            Report("正在准备云端目录…");
            await WebDavService.EnsureFolderAsync(dav, folder);

            Report("正在打包存档…");
            Directory.CreateDirectory(tmpDir);
            var bytes = await Task.Run(() => Pack(location.Paths, tmpZip));

            Report("正在上传到云端…");
            await WebDavService.UploadAsync(dav, folder, fileName, tmpZip);

            var keep = item.BackupKeep <= 0 ? 5 : item.BackupKeep;
            Report("正在整理云端历史备份…");
            try
            {
                var list = await WebDavService.ListAsync(dav, folder);
                foreach (var old in list.OrderByDescending(e => e.Modified).Skip(keep))
                {
                    try { await WebDavService.DeleteAsync(dav, old.Href); }
                    catch { /* 单条删除失败不影响本次备份结果 */ }
                }
            }
            catch { /* 列举失败不影响本次备份结果 */ }

            var sizeMb = bytes / 1024d / 1024d;
            Report($"已备份到云端：{folder}/{fileName}（{sizeMb:0.#}MB）");
        }
        catch (Exception ex)
        {
            Report("云端备份失败：" + ex.Message, true);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 清理失败无害 */ }
        }
    }

    /// <summary>
    /// 同步（Y 键 / 长按）：与备份流程对称——锁定当前正在玩的游戏 → 取最近一份备份 → 解压覆盖回本地存档。
    /// 来源跟随磁贴的备份方式：本地磁贴取本地目录最近一份 zip；云端磁贴下载云端最近一份 zip。
    /// </summary>
    private static async Task SyncAsync(LaunchItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(TrainerService.LastExeName))
            {
                Report("未检测到游戏进程，请先运行游戏再同步存档", true);
                return;
            }
            if (!TrainerService.IsGameRunning())
            {
                Report($"游戏 {TrainerService.CurrentGameName()} 已退出，请先运行游戏", true);
                return;
            }

            var gameName = Sanitize(TrainerService.CurrentGameName());
            await SaveManifest.EnsureLoadedAsync(t => Report(t));

            var exePath = TrainerService.CurrentGameExePath();
            var location = SavePathResolver.Resolve(exePath, TrainerService.LastWindowTitle, TrainerService.LastExeName);
            if (location.Paths.Count == 0)
            {
                Report($"未找到 {gameName} 的存档位置", true);
                return;
            }

            if (item.BackupMode == BackupMode.Cloud)
            {
                await CloudSyncAsync(gameName, location);
                return;
            }
            LocalSync(item, gameName, location);
        }
        catch (Exception ex)
        {
            Report("存档同步失败：" + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>本地同步：取目标目录下该游戏最近一份 zip，解压覆盖回本地存档</summary>
    private static void LocalSync(LaunchItem item, string gameName, SaveLocation location)
    {
        var dir = Path.Combine(TargetDir(item), gameName);
        var zip = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.zip")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (zip is null)
        {
            Report($"本地没有 {gameName} 的备份，请先按 A 备份一次", true);
            return;
        }

        Report("正在恢复存档…");
        var count = Restore(location.Paths, zip.FullName);
        Report($"已用最近的本地备份覆盖 {gameName} 的存档：{zip.Name}（{count} 个文件）");
    }

    /// <summary>
    /// 云端同步：取云端该游戏目录下最近一份 zip → 下载到本地临时文件 → 解压覆盖回本地存档 → 删临时文件。
    /// 未配置云端 / 云端无备份 / 下载失败都只提示错误，**不**回退读本地（与云端备份对称）。
    /// </summary>
    private static async Task CloudSyncAsync(string gameName, SaveLocation location)
    {
        var acct = WebDavService.CurrentAccount();
        if (acct is null)
        {
            Report("未配置云端备份：请到「设置 → 云端备份」填写 WebDAV 地址、账号与应用密码", true);
            return;
        }
        var dav = acct.Value;

        var rootRaw = SettingsService.Current.WebDavRootFolder;
        var root = string.IsNullOrWhiteSpace(rootRaw) ? "GameLocker" : Sanitize(rootRaw);
        var folder = $"{root}/{gameName}";

        var tmpDir = Path.Combine(DefaultBackupsDir, "_tmp");
        string? tmpZip = null;
        try
        {
            Report("正在查询云端备份…");
            var list = await WebDavService.ListAsync(dav, folder);
            var latest = list
                .Where(e => e.Href.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Modified)
                .ToArray();
            if (latest.Length == 0)
            {
                Report($"云端没有 {gameName} 的备份，请先按 A 备份一次", true);
                return;
            }

            var href = latest[0].Href;
            var fileName = Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]);
            tmpZip = Path.Combine(tmpDir, fileName);
            Directory.CreateDirectory(tmpDir);

            Report("正在从云端下载备份…");
            await WebDavService.DownloadAsync(dav, href, tmpZip);

            Report("正在恢复存档…");
            var count = Restore(location.Paths, tmpZip);
            Report($"已用最近的云端备份覆盖 {gameName} 的存档：{fileName}（{count} 个文件）");
        }
        catch (Exception ex)
        {
            Report("云端同步失败：" + ex.Message, true);
        }
        finally
        {
            try { if (tmpZip is not null && File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 清理失败无害 */ }
        }
    }

    /// <summary>把备份 zip 解压覆盖回本地存档：zip 内以源目录名为前缀，反查回 SaveLocation.Paths；返回成功写入的文件数</summary>
    private static int Restore(IReadOnlyList<string> sources, string zipPath)
    {
        // 与 Pack 同规则重建「前缀 → 源目录」映射（重名时同样加序号后缀）
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources)
        {
            var prefix = Path.GetFileName(src.TrimEnd('\\'));
            if (string.IsNullOrEmpty(prefix)) prefix = "save";
            if (!used.Add(prefix)) prefix += "_" + (used.Count + 1);
            map[prefix] = src;
        }

        var count = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // 目录项跳过
            var name = entry.FullName.Replace('\\', '/');
            var idx = name.IndexOf('/');
            if (idx <= 0) continue;
            if (!map.TryGetValue(name[..idx], out var srcDir)) continue;
            var rel = name[(idx + 1)..];
            var dst = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                entry.ExtractToFile(dst, overwrite: true);
                count++;
            }
            catch { /* 单个文件写入失败（被占用）：跳过，不影响其他文件 */ }
        }
        return count;
    }

    /// <summary>备份目标目录：编辑页指定的文件夹优先，未指定时用默认目录</summary>
    public static string TargetDir(LaunchItem item) =>
        string.IsNullOrWhiteSpace(item.BackupFolder) ? DefaultBackupsDir : item.BackupFolder!;

    /// <summary>打开当前备份目标目录（资源管理器）；目录不存在时先建出来</summary>
    public static void OpenBackupsFolder(LaunchItem item)
    {
        try
        {
            var dir = TargetDir(item);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了，不打断编辑流程 */ }
    }

    // ---- 打包 ----

    /// <summary>把各存档目录打包：以源目录名为包内前缀保留相对结构；返回 zip 字节数</summary>
    private static long Pack(IReadOnlyList<string> sources, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources)
        {
            var prefix = Path.GetFileName(src.TrimEnd('\\'));
            if (string.IsNullOrEmpty(prefix)) prefix = "save";
            if (!used.Add(prefix)) prefix += "_" + (used.Count + 1);
            AddDirectory(zip, src, prefix);
        }
        return new FileInfo(zipPath).Length;
    }

    private static void AddDirectory(ZipArchive zip, string srcDir, string prefix)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories); }
        catch { return; }
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1024L * 1024 * 1024) continue; // 跳过超大无关文件（>1GB）
                var rel = Path.GetRelativePath(srcDir, file);
                var entryName = Path.Combine(prefix, rel);
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var dst = entry.Open();
                using var src = File.OpenRead(file);
                src.CopyTo(dst);
            }
            catch { /* 单个文件读失败/被占用：跳过，不影响整体备份 */ }
        }
    }

    /// <summary>写一份与 zip 同名的 meta.json：命中方式、源路径、字节数、时间</summary>
    private static void SaveMeta(string dir, string baseName, SaveLocation location, long bytes)
    {
        try
        {
            var meta = new
            {
                method = location.Method,
                sources = location.Paths,
                bytes,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            File.WriteAllText(Path.Combine(dir, baseName + ".meta.json"), JsonSerializer.Serialize(meta));
        }
        catch { /* meta 写失败不影响备份本体 */ }
    }

    /// <summary>保留策略：只清理本服务生成的 zip + meta.json，按修改时间倒序保留 keep 份</summary>
    private static void Prune(string dir, int keep)
    {
        if (keep <= 0) keep = 5;
        try
        {
            var zips = Directory.EnumerateFiles(dir, "*.zip")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var old in zips.Skip(keep))
            {
                try
                {
                    var meta = Path.Combine(dir, Path.GetFileNameWithoutExtension(old.Name) + ".meta.json");
                    old.Delete();
                    if (File.Exists(meta)) File.Delete(meta);
                }
                catch { /* 删除失败（被占用）跳过 */ }
            }
        }
        catch { /* 目录枚举失败跳过 */ }
    }

    /// <summary>整理成本地文件名与云端路径都可用的名称（云端对非法字符的限制比 Windows 文件名更严）</summary>
    private static string Sanitize(string name)
    {
        const string extra = ":\\/#%?*\"<>|";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var bad = c < ' ' || extra.Contains(c) || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0;
            sb.Append(bad ? '_' : c);
        }
        var s = sb.ToString().Trim().TrimEnd('.').Trim();
        return s.Length == 0 ? "未命名游戏" : s;
    }
}
