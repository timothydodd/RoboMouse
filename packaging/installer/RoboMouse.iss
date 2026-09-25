; RoboMouse direct-download installers (Inno Setup 6). Built by packaging\Build-Installer.ps1, which
; passes the defines below. One script, two outputs:
;
;   full          RoboMouse-Setup-<ver>.exe          app + desktop service + helper
;   /DServiceOnly RoboMouse-Service-Setup-<ver>.exe  service + helper, for people using the Store app
;
; Either way the service is registered LocalSystem, manual start and stopped; the app's Advanced page
; toggle turns it on (one UAC prompt). See plans/uac-service.md.
;
; Both products can be installed at once and share the one service registration. Each records itself
; under HKLM\SOFTWARE\RoboMouse\DesktopService (value "Full" or "ServiceOnly" = its install folder);
; the service runs from the full install's copy when there is one, and is only deleted, together with
; %ProgramData%\RoboMouse, when the last of the two is uninstalled.
;
;   /DAppVersion=1.2.3        required
;   /DStageDir=<dir>          required: holds app\ and service\ publish folders
;   /DOutputDir=<dir>         required
;   /DPackageFamily=<pfn>     Store app allowed to connect (required for ServiceOnly, optional otherwise)
;
; Command line: /ALLOWDOWNGRADE installs over a newer version (refused otherwise).

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
#define ServiceOnlyAppId "{{B7C1E0B2-2C3A-4B0F-8E57-0C6E5C3D9A22}"
#define ServiceOnlyAppIdKey "{B7C1E0B2-2C3A-4B0F-8E57-0C6E5C3D9A22}_is1"
#ifdef ServiceOnly
  #define OwnerName "ServiceOnly"
  #define OtherOwnerName "Full"
  #define ThisAppIdKey ServiceOnlyAppIdKey
#else
  #define OwnerName "Full"
  #define OtherOwnerName "ServiceOnly"
  #define ThisAppIdKey FullAppIdKey
#endif

[Setup]
#ifdef ServiceOnly
AppId={#ServiceOnlyAppId}
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
; The service must live where a normal user cannot replace it, so this is always a machine install
; under Program Files (a /DIR= anywhere else is refused in PrepareToInstall).
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
const
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\';
  OwnersKey = 'SOFTWARE\RoboMouse\DesktopService';
  EventSourceKey = 'SYSTEM\CurrentControlSet\Services\EventLog\Application\{#ServiceName}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  FirewallRuleTcp = 'RoboMouse app (TCP)';
  FirewallRuleUdp = 'RoboMouse app (UDP)';
  ReparsePointAttribute = $400;
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;

type
  { An SC_HANDLE. Setup and the uninstaller are 32-bit processes, so handles fit in 32 bits. }
  TScHandle = Longword;
  TServiceStatus = record
    dwServiceType: Cardinal;
    dwCurrentState: Cardinal;
    dwControlsAccepted: Cardinal;
    dwWin32ExitCode: Cardinal;
    dwServiceSpecificExitCode: Cardinal;
    dwCheckPoint: Cardinal;
    dwWaitHint: Cardinal;
  end;

function OpenSCManager(lpMachineName, lpDatabaseName: String; dwDesiredAccess: Cardinal): TScHandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(hSCManager: TScHandle; lpServiceName: String; dwDesiredAccess: Cardinal): TScHandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(hService: TScHandle; var ServiceStatus: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(hSCObject: TScHandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

var
  ServiceWasRunning: Boolean;

function RunTool(const Exe, Params: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\' + Exe), Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function Sc(const Params: String): Integer;
begin
  Result := RunTool('sc.exe', Params);
end;

{ --- service state ---------------------------------------------------------------------------- }

{ The service's current state (SERVICE_STOPPED, SERVICE_RUNNING, ...), or 0 when it is not installed. }
function ServiceState: Cardinal;
var
  Manager, Service: TScHandle;
  Status: TServiceStatus;
begin
  Result := 0;
  Manager := OpenSCManager('', 'ServicesActive', SC_MANAGER_CONNECT);
  if Manager = 0 then
    Exit;
  Service := OpenService(Manager, '{#ServiceName}', SERVICE_QUERY_STATUS);
  if Service <> 0 then
  begin
    if QueryServiceStatus(Service, Status) then
      Result := Status.dwCurrentState;
    CloseServiceHandle(Service);
  end;
  CloseServiceHandle(Manager);
end;

{ "sc stop" returns as soon as the stop is requested, but the exe stays locked until the process has
  exited, so wait for STOPPED (up to 30 s) before files are replaced or removed. }
procedure WaitForServiceStopped;
var
  I: Integer;
  State: Cardinal;
begin
  for I := 1 to 60 do
  begin
    State := ServiceState;
    if (State = 0) or (State = SERVICE_STOPPED) then
      Exit;
    Sleep(500);
  end;
  Log('The service did not report STOPPED within 30 seconds.');
end;

procedure StopProcesses;
var
  Code: Integer;
begin
  { "sc stop" succeeds only when the service was running, which is what an upgrade must restore. }
  ServiceWasRunning := Sc('stop {#ServiceName}') = 0;
  if ServiceWasRunning then
    WaitForServiceStopped;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RoboMouse.Helper.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
#ifndef ServiceOnly
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im RoboMouse.App.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
#endif
end;

{ --- which installs own the service ----------------------------------------------------------- }

{ Install folder recorded for Owner ("Full" or "ServiceOnly"), or '' when that product does not
  have the service installed (any more). }
function OwnerDir(const Owner: String): String;
begin
  if not RegQueryStringValue(HKLM, OwnersKey, Owner, Result) then
    Result := ''
  else if not FileExists(AddBackslash(Result) + 'RoboMouse.Service.exe') then
    Result := '';
end;

{ Installs from before 1.1.5 did not record themselves; fill the list in from their uninstall entries. }
procedure RecordLegacyOwners;
var
  Dir: String;
begin
  if (OwnerDir('Full') = '') and RegQueryStringValue(HKLM, UninstallKey + '{#FullAppIdKey}', 'Inno Setup: App Path', Dir) then
    if FileExists(AddBackslash(Dir) + 'RoboMouse.Service.exe') then
      RegWriteStringValue(HKLM, OwnersKey, 'Full', Dir);
  if (OwnerDir('ServiceOnly') = '') and RegQueryStringValue(HKLM, UninstallKey + '{#ServiceOnlyAppIdKey}', 'Inno Setup: App Path', Dir) then
    if FileExists(AddBackslash(Dir) + 'RoboMouse.Service.exe') then
      RegWriteStringValue(HKLM, OwnersKey, 'ServiceOnly', Dir);
end;

{ Points the one service registration at the preferred copy: the full install's (the app it trusts
  by path sits beside it), else the service-only install's. Keeps the start type the user chose. }
procedure ConfigureService;
var
  Dir, Family, BinPath: String;
begin
  Dir := OwnerDir('Full');
  if Dir = '' then
    Dir := OwnerDir('ServiceOnly');
  if Dir = '' then
    Exit;

  { The service only accepts pipe connections from the app named on its own command line. With no
    --app-path it expects RoboMouse.App.exe beside itself, which is the full install. }
  BinPath := '\"' + AddBackslash(Dir) + 'RoboMouse.Service.exe\"';
  if RegQueryStringValue(HKLM, OwnersKey, 'PackageFamily', Family) and (Family <> '') then
    BinPath := BinPath + ' --package-family ' + Family;

  { Create fails harmlessly when the service exists; config then brings the command line up to date. }
  Sc('create {#ServiceName} binPath= "' + BinPath + '" start= demand obj= LocalSystem DisplayName= "RoboMouse Desktop Service"');
  Sc('config {#ServiceName} binPath= "' + BinPath + '"');
  { Restart after a crash (5 s, 5 s, then every minute); the count resets after a day without one. }
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000');
  Sc('failureflag {#ServiceName} 1');
  Sc('description {#ServiceName} "Lets RoboMouse control UAC prompts, the lock screen and windows running as administrator."');
  { Least privilege: only what launching the helper into the user's session needs. The service SID
    stays unrestricted: a write-restricted token would also bind the helper, a copy of it, and
    deny it the Winlogon desktop rights SendInput needs (see plans/uac-service.md). }
  Sc('sidtype {#ServiceName} unrestricted');
  Sc('privs {#ServiceName} SeTcbPrivilege/SeAssignPrimaryTokenPrivilege/SeIncreaseQuotaPrivilege/SeChangeNotifyPrivilege');
end;

{ --- %ProgramData%\RoboMouse (the service log) ------------------------------------------------- }

function IsReparsePoint(const Path: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(Path, FindRec) then
  begin
    Result := (FindRec.Attributes and ReparsePointAttribute) <> 0;
    FindClose(FindRec);
  end;
end;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\RoboMouse');
end;

{ The SYSTEM service writes its log here, so a normal user must not be able to plant a junction or
  link in its place: SYSTEM and Administrators full control, Users read, owner Administrators, no
  inherited entries (ProgramData lets Users create files in new subfolders). }
procedure PrepareDataDir;
var
  Dir: String;
begin
  Dir := DataDir;
  if IsReparsePoint(Dir) then
    RemoveDir(Dir);
  if IsReparsePoint(Dir + '\service.log') then
    DeleteFile(Dir + '\service.log');
  if IsReparsePoint(Dir + '\service.log.1') then
    DeleteFile(Dir + '\service.log.1');
  ForceDirectories(Dir);
  RunTool('icacls.exe', '"' + Dir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q');
  RunTool('icacls.exe', '"' + Dir + '" /setowner *S-1-5-32-544 /Q');
  { Files left from an older install go back to inheriting the new ACL. }
  RunTool('icacls.exe', '"' + Dir + '\*" /reset /T /C /Q');
end;

procedure RemoveDataDir;
begin
  if IsReparsePoint(DataDir) then
    RemoveDir(DataDir)
  else
    DelTree(DataDir, True, True, True);
end;

{ The service falls back to the Application event log when it will not write the file log. }
procedure RegisterEventSource;
begin
  RegWriteExpandStringValue(HKLM, EventSourceKey, 'EventMessageFile', '%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll');
  RegWriteDWordValue(HKLM, EventSourceKey, 'TypesSupported', 7);
end;

{ --- firewall (full install only) -------------------------------------------------------------- }

#ifndef ServiceOnly
procedure RemoveFirewallRules;
begin
  RunTool('netsh.exe', 'advfirewall firewall delete rule name="' + FirewallRuleTcp + '"');
  RunTool('netsh.exe', 'advfirewall firewall delete rule name="' + FirewallRuleUdp + '"');
end;

{ Inbound TCP (peer connections) and UDP (discovery) for the app itself, on Private and Domain
  networks only. Scoped to the program rather than a port so a port changed in Settings still works. }
procedure AddFirewallRules;
var
  App: String;
begin
  RemoveFirewallRules;
  App := ExpandConstant('{app}\RoboMouse.App.exe');
  RunTool('netsh.exe', 'advfirewall firewall add rule name="' + FirewallRuleTcp + '" dir=in action=allow program="' + App + '" protocol=TCP profile=private,domain');
  RunTool('netsh.exe', 'advfirewall firewall add rule name="' + FirewallRuleUdp + '" dir=in action=allow program="' + App + '" protocol=UDP profile=private,domain');
end;
#endif

{ --- setup ------------------------------------------------------------------------------------- }

{ Compares dotted version strings numerically: -1, 0 or 1. }
function CompareVersions(A, B: String): Integer;
var
  PA, PB, NA, NB: Integer;
begin
  Result := 0;
  while (Result = 0) and ((A <> '') or (B <> '')) do
  begin
    PA := Pos('.', A);
    if PA = 0 then PA := Length(A) + 1;
    PB := Pos('.', B);
    if PB = 0 then PB := Length(B) + 1;
    NA := StrToIntDef(Copy(A, 1, PA - 1), 0);
    NB := StrToIntDef(Copy(B, 1, PB - 1), 0);
    Delete(A, 1, PA);
    Delete(B, 1, PB);
    if NA < NB then
      Result := -1
    else if NA > NB then
      Result := 1;
  end;
end;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

function InitializeSetup: Boolean;
var
  Installed: String;
begin
  Result := True;
  { A test build (0.0.<run>) or an older release must not silently replace a newer SYSTEM service. }
  if RegQueryStringValue(HKLM, UninstallKey + '{#ThisAppIdKey}', 'DisplayVersion', Installed)
    and (CompareVersions(Installed, '{#AppVersion}') > 0) and not HasParam('/ALLOWDOWNGRADE') then
  begin
    SuppressibleMsgBox('A newer version (' + Installed + ') is already installed. Run this installer with /ALLOWDOWNGRADE to replace it with {#AppVersion}.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function IsUnder(const Path, Folder: String): Boolean;
begin
  Result := Pos(AddBackslash(Lowercase(Folder)), AddBackslash(Lowercase(Path))) = 1;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { The SYSTEM service must never run from a folder a normal user can write to. }
  if not (IsUnder(ExpandConstant('{app}'), ExpandConstant('{commonpf64}'))
          or IsUnder(ExpandConstant('{app}'), ExpandConstant('{commonpf32}'))) then
  begin
    Result := 'RoboMouse must be installed under ' + ExpandConstant('{commonpf64}') + ', because its desktop service runs as SYSTEM. Remove the /DIR= option or choose a folder there.';
    Exit;
  end;
  StopProcesses;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    PrepareDataDir;
    RecordLegacyOwners;
    RegWriteStringValue(HKLM, OwnersKey, '{#OwnerName}', ExpandConstant('{app}'));
#ifdef PackageFamily
    RegWriteStringValue(HKLM, OwnersKey, 'PackageFamily', '{#PackageFamily}');
#endif
    ConfigureService;
    RegisterEventSource;
#ifndef ServiceOnly
    AddFirewallRules;
#endif
    if ServiceWasRunning then
      Sc('start {#ServiceName}');
  end;
end;

{ --- uninstall --------------------------------------------------------------------------------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopProcesses;
    RecordLegacyOwners;
    RegDeleteValue(HKLM, OwnersKey, '{#OwnerName}');

    if OwnerDir('{#OtherOwnerName}') <> '' then
    begin
      { The other product still uses the service: run it from that product's copy instead. }
      ConfigureService;
      if ServiceWasRunning then
        Sc('start {#ServiceName}');
    end
    else
    begin
      Sc('delete {#ServiceName}');
      RegDeleteKeyIncludingSubkeys(HKLM, OwnersKey);
      RegDeleteKeyIfEmpty(HKLM, 'SOFTWARE\RoboMouse');
      RegDeleteKeyIncludingSubkeys(HKLM, EventSourceKey);
      RemoveDataDir;
    end;

#ifndef ServiceOnly
    RemoveFirewallRules;
    { "Start with Windows" in a direct install is a Run value; it would point at a deleted exe.
      This is the HKCU of the account that approved the uninstall. }
    RegDeleteValue(HKCU, RunKey, 'RoboMouse');
#endif
  end;
end;
