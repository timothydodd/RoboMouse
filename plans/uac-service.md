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
        └───────────── named pipe ◀──────────────  RoboMouse.Helper (SYSTEM, per desktop)
                       input events / commands              hooks, raw input, SendInput
```

- **RoboMouse.Contracts** — a tiny shared library: the pipe message types and the pipe names. No
  Windows dependency, referenced by app, service and helper.
- **RoboMouse.Service** — LocalSystem Windows service. Watches session-change and desktop-switch
  events; whenever the input desktop changes it ensures a helper is running on it. Owns the control
  pipe the app connects to. Never does networking.
- **RoboMouse.Helper** — short-lived per-desktop process launched by the service with the SYSTEM
  token bound to `winsta0\<desktop>`. **Injection only**: it applies `InputSimulator` calls and
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
- `ServiceInjector` (Phase 3) — sends the same calls over the control pipe; the service forwards them
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
2. **The helper only acts on the desktop it was launched onto** and only on commands from the
   service, over an inherited pipe handle, never a named endpoint other processes could reach.
3. **The service does no networking and parses no untrusted data.** All network traffic stays in the
   normal-user app; the service only relays already-validated input intents.
4. **The helper is spawned per desktop and dies with it**, so a captured token is never long-lived.
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
3. **ServiceInjector + helper relay**: inject/cursor-query messages in Contracts, service spawns the
   helper on the input desktop (`CreateProcessAsUser`, inherited pipe) and forwards to it, helper
   applies them. Testable on Windows with `--console` from an elevated prompt via PsExec `-s`.
4. **App integration**: detect the service, the enable toggle (one elevated `sc config`/`sc start`),
   pass `ServiceInjector` to `RoboMouseService`, and stop reporting `InputStatus.SecureDesktop` while
   the service path is live. Package-family caller check lands here.
5. **Installers + release workflow**: the two Inno Setup scripts under `packaging/installer/`, built
   and attached by `build.yml` on `v*` tags.
6. (optional) Store packaging of the service.

## Testing reality

Everything below the pipe (SYSTEM token, `CreateProcessAsUser` onto `winsta0\Winlogon`, secure-desktop
injection) can only be verified on real Windows, ideally two machines. CI confirms it compiles and
the contract round-trips; correctness needs manual runs. Treat each phase as "builds + unit tests
pass" until validated on Windows.
