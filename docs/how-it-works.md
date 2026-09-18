# How it works

## Moving between screens

1. The mouse hook sees the cursor reach a screen edge that has a peer configured.
2. The controlling machine hides its cursor, starts reading raw hardware motion (Raw Input) and
   tells the peer the cursor is entering.
3. While controlling, local mouse and keyboard events are swallowed and forwarded instead.
4. The controlled machine places its cursor on the entry edge and injects each motion delta as
   relative movement, so its own pointer speed and acceleration apply, exactly as for a directly
   attached mouse.
5. When the cursor is pushed back through the edge it came in on, the controlled machine hands
   control back and releases any held keys or buttons.

With **wrap-around** on, an edge with no peer leads to the peer on the opposite edge, so two
screens form a ring.

The hotkey (default Ctrl+Alt+M) is checked before anything else: while controlling it brings
control straight back, even if the other machine has stopped responding.

## Pairing and encryption

Every connection runs through an authenticated, encrypted channel before any protocol message:
an ECDH key exchange authenticated with HMACs keyed from the shared pairing code, then AES-256-GCM
per frame. A machine with a different code cannot connect, and nobody on the network can read or
inject input.

The pairing code is stored in plain text in `%AppData%\RoboMouse\settings.json`, so protect that
file as you would a password. **Generate new** under Settings > Network rotates it; the other
machines then need the new code.

## Network protocol

A custom binary protocol over TCP (default port 24800), with a 16-byte header (magic, version,
type, length, timestamp). Both peers must run the same protocol version.

- **Handshake**: machine info, screen dimensions, connection kind
- **Mouse / Keyboard**: relative motion in raw hardware counts, buttons, wheel, keys with scan codes
- **Clipboard**: text and images
- **FileOffer / FileRequest / FileChunk**: file copy and paste
- **CursorEnter / CursorLeave**: handing control over and back
- **InputStatus**: the controlled side reporting a UAC prompt or elevated window
- **Ping / Pong**: every second; round-trip time is shown in the Debug Panel, and a connection with
  no pong for 5 s is dropped

Consecutive motion messages are merged while waiting to send. Configured peers are retried every
5 s. Discovery uses UDP broadcast (default port 24801) and never crosses subnets.

## Copying files

Copying files sends only their names and sizes. The bytes stream from the source machine when you
paste, in 1 MB chunks over a second connection so mouse input is never delayed. Explorer shows its
usual progress dialog and cancelling it stops the transfer.

- The source machine must stay running and reachable until the paste finishes. If its clipboard
  changes mid-copy, the old offer stays readable for 60 s after its last request.
- In a chain of three or more machines the middle one proxies the chunks to the source.
- Explorer, Outlook, Teams and Office accept these "virtual file" pastes. Applications that only
  accept plain file paths (VS Code, for instance) will not see them; paste into a folder first.

## UAC prompts and the lock screen

See [desktop-service.md](desktop-service.md).
