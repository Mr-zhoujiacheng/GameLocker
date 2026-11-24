using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

public enum LaunchOutcome { Launched, Activated, Failed }
public sealed record LaunchResult(LaunchOutcome Outcome, string Message = "")
{
    public bool Success => Outcome != LaunchOutcome.Failed;
}

/// <summary>进程身份、窗口恢复与托盘唤醒；不强显隐藏窗口，不重复启动已运行程序。</summary>
public static class LaunchService
{
    private static readonly string DiagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "launch-diag.log");
    private static readonly object LogLock = new();
    internal static void Diag(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagFile)!);
                if (File.Exists(DiagFile) && new FileInfo(DiagFile).Length > 2_000_000)
                    File.Move(DiagFile, DiagFile + ".old", true);
                File.AppendAllText(DiagFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private sealed record ProcessIdentity(int Pid, long Started, string Path);
    private sealed class LaunchRecord
    {
        public readonly Dictionary<int, ProcessIdentity> Processes = new();
        // 短命引导进程退出后仍需发现它已启动的子进程。
        public readonly Dictionary<int, long> ExitedBefore = new();
    }
    private sealed record TargetState(HashSet<int> Pids, bool Uncertain);
    private static readonly ConcurrentDictionary<string, LaunchRecord> Records = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="allowTrayWake">
    /// 目标只有后台进程、没有可见窗口时，是否允许模拟点击通知区托盘图标来唤醒。
    /// 底栏传 false：先尝试显示被隐藏的窗口，再退回到重开入口，全程不动鼠标。
    /// </param>
    public static async Task<LaunchResult> LaunchOrActivateAsync(LaunchItem item, CancellationToken token,
        bool allowTrayWake = true)
    {
        if (!InFlight.TryAdd(item.Target, 0)) return new(LaunchOutcome.Failed, "该程序正在处理中，请稍候");
        var started = false;
        try
        {
            token.ThrowIfCancellationRequested();

            // 原生/商店应用（shell:AppsFolder\AUMID）：已经有窗口就直接切到前台，别重复开实例
            var appId = AppWindows.AppIdOfTarget(item.Target);
            if (appId.Length > 0)
            {
                var hostedWindow = AppWindows.ForAppId(appId).FirstOrDefault();
                if (hostedWindow.Handle != IntPtr.Zero)
                    return await ActivateResultAsync(hostedWindow.Handle, token);
            }

            var shortcut = Path.GetExtension(item.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            var shellTarget = item.Kind is LaunchKind.Folder or LaunchKind.Url || IsDirectoryTarget(item.Target);
            var path = ResolveExecutable(item.Target);
            if (shellTarget || (path is null && (!IsExecutableTarget(item.Target) || shortcut)))
            {
                // URL、文档、商店快捷方式由 Shell/共享进程接管，标题不能可靠验证是否打开。
                // 每次点击都交给 Shell，避免只激活同网站旧标签而没有打开本次链接。
                token.ThrowIfCancellationRequested();
                Start(item, null);
                return new(LaunchOutcome.Launched);
            }

            var state = CollectTargetProcesses(item, path);
            token.ThrowIfCancellationRequested();
            var hwnd = FindProcessWindow(state.Pids);
            Diag($"打开 {item.Name}: pids=[{string.Join(",", state.Pids)}] uncertain={state.Uncertain} hwnd=0x{hwnd.ToInt64():X}");
            if (hwnd != 0) return await ActivateResultAsync(hwnd, token);
            if (state.Pids.Count > 0)
            {
                // 按 MiniDock 的 RevealHiddenWindow 做法：应用关到托盘只是把主窗口 Hide 了，
                // 直接把窗口 Show 出来再拉前台，不动鼠标、不点托盘、不重开实例。
                var hidden = await RevealHiddenWindowAsync(item, state.Pids, token);
                if (hidden != 0) return await ActivateResultAsync(hidden, token, restoredFromHidden: true);
                if (allowTrayWake) return await WakeTrayAsync(item, path, token);
                Diag($"{item.Name}: 只有后台进程、没有可显示窗口，跳过托盘唤醒，直接重开入口");
                Start(item, path);
                return new(LaunchOutcome.Launched);
            }
            if (state.Uncertain || path is null)
                return new(LaunchOutcome.Failed, "无法读取程序路径，请检查入口或权限");

            token.ThrowIfCancellationRequested();
            Start(item, path);
            started = true;
            CollectTargetProcesses(item, path); // 尽早记录引导进程及子进程。
            // 等待启动页切换到稳定窗口，只观察已验证进程，不捕获无关前台窗口。
            IntPtr pendingWindow = 0;
            var stableSince = Stopwatch.StartNew();
            for (var i = 0; i < 65; i++)
            {
                await Task.Delay(200, token);
                state = CollectTargetProcesses(item, path);
                hwnd = FindProcessWindow(state.Pids);
                if (hwnd != pendingWindow)
                {
                    pendingWindow = hwnd;
                    stableSince.Restart();
                }
                if (hwnd == 0 || i < 9 || stableSince.ElapsedMilliseconds < 1000) continue;
                var result = await ActivateResultAsync(hwnd, token);
                // 启动请求已成功，窗口暂时忙或拒绝抢焦点不等于启动失败。
                if (!result.Success) Diag($"已启动 {item.Name}，窗口切换未确认: {result.Message}");
                return new(LaunchOutcome.Launched);
            }
            Diag($"已启动 {item.Name}，等待窗口结束（可能仍在加载或驻留后台）");
            return new(LaunchOutcome.Launched);
        }
        catch (OperationCanceledException)
        {
            Diag($"打开 {item.Name}: 超时/取消");
            return started ? new(LaunchOutcome.Launched)
                : new(LaunchOutcome.Failed, "操作已取消或超时");
        }
        catch (Exception ex)
        {
            Diag($"打开 {item.Name}: {ex.GetType().Name} {ex.Message}");
            return new(LaunchOutcome.Failed, $"打开失败：{ex.Message}");
        }
        finally { InFlight.TryRemove(item.Target, out _); }
    }

    /// <summary>
    /// 点击切换：目标此刻就占着前台就收起它，否则走原来的激活/启动。
    /// 对应 MiniDock 的 clickToggle —— 同一个图标连点两下等于开关。
    /// </summary>
    public static async Task<LaunchResult> ToggleAsync(LaunchItem item, CancellationToken token,
        bool allowTrayWake = true)
    {
        try
        {
            if (item.Kind == LaunchKind.Tray)
            {
                var entry = TrayAppService.FindEntry(item.Target);
                if (entry is null) return new(LaunchOutcome.Failed, "没有登记这个托盘应用");

                var message = TrayAppService.Toggle(entry);
                return message.Length == 0
                    ? new(LaunchOutcome.Activated)
                    : new(LaunchOutcome.Failed, message);
            }

            // 原生/商店应用：前台判断和收起都按包族名找到的那扇窗来做
            var appId = AppWindows.AppIdOfTarget(item.Target);
            if (appId.Length > 0)
            {
                var hosted = AppWindows.ForAppId(appId).FirstOrDefault();
                if (hosted.Handle != IntPtr.Zero)
                {
                    var foregroundHosted = GetForegroundWindow();
                    if (foregroundHosted == hosted.Handle && !IsIconic(foregroundHosted) && IsWindowVisible(foregroundHosted))
                    {
                        Minimize(item);
                        Diag($"{item.Name}: 已在前台，收起");
                        return new(LaunchOutcome.Activated);
                    }
                    return await ActivateResultAsync(hosted.Handle, token);
                }
            }

            // 程序自己（或它的子进程）此刻占着前台：收起，不再重新激活
            var path = ResolveExecutable(item.Target);
            var state = CollectTargetProcesses(item, path);
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && state.Pids.Count > 0)
            {
                GetWindowThreadProcessId(foreground, out var foregroundPid);
                // 已在前台、而且真的看得见，才收起。
                // 应用收进托盘后，那扇隐藏窗口往往还是"前台窗口"（Windows 不会因为 Hide 就换前台），
                // 只看 GetForegroundWindow 会把这种状态误判成"在前台"，于是点一下只是又最小化了一次，
                // 用户看到的就是"点了没反应"。
                if (state.Pids.Contains((int)foregroundPid) && !IsIconic(foreground) && IsWindowVisible(foreground))
                {
                    Minimize(item);
                    Diag($"{item.Name}: 已在前台，收起");
                    return new(LaunchOutcome.Activated);
                }
            }
        }
        catch (Exception ex) { Diag($"切换 {item.Name} 失败: {ex.Message}"); }

        return await LaunchOrActivateAsync(item, token, allowTrayWake);
    }

    /// <summary>
    /// 找出该进程组里"被隐藏、但窗口仍然存在"的主窗口（关到托盘 / 后台驻留的常见形态），
    /// 直接 Show 出来并返回句柄；没有可用的隐藏窗口时返回 0。
    /// 判定条件与 MiniDock 的 HiddenProc 一致：排除工具窗、有 owner 的浮层、外壳窗口，
    /// 要求有标题且尺寸不小于 160×100，多个候选取面积最大的那个。
    /// </summary>
    private static async Task<IntPtr> RevealHiddenWindowAsync(LaunchItem item, HashSet<int> pids,
        CancellationToken token)
    {
        var best = IntPtr.Zero;
        double bestArea = -1;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid) || IsWindowVisible(hwnd) || IsCloaked(hwnd) || IsShellWindow(hwnd))
                return true;
            var exStyle = GetWindowLongPtrW(hwnd, -20).ToInt64();
            if ((exStyle & 0x80) != 0) return true;                  // WS_EX_TOOLWINDOW：托盘提示、输入法之类
            if (GetWindow(hwnd, 4) != IntPtr.Zero) return true;      // 有 owner：对话框 / 浮层，不是主窗
            if (WindowText(hwnd).Length == 0) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 160 || height < 100) return true;
            var area = (double)width * height;
            if (area <= bestArea) return true;
            bestArea = area;
            best = hwnd;
            return true;
        }, 0);
        if (best == IntPtr.Zero) return IntPtr.Zero;

        Diag($"显示隐藏窗口 {item.Name}: hwnd=0x{best.ToInt64():X}");
        ShowWindowAsync(best, IsIconic(best) ? 9 : 1);   // SW_RESTORE / SW_SHOWNORMAL
        // ShowWindowAsync 是投递消息：等窗口真正可见，否则紧接着的激活会以为它不存在。
        for (var i = 0; i < 10 && !IsWindowVisible(best); i++) await Task.Delay(20, token);
        return IsWindowVisible(best) ? best : IntPtr.Zero;
    }

    private static async Task<LaunchResult> WakeTrayAsync(LaunchItem item, string? path, CancellationToken token)
    {
        var names = new[] { item.Name, path is null ? null : Path.GetFileNameWithoutExtension(path) };
        // 先尝试一次托盘默认动作；无效时让原启动入口处理唤醒。
        for (var attempt = 0; attempt < 1; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var state = CollectTargetProcesses(item, path);
            var hwnd = FindProcessWindow(state.Pids);
            if (hwnd != 0) return await ActivateResultAsync(hwnd, token);
            var clicked = TrayIconService.Click(token, attempt == 1, path, state.Pids, names);
            Diag($"托盘 {item.Name}: attempt={attempt + 1} clicked={clicked}");
            if (!clicked) continue;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(150, token);
                state = CollectTargetProcesses(item, path);
                hwnd = FindProcessWindow(state.Pids);
                if (hwnd != 0) return await ActivateResultAsync(hwnd, token);
            }
        }
        // 浏览器后台模式、单实例应用和托盘应用可能没有可操作的托盘图标。
        // 按用户本次点击重新交给原入口一次，由应用自身处理激活/新窗口请求。
        token.ThrowIfCancellationRequested();
        Diag($"重新打开入口 {item.Name}: 后台进程存在但无窗口");
        Start(item, path);
        return new(LaunchOutcome.Launched);
    }

    private static void Start(LaunchItem item, string? path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = item.Target, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Maximized,
            WorkingDirectory = path is not null ? Path.GetDirectoryName(path) ?? "" : ""
        });
        var record = Records.GetOrAdd(item.Target, _ => new LaunchRecord());
        if (process is not null && ReadIdentity(process.Id) is { } identity)
        {
            lock (record)
            {
                record.Processes[identity.Pid] = identity;
                record.ExitedBefore.Remove(identity.Pid);
            }
        }
        // CPU 调度登记失败不能把已成功的 Shell 启动反报为失败（引导进程可能已经退出）。
        if (path is not null && process is not null)
        {
            try { GameCpuService.Register(process.Id); }
            catch (Exception ex) { Diag($"调度登记跳过 {item.Name}: {ex.Message}"); }
        }
        Diag($"启动 {item.Name}: pid={process?.Id} executable={path is not null}");
    }

    private static TargetState CollectTargetProcesses(LaunchItem item, string? path)
    {
        var record = Records.GetOrAdd(item.Target, _ => new LaunchRecord());
        var pids = new HashSet<int>();
        var uncertain = false;
        lock (record)
        {
            foreach (var known in record.Processes.Values.ToArray())
            {
                var current = ReadIdentity(known.Pid);
                if (current == known) pids.Add(known.Pid);
                else if (current is not null)
                {
                    // PID 已复用：不能再用这个父 PID 发现后代。
                    record.Processes.Remove(known.Pid);
                    record.ExitedBefore.Remove(known.Pid);
                }
                else if (!record.ExitedBefore.ContainsKey(known.Pid))
                    record.ExitedBefore[known.Pid] = DateTime.UtcNow.ToFileTimeUtc();
            }
            var leaf = path is null ? Path.GetFileNameWithoutExtension(item.Target) : Path.GetFileNameWithoutExtension(path);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || pids.Contains(process.Id)) continue;
                        if (!process.ProcessName.Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                        var identity = ReadIdentity(process.Id);
                        if (identity is null) { uncertain = true; continue; }
                        if (path is null) { uncertain = true; continue; }
                        if (!identity.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                        pids.Add(identity.Pid);
                        record.Processes[identity.Pid] = identity;
                    }
                    catch { /* 无关进程可能已退出；只有同名候选身份不可读才标为不确定。 */ }
                }
            }
            // 父 PID + 父启动时间校验，避免 PID 复用认错后代进程。
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot != new IntPtr(-1))
            {
                try
                {
                    var entries = new List<(int Pid, int Parent)>();
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (Process32FirstW(snapshot, ref entry))
                        do { entries.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID)); }
                        while (Process32NextW(snapshot, ref entry));
                    bool added;
                    do
                    {
                        added = false;
                        foreach (var e in entries)
                        {
                            if (pids.Contains(e.Pid) || e.Pid == Environment.ProcessId
                                || !record.Processes.TryGetValue(e.Parent, out var parent)) continue;
                            var liveParent = ReadIdentity(parent.Pid);
                            if (liveParent is not null && liveParent != parent) continue;
                            var child = ReadIdentity(e.Pid);
                            if (child is null || child.Started < parent.Started) continue;
                            if (record.ExitedBefore.TryGetValue(parent.Pid, out var exitedBefore)
                                && child.Started > exitedBefore) continue;
                            pids.Add(child.Pid);
                            record.Processes[child.Pid] = child;
                            added = true;
                        }
                    } while (added);
                }
                finally { CloseHandle(snapshot); }
            }
            return new(pids, uncertain);
        }
    }

    /// <summary>
    /// 该进程是否是本启动器启动过的目标程序（含由其派生的子进程）。
    /// 用进程号 + 启动时间校验，避免 PID 复用后把无关进程当成游戏。
    /// </summary>
    public static bool IsTrackedProcess(int pid)
    {
        foreach (var record in Records.Values)
        {
            lock (record)
            {
                if (!record.Processes.TryGetValue(pid, out var known)) continue;
                if (ReadIdentity(known.Pid) == known) return true;
            }
        }
        return false;
    }

    private static ProcessIdentity? ReadIdentity(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var text = new System.Text.StringBuilder(32768);
            var size = text.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, text, ref size)
                || !GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            return new(pid, created, Path.GetFullPath(text.ToString()));
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    private static string? ResolveExecutable(string target)
    {
        try
        {
            if (Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                target = DropImportService.ResolveShortcutTarget(target) ?? "";
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (Path.IsPathFullyQualified(target))
                return File.Exists(target) ? Path.GetFullPath(target) : null;
            // 系统磁贴可能只保存 notepad.exe / calc.exe，Shell 能打开，身份解析也应支持。
            var directories = new[] { AppContext.BaseDirectory, Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (var directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory.Trim().Trim('"'), target);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }
        catch { return null; }
    }
    private static bool IsExecutableTarget(string target) =>
        Path.GetExtension(target).ToLowerInvariant() is ".exe" or ".lnk";

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    private static bool IsUsableWindow(IntPtr hwnd) => hwnd != 0 && IsWindow(hwnd)
        && IsWindowVisible(hwnd) && !IsCloaked(hwnd) && !IsShellWindow(hwnd);

    private static IntPtr FindProcessWindow(HashSet<int> pids)
    {
        // Z 序快照：越靠上（序号越小）越可能是用户最近用过的那扇窗。
        // 多窗口时按它选，才符合"点底栏图标把刚才那个窗口拿回来"的直觉。
        var zOrder = AppWindows.TopLevelZOrder();
        IntPtr best = 0;
        double bestScore = double.MinValue;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid) || !IsUsableWindow(hwnd)) return true;
            var style = GetWindowLongPtrW(hwnd, -20).ToInt64();
            if ((style & 0x80) != 0) return true;
            var area = WindowArea(hwnd);
            if (area <= 0 && !IsIconic(hwnd)) return true;
            // 尺寸/主窗框架只用来"同一层里"挑，优先顺序是：没最小化 > Z 序靠上 > 主窗框架
            var taskbarWindow = (style & 0x40000) != 0;
            var windowStyle = GetWindowLongPtrW(hwnd, -16).ToInt64();
            var mainFrame = (windowStyle & 0x00C00000) == 0x00C00000
                || (windowStyle & 0x00050000) != 0;
            var z = zOrder.TryGetValue(hwnd, out var index) ? index : 100_000;
            var score = (IsIconic(hwnd) ? 0 : 4_000_000_000_000d)
                + (1_000_000_000_000d - z * 1_000_000d)
                + (taskbarWindow ? 300_000d : 0)
                + (mainFrame ? 200_000d : 0)
                + (GetWindow(hwnd, 4) == 0 ? 100_000d : 0)
                + Math.Min(area, 99_000d);
            Diag($"    [候选窗] hwnd=0x{hwnd.ToInt64():X} z={z} style=0x{windowStyle:X} ex=0x{style:X} area={area} score={score}");
            if (score > bestScore) { bestScore = score; best = hwnd; }
            return true;
        }, 0);
        if (best == 0)
        {
            // 原生（UWP）应用：进程自己不开顶层窗口，窗口在 ApplicationFrameHost 下
            best = FindHostedFrameWindow(pids);
            if (best == 0) return 0;
        }
        var popup = GetLastActivePopup(best);
        // 只有主窗口被模态对话框禁用时才跟随弹窗，避免 Electron 浮窗抢走主界面。
        if (popup != best && (GetWindowLongPtrW(best, -16).ToInt64() & 0x08000000) != 0
            && IsUsableWindow(popup))
        {
            GetWindowThreadProcessId(popup, out var pid);
            if (pids.Contains((int)pid)) return popup;
        }
        return best;
    }

    /// <summary>
    /// 原生（UWP/商店）应用：应用进程自己不开顶层窗口，窗口挂在 ApplicationFrameHost 下。
    /// 按 pids 找到它的框架窗口，否则设置/计算器这类应用永远"没有窗口"可激活。
    /// </summary>
    private static IntPtr FindHostedFrameWindow(HashSet<int> pids)
    {
        IntPtr found = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsUsableWindow(hwnd)) return true;
            var cls = new System.Text.StringBuilder(64);
            GetClassNameW(hwnd, cls, 64);
            if (!cls.ToString().Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)) return true;

            var core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core == IntPtr.Zero) return true;
            GetWindowThreadProcessId(core, out var corePid);
            if (!pids.Contains((int)corePid)) return true;

            found = hwnd;
            return false;
        }, 0);
        return found;
    }

    private static async Task<LaunchResult> ActivateResultAsync(IntPtr hwnd, CancellationToken token,
        bool restoredFromHidden = false)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!IsUsableWindow(hwnd)) break;
            if (IsHungAppWindow(hwnd))
            {
                // 加载期间可能暂时被 Windows 标记为忙，短暂等待，避免第一次采样就误报。
                await Task.Delay(350, token);
                continue;
            }
            // 最大化主窗口，模态对话框仍保留焦点与原尺寸；不强显托盘隐藏窗。
            var main = GetAncestor(hwnd, 3);
            if (!IsUsableWindow(main)) main = hwnd;
            var canMaximize = (GetWindowLongPtrW(main, -16).ToInt64() & 0x10000) != 0;
            if (restoredFromHidden)
            {
                // 刚从托盘/隐藏状态"捞"出来的窗口：只做最小还原，不再替它最大化。
                // 这类窗口（QQ/Electron 一类）被外部强行 Show + 最大化后，自身状态机常常停在
                // 隐藏时的布局上，表现为窗口出来了但界面卡住不刷新。
                if (IsIconic(main)) ShowWindowAsync(main, 9);
                // 关键一步：ShowWindow 只让 DWM 把最后一帧贴出来，Chromium/Electron 的渲染进程
                // 仍停在"隐藏"状态——窗口看着正常，鼠标却点不动。补一次激活消息 + 1px 尺寸变化，
                // 强制它重新走一遍"窗口被激活/尺寸变化"的流程，页面才真正恢复（实测 QQ 有效）。
            }
            else if (canMaximize)
            {
                if (!IsZoomed(main) || IsIconic(main)) ShowWindowAsync(main, 3);
            }
            else if (IsIconic(main)) ShowWindowAsync(main, 9);
            if (hwnd != main && IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);

            // 真正置前：内部会处理 Windows 前台锁、必要时把它抬到普通窗口最上层
            var brought = AppWindows.BringToFront(hwnd);
            await Task.Delay(140, token);
            var fg = GetForegroundWindow();
            var success = fg == hwnd
                          || (IsUsableWindow(fg) && GetAncestor(fg, 3) == GetAncestor(hwnd, 3))
                          // 抢不到焦点至少要让窗口露出来（否则用户就是"点了没反应"）
                          || AppWindows.IsAboveNormalWindows(hwnd);
            Diag($"激活 hwnd=0x{hwnd.ToInt64():X} attempt={attempt + 1} brought={brought} foreground=0x{fg.ToInt64():X} success={success} canMaximize={canMaximize} maximized={IsZoomed(main)} restoredFromHidden={restoredFromHidden}");
            if (success)
            {
                if (restoredFromHidden) _ = VerifyRevealedAsync(hwnd);
                return new(LaunchOutcome.Activated);
            }
        }
        return new(LaunchOutcome.Failed, "窗口存在，但未能完成前台切换或最大化；请检查程序权限或手动点击任务栏");
    }

    /// <summary>
    /// "从隐藏窗口恢复"之后的自检：一秒后再看这扇窗是不是卡住了（不刷新、不响应消息）。
    /// 只写诊断日志，不改变行为——用来定位"点第二次后应用界面卡死"这类只在个别应用上出现的问题。
    /// </summary>
    private static async Task VerifyRevealedAsync(IntPtr hwnd)
    {
        try
        {
            await Task.Delay(1000);
            if (!IsWindow(hwnd)) { Diag("恢复自检：窗口已关闭"); return; }
            IntPtr result;
            var sw = Stopwatch.StartNew();
            var responds = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 800, out result) != IntPtr.Zero;
            sw.Stop();
            Diag($"恢复自检 hwnd=0x{hwnd.ToInt64():X} hung={IsHungAppWindow(hwnd)} responds={responds}({sw.ElapsedMilliseconds}ms) visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} zoomed={IsZoomed(hwnd)}");
        }
        catch { }
    }

    public static int Minimize(LaunchItem item)
    {
        // 原生/商店应用：窗口挂在 ApplicationFrameHost 下，按包族名找到再收起
        var appId = AppWindows.AppIdOfTarget(item.Target);
        if (appId.Length > 0)
        {
            var count = 0;
            foreach (var window in AppWindows.ForAppId(appId))
                if (IsUsableWindow(window.Handle) && (IsIconic(window.Handle) || ShowWindowAsync(window.Handle, 6))) count++;
            return count;
        }

        var shell = item.Kind is LaunchKind.Url or LaunchKind.Folder || IsDirectoryTarget(item.Target);
        var windows = shell ? new[] { FindShellWindow(item) }
            : ProcessWindows(CollectTargetProcesses(item, ResolveExecutable(item.Target)).Pids);
        var minimized = 0;
        foreach (var hwnd in windows)
            if (IsUsableWindow(hwnd) && (IsIconic(hwnd) || ShowWindowAsync(hwnd, 6))) minimized++;
        return minimized;
    }
    private static IntPtr[] ProcessWindows(HashSet<int> pids)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && IsUsableWindow(h)) list.Add(h);
            return true;
        }, 0);

        // UWP 应用的窗口在宿主进程里，也要一起收起
        var hosted = FindHostedFrameWindow(pids);
        if (hosted != IntPtr.Zero && !list.Contains(hosted)) list.Add(hosted);
        return list.ToArray();
    }

    private static IntPtr FindShellWindow(LaunchItem item)
    {
        // 文件夹/URL 由共享进程承载，保持标题匹配路径，但不写入程序身份记录。
        string?[] keys = item.Kind == LaunchKind.Url ? [HostOf(item.Target)]
            : [NormalizeLeaf(item.Target), GetDisplayName(item.Target)];
        IntPtr best = 0;
        double area = 0;
        EnumWindows((h, _) =>
        {
            if (!IsUsableWindow(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            var identity = ReadIdentity((int)pid);
            var folder = item.Kind != LaunchKind.Url;
            if (folder && (identity is null || !Path.GetFileName(identity.Path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))) return true;
            var title = WindowText(h);
            if (!keys.Any(k => !string.IsNullOrWhiteSpace(k) && title.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
            var current = WindowArea(h);
            if (current > area) { best = h; area = current; }
            return true;
        }, 0);
        return best;
    }
    private static double WindowArea(IntPtr h) => GetWindowRect(h, out var r)
        ? Math.Max(0, r.Right - r.Left) * (double)Math.Max(0, r.Bottom - r.Top) : 0;
    private static bool IsShellWindow(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
    private static string WindowText(IntPtr h)
    {
        if (IsHungAppWindow(h)) return "";
        var sb = new System.Text.StringBuilder(1024);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
    private static bool IsDirectoryTarget(string target) => Directory.Exists(target);
    private static string? HostOf(string url)
    {
        try { var host = new Uri(url).Host; return host.StartsWith("www.") ? host[4..] : host; }
        catch { return null; }
    }
    private static string? NormalizeLeaf(string path)
    {
        try { var full = Path.GetFullPath(path).TrimEnd('\\', '/'); return Path.GetFileName(full) is { Length: > 0 } leaf ? leaf : full; }
        catch { return null; }
    }
    private static string? GetDisplayName(string path) => NormalizeLeaf(path)?.ToLowerInvariant() switch
    {
        "downloads" => "下载", "documents" => "文档", "pictures" => "图片", "music" => "音乐",
        "videos" => "视频", "desktop" => "桌面", "favorites" => "收藏夹", "links" => "快速启动",
        "saved games" => "已保存的游戏", "searches" => "搜索", "3d objects" => "三维对象", "contacts" => "联系人", _ => null
    };

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr param);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder text, ref int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_ACTIVATEAPP = 0x001C;
    private const int WA_ACTIVE = 1;
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowTitle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder text, int size);
}
