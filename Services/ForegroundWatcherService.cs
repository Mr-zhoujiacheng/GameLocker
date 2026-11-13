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
    private static Thread? _foregroundThread;
    private static uint _foregroundThreadId;
    private static IntPtr _foregroundHook;
    private static System.Threading.Timer? _frameGenFallbackTimer;
    private static bool _watching;

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

        _frameGenTargetWindow = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || pid == 0 || !IsWindow(hwnd)
            || !IsWindowVisible(hwnd) || IsIconic(hwnd) || GetAncestor(hwnd, 2) != hwnd) return;
        var className = ClassName(hwnd);
        if (string.IsNullOrEmpty(className)
            || className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            || className.Contains("Magpie", StringComparison.OrdinalIgnoreCase)
            || className.Contains("FGHost", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(WindowTitle(hwnd))) return;
        var (name, _) = Describe(pid);
        if (name is null || name.ToLowerInvariant() is
            "explorer" or "startmenuexperiencehost" or "searchhost" or "searchapp"
            or "textinputhost" or "shellexperiencehost" or "applicationframehost"
            or "sihost" or "dwm" or "lockapp" or "logonui" or "shellhost"
            or "fghost" or "magpie" or "gamelocker") return;
        if (IsWindow(hwnd)) _frameGenTargetWindow = hwnd;
    }

    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint eventTime)
    {
        try
        {
            // 使用事件携带的 HWND，不重新采样，避免漏掉“桌面 -> 启动器”的短暂切换。
            lock (_frameGenLock)
            {
                if (_watching) UpdateFrameGenTarget(hwnd);
            }
        }
        catch (Exception ex)
        {
            lock (_frameGenLock) _frameGenTargetWindow = IntPtr.Zero;
            Debug.WriteLine($"[Foreground] 插帧候选刷新失败: {ex.Message}");
        }
    }

    /// <summary>已被本小组件挂起的进程号集合：系统没有"是否被挂起"的查询接口，由本服务记录；
    /// 按进程号而非单个标记记录，避免"冻结 A 后切到 B，A 再也无法解冻"（多进程访问，加锁保护）</summary>
    private static readonly HashSet<int> _frozen = [];

    /// <summary>当前前台应用是否已被本小组件冻结</summary>
    public static bool IsFrozen
    {
        get
        {
            var app = Current;
            if (app is null) return false;
            lock (_frozen) return _frozen.Contains(app.ProcessId);
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
        {
            if (_timer is not null) return;
            lock (_frameGenLock) _watching = true;
            using var ready = new ManualResetEventSlim();
            _foregroundThread = new Thread(() =>
            {
                // OUTOFCONTEXT 事件必须在注册线程泵消息，不能依赖调用方是 UI 线程。
                _foregroundThreadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback, 0, 0, 0);
                ready.Set();
                if (_foregroundHook == IntPtr.Zero) return;
                try
                {
                    while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                }
                finally
                {
                    UnhookWinEvent(_foregroundHook);
                    _foregroundHook = IntPtr.Zero;
                }
            }) { IsBackground = true, Name = "ForegroundWindowEvents" };
            _foregroundThread.Start();
            ready.Wait();
            if (_foregroundHook == IntPtr.Zero)
            {
                Debug.WriteLine("[Foreground] 前台事件注册失败，插帧候选降级为 400ms 采样，可能漏掉短暂桌面切换");
                _frameGenFallbackTimer = new System.Threading.Timer(_ =>
                {
                    lock (_frameGenLock)
                    {
                        if (_watching) RefreshFrameGenTarget();
                    }
                }, null, 0, PollMsForeground);
            }
            _timer = new System.Threading.Timer(_ => Poll(), null, 0, _pollMs);
        }
    }

    /// <summary>暂停轮询，保留冻结记录，恢复后仍可解冻原来的应用。</summary>
    public static void Pause()
    {
        lock (_lifecycleLock) PauseCore();
    }

    private static void PauseCore()
    {
        lock (_frameGenLock) _watching = false;
        _timer?.Dispose();
        _timer = null;
        _frameGenFallbackTimer?.Dispose();
        _frameGenFallbackTimer = null;
        if (_foregroundThread is not null)
        {
            if (_foregroundThread.IsAlive) PostThreadMessage(_foregroundThreadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
            _foregroundThread.Join();
            _foregroundThread = null;
            _foregroundThreadId = 0;
        }
    }

    /// <summary>停止轮询并清除当前状态（退出时调用）</summary>
    public static void Stop()
    {
        lock (_lifecycleLock)
        {
            PauseCore();
            lock (_frameGenLock) _frameGenTargetWindow = IntPtr.Zero;
            Current = null;
            lock (_frozen) _frozen.Clear();
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
        var wasFrozen = IsFrozen;
        var handle = OpenProcess(ProcessSuspendResume, false, app.ProcessId);
        if (handle == IntPtr.Zero) return (false, wasFrozen);   // 权限不足（多为受保护进程）
        try
        {
            var nt = wasFrozen ? NtResumeProcess(handle) : NtSuspendProcess(handle);
            if (nt != 0) return (false, wasFrozen);
            lock (_frozen)
            {
                if (wasFrozen) _frozen.Remove(app.ProcessId);
                else _frozen.Add(app.ProcessId);
            }
            StateChanged?.Invoke();
            return (true, !wasFrozen);
        }
        finally { CloseHandle(handle); }
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
            lock (_frozen) _frozen.Remove(app.ProcessId);
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
            lock (_frameGenLock)
            {
                if (_watching) RefreshFrameGenTarget();
            }
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
        if (_frozen.Count == 0) return;
        List<int> dead = [];
        lock (_frozen)
        {
            foreach (var pid in _frozen)
            {
                try { using var p = Process.GetProcessById(pid); }
                catch { dead.Add(pid); }
            }
            foreach (var pid in dead) _frozen.Remove(pid);
        }
        if (dead.Count > 0) StateChanged?.Invoke();
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
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

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
