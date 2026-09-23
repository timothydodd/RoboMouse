using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Thread-safe queue of messages waiting to be sent. Consecutive mouse-motion messages are merged so
/// that a stalled socket produces one catch-up message instead of a burst of stale ones, while every
/// other message keeps its place in line. Pings and pongs jump the queue: they measure the link, and
/// must not wait behind a large clipboard message. Clipboard chunks go in a bulk lane that hands out
/// one chunk per drain, after everything else, so a big copy is interleaved with input instead of
/// holding it up.
/// </summary>
public sealed class OutboundQueue
{
    private readonly object _lock = new();
    private readonly Queue<ProtocolMessage> _urgent = new();
    private readonly Queue<ProtocolMessage> _queue = new();
    private readonly Queue<ProtocolMessage> _bulk = new();
    private ProtocolMessage? _tail;

    /// <summary>Number of messages waiting.</summary>
    public int Count
    {
        get { lock (_lock) return _urgent.Count + _queue.Count + _bulk.Count; }
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

            if (message is ClipboardChunkMessage)
            {
                _bulk.Enqueue(message);
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
    /// Moves waiting messages into <paramref name="into"/>: pings and pongs first, then everything else
    /// in order, then at most one bulk chunk. Whatever is left of the bulk lane waits for the next call.
    /// </summary>
    public void DrainTo(List<ProtocolMessage> into)
    {
        lock (_lock)
        {
            while (_urgent.Count > 0)
                into.Add(_urgent.Dequeue());
            while (_queue.Count > 0)
                into.Add(_queue.Dequeue());
            if (_bulk.Count > 0)
                into.Add(_bulk.Dequeue());
            _tail = null;
        }
    }
}
