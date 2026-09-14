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
- `MouseHook` / `KeyboardHook` - Low-level Windows hooks via SetWindowsHookEx. Used to detect edge hits and to freeze/swallow local input while controlling a remote. Events carry `IsInjected` so software-generated input is never acted on.
- `RawMouseInput` - Raw Input (WM_INPUT) receiver giving unaccelerated hardware motion counts; this is the only source of motion forwarded to a remote.
- `InputSimulator` - Generates synthetic input via SendInput API. Remote motion is injected as relative `MOUSEEVENTF_MOVE` so the local pointer settings apply.
- `ClipboardManager` - Monitors and syncs clipboard changes

**Network Layer** (`src/RoboMouse.Core/Network/`):
- `PeerDiscovery` - UDP broadcast for automatic peer finding
- `ConnectionListener` - TCP server for incoming connections
- `SecureChannel` - Authenticated, encrypted stream over the socket: ECDH key exchange authenticated with HMACs keyed from the shared pairing code, then AES-256-GCM per frame. Every connection goes through it before any protocol message.
- `PeerConnection` - One TCP connection with a dedicated sender thread (`OutboundQueue`, consecutive motion messages merged) and receiver thread (`MessageFramer`). `Post()` is non-blocking and safe from hooks. Pings each second, exposes `RoundTripMs`, and drops the connection after 5 s without a pong. `IsOutbound` is used to resolve simultaneous connects (keep the one initiated by the smaller machine id).
- `RoboMouseService` retries configured peers every 5 s in the background.

**Protocol** (`src/RoboMouse.Core/Network/Protocol/`):
- Binary message format with 2-byte magic, version, type, length prefix, and timestamp (16-byte header)
- Message types: Handshake, Mouse (relative deltas), Keyboard, Clipboard, CursorEnter/Leave, Ping/Pong
- Protocol version 2; both peers must run the same version

### Control Flow

1. Mouse reaches screen edge → `ScreenInfo.GetEdgeAt()` detects it (from the hook)
2. `RoboMouseService` finds the peer configured at that edge, hides the local cursor, starts `RawMouseInput`, sends `CursorEnterMessage`
3. While controlling: the hook swallows all local mouse/keyboard events; raw motion deltas and button/wheel/key events are posted to the peer
4. Controlled peer places its cursor on the entry edge and injects each delta relatively; it tracks whether its cursor is pinned on the entry edge while the controller keeps pushing into it
5. When pushed through the entry edge, the controlled peer sends `CursorLeaveMessage` with the normalized edge position and releases any held keys/buttons
6. Controller restores its cursor one pixel inside the matching local edge and resumes local control (short cooldown prevents immediate re-entry)

The keyboard hook checks the toggle hotkey (`Hotkey`) before anything else: while controlling it releases control; otherwise it toggles `Enabled`. Hooks stay installed while the service runs so this works when disabled.

Never do per-event file logging on the input path: the hook callback has a system timeout and file I/O at 1000 Hz adds visible latency.

### Windows-Specific

- Target framework: `net10.0-windows`
- Uses Windows Forms for system tray UI
- P/Invoke calls in `NativeMethods.cs` for hooks and input simulation
- Settings stored in `%AppData%/RoboMouse/settings.json`
