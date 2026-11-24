using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>Controls one OptiCaptureHost launched from an explicitly prepared runtime directory.</summary>
internal sealed class OptiCaptureHostService
{
    private const string ExeName = "OptiCaptureHost.exe";
    private const string OverlayClass = "OptiCaptureHostOverlay";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _host;
    private string? _directory;
    private long _launchedAt;

    internal async Task<OptiCaptureHostStatus> StartAsync(string directory, nint target, CancellationToken cancellationToken = default,
        OptiCaptureProfile? profile = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasRunningHost()) throw new InvalidOperationException("An OptiCaptureHost process is already running.");
            directory = Path.GetFullPath(directory);
            ValidateRuntime(directory);
            if (target == 0 || !IsWindow(target) || !IsWindowVisible(target) || IsIconic(target))
                throw new ArgumentException("A visible, non-minimized target window is required.", nameof(target));

            var info = new ProcessStartInfo(Path.Combine(directory, ExeName))
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("--hwnd");
            info.ArgumentList.Add(unchecked((ulong)target.ToInt64()).ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            cancellationToken.ThrowIfCancellationRequested();
            profile?.ValidateDependencies(directory);
            profile?.Apply(directory);
            cancellationToken.ThrowIfCancellationRequested();
            _launchedAt = OptiCaptureHostStatus.GetTickCount64();
            var host = Process.Start(info) ?? throw new InvalidOperationException("Unable to start OptiCaptureHost.");
            _host = host;
            _directory = directory;
            try
            {
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < ReadyTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (host.HasExited)
                        throw new InvalidOperationException($"OptiCaptureHost exited with code {host.ExitCode}; see OptiCaptureHost.log.");
                    if (OptiCaptureHostStatus.Read(directory, host.Id, _launchedAt) is { } status)
                        return status;
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                throw new TimeoutException("OptiCaptureHost did not publish a fresh status; see OptiCaptureHost.log.");
            }
            catch
            {
                await StopOwnedProcessAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<(int ProcessId, OptiCaptureHostStatus? Status)> ReadSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasRunningHost()) return default;
            var pid = _host!.Id;
            return (pid, OptiCaptureHostStatus.Read(_directory!, pid, _launchedAt));
        }
        finally { _gate.Release(); }
    }

    internal async Task<OptiCaptureHostStatus?> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        (await ReadSessionAsync(cancellationToken).ConfigureAwait(false)).Status;

    // To interrupt an in-flight start, cancel its token before requesting a stop.
    internal async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopOwnedProcessAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // Known base files for the validated XeFG profile, not a redistribution list.
    private static readonly string[] RequiredFiles = [ExeName, "dxgi.dll", "OptiCaptureHost.ini", "OptiScaler.ini",
        Path.Combine("Optiscaler", "libxess_fg.dll"), Path.Combine("Optiscaler", "libxell.dll")];

    internal static bool IsRuntimePresent(string directory) =>
        RequiredFiles.All(relative => File.Exists(Path.Combine(directory, relative)));

    internal static void ValidateRuntime(string directory)
    {
        foreach (var relative in RequiredFiles)
        {
            var path = Path.Combine(directory, relative);
            if (!File.Exists(path)) throw new FileNotFoundException($"OptiCaptureHost runtime is missing {relative}.", path);
        }
    }

    private bool HasRunningHost()
    {
        if (_host is null) return false;
        if (!_host.HasExited) return true;
        _host.Dispose();
        _host = null;
        _directory = null;
        return false;
    }

    private async Task StopOwnedProcessAsync()
    {
        if (!HasRunningHost()) return;
        var host = _host!;
        var postedClose = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != (uint)host.Id || host.HasExited) return true;
            var name = new StringBuilder(64);
            if (GetClassNameW(window, name, name.Capacity) == 0 || name.ToString() != OverlayClass) return true;
            postedClose = PostMessageW(window, 0x0010, 0, 0);
            return false;
        }, 0);
        if ((!postedClose || !await ExitsWithinAsync(host).ConfigureAwait(false)) && !host.HasExited)
        {
            try { host.Kill(); }
            catch (InvalidOperationException) when (host.HasExited) { }
            catch (Win32Exception) when (host.HasExited) { }
            if (!await ExitsWithinAsync(host).ConfigureAwait(false))
                throw new TimeoutException("The owned OptiCaptureHost process did not exit.");
        }
        HasRunningHost();
    }

    private static async Task<bool> ExitsWithinAsync(Process host)
    {
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await host.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return host.HasExited; }
    }

    private delegate bool EnumWindowsCallback(nint window, nint state);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint state);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsIconic(nint window);
}
