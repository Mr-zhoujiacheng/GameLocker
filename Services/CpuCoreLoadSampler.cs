using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameLocker.Services;

/// <summary>
/// Samples the mean busy-time fraction of the selected logical processors using one PDH query.
/// Call <see cref="TryRead"/> periodically (typically about once per second); the first successful
/// collection only establishes a baseline and returns false. No background work is started.
/// </summary>
public sealed class CpuCoreLoadSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;

    private readonly object _gate = new();
    private SafePdhQueryHandle? _query;
    private IntPtr[] _counters = Array.Empty<IntPtr>();
    private bool _primed;
    private bool _disposed;

    /// <summary>
    /// Creates one English-named counter per distinct (processor group, group-relative logical
    /// processor index). Empty/null inputs, unavailable counters, and initialization failures leave
    /// the sampler unavailable: <see cref="TryRead"/> returns false until it is replaced.
    /// </summary>
    public CpuCoreLoadSampler(IEnumerable<(ushort Group, byte Logical)> processors)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || processors is null) return;

            var selected = new HashSet<(ushort Group, byte Logical)>(processors);
            if (selected.Count == 0) return;

            uint status = PdhOpenQueryW(null, UIntPtr.Zero, out var query);
            _query = query;
            if (status != 0 || query is null || query.IsInvalid)
            {
                ReleaseQuery();
                return;
            }

            var counters = new List<IntPtr>(selected.Count);
            foreach (var processor in selected)
            {
                string path = string.Format(CultureInfo.InvariantCulture,
                    @"\Processor Information({0},{1})\% Processor Time", processor.Group, processor.Logical);
                status = PdhAddEnglishCounterW(query, path, UIntPtr.Zero, out var counter);
                if (status != 0 || counter == IntPtr.Zero)
                {
                    // Closing the query also releases every counter already added to it.
                    ReleaseQuery();
                    return;
                }
                counters.Add(counter);
            }

            _counters = counters.ToArray();
        }
        catch (Exception)
        {
            // Missing PDH exports, provider failures, and failed input enumeration are unavailable
            // samples, not reasons to interrupt the caller's monitoring loop.
            ReleaseQuery();
        }
    }

    /// <summary>
    /// Collects all selected processors together and returns their arithmetic mean in [0, 1].
    /// False means warm-up, disposal, or an unavailable/invalid sample; utilization is then zero.
    /// Every counter must be valid so an unavailable processor cannot silently bias the mean.
    /// Calls and disposal are serialized. Sampling too frequently can produce invalid PDH data.
    /// </summary>
    public bool TryRead(out double utilization)
    {
        utilization = 0;
        lock (_gate)
        {
            if (_disposed || _query is null || _query.IsClosed || _query.IsInvalid || _counters.Length == 0)
                return false;

            try
            {
                if (PdhCollectQueryData(_query) != 0)
                {
                    _primed = false;
                    return false;
                }

                if (!_primed)
                {
                    _primed = true;
                    return false;
                }

                double sum = 0;
                foreach (var counter in _counters)
                {
                    uint status = PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var value);
                    if (status != 0
                        || (value.CStatus != PdhCstatusValidData && value.CStatus != PdhCstatusNewData)
                        || !double.IsFinite(value.DoubleValue))
                    {
                        _primed = false;
                        return false;
                    }

                    // Busy-time percentages can slightly overshoot their nominal range.
                    sum += Math.Clamp(value.DoubleValue, 0.0, 100.0);
                }

                utilization = Math.Clamp(sum / _counters.Length / 100.0, 0.0, 1.0);
                return true;
            }
            catch (Exception)
            {
                _primed = false;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseQuery();
        }
    }

    private void ReleaseQuery()
    {
        _primed = false;
        _counters = Array.Empty<IntPtr>();
        _query?.Dispose();
        _query = null;
    }

    // PDH_FMT_COUNTERVALUE contains a DWORD followed by an 8-byte-aligned union on Windows x64.
    // A sequential DWORD/double declaration with the wrong packing reads garbage as the value.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    private sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePdhQueryHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            try { return PdhCloseQuery(handle) == 0; }
            catch (Exception) { return false; }
        }
    }

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out SafePdhQueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(SafePdhQueryHandle query, string fullCounterPath,
        UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(SafePdhQueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
        out uint counterType, out PdhFormattedCounterValue value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
