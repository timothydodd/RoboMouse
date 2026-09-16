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
  token bound to `winsta0\<desktop>`. Runs only the input layer (the existing `MouseHook`,
  `KeyboardHook`, `RawMouseInput`, `InputSimulator`). Connects back to the service pipe and relays:
  captured edge/motion/button/key events up, injection commands down.
- **RoboMouse.App** — unchanged brain. When the service is present and enabled, the app routes input
  through the service instead of injecting in-process, so the helper on the current desktop applies
  it. When absent, the in-process path (today's behaviour) is used.

## Input abstraction (the seam)

Extract today's input path behind an interface in Core so it has two implementations:

- `InProcessInput` — the current `MouseHook`/`RawMouseInput`/`InputSimulator` calls (default).
- `ServiceInput` — forwards capture subscriptions and injection calls over the pipe to the service,
  which routes them to the helper on the active desktop.

`RoboMouseService` depends only on the interface, so the control logic (edge detection, transitions,
wrap-around, held-key release) is unchanged and stays in one place.

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
- Direct-download installer (WiX MSI or Inno Setup) installs the app, the service (manual start) and
  the helper, and registers the service. The Store package is untouched.
- A later, optional step packages the service into the Store build via the `windows.service` MSIX
  extension (needs the `packagedServices` + `localSystemServices` restricted capabilities and a
  review justification). If declined, the Store build points users at the direct download.

## Phases

1. **Contracts + skeletons** (this change): `RoboMouse.Contracts`, and `RoboMouse.Service` /
   `RoboMouse.Helper` projects that build, with session/desktop monitoring and the pipe server/client
   wired but not yet driving input. Not referenced by the app's runtime path.
2. **Input abstraction** in Core: interface + `InProcessInput` (no behaviour change, tested).
3. **ServiceInput** + helper input relay: real capture/injection over the pipe.
4. **App integration**: detect the service, the enable toggle, route through it when on.
5. **Installer**.
6. (optional) Store packaging of the service.

## Testing reality

Everything below the pipe (SYSTEM token, `CreateProcessAsUser` onto `winsta0\Winlogon`, secure-desktop
injection) can only be verified on real Windows, ideally two machines. CI confirms it compiles and
the contract round-trips; correctness needs manual runs. Treat each phase as "builds + unit tests
pass" until validated on Windows.
