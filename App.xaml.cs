using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using GameLocker.Services;

namespace GameLocker;

/// <summary>应用入口：创建主窗口并启动托盘/热键常驻</summary>
public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    private TrayHotkeyService? _tray;
    private Mutex? _instanceMutex;

    public App()
    {
        InstallCrashLogging();
        InitializeComponent();
        // 兜底：即使退出路径没有走到 MainWindow.Closed，也把「任务栏隐藏」恢复
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { TaskbarControl.Shutdown(); } catch { }
            try { TopTaskbarMode.ForceRestore(); } catch { }
            try { StatusBarHost.Shutdown(); } catch { }
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private bool AcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(false, @"Local\GameLocker.MainInstance");
            try
            {
                if (_instanceMutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException)
            {
                // 上次异常退出时，系统已将互斥锁的所有权交给当前线程。
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 已提权的实例持有互斥锁时，普通权限实例也不能再次启动。
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        MessageBoxW(IntPtr.Zero, "软件已在运行，请使用托盘图标打开现有窗口。", "GameLocker", 0x00000040 | 0x00010000);
        return false;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 提权监控助手模式（--monitor-helper <父进程PID>）：只跑 ETW 帧率采集并写共享内存，
        // 不创建主窗口，父进程退出或发出停止事件后自行结束。
        var argv = Environment.GetCommandLineArgs();

        // 仅测试恢复顶部任务栏（--restore-taskbar）。
        if (Array.IndexOf(argv, "--restore-taskbar") >= 0)
        {
            TopTaskbarMode.ForceRestore();
            return;
        }

        // 仅显示设置主页（--settings-hub-preview）：确认「顶栏2」卡片位置，不启动启动器。
        if (Array.IndexOf(argv, "--settings-hub-preview") >= 0)
        {
            var preview = new Window { Title = "设置预览" };
            preview.Content = new Frame();
            if (preview.Content is Frame frame) frame.Navigate(typeof(SettingsPage));
            preview.Activate();
            return;
        }

        // 右键诊断（--right-click-test）：把光标放到桌面并注入一次右键。
        if (Array.IndexOf(argv, "--right-click-test") >= 0)
        {
            SetCursorPos(600, 400);
            Thread.Sleep(150);
            KeySendService.SendMouse(KeySendService.MouseRight);
            return;
        }

        // 仅测试打开系统托盘（--open-tray-preview）：验证 Win+B 注入是否可用。
        if (Array.IndexOf(argv, "--open-tray-preview") >= 0)
        {
            TrayAppService.OpenSystemTray();
            return;
        }

        // 仅显示控制中心（--control-center-preview）：调弹层版式，不启动启动器/顶栏。
        if (Array.IndexOf(argv, "--control-center-preview") >= 0)
        {
            var monitor = Monitors.Primary();
            StatusBarControlCenterWindow.Toggle(monitor.Bounds.Right, monitor.Bounds.Top, monitor.Bounds.Top + 64, false);
            return;
        }

        // 仅显示「顶栏2」设置页（--statusbar-settings-preview）：用于调设置页版式，不启动启动器。
        if (Array.IndexOf(argv, "--statusbar-settings-preview") >= 0)
        {
            var preview = new Window { Title = "系统 设置预览" };
            preview.Content = new Frame();
            if (preview.Content is Frame frame) frame.Navigate(typeof(StatusBarSettingsPage));
            preview.Activate();
            return;
        }

        // 仅显示系统顶栏（--statusbar-preview）：用于截图调样式，不启动启动器。
        if (Array.IndexOf(argv, "--statusbar-preview") >= 0)
        {
            try
            {
                _ = StatusBarHost.InitializeAsync();
                _ = new StatusBarSettingsPage(); // 验证设置页 XAML 可构造
                DebugLog.Write("[预览] 顶栏2 已创建");
            }
            catch (Exception ex) { DebugLog.Write("[预览] 顶栏2 失败：" + ex); }
            return;
        }

        // 仅显示底栏与系统顶栏（--dock-preview）：用于截图与调样式，不启动启动器。
        if (Array.IndexOf(argv, "--dock-preview") >= 0)
        {
            try
            {
                _ = DockHost.InitializeAsync();
                _ = StatusBarHost.InitializeAsync();
                // 构建设置页，验证 XAML 与行构建不会在运行时抛异常
                _ = new DockSettingsPage();
                DebugLog.Write("[预览] 底栏已创建");
            }
            catch (Exception ex) { DebugLog.Write("[预览] 失败：" + ex); }
            return;
        }

        var edgePolicyIndex = Array.IndexOf(argv, SystemEdgeSwipePolicy.HelperArgument);
        if (edgePolicyIndex >= 0)
        {
            var operation = edgePolicyIndex + 1 < argv.Length ? argv[edgePolicyIndex + 1] : "";
            var validParent = edgePolicyIndex + 3 < argv.Length
                && int.TryParse(argv[edgePolicyIndex + 2], out _)
                && long.TryParse(argv[edgePolicyIndex + 3], out _);
            Environment.Exit(validParent
                ? SystemEdgeSwipePolicy.RunHelper(operation, int.Parse(argv[edgePolicyIndex + 2]), long.Parse(argv[edgePolicyIndex + 3]))
                : 2);
            return;
        }
        // 登录计划任务模式（--apply-cpu-power）：以管理员权限把已保存的 CPU 功耗写回硬件后退出，
        // 不创建窗口。放在单实例检查之前，避免开机自启已经在跑时任务被吞掉。
        if (Array.IndexOf(argv, "--apply-cpu-power") >= 0)
        {
            CpuPowerAutoApply.RunFromTask();
            Environment.Exit(0);
        }
        var powerIndex = Array.IndexOf(argv, "--cpu-power-helper");
        if (powerIndex >= 0 && powerIndex + 2 < argv.Length
            && int.TryParse(argv[powerIndex + 1], out var powerParent))
        {
            CpuPowerService.RunHelper(powerParent, argv[powerIndex + 2]);
            Environment.Exit(0);
        }
        var helperIndex = Array.IndexOf(argv, "--monitor-helper");
        if (helperIndex >= 0 && helperIndex + 1 < argv.Length
            && int.TryParse(argv[helperIndex + 1], out var parentPid))
        {
            MonitorHelperHost.Run(parentPid);
            Environment.Exit(0);
        }

        if (!AcquireSingleInstance())
        {
            Exit();
            return;
        }

        DebugLog.Write($"启动：PID={Environment.ProcessId} 程序={Environment.ProcessPath}");
        MigrateLegacyConfig();
        SettingsService.EnsureAutoStartPath(); // 校正开机自启路径（程序移动/改名后 Run 键会失效）
        var coreResult = GameCpuService.Configure(SettingsService.Current.GameCoreCount, SettingsService.Current.GameCoreMode);
        if (!coreResult.Ok) LaunchService.Diag(coreResult.Message);

        // 记录掌机原始基础电源方案：标准“平衡”方案可能不存在，切回平衡时用它恢复
        PerformanceService.RememberBaseScheme();
        // 先校准当前供电的保存目标，避免登录任务恢复成关机前另一套功耗。
        PowerProfileService.SynchronizeSavedCpuTarget();
        PowerProfileWatcher.Start();
        // 睡眠/休眠唤醒后固件可能复位功耗限制：助手还连着时静默写回（与供电切换各自幂等）
        PowerResumeWatcher.Resumed += CpuPowerAutoApply.ReapplyIfActive;
        PowerResumeWatcher.Start();
        // 保存过功耗目标即视为已接管：恢复展示并触发登录任务静默写回，重启/关机/睡眠唤醒后都不用再点启用
        if (SettingsService.Current.CpuPowerPl1 > 0) CpuPowerService.ResumeSavedTakeover();

        // 接管系统屏幕键盘：关掉触屏键盘自动弹出、收起 osk/TabTip，改用本程序键盘
        SystemKeyboardWatcher.Apply(SettingsService.Current.TakeOverSystemKeyboard);
        Main = new MainWindow();
        // 启动后不显示启动器，避免透明覆盖层吃掉桌面右键；需要时再呼出。
        Main.HideToTray();

        // 底栏与系统顶栏都由「启用系统」控制，默认关闭。
        try
        {
            _ = DockHost.InitializeAsync();
        }
        catch (Exception ex) { DebugLog.Write("底栏初始化失败：" + ex); }

        try
        {
            _ = StatusBarHost.InitializeAsync();
        }
        catch (Exception ex) { DebugLog.Write("顶栏2 初始化失败：" + ex); }

        _tray = new TrayHotkeyService();
        // 事件在托盘 STA 线程触发，marshal 回 UI 线程
        _tray.ShowRequested += () => Main?.DispatcherQueue.TryEnqueue(Main.ShowFromTray);
        _tray.ExitRequested += () => Main?.DispatcherQueue.TryEnqueue(Main.ExitApp);

        // 启动后驻留托盘，不自动弹出启动器；需要时用手柄组合键/键盘/边缘/托盘呼出。
        // （此前原型阶段无条件 ShowFromTray，会留下一个覆盖桌面的透明窗口，导致桌面右键被吃掉。）

        // 启动时后台检查 GitHub 最新版本（关于页可关闭）；失败静默，不阻塞启动
        if (SettingsService.Current.AutoCheckUpdates)
            _ = UpdateService.AutoCheckAsync();
    }

    /// <summary>全局异常记录：写入 %APPDATA%\GameLocker\crash.log，便于定位闪退</summary>
    private void InstallCrashLogging()
    {
        UnhandledException += (_, e) =>
        {
            LogCrash("WinUI UnhandledException", e.Exception);
            e.Handled = true; // 记录后不闪退，便于连续复现
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        // 原生回调（窗口过程 / WinEvent）里抛出的互操作异常不会走到上面三个处理器就直接崩进程，
        // 这里只记录入口点/依赖库两类（0x80131523 / DllNotFound），作为闪退定位的最后依据
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is EntryPointNotFoundException or DllNotFoundException)
                LogCrash("FirstChance", e.Exception);
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLocker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch { }
    }

    /// <summary>旧名 GameLauncher 时期的配置迁移到新目录（仅当新目录尚无文件）</summary>
    private static void MigrateLegacyConfig()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var old = Path.Combine(appData, "GameLauncher");
        var @new = Path.Combine(appData, "GameLocker");
        if (!Directory.Exists(old)) return;
        Directory.CreateDirectory(@new);
        foreach (var file in new[] { "items.json", "settings.json" })
        {
            var src = Path.Combine(old, file);
            var dst = Path.Combine(@new, file);
            if (File.Exists(src) && !File.Exists(dst))
                File.Copy(src, dst);
        }
    }
}
