<p align="center">
  <img src="docs/icon.png" alt="RoboMouse">
</p>

# RoboMouse

Share one mouse and keyboard across your Windows PCs. Move the cursor to the edge of one screen and
it carries on to the next computer, keyboard and clipboard included. No server, no account:
machines find each other on your network and connect directly, encrypted.

## Features

- **Mouse and keyboard sharing** - cross a screen edge to control another PC; each machine keeps its own pointer speed
- **Clipboard and files** - copy text, images or files on one machine, paste on another
- **UAC prompts and the lock screen** - with the optional desktop service (new in 1.1.0)
- **Automatic discovery** and a drag-and-drop **screen layout**
- **Private** - every connection is authenticated with your pairing code and encrypted (AES-256-GCM)
- **Tray app** - the icon's border shows state (grey = no peers, green = connected, blue = controlling, orange = being controlled, faded = disabled)

## Install

Windows 10/11. No .NET runtime needed. Current version: **1.1.0**, from the
[latest release](https://github.com/timothydodd/RoboMouse/releases/latest).

| Download | What it is |
| --- | --- |
| `RoboMouse-Setup-<ver>.exe` | **Recommended.** The app plus the desktop service for UAC prompts and the lock screen. |
| `RoboMouse-v<ver>-win-x64.zip` | Portable: unzip and run `RoboMouse.App.exe`. No desktop service. |
| `RoboMouse-Service-Setup-<ver>.exe` | The desktop service only, for people who installed RoboMouse from the Microsoft Store. |

RoboMouse is also in the [Microsoft Store](https://apps.microsoft.com/detail/9N4HSV1HP9B0), which updates itself. The
Store version is still 1.0.x; 1.1.0 is on its way. Until it arrives, the Store app cannot use the
desktop service, so use `RoboMouse-Setup` if you need UAC and lock screen control today.

`SHA256SUMS.txt` in each release lists the file hashes.

## Setup

1. Install RoboMouse on each PC. It lives in the system tray.
2. **Settings > Network**: enter the same **pairing code** on every machine.
3. **Settings > Peers**: machines on your network are listed; add one, or add by IP address.
4. **Settings > Layout**: drag each computer to the edge of your screen where it sits.
5. Move the mouse off that edge. Push back through the same edge to return.

The hotkey (default **Ctrl+Alt+M**) always brings control back to the machine in front of you, even
if the other one has stopped responding. Otherwise it turns sharing on or off.

Untick a peer under **Settings > Peers** to switch it off without removing it.

## UAC prompts, elevated windows and the lock screen

RoboMouse runs as a normal user, so by itself it cannot click a UAC prompt, type into a window
running as administrator, or unlock the PC. The tray status tells you when that happens.

The **desktop service** fixes this. It comes with `RoboMouse-Setup`; Store users add it with
`RoboMouse-Service-Setup`. On the machine being controlled, turn on **Settings > General > Control
UAC prompts and the lock screen** and approve the one UAC prompt. It stays off until you do.

What it does, how it is locked down and its limits: [docs/desktop-service.md](docs/desktop-service.md).

## Firewall

Allow these through the firewall (or use **Settings > Network > Allow through Windows Firewall**):

- **TCP 24800** - peer connections
- **UDP 24801** - discovery

## Known limitations

- **Sign-in after a reboot and Ctrl+Alt+Del** are not covered, even with the desktop service.
- **Keyboard layouts.** Keys are sent as virtual key codes, so with different layouts some symbol
  keys produce different characters on the remote.
- **Different subnets.** Discovery does not cross subnets; add such peers by IP.
- **File paste targets.** Explorer, Outlook, Teams and Office accept pasted files; apps that want
  plain file paths (VS Code) do not. Paste into a folder first.
- **Blank cursor after a crash.** Run `RoboMouse.App.exe --restore-cursor`.

## More

- [How it works](docs/how-it-works.md) - screen transitions, encryption, the protocol, file transfer
- [Desktop service](docs/desktop-service.md) - UAC and lock screen support, security boundary
- [Building and releasing](docs/building.md) - build from source, installers, Store package, CI/CD
- [Privacy policy](docs/privacy.md)

## License

MIT. Issues and pull requests are welcome.
