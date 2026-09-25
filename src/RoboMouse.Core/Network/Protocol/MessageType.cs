namespace RoboMouse.Core.Network.Protocol;

/// <summary>
/// Types of messages in the RoboMouse protocol.
/// </summary>
public enum MessageType : byte
{
    /// <summary>Initial handshake to exchange capabilities.</summary>
    Handshake = 0x01,

    /// <summary>Response to handshake.</summary>
    HandshakeAck = 0x02,

    /// <summary>Mouse movement or button event.</summary>
    Mouse = 0x10,

    /// <summary>Keyboard event.</summary>
    Keyboard = 0x11,

    /// <summary>Cursor entering the peer's screen.</summary>
    CursorEnter = 0x20,

    /// <summary>Cursor leaving the peer's screen (returning control).</summary>
    CursorLeave = 0x21,

    /// <summary>The controlled peer reports whether it can currently apply input (UAC prompt, elevated window).</summary>
    InputStatus = 0x22,

    /// <summary>The sender's display turned on or off, or it is about to sleep.</summary>
    PowerState = 0x23,

    /// <summary>The controller locked (or unlocked) the cursor to the controlled screen.</summary>
    CursorLock = 0x24,

    /// <summary>The sender's session locked or unlocked, or its screen saver started or stopped.</summary>
    SessionState = 0x25,

    /// <summary>"Lock all PCs": the sender asks the receiver to lock its session.</summary>
    LockRequest = 0x26,

    /// <summary>The sender's monitors: id, rectangle and scaling of each (protocol 6).</summary>
    ScreenInfo = 0x27,

    /// <summary>The controller's layout as seen by the receiver: its own monitors and everyone else's screens (protocol 6).</summary>
    VirtualLayout = 0x28,

    /// <summary>Clipboard data.</summary>
    Clipboard = 0x30,

    /// <summary>Clipboard data request.</summary>
    ClipboardRequest = 0x31,

    /// <summary>Files were copied on the sender; lists names and sizes only.</summary>
    FileOffer = 0x32,

    /// <summary>A previous file offer is no longer valid.</summary>
    FileOfferRevoked = 0x33,

    /// <summary>Request for a range of one offered file (sent over a transfer connection).</summary>
    FileRequest = 0x34,

    /// <summary>A range of file bytes, or an error, answering a FileRequest.</summary>
    FileChunk = 0x35,

    /// <summary>One piece of clipboard content too big to send in one message (protocol 5).</summary>
    ClipboardChunk = 0x36,

    /// <summary>Keep-alive ping.</summary>
    Ping = 0x40,

    /// <summary>Keep-alive pong response.</summary>
    Pong = 0x41,

    /// <summary>Graceful disconnect.</summary>
    Disconnect = 0xF0,

    /// <summary>Error message.</summary>
    Error = 0xFF
}
