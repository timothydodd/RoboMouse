<#
.SYNOPSIS
  Builds the direct-download installers: RoboMouse-Setup (app + desktop service) and
  RoboMouse-Service-Setup (service only, for people using the Microsoft Store app).

.DESCRIPTION
  Publishes the app, the service and the helper as Native AOT win-x64 (needs the Visual Studio C++
  build tools) and compiles packaging\installer\RoboMouse.iss with Inno Setup 6.

  The service only accepts the Store app when it is told that app's package family name, which is
  derived here from the same Partner Center identity Build-Msix.ps1 uses. Without it the
  service-only installer would install a service nothing can talk to, so it is skipped.

.PARAMETER Version
  Three-part version, the same one the app is built with.
.PARAMETER PackageName
  Identity Name from Partner Center (defaults to $env:STORE_PACKAGE_NAME).
.PARAMETER Publisher
  Publisher from Partner Center, e.g. CN=1A2B3C4D-... (defaults to $env:STORE_PUBLISHER).

.EXAMPLE
  .\packaging\Build-Installer.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$PackageName = $env:STORE_PACKAGE_NAME,
    [string]$Publisher = $env:STORE_PUBLISHER,
    [string]$Output = "artifacts"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

# Package family name = <Identity Name>_<publisher id>, where the publisher id is the first 8 bytes of
# SHA-256 over the UTF-16LE publisher string, written as 13 characters of Crockford-style base32.
function Get-PackageFamilyName([string]$Name, [string]$PublisherName) {
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::Unicode.GetBytes($PublisherName))
    $bits = (($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join '') + '0'
    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    $id = -join (0..12 | ForEach-Object { $alphabet[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)] })
    return "${Name}_$id"
}

# Guard the one piece of this nobody can eyeball: Microsoft's own publisher id is well known.
$check = Get-PackageFamilyName "x" "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
if ($check -ne "x_8wekyb3d8bbwe") { throw "Package family name derivation is wrong (got $check)." }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php or 'choco install innosetup'." }

$stage = Join-Path $root "artifacts\installer-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$outDir = Join-Path $root $Output
New-Item $outDir -ItemType Directory -Force | Out-Null

$targets = @{ 'RoboMouse.App' = 'app'; 'RoboMouse.Service' = 'service'; 'RoboMouse.Helper' = 'service' }
foreach ($project in $targets.Keys) {
    $dest = Join-Path $stage $targets[$project]
    Write-Host "Publishing $project (Native AOT win-x64)..."
    dotnet publish (Join-Path $root "src\$project") -c Release -r win-x64 -o $dest -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item

$family = if ($PackageName -and $Publisher) { Get-PackageFamilyName $PackageName $Publisher } else { $null }
$common = @("/Qp", "/DAppVersion=$Version", "/DStageDir=$stage", "/DOutputDir=$outDir")
$script = Join-Path $PSScriptRoot "installer\RoboMouse.iss"

Write-Host "Compiling RoboMouse-Setup-$Version.exe..."
$full = $common
if ($family) { $full += "/DPackageFamily=$family" }
& $iscc @full $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for the full installer" }

if ($family) {
    Write-Host "Compiling RoboMouse-Service-Setup-$Version.exe (Store app: $family)..."
    & $iscc @common "/DServiceOnly" "/DPackageFamily=$family" $script
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for the service-only installer" }
} else {
    Write-Warning "STORE_PACKAGE_NAME / STORE_PUBLISHER are not set, so the service-only installer (for Store users) was skipped."
}

Get-ChildItem $outDir -Filter "RoboMouse-*Setup-$Version.exe" | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Tee-Object -FilePath (Join-Path $outDir "SHA256SUMS.txt")
