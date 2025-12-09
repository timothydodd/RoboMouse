using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RoboMouse.Core.Network;

/// <summary>
/// Handles UDP broadcast-based peer discovery on the local network.
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    private readonly int _discoveryPort;
    private readonly int _listenPort;
    private readonly string _machineId;
    private readonly string _machineName;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _broadcastTask;

    private readonly ConcurrentDictionary<string, DiscoveredPeer> _discoveredPeers = new();
    private readonly TimeSpan _peerTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _broadcastInterval = TimeSpan.FromSeconds(5);

    private static readonly byte[] DiscoveryMagic = "MSDISC"u8.ToArray();
    private const byte DiscoveryVersion = 1;

    /// <summary>
    /// Event raised when a new peer is discovered.
    /// </summary>
    public event EventHandler<DiscoveredPeer>? PeerDiscovered;

    /// <summary>
    /// Event raised when a peer is no longer responding.
    /// </summary>
    public event EventHandler<DiscoveredPeer>? PeerLost;

    /// <summary>
    /// Gets the currently discovered peers.
    /// </summary>
    public IReadOnlyCollection<DiscoveredPeer> Peers => _discoveredPeers.Values.ToList();

    public PeerDiscovery(
        int discoveryPort,
        int listenPort,
        string machineId,
        string machineName,
        int screenWidth,
        int screenHeight)
    {
        _discoveryPort = discoveryPort;
        _listenPort = listenPort;
        _machineId = machineId;
        _machineName = machineName;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    /// <summary>
    /// Starts peer discovery.
    /// </summary>
    public void Start()
    {
        if (_listener != null)
            return;

        _cts = new CancellationTokenSource();

        // Create UDP listener
        _listener = new UdpClient();
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        _listener.EnableBroadcast = true;

        // Start listening for discovery messages
        _listenTask = ListenAsync(_cts.Token);

        // Start broadcasting our presence
        _broadcastTask = BroadcastAsync(_cts.Token);
    }

    /// <summary>
    /// Stops peer discovery.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _listener?.Close();
        }
        catch { }

        _listener?.Dispose();
        _listener = null;

        _cts?.Dispose();
        _cts = null;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(ct);
                ProcessDiscoveryMessage(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // Socket closed
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Discovery listen error: {ex.Message}");
            }
        }
    }

    private async Task BroadcastAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BroadcastPresenceAsync();
                CleanupStalePeers();
                await Task.Delay(_broadcastInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Discovery broadcast error: {ex.Message}");
            }
        }
    }

    private async Task BroadcastPresenceAsync()
    {
        var message = CreateDiscoveryMessage();

        // Broadcast on all network interfaces
        foreach (var broadcastAddress in GetBroadcastAddresses())
        {
            try
            {
                var endpoint = new IPEndPoint(broadcastAddress, _discoveryPort);
                await _listener!.SendAsync(message, endpoint);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Broadcast to {broadcastAddress} failed: {ex.Message}");
            }
        }
    }

    private byte[] CreateDiscoveryMessage()
    {
        var buffer = new List<byte>();

        // Magic bytes
        buffer.AddRange(DiscoveryMagic);

        // Version
        buffer.Add(DiscoveryVersion);

        // Machine ID (length-prefixed)
        var machineIdBytes = Encoding.UTF8.GetBytes(_machineId);
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, machineIdBytes.Length);
        buffer.AddRange(lengthBytes);
        buffer.AddRange(machineIdBytes);

        // Machine name (length-prefixed)
        var nameBytes = Encoding.UTF8.GetBytes(_machineName);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, nameBytes.Length);
        buffer.AddRange(lengthBytes);
        buffer.AddRange(nameBytes);

        // Listen port
        var portBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(portBytes, _listenPort);
        buffer.AddRange(portBytes);

        // Screen dimensions
        var dimBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(dimBytes, _screenWidth);
        buffer.AddRange(dimBytes);
        BinaryPrimitives.WriteInt32LittleEndian(dimBytes, _screenHeight);
        buffer.AddRange(dimBytes);

        return buffer.ToArray();
    }

    private void ProcessDiscoveryMessage(byte[] data, IPEndPoint remoteEndpoint)
    {
        if (data.Length < 7) // Minimum: magic(6) + version(1)
            return;

        // Verify magic bytes
        for (int i = 0; i < DiscoveryMagic.Length; i++)
        {
            if (data[i] != DiscoveryMagic[i])
                return;
        }

        // Verify version
        if (data[6] != DiscoveryVersion)
            return;

        try
        {
            var offset = 7;

            // Machine ID
            var machineIdLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            var machineId = Encoding.UTF8.GetString(data, offset, machineIdLength);
            offset += machineIdLength;

            // Ignore our own broadcasts
            if (machineId == _machineId)
                return;

            // Machine name
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            var machineName = Encoding.UTF8.GetString(data, offset, nameLength);
            offset += nameLength;

            // Listen port
            var port = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;

            // Screen dimensions
            var screenWidth = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            var screenHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

            var peer = new DiscoveredPeer
            {
                MachineId = machineId,
                MachineName = machineName,
                Address = remoteEndpoint.Address,
                Port = port,
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                LastSeen = DateTime.UtcNow
            };

            var isNew = !_discoveredPeers.ContainsKey(machineId);
            _discoveredPeers[machineId] = peer;

            if (isNew)
            {
                PeerDiscovered?.Invoke(this, peer);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to parse discovery message: {ex.Message}");
        }
    }

    private void CleanupStalePeers()
    {
        var now = DateTime.UtcNow;
        var staleIds = _discoveredPeers
            .Where(kvp => now - kvp.Value.LastSeen > _peerTimeout)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            if (_discoveredPeers.TryRemove(id, out var peer))
            {
                PeerLost?.Invoke(this, peer);
            }
        }
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        var broadcastAddresses = new List<IPAddress>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var properties = ni.GetIPProperties();
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask?.GetAddressBytes();

                    if (mask == null)
                    {
                        // Default to /24 if no mask available
                        mask = new byte[] { 255, 255, 255, 0 };
                    }

                    // Calculate broadcast address: IP | ~mask
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    }

                    broadcastAddresses.Add(new IPAddress(broadcast));
                }
            }
        }
        catch
        {
            // Fallback to general broadcast
            broadcastAddresses.Add(IPAddress.Broadcast);
        }

        if (broadcastAddresses.Count == 0)
        {
            broadcastAddresses.Add(IPAddress.Broadcast);
        }

        return broadcastAddresses.Distinct();
    }

    public void Dispose()
    {
        Stop();
    }
}
