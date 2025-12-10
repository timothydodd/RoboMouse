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

    // Accumulated cursor position on remote screen (in remote screen pixel coordinates)
    // This is tracked on the server side by accumulating deltas from local mouse movement
    private int _remoteX;
    private int _remoteY;
    private bool _hasMovedIntoRemote; // Must move away from entry edge before return is allowed

    // Track last seen mouse position for delta calculation
    // We save this after each warp to center so we can calculate the next delta
    private int _lastSeenX;
    private int _lastSeenY;

    // Velocity tracking for smooth movement
    private float _velocityX;
    private float _velocityY;
    private long _lastMoveTime;

    // Cooldown to prevent immediate re-entry after returning from remote
    private long _returnCooldownUntil;



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
            if (e.EventType == MouseEventType.Move)
            {
                var capturedPos = _cursorManager.CapturedPosition;

                // Calculate delta from last seen position (like Deskflow)
                var deltaX = e.X - _lastSeenX;
                var deltaY = e.Y - _lastSeenY;

                // Save position to compute delta of next motion
                _lastSeenX = e.X;
                _lastSeenY = e.Y;

                // Ignore if the mouse didn't move
                if (deltaX == 0 && deltaY == 0)
                    return;

                // Warp cursor back to center (like Deskflow does on every move)
                // This allows infinite movement regardless of screen size differences
                InputSimulator.MoveTo(capturedPos.X, capturedPos.Y);

                // Filter out bogus motion from the warp itself
                // If the delta is suspiciously close to half the screen size, it's probably from the warp
                var bounds = _screenInfo.VirtualBounds;
                int halfW = bounds.Width / 2;
                int halfH = bounds.Height / 2;
                const int bogusZoneSize = 10;

                if (Math.Abs(deltaX) + bogusZoneSize > halfW || Math.Abs(deltaY) + bogusZoneSize > halfH)
                {
                    SimpleLogger.Log("Mouse", $"Dropped bogus delta motion: {deltaX},{deltaY}");
                    e.Handled = true;
                    return;
                }

                // Calculate velocity for prediction
                var now = Environment.TickCount64;
                var timeDelta = now - _lastMoveTime;
                if (timeDelta > 0 && timeDelta < 1000)
                {
                    var newVelX = (deltaX * 1000f) / timeDelta;
                    var newVelY = (deltaY * 1000f) / timeDelta;

                    const float smoothing = 0.3f;
                    _velocityX = _velocityX * (1 - smoothing) + newVelX * smoothing;
                    _velocityY = _velocityY * (1 - smoothing) + newVelY * smoothing;
                }
                else
                {
                    _velocityX = 0;
                    _velocityY = 0;
                }
                _lastMoveTime = now;

                // Accumulate motion into remote position (like Deskflow's m_x += dx, m_y += dy)
                _remoteX += deltaX;
                _remoteY += deltaY;

                // Get remote screen bounds
                int remoteW = _activePeer.ScreenWidth;
                int remoteH = _activePeer.ScreenHeight;

                // Check if we've moved into the remote screen (away from entry edge)
                if (!_hasMovedIntoRemote)
                {
                    _hasMovedIntoRemote = HasMovedIntoRemotePixels(_activePeer.Position, _remoteX, _remoteY, remoteW, remoteH);
                }

                // Check if returning from remote (hit opposite edge)
                if (_hasMovedIntoRemote)
                {
                    var returnEdge = GetReturnEdgePixels(_activePeer.Position, _remoteX, _remoteY, remoteW, remoteH);
                    if (returnEdge != null)
                    {
                        // Calculate normalized position for where to place cursor on local screen
                        var peerPosition = _activePeer.Position;
                        var normalizedPos = peerPosition is ScreenPosition.Left or ScreenPosition.Right
                            ? (float)_remoteY / remoteH
                            : (float)_remoteX / remoteW;
                        normalizedPos = Math.Clamp(normalizedPos, 0f, 1f);

                        // Set cooldown to prevent immediate re-entry
                        _returnCooldownUntil = Environment.TickCount64 + 500;

                        SimpleLogger.Log("Control", $"Return detected! Setting cooldown, peerPosition={peerPosition}");

                        // End remote control first (restores cursor visibility)
                        EndRemoteControl();

                        // Then position cursor at the edge we're returning from
                        _cursorManager.ReleaseAt(peerPosition, normalizedPos);

                        e.Handled = true;
                        return;
                    }
                }

                // Clamp position to remote screen bounds (like Deskflow)
                int clampedX = Math.Clamp(_remoteX, 0, remoteW - 1);
                int clampedY = Math.Clamp(_remoteY, 0, remoteH - 1);

                // Send absolute position to remote (like Deskflow's m_active->mouseMove(m_x, m_y))
                var msg = new MouseMessage
                {
                    X = clampedX,
                    Y = clampedY,
                    EventType = MouseEventType.Move,
                    WheelDelta = 0,
                    VelocityX = _velocityX,
                    VelocityY = _velocityY
                };
                SendToActivePeer(msg);

                // Fire debug event
                MouseDebugUpdate?.Invoke(this, new MouseDebugEventArgs
                {
                    IsControlling = true,
                    PeerName = _activePeer.Name,
                    LocalX = e.X,
                    LocalY = e.Y,
                    PrevX = e.X - deltaX,
                    PrevY = e.Y - deltaY,
                    VirtualX = (float)clampedX / remoteW,
                    VirtualY = (float)clampedY / remoteH,
                    DeltaX = deltaX,
                    DeltaY = deltaY,
                    VelocityX = _velocityX,
                    VelocityY = _velocityY,
                    RemoteX = clampedX,
                    RemoteY = clampedY,
                    PeerScreenWidth = remoteW,
                    PeerScreenHeight = remoteH,
                    CaptureX = capturedPos.X,
                    CaptureY = capturedPos.Y,
                    PeerPosition = _activePeer.Position.ToString()
                });

                e.Handled = true;
            }
            else
            {
                e.Handled = true;
                // For clicks/wheel, use current accumulated position
                var msg = new MouseMessage
                {
                    X = Math.Clamp(_remoteX, 0, _activePeer.ScreenWidth - 1),
                    Y = Math.Clamp(_remoteY, 0, _activePeer.ScreenHeight - 1),
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
            // Skip if we just returned from remote (cooldown period)
            var now = Environment.TickCount64;
            if (now < _returnCooldownUntil)
            {
                SimpleLogger.Log("Control", $"Cooldown active: {_returnCooldownUntil - now}ms remaining, ignoring edge at ({e.X}, {e.Y})");
                return;
            }

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

    private void HandleRemoteMouseInput(MouseMessage msg, PeerConnection connection)
    {
        if (!_isControlledByRemote)
            return;

        // The sender sends coordinates in OUR screen space directly
        var localX = msg.X;
        var localY = msg.Y;

        // Clamp to screen bounds
        var bounds = _screenInfo.PrimaryBounds;
        var clampedX = Math.Clamp(localX, bounds.Left, bounds.Right - 1);
        var clampedY = Math.Clamp(localY, bounds.Top, bounds.Bottom - 1);

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
        SimpleLogger.Log("Control", $">>> StartRemoteControl CALLED for {peer.Name}");

        _activePeer = peer;
        _isControllingRemote = true;
        _hasMovedIntoRemote = false;

        // Initialize cursor position on remote screen in pixel coordinates (like Deskflow)
        int remoteW = peer.ScreenWidth;
        int remoteH = peer.ScreenHeight;

        switch (peer.Position)
        {
            case ScreenPosition.Right:
                // Entering from left edge of remote screen
                _remoteX = 0;
                _remoteY = (int)(edge.NormalizedPosition * remoteH);
                break;
            case ScreenPosition.Left:
                // Entering from right edge of remote screen
                _remoteX = remoteW - 1;
                _remoteY = (int)(edge.NormalizedPosition * remoteH);
                break;
            case ScreenPosition.Bottom:
                // Entering from top edge of remote screen
                _remoteX = (int)(edge.NormalizedPosition * remoteW);
                _remoteY = 0;
                break;
            case ScreenPosition.Top:
                // Entering from bottom edge of remote screen
                _remoteX = (int)(edge.NormalizedPosition * remoteW);
                _remoteY = remoteH - 1;
                break;
        }

        SimpleLogger.Log("Control", $"StartRemoteControl: peer={peer.Name}, remotePos=({_remoteX},{_remoteY})");

        // Set capture position to CENTER of the primary screen (like Deskflow)
        var bounds = _screenInfo.PrimaryBounds;
        int captureX = bounds.Left + bounds.Width / 2;
        int captureY = bounds.Top + bounds.Height / 2;

        // Hide cursor while controlling remote
        InputSimulator.HideSystemCursor();

        // Move cursor to capture position and set it as the warp-back point
        _cursorManager.Capture(captureX, captureY);
        InputSimulator.MoveTo(captureX, captureY);

        // Initialize last seen position for delta tracking (after the warp)
        _lastSeenX = captureX;
        _lastSeenY = captureY;

        // Notify the peer that cursor is entering with initial absolute position
        var enterMsg = new CursorEnterMessage
        {
            EntryX = (float)_remoteX / remoteW,
            EntryY = (float)_remoteY / remoteH,
            EntryEdge = CursorManager.GetOppositeEdge(peer.Position)
        };

        SendToActivePeer(enterMsg);
        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndRemoteControl()
    {
        if (!_isControllingRemote)
            return;

        // Restore cursor
        InputSimulator.RestoreSystemCursor();

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
    /// Checks if we've moved far enough into the remote screen (pixel-based).
    /// </summary>
    private bool HasMovedIntoRemotePixels(ScreenPosition peerPosition, int x, int y, int screenW, int screenH)
    {
        // 5% of screen size threshold
        int thresholdX = screenW / 20;
        int thresholdY = screenH / 20;

        return peerPosition switch
        {
            ScreenPosition.Right => x >= thresholdX,
            ScreenPosition.Left => x <= screenW - thresholdX,
            ScreenPosition.Bottom => y >= thresholdY,
            ScreenPosition.Top => y <= screenH - thresholdY,
            _ => false
        };
    }

    /// <summary>
    /// Checks if cursor has hit the return edge (pixel-based).
    /// </summary>
    private ScreenPosition? GetReturnEdgePixels(ScreenPosition peerPosition, int x, int y, int screenW, int screenH)
    {
        return peerPosition switch
        {
            ScreenPosition.Right => x < 0 ? ScreenPosition.Left : null,
            ScreenPosition.Left => x >= screenW ? ScreenPosition.Right : null,
            ScreenPosition.Bottom => y < 0 ? ScreenPosition.Top : null,
            ScreenPosition.Top => y >= screenH ? ScreenPosition.Bottom : null,
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
public class MouseDebugEventArgs : EventArgs
{
    public bool IsControlling { get; set; }
    public string? PeerName { get; set; }
    public int LocalX { get; set; }
    public int LocalY { get; set; }
    public int PrevX { get; set; }
    public int PrevY { get; set; }
    public float VirtualX { get; set; }
    public float VirtualY { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public bool IsIgnored { get; set; }

    // Extra debug info
    public int RemoteX { get; set; }
    public int RemoteY { get; set; }
    public int PeerScreenWidth { get; set; }
    public int PeerScreenHeight { get; set; }
    public int CaptureX { get; set; }
    public int CaptureY { get; set; }
    public string? PeerPosition { get; set; }
}

