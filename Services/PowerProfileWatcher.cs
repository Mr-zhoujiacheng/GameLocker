namespace GameLocker.Services;

/// <summary>独立于页面生命周期的供电校准：事件触发，低频轮询补漏，串行应用且合并重复通知。</summary>
internal static class PowerProfileWatcher
{
    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static bool? _lastApplied;
    private static long _lastAppliedAt;
    private static bool _pending;
    private static bool _running;
    private static bool _stopped;
    private static long _due;
    private static string _reason = "";
    public static event Action? Applied;

    public static void Start()
    {
        lock (Gate)
        {
            if (_timer is not null) return;
            _stopped = false;
            PowerResumeWatcher.Resumed += OnResume;
            PowerResumeWatcher.StateChanged += OnStateChanged;
            _timer = new System.Threading.Timer(_ => Check(), null, 1000, 3000);
        }
        Request("启动", 2500);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
            PowerResumeWatcher.Resumed -= OnResume;
            PowerResumeWatcher.StateChanged -= OnStateChanged;
        }
    }

    private static void OnResume() => Request("睡眠/休眠恢复", 3000);
    private static void OnStateChanged(string reason) => Request(reason, 1500);

    private static void Request(string reason, int delay)
    {
        lock (Gate)
        {
            if (_stopped) return;
            // 恢复、亮屏、解锁常在一秒内连续到达，合成一次应用。
            _pending = true;
            _reason = reason;
            _due = Environment.TickCount64 + delay;
        }
    }

    private static void Check()
    {
        lock (Gate)
        {
            if (_stopped || _running) return;
            var onAc = PowerProfileService.ReadOnAc();
            if (onAc is null) return;
            if (!_pending && _lastApplied != onAc)
            {
                _pending = true;
                _reason = "供电轮询校准";
                _due = Environment.TickCount64 + 1000;
            }
            if (!_pending || Environment.TickCount64 < _due) return;
            _pending = false;
            // 同一供电方式刚应用过：亮屏/解锁/唤醒常连发多次，5 秒内不重复写硬件。
            if (_lastApplied == onAc && Environment.TickCount64 - _lastAppliedAt < 5000) return;
            _running = true;
            _ = ApplyAsync(onAc.Value, _reason);
        }
    }

    private static async Task ApplyAsync(bool onAc, string reason)
    {
        try
        {
            DebugLog.Write($"[供电校准] {reason}：当前{(onAc ? "插电" : "电池")}");
            var result = await PowerProfileService.ApplyActiveAsync().ConfigureAwait(false);
            lock (Gate) { _lastApplied = onAc; _lastAppliedAt = Environment.TickCount64; }
            DebugLog.Write($"[供电校准] 完成：{result}");
            // 应用期间发生插拔电，下次轮询会立即安排当前正确的一套。
            Applied?.Invoke();
        }
        catch (Exception ex)
        {
            DebugLog.Write("[供电校准] 失败：" + ex.Message);
            Request("失败重试", 10000);
        }
        finally { lock (Gate) _running = false; }
    }
}
