using RoboMouse.Core.Network;

namespace RoboMouse.Core;

/// <summary>Result of <see cref="ConnectionRegistry{T}.Add"/>.</summary>
public readonly record struct RegistryAdd<T>(bool Added, T? Replaced, bool ReplacedWasActive) where T : class;

/// <summary>
/// The live control connections, one per peer, plus the one this machine is controlling through (the
/// active connection). Both change under one lock, so starting control can never pick a connection
/// that is being removed or replaced at the same moment, and replacing or removing the active
/// connection always clears it. Thread-safe.
/// </summary>
public sealed class ConnectionRegistry<T> where T : class, IPeerLink
{
    private readonly object _lock = new();
    private readonly Dictionary<string, T> _connections = new();
    private T? _active;

    /// <summary>The connection being controlled through, or null.</summary>
    public T? Active
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>
    /// Registers a connection. When the peer already has a live one (both machines connected to each
    /// other at once), the one initiated by the machine with the smaller id is kept on both sides; the
    /// loser is either the new connection (not added) or the old one (returned in Replaced).
    /// </summary>
    public RegistryAdd<T> Add(T connection, string localMachineId)
    {
        lock (_lock)
        {
            _connections.TryGetValue(connection.PeerId, out var existing);
            if (ReferenceEquals(existing, connection))
                return new RegistryAdd<T>(false, null, false);

            if (existing is { IsConnected: true })
            {
                var keepOutbound = string.CompareOrdinal(localMachineId, connection.PeerId) < 0;
                if (existing.IsOutbound == keepOutbound)
                    return new RegistryAdd<T>(false, null, false);
            }

            // A dead connection not cleaned up yet is replaced too, so its owner can dispose it.
            _connections[connection.PeerId] = connection;
            var wasActive = existing != null && ReferenceEquals(_active, existing);
            if (wasActive)
                _active = null;
            return new RegistryAdd<T>(true, existing, wasActive);
        }
    }

    /// <summary>
    /// Removes the connection if it is still the one registered for its peer (a replacement may have
    /// taken over). <paramref name="wasActive"/> says whether it was the active connection, which is cleared.
    /// </summary>
    public bool Remove(T connection, out bool wasActive)
    {
        lock (_lock)
        {
            wasActive = false;
            if (!_connections.TryGetValue(connection.PeerId, out var current) || !ReferenceEquals(current, connection))
                return false;
            _connections.Remove(connection.PeerId);
            if (ReferenceEquals(_active, connection))
            {
                _active = null;
                wasActive = true;
            }
            return true;
        }
    }

    /// <summary>The live connection to a peer, if any.</summary>
    public T? Get(string peerId)
    {
        lock (_lock)
            return _connections.TryGetValue(peerId, out var c) && c.IsConnected ? c : null;
    }

    /// <summary>True when a connection (live or not yet cleaned up) is registered for the peer.</summary>
    public bool Contains(string peerId)
    {
        lock (_lock) return _connections.ContainsKey(peerId);
    }

    /// <summary>A copy of every registered connection.</summary>
    public List<T> Snapshot()
    {
        lock (_lock) return _connections.Values.ToList();
    }

    /// <summary>
    /// Makes the peer's connection the active one, provided it is still registered and connected.
    /// Returns it, or null when there is none (it just dropped).
    /// </summary>
    public T? TryActivate(string peerId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(peerId, out var c) || !c.IsConnected)
                return null;
            _active = c;
            return c;
        }
    }

    /// <summary>Clears the active connection. Returns what it was.</summary>
    public T? ClearActive()
    {
        lock (_lock)
        {
            var active = _active;
            _active = null;
            return active;
        }
    }

    /// <summary>Removes and returns every connection; clears the active one.</summary>
    public List<T> Clear()
    {
        lock (_lock)
        {
            var all = _connections.Values.ToList();
            _connections.Clear();
            _active = null;
            return all;
        }
    }
}
