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
public sealed class PeerConnection : IPeerLink, IDisposable
{
    private const int HeaderSize = MessageFramer.HeaderSize;
    private const int MaxMessageSize = MessageFramer.MaxMessageSize;
    private const int PingIntervalMs = 1000;
    private const int SilenceTimeoutMs = 5000;

    /// <summary>Messages are coalesced into writes of up to this size; bigger ones are written alone.</summary>
    private const int MaxBatchBytes = 256 * 1024;

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

    // Last time anything arrived from the peer. Any data proves it is alive, not just a pong: a pong
    // can be stuck behind a big clipboard message that is still streaming in.
    private long _lastHeardTicks;

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
    /// How long connect, secure handshake and the handshake message exchange may take together. A peer
    /// that accepts TCP and then says nothing must not hold a reconnect attempt (or an accept slot) for ever.
    /// </summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Replaces the raw socket stream with an authenticated, encrypted channel. Must run before any
    /// protocol message is exchanged.
    /// </summary>
    private async Task SecureAsync(byte[] pairingKey, bool isClient, CancellationToken ct)
    {
        _stream = isClient
            ? await SecureChannel.ConnectAsync(_stream, pairingKey, ct)
            : await SecureChannel.AcceptAsync(_stream, pairingKey, ct);
    }

    /// <summary>
    /// Runs <paramref name="handshake"/> under one timeout (<see cref="HandshakeTimeout"/>, or sooner if
    /// <paramref name="ct"/> says so). Cancelling closes the socket, which is the only thing that
    /// reliably ends a read blocked inside the encrypted stream; the failure is then reported as a
    /// cancellation. The socket is closed on any failure.
    /// </summary>
    private static async Task<PeerConnection> WithHandshakeTimeoutAsync(
        TcpClient client, CancellationToken ct, Func<CancellationToken, Task<PeerConnection>> handshake)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var token = timeout.Token;

        var abort = token.Register(static state => { try { ((TcpClient)state!).Dispose(); } catch { } }, client);
        try
        {
            var connection = await handshake(token);

            // Once this returns the socket can no longer be closed from under the new connection.
            abort.Dispose();
            if (token.IsCancellationRequested)
            {
                connection.Dispose();
                throw new OperationCanceledException(TimeoutMessage(ct), token);
            }
            return connection;
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is not OperationCanceledException)
        {
            client.Dispose();
            throw new OperationCanceledException(TimeoutMessage(ct), ex, token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally
        {
            abort.Dispose();
        }
    }

    private static string TimeoutMessage(CancellationToken callerToken) => callerToken.IsCancellationRequested
        ? "The connection attempt was cancelled."
        : $"The other machine did not complete the handshake within {HandshakeTimeout.TotalSeconds:0} seconds.";

    /// <summary>
    /// Creates a connection by connecting to a remote peer. Throws <see cref="PairingException"/> when the
    /// pairing codes differ, <see cref="ConnectionRejectedException"/> when the peer refuses us,
    /// <see cref="IncompatibleVersionException"/> for a different protocol version, and
    /// <see cref="OperationCanceledException"/> on timeout.
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
        var result = await WithHandshakeTimeoutAsync(client, ct, async token =>
        {
            await client.ConnectAsync(host, port, token);

            var connection = new PeerConnection(client);
            try
            {
                await connection.SecureAsync(pairingKey, isClient: true, token);

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

                await connection.WriteDirectAsync(handshake, token);
                var response = await connection.ReadOneAsync(token);

                if (response is not HandshakeAckMessage ack)
                {
                    throw response == null
                        ? new IncompatibleVersionException("No valid handshake reply. The other machine may be running a different RoboMouse version.")
                        : new InvalidOperationException($"Invalid handshake response: received {response.GetType().Name}");
                }

                if (!ack.Accepted)
                {
                    var (code, text) = RejectReasons.Parse(ack.RejectReason);
                    throw new ConnectionRejectedException(code, text);
                }

                if (ack.MachineId == localMachineId)
                    throw new ConnectionRejectedException(RejectCode.SameMachine, "That address is this PC.");

                connection.PeerId = ack.MachineId;
                connection.PeerName = ack.MachineName;
                connection.PeerScreenWidth = ack.ScreenWidth;
                connection.PeerScreenHeight = ack.ScreenHeight;
                connection.PeerListenPort = ack.ListenPort;
                connection.PeerMacAddress = WakeOnLan.Normalize(ack.MacAddress);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        });

        SimpleLogger.Log("Connect", $"Connected to {result.PeerName} ({result.PeerScreenWidth}x{result.PeerScreenHeight})");
        return result;
    }

    /// <summary>
    /// Creates a connection from an accepted TCP client. <paramref name="decide"/> sees the peer's
    /// handshake and returns null to accept it, or a reject reason (see <see cref="RejectReasons"/>),
    /// which is sent back before the connection is closed and <see cref="ConnectionRejectedException"/>
    /// is thrown. The TCP client is disposed on any failure.
    /// </summary>
    public static Task<PeerConnection> AcceptAsync(
        TcpClient client,
        byte[] pairingKey,
        string localMachineId,
        string localMachineName,
        int localScreenWidth,
        int localScreenHeight,
        int localListenPort,
        CancellationToken ct = default,
        Func<HandshakeMessage, IPEndPoint?, string?>? decide = null)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        return WithHandshakeTimeoutAsync(client, ct, async token =>
        {
            var connection = new PeerConnection(client);
            try
            {
                await connection.SecureAsync(pairingKey, isClient: false, token);

                var message = await connection.ReadOneAsync(token);
                if (message is not HandshakeMessage handshake)
                    throw new InvalidOperationException($"Expected handshake message, got {message?.GetType().Name ?? "null"}");

                connection.PeerId = handshake.MachineId;
                connection.PeerName = handshake.MachineName;
                connection.PeerScreenWidth = handshake.ScreenWidth;
                connection.PeerScreenHeight = handshake.ScreenHeight;
                connection.Kind = handshake.Kind;
                connection.PeerListenPort = handshake.ListenPort;
                connection.PeerMacAddress = WakeOnLan.Normalize(handshake.MacAddress);

                var reject = handshake.MachineId == localMachineId
                    ? RejectReasons.Format(RejectCode.SameMachine, "That address is this PC.")
                    : decide?.Invoke(handshake, remote);

                var ack = new HandshakeAckMessage
                {
                    Accepted = reject == null,
                    RejectReason = reject,
                    MachineId = localMachineId,
                    MachineName = localMachineName,
                    ScreenWidth = localScreenWidth,
                    ScreenHeight = localScreenHeight,
                    ListenPort = localListenPort,
                    MacAddress = connection.LocalMacAddress
                };
                await connection.WriteDirectAsync(ack, token);

                if (reject != null)
                {
                    var (code, text) = RejectReasons.Parse(reject);
                    throw new ConnectionRejectedException(code, $"{handshake.MachineName}: {text}");
                }

                SimpleLogger.Log("Accept", $"Accepted {handshake.MachineName} from {remote} ({handshake.ScreenWidth}x{handshake.ScreenHeight}, {handshake.Kind})");
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        });
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

        _lastHeardTicks = Environment.TickCount64;
        _pingTimer = new System.Threading.Timer(OnPingTimer, null, PingIntervalMs, PingIntervalMs);
    }

    private void OnPingTimer(object? state)
    {
        if (_disposed)
            return;

        if (Environment.TickCount64 - Interlocked.Read(ref _lastHeardTicks) > SilenceTimeoutMs)
        {
            SimpleLogger.Log("Conn", $"Nothing heard from {PeerName} for {SilenceTimeoutMs / 1000} s; dropping connection");
            RaiseDisconnected(new TimeoutException($"{PeerName} did not respond for {SilenceTimeoutMs / 1000} seconds."));
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

                // Small messages are coalesced into one write. A big one (a clipboard image) goes out on
                // its own after flushing what is ahead of it, so input and pings queued before it are
                // not held until all of it has been encrypted and sent.
                buffer.SetLength(0);
                foreach (var message in batch)
                {
                    var data = message.Serialize();
                    if (buffer.Length > 0 && buffer.Length + data.Length > MaxBatchBytes)
                    {
                        _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                        buffer.SetLength(0);
                    }
                    if (data.Length > MaxBatchBytes)
                        _stream.Write(data, 0, data.Length);
                    else
                        buffer.Write(data, 0, data.Length);
                }
                batch.Clear();

                if (buffer.Length > 0)
                    _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                if (buffer.Capacity > 4 * MaxBatchBytes)
                    buffer = new MemoryStream(4096);

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
                Interlocked.Exchange(ref _lastHeardTicks, Environment.TickCount64);

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
                Interlocked.Exchange(ref _lastHeardTicks, Environment.TickCount64);
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
