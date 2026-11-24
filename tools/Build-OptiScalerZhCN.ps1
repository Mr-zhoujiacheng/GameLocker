[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OriginalDxgi,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$Python = 'python.exe',
    [string]$NotoSansSC
)

$ErrorActionPreference = 'Stop'
$sourceCommit = '956bb7198e819da857710f7f9ace223790a7ba55'
$sourceUrl = 'https://github.com/janblade/OptiScaler-F5-DLSSNR-Multipass.git'
$fontBase = 'https://raw.githubusercontent.com/google/fonts/2894aab31764f10f29c421bdfd2340d3b382d384/ofl/notosanssc'
$fontLicenseHash = '1C05C68C34F9708415AADA51F17E1B0092D2CEA709BF4A94CD38114F9E73D7D9'
$originalHash = '8570433BA0A5293298EBF43EEE494E64DAA283E9A2FD5A20B789CE6CFB27520D'
$kit = Join-Path $PSScriptRoot 'OptiScalerZhCN'

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command exited with $LASTEXITCODE" }
}

$original = Get-Item -LiteralPath $OriginalDxgi
if ($original.PSIsContainer -or (Get-FileHash -LiteralPath $original.FullName -Algorithm SHA256).Hash -ne $originalHash) {
    throw 'Original dxgi.dll does not match the supported OptiCaptureHost v2.10.80 build.'
}
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ((Test-Path -LiteralPath $work) -or (Test-Path -LiteralPath $output) -or
    $work.Equals($output, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory and OutputDirectory must be separate, new directories.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -version '[17.0,18.0)' -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' |
    Select-Object -First 1
if (!$msbuild -or !(Test-Path -LiteralPath $msbuild)) { throw 'Visual Studio 2022 C++ Build Tools are required.' }

$null = New-Item -ItemType Directory -Path $work
$source = Join-Path $work 'source'
Invoke-Checked git.exe @('-c','core.autocrlf=false','clone','--depth','1','--branch','v0.1.39','--recurse-submodules',
    '--shallow-submodules',$sourceUrl,$source)
$actualCommit = Invoke-Checked git.exe @('-C',$source,'rev-parse','HEAD')
$submodules = Invoke-Checked git.exe @('-C',$source,'submodule','status','--recursive')
if ($actualCommit -ne $sourceCommit -or ($submodules | Where-Object { !($_.StartsWith(' ')) })) {
    throw 'Unexpected upstream source or submodule revision.'
}

if ($NotoSansSC) { $fontPath = (Get-Item -LiteralPath $NotoSansSC).FullName }
else {
    $fontPath = Join-Path $work 'NotoSansSC-wght.ttf'
    Invoke-WebRequest -Uri "$fontBase/NotoSansSC%5Bwght%5D.ttf" -OutFile $fontPath
}
$fontLicense = Join-Path $work 'OFL-NotoSansSC.txt'
Invoke-WebRequest -Uri "$fontBase/OFL.txt" -OutFile $fontLicense
if ((Get-FileHash -LiteralPath $fontLicense -Algorithm SHA256).Hash -ne $fontLicenseHash) {
    throw 'Unexpected Noto Sans SC license hash.'
}
Invoke-Checked $Python @('-B',(Join-Path $kit 'generate.py'),$source,$fontPath)
Invoke-Checked $msbuild @((Join-Path $source 'OptiScaler.sln'),'/m:2','/p:Configuration=Release','/p:Platform=x64','/verbosity:minimal')

$localized = Join-Path $source 'x64\Release\a\OptiScaler.dll'
if (!(Test-Path -LiteralPath $localized -PathType Leaf)) { throw 'Missing native OptiScaler Release x64 output.' }
$hash = (Get-FileHash -LiteralPath $localized -Algorithm SHA256).Hash
if ($hash -eq $originalHash) { throw 'The generated DLL is identical to the English upstream DLL.' }
$publish = Join-Path $work 'artifact'
$null = New-Item -ItemType Directory -Path $publish
Copy-Item -LiteralPath $localized -Destination (Join-Path $publish 'dxgi.zh-CN.dll')
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $publish 'GPL-3.0-OptiScaler.txt')
Copy-Item -LiteralPath $fontLicense -Destination (Join-Path $publish 'OFL-NotoSansSC.txt')
Copy-Item -LiteralPath (Join-Path $kit 'NOTICE.md') -Destination $publish
$manifest = [ordered]@{
    schemaVersion = 1
    runtimeVersion = 'v2.10.80'
    sourceCommit = $sourceCommit
    officialDxgiSha256 = $originalHash
    localizedDxgiSha256 = $hash
}
[IO.File]::WriteAllText((Join-Path $publish 'OptiScaler.zh-CN.json'), ($manifest | ConvertTo-Json) + "`n")
[IO.Directory]::Move($publish, $output)
[PSCustomObject]@{ OutputDirectory = $output; LocalizedDxgiSha256 = $hash }
