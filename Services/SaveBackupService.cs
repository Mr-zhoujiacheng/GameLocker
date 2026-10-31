using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>游戏存档备份/同步过程中的一条进度/结果提示（Error=true 时以警告样式显示）</summary>
public readonly record struct BackupMessage(string Text, bool Error);

public sealed record SaveBackupVersion(string Location, string FileName, DateTime Created, DateTime Modified, long Size);

public sealed record SaveRestoreRequest(string GameName, bool Cloud, IReadOnlyList<SaveBackupVersion> Versions);
public sealed record SaveRestoreChoice(SaveBackupVersion Version, bool RestartGame);

/// <summary>
/// 游戏存档小组件服务：点磁贴 / A 键 → 定位当前正在玩的游戏的存档 → 打包为 zip；
/// Y 键 / 长按 → 把用户选定的备份版本解压覆盖回游戏本地存档（见 Sync）。
/// 形态照 TrainerService：_busy 去重 + 后台执行 + StatusChanged 上报进度。
/// 「本地」= 写入本地目录（默认 %APPDATA%\GameLocker\backups，可在编辑页指定）；
/// 「云端」= 上传到天翼云盘（需先在磁贴编辑页打开云端备份设置扫码登录）。
/// </summary>
public static class SaveBackupService
{
    /// <summary>默认备份目录（未指定目标文件夹时使用）：%APPDATA%\GameLocker\backups</summary>
    public static readonly string DefaultBackupsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "backups");

    /// <summary>进度/结果提示（可能来自后台线程，订阅方自行 marshal 到 UI 线程）</summary>
    public static event Action<BackupMessage>? StatusChanged;

    public static Func<SaveRestoreRequest, Task<SaveRestoreChoice?>>? ChooseRestoreAsync { get; set; }
    public static Func<string, Task<bool>>? ConfirmRestartAsync { get; set; }

    private static async Task<SaveRestoreChoice?> ChooseAsync(string gameName, bool cloud, IReadOnlyList<SaveBackupVersion> versions)
    {
        var choose = ChooseRestoreAsync ?? throw new IOException("无法显示备份选择窗口，请返回启动器后重试");
        var selected = await choose(new SaveRestoreRequest(gameName, cloud, versions));
        if (selected is null) Report("已取消恢复，存档未改动");
        else if (!versions.Contains(selected.Version)) throw new IOException("所选备份不在当前列表中");
        return selected;
    }

    private static DateTime BackupCreated(string fileName, string gameName, DateTime fallback) =>
        DateTime.TryParseExact(fileName[(gameName.Length + 1)..^4],
            new[] { "yyyyMMdd-HHmmss", "yyyyMMdd-HHmmss-fffffff" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var created)
            ? created : fallback;

    private static void Report(string text, bool error = false) =>
        StatusChanged?.Invoke(new BackupMessage(text, error));

    private static int _busy;

    /// <summary>点击 / A 键触发：备份当前游戏的存档（后台执行，进度经 StatusChanged 上报）</summary>
    public static void Activate(LaunchItem item)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Report("正在备份，请稍候…", true);
            return;
        }
        _ = ActivateAsync(item);
    }

    /// <summary>Y 键 / 长按触发：把用户选定的备份版本覆盖回游戏本地存档（后台执行，进度经 StatusChanged 上报）</summary>
    public static void Sync(LaunchItem item)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Report("正在处理存档，请稍候…", true);
            return;
        }
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
            if (!TrainerService.IsLastGameActuallyForeground())
            {
                Report($"请先把游戏「{TrainerService.CurrentGameName()}」切到前台再备份存档", true);
                return;
            }

            var exeName = TrainerService.LastExeName;
            var title = TrainerService.LastWindowTitle;
            var exePath = TrainerService.CurrentGameExePath();
            var gameName = Sanitize(TrainerService.CurrentGameName());
            DebugLog.Write($"存档备份: 触发 exe={exeName} 标题={title} 模式={item.BackupMode}");
            Report("正在查找存档位置…");
            await SaveManifest.EnsureLoadedAsync(t => Report(t));

            var location = await Task.Run(() => SavePathResolver.Resolve(exePath, title, exeName));
            DebugLog.Write($"存档备份: 定位 method={location.Method} paths={string.Join(" ; ", location.Paths)}");
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
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff");
            var baseName = $"{gameName}-{stamp}";
            var zipPath = Path.Combine(dir, baseName + ".zip");

            var bytes = await Task.Run(() => Pack(location.Paths, zipPath));
            SaveMeta(dir, baseName, location, bytes);
            Prune(dir, item.BackupKeep, gameName);

            var sizeMb = bytes / 1024d / 1024d;
            Report($"已备份到 {zipPath}（{sizeMb:0.#}MB）");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"存档备份: 异常 {ex.GetType().Name}: {ex.Message}");
            Report("备份失败：" + ex.Message, true);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 云端（天翼云上传）：打包到本地临时 zip → 上传 → 删临时文件 → 按保留份数裁剪云端旧备份。
    /// 未配置 / 上传失败都只提示错误，**不**回退写本地（避免用户误以为已上云）。
    /// </summary>
    private static async Task CloudBackupAsync(LaunchItem item, string gameName, SaveLocation location)
    {
        if (!CloudBackupProvider.IsSignedIn)
        {
            Report($"{CloudBackupProvider.DisplayName}未登录：请在存档磁贴编辑页打开「云端备份设置」扫码登录", true);
            return;
        }

        var root = Sanitize(CloudBackupProvider.RootFolder);
        var folder = $"{root}/{gameName}";

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff");
        var fileName = $"{gameName}-{stamp}.zip";
        var tmpDir = Path.Combine(DefaultBackupsDir, "_tmp");
        var tmpZip = Path.Combine(tmpDir, fileName);

        try
        {
            Report("正在准备云端目录…");
            await CloudBackupProvider.EnsureFolderAsync(folder);

            Report("正在打包存档…");
            Directory.CreateDirectory(tmpDir);
            var bytes = await Task.Run(() => Pack(location.Paths, tmpZip));

            Report("正在上传到云端…");
            await CloudBackupProvider.UploadAsync(folder, fileName, tmpZip);

            var keep = item.BackupKeep <= 0 ? 5 : item.BackupKeep;
            Report("正在整理云端历史备份…");
            var cleanupFailed = false;
            try
            {
                var list = await CloudBackupProvider.ListAsync(folder);
                foreach (var old in list.Where(e => IsBackupFile(e.Name, gameName))
                    .OrderByDescending(e => e.Modified).Skip(keep))
                {
                    try { await CloudBackupProvider.DeleteAsync(folder, old.Href); }
                    catch { cleanupFailed = true; }
                }
            }
            catch { cleanupFailed = true; }

            var sizeMb = bytes / 1024d / 1024d;
            Report($"已备份到{CloudBackupProvider.DisplayName}：{folder}/{fileName}（{sizeMb:0.#}MB）" +
                (cleanupFailed ? "；旧版本清理未完成，可稍后重试" : ""));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"云端备份: 异常 {ex.GetType().Name}: {ex.Message}");
            Report("云端备份失败：" + ex.Message, true);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 清理失败无害 */ }
        }
    }

    /// <summary>
    /// 恢复（Y 键 / 长按）：锁定最近识别的游戏，选择版本及主菜单恢复/退出恢复重启方式。
    /// 来源跟随磁贴的备份方式，恢复前保留本地快照。
    /// </summary>
    private static async Task SyncAsync(LaunchItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(TrainerService.LastExeName))
            {
                Report("未记录目标游戏，请先运行游戏让启动器识别，再回到游戏主菜单后恢复", true);
                return;
            }
            var exeName = TrainerService.LastExeName;
            var title = TrainerService.LastWindowTitle;
            var exePath = TrainerService.CurrentGameExePath();

            var gameName = Sanitize(TrainerService.CurrentGameName());
            DebugLog.Write($"存档同步: 触发 exe={exeName} 标题={title} 模式={item.BackupMode}");
            await SaveManifest.EnsureLoadedAsync(t => Report(t));

            var location = await Task.Run(() => SavePathResolver.Resolve(exePath, title, exeName));
            DebugLog.Write($"存档同步: 定位 method={location.Method} paths={string.Join(" ; ", location.Paths)}");
            if (location.Paths.Count == 0)
            {
                Report($"未找到 {gameName} 的存档位置", true);
                return;
            }

            if (item.BackupMode == BackupMode.Cloud)
            {
                await CloudSyncAsync(gameName, location, exeName, exePath);
                return;
            }
            await LocalSyncAsync(item, gameName, location, exeName, exePath);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"存档同步: 异常 {ex.GetType().Name}: {ex.Message}");
            Report("存档同步失败：" + ex.Message, true);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>本地同步：列举版本，由用户选择并确认后再覆盖存档。</summary>
    private static async Task LocalSyncAsync(LaunchItem item, string gameName, SaveLocation location, string exeName, string? exePath)
    {
        var dir = Path.Combine(TargetDir(item), gameName);
        Report("正在读取本地备份列表…");
        var versions = await Task.Run(() => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.zip")
                .Where(p => IsBackupFile(Path.GetFileName(p), gameName))
                .Select(p => new FileInfo(p))
                .Select(f => new SaveBackupVersion(f.FullName, f.Name,
                    BackupCreated(f.Name, gameName, f.CreationTime), f.LastWriteTime, f.Length))
                .OrderByDescending(v => v.Created).ToArray()
            : Array.Empty<SaveBackupVersion>());
        if (versions.Length == 0)
        {
            Report($"本地没有 {gameName} 的备份，请先按 A 备份一次", true);
            return;
        }
        var selected = await ChooseAsync(gameName, false, versions);
        if (selected is null) return;

        var version = selected.Version;
        var current = new FileInfo(version.Location);
        if (!current.Exists || current.Length != version.Size || current.LastWriteTime != version.Modified)
            throw new IOException("所选备份已被修改或删除，请重新打开列表");
        await RestoreSelectedAsync(location.Paths, version.Location, exeName, exePath, selected.RestartGame);
    }

    /// <summary>
    /// 云端同步：取云端该游戏目录下最近一份 zip → 下载到本地临时文件 → 解压覆盖回本地存档 → 删临时文件。
    /// 未配置云端 / 云端无备份 / 下载失败都只提示错误，**不**回退读本地（与云端备份对称）。
    /// </summary>
    private static async Task CloudSyncAsync(string gameName, SaveLocation location, string exeName, string? exePath)
    {
        if (!CloudBackupProvider.IsSignedIn)
        {
            Report($"{CloudBackupProvider.DisplayName}未登录：请在存档磁贴编辑页打开「云端备份设置」扫码登录", true);
            return;
        }

        var root = Sanitize(CloudBackupProvider.RootFolder);
        var folder = $"{root}/{gameName}";

        var tmpDir = Path.Combine(DefaultBackupsDir, "_tmp");
        string? tmpZip = null;
        try
        {
            Report("正在查询云端备份…");
            var list = await CloudBackupProvider.ListAsync(folder);
            var versions = list
                .Where(e => IsBackupFile(e.Name, gameName))
                .Select(e => new SaveBackupVersion(e.Href, e.Name,
                    BackupCreated(e.Name, gameName, e.Modified.LocalDateTime),
                    e.Modified.LocalDateTime, e.Size))
                .OrderByDescending(v => v.Created).ToArray();
            if (versions.Length == 0)
            {
                Report($"云端没有 {gameName} 的备份，请先按 A 备份一次", true);
                return;
            }

            var selected = await ChooseAsync(gameName, true, versions);
            if (selected is null) return;
            var version = selected.Version;
            var currentList = await CloudBackupProvider.ListAsync(folder);
            if (!currentList.Any(e => e.Href == version.Location && e.Size == version.Size &&
                e.Modified.LocalDateTime == version.Modified))
                throw new IOException("所选云端备份已被修改或删除，请重新打开列表");
            var href = version.Location;
            var fileName = version.FileName;
            tmpZip = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(tmpDir);

            Report("正在从云端下载备份…");
            await CloudBackupProvider.DownloadAsync(href, tmpZip);

            await RestoreSelectedAsync(location.Paths, tmpZip, exeName, exePath, selected.RestartGame);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"云端同步: 异常 {ex.GetType().Name}: {ex.Message}");
            Report("云端同步失败：" + ex.Message, true);
        }
        finally
        {
            try { if (tmpZip is not null && File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 清理失败无害 */ }
        }
    }

    private static string NextPrefix(string source, HashSet<string> used)
    {
        var prefix = Path.GetFileName(source.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(prefix)) prefix = "save";
        var candidate = prefix;
        var suffix = used.Count + 1;
        while (!used.Add(candidate)) candidate = prefix + "_" + suffix++;
        return candidate;
    }

    private static bool IsBackupFile(string name, string gameName)
    {
        var prefix = gameName + "-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        var stamp = name[prefix.Length..^4];
        return DateTime.TryParseExact(stamp, new[] { "yyyyMMdd-HHmmss", "yyyyMMdd-HHmmss-fffffff" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    }

    private static void EnsureGameStopped(string exeName)
    {
        var processes = Process.GetProcessesByName(exeName);
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        if (running) throw new IOException("请先退出游戏再恢复存档，避免覆盖游戏正在写入的进度");
    }

    private static async Task RestoreSelectedAsync(IReadOnlyList<string> sources, string zipPath,
        string exeName, string? exePath, bool restart)
    {
        if (!restart)
        {
            try
            {
                var count = await Task.Run(() => RestoreSafely(sources, zipPath, exeName, allowRunning: true));
                Report($"已恢复 {count} 个文件。请在游戏主菜单重新加载存档；如未生效，再按 Y 选择“退出恢复重启”。");
                return;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"主菜单恢复失败：{ex.Message}");
                if (ConfirmRestartAsync is null || !await ConfirmRestartAsync(
                    $"主菜单恢复失败：{ex.Message}\n\n是否请求游戏正常退出，退出后重新恢复并打开游戏？"))
                {
                    Report("恢复未完成：" + ex.Message, true);
                    return;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new IOException("无法确定游戏启动路径，已取消自动退出。请手动退出游戏、恢复存档并重新启动");

        Report("正在请求游戏正常退出，请处理游戏中的保存/退出确认…");
        await Task.Run(async () =>
        {
            var processes = Process.GetProcessesByName(exeName);
            try
            {
                foreach (var process in processes)
                {
                    if (process.HasExited) continue;
                    if (!string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("检测到同名但路径不同的进程，已取消自动退出，请手动退出目标游戏");
                }
                foreach (var process in processes)
                    if (!process.HasExited && !process.CloseMainWindow())
                        throw new IOException("游戏不接受正常退出请求。请手动退出游戏，再按 Y 选择退出恢复重启");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(timeout.Token)));
                }
                catch (OperationCanceledException)
                {
                    throw new IOException("游戏尚未退出，未执行本次恢复。请处理游戏退出确认后重试（不会强制结束游戏）");
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        });
        EnsureGameStopped(exeName);
        Report("游戏已退出，正在恢复存档…");
        var restored = await Task.Run(() => RestoreSafely(sources, zipPath, exeName));
        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)!
            });
            Report($"已恢复 {restored} 个文件，并已发送游戏启动请求。若平台/反作弊要求启动器，请从原平台打开游戏。");
        }
        catch (Exception ex)
        {
            Report($"已恢复 {restored} 个文件，但自动打开游戏失败：{ex.Message}。请从原平台手动启动。", true);
        }
    }

    private static int RestoreSafely(IReadOnlyList<string> sources, string zipPath, string exeName, bool allowRunning = false)
    {
        if (!allowRunning) EnsureGameStopped(exeName);
        var dir = Path.Combine(DefaultBackupsDir, "_before-restore");
        Directory.CreateDirectory(dir);
        var snapshot = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
        Pack(sources, snapshot);
        Report($"恢复前已保留本地快照：{snapshot}");
        if (!allowRunning) EnsureGameStopped(exeName);
        try { return Restore(sources, zipPath); }
        catch (Exception ex) { throw new IOException($"恢复未完成：{ex.Message}。原存档快照：{snapshot}", ex); }
    }

    /// <summary>把备份 zip 解压覆盖回本地存档：zip 内以源目录名为前缀，反查回 SaveLocation.Paths；返回成功写入的文件数</summary>
    private static int Restore(IReadOnlyList<string> sources, string zipPath)
    {
        // 与 Pack 同规则重建「前缀 → 源目录」映射（重名时同样加序号后缀）
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources)
        {
            map[NextPrefix(src, used)] = src;
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var pending = new List<(ZipArchiveEntry Entry, string Destination)>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var name = entry.FullName.Replace('\\', '/');
            var idx = name.IndexOf('/');
            if (idx <= 0 || !map.TryGetValue(name[..idx], out var srcDir))
                throw new IOException("备份目录与当前游戏存档不匹配，已取消恢复");
            var rel = name[(idx + 1)..];
            if (rel.Split('/').Any(p => p.Length == 0 || p is "." or ".." ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
                throw new IOException("备份包含不安全的文件路径，已取消恢复");
            var root = Path.GetFullPath(srcDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var dst = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dst.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !destinations.Add(dst))
                throw new IOException("备份包含越界或重复文件路径，已取消恢复");
            for (var parent = Path.GetDirectoryName(dst); parent is not null; parent = Path.GetDirectoryName(parent))
                if ((Directory.Exists(parent) || File.Exists(parent)) &&
                    (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("存档路径包含链接目录，已取消恢复");
            if (File.Exists(dst) && (File.GetAttributes(dst) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存档文件为链接，已取消恢复");
            pending.Add((entry, dst));
        }
        if (pending.Count == 0) throw new IOException("备份中没有可恢复的存档文件");

        var staging = Path.Combine(Path.GetTempPath(), "GameLocker-Restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            for (var i = 0; i < pending.Count; i++)
                pending[i].Entry.ExtractToFile(Path.Combine(staging, i.ToString()));
            for (var i = 0; i < pending.Count; i++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pending[i].Destination)!);
                File.Copy(Path.Combine(staging, i.ToString()), pending[i].Destination, overwrite: true);
            }
            return pending.Count;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
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
        var partial = zipPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fileCount = 0;
                foreach (var src in sources)
                {
                    var root = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (Path.GetFullPath(zipPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("备份目录不能位于游戏存档目录内");
                    fileCount += AddDirectory(zip, src, NextPrefix(src, used));
                }
                if (fileCount == 0) throw new IOException("没有可备份的存档文件");
            }
            File.Move(partial, zipPath);
            return new FileInfo(zipPath).Length;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    private static int AddDirectory(ZipArchive zip, string srcDir, string prefix)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            // Unity 运行日志不是存档，游戏运行时通常独占写入。
            if (string.Equals(rel, "Player.log", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rel, "Player-prev.log", StringComparison.OrdinalIgnoreCase))
            {
                DebugLog.Write($"存档备份: 排除 Unity 运行日志 {file}");
                continue;
            }
            using var src = File.OpenRead(file);
            var entry = zip.CreateEntry(prefix + "/" + rel.Replace('\\', '/'), CompressionLevel.Optimal);
            using var dst = entry.Open();
            src.CopyTo(dst);
            count++;
        }
        return count;
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
    private static void Prune(string dir, int keep, string gameName)
    {
        if (keep <= 0) keep = 5;
        try
        {
            var zips = Directory.EnumerateFiles(dir, "*.zip")
                .Where(p => IsBackupFile(Path.GetFileName(p), gameName))
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
