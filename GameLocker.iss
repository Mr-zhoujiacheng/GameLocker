; Game Locker 安装包脚本（Inno Setup 6）
; 用法：ISCC.exe GameLocker.iss
; 源目录 = dotnet build 的 Release 产物；产物落在仓库外的 ..\dist\ 下
; 说明：publish 目录（dotnet publish 产物）不含 GameLocker.pri，运行时无法加载 XAML，
;       故打包统一取 build 产物，并排除 publish 与 *.pdb

#define AppName "Game Locker"
#define AppVersion "1.6.1"
#define AppPublisher "Mr-zhoujiacheng"
#define AppUrl "https://github.com/Mr-zhoujiacheng/GameLocker"
#define SrcDir "bin\Release\net8.0-windows10.0.19041.0\win-x64"

[Setup]
AppId={{A7F3C4E2-9B21-4E7D-8C5A-6D4E3F1B2A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
; 每用户安装（无需管理员）：默认落到 %LOCALAPPDATA%\Programs\GameLocker
PrivilegesRequired=lowest
DefaultDirName={autopf}\GameLocker
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=GameLocker-Setup-v{#AppVersion}
SetupIconFile=Assets\GameLocker.ico
UninstallDisplayIcon={app}\GameLocker.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; 覆盖安装时自动结束正在运行的 Game Locker（Restart Manager）
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "publish,publish\*,*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\GameLocker.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\GameLocker.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GameLocker.exe"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent
