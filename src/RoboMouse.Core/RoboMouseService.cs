using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using RoboMouse.Core.Screen;
using InputMouseEventArgs = RoboMouse.Core.Input.MouseEventArgs;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core;

/// <summary>
/// The main service that coordinates mouse/keyboard sharing.
/// </summary>
public sealed class RoboMouseService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ScreenInfo _screenInfo;
    private readonly CursorManager _cursorManager;
    private readonly MouseHook _mouseHook;
    private readonly KeyboardHook _keyboardHook;
    private readonly ClipboardManager _clipboardManager;
    private readonly PeerDiscovery _discovery;
    private readonly ConnectionListener _listener;

    private readonly Dictionary<string, PeerConnection> _connections = new();
    private readonly object _connectionLock = new();

    private PeerConfig? _activePeer;
    private bool _isControllingRemote;
    private bool _isControlledByRemote;
    private bool _enabled;
    private bool _disposed;

    /// <summary>
    /// Whether the service is enabled.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            OnEnabledChanged();
        }
    }

    /// <summary>
    /// Whether we are currently controlling a remote machine.
    /// </summary>
    public bool IsControllingRemote => _isControllingRemote;

    /// <summary>
    /// Whether we are currently being controlled by a remote machine.
    /// </summary>
    public bool IsControlledByRemote => _isControlledByRemote;

    /// <summary>
    /// The currently active peer configuration.
    /// </summary>
    public PeerConfig? ActivePeer => _activePeer;

    /// <summary>
    /// Connected peers.
    /// </summary>
    public IReadOnlyCollection<PeerConnection> ConnectedPeers
    {
        get
        {
            lock (_connectionLock)
            {
                return _connections.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Discovered peers on the network.
    /// </summary>
    public IReadOnlyCollection<DiscoveredPeer> DiscoveredPeers => _discovery.Peers;

    /// <summary>
    /// Event raised when connection status changes.
    /// </summary>
    public event EventHandler<PeerConnection>? PeerConnected;

    /// <summary>
    /// Event raised when a peer disconnects.
    /// </summary>
    public event EventHandler<string>? PeerDisconnected;

    /// <summary>
    /// Event raised when a new peer is discovered.
    /// </summary>
    public event EventHandler<DiscoveredPeer>? PeerDiscovered;

    /// <summary>
    /// Event raised when control state changes.
    /// </summary>
    public event EventHandler? ControlStateChanged;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event EventHandler<Exception>? Error;

    public RoboMouseService(AppSettings settings)
    {
        _settings = settings;
        _screenInfo = new ScreenInfo();
        _cursorManager = new CursorManager(_screenInfo);

        _mouseHook = new MouseHook();
        _mouseHook.MouseEvent += OnMouseEvent;

        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyboardEvent += OnKeyboardEvent;

        _clipboardManager = new ClipboardManager(_settings.Clipboard.MaxSizeBytes);
        _clipboardManager.ClipboardChanged += OnClipboardChanged;

        var (width, height) = InputSimulator.GetPrimaryScreenSize();

        _discovery = new PeerDiscovery(
            _settings.DiscoveryPort,
            _settings.LocalPort,
            _settings.MachineId,
            _settings.MachineName,
            width,
            height);
        _discovery.PeerDiscovered += OnPeerDiscovered;
        _discovery.PeerLost += OnPeerLost;

        _listener = new ConnectionListener(
            _settings.LocalPort,
            _settings.MachineId,
            _settings.MachineName,
            width,
            height);
        _listener.PeerConnected += OnIncomingConnection;
    }

    /// <summary>
    /// Starts the service.
    /// </summary>
    public void Start()
    {
        _listener.Start();
        _discovery.Start();

        if (_settings.Clipboard.Enabled)
        {
            _clipboardManager.Start();
        }

        _enabled = _settings.Enabled;
        OnEnabledChanged();
    }

    /// <summary>
    /// Stops the service.
    /// </summary>
    public void Stop()
    {
        _enabled = false;
        OnEnabledChanged();

        _clipboardManager.Stop();
        _discovery.Stop();
        _listener.Stop();

        lock (_connectionLock)
        {
            foreach (var connection in _connections.Values)
            {
                connection.Dispose();
            }
            _connections.Clear();
        }
    }

    /// <summary>
    /// Connects to a peer.
    /// </summary>
    public async Task ConnectToPeerAsync(PeerConfig peerConfig, CancellationToken ct = default)
    {
        var (width, height) = InputSimulator.GetPrimaryScreenSize();

        var connection = await PeerConnection.ConnectAsync(
            peerConfig.Address,
            peerConfig.Port,
            _settings.MachineId,
            _settings.MachineName,
            width,
            height,
            ct);

        // Update peer config with received screen info
        peerConfig.ScreenWidth = connection.PeerScreenWidth;
        peerConfig.ScreenHeight = connection.PeerScreenHeight;

        // Update the peer ID to match what the remote machine reports
        peerConfig.Id = connection.PeerId;

        AddConnection(connection);
    }

    /// <summary>
    /// Connects to a peer by IP address. Creates and saves the peer config.
    /// </summary>
    public async Task<PeerConfig> ConnectToAddressAsync(string address, int port, ScreenPosition position, CancellationToken ct = default)
    {
        var peerConfig = new PeerConfig
        {
            Address = address,
            Port = port,
            Position = position,
            Name = address // Will be updated after connection
        };

        await ConnectToPeerAsync(peerConfig, ct);

        // Update name from connection info
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(peerConfig.Id, out var conn))
            {
                peerConfig.Name = conn.PeerName;
            }
        }

        // Add to settings if not already present
        var existing = _settings.Peers.FirstOrDefault(p => p.Id == peerConfig.Id);
        if (existing == null)
        {
            _settings.Peers.Add(peerConfig);
        }
        else
        {
            // Update existing config
            existing.Address = peerConfig.Address;
            existing.Port = peerConfig.Port;
            existing.Position = peerConfig.Position;
            existing.Name = peerConfig.Name;
        }

        return peerConfig;
    }

    /// <summary>
    /// Connects to all configured peers that have addresses.
    /// </summary>
    public async Task ConnectToConfiguredPeersAsync(CancellationToken ct = default)
    {
        var peersToConnect = _settings.Peers.Where(p => !string.IsNullOrEmpty(p.Address)).ToList();

        foreach (var peer in peersToConnect)
        {
            try
            {
                // Skip if already connected
                lock (_connectionLock)
                {
                    if (_connections.ContainsKey(peer.Id))
                        continue;
                }

                await ConnectToPeerAsync(peer, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to connect to {peer.Name} ({peer.Address}): {ex.Message}");
                // Continue trying other peers
            }
        }
    }

    /// <summary>
    /// Connects to a discovered peer.
    /// </summary>
    public async Task ConnectToPeerAsync(DiscoveredPeer peer, ScreenPosition position, CancellationToken ct = default)
    {
        var peerConfig = new PeerConfig
        {
            Id = peer.MachineId,
            Name = peer.MachineName,
            Address = peer.Address.ToString(),
            Port = peer.Port,
            Position = position,
            ScreenWidth = peer.ScreenWidth,
            ScreenHeight = peer.ScreenHeight
        };

        await ConnectToPeerAsync(peerConfig, ct);
    }

    /// <summary>
    /// Disconnects from a peer.
    /// </summary>
    public async Task DisconnectFromPeerAsync(string peerId)
    {
        PeerConnection? connection;
        lock (_connectionLock)
        {
            _connections.TryGetValue(peerId, out connection);
        }

        if (connection != null)
        {
            await connection.DisconnectAsync();
            RemoveConnection(peerId);
        }
    }

    private void AddConnection(PeerConnection connection)
    {
        lock (_connectionLock)
        {
            _connections[connection.PeerId] = connection;
        }

        connection.MessageReceived += OnMessageReceived;
        connection.Disconnected += (s, e) => RemoveConnection(connection.PeerId);

        PeerConnected?.Invoke(this, connection);
    }

    private void RemoveConnection(string peerId)
    {
        PeerConnection? connection;
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(peerId, out connection))
            {
                _connections.Remove(peerId);
            }
        }

        if (connection != null)
        {
            connection.Dispose();
            PeerDisconnected?.Invoke(this, peerId);

            // Release cursor if we were controlling/controlled by this peer
            if (_activePeer?.Id == peerId)
            {
                EndRemoteControl();
            }
        }
    }

    private void OnEnabledChanged()
    {
        if (_enabled)
        {
            _mouseHook.Install();
            _keyboardHook.Install();
        }
        else
        {
            _mouseHook.Uninstall();
            _keyboardHook.Uninstall();
            EndRemoteControl();
        }
    }

    private void OnMouseEvent(object? sender, InputMouseEventArgs e)
    {
        if (!_enabled)
            return;

        // If we're being controlled, ignore local input
        if (_isControlledByRemote)
        {
            return;
        }

        // If we're controlling a remote, forward input
        if (_isControllingRemote && _activePeer != null)
        {
            e.Handled = true;
            SendToActivePeer(MouseMessage.FromEvent(e));

            // Check if returning from remote
            var edge = _screenInfo.GetEdgeAt(e.X, e.Y, _settings.EdgeThreshold);
            if (edge != null && ShouldReturnFromRemote(edge.Edge, _activePeer.Position))
            {
                EndRemoteControl();
                _cursorManager.ReleaseAt(
                    CursorManager.GetOppositeEdge(_activePeer.Position),
                    edge.NormalizedPosition);
            }
            return;
        }

        // Check for edge transition to remote
        if (e.EventType == MouseEventType.Move)
        {
            var edge = _screenInfo.GetEdgeAt(e.X, e.Y, _settings.EdgeThreshold);
            if (edge != null)
            {
                var targetPeer = GetPeerAtEdge(edge.Edge);
                if (targetPeer != null)
                {
                    StartRemoteControl(targetPeer, edge);
                    e.Handled = true;
                }
            }
        }
    }

    private void OnKeyboardEvent(object? sender, KeyboardEventArgs e)
    {
        if (!_enabled)
            return;

        // If we're being controlled, ignore local input
        if (_isControlledByRemote)
        {
            return;
        }

        // If we're controlling a remote, forward input
        if (_isControllingRemote && _activePeer != null)
        {
            e.Handled = true;
            SendToActivePeer(KeyboardMessage.FromEvent(e));
        }
    }

    private void OnClipboardChanged(object? sender, ClipboardMessage message)
    {
        if (!_enabled || !_settings.Clipboard.Enabled)
            return;

        // Broadcast to all connected peers
        lock (_connectionLock)
        {
            foreach (var connection in _connections.Values)
            {
                _ = connection.SendAsync(message);
            }
        }
    }

    private void OnMessageReceived(object? sender, ProtocolMessage message)
    {
        var connection = sender as PeerConnection;
        if (connection == null)
            return;

        try
        {
            switch (message)
            {
                case MouseMessage mouseMsg:
                    HandleRemoteMouseInput(mouseMsg, connection);
                    break;

                case KeyboardMessage keyMsg:
                    HandleRemoteKeyboardInput(keyMsg);
                    break;

                case CursorEnterMessage enterMsg:
                    HandleCursorEnter(enterMsg, connection);
                    break;

                case CursorLeaveMessage leaveMsg:
                    HandleCursorLeave(leaveMsg, connection);
                    break;

                case ClipboardMessage clipMsg:
                    HandleRemoteClipboard(clipMsg);
                    break;
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
    }

    private void HandleRemoteMouseInput(MouseMessage msg, PeerConnection connection)
    {
        if (!_isControlledByRemote)
            return;

        // Convert coordinates based on peer's screen size
        var peerConfig = GetPeerConfig(connection.PeerId);
        if (peerConfig == null)
            return;

        var localX = (int)(msg.X * _screenInfo.PrimaryBounds.Width / (float)peerConfig.ScreenWidth);
        var localY = (int)(msg.Y * _screenInfo.PrimaryBounds.Height / (float)peerConfig.ScreenHeight);

        if (msg.EventType == MouseEventType.Move)
        {
            InputSimulator.MoveTo(localX, localY);
        }
        else
        {
            InputSimulator.SimulateMouseEvent(msg.EventType, wheelDelta: msg.WheelDelta);
        }
    }

    private void HandleRemoteKeyboardInput(KeyboardMessage msg)
    {
        if (!_isControlledByRemote)
            return;

        InputSimulator.SimulateKeyboardEvent(msg.KeyCode, msg.ScanCode, msg.EventType, msg.IsExtendedKey);
    }

    private void HandleCursorEnter(CursorEnterMessage msg, PeerConnection connection)
    {
        _isControlledByRemote = true;

        // Calculate entry position
        var bounds = _screenInfo.PrimaryBounds;
        int x, y;

        switch (msg.EntryEdge)
        {
            case ScreenPosition.Left:
                x = bounds.Left;
                y = bounds.Top + (int)(msg.EntryY * bounds.Height);
                break;
            case ScreenPosition.Right:
                x = bounds.Right - 1;
                y = bounds.Top + (int)(msg.EntryY * bounds.Height);
                break;
            case ScreenPosition.Top:
                x = bounds.Left + (int)(msg.EntryX * bounds.Width);
                y = bounds.Top;
                break;
            case ScreenPosition.Bottom:
                x = bounds.Left + (int)(msg.EntryX * bounds.Width);
                y = bounds.Bottom - 1;
                break;
            default:
                x = bounds.Width / 2;
                y = bounds.Height / 2;
                break;
        }

        InputSimulator.MoveTo(x, y);
        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleCursorLeave(CursorLeaveMessage msg, PeerConnection connection)
    {
        _isControlledByRemote = false;
        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRemoteClipboard(ClipboardMessage msg)
    {
        if (!_settings.Clipboard.Enabled)
            return;

        _clipboardManager.SetClipboard(msg);
    }

    private void OnPeerDiscovered(object? sender, DiscoveredPeer peer)
    {
        PeerDiscovered?.Invoke(this, peer);
    }

    private void OnPeerLost(object? sender, DiscoveredPeer peer)
    {
        // Could notify UI
    }

    private void OnIncomingConnection(object? sender, PeerConnection connection)
    {
        // Add to connections but don't set as active peer yet
        AddConnection(connection);
    }

    private void StartRemoteControl(PeerConfig peer, EdgeInfo edge)
    {
        _activePeer = peer;
        _isControllingRemote = true;
        _cursorManager.Capture(edge.X, edge.Y);

        // Notify the peer that cursor is entering
        var enterMsg = new CursorEnterMessage
        {
            EntryX = edge.NormalizedPosition,
            EntryY = edge.NormalizedPosition,
            EntryEdge = CursorManager.GetOppositeEdge(peer.Position)
        };

        SendToActivePeer(enterMsg);
        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndRemoteControl()
    {
        if (!_isControllingRemote)
            return;

        _cursorManager.Release();

        if (_activePeer != null)
        {
            var leaveMsg = new CursorLeaveMessage
            {
                ExitX = 0.5f,
                ExitY = 0.5f,
                ExitEdge = CursorManager.GetOppositeEdge(_activePeer.Position)
            };
            SendToActivePeer(leaveMsg);
        }

        _isControllingRemote = false;
        _activePeer = null;
        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SendToActivePeer(ProtocolMessage message)
    {
        if (_activePeer == null)
            return;

        PeerConnection? connection;
        lock (_connectionLock)
        {
            _connections.TryGetValue(_activePeer.Id, out connection);
        }

        if (connection != null)
        {
            _ = connection.SendAsync(message);
        }
    }

    private PeerConfig? GetPeerAtEdge(ScreenPosition edge)
    {
        // Find configured peer at this edge
        var peer = _settings.Peers.FirstOrDefault(p => p.Position == edge);
        if (peer == null)
            return null;

        // Check if connected to this peer
        lock (_connectionLock)
        {
            if (!_connections.ContainsKey(peer.Id))
                return null;
        }

        return peer;
    }

    private PeerConfig? GetPeerConfig(string peerId)
    {
        return _settings.Peers.FirstOrDefault(p => p.Id == peerId);
    }

    private bool ShouldReturnFromRemote(ScreenPosition currentEdge, ScreenPosition peerPosition)
    {
        // Return when hitting the opposite edge
        return currentEdge == CursorManager.GetOppositeEdge(peerPosition);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _clipboardManager.Dispose();
        _discovery.Dispose();
        _listener.Dispose();
    }
}
