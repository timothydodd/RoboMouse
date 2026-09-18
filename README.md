<p align="center">
  <img src="docs/icon.png" alt="RoboMouse">
</p>

# RoboMouse

A Windows application that lets you share your mouse and keyboard across multiple computers on the same network. Move your cursor to the edge of one screen and it seamlessly transitions to control another computer.

## Features

- **Seamless Mouse/Keyboard Sharing** - Move your mouse to the screen edge to control another computer
- **Automatic Peer Discovery** - Computers on the same network find each other automatically via UDP broadcast
- **Clipboard Synchronization** - Copy text, images or files on one machine, paste on another
- **Visual Screen Layout Editor** - Drag and drop to arrange your screens
- **System Tray Application** - Runs quietly in the background; the icon's border colour shows state (grey = no peers, green = connected, blue = controlling another screen, orange = being controlled, faded = disabled)

## Requirements

- Windows 10/11
- Network connectivity between computers

Releases are compiled ahead of time (Native AOT), so no .NET runtime needs to be installed.

## Getting Started

### Releases

Every push to `main` is built and tested on GitHub Actions (see the Actions tab for the
`RoboMouse-win-x64` artifact). Pushing a tag such as `v1.0.0` publishes a zipped Native AOT
build as a GitHub Release automatically.

### Microsoft Store Package

`packaging/Build-Msix.ps1` produces the MSIX for Store submission (Native AOT, unsigned;
Partner Center signs it). It needs the identity values from Partner Center > Product identity,
supplied as parameters or as the `STORE_PACKAGE_NAME`, `STORE_PUBLISHER` and
`STORE_PUBLISHER_DISPLAY` environment variables. The release workflow builds it on every tag when
those are set as repository secrets. For a local sideload test run it with `-Sign`, which creates
a self-signed certificate and prints the two commands to install it.

The privacy policy required by the Store listing is in `docs/privacy.md`.

### Publishing a Release Build

```bash
dotnet publish src/RoboMouse.App -c Release -r win-x64
```

The app project has `PublishAot` enabled, so this compiles it to a native executable. It must run on
Windows with the Visual Studio "Desktop development with C++" workload installed (the AOT compiler
needs the MSVC linker). The output in `src/RoboMouse.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`
runs on any Windows 10/11 machine with no .NET runtime installed.

### Building from Source

```bash
git clone https://github.com/timothydodd/RoboMouse.git
cd RoboMouse
dotnet build
```

### Running

```bash
dotnet run --project src/RoboMouse.App
```

Or build and run the executable directly from `bin/Debug/net10.0-windows10.0.19041.0/`. A normal
(JIT) build like this is fine for development; only `dotnet publish` does the ahead-of-time compile.
`RoboMouse.Core` and the tests target plain `net10.0`, so `dotnet build` and `dotnet test` also work
on Linux and macOS even though the app itself only runs on Windows.

## Usage

1. Run RoboMouse on each computer you want to share
2. Right-click the system tray icon to access the menu
3. Open **Peers** in the tray menu to see machines found on the network and pick which edge of your screen they sit on, or add them by address under **Settings > Peers**
4. Move your mouse to the configured edge to start controlling the other computer
5. Move back to the opposite edge to return control to your local machine

### Screen Layout

Use **Screen layout…** under **Settings > Peers** to arrange peer screens by dragging them to the edge of your local screen where that computer sits.

### Disabling a peer

Untick a peer under **Settings > Peers** (or use **Enabled** in its tray submenu) to switch it off without removing it. A disabled peer keeps its settings but is never connected to, cannot take control of your screen, and its edge behaves like a normal screen edge.

### Settings

- **Machine Name** - Friendly name shown to other peers
- **Listen Port** - TCP port for incoming connections (default: 24800)
- **Discovery Port** - UDP port for peer discovery (default: 24801)
- **Clipboard Sharing** - Enable/disable clipboard sync
- **Start with Windows** - Launch automatically on login

## Architecture

```
RoboMouse.sln
├── src/
│   ├── RoboMouse.Core/        # Core library
│   │   ├── Configuration/     # Settings and peer config
│   │   ├── Input/             # Mouse/keyboard hooks and simulation
│   │   ├── Network/           # Peer discovery and connections
│   │   │   └── Protocol/      # Binary message protocol
│   │   └── Screen/            # Screen edge detection
│   └── RoboMouse.App/         # Avalonia tray application (Native AOT)
│       ├── ViewModels/        # MVVM view models (CommunityToolkit.Mvvm)
│       ├── Views/             # XAML windows and pages, custom-drawn controls
│       └── Styles/            # App styles on top of the Fluent theme
├── tests/
│   └── RoboMouse.Core.Tests/  # Unit tests
└── tools/
    └── RoboMouse.UiPreview/   # Renders the windows headlessly to PNG (any OS)
```

## Copying Files Between Machines

Copy files or folders in Explorer on one machine, move to the other, and paste in Explorer.
Only the names and sizes are sent when you copy; the bytes stream directly from the source
machine when you paste, over a separate connection so mouse input is never delayed. Explorer
shows its usual progress dialog and cancelling it stops the transfer.

Notes:

- The source machine must stay running and reachable until the paste finishes.
- Explorer, Outlook, Teams and Office accept these "virtual file" pastes. Some applications
  only accept plain file paths (VS Code, for instance) and will not see them; paste into a
  folder first.
- Turn it off under Settings > General > Clipboard if you do not want copied files offered.

## Pairing and Security

Every machine has a **pairing code** (Settings > Network). RoboMouse generates one on first
run; enter the same code on each machine you want to link. Connections are authenticated
against the code and all traffic is encrypted (ECDH key exchange authenticated with the
pairing code, AES-256-GCM per frame). A machine with a different code cannot connect, and
nobody on the network can read or inject input.

The code is stored in plain text in `%AppData%\RoboMouse\settings.json`, so protect that
file as you would a password. Use **Generate new** in Settings to rotate it; other machines then need
the new code.

## Network Protocol

RoboMouse uses a custom binary protocol over TCP for low-latency input transmission:

- **Handshake** - Exchange machine info and screen dimensions
- **Mouse Events** - Relative motion in raw hardware counts, clicks, and scroll
- **Keyboard Events** - Key presses with scan codes
- **Clipboard** - Text and file clipboard data
- **Cursor Control** - Enter/leave notifications

Motion is captured on the controlling machine with the Raw Input API and injected on the
controlled machine as relative movement, so the controlled machine applies its own pointer
speed and acceleration exactly as it would for a directly attached mouse. The controlled
machine hands control back when its cursor is pushed through the edge it entered from.
Consecutive motion messages are merged while waiting to send, and a ping every second
measures round-trip time (shown in the Debug Panel).

Peer discovery uses UDP broadcast on the local network.

## Hotkey

The hotkey (default **Ctrl+Alt+M**, Settings > General) is the escape hatch: while you are
controlling another screen it hands control straight back to this machine, even if the
other machine has stopped responding. Otherwise it turns sharing on or off.

## Known Limitations

- **Elevated windows and UAC prompts.** RoboMouse runs as a normal user, so Windows drops input
  aimed at a program running as administrator on the controlled machine, and UAC prompts, the lock
  screen and the sign-in screen are on a secure desktop no ordinary application can drive. When
  that happens the tray status on the controlling PC says so ("Waiting for UAC on Laptop"), the
  cursor still moves, and the hotkey always brings control back. A helper service to cover these
  cases is planned.
- **Keyboard layouts.** Keys are forwarded as virtual key codes, so with different layouts on the
  two machines some symbol keys will produce different characters on the remote.
- **Different subnets.** Automatic discovery uses broadcast and never crosses subnets; add such
  peers by IP and make sure the firewall rule on each side allows any remote address
  (Settings > Network > Allow through Windows Firewall).
- **Blank cursor after a crash.** The cursor is hidden by swapping the system cursors while
  controlling. If RoboMouse is killed at that moment, run `RoboMouse.exe --restore-cursor`.

## Firewall Configuration

You may need to allow RoboMouse through your firewall:

- **TCP 24800** - Peer connections
- **UDP 24801** - Peer discovery (broadcast)

## License

MIT License

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.
