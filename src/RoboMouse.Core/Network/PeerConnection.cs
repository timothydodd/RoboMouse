using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Represents a TCP connection to a peer.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts;
    private readonly Task _receiveTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Unique identifier of the connected peer.
    /// </summary>
    public string PeerId { get; private set; } = string.Empty;

    /// <summary>
    /// Display name of the connected peer.
    /// </summary>
    public string PeerName { get; private set; } = string.Empty;

    /// <summary>
    /// Peer's screen width.
    /// </summary>
    public int PeerScreenWidth { get; private set; }

    /// <summary>
    /// Peer's screen height.
    /// </summary>
    public int PeerScreenHeight { get; private set; }

    /// <summary>
    /// Remote endpoint address.
    /// </summary>
    public IPEndPoint? RemoteEndPoint => _client.Client.RemoteEndPoint as IPEndPoint;

    /// <summary>
    /// Whether the connection is established and active.
    /// </summary>
    public bool IsConnected => _client.Connected && !_disposed;

    /// <summary>
    /// Event raised when a message is received.
    /// </summary>
    public event EventHandler<ProtocolMessage>? MessageReceived;

    /// <summary>
    /// Event raised when the connection is lost.
    /// </summary>
    public event EventHandler<Exception?>? Disconnected;

    private PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true; // Disable Nagle's algorithm for lower latency
        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_cts.Token);
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
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);

        var connection = new PeerConnection(client);

        // Send handshake
        var handshake = new HandshakeMessage
        {
            MachineId = localMachineId,
            MachineName = localMachineName,
            ScreenWidth = localScreenWidth,
            ScreenHeight = localScreenHeight,
            SupportsClipboard = true
        };

        await connection.SendAsync(handshake, ct);

        // Wait for handshake acknowledgment
        var response = await connection.ReceiveMessageAsync(ct);

        if (response is not HandshakeAckMessage ack)
        {
            connection.Dispose();
            throw new InvalidOperationException("Invalid handshake response");
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
        var connection = new PeerConnection(client);

        // Wait for handshake
        var message = await connection.ReceiveMessageAsync(ct);

        if (message is not HandshakeMessage handshake)
        {
            connection.Dispose();
            throw new InvalidOperationException("Expected handshake message");
        }

        connection.PeerId = handshake.MachineId;
        connection.PeerName = handshake.MachineName;
        connection.PeerScreenWidth = handshake.ScreenWidth;
        connection.PeerScreenHeight = handshake.ScreenHeight;

        // Send acknowledgment
        var ack = new HandshakeAckMessage
        {
            Accepted = true,
            MachineId = localMachineId,
            MachineName = localMachineName,
            ScreenWidth = localScreenWidth,
            ScreenHeight = localScreenHeight
        };

        await connection.SendAsync(ack, ct);

        return connection;
    }

    /// <summary>
    /// Sends a message to the peer.
    /// </summary>
    public async Task SendAsync(ProtocolMessage message, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PeerConnection));

        var data = message.Serialize();

        await _sendLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Receives a single message from the peer.
    /// </summary>
    private async Task<ProtocolMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[16];
        var bytesRead = 0;

        // Read header
        while (bytesRead < 16)
        {
            var read = await _stream.ReadAsync(headerBuffer.AsMemory(bytesRead, 16 - bytesRead), ct);
            if (read == 0)
                return null;
            bytesRead += read;
        }

        // Get total message size
        var messageSize = ProtocolMessage.GetMessageSize(headerBuffer);
        if (messageSize < 0)
            return null;

        // Read remaining data
        var fullBuffer = new byte[messageSize];
        headerBuffer.CopyTo(fullBuffer, 0);

        bytesRead = 16;
        while (bytesRead < messageSize)
        {
            var read = await _stream.ReadAsync(fullBuffer.AsMemory(bytesRead, messageSize - bytesRead), ct);
            if (read == 0)
                return null;
            bytesRead += read;
        }

        return ProtocolMessage.Deserialize(fullBuffer);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        Exception? disconnectReason = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(ct);

                if (message == null)
                {
                    // Connection closed gracefully
                    break;
                }

                // Handle ping internally
                if (message is PingMessage)
                {
                    await SendAsync(new PongMessage(), ct);
                    continue;
                }

                MessageReceived?.Invoke(this, message);

                if (message is DisconnectMessage)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (IOException ex)
        {
            disconnectReason = ex;
        }
        catch (SocketException ex)
        {
            disconnectReason = ex;
        }
        catch (Exception ex)
        {
            disconnectReason = ex;
        }

        if (!_disposed)
        {
            Disconnected?.Invoke(this, disconnectReason);
        }
    }

    /// <summary>
    /// Gracefully disconnects from the peer.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_disposed)
            return;

        try
        {
            await SendAsync(new DisconnectMessage());
        }
        catch { }

        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try { _stream.Close(); } catch { }
        try { _client.Close(); } catch { }

        _stream.Dispose();
        _client.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
