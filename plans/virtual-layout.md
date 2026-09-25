# Virtual layout: per-monitor placement of remote screens

Status: **implemented, not yet tried on two machines.** Target release 1.3.0 (protocol 5 -> 6, both
machines must update together). 1.2.0 shipped protocol 5 first, so the numbers below are one higher
than first planned.

What was built differs from the plan below in a few places:

- Message numbers: `ScreenInfo` is 0x27 and `VirtualLayout` 0x28 (0x23-0x26 were taken by 1.2.0).
  The secure handshake version went 2 -> 3 so a 1.2 peer reports a version mismatch.
- The wrap-around flag stays on `CursorEnter`; `CursorLeave` carries an int point plus a `Released`
  flag. Refusals, releases and take-overs all send `Released`.
- The controlled-side logic is `ControlledCrossing` (pure, tested); the controller's layout
  plumbing is in `RoboMouseService.Layout.cs`; placement rules in `PlacementPlanner`.
- Shift-drag of a whole group is not done (open question below still stands).
- `ClipCursor` is not done (see Known compromise).
- The settings pages were split at the same time: General (everyday) and Advanced (tuning).

## Why

Today a peer is one rectangle (the bounding box of all its monitors) stuck to one of the four edges
of this PC, and a crossing stretches our whole edge onto its whole edge (0..1 to 0..1). The layout
page's offsets (`PeerConfig.OffsetX/OffsetY`) are only drawn; nothing in the control path reads them.
A peer reports its screen size once, in the handshake, so later changes never reach us.

Wanted instead:

- Each monitor of a remote PC is its own rectangle that the host can place anywhere on the layout,
  against any other screen, including in an order that differs from the remote's own Windows
  arrangement (for example Laptop-1 left of this PC and Laptop-2 right of it).
- The cursor arrives on the right monitor at the right spot, and leaves again at the matching spot.
- When the remote's displays change (monitor plugged in or removed, resolution, scaling, main
  display, display adapter change) the host is told, and its layout page shows it, while connected.

## Model

**One virtual desktop per host.** Layout space is this PC's own virtual-screen coordinates (main
display's top-left is 0,0), extended outwards. Local monitors sit exactly where Windows has them and
cannot be dragged. Every remote monitor is a rectangle placed in that same space. Rectangles never
overlap.

**Each PC owns its own layout.** What is arranged on DODD-MAIN applies while DODD-MAIN is
controlling; TIM-WORK's own layout applies while it controls. Same as today's per-machine peer
positions.

**The controlled side decides crossings.** Only it knows the true cursor position (Windows applies
pointer acceleration there), so the controller sends its arrangement and the controlled machine
works out what is beyond each monitor edge: another of its own monitors, a screen belonging to
someone else (hand back), or nothing.

**Monitor identity** is the Windows device name (`\\.\DISPLAY1`, from `MONITORINFOEX.szDevice`).
Stable enough across sessions for saved placements; an adapter change may rename monitors, which is
handled the same way as a new monitor appearing.

**Size on the layout.** A remote monitor's layout size is its pixel size scaled by
`hostMainScale / remoteMonitorScale`, so a 4K laptop at 200 % draws as 1080p beside a 1080p screen at
100 % and the edges line up along their whole length. Mapping between a layout rectangle and real
pixels is always proportional, so layout size and pixel size are free to differ.

## Protocol (version 5 -> 6)

| Message | Direction | Content |
| --- | --- | --- |
| `ScreenInfo` (new, 0x23) | both ways, after the handshake and on every change | list of monitors: id, x, y, width, height (own virtual-screen pixels), scale %, primary flag |
| `VirtualLayout` (new, 0x24) | controller -> peer, after it has the peer's `ScreenInfo`, and whenever its layout changes | list of rectangles in the controller's layout space, each tagged *yours* (with monitor id) or *foreign*; only screens that can currently be entered are included |
| `CursorEnter` (changed) | controller -> peer | monitor id, position inside that monitor (0..1, 0..1), wrap-around flag. No entry edge |
| `CursorLeave` (changed) | peer -> controller | target point in the controller's layout space, or a "released" flag (hotkey release, no target) |

The handshake keeps its width/height fields (still shown by the connection test).

A peer cannot be entered until its `ScreenInfo` has arrived on the current connection, so stale saved
placements are never used against a changed remote.

## Settings

`PeerConfig` gains `Monitors`: a list of `{ Id, X, Y, Width, Height, RemoteX, RemoteY, Scale }`
(layout placement plus the remote's own rectangle, used for auto-placement). Add the type to
`SettingsJsonContext`.

- `Position`, `OffsetX`, `OffsetY` stay in the file but only seed the first placement of a peer
  (migration of existing settings, and "add to the left of this screen" from the tray).
- A monitor that disappears keeps its saved placement (hidden and not enterable) so it returns to the
  same spot when plugged back in.
- A new monitor is auto-placed beside that peer's already placed monitors, following the remote's
  own arrangement; if that overlaps something it goes to the nearest free position.
- If this PC's own monitors change so that one now overlaps a remote rectangle, the remote rectangle
  is pushed out by the smallest move that clears it (fallback: right of everything).

## Core changes

1. **`Screen/MonitorLayout`, `ScreenInfo`** - carry monitor id and scale (`GetMonitorInfoW` with
   `MONITORINFOEX`, `GetDpiForMonitor` from shcore). Raise a `Changed` event from a 1-2 s check in
   the service (the lazy one-second refresh stays for the hook path).
2. **`Screen/VirtualDesktop` (new, pure geometry, immutable snapshot)** - built from local monitors
   plus peer placements. Operations:
   - `Resolve(fromRect, edge, pointOnEdge, wrap)` -> target rectangle and point inside it, or none.
     Wrap-around generalises to: nothing beyond the edge -> the farthest rectangle on the same row or
     column in the opposite direction, entered from its far side.
   - map a layout point to (monitor id, 0..1, 0..1) and back.
   - `Snap(draggedRect, others)` -> nearest position that touches another rectangle and overlaps
     none, with alignment snapping of tops/bottoms/sides.
   - auto-place and push-out described under Settings.
3. **Protocol** - the two new messages, the two changed ones, `MessageType`, the deserialiser switch,
   version constant, round-trip tests.
4. **`RoboMouseService`, controller side**
   - hook: at an outer edge of a local monitor, `Resolve` replaces `GetPeerAtEdge`; start control
     with the peer, monitor id and position. The snapshot is swapped atomically and read lock-free
     (hook path, no allocation per move).
   - `HandleReturnFromRemote`: the layout point is on a local monitor (place the cursor one pixel
     inside it) or on another peer's monitor (end control of the first peer, enter the second).
     Fallback if that peer went away: centre of the main display.
   - send `ScreenInfo` on connect and on local display change; rebuild and resend `VirtualLayout` to
     every peer when settings are saved, a peer's `ScreenInfo` changes, local monitors change, or a
     peer connects/disconnects.
   - on a peer's `ScreenInfo`: reconcile placements, save settings, raise `PeerScreensChanged`.
5. **`RoboMouseService`, controlled side** - track the current monitor from `CursorEnter`. After each
   injected delta:
   - cursor still on the current monitor and pinned against an edge while pushed: resolve. Own
     monitor -> `MoveTo` the mapped point immediately. Foreign -> accumulate the existing
     `ReturnOvershootCounts` push, then `CursorLeave` with the layout point.
   - cursor left the current monitor because Windows has a neighbour there: resolve from the edge it
     crossed. Layout agrees (same monitor, within 2 px) -> accept. Otherwise correct with `MoveTo`
     (own monitor), hand back (foreign) or put it back on the edge (nothing there).
   - no `VirtualLayout` received yet: behave as today (outer edges of the desktop hand back).
6. **Injection path** - only `MoveTo` and `GetCursorPosition` are needed, both already in
   `IInputInjector` and the helper pipe, so the pipe protocol and the helper do not change.

### Known compromise

When the layout disagrees with the remote's Windows arrangement, Windows moves the cursor onto the
"wrong" monitor before we correct it, so it can show there for a frame at the moment of crossing.
Removing that needs `ClipCursor` to the current monitor, re-applied whenever Windows clears it
(foreground and desktop switches), and a new helper opcode (pipe protocol 3). Do this only if the
flicker is noticeable in practice.

## App changes

- **`ScreenLayoutControl`** - one draggable rectangle per remote monitor ("Laptop - 1", size), local
  monitors fixed, snapping through `VirtualDesktop.Snap`, no overlaps, absent monitors not drawn,
  disconnected peers drawn from saved placements. `SaveLayout` writes placements. Reloads on
  `PeerScreensChanged` and on local display change, keeping unsaved drags.
- **`IAppBackend` / `TrayController`** - surface `PeerScreensChanged` on the UI thread.
- **Tray "Add left/right/above/below of this screen"** and **`PeerSetupWindow`'s position picker** -
  keep as "place this PC's screens as a group on that side"; drop the "edge already in use, swap?"
  dialog since several screens can share a side.
- **Edge flash and debug panel** - take the edge actually crossed (from the resolve result) instead
  of `peer.Position`.
- **`tools/RoboMouse.UiPreview`** - fake backend gets a two-monitor peer placed on both sides of a
  two-monitor host; render light and dark.

## Tests

- `VirtualDesktop`: resolve across equal and unequal monitors, gaps, wrap-around, chained peers,
  scaled monitors, snap never overlaps, auto-place, push-out.
- Protocol round trips for the four messages; version mismatch still rejected.
- Settings migration from `Position`/`Offset` to placements; unknown and returning monitor ids.
- Controlled-side crossing logic extracted so it can be driven with a fake injector (as
  `DesktopServiceInjectorTests` does).

## Order of work

1. Monitor id and scale in `MonitorLayout`; `VirtualDesktop` with tests.
2. Settings model and migration.
3. Protocol messages with tests.
4. Service: `ScreenInfo` exchange and reconcile (layout page can already show real remote monitors).
5. Layout page: per-monitor dragging and live reload.
6. Service: crossings on both sides.
7. Tray, peer setup, edge flash, debug panel, preview tool.
8. Two-machine test on DODD-MAIN and TIM-WORK, then docs (`CLAUDE.md`, README) and 1.2.0.

Steps 1-5 can be verified without a second machine; step 6 cannot.

## Open questions

- Is scale-based layout size the right default, or should remote monitors be resizable on the layout?
- Should Shift-drag move all of a peer's monitors as a group?
- `ClipCursor` (see Known compromise): decide after trying the simple approach on real hardware.
