#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RuntimeDirectory,
    [string]$DotNet = 'dotnet.exe',
    [string]$OriginalMenuDll,
    [string]$MenuBundleDirectory
)

$ErrorActionPreference = 'Stop'
$project = Join-Path ([IO.Path]::GetTempPath()) ('GameLocker-OptiCaptureHost-' + [guid]::NewGuid().ToString('N'))
$sdk = (Get-Command $DotNet -CommandType Application | Select-Object -First 1).Source
$services = Join-Path (Split-Path -Parent $PSScriptRoot) 'Services'
$hostArgs = @()
if ($OriginalMenuDll -or $MenuBundleDirectory) {
    if ($RuntimeDirectory -or !$OriginalMenuDll -or !$MenuBundleDirectory) {
        throw 'Provide both menu arguments and omit RuntimeDirectory.'
    }
    $hostArgs = @('--menu', (Resolve-Path -LiteralPath $OriginalMenuDll).ProviderPath,
        (Resolve-Path -LiteralPath $MenuBundleDirectory).ProviderPath)
}
if ($RuntimeDirectory) {
    if (![Environment]::UserInteractive) { throw 'Real-host testing needs an interactive desktop.' }
    $runtime = (Get-Item -LiteralPath $RuntimeDirectory).FullName
    $scalerIni = Join-Path $runtime 'OptiScaler.ini'
    if (!(Test-Path -LiteralPath $scalerIni -PathType Leaf) -or
        [IO.File]::ReadAllText($scalerIni) -notmatch '(?im)^\[FrameGen\][ \t]*\r?\n(?:(?!^\[)[\s\S])*?^Enabled[ \t]*=[ \t]*false[ \t]*\r?$') {
        throw 'Use a prepared OFF probe directory; this test does not enable frame generation.'
    }
}

try {
    & $sdk new console --framework net8.0 --name HostSmoke --output $project --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the smoke-test project.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OptiCaptureHostSmoke.cs.txt') -Destination (Join-Path $project 'Program.cs') -Force
    foreach ($name in 'OptiCaptureHostService.cs','OptiCaptureHostStatus.cs','OptiCaptureProfile.cs','OptiCaptureMenuLocalization.cs') {
        Copy-Item -LiteralPath (Join-Path $services $name) -Destination $project
    }
    if ($RuntimeDirectory) {
        $fixture = Join-Path $project 'OptiCaptureFixture.exe'
        Add-Type -ReferencedAssemblies 'System.Windows.Forms.dll','System.Drawing.dll' -OutputAssembly $fixture -OutputType WindowsApplication -TypeDefinition @"
using System;
using System.Drawing;
using System.Windows.Forms;
internal sealed class MotionProbe : Form {
    readonly Timer timer = new Timer { Interval = 33 };
    readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    int frame;
    MotionProbe() {
        Text = "GameLocker OptiCaptureHost smoke test";
        ClientSize = new Size(640, 360);
        DoubleBuffered = true;
        timer.Tick += (s, e) => {
            if (elapsed.Elapsed.TotalSeconds >= 90) Close();
            else { frame++; Invalidate(); }
        };
        Shown += (s, e) => timer.Start();
        FormClosed += (s, e) => timer.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Color.FromArgb(24, 26, 30));
        for (int i = 0; i < 7; i++)
            e.Graphics.FillRectangle(i % 2 == 0 ? Brushes.CornflowerBlue : Brushes.Orange,
                ((frame * (i % 3 + 3)) + i * 83) % 720 - 58, 52 + i * 34, 54, 26);
    }
    [STAThread] static void Main() { Application.EnableVisualStyles(); Application.Run(new MotionProbe()); }
}
"@
        $hostArgs = @($runtime, $fixture)
    }
    & $sdk run --project (Join-Path $project 'HostSmoke.csproj') --verbosity quiet -- @hostArgs
    if ($LASTEXITCODE -ne 0) { throw "Smoke test failed with exit code $LASTEXITCODE." }
} finally {
    if (Test-Path -LiteralPath $project) {
        $resolved = (Resolve-Path -LiteralPath $project).ProviderPath
        if ($resolved -eq $project -and (Split-Path -Leaf $resolved) -match '^GameLocker-OptiCaptureHost-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
