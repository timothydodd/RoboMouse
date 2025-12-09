using System.Net;
using System.Net.Sockets;

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
    /// Event raised when a new peer connects.
    /// </summary>
    public event EventHandler<PeerConnection>? PeerConnected;

    /// <summary>
    /// Event raised when an error occurs while accepting connections.
    /// </summary>
    public event EventHandler<Exception>? AcceptError;

    public ConnectionListener(
        int port,
        string machineId,
        string machineName,
        int screenWidth,
        int screenHeight)
    {
        Port = port;
        _machineId = machineId;
        _machineName = machineName;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>
    /// Starts listening for connections.
    /// </summary>
    public void Start()
    {
        if (IsListening)
            return;

        _cts = new CancellationTokenSource();
        _listener.Start();
        IsListening = true;

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

                // Handle connection in background
                _ = HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
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

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var connection = await PeerConnection.AcceptAsync(
                client,
                _machineId,
                _machineName,
                _screenWidth,
                _screenHeight,
                ct);

            PeerConnected?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to accept connection: {ex.Message}");
            client.Dispose();
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
