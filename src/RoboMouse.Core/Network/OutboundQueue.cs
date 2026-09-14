using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Thread-safe queue of messages waiting to be sent. Consecutive mouse-motion messages are merged so
/// that a stalled socket produces one catch-up message instead of a burst of stale ones, while every
/// other message keeps its place in line.
/// </summary>
public sealed class OutboundQueue
{
    private readonly object _lock = new();
    private readonly Queue<ProtocolMessage> _queue = new();
    private ProtocolMessage? _tail;

    /// <summary>Number of messages waiting.</summary>
    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>Adds a message, merging it into the previous one when both are pure motion.</summary>
    public void Post(ProtocolMessage message)
    {
        lock (_lock)
        {
            if (message is MouseMessage { IsMotion: true } motion && _tail is MouseMessage { IsMotion: true } tailMotion)
            {
                tailMotion.DeltaX += motion.DeltaX;
                tailMotion.DeltaY += motion.DeltaY;
                return;
            }

            _queue.Enqueue(message);
            _tail = message;
        }
    }

    /// <summary>Moves every waiting message into <paramref name="into"/> in order and empties the queue.</summary>
    public void DrainTo(List<ProtocolMessage> into)
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
                into.Add(_queue.Dequeue());
            _tail = null;
        }
    }
}
