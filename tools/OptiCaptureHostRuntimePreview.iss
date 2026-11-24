; Local runtime preview.  Do not publish this installer.
#ifndef RuntimeSource
  #error "Pass /DRuntimeSource=<verified CoreFilesOnly directory>."
#endif
#ifndef PreviewNotice
  #error "Pass /DPreviewNotice=<preview notice file>."
#endif

[Setup]
AppId=GameLocker-OptiCaptureHost-Runtime-Preview
AppName=GameLocker OptiCaptureHost Runtime Preview
AppVersion=2.10.80
PrivilegesRequired=lowest
DefaultDirName={localappdata}\GameLocker-OptiCaptureHost-Preview\v2.10.80
UsePreviousAppDir=no
DisableProgramGroupPage=yes
CreateUninstallRegKey=no
Uninstallable=no
CloseApplications=no
RestartApplications=no
OutputBaseFilename=GameLocker-OptiCaptureHost-Runtime-Preview
Compression=lzma2/fast
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

[Files]
Source: "{#RuntimeSource}\OptiCaptureHost.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeSource}\dxgi.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeSource}\OptiCaptureHost.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeSource}\OptiScaler.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeSource}\Optiscaler\libxess_fg.dll"; DestDir: "{app}\Optiscaler"; Flags: ignoreversion
Source: "{#RuntimeSource}\Optiscaler\libxell.dll"; DestDir: "{app}\Optiscaler"; Flags: ignoreversion
Source: "{#RuntimeSource}\Licenses\XeSS_LICENSE.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#PreviewNotice}"; DestDir: "{app}"; DestName: "PREVIEW-NOTICE.md"; Flags: ignoreversion

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target, PreviewRoot: String;
begin
  Target := AddBackslash(ExpandFileName(ExpandConstant('{app}')));
  PreviewRoot := AddBackslash(ExpandFileName(ExpandConstant('{localappdata}\GameLocker-OptiCaptureHost-Preview')));
  Result := '';
  if (CompareText(Copy(Target, 1, Length(PreviewRoot)), PreviewRoot) <> 0) or
    (Length(Target) <= Length(PreviewRoot)) then
    Result := 'Choose a subdirectory under ' + PreviewRoot + ' for this local preview.';
end;
