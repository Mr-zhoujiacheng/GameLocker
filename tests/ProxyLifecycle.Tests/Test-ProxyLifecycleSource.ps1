$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot '..\..\Services\ProxyService.cs')
$checks = [ordered]@{}
$checks['shutdown entry point is internal and asynchronous'] = $source -match 'internal static async Task ShutdownAsync\(\)'
$checks['proxy snapshot includes original and last-written values'] = $source -match 'ProxySettingsSnapshot\(ProxyValueSnapshot ProxyEnable, ProxyValueSnapshot ProxyServer,'
$checks['stale snapshots compare original and written values'] = $source -match 'IsKnownSnapshotValue\(currentEnable, saved\.ProxyEnable, saved\.WrittenEnable\)'
$checks['external edits skip restoration of the entire pair'] = $source -match 'if \(externalChange\)\s*\{[^}]*ClearProxySnapshot\(\);\s*return true;'
$checks['known partial writes restore only owned values'] = $source -match 'if \(enableIsWritten && !enableIsOriginal\)' -and $source -match 'if \(serverIsWritten && !serverIsOriginal\)'
$checks['legacy fallback only protects GameLocker proxy port'] = $source -match 'private static bool IsGameLockerProxy' -and $source -match 'if \(port == 7897\) return true'
$checks['shutdown is serialized with proxy operations'] = $source -match 'OperationGate\.WaitAsync' -and $source -match 'OperationGate\.Release\(\)'
$checks['exit cancels current proxy operation'] = $source -match 'OperationCancellation\.Cancel\(\)'
$checks['restore notifies WinINet'] = $source -match 'NotifyProxyChanged\s*\('
$checks['kernel stop honors process ownership'] = $source -match 'Interlocked\.Exchange\(ref _ownsCore, 0\) == 0'
$checks['temporary probe kernel is stopped'] = $source -match 'if \(!wasRunning\) KillCore\(\)'
$checks['legacy disconnect disables only a verified GameLocker loopback proxy'] = $source -match 'if \(running && proxyOn\) AdoptCoreForDisconnect\(port\)' -and $source -match 'HasExpectedLoopbackProxy\(ProxyValueSnapshot enabled, ProxyValueSnapshot server, int port\)' -and $source -match 'FindPrivateCoreProcess\(\)' -and $source -match 'Interlocked\.Exchange\(ref _ownsCore, 1\)' -and $source -match 'key\.SetValue\("ProxyEnable", 0, RegistryValueKind\.DWord\)' -and $source -match 'DisableLegacyProxyAndAdoptCore\(port\)'
$failed = @()
foreach ($check in $checks.GetEnumerator()) {
    if ($check.Value) { Write-Output ('PASS: ' + $check.Key) }
    else { Write-Output ('FAIL: ' + $check.Key); $failed += $check.Key }
}
if ($failed.Count) { exit 1 }
