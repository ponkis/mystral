#define MyAppName "Mystral"

#ifndef MyAppVersion
#error MyAppVersion must be supplied by the release workflow or installer build script.
#endif

#define MyAppPublisher "ponkis.xyz"
#define MyAppExeName "Mystral.exe"

#ifndef MyRuntimeId
#define MyRuntimeId "win-x64"
#endif

#ifndef MyPublishDir
#define MyPublishDir "..\artifacts\publish\Mystral-" + MyAppVersion + "-" + MyRuntimeId + "-folder"
#endif

#ifndef MyOutputBaseFilename
#define MyOutputBaseFilename "Mystral-" + MyAppVersion + "-" + MyRuntimeId + "-setup"
#endif

[Setup]
AppId={{9B29E19E-864E-4D26-961B-B44E91D94D44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\res\ico.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
