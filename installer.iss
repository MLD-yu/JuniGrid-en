; Inno Setup script for JuniGrid v1.0.0
; Packages the self-contained build (publish/sc) into a Windows installer
; that creates a desktop shortcut — click and launch.

#define MyAppName "JuniGrid"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "JuniGrid"
#define MyAppExeName "JuniGrid.exe"

[Setup]
AppId={{7E1B2C64-9A4D-4C0E-9F61-3A5D8B2C4E10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\JuniGrid
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=release
OutputBaseFilename=JuniGrid-v1.0.0-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "publish\sc\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; WebView2 user-data folder and logs created at runtime inside the install dir
Type: filesandordirs; Name: "{app}\userdata"
