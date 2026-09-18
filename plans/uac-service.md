# RoboMouse desktop service (UAC / lock screen / sign-in)

## Why

The app runs as a normal user, so Windows drops the input it injects whenever the target is on the
**secure desktop** (a UAC prompt, the lock screen, the sign-in screen) or is an elevated window. A
normal process cannot even open the secure desktop. Covering these cases needs a process running as
**LocalSystem** that can launch a helper onto whatever desktop is currently active, including the
secure one.

This ships **separately from the Store package** (a direct-download installer). The Store app detects
whether the service is present and offers a toggle; without the service everything works exactly as
it does today, minus the secure-desktop cases.

## Pieces

```
RoboMouse.App (normal user)  ── named pipe ──▶  RoboMouse.Service (LocalSystem)
   networking, UI, the brain                      desktop/session watch, spawns helpers
        ▲                                                    │ CreateProcessAsUser onto winsta0\<desktop>
                                                   RoboMouse.Helper (SYSTEM, in the app's session)
                                                     SendInput / cursor position, follows the input desktop
```

- **RoboMouse.Contracts** — a tiny shared library: the pipe message types and the pipe names. No
  Windows dependency, referenced by app, service and helper.
- **RoboMouse.Service** — LocalSystem Windows service. Owns the control pipe the app connects to
  and, only while an app is connected, keeps one helper running in that app's session, relaying the
  app's injection commands to it in order. Never does networking.
- **RoboMouse.Helper** — started by the service with a copy of its SYSTEM token re-targeted at the
  app's session (`SetTokenInformation(TokenSessionId)` + `CreateProcessAsUser`). One helper per
  session, not per desktop: its single inject thread calls `OpenInputDesktop` + `SetThreadDesktop`
  to follow input onto `Winlogon` and back (checked every 100 ms and on any failed `SendInput`), so
  there is no process spawn at the moment a UAC prompt appears. It releases any held keys/buttons
  when the pipe closes. **Injection only**: it applies `InputSimulator` calls and
  answers cursor-position queries. It installs no hooks and reads no raw input (see the seam below).
- **RoboMouse.App** — unchanged brain. When the service is present and enabled, the app routes input
  through the service instead of injecting in-process, so the helper on the current desktop applies
  it. When absent, the in-process path (today's behaviour) is used.

## Input abstraction (the seam)

The secure desktop only matters on the **controlled** machine: someone driving it from another PC
hits a UAC prompt or the lock screen and their input stops landing. Nobody needs to *start*
controlling a remote from their own lock screen. So the seam is injection only, and capture (hooks,
raw input, hiding/moving our own cursor while controlling) always stays in the app process.

This is also the safer design: a SYSTEM process with a keyboard hook on the sign-in desktop would be
a password logger by construction. The helper never sees a keystroke it was not told to inject.

`IInputInjector` (Core, `Input/IInputInjector.cs`) — `MoveRelative`, `MoveTo`, `GetCursorPosition`,
`SimulateMouseEvent`, `SimulateKeyboardEvent`:

- `InProcessInjector` — today's `InputSimulator` calls (default).
- `DesktopServiceInjector` — sends the same calls over the control pipe; the service forwards them
  to the helper on the active desktop. `GetCursorPosition` has to go through it too, because
  `GetCursorPos` fails from a process that is not on the input desktop, and the return-edge check
  depends on it. Falls back to in-process when the pipe is down.

`RoboMouseService` takes the injector in its constructor and uses it for everything it applies on
behalf of a controller (entry placement, motion, buttons, keys, held-input release, return-edge
probe). Edge detection, transitions and wrap-around are unchanged.

Latency: motion is one-way fire-and-forget over the pipe; only `GetCursorPosition` is a round trip,
once per motion message. If that shows up, have the helper push the position back with each move ack
and cache it.

## Enabling it (no UAC at app launch)

The service installs **manual-start, stopped**. Settings shows "Drive UAC prompts and the lock
screen" only when the service is detected. Turning it on runs one elevated action (a single UAC
prompt on that click) that sets the service to auto-start and starts it; turning it off reverses it.
The app process itself stays a normal user.

## Security boundary (write this down; a reviewer and a careful user will ask)

The service and helper run as SYSTEM and inject input, so the trust rules are explicit:

1. **The control pipe only accepts the interactive user's RoboMouse.App.** The pipe ACL grants the
   console session's user; the service additionally checks the connecting process's image path is the
   installed `RoboMouse.App.exe` and that it runs in the active console session. No elevation, no
   other process, no other session.
2. **The helper only takes commands from the service.** Its pipe has a random name, one instance and
   an ACL admitting only the service's own identity (SYSTEM), and the service checks the connecting
   pid is the helper it just started.
   **Input never crosses sessions:** the helper runs in the session the verified app runs in, and is
   stopped while another session owns the console (fast user switching).
3. **The service does no networking and parses no untrusted data.** All network traffic stays in the
   normal-user app; the service only relays already-validated input intents.
4. **The helper exists only while the app is connected**, so nothing SYSTEM-level sits in the user's
   session when RoboMouse is not running.
5. Injection on the secure desktop is limited to what Winlogon allows (mouse move, click, keystrokes
   to the credential UI); the service never reads secure-desktop contents.

## Build / packaging

- Service + helper are plain `net10.0-windows` console executables, published Native AOT like the app.
- The Store package is untouched and never contains the service.
- **Distribution: same GitHub release, extra assets.** The `v*` tag workflow already publishes the AOT
  zip; add two installers to the same release so versions can never drift apart:
  - `RoboMouse-Setup-<ver>.exe` — app + service + helper, for people not using the Store.
  - `RoboMouse-Service-Setup-<ver>.exe` — service + helper only, for Store users who want UAC /
    lock-screen control on top of the Store app.
  A separate release stream would mean a second version number and a compatibility matrix for the
  pipe protocol; one tag avoids that. The pipe handshake still carries a contract version so a Store
  app that updated ahead of the service degrades to in-process injection with a "service is out of
  date" notice instead of misbehaving.
- Inno Setup rather than WiX: one script, runs on the `windows-latest` runner, handles
  `sc create`/stop-before-upgrade/uninstall without custom actions. Both installers register the
  service manual-start and stopped.
- **Caller verification has to cover the Store app.** Phase 1 checks the connecting process's image
  path against the installed `RoboMouse.App.exe`. A Store install lives under
  `C:\Program Files\WindowsApps\<package full name>\`, so the service-only installer needs the
  check to also accept a process whose package family name is ours (`GetPackageFamilyName` on the
  caller's process handle). WindowsApps is not user-writable, so this is as strong as the
  Program Files path check.
- Unsigned installers trip SmartScreen. Code signing is a cost decision; until then the release
  notes say so and publish SHA-256 hashes.
- A later, optional step packages the service into the Store build via the `windows.service` MSIX
  extension (needs the `packagedServices` + `localSystemServices` restricted capabilities and a
  review justification). If declined, the Store build points users at the direct download.

## Phases

1. **Contracts + skeletons** (this change): `RoboMouse.Contracts`, and `RoboMouse.Service` /
   `RoboMouse.Helper` projects that build, with session/desktop monitoring and the pipe server/client
   wired but not yet driving input. Not referenced by the app's runtime path.
2. **Injection seam** in Core (done): `IInputInjector` + `InProcessInjector`, `RoboMouseService`
   injects only through it. No behaviour change.
3. **Helper relay** (written, untested on Windows): pipe protocol 2 (`InjectMotion/Button/Key`,
   `MoveTo`, `QueryCursor`/`CursorPosition`, `HelperReady`/`HelperLost`), `HelperHost`/`HelperLauncher`
   in the service, the inject loop + `InputDesktop` in the helper.
4. **App integration** (written, untested on Windows): `DesktopServiceInjector` (falls back to
   in-process whenever the service is not ready; loopback-tested), `DesktopServiceControl`
   (SCM query + one elevated `sc config`/`sc start`), the `UseDesktopService` setting and the General
   page card shown only when the service is installed, `InputStatus` reports no block while routed.
   The service accepts `--app-path` and `--package-family` (Store app) on its registered command
   line. `packaging/Install-DevService.ps1` registers it for testing until the installer exists.
5. **Installers + release workflow**: the two Inno Setup scripts under `packaging/installer/`, built
   and attached by `build.yml` on `v*` tags.
6. (optional) Store packaging of the service.

## Testing reality

Everything below the pipe (SYSTEM token, `CreateProcessAsUser` onto `winsta0\Winlogon`, secure-desktop
injection) can only be verified on real Windows, ideally two machines. CI confirms it compiles and
the contract round-trips; correctness needs manual runs. Treat each phase as "builds + unit tests
pass" until validated on Windows.

### First Windows test checklist

1. Build the app, run `packaging\Install-DevService.ps1` from an elevated PowerShell on the machine
   that will be *controlled*, start the app, turn on the General page toggle (approve the one UAC).
2. `%ProgramData%\RoboMouse\service.log` should show the app connecting and a helper pid; the app log
   shows "Desktop service ready".
3. From the other PC: move/click/type normally (regression), then trigger a UAC prompt and click
   Yes, use an elevated window (Task Manager), lock with Win+L and sign back in, hand the mouse back
   across the edge while a UAC prompt is up.
4. Known limits: Ctrl+Alt+Del cannot be injected with SendInput (needs `SendSAS` + policy), so a
   machine that requires it at sign-in still needs a local keypress; the pre-login screen after a
   reboot has no app running, so nothing connects until someone signs in.
