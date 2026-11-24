[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OriginalDxgi,
    [Parameter(Mandatory)][string]$LocalizedDxgi,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath
$original = (Resolve-Path -LiteralPath $OriginalDxgi).ProviderPath
$localized = (Resolve-Path -LiteralPath $LocalizedDxgi).ProviderPath
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new smoke-test output directory.' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = & $vswhere -latest -version '[17.0,18.0)' -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' |
    Select-Object -First 1
if (!$vcvars -or !(Test-Path -LiteralPath $vcvars)) { throw 'Visual Studio 2022 C++ Build Tools are required.' }
$setup = & cmd.exe /d /c ('call "' + $vcvars + '" && set')
if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize MSVC environment.' }
foreach ($line in $setup) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}

function Get-DllExports([string]$Path) {
    $listing = & dumpbin.exe /nologo /exports $Path
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect PE exports for $Path" }
    foreach ($row in $listing) {
        if ($row -match '^\s*(\d+)\s+[\dA-F]+\s+[\dA-F]+\s+(\S+)') { "$($Matches[1]) $($Matches[2])" }
    }
}
$expected = @(Get-DllExports $original)
$actual = @(Get-DllExports $localized)
if ($expected.Count -eq 0 -or @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual).Count -ne 0) {
    throw 'Native export names or ordinals differ from the original DLL.'
}
Write-Host "PASS: $($expected.Count) exported names/ordinals match the original DLL"

$imgui = Join-Path $source 'OptiScaler\include\imgui'
$menu = Join-Path $source 'OptiScaler\menu'
$freetype = Join-Path $source 'external\freetype'
$probe = Join-Path $PSScriptRoot 'OptiScalerZhCN\Smoke.cpp'
$null = New-Item -ItemType Directory -Path $output
Push-Location $output
try {
    & cl.exe /nologo /std:c++20 /EHsc /O2 /MT /utf-8 "/I$imgui" "/I$menu" "/I$freetype" /Fe:Smoke.exe `
        $probe "$imgui\imgui.cpp" "$imgui\imgui_draw.cpp" "$imgui\imgui_tables.cpp" "$imgui\imgui_widgets.cpp" `
        "$imgui\misc\freetype\imgui_freetype.cpp" "$freetype\freetype.lib"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to build the native menu test.' }
    & .\Smoke.exe $localized
    if ($LASTEXITCODE -ne 0) { throw 'The native menu localization test failed.' }
} finally { Pop-Location }
