using System.Runtime.InteropServices;

namespace GameLocker.Services;

/// <summary>
/// 顶栏和底栏共享的前台游戏状态；窗口化游戏也可以触发。
/// </summary>
internal static class GamePresence
{
    private static readonly GamePresenceState State = new();
    private static volatile bool _inGame;

    public static bool InGame => _inGame;

    /// <summary>进出游戏（UI 线程回调）。</summary>
    public static event Action<bool>? Changed;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static void Refresh()
    {
        if (!SettingsService.Current.SystemEnabled) State.Clear();
        else
        {
            var foreground = GetForegroundWindow();
            var pid = WindowPid(foreground);
            var ownWindow = pid == (uint)Environment.ProcessId;
            var isGame = !ownWindow && GameWindowDetector.IsGameForSystemBars(foreground, pid);
            var previous = State.Game;
            var gameStillUsable = !isGame && WindowPid(previous.Window) == previous.Pid
                && GameWindowDetector.IsUsableWindow(previous.Window);
            State.Observe((foreground, pid), isGame, ownWindow, gameStillUsable, Environment.TickCount64);
        }
        var inGame = State.InGame;

        if (inGame == _inGame) return;
        _inGame = inGame;
        DebugLog.Write("[游戏态] " + (inGame ? "进入游戏：顶栏/底栏让路" : "离开游戏：恢复显示"));
        try { Changed?.Invoke(inGame); }
        catch (Exception ex) { DebugLog.Write("[游戏态] 通知失败：" + ex.Message); }
    }

    private static uint WindowPid(nint window)
    {
        return window != 0 && GetWindowThreadProcessId(window, out var pid) != 0 ? pid : 0;
    }
}
