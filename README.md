# RoboMouse

A Windows application that lets you share your mouse and keyboard across multiple computers on the same network. Move your cursor to the edge of one screen and it seamlessly transitions to control another computer.

## Features

- **Seamless Mouse/Keyboard Sharing** - Move your mouse to the screen edge to control another computer
- **Automatic Peer Discovery** - Computers on the same network find each other automatically via UDP broadcast
- **Clipboard Synchronization** - Copy on one machine, paste on another
- **Visual Screen Layout Editor** - Drag and drop to arrange your screens
- **System Tray Application** - Runs quietly in the background

## Requirements

- Windows 10/11
- .NET 9.0 Runtime
- Network connectivity between computers

## Getting Started

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

Or build and run the executable directly from `bin/Debug/net9.0-windows/`.

## Usage

1. Run RoboMouse on each computer you want to share
2. Right-click the system tray icon to access the menu
3. Use **Connect to...** to see discovered peers and select their position relative to your screen
4. Move your mouse to the configured edge to start controlling the other computer
5. Move back to the opposite edge to return control to your local machine

### Screen Layout

Use **Screen Layout...** from the tray menu to visually arrange peer screens by dragging them to the desired position relative to your local screen.

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
│   └── RoboMouse.App/         # Windows Forms tray application
│       └── Forms/             # Settings and layout forms
└── tests/
    └── RoboMouse.Core.Tests/  # Unit tests
```

## Network Protocol

RoboMouse uses a custom binary protocol over TCP for low-latency input transmission:

- **Handshake** - Exchange machine info and screen dimensions
- **Mouse Events** - Position, clicks, and scroll
- **Keyboard Events** - Key presses with scan codes
- **Clipboard** - Text and file clipboard data
- **Cursor Control** - Enter/leave notifications

Peer discovery uses UDP broadcast on the local network.

## Firewall Configuration

You may need to allow RoboMouse through your firewall:

- **TCP 24800** - Peer connections
- **UDP 24801** - Peer discovery (broadcast)

## License

MIT License

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.
