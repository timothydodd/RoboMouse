using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Network;

/// <summary>
/// Orders clipboard changes across machines so that every machine ends up with the same content and
/// nothing loops, however the peers are connected (a chain, a ring of three, a mesh).
///
/// Every change announced (text, image or files) carries a stamp: the origin's machine id and a
/// sequence number. A machine applies and passes on a message only when its stamp is newer than the
/// one its clipboard holds now, so its own change coming back round a ring, a copy it already has, or an
/// older change overtaken by a newer one is dropped; and two copies made at the same time on different
/// machines settle on the same winner everywhere. Sequence numbers start from the clock (so they keep
/// growing across restarts) and a local copy is always stamped newer than the content it replaces, so a
/// machine with a slow clock still wins with its own copy. Thread-safe.
/// </summary>
public sealed class ClipboardStamps
{
    private readonly object _lock = new();
    private readonly string _localId;
    private readonly Func<ulong> _clock;
    private string _currentOrigin = string.Empty;
    private ulong _currentSequence;

    /// <param name="localId">This machine's id.</param>
    /// <param name="clock">Source of the base sequence (default: Unix time in microseconds).</param>
    public ClipboardStamps(string localId, Func<ulong>? clock = null)
    {
        _localId = localId;
        _clock = clock ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
    }

    /// <summary>The stamp of what the clipboard holds now (empty origin and zero before anything happened).</summary>
    public (string Origin, ulong Sequence) Current
    {
        get { lock (_lock) return (_currentOrigin, _currentSequence); }
    }

    /// <summary>Stamps a change made on this machine; it becomes the current content.</summary>
    public ulong StampLocal()
    {
        lock (_lock)
        {
            var sequence = Math.Max(_clock(), _currentSequence + 1);
            _currentOrigin = _localId;
            _currentSequence = sequence;
            return sequence;
        }
    }

    /// <summary>
    /// True (and it becomes the current content) when a received change is newer than the current one:
    /// apply it and pass it on. False: drop it.
    /// </summary>
    public bool TryAccept(string origin, ulong sequence)
    {
        lock (_lock)
        {
            if (!IsNewer(origin, sequence, _currentOrigin, _currentSequence))
                return false;
            _currentOrigin = origin;
            _currentSequence = sequence;
            return true;
        }
    }

    private static bool IsNewer(string origin, ulong sequence, string currentOrigin, ulong currentSequence) =>
        sequence > currentSequence || (sequence == currentSequence && string.CompareOrdinal(origin, currentOrigin) > 0);
}

/// <summary>
/// Puts chunked clipboard content (<see cref="ClipboardChunkMessage"/>) back together, for one
/// connection. Chunks arrive in order on a connection; a chunk that does not continue the transfer in
/// progress abandons it. Not thread-safe (one connection's receive thread uses it).
/// </summary>
public sealed class ClipboardAssembler
{
    private ClipboardChunkMessage? _first;
    private byte[]? _buffer;
    private int _received;

    /// <summary>True while a transfer is partly received.</summary>
    public bool InProgress => _buffer != null;

    /// <summary>
    /// Adds a chunk. Returns the whole message when this chunk completes it, otherwise null. Content
    /// bigger than <paramref name="maxBytes"/>, and chunks that do not fit the transfer, are dropped.
    /// </summary>
    public ClipboardMessage? Add(ClipboardChunkMessage chunk, long maxBytes)
    {
        if (chunk.Offset == 0)
        {
            Reset();
            if (chunk.TotalLength <= 0 || chunk.TotalLength > maxBytes)
                return null;
            _first = chunk;
            _buffer = new byte[chunk.TotalLength];
        }
        else if (_first == null || chunk.OriginId != _first.OriginId || chunk.Sequence != _first.Sequence
                 || chunk.TotalLength != _first.TotalLength || chunk.Offset != _received)
        {
            Reset();
            return null;
        }

        if (chunk.Data.Length == 0 || chunk.Data.Length > _buffer!.Length - _received)
        {
            Reset();
            return null;
        }

        chunk.Data.CopyTo(_buffer, _received);
        _received += chunk.Data.Length;
        if (_received < _buffer.Length)
            return null;

        var message = new ClipboardMessage
        {
            ContentType = _first!.ContentType,
            FormatHint = _first.FormatHint,
            OriginId = _first.OriginId,
            Sequence = _first.Sequence,
            Data = _buffer
        };
        _first = null;
        _buffer = null;
        _received = 0;
        return message;
    }

    /// <summary>Abandons a partly received transfer.</summary>
    public void Reset()
    {
        _first = null;
        _buffer = null;
        _received = 0;
    }
}
