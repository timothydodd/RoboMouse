using System.Net;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using RoboMouse.Core.Power;
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
    private readonly DesktopServiceInjector? _serviceInjector;
    private readonly ClipboardManager _clipboardManager;
    private readonly SessionMonitor _sessionMonitor;
    private PeerDiscovery _discovery;
    private ConnectionListener _listener;

    // Work the input hooks must not do themselves (logging, swapping the system cursors, registering
    // raw input): the hook callback has a system timeout, so it posts the work here and returns. The
    // window belongs to the thread that created the service, the same one the hooks run on, and runs
    // the work in order right after the callback returns.
    private readonly MessageWindow _uiQueue;

    private readonly ConnectionRegistry<PeerConnection> _registry = new();
    private readonly object _connectionLock = new();
    private readonly Dictionary<string, Task<PeerConnection>> _connectsInFlight = new();
    private readonly HashSet<string> _reportedReconnectFailures = new();
    private readonly Dictionary<string, PeerConnectFailure> _connectFailures = new();
    private System.Threading.Timer? _reconnectTimer;
    private const int ReconnectIntervalMs = 5000;

    // Machines with the pairing code that are not configured peers, waiting for the user; and the ones
    // the user chose to ignore for this session.
    private readonly Dictionary<string, PendingPeer> _pendingPeers = new();
    private readonly HashSet<string> _ignoredPeers = new();

    // Controller state (this machine's mouse drives a remote screen). The connection itself is the
    // registry's active connection, so it changes under the registry's lock.
    private PeerConfig? _activePeer;
    private volatile bool _isControllingRemote;
    private long _returnCooldownUntil;

    // Where the cursor left this screen, so it can be put back there if the peer drops mid-session.
    private ScreenPosition _exitEdge;
    private float _exitPosition;

    // Whether the system cursors are currently swapped for blank ones (only touched on the UI queue).
    private bool _cursorHidden;

    // Raw input stops arriving when an elevated window is in the foreground (UIPI does not deliver it to
    // a normal-user process), but the low-level hook still sees every move. While controlling, the
    // cursor is parked and every move is blocked, so a hooked move's position minus the parked point
    // is the (accelerated) delta the hardware produced. That is the fallback motion source.
    private const int RawInputSilenceMs = 250;
    private const int HookMovesBeforeFallback = 3;
    private int _parkedX;
    private int _parkedY;
    private long _lastRawMotionTick;
    private int _hookMovesWithoutRaw;
    private bool _hookMotionFallback;

    // Physical keys and buttons held on this machine (from the hooks), and what was forwarded to the
    // peer as held while controlling it.
    private readonly HeldInput _physical = new();
    private readonly HeldInput _forwarded = new();

    // Controlled state (a remote machine drives this screen)
    private readonly ControlledSession _controlled;
    private int _edgeOvershoot;
    private InputBlockReason _localBlockReason;
    private System.Threading.Timer? _desktopPollTimer;
    private volatile InputBlockReason _remoteBlockReason;

    // Power. Every machine announces its display/sleep state; with FollowHostPower on, this one mirrors
    // the peer that last controlled it. Controlling that peer back ends it, so two machines never hold
    // each other awake.
    private readonly PowerMonitor _powerMonitor;
    private readonly PowerFollower _powerFollower;
    private readonly object _powerLock = new();
    private readonly Dictionary<string, PeerPowerState> _peerPowerStates = new();
    private string? _powerHostId;

    private bool _enabled;
    private bool _disposed;

    private string? _pairingKeySource;
    private byte[]? _pairingKey;

    // The pairing code the live connections were authenticated with.
    private string _connectedPairingCode;

    // File sharing. Offers are kept by id on both sides: what we offer (serving side) and what we hold
    // from peers (paste side). A superseded or revoked offer stays servable for a grace period after its
    // last read, so a paste that is still copying is not cut off when the clipboard moves on. Offers are
    // relayed to every other peer, so a machine in the middle of a chain proxies reads between its neighbours.
    private readonly object _fileLock = new();
    private readonly List<PeerConnection> _transferServers = new();
    private readonly Dictionary<PeerConnection, SerialWorkQueue> _transferQueues = new();
    private readonly Dictionary<string, LocalOffer> _localOffers = new();
    private readonly Dictionary<string, RemoteOffer> _remoteOffers = new();
    private string? _currentLocalOfferId;
    private string? _currentRemoteOfferId;
    private System.Threading.Timer? _offerSweepTimer;
    private string? _lastClipboardHash;

    /// <summary>How long a retired offer stays readable after its last request.</summary>
    private static readonly TimeSpan OfferGrace = TimeSpan.FromSeconds(60);

    /// <summary>Longest a retired offer stays readable however busy it is, so a stuck paste cannot pin it for ever.</summary>
    private static readonly TimeSpan OfferMaxRetiredLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How often retired offers are checked.</summary>
    private static readonly TimeSpan OfferSweepInterval = TimeSpan.FromSeconds(15);

    private sealed class LocalOffer
    {
        public required FileOfferSource Source { get; init; }
        public bool Retired { get; set; }
        public long RetiredTicks { get; set; }
        public long LastUsedTicks { get; set; } = Environment.TickCount64;
    }

    private sealed class RemoteOffer
    {
        public required string PeerId { get; init; }
        public required FileTransferClient Client { get; init; }
        public bool Retired { get; set; }
        public long RetiredTicks { get; set; }
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
    public bool IsControlledByRemote => _controlled.IsActive;

    /// <summary>The local edge the remote cursor came in on while <see cref="IsControlledByRemote"/>.</summary>
    public ScreenPosition EntryEdge => _controlled.EntryEdge;

    /// <summary>
    /// While controlling a remote: why that machine cannot apply our input right now (a UAC prompt, an
    /// elevated window), or <see cref="InputBlockReason.None"/>. Changes raise <see cref="ControlStateChanged"/>.
    /// </summary>
    public InputBlockReason RemoteInputBlockReason => _remoteBlockReason;

    /// <summary>The currently active peer configuration.</summary>
    public PeerConfig? ActivePeer => _activePeer;

    /// <summary>Connected peers.</summary>
    public IReadOnlyCollection<PeerConnection> ConnectedPeers => _registry.Snapshot();

    /// <summary>Discovered peers on the network.</summary>
    public IReadOnlyCollection<DiscoveredPeer> DiscoveredPeers => _discovery.Peers;

    /// <summary>
    /// Set when the TCP port peers connect to could not be opened (another program uses it): nothing
    /// can connect to this machine, though it can still connect out. Null when listening normally.
    /// Changes raise <see cref="NetworkStatusChanged"/>.
    /// </summary>
    public NetworkStartError? ListenerError { get; private set; }

    /// <summary>Set when the discovery port could not be opened: other machines are not found automatically.</summary>
    public NetworkStartError? DiscoveryError { get; private set; }

    public event EventHandler<PeerConnection>? PeerConnected;
    public event EventHandler<string>? PeerDisconnected;
    public event EventHandler<DiscoveredPeer>? PeerDiscovered;
    public event EventHandler? ControlStateChanged;
    public event EventHandler? EnabledChanged;
    public event EventHandler<Exception>? Error;

    /// <summary>Raised (on a background thread) when <see cref="ListenerError"/> or <see cref="DiscoveryError"/> changes.</summary>
    public event EventHandler? NetworkStatusChanged;

    /// <summary>
    /// Raised (on a background thread) with a peer config id when its last connect failure is set or
    /// cleared; read it with <see cref="GetLastConnectFailure"/>.
    /// </summary>
    public event EventHandler<string>? PeerStatusChanged;

    /// <summary>
    /// Raised (on a background thread) the first time in a session that an unknown machine with the
    /// pairing code asks to connect. Answer with <see cref="AllowPendingPeer"/> or <see cref="IgnorePendingPeer"/>.
    /// </summary>
    public event EventHandler<PendingPeer>? PendingPeerRequested;

    /// <summary>Raised when <see cref="PendingPeers"/> changes (a request arrives, or is allowed or ignored).</summary>
    public event EventHandler? PendingPeersChanged;

    /// <summary>
    /// Raised when the service itself changed <see cref="AppSettings.Peers"/>: a pending peer was
    /// allowed, a peer was removed, or two entries for the same machine were merged. Settings are
    /// already saved.
    /// </summary>
    public event EventHandler? PeersChanged;

    /// <summary>Raised for each forwarded motion sample while controlling (for the debug panel).</summary>
    public event EventHandler<MouseDebugEventArgs>? MouseDebugUpdate;

    public RoboMouseService(AppSettings settings, IInputInjector? injector = null)
    {
        _settings = settings;
        // Inert (plain in-process injection) until the desktop-service setting turns it on.
        _injector = injector ?? (_serviceInjector = new DesktopServiceInjector());
        _controlled = new ControlledSession(_injector);
        _screenInfo = new ScreenInfo();
        _cursorManager = new CursorManager(_screenInfo);
        _uiQueue = new MessageWindow();
        _connectedPairingCode = settings.PairingCode;

        _mouseHook = new MouseHook();
        _mouseHook.MouseEvent += OnMouseEvent;

        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyboardEvent += OnKeyboardEvent;

        _rawMouse = new RawMouseInput();
        _rawMouse.Motion += OnRawMouseMotion;

        _sessionMonitor = new SessionMonitor();
        _sessionMonitor.LockChanged += OnSessionLockChanged;

        _powerMonitor = new PowerMonitor();
        _powerMonitor.Changed += OnLocalPowerStateChanged;
        _powerFollower = new PowerFollower(_injector);

        _clipboardManager = new ClipboardManager(_settings.Clipboard.MaxSizeBytes)
        {
            ShareFiles = _settings.Clipboard.SyncFiles
        };
        _clipboardManager.ClipboardChanged += OnClipboardChanged;
        _clipboardManager.FilesCopied += OnLocalFilesCopied;
        _clipboardManager.FilesCleared += OnLocalFilesCleared;

        _discovery = CreateDiscovery();
        _discoveryPort = settings.DiscoveryPort;
        _listener = CreateListener();
        _listenerName = settings.MachineName;

        ApplyHotkeySetting();
    }

    private PeerDiscovery CreateDiscovery()
    {
        var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();
        var discovery = new PeerDiscovery(
            _settings.DiscoveryPort,
            _settings.LocalPort,
            _settings.MachineId,
            _settings.MachineName,
            width,
            height);
        discovery.PeerDiscovered += OnPeerDiscovered;
        discovery.PeerLost += OnPeerLost;
        return discovery;
    }

    private ConnectionListener CreateListener()
    {
        var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();
        var listener = new ConnectionListener(
            _settings.LocalPort,
            GetPairingKey,
            _settings.MachineId,
            _settings.MachineName,
            width,
            height)
        {
            AcceptPolicy = DecideIncoming
        };
        listener.PeerConnected += OnIncomingConnection;
        return listener;
    }

    /// <summary>Starts the service.</summary>
    public void Start()
    {
        ApplyDesktopServiceSetting();
        StartListener();
        StartDiscovery();

        if (_settings.Clipboard.Enabled)
        {
            _clipboardManager.Start();
        }

        // Hooks stay installed while the service runs so the toggle hotkey works even when disabled.
        _mouseHook.Install();
        _keyboardHook.Install();

        _enabled = _settings.Enabled;

        _reconnectTimer = new System.Threading.Timer(_ => _ = ReconnectConfiguredPeersAsync(), null, ReconnectIntervalMs, ReconnectIntervalMs);
        _offerSweepTimer = new System.Threading.Timer(_ => SweepRetiredOffers(), null, OfferSweepInterval, OfferSweepInterval);
    }

    /// <summary>
    /// Opens the listening port. A port another program already uses (24800 is also Synergy's, Barrier's
    /// and Input Leap's default) must not take the app down: the error is kept in <see cref="ListenerError"/>
    /// and this machine can still connect out.
    /// </summary>
    private void StartListener()
    {
        NetworkStartError? error = null;
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            error = NetworkStartError.From(NetworkErrorKind.ListenPort, _listener.Port, ex);
            SimpleLogger.Log("Listener", error.Message);
        }
        if (error != ListenerError)
        {
            ListenerError = error;
            NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartDiscovery()
    {
        NetworkStartError? error = null;
        try
        {
            _discovery.Start();
        }
        catch (Exception ex)
        {
            error = NetworkStartError.From(NetworkErrorKind.DiscoveryPort, _settings.DiscoveryPort, ex);
            SimpleLogger.Log("Discovery", error.Message);
        }
        if (error != DiscoveryError)
        {
            DiscoveryError = error;
            NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies changed <see cref="AppSettings.LocalPort"/>, <see cref="AppSettings.DiscoveryPort"/> and
    /// <see cref="AppSettings.MachineName"/> without a restart: the listener and discovery are
    /// recreated with the new values (a failure lands in <see cref="ListenerError"/> /
    /// <see cref="DiscoveryError"/>). Live connections are kept; peers see a new name when they next
    /// connect. Call after saving the settings.
    /// </summary>
    public void ApplyNetworkSettings()
    {
        if (_disposed)
            return;

        var restartListener = _listener.Port != _settings.LocalPort || _listenerName != _settings.MachineName || ListenerError != null;
        if (restartListener)
        {
            SimpleLogger.Log("Listener", $"Network settings changed; listening on port {_settings.LocalPort} as {_settings.MachineName}");
            var old = _listener;
            old.PeerConnected -= OnIncomingConnection;
            old.Dispose();
            _listener = CreateListener();
            _listenerName = _settings.MachineName;
            StartListener();
        }

        var restartDiscovery = restartListener || _discoveryPort != _settings.DiscoveryPort || DiscoveryError != null;
        if (restartDiscovery)
        {
            var old = _discovery;
            old.PeerDiscovered -= OnPeerDiscovered;
            old.PeerLost -= OnPeerLost;
            old.Dispose();
            _discovery = CreateDiscovery();
            _discoveryPort = _settings.DiscoveryPort;
            StartDiscovery();
        }
    }

    /// <summary>
    /// Call after <see cref="AppSettings.PairingCode"/> changed. Every live connection was authenticated
    /// with the old code, so all of them (control, file transfers) are dropped; the background retry
    /// connects again with the new one. Returns true when connections were dropped.
    /// </summary>
    public bool ApplyPairingCode()
    {
        static string Normalize(string code) => code.Trim().ToUpperInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);

        if (Normalize(_settings.PairingCode) == Normalize(_connectedPairingCode))
            return false;
        _connectedPairingCode = _settings.PairingCode;

        SimpleLogger.Log("Connect", "Pairing code changed; dropping connections made with the old one");
        DropAllConnections();
        lock (_connectionLock)
        {
            _reportedReconnectFailures.Clear();
        }
        return true;
    }

    /// <summary>Drops every control connection, transfer server and transfer client.</summary>
    private void DropAllConnections()
    {
        foreach (var connection in _registry.Snapshot())
        {
            try { connection.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
            RemoveConnection(connection);
        }

        List<PeerConnection> servers;
        List<FileTransferClient> clients;
        lock (_fileLock)
        {
            servers = _transferServers.ToList();
            _transferServers.Clear();
            _transferQueues.Clear();
            clients = _remoteOffers.Values.Select(o => o.Client).ToList();
        }
        foreach (var server in servers)
            server.Dispose();
        foreach (var client in clients)
            client.DropConnection();
    }

    private string _listenerName = string.Empty;
    private int _discoveryPort;

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

        foreach (var connection in _registry.Clear())
        {
            connection.Dispose();
        }

        lock (_powerLock)
        {
            _peerPowerStates.Clear();
        }
        UpdatePowerFollowing();
    }

    #region Connection management

    /// <summary>
    /// Connects to a peer. A second call for the same address while one is in progress waits for that
    /// one instead of failing. The outcome is kept per peer (<see cref="GetLastConnectFailure"/>).
    /// </summary>
    public async Task ConnectToPeerAsync(PeerConfig peerConfig, CancellationToken ct = default)
    {
        var key = $"{peerConfig.Address}:{peerConfig.Port}";
        var configId = peerConfig.Id;
        Task<PeerConnection> attempt;
        bool joined;
        lock (_connectionLock)
        {
            joined = _connectsInFlight.TryGetValue(key, out attempt!);
            if (!joined)
            {
                attempt = ConnectCoreAsync(peerConfig, ct);
                _connectsInFlight[key] = attempt;
            }
        }

        PeerConnection connection;
        try
        {
            connection = joined ? await attempt.WaitAsync(ct) : await attempt;
        }
        catch (Exception ex) when (!joined)
        {
            SetConnectFailure(configId, PeerConnectFailure.From(ex));
            throw;
        }
        finally
        {
            if (!joined)
            {
                lock (_connectionLock)
                {
                    _connectsInFlight.Remove(key);
                }
            }
        }

        if (joined)
        {
            // The other caller's config object got the details; copy them onto this one.
            peerConfig.Id = connection.PeerId;
            peerConfig.ScreenWidth = connection.PeerScreenWidth;
            peerConfig.ScreenHeight = connection.PeerScreenHeight;
        }
    }

    private async Task<PeerConnection> ConnectCoreAsync(PeerConfig peerConfig, CancellationToken ct)
    {
        await Task.Yield(); // run outside the caller's lock
        var (_, _, width, height) = InputSimulator.GetVirtualScreenBounds();
        var configId = peerConfig.Id;

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

        OnConfigConnected(peerConfig, configId);
        AddConnection(connection);
        lock (_connectionLock)
        {
            _reportedReconnectFailures.Remove(peerConfig.Id);
        }
        return connection;
    }

    /// <summary>
    /// Housekeeping once a configured peer's real id is known: forget its failure, take it off the
    /// blocked list (the user configured it, so they want it), and merge a second entry for the same
    /// machine (added once by address and once by discovery, say) into the first.
    /// </summary>
    private void OnConfigConnected(PeerConfig peerConfig, string previousId)
    {
        SetConnectFailure(previousId, null);
        SetConnectFailure(peerConfig.Id, null);

        var changed = false;
        var peers = _settings.Peers.ToList();
        if (!peers.Contains(peerConfig))
            return; // not (yet) a configured peer: nothing to tidy

        if (_settings.BlockedMachineIds.Remove(peerConfig.Id))
            changed = true;

        var duplicate = peers.FirstOrDefault(p => !ReferenceEquals(p, peerConfig) && p.Id == peerConfig.Id);
        if (duplicate != null)
        {
            // Keep the older entry (its position and MAC address) with the address that just worked.
            SimpleLogger.Log("Connect", $"{peerConfig.Name} was configured twice; merging the entries");
            duplicate.Address = peerConfig.Address;
            duplicate.Port = peerConfig.Port;
            duplicate.Enabled |= peerConfig.Enabled;
            _settings.Peers.Remove(peerConfig);
            changed = true;
        }

        if (changed)
        {
            SaveSettings("peer list");
            PeersChanged?.Invoke(this, EventArgs.Empty);
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
            candidates = _settings.Peers.ToList()
                .Where(p => p.Enabled
                            && !string.IsNullOrEmpty(p.Address)
                            && _registry.Get(p.Id) == null
                            && !_connectsInFlight.ContainsKey($"{p.Address}:{p.Port}"))
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

    /// <summary>
    /// Why the last attempt to connect to this peer (by config id) failed, or null when it connected
    /// or has not been tried. Cleared as soon as a connection succeeds.
    /// </summary>
    public PeerConnectFailure? GetLastConnectFailure(string peerId)
    {
        lock (_connectionLock)
        {
            return _connectFailures.TryGetValue(peerId, out var failure) ? failure : null;
        }
    }

    private void SetConnectFailure(string peerId, PeerConnectFailure? failure)
    {
        bool changed;
        lock (_connectionLock)
        {
            if (failure == null)
                changed = _connectFailures.Remove(peerId);
            else
            {
                changed = !_connectFailures.TryGetValue(peerId, out var old) || old.Kind != failure.Kind || old.Message != failure.Message;
                _connectFailures[peerId] = failure;
            }
        }
        if (changed)
            PeerStatusChanged?.Invoke(this, peerId);
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

        var conn = _registry.Get(peerConfig.Id);
        if (conn != null)
            peerConfig.Name = conn.PeerName;

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
        _settings.BlockedMachineIds.Remove(peerConfig.Id);

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
                if (_registry.Contains(peer.Id))
                    continue;

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

        _settings.BlockedMachineIds.Remove(peer.MachineId);
        await ConnectToPeerAsync(peerConfig, ct);
    }

    /// <summary>Disconnects from a peer.</summary>
    public async Task DisconnectFromPeerAsync(string peerId)
    {
        var connection = _registry.Get(peerId);
        if (connection != null)
        {
            await connection.DisconnectAsync();
            RemoveConnection(connection);
        }
    }

    /// <summary>
    /// Removes a configured peer: drops its connection, deletes its settings entry and blocks its
    /// machine id, so that it cannot come straight back as a pending request. Adding it again by hand
    /// unblocks it. Saves the settings.
    /// </summary>
    public async Task RemovePeerAsync(PeerConfig peer)
    {
        try { await DisconnectFromPeerAsync(peer.Id); }
        catch (Exception ex) { SimpleLogger.Log("Connect", $"Disconnecting {peer.Name} failed: {ex.Message}"); }

        _settings.Peers.Remove(peer);
        if (!_settings.BlockedMachineIds.Contains(peer.Id))
            _settings.BlockedMachineIds.Add(peer.Id);
        SetConnectFailure(peer.Id, null);
        SaveSettings("removed peer");
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes a machine off <see cref="AppSettings.BlockedMachineIds"/> (it may then ask to connect again). Saves.</summary>
    public void UnblockMachine(string machineId)
    {
        if (_settings.BlockedMachineIds.Remove(machineId))
            SaveSettings("blocked list");
    }

    private void AddConnection(PeerConnection connection)
    {
        var result = _registry.Add(connection, _settings.MachineId);
        if (!result.Added)
        {
            SimpleLogger.Log("Conn", $"Dropping duplicate {(connection.IsOutbound ? "outbound" : "inbound")} connection to {connection.PeerName}");
            connection.Dispose();
            return;
        }

        var replaced = result.Replaced;
        if (replaced != null)
        {
            SimpleLogger.Log("Conn", $"Replacing duplicate connection to {connection.PeerName}");
            replaced.MessageReceived -= OnMessageReceived;
            replaced.Dispose();

            // Control state must not stay on the connection that was just thrown away: input would be
            // posted into a disposed connection and the cursor would stay parked here.
            if (result.ReplacedWasActive)
            {
                EndRemoteControl(notifyPeer: false);
                PutCursorBackAtExitEdge();
            }
            EndBeingControlled(notifyPeer: false, onlyIf: replaced);
        }

        RememberMacAddress(connection);

        connection.MessageReceived += OnMessageReceived;
        connection.Disconnected += (s, e) => RemoveConnection(connection);
        connection.Start();
        connection.Post(new PowerStateMessage { State = _powerMonitor.State });

        if (replaced == null)
            PeerConnected?.Invoke(this, connection);
    }

    private void RemoveConnection(PeerConnection connection)
    {
        if (!_registry.Remove(connection, out var wasActive))
            return;

        connection.Dispose();

        if (wasActive)
        {
            SimpleLogger.Log("Control", $"Lost {connection.PeerName} while controlling it");
            EndRemoteControl(notifyPeer: false);
            PutCursorBackAtExitEdge();
        }

        EndBeingControlled(notifyPeer: false, onlyIf: connection);

        ForgetRemoteOffer(connection.PeerId);

        // A host that vanished (it slept before it could say so, or the network dropped) is no reason to
        // blank this display; just stop holding it on. The host is remembered for when it reconnects.
        lock (_powerLock)
        {
            _peerPowerStates.Remove(connection.PeerId);
        }
        UpdatePowerFollowing();

        PeerDisconnected?.Invoke(this, connection.PeerId);
    }

    /// <summary>
    /// The listener's accept policy: runs after the pairing code was verified, before the handshake is
    /// acknowledged. Unknown machines become pending requests.
    /// </summary>
    private string? DecideIncoming(HandshakeMessage handshake, IPEndPoint? remote)
    {
        bool ignored;
        lock (_connectionLock)
        {
            ignored = _ignoredPeers.Contains(handshake.MachineId);
        }

        var decision = AcceptPolicy.Decide(_settings, handshake.MachineId, handshake.Kind, remote?.Address,
            handshake.ListenPort, ignored, out _);
        if (decision == AcceptDecision.Pending)
            RegisterPending(handshake, remote);

        return AcceptPolicy.RejectReason(decision, handshake.Kind, _settings.MachineName);
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
                    _transferQueues[connection] = new SerialWorkQueue(maxPending: 4);
                }
                connection.MessageReceived += OnTransferRequest;
                connection.Disconnected += (s, e) =>
                {
                    lock (_fileLock)
                    {
                        _transferServers.Remove(connection);
                        _transferQueues.Remove(connection);
                    }
                    connection.Dispose();
                };
                connection.Start();
                return;
        }

        // Settings can change between the accept decision and now; check again, and learn the id of a
        // peer that was configured by address and is connecting for the first time.
        var decision = AcceptPolicy.Decide(_settings, connection.PeerId, connection.Kind, connection.RemoteEndPoint?.Address,
            connection.PeerListenPort, ignoredThisSession: false, out var config);
        if (decision != AcceptDecision.Accept || config == null)
        {
            SimpleLogger.Log("Accept", $"Refusing connection from {connection.PeerName} ({decision})");
            connection.Disconnected += (s, e) => connection.Dispose();
            connection.Start();
            _ = connection.DisconnectAsync();
            return;
        }

        if (config.Id != connection.PeerId)
        {
            var previousId = config.Id;
            SimpleLogger.Log("Accept", $"{config.Name} at {config.Address} is {connection.PeerName} ({connection.PeerId})");
            config.Id = connection.PeerId;
            OnConfigConnected(config, previousId);
            SaveSettings("peer id");
        }
        else
        {
            SetConnectFailure(config.Id, null);
        }

        AddConnection(connection);
    }

    #region Pending peers

    /// <summary>Machines waiting for the user to allow or ignore them, oldest first.</summary>
    public IReadOnlyList<PendingPeer> PendingPeers
    {
        get
        {
            lock (_connectionLock)
                return _pendingPeers.Values.OrderBy(p => p.RequestedAt).ToList();
        }
    }

    private void RegisterPending(HandshakeMessage handshake, IPEndPoint? remote)
    {
        var pending = new PendingPeer(
            handshake.MachineId,
            handshake.MachineName,
            remote?.Address is { } address ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : string.Empty,
            handshake.ListenPort > 0 ? handshake.ListenPort : 24800,
            handshake.ScreenWidth,
            handshake.ScreenHeight,
            WakeOnLan.Normalize(handshake.MacAddress),
            DateTime.Now);

        bool isNew;
        lock (_connectionLock)
        {
            isNew = !_pendingPeers.ContainsKey(pending.MachineId);
            // Keep the first request time; refresh the address in case it moved.
            if (!isNew)
                pending = pending with { RequestedAt = _pendingPeers[pending.MachineId].RequestedAt };
            _pendingPeers[pending.MachineId] = pending;
        }

        if (!isNew)
            return;
        SimpleLogger.Log("Accept", $"{pending.MachineName} ({pending.Address}) wants to connect; waiting for the user to allow it");
        PendingPeersChanged?.Invoke(this, EventArgs.Empty);
        PendingPeerRequested?.Invoke(this, pending);
    }

    /// <summary>
    /// Allows a pending machine: adds it as a configured peer on <paramref name="position"/> (default: the
    /// first edge no peer uses), takes it off the blocked list, saves, and connects in the background.
    /// Returns the new config, or null when no such request is pending.
    /// </summary>
    public PeerConfig? AllowPendingPeer(string machineId, ScreenPosition? position = null)
    {
        PendingPeer? pending;
        lock (_connectionLock)
        {
            if (_pendingPeers.Remove(machineId, out pending))
                _ignoredPeers.Remove(machineId);
        }
        if (pending == null)
            return null;

        var config = _settings.Peers.FirstOrDefault(p => p.Id == machineId);
        if (config == null)
        {
            config = new PeerConfig
            {
                Id = pending.MachineId,
                Name = pending.MachineName,
                Address = pending.Address,
                Port = pending.Port,
                Position = position ?? FirstFreeEdge(),
                ScreenWidth = pending.ScreenWidth,
                ScreenHeight = pending.ScreenHeight,
                MacAddress = pending.MacAddress
            };
            _settings.Peers.Add(config);
        }
        else
        {
            config.Enabled = true;
        }
        _settings.BlockedMachineIds.Remove(machineId);
        SaveSettings("allowed peer");
        SimpleLogger.Log("Accept", $"Allowed {pending.MachineName}; placed {config.Position}");

        PendingPeersChanged?.Invoke(this, EventArgs.Empty);
        PeersChanged?.Invoke(this, EventArgs.Empty);

        // Connect now rather than waiting for either side's retry.
        var toConnect = config;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (_registry.Get(toConnect.Id) == null && !string.IsNullOrEmpty(toConnect.Address))
                    await ConnectToPeerAsync(toConnect, cts.Token);
            }
            catch (Exception ex)
            {
                SimpleLogger.Log("Connect", $"Could not connect to {toConnect.Name} yet: {ex.GetBaseException().Message}");
            }
        });
        return config;
    }

    /// <summary>Ignores a pending machine for the rest of this session: its connections are refused without asking again.</summary>
    public void IgnorePendingPeer(string machineId)
    {
        bool removed;
        lock (_connectionLock)
        {
            removed = _pendingPeers.Remove(machineId);
            _ignoredPeers.Add(machineId);
        }
        if (removed)
        {
            SimpleLogger.Log("Accept", $"Ignoring connection requests from {machineId} for this session");
            PendingPeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The first edge (right, left, top, bottom) no configured peer uses; right when all are taken.</summary>
    public ScreenPosition FirstFreeEdge()
    {
        var used = _settings.Peers.ToList().Select(p => p.Position).ToHashSet();
        foreach (var edge in new[] { ScreenPosition.Right, ScreenPosition.Left, ScreenPosition.Top, ScreenPosition.Bottom })
        {
            if (!used.Contains(edge))
                return edge;
        }
        return ScreenPosition.Right;
    }

    #endregion

    /// <summary>
    /// Turns a configured peer on or off and saves. Disabling drops any live connection to it;
    /// enabling connects again in the background.
    /// </summary>
    public async Task SetPeerEnabledAsync(PeerConfig peer, bool enabled)
    {
        if (peer.Enabled == enabled)
            return;

        peer.Enabled = enabled;
        SaveSettings("peer on/off");

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

    /// <summary>
    /// Saves the settings from background work. A failure (disk full, file locked) is logged rather than
    /// thrown into a network or timer thread.
    /// </summary>
    private void SaveSettings(string what)
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Settings", $"Could not save settings ({what}): {ex.Message}");
        }
    }

    /// <summary>Whether a live connection to the given peer exists.</summary>
    public bool IsPeerConnected(string peerId) => _registry.Get(peerId) != null;

    /// <summary>Gets the live connection to a peer, if any.</summary>
    public PeerConnection? GetConnection(string peerId) => _registry.Get(peerId);

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
    /// The remote does not register us as a peer for this (an unknown machine shows there as a request
    /// to connect).
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
        catch (Exception ex)
        {
            var (kind, message) = PeerConnectFailure.Classify(ex);
            result.FailureKind = kind;
            result.Error = message;
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
            result.FailureKind = PeerFailureKind.TimedOut;
            result.Error = "The peer stopped answering pings. The connection may be dead.";
        }
        catch (Exception ex)
        {
            result.FailureKind = PeerFailureKind.Other;
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

    #region Wake-on-LAN

    // Edge hits (one per mouse move while leaning on the edge) needed before a sleeping peer is woken,
    // so brushing the edge does not wake it, and the pause before another packet is sent.
    private const int WakeEdgeHits = 25;
    private const int WakeEdgeHitGapMs = 400;
    private const int WakeRepeatMs = 10000;

    private int _wakeEdgeHits;
    private long _lastWakeEdgeHit;
    private long _nextWakeAllowed;

    /// <summary>Raised (on a background thread) after a wake packet was sent to a peer.</summary>
    public event EventHandler<PeerConfig>? PeerWakeSent;

    /// <summary>Keeps the MAC address a peer reported so it can be woken after it goes to sleep.</summary>
    private void RememberMacAddress(PeerConnection connection)
    {
        if (connection.PeerMacAddress.Length == 0)
            return;
        var peer = _settings.Peers.FirstOrDefault(p => p.Id == connection.PeerId);
        if (peer == null || peer.MacAddress == connection.PeerMacAddress)
            return;

        peer.MacAddress = connection.PeerMacAddress;
        SimpleLogger.Log("Wake", $"{peer.Name} can be woken at {WakeOnLan.Format(peer.MacAddress)}");
        try { _settings.Save(); }
        catch (Exception ex) { SimpleLogger.Log("Wake", $"Could not save the MAC address: {ex.Message}"); }
    }

    /// <summary>True when the peer's MAC address is known, so a wake packet can be sent.</summary>
    public static bool CanWake(PeerConfig peer) => WakeOnLan.Normalize(peer.MacAddress).Length != 0;

    /// <summary>
    /// Sends a Wake-on-LAN packet to the peer. The background reconnect picks it up once it is awake.
    /// Returns false when its MAC address is not known yet or nothing could be sent.
    /// </summary>
    public bool WakePeer(PeerConfig peer)
    {
        if (!CanWake(peer))
            return false;
        SimpleLogger.Log("Wake", $"Waking {peer.Name}");
        var sent = WakeOnLan.Send(peer.MacAddress) > 0;
        if (sent)
            PeerWakeSent?.Invoke(this, peer);
        return sent;
    }

    /// <summary>
    /// Called from the mouse hook while the cursor leans on an edge whose peer is not connected. Never
    /// does I/O here: the packet is sent from the thread pool.
    /// </summary>
    private void ConsiderWakingPeerAt(ScreenPosition edge)
    {
        if (!_settings.WakeOnEdge)
            return;

        var now = Environment.TickCount64;
        if (now - _lastWakeEdgeHit > WakeEdgeHitGapMs)
            _wakeEdgeHits = 0;
        _lastWakeEdgeHit = now;
        if (++_wakeEdgeHits < WakeEdgeHits || now < _nextWakeAllowed)
            return;

        var peer = _settings.Peers.FirstOrDefault(p => p.Position == edge && p.Enabled);
        if (peer == null || !CanWake(peer))
            return;

        _wakeEdgeHits = 0;
        _nextWakeAllowed = now + WakeRepeatMs;
        _ = Task.Run(() => WakePeer(peer));
    }

    #endregion

    private PeerConfig? GetPeerAtEdge(ScreenPosition edge)
    {
        var peer = _settings.Peers.FirstOrDefault(p => p.Position == edge && p.Enabled);
        if (peer == null)
            return null;

        return _registry.Contains(peer.Id) ? peer : null;
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

        _lastRawMotionTick = Environment.TickCount64;
        _hookMovesWithoutRaw = 0;
        if (_hookMotionFallback)
        {
            _hookMotionFallback = false;
            SimpleLogger.Log("Control", "Raw input resumed; back to raw motion");
        }

        var connection = _registry.Active;
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
        // Injected events (our own SendInput/SetCursorPos, or a remote controller's) are never ours to act on.
        if (e.IsInjected)
            return;

        _physical.TrackButton(e.EventType);

        if (!_enabled)
            return;

        // While being controlled, let the local mouse behave normally.
        if (_controlled.IsActive)
            return;

        if (_isControllingRemote)
        {
            // Freeze the local cursor: swallow everything. Motion arrives separately via raw input.
            e.Handled = true;

            if (e.EventType == MouseEventType.Move)
                ForwardHookedMotionIfRawSilent(e);
            else
            {
                _forwarded.TrackButton(e.EventType);
                _registry.Active?.Post(new MouseMessage
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

        var edges = _screenInfo.GetEdgesAt(e.X, e.Y, _settings.EdgeThreshold);
        if (edges.Count == 0)
            return;

        // Never cross while a button is held (dragging a window or selecting text): the button would stay
        // down here while the cursor is parked, and the drag would jump to the middle of the screen.
        if (_physical.AnyButtonDown && ButtonsReallyHeld())
            return;

        // In a corner the cursor is on two edges; take the one that leads to a peer.
        EdgeInfo? edge = null;
        PeerConfig? targetPeer = null;
        foreach (var candidate in edges)
        {
            targetPeer = GetPeerAtEdge(candidate.Edge);
            if (targetPeer != null)
            {
                edge = candidate;
                break;
            }
        }
        if (targetPeer == null && _settings.WrapAround)
        {
            // No peer on this edge: wrap to the peer on the opposite edge, arriving from its far side.
            foreach (var candidate in edges)
            {
                targetPeer = GetPeerAtEdge(CursorManager.GetOppositeEdge(candidate.Edge));
                if (targetPeer != null)
                {
                    edge = candidate;
                    break;
                }
            }
        }
        if (targetPeer == null || edge == null)
        {
            foreach (var candidate in edges)
                ConsiderWakingPeerAt(candidate.Edge);
            return;
        }

        if (StartRemoteControl(targetPeer, edge))
            e.Handled = true;
    }

    /// <summary>
    /// Double-checks with Windows that a mouse button the hook saw go down is still down. A button
    /// released on the secure desktop (a UAC prompt) never reaches the hook, and a stale "held" would
    /// block crossing for good.
    /// </summary>
    private bool ButtonsReallyHeld()
    {
        const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06;
        foreach (var vk in new[] { VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2 })
        {
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                return true;
        }
        _physical.Clear();
        return false;
    }

    /// <summary>
    /// Forwards a blocked move as motion when raw input has gone quiet (an elevated window took the
    /// foreground on this machine). A few hooked moves with no raw motion beside them are needed before
    /// switching, so the two sources never both forward the same movement.
    /// </summary>
    private void ForwardHookedMotionIfRawSilent(InputMouseEventArgs e)
    {
        var now = Environment.TickCount64;
        if (!_hookMotionFallback)
        {
            if (now - _lastRawMotionTick < RawInputSilenceMs)
            {
                _hookMovesWithoutRaw = 0;
                return;
            }
            if (++_hookMovesWithoutRaw < HookMovesBeforeFallback)
                return;

            _hookMotionFallback = true;
            _uiQueue.BeginInvoke(() => SimpleLogger.Log("Control", "Raw input is silent (elevated window in front?); using hooked motion"));
        }

        var dx = e.X - _parkedX;
        var dy = e.Y - _parkedY;
        if (dx == 0 && dy == 0)
            return;

        _registry.Active?.Post(MouseMessage.Motion(dx, dy));
    }

    private readonly ModifierState _modifiers = new();

    private void OnKeyboardEvent(object? sender, KeyboardEventArgs e)
    {
        if (e.IsInjected)
        {
            // Volume knobs and media buttons are often turned into keystrokes by the vendor's software,
            // so they arrive marked as injected. While controlling a remote they belong to it. Nothing
            // of ours injects keys on this side while it is the controller, apart from releasing held
            // modifiers at the moment of crossing, and those are not media keys.
            if (_enabled && _isControllingRemote && IsMediaKey(e.KeyCode))
            {
                e.Handled = true;
                _registry.Active?.Post(KeyboardMessage.FromEvent(e));
            }
            return;
        }

        var isDown = e.EventType is KeyboardEventType.KeyDown or KeyboardEventType.SysKeyDown;
        _modifiers.Update(e.KeyCode, isDown);
        _physical.TrackKey(e.KeyCode, e.ScanCode, e.IsExtendedKey, isDown);

        // Escape hatch and on/off switch. Works whether or not sharing is enabled, and while controlling a
        // remote it takes priority over forwarding so a hung peer can never trap the keyboard.
        if (isDown && _hotkey?.Key == e.KeyCode)
        {
            // Modifier ups pressed on the secure desktop never reached the hook, so a stale "held" could
            // make the plain key fire the hotkey. While not controlling, Windows' own key state is the
            // truth; while controlling, the hook swallows modifiers so Windows never sees them held.
            if (!_isControllingRemote)
                _modifiers.Confirm(key => (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0);

            if (_hotkey.Matches(e.KeyCode, _modifiers))
            {
                e.Handled = true;
                OnHotkeyPressed();
                return;
            }
        }

        if (!_enabled || _controlled.IsActive)
            return;

        if (_isControllingRemote)
        {
            e.Handled = true;
            _forwarded.TrackKey(e.KeyCode, e.ScanCode, e.IsExtendedKey, isDown);
            _registry.Active?.Post(KeyboardMessage.FromEvent(e));
        }
    }

    private static bool IsMediaKey(Keys key) =>
        key is >= Keys.VolumeMute and <= Keys.MediaPlayPause;

    /// <summary>
    /// The session was locked or unlocked. Keys released on the lock screen never reached the hooks,
    /// so the modifier and held-key state is reset, and a peer being controlled is sent the key-ups for
    /// everything forwarded as held, so nothing sticks there either.
    /// </summary>
    private void OnSessionLockChanged(bool locked)
    {
        SimpleLogger.Log("Session", locked ? "Session locked" : "Session unlocked");
        _modifiers.Clear();
        _physical.Clear();
        ReleaseForwardedInput();
    }

    /// <summary>Sends the peer being controlled an up for every key and button forwarded to it as down.</summary>
    private void ReleaseForwardedInput()
    {
        var (keys, buttons) = _forwarded.TakeReleases();
        var connection = _registry.Active;
        if (connection == null || !_isControllingRemote)
            return;
        foreach (var (key, scan, extended) in keys)
            connection.Post(new KeyboardMessage { KeyCode = key, ScanCode = scan, EventType = KeyboardEventType.KeyUp, IsExtendedKey = extended });
        foreach (var up in buttons)
            connection.Post(new MouseMessage { EventType = up });
    }

    private Hotkey? _hotkey;

    /// <summary>How remote input is being applied: in-process, or through the desktop service.</summary>
    public DesktopServiceState DesktopServiceState => _serviceInjector?.State ?? DesktopServiceState.Off;

    /// <summary>Connects to or lets go of the desktop service to match the current setting.</summary>
    public void ApplyDesktopServiceSetting()
    {
        if (_serviceInjector == null)
            return;
        var installed = DesktopServiceControl.IsInstalled;
        SimpleLogger.Log("Service", $"Desktop service: setting {(_settings.UseDesktopService ? "on" : "off")}, " +
            $"installed {installed}, running {DesktopServiceControl.IsRunning}");
        // The setting can outlive the service (it was uninstalled, or the settings file came from another
        // machine); without the service there is nothing to connect to and no card to turn it off with.
        _serviceInjector.Enabled = _settings.UseDesktopService && installed;
    }

    /// <summary>Re-reads the toggle hotkey from settings.</summary>
    public void ApplyHotkeySetting()
    {
        _hotkey = Hotkey.Parse(_settings.ToggleHotkey);
    }

    #region Power

    private void OnLocalPowerStateChanged(PeerPowerState state)
    {
        foreach (var connection in _registry.Snapshot())
            connection.Post(new PowerStateMessage { State = state });
    }

    /// <summary>The peer that controls this machine becomes its host; controlling a peer stops following it.</summary>
    private void SetPowerHost(string peerId, bool isHost)
    {
        lock (_powerLock)
        {
            if (isHost)
                _powerHostId = peerId;
            else if (_powerHostId == peerId)
                _powerHostId = null;
            else
                return;
        }
        UpdatePowerFollowing();
    }

    /// <summary>Re-evaluates what to mirror. Call after the setting, the host or a peer's state changes.</summary>
    public void UpdatePowerFollowing()
    {
        PeerPowerState? host = null;
        lock (_powerLock)
        {
            if (_settings.FollowHostPower && _powerHostId != null && _peerPowerStates.TryGetValue(_powerHostId, out var state))
                host = state;
        }
        _powerFollower.Apply(host);
    }

    #endregion

    private void OnHotkeyPressed()
    {
        if (_isControllingRemote)
        {
            _uiQueue.BeginInvoke(() => SimpleLogger.Log("Control", "Hotkey pressed: releasing remote control"));
            EndRemoteControl(notifyPeer: true);
            var bounds = _screenInfo.PrimaryBounds;
            var (x, y) = (bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            _uiQueue.BeginInvoke(() => InputSimulator.MoveTo(x, y));
            Interlocked.Exchange(ref _returnCooldownUntil, Environment.TickCount64 + ReturnCooldownMs);
            return;
        }

        Enabled = !Enabled;
        _settings.Enabled = Enabled;
        var enabled = Enabled;
        _uiQueue.BeginInvoke(() => SimpleLogger.Log("Control", $"Hotkey pressed: sharing {(enabled ? "enabled" : "disabled")}"));
    }

    /// <summary>
    /// Hands the cursor to <paramref name="peer"/>. Called from the mouse hook, so only the state change
    /// and the enter message happen here; logging, raw input, hiding and parking the cursor are posted
    /// to run right after the hook returns. Returns false when the peer's connection just went away.
    /// </summary>
    private bool StartRemoteControl(PeerConfig peer, EdgeInfo edge)
    {
        // Becomes the active connection only if it is still registered and connected, under the same
        // lock that removing or replacing it takes, so a connection dropping at this moment is never
        // left as the one we control through.
        var connection = _registry.TryActivate(peer.Id);
        if (connection == null)
            return false;

        _activePeer = peer;
        _exitEdge = edge.Edge;
        _exitPosition = edge.NormalizedPosition;
        _isControllingRemote = true;
        _remoteBlockReason = InputBlockReason.None;
        _forwarded.Clear();

        var bounds = _screenInfo.PrimaryBounds;
        _parkedX = bounds.Left + bounds.Width / 2;
        _parkedY = bounds.Top + bounds.Height / 2;
        _lastRawMotionTick = Environment.TickCount64;
        _hookMovesWithoutRaw = 0;
        _hookMotionFallback = false;

        // The cursor appears on the peer's edge opposite the one it left here (its left edge when we
        // left through our right). With wrap-around that is not necessarily the edge facing this screen.
        var entryEdge = CursorManager.GetOppositeEdge(edge.Edge);
        connection.Post(new CursorEnterMessage
        {
            EntryEdge = entryEdge,
            EntryX = entryEdge is ScreenPosition.Left or ScreenPosition.Right ? 0f : edge.NormalizedPosition,
            EntryY = entryEdge is ScreenPosition.Left or ScreenPosition.Right ? edge.NormalizedPosition : 0f,
            WrapAround = _settings.WrapAround
        });

        // Modifiers held while crossing (Ctrl held to copy-drag, Shift to extend a selection) go with the
        // cursor: the peer gets the downs so the chord still works there, and they are released here so
        // they do not stay down on this machine while its keyboard is swallowed.
        var modifiers = _physical.HeldModifiers();
        foreach (var (key, scan, extended) in modifiers)
        {
            _forwarded.TrackKey(key, scan, extended, isDown: true);
            connection.Post(new KeyboardMessage { KeyCode = key, ScanCode = scan, EventType = KeyboardEventType.KeyDown, IsExtendedKey = extended });
        }

        var parkedX = _parkedX;
        var parkedY = _parkedY;
        _uiQueue.BeginInvoke(() =>
        {
            SimpleLogger.Log("Control", $"Entering {peer.Name} via {edge.Edge} edge at {edge.NormalizedPosition:F3}");
            if (!_isControllingRemote || !ReferenceEquals(_registry.Active, connection))
                return; // Already over: the end was queued behind this and cleans up.

            foreach (var (key, scan, extended) in modifiers)
                InputSimulator.SimulateKeyboardEvent(key, scan, KeyboardEventType.KeyUp, extended);

            try
            {
                _rawMouse.Start();
            }
            catch (Exception ex)
            {
                SimpleLogger.Log("Control", $"Raw input could not start: {ex.Message}");
                EndRemoteControl(notifyPeer: true);
                Error?.Invoke(this, ex);
                return;
            }

            InputSimulator.HideSystemCursor();
            _cursorHidden = true;

            // Park the (now hidden and frozen) cursor away from the edge so nothing local reacts to it.
            InputSimulator.MoveTo(parkedX, parkedY);

            SetPowerHost(peer.Id, isHost: false);
        });

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Stops controlling the remote. When <paramref name="notifyPeer"/> is true the remote is told to
    /// release; when false the remote initiated the hand-back (or is gone) and already knows. Undoing the
    /// local side (raw input, hidden cursor) is queued behind the work that set it up, so the two can
    /// never run in the wrong order.
    /// </summary>
    private void EndRemoteControl(bool notifyPeer)
    {
        if (!_isControllingRemote)
            return;

        var connection = _registry.ClearActive();
        var peer = _activePeer;

        _isControllingRemote = false;
        _activePeer = null;
        _remoteBlockReason = InputBlockReason.None;
        _forwarded.Clear();

        _uiQueue.BeginInvoke(() =>
        {
            // A new session may have started in the meantime; it owns raw input and the cursor now.
            if (_isControllingRemote)
                return;
            _rawMouse.Stop();
            InputSimulator.RestoreSystemCursor();
            _cursorHidden = false;
        });

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
        PlaceCursorInsideEdge(CursorManager.GetOppositeEdge(msg.ExitEdge), normalized);
    }

    /// <summary>
    /// The peer went away while we controlled it: put the cursor back where it left this screen rather
    /// than leaving it at the parked point in the middle.
    /// </summary>
    private void PutCursorBackAtExitEdge()
    {
        Interlocked.Exchange(ref _returnCooldownUntil, Environment.TickCount64 + ReturnCooldownMs);
        PlaceCursorInsideEdge(_exitEdge, _exitPosition);
    }

    /// <summary>Moves the cursor one pixel inside a local outer edge, queued behind any pending cursor work.</summary>
    private void PlaceCursorInsideEdge(ScreenPosition edge, float normalized)
    {
        var (x, y) = _cursorManager.GetEdgePoint(edge, normalized);
        var (nudgeX, nudgeY) = edge switch
        {
            ScreenPosition.Left => (1, 0),
            ScreenPosition.Right => (-1, 0),
            ScreenPosition.Top => (0, 1),
            ScreenPosition.Bottom => (0, -1),
            _ => (0, 0)
        };
        _uiQueue.BeginInvoke(() =>
        {
            if (!_isControllingRemote)
                InputSimulator.MoveTo(x + nudgeX, y + nudgeY);
        });
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
                    _controlled.ApplyKey(connection, keyMsg);
                    break;

                case CursorEnterMessage enterMsg:
                    HandleCursorEnter(enterMsg, connection);
                    break;

                case InputStatusMessage statusMsg:
                    if (_isControllingRemote && connection == _registry.Active && statusMsg.Reason != _remoteBlockReason)
                    {
                        _remoteBlockReason = statusMsg.Reason;
                        SimpleLogger.Log("Control", $"{connection.PeerName} input status: {statusMsg.Reason}");
                        ControlStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;

                case PowerStateMessage powerMsg:
                    SimpleLogger.Log("Power", $"{connection.PeerName}: {powerMsg.State}");
                    lock (_powerLock)
                    {
                        _peerPowerStates[connection.PeerId] = powerMsg.State;
                    }
                    UpdatePowerFollowing();
                    break;

                case CursorLeaveMessage leaveMsg:
                    if (_isControllingRemote && connection == _registry.Active)
                    {
                        HandleReturnFromRemote(leaveMsg);
                    }
                    else
                    {
                        EndBeingControlled(notifyPeer: false, onlyIf: connection);
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
        // Sharing switched off here, or this machine is driving another screen right now: say no, and
        // hand the cursor straight back so the controller does not sit with a hidden, parked pointer.
        var previousEdge = _controlled.EntryEdge;
        var outcome = _controlled.Enter(connection, msg.EntryEdge, msg.WrapAround, _enabled, _isControllingRemote, out var replaced);

        if (outcome == EnterOutcome.Refused)
        {
            SimpleLogger.Log("Control", $"Refusing control from {connection.PeerName}: {(_enabled ? "controlling another machine" : "sharing is off")}");
            connection.Post(new CursorLeaveMessage { ExitEdge = msg.EntryEdge, ExitX = msg.EntryX, ExitY = msg.EntryY });
            return;
        }

        if (replaced != null)
        {
            // A second machine took over; the first one's held input was released. Give it its cursor back.
            SimpleLogger.Log("Control", $"{connection.PeerName} takes over from {replaced.PeerName}");
            replaced.Post(new CursorLeaveMessage { ExitEdge = previousEdge, ExitX = 0.5f, ExitY = 0.5f });
        }

        SimpleLogger.Log("Control", $"Controlled by {connection.PeerName} via {msg.EntryEdge} edge");

        _edgeOvershoot = 0;
        _injectionBlocked = false;
        _localBlockReason = InputBlockReason.None;
        SetPowerHost(connection.PeerId, isHost: true);
        _desktopPollTimer?.Dispose();
        _desktopPollTimer = new System.Threading.Timer(_ => ReportInputStatus(), null, 250, 250);

        var normalized = msg.EntryEdge is ScreenPosition.Left or ScreenPosition.Right ? msg.EntryY : msg.EntryX;
        var (entryX, entryY) = _cursorManager.GetEdgePoint(msg.EntryEdge, normalized);
        _injector.MoveTo(entryX, entryY);

        ControlStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRemoteMouseInput(MouseMessage msg, PeerConnection connection)
    {
        if (msg.IsMotion)
        {
            if (!_controlled.IsControlledBy(connection))
                return;
            MoveRemoteCursor(msg.DeltaX, msg.DeltaY);
            CheckForReturnEdge(msg.DeltaX, msg.DeltaY);
            return;
        }

        _controlled.ApplyMouseButton(connection, msg.EventType, msg.WheelDelta);
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
        var layout = _screenInfo.Layout;
        var entryEdge = _controlled.EntryEdge;

        // Which edge the cursor is pinned against while being pushed further into it. Normally only the
        // entry edge hands control back; with wrap-around any edge does.
        static (bool pinned, int push) Probe(ScreenPosition edge, int x, int y, int dx, int dy, MonitorLayout layout) => edge switch
        {
            ScreenPosition.Left => (layout.IsAtOuterEdge(edge, x, y), -dx),
            ScreenPosition.Right => (layout.IsAtOuterEdge(edge, x, y), dx),
            ScreenPosition.Top => (layout.IsAtOuterEdge(edge, x, y), -dy),
            ScreenPosition.Bottom => (layout.IsAtOuterEdge(edge, x, y), dy),
            _ => (false, 0)
        };

        var exitEdge = entryEdge;
        var (pinned, push) = Probe(entryEdge, x, y, dx, dy, layout);
        if (_controlled.WrapsAround && (!pinned || push <= 0))
        {
            foreach (var edge in new[] { ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom })
            {
                if (edge == entryEdge)
                    continue;
                var probe = Probe(edge, x, y, dx, dy, layout);
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

        var connection = _controlled.Controller;
        EndBeingControlled(notifyPeer: false);
        connection?.Post(leave);
    }

    /// <summary>
    /// Stops being controlled. Releases any keys or buttons the controller left held so nothing sticks.
    /// With <paramref name="onlyIf"/> set, only when that peer is the one in control.
    /// </summary>
    private void EndBeingControlled(bool notifyPeer, IPeerLink? onlyIf = null)
    {
        var entryEdge = _controlled.EntryEdge;
        if (!_controlled.TryEnd(onlyIf, out var connection))
            return;

        _edgeOvershoot = 0;
        _desktopPollTimer?.Dispose();
        _desktopPollTimer = null;
        _localBlockReason = InputBlockReason.None;

        if (notifyPeer && connection != null)
        {
            connection.Post(new CursorLeaveMessage
            {
                ExitEdge = entryEdge,
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
        var connection = _controlled.Controller;
        if (!_controlled.IsActive || connection == null)
            return;

        InputBlockReason reason;
        try
        {
            // Through the desktop service input lands on the secure desktop and elevated windows too.
            reason = _injector.ReachesSecureDesktop ? InputBlockReason.None
                : InputSimulator.IsSecureDesktopActive() ? InputBlockReason.SecureDesktop
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
        foreach (var connection in _registry.Snapshot())
        {
            if (connection != except)
                connection.Post(message);
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
                offer.RetiredTicks = offer.LastUsedTicks = Environment.TickCount64;
            }
            return id;
        }
    }

    private void OnTransferRequest(object? sender, ProtocolMessage message)
    {
        if (sender is not PeerConnection connection || message is not FileRequestMessage request)
            return;

        var reply = new FileChunkMessage
        {
            OfferId = request.OfferId,
            EntryIndex = request.EntryIndex,
            Offset = request.Offset
        };

        // Reading a 1 MB chunk from disk, or pulling it from another peer, must not run on the receive
        // thread (it also answers pings). One request per connection is served at a time, in order;
        // a client only ever has one outstanding, so a full queue means a misbehaving peer.
        SerialWorkQueue? queue;
        lock (_fileLock)
        {
            _transferQueues.TryGetValue(connection, out queue);
        }
        if (queue == null || !queue.TryEnqueue(() => ServeTransferRequest(connection, request, reply)))
        {
            reply.Error = "Too many file requests at once.";
            connection.Post(reply);
        }
    }

    private void ServeTransferRequest(PeerConnection connection, FileRequestMessage request, FileChunkMessage reply)
    {
        var length = Math.Clamp(request.Length, 0, FileTransferClient.ChunkSize);
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

        // The offer belongs to one of our other peers: pull the chunk from there and pass it on.
        try
        {
            reply.Data = relay.Fetch(request.OfferId, request.EntryIndex, request.Offset, length);
        }
        catch (Exception ex)
        {
            reply.Error = ex.Message;
        }
        connection.Post(reply);
    }

    // Paste side: an offer from a peer becomes virtual files on our clipboard, fetched on demand.

    private void HandleRemoteFileOffer(FileOfferMessage offer, PeerConnection connection)
    {
        if (!_settings.Clipboard.Enabled || !_settings.Clipboard.SyncFiles || offer.Entries.Count == 0)
            return;

        // The names become paths when Explorer pastes them; refuse anything that could land outside
        // the folder being pasted into.
        var problem = FileOfferValidator.Validate(offer.Entries);
        if (problem != null)
        {
            SimpleLogger.Log("Files", $"Ignoring file offer from {connection.PeerName}: {problem}");
            return;
        }

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
        _clipboardManager.SetVirtualFiles(offer, (index, offset, length) =>
        {
            // A paste still copying keeps the offer alive even after the clipboard moved on.
            lock (_fileLock)
            {
                if (_remoteOffers.TryGetValue(offerId, out var held))
                    held.LastUsedTicks = Environment.TickCount64;
            }
            return client.Fetch(offerId, index, offset, length);
        });

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
        if (_remoteOffers.TryGetValue(offerId, out var offer) && !offer.Retired)
        {
            offer.Retired = true;
            offer.RetiredTicks = offer.LastUsedTicks = Environment.TickCount64;
        }
        if (_currentRemoteOfferId == offerId)
            _currentRemoteOfferId = null;
    }

    /// <summary>
    /// Drops retired offers nobody has read from for the grace period, and any retired longer than the
    /// absolute limit however busy: a paste that keeps reading for half an hour after the clipboard
    /// moved on is stuck, not copying.
    /// </summary>
    private void SweepRetiredOffers()
    {
        var clients = new List<FileTransferClient>();
        var now = Environment.TickCount64;
        var idleCutoff = now - (long)OfferGrace.TotalMilliseconds;
        var lifetimeCutoff = now - (long)OfferMaxRetiredLifetime.TotalMilliseconds;
        lock (_fileLock)
        {
            foreach (var (id, offer) in _localOffers.ToList())
            {
                if (offer.Retired && (offer.LastUsedTicks < idleCutoff || offer.RetiredTicks < lifetimeCutoff))
                    _localOffers.Remove(id);
            }
            foreach (var (id, offer) in _remoteOffers.ToList())
            {
                if (offer.Retired && (offer.LastUsedTicks < idleCutoff || offer.RetiredTicks < lifetimeCutoff))
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
            _transferQueues.Clear();
        }

        _desktopPollTimer?.Dispose();
        _sessionMonitor.Dispose();
        _powerMonitor.Dispose();
        _powerFollower.Dispose();
        _serviceInjector?.Dispose();
        _rawMouse.Dispose();
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
        _clipboardManager.Dispose();
        _discovery.Dispose();
        _listener.Dispose();

        // Queued cursor work dies with the window; never leave the pointer hidden.
        if (_cursorHidden)
            InputSimulator.RestoreSystemCursor();
        _uiQueue.Dispose();
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

    /// <summary>Why the test failed, when it did.</summary>
    public PeerFailureKind? FailureKind { get; set; }

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
