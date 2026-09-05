; Adam CodexHub — Windows installer (Inno Setup 6)
; Compiled by scripts\package-release.ps1 (and CI) via:
;   ISCC.exe installer\adam-codexhub.iss ^
;     /DMyAppVersion=1.0.4 ^
;     /DStagingDir="<published-app-folder>" ^
;     /DOutputDir="<artifacts-folder>" ^
;     /DRepoRoot="<repository-root>"
;
; Per-user install (no admin prompt), mirrors VS Code style:
;   {autopf} resolves to %LocalAppData%\Programs when PrivilegesRequired=lowest.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef StagingDir
  #define StagingDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef RepoRoot
  #define RepoRoot "."
#endif
#ifndef OutputBaseName
  #define OutputBaseName "AdamCodexHub-Setup-" + MyAppVersion + "-win-x64"
#endif

#define MyAppName "Adam CodexHub"
#define MyAppPublisher "Adam Wang"
#define MyAppURL "https://github.com/adamwang99/Adam-CodexHub"
#define MyAppExeName "AdamCodexHub.App.exe"

[Setup]
AppId={{B6D2E8A4-3F91-4C7B-9A2E-5D1C0F8E7A33}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Adam CodexHub
DefaultGroupName=Adam CodexHub
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
SetupIconFile={#RepoRoot}\src\AdamCodexHub.App\Assets\adam-codexhub.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
RestartApplications=no
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Adam CodexHub CLI"; Filename: "{app}\cli\AdamCodexHub.Cli.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
