# How it works

## Moving between screens

1. The mouse hook sees the cursor reach an outer edge of one of this PC's monitors, and looks up
   what the **layout** has beyond that stretch of edge. The layout is this PC's own monitors where
   Windows has them plus every peer monitor wherever it was placed (Settings > Layout), all in one
   coordinate space; a peer monitor's size there is its resolution scaled by the two display
   scalings, so screens of the same physical size line up. Only the part of an edge that another
   screen actually touches leads anywhere, so two PCs side by side on one edge each get their own
   stretch of it.
2. The **crossing guards** decide whether it may cross now: not while a mouse button is held (on by
   default, so a window drag is never cut off), not within the corner dead zone at the ends of the
   edge (20 px by default), and, if you turned them on, only after pushing a set distance past the
   edge (measured from raw mouse motion, since Windows pins the cursor at the edge; the same distance
   applies on the way back), on a second push, after pushing for a moment, while a chosen key is held, or when no full-screen program is in front. Nothing crosses
   while the cursor is locked to its screen (Scroll Lock by default).
3. The controlling machine hides its cursor, starts reading raw hardware motion (Raw Input) and
   tells the peer which of its monitors the cursor enters, and where. Modifier keys held at that moment (Ctrl for a copy-drag,
   say) go with it: they are released locally and pressed on the peer.
4. While controlling, local mouse and keyboard events are swallowed and forwarded instead.
5. The controlled machine places its cursor there and injects each motion delta as
   relative movement, so its own pointer speed and acceleration apply, exactly as for a directly
   attached mouse. Keys are injected by scan code, so the controlled machine's own keyboard layout
   decides what they type; text from an on-screen keyboard or password manager arrives as the
   characters themselves.
6. Only the controlled machine knows where its cursor really is, so it decides what happens at its
   edges, from the controller's layout (sent to it whenever it changes). Pushed against an edge with
   another of its own monitors beyond it on the layout, the cursor moves there, even if Windows
   arranges its monitors differently; a move Windows makes that the layout does not allow is undone.
   Pushed far enough into someone else's screen, it hands control back with that point on the
   controller's layout: one of the controller's monitors (control resumes there) or another peer's
   (control moves straight on to that peer). Held keys and buttons are released. If the connection
   drops instead, the cursor comes back where it left.

With **wrap-around** on, an edge with nothing beyond it leads to the farthest screen the other way
on the same row or column, entered from its far side, so screens in a row form a loop. A peer's **jump hotkey** (Ctrl+Alt+F1 to F4 for the first four peers) puts the
cursor straight onto the middle of its screen, from here or from another peer.

The toggle hotkey (default Ctrl+Alt+M) is checked before anything else: while controlling it brings
control straight back, even if the other machine has stopped responding.

## Pairing and encryption

Every install has its own **identity key** (ECDSA P-256), created on first run and kept in
`%AppData%\RoboMouse\identity.key`, encrypted with DPAPI so only that Windows account can read it.

Every connection runs through an authenticated, encrypted channel before any protocol message: an
ephemeral ECDH (P-256) key exchange in which each side signs the handshake with its identity key.
Then one of two modes:

- **Pairing** (the first connection between two machines): both also prove they know the pairing
  code (HMACs keyed from it, the connecting side first), and the code is mixed into the session
  keys. Each side then records the other's identity key.
- **Pinned** (both already recorded each other's key): the signatures are the whole proof; the
  pairing code is not used. Changing the code therefore only affects machines that have not paired
  yet.

A peer whose key differs from the one recorded for it is refused, so a machine that knows the code
still cannot take over a paired peer's place. Pair again from the Peers page after reinstalling a
PC. Machines it does not know yet must also be approved on the receiving PC before they can control
it. Traffic is AES-256-GCM per frame with separate keys per direction.

There is no PAKE, so someone who records a pairing handshake can test code guesses offline. That is
why codes are always generated: 12 characters from a 32-character alphabet (60 random bits),
stretched with PBKDF2 (120 000 rounds, salted with the protocol version). A code typed by hand in an
older version still works but is flagged on the Network page until you generate a new one.

The pairing code is stored in plain text in `%AppData%\RoboMouse\settings.json`, so protect that
file as you would a password. **Generate new** under Settings > Network replaces it.

## Network protocol

A custom binary protocol over TCP (default port 24800), with a 16-byte header (magic, version,
type, length, timestamp). This is protocol **version 6** (1.3.0); both peers must run the same
version. Different versions refuse each other with a version message rather than a broken
connection.

- **Handshake / HandshakeAck**: machine info, screen dimensions, connection kind, listen port; the
  ack carries the reason when a connection is refused (not approved yet, switched off, removed,
  identity mismatch)
- **Mouse / Keyboard**: relative motion in raw hardware counts, buttons, wheel, keys with scan codes
- **Clipboard / ClipboardChunk**: text and images; content over 256 KB goes in 256 KB chunks
- **FileOffer / FileOfferRevoked / FileRequest / FileChunk**: file copy and paste
- **ScreenInfo**: each machine's monitors (id, rectangle, scaling, main display), on connect and
  whenever they change
- **VirtualLayout**: the controller's layout as the receiver needs it: the receiver's own monitors by
  id, every other screen unnamed
- **CursorEnter / CursorLeave**: handing control over (a monitor and a point on it) and back (a
  point on the controller's layout, or "released")
- **CursorLock**: the controller locked the cursor to the controlled screen
- **InputStatus**: the controlled side reporting a UAC prompt or elevated window
- **PowerState / SessionState / LockRequest**: display and sleep state, lock and screen saver
  state, and "Lock all PCs"
- **Ping / Pong**: every second; round-trip time is shown in the Debug Panel, and a connection
  that has received nothing for 5 s is dropped

Consecutive motion messages are merged while waiting to send, and pings go ahead of everything
else. Big clipboard content travels one chunk at a time between input messages, so a large copy
never delays the cursor. A handshake must finish within 10 s. Malformed messages are dropped
without taking the connection down. Configured peers are retried every 5 s.

## Discovery

UDP broadcast (default port 24801), never crossing subnets. A broadcast carries the machine's id,
name, listen port, screen size and identity public key, and is signed with that key. Unsigned
broadcasts (older versions) are ignored, and one using a paired peer's id with a different key is
dropped, so the list cannot show a forged copy of a machine you have paired with.

## Clipboard

Text and images are sent to every peer you share the clipboard with (General page switches for
text, images and files, a size limit on the Advanced page, and a per-peer switch when you edit a
peer). A peer with sharing off is sent nothing and nothing it sends is used.

Every change carries a stamp: the machine it was copied on and a number that only grows. A machine
applies and passes on a change only when it is newer than what its clipboard holds, so in a chain
or a ring of three or more machines a copy never loops, and two copies made at once settle on the
same winner everywhere.

## Copying files

Copying files sends only their names and sizes. The bytes stream from the source machine when you
paste, in 1 MB chunks over a second connection so mouse input is never delayed. Explorer shows its
usual progress dialog and cancelling it stops the transfer.

- The source machine must stay running and reachable until the paste finishes. If its clipboard
  changes mid-copy, the old offer stays readable for 60 s after its last request (30 minutes at
  most).
- File names in an offer are checked before anything is put on the clipboard: no `..`, drive or
  network paths, or reserved device names.
- In a chain of three or more machines the middle one proxies the chunks to the source.
- Explorer, Outlook, Teams and Office accept these "virtual file" pastes. Applications that only
  accept plain file paths (VS Code, for instance) will not see them; paste into a folder first.

## UAC prompts and the lock screen

See [desktop-service.md](desktop-service.md).
