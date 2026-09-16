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
- `ClipboardManager` - Monitors and syncs clipboard changes. Copied files become a `FileOfferSource` (names/sizes plus local paths that never leave the machine). Offers from peers are placed on the clipboard as a `VirtualFileDataObject` (CFSTR_FILEDESCRIPTORW/FILECONTENTS) created on a dedicated `StaWorker` thread so Explorer's reads never block the UI thread or the hooks.

**Network Layer** (`src/RoboMouse.Core/Network/`):
- `PeerDiscovery` - UDP broadcast for automatic peer finding
- `ConnectionListener` - TCP server for incoming connections
- `SecureChannel` - Authenticated, encrypted stream over the socket: ECDH key exchange authenticated with HMACs keyed from the shared pairing code, then AES-256-GCM per frame. Every connection goes through it before any protocol message.
- `PeerConnection` - One TCP connection with a dedicated sender thread (`OutboundQueue`, consecutive motion messages merged) and receiver thread (`MessageFramer`). `Post()` is non-blocking and safe from hooks. Pings each second, exposes `RoundTripMs`, and drops the connection after 5 s without a pong. `IsOutbound` is used to resolve simultaneous connects (keep the one initiated by the smaller machine id).
- `FileTransferClient` - Paste side: opens a second connection (`ConnectionKind.Transfer`) to the peer that announced the offer on first read and pulls 1 MB chunks (`FileRequest`/`FileChunk`). The serving side answers from `_localOffers` in `RoboMouseService`. Offers (and clipboard text/images) are relayed to every other peer, so in a chain the middle machine proxies chunk requests to the source; a content hash stops relays looping round a ring. When the source clipboard changes the old offer is revoked, but both sides keep it readable for a 60 s grace period after its last request (`SweepRetiredOffers`) so a paste already copying is not cut off.
- `RoboMouseService` retries configured peers every 5 s in the background.

**Protocol** (`src/RoboMouse.Core/Network/Protocol/`):
- Binary message format with 2-byte magic, version, type, length prefix, and timestamp (16-byte header)
- Message types: Handshake (carries `ConnectionKind` and listen port), Mouse (relative deltas), Keyboard, Clipboard, FileOffer/FileOfferRevoked/FileRequest/FileChunk, CursorEnter (carries the wrap-around flag)/Leave, InputStatus (controlled side reports a UAC prompt or elevated window), Ping/Pong
- Protocol version 4; both peers must run the same version

### Control Flow

1. Mouse reaches screen edge → `ScreenInfo.GetEdgeAt()` detects it (from the hook)
2. `RoboMouseService` finds the peer configured at that edge, hides the local cursor, starts `RawMouseInput`, sends `CursorEnterMessage`
3. While controlling: the hook swallows all local mouse/keyboard events; raw motion deltas and button/wheel/key events are posted to the peer
4. Controlled peer places its cursor on the entry edge and injects each delta relatively; it tracks whether its cursor is pinned on the entry edge while the controller keeps pushing into it
5. When pushed through the entry edge, the controlled peer sends `CursorLeaveMessage` with the normalized edge position and releases any held keys/buttons
6. Controller restores its cursor one pixel inside the local edge opposite the one the cursor left through and resumes local control (short cooldown prevents immediate re-entry)

With `WrapAround` on, an edge with no peer routes to the peer on the opposite edge (entering from its far side) and the controlled peer hands back from any edge, so two screens form a ring. The app runs as a normal user (no UAC prompt at launch); when the controlled machine cannot apply input (secure desktop or an elevated window in front) it sends `InputStatus` and the controller shows why. Driving the secure desktop needs a service, planned later.

The keyboard hook checks the toggle hotkey (`Hotkey`) before anything else: while controlling it releases control; otherwise it toggles `Enabled`. Hooks stay installed while the service runs so this works when disabled.

Never do per-event file logging on the input path: the hook callback has a system timeout and file I/O at 1000 Hz adds visible latency.

### Distribution

- `packaging/` holds the MSIX manifest (`runFullTrust`, startup task), Store assets, and `Build-Msix.ps1`. `StartupRegistration` picks the startup task when packaged and the Run key otherwise.
- `.github/workflows/build.yml` builds/tests on every push, publishes a Native AOT zip and (with Store secrets set) the MSIX on `v*` tags.

### UI (`src/RoboMouse.App/`)

Avalonia 12 with the Fluent theme, MVVM via CommunityToolkit.Mvvm, XAML views with compiled bindings (`x:DataType` everywhere; `AvaloniaUseCompiledBindingsByDefault` is on). Follows the OS light/dark setting (`RequestedThemeVariant="Default"`) and uses the theme's own `SystemControl*` brushes plus a few app tokens in `Styles/AppStyles.axaml` (cards, nav pane, status colours, the `SettingCard` control theme).

- `ViewModels/`: `SettingsViewModel` (status + navigation) with one `PageViewModel` per page (General, Network, Peers, Layout), `PeerSetupViewModel`, `DebugPanelViewModel`. View models talk to the service only through `Services/IAppBackend` and to the UI only through `Services/IDialogService`, so they can be constructed without hooks, sockets or a desktop.
- `Views/`: `SettingsWindow` (nav pane + the four page `UserControl`s, kept alive so unsaved edits survive switching), `PeerSetupWindow`, `MessageDialog`, `DebugPanelWindow`, plus the custom-drawn `ScreenLayoutControl` and `EdgeHighlightWindow`. `WindowDialogService` implements `IDialogService` for a window. `SettingCard` is the WinUI-style settings row.
- `TrayController` owns the `TrayIcon`, its `NativeMenu` (rebuilt in `NeedsUpdate`) and the `RoboMouseService`; service events arrive on network threads and are marshalled with `Dispatcher.UIThread.Post`. `AvaloniaImageCodec` converts clipboard images between PNG and DIB for the core. Icons come from `FluentIcons.Avalonia` (vector, renders everywhere).
- **Previewing the UI without Windows:** `tools/RoboMouse.UiPreview` renders every window with the headless platform and a fake backend: `dotnet run --project tools/RoboMouse.UiPreview -- <outDir>` writes light and dark PNGs. Pass resource keys after the directory to check what the theme defines. Use it after any UI change.

### Desktop service (UAC / secure desktop) — in progress

Separate from the Store app, shipped in the direct-download installer, to drive the secure desktop
(UAC prompts, lock screen, sign-in) where a normal-user process cannot. See `plans/uac-service.md`
for the design and security boundary. New projects:

- `RoboMouse.Contracts` — dependency-free pipe names, message envelope (`PipeMessage`) and transport
  (`PipeConnection`), shared by app/service/helper. Round-trip tests in the Core test project.
- `RoboMouse.Service` — LocalSystem Windows service (SCM plumbing in `ServiceNative`/`Program`,
  `DesktopMonitor` polls the active desktop/session, `ControlPipeServer` hosts the ACL'd control pipe
  and verifies the caller is RoboMouse.App in the console session). Run with `--console` to test.
- `RoboMouse.Helper` — per-desktop SYSTEM input process the service will spawn (skeleton).

Phase 1 (contracts + skeletons that build) is done; the input abstraction, real input relay, app
integration and installer are Phases 2-5 in the plan. Nothing here is wired into the app's runtime
path yet, and the SYSTEM parts need real-Windows testing (CI only proves they compile).

### Native AOT

- The app project has `PublishAot`; `dotnet publish -r win-x64` produces a native executable. This only works on Windows with the VS C++ build tools, so verify AOT publishes in CI or on the user's machine. `dotnet build` and the tests run anywhere.
- Keep everything AOT-clean: source-generated JSON (`SettingsJsonContext`), `LibraryImport` for P/Invoke, `[UnmanagedCallersOnly]` callbacks for hooks and window procedures, `GeneratedComInterface`/`GeneratedComClass` for COM (`VirtualFileDataObject`). No `System.Windows.Forms`, no `System.Drawing.Common`, no reflection-based serialization or Avalonia bindings.
- `Keys` is the core's own enum with the Windows virtual-key values; the names match the old Windows Forms names so saved hotkeys parse.

### Windows-Specific

- Core targets `net10.0` (marked `SupportedOSPlatform("windows")`); the app targets `net10.0-windows10.0.19041.0` for the Store startup task API.
- `MessageWindow` is a Win32 message-only window with an invoke queue; it backs `RawMouseInput`, the clipboard listener and `StaWorker`.
- P/Invoke declarations live in `NativeMethods.cs`
- Settings stored in `%AppData%/RoboMouse/settings.json`
