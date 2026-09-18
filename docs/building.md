# Building and releasing

## Build, run, test

```bash
git clone https://github.com/timothydodd/RoboMouse.git
cd RoboMouse
dotnet build
dotnet run --project src/RoboMouse.App
dotnet test
```

A normal (JIT) build is fine for development. `RoboMouse.Core` and the tests target plain
`net10.0`, so `dotnet build` and `dotnet test` also work on Linux and macOS even though the app only
runs on Windows.

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
  RoboMouse.Core.Tests/  Unit tests
tools/
  RoboMouse.UiPreview/   Renders the windows to PNG on any OS
packaging/               MSIX manifest and assets, Inno Setup script, build scripts
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

Both register the service stopped and manual-start. Upgrades stop the service, keep the start type
the user chose, and restart it if it was running.

## Microsoft Store package

`packaging/Build-Msix.ps1` produces the MSIX for Store submission (Native AOT, unsigned; Partner
Center signs it). It needs the identity values from Partner Center > Product identity, as parameters
or as the `STORE_PACKAGE_NAME`, `STORE_PUBLISHER` and `STORE_PUBLISHER_DISPLAY` environment
variables. For a local sideload test run it with `-Sign`, which creates a self-signed certificate
and prints the two commands to install it.

The Store package never contains the desktop service. Listing copy and screenshots are in
`docs/store/`; the privacy policy the listing links to is `docs/privacy.md`.

## Releases

`.github/workflows/build.yml` builds and tests every push to `main`. Pushing a `v*` tag publishes a
GitHub Release with:

- `RoboMouse-v<ver>-win-x64.zip` (portable Native AOT build)
- `RoboMouse-Setup-<ver>.exe` and `RoboMouse-Service-Setup-<ver>.exe`
- `SHA256SUMS.txt` (verify with `sha256sum -c`)
- the MSIX, when the Store secrets are set

The version comes from the tag (`v1.2.3` becomes assembly 1.2.3 and package 1.2.3.0). The
`<Version>` in the csproj files is only the fallback for local builds; bump it when tagging.
