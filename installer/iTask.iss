; iTask installer (Inno Setup 6). Build with installer\build.ps1, which publishes the app and
; passes AppVersion, Arch (x64 / arm64) and PublishDir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef PublishDir
  #error PublishDir must point at a self-contained publish of iTask (see build.ps1)
#endif

[Setup]
AppId={{8C4B0E6A-3F1D-4B7E-9A52-1D6E2F7C9B31}
AppName=iTask
AppVersion={#AppVersion}
AppVerName=iTask {#AppVersion}
AppPublisher=Aanan Chopra
AppPublisherURL=https://github.com/AananChopra/iTask
AppSupportURL=https://github.com/AananChopra/iTask
; Per-user install: no admin prompt, and iTask runs as the user anyway.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\iTask
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=iTask
UninstallDisplayName=iTask
UninstallDisplayIcon={app}\iTask.exe
SetupIconFile=..\src\Assets\iTask.ico
; iTask's icon in the wizard's corner (Setup picks the size for the display's scaling).
WizardSmallImageFile=wizard-small-55.png,wizard-small-69.png,wizard-small-83.png,wizard-small-110.png
OutputDir=..\artifacts
OutputBaseFilename=iTask-Setup-{#AppVersion}-{#Arch}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Windows 11 only.
MinVersion=10.0.22000
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; We stop a running iTask ourselves (cleanly, so it puts the taskbar back first).
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start iTask when Windows starts"

[InstallDelete]
; Upgrades: clear out the previous version's files so no stale DLLs linger.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\iTask"; Filename: "{app}\iTask.exe"

[Registry]
; The same value iTask Settings' "Start iTask when Windows starts" switch reads and writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "iTask"; \
    ValueData: """{app}\iTask.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "iTask"; \
    Tasks: not startup; Flags: deletevalue uninsdeletevalue

[Run]
Filename: "{app}\iTask.exe"; Description: "Start iTask now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stops iTask (if running) and makes sure the Windows taskbar is back before the files go.
Filename: "{app}\iTask.exe"; Parameters: "--restore-taskbar"; Flags: runhidden waituntilterminated; \
    RunOnceId: "RestoreTaskbar"

[UninstallDelete]
; iTask has only just exited when the files go, which can leave the folder behind.
Type: dirifempty; Name: "{app}"

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

function OpenEvent(DesiredAccess: Cardinal; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function OpenMutex(DesiredAccess: Cardinal; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

// Asks a running iTask (from any folder) to quit cleanly, the same way "iTask.exe --quit" does,
// and waits for it to exit. Returns False if it is still running after 10 seconds.
function QuitRunningITask(): Boolean;
var
  Event, Mutex: THandle;
  I: Integer;
begin
  Result := True;
  Event := OpenEvent(EVENT_MODIFY_STATE, False, 'Local\iTask.Quit');
  if Event = 0 then
    Exit; // not running
  SetEvent(Event);
  CloseHandle(Event);
  for I := 1 to 50 do
  begin
    Mutex := OpenMutex(SYNCHRONIZE, False, 'Local\iTask.SingleInstance');
    if Mutex = 0 then
      Exit; // gone
    CloseHandle(Mutex);
    Sleep(200);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not QuitRunningITask() then
    Result := 'iTask is still running and did not close. Quit it from its top-left menu, then run setup again.';
end;
