using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Thread-safe queue of messages waiting to be sent. Consecutive mouse-motion messages are merged so
/// that a stalled socket produces one catch-up message instead of a burst of stale ones, while every
/// other message keeps its place in line. Pings and pongs jump the queue: they measure the link, and
/// must not wait behind a large clipboard message.
/// </summary>
public sealed class OutboundQueue
{
    private readonly object _lock = new();
    private readonly Queue<ProtocolMessage> _urgent = new();
    private readonly Queue<ProtocolMessage> _queue = new();
    private ProtocolMessage? _tail;

    /// <summary>Number of messages waiting.</summary>
    public int Count
    {
        get { lock (_lock) return _urgent.Count + _queue.Count; }
    }

    /// <summary>Adds a message, merging it into the previous one when both are pure motion.</summary>
    public void Post(ProtocolMessage message)
    {
        lock (_lock)
        {
            if (message is PingMessage or PongMessage)
            {
                _urgent.Enqueue(message);
                return;
            }

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

    /// <summary>
    /// Moves every waiting message into <paramref name="into"/> and empties the queue: pings and pongs
    /// first, then everything else in order.
    /// </summary>
    public void DrainTo(List<ProtocolMessage> into)
    {
        lock (_lock)
        {
            while (_urgent.Count > 0)
                into.Add(_urgent.Dequeue());
            while (_queue.Count > 0)
                into.Add(_queue.Dequeue());
            _tail = null;
        }
    }
}
