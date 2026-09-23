#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Smoke-tests the direct-download installers on a clean Windows machine (CI runs it on windows-latest).

.DESCRIPTION
  Silent install -> service registration (quoted path, LocalSystem, manual start, privileges) ->
  %ProgramData%\RoboMouse ACL -> firewall rules -> upgrade over itself -> a /DIR= outside Program Files
  is refused -> uninstall leaves no service, files, rules, registry or ProgramData folder behind.
  With the service-only installer as well, checks the two share the one service: it survives
  uninstalling either one and goes with the last.

  Changes the machine it runs on; meant for throwaway CI runners and test VMs.

.EXAMPLE
  .\packaging\Test-Installer.ps1 -Installer artifacts\RoboMouse-Setup-1.1.5.exe
.EXAMPLE
  .\packaging\Test-Installer.ps1 -Installer artifacts\RoboMouse-Setup-1.1.5.exe -ServiceInstaller artifacts\RoboMouse-Service-Setup-1.1.5.exe
#>
param(
    [Parameter(Mandatory)][string]$Installer,
    [string]$ServiceInstaller,
    [string]$LogDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'robomouse-installer-logs')
)

$ErrorActionPreference = 'Stop'
# sc.exe exits non-zero for a missing service, which some checks expect.
$PSNativeCommandUseErrorActionPreference = $false
$serviceName = 'RoboMouseService'
$fullDir = Join-Path $env:ProgramFiles 'RoboMouse'
$serviceOnlyDir = Join-Path $env:ProgramFiles 'RoboMouse Desktop Service'
$dataDir = Join-Path $env:ProgramData 'RoboMouse'
$logDir = $LogDir
New-Item $logDir -ItemType Directory -Force | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Check([bool]$condition, [string]$what) {
    if ($condition) { Write-Host "  ok   $what" }
    else { Write-Host "  FAIL $what" -ForegroundColor Red; $failures.Add($what) }
}

function Invoke-Setup([string]$exe, [string[]]$extra = @()) {
    $log = Join-Path $logDir ("{0}-{1}.log" -f [IO.Path]::GetFileNameWithoutExtension($exe), [DateTime]::Now.Ticks)
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $extra
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru
    return $p.ExitCode
}

function Invoke-Uninstall([string]$dir) {
    $uninstaller = Join-Path $dir 'unins000.exe'
    if (-not (Test-Path $uninstaller)) { throw "No uninstaller at $uninstaller" }
    Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait | Out-Null
    # The uninstaller hands over to a copy of itself in %TEMP%; wait for the files to go.
    for ($i = 0; $i -lt 120 -and (Test-Path $uninstaller); $i++) { Start-Sleep -Milliseconds 500 }
}

function Get-ServiceConfig {
    $qc = (sc.exe qc $serviceName 5000) -join "`n"
    if ($LASTEXITCODE -ne 0) { return $null }
    return $qc
}

function Test-Service([string]$expectedDir, [string]$label) {
    $qc = Get-ServiceConfig
    Check ($null -ne $qc) "$label - service is registered"
    if ($null -eq $qc) { return }
    $exe = Join-Path $expectedDir 'RoboMouse.Service.exe'
    Check ($qc -match ('BINARY_PATH_NAME\s*:\s*"' + [regex]::Escape($exe) + '"')) "$label - binary path is quoted and points at $exe"
    Check ($qc -match 'START_TYPE\s*:\s*3\s') "$label - manual start"
    Check ($qc -match 'SERVICE_START_NAME\s*:\s*LocalSystem') "$label - runs as LocalSystem"
    Check ((Get-Service $serviceName).Status -eq 'Stopped') "$label - installed stopped"
    $sid = (sc.exe qsidtype $serviceName) -join "`n"
    Check ($sid -match 'UNRESTRICTED') "$label - service SID type is unrestricted"
    $privs = (sc.exe qprivs $serviceName 4096) -join "`n"
    Check (($privs -match 'SeTcbPrivilege') -and ($privs -match 'SeAssignPrimaryTokenPrivilege') -and ($privs -notmatch 'SeDebugPrivilege')) "$label - required privileges limited"
}

function Test-DataDir {
    Check (Test-Path $dataDir) 'ProgramData\RoboMouse exists'
    if (-not (Test-Path $dataDir)) { return }
    $item = Get-Item $dataDir -Force
    Check (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'ProgramData\RoboMouse is a real folder'
    $acl = Get-Acl $dataDir
    $owner = (New-Object System.Security.Principal.NTAccount($acl.Owner)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    Check ($owner -eq 'S-1-5-32-544') "ProgramData\RoboMouse is owned by Administrators (got $($acl.Owner))"
    Check $acl.AreAccessRulesProtected 'ProgramData\RoboMouse does not inherit'
    $allowed = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545')
    # Any right that lets a principal create, change or delete something in the folder.
    $writeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        $sid = $rule.IdentityReference.Value
        Check ($allowed -contains $sid) "ProgramData\RoboMouse grants only SYSTEM, Administrators and Users ($sid)"
        if ($sid -eq 'S-1-5-32-545') {
            Check ((([int]$rule.FileSystemRights) -band $writeMask) -eq 0) "Users can only read ProgramData\RoboMouse ($($rule.FileSystemRights))"
        }
    }
}

function Test-FirewallRules([bool]$present) {
    foreach ($name in 'RoboMouse app (TCP)', 'RoboMouse app (UDP)') {
        $rule = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
        if (-not $present) { Check ($null -eq $rule) "firewall rule '$name' removed"; continue }
        Check ($null -ne $rule) "firewall rule '$name' exists"
        if ($null -eq $rule) { continue }
        $profiles = $rule.Profile.ToString()
        Check (($profiles -match 'Private') -and ($profiles -match 'Domain') -and ($profiles -notmatch 'Public|Any')) "firewall rule '$name' is Private + Domain ($profiles)"
        $program = ($rule | Get-NetFirewallApplicationFilter).Program
        Check ($program -eq (Join-Path $fullDir 'RoboMouse.App.exe')) "firewall rule '$name' is scoped to the app ($program)"
    }
}

function Test-Gone {
    Check ($null -eq (Get-ServiceConfig)) 'service deleted'
    Check (-not (Test-Path (Join-Path $fullDir 'RoboMouse.App.exe'))) 'app files removed'
    Check (-not (Test-Path $dataDir)) 'ProgramData\RoboMouse removed'
    Check (-not (Test-Path 'HKLM:\SOFTWARE\RoboMouse')) 'HKLM\SOFTWARE\RoboMouse removed'
    Check (-not (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\RoboMouseService')) 'event log source removed'
    Test-FirewallRules $false
}

# First, on the clean machine, so no earlier install's folder is reused.
Write-Host 'Refuse a folder outside Program Files'
$outside = Join-Path $env:SystemDrive 'RoboMouseOutside'
Check ((Invoke-Setup $Installer @("/DIR=`"$outside`"")) -ne 0) '/DIR outside Program Files fails'
Check (-not (Test-Path (Join-Path $outside 'RoboMouse.Service.exe'))) 'nothing installed outside Program Files'
Check ($null -eq (Get-ServiceConfig)) 'no service registered by the refused install'

Write-Host "Install $Installer"
Check ((Invoke-Setup $Installer) -eq 0) 'install exits 0'
Test-Service $fullDir 'full install'
Test-DataDir
Test-FirewallRules $true
Check (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\RoboMouseService') 'event log source registered'

Write-Host 'Upgrade over itself'
Check ((Invoke-Setup $Installer) -eq 0) 'reinstall exits 0'
Test-Service $fullDir 'after upgrade'
Test-DataDir

if ($ServiceInstaller) {
    Write-Host "Service-only installer alongside: $ServiceInstaller"
    Check ((Invoke-Setup $ServiceInstaller) -eq 0) 'service-only install exits 0'
    Test-Service $fullDir 'both installed (the full install owns the running copy)'
    Check ((Get-ServiceConfig) -match '--package-family') 'service also trusts the Store app'

    Write-Host 'Uninstall the full install; the service must stay for the Store app'
    Invoke-Uninstall $fullDir
    Test-Service $serviceOnlyDir 'after removing the full install'
    Test-DataDir
    Test-FirewallRules $false

    Write-Host 'Uninstall the service-only install'
    Invoke-Uninstall $serviceOnlyDir
}
else {
    Write-Host 'Uninstall'
    Invoke-Uninstall $fullDir
}
Test-Gone

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed. Setup logs: $logDir" -ForegroundColor Red
    exit 1
}
Write-Host "`nAll installer checks passed."
