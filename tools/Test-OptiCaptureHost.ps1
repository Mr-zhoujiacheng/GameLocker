[CmdletBinding(DefaultParameterSetName = 'Measure')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Measure')][int]$GameProcessId,
    [Parameter(Mandatory, ParameterSetName = 'Measure')][string]$OffDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Measure')][string]$OnDirectory,
    [Parameter(ParameterSetName = 'Measure')][ValidateRange(3,30)][int]$Samples = 8,
    [Parameter(ParameterSetName = 'Measure')][string]$ReportPath,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

function Convert-HostSample([string]$Text, [int]$HostId, [long]$Now, [long]$After, [ref]$LastFocus) {
    $values = ConvertFrom-StringData -StringData $Text
    [long]$stamp = 0
    if ($values.pid -ne [string]$HostId -or
        ![long]::TryParse($values.stamp, [ref]$stamp) -or
        $stamp -le $After -or $stamp -gt $Now -or ($Now - $stamp) -gt 2500) { return }
    if ($null -ne $LastFocus) { $LastFocus.Value = [string]$values.focus }
    if ($values.focus -ne '0') { return }

    $rates = @{}
    foreach ($key in 'capture','present','dxgi','refresh') {
        [double]$value = 0
        if (![double]::TryParse($values[$key], [Globalization.NumberStyles]::Float,
              [Globalization.CultureInfo]::InvariantCulture, [ref]$value) -or
              [double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -le 0) { return }
        $rates[$key] = $value
    }
    [pscustomobject]@{ HostProcessId = $HostId; Stamp = $stamp; Capture = $rates.capture;
        Present = $rates.present; Output = $rates.dxgi; Refresh = $rates.refresh }
}

function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $mid = [int][math]::Floor($ordered.Length / 2)
    if ($ordered.Length % 2) { return $ordered[$mid] }
    ($ordered[$mid - 1] + $ordered[$mid]) / 2
}

function Compare-HostRuns($Off, $On) {
    $offCapture = Get-Median $Off.Capture
    $onCapture = Get-Median $On.Capture
    $offOutput = Get-Median $Off.Output
    $onOutput = Get-Median $On.Output
    $offPresent = Get-Median $Off.Present
    $onPresent = Get-Median $On.Present
    $offRefresh = Get-Median $Off.Refresh
    $refresh = Get-Median $On.Refresh
    $reason = if ([math]::Abs($refresh - $offRefresh) -gt 1) {
        'Inconclusive: monitor refresh rate changed between runs.'
    } elseif ([math]::Min($offCapture, $onCapture) -lt 15 -or [math]::Abs($onCapture / $offCapture - 1) -gt 0.15) {
        'Inconclusive: capture FPS is too low or differs by more than 15% between runs.'
    } elseif ([math]::Max($offCapture, $onCapture) -ge 0.75 * $refresh) {
        'Inconclusive: lower the game FPS cap to leave display headroom.'
    } elseif ([math]::Abs($offOutput / $offCapture - 1) -gt 0.15 -or [math]::Abs($offOutput / $offPresent - 1) -gt 0.15) {
        'Inconclusive: OFF output is not close to both capture and host Present FPS.'
    } elseif ($onOutput -lt (1.25 * [math]::Max($offOutput, [math]::Max($onCapture, $onPresent)))) {
        'Not confirmed: ON output did not exceed OFF output, ON capture, and ON host Present by at least 25%.'
    }
    [pscustomobject]@{
        Confirmed = !$reason; Reason = $reason
        OffCapture = $offCapture; OffPresent = $offPresent; OffOutput = $offOutput
        OnCapture = $onCapture; OnPresent = $onPresent; OnOutput = $onOutput; Refresh = $refresh
    }
}

if ($SelfTest) {
    $status = "stamp=9000`npid=42`nfocus=0`ncapture=30.0`npresent=30.0`ndxgi=60.0`nrefresh=120.0"
    if (!(Convert-HostSample $status 42 10000 8000) -or
         (Convert-HostSample $status 17 10000 8000) -or
         (Convert-HostSample $status 42 12000 8000) -or
         (Convert-HostSample $status 42 10000 9000) -or
         (Convert-HostSample ($status.Replace('focus=0','focus=2')) 42 10000 8000) -or
         (Convert-HostSample ($status.Replace('dxgi=60.0','dxgi=0')) 42 10000 8000)) {
        throw 'Status sampling self-test failed.'
    }
    $focus = ''
    if ((Convert-HostSample ($status.Replace('focus=0','focus=2')) 42 10000 8000 ([ref]$focus)) -or $focus -ne '2') {
        throw 'Focus diagnostics self-test failed.'
    }
    $off = [pscustomobject]@{ Capture = @(30,30,31); Present = @(30,30,30); Output = @(29,30,30); Refresh = @(120,120,120) }
    $on = [pscustomobject]@{ Capture = @(29,30,30); Present = @(29,30,30); Output = @(59,60,61); Refresh = @(120,120,120) }
    $repeats = [pscustomobject]@{ Capture = $on.Capture; Present = $on.Output; Output = $on.Output; Refresh = $on.Refresh }
    $changedRefresh = [pscustomobject]@{ Capture = $on.Capture; Present = $on.Present; Output = $on.Output; Refresh = @(144,144,144) }
    if (!(Compare-HostRuns $off $on).Confirmed -or (Compare-HostRuns $off $off).Confirmed -or
        (Compare-HostRuns $on $on).Confirmed -or (Compare-HostRuns $off $repeats).Confirmed -or
        (Compare-HostRuns $off $changedRefresh).Confirmed -or (Get-Median @(10,20)) -ne 15) { throw 'A/B comparison self-test failed.' }
    Write-Host 'Status and A/B comparison self-tests passed.'
    return
}

if (-not ('OptiHostProbeWindows' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OptiHostProbeWindows {
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("kernel32.dll")] public static extern long GetTickCount64();
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static void CloseOverlay(int processId) {
        EnumWindows((window, state) => {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid != (uint)processId) return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != "OptiCaptureHostOverlay") return true;
            PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return false;
        }, IntPtr.Zero);
    }
}
"@
}

function Resolve-HostDirectory([string]$Directory) {
    $resolved = (Get-Item -LiteralPath $Directory).FullName
    foreach ($name in 'OptiCaptureHost.exe','dxgi.dll','OptiCaptureHost.ini','OptiScaler.ini') {
        if (!(Test-Path -LiteralPath (Join-Path $resolved $name) -PathType Leaf)) { throw "Missing $name in $resolved" }
    }
    $resolved
}

function Measure-HostRun([string]$Directory, [string]$Name, [Diagnostics.Process]$Game, [Collections.Generic.List[object]]$RunSamples) {
    $Game.Refresh()
    $window = $Game.MainWindowHandle
    if ($Game.HasExited -or $window -eq [IntPtr]::Zero) { throw 'The selected game window is not available.' }
    $statusPath = Join-Path $Directory 'OptiCaptureHost.status'
    $start = [OptiHostProbeWindows]::GetTickCount64()
    $last = $start
    $lastFocus = ''
    $logPath = Join-Path $Directory 'OptiCaptureHost.log'
    Write-Host "Taking $Name samples. Switch to the selected game and keep it in the foreground."
    $worker = Start-Process -FilePath (Join-Path $Directory 'OptiCaptureHost.exe') -WorkingDirectory $Directory `
        -ArgumentList @('--hwnd', $window.ToInt64().ToString([Globalization.CultureInfo]::InvariantCulture), '--parent', "$PID") -PassThru
    try {
        $taken = 0
        while ($taken -lt $Samples) {
            if ($worker.HasExited) { throw "$Name host exited ($($worker.ExitCode)). Check $logPath" }
            if ($Game.HasExited) { throw 'The selected game exited.' }
            if ([OptiHostProbeWindows]::GetTickCount64() - $start -gt (20000 + 2000 * $Samples)) {
                $detail = switch ($lastFocus) {
                    '0' { 'The last status had game focus, but too few valid FPS samples were collected.' }
                    '1' { 'The host overlay had focus. Return focus to the selected game.' }
                    '2' { 'The game was not in the foreground. Bring the selected game forward.' }
                    default { 'No fresh usable status was published for the current host.' }
                }
                throw "Timed out taking $Name samples ($taken/$Samples). $detail Check $logPath"
            }
            $text = $null
            try { $text = [IO.File]::ReadAllText($statusPath) } catch [IO.IOException] { }
            if ($text) {
                $sample = Convert-HostSample $text $worker.Id ([OptiHostProbeWindows]::GetTickCount64()) $last ([ref]$lastFocus)
                if ($null -ne $sample) {
                    $last = $sample.Stamp
                    $taken++
                    $sample | Add-Member -NotePropertyName Run -NotePropertyValue $Name
                    $RunSamples.Add($sample)
                }
            }
            Start-Sleep -Milliseconds 200
        }
    } finally {
        if (!$worker.HasExited) {
            [OptiHostProbeWindows]::CloseOverlay($worker.Id)
            if (!$worker.WaitForExit(5000)) {
                Write-Warning "$Name host did not close normally; terminating the probe's child process."
                $worker.Kill()
                if (!$worker.WaitForExit(5000)) { throw "$Name host did not exit after termination." }
            }
        }
        $worker.Dispose()
    }
}

$off = [Collections.Generic.List[object]]::new()
$on = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ Version = 1; GameProcessId = $GameProcessId; StartedUtc = [datetime]::UtcNow.ToString('o');
    RequestedSamples = $Samples; Result = $null; Failure = $null; Off = $null; On = $null }
$outputFile = $null
if ($ReportPath) {
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
    if ([IO.Path]::GetExtension($path) -ne '.json') { throw 'ReportPath must end in .json.' }
    $outputFile = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
}
try {
    $offPath = Resolve-HostDirectory $OffDirectory
    $onPath = Resolve-HostDirectory $OnDirectory
    if ($offPath -eq $onPath) { throw 'Use two separate directories with matched OFF and ON presets.' }
    $game = Get-Process -Id $GameProcessId
    Measure-HostRun $offPath 'OFF' $game $off
    Measure-HostRun $onPath 'ON' $game $on
    ($off.ToArray() + $on.ToArray()) | Format-Table Run,Capture,Present,Output,Refresh
    $report.Result = Compare-HostRuns ($off.ToArray()) ($on.ToArray())
    $report.Result | Format-List
} catch {
    $report.Failure = $_.Exception.Message
    throw
} finally {
    if ($null -ne $outputFile) {
        try {
            $report.Off = $off.ToArray()
            $report.On = $on.ToArray()
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 6))
            $outputFile.Write($bytes, 0, $bytes.Length)
        } finally { $outputFile.Dispose() }
    }
}
if (!$report.Result.Confirmed) { exit 2 }
