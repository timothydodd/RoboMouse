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
    private readonly IInputInjector _injector;
    private readonly ClipboardManager _clipboardManager;
    private readonly PeerDiscovery _discovery;
    private readonly ConnectionListener _listener;

    private readonly Dictionary<string, PeerConnection> _connections = new();
    private readonly object _connectionLock = new();
    private readonly HashSet<string> _connectsInFlight = new();
    private readonly HashSet<string> _reportedReconnectFailures = new();
    private System.Threading.Timer? _reconnectTimer;
    private const int ReconnectIntervalMs = 5000;

    // Controller state (this machine's mouse drives a remote screen)
    private PeerConfig? _activePeer;
    private PeerConnection? _activeConnection;
    private volatile bool _isControllingRemote;
    private long _returnCooldownUntil;

    // Controlled state (a remote machine drives this screen)
    private volatile bool _isControlledByRemote;
    private PeerConnection? _controllerConnection;
    private ScreenPosition _entryEdge;
    private bool _controllerWrapsAround;
    private int _edgeOvershoot;
    private InputBlockReason _localBlockReason;
    private System.Threading.Timer? _desktopPollTimer;
    private volatile InputBlockReason _remoteBlockReason;
    private readonly HashSet<MouseEventType> _heldButtons = new();
    private readonly Dictionary<Keys, (uint ScanCode, bool Extended)> _heldKeys = new();

    private bool _enabled;
    private bool _disposed;

    private string? _pairingKeySource;
    private byte[]? _pairingKey;

    // File sharing. Offers are kept by id on both sides: what we offer (serving side) and what we hold
    // from peers (paste side). A superseded or revoked offer stays servable for a grace period after its
    // last read, so a paste that is still copying is not cut off when the clipboard moves on. Offers are
    // relayed to every other peer, so a machine in the middle of a chain proxies reads between its neighbours.
    private readonly object _fileLock = new();
    private readonly List<PeerConnection> _transferServers = new();
    private readonly Dictionary<string, LocalOffer> _localOffers = new();
    private readonly Dictionary<string, RemoteOffer> _remoteOffers = new();
    private string? _currentLocalOfferId;
    private string? _currentRemoteOfferId;
    private System.Threading.Timer? _offerSweepTimer;
    private string? _lastClipboardHash;

    /// <summary>How long a retired offer stays readable after its last request.</summary>
    private static readonly TimeSpan OfferGrace = TimeSpan.FromSeconds(60);

    private sealed class LocalOffer
    {
        public required FileOfferSource Source { get; init; }
        public bool Retired { get; set; }
        public long LastUsedTicks { get; set; } = Environment.TickCount64;
    }

    private sealed class RemoteOffer
    {
        public required string PeerId { get; init; }
        public required FileTransferClient Client { get; init; }
        public bool Retired { get; set; }
        public long LastUsedTicks { get; set; } = Environment.TickCount64;
    }

    /// <summary>
    /// The key derived from the pairing code. Derivation is deliberately slow, so it is cached until the code changes.
    /// </summary>
    private byte[] GetPairingKey()
    {
        var code = _settings.PairingCode;
        if (_pairingKey == null || _pairingKeySource != code)
        {
            _pairingKey = SecureChannel.DerivePairingKey(code);
            _pairingKeySource = code;
        }
        return _pairingKey;
    }

    /// <summary>
    /// Converter between PNG and Windows DIB clipboard images, supplied by the app (the core has no
    /// image codec). Without one, images still sync as PNG where the source offers it.
    /// </summary>
    public IClipboardImageCodec? ClipboardImageCodec
    {
        get => _clipboardManager.ImageCodec;
        set => _clipboardManager.ImageCodec = value;
    }

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
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Whether we are currently controlling a remote machine.</summary>
    public bool IsControllingRemote => _isControllingRemote;

    /// <summary>Whether we are currently being controlled by a remote machine.</summary>
    public bool IsControlledByRemote => _isControlledByRemote;

    /// <summary>The local edge the remote cursor came in on while <see cref="IsControlledByRemote"/>.</summary>
    public ScreenPosition EntryEdge => _entryEdge;

    /// <summary>
    /// While controlling a remote: why that machine cannot apply our input right now (a UAC prompt, an
    /// elevated window), or <see cref="InputBlockReason.None"/>. Changes raise <see cref="ControlStateChanged"/>.
    /// </summary>
    public InputBlockReason RemoteInputBlockReason => _remoteBlockReason;

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
    public event EventHandler? EnabledChanged;
    public event EventHandler<Exception>? Error;

    /// <summary>Raised for each forwarded motion sample while controlling (for the debug panel).</summary>
    public event EventHandler<MouseDebugEventArgs>? MouseDebugUpdate;

    public RoboMouseService(AppSettings settings, IInputInjector? injector = null)
    {
        _settings = settings;
        _injector = injector ?? new InProcessInjector();
        _screenInfo = new ScreenInfo();
        _cursorManager = new CursorManager(_screenInfo);

        _mouseHook = new MouseHook();
        _mouseHook.MouseEvent += OnMouseEvent;

        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyboardEvent += OnKeyboardEvent;

        _rawMouse = new RawMouseInput();
        _rawMouse.Motion += OnRawMouseMotion;

        _clipboardManager = new ClipboardManager(_settings.Clipboard.MaxSizeBytes)
        {
            ShareFiles = _settings.Clipboard.SyncFiles
        };
        _clipboardManager.ClipboardChanged += OnClipboardChanged;
        _clipboardManager.FilesCopied += OnLocalFilesCopied;
        _clipboardManager.FilesCleared += OnLocalFilesCleared;

        var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();

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
            GetPairingKey,
            _settings.MachineId,
            _settings.MachineName,
            width,
            height);
        _listener.PeerConnected += OnIncomingConnection;

        ApplyHotkeySetting();
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

        // Hooks stay installed while the service runs so the toggle hotkey works even when disabled.
        _mouseHook.Install();
        _keyboardHook.Install();

        _enabled = _settings.Enabled;

        _reconnectTimer = new System.Threading.Timer(_ => _ = ReconnectConfiguredPeersAsync(), null, ReconnectIntervalMs, ReconnectIntervalMs);
        _offerSweepTimer = new System.Threading.Timer(_ => SweepRetiredOffers(), null, OfferGrace, OfferGrace);
    }

    /// <summary>Stops the service.</summary>
    public void Stop()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _offerSweepTimer?.Dispose();
        _offerSweepTimer = null;

        _enabled = false;
        OnEnabledChanged();
        _mouseHook.Uninstall();
        _keyboardHook.Uninstall();

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
        var key = $"{peerConfig.Address}:{peerConfig.Port}";
        lock (_connectionLock)
        {
            if (!_connectsInFlight.Add(key))
                throw new InvalidOperationException($"Already connecting to {key}.");
        }

        try
        {
            var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();

            var connection = await PeerConnection.ConnectAsync(
                peerConfig.Address,
                peerConfig.Port,
                GetPairingKey(),
                _settings.MachineId,
                _settings.MachineName,
                width,
                height,
                _settings.LocalPort,
                ct);

            peerConfig.ScreenWidth = connection.PeerScreenWidth;
            peerConfig.ScreenHeight = connection.PeerScreenHeight;
            peerConfig.Id = connection.PeerId;

            AddConnection(connection);
            lock (_connectionLock)
            {
                _reportedReconnectFailures.Remove(peerConfig.Id);
            }
        }
        finally
        {
            lock (_connectionLock)
            {
                _connectsInFlight.Remove(key);
            }
        }
    }

    /// <summary>
    /// Background reconnect for configured peers that are not connected. Failures are logged once per
    /// outage so the log does not fill up while a machine is switched off.
    /// </summary>
    private async Task ReconnectConfiguredPeersAsync()
    {
        if (_disposed)
            return;

        List<PeerConfig> candidates;
        lock (_connectionLock)
        {
            candidates = _settings.Peers
                .Where(p => p.Enabled
                            && !string.IsNullOrEmpty(p.Address)
                            && !(_connections.TryGetValue(p.Id, out var c) && c.IsConnected)
                            && !_connectsInFlight.Contains($"{p.Address}:{p.Port}"))
                .ToList();
        }

        foreach (var peer in candidates)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await ConnectToPeerAsync(peer, cts.Token);
                SimpleLogger.Log("Connect", $"Reconnected to {peer.Name}");
            }
            catch (Exception ex)
            {
                bool first;
                lock (_connectionLock)
                {
                    first = _reportedReconnectFailures.Add(peer.Id);
                }
                if (first)
                    SimpleLogger.Log("Connect", $"Cannot reach {peer.Name} ({peer.Address}:{peer.Port}); will keep retrying. {ex.GetBaseException().Message}");
            }
        }
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
        var peersToConnect = _settings.Peers.Where(p => p.Enabled && !string.IsNullOrEmpty(p.Address)).ToList();

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
            RemoveConnection(connection);
        }
    }

    private void AddConnection(PeerConnection connection)
    {
        PeerConnection? replaced = null;
        lock (_connectionLock)
        {
            if (_connections.TryGetValue(connection.PeerId, out var existing) && existing.IsConnected)
            {
                // Both machines connected to each other at once. Keep the connection initiated by the
                // machine with the smaller id so both sides make the same choice, and drop the other.
                var keepOutbound = string.CompareOrdinal(_settings.MachineId, connection.PeerId) < 0;
                if (existing.IsOutbound == keepOutbound)
                {
                    SimpleLogger.Log("Conn", $"Dropping duplicate {(connection.IsOutbound ? "outbound" : "inbound")} connection to {connection.PeerName}");
                    connection.Dispose();
                    return;
                }

                replaced = existing;
                _connections.Remove(connection.PeerId);
            }

            _connections[connection.PeerId] = connection;
        }

        if (replaced != null)
        {
            SimpleLogger.Log("Conn", $"Replacing duplicate connection to {connection.PeerName}");
            replaced.MessageReceived -= OnMessageReceived;
            replaced.Dispose();
        }

        connection.MessageReceived += OnMessageReceived;
        connection.Disconnected += (s, e) => RemoveConnection(connection);
        connection.Start();

        if (replaced == null)
            PeerConnected?.Invoke(this, connection);
    }

    private void RemoveConnection(PeerConnection connection)
    {
        lock (_connectionLock)
        {
            // Only remove if this exact connection is still the registered one; a replacement may have taken over.
            if (!_connections.TryGetValue(connection.PeerId, out var current) || !ReferenceEquals(current, connection))
                return;
            _connections.Remove(connection.PeerId);
        }

        connection.Dispose();

        if (_activeConnection == connection)
        {
            EndRemoteControl(notifyPeer: false);
        }

        if (_controllerConnection == connection)
        {
            EndBeingControlled(notifyPeer: false);
        }

        ForgetRemoteOffer(connection.PeerId);

        PeerDisconnected?.Invoke(this, connection.PeerId);
    }

    private void OnIncomingConnection(object? sender, PeerConnection connection)
    {
        switch (connection.Kind)
        {
            case ConnectionKind.Probe:
                // A connection test from another machine. It only needs pings answered (the connection
                // does that itself) until the tester hangs up; never treat it as a peer.
                SimpleLogger.Log("Accept", $"Connection test from {connection.PeerName}");
                connection.Disconnected += (s, e) => connection.Dispose();
                connection.Start();
                return;

            case ConnectionKind.Transfer:
                // A peer is pasting files we offered. Serve requests until it hangs up.
                lock (_fileLock)
                {
                    _transferServers.Add(connection);
                }
                connection.MessageReceived += OnTransferRequest;
                connection.Disconnected += (s, e) =>
                {
                    lock (_fileLock)
                    {
                        _transferServers.Remove(connection);
                    }
                    connection.Dispose();
                };
                connection.Start();
                return;
        }

        // A peer the user has switched off must not be able to take control of this screen either.
        var config = _settings.Peers.FirstOrDefault(p => p.Id == connection.PeerId);
        if (config is { Enabled: false })
        {
            SimpleLogger.Log("Accept", $"Refusing connection from disabled peer {connection.PeerName}");
            connection.Disconnected += (s, e) => connection.Dispose();
            connection.Start();
            _ = connection.DisconnectAsync();
            return;
        }

        AddConnection(connection);
    }

    /// <summary>
    /// Turns a configured peer on or off and saves. Disabling drops any live connection to it;
    /// enabling connects again in the background.
    /// </summary>
    public async Task SetPeerEnabledAsync(PeerConfig peer, bool enabled)
    {
        if (peer.Enabled == enabled)
            return;

        peer.Enabled = enabled;
        _settings.Save();

        if (!enabled)
        {
            // Dropping the connection also ends remote control if we were on that screen.
            await DisconnectFromPeerAsync(peer.Id);
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await ConnectToPeerAsync(peer, cts.Token);
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Connect", $"Cannot reach {peer.Name} after enabling; will keep retrying. {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Whether a live connection to the given peer exists.</summary>
    public bool IsPeerConnected(string peerId)
    {
        lock (_connectionLock)
        {
            return _connections.TryGetValue(peerId, out var c) && c.IsConnected;
        }
    }

    /// <summary>Gets the live connection to a peer, if any.</summary>
    public PeerConnection? GetConnection(string peerId)
    {
        lock (_connectionLock)
        {
            return _connections.TryGetValue(peerId, out var c) && c.IsConnected ? c : null;
        }
    }

    /// <summary>
    /// Tests a configured peer. Uses the live connection when there is one; otherwise probes the address.
    /// </summary>
    public Task<ConnectionTestResult> TestConnectionAsync(PeerConfig peer, CancellationToken ct = default)
    {
        var existing = GetConnection(peer.Id);
        return existing != null
            ? MeasureExistingAsync(existing, ct)
            : TestConnectionAsync(peer.Address, peer.Port, ct);
    }

    /// <summary>
    /// Tests reachability of an address: TCP connect, handshake, one round-trip ping, then hang up.
    /// The remote does not register us as a peer for this.
    /// </summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(string address, int port, CancellationToken ct = default)
    {
        var result = new ConnectionTestResult { Address = address, Port = port };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        PeerConnection? probe = null;
        try
        {
            var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();
            probe = await PeerConnection.ConnectAsync(
                address, port, GetPairingKey(), _settings.MachineId, _settings.MachineName, width, height,
                _settings.LocalPort, ct, ConnectionKind.Probe);
            probe.Start();

            result.ConnectMs = (int)sw.ElapsedMilliseconds;
            result.PeerName = probe.PeerName;
            result.PeerId = probe.PeerId;
            result.PeerScreenWidth = probe.PeerScreenWidth;
            result.PeerScreenHeight = probe.PeerScreenHeight;

            result.RoundTripMs = await MeasureAveragedAsync(probe, ct);
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Error = "Timed out. Is RoboMouse running there, and is the port open in the firewall?";
        }
        catch (PairingException ex)
        {
            result.Error = ex.Message + " Enter the same pairing code on both machines (Settings > Network).";
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            result.Error = ex.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => "Connection refused. RoboMouse is not listening on that port.",
                System.Net.Sockets.SocketError.HostNotFound => "Host name could not be resolved.",
                System.Net.Sockets.SocketError.TimedOut => "No response. Check the address and firewall.",
                _ => ex.Message
            };
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            if (probe != null)
            {
                try { await probe.DisconnectAsync(); } catch { }
            }
        }

        return result;
    }

    private async Task<ConnectionTestResult> MeasureExistingAsync(PeerConnection connection, CancellationToken ct)
    {
        var result = new ConnectionTestResult
        {
            Address = connection.RemoteEndPoint?.Address.ToString() ?? string.Empty,
            Port = connection.RemoteEndPoint?.Port ?? 0,
            PeerName = connection.PeerName,
            PeerId = connection.PeerId,
            PeerScreenWidth = connection.PeerScreenWidth,
            PeerScreenHeight = connection.PeerScreenHeight,
            UsedExistingConnection = true
        };

        try
        {
            result.RoundTripMs = await MeasureAveragedAsync(connection, ct);
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Error = "The peer stopped answering pings. The connection may be dead.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private static async Task<int> MeasureAveragedAsync(PeerConnection connection, CancellationToken ct)
    {
        const int samples = 5;
        var total = 0;
        for (var i = 0; i < samples; i++)
        {
            total += await connection.MeasureRoundTripAsync(ct);
        }
        return total / samples;
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
        var peer = _settings.Peers.FirstOrDefault(p => p.Position == edge && p.Enabled);
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
        if (!_enabled)
        {
            EndRemoteControl(notifyPeer: true);
            EndBeingControlled(notifyPeer: true);
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
        if (targetPeer == null && _settings.WrapAround)
        {
            // No peer on this edge: wrap to the peer on the opposite edge, arriving from its far side.
            targetPeer = GetPeerAtEdge(CursorManager.GetOppositeEdge(edge.Edge));
        }
        if (targetPeer == null)
            return;

        StartRemoteControl(targetPeer, edge);
        e.Handled = true;
    }

    private readonly ModifierState _modifiers = new();

    private void OnKeyboardEvent(object? sender, KeyboardEventArgs e)
    {
        if (e.IsInjected)
            return;

        var isDown = e.EventType is KeyboardEventType.KeyDown or KeyboardEventType.SysKeyDown;
        _modifiers.Update(e.KeyCode, isDown);

        // Escape hatch and on/off switch. Works whether or not sharing is enabled, and while controlling a
        // remote it takes priority over forwarding so a hung peer can never trap the keyboard.
        if (isDown && _hotkey?.Matches(e.KeyCode, _modifiers) == true)
        {
            e.Handled = true;
            OnHotkeyPressed();
            return;
        }

        if (!_enabled || _isControlledByRemote)
            return;

        if (_isControllingRemote)
        {
            e.Handled = true;
            _activeConnection?.Post(KeyboardMessage.FromEvent(e));
        }
    }

    private Hotkey? _hotkey;

    /// <summary>Re-reads the toggle hotkey from settings.</summary>
    public void ApplyHotkeySetting()
    {
        _hotkey = Hotkey.Parse(_settings.ToggleHotkey);
    }

    private void OnHotkeyPressed()
    {
        if (_isControllingRemote)
        {
            SimpleLogger.Log("Control", "Hotkey pressed: releasing remote control");
            EndRemoteControl(notifyPeer: true);
            var bounds = _screenInfo.PrimaryBounds;
            InputSimulator.MoveTo(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Interlocked.Exchange(ref _returnCooldownUntil, Environment.TickCount64 + ReturnCooldownMs);
            return;
        }

        Enabled = !Enabled;
        _settings.Enabled = Enabled;
        SimpleLogger.Log("Control", $"Hotkey pressed: sharing {(Enabled ? "enabled" : "disabled")}");
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

        // The cursor appears on the peer's edge opposite the one it left here (its left edge when we
        // left through our right). With wrap-around that is not necessarily the edge facing this screen.
        var entryEdge = CursorManager.GetOppositeEdge(edge.Edge);
        _remoteBlockReason = InputBlockReason.None;
        var enterMsg = new CursorEnterMessage
        {
            EntryEdge = entryEdge,
            EntryX = entryEdge is ScreenPosition.Left or ScreenPosition.Right ? 0f : edge.NormalizedPosition,
            EntryY = entryEdge is ScreenPosition.Left or ScreenPosition.Right ? edge.NormalizedPosition : 0f,
            WrapAround = _settings.WrapAround
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
        _remoteBlockReason = InputBlockReason.None;

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

        // Land one pixel inside the local edge facing the one the cursor left through: the peer's
        // entry edge on a normal return, or the far edge when it wrapped around.
        var localEdge = CursorManager.GetOppositeEdge(msg.ExitEdge);
        var (x, y) = _cursorManager.GetEdgePoint(localEdge, normalized);
        var (nudgeX, nudgeY) = localEdge switch
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

                case InputStatusMessage statusMsg:
                    if (_isControllingRemote && connection == _activeConnection && statusMsg.Reason != _remoteBlockReason)
                    {
                        _remoteBlockReason = statusMsg.Reason;
                        SimpleLogger.Log("Control", $"{connection.PeerName} input status: {statusMsg.Reason}");
                        ControlStateChanged?.Invoke(this, EventArgs.Empty);
                    }
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
                    HandleRemoteClipboard(clipMsg, connection);
                    break;

                case FileOfferMessage offer:
                    HandleRemoteFileOffer(offer, connection);
                    break;

                case FileOfferRevokedMessage revoked:
                    HandleRemoteOfferRevoked(revoked, connection);
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
        _controllerWrapsAround = msg.WrapAround;
        _edgeOvershoot = 0;
        _injectionBlocked = false;
        _localBlockReason = InputBlockReason.None;
        _isControlledByRemote = true;
        _desktopPollTimer?.Dispose();
        _desktopPollTimer = new System.Threading.Timer(_ => ReportInputStatus(), null, 250, 250);

        var normalized = msg.EntryEdge is ScreenPosition.Left or ScreenPosition.Right ? msg.EntryY : msg.EntryX;
        var (entryX, entryY) = _cursorManager.GetEdgePoint(msg.EntryEdge, normalized);
        _injector.MoveTo(entryX, entryY);

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRemoteMouseInput(MouseMessage msg, PeerConnection connection)
    {
        if (!_isControlledByRemote || connection != _controllerConnection)
            return;

        if (msg.IsMotion)
        {
            MoveRemoteCursor(msg.DeltaX, msg.DeltaY);
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

        _injector.SimulateMouseEvent(msg.EventType, msg.WheelDelta);
    }

    private bool _injectionBlocked;

    /// <summary>
    /// Applies motion through SendInput so the local pointer settings apply. When an elevated window is in
    /// the foreground Windows silently drops injected input (UIPI), which would freeze the cursor and leave
    /// the controller unable to reach an edge and get back. Detect that and move the cursor directly
    /// with SetCursorPos, which UIPI does not block. Clicks on the elevated window still cannot work
    /// unless RoboMouse runs as administrator.
    /// </summary>
    private void MoveRemoteCursor(int dx, int dy)
    {
        // SendInput reports a UIPI block as a failed call. It is asynchronous, so the cursor position
        // right after it is not evidence of anything and must not be used to second-guess it.
        if (_injector.MoveRelative(dx, dy))
        {
            if (_injectionBlocked)
            {
                _injectionBlocked = false;
                SimpleLogger.Log("Input", "Injected input accepted again");
                ReportInputStatus();
            }
            return;
        }

        if (!_injectionBlocked)
        {
            _injectionBlocked = true;
            SimpleLogger.Log("Input", "Injected input is being blocked (elevated window in front?); moving cursor directly");
            ReportInputStatus();
        }

        var (x, y) = _injector.GetCursorPosition();
        _injector.MoveTo(x + dx, y + dy);
    }

    /// <summary>
    /// Hands control back once the cursor is pinned against the entry edge and the controller keeps
    /// pushing into it. Motion away from the edge resets the count so leaning on it briefly is harmless.
    /// </summary>
    private void CheckForReturnEdge(int dx, int dy)
    {
        var (x, y) = _injector.GetCursorPosition();
        var bounds = _screenInfo.VirtualBounds;

        // Which edge the cursor is pinned against while being pushed further into it. Normally only the
        // entry edge hands control back; with wrap-around any edge does.
        static (bool pinned, int push) Probe(ScreenPosition edge, int x, int y, int dx, int dy, System.Drawing.Rectangle bounds) => edge switch
        {
            ScreenPosition.Left => (x <= bounds.Left, -dx),
            ScreenPosition.Right => (x >= bounds.Right - 1, dx),
            ScreenPosition.Top => (y <= bounds.Top, -dy),
            ScreenPosition.Bottom => (y >= bounds.Bottom - 1, dy),
            _ => (false, 0)
        };

        var exitEdge = _entryEdge;
        var (pinned, push) = Probe(_entryEdge, x, y, dx, dy, bounds);
        if (_controllerWrapsAround && (!pinned || push <= 0))
        {
            foreach (var edge in new[] { ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom })
            {
                if (edge == _entryEdge)
                    continue;
                var probe = Probe(edge, x, y, dx, dy, bounds);
                if (probe.pinned && probe.push > 0)
                {
                    (exitEdge, pinned, push) = (edge, true, probe.push);
                    break;
                }
            }
        }

        if (!pinned || push <= 0)
        {
            _edgeOvershoot = 0;
            return;
        }

        _edgeOvershoot += push;
        if (_edgeOvershoot < ReturnOvershootCounts)
            return;

        var normalized = _cursorManager.GetNormalizedPositionOnEdge(exitEdge, x, y);
        var leave = new CursorLeaveMessage
        {
            ExitEdge = exitEdge,
            ExitX = exitEdge is ScreenPosition.Left or ScreenPosition.Right ? 0f : normalized,
            ExitY = exitEdge is ScreenPosition.Left or ScreenPosition.Right ? normalized : 0f
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

        _injector.SimulateKeyboardEvent(msg.KeyCode, msg.ScanCode, msg.EventType, msg.IsExtendedKey);
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
        _desktopPollTimer?.Dispose();
        _desktopPollTimer = null;
        _localBlockReason = InputBlockReason.None;

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

    /// <summary>
    /// Tells the controller whether its input is currently landing. Runs on the desktop poll timer while
    /// controlled and whenever SendInput starts or stops failing; only changes are sent.
    /// </summary>
    private void ReportInputStatus()
    {
        var connection = _controllerConnection;
        if (!_isControlledByRemote || connection == null)
            return;

        InputBlockReason reason;
        try
        {
            reason = InputSimulator.IsSecureDesktopActive() ? InputBlockReason.SecureDesktop
                : _injectionBlocked ? InputBlockReason.ElevatedWindow
                : InputBlockReason.None;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Input", $"Desktop check failed: {ex.Message}");
            return;
        }

        if (reason == _localBlockReason)
            return;
        _localBlockReason = reason;
        SimpleLogger.Log("Input", $"Input status for {connection.PeerName}: {reason}");
        connection.Post(new InputStatusMessage { Reason = reason });
    }

    private void ReleaseHeldInput()
    {
        foreach (var (key, (scan, extended)) in _heldKeys)
        {
            _injector.SimulateKeyboardEvent(key, scan, KeyboardEventType.KeyUp, extended);
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
                _injector.SimulateMouseEvent(up.Value);
        }
        _heldButtons.Clear();
    }

    #endregion

    #region Clipboard

    /// <summary>Starts or stops clipboard monitoring to match the current setting.</summary>
    public void ApplyClipboardSetting()
    {
        _clipboardManager.ShareFiles = _settings.Clipboard.SyncFiles;
        if (_settings.Clipboard.Enabled)
            _clipboardManager.Start();
        else
            _clipboardManager.Stop();

        if (!_settings.Clipboard.SyncFiles)
        {
            OnLocalFilesCleared(this, EventArgs.Empty);
            _clipboardManager.ClearVirtualFiles(null);
        }
    }

    private void OnClipboardChanged(object? sender, ClipboardMessage message)
    {
        if (!_enabled || !_settings.Clipboard.Enabled)
            return;

        _lastClipboardHash = HashOf(message);
        PostToPeers(message, except: null);
    }

    private void HandleRemoteClipboard(ClipboardMessage msg, PeerConnection from)
    {
        if (!_settings.Clipboard.Enabled)
            return;

        // Peers only connect to their neighbours, so pass it on to ours. The hash stops the same content
        // going round a ring for ever.
        var hash = HashOf(msg);
        if (hash == _lastClipboardHash)
            return;
        _lastClipboardHash = hash;

        _clipboardManager.SetClipboard(msg);
        PostToPeers(msg, except: from);
    }

    private static string HashOf(ClipboardMessage message)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(message.Data, hash);
        return $"{(int)message.ContentType}:{Convert.ToHexString(hash)}";
    }

    /// <summary>Posts a message to every connected peer except the one it came from.</summary>
    private void PostToPeers(ProtocolMessage message, PeerConnection? except)
    {
        lock (_connectionLock)
        {
            foreach (var connection in _connections.Values)
            {
                if (connection != except)
                    connection.Post(message);
            }
        }
    }

    #endregion

    #region File sharing

    // Serving side: files copied here are announced to every peer; bytes are read on request.

    private void OnLocalFilesCopied(object? sender, FileOfferSource offer)
    {
        if (!_enabled || !_settings.Clipboard.Enabled || !_settings.Clipboard.SyncFiles)
            return;

        var previous = RetireLocalOffer();
        lock (_fileLock)
        {
            _localOffers[offer.OfferId] = new LocalOffer { Source = offer };
            _currentLocalOfferId = offer.OfferId;
        }

        SimpleLogger.Log("Files", $"Offering {offer.Entries.Count} item(s), {offer.TotalSize / 1024.0 / 1024.0:0.#} MB");

        if (previous != null)
            PostToPeers(new FileOfferRevokedMessage { OfferId = previous }, except: null);
        PostToPeers(offer.ToMessage(), except: null);
    }

    private void OnLocalFilesCleared(object? sender, EventArgs e)
    {
        var previous = RetireLocalOffer();
        if (previous != null)
            PostToPeers(new FileOfferRevokedMessage { OfferId = previous }, except: null);
    }

    /// <summary>
    /// Takes the current local offer off the market. It stays readable for the grace period so a paste
    /// already in progress can finish. Returns its id, or null if there was none.
    /// </summary>
    private string? RetireLocalOffer()
    {
        lock (_fileLock)
        {
            var id = _currentLocalOfferId;
            _currentLocalOfferId = null;
            if (id != null && _localOffers.TryGetValue(id, out var offer))
            {
                offer.Retired = true;
                offer.LastUsedTicks = Environment.TickCount64;
            }
            return id;
        }
    }

    private void OnTransferRequest(object? sender, ProtocolMessage message)
    {
        if (sender is not PeerConnection connection || message is not FileRequestMessage request)
            return;

        var length = Math.Clamp(request.Length, 0, FileTransferClient.ChunkSize);
        var reply = new FileChunkMessage
        {
            OfferId = request.OfferId,
            EntryIndex = request.EntryIndex,
            Offset = request.Offset
        };

        FileOfferSource? local = null;
        FileTransferClient? relay = null;
        lock (_fileLock)
        {
            if (_localOffers.TryGetValue(request.OfferId, out var localOffer))
            {
                localOffer.LastUsedTicks = Environment.TickCount64;
                local = localOffer.Source;
            }
            else if (_remoteOffers.TryGetValue(request.OfferId, out var remoteOffer))
            {
                remoteOffer.LastUsedTicks = Environment.TickCount64;
                relay = remoteOffer.Client;
            }
        }

        if (local != null)
        {
            try
            {
                reply.Data = local.Read(request.EntryIndex, request.Offset, length);
            }
            catch (Exception ex)
            {
                reply.Error = ex.Message;
            }
            connection.Post(reply);
            return;
        }

        if (relay == null)
        {
            reply.Error = "Those files are no longer on the clipboard of the other machine.";
            connection.Post(reply);
            return;
        }

        // The offer belongs to one of our other peers: pull the chunk from there and pass it on. This
        // waits on the network, so it must not block the receive thread (which also answers pings).
        _ = Task.Run(() =>
        {
            try
            {
                reply.Data = relay.Fetch(request.OfferId, request.EntryIndex, request.Offset, length);
            }
            catch (Exception ex)
            {
                reply.Error = ex.Message;
            }
            connection.Post(reply);
        });
    }

    // Paste side: an offer from a peer becomes virtual files on our clipboard, fetched on demand.

    private void HandleRemoteFileOffer(FileOfferMessage offer, PeerConnection connection)
    {
        if (!_settings.Clipboard.Enabled || !_settings.Clipboard.SyncFiles || offer.Entries.Count == 0)
            return;

        var address = connection.RemoteEndPoint?.Address;
        var port = connection.PeerListenPort;
        if (address == null || port <= 0)
        {
            SimpleLogger.Log("Files", $"Cannot fetch files from {connection.PeerName}: no address to connect back to");
            return;
        }

        FileTransferClient client;
        lock (_fileLock)
        {
            // Our own offer coming back round a ring, or one we already hold via another peer.
            if (_localOffers.ContainsKey(offer.OfferId) || _remoteOffers.ContainsKey(offer.OfferId))
                return;

            var peerName = connection.PeerName;
            client = new FileTransferClient(peerName, async ct =>
            {
                var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();
                return await PeerConnection.ConnectAsync(
                    address.ToString(), port, GetPairingKey(), _settings.MachineId, _settings.MachineName,
                    width, height, _settings.LocalPort, ct, ConnectionKind.Transfer);
            });

            RetireRemoteOfferLocked(_currentRemoteOfferId);
            _remoteOffers[offer.OfferId] = new RemoteOffer { PeerId = connection.PeerId, Client = client };
            _currentRemoteOfferId = offer.OfferId;
        }

        var offerId = offer.OfferId;
        _clipboardManager.SetVirtualFiles(offer, (index, offset, length) => client.Fetch(offerId, index, offset, length));

        // Pass it on so peers that are not connected to the source can paste it too, through us.
        PostToPeers(offer, except: connection);
    }

    private void HandleRemoteOfferRevoked(FileOfferRevokedMessage revoked, PeerConnection connection)
    {
        bool known;
        lock (_fileLock)
        {
            known = _remoteOffers.TryGetValue(revoked.OfferId, out var offer)
                    && offer.PeerId == connection.PeerId && !offer.Retired;
            if (known)
                RetireRemoteOfferLocked(revoked.OfferId);
        }
        if (!known)
            return;

        _clipboardManager.ClearVirtualFiles(revoked.OfferId);
        PostToPeers(revoked, except: connection);
    }

    /// <summary>Retires every offer held from a peer that has gone away.</summary>
    private void ForgetRemoteOffer(string peerId)
    {
        var retired = new List<string>();
        lock (_fileLock)
        {
            foreach (var (id, offer) in _remoteOffers)
            {
                if (offer.PeerId == peerId && !offer.Retired)
                {
                    RetireRemoteOfferLocked(id);
                    retired.Add(id);
                }
            }
        }

        foreach (var id in retired)
        {
            _clipboardManager.ClearVirtualFiles(id);
            PostToPeers(new FileOfferRevokedMessage { OfferId = id }, except: null);
        }
    }

    /// <summary>Marks a remote offer as no longer current; its transfer client lives on until it has been idle for the grace period.</summary>
    private void RetireRemoteOfferLocked(string? offerId)
    {
        if (offerId == null)
            return;
        if (_remoteOffers.TryGetValue(offerId, out var offer))
        {
            offer.Retired = true;
            offer.LastUsedTicks = Environment.TickCount64;
        }
        if (_currentRemoteOfferId == offerId)
            _currentRemoteOfferId = null;
    }

    /// <summary>Drops retired offers nobody has read from for the grace period.</summary>
    private void SweepRetiredOffers()
    {
        var clients = new List<FileTransferClient>();
        var cutoff = Environment.TickCount64 - (long)OfferGrace.TotalMilliseconds;
        lock (_fileLock)
        {
            foreach (var (id, offer) in _localOffers.ToList())
            {
                if (offer.Retired && offer.LastUsedTicks < cutoff)
                    _localOffers.Remove(id);
            }
            foreach (var (id, offer) in _remoteOffers.ToList())
            {
                if (offer.Retired && offer.LastUsedTicks < cutoff)
                {
                    _remoteOffers.Remove(id);
                    clients.Add(offer.Client);
                }
            }
        }
        foreach (var client in clients)
            client.Dispose();
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        lock (_fileLock)
        {
            foreach (var offer in _remoteOffers.Values)
                offer.Client.Dispose();
            _remoteOffers.Clear();
            _localOffers.Clear();
            foreach (var server in _transferServers)
                server.Dispose();
            _transferServers.Clear();
        }

        _desktopPollTimer?.Dispose();
        _rawMouse.Dispose();
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _clipboardManager.Dispose();
        _discovery.Dispose();
        _listener.Dispose();
    }
}

/// <summary>
/// Outcome of a connection test.
/// </summary>
public class ConnectionTestResult
{
    public bool Success { get; set; }
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? PeerName { get; set; }
    public string? PeerId { get; set; }
    public int PeerScreenWidth { get; set; }
    public int PeerScreenHeight { get; set; }
    /// <summary>Time to establish TCP and complete the handshake. Zero when an existing connection was used.</summary>
    public int ConnectMs { get; set; }
    /// <summary>Average round-trip time over several pings.</summary>
    public int RoundTripMs { get; set; }
    public bool UsedExistingConnection { get; set; }
    public string? Error { get; set; }

    public string Summary
    {
        get
        {
            if (!Success)
                return $"Could not reach {Address}:{Port}.\n\n{Error}";

            var lines = new List<string>
            {
                $"Reached {PeerName} at {Address}:{Port}.",
                string.Empty,
                $"Screen: {PeerScreenWidth}x{PeerScreenHeight}",
                $"Round trip: {RoundTripMs} ms (average of 5 pings)"
            };
            if (UsedExistingConnection)
                lines.Add("Measured over the existing connection.");
            else
                lines.Add($"Connect + handshake: {ConnectMs} ms");
            return string.Join("\n", lines);
        }
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
