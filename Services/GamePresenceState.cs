namespace GameLocker.Services;

internal sealed class GamePresenceState
{
    internal const int ExitDelayMs = 550;
    private long? _lostAt;
    private bool _otherAppSeen;

    internal (nint Window, uint Pid) Game { get; private set; }
    internal bool InGame => Game.Window != 0;

    internal void Observe((nint Window, uint Pid) foreground, bool isGame, bool ownWindow, bool gameStillUsable, long now)
    {
        if (isGame)
        {
            Game = foreground;
            _lostAt = null;
            _otherAppSeen = false;
            return;
        }

        if (!InGame || !gameStillUsable)
        {
            Clear();
            return;
        }

        // GameLocker may take focus over the game, but cannot revive a session after a real app switch.
        if (ownWindow && !_otherAppSeen)
        {
            _lostAt = null;
            return;
        }

        _otherAppSeen |= foreground.Window != 0 && !ownWindow;
        _lostAt ??= now;
        if (now - _lostAt.Value >= ExitDelayMs) Clear();
    }

    internal void Clear() => (Game, _lostAt, _otherAppSeen) = (default, null, false);
}
