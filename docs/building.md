# Building and releasing

## Build, run, test

```bash
git clone https://github.com/timothydodd/RoboMouse.git
cd RoboMouse
dotnet build
dotnet run --project src/RoboMouse.App
dotnet test
```

A normal (JIT) build is fine for development. `RoboMouse.Core` and its tests target plain
`net10.0`; the Windows-only projects set `EnableWindowsTargeting`, so `dotnet build` also works on
Linux and macOS even though the app only runs on Windows.

There are three test projects, all run by CI on Windows:

- `RoboMouse.Core.Tests`: protocol, secure channel and identity keys, accept policy, clipboard
  ordering, crossing guards, settings, file transfer, pipe messages.
- `RoboMouse.App.Tests`: the view models (pairing wizard, pages, peer dialog, notifications, update
  check, diagnostics) against a fake backend, so no hooks, sockets or desktop are needed.
- `RoboMouse.Service.Tests`: the service's caller checks, relay rules and log, through seams that
  stand in for the Win32 process and pipe calls.

To check UI changes without Windows, `tools/RoboMouse.UiPreview` renders every window headlessly
with a fake backend:

```bash
dotnet run --project tools/RoboMouse.UiPreview -- <outDir>
```

## Solution layout

```
src/
  RoboMouse.Core/        Input hooks and injection, networking, protocol, screen edges
  RoboMouse.App/         Avalonia tray application (MVVM, Native AOT)
  RoboMouse.Contracts/   Pipe names and messages shared by app, service and helper
  RoboMouse.Service/     LocalSystem Windows service (see desktop-service.md)
  RoboMouse.Helper/      SYSTEM helper that injects input on the active desktop
tests/
  RoboMouse.Core.Tests/     Core unit tests
  RoboMouse.App.Tests/      View-model tests with a fake backend
  RoboMouse.Service.Tests/  Desktop service tests
tools/
  RoboMouse.UiPreview/   Renders the windows to PNG on any OS
packaging/               MSIX manifest and assets, Inno Setup script, build and test scripts
```

## Native AOT publish

```bash
dotnet publish src/RoboMouse.App -c Release -r win-x64
```

The app project has `PublishAot` enabled, so this compiles a native executable that needs no .NET
runtime. It must run on Windows with the Visual Studio "Desktop development with C++" workload (the
AOT compiler needs the MSVC linker). Output lands in
`src/RoboMouse.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`.

## Installers

`packaging/Build-Installer.ps1` builds two installers from one Inno Setup 6 script
(`packaging/installer/RoboMouse.iss`):

- `RoboMouse-Setup-<ver>.exe`: the app plus the desktop service.
- `RoboMouse-Service-Setup-<ver>.exe`: the service only, for Microsoft Store users. It registers
  the service to trust the Store package, so it needs the `STORE_PACKAGE_NAME` and
  `STORE_PUBLISHER` values and is skipped without them.

Both register the service stopped and manual-start. Upgrades stop the service (waiting until it has
stopped), keep the start type the user chose, and restart it if it was running. The two share one
service: it stays while either is installed and goes with the last. Both create
`%ProgramData%\RoboMouse` (the service log) with a locked-down ACL and refuse a `/DIR=` outside
Program Files (a LocalSystem service must never run from a user-writable folder). The full
installer also adds inbound firewall rules for the app and removes them on uninstall.

`packaging/Test-Installer.ps1` smoke-tests the built installers: silent install, service
registration (quoted path, LocalSystem, manual start, privileges), the ProgramData ACL, firewall
rules, upgrade over itself, a refused `/DIR=`, and a clean uninstall; with `-ServiceInstaller` it
also checks the two installers share the service. It needs an elevated shell and changes the
machine, so run it on a throwaway VM (CI runs it on every tag and manual build).

## Microsoft Store package

`packaging/Build-Msix.ps1` produces the MSIX for Store submission (Native AOT, unsigned; Partner
Center signs it). It needs the identity values from Partner Center > Product identity, as parameters
or as the `STORE_PACKAGE_NAME`, `STORE_PUBLISHER` and `STORE_PUBLISHER_DISPLAY` environment
variables. For a local sideload test run it with `-Sign`, which creates a self-signed certificate
and prints the two commands to install it.

The Store package never contains the desktop service. Listing copy and screenshots are in
`docs/store/`; the privacy policy the listing links to is `docs/privacy.md`.

## Releases

`.github/workflows/build.yml` builds and tests every push and pull request to `main`. Pushing a
`v*` tag then builds the installers, runs the installer smoke test, and only if that passes
publishes a GitHub Release with:

- `RoboMouse-v<ver>-win-x64.zip` (portable Native AOT build)
- `RoboMouse-Setup-<ver>.exe` and `RoboMouse-Service-Setup-<ver>.exe`
- `SHA256SUMS.txt` (verify with `sha256sum -c`)

The unsigned MSIX for Partner Center is a workflow artifact, not part of the release.

The version comes from the tag (`v1.2.3` becomes assembly 1.2.3 and package 1.2.3.0). The
`<Version>` in the csproj files is only the fallback for local builds; bump it when tagging.

Manual runs (Actions > Build > Run workflow) build the installers and MSIX from any branch for
testing. They are never code-signed and are versioned `0.0.<run>`, which the installer refuses to
put over an installed release.

### Code signing

Only tag builds are signed (Azure Trusted Signing). Each job gets only the permissions it needs:
the signing job alone can request an OIDC token, and only the final release job can write to the
repository. Third-party actions (Azure login, Trusted Signing, the release upload) are pinned to commit SHAs. Signing switches itself on once this is
set up in the GitHub repository settings:

- **Environment `release`**: deployment limited to `v*` tags, with a required reviewer. The signing
  job runs in it, so every signed build waits for approval.
- **Environment secrets** (in `release`, not the repository): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
  `AZURE_SUBSCRIPTION_ID`, for an app registration whose federated credential names this repo's
  `release` environment and which has the "Trusted Signing Certificate Profile Signer" role.
- **Variables**: `SIGNING_ENDPOINT`, `SIGNING_ACCOUNT` and `SIGNING_PROFILE` (set last; it is what
  turns signing on).

Without them every signing step is skipped and the outputs are unsigned. The Store identity
secrets (`STORE_PACKAGE_NAME`, `STORE_PUBLISHER`, `STORE_PUBLISHER_DISPLAY`) are needed for the MSIX
and the service-only installer.
