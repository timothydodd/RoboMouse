; RoboMouse direct-download installers (Inno Setup 6). Built by packaging\Build-Installer.ps1, which
; passes the defines below. One script, two outputs:
;
;   full          RoboMouse-Setup-<ver>.exe          app + desktop service + helper
;   /DServiceOnly RoboMouse-Service-Setup-<ver>.exe  service + helper, for people using the Store app
;
; Either way the service is registered LocalSystem, manual start and stopped; the app's General page
; toggle turns it on (one UAC prompt). See plans/uac-service.md.
;
;   /DAppVersion=1.2.3        required
;   /DStageDir=<dir>          required: holds app\ and service\ publish folders
;   /DOutputDir=<dir>         required
;   /DPackageFamily=<pfn>     Store app allowed to connect (required for ServiceOnly, optional otherwise)

#ifndef AppVersion
  #error AppVersion is not defined; build with packaging\Build-Installer.ps1
#endif
#ifdef ServiceOnly
  #ifndef PackageFamily
    #error The service-only installer needs PackageFamily (the Store app's package family name)
  #endif
#endif

#define ServiceName "RoboMouseService"
#define FullAppId "{{6B0E6F0B-5C0B-4E53-9C43-7F1B7B3B7A11}"
#define FullAppIdKey "{6B0E6F0B-5C0B-4E53-9C43-7F1B7B3B7A11}_is1"

[Setup]
#ifdef ServiceOnly
AppId={{B7C1E0B2-2C3A-4B0F-8E57-0C6E5C3D9A22}
AppName=RoboMouse Desktop Service
DefaultDirName={autopf}\RoboMouse Desktop Service
OutputBaseFilename=RoboMouse-Service-Setup-{#AppVersion}
DisableProgramGroupPage=yes
#else
AppId={#FullAppId}
AppName=RoboMouse
DefaultDirName={autopf}\RoboMouse
DefaultGroupName=RoboMouse
OutputBaseFilename=RoboMouse-Setup-{#AppVersion}
UninstallDisplayIcon={app}\RoboMouse.App.exe
#endif
AppVersion={#AppVersion}
AppPublisher=Tim Dodd
AppPublisherURL=https://github.com/timothydodd/RoboMouse
OutputDir={#OutputDir}
; The service must live where a normal user cannot replace it, so this is always a machine install.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableDirPage=yes
CloseApplications=no

[Files]
#ifndef ServiceOnly
Source: "{#StageDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
#endif
Source: "{#StageDir}\service\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

#ifndef ServiceOnly
[Icons]
Name: "{group}\RoboMouse"; Filename: "{app}\RoboMouse.App.exe"

[Run]
Filename: "{app}\RoboMouse.App.exe"; Description: "Start RoboMouse"; Flags: nowait postinstall skipifsilent runasoriginaluser
#endif

[Code]
var
  ServiceWasRunning: Boolean;

function Sc(const Params: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

procedure StopProcesses;
var
  Code: Integer;
begin
  { "sc stop" succeeds only when the service was running, which is what an upgrade must restore. }
  ServiceWasRunning := Sc('stop {#ServiceName}') = 0;
  if ServiceWasRunning then
    Sleep(2000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RoboMouse.Helper.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
#ifndef ServiceOnly
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RoboMouse.App.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
#endif
end;

#ifdef ServiceOnly
function InitializeSetup: Boolean;
begin
  Result := True;
  { The full installer already contains the service; two registrations would fight over one name. }
  if RegKeyExists(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#FullAppIdKey}') then
  begin
    MsgBox('RoboMouse is already installed with its desktop service. This separate service is only for the Microsoft Store version of RoboMouse.', mbInformation, MB_OK);
    Result := False;
  end;
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopProcesses;
  Result := '';
end;

procedure RegisterService;
var
  BinPath: String;
begin
  { The service only accepts pipe connections from the app named on its own command line. With no
    --app-path it expects RoboMouse.App.exe beside itself, which is the full install. }
  BinPath := '\"' + ExpandConstant('{app}\RoboMouse.Service.exe') + '\"';
#ifdef PackageFamily
  BinPath := BinPath + ' --package-family {#PackageFamily}';
#endif

  { Create fails harmlessly when upgrading; config then brings the command line up to date while
    leaving the start type the user chose through the app. }
  Sc('create {#ServiceName} binPath= "' + BinPath + '" start= demand obj= LocalSystem DisplayName= "RoboMouse Desktop Service"');
  Sc('config {#ServiceName} binPath= "' + BinPath + '"');
  { Restart after a crash (5 s, 5 s, then every minute); the count resets after a day without one. }
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000');
  Sc('failureflag {#ServiceName} 1');
  Sc('description {#ServiceName} "Lets RoboMouse control UAC prompts, the lock screen and windows running as administrator."');

  if ServiceWasRunning then
    Sc('start {#ServiceName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopProcesses;
    Sc('delete {#ServiceName}');
  end;
end;
