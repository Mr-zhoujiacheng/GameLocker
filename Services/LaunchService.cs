using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLocker.Models;

namespace GameLocker.Services;

/// <summary>打开结果：新启动 / 已运行并切换到前台</summary>
public enum LaunchOutcome
{
    Launched,
    Activated
}

/// <summary>
/// 打开/收起快捷项。
/// 打开：先检查是否已在运行 —— 未运行则启动；已运行则把它的窗口最大化并置前台。
/// 收起（Y）：只把窗口最小化，不结束进程（下次按 A 直接回到原窗口）。
/// 兼容程序/计算器(UWP)/文件夹(explorer)/网站(浏览器)。
/// </summary>
public static class LaunchService
{
    private static readonly string DiagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "launch-diag.log");

    /// <summary>诊断日志（查找窗口/托盘点击等模块共用一条日志，方便定位唤醒失败原因）</summary>
    internal static void Diag(string msg)
    {
        try
        {
            File.AppendAllText(DiagFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响功能 */ }
    }

    /// <summary>打开磁贴：已运行则最大化置前台，否则启动</summary>
    public static LaunchOutcome LaunchOrActivate(LaunchItem item)
    {
        var hwnd = FindExistingWindow(item);
        Diag($"▶ {item.Name} kind={item.Kind} hwnd=0x{hwnd.ToInt64():X} vis={(hwnd != 0 && IsWindowVisible(hwnd))} " +
             $"iconic={(hwnd != 0 && IsIconic(hwnd))} area={(hwnd != 0 ? WindowArea(hwnd) : 0)} title=\"{WindowText(hwnd)}\"");
        if (hwnd == 0)
        {
            Launch(item);
            return LaunchOutcome.Launched;
        }

        if (IsWindowVisible(hwnd))
        {
            ActivateWindow(hwnd);
            // 启动器随后可能自动收起，焦点会被系统转移一次：稍后再抢回前台
            _ = ReassertForegroundAsync(hwnd);
            return LaunchOutcome.Activated;
        }

        // 只有隐藏窗口（缩在托盘）：让应用自己恢复窗口 —— 模拟点击它的托盘图标，
        // 点不到就多重试几次，不做外部强显（强显会让 Electron 白屏）。
        // 放到线程池执行：UIA/等待都要几百毫秒到几秒，不能卡住 UI 线程
        _ = Task.Run(() => ActivateTrayAppAsync(item, hwnd));
        return LaunchOutcome.Activated;
    }

    /// <summary>托盘应用唤醒：点托盘图标 → 等应用自己显示窗口 → 最大化置前台。
    /// 点不到图标时只多重试几次；既不外部强显（Electron 白屏），也不二次启动（会多开窗口）。</summary>
    private static async Task ActivateTrayAppAsync(LaunchItem item, IntPtr hiddenHwnd)
    {
        try
        {
            // 启动器收起动画约 260ms：等它让开屏幕（置顶窗口会盖住任务栏/溢出面板）
            await Task.Delay(420);

            var pids = CollectTargetPids(item, consumeRecord: false);
            if (pids.Count == 0)
            {
                GetWindowThreadProcessId(hiddenHwnd, out uint pid);
                if (pid != 0) pids.Add((int)pid);
            }

            // 托盘提示文字常和磁贴名对不上（如磁贴"TraeCode CN"、托盘"TRAE CN"），
            // 用磁贴名 / exe 名 / 隐藏窗口标题多试几次
            var candidates = new[] { item.Name, ExeLeafOf(item.Target), WindowText(hiddenHwnd) };
            Diag($"  托盘 pids=[{string.Join(",", pids)}] 候选=[{string.Join("|", candidates.Select(c => c ?? ""))}]");
            // 托盘图标偶尔点不中（图标在溢出面板里换位、面板还没展开）：最多点三次；
            // 每轮开始先看看窗口是不是已经出来了（上一次点击可能已经生效，只是显示得慢）
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(400);
                    pids = CollectTargetPids(item, consumeRecord: false);
                    var early = PickShownWindow(pids, hiddenHwnd);
                    if (early != 0)
                    {
                        ActivateWindow(early);
                        await ReassertForegroundAsync(early);
                        Diag($"  重试前已有窗口 0x{early.ToInt64():X} fg=0x{GetForegroundWindow().ToInt64():X}");
                        return;
                    }
                }
                var clicked = TrayIconService.Click(candidates);
                Diag($"  点托盘图标（第{attempt + 1}次） => {clicked}");
                if (!clicked) continue;
                for (var i = 0; i < 30; i++)   // 最多约 4.5 秒等应用显示窗口
                {
                    await Task.Delay(150);
                    var shown = PickShownWindow(pids, hiddenHwnd);
                    if (shown == 0) continue;
                    Diag($"  图标点击后出现可见窗口 0x{shown.ToInt64():X} \"{WindowText(shown)}\" 第{i * 150}ms");
                    // 应用刚显示时还在初始化（Electron 尤其明显）：等它稳定再接管
                    await Task.Delay(500);
                    if (IsWindow(shown) && IsWindowVisible(shown))
                    {
                        var settled = PickShownWindow(pids, hiddenHwnd);
                        if (settled != 0) shown = settled;
                    }
                    ActivateWindow(shown);
                    await ReassertForegroundAsync(shown);
                    Diag($"  托盘路径完成 fg=0x{GetForegroundWindow().ToInt64():X}");
                    return;
                }
            }

            // 托盘图标没点成（图标被折叠/换位/应用没监听）：一律不再二次启动程序。
            // 二次启动会额外打出一个窗口（QQ 等应用的表现就是"多开了一个页面"），
            // 比"没打开"更糟；这里只再补一段观察时间，等应用自己把窗口显示出来。
            for (var i = 0; i < 20; i++)   // 最多再等约 3 秒
            {
                await Task.Delay(150);
                var shown = PickShownWindow(pids, hiddenHwnd);
                if (shown == 0) continue;
                ActivateWindow(shown);
                await ReassertForegroundAsync(shown);
                Diag($"  延迟出现窗口 0x{shown.ToInt64():X} fg=0x{GetForegroundWindow().ToInt64():X}");
                return;
            }

            // 不外部强显隐藏窗口：Electron 类应用（QQ/微信等）被外部 ShowWindow 之后
            // 画面不刷新、点击无响应（白屏假死），比"没打开"更糟。宁可放弃并提示。
            Diag("  唤醒失败：已跳过强显与二次启动（强显让 QQ 白屏、二次启动会多开窗口），请手动点开一次");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"托盘唤醒失败 {item.Name}: {ex.Message}");
        }
    }

    /// <summary>托盘图标点完后等应用自己把窗口显示出来：优先认原来那个隐藏窗口，
    /// 其次接受该进程任意够大的可见窗口。不能按"原隐藏窗口面积的比例"筛——
    /// 原窗口可能被我们最大化过，而应用恢复时常常给回它自己保存的小面板尺寸。</summary>
    private static IntPtr PickShownWindow(HashSet<int> pids, IntPtr expected)
    {
        if (expected != 0 && IsWindow(expected) && IsWindowVisible(expected)) return expected;
        return FindVisibleProcessWindow(pids);
    }

    private static void Launch(LaunchItem item)
    {
        var psi = new ProcessStartInfo
        {
            FileName = item.Target,
            UseShellExecute = true
        };
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动失败 {item.Target}: {ex.Message}");
        }

        // 记录本次启动，稍后异步捕获到前台的窗口句柄（用于后续最大化/最小化）
        if (proc is not null)
        {
            _records[item.Target] = new LaunchRecord { Pid = proc.Id };
            _ = CaptureForegroundAsync(item.Target, proc.Id);
            // 首次启动同样要"最大化 + 置前台"：等新程序把主窗口显示出来后接管。
            // 文件夹/网站交由系统（资源管理器/浏览器）自行决定窗口大小，不强行最大化
            if (item.Kind is not (LaunchKind.Folder or LaunchKind.Url) && !IsDirectoryTarget(item.Target))
                _ = MaximizeLaunchedAsync(item, proc.Id);
        }
    }

    /// <summary>
    /// 首次启动后的窗口接管：新程序从启动到显示窗口有时间差（尤其启动器型应用），
    /// 这里每 200ms 观察一次目标进程的最大可见窗口，出现即最大化并置前台；
    /// 窗口稳定 1.5 秒（或超过 15 秒）后收尾，再补一次前台争夺。
    /// </summary>
    private static async Task MaximizeLaunchedAsync(LaunchItem item, int startedPid)
    {
        try
        {
            HashSet<int> pids = [];
            IntPtr last = 0;
            long stableSince = 0;
            for (var i = 0; i < 75; i++)   // 最多约 15 秒
            {
                await Task.Delay(200);
                if (pids.Count == 0 || i % 5 == 0) pids = CollectTargetPids(item, consumeRecord: false);
                if (pids.Count == 0 && startedPid != 0) pids.Add(startedPid);

                // 前 2 秒先只认"真正的应用窗口"，避免把启动画面/悬浮条当成主窗
                var hwnd = PickLaunchWindow(pids, allowSmall: i >= 10);
                if (hwnd == 0) continue;
                if (hwnd != last)
                {
                    last = hwnd;
                    stableSince = Environment.TickCount64;
                    ActivateWindow(hwnd);
                    Diag($"  首次启动激活 0x{hwnd.ToInt64():X} \"{WindowText(hwnd)}\" 第{i * 200}ms");
                }
                else if (Environment.TickCount64 - stableSince > 1500)
                {
                    break;   // 窗口已稳定，后面可能弹的是文件对话框，不再接管
                }
            }

            if (last != 0 && IsWindow(last))
            {
                ActivateWindow(last);
                await ReassertForegroundAsync(last);
                Diag($"  首次启动完成 0x{last.ToInt64():X} fg=0x{GetForegroundWindow().ToInt64():X}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"首次启动接管失败 {item.Name}: {ex.Message}");
        }
    }

    /// <summary>在目标进程的可见顶层窗口里挑面积最大的（allowSmall=false 时忽略悬浮条/启动小窗）</summary>
    private static IntPtr PickLaunchWindow(HashSet<int> pids, bool allowSmall)
    {
        IntPtr best = 0, fallback = 0;
        double bestArea = 0, fallbackArea = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            if (IsShellWindow(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains((int)pid)) continue;
            var area = WindowArea(hwnd);
            if (area <= 0) continue;
            if (area < MinAppWindowArea)
            {
                if (area > fallbackArea) { fallbackArea = area; fallback = hwnd; }
                continue;
            }
            if (area > bestArea) { bestArea = area; best = hwnd; }
        }
        return best != 0 || !allowSmall ? best : fallback;
    }

    /// <summary>最小化该磁贴对应的窗口（不结束进程）。返回处理的窗口数（0=未在运行）。</summary>
    public static int Minimize(LaunchItem item)
    {
        // 文件夹磁贴由系统共享 explorer.exe 承载：按窗口标题匹配对应文件夹窗口
        if (item.Kind == LaunchKind.Folder || IsDirectoryTarget(item.Target))
            return MinimizeWindowsByTitle(FolderTitleKeys(item.Target));

        // 网站磁贴：有记录窗口时优先用它，否则按域名匹配浏览器窗口
        if (item.Kind == LaunchKind.Url)
        {
            if (_records.TryGetValue(item.Target, out var rec) && IsUsableWindow(rec!.Hwnd)
                && MinimizeWindow(rec.Hwnd))
                return 1;
            return MinimizeWindowsByTitle([HostOf(item.Target)]);
        }

        var pids = CollectTargetPids(item, consumeRecord: false);
        if (pids.Count == 0) return 0;

        var count = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pids.Contains((int)pid) && MinimizeWindow(hwnd)) count++;
        }
        return count;
    }

    /// <summary>找出该磁贴已在运行的窗口句柄（0=没在运行）。程序按进程取主窗口，
    /// 文件夹/网站按窗口标题匹配。</summary>
    private static IntPtr FindExistingWindow(LaunchItem item)
    {
        if (item.Kind == LaunchKind.Folder || IsDirectoryTarget(item.Target))
            return FindWindowByTitle(FolderTitleKeys(item.Target));

        if (item.Kind == LaunchKind.Url)
        {
            if (_records.TryGetValue(item.Target, out var rec) && IsUsableWindow(rec!.Hwnd))
                return rec.Hwnd;
            return FindWindowByTitle([HostOf(item.Target)]);
        }

        var pids = CollectTargetPids(item, consumeRecord: false);
        if (pids.Count == 0)
        {
            // 兜底：进程名认不出来（例如磁贴指向启动器/别名）时，按磁贴名称直接找隐藏窗口
            return FindHiddenWindowByTitle(item.Name);
        }

        // 在进程的全部顶层窗口里挑"真正的应用窗口"。Electron 类应用缩到托盘时主窗被隐藏，
        // 但悬浮条那种几十像素的小窗还留在屏幕上：只看可见窗口会选中这个小窗（如 Trae）。
        IntPtr visBest = 0, hidBest = 0;
        double visArea = 0, hidArea = 0;
        foreach (var hwnd in EnumProcessWindows(pids))
        {
            var area = WindowArea(hwnd);
            if (IsWindowVisible(hwnd))
            {
                if (area > visArea) { visArea = area; visBest = hwnd; }
            }
            else if (area > hidArea) { hidArea = area; hidBest = hwnd; }
        }

        // 任务栏上有它（可见的、或最小化在任务栏）：直接激活这个窗口，不需要碰托盘图标
        if (visBest != 0 && (IsIconic(visBest) || visArea >= MinAppWindowArea))
            return visBest;

        // 可见的只是个悬浮小工具，真正的窗口收在托盘里 → 交给托盘唤醒
        if (hidBest != 0) return hidBest;
        if (visBest != 0) return visBest;

        // 兜底：进程主窗口句柄
        foreach (var pid in pids)
        {
            if (TryGetProcess(pid, out var p) && p.MainWindowHandle != 0) return p.MainWindowHandle;
        }
        return IntPtr.Zero;
    }

    /// <summary>该组进程的可见窗口：取面积最大的。Electron/Chromium 类应用的进程主窗口句柄
    /// 常常指向托盘悬浮条那种几十像素的无界面小窗（如 Trae 的悬浮球），按主窗口优先会切错窗口</summary>
    private static IntPtr FindVisibleProcessWindow(HashSet<int> pids)
    {
        IntPtr best = 0;
        double bestArea = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains((int)pid)) continue;
            var area = WindowArea(hwnd);
            if (area <= bestArea) continue;
            bestArea = area;
            best = hwnd;
        }
        return best;
    }

    /// <summary>按磁贴名称找"隐藏"的主窗口（软件缩在托盘时的窗口标题通常就是软件名）</summary>
    private static IntPtr FindHiddenWindowByTitle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var ownPid = Environment.ProcessId;
        IntPtr best = 0;
        double bestArea = 0;
        foreach (var hwnd in EnumAllTopWindows())
        {
            if (IsWindowVisible(hwnd) || IsShellWindow(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid == ownPid || IsShellProcess((int)pid)) continue;
            if (!string.Equals(WindowText(hwnd).Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var area = WindowArea(hwnd);
            if (area <= bestArea) continue;
            bestArea = area;
            best = hwnd;
        }
        return best;
    }

    /// <summary>按窗口标题片段找窗口（可见窗口里取面积最大的；文件夹 / 网站磁贴）</summary>
    private static IntPtr FindWindowByTitle(string?[] keys)
    {
        var ownPid = Environment.ProcessId;
        IntPtr best = 0;
        double bestArea = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            if (IsShellWindow(hwnd)) continue; // 桌面/任务栏不能激活
            GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid == ownPid) continue;
            if (!TitleMatches(hwnd, keys)) continue;
            var area = WindowArea(hwnd);
            if (area <= bestArea) continue;
            bestArea = area;
            best = hwnd;
        }
        return best;
    }

    /// <summary>枚举指定进程的顶层窗口（含被隐藏的托盘主窗），跳过工具窗口与 0 面积窗口</summary>
    private static List<IntPtr> EnumProcessWindows(HashSet<int> pids)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains((int)pid) || IsShellWindow(h)) return true;
            var ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            if (WindowArea(h) <= 0) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>按窗口标题片段找窗口并最小化</summary>
    private static int MinimizeWindowsByTitle(string?[] keys)
    {
        var count = 0;
        foreach (var hwnd in EnumVisibleTopWindows())
        {
            if (IsShellWindow(hwnd)) continue;
            if (TitleMatches(hwnd, keys) && MinimizeWindow(hwnd)) count++;
        }
        return count;
    }

    private static bool TitleMatches(IntPtr hwnd, string?[] keys)
    {
        var title = WindowText(hwnd);
        if (title.Length == 0) return false;
        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key) && title.Contains(key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>文件夹磁贴的标题关键词：目录名 + 已知目录的本地化显示名</summary>
    private static string?[] FolderTitleKeys(string? path) => [NormalizeLeaf(path), GetDisplayName(path)];

    /// <summary>显示、还原、最大化并置前台（对托盘应用的隐藏主窗也强制显示）</summary>
    private static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsShellWindow(hwnd)) return false;
        var wasHidden = !IsWindowVisible(hwnd);
        if (IsIconic(hwnd) || wasHidden) ShowWindow(hwnd, SW_RESTORE);
        if (wasHidden) NudgeWindowSize(hwnd);   // 唤醒 Chromium/Electron 类应用的渲染与输入循环
        ShowWindow(hwnd, SW_MAXIMIZE);
        BringToForeground(hwnd);
        if (wasHidden)
        {
            // 只做异步失效，去掉 RDW_UPDATENOW：同步重绘要等目标线程响应，
            // 目标应用忙时会把调用线程一起拖住
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME);
        }
        Diag($"  ActivateWindow 0x{hwnd.ToInt64():X} wasHidden={wasHidden} " +
             $"fg=0x{GetForegroundWindow().ToInt64():X} nowVis={IsWindowVisible(hwnd)}");
        return true;
    }

    /// <summary>把窗口尺寸轻推 1px 再还原：隐藏窗口被外部显示后，Chromium 内核
    /// 不会刷新渲染/输入状态（看起来卡死），一次真实的尺寸变化能让它重新布局。</summary>
    private static void NudgeWindowSize(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var w = Math.Max(200, r.Right - r.Left);
        var h = Math.Max(200, r.Bottom - r.Top);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w + 1, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>把窗口拉到前台：优先走非侵入路径（SwitchToThisWindow + SetForegroundWindow），
    /// 只有被前台锁定挡住时才临时把"本线程"挂到前台线程的输入队列（挂的是自己，不挂目标线程；
    /// 挂目标线程会把两边的消息队列并在一起，目标应用容易假死无响应）。</summary>
    private static void BringToForeground(IntPtr hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return;
        var fg = GetForegroundWindow();
        if (fg == hwnd) return;

        SwitchToThisWindow(hwnd, true);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return;

        var curThread = GetCurrentThreadId();
        var fgThread = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
        var attached = false;
        try
        {
            if (fgThread != 0 && fgThread != curThread)
                attached = AttachThreadInput(curThread, fgThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, true);
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }

    /// <summary>启动器收起动画约 260ms，动画完成后焦点还会被系统再分配一次，
    /// 所以分两次（0.45s / 1s）把目标窗口重新拉回前台</summary>
    private static async Task ReassertForegroundAsync(IntPtr hwnd)
    {
        await Task.Delay(450);
        if (!IsWindow(hwnd)) return;
        BringToForeground(hwnd);
        await Task.Delay(550);
        if (IsWindow(hwnd)) BringToForeground(hwnd);
    }

    /// <summary>最小化窗口；本来就在最小化状态也算处理过（返回 true）</summary>
    private static bool MinimizeWindow(IntPtr hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsShellWindow(hwnd)) return false;
        if (IsIconic(hwnd)) return true;
        return ShowWindow(hwnd, SW_MINIMIZE);
    }

    private static double WindowArea(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return 0;
        return Math.Max(0, r.Right - r.Left) * (double)Math.Max(0, r.Bottom - r.Top);
    }

    private static bool IsUsableWindow(IntPtr hwnd)
        => hwnd != 0 && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsShellWindow(hwnd);

    // ---- 启动记录 ----

    private sealed class LaunchRecord
    {
        public IntPtr Hwnd;
        public int Pid;
    }

    private static readonly ConcurrentDictionary<string, LaunchRecord> _records = new();

    /// <summary>启动后轮询前台窗口：出现非本进程、非启动器自身的可见顶层窗口即记录其句柄。</summary>
    private static async Task CaptureForegroundAsync(string target, int startedPid)
    {
        var ownPid = Environment.ProcessId;
        for (int i = 0; i < 100; i++) // 最多约 10 秒：启动器型应用（先起 launcher 再拉起真身）需要更久
        {
            await Task.Delay(100);
            var fg = GetForegroundWindow();
            if (fg == 0 || !IsWindowVisible(fg)) continue;
            if (IsShellWindow(fg)) continue; // 桌面/任务栏：启动器隐藏后短暂切前台，记录后最小化会误触发
            GetWindowThreadProcessId(fg, out uint pid);
            if ((int)pid == ownPid) continue; // 仍是启动器自己（未隐藏/未切前台）
            if (IsShellProcess((int)pid)) continue; // 外壳进程 explorer：文件夹磁贴另有标题匹配路径
            // 记录：优先匹配本次启动进程；否则记录当前前台窗口所属进程
            var rec = _records.TryGetValue(target, out var r) ? r : new LaunchRecord();
            rec.Hwnd = fg;
            rec.Pid = (int)pid;
            _records[target] = rec;
            return;
        }
    }

    // ---- 目标进程匹配 ----

    /// <summary>收集该磁贴对应的进程：启动记录 + 进程名/启动器前缀/窗口标题匹配。
    /// 启动器型应用（SodaMusicLauncher → SodaMusic）与跨会话重启后都靠这一层兜底。</summary>
    private static HashSet<int> CollectTargetPids(LaunchItem item, bool consumeRecord = true)
    {
        var ownPid = Environment.ProcessId;
        var pids = new HashSet<int>();

        LaunchRecord? rec = null;
        if (consumeRecord) _records.TryRemove(item.Target, out rec);
        else _records.TryGetValue(item.Target, out rec);

        if (rec is not null)
        {
            // 捕获窗口所属进程优先（启动器场景下它就是真身进程），再补启动 PID
            if (rec.Hwnd != 0 && IsWindow(rec.Hwnd))
            {
                GetWindowThreadProcessId(rec.Hwnd, out uint pid);
                if ((int)pid == rec.Pid && rec.Pid != ownPid) pids.Add(rec.Pid);
            }
            if (rec.Pid != 0 && rec.Pid != ownPid) pids.Add(rec.Pid);
        }

        var leaf = ExeLeafOf(item.Target);
        var titleKey = item.Name?.Trim();

        // 按进程名收集（不要求有可见窗口）：应用缩到托盘后也能识别为"已运行"
        if (!string.IsNullOrEmpty(leaf))
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != ownPid && !pids.Contains(p.Id) && MatchesApp(p.ProcessName, leaf, "", null))
                        pids.Add(p.Id);
                }
                catch { /* 受保护进程取不到名字 */ }
                finally { p.Dispose(); }
            }
        }

        // 再按窗口标题补齐（启动器 exe 名与真身完全不同、只能靠磁贴名称认亲的情况）
        // 含被隐藏的托盘窗口：软件缩到托盘后仍然算"已运行"，不能重复启动
        foreach (var hwnd in EnumAllTopWindows())
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            var id = (int)pid;
            if (id == ownPid || IsShellProcess(id) || pids.Contains(id)) continue;
            if (!TryGetProcess(id, out var p)) continue;
            if (MatchesApp(p.ProcessName, leaf, WindowText(hwnd), titleKey)) pids.Add(id);
        }
        return pids;
    }

    /// <summary>进程是否属于这个磁贴：1) 进程名 = exe 名；2) 启动器与被启动程序共享前缀
    /// （SodaMusicLauncher / SodaMusic）；3) 窗口标题与磁贴名称完全相同。</summary>
    private static bool MatchesApp(string processName, string? exeLeaf, string windowTitle, string? displayName)
    {
        if (!string.IsNullOrEmpty(exeLeaf))
        {
            if (string.Equals(processName, exeLeaf, StringComparison.OrdinalIgnoreCase)) return true;
            // 启动器与真身互为前缀（SodaMusicLauncher ↔ SodaMusic、WeChat ↔ WeChatAppEx）
            if (processName.Length >= 4 && exeLeaf.Length >= 4
                && (processName.StartsWith(exeLeaf, StringComparison.OrdinalIgnoreCase)
                    || exeLeaf.StartsWith(processName, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return !string.IsNullOrEmpty(displayName)
               && string.Equals(windowTitle.Trim(), displayName, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 目标解析 ----

    /// <summary>程序磁贴 → 进程名（exe 文件名去扩展名；.lnk 解析真实目标）</summary>
    private static string? ExeLeafOf(string target)
    {
        try
        {
            var path = Path.GetFullPath(target);
            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var real = DropImportService.ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(real)) path = real;
            }
            var name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>URL → 主机名（去掉 www.），用于匹配浏览器窗口标题</summary>
    private static string? HostOf(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch { return null; }
    }

    /// <summary>文件夹路径 → 目录名（资源管理器窗口标题即目录名）</summary>
    private static string? NormalizeLeaf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var name = Path.GetFileName(full);
            // 盘符根（如 D:\）标题为 "数据盘 (D:)" 等，兜底用盘符匹配
            return string.IsNullOrEmpty(name) ? full : name;
        }
        catch { return null; }
    }

    private static bool TryGetProcess(int pid, out Process process)
    {
        try { process = Process.GetProcessById(pid); return true; }
        catch { process = null!; return false; }
    }

    /// <summary>目标路径是否为目录（Kind 被误存为 App 的文件夹磁贴也走 explorer 标题匹配；含"."的目录名不受影响）</summary>
    private static bool IsDirectoryTarget(string target)
    {
        try { return Directory.Exists(target); }
        catch { return false; }
    }

    /// <summary>常见用户目录的本地化显示名（英文目录名 → 资源管理器中文标题）；非已知目录返回 null</summary>
    private static string? GetDisplayName(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return null;
        try
        {
            var leaf = Path.GetFileName(Path.GetFullPath(folderPath).TrimEnd('\\', '/'));
            return leaf.ToLowerInvariant() switch
            {
                "downloads" => "下载", "documents" => "文档", "pictures" => "图片",
                "music" => "音乐", "videos" => "视频", "desktop" => "桌面",
                "favorites" => "收藏夹", "links" => "快速启动", "saved games" => "已保存的游戏",
                "searches" => "搜索", "3d objects" => "三维对象", "contacts" => "联系人",
                _ => null
            };
        }
        catch { return null; }
    }

    // ---- Win32 ----

    /// <summary>小于这个面积的窗口视为悬浮小工具/托盘辅助窗，不算"任务栏上的应用窗口"</summary>
    private const double MinAppWindowArea = 60000;

    private const int SW_SHOW = 5;
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    /// <summary>是否为系统外壳窗口（桌面 / 任务栏）：既不能激活也不能最小化，误操作会牵连整个外壳</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = WindowClass(hwnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>该 PID 是否为 Windows 外壳进程（explorer.exe）</summary>
    private static bool IsShellProcess(int pid)
        => TryGetProcess(pid, out var p) && string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);

    private static string WindowClass(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) <= 0 ? "" : sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr update, IntPtr region, uint flags);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ALLCHILDREN = 0x0080;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    /// <summary>枚举所有可见、有标题的顶层窗口句柄</summary>
    private static List<IntPtr> EnumVisibleTopWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowTextLengthW(h) > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>枚举所有有标题的顶层窗口（含被隐藏的托盘窗口），跳过工具窗口与 0 面积窗口</summary>
    private static List<IntPtr> EnumAllTopWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (GetWindowTextLengthW(h) <= 0) return true;
            var ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            if (WindowArea(h) <= 0) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string WindowText(IntPtr hwnd)
    {
        // 卡死的窗口不要读标题：GetWindowTextW 是跨进程同步消息，目标线程不响应时会一直等，
        // 拖住调用线程（放在 UI 线程就是整个启动器"卡死"）
        if (hwnd == 0 || IsHungAppWindow(hwnd)) return "";
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
