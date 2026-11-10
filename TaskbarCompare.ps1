param(
    [string[]]$GameProcessName = @('deadcells'),
    [ValidateRange(5, 300)][int]$SampleSeconds = 30,
    [ValidateRange(10, 1800)][int]$WaitSeconds = 300
)

$ErrorActionPreference = 'Stop'
$gameNames = @($GameProcessName | ForEach-Object {
    [IO.Path]::GetFileNameWithoutExtension($_)
})

if (-not ('GameLockerTaskbarProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class GameLockerTaskbarProbe {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int z, uint f);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr h, uint command);
}
'@
}

function Get-ForegroundInfo {
    $hwnd = [GameLockerTaskbarProbe]::GetForegroundWindow()
    [uint32]$processId = 0
    [void][GameLockerTaskbarProbe]::GetWindowThreadProcessId($hwnd, [ref]$processId)
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    $title = New-Object Text.StringBuilder 256
    [void][GameLockerTaskbarProbe]::GetWindowText($hwnd, $title, 256)
    [pscustomobject]@{ Hwnd = $hwnd; ProcessName = $process.ProcessName; Title = $title.ToString() }
}

Write-Output ('WAIT games={0} timeout={1}s; switch to game, then summon GameLocker' -f ($gameNames -join ','), $WaitSeconds)
$wait = [Diagnostics.Stopwatch]::StartNew()
$triggered = $false
while ($wait.Elapsed.TotalSeconds -lt $WaitSeconds) {
    $foreground = Get-ForegroundInfo
    if ($gameNames -contains $foreground.ProcessName) {
        $triggered = $true
        Write-Output ('TRIGGER time={0:HH:mm:ss.fff} game={1} hwnd={2}' -f (Get-Date), $foreground.ProcessName, $foreground.Hwnd)
        break
    }
    Start-Sleep -Milliseconds 200
}
if (-not $triggered) {
    Write-Output 'TIMEOUT: no matching game foreground; no taskbar changes made'
    return
}

$bar = [GameLockerTaskbarProbe]::FindWindow('Shell_TrayWnd', $null)
if ($bar -eq [IntPtr]::Zero) { throw 'Shell_TrayWnd not found' }
$originalStyle = [GameLockerTaskbarProbe]::GetWindowLongPtr($bar, -20).ToInt64()
$changed = $false
$raisedInGame = 0
$raisedInLauncher = 0
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    while ($timer.Elapsed.TotalSeconds -lt $SampleSeconds) {
        if ([GameLockerTaskbarProbe]::FindWindow('Shell_TrayWnd', $null) -ne $bar) {
            Write-Output 'STOP: taskbar was recreated'
            break
        }
        $foreground = Get-ForegroundInfo
        $before = [GameLockerTaskbarProbe]::GetWindowLongPtr($bar, -20).ToInt64()
        $isGame = $gameNames -contains $foreground.ProcessName
        $isLauncher = $foreground.ProcessName -eq 'GameLocker'
        $action = 'OBSERVE'
        $result = '-'
        $errorCode = 0
        # Leave the first two seconds untouched for a baseline. Never raise over unrelated apps.
        if ($timer.Elapsed.TotalSeconds -ge 2 -and ($isGame -or $isLauncher)) {
            $action = 'RAISE'
            $changed = $true
            $result = [GameLockerTaskbarProbe]::SetWindowPos($bar, [IntPtr]::new(-1), 0, 0, 0, 0, 0x413)
            if (-not $result) { $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error() }
            if ($isGame) { $raisedInGame++ } else { $raisedInLauncher++ }
        }
        $after = [GameLockerTaskbarProbe]::GetWindowLongPtr($bar, -20).ToInt64()
        Write-Output ('{0} time={1:HH:mm:ss.fff} before={2:X} result={3} error={4} after={5:X} foreground={6} process={7} title={8}' -f $action, (Get-Date), $before, $result, $errorCode, $after, $foreground.Hwnd, $foreground.ProcessName, $foreground.Title)
        Start-Sleep -Seconds 1
    }
}
finally {
    if ($changed -and [GameLockerTaskbarProbe]::FindWindow('Shell_TrayWnd', $null) -eq $bar) {
        $wasTopmost = ($originalStyle -band 8) -ne 0
        $currentTopmost = ([GameLockerTaskbarProbe]::GetWindowLongPtr($bar, -20).ToInt64() -band 8) -ne 0
        $restoreResult = $true
        if ($wasTopmost -ne $currentTopmost) {
            $z = if ($wasTopmost) { [IntPtr]::new(-1) } else { [IntPtr]::new(-2) }
            $restoreResult = [GameLockerTaskbarProbe]::SetWindowPos($bar, $z, 0, 0, 0, 0, 0x413)
        }
        Write-Output ('RESTORE result={0} ex={1:X}' -f $restoreResult, [GameLockerTaskbarProbe]::GetWindowLongPtr($bar, -20).ToInt64())
    }
    Write-Output ('SUMMARY gameRaises={0} launcherRaises={1}' -f $raisedInGame, $raisedInLauncher)
}
