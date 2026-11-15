namespace GameLocker.Services;

/// <summary>1 秒采样：优先核连续两次达到 90% 时扩展；总负载可由优先核以 65% 容量承载时连续五次收回。</summary>
internal sealed class CorePriorityPolicy
{
    public bool Expanded { get; private set; } = true;
    private int _busySamples, _quietSamples, _heldSamples;
    private bool _initialized;
    private int _expansionHold = 10;
    private double _busyBaseline;

    public bool Update(double? preferredLoad, double? totalLoad, int preferredThreads, int totalThreads)
    {
        var before = Expanded;
        if (preferredLoad is not double preferred || totalLoad is not double total
            || !double.IsFinite(preferred) || !double.IsFinite(total)
            || preferredThreads <= 0 || totalThreads < preferredThreads)
        {
            Expanded = true; // 无法可靠观测时不把进程困在一类核心中。
            _busySamples = _quietSamples = _heldSamples = 0;
            _initialized = false;
            return Expanded != before;
        }
        if (!_initialized)
        {
            _initialized = true;
            Expanded = preferred >= 0.90;
            _busySamples = _quietSamples = _heldSamples = 0;
            return Expanded != before;
        }
        _heldSamples++;
        if (!Expanded)
        {
            _busySamples = preferred >= 0.90 ? _busySamples + 1 : 0;
            if (_busySamples >= 2)
            {
                Expanded = true;
                // 收回后很快再次饱和，延长下一次开放期，避免不同核心性能差造成周期性收放。
                _expansionHold = _heldSamples < 10 ? Math.Min(120, _expansionHold * 2) : 10;
                _busyBaseline = 0;
                _busySamples = _quietSamples = _heldSamples = 0;
            }
        }
        else
        {
            // 用所有核心的忙碌时间估计收回后的压力，避免任务刚迁走、优先核读数降低就反复收放。
            var projected = total * totalThreads / preferredThreads;
            if (_heldSamples >= 3) _busyBaseline = Math.Max(_busyBaseline, total);
            // 扩展后等待迁移稳定，并要求总忙碌量较扩展期下降至少25%，再尝试收回。
            var workDropped = _busyBaseline <= 0.01 || total <= _busyBaseline * 0.75;
            _quietSamples = projected <= 0.65 && preferred <= 0.65 && workDropped ? _quietSamples + 1 : 0;
            if (_heldSamples >= _expansionHold && _quietSamples >= 5)
            {
                Expanded = false;
                _busySamples = _quietSamples = _heldSamples = 0;
            }
        }
        return Expanded != before;
    }
}
