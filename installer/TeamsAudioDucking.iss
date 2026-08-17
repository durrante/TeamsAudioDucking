; Inno Setup script for Teams Audio Ducking (per-user, no admin required).
; Build the app first:  .\build.ps1   (produces ..\publish relative to this file's parent)
; Then compile this script with Inno Setup 6: iscc installer\TeamsAudioDucking.iss

#define MyAppName "Teams Audio Ducking"
#define MyAppVersion "1.4.2"
#define MyAppExeName "TeamsAudioDucking.exe"

[Setup]
AppId={{9B0C3E86-2F0D-4C4A-9E4F-6A1D26FE0B11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputBaseFilename=TeamsAudioDucking-Setup-{#MyAppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "autostart"; Description: "Start automatically when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TeamsAudioDucking"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /f /im {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"
