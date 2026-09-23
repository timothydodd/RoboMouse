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

> The Store app needs version 1.1.0 or later to use the service. If the Store has not reached that
> yet, use the full installer.

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

**What turning it on means:** while it is on, a program running as you could approve UAC prompts
on this PC, the same way the PC controlling it can. The checks below stop *other* programs from
pretending to be RoboMouse, but they cannot stop a program already running as you from driving
RoboMouse itself (by injecting code into it, for example). The card says this under the switch.
Leave it off unless you need to click through UAC prompts or the lock screen from another PC.

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

1. **Only your RoboMouse app can talk to the service.** The pipe admits interactive users; the
   service then checks the connecting process through one handle held for the whole check, so its
   process id cannot be recycled part-way:
   - its executable path comes from the kernel, not from the process's own (writable) memory;
   - it runs in the active console session, the same session the pipe reports;
   - it existed before the connection and has not exited or been replaced;
   - for `RoboMouse-Setup`: it is the installed `RoboMouse.App.exe`, and its Authenticode signature
     verifies and names the same publisher as the service's own signature;
   - for the Store app (`RoboMouse-Service-Setup`): it is `RoboMouse.App.exe` with the RoboMouse
     package identity **and** inside that package's install folder. Windows verifies the package
     signature itself.
2. **The app only talks to the real service.** Before sending anything, the app checks that the
   pipe's server is the process Windows started for `RoboMouseService`, in session 0, configured to
   run as LocalSystem. It connects so that the service can learn who it is but never act as it.
   The service creates the pipe with "first instance" set, so it cannot join a pipe another program
   created first.
3. **The helper only listens to the service.** Its pipe has a random name, a single instance and an
   ACL that admits SYSTEM only; the service checks the connecting process is the helper it started.
   The helper's token has the powerful privileges (debug, act as part of the OS, impersonate, load
   drivers, backup/restore, take ownership and others) removed; being SYSTEM is all it needs to
   reach the secure desktop.
4. **Injection only.** The helper installs no hooks and reads no raw input, so it never sees a
   keystroke it was not told to type. A SYSTEM process with a keyboard hook on the sign-in screen
   would be a password logger by construction; this design cannot be one.
5. **No networking in the service, and only well-formed input.** All network traffic stays in the
   normal-user app. Nothing is relayed before a version-checked hello, every command is checked
   for size and values (by the service, and again by the helper, which skips bad ones), and
   messages are capped at 64 KB.
6. **Input never crosses sessions.** The helper runs in the session of the verified app, is stopped
   while another user owns the console, and the app's connection is dropped when the console
   changes hands (fast user switching) so the new user's app can connect.
7. **Nothing lingers.** The helper exists only while the app is connected. Keys and buttons it
   pressed are released if it is replaced.
8. **The log cannot be redirected.** The installer creates `%ProgramData%\RoboMouse` owned by
   Administrators (SYSTEM and Administrators full control, Users read only). The service refuses to
   log there if the folder is a junction or owned by anyone else (it uses the Windows event log
   instead), keeps the log under 1 MB plus one previous file, and rate-limits lines another program
   could trigger.
9. **Least privilege for the service.** It holds only the privileges needed to start the helper in
   your session. Its service SID type is left unrestricted: a restricted token would also bind the
   helper and deny it the desktop access it needs; that change waits for testing on real hardware.

## Limits

- **Ctrl+Alt+Del cannot be injected.** A machine whose policy requires it at sign-in still needs a
  local keypress.
- **Sign-in after a reboot is not covered.** Until someone signs in, no app is running to connect
  to the service. Locking (Win+L) and unlocking works.

## Troubleshooting

- The service logs to `%ProgramData%\RoboMouse\service.log`; it should show the app connecting and
  a helper process id. The app log shows "Desktop service ready".
- The card is missing from Settings > General: the service is not installed on that machine.
- The service log rolls to `service.log.1` at 1 MB. If the folder is not safe to write, lines go to
  the Application event log under `RoboMouseService`.
- For development, `packaging\Install-DevService.ps1` (elevated PowerShell) registers a locally
  built service. A dev build is unsigned, so it checks the app by path only; use it on your own
  machines. `RoboMouse.Service --console` still runs the worker for debugging, but the app will not
  use it: the app only talks to the service Windows started.

The full design and its history are in [`plans/uac-service.md`](../plans/uac-service.md).
