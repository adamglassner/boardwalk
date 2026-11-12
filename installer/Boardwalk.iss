; Inno Setup script for Boardwalk (Inno Setup 6.3 or newer)
; Build: run ..\build.ps1, or compile this file with ISCC.exe after publishing the exe.
;
; Installs to Program Files for all users by default (asks for administrator permission).
; The first page also offers "Install for me only", which needs no administrator rights.
;
; The Bytehawk Studio Launcher can run this setup silently:
;   Boardwalk-<version>-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR="<folder>"

#define MyAppName "Boardwalk"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Bytehawk Studio"
#define MyAppExeName "Boardwalk.exe"
#define MyAppUrl "https://github.com/adamglassner/boardwalk"
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

[Setup]
AppId={{C4E2A7B1-5D3F-4A8E-B6C9-2F1D7E9A4B35}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
AppCopyright=© 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Boardwalk-{#MyAppVersion}-Setup
SetupIconFile=..\assets\boardwalk.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Ask to close Boardwalk if it's running (Windows Restart Manager)
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Show the version number on the first and last pages
WelcomeLabel1=Welcome to the [name/ver] Setup Wizard
FinishedHeadingLabel=[name/ver] is installed

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The icon is embedded in Boardwalk.exe, so shortcuts use the exe's own icon.
; Straight into the Start menu's app list (no subfolder), like other Windows 10/11 apps.
; /NOICONS (used by the Bytehawk Studio Launcher) skips it.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Kanban boards for your projects"; Check: not NoIconsRequested
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Kanban boards for your projects"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete]: uninstalling removes the program and shortcuts.
; Your boards and settings (%APPDATA%\Boardwalk) are left alone.

[Code]
{ /NOICONS: the launcher installs Boardwalk without Start menu entries. Inno Setup only honours it
  on the Start Menu folder page, which Boardwalk doesn't show, so check the command line directly. }
function NoIconsRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NOICONS') = 0 then
      Result := True;
end;
