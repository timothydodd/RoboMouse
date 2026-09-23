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

The service and helper run as SYSTEM and inject input, so the trust rules are explicit.

**What turning it on means, in plain words:** while the service is on, a program running as you
could approve UAC prompts on this PC, the same way the PC controlling it can. The caller checks below
stop *other* programs from impersonating RoboMouse, but they cannot stop a program running as you from
driving RoboMouse itself (for example by injecting into it). The General page says this under the
toggle; it is off by default and installs stopped.

1. **The control pipe only accepts the interactive user's RoboMouse.App** (`CallerPolicy`). The pipe
   ACL grants interactive users; then, from one `PROCESS_QUERY_LIMITED_INFORMATION` handle held for
   the whole check (so the pid cannot be recycled mid-check):
   - the image path comes from the kernel (`QueryFullProcessImageNameW`), not the client's own PEB;
   - the process was created before the connection was accepted and its creation time is unchanged
     at the end of the check (pid reuse), and it has not exited;
   - its session equals both `GetNamedPipeClientSessionId` and the active console session;
   - installed app: the path equals the configured `RoboMouse.App.exe`, and when the service exe is
     Authenticode-signed the app's signature must verify (`WinVerifyTrust`) with the same signer
     subject (subject, not thumbprint: Artifact Signing rotates short-lived leaf certificates). An
     unsigned service, i.e. a dev build from `Install-DevService.ps1`, checks the path only and logs it;
   - Store app: `RoboMouse.App.exe` with our package family **and** inside that package's install
     folder (`GetPackageFullName` → `GetPackagePathByFullName`). Files in an MSIX are not signed one by
     one; Windows verifies the package itself.
   The first pipe instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`.
2. **The app only talks to the real service** (`DesktopServiceControl.VerifyPipeServer`). Before it
   sends anything it checks the pipe's server process is the pid the SCM reports for
   `RoboMouseService`, in session 0, with the service configured as LocalSystem, and it connects with
   `Identification` impersonation so the service can never act as the user.
3. **The helper only takes commands from the service.** Its pipe has a random name, one instance and
   an ACL admitting only the service's own identity (SYSTEM), and the service checks the connecting
   pid is the helper it just started. The helper's token has SeDebug, SeTcb, SeImpersonate,
   SeLoadDriver, SeBackup/Restore, SeTakeOwnership and the other powerful privileges removed; SYSTEM
   identity is all it needs for the Winlogon desktop.
   **Input never crosses sessions:** the helper runs in the session the verified app runs in, is
   stopped while another session owns the console, and the app connection is dropped when the
   console session changes (fast user switching) so the new user's app can connect.
4. **The service does no networking and relays only well-formed input.** Nothing is relayed before a
   version-checked `Hello`; every command's payload length and values are checked per opcode before
   relaying (and again in the helper, which skips bad commands instead of failing); frames are capped
   at 64 KB.
5. **The helper exists only while the app is connected**, so nothing SYSTEM-level sits in the user's
   session when RoboMouse is not running. Keys and buttons relayed as down are tracked, and a
   replacement helper releases them first.
6. **The service log cannot be redirected.** `%ProgramData%\RoboMouse` is created by the installer
   with an explicit ACL (SYSTEM and Administrators full, Users read, owner Administrators). The
   service refuses to log there if the folder is a junction or owned by anyone else (it uses the
   event log instead), removes link files in place of the log, rolls it at 1 MB and rate-limits lines
   a local process can trigger.
7. Injection on the secure desktop is limited to what Winlogon allows (mouse move, click, keystrokes
   to the credential UI); the service never reads secure-desktop contents.
8. The service runs with `sc privs` limited to SeTcb, SeAssignPrimaryToken, SeIncreaseQuota (and
   SeChangeNotify) and an unrestricted service SID. A *restricted* SID type makes the token
   write-restricted, which would deny the helper (a copy of that token) the desktop write rights
   `SendInput` needs on Winlogon; switching to it needs a test on real hardware first.

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
3. **Helper relay** (done; elevated windows, UAC prompts and the lock screen verified on two real
   machines 2026-09-18): pipe protocol 2 (`InjectMotion/Button/Key`,
   `MoveTo`, `QueryCursor`/`CursorPosition`, `HelperReady`/`HelperLost`), `HelperHost`/`HelperLauncher`
   in the service, the inject loop + `InputDesktop` in the helper.
4. **App integration** (done, verified with it): `DesktopServiceInjector` (falls back to
   in-process whenever the service is not ready; loopback-tested), `DesktopServiceControl`
   (SCM query + one elevated `sc config`/`sc start`), the `UseDesktopService` setting and the General
   page card shown only when the service is installed, `InputStatus` reports no block while routed.
   The service accepts `--app-path` and `--package-family` (Store app) on its registered command
   line. `packaging/Install-DevService.ps1` registers it for testing until the installer exists.
5. **Installers + release workflow** (done; CI builds both installers and signs them and the binaries
   inside with Azure Artifact Signing, verified with signtool; running the installers is untested): one Inno Setup script,
   `packaging/installer/RoboMouse.iss`, built twice by `packaging/Build-Installer.ps1` (full, and
   `/DServiceOnly`). The service-only installer registers the service with `--package-family`, derived
   from the `STORE_PACKAGE_NAME`/`STORE_PUBLISHER` secrets, and is skipped when they are absent.
   The two can be installed side by side and share the one registration: each records its folder
   under `HKLM\SOFTWARE\RoboMouse\DesktopService`, the service runs from the full install's copy when
   there is one, and it (with `%ProgramData%\RoboMouse`) is deleted only with the last of the two.
   Upgrades stop the service and wait for STOPPED, keep the start type the user chose, and restart it
   if it was running; an older version or a `/DIR=` outside Program Files is refused. The installer
   also sets the ProgramData ACL, `sc privs`/`sidtype`, the event-log source and program-scoped
   firewall rules (Private + Domain), and the uninstaller removes the HKCU Run value.
   `build.yml` builds both on `v*` tags and manual runs, smoke-tests them on a clean runner
   (`packaging/Test-Installer.ps1`), and only then attaches them plus `SHA256SUMS.txt` to the release.
6. (optional) Store packaging of the service.

## Testing reality

Everything below the pipe (SYSTEM token, `CreateProcessAsUser` onto `winsta0\Winlogon`, secure-desktop
injection) can only be verified on real Windows, ideally two machines. CI confirms it compiles and
the contract round-trips; correctness needs manual runs. Treat each phase as "builds + unit tests
pass" until validated on Windows.

`RoboMouse.Service --console` still runs the worker for debugging, but the app refuses it: the app
only talks to the pipe server the SCM started for `RoboMouseService` in session 0. Use
`Install-DevService.ps1` to test with the app.

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
