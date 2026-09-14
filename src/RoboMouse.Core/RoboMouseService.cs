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
///
/// Control model: the machine whose physical mouse is in use (the controller) reads raw
/// hardware motion and forwards it as relative deltas. The controlled machine injects those
/// deltas through its own input pipeline, so its own pointer settings apply and its cursor
/// position is always the real one. The controlled machine therefore owns edge detection:
/// when its cursor is pushed through the edge it entered from, it hands control back.
/// </summary>
public sealed class RoboMouseService : IDisposable
{
    /// <summary>Raw counts of motion into the entry edge required before control returns.</summary>
    private const int ReturnOvershootCounts = 12;

    /// <summary>After control returns, ignore edge hits for this long so the placed cursor does not re-enter.</summary>
    private const int ReturnCooldownMs = 300;

    private readonly AppSettings _settings;
    private readonly ScreenInfo _screenInfo;
    private readonly CursorManager _cursorManager;
    private readonly MouseHook _mouseHook;
    private readonly KeyboardHook _keyboardHook;
    private readonly RawMouseInput _rawMouse;
    private readonly ClipboardManager _clipboardManager;
    private readonly PeerDiscovery _discovery;
    private readonly ConnectionListener _listener;

    private readonly Dictionary<string, PeerConnection> _connections = new();
    private readonly object _connectionLock = new();

    // Controller state (this machine's mouse drives a remote screen)
    private PeerConfig? _activePeer;
    private PeerConnection? _activeConnection;
    private volatile bool _isControllingRemote;
    private long _returnCooldownUntil;

    // Controlled state (a remote machine drives this screen)
    private volatile bool _isControlledByRemote;
    private PeerConnection? _controllerConnection;
    private ScreenPosition _entryEdge;
    private int _edgeOvershoot;
    private readonly HashSet<MouseEventType> _heldButtons = new();
    private readonly Dictionary<Keys, (uint ScanCode, bool Extended)> _heldKeys = new();

    private bool _enabled;
    private bool _disposed;

    /// <summary>Whether the service is enabled.</summary>
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

    /// <summary>Whether we are currently controlling a remote machine.</summary>
    public bool IsControllingRemote => _isControllingRemote;

    /// <summary>Whether we are currently being controlled by a remote machine.</summary>
    public bool IsControlledByRemote => _isControlledByRemote;

    /// <summary>The currently active peer configuration.</summary>
    public PeerConfig? ActivePeer => _activePeer;

    /// <summary>Connected peers.</summary>
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

    /// <summary>Discovered peers on the network.</summary>
    public IReadOnlyCollection<DiscoveredPeer> DiscoveredPeers => _discovery.Peers;

    public event EventHandler<PeerConnection>? PeerConnected;
    public event EventHandler<string>? PeerDisconnected;
    public event EventHandler<DiscoveredPeer>? PeerDiscovered;
    public event EventHandler? ControlStateChanged;
    public event EventHandler<Exception>? Error;

    /// <summary>Raised for each forwarded motion sample while controlling (for the debug panel).</summary>
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

        _rawMouse = new RawMouseInput();
        _rawMouse.Motion += OnRawMouseMotion;

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

    /// <summary>Starts the service.</summary>
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

    /// <summary>Stops the service.</summary>
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

    #region Connection management

    /// <summary>Connects to a peer.</summary>
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

        peerConfig.ScreenWidth = connection.PeerScreenWidth;
        peerConfig.ScreenHeight = connection.PeerScreenHeight;
        peerConfig.Id = connection.PeerId;

        AddConnection(connection);
    }

    /// <summary>Connects to a peer by IP address. Creates and saves the peer config.</summary>
    public async Task<PeerConfig> ConnectToAddressAsync(string address, int port, ScreenPosition position, CancellationToken ct = default)
    {
        var peerConfig = new PeerConfig
        {
            Address = address,
            Port = port,
            Position = position,
            Name = address
        };

        await ConnectToPeerAsync(peerConfig, ct);

        lock (_connectionLock)
        {
            if (_connections.TryGetValue(peerConfig.Id, out var conn))
            {
                peerConfig.Name = conn.PeerName;
            }
        }

        var existing = _settings.Peers.FirstOrDefault(p => p.Id == peerConfig.Id);
        if (existing == null)
        {
            _settings.Peers.Add(peerConfig);
        }
        else
        {
            existing.Address = peerConfig.Address;
            existing.Port = peerConfig.Port;
            existing.Position = peerConfig.Position;
            existing.Name = peerConfig.Name;
        }

        return peerConfig;
    }

    /// <summary>Connects to all configured peers that have addresses.</summary>
    public async Task ConnectToConfiguredPeersAsync(CancellationToken ct = default)
    {
        var peersToConnect = _settings.Peers.Where(p => !string.IsNullOrEmpty(p.Address)).ToList();

        foreach (var peer in peersToConnect)
        {
            try
            {
                lock (_connectionLock)
                {
                    if (_connections.ContainsKey(peer.Id))
                        continue;
                }

                await ConnectToPeerAsync(peer, ct);
            }
            catch (Exception ex)
            {
                SimpleLogger.Log("Connect", $"Failed to connect to {peer.Name} ({peer.Address}): {ex.Message}");
            }
        }
    }

    /// <summary>Connects to a discovered peer.</summary>
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

    /// <summary>Disconnects from a peer.</summary>
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

        if (connection == null)
            return;

        connection.Dispose();

        if (_activeConnection == connection)
        {
            EndRemoteControl(notifyPeer: false);
        }

        if (_controllerConnection == connection)
        {
            EndBeingControlled(notifyPeer: false);
        }

        PeerDisconnected?.Invoke(this, peerId);
    }

    private void OnIncomingConnection(object? sender, PeerConnection connection)
    {
        AddConnection(connection);
    }

    private void OnPeerDiscovered(object? sender, DiscoveredPeer peer)
    {
        PeerDiscovered?.Invoke(this, peer);
    }

    private void OnPeerLost(object? sender, DiscoveredPeer peer)
    {
    }

    private PeerConfig? GetPeerAtEdge(ScreenPosition edge)
    {
        var peer = _settings.Peers.FirstOrDefault(p => p.Position == edge);
        if (peer == null)
            return null;

        lock (_connectionLock)
        {
            return _connections.ContainsKey(peer.Id) ? peer : null;
        }
    }

    #endregion

    private void OnEnabledChanged()
    {
        if (_enabled)
        {
            _mouseHook.Install();
            _keyboardHook.Install();
        }
        else
        {
            EndRemoteControl(notifyPeer: true);
            EndBeingControlled(notifyPeer: true);
            _mouseHook.Uninstall();
            _keyboardHook.Uninstall();
        }
    }

    #region Local input (controller side)

    private void OnRawMouseMotion(int dx, int dy)
    {
        if (!_isControllingRemote)
            return;

        var connection = _activeConnection;
        if (connection == null)
            return;

        connection.Post(MouseMessage.Motion(dx, dy));

        var debug = MouseDebugUpdate;
        if (debug != null)
        {
            debug(this, new MouseDebugEventArgs
            {
                IsControlling = true,
                PeerName = _activePeer?.Name,
                PeerPosition = _activePeer?.Position.ToString(),
                DeltaX = dx,
                DeltaY = dy,
                RoundTripMs = connection.RoundTripMs
            });
        }
    }

    private void OnMouseEvent(object? sender, InputMouseEventArgs e)
    {
        if (!_enabled)
            return;

        // Injected events (our own SendInput/SetCursorPos, or a remote controller's) are never ours to act on.
        if (e.IsInjected)
            return;

        // While being controlled, let the local mouse behave normally.
        if (_isControlledByRemote)
            return;

        if (_isControllingRemote)
        {
            // Freeze the local cursor: swallow everything. Motion arrives separately via raw input.
            e.Handled = true;

            if (e.EventType != MouseEventType.Move)
            {
                _activeConnection?.Post(new MouseMessage
                {
                    EventType = e.EventType,
                    WheelDelta = e.WheelDelta
                });
            }
            return;
        }

        if (e.EventType != MouseEventType.Move)
            return;

        if (Environment.TickCount64 < Interlocked.Read(ref _returnCooldownUntil))
            return;

        var edge = _screenInfo.GetEdgeAt(e.X, e.Y, _settings.EdgeThreshold);
        if (edge == null)
            return;

        var targetPeer = GetPeerAtEdge(edge.Edge);
        if (targetPeer == null)
            return;

        StartRemoteControl(targetPeer, edge);
        e.Handled = true;
    }

    private void OnKeyboardEvent(object? sender, KeyboardEventArgs e)
    {
        if (!_enabled || e.IsInjected || _isControlledByRemote)
            return;

        if (_isControllingRemote)
        {
            e.Handled = true;
            _activeConnection?.Post(KeyboardMessage.FromEvent(e));
        }
    }

    private void StartRemoteControl(PeerConfig peer, EdgeInfo edge)
    {
        PeerConnection? connection;
        lock (_connectionLock)
        {
            _connections.TryGetValue(peer.Id, out connection);
        }
        if (connection == null)
            return;

        SimpleLogger.Log("Control", $"Entering {peer.Name} via {edge.Edge} edge at {edge.NormalizedPosition:F3}");

        _activePeer = peer;
        _activeConnection = connection;
        _isControllingRemote = true;

        try
        {
            _rawMouse.Start();
        }
        catch (Exception ex)
        {
            _isControllingRemote = false;
            _activePeer = null;
            _activeConnection = null;
            Error?.Invoke(this, ex);
            return;
        }

        InputSimulator.HideSystemCursor();

        // Park the (now hidden and frozen) cursor away from the edge so nothing local reacts to it.
        var bounds = _screenInfo.PrimaryBounds;
        InputSimulator.MoveTo(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

        var entryEdge = CursorManager.GetOppositeEdge(peer.Position);
        var enterMsg = new CursorEnterMessage
        {
            EntryEdge = entryEdge,
            EntryX = entryEdge is ScreenPosition.Left or ScreenPosition.Right ? 0f : edge.NormalizedPosition,
            EntryY = entryEdge is ScreenPosition.Left or ScreenPosition.Right ? edge.NormalizedPosition : 0f
        };
        connection.Post(enterMsg);

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops controlling the remote. When <paramref name="notifyPeer"/> is true the remote is told to
    /// release; when false the remote initiated the hand-back (or is gone) and already knows.
    /// </summary>
    private void EndRemoteControl(bool notifyPeer)
    {
        if (!_isControllingRemote)
            return;

        var connection = _activeConnection;
        var peer = _activePeer;

        _isControllingRemote = false;
        _activeConnection = null;
        _activePeer = null;

        _rawMouse.Stop();
        InputSimulator.RestoreSystemCursor();

        if (notifyPeer && connection != null && peer != null)
        {
            connection.Post(new CursorLeaveMessage
            {
                ExitEdge = CursorManager.GetOppositeEdge(peer.Position),
                ExitX = 0.5f,
                ExitY = 0.5f
            });
        }

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The controlled machine pushed the cursor back through its entry edge: place our cursor on the
    /// matching local edge and resume local control.
    /// </summary>
    private void HandleReturnFromRemote(CursorLeaveMessage msg)
    {
        var peer = _activePeer;
        if (peer == null)
            return;

        var normalized = msg.ExitEdge is ScreenPosition.Left or ScreenPosition.Right ? msg.ExitY : msg.ExitX;

        SimpleLogger.Log("Control", $"Returned from {peer.Name} at {normalized:F3}");

        Interlocked.Exchange(ref _returnCooldownUntil, Environment.TickCount64 + ReturnCooldownMs);
        EndRemoteControl(notifyPeer: false);

        // Land one pixel inside the edge so only a deliberate push back toward it re-enters.
        var (x, y) = _cursorManager.GetEdgePoint(peer.Position, normalized);
        var (nudgeX, nudgeY) = peer.Position switch
        {
            ScreenPosition.Left => (1, 0),
            ScreenPosition.Right => (-1, 0),
            ScreenPosition.Top => (0, 1),
            ScreenPosition.Bottom => (0, -1),
            _ => (0, 0)
        };
        InputSimulator.MoveTo(x + nudgeX, y + nudgeY);
    }

    #endregion

    #region Remote input (controlled side)

    private void OnMessageReceived(object? sender, ProtocolMessage message)
    {
        if (sender is not PeerConnection connection)
            return;

        try
        {
            switch (message)
            {
                case MouseMessage mouseMsg:
                    HandleRemoteMouseInput(mouseMsg, connection);
                    break;

                case KeyboardMessage keyMsg:
                    HandleRemoteKeyboardInput(keyMsg, connection);
                    break;

                case CursorEnterMessage enterMsg:
                    HandleCursorEnter(enterMsg, connection);
                    break;

                case CursorLeaveMessage leaveMsg:
                    if (_isControllingRemote && connection == _activeConnection)
                    {
                        HandleReturnFromRemote(leaveMsg);
                    }
                    else if (_isControlledByRemote && connection == _controllerConnection)
                    {
                        EndBeingControlled(notifyPeer: false);
                    }
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

    private void HandleCursorEnter(CursorEnterMessage msg, PeerConnection connection)
    {
        SimpleLogger.Log("Control", $"Controlled by {connection.PeerName} via {msg.EntryEdge} edge");

        _controllerConnection = connection;
        _entryEdge = msg.EntryEdge;
        _edgeOvershoot = 0;
        _isControlledByRemote = true;

        var normalized = msg.EntryEdge is ScreenPosition.Left or ScreenPosition.Right ? msg.EntryY : msg.EntryX;
        _cursorManager.PlaceAtEdge(msg.EntryEdge, normalized);

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRemoteMouseInput(MouseMessage msg, PeerConnection connection)
    {
        if (!_isControlledByRemote || connection != _controllerConnection)
            return;

        if (msg.IsMotion)
        {
            InputSimulator.MoveRelative(msg.DeltaX, msg.DeltaY);
            CheckForReturnEdge(msg.DeltaX, msg.DeltaY);
            return;
        }

        switch (msg.EventType)
        {
            case MouseEventType.LeftDown or MouseEventType.RightDown or MouseEventType.MiddleDown
                or MouseEventType.XButton1Down or MouseEventType.XButton2Down:
                _heldButtons.Add(msg.EventType);
                break;
            case MouseEventType.LeftUp:
                _heldButtons.Remove(MouseEventType.LeftDown);
                break;
            case MouseEventType.RightUp:
                _heldButtons.Remove(MouseEventType.RightDown);
                break;
            case MouseEventType.MiddleUp:
                _heldButtons.Remove(MouseEventType.MiddleDown);
                break;
            case MouseEventType.XButton1Up:
                _heldButtons.Remove(MouseEventType.XButton1Down);
                break;
            case MouseEventType.XButton2Up:
                _heldButtons.Remove(MouseEventType.XButton2Down);
                break;
        }

        InputSimulator.SimulateMouseEvent(msg.EventType, wheelDelta: msg.WheelDelta);
    }

    /// <summary>
    /// Hands control back once the cursor is pinned against the entry edge and the controller keeps
    /// pushing into it. Motion away from the edge resets the count so leaning on it briefly is harmless.
    /// </summary>
    private void CheckForReturnEdge(int dx, int dy)
    {
        var (x, y) = InputSimulator.GetCursorPosition();
        var bounds = _screenInfo.VirtualBounds;

        var (pinned, push) = _entryEdge switch
        {
            ScreenPosition.Left => (x <= bounds.Left, -dx),
            ScreenPosition.Right => (x >= bounds.Right - 1, dx),
            ScreenPosition.Top => (y <= bounds.Top, -dy),
            ScreenPosition.Bottom => (y >= bounds.Bottom - 1, dy),
            _ => (false, 0)
        };

        if (!pinned || push <= 0)
        {
            _edgeOvershoot = 0;
            return;
        }

        _edgeOvershoot += push;
        if (_edgeOvershoot < ReturnOvershootCounts)
            return;

        var normalized = _cursorManager.GetNormalizedPositionOnEdge(_entryEdge, x, y);
        var leave = new CursorLeaveMessage
        {
            ExitEdge = _entryEdge,
            ExitX = _entryEdge is ScreenPosition.Left or ScreenPosition.Right ? 0f : normalized,
            ExitY = _entryEdge is ScreenPosition.Left or ScreenPosition.Right ? normalized : 0f
        };

        var connection = _controllerConnection;
        EndBeingControlled(notifyPeer: false);
        connection?.Post(leave);
    }

    private void HandleRemoteKeyboardInput(KeyboardMessage msg, PeerConnection connection)
    {
        if (!_isControlledByRemote || connection != _controllerConnection)
            return;

        if (msg.EventType is KeyboardEventType.KeyDown or KeyboardEventType.SysKeyDown)
        {
            _heldKeys[msg.KeyCode] = (msg.ScanCode, msg.IsExtendedKey);
        }
        else
        {
            _heldKeys.Remove(msg.KeyCode);
        }

        InputSimulator.SimulateKeyboardEvent(msg.KeyCode, msg.ScanCode, msg.EventType, msg.IsExtendedKey);
    }

    /// <summary>
    /// Stops being controlled. Releases any keys or buttons the controller left held so nothing sticks.
    /// </summary>
    private void EndBeingControlled(bool notifyPeer)
    {
        if (!_isControlledByRemote)
            return;

        var connection = _controllerConnection;
        _isControlledByRemote = false;
        _controllerConnection = null;
        _edgeOvershoot = 0;

        ReleaseHeldInput();

        if (notifyPeer && connection != null)
        {
            connection.Post(new CursorLeaveMessage
            {
                ExitEdge = _entryEdge,
                ExitX = 0.5f,
                ExitY = 0.5f
            });
        }

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseHeldInput()
    {
        foreach (var (key, (scan, extended)) in _heldKeys)
        {
            InputSimulator.SimulateKeyboardEvent(key, scan, KeyboardEventType.KeyUp, extended);
        }
        _heldKeys.Clear();

        foreach (var button in _heldButtons)
        {
            var up = button switch
            {
                MouseEventType.LeftDown => MouseEventType.LeftUp,
                MouseEventType.RightDown => MouseEventType.RightUp,
                MouseEventType.MiddleDown => MouseEventType.MiddleUp,
                MouseEventType.XButton1Down => MouseEventType.XButton1Up,
                MouseEventType.XButton2Down => MouseEventType.XButton2Up,
                _ => (MouseEventType?)null
            };
            if (up != null)
                InputSimulator.SimulateMouseEvent(up.Value);
        }
        _heldButtons.Clear();
    }

    #endregion

    #region Clipboard

    private void OnClipboardChanged(object? sender, ClipboardMessage message)
    {
        if (!_enabled || !_settings.Clipboard.Enabled)
            return;

        lock (_connectionLock)
        {
            foreach (var connection in _connections.Values)
            {
                connection.Post(message);
            }
        }
    }

    private void HandleRemoteClipboard(ClipboardMessage msg)
    {
        if (!_settings.Clipboard.Enabled)
            return;

        _clipboardManager.SetClipboard(msg);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        _rawMouse.Dispose();
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _clipboardManager.Dispose();
        _discovery.Dispose();
        _listener.Dispose();
    }
}

/// <summary>
/// Debug information about forwarded motion while controlling a remote machine.
/// </summary>
public class MouseDebugEventArgs : EventArgs
{
    public bool IsControlling { get; set; }
    public string? PeerName { get; set; }
    public string? PeerPosition { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int RoundTripMs { get; set; }
}
