using System.ComponentModel;
using System.Diagnostics;
using GameLocker.Models;

namespace GameLocker.Services;

internal sealed class OptiCaptureBusyException : InvalidOperationException;

public static partial class LosslessScalingService
{
    public const string EngineOptiScaler = "OPTISCALER";
    private static readonly OptiCaptureHostService OptiHost = new();
    private static readonly object OptiStartLock = new();
    private static CancellationTokenSource? _optiStart;
    private static Task? _optiStartFinished;
    private static bool _optiExiting;
    private static OptiSession? _optiSession;

    internal static string NormalizeFrameGenEngine(string? engine) => engine?.Trim().ToUpperInvariant() switch
    {
        EngineOptiScaler or EngineXess or EngineFsr3 => EngineOptiScaler,
        _ => EngineLsfg
    };

    internal static bool UsesOptiScaler(LaunchItem item) => NormalizeFrameGenEngine(item.FrameGenEngine) == EngineOptiScaler;
    internal static OptiCaptureHostUpdateService OptiCaptureUpdates { get; } = new();
    internal static bool IsOptiScalerInstalled => OptiCaptureUpdates.GetInstalledDirectory() is not null;
    private static bool _optiInstalling;

    internal static async Task InstallOptiCaptureAsync(OptiCaptureHostRelease release, Action<long, long?> onProgress,
        CancellationToken cancellationToken)
    {
        lock (OptiStartLock)
        {
            if (_optiExiting || _optiInstalling || _optiStart is not null || IsOptiScalerRunning)
                throw new OptiCaptureBusyException();
            _optiInstalling = true;
        }
        try { await OptiCaptureUpdates.InstallAsync(release, onProgress, cancellationToken).ConfigureAwait(false); }
        finally { lock (OptiStartLock) _optiInstalling = false; }
    }
    internal static bool IsOptiScalerRunning => Volatile.Read(ref _optiSession) is not null;
    internal static bool IsOptiScalerScaling => ReadOptiScalerTelemetry()?.Confirmed == true;
    internal static string OptiFailureReason { get; private set; } = "";
    internal static bool IsOptiScalerStarting
    {
        get { lock (OptiStartLock) return _optiStart is not null; }
    }

    internal static void CancelOptiScalerStart()
    {
        lock (OptiStartLock) _optiStart?.Cancel();
    }

    /// <summary>退出时取消启动并等待清理本服务持有的捕获宿主；不触碰游戏或其他进程。</summary>
    internal static async Task<bool> StopOptiScalerForExitAsync()
    {
        Task? startup;
        lock (OptiStartLock)
        {
            _optiExiting = true;
            _optiStart?.Cancel();
            startup = _optiStartFinished;
        }
        try
        {
            if (startup is not null) await startup.ConfigureAwait(false);
            await OptiHost.StopAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _optiSession, null);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiCaptureHost exit cleanup: {ex}");
            return false;
        }
    }

    internal sealed record OptiTelemetry(int HostPid, int GamePid, OptiCaptureHostStatus? Status, bool Confirmed);

    private sealed class OptiSession(OptiTelemetry telemetry, bool usesFrameGeneration)
    {
        internal OptiTelemetry Telemetry = telemetry;
        internal readonly bool UsesFrameGeneration = usesFrameGeneration;
        internal readonly OptiCaptureHostFrameEvidence Evidence = new();
    }

    internal static OptiTelemetry? ActiveOptiSession
    {
        get
        {
            var session = Volatile.Read(ref _optiSession);
            return session is null ? null : Volatile.Read(ref session.Telemetry);
        }
    }

    internal static OptiTelemetry? ReadOptiScalerTelemetry()
    {
        if (ActiveOptiSession is not { } value) return null;
        return value.Status is { } status && OptiCaptureHostStatus.GetTickCount64() - status.Stamp <= OptiCaptureHostStatus.MaxAgeMs
            ? value : value with { Status = null, Confirmed = false };
    }

    private static async Task<ScaleResult> ActivateOptiScalerAsync(LaunchItem item)
    {
        OptiFailureReason = "";
        if (IsOptiScalerRunning)
        {
            try
            {
                await OptiHost.StopAsync();
                Interlocked.Exchange(ref _optiSession, null);
                return ScaleResult.FrameGenOff;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"OptiCaptureHost stop: {ex}");
                return ScaleResult.ToggleFailed;
            }
        }
        if (IsLsfgScaling) return ScaleResult.BusyWithOtherEngine;
        var profile = new OptiCaptureProfile(item.OptiCaptureFrameGeneration, item.OptiCaptureUpscaler,
            item.OptiCaptureVSync, item.OptiCaptureFpsOverlay, item.OptiCaptureMenuScale, item.OptiCaptureSharpness);

        var restoreLauncher = false;
        using var start = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (OptiStartLock)
        {
            if (_optiExiting) return ScaleResult.FrameGenOff;
            if (_optiInstalling || _optiStart is not null)
            {
                OptiFailureReason = "组件正在启动或更新，请稍后再试。";
                return ScaleResult.LaunchFailed;
            }
            _optiStart = start;
            _optiStartFinished = finished.Task;
        }
        try
        {
            var directory = OptiCaptureUpdates.GetInstalledDirectory();
            if (directory is null)
            {
                OptiFailureReason = "请到小黄鸭的 OptiScaler 设置中检查并安装组件。";
                return ScaleResult.NotInstalled;
            }
            ForegroundWatcherService.RefreshFrameGenTarget();
            var target = ForegroundWatcherService.FrameGenTargetWindow;
            if (target == 0 || !IsWindow(target) || !IsWindowVisible(target) || IsIconic(target)) return ScaleResult.NoGame;
            GetWindowThreadProcessId(target, out var targetPid);
            try
            {
                using var game = Process.GetProcessById((int)targetPid);
                if (targetPid == Environment.ProcessId || !TrainerService.IsGameLikeProcess(target, game.ProcessName))
                    return ScaleResult.NoGame;
            }
            catch (ArgumentException) { return ScaleResult.NoGame; }

            restoreLauncher = App.Main?.IsShown == true;
            App.Main?.HideToTray();
            await Task.Delay(350, start.Token);
            for (var attempt = 0; attempt < 2 && GetForegroundWindow() != target; attempt++)
            {
                BringToForeground(target);
                await Task.Delay(150, start.Token);
            }
            if (GetForegroundWindow() != target) return ScaleResult.FocusFailed;

            try { OptiCaptureMenuLocalization.Apply(directory, item.OptiCaptureMenuLanguage); }
            catch (Exception ex) { DebugLog.Write($"OptiScaler menu language: {ex}"); }
            await OptiHost.StartAsync(directory, target, start.Token, profile);
            var (hostPid, status) = await OptiHost.ReadSessionAsync(start.Token);
            if (hostPid == 0) throw new InvalidOperationException("宿主在初始化后退出。");
            var session = new OptiSession(new(hostPid, (int)targetPid, status, false), profile.UsesFrameGeneration);
            lock (OptiStartLock)
            {
                start.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref _optiSession, session);
            }
            var confirmed = session.UsesFrameGeneration && session.Evidence.Observe(status);
            var initialWait = Stopwatch.StartNew();
            while (session.UsesFrameGeneration && !confirmed && initialWait.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(300, start.Token);
                if (!await ObserveOptiSessionAsync(session, start.Token))
                    throw new InvalidOperationException("宿主在初始化后退出。");
                confirmed = session.Telemetry.Confirmed;
            }

            restoreLauncher = false;
            _ = Task.Run(() => WatchOptiSessionAsync(session));
            return confirmed ? ScaleResult.FrameGenOn : ScaleResult.HostRunning;
        }
        catch (FileNotFoundException ex)
        {
            OptiFailureReason = $"缺少 {Path.GetFileName(ex.FileName)}";
            return ScaleResult.NotInstalled;
        }
        catch (OperationCanceledException) when (start.IsCancellationRequested)
        {
            return await CleanFailedOptiStartAsync() ? ScaleResult.FrameGenOff : ScaleResult.ToggleFailed;
        }
        catch (Exception ex)
        {
            OptiFailureReason = ex switch
            {
                TimeoutException => "等待宿主就绪超时，请查看 OptiCaptureHost.log。",
                Win32Exception => "无法启动宿主，请检查运行库和权限。",
                _ => "宿主未能启动，请查看 OptiCaptureHost.log。"
            };
            DebugLog.Write($"OptiCaptureHost start: {ex}");
            await CleanFailedOptiStartAsync();
            return ScaleResult.LaunchFailed;
        }
        finally
        {
            try
            {
                bool showLauncher;
                lock (OptiStartLock) showLauncher = restoreLauncher && !_optiExiting;
                if (showLauncher) App.Main?.ShowFromTray();
            }
            finally
            {
                lock (OptiStartLock)
                {
                    _optiStart = null;
                    _optiStartFinished = null;
                    finished.TrySetResult();
                }
            }
        }
    }

    private static async Task<bool> CleanFailedOptiStartAsync()
    {
        try
        {
            await OptiHost.StopAsync();
            Interlocked.Exchange(ref _optiSession, null);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"OptiCaptureHost cleanup: {ex}");
            return false;
        }
    }

    private static async Task<bool> ObserveOptiSessionAsync(OptiSession session, CancellationToken cancellationToken = default)
    {
        var previous = Volatile.Read(ref session.Telemetry);
        var (pid, status) = await OptiHost.ReadSessionAsync(cancellationToken).ConfigureAwait(false);
        if (pid == 0 || pid != previous.HostPid)
        {
            Interlocked.CompareExchange(ref _optiSession, null, session);
            return false;
        }
        var confirmed = session.UsesFrameGeneration && session.Evidence.Observe(status);
        Volatile.Write(ref session.Telemetry, previous with { Status = status, Confirmed = confirmed });
        return true;
    }

    private static async Task WatchOptiSessionAsync(OptiSession session)
    {
        while (ReferenceEquals(Volatile.Read(ref _optiSession), session))
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (!ReferenceEquals(Volatile.Read(ref _optiSession), session)) return;
            try
            {
                if (!await ObserveOptiSessionAsync(session).ConfigureAwait(false)) return;
            }
            catch (Exception ex) { DebugLog.Write($"OptiCaptureHost status: {ex.Message}"); }
        }
    }
}
