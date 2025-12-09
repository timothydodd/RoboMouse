using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;
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

    // Virtual cursor position on remote screen (0.0 to 1.0 normalized)
    private float _virtualX;
    private float _virtualY;
    private bool _hasMovedIntoRemote; // Must move away from entry edge before return is allowed

    // Track last seen mouse position for delta calculation (avoids race conditions with warp)
    private int _lastSeenX;
    private int _lastSeenY;

    // Velocity tracking for smooth movement
    private float _velocityX;
    private float _velocityY;
    private long _lastMoveTime;

    // Track last sent velocity to avoid redundant sends
    private float _lastSentVelocityX;
    private float _lastSentVelocityY;
    private long _lastVelocitySendTime;
    private const float VelocityChangeThreshold = 50f; // px/s change to trigger send
    private const int VelocitySendIntervalMs = 100; // send at least every 100ms

    // Flag to ignore warp-back events
    private bool _isWarping;


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

    /// <summary>
    /// Event raised when mouse debug data is updated (for debug panel).
    /// </summary>
    public event EventHandler<MouseDebugEventArgs>? MouseDebugUpdate;

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

            if (e.EventType == MouseEventType.Move)
            {
                // Ignore events generated by our own warp-back
                if (_isWarping)
                {
                    _isWarping = false;
                    _lastSeenX = e.X;
                    _lastSeenY = e.Y;
                    return;
                }

                // Calculate delta from last seen position (not captured position)
                // This avoids race conditions where warp-back hasn't completed yet
                var deltaX = e.X - _lastSeenX;
                var deltaY = e.Y - _lastSeenY;

                // Update last seen position BEFORE any other processing
                _lastSeenX = e.X;
                _lastSeenY = e.Y;

                // Skip if no movement
                if (deltaX == 0 && deltaY == 0)
                    return;

                // Calculate velocity for prediction
                var now = Environment.TickCount64;
                var timeDelta = now - _lastMoveTime;
                if (timeDelta > 0 && timeDelta < 1000) // Only calculate velocity for reasonable intervals
                {
                    // Velocity in pixels per second
                    var newVelX = (deltaX * 1000f) / timeDelta;
                    var newVelY = (deltaY * 1000f) / timeDelta;

                    // Smooth velocity with exponential moving average
                    const float smoothing = 0.3f;
                    _velocityX = _velocityX * (1 - smoothing) + newVelX * smoothing;
                    _velocityY = _velocityY * (1 - smoothing) + newVelY * smoothing;
                }
                else
                {
                    // Reset velocity on first move or after long pause
                    _velocityX = 0;
                    _velocityY = 0;
                }
                _lastMoveTime = now;

                // Update virtual position on remote screen (normalized 0-1)
                _virtualX += (float)deltaX / _activePeer.ScreenWidth;
                _virtualY += (float)deltaY / _activePeer.ScreenHeight;

                // Clamp to screen bounds
                _virtualX = Math.Clamp(_virtualX, 0f, 1f);
                _virtualY = Math.Clamp(_virtualY, 0f, 1f);

                // Check if we've moved into the remote screen
                if (!_hasMovedIntoRemote)
                {
                    _hasMovedIntoRemote = HasMovedIntoRemote(_activePeer.Position, _virtualX, _virtualY);
                }

                // Check if returning from remote (hit opposite edge)
                if (_hasMovedIntoRemote)
                {
                    var returnEdge = GetReturnEdge(_activePeer.Position, _virtualX, _virtualY);
                    if (returnEdge != null)
                    {
                        var peerPosition = _activePeer.Position;
                        var normalizedPos = peerPosition is ScreenPosition.Left or ScreenPosition.Right
                            ? _virtualY : _virtualX;
                        EndRemoteControl();
                        _cursorManager.ReleaseAt(peerPosition, normalizedPos);
                        return;
                    }
                }

                // Send position to remote
                var remoteX = (int)(_virtualX * _activePeer.ScreenWidth);
                var remoteY = (int)(_virtualY * _activePeer.ScreenHeight);

                // Only include velocity if it changed significantly or enough time passed
                var velocityDeltaX = Math.Abs(_velocityX - _lastSentVelocityX);
                var velocityDeltaY = Math.Abs(_velocityY - _lastSentVelocityY);
                var timeSinceVelocitySend = now - _lastVelocitySendTime;

                var shouldSendVelocity = velocityDeltaX > VelocityChangeThreshold ||
                                         velocityDeltaY > VelocityChangeThreshold ||
                                         timeSinceVelocitySend > VelocitySendIntervalMs;

                var msg = new MouseMessage
                {
                    X = remoteX,
                    Y = remoteY,
                    EventType = MouseEventType.Move,
                    WheelDelta = 0,
                    VelocityX = shouldSendVelocity ? _velocityX : 0,
                    VelocityY = shouldSendVelocity ? _velocityY : 0
                };

                if (shouldSendVelocity)
                {
                    _lastSentVelocityX = _velocityX;
                    _lastSentVelocityY = _velocityY;
                    _lastVelocitySendTime = now;
                }

                SendToActivePeer(msg);

                // Fire debug event for UI
                MouseDebugUpdate?.Invoke(this, new MouseDebugEventArgs
                {
                    IsControlling = true,
                    PeerName = _activePeer.Name,
                    LocalX = e.X,
                    LocalY = e.Y,
                    VirtualX = _virtualX,
                    VirtualY = _virtualY,
                    DeltaX = deltaX,
                    DeltaY = deltaY,
                    VelocityX = _velocityX,
                    VelocityY = _velocityY
                });

                // Only warp back if cursor is getting close to screen edge
                // This reduces overhead while preventing cursor from escaping
                var bounds = _screenInfo.PrimaryBounds;
                var edgeMargin = 100; // pixels from edge before we warp back

                var nearEdge = e.X < bounds.Left + edgeMargin ||
                               e.X > bounds.Right - edgeMargin ||
                               e.Y < bounds.Top + edgeMargin ||
                               e.Y > bounds.Bottom - edgeMargin;

                if (nearEdge)
                {
                    var capturedPos = _cursorManager.CapturedPosition;
                    _isWarping = true;
                    InputSimulator.MoveTo(capturedPos.X, capturedPos.Y);
                    _lastSeenX = capturedPos.X;
                    _lastSeenY = capturedPos.Y;
                }
            }
            else
            {
                // For clicks/wheel, use current virtual position
                var msg = new MouseMessage
                {
                    X = (int)(_virtualX * _activePeer.ScreenWidth),
                    Y = (int)(_virtualY * _activePeer.ScreenHeight),
                    EventType = e.EventType,
                    WheelDelta = e.WheelDelta
                };
                SendToActivePeer(msg);
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
                    SimpleLogger.Log("Control", $"Starting remote control to {targetPeer.Name} at edge {edge.Edge}");
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
                    SimpleLogger.Log("Input", $"MouseMsg: Type={mouseMsg.EventType}, Pos=({mouseMsg.X},{mouseMsg.Y}), Controlled={_isControlledByRemote}");
                    HandleRemoteMouseInput(mouseMsg, connection);
                    break;

                case KeyboardMessage keyMsg:
                    HandleRemoteKeyboardInput(keyMsg);
                    break;

                case CursorEnterMessage enterMsg:
                    SimpleLogger.Log("Input", $"CursorEnter: Edge={enterMsg.EntryEdge}, Pos=({enterMsg.EntryX},{enterMsg.EntryY})");
                    HandleCursorEnter(enterMsg, connection);
                    break;

                case CursorLeaveMessage leaveMsg:
                    SimpleLogger.Log("Input", "CursorLeave received");
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

    // For velocity-based prediction on receiver
    private long _lastRemoteMoveTime;
    private float _predictedX;
    private float _predictedY;
    private float _remoteVelocityX;
    private float _remoteVelocityY;

    private void HandleRemoteMouseInput(MouseMessage msg, PeerConnection connection)
    {
        if (!_isControlledByRemote)
            return;

        // The sender now sends coordinates in OUR screen space directly
        // (they track a virtual cursor position and scale it to our screen dimensions)
        var localX = (float)msg.X;
        var localY = (float)msg.Y;

        // Apply velocity-based prediction to compensate for network latency
        if (msg.EventType == MouseEventType.Move && (msg.VelocityX != 0 || msg.VelocityY != 0))
        {
            var now = Environment.TickCount64;
            var timeSinceLastUpdate = now - _lastRemoteMoveTime;

            // Only predict for reasonable time windows (up to 100ms of latency compensation)
            if (_lastRemoteMoveTime > 0 && timeSinceLastUpdate < 100)
            {
                // Predict position based on velocity and time since last known position
                // Use a fraction of the prediction to avoid overshooting
                const float predictionFactor = 0.5f;
                var predictMs = timeSinceLastUpdate * predictionFactor / 1000f;
                localX += msg.VelocityX * predictMs;
                localY += msg.VelocityY * predictMs;
            }

            _lastRemoteMoveTime = now;
            _remoteVelocityX = msg.VelocityX;
            _remoteVelocityY = msg.VelocityY;
        }

        // Clamp to screen bounds
        var bounds = _screenInfo.PrimaryBounds;
        var clampedX = (int)Math.Clamp(localX, bounds.Left, bounds.Right - 1);
        var clampedY = (int)Math.Clamp(localY, bounds.Top, bounds.Bottom - 1);

        if (msg.EventType == MouseEventType.Move)
        {
            InputSimulator.MoveTo(clampedX, clampedY);
        }
        else
        {
            // Move to position first for clicks
            InputSimulator.MoveTo(clampedX, clampedY);
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
        SimpleLogger.Log("Control", $"HandleCursorEnter: Setting _isControlledByRemote = true, entry edge = {msg.EntryEdge}");
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
        _hasMovedIntoRemote = false;

        // Initialize virtual cursor position based on entry edge
        switch (peer.Position)
        {
            case ScreenPosition.Right:
                _virtualX = 0f; // Enter from left of remote screen
                _virtualY = edge.NormalizedPosition;
                break;
            case ScreenPosition.Left:
                _virtualX = 1f; // Enter from right of remote screen
                _virtualY = edge.NormalizedPosition;
                break;
            case ScreenPosition.Bottom:
                _virtualX = edge.NormalizedPosition;
                _virtualY = 0f; // Enter from top of remote screen
                break;
            case ScreenPosition.Top:
                _virtualX = edge.NormalizedPosition;
                _virtualY = 1f; // Enter from bottom of remote screen
                break;
        }

        SimpleLogger.Log("Control", $"StartRemoteControl: peer={peer.Name}, virtualPos=({_virtualX:F2},{_virtualY:F2})");

        // Initialize last seen position for delta tracking
        _lastSeenX = edge.X;
        _lastSeenY = edge.Y;

        // Reset velocity tracking
        _velocityX = 0;
        _velocityY = 0;
        _lastSentVelocityX = 0;
        _lastSentVelocityY = 0;
        _lastVelocitySendTime = 0;
        _isWarping = false;

        // Capture cursor at edge position
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

        // Release cursor
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

    /// <summary>
    /// Checks if we've moved far enough into the remote screen.
    /// </summary>
    private bool HasMovedIntoRemote(ScreenPosition peerPosition, float virtualX, float virtualY)
    {
        const float threshold = 0.05f; // 5% into screen

        return peerPosition switch
        {
            ScreenPosition.Right => virtualX >= threshold,
            ScreenPosition.Left => virtualX <= 1f - threshold,
            ScreenPosition.Bottom => virtualY >= threshold,
            ScreenPosition.Top => virtualY <= 1f - threshold,
            _ => false
        };
    }

    /// <summary>
    /// Checks if virtual cursor has hit the return edge.
    /// </summary>
    private ScreenPosition? GetReturnEdge(ScreenPosition peerPosition, float virtualX, float virtualY)
    {
        const float threshold = 0.01f; // 1% from edge

        return peerPosition switch
        {
            ScreenPosition.Right => virtualX <= threshold ? ScreenPosition.Left : null,
            ScreenPosition.Left => virtualX >= 1f - threshold ? ScreenPosition.Right : null,
            ScreenPosition.Bottom => virtualY <= threshold ? ScreenPosition.Top : null,
            ScreenPosition.Top => virtualY >= 1f - threshold ? ScreenPosition.Bottom : null,
            _ => null
        };
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

/// <summary>
/// Event args for mouse debug updates.
/// </summary>
public class MouseDebugEventArgs : EventArgs
{
    public bool IsControlling { get; set; }
    public string? PeerName { get; set; }
    public int LocalX { get; set; }
    public int LocalY { get; set; }
    public float VirtualX { get; set; }
    public float VirtualY { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
}
