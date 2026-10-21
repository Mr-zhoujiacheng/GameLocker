using Microsoft.UI.Xaml;
using GameLocker.Services;

namespace GameLocker;

/// <summary>应用入口：创建主窗口并启动托盘/热键常驻</summary>
public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    private TrayHotkeyService? _tray;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 提权监控助手模式（--monitor-helper <父进程PID>）：只跑 ETW 帧率采集并写共享内存，
        // 不创建主窗口，父进程退出或发出停止事件后自行结束。
        var argv = Environment.GetCommandLineArgs();
        var helperIndex = Array.IndexOf(argv, "--monitor-helper");
        if (helperIndex >= 0 && helperIndex + 1 < argv.Length
            && int.TryParse(argv[helperIndex + 1], out var parentPid))
        {
            MonitorHelperHost.Run(parentPid);
            Environment.Exit(0);
        }

        InstallCrashLogging();
        MigrateLegacyConfig();
        Main = new MainWindow();

        _tray = new TrayHotkeyService();
        // 事件在托盘 STA 线程触发，marshal 回 UI 线程
        _tray.ShowRequested += () => Main?.DispatcherQueue.TryEnqueue(Main.ShowFromTray);
        _tray.ExitRequested += () => Main?.DispatcherQueue.TryEnqueue(Main.ExitApp);

        // 原型阶段首次启动直接显示，便于验证。
        // 用 ShowFromTray 而非裸 Activate：构造函数里 PrepareSummon 已把窗口移到屏外等滑入，
        // 若系统前台锁导致 Activated 不触发，裸 Activate 会让窗口永远停在屏外（前台无窗口）。
        // ShowFromTray 自带兜底：Activated 没来就直接播滑入动画。
        Main.ShowFromTray();
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
