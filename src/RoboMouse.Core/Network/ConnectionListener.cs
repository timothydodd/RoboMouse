using System.Net;
using System.Net.Sockets;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Network;

/// <summary>
/// Listens for incoming peer connections.
/// </summary>
public sealed class ConnectionListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _machineId;
    private readonly string _machineName;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private readonly Func<ChannelCredentials> _credentials;
    private readonly HandshakeGate _gate = new();
    private readonly LogThrottle _rejectLog = new(TimeSpan.FromMinutes(1));
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;

    /// <summary>
    /// The port the listener is bound to.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Whether the listener is currently running.
    /// </summary>
    public bool IsListening { get; private set; }

    /// <summary>
    /// Decides, from a peer's handshake, address and identity key, whether to take the connection:
    /// null accepts it, anything else is the reject reason sent back (see <see cref="RejectReasons"/>).
    /// Runs on the accept path, after the secure handshake. Without a policy everything is accepted.
    /// </summary>
    public Func<IncomingPeer, string?>? AcceptPolicy { get; set; }

    /// <summary>
    /// Event raised when a new peer connects.
    /// </summary>
    public event EventHandler<PeerConnection>? PeerConnected;

    /// <summary>
    /// Event raised when an error occurs while accepting connections.
    /// </summary>
    public event EventHandler<Exception>? AcceptError;

    public ConnectionListener(
        int port,
        Func<ChannelCredentials> credentials,
        string machineId,
        string machineName,
        int screenWidth,
        int screenHeight)
    {
        Port = port;
        _credentials = credentials;
        _machineId = machineId;
        _machineName = machineName;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>
    /// Starts listening for connections. Throws <see cref="SocketException"/> when the port cannot be
    /// bound (another program already uses it).
    /// </summary>
    public void Start()
    {
        if (IsListening)
            return;

        _listener.Start();
        _cts = new CancellationTokenSource();
        IsListening = true;

        SimpleLogger.Log("Listener", $"Started listening on port {Port}");

        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stops listening for connections.
    /// </summary>
    public void Stop()
    {
        if (!IsListening)
            return;

        IsListening = false;
        _cts?.Cancel();
        _listener.Stop();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);

                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                if (!_gate.TryEnter(address))
                {
                    _rejectLog.Log(address.ToString(), "Listener", $"Too many handshakes in progress; dropping connection from {address}");
                    client.Dispose();
                    continue;
                }

                _ = HandleConnectionAsync(client, address, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (!IsListening)
            {
                // Listener stopped
                break;
            }
            catch (Exception ex)
            {
                AcceptError?.Invoke(this, ex);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, IPAddress address, CancellationToken ct)
    {
        var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        try
        {
            var connection = await PeerConnection.AcceptAsync(
                client,
                _credentials(),
                _machineId,
                _machineName,
                _screenWidth,
                _screenHeight,
                Port,
                ct,
                AcceptPolicy);

            PeerConnected?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            // Rejections repeat every few seconds while the other machine keeps retrying.
            _rejectLog.Log(address + ":" + ex.GetType().Name, "Accept", $"Rejected {remoteEp}: {ex.Message}");
            client.Dispose();
        }
        finally
        {
            _gate.Exit(address);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
