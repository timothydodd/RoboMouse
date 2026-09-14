using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Splits a byte stream into protocol messages. Bytes that do not yet form a complete frame are left
/// for the caller to keep and present again once more data has arrived.
/// </summary>
public static class MessageFramer
{
    public const int HeaderSize = 16;
    public const int MaxMessageSize = 64 * 1024 * 1024;

    /// <summary>
    /// Parses every complete frame at the start of <paramref name="data"/>, invoking <paramref name="onMessage"/>
    /// for each one that deserializes, and returns the number of bytes consumed. Throws on a corrupt header.
    /// </summary>
    public static int ReadFrames(ReadOnlySpan<byte> data, Action<ProtocolMessage> onMessage)
    {
        var consumed = 0;
        while (data.Length - consumed >= HeaderSize)
        {
            var size = ProtocolMessage.GetMessageSize(data.Slice(consumed, HeaderSize));
            if (size < HeaderSize || size > MaxMessageSize)
                throw new InvalidDataException("Invalid frame header from peer.");

            if (data.Length - consumed < size)
                break;

            var message = ProtocolMessage.Deserialize(data.Slice(consumed, size));
            consumed += size;

            if (message != null)
                onMessage(message);
        }
        return consumed;
    }

    /// <summary>
    /// Returns the full size of the frame starting at the given header, or -1 if the header is invalid.
    /// </summary>
    public static int PeekFrameSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            return -1;
        var size = ProtocolMessage.GetMessageSize(header);
        return size < HeaderSize || size > MaxMessageSize ? -1 : size;
    }
}
