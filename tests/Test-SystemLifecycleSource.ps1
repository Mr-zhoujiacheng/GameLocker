param([string] $Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

function Assert-SourcePattern {
    param([string] $Path, [string] $Pattern, [string] $Description)
    $text = Get-Content -LiteralPath (Join-Path $Root $Path) -Raw
    if ($text -cnotmatch "(?s)$Pattern") {
        throw "FAIL: $Description"
    }
    Write-Output "PASS: $Description"
}

Assert-SourcePattern 'App.xaml.cs' 'ProcessExit\s*\+=\s*\(.*?\)\s*=>\s*\{\s*if\s*\(!_ownsMainInstance\)\s*return;\s*try\s*\{\s*SystemKeyboardWatcher\.Apply\(false\)' 'process exit restores desktop state only for its owner'
Assert-SourcePattern 'App.xaml.cs' 'BeginDesktopSession\(\)\s*\{\s*if\s*\(!AcquireSingleInstance\(\)\).*?return false;\s*\}\s*TopTaskbarMode\.SetRestoreOwner\(true\);\s*_ownsMainInstance\s*=\s*true;' 'mutex acquisition precedes ownership'
Assert-SourcePattern 'App.xaml.cs' '"--restore-taskbar"\)\s*>=\s*0\)\s*\{\s*try\s*\{\s*if\s*\(AcquireSingleInstance\(\)\)\s*\{\s*TopTaskbarMode\.SetRestoreOwner\(true\);\s*TopTaskbarMode\.ForceRestore\(\)' 'explicit recovery shares the instance mutex'
Assert-SourcePattern 'App.xaml.cs' 'Main\.Closed\s*\+=\s*\(_,\s*_\)\s*=>\s*SystemKeyboardWatcher\.Apply\(false\)' 'closing the main window restores the keyboard setting'
Assert-SourcePattern 'Services/TaskbarControl.cs' 'ApplyRequestedMode\(\)\s*\{\s*if\s*\(_requestedMode\s*==\s*_applied\)\s*return;\s*_applied\s*=\s*-1;.*?tray\s*==\s*IntPtr\.Zero\)\s*\{[^}]*return;[^}]*\}.*?if\s*\(!SetState\(tray,\s*ABS_AUTOHIDE\)\s*\|\|\s*!HideTaskbars\(\).*?\)\s*return;.*?_applied\s*=\s*_requestedMode;' 'missing Explorer and failed hiding leave the request pending'
Assert-SourcePattern 'Services/TaskbarControl.cs' 'OnTaskbarRecreated\(\)\s*\{\s*lock\s*\(Gate\)\s*\{\s*if\s*\(!_shellHooked\)\s*return;\s*_applied\s*=\s*-1;\s*ApplyRequestedMode\(\)' 'Explorer recreation retries the latest taskbar request under the same lock'
Assert-SourcePattern 'Services/TrayHotkeyService.cs' '_taskbarCreatedMessage\s*=\s*ShellRestartWatcher\.RegisterTaskbarCreatedMessage\(\);.*?if\s*\(_taskbarCreatedMessage\s*!=\s*0\s*&&\s*msg\s*==\s*_taskbarCreatedMessage\)\s*\{\s*AddTrayIcon\(\)' 'tray icon is re-added on the Explorer broadcast'
Assert-SourcePattern 'Services/SystemKeyboardWatcher.cs' 'if\s*\(!enabled\)\s*\{\s*Stop\(\);\s*Restore\(\);\s*return;\s*\}.*?private static void Restore\(\)\s*\{\s*if\s*\(!_snapshotCaptured\)\s*return;' 'disabling the keyboard watcher without a snapshot does not write'
Assert-SourcePattern 'Services/SystemKeyboardWatcher.cs' 'GetValue\(AutoInvokeValue,\s*null,\s*RegistryValueOptions\.DoNotExpandEnvironmentNames\).*?_originalKind\s*=\s*key\.GetValueKind\(AutoInvokeValue\).*?if\s*\(_originalValue\s+is\s+null\)\s*\{[^}]*DeleteValue\(AutoInvokeValue,\s*throwOnMissingValue:\s*false\);\s*\}.*?key\.SetValue\(AutoInvokeValue,\s*_originalValue,\s*_originalKind\)' 'keyboard restoration preserves the value type and absence'
Assert-SourcePattern 'Services/SystemKeyboardWatcher.cs' 'Start\(\);\s*if\s*\(_hook\s*==\s*IntPtr\.Zero\).*?catch\s*\(Exception ex\)\s*\{\s*Stop\(\);\s*Restore\(\);' 'failed keyboard hook installation rolls the setting back'
Assert-SourcePattern 'Services/TopTaskbarMode.cs' 'private static void Disable\(bool force\)\s*\{\s*lock\s*\(Gate\)\s*\{\s*if\s*\(!_restoreOwner\)\s*return;.*?RestoreTaskbarSize\(\)' 'shared taskbar snapshot requires ownership before restoration'
Assert-SourcePattern 'MainWindow.xaml.cs' 'now\s*-\s*_lastStartMenuToggle\s*<\s*800' 'start menu touch toggles are debounced'
