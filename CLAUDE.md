# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build entire solution
dotnet build

# Build specific project
dotnet build src/RoboMouse.Core
dotnet build src/RoboMouse.App

# Run the application
dotnet run --project src/RoboMouse.App

# Run all tests
dotnet test

# Run specific test
dotnet test --filter "FullyQualifiedName~HandshakeMessage_RoundTrip"
```

## Architecture

RoboMouse is a Windows application for sharing mouse/keyboard between computers. It uses a client-server model where any peer can initiate connections.

### Core Components

**RoboMouseService** (`src/RoboMouse.Core/RoboMouseService.cs`) - Central orchestrator that:
- Manages peer connections and discovery
- Installs/uninstalls input hooks based on enabled state
- Routes messages between local input and remote peers
- Handles cursor transitions when mouse hits screen edges

**Input Layer** (`src/RoboMouse.Core/Input/`):
- `MouseHook` / `KeyboardHook` - Low-level Windows hooks via SetWindowsHookEx
- `InputSimulator` - Generates synthetic input via SendInput API
- `ClipboardManager` - Monitors and syncs clipboard changes

**Network Layer** (`src/RoboMouse.Core/Network/`):
- `PeerDiscovery` - UDP broadcast for automatic peer finding
- `ConnectionListener` - TCP server for incoming connections
- `PeerConnection` - Manages individual TCP connection with message framing

**Protocol** (`src/RoboMouse.Core/Network/Protocol/`):
- Binary message format with 4-byte magic header, message type, and length prefix
- Message types: Handshake, Mouse, Keyboard, Clipboard, CursorEnter/Leave, Ping/Pong

### Control Flow

1. Mouse reaches screen edge → `ScreenInfo.GetEdgeAt()` detects it
2. `RoboMouseService` finds peer configured at that edge
3. Sends `CursorEnterMessage` to peer, starts forwarding input
4. Remote peer receives enter message, begins accepting input simulation
5. When cursor returns to opposite edge, sends `CursorLeaveMessage`

### Windows-Specific

- Target framework: `net9.0-windows`
- Uses Windows Forms for system tray UI
- P/Invoke calls in `NativeMethods.cs` for hooks and input simulation
- Settings stored in `%AppData%/RoboMouse/settings.json`
