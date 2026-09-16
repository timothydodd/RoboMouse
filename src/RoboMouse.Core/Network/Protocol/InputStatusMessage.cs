namespace RoboMouse.Core.Network.Protocol;

/// <summary>Why the controlled machine cannot currently apply the controller's input.</summary>
public enum InputBlockReason : byte
{
    /// <summary>Input is being applied normally.</summary>
    None = 0,

    /// <summary>A UAC prompt, the lock screen or the sign-in screen is showing (the secure desktop).</summary>
    SecureDesktop = 1,

    /// <summary>An elevated window is in the foreground and Windows drops input from a non-elevated process.</summary>
    ElevatedWindow = 2
}

/// <summary>
/// Sent by the controlled machine to its controller when injected input starts or stops being
/// blocked, so the controller can tell the user why nothing is happening instead of eating input.
/// </summary>
public class InputStatusMessage : Message
{
    public override MessageType Type => MessageType.InputStatus;

    public InputBlockReason Reason { get; set; }

    public bool IsBlocked => Reason != InputBlockReason.None;

    protected override byte[] SerializePayload() => new[] { (byte)Reason };

    public static InputStatusMessage DeserializePayload(ReadOnlySpan<byte> payload) =>
        new() { Reason = payload.Length > 0 ? (InputBlockReason)payload[0] : InputBlockReason.None };
}
