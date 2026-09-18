#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Builds and registers the RoboMouse desktop service for local testing (stand-in for the installer).

.DESCRIPTION
  Publishes RoboMouse.Service and RoboMouse.Helper (framework-dependent, no AOT toolchain needed) into
  "%ProgramFiles%\RoboMouse\Service" and registers RoboMouseService as LocalSystem, manual start.
  The service only accepts pipe connections from the exe given by -AppPath, so point it at the
  RoboMouse.App.exe you actually run. A dev build output is user-writable, which the real installer
  never allows; use this on your own machines only.

  Turn it on from RoboMouse Settings > General > "Control UAC prompts and the lock screen", or:
      sc start RoboMouseService
  Log: %ProgramData%\RoboMouse\service.log

.EXAMPLE
  .\packaging\Install-DevService.ps1
  .\packaging\Install-DevService.ps1 -AppPath "C:\Tools\RoboMouse\RoboMouse.App.exe"
  .\packaging\Install-DevService.ps1 -Uninstall
#>
param(
    [string]$AppPath,
    [string]$Configuration = 'Release',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$serviceName = 'RoboMouseService'
$repo = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:ProgramFiles 'RoboMouse\Service'

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
    Write-Host "Removed the existing $serviceName registration."
}

if ($Uninstall) {
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    Write-Host 'Uninstalled.'
    return
}

if (-not $AppPath) {
    $AppPath = Join-Path $repo 'src\RoboMouse.App\bin\Debug\net10.0-windows10.0.19041.0\RoboMouse.App.exe'
}
$AppPath = [System.IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path $AppPath)) {
    throw "RoboMouse.App.exe not found at '$AppPath'. Build the app first or pass -AppPath."
}

foreach ($project in 'RoboMouse.Service', 'RoboMouse.Helper') {
    dotnet publish (Join-Path $repo "src\$project") -c $Configuration -r win-x64 `
        --self-contained false -p:PublishAot=false -o $installDir
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

$binary = '"{0}" --app-path "{1}"' -f (Join-Path $installDir 'RoboMouse.Service.exe'), $AppPath
New-Service -Name $serviceName -BinaryPathName $binary -DisplayName 'RoboMouse Desktop Service' `
    -Description 'Lets RoboMouse control UAC prompts, the lock screen and elevated windows.' `
    -StartupType Manual | Out-Null
# Restart after a crash, as the installer sets it up.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
sc.exe failureflag $serviceName 1 | Out-Null

Write-Host ''
Write-Host "Installed to $installDir"
Write-Host "Accepts connections from: $AppPath"
Write-Host 'Now open RoboMouse Settings > General and turn on "Control UAC prompts and the lock screen".'
