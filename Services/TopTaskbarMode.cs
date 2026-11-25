using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameLocker.Services;

/// <summary>
/// 顶部任务栏模式：配合 Windows 11 原生“任务栏位置=顶部”。
/// 任务栏在顶部时，系统会永久预留顶部一条（约 48 DIP）的工作区；本服务把任务栏窗口强制隐藏，
/// 让顶栏2 用同样的高度填满这条预留区，于是托盘溢出（Win+B）和通知中心（Win+N）仍从顶部弹出，
/// 但看不到系统任务栏本身。
///
/// 与 TaskbarControl（底栏的“任务栏隐藏”）分开：这里不做自动隐藏/工作区改写，只压制窗口显示。
/// </summary>
internal static class TopTaskbarMode
{
    private const string TrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABE_TOP = 1;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private static readonly object Gate = new();
    private static Timer? _watchdog;
    private static bool _active;
    private static bool _restoreOwner;

    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskbarSizeValue = "TaskbarSize"; // 26H2：0=默认 48 DIP，1=小 32 DIP
    private const int SmallTaskbarSize = 1;
    private static bool _sizeCaptured;
    private static bool _sizeHadValue;
    private static int _sizeOriginal;

    private static string SizeBackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker", "top-taskbar-size.txt");

    public static bool IsActive
    {
        get { lock (Gate) return _active; }
    }

    /// <summary>任务栏高度（DIP），启用时读取；未启用时为 48。</summary>
    public static int HeightDip { get; private set; } = 48;

    /// <summary>当前跟随的任务栏边缘（顶部或底部）。</summary>
    public static TaskbarEdge Edge { get; private set; } = TaskbarEdge.Top;

    /// <summary>顶栏实际使用的高度：与任务栏预留条一致，才能完整覆盖、不露出桌面。</summary>
    public static int DisplayHeightDip => HeightDip;

    /// <summary>因任务栏不再位于顶部而自动退出时触发（后台线程）。</summary>
    public static event Action? StateChanged;

    /// <summary>任务栏当前是否在顶部，并返回其高度（DIP）。</summary>
    public static bool IsTaskbarOnTop(out int heightDip)
    {
        if (!TaskbarEdgeService.TryGet(out var edge, out heightDip)) return false;
        return edge == TaskbarEdge.Top;
    }

    /// <summary>尝试启用。任务栏不在顶部时返回 false，message 给用户看。</summary>
    public static bool TryEnable(out string message)
    {
        lock (Gate)
        {
            if (!_restoreOwner)
            {
                message = "只有主实例可以更改系统任务栏状态";
                return false;
            }
            if (_active)
            {
                message = "";
                return true;
            }
        }

        if (!TaskbarEdgeService.TryGet(out var edge, out var height))
        {
            message = "未找到系统任务栏";
            return false;
        }
        if (edge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            message = "请先把任务栏位置设置为「顶部」或「底部」";
            return false;
        }

        lock (Gate)
        {
            if (_active)
            {
                message = "";
                return true;
            }
            HeightDip = height;
            Edge = edge;
            _active = true;
        }

        // 只有任务栏在顶部时，才把它切成“小”以压缩顶部预留条；底部保持原高度
        if (Edge == TaskbarEdge.Top)
        {
            CaptureTaskbarSize();
            if (ReadTaskbarSize() != SmallTaskbarSize)
            {
                WriteTaskbarSize(SmallTaskbarSize);
                BroadcastSettingsChange();
                Thread.Sleep(450);
                if (TaskbarEdgeService.TryGet(out _, out var newHeight)) HeightDip = newHeight;
            }
        }

        HideTaskbars();
        _watchdog ??= new Timer(_ => WatchdogTick(), null, 150, 150);
        DebugLog.Write($"[任务栏] 顶部模式：已隐藏任务栏，高度 {HeightDip} DIP（小任务栏）");
        message = "";
        return true;
    }

    internal static void SetRestoreOwner(bool owns)
    {
        lock (Gate) _restoreOwner = owns;
    }

    public static void Disable() => Disable(force: false);

    /// <summary>仅主实例或显式恢复工具可消费共享任务栏快照。</summary>
    public static void ForceRestore() => Disable(force: true);

    private static void Disable(bool force)
    {
        lock (Gate)
        {
            if (!_restoreOwner) return;
            if (!_active && !force && !File.Exists(SizeBackupPath)) return;
            _active = false;
        }

        _watchdog?.Dispose();
        _watchdog = null;
        RestoreTaskbarSize();
        BroadcastSettingsChange();
        Thread.Sleep(350);
        ShowTaskbars();
        Thread.Sleep(200);
        ShowTaskbars();
        DebugLog.Write("[任务栏] 顶部模式：已恢复任务栏显示与大小");
    }

    private static void WatchdogTick()
    {
        lock (Gate)
        {
            if (!_active) return;
        }

        if (TaskbarEdgeService.TryGet(out var edge, out _) && edge != Edge)
        {
            // 用户把任务栏改到其它边缘：立即恢复显示并通知宿主重新布局
            Disable();
            StateChanged?.Invoke();
            return;
        }

        HideTaskbars();
    }

    private static void CaptureTaskbarSize()
    {
        if (_sizeCaptured) return;

        // 上次异常退出（强杀）留下的恢复点优先，避免把已经被我们改成“小”的值当成原值
        try
        {
            if (File.Exists(SizeBackupPath))
            {
                var text = File.ReadAllText(SizeBackupPath).Trim();
                _sizeHadValue = !text.Equals("absent", StringComparison.OrdinalIgnoreCase);
                _sizeOriginal = _sizeHadValue && int.TryParse(text, out var parsed) ? parsed : 0;
                _sizeCaptured = true;
                return;
            }
        }
        catch { }

        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        var value = key?.GetValue(TaskbarSizeValue);
        _sizeHadValue = value is not null;
        _sizeOriginal = value is int i ? i : 0;
        _sizeCaptured = true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SizeBackupPath)!);
            File.WriteAllText(SizeBackupPath, _sizeHadValue ? _sizeOriginal.ToString() : "absent");
        }
        catch { }
    }

    private static int ReadTaskbarSize()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return key?.GetValue(TaskbarSizeValue) is int i ? i : 0;
    }

    private static void WriteTaskbarSize(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey);
        key?.SetValue(TaskbarSizeValue, value, RegistryValueKind.DWord);
    }

    private static void RestoreTaskbarSize()
    {
        if (!_sizeCaptured)
        {
            // 进程被强杀后 _sizeCaptured 会丢失，退出恢复时以磁盘备份为准
            try
            {
                if (!File.Exists(SizeBackupPath)) return;
                var text = File.ReadAllText(SizeBackupPath).Trim();
                _sizeHadValue = !text.Equals("absent", StringComparison.OrdinalIgnoreCase);
                _sizeOriginal = _sizeHadValue && int.TryParse(text, out var parsed) ? parsed : 0;
                _sizeCaptured = true;
            }
            catch { return; }
        }

        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: true);
        if (key is null) return;
        if (_sizeHadValue) key.SetValue(TaskbarSizeValue, _sizeOriginal, RegistryValueKind.DWord);
        else key.DeleteValue(TaskbarSizeValue, throwOnMissingValue: false);
        _sizeCaptured = false;

        try { File.Delete(SizeBackupPath); } catch { }
    }

    private static void BroadcastSettingsChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xffff), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero,
                "TraySettings", 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    private static void HideTaskbars()
    {
        EnumWindows((hwnd, _) =>
        {
            var cls = ClassName(hwnd);
            if (cls is TrayClass or SecondaryTrayClass && IsWindowVisible(hwnd))
                ShowWindow(hwnd, SW_HIDE);
            return true;
        }, IntPtr.Zero);
    }

    private static void ShowTaskbars()
    {
        EnumWindows((hwnd, _) =>
        {
            var cls = ClassName(hwnd);
            if (cls is TrayClass or SecondaryTrayClass)
                ShowWindow(hwnd, 5 /* SW_SHOW */);
            return true;
        }, IntPtr.Zero);
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(128);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString(0, n) : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public AppBarHost.RECT rc;
        public IntPtr lParam;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
