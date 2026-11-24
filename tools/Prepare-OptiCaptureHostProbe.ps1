[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$RuntimeOnly,
    [switch]$CoreFilesOnly,
    [string]$OfficialScalerArchive
)

$ErrorActionPreference = 'Stop'
$expectedHash = 'E61853C6E9B577F87DFD505AE669656D241562FFCE93F37CAA4235203E878120'
$archive = Get-Item -LiteralPath $ArchivePath
if ($archive.PSIsContainer -or (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Expected the original OptiCaptureHost v2.10.80 Full Pack archive (SHA-256 mismatch).'
}
$tar = (Get-Command tar.exe -CommandType Application).Source
$scalerZipPath = $null
if ($OfficialScalerArchive) {
    if (!$CoreFilesOnly) { throw 'OfficialScalerArchive requires CoreFilesOnly.' }
    $scalerZip = Get-Item -LiteralPath $OfficialScalerArchive
    $expectedScalerHash = '532ADA6B80D1CAAAE222734E053F6FC992EAD5BBCD01ECAAC851F26E6FD66ECF'
    if ($scalerZip.PSIsContainer -or (Get-FileHash -LiteralPath $scalerZip.FullName -Algorithm SHA256).Hash -ne $expectedScalerHash) {
        throw 'Expected the official v0.1.39 OptiScaler archive (SHA-256 mismatch).'
    }
    $scalerZipPath = $scalerZip.FullName
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a new output directory: $output" }

function Set-RequiredIniValue([string]$Text, [string]$Section, [string]$Key, [string]$Value) {
    $sectionPattern = '(?m)^\[' + [regex]::Escape($Section) + '\][ \t]*\r?\n(?<body>(?:(?!^\[)[\s\S])*)'
    $sections = [regex]::Matches($Text, $sectionPattern)
    if ($sections.Count -ne 1) { throw "Expected exactly one [$Section] section." }
    $body = $sections[0].Groups['body']
    $keyPattern = [regex]::new('(?m)^(?<prefix>[ \t]*' + [regex]::Escape($Key) + '[ \t]*=[ \t]*)[^\r\n]*')
    if ($keyPattern.Matches($body.Value).Count -ne 1) { throw "Expected exactly one [$Section] $Key key." }
    $updated = $keyPattern.Replace($body.Value, ('${prefix}' + $Value))
    $Text.Substring(0, $body.Index) + $updated + $Text.Substring($body.Index + $body.Length)
}

function Install-OfficialScaler([string]$ZipPath, [string]$Destination) {
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $matches = @($zip.Entries | Where-Object { $_.Name -match '^(OptiScaler|dxgi)\.dll$' })
        if ($matches.Count -ne 1) { throw 'Expected exactly one OptiScaler library in the official archive.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($matches[0], $Destination, $true)
    } finally { $zip.Dispose() }
}

$encoding = [Text.UTF8Encoding]::new($false)
$coreFiles = @('OptiCaptureHost.exe','dxgi.dll','OptiCaptureHost.ini','OptiScaler.ini',
    'Optiscaler/libxess_fg.dll','Optiscaler/libxell.dll','Licenses/XeSS_LICENSE.txt')
$variants = if ($RuntimeOnly) { 'on' } else { 'off','on' }
$staging = if ($RuntimeOnly) { "$output.staging-$([guid]::NewGuid().ToString('N'))" } else { $output }
$null = New-Item -ItemType Directory -Path $staging
foreach ($variant in $variants) {
    $directory = if ($RuntimeOnly) { $staging } else { Join-Path $staging $variant }
    if (!$RuntimeOnly) { $null = New-Item -ItemType Directory -Path $directory }
    $archiveMembers = @()
    if ($CoreFilesOnly) {
        $archiveMembers = @($coreFiles | ForEach-Object { "OptiCaptureHost-2.10.80_Full Pack/$_" })
    }
    & $tar -xf $archive.FullName --strip-components=1 -C $directory @archiveMembers
    if ($LASTEXITCODE -ne 0) { throw "Unable to extract $variant into $directory." }

    $expectedFiles = if ($CoreFilesOnly) { $coreFiles } else {
        'OptiCaptureHost.exe','dxgi.dll','Optiscaler/libxess_fg.dll','Optiscaler/libxell.dll'
    }
    foreach ($name in $expectedFiles) {
        if (!(Test-Path -LiteralPath (Join-Path $directory $name) -PathType Leaf)) { throw "Missing $name after extraction." }
    }
    if ($scalerZipPath) { Install-OfficialScaler $scalerZipPath (Join-Path $directory 'dxgi.dll') }
    $hostIniPath = Join-Path $directory 'OptiCaptureHost.ini'
    $hostIni = [IO.File]::ReadAllText($hostIniPath)
    foreach ($key in 'Target','OptiFps','Auto') { $hostIni = Set-RequiredIniValue $hostIni 'Limiter' $key '0' }
    [IO.File]::WriteAllText($hostIniPath, $hostIni, $encoding)

    $scalerIniPath = Join-Path $directory 'OptiScaler.ini'
    $enabled = if ($variant -eq 'on') { 'true' } else { 'false' }
    $scalerIni = Set-RequiredIniValue ([IO.File]::ReadAllText($scalerIniPath)) 'FrameGen' 'Enabled' $enabled
    if ($CoreFilesOnly) {
        $scalerIni = Set-RequiredIniValue $scalerIni 'Plugins' 'LoadAsiPlugins' 'false'
        foreach ($key in 'XeFG','RTX40','RTX2030') {
            $scalerIni = Set-RequiredIniValue $scalerIni 'MFGUnlock' $key 'false'
        }
    }
    [IO.File]::WriteAllText($scalerIniPath, $scalerIni, $encoding)
}

if ($RuntimeOnly) {
    # Directory.Move refuses an existing destination rather than merging into it.
    [IO.Directory]::Move($staging, $output)
    [pscustomobject]@{ RuntimeDirectory = $output }
} else {
    [pscustomobject]@{ OffDirectory = Join-Path $output 'off'; OnDirectory = Join-Path $output 'on' }
}
