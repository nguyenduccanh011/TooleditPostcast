#define MyAppName "Podcast Video Editor"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #error "PublishDir define is required"
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "PodcastVideoEditor-Setup-v" + AppVersion
#endif

[Setup]
AppId={{9FA2D3B3-6E6A-4DA4-9AA1-0D45F08D39A7}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
DefaultDirName={localappdata}\Programs\PodcastVideoEditor
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
UninstallDisplayIcon={app}\PodcastVideoEditor.Ui.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Podcast Video Editor Installer

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\PodcastVideoEditor.Ui.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\PodcastVideoEditor.Ui.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PodcastVideoEditor.Ui.exe"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
