; Inno Setup script for Protagonist Labs Launchpad. Build it through build.ps1, which publishes the app first and passes
; the version in. Modelled on Uproot's installer. Installs to Program Files\ProtagonistLabs\Launchpad, the studio's common folder.

#define AppName "Launchpad"
#define AppLongName "Protagonist Labs Launchpad"
#define AppExe "Launchpad.exe"
#define AppPublisher "Protagonist Labs"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{48F919EA-665D-4FD5-8221-A452E49EC2AC}
AppName={#AppLongName}
AppVersion={#AppVersion}
AppVerName={#AppLongName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://protagonistlabs.app/launchpad/
AppCopyright=Copyright (c) 2026 Protagonist Labs
VersionInfoVersion={#AppVersion}

; Per-machine: Launchpad stages installers in {app}\staging, a folder only admins can write (Installer.cs).
PrivilegesRequired=admin
DefaultDirName={autopf}\ProtagonistLabs\{#AppName}
DefaultGroupName={#AppLongName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppLongName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

CloseApplications=force
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=Launchpad-{#AppVersion}-setup
SetupIconFile=..\Launchpad\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Launchpad with Windows (in the notification area)"; GroupDescription: "Startup:"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppLongName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppLongName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The user who installs gets the logon start; others turn it on in Launchpad (State.StartWithWindows writes the same value).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppLongName}"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; As the signed-in user, not with the installer's admin rights: Launchpad runs as the user and asks for admin per batch.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; A silent install is an update: Launchpad comes back in the tray, where it was.
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallDelete]
; Its card in the shared folder, so the sister apps stop listing it, and any installer left in staging. Its state and log stay.
Type: files; Name: "{userappdata}\ProtagonistLabs\apps\launchpad.json"
Type: filesandordirs; Name: "{app}\staging"

[Code]
procedure StopLaunchpad();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopLaunchpad();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopLaunchpad();
  Result := True;
end;
