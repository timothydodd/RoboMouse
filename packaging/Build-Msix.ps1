<#
.SYNOPSIS
  Builds the Microsoft Store package (MSIX) for RoboMouse.

.DESCRIPTION
  Publishes a self-contained win-x64 build, stamps the package manifest with the identity from
  Partner Center, and packs it with makeappx from the Windows SDK. The result is unsigned: Partner
  Center signs Store submissions. Use -Sign to sign with a self-signed certificate for sideload testing.

.PARAMETER PackageName
  Identity Name from Partner Center > Product identity (e.g. 12345TimDodd.RoboMouse).
.PARAMETER Publisher
  Publisher from the same page (e.g. CN=1A2B3C4D-...).
.PARAMETER PublisherDisplay
  Publisher display name from the same page.
.PARAMETER Version
  Four-part package version. Store versions must end in .0.

.EXAMPLE
  .\packaging\Build-Msix.ps1 -PackageName 12345TimDodd.RoboMouse -Publisher "CN=..." -PublisherDisplay "Tim Dodd" -Version 1.0.0.0
.EXAMPLE
  .\packaging\Build-Msix.ps1 -Sign     # local test package with a self-signed certificate
#>
[CmdletBinding()]
param(
    [string]$PackageName = $env:STORE_PACKAGE_NAME,
    [string]$Publisher = $env:STORE_PUBLISHER,
    [string]$PublisherDisplay = $env:STORE_PUBLISHER_DISPLAY,
    [string]$Version = "1.0.0.0",
    [string]$Output = "artifacts",
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not $PackageName) { $PackageName = "RoboMouse.Local" }
if (-not $Publisher) { $Publisher = "CN=RoboMouse Local Test" }
if (-not $PublisherDisplay) { $PublisherDisplay = "RoboMouse (local build)" }

# --- Locate makeappx / signtool from the Windows SDK -------------------------------------------
$kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$sdkBin = Get-ChildItem $kits -Directory -Filter "10.*" | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName "x64" } | Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } | Select-Object -First 1
if (-not $sdkBin) { throw "makeappx.exe not found. Install the Windows 10/11 SDK (Visual Studio Installer > Individual components)." }
$makeappx = Join-Path $sdkBin "makeappx.exe"
$signtool = Join-Path $sdkBin "signtool.exe"

# --- Publish self-contained (Store packages cannot rely on a separately installed .NET runtime) --
$stage = Join-Path $root "artifacts\msix-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $stage -ItemType Directory | Out-Null

Write-Host "Publishing self-contained win-x64..."
dotnet publish (Join-Path $root "src\RoboMouse.App") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- Manifest and assets ------------------------------------------------------------------------
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $stage "Assets") -Recurse
$manifest = Get-Content (Join-Path $PSScriptRoot "Package.appxmanifest") -Raw
$manifest = $manifest.Replace("__PACKAGE_NAME__", $PackageName).Replace("__PUBLISHER__", $Publisher) `
    .Replace("__PUBLISHER_DISPLAY__", $PublisherDisplay).Replace("__VERSION__", $Version)
Set-Content (Join-Path $stage "AppxManifest.xml") $manifest -Encoding UTF8

# --- Pack -----------------------------------------------------------------------------------------
New-Item $Output -ItemType Directory -Force | Out-Null
$msix = Join-Path (Resolve-Path $Output) "RoboMouse-$Version-x64.msix"
if (Test-Path $msix) { Remove-Item $msix }
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Host "Package: $msix"

# --- Optional self-signed signing for sideload testing -------------------------------------------
if ($Sign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Creating self-signed certificate for $Publisher"
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "RoboMouse sideload" -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

    $cerPath = [System.IO.Path]::ChangeExtension($msix, ".cer")
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Write-Host ""
    Write-Host "Signed. To install on a test machine, first trust the certificate (once, as administrator):"
    Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\Root"
    Write-Host "then double-click the .msix or run: Add-AppxPackage `"$msix`""
}
