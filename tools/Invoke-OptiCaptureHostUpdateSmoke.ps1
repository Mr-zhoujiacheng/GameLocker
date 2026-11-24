#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OfficialArchive,
    [string]$DotNet = 'dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path ([IO.Path]::GetTempPath()) ('GameLocker-OptiCaptureUpdate-' + [guid]::NewGuid().ToString('N'))
$sdk = (Get-Command $DotNet -CommandType Application | Select-Object -First 1).Source
$services = Join-Path (Split-Path -Parent $PSScriptRoot) 'Services'
$archiveArgs = @()
if ($OfficialArchive) { $archiveArgs = @((Resolve-Path -LiteralPath $OfficialArchive).ProviderPath) }

try {
    & $sdk new console --framework net8.0 --name UpdateSmoke --output $project --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the update-test project.' }
    $csproj = Join-Path $project 'UpdateSmoke.csproj'
    & $sdk add $csproj package SharpCompress --version 0.39.0 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not add the existing SharpCompress dependency.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OptiCaptureHostUpdateSmoke.cs.txt') -Destination (Join-Path $project 'Program.cs') -Force
    foreach ($name in 'OptiCaptureHostUpdateService.cs','OptiCaptureHostPackage.cs','OptiCaptureHostService.cs','OptiCaptureHostStatus.cs','OptiCaptureProfile.cs') {
        Copy-Item -LiteralPath (Join-Path $services $name) -Destination $project
    }
    & $sdk run --project $csproj -p:NuGetAudit=false --verbosity quiet -- @archiveArgs
    if ($LASTEXITCODE -ne 0) { throw "Update tests failed with exit code $LASTEXITCODE." }
} finally {
    if (Test-Path -LiteralPath $project) {
        $resolved = (Resolve-Path -LiteralPath $project).ProviderPath
        if ($resolved -eq $project -and (Split-Path -Leaf $resolved) -match '^GameLocker-OptiCaptureUpdate-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
