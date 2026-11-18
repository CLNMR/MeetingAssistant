; Inno Setup script for the Windows release (built by .github/workflows/release.yml):
;   iscc /DAppVersion=1.2.3 /DSourceDir=<publish folder> /DOutputDir=<folder> MeetingAssistant.iss
; Installs per user (no administrator rights) into %LOCALAPPDATA%\Programs\MeetingAssistant.
; App data (%LOCALAPPDATA%\MeetingAssistant: recordings, models, settings) is never touched,
; also not by the uninstaller.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{2C963126-3B22-4C7D-8E38-36A214633CBF}
AppName=MeetingAssistant
AppVersion={#AppVersion}
AppVerName=MeetingAssistant {#AppVersion}
AppPublisher=MeetingAssistant
DefaultDirName={localappdata}\Programs\MeetingAssistant
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=MeetingAssistant-{#AppVersion}-windows-x64-setup
SetupIconFile=..\src\MeetingAssistant.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MeetingAssistant.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app's single-instance mutex: setup asks to quit MeetingAssistant first and never closes
; it by itself, so a running recording is never cut off by an update.
AppMutex=Local\de.local.MeetingScribe.MeetingAssistant
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files of the previous version that a new version no longer ships.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\MeetingAssistant"; Filename: "{app}\MeetingAssistant.exe"
Name: "{autodesktop}\MeetingAssistant"; Filename: "{app}\MeetingAssistant.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MeetingAssistant.exe"; Description: "{cm:LaunchProgram,MeetingAssistant}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the "open at login" entry the app may have written.
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MeetingAssistant /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"
