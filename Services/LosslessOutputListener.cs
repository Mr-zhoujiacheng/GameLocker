using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GameLocker.Services;

internal enum FrameCaptureState { NoTarget, WaitingForData, CaptureError, ValidData }
internal enum FrameCaptureReason { NoTarget, Starting, AwaitingHeader, NoTargetRecords, InvalidRecords, UnsupportedPresentMode, MultipleChains, InsufficientSamples, InsufficientDuration, ProcessExited, CollectorError, Valid, HelperUnavailable }
internal sealed record FrameCaptureDiagnostic(FrameCaptureState State, FrameCaptureReason Reason,
    int TargetPid, int OutputPid, string Columns, IReadOnlyDictionary<string, long> FrameTypes,
    IReadOnlyDictionary<string, long> Rejections, long ValidRecords, int ChainCount, int SampleCount,
    string Chains, string? Error, bool ProcessExited, int? ExitCode);

/// <summary>监听目标进程的实际显示时长；Lossless Scaling 仅接受独立显示链。</summary>
internal sealed class LosslessOutputListener : IDisposable
{
    private const int MaxSamples = 2000;
    private static readonly string Executable = Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");
    private readonly string _sessionName = $"GameLocker-Lossless-{Environment.ProcessId}";
    private readonly object _gate = new();
    private readonly Dictionary<(int Pid, ulong Chain), Queue<Sample>> _chains = new();
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _startAttempted;
    private bool _ownsSession;
    private bool _disposed;
    private int[]? _columns;
    private string? _lastError;

    private string _columnNames = "";
    private readonly Dictionary<string, long> _frameTypes = new();
    private readonly Dictionary<string, long> _rejections = new();
    private long _validRecords;
    private long _lastDiagnosticTick;
    private bool _outputEnded;
    private bool _readFailed;
    private FrameCaptureReason _lastRejection = FrameCaptureReason.NoTargetRecords;
    internal static string DiagnosticPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLocker", Environment.GetCommandLineArgs().Contains("--monitor-helper") ? "monitor-frames-helper.log" : "monitor-frames-local.log");

    private void Reject(string reason, FrameCaptureReason status = FrameCaptureReason.InvalidRecords)
    {
        _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;
        _lastRejection = status;
    }

    internal FrameCaptureDiagnostic GetDiagnostic()
    {
        lock (_gate)
        {
            Prune(Stopwatch.GetTimestamp());
            var exited = _process?.HasExited ?? false;
            var chains = _chains.Where(pair => pair.Key.Pid == _outputPid).ToArray();
            var samples = chains.Sum(pair => pair.Value.Count);
            var state = FrameCaptureState.WaitingForData;
            var reason = FrameCaptureReason.NoTargetRecords;
            if (_targetPid <= 0 || _outputPid <= 0) { state = FrameCaptureState.NoTarget; reason = FrameCaptureReason.NoTarget; }
            else if (exited || _outputEnded) { state = FrameCaptureState.CaptureError; reason = FrameCaptureReason.ProcessExited; }
            else if (_readFailed || (_startAttempted && _process == null) || (_columns == null && _columnNames.Length > 0))
            { state = FrameCaptureState.CaptureError; reason = FrameCaptureReason.CollectorError; }
            else if (!_startAttempted) reason = FrameCaptureReason.Starting;
            else if (_columns == null) reason = FrameCaptureReason.AwaitingHeader;
            else if (chains.Length > 1) reason = FrameCaptureReason.MultipleChains;
            else if (samples == 0) reason = _lastRejection;
            else if (samples < 10) reason = FrameCaptureReason.InsufficientSamples;
            else if (chains[0].Value.Sum(sample => sample.Interval) < 500) reason = FrameCaptureReason.InsufficientDuration;
            else { state = FrameCaptureState.ValidData; reason = FrameCaptureReason.Valid; }
            return new(state, reason, _targetPid, _outputPid, _columnNames,
                new Dictionary<string, long>(_frameTypes), new Dictionary<string, long>(_rejections),
                _validRecords, chains.Length, samples,
                string.Join(";", _chains.Select(pair => $"{pair.Key.Pid}:0x{pair.Key.Chain:X}={pair.Value.Count}")),
                _lastError, exited, exited ? _process!.ExitCode : null);
        }
    }

    // Called by both sampling hosts, never by the per-frame CSV reader.
    internal FrameCaptureDiagnostic SampleDiagnostic()
    {
        var snapshot = GetDiagnostic();
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastDiagnosticTick != 0 && now - _lastDiagnosticTick < 5000) return snapshot;
            _lastDiagnosticTick = now;
        }
        try
        {
            var path = DiagnosticPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = $"[{DateTime.Now:O}] host={Environment.ProcessId} state={snapshot.State}/{snapshot.Reason} target={snapshot.TargetPid} output={snapshot.OutputPid} columns={snapshot.Columns} types={string.Join(';', snapshot.FrameTypes.Select(p => $"{p.Key}={p.Value}"))} rejected={string.Join(';', snapshot.Rejections.Select(p => $"{p.Key}={p.Value}"))} accepted={snapshot.ValidRecords} chains={snapshot.Chains} outputChains={snapshot.ChainCount} samples={snapshot.SampleCount} exited={snapshot.ProcessExited} exitCode={snapshot.ExitCode} error={snapshot.Error}\n";
            if (text.Length > 16384) text = text[..16384] + "\n";
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text) > 512 * 1024)
                File.WriteAllText(path, "");
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch { }
        return snapshot;
    }

    private int _targetPid;
    private int _outputPid;

    private readonly record struct Sample(long ReceivedAt, double Interval, bool Generated);

    // 捕获会话只启动一次；前台切换只更新内存过滤条件，不能携带上个游戏的样本。
    internal void SetTargets(int targetPid, int outputPid)
    {
        lock (_gate)
        {
            if (_targetPid == targetPid && _outputPid == outputPid) return;
            _targetPid = targetPid;
            _outputPid = outputPid;
            _chains.Clear();
            _frameTypes.Clear();
            _rejections.Clear();
            _validRecords = 0;
            _lastRejection = FrameCaptureReason.NoTargetRecords;
        }
    }

    internal bool HasGeneratedFrames(int pid)
    {
        lock (_gate)
        {
            if (_disposed || pid <= 0) return false;
            Prune(Stopwatch.GetTimestamp());
            return _chains.Any(pair => pair.Key.Pid == pid && pair.Value.Any(sample => sample.Generated));
        }
    }

    internal string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    // 构造不启动；即使子进程退出，重复调用也不会重新启动采集。
    internal bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_startAttempted) return _process != null && !_process.HasExited;
            _startAttempted = true;
            var process = new Process
            {
                StartInfo = CreateStartInfo(
                    "--output_stdout", "--no_console_stats", "--v2_metrics", "--track_frame_type", "--exclude_dropped",
                    "--no_track_input", "--no_track_gpu", "--session_name", _sessionName,
                    "--stop_existing_session")
            };
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    _lastError = "PresentMon 未能启动。";
                    return false;
                }
                _process = process;
                _ownsSession = true;
                _stdoutTask = Task.Run(() => ReadOutputAsync(process.StandardOutput));
                _stderrTask = Task.Run(() => ReadErrorsAsync(process.StandardError));
                return !process.HasExited;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                if (_process == null) process.Dispose();
                return false;
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private async Task ReadOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                ConsumeLine(line);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_gate)
                if (!_disposed) { _lastError = ex.Message; _readFailed = true; }
        }
        finally
        {
            lock (_gate) { _chains.Clear(); _outputEnded = true; }
        }
    }

    private async Task ReadErrorsAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (_gate)
                    if (!_disposed) _lastError = line.Length > 2048 ? line[..2048] : line;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_gate)
                if (!_disposed) { _lastError = ex.Message; _readFailed = true; }
        }
    }

    internal void ConsumeLine(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = Stopwatch.GetTimestamp();
            Prune(now);
            var fields = SplitCsv(line);
            if (fields == null || fields.Count == 0) { Reject("MalformedCsv"); return; }
            if (fields[0].Equals("Application", StringComparison.OrdinalIgnoreCase))
            {
                _columnNames = string.Join(',', fields);
                if (_columnNames.Length > 4096) _columnNames = _columnNames[..4096];
                string[] names = { "Application", "ProcessID", "SwapChainAddress", "FrameType",
                    "DisplayedTime", "PresentMode" };
                var columns = new int[names.Length];
                for (var i = 0; i < names.Length; i++)
                {
                    columns[i] = fields.FindIndex(value => value.Equals(names[i], StringComparison.OrdinalIgnoreCase));
                    if (columns[i] < 0)
                    {
                        _columns = null;
                        _chains.Clear();
                        _lastError = $"PresentMon CSV 缺少列：{names[i]}";
                        return;
                    }
                }
                _columns = columns;
                _chains.Clear();
                return;
            }
            if (_columns == null) { Reject("NoHeader"); return; }
            if (_columns.Any(column => column >= fields.Count)) { Reject("ShortRow"); return; }
            var c = _columns;
            if (!int.TryParse(fields[c[1]], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            { Reject("InvalidPid"); return; }
            if (pid != _targetPid && pid != _outputPid)
            {
                _rejections["OtherPid"] = _rejections.GetValueOrDefault("OtherPid") + 1;
                return;
            }
            var frameType = fields[c[3]];
            if (frameType.Length > 128) frameType = frameType[..128];
            if (!_frameTypes.ContainsKey(frameType) && _frameTypes.Count >= 32) frameType = "(other)";
            _frameTypes[frameType] = _frameTypes.GetValueOrDefault(frameType) + 1;
            if (!TryParseChain(fields[c[2]], out var chain)) { Reject("InvalidChain"); return; }
            if (!double.TryParse(fields[c[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out var interval)
                || !double.IsFinite(interval) || interval <= 0)
            { Reject("InvalidDisplayedTime"); return; }
            var mode = fields[c[5]];
            var lossless = pid == _outputPid && (pid != _targetPid
                || fields[c[0]].Equals("LosslessScaling.exe", StringComparison.OrdinalIgnoreCase));
            if (lossless && !mode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("Hardware: Legacy Flip", StringComparison.OrdinalIgnoreCase))
            { Reject("UnsupportedPresentMode", FrameCaptureReason.UnsupportedPresentMode); return; }
            _validRecords++;

            var key = (pid, chain);
            if (!_chains.TryGetValue(key, out var samples))
                _chains[key] = samples = new Queue<Sample>();
            // v2.6 CsvOutput.cpp 的 FrameTypeToString；Unknown/Application 不推测为生成帧。
            var generated = fields[c[3]].Equals("Intel XeSS-FG", StringComparison.OrdinalIgnoreCase)
                || fields[c[3]].Equals("AMD AFMF", StringComparison.OrdinalIgnoreCase);
            samples.Enqueue(new Sample(now, interval, generated));
            while (samples.Count > MaxSamples) samples.Dequeue();
        }
    }

    internal double GetFps(int pid)
    {
        lock (_gate)
        {
            if (_disposed) return double.NaN;
            Prune(Stopwatch.GetTimestamp());
            Queue<Sample>? onlyChain = null;
            foreach (var pair in _chains)
            {
                if (pair.Key.Pid != pid) continue;
                // 即使第二条链的样本不足，也不能猜测哪一条才是真正输出。
                if (onlyChain != null) return double.NaN;
                onlyChain = pair.Value;
            }
            if (onlyChain == null || onlyChain.Count < 10) return double.NaN;
            // ETW 会批量投递；覆盖时长取显示间隔，不按管道回调到达跨度判断。
            if (onlyChain.Sum(sample => sample.Interval) < 500) return double.NaN;
            var average = onlyChain.Average(sample => sample.Interval);
            var fps = 1000.0 / average;
            return double.IsFinite(fps) ? fps : double.NaN;
        }
    }

    private void Prune(long now)
    {
        List<(int Pid, ulong Chain)>? expired = null;
        foreach (var pair in _chains)
        {
            var samples = pair.Value;
            while (samples.Count > 0 && now - samples.Peek().ReceivedAt > Stopwatch.Frequency)
                samples.Dequeue();
            if (samples.Count == 0) (expired ??= new()).Add(pair.Key);
        }
        if (expired != null)
            foreach (var key in expired) _chains.Remove(key);
    }

    private static bool TryParseChain(string text, out ulong chain)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return ulong.TryParse(hex ? text[2..] : text,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out chain);
    }

    // PresentMon 每条记录占一行；支持引号字段、字段内逗号以及双引号转义。
    private static List<string>? SplitCsv(string line)
    {
        var result = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(field.ToString().Trim().TrimStart('\uFEFF'));
                field.Clear();
            }
            else field.Append(ch);
        }
        if (quoted) return null;
        result.Add(field.ToString().Trim().TrimStart('\uFEFF'));
        return result;
    }

    public void Dispose()
    {
        Process? process;
        Task[] readers;
        bool ownsSession;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
            _process = null;
            ownsSession = _ownsSession;
            _ownsSession = false;
            readers = new[] { _stdoutTask, _stderrTask }.OfType<Task>().ToArray();
            _chains.Clear();
        }
        _stop.Cancel();
        if (process != null)
        {
            StopProcess(process);
            process.Dispose();
        }
        try { Task.WaitAll(readers, 1000); }
        catch (AggregateException) { }
        // 不等待仍可能读取 Token 的任务结束就释放 CTS 会产生竞态。
        if (readers.All(task => task.IsCompleted)) _stop.Dispose();
        else _ = Task.WhenAll(readers).ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        if (ownsSession) TerminateSession();
    }

    private void TerminateSession()
    {
        try
        {
            using var terminator = new Process
            {
                StartInfo = CreateStartInfo("--session_name", _sessionName, "--terminate_existing_session")
            };
            if (!terminator.Start()) return;
            // 同时排空两个管道，避免退出命令被重定向缓冲区阻塞。
            var stdout = terminator.StandardOutput.ReadToEndAsync();
            var stderr = terminator.StandardError.ReadToEndAsync();
            if (!terminator.WaitForExit(1500)) StopProcess(terminator);
            _ = Task.WhenAll(stdout, stderr).ContinueWith(task => { _ = task.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = ex.Message;
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit(1500);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
