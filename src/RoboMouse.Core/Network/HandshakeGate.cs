using System.Net;

namespace RoboMouse.Core.Network;

/// <summary>
/// Limits how many incoming connections may be in the unauthenticated handshake at once, in total
/// and per remote address. Each handshake holds a socket and runs a slow key derivation check, so
/// without a cap anyone on the network could tie up the listener by opening connections and saying
/// nothing. Thread-safe.
/// </summary>
public sealed class HandshakeGate
{
    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, int> _perAddress = new();
    private int _total;

    public HandshakeGate(int maxTotal = 8, int maxPerAddress = 2)
    {
        MaxTotal = maxTotal;
        MaxPerAddress = maxPerAddress;
    }

    public int MaxTotal { get; }
    public int MaxPerAddress { get; }

    /// <summary>Handshakes currently admitted.</summary>
    public int InFlight
    {
        get { lock (_lock) return _total; }
    }

    /// <summary>Admits a handshake from <paramref name="address"/>, or returns false when over a limit.</summary>
    public bool TryEnter(IPAddress address)
    {
        lock (_lock)
        {
            _perAddress.TryGetValue(address, out var count);
            if (_total >= MaxTotal || count >= MaxPerAddress)
                return false;
            _perAddress[address] = count + 1;
            _total++;
            return true;
        }
    }

    /// <summary>Releases a slot taken by <see cref="TryEnter"/>.</summary>
    public void Exit(IPAddress address)
    {
        lock (_lock)
        {
            if (!_perAddress.TryGetValue(address, out var count))
                return;
            if (count <= 1)
                _perAddress.Remove(address);
            else
                _perAddress[address] = count - 1;
            _total--;
        }
    }
}
