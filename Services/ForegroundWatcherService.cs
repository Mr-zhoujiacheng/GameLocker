using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// <summary>前台应用监测小组件（WidgetKind.Foreground）的领域逻辑：后台定时轮询当前前台窗口，解析其所属进程，
    /// 换前台窗口即更新 <see cref="Current"/> 并触发 <see cref="StateChanged"/>（调用方 marshal 回 UI 线程刷新磁贴）。
    /// 启动器自身与桌面/任务栏窗口不算"前台应用"，遇到时保留上一次检测到的真实应用。
    /// 交互：A/点击=冻结/解冻（NtSuspendProcess / NtResumeProcess，解冻用 A 再按一次）；
    /// Y=强制结束（TerminateProcess，冻结状态下同样有效）。关键系统进程与启动器自身不可操作。
    /// </summary>
    public static class ForegroundWatcherService
    {
        /// <summary>前台应用信息：进程号、进程名、可执行文件路径（用于取图标）、窗口标题</summary>
        public sealed record ForegroundApp(int ProcessId, string ProcessName, string? ExePath, string Title);

        /// <summary>前台时的轮询间隔（毫秒）：换前台窗口后最迟一个周期内被检测到</summary>
        private const int PollMsForeground = 400;
        /// <summary>后台时的轮询间隔（毫秒）：前台进程变化少，降频到 3s 足够，省 CPU</summary>
        private const int PollMsBackground = 3000;

        /// <summary>当前实际生效的轮询间隔</summary>
        private static int _pollMs = PollMsForeground;

    /// <summary>当前监测到的前台应用（null=尚未检测到任何应用）</summary>
    public static ForegroundApp? Current { get; private set; }

    private static readonly object _frameGenLock = new();
    private static readonly object _lifecycleLock = new();
    private static IntPtr _frameGenTargetWindow;
    private static readonly WinEventProc _foregroundCallback = OnForegroundChanged;
    private static IntPtr _foregroundHook;
    internal static event Action? ForegroundWindowChanged;

    internal static void StartForegroundEvents()
    {
        if (_foregroundHook == IntPtr.Zero)
            _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback, 0, 0, 0);
        RefreshFrameGenTarget();
    }

    internal static void StopForegroundEvents()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
    }

    /// <summary>独立于冻结目标的插帧候选窗口；零表示当前没有可用候选。</summary>
    public static IntPtr FrameGenTargetWindow
    {
        get { lock (_frameGenLock) return _frameGenTargetWindow; }
    }

    /// <summary>以当前前台 HWND 刷新插帧候选；启动器自身前台时保留上一个候选。</summary>
    public static void RefreshFrameGenTarget()
    {
        lock (_frameGenLock) UpdateFrameGenTarget(GetForegroundWindow());
    }

    private static void UpdateFrameGenTarget(IntPtr hwnd)
    {
        // 调用方持有 _frameGenLock；先识别自身，避免启动器覆盖或清空候选。
        GetWindowThreadProcessId(hwnd, out var pid);
        if (hwnd != IntPtr.Zero && pid == (uint)Environment.ProcessId) return;
        // 内置插帧输出窗不能覆盖上一个游戏候选。
        if (hwnd != IntPtr.Zero && IsFrameGenOutputWindow(hwnd, pid)) return;

        _frameGenTargetWindow = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || pid == 0 || !IsWindow(hwnd)
            || !IsWindowVisible(hwnd) || IsIconic(hwnd) || GetAncestor(hwnd, 2) != hwnd) return;
        var className = ClassName(hwnd);
        if (string.IsNullOrEmpty(className)
            || className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;
        if (string.IsNullOrWhiteSpace(WindowTitle(hwnd))) return;
        var (name, _) = Describe(pid);
        if (name is null || name.ToLowerInvariant() is
            "explorer" or "startmenuexperiencehost" or "searchhost" or "searchapp"
            or "textinputhost" or "shellexperiencehost" or "applicationframehost"
            or "sihost" or "dwm" or "lockapp" or "logonui" or "shellhost"
            or "gamelocker") return;
        if (IsWindow(hwnd)) _frameGenTargetWindow = hwnd;
    }

    private static bool IsFrameGenOutputWindow(IntPtr hwnd, uint pid)
    {
        if (ClassName(hwnd) is "OptiCaptureHostOverlay" or "OptiCaptureHostLabel") return true;
        if (pid <= 4) return false;
        var (name, _) = Describe(pid);
        return string.Equals(name, "OptiCaptureHost", StringComparison.OrdinalIgnoreCase);
    }

    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint eventTime)
    {
        try
        {
            // 使用事件携带的 HWND，不重新采样，避免漏掉“桌面 -> 启动器”的短暂切换。
            lock (_frameGenLock) UpdateFrameGenTarget(hwnd);
            ForegroundWindowChanged?.Invoke();
        }
        catch (Exception ex)
        {
            lock (_frameGenLock) _frameGenTargetWindow = IntPtr.Zero;
            Debug.WriteLine($"[Foreground] 插帧候选刷新失败: {ex.Message}");
        }
    }

    // 持有原始进程句柄，退出恢复时不会误操作复用 PID 的新进程。
    private static readonly Dictionary<int, SafeProcessHandle> _frozen = [];
    private static bool _stopping;

    /// <summary>当前前台应用是否已被本小组件冻结</summary>
    public static bool IsFrozen
    {
        get
        {
            var app = Current;
            if (app is null) return false;
            lock (_frozen) return _frozen.TryGetValue(app.ProcessId, out var handle) && IsAlive(handle);
        }
    }

    /// <summary>前台应用或冻结状态变化（在后台轮询线程触发；Kill/ToggleFreeze 则由调用线程触发）</summary>
    public static event Action? StateChanged;

    private static System.Threading.Timer? _timer;
    private static int _polling;   // 重入保护：上一轮未结束则跳过本轮

    /// <summary>启动轮询（重复调用无副作用；由主页进入时调用）</summary>
    public static void Start()
    {
        lock (_lifecycleLock)
            _timer ??= new System.Threading.Timer(_ => Poll(), null, 0, _pollMs);
    }

    /// <summary>暂停轮询，保留冻结记录，恢复后仍可解冻原来的应用。</summary>
    public static void Pause()
    {
        lock (_lifecycleLock) PauseCore();
    }

    private static void PauseCore()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>停止轮询并清除当前状态（退出时调用）</summary>
    public static void Stop()
    {
        lock (_lifecycleLock)
        {
            PauseCore();
            lock (_frameGenLock) _frameGenTargetWindow = IntPtr.Zero;
            Current = null;
        }
    }

    internal static void ResumeFrozenOnExit()
    {
        lock (_frozen)
        {
            _stopping = true;
            foreach (var (pid, handle) in _frozen)
            {
                using (handle)
                {
                    if (IsAlive(handle) && NtResumeProcess(handle) != 0)
                        DebugLog.Write($"[前台应用] 退出时恢复冻结进程 {pid} 失败");
                }
            }
            _frozen.Clear();
        }
    }

    /// <summary>主窗口切换前台/后台时动态调整轮询间隔：
    /// 前台 400ms 即时响应切换，后台 3000ms 降频省 CPU。Timer 已创建时直接 Change，不重建</summary>
    public static void SetForeground(bool foreground)
    {
        var target = foreground ? PollMsForeground : PollMsBackground;
        if (_pollMs == target) return;
        _pollMs = target;
        if (_timer is not null) _timer.Change(0, target);
    }

    /// <summary>冻结/解冻当前前台应用，返回（是否成功，操作后是否处于冻结态）</summary>
    public static (bool Ok, bool Frozen) ToggleFreeze()
    {
        var app = Current;
        if (app is null || IsProtected(app)) return (false, false);
        bool frozen;
        lock (_frozen)
        {
            if (_stopping) return (false, false);
            if (_frozen.TryGetValue(app.ProcessId, out var previous) && IsAlive(previous))
            {
                if (NtResumeProcess(previous) != 0) return (false, true);
                _frozen.Remove(app.ProcessId);
                previous.Dispose();
                frozen = false;
            }
            else
            {
                if (_frozen.Remove(app.ProcessId, out var ended)) ended.Dispose();
                var handle = new SafeProcessHandle(OpenProcess(ProcessSuspendResume | Synchronize, false, app.ProcessId), ownsHandle: true);
                if (handle.IsInvalid || NtSuspendProcess(handle) != 0)
                {
                    handle.Dispose();
                    return (false, false);
                }
                _frozen.Add(app.ProcessId, handle);
                frozen = true;
            }
        }
        StateChanged?.Invoke();
        return (true, frozen);
    }

    /// <summary>强制结束当前前台应用，成功后清空当前应用（下一次轮询会重新检测）</summary>
    public static bool Kill()
    {
        var app = Current;
        if (app is null || IsProtected(app)) return false;
        var handle = OpenProcess(ProcessTerminate, false, app.ProcessId);
        if (handle == IntPtr.Zero) return false;   // 权限不足（多为受保护进程）
        try
        {
            if (!TerminateProcess(handle, 1)) return false;
            Current = null;
            lock (_frozen)
            {
                if (_frozen.Remove(app.ProcessId, out var frozen)) frozen.Dispose();
            }
            StateChanged?.Invoke();
            return true;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>关键系统进程 / 启动器自身不可冻结或结束（冻结 lsass 之类会让系统不可恢复）</summary>
    public static bool IsProtected(ForegroundApp app)
    {
        if (app.ProcessId == Environment.ProcessId) return true;
        return app.ProcessName.ToLowerInvariant() is
            "system" or "registry" or "idle" or "csrss" or "wininit" or "winlogon"
            or "services" or "lsass" or "smss" or "dwm" or "explorer" or "audiodg";
    }

    private static void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;   // 上一轮未结束：跳过
        try
        {
            PruneFrozen();
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            // 桌面/任务栏窗口不作为"前台应用"（否则会把 explorer 当成前台应用）
            if (ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd") return;
            if (Current is not null && Current.ProcessId == (int)pid) return;
            var (name, exe) = Describe(pid);
            if (name is null) return;
            // 开始菜单 / 搜索 / 输入面板等系统壳 UI 不算前台应用，保留上一次真正的应用
            if (name is "StartMenuExperienceHost" or "SearchHost" or "TextInputHost" or "ShellExperienceHost") return;
            Current = new ForegroundApp((int)pid, name, exe, WindowTitle(hwnd));
            StateChanged?.Invoke();
            if (exe is not null) _ = ExtractIconAsync(exe);
        }
        catch (Exception ex) { Debug.WriteLine($"[Foreground] 轮询失败: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    /// <summary>清理已退出的冻结记录（进程被结束/顶替）：集合通常只有 0~1 项，逐项探测开销可忽略</summary>
    private static void PruneFrozen()
    {
        int[] dead;
        lock (_frozen)
        {
            if (_frozen.Count == 0) return;
            dead = _frozen.Where(entry => !IsAlive(entry.Value)).Select(entry => entry.Key).ToArray();
            foreach (var pid in dead)
            {
                if (_frozen.Remove(pid, out var handle)) handle.Dispose();
            }
        }
        if (dead.Length > 0) StateChanged?.Invoke();
    }

    private static bool IsAlive(SafeProcessHandle handle) => WaitForSingleObject(handle, 0) == 0x102;

    /// <summary>图标未缓存时后台提取，完成后通知刷新（磁贴据此把占位字形换成应用图标）</summary>
    private static async Task ExtractIconAsync(string exePath)
    {
        try
        {
            if (IconExtractService.CachedIconPath(exePath) is not null) return;
            if (await IconExtractService.ExtractAsync(exePath) is not null) StateChanged?.Invoke();
        }
        catch (Exception ex) { Debug.WriteLine($"[Foreground] 提取图标失败: {ex.Message}"); }
    }

    /// <summary>取进程名与可执行文件路径（进程已退出 / 受保护进程读主模块会抛异常，此时 ExePath 为空）</summary>
    private static (string? Name, string? Exe) Describe(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch { }
            return (p.ProcessName, exe);
        }
        catch { return (null, null); }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    // ---- P/Invoke ----
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSuspendResume = 0x0800;
    private const uint Synchronize = 0x00100000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(SafeProcessHandle processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(SafeProcessHandle processHandle);
}
