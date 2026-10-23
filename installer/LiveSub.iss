#define MyAppName "LiveSub"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Jwforever"
#define MyAppURL "https://github.com/Jwforever/livesub"
#define MyAppExeName "LiveSub.exe"

[Setup]
AppId={{8F2C1E6A-4B7D-4E39-9F1A-2D6B8C0E5A73}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=LiveSub Windows subtitle translator
DefaultDirName={localappdata}\Programs\LiveSub
DefaultGroupName=LiveSub
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LiveSub-{#MyAppVersion}-setup
SetupIconFile=..\src\LiveSub\Assets\livesub.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LiveSub"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\LiveSub"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch LiveSub"; Flags: nowait postinstall skipifsilent
