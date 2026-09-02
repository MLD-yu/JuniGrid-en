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
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=C:\Users\21092\AppData\Local\Temp\jg-release
OutputBaseFilename=JuniGrid-v1.0.0-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile=JuniGrid\Assets\junigrid-logo.ico
WizardImageFile=installer\wizard-side.png
WizardSmallImageFile=installer\wizard-small-blank.png

[Messages]
; Branded wording instead of the stock lines
SetupAppTitle=JuniGrid Setup
SetupWindowTitle=JuniGrid Setup — v{#MyAppVersion}
WelcomeLabel2=This will install [name/ver] on your computer.%n%nA desktop mod manager and launcher for Stardew Valley, with Nexus Mods integration.%n%nIt is recommended that you close all other applications before continuing.

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

[Code]
// Branded colors: warm amber accent over dark/white surfaces.
// The sidebar/welcome area already carries the branded artwork; here we tint
// the remaining plain surfaces so nothing stays stock grey-blue.
procedure InitializeWizard();
begin
  WizardForm.Color := $00202020;            // dark frame around the page
  WizardForm.WelcomePage.Color := $00202020;
  WizardForm.FinishedPage.Color := $00202020;
  WizardForm.WelcomeLabel2.Color := $00202020;
  WizardForm.WelcomeLabel2.Font.Color := clWhite;
  WizardForm.FinishedLabel.Font.Color := clWhite;
  WizardForm.PageNameLabel.Font.Color := $0019943C;   // logo green #19943C (BGR) accent
  WizardForm.PageDescriptionLabel.Font.Color := clBlack;
end;
