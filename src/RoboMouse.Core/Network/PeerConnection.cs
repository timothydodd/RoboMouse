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
    private const int HeaderSize = 16;
    private const int MaxMessageSize = 64 * 1024 * 1024;
    private const int PingIntervalMs = 1000;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _sendLock = new();
    private readonly Queue<ProtocolMessage> _outbound = new();
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
    /// Creates a connection by connecting to a remote peer.
    /// </summary>
    public static async Task<PeerConnection> ConnectAsync(
        string host,
        int port,
        string localMachineId,
        string localMachineName,
        int localScreenWidth,
        int localScreenHeight,
        CancellationToken ct = default)
    {
        SimpleLogger.Log("Connect", $"Connecting to {host}:{port}...");

        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);

        var connection = new PeerConnection(client);

        var handshake = new HandshakeMessage
        {
            MachineId = localMachineId,
            MachineName = localMachineName,
            ScreenWidth = localScreenWidth,
            ScreenHeight = localScreenHeight,
            SupportsClipboard = true
        };

        await connection.WriteDirectAsync(handshake, ct);
        var response = await connection.ReadOneAsync(ct);

        if (response is not HandshakeAckMessage ack)
        {
            var responseType = response?.GetType().Name ?? "null/invalid";
            connection.Dispose();
            throw new InvalidOperationException($"Invalid handshake response: received {responseType}");
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

        connection.StartThreads();

        SimpleLogger.Log("Connect", $"Connected to {ack.MachineName} ({ack.ScreenWidth}x{ack.ScreenHeight})");
        return connection;
    }

    /// <summary>
    /// Creates a connection from an accepted TCP client.
    /// </summary>
    public static async Task<PeerConnection> AcceptAsync(
        TcpClient client,
        string localMachineId,
        string localMachineName,
        int localScreenWidth,
        int localScreenHeight,
        CancellationToken ct = default)
    {
        var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connection = new PeerConnection(client);

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

        var ack = new HandshakeAckMessage
        {
            Accepted = true,
            MachineId = localMachineId,
            MachineName = localMachineName,
            ScreenWidth = localScreenWidth,
            ScreenHeight = localScreenHeight
        };

        await connection.WriteDirectAsync(ack, ct);
        connection.StartThreads();

        SimpleLogger.Log("Accept", $"Accepted {handshake.MachineName} from {remoteEp} ({handshake.ScreenWidth}x{handshake.ScreenHeight})");
        return connection;
    }

    private void StartThreads()
    {
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

        _pingTimer = new System.Threading.Timer(_ => Post(new PingMessage()), null, PingIntervalMs, PingIntervalMs);
    }

    /// <summary>
    /// Queues a message for sending. Never blocks and never throws; safe to call from input hooks.
    /// Consecutive mouse-motion messages waiting in the queue are merged into one.
    /// </summary>
    public void Post(ProtocolMessage message)
    {
        if (_disposed)
            return;

        lock (_sendLock)
        {
            if (message is MouseMessage { IsMotion: true } motion
                && _outbound.Count > 0
                && _lastQueued is MouseMessage { IsMotion: true } tail)
            {
                tail.DeltaX += motion.DeltaX;
                tail.DeltaY += motion.DeltaY;
            }
            else
            {
                _outbound.Enqueue(message);
                _lastQueued = message;
            }
            _outboundDrained.Reset();
        }

        _outboundSignal.Set();
    }

    private ProtocolMessage? _lastQueued;

    private void SendLoop()
    {
        var batch = new List<ProtocolMessage>();
        var buffer = new MemoryStream(4096);

        try
        {
            while (!_disposed)
            {
                _outboundSignal.WaitOne();

                lock (_sendLock)
                {
                    while (_outbound.Count > 0)
                        batch.Add(_outbound.Dequeue());
                    _lastQueued = null;
                }

                if (batch.Count == 0)
                    continue;

                buffer.SetLength(0);
                foreach (var message in batch)
                {
                    var data = message.Serialize();
                    buffer.Write(data, 0, data.Length);
                }
                batch.Clear();

                _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);

                lock (_sendLock)
                {
                    if (_outbound.Count == 0)
                        _outboundDrained.Set();
                }
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

                var consumed = 0;
                while (filled - consumed >= HeaderSize)
                {
                    var size = ProtocolMessage.GetMessageSize(buffer.AsSpan(consumed, HeaderSize));
                    if (size < HeaderSize || size > MaxMessageSize)
                        throw new InvalidDataException("Invalid frame header from peer.");

                    if (size > buffer.Length)
                    {
                        Array.Resize(ref buffer, Math.Max(size, buffer.Length * 2));
                    }

                    if (filled - consumed < size)
                        break; // Need more data for this frame

                    var message = ProtocolMessage.Deserialize(buffer.AsSpan(consumed, size));
                    consumed += size;

                    if (message != null && !Dispatch(message))
                    {
                        filled = 0;
                        consumed = 0;
                        goto done;
                    }
                }

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
                var rtt = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.Timestamp);
                if (rtt >= 0)
                {
                    RoundTripMs = rtt;
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

        try { _stream.Close(); } catch { }
        try { _client.Close(); } catch { }

        _stream.Dispose();
        _client.Dispose();
        _cts.Dispose();
    }
}
