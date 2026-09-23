# Audit fixes: security, reliability, UX and missing features

Status: **implemented in 1.2.0** (all six phases, shipped together; see `git log 5afa42b..v1.2.0`).
Comes from the September 2026 code audit (core, network, app, desktop service/installer/CI). Findings
were read against the code; the ones marked ✔ were re-checked by hand.

Not done:
- the Phase 6 **nice-to-have** list (drag-and-drop files across screens, localization, opt-in crash
  reporting, sign-in after reboot / Ctrl+Alt+Del, macOS/Linux clients, cross-subnet rendezvous);
- **virtual layout** / more than one peer per edge (6.10, `plans/virtual-layout.md`);
- 1.7 `sc sidtype restricted`: see deviations;
- 6.5 "Download and install": the update check only notifies and opens the release page; nothing is
  downloaded or run.

Deviations from the plan:
- **No PAKE** (3.1): as decided there; generated codes only, version in the PBKDF2 salt, and the
  code is needed only when pairing thanks to pinned identity keys. Per-pair salts (3.3) were not
  needed: pinned connections do not use the code at all.
- **Protocol 4 peers** get a one-byte handshake version answer (so they report "incompatible
  version"), not a reject reason; nothing is proved to them.
- **Clipboard loops** (3.5): every change carries an origin id + a clock-based sequence stamp and a
  machine applies only what is newer than its current content (`ClipboardStamps`), instead of a
  per-origin sequence table and recent-hash set.
- **Authenticated discovery** (3.6): broadcasts are signed with the identity key (ECDSA) rather
  than carrying an HMAC.
- **Service SID type** (1.7) stays `unrestricted`: a restricted (write-restricted) token would also
  bind the helper and deny it the Winlogon desktop rights `SendInput` needs. Privileges are limited
  with `sc privs`. Revisit after a test on real hardware.

Phases were ordered by risk; the numbering is kept for reference.

Each phase lists its tests; nothing in a phase is done until they pass.

---

## Phase 1 — Desktop service security (1.1.5)

The service's security boundary (`plans/uac-service.md` §Security boundary) does not hold today: any
process running as the console user can drive UAC prompts through the SYSTEM helper.

1. ✔ **Caller check reads a spoofable path.** `ControlPipeServer.IsCallerAllowed` uses
   `Process.MainModule.FileName` (PEB data the caller can rewrite). Replace with:
   - `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` once, keep the handle for the whole check
     (also closes the PID-reuse window, finding 5 below);
   - `QueryFullProcessImageNameW` for the path;
   - Authenticode check (`WinVerifyTrust` + signer thumbprint/subject equal to the service's own
     signer). Skip the signer check only for the dev service (`Install-DevService.ps1`), which already
     warns that it is dev-only.
2. **Store identity check only compares the file name.** Also require the image path to sit under
   the package's install folder (`GetPackageFullName` → `GetPackagePathByFullName`).
3. **PID reuse.** Compare the process creation time from the held handle before and after the checks;
   also check the pipe client's session (`GetNamedPipeClientSessionId`) matches.
4. ✔ **SYSTEM log in a user-creatable folder.** `Service/Log.cs` writes `%ProgramData%\RoboMouse\service.log`.
   - Installer creates `%ProgramData%\RoboMouse` with an explicit ACL (SYSTEM + Administrators full,
     Users read), owner Administrators.
   - At start the service refuses to log there if the folder is a reparse point or not owned by
     SYSTEM/Administrators (falls back to the event log).
   - Cap the log (roll at 1 MB, keep one `.1`). Rate-limit "Pipe client is ..." rejections.
5. **App trusts any pipe server.** In `DesktopServiceInjector`, after connecting:
   `GetNamedPipeServerProcessId` → must equal the service's PID from `QueryServiceStatusEx`, and that
   process's token must be LocalSystem. Connect with `TokenImpersonationLevel.Identification`.
   Server side creates the first instance with `FILE_FLAG_FIRST_PIPE_INSTANCE`.
6. **Relay only what makes sense.**
   - Require `Hello` (version-checked) before relaying anything (`ServiceWorker.cs:72-98`).
   - Validate payload lengths per opcode in the service before relaying; the helper also validates
     and ignores bad commands instead of throwing (`Helper/Program.cs:72-95`).
   - Cap pipe message size (64 KB) in `PipeConnection`.
7. **Least privilege.**
   - Helper launched with a restricted token (drop SeDebug, SeTcb, SeImpersonate, SeLoadDriver,
     SeBackup/Restore, SeTakeOwnership); SYSTEM identity is enough for the Winlogon desktop.
   - Installer: `sc sidtype RoboMouseService restricted` and `sc privs` limited to what `HelperHost`
     needs (SeTcb, SeAssignPrimaryToken, SeIncreaseQuota).
8. **Robustness.**
   - Helper restart: exponential backoff (1 s → 30 s), stop retrying on a missing exe.
   - Held input released by the service side too: track key/button downs relayed, and on helper
     loss/restart send the ups to the new helper.
   - Second console user (fast user switching): when the console session changes, drop the old app
     connection so the new user's app can connect (`ControlPipeServer.cs:61-64`).
   - `DesktopMonitor` in session 0 always falls back to "Winlogon"; report the helper's view instead,
     or remove `DesktopChanged` (the app ignores it).
   - App side: bound the outbound queue (drop motion first) in `DesktopServiceInjector`.
9. **Say what the feature means.** The General-page card for the desktop service and
   `plans/uac-service.md` state plainly: "while this is on, a program running as you could approve UAC
   prompts on this PC".

Tests (new `RoboMouse.Service.Tests` or in Core tests via an `IProcessInfo` seam):
- caller check: wrong session, wrong path, Store identity from a folder outside the package, exited
  client, PID reused (creation time changed), unsigned/wrong-signer exe;
- pipe framing: truncated payload per opcode, length 0 / negative / > 64 KB, partial read at EOF;
- `ServiceWorker` with a fake helper: nothing relayed before `Hello`, version mismatch disconnects,
  order preserved, `HelperLost` sent when the helper dies, held input released on helper restart;
- `DesktopServiceInjector`: rejects a pipe server not owned by the service, falls back in-process on
  `HelperLost`, reconnects after the service restarts.

---

## Phase 2 — Network accept policy and core reliability (1.1.5 / 1.1.6)

### 2a. Who may connect (no protocol change)

1. ✔ **Unknown machines can take control.** `OnIncomingConnection` only rejects configured *and*
   disabled peers. New rule:
   - Control connections are accepted only from configured, enabled peers.
   - An unknown machine that has the code is held as a **pending peer**: tray notification "TIM-WORK
     wants to connect — Allow / Ignore", and it shows on the Peers page. Allow adds it (placed on a
     free edge, then the layout page opens); Ignore drops it for this session.
   - Removed peers go on a `BlockedMachineIds` list so they do not silently come back
     (`SettingsViewModel.RemovePeerAsync`); re-adding by hand removes them from it.
   - Transfer and Probe connections get the same "configured and enabled" check (Probe may stay open
     to pending peers, it only answers pings).
   - Reject our own machine id in the handshake (adding this PC's own IP connects to itself today).
2. **Duplicate peers.** `PeerSetupViewModel.ConfirmAsync` refuses an address:port or machine id that
   is already configured. After a connect, if `peerConfig.Id` is overwritten to an id another config
   already has, merge the two (`RoboMouseService.cs:344`).
3. **Changing the pairing code drops existing connections** (they were authenticated with the old
   one), and says so in the Network page.

Note: a peer claiming someone else's machine id is only fully fixed by Phase 3's pinned identity keys.
Until then the accept policy narrows it to machines that know the code.

### 2b. Stuck control and stuck input

4. ✔ **Replaced duplicate connection leaves control state on the dead one**
   (`RoboMouseService.AddConnection`, `:495-524`). After swapping, if `_activeConnection == replaced`
   call `EndRemoteControl(false)`; if `_controllerConnection == replaced` call `EndBeingControlled(false)`.
5. **Race in `StartRemoteControl`.** Assign `_activeConnection` under `_connectionLock` after
   re-checking the connection is still registered and connected; `RemoveConnection`'s active check
   takes the same lock.
6. **Crossing with a button or modifier held** leaves it down locally (window dragged to screen
   centre when the cursor is parked). Track physical button/modifier state from the hooks; refuse the
   crossing while a mouse button is down (this is also the "don't cross while dragging" guard, see
   Phase 6). For held modifiers, inject local key-ups before parking and forward the downs to the peer
   so the chord still works there.
7. **Controlled side accepts `CursorEnter` when it should not.** Ignore it (and answer `CursorLeave`)
   when sharing is disabled or this machine is controlling someone else. A `CursorEnter` from a
   different peer first ends being controlled by the current one (releases held input).
8. **Held-input bookkeeping races.** One lock around `_heldKeys`, `_heldButtons` and
   `_isControlledByRemote`, including the check-then-inject in the receive path.
9. **Hotkey fires on a plain key after the secure desktop.** At match time, confirm modifiers with
   `GetAsyncKeyState` when not controlling. Register `WTSRegisterSessionNotification` on the message
   window; on lock/unlock clear `ModifierState` and, if controlling, send key-ups for everything
   forwarded as down.
10. When the peer drops while controlling, put the cursor back at the edge it left through, not the
    parked centre point.

### 2c. Settings safety

11. ✔ **Atomic save.** `AppSettings.Save` writes `settings.json.tmp`, then `File.Replace` keeping
    `settings.json.bak`. One static lock around Save; serialize from a snapshot so `Peers` can't change
    underneath.
12. ✔ **Never overwrite an unreadable file.** On a parse failure in `Load`: rename it to
    `settings.corrupt-<timestamp>.json`, try `.bak`, and only then fall back to defaults. Raise a flag the
    app shows once ("Settings could not be read; restored from backup" / "reset").
13. Background saves (`RememberMacAddress`, `SetPeerEnabledAsync`) go through the same locked Save and
    are wrapped in try/catch.
14. App: hook `Dispatcher.UIThread.UnhandledException` and `TaskScheduler.UnobservedTaskException`;
    log and show a dialog instead of dying.

### 2d. Startup and connection robustness

15. **Port already in use kills the app silently** (24800 is also Synergy/Barrier/Input Leap's
    default). `ConnectionListener.Start` / `PeerDiscovery` bind failures are caught; the service keeps
    running with the tray up, reports `ListenerError`, and the tray shows "Port 24800 is in use —
    change it in Settings".
16. **Reconnect can hang forever.** `SecureChannel.ReadAsync` ignores cancellation once reading.
    Register the token to dispose the socket; the whole outbound connect + handshake + ack runs under
    one 10 s timeout; inbound `AcceptAsync` likewise. Move socket creation inside the try/dispose
    (`PeerConnection.cs:157-158`) so failures don't leak it.
17. **False ping timeouts behind big messages.** Any received frame counts as liveness, not just a
    Pong. Ping/Pong jump the outbound queue. (Splitting large clipboard payloads needs Phase 3.)
18. `SendLoop` never batches past the 64 MB frame cap: flush a frame before it would exceed it.
19. Cap in-flight unauthenticated handshakes (e.g. 8 total, 2 per IP) in `ConnectionListener`;
    rate-limit the "Rejected" log line.
20. `PeerDiscovery._discoveredPeers` capped (e.g. 64 entries).

### 2e. Files and clipboard

21. **Paste side lets an in-use offer expire.** Refresh `RemoteOffer.LastUsedTicks` in the paste
    read callback (`RoboMouseService.cs:1806`), under `_fileLock`.
22. Retired offers get an absolute lifetime cap (e.g. 30 min) as well as the idle 60 s.
23. **Validate offered file names** in `HandleRemoteFileOffer`: each component non-empty, no `..`,
    no rooted/UNC/drive paths, no `:` , no reserved device names, ≤ 259 chars; parents listed before
    children. Reject the whole offer on failure. Cap entry count (`MaxEntries`, e.g. 100 000).
24. Serving side: the 1 MB `local.Read` in `OnTransferRequest` moves off the receive thread; relay
    requests go through a per-connection queue with one in flight instead of unbounded `Task.Run`s.
25. `FileTransferClient` checks each chunk's offer id, entry index and offset against the request.
26. `ClipboardManager`: replace the `_ignoreNextChange` flag with `GetClipboardSequenceNumber`
    comparison; `FromPaths` scan wrapped in try/catch (raise `FilesCleared` on failure);
    `Dispose` waits on the STA with a timeout.
27. `AvaloniaImageCodec.PngToDib`: read the PNG header first and refuse images over e.g. 100 MP;
    compute sizes in `long`.
28. `VirtualFileDataObject`: null-check `GlobalAlloc`.

### 2f. Smaller core items

29. Hook callback does per-transition slow work (logging, `SetSystemCursor` ×13,
    `RegisterRawInputDevices`): post it to the message window and return first.
30. `DesktopServiceInjector` cursor query: add a sequence id so a late reply can't answer the next
    query.
31. `RawMouseInput`: honour `MOUSE_VIRTUAL_DESKTOP` for absolute devices.
32. `MonitorLayout.GetEdgeAt`: at a corner, return every matching edge and let the caller pick the
    one with a peer.

Tests:
- `AddConnection` replacing a connection that is active / controlling clears the state;
- `CursorEnter` ignored while disabled / while controlling;
- settings: save+load round trip; truncated file → restored from `.bak`, corrupt file kept aside,
  pairing code and machine id preserved;
- concurrent `Save` from several threads;
- accept policy: unknown id → pending, disabled → rejected, removed → blocked, own id → rejected;
- file-offer name validation table (`..\x`, `C:\x`, `\\srv\x`, `a:b`, `CON`, 260 chars, child before
  parent);
- `SecureChannel` connect cancelled mid-handshake returns within the timeout;
- `MonitorLayout.GetEdgeAt` corner cases.

---

## Phase 3 — Protocol v5 (with virtual layout, 1.2.0)

Both machines must update together; do it once, with `plans/virtual-layout.md`.

1. ✔ **Offline guessing of the pairing code.** Today the server sends its HMAC proof before the
   client proves anything, and PBKDF2 uses a fixed salt, so a probed or sniffed handshake lets an
   attacker test guesses offline. That only matters for weak codes: a generated code is 60 random
   bits, and 2^60 guesses × 120 000 PBKDF2 rounds is out of reach. Decision: **no PAKE** (.NET exposes
   no EC point arithmetic, so CPace/SPAKE2 would mean hand-rolled curve code, a bigger risk than the
   attack). Instead:
   - only generated codes are accepted (the Network page offers "Generate", not free text; a code
     is accepted if it has the generated format); an existing weak code is flagged on the Network page
     until regenerated;
   - the salt includes the protocol version, so v4 transcripts can't be reused against v5;
   - with item 2's pinned identity keys the code is only used when pairing a new machine, which
     shrinks the window further.
2. **Pinned peer identity.** Each install generates a long-lived identity key pair (stored with DPAPI
   under `%AppData%`). At first pairing the peers exchange public keys inside the PAKE-protected
   channel and save them in `PeerConfig`. Later connections must prove the pinned key, so a machine
   that knows the code can no longer claim another peer's id or hijack its slot. The pairing code is
   then only needed for pairing a new machine.
3. **Per-pair salt / code rotation.** With pinned keys, changing the pairing code no longer breaks
   existing peers; it only affects new pairings.
4. **One frame per message, big payloads chunked.** Clipboard text/images over 256 KB are sent as
   chunks interleaved with input and pings (same model as files), so a big copy never delays the
   cursor or the liveness check.
5. **Clipboard loop prevention.** Clipboard/offer messages carry origin machine id + sequence number;
   each node keeps the last applied sequence per origin and a small recent-hash set, fixing the
   3+-machine ring ping-pong.
6. **Authenticated discovery** (optional): broadcasts carry an HMAC keyed from the identity key so
   the discovered list only shows real RoboMouse installs.
7. Deserializers return null on malformed payloads instead of throwing and dropping the connection;
   the receive buffer shrinks back after a large message.

Tests: PAKE both sides agree / wrong code fails / transcript tampering fails; identity mismatch
rejected; chunked clipboard reassembly and interleaving; ring-of-three clipboard converges; malformed
payload fuzz (random bytes for every message type never throws out of `Deserialize`).

---

## Phase 4 — App and UX bugs (1.1.x)

1. **Cancel doesn't undo layout edits.** `ScreenLayoutControl` edits copies of the peer configs;
   Save applies them (`ScreenLayoutControl.cs:90,401,441`).
2. **Two peers on one edge.** Dropping on an occupied edge swaps the two; `GetPeerAtEdge` prefers a
   connected peer if the settings file already has a clash.
3. **Settings that need a restart** (`LocalPort`, `DiscoveryPort`, `MachineName`): restart the listener
   and discovery when they change on Save instead of saying "restart required".
4. **First run / `StartMinimized`.** Open Settings on first run and whenever no peers are configured;
   honour `StartMinimized` otherwise (today it is saved but never read).
5. **Startup registration.** Apply it even when the desktop-service start fails in `SaveAsync`
   (`:140-148`); show "Turned off in Task Manager / by policy" for the packaged
   `DisabledByUser`/`DisabledByPolicy` states; re-sync the Run key path on launch; the uninstaller removes
   the HKCU Run value.
6. **Errors reach the user.** Per-peer status reason on the Peers page ("pairing code doesn't
   match", "unreachable", "port in use", "blocked"); tray notification for `Error`, `PeerWakeSent` and
   pending peers. Needs a reason field on the connect-failure path in `RoboMouseService`.
7. **Logs.** Stop clearing the log at startup (`Program.cs:32`); roll it (keep last 3 runs, 5 MB cap).
8. `SingleInstance`: catch `UnauthorizedAccessException` when an elevated copy owns the mutex and
   tell the user another copy is running as administrator.
9. Tray "add discovered" and the Peers page use the same flow (add config, then connect); error
   dialogs get an owner window.
10. "Already connecting" racing the 5 s retry isn't reported as a failure.
11. PeerSetup dialog title/button say "Add" for a discovered (new) peer; the default edge for a new
    peer is the first free one.
12. `ScreenLayoutControl` handles `PointerCaptureLost` (no stuck drag).
13. `NumericUpDown` fields: clearing the box shows a validation error instead of silently keeping the
    old value.
14. Firewall button: rules scoped to the program path, Private + Domain profiles, `LocalSubnet`; handle
    the 15 s timeout without the `ExitCode` exception.
15. Hotkey: a key-capture control instead of free text; refuse an empty hotkey.
16. Clipboard settings: wire up `SyncText` / `SyncImages` (currently unused) and expose them plus
    `MaxSizeBytes` on the General page; `MaxSizeBytes` applied live.

Verify every UI change with `tools/RoboMouse.UiPreview` (light and dark).

Tests (view models, with the fake backend): layout cancel restores positions; drop on occupied edge
swaps; duplicate peer refused; first run opens settings; hotkey empty refused.

---

## Phase 5 — Installer and CI (1.1.x)

1. After `sc stop`, poll `sc query` for STOPPED (up to 30 s) instead of a fixed 2 s wait
   (`RoboMouse.iss:87-89`).
2. The two installers share one service: both detect the other; the full installer taking over
   from service-only (and vice versa) is handled; uninstalling one doesn't delete a service the other
   still owns (reference count in the registry, or make them one product with components).
3. Installer creates `%ProgramData%\RoboMouse` with the Phase 1 ACL and removes it on uninstall.
4. Installer adds firewall rules for `{app}\RoboMouse.App.exe` (TCP listen port, UDP discovery;
   Private + Domain) and removes them on uninstall.
5. Refuse `/DIR=` outside Program Files in `[Code]` (the SYSTEM service must never run from a
   user-writable folder).
6. CI (`.github/workflows/build.yml`):
   - `release` environment limited to `v*` tags with a required reviewer;
   - third-party actions pinned to commit SHAs (`azure/trusted-signing-action`,
     `softprops/action-gh-release`);
   - `id-token: write` / `contents: write` only on the jobs that need them;
   - manual (`workflow_dispatch`) builds are unsigned or clearly versioned so they can't downgrade an
     installed release.
7. Installer smoke job on `windows-latest`: silent install → `sc qc` (quoted path, LocalSystem,
   manual start) → ProgramData ACL check → upgrade over itself → uninstall (service, files, rules gone).

---

## Phase 6 — Missing features

Must-have, in this order:

1. **Crossing guards** (General page): don't cross while a mouse button is held (from 2b.6, on by
   default); corner dead zone (px); optional "push twice" / delay; optional modifier to allow crossing;
   don't cross while a full-screen app is in front.
2. **Hotkeys**: jump to machine N (Ctrl+Alt+F1–F4 style, per peer); lock the cursor to the current
   screen (toggle).
3. **Diagnostics**: "Open log folder" and "Export diagnostics" (zip of logs, settings with pairing
   code and identity keys redacted, version, monitor layout); last unhandled exception written to
   `crash.txt`.
4. **First-run pairing flow**: a small wizard — this PC's name, IP and code; pick a discovered
   machine or enter an IP; enter its code (or accept the pending request on the other side); place it
   on an edge. Replaces the hand-copy of a 12-character code via Network settings.
5. **Update check** for the direct-download builds: check GitHub releases daily, tray notification,
   "Download and install" runs the new installer. Off in the Store build.
6. **Clipboard controls** (from 4.16) plus a per-peer "share clipboard" toggle.

Should-have:

7. Lock all machines together (hotkey + "lock follows host" alongside `FollowHostPower`); screensaver
   follows host.
8. Keyboard-layout independence: send scan codes (and Unicode for characters with no key) so peers
   with different layouts type the right thing.
9. Accessibility: `AutomationProperties` on all controls; keyboard-operable layout canvas (arrow keys
   move the selected screen).
10. More than one peer per edge / per-monitor placement — covered by `plans/virtual-layout.md`.

Nice-to-have: drag-and-drop files across screens, localization (move strings to resources), opt-in
crash reporting, sign-in after reboot / Ctrl+Alt+Del (service phase 6), macOS/Linux clients,
cross-subnet rendezvous.

---

## Release grouping

Everything above shipped together in 1.2.0 (protocol 5); no 1.1.5 / 1.1.6 releases were made.
