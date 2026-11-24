using System.Globalization;
using System.Runtime.InteropServices;

namespace GameLocker.Services;

internal enum OptiCaptureHostFocus { Target, Overlay, Other }

/// <summary>Snapshot written once per second by OptiCaptureHost v2.10.80.</summary>
internal readonly record struct OptiCaptureHostStatus(
    long Stamp, OptiCaptureHostFocus Focus, double CaptureFps, double PresentFps, double DxgiFps)
{
    internal const string FileName = "OptiCaptureHost.status";
    internal const long MaxAgeMs = 2500;
    private const int MaxStatusBytes = 4096;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern long GetTickCount64();

    internal static OptiCaptureHostStatus? Read(string directory, int pid, long launchedAt)
    {
        try
        {
            // The host publishes by replacing the file, so allow replacement while reading.
            using var file = new FileStream(Path.Combine(directory, FileName), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > MaxStatusBytes) return null;
            using var text = new StreamReader(file);
            return Parse(text.ReadToEnd(), pid, launchedAt, GetTickCount64());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static OptiCaptureHostStatus? Parse(string text, int pid, long launchedAt, long now)
    {
        long? stamp = null;
        int? processId = null, focus = null;
        double? capture = null, present = null, dxgi = null;
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var value = line.AsSpan(separator + 1);
            switch (line.AsSpan(0, separator))
            {
                case "stamp":
                    if (stamp.HasValue || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tick)) return null;
                    stamp = tick;
                    break;
                case "pid":
                    if (processId.HasValue || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid)) return null;
                    processId = parsedPid;
                    break;
                case "focus":
                    if (focus.HasValue || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedFocus) || parsedFocus > 2) return null;
                    focus = parsedFocus;
                    break;
                case "capture":
                    if (!ReadRate(value, ref capture)) return null;
                    break;
                case "present":
                    if (!ReadRate(value, ref present)) return null;
                    break;
                case "dxgi":
                    if (!ReadRate(value, ref dxgi)) return null;
                    break;
            }
        }
        if (pid <= 0 || processId != pid || stamp is not long tickCount || tickCount <= launchedAt ||
            tickCount > now || now - tickCount > MaxAgeMs || focus is not int focusValue ||
            capture is not double captureFps || present is not double presentFps || dxgi is not double dxgiFps)
            return null;
        return new(tickCount, (OptiCaptureHostFocus)focusValue, captureFps, presentFps, dxgiFps);
    }

    private static bool ReadRate(ReadOnlySpan<char> value, ref double? destination)
    {
        if (destination.HasValue || !double.TryParse(value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var fps) || !double.IsFinite(fps) || fps < 0) return false;
        destination = fps;
        return true;
    }
}

/// <summary>Conservative indication from live FPS, not a substitute for game A/B validation.</summary>
internal sealed class OptiCaptureHostFrameEvidence
{
    private long _lastStamp;
    private int _consecutive;

    // Create a new tracker per host process; pass null when the current snapshot is unavailable.
    internal bool Observe(OptiCaptureHostStatus? status)
    {
        if (status is not { } sample)
        {
            _consecutive = 0;
            return false;
        }
        if (sample.Stamp < _lastStamp) return false;
        if (sample.Stamp == _lastStamp) return _consecutive >= 3;
        if (sample.Stamp - _lastStamp > OptiCaptureHostStatus.MaxAgeMs) _consecutive = 0;
        _lastStamp = sample.Stamp;
        if (sample.Focus != OptiCaptureHostFocus.Target || sample.CaptureFps < 15 ||
            sample.PresentFps < 15 || sample.DxgiFps < Math.Max(sample.CaptureFps, sample.PresentFps) * 1.25)
        {
            _consecutive = 0;
            return false;
        }
        if (_consecutive < 3) _consecutive++;
        return _consecutive >= 3;
    }
}
