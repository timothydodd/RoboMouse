# Desktop service: UAC prompts, elevated windows and the lock screen

RoboMouse runs as a normal user. That is deliberate (no UAC prompt at launch, and it is what the
Microsoft Store allows), but it means Windows drops the input RoboMouse injects whenever the
controlled machine shows:

- a **UAC prompt**, the **lock screen** or the **sign-in screen** (these live on the *secure
  desktop*, which an ordinary process cannot even open), or
- a window running **as administrator** (Task Manager, an elevated terminal, most installers).

Without the service the controlling PC's tray status tells you why input stopped ("Waiting for UAC
on Laptop"), the cursor still moves, and the hotkey always brings control back.

The optional **RoboMouse Desktop Service**, new in 1.1.0, covers these cases. It is only needed on
machines that get *controlled*; the machine whose mouse you are holding does not need it.

## Getting it

| You installed RoboMouse from | Install |
| --- | --- |
| GitHub (`RoboMouse-Setup-<ver>.exe`) | Nothing more. The service is included. |
| [Microsoft Store](https://apps.microsoft.com/detail/9N4HSV1HP9B0) | `RoboMouse-Service-Setup-<ver>.exe` from the same [release](https://github.com/timothydodd/RoboMouse/releases). It adds only the service and trusts only the Store app. |
| The portable zip | The service is not available; use the full installer instead. |

> The Store app needs version 1.1.0 or later to use the service. The Store listing is still on
> 1.0.x while 1.1.0 goes through certification; until it updates, use the full installer.

The Store package never contains the service: Store apps cannot install a LocalSystem service
without restricted capabilities. The two installers cannot be installed side by side (they register
the same service).

## Turning it on

The installer registers the service **stopped and manual-start**, so installing it changes nothing
by itself.

1. Open **Settings > General**. The card **Control UAC prompts and the lock screen** appears only
   when the service is installed.
2. Turn it on and approve the single UAC prompt. That sets the service to start automatically and
   starts it. Turning it off reverses both.

RoboMouse itself keeps running as a normal user either way. If the service stops or is out of date,
the app falls back to injecting input itself, exactly as if the service were not installed.

## How it works

```
RoboMouse.App (normal user)  -- named pipe -->  RoboMouse.Service (LocalSystem)
   networking, UI, hooks                            verifies the caller, starts the helper
                                                           |
                                                 RoboMouse.Helper (SYSTEM, in your session)
                                                   injects input, follows the active desktop
```

- The **app** still does everything it did before: networking, encryption, hooks, the UI. When the
  service is ready it sends the input it would have injected down a named pipe instead.
- The **service** checks who is connecting and, only while the app is connected, keeps one helper
  running in the app's session.
- The **helper** applies the input. Its single thread follows whichever desktop currently has
  input, so it moves onto the secure desktop when a UAC prompt appears and back afterwards, with no
  process start at that moment.

## Security boundary

The service and helper run as SYSTEM and inject input, so the rules are explicit:

1. **Only your RoboMouse app can talk to the service.** The pipe admits the console session's user,
   and the service also checks that the connecting process is the installed `RoboMouse.App.exe`
   (or, for the service-only installer, a process from the RoboMouse Store package) running in the
   active console session.
2. **The helper only listens to the service.** Its pipe has a random name, a single instance and an
   ACL that admits SYSTEM only; the service checks the connecting process id is the helper it
   started.
3. **Injection only.** The helper installs no hooks and reads no raw input, so it never sees a
   keystroke it was not told to type. A SYSTEM process with a keyboard hook on the sign-in screen
   would be a password logger by construction; this design cannot be one.
4. **No networking in the service.** All network traffic stays in the normal-user app. The service
   only relays input that the app has already authenticated and decrypted.
5. **Input never crosses sessions.** The helper runs in the session of the verified app and is
   stopped while another user owns the console (fast user switching).
6. **Nothing lingers.** The helper exists only while the app is connected.

## Limits

- **Ctrl+Alt+Del cannot be injected.** A machine whose policy requires it at sign-in still needs a
  local keypress.
- **Sign-in after a reboot is not covered.** Until someone signs in, no app is running to connect
  to the service. Locking (Win+L) and unlocking works.

## Troubleshooting

- The service logs to `%ProgramData%\RoboMouse\service.log`; it should show the app connecting and
  a helper process id. The app log shows "Desktop service ready".
- The card is missing from Settings > General: the service is not installed on that machine.
- For development, `packaging\Install-DevService.ps1` (elevated PowerShell) registers a locally
  built service, and `RoboMouse.Service --console` runs it in a console.

The full design and its history are in [`plans/uac-service.md`](../plans/uac-service.md).
