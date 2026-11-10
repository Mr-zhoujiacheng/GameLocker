using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GameLocker.Services;

internal static class CpuPowerService
{
    public sealed record Limit(string Name, double Watts, double Minimum, double Maximum, bool Writable);
    public sealed record State(bool Supported, bool Enabled, string Message, Limit[] Limits);
    public sealed record Result(bool Ok, string Message);

    private sealed record Request(string Command, int Index = 0, double Watts = 0);
    private sealed record Response(State? State, Result Result);
    private const int TimeoutMilliseconds = 8000;
    private const int MaximumLineLength = 65536;
    private const string DisabledMessage = "点击设备功耗启用并授权";
    private const string LogonTaskName = "GameLocker CPU Power AutoApply";
    private static readonly object Gate = new();
    private static NamedPipeServerStream? _pipe;
    private static StreamReader? _reader;
    private static Stream? _writer;
    private static Process? _helper;
    private static readonly object CacheGate = new();
    private static State _cached = new(false, false, DisabledMessage, []);

    /// <summary>上一次读取并缓存的 CPU 功耗状态（线程安全；磁贴绘制只读缓存，避免反复走命名管道）</summary>
    public static State Cached
    {
        get { lock (CacheGate) return _cached; }
    }

    /// <summary>当前是否已连上提权助手（连接存活时才能直接写回硬件）</summary>
    public static bool Active
    {
        get { lock (Gate) return _pipe is not null; }
    }

    private static void SetCached(State state)
    {
        lock (CacheGate) _cached = state;
    }

    /// <summary>后台刷新缓存（内部 Task.Run，不阻塞 UI 线程）</summary>
    public static async Task<State> RefreshCacheAsync()
    {
        var state = await Task.Run(Read).ConfigureAwait(false);
        SetCached(state);
        return state;
    }

    public static State Read()
    {
        lock (Gate)
        {
            if (_pipe is null)
            {
                var disabled = new State(false, false, DisabledMessage, []);
                SetCached(disabled);
                return disabled;
            }
            try
            {
                var response = Exchange(new("read"));
                var state = response.State ?? new State(false, true, response.Result.Message, []);
                SetCached(state);
                return state;
            }
            catch (Exception ex)
            {
                CloseSession();
                var failed = new State(false, false, "读取 CPU 功耗失败：" + ErrorMessage(ex), []);
                SetCached(failed);
                return failed;
            }
        }
    }

    public static Result Enable()
    {
        lock (Gate)
        {
            if (_pipe is not null) return new(true, "CPU 功耗助手已连接");
            if (_helper is { HasExited: false }) return new(false, "此前功耗助手尚在恢复 / 退出，暂不能重新接管");
            _helper?.Dispose();
            _helper = null;
            try
            {
                string executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法确定当前程序路径");
                string pipeName = "GameLocker.CpuPower." + Guid.NewGuid().ToString("N");
                _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                _helper = Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--cpu-power-helper " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                        + " " + pipeName,
                    WorkingDirectory = AppContext.BaseDirectory
                }) ?? throw new InvalidOperationException("无法启动 CPU 功耗助手");
                using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
                _pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint clientPid))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (clientPid != (uint)_helper.Id)
                    throw new IOException("CPU 功耗管道连接的不是已授权助手");
                _reader = CreateReader(_pipe);
                _writer = _pipe;
                var response = Exchange(new("read"));
                if (!response.Result.Ok)
                {
                    CloseSession();
                    return response.Result;
                }
                if (response.State is null) throw new IOException("CPU 功耗助手未返回设备状态");
                SetCached(response.State);
                return new(response.State.Supported, response.State.Message);
            }
            catch (Exception ex)
            {
                CloseSession();
                if (ex is Win32Exception { NativeErrorCode: 1223 })
                    return new(false, "已取消管理员授权，CPU 功耗未启用");
                return new(false, "启用 CPU 功耗失败：" + ErrorMessage(ex));
            }
        }
    }

    public static Result Apply(int index, double watts)
    {
        lock (Gate)
        {
            if (_pipe is null) return new(false, DisabledMessage);
            if (index < 0 || !double.IsFinite(watts)) return new(false, "功耗索引或瓦数无效");
            try { return Exchange(new("set", index, watts)).Result; }
            catch (Exception ex)
            {
                CloseSession();
                return new(false, "设置 CPU 功耗失败，结果未确认，助手将尝试恢复：" + ErrorMessage(ex));
            }
        }
    }

    /// <summary>注册/更新登录时静默写回已保存功耗的计划任务（助手此时已是管理员权限，无需再次 UAC）</summary>
    public static Result EnsureAutoApplyTask()
    {
        lock (Gate)
        {
            if (_pipe is null) return new(false, DisabledMessage);
            try { return Exchange(new("task")).Result; }
            catch (Exception ex)
            {
                CloseSession();
                return new(false, "注册开机自动恢复失败：" + ErrorMessage(ex));
            }
        }
    }

    public static Result Disable()
    {
        lock (Gate)
        {
            if (_pipe is null) return new(true, "CPU 功耗未启用");
            try
            {
                var result = Exchange(new("restore")).Result;
                if (result.Ok) CloseSession();
                return result;
            }
            catch (Exception ex)
            {
                CloseSession();
                return new(false, "恢复 CPU 功耗未获确认：" + ErrorMessage(ex));
            }
        }
    }

    /// <summary>程序启动时触发登录计划任务，把已保存的功耗立即写回硬件（任务本身是管理员权限，不弹 UAC）。
    /// 正常情况下登录任务已经写过一次，这里只是兜底重开程序后的场景。</summary>
    public static void TriggerLogonTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", "/Run /TN \"" + LogonTaskName + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex) { DebugLog.Write("[功耗] 触发登录写回任务失败：" + ErrorMessage(ex)); }
    }

    /// <summary>退出程序：有已保存的功耗目标时保留硬件设置（否则关掉程序就恢复默认），
    /// 没有保存目标时按原有安全语义恢复接管前的值。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_pipe is null) return;
            try { Exchange(new(SettingsService.Current.CpuPowerPl1 > 0 ? "keep" : "restore")); }
            catch (Exception) { /* 管道已断：助手会自行恢复 */ }
            finally { CloseSession(); }
        }
    }

    private static Response Exchange(Request request)
    {
        using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
        WriteLineAsync(_writer!, request, timeout.Token).GetAwaiter().GetResult();
        string line = ReadLineAsync(_reader!, timeout.Token).GetAwaiter().GetResult();
        var response = JsonSerializer.Deserialize<Response>(line)
            ?? throw new IOException("CPU 功耗助手返回了空响应");
        if (response.Result is null) throw new IOException("CPU 功耗助手响应格式无效");
        return response;
    }

    private static void CloseSession()
    {
        // Closing the pipe, rather than killing the helper, lets its finally restore the hardware.
        _pipe?.Dispose();
        _pipe = null;
        _reader?.Dispose();
        _reader = null;
        _writer?.Dispose();
        _writer = null;
        // Keep the process handle so a new session cannot race the old helper's restoration.
        if (_helper is not null && _helper.WaitForExit(2000))
        {
            _helper.Dispose();
            _helper = null;
        }
    }

    public static void RunHelper(int parentPid, string pipeName)
        => RunHelperAsync(parentPid, pipeName).GetAwaiter().GetResult();

    private static async Task RunHelperAsync(int parentPid, string pipeName)
    {
        if (parentPid <= 0 || parentPid == Environment.ProcessId
            || !pipeName.StartsWith("GameLocker.CpuPower.", StringComparison.Ordinal)
            || !Guid.TryParseExact(pipeName["GameLocker.CpuPower.".Length..], "N", out _)) return;

        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        Process? parent = null;
        CpuPowerBackend? backend = null;
        StreamReader? reader = null;
        Stream? writer = null;
        Task? watch = null;
        bool restoreRequested = false;
        bool keepOnExit = false;
        try
        {
            parent = Process.GetProcessById(parentPid);
            // Retain the original process handle: a subsequently reused PID must not keep us alive.
            _ = parent.Handle;
            using (var self = Process.GetCurrentProcess())
            {
                if (parent.HasExited || parent.StartTime > self.StartTime)
                    throw new IOException("CPU 功耗助手的父进程已退出或无效");
            }
            watch = WatchParentAsync(parent, lifetime);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connect.CancelAfter(TimeoutMilliseconds);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (serverPid != (uint)parentPid) throw new IOException("CPU 功耗管道不属于父进程");
            reader = CreateReader(pipe);
            writer = pipe;
            backend = new CpuPowerBackend();
            while (!lifetime.IsCancellationRequested)
            {
                // Idle sessions stay connected; once a line starts it must complete within eight seconds.
                string line = await ReadLineAsync(reader, lifetime.Token, allowIdle: true).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                Response response;
                try
                {
                    var request = JsonSerializer.Deserialize<Request>(line)
                        ?? throw new IOException("CPU 功耗请求为空");
                    switch (request.Command)
                    {
                        case "read":
                            response = new(backend.Read(), new(true, "已读取 CPU 功耗"));
                            break;
                        case "set":
                            if (request.Index < 0 || !double.IsFinite(request.Watts))
                                throw new ArgumentException("功耗索引或瓦数无效");
                            response = new(null, backend.Apply(request.Index, request.Watts));
                            break;
                        case "task":
                            response = new(null, InstallLogonTask());
                            break;
                        case "keep":
                            // 用户已保存功耗目标：退出时保留硬件设置，不恢复接管前的值。
                            keepOnExit = true;
                            response = new(null, new(true, "已保留功耗设置"));
                            break;
                        case "restore":
                            var restoreResult = backend.Restore();
                            if (restoreResult.Ok)
                            {
                                restoreRequested = true;
                                return;
                            }
                            response = new(null, restoreResult);
                            break;
                        default:
                            throw new IOException("不支持的 CPU 功耗命令");
                    }
                }
                catch (Exception ex) { response = new(null, new(false, ErrorMessage(ex))); }
                await SendResponseAsync(writer, response, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (writer is not null && pipe.IsConnected && !lifetime.IsCancellationRequested)
            {
                try { await SendResponseAsync(writer, new(null, new(false, ErrorMessage(ex))), lifetime.Token).ConfigureAwait(false); }
                catch (Exception) { /* A closed pipe has no remaining recipient. */ }
            }
        }
        finally
        {
            Result restored = new(false, "CPU 功耗后端未初始化");
            if (backend is not null)
            {
                if (keepOnExit)
                {
                    restored = new(true, "已保留功耗设置");
                    try { backend.Dispose(); }
                    catch (Exception ex) { restored = new(false, "释放后端失败：" + ErrorMessage(ex)); }
                }
                else
                {
                    try { restored = backend.Restore(); }
                    catch (Exception ex) { restored = new(false, "恢复 CPU 功耗失败：" + ErrorMessage(ex)); }
                    finally
                    {
                        try { backend.Dispose(); }
                        catch (Exception ex) { restored = new(false, restored.Message + "；释放后端失败：" + ErrorMessage(ex)); }
                    }
                }
            }
            if (restoreRequested && writer is not null && pipe.IsConnected)
            {
                try { await SendResponseAsync(writer, new(null, restored), lifetime.Token).ConfigureAwait(false); }
                catch (Exception) { /* Restore has completed even if its acknowledgement cannot be delivered. */ }
            }
            lifetime.Cancel();
            if (watch is not null) await watch.ConfigureAwait(false);
            reader?.Dispose();
            writer?.Dispose();
            parent?.Dispose();
        }
    }

    /// <summary>以当前管理员助手身份注册“登录时静默写回功耗”的计划任务，之后开机不再弹 UAC。</summary>
    private static Result InstallLogonTask()
    {
        try
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前程序路径");
            string arguments = "/Create /F /SC ONLOGON /RL HIGHEST /DELAY 0000:15 /TN \""
                + LogonTaskName + "\" /TR \"\\\"" + executable + "\\\" --apply-cpu-power\"";
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("无法启动 schtasks");
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch { }
                return new(false, "注册开机自动恢复任务超时");
            }
            return process.ExitCode == 0
                ? new(true, "已注册登录时静默恢复功耗")
                : new(false, "注册开机自动恢复任务失败：" + output.Trim());
        }
        catch (Exception ex) { return new(false, "注册开机自动恢复任务失败：" + ErrorMessage(ex)); }
    }

    private static async Task WatchParentAsync(Process parent, CancellationTokenSource lifetime)
    {
        try
        {
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Cancel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { lifetime.Cancel(); }
    }

    private static StreamReader CreateReader(Stream stream)
        => new(stream, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);

    private static async Task WriteLineAsync<T>(Stream writer, T value, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(value);
        if (json.Length > MaximumLineLength) throw new IOException("CPU 功耗消息过长");
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await writer.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task SendResponseAsync(Stream writer, Response response, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeoutMilliseconds);
        await WriteLineAsync(writer, response, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken token, bool allowIdle = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!allowIdle) timeout.CancelAfter(TimeoutMilliseconds);
        var line = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(character.AsMemory(), timeout.Token).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("CPU 功耗管道已断开");
            if (allowIdle && line.Length == 0) timeout.CancelAfter(TimeoutMilliseconds);
            if (character[0] == '\n') return line.ToString();
            if (line.Length >= MaximumLineLength) throw new IOException("CPU 功耗消息过长");
            line.Append(character[0]);
        }
    }

    private static string ErrorMessage(Exception ex)
        => ex is OperationCanceledException ? "CPU 功耗通信超时或会话已结束" : ex.Message;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
