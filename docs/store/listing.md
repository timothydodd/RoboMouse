# Microsoft Store listing

Copy for the Partner Center submission, plus the checklist of what the submission needs. Keep this
file in sync with what is entered in Partner Center so a resubmission does not start from scratch.

## Product

- **Name:** RoboMouse (reserved in Partner Center; identity values live in the repository secrets
  `STORE_PACKAGE_NAME`, `STORE_PUBLISHER`, `STORE_PUBLISHER_DISPLAY`)
- **Category:** Utilities & tools
- **Pricing:** Free
- **Privacy policy URL:** https://raw.githubusercontent.com/timothydodd/RoboMouse/main/docs/privacy.md
  (or the GitHub Pages address once enabled)
- **Support / website:** https://github.com/timothydodd/RoboMouse

## Description

Share one mouse and keyboard across your Windows PCs.

Put two or more computers side by side, move the mouse to the edge of the screen and it carries on
to the next PC, keyboard included. There is nothing to plug in and no server to run: RoboMouse finds
the other machines on your network and connects to them directly.

- **Move between PCs like they are one desktop.** Drag a peer screen onto the edge it sits on in the
  layout view. Your pointer settings apply on each machine, so it always feels native.
- **Shared clipboard.** Copy text or an image on one PC and paste it on another.
- **Copy files, too.** Copy files on one machine and paste them in Explorer on the other. They
  transfer directly between the two, and only when you paste.
- **Private by design.** Every connection is authenticated with a pairing code you choose and
  encrypted end to end. Nothing is sent to the internet and there is no account.
- **Stays out of the way.** Runs from the tray. The icon's colour shows what it is doing, and a hotkey
  turns sharing on or off or brings the mouse back if you get stuck on another screen.
- **A brief glow marks the edge the mouse came in on**, so you always know which screen you are on.

RoboMouse is free and open source.

## Short description (Store "product description" summary, under 200 characters)

Move your mouse to the edge of the screen to control another PC. Shares the keyboard, clipboard and
files over your own network, encrypted, with no server or account.

## What's new (first submission)

First release: mouse and keyboard sharing between Windows PCs, clipboard and file sharing, pairing
code encryption, drag-to-arrange screen layout, start with Windows.

## Search terms

mouse sharing, keyboard sharing, kvm, synergy, mouse without borders, multiple computers, clipboard
sync, share mouse

## Screenshots

Generate with `dotnet run --project tools/RoboMouse.UiPreview -- --store docs/store/screenshots`
(the listing images are committed under `docs/store/screenshots/`).
They are rendered from the real windows at 1366x768 (the Store minimum for desktop). Upload in
this order:

1. `01-general.png` – settings, General page
2. `02-network.png` – pairing code and ports
3. `03-peers.png` – configured and discovered peers
4. `04-layout.png` – drag-to-arrange screen layout
5. `05-peer-setup.png` – add/edit peer dialog
6. `06-layout-dark.png` – dark theme

Also upload `store-icon-300x300.png` (Store logo) and `store-hero-2400x1200.png` (hero image, optional).

## Age rating

IARC questionnaire: no user-generated content, no communication between users beyond the user's own
devices, no purchases, no location, no personal data collection. Expect "Everyone".

## Capabilities and restricted capability justification

The manifest declares `runFullTrust` (granted automatically for desktop apps) and `allowElevation`,
which needs a justification in the submission. Suggested text:

> RoboMouse installs low-level mouse and keyboard hooks to detect when the pointer reaches the edge
> of the screen and injects input on the receiving PC with SendInput. Windows User Interface
> Privilege Isolation prevents a non-elevated process from seeing input directed at an elevated
> window or from sending input to one. Without elevation, sharing silently stops working whenever an
> elevated application (Task Manager, an installer, an admin console) is in the foreground on either
> PC. The app requests elevation at launch for this reason only; it does not modify the system, and
> the only privileged operation it performs on request is adding two inbound Windows Firewall rules
> for its own ports when the user clicks that button.

If the reviewer declines `allowElevation`, the fallback is to set `requestedExecutionLevel` to
`asInvoker` in `src/RoboMouse.App/app.manifest`, remove the capability from the package manifest, and
document the elevated-window limitation.

## Notes for certification testers

> RoboMouse shares the mouse and keyboard between two or more PCs on the same network, so a single
> test machine can only exercise the settings window and the tray icon. To test sharing: install on
> two PCs on one network, open Settings on each, enter the same pairing code on both under Network,
> then on one PC open the Peers page, select the other PC under "Found on this network" and click
> "Add selected". Moving the mouse off the chosen edge of the screen then controls the other PC. The
> app runs elevated so that input reaches elevated windows on the other PC (see the allowElevation
> justification).

## Submission checklist

- [ ] Partner Center: name reserved, product identity copied into the three repository secrets
- [ ] Privacy policy URL reachable (the raw GitHub link above works without GitHub Pages)
- [ ] Screenshots regenerated after any UI change (`--store` mode of the preview tool)
- [ ] Package built by CI from a `v*` tag (unsigned; the Store signs it), downloaded from the
      `RoboMouse-msix` artifact of that run
- [ ] Version is `x.y.z.0` (the tag `v1.0.0` becomes `1.0.0.0`; the script enforces the trailing 0)
- [ ] `allowElevation` justification pasted into the Submission options page
- [ ] Notes for certification pasted into the Submission options page
- [ ] Age rating questionnaire completed
- [ ] Listing text, search terms and "What's new" entered

## Sideload testing before submission

Run the **Build** workflow manually (Actions > Build > Run workflow) with "sign" on. The
`RoboMouse-msix` artifact then contains a `.msix` signed with a throwaway certificate and the
matching `.cer`. On the test PC, as administrator:

```powershell
Import-Certificate -FilePath .\RoboMouse-0.0.N.0-x64.cer -CertStoreLocation Cert:\LocalMachine\Root
Add-AppxPackage .\RoboMouse-0.0.N.0-x64.msix
```

Then check: the app appears in Start, starts from the tray, "Start with Windows" toggles the entry
under Settings > Apps > Startup, settings persist across restarts, and sharing works with a second
PC running any build.
