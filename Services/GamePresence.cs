using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 当前是否"在游戏里"：前台是全屏应用，或前台就是最近在玩的游戏（窗口化的游戏也算）。
/// 顶栏 / 底栏在游戏里默认让路——顶栏隐藏、底栏收起，交给边缘手势临时呼出。
/// 每 800ms 由 MainWindow 的巡检查一次，只在状态翻转时通知。
/// </summary>
internal static class GamePresence
{
    private static bool _inGame;

    public static bool InGame => _inGame;

    /// <summary>进出游戏（UI 线程回调）。</summary>
    public static event Action<bool>? Changed;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static void Refresh()
    {
        bool inGame;
        try
        {
            inGame = FullscreenDetector.IsForegroundFullscreen() || ForegroundIsTrackedGame();
        }
        catch
        {
            inGame = false;
        }

        if (inGame == _inGame) return;
        _inGame = inGame;
        DebugLog.Write("[游戏态] " + (inGame ? "进入游戏：顶栏/底栏让路" : "离开游戏：恢复显示"));
        try { Changed?.Invoke(inGame); }
        catch (Exception ex) { DebugLog.Write("[游戏态] 通知失败：" + ex.Message); }
    }

    /// <summary>前台进程就是最近记录的游戏（含窗口化运行），且不是本程序自己。</summary>
    private static bool ForegroundIsTrackedGame()
    {
        var exe = TrainerService.LastExeName;
        if (string.IsNullOrEmpty(exe)) return false;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
