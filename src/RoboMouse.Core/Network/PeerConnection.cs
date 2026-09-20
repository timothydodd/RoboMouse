using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Represents a TCP connection to a peer.
///
/// After the handshake, all traffic flows through two dedicated threads:
///  - a sender thread that drains an outbound queue. Consecutive mouse-motion messages
///    are merged while they wait, so a stalled socket produces one catch-up message
///    instead of a burst of stale ones, and the input hook never touches the socket.
///  - a receiver thread that reads into a large buffer and parses as many frames as
///    arrived in one read.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    private const int HeaderSize = MessageFramer.HeaderSize;
    private const int MaxMessageSize = MessageFramer.MaxMessageSize;
    private const int PingIntervalMs = 1000;
    private const int PongTimeoutMs = 5000;

    private readonly TcpClient _client;
    private Stream _stream;
    private readonly CancellationTokenSource _cts = new();

    private readonly OutboundQueue _outbound = new();
    private readonly AutoResetEvent _outboundSignal = new(false);
    private readonly ManualResetEventSlim _outboundDrained = new(true);
    private Thread? _sendThread;
    private Thread? _receiveThread;
    private System.Threading.Timer? _pingTimer;

    private volatile bool _disposed;
    private int _disconnectRaised;

    /// <summary>Unique identifier of the connected peer.</summary>
    public string PeerId { get; private set; } = string.Empty;

    /// <summary>Display name of the connected peer.</summary>
    public string PeerName { get; private set; } = string.Empty;

    /// <summary>Peer's screen width.</summary>
    public int PeerScreenWidth { get; private set; }

    /// <summary>Peer's screen height.</summary>
    public int PeerScreenHeight { get; private set; }

    /// <summary>Most recent measured round-trip time in milliseconds, or -1 if not yet measured.</summary>
    public int RoundTripMs { get; private set; } = -1;

    /// <summary>What this connection is for.</summary>
    public ConnectionKind Kind { get; private set; }

    /// <summary>True when the remote side opened this connection only to test reachability.</summary>
    public bool IsProbe => Kind == ConnectionKind.Probe;

    /// <summary>The port the peer listens on for new connections.</summary>
    public int PeerListenPort { get; private set; }

    /// <summary>The peer's MAC address (12 hex digits) for Wake-on-LAN, or empty if it did not send one.</summary>
    public string PeerMacAddress { get; private set; } = string.Empty;

    /// <summary>MAC address of the local adapter this connection runs over.</summary>
    private string LocalMacAddress => WakeOnLan.GetMacAddressFor((_client.Client.LocalEndPoint as IPEndPoint)?.Address);

    /// <summary>True when this machine initiated the connection.</summary>
    public bool IsOutbound { get; private set; }

    private long _lastPongTicks;

    /// <summary>Remote endpoint address.</summary>
    public IPEndPoint? RemoteEndPoint => _client.Client.RemoteEndPoint as IPEndPoint;

    /// <summary>Whether the connection is established and active.</summary>
    public bool IsConnected => !_disposed && _client.Connected;

    /// <summary>Raised on the receive thread when a message is received.</summary>
    public event EventHandler<ProtocolMessage>? MessageReceived;

    /// <summary>Raised when a round-trip measurement completes.</summary>
    public event EventHandler<int>? RoundTripMeasured;

    /// <summary>Raised when the connection is lost.</summary>
    public event EventHandler<Exception?>? Disconnected;

    private PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true; // Disable Nagle's algorithm for lower latency
        _client.ReceiveBufferSize = 256 * 1024;
        _client.SendBufferSize = 256 * 1024;
        _stream = _client.GetStream();
    }

    /// <summary>
    /// Replaces the raw socket stream with an authenticated, encrypted channel. Must run before any
    /// protocol message is exchanged.
    /// </summary>
    private async Task SecureAsync(byte[] pairingKey, bool isClient, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        _stream = isClient
            ? await SecureChannel.ConnectAsync(_stream, pairingKey, timeout.Token)
            : await SecureChannel.AcceptAsync(_stream, pairingKey, timeout.Token);
    }

    /// <summary>
    /// Creates a connection by connecting to a remote peer.
    /// </summary>
    public static async Task<PeerConnection> ConnectAsync(
        string host,
        int port,
        byte[] pairingKey,
        string localMachineId,
        string localMachineName,
        int localScreenWidth,
        int localScreenHeight,
        int localListenPort,
        CancellationToken ct = default,
        ConnectionKind kind = ConnectionKind.Control)
    {
        SimpleLogger.Log("Connect", $"Connecting to {host}:{port} ({kind})...");

        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);

        var connection = new PeerConnection(client);
        try
        {
            await connection.SecureAsync(pairingKey, isClient: true, ct);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        var handshake = new HandshakeMessage
        {
            MachineId = localMachineId,
            MachineName = localMachineName,
            ScreenWidth = localScreenWidth,
            ScreenHeight = localScreenHeight,
            SupportsClipboard = true,
            Kind = kind,
            ListenPort = localListenPort,
            MacAddress = connection.LocalMacAddress
        };
        connection.Kind = kind;
        connection.IsOutbound = true;

        await connection.WriteDirectAsync(handshake, ct);
        var response = await connection.ReadOneAsync(ct);

        if (response is not HandshakeAckMessage ack)
        {
            var responseType = response?.GetType().Name ?? "null/invalid";
            connection.Dispose();
            throw new InvalidOperationException(response == null
                ? "No valid handshake reply. The other machine may be running a different RoboMouse version."
                : $"Invalid handshake response: received {responseType}");
        }

        if (!ack.Accepted)
        {
            connection.Dispose();
            throw new InvalidOperationException($"Connection rejected: {ack.RejectReason}");
        }

        connection.PeerId = ack.MachineId;
        connection.PeerName = ack.MachineName;
        connection.PeerScreenWidth = ack.ScreenWidth;
        connection.PeerScreenHeight = ack.ScreenHeight;
        connection.PeerListenPort = ack.ListenPort;
        connection.PeerMacAddress = WakeOnLan.Normalize(ack.MacAddress);

        SimpleLogger.Log("Connect", $"Connected to {ack.MachineName} ({ack.ScreenWidth}x{ack.ScreenHeight})");
        return connection;
    }

    /// <summary>
    /// Creates a connection from an accepted TCP client.
    /// </summary>
    public static async Task<PeerConnection> AcceptAsync(
        TcpClient client,
        byte[] pairingKey,
        string localMachineId,
        string localMachineName,
        int localScreenWidth,
        int localScreenHeight,
        int localListenPort,
        CancellationToken ct = default)
    {
        var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connection = new PeerConnection(client);
        try
        {
            await connection.SecureAsync(pairingKey, isClient: false, ct);
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Accept", $"Rejected {remoteEp}: {ex.Message}");
            connection.Dispose();
            throw;
        }

        var message = await connection.ReadOneAsync(ct);

        if (message is not HandshakeMessage handshake)
        {
            connection.Dispose();
            throw new InvalidOperationException($"Expected handshake message, got {message?.GetType().Name ?? "null"}");
        }

        connection.PeerId = handshake.MachineId;
        connection.PeerName = handshake.MachineName;
        connection.PeerScreenWidth = handshake.ScreenWidth;
        connection.PeerScreenHeight = handshake.ScreenHeight;
        connection.Kind = handshake.Kind;
        connection.PeerListenPort = handshake.ListenPort;
        connection.PeerMacAddress = WakeOnLan.Normalize(handshake.MacAddress);

        var ack = new HandshakeAckMessage
        {
            Accepted = true,
            MachineId = localMachineId,
            MachineName = localMachineName,
            ScreenWidth = localScreenWidth,
            ScreenHeight = localScreenHeight,
            ListenPort = localListenPort,
            MacAddress = connection.LocalMacAddress
        };

        await connection.WriteDirectAsync(ack, ct);
        SimpleLogger.Log("Accept", $"Accepted {handshake.MachineName} from {remoteEp} ({handshake.ScreenWidth}x{handshake.ScreenHeight})");
        return connection;
    }

    /// <summary>
    /// Starts the send and receive threads and the ping timer. A new connection does nothing until
    /// this is called, so the owner can attach <see cref="MessageReceived"/> first; otherwise a message
    /// the peer sends straight after the handshake (a file request, say) is read while nobody is
    /// listening and silently lost.
    /// </summary>
    public void Start()
    {
        if (_disposed || _receiveThread != null)
            return;

        _sendThread = new Thread(SendLoop)
        {
            Name = $"RoboMouse-Send-{PeerName}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _receiveThread = new Thread(ReceiveLoop)
        {
            Name = $"RoboMouse-Recv-{PeerName}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _sendThread.Start();
        _receiveThread.Start();

        _lastPongTicks = Environment.TickCount64;
        _pingTimer = new System.Threading.Timer(OnPingTimer, null, PingIntervalMs, PingIntervalMs);
    }

    private void OnPingTimer(object? state)
    {
        if (_disposed)
            return;

        if (Environment.TickCount64 - Interlocked.Read(ref _lastPongTicks) > PongTimeoutMs)
        {
            SimpleLogger.Log("Conn", $"{PeerName} stopped answering pings; dropping connection");
            RaiseDisconnected(new TimeoutException($"{PeerName} did not respond for {PongTimeoutMs / 1000} seconds."));
            Dispose();
            return;
        }

        Post(new PingMessage());
    }

    /// <summary>
    /// Queues a message for sending. Never blocks and never throws; safe to call from input hooks.
    /// Consecutive mouse-motion messages waiting in the queue are merged into one.
    /// </summary>
    public void Post(ProtocolMessage message)
    {
        if (_disposed)
            return;

        _outbound.Post(message);
        _outboundDrained.Reset();
        _outboundSignal.Set();
    }

    private void SendLoop()
    {
        var batch = new List<ProtocolMessage>();
        var buffer = new MemoryStream(4096);

        try
        {
            while (!_disposed)
            {
                _outboundSignal.WaitOne();

                _outbound.DrainTo(batch);

                if (batch.Count == 0)
                {
                    _outboundDrained.Set();
                    continue;
                }

                buffer.SetLength(0);
                foreach (var message in batch)
                {
                    var data = message.Serialize();
                    buffer.Write(data, 0, data.Length);
                }
                batch.Clear();

                _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);

                if (_outbound.Count == 0)
                    _outboundDrained.Set();
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            RaiseDisconnected(ex);
        }
        catch
        {
            // Disposed; ignore.
        }
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[64 * 1024];
        var filled = 0;
        Exception? reason = null;

        try
        {
            while (!_disposed)
            {
                var read = _stream.Read(buffer, filled, buffer.Length - filled);
                if (read == 0)
                    break; // Closed gracefully
                filled += read;

                // Grow the buffer if the next frame is larger than what we can hold.
                if (filled >= HeaderSize)
                {
                    var next = MessageFramer.PeekFrameSize(buffer.AsSpan(0, HeaderSize));
                    if (next < 0)
                        throw new InvalidDataException("Invalid frame header from peer.");
                    if (next > buffer.Length)
                        Array.Resize(ref buffer, Math.Max(next, buffer.Length * 2));
                }

                var stop = false;
                var consumed = MessageFramer.ReadFrames(buffer.AsSpan(0, filled), message =>
                {
                    if (!stop && !Dispatch(message))
                        stop = true;
                });

                if (stop)
                    goto done;

                if (consumed > 0)
                {
                    var remaining = filled - consumed;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                    filled = remaining;
                }
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            reason = ex;
        }
        catch
        {
            return;
        }

    done:
        if (!_disposed)
        {
            RaiseDisconnected(reason);
        }
    }

    /// <summary>
    /// Handles a received message. Returns false when the connection should end.
    /// </summary>
    private bool Dispatch(ProtocolMessage message)
    {
        switch (message)
        {
            case PingMessage ping:
                Post(new PongMessage { Timestamp = ping.Timestamp });
                return true;

            case PongMessage pong:
                Interlocked.Exchange(ref _lastPongTicks, Environment.TickCount64);
                var rtt = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.Timestamp);
                if (rtt >= 0)
                {
                    RoundTripMs = rtt;
                    Interlocked.Exchange(ref _pendingRtt, null)?.TrySetResult(rtt);
                    RoundTripMeasured?.Invoke(this, rtt);
                }
                return true;

            case DisconnectMessage:
                MessageReceived?.Invoke(this, message);
                return false;

            default:
                try
                {
                    MessageReceived?.Invoke(this, message);
                }
                catch (Exception ex)
                {
                    SimpleLogger.Log("Recv", $"Handler for {message.Type} threw: {ex.Message}");
                }
                return true;
        }
    }

    private TaskCompletionSource<int>? _pendingRtt;

    /// <summary>
    /// Sends a ping now and returns the measured round-trip time in milliseconds.
    /// </summary>
    public async Task<int> MeasureRoundTripAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _pendingRtt, tcs);
        previous?.TrySetCanceled();

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        Post(new PingMessage());
        return await tcs.Task;
    }

    private void RaiseDisconnected(Exception? reason)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            Disconnected?.Invoke(this, reason);
        }
    }

    /// <summary>
    /// Writes a message straight to the socket. Only for the handshake, before the sender thread starts.
    /// </summary>
    private async Task WriteDirectAsync(ProtocolMessage message, CancellationToken ct)
    {
        var data = message.Serialize();
        await _stream.WriteAsync(data, ct);
    }

    /// <summary>
    /// Reads one message straight from the socket. Only for the handshake, before the receiver thread starts.
    /// </summary>
    private async Task<ProtocolMessage?> ReadOneAsync(CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactlyAsync(header, HeaderSize, ct))
            return null;

        var size = ProtocolMessage.GetMessageSize(header);
        if (size < HeaderSize || size > MaxMessageSize)
            return null;

        var full = new byte[size];
        header.CopyTo(full, 0);
        if (!await ReadExactlyAsync(full.AsMemory(HeaderSize), size - HeaderSize, ct))
            return null;

        return ProtocolMessage.Deserialize(full);
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> target, int count, CancellationToken ct)
    {
        var got = 0;
        while (got < count)
        {
            var read = await _stream.ReadAsync(target.Slice(got, count - got), ct);
            if (read == 0)
                return false;
            got += read;
        }
        return true;
    }

    /// <summary>
    /// Gracefully disconnects from the peer.
    /// </summary>
    public Task DisconnectAsync()
    {
        if (_disposed)
            return Task.CompletedTask;

        try
        {
            Post(new DisconnectMessage());
            _outboundDrained.Wait(250);
        }
        catch { }

        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _pingTimer?.Dispose();
        _outboundSignal.Set();
        Interlocked.Exchange(ref _pendingRtt, null)?.TrySetException(new ObjectDisposedException(nameof(PeerConnection)));

        try { _stream.Close(); } catch { }
        try { _client.Close(); } catch { }

        _stream.Dispose();
        _client.Dispose();
        _cts.Dispose();
    }
}
