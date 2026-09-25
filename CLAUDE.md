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
- `RawMouseInput` - Raw Input (WM_INPUT) receiver giving unaccelerated hardware motion counts; the normal source of motion forwarded to a remote. Windows stops delivering raw input to a normal-user process while an elevated window is in the foreground (e.g. an app launched by a scheduled task), so when raw input goes silent while controlling, `RoboMouseService` falls back to the hook's blocked moves (position minus the parked point) until raw input resumes.
- `InputSimulator` - Generates synthetic input via SendInput API. Remote motion is injected as relative `MOUSEEVENTF_MOVE` so the local pointer settings apply. Remote keys are injected by scan code (`KeyInjection`: `KEYEVENTF_SCANCODE`, VK only for keys with no or an ambiguous scan code, `KEYEVENTF_UNICODE` for `Keys.Packet`), so the controlled machine's own keyboard layout applies.
- `ClipboardManager` - Monitors and syncs clipboard changes. Copied files become a `FileOfferSource` (names/sizes plus local paths that never leave the machine). Offers from peers are placed on the clipboard as a `VirtualFileDataObject` (CFSTR_FILEDESCRIPTORW/FILECONTENTS) created on a dedicated `StaWorker` thread so Explorer's reads never block the UI thread or the hooks.

**Network Layer** (`src/RoboMouse.Core/Network/`):
- `PeerDiscovery` - UDP broadcast for automatic peer finding. Broadcasts carry the sender's identity key and are signed with it; one using a paired peer's id with another key is not listed.
- `ConnectionListener` - TCP server for incoming connections
- `SecureChannel` - Authenticated, encrypted stream over the socket; every connection goes through it before any protocol message. Ephemeral ECDH, and each side signs the transcript with its long-lived `IdentityKey` (ECDSA P-256, private key DPAPI-protected in `%AppData%\RoboMouse\identity.key`). Pairing mode (first contact) also proves the pairing code (HMACs, connecting side first; PBKDF2 salt names the protocol version); the caller then pins the peer's key in `PeerConfig.IdentityKey`. Pinned mode (both sides pinned each other) needs no code, so changing the code only affects new pairings. A key that differs from the pinned one is refused (`IdentityMismatchException` / `RejectCode.IdentityMismatch` → `PeerFailureKind.IdentityMismatch`); `RoboMouseService.ForgetPeerIdentity` re-pairs. No PAKE: only generated codes (`PairingCode.IsStrong`) resist offline guessing. Then AES-256-GCM per frame.
- `PeerConnection` - One TCP connection with a dedicated sender thread (`OutboundQueue`: consecutive motion messages merged, pings first, clipboard chunks in a bulk lane one per round) and receiver thread (`MessageFramer`; the buffer shrinks back after a big message). `Post()` is non-blocking and safe from hooks. Pings each second, exposes `RoundTripMs`, and drops the connection after 5 s without a pong. `IsOutbound` is used to resolve simultaneous connects (keep the one initiated by the smaller machine id).
- `FileTransferClient` - Paste side: opens a second connection (`ConnectionKind.Transfer`) to the peer that announced the offer on first read and pulls 1 MB chunks (`FileRequest`/`FileChunk`). The serving side answers from `_localOffers` in `RoboMouseService`. Offers (and clipboard text/images) are relayed to every other peer, so in a chain the middle machine proxies chunk requests to the source. Every clipboard change carries an origin id + sequence stamp (`ClipboardStamps`): a machine applies and relays only what is newer than its current content, so nothing loops round a ring and all machines converge. Text/images over 256 KB travel as `ClipboardChunk`s (`ClipboardAssembler` reassembles). `ClipboardSettings.Allows` applies the text/image/file switches and `MaxSizeBytes` both ways; `ApplyClipboardSettings()` applies changes live. Per peer, `PeerConfig.ShareClipboard` (`ClipboardSharing.SharesWith`) gates content and file offers in both directions and on relays: a peer with it off is sent nothing and nothing it sends is applied or passed on (revokes still go to everyone; the serving side refuses its file requests). When the source clipboard changes the old offer is revoked, but both sides keep it readable for a 60 s grace period after its last request (`SweepRetiredOffers`) so a paste already copying is not cut off.
- `RoboMouseService` retries configured peers every 5 s in the background.
- `WakeOnLan` - each side reports the MAC address of the adapter the connection uses in a trailing, optional handshake field (older builds ignore it, so no protocol bump); it is saved in `PeerConfig.MacAddress`. Leaning on the edge of a configured peer that is not connected (`WakeOnEdge` setting) or the tray's "Wake" item broadcasts the magic packet from the thread pool, never from the hook; the 5 s retry reconnects once the peer is up.

**Locking together**: every machine also announces its session lock and screen-saver state (`SessionStateMessage`, on connect and on change; the screen saver is polled every 2 s with `SPI_GETSCREENSAVERRUNNING`). With `LockWithHost` / `ScreensaverWithHost`, a machine follows the host's lock (`LockWorkStation`) and screen saver (`SC_SCREENSAVE`, skipped if it was used in the last minute). "Lock all PCs" (tray item, `LockAllHotkey`) posts `LockRequestMessage` to every peer and locks this PC. `LockPolicy` decides both: only a configured, enabled peer that proved its pinned identity key is obeyed.

**Power** (`src/RoboMouse.Core/Power/`): every machine announces its display/sleep state in `PowerStateMessage` (`PowerMonitor`: display-state and suspend notifications on a message-only window) on connect and on change; unknown message types are skipped by older builds, so no protocol bump. With `FollowHostPower` on, `PowerFollower` mirrors the peer that last controlled this machine (the host): a `PowerCreateRequest` hold for system + display while the host's display is on, system only while it is off, `SC_MONITORPOWER` off when the host's display turns off or it suspends (skipped if this machine saw input in the last minute). Controlling the host back stops following it, so two machines never hold each other awake. A host that just disconnects only releases the hold.

**Protocol** (`src/RoboMouse.Core/Network/Protocol/`):
- Binary message format with 2-byte magic, version, type, length prefix, and timestamp (16-byte header)
- Message types: Handshake (carries `ConnectionKind` and listen port), Mouse (relative deltas), Keyboard (VK + scan code + extended flag; `Keys.Packet` carries a UTF-16 character in the scan code), Clipboard and FileOffer (both carry `OriginId` + `Sequence`), ClipboardChunk, FileOfferRevoked/FileRequest/FileChunk, ScreenInfo (the sender's monitors), VirtualLayout (the controller's layout for the receiver), CursorEnter (monitor id + normalized point + wrap-around flag)/CursorLeave (a point on the controller's layout, or `Released`), CursorLock (controller locked the cursor to the controlled screen), InputStatus (controlled side reports a UAC prompt or elevated window), PowerState, SessionState (locked / screen saver running), LockRequest ("Lock all PCs"), Ping/Pong
- `Message.Deserialize` returns null for a malformed payload instead of throwing, so a bad message is skipped
- Protocol version 6 (secure handshake version 3; the handshake format is unchanged since 2, the number moved so a protocol 5 peer reports a version mismatch instead of a failed signature); both peers must run the same version. An older peer is answered with our handshake version and sees "incompatible version"; this side reports "update RoboMouse on both machines" (`PeerFailureKind.VersionMismatch`)

### Layout

Each PC owns its own layout (`RoboMouseService.Layout.cs`): its monitors where Windows has them plus every peer monitor wherever it was placed, in one coordinate space (this PC's virtual-screen pixels). `MonitorLayout` monitors carry a device-name `Id` (`\\.\DISPLAY1`) and `Scale`. A peer's placements are `PeerConfig.Monitors` (`MonitorPlacement`: layout rect + the peer's own rect and scaling), replaced as a whole, never edited in place. `PeerConfig.Position`/`OffsetX`/`OffsetY` only seed the first placement. `PlacementPlanner` (pure) reconciles placements with what a peer reports: sizes follow its resolution and scaling (`LayoutSize`: pixels x host main scale / monitor scale), new monitors go beside their neighbours as the peer arranges them, a new peer's monitors go as a group on its `Position` side, nothing overlaps (`FindFree`), and placements of unplugged monitors are kept. `VirtualDesktop` (pure, immutable, swapped whole) resolves crossings: only a screen that touches the edge at that point is reached (`Resolve`), wrap-around goes to the farthest screen on the row/column; `Snap` is the Layout page's drop rule. Every machine sends `ScreenInfo` on connect and when its monitors change (polled every 2 s, `CheckLocalDisplays`); the receiver reconciles, saves, rebuilds the desktop and sends every peer a `VirtualLayout` (its own monitors by id, other connected screens unnamed). A peer cannot be entered until its monitors have arrived on the current connection. `ScreensChanged` tells the UI (`IAppBackend.ScreensVersion`, read on the settings timer).

### Control Flow

1. Mouse reaches an outer edge of a local monitor → `ScreenInfo.GetEdgesAt()` detects it (from the hook). `MonitorLayout` (`Screen/`) holds the per-monitor geometry: an outer edge is a monitor edge with no other monitor beyond it, so unequal or staggered monitors work. `ScreenInfo` re-reads the arrangement when its copy is over a second old (message-only windows never get `WM_DISPLAYCHANGE`), so plugging in a monitor or changing the main display is picked up while running
2. `RoboMouseService` resolves that point on the layout (`_desktop.Resolve`; with `WrapAround`, a second pass wraps). A connected peer's monitor there is the target; a disconnected one's is a Wake-on-LAN candidate. Then it asks `CrossingGuard` (pure, hook thread only; settings in `AppSettings.Crossing`) whether it may cross: not while a mouse button is held (default on; `ButtonsReallyHeld` confirms with `GetAsyncKeyState`), not within `CornerDeadZone` px of where the outer edge ends (`MonitorLayout.IsNearEdgeEnd`, default 20), optionally only on a second push within 500 ms and/or after pushing for `DelayMs` (either suffices), only while a required modifier is held, and not while a full-screen program is in front (`FullScreenDetector` polls `SHQueryUserNotificationState` + foreground-window-covers-monitor on a timer only while that guard is on; the hook reads the cached answer). Nothing crosses while the cursor lock is on (`CursorLocked`). With `Crossing.PushDistance` set, an allowed crossing waits (`_pendingCrossing`, followed along the edge) while raw input measures the push into the edge (`EdgePush`; Windows pins the cursor so the hook cannot); the controller also sends it in `CursorEnter.HandBackPush` so the controlled side needs the same push to hand back. Then it hides the local cursor, starts `RawMouseInput`, sends `CursorEnterMessage` (monitor id + point)
3. While controlling: the hook swallows all local mouse/keyboard events; raw motion deltas and button/wheel/key events are posted to the peer
4. Controlled peer places its cursor on that monitor and injects each delta relatively. After each delta `ControlledCrossing` (pure) checks the cursor against the controller's `VirtualLayout`: pinned against an edge with another of its own monitors beyond on the layout → `MoveTo` there; a move Windows made that the layout disagrees with → undone or corrected; pushed `ReturnOvershootCounts` into someone else's screen → hand back. Without a layout yet, any outer edge hands back `Released`
5. The hand-back `CursorLeaveMessage` names the point on the controller's layout; the controlled peer releases any held keys/buttons
6. Controller: a point on one of its monitors → cursor placed there one pixel in from the edge and local control resumes (short cooldown prevents immediate re-entry); a point on another peer's monitor → control moves straight on to that peer; `Released` or nothing there → back where it left (`PutCursorBackAtExit`)

The app runs as a normal user (no UAC prompt at launch); when the controlled machine cannot apply input (secure desktop or an elevated window in front) it sends `InputStatus` and the controller shows why. The optional desktop service (below) drives the secure desktop.

The keyboard hook checks the global hotkeys (`HotkeySet`, rebuilt by `ApplyHotkeySetting()` whenever they or the peer list change) before anything else. The toggle hotkey comes first and wins any clash: while controlling it releases control; otherwise it toggles `Enabled`. `LockCursorHotkey` (default Scroll Lock; Scroll Lock, Pause and F13-F24 may be hotkeys without a modifier) locks the cursor to its screen: no crossing from here, and while controlling, the peer gets `CursorLockMessage` and stops handing the cursor back. Each peer's jump hotkey (`PeerConfig.JumpHotkey`; null means Ctrl+Alt+F1-F4 by list position, empty means none) enters that peer at the middle of its edge, from here or from another peer. `LockAllHotkey` runs "Lock all PCs" off the hook. `SuspendHotkeys()` (used by `HotkeyBox` while it has focus) stops them acting so a chord can be recorded. Hooks stay installed while the service runs so this works when disabled.

Never do per-event file logging on the input path: the hook callback has a system timeout and file I/O at 1000 Hz adds visible latency.

### Distribution

- `packaging/` holds the MSIX manifest (`runFullTrust`, startup task), Store assets, and `Build-Msix.ps1`. `StartupRegistration` picks the startup task when packaged and the Run key otherwise.
- `packaging/Build-Installer.ps1` + `packaging/installer/RoboMouse.iss` (Inno Setup 6) build the direct-download installers: `RoboMouse-Setup` (app + desktop service) and `RoboMouse-Service-Setup` (service only, for Store users; needs the Store identity secrets to derive the package family name the service trusts). The Store package never contains the service. `Install-DevService.ps1` is the dev stand-in. `Test-Installer.ps1` is the installer smoke test (CI job `installer-smoke`, gates the GitHub release).
- `.github/workflows/build.yml` builds/tests on every push; on `v*` tags it builds the Native AOT zip and installers (signed only on tags, in the `release` environment) and the MSIX (workflow artifact only), and publishes the GitHub release once `installer-smoke` passes. The version comes from the tag (`v1.2.3` → assembly 1.2.3, package 1.2.3.0) and is passed as `-p:Version`; the `<Version>` in the csproj files is only the fallback for local builds, so bump it when tagging.

### UI (`src/RoboMouse.App/`)

Avalonia 12 with the Fluent theme, MVVM via CommunityToolkit.Mvvm, XAML views with compiled bindings (`x:DataType` everywhere; `AvaloniaUseCompiledBindingsByDefault` is on). Follows the OS light/dark setting (`RequestedThemeVariant="Default"`) and uses the theme's own `SystemControl*` brushes plus a few app tokens in `Styles/AppStyles.axaml` (cards, nav pane, status colours, the `SettingCard` control theme).

- `ViewModels/`: `SettingsViewModel` (status + navigation, Save applies everything live) with one `PageViewModel` per page (General, Peers, Layout, Network, Advanced, About). General holds what most people set once; `AdvancedPageViewModel` the tuning (crossing guards, lock hotkeys, clipboard size limit, Wake-on-LAN, desktop service, arrival cue, debug panel). `LayoutPageViewModel` has one `LayoutItem` per monitor (this PC's fixed, peers' movable; a peer never seen since it was added is a fixed placeholder at its `Position`), keeps unsaved moves across reloads, and `Save` replaces `PeerConfig.Monitors`. Also `PeerSetupViewModel` (offers a side only until the peer's monitors are placed), `PairingWizardViewModel` (first run / no peers), `ToastViewModel`, `DebugPanelViewModel`. View models talk to the service only through `Services/IAppBackend` (which also saves the settings file, so tests never write the real one) and to the UI only through `Services/IDialogService`, so they can be constructed without hooks, sockets or a desktop.
- `Services/`: `Notifications` (every toast's wording) shown by `Views/ToastPresenter` as non-activating windows by the tray (the tray icon has no balloons); `UpdateChecker` (GitHub latest release, direct-download builds only, daily); `Diagnostics` (log folder, redacted diagnostics zip, `crash.txt`); `AppState` (`app.json`: app-only settings such as the update check). The pairing code is only ever generated or copied from another PC (`PairingCode.TryFormat`; strength is `PairingCode.IsStrong`, the single rule), never free text. The Network page shows this PC's identity fingerprint, the peer dialog the peer's; a peer failing with `IdentityMismatch` gets a "Pair again" button on the Peers page (`ForgetPeerIdentityAsync`).
- `Views/`: `SettingsWindow` (nav pane + the six page `UserControl`s, kept alive so unsaved edits survive switching), `PeerSetupWindow`, `PairingWizardWindow`, `ToastWindow`, `MessageDialog`, `DebugPanelWindow`, `HotkeyBox`, plus the custom-drawn `ScreenLayoutControl` and `EdgeHighlightWindow`. `WindowDialogService` implements `IDialogService` for a window. `SettingCard` is the WinUI-style settings row.
- `TrayController` owns the `TrayIcon`, its `NativeMenu` (rebuilt in `NeedsUpdate`) and the `RoboMouseService`; service events arrive on network threads and are marshalled with `Dispatcher.UIThread.Post`. `AvaloniaImageCodec` converts clipboard images between PNG and DIB for the core. Icons come from `FluentIcons.Avalonia` (vector, renders everywhere).
- **Previewing the UI without Windows:** `tools/RoboMouse.UiPreview` renders every window with the headless platform and a fake backend: `dotnet run --project tools/RoboMouse.UiPreview -- <outDir>` writes light and dark PNGs. Pass resource keys after the directory to check what the theme defines. Use it after any UI change.

### Desktop service (UAC / secure desktop)

Separate from the Store app, shipped in the direct-download installer, to drive the secure desktop
(UAC prompts, lock screen, sign-in) where a normal-user process cannot. See `plans/uac-service.md`
for the design and security boundary. Projects:

- `RoboMouse.Contracts` — dependency-free pipe names, message envelope (`PipeMessage`) and transport
  (`PipeConnection`, frames capped at `PipeNames.MaxFrameLength`, 64 KB), shared by app/service/helper.
  Pipe protocol version is `PipeNames.ProtocolVersion` (3), checked in `Hello`.
- `RoboMouse.Service` — LocalSystem Windows service (SCM plumbing in `ServiceNative`/`Program`).
  `ControlPipeServer` hosts the ACL'd control pipe and verifies the caller is RoboMouse.App in the
  console session (`CallerPolicy`: kernel image path from one held process handle, creation time for
  pid reuse, Authenticode signer equal to the service's own when it is signed; `--app-path`, or
  `--package-family` plus the package install folder for the Store app). While an app is connected,
  `ServiceWorker` keeps one `HelperHost` alive in the app's session (restricted token, powerful
  privileges removed) and relays injection commands, nothing before a version-checked `Hello` and
  only well-formed ones. `--console` runs the worker for debugging, but the app refuses it:
  `DesktopServiceControl.VerifyPipeServer` only accepts the pipe server the SCM started for
  `RoboMouseService` (session 0, LocalSystem). Test with `Install-DevService.ps1`.
- `RoboMouse.Helper` — SYSTEM process in the user's session. **Injection only** (no hooks, no raw
  input): one thread applies commands and follows the input desktop with `SetThreadDesktop`
  (`InputDesktop`), so it reaches UAC prompts and the lock screen.
- Core: everything applied on a controller's behalf goes through `IInputInjector`.
  `DesktopServiceInjector` routes over the pipe when the service is ready and falls back to
  `InProcessInjector` otherwise; `DesktopServiceControl` detects/starts the service.
  `GetCursorPosition` (asked after every motion delta) answers from this process with `GetCursorPos`
  and only asks the helper when that fails (secure desktop): a pipe round trip per delta backed up
  the receive thread and made the cursor lag. `LagMonitor` logs a `Lag` line (at most every 2 s)
  when motion arrives late or is slow to apply on the controlled side. Setting:
  `UseDesktopService`; the Advanced page card only shows when the service is installed.

Shipped since 1.1.0 and hardened in 1.2.0 (`plans/audit-fixes.md` Phase 1); the input path is
verified on real Windows. Pipe servers must keep a non-zero buffer (`PipeNames.BufferSize`):
unbuffered, both ends block sending `Hello`, and Linux pipes hide that. Not covered yet: sign-in
after a reboot (no app is running to connect) and Ctrl+Alt+Del.

### Native AOT

- The app project has `PublishAot`; `dotnet publish -r win-x64` produces a native executable. This only works on Windows with the VS C++ build tools, so verify AOT publishes in CI or on the user's machine. `dotnet build` and the tests run anywhere.
- Keep everything AOT-clean: source-generated JSON (`SettingsJsonContext`), `LibraryImport` for P/Invoke, `[UnmanagedCallersOnly]` callbacks for hooks and window procedures, `GeneratedComInterface`/`GeneratedComClass` for COM (`VirtualFileDataObject`). No `System.Windows.Forms`, no `System.Drawing.Common`, no reflection-based serialization or Avalonia bindings.
- `Keys` is the core's own enum with the Windows virtual-key values; the names match the old Windows Forms names so saved hotkeys parse.

### Windows-Specific

- Core targets `net10.0` (marked `SupportedOSPlatform("windows")`); the app targets `net10.0-windows10.0.19041.0` for the Store startup task API.
- `MessageWindow` is a Win32 message-only window with an invoke queue; it backs `RawMouseInput`, the clipboard listener and `StaWorker`.
- P/Invoke declarations live in `NativeMethods.cs`
- Settings stored in `%AppData%/RoboMouse/settings.json`; the identity key (DPAPI, current user) in `%AppData%/RoboMouse/identity.key`
