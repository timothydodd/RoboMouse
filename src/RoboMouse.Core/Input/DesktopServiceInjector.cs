using System.IO.Pipes;
using System.Threading.Channels;
using RoboMouse.Contracts;
using RoboMouse.Core.Logging;

namespace RoboMouse.Core.Input;

/// <summary>Where remote input is being applied from.</summary>
public enum DesktopServiceState
{
    /// <summary>Not in use; input is injected from this process.</summary>
    Off,
    /// <summary>Turned on but the service is not answering (not installed, stopped, or starting).</summary>
    Connecting,
    /// <summary>Connected and the helper is attached: UAC prompts and the lock screen can be driven.</summary>
    Active,
    /// <summary>The installed service speaks a different pipe protocol than this app.</summary>
    Incompatible
}

/// <summary>
/// Injects through the separately installed RoboMouse desktop service, whose helper runs as SYSTEM on
/// whichever desktop is active, so input keeps landing on UAC prompts, the lock screen and elevated
/// windows. Whenever the service is not there or not ready, every call falls straight through to
/// in-process injection, so the app behaves exactly as it does without the service.
/// </summary>
public sealed class DesktopServiceInjector : IInputInjector, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    /// <summary>A lost reply must not stall the network thread; past this the local position is used.</summary>
    internal int CursorQueryTimeoutMs { get; set; } = 50;

    private readonly InProcessInjector _local = new();
    private readonly string _pipeName;
    private readonly object _gate = new();
    private readonly object _queryLock = new();
    private readonly ManualResetEventSlim _cursorReply = new(false);

    private CancellationTokenSource? _cts;
    private volatile ChannelWriter<PipeMessage>? _outbound;
    private volatile bool _active;
    private (int X, int Y) _cursor;
    private DesktopServiceState _state = DesktopServiceState.Off;

    public DesktopServiceInjector() : this(PipeNames.Control) { }

    internal DesktopServiceInjector(string pipeName) => _pipeName = pipeName;

    public DesktopServiceState State => _state;

    /// <summary>Raised (on a background thread) when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    public bool ReachesSecureDesktop => _active;

    /// <summary>Connects to the service and keeps reconnecting while true.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _cts != null; }
        set
        {
            lock (_gate)
            {
                if (value == (_cts != null))
                    return;
                if (value)
                {
                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    SetState(DesktopServiceState.Connecting);
                    _ = Task.Run(() => RunAsync(token));
                }
                else
                {
                    _cts!.Cancel();
                    _cts = null;
                    _active = false;
                    _outbound = null;
                    SetState(DesktopServiceState.Off);
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                SimpleLogger.Log("Service", $"Desktop service connection: {ex.Message}");
            }

            _active = false;
            _outbound = null;
            if (ct.IsCancellationRequested)
                return;
            if (_state != DesktopServiceState.Incompatible)
                SetState(DesktopServiceState.Connecting);

            // An incompatible service will not fix itself in seconds; check back rarely.
            var delay = _state == DesktopServiceState.Incompatible ? TimeSpan.FromMinutes(1) : RetryDelay;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(1000, ct).ConfigureAwait(false);
        using var pipe = new PipeConnection(client);
        await pipe.SendAsync(PipeMessage.Hello(), ct).ConfigureAwait(false);

        // Injection calls arrive on network threads and must not block on the pipe, so they queue here
        // and one writer drains them in order.
        var channel = Channel.CreateUnbounded<PipeMessage>(new UnboundedChannelOptions { SingleReader = true });
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in channel.Reader.ReadAllAsync(connectionCts.Token).ConfigureAwait(false))
                    await pipe.SendAsync(message, connectionCts.Token).ConfigureAwait(false);
            }
            catch { }
            pipe.Dispose();
        });

        try
        {
            while (pipe.IsConnected)
            {
                var received = await pipe.ReceiveAsync(ct).ConfigureAwait(false);
                if (received is null)
                    break;
                var message = received.Value;

                switch (message.Opcode)
                {
                    case PipeOpcode.Hello:
                        if (message.ReadHelloVersion() != PipeNames.ProtocolVersion)
                        {
                            SimpleLogger.Log("Service", $"Desktop service speaks pipe protocol {message.ReadHelloVersion()}, this app {PipeNames.ProtocolVersion}; not using it");
                            SetState(DesktopServiceState.Incompatible);
                            return;
                        }
                        break;
                    case PipeOpcode.HelperReady:
                        _outbound = channel.Writer;
                        _active = true;
                        SimpleLogger.Log("Service", "Desktop service ready; remote input goes through it");
                        SetState(DesktopServiceState.Active);
                        break;
                    case PipeOpcode.HelperLost:
                        _active = false;
                        SimpleLogger.Log("Service", "Desktop service lost its helper; injecting in-process");
                        SetState(DesktopServiceState.Connecting);
                        break;
                    case PipeOpcode.CursorPosition:
                        _cursor = message.ReadMotion();
                        _cursorReply.Set();
                        break;
                }
            }
        }
        finally
        {
            _active = false;
            _outbound = null;
            channel.Writer.TryComplete();
            connectionCts.Cancel();
            await writer.ConfigureAwait(false);
        }
    }

    private bool TrySend(PipeMessage message)
    {
        var outbound = _outbound;
        return _active && outbound != null && outbound.TryWrite(message);
    }

    // UIPI does not apply to the SYSTEM helper, so a routed move never reports "blocked".
    public bool MoveRelative(int deltaX, int deltaY) =>
        TrySend(PipeMessage.Motion(PipeOpcode.InjectMotion, deltaX, deltaY)) || _local.MoveRelative(deltaX, deltaY);

    public void MoveTo(int x, int y)
    {
        if (!TrySend(PipeMessage.Motion(PipeOpcode.MoveTo, x, y)))
            _local.MoveTo(x, y);
    }

    public bool SimulateMouseEvent(MouseEventType eventType, int wheelDelta = 0) =>
        TrySend(PipeMessage.Button(PipeOpcode.InjectButton, (int)eventType, wheelDelta)) || _local.SimulateMouseEvent(eventType, wheelDelta);

    public bool SimulateKeyboardEvent(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended) =>
        TrySend(PipeMessage.Key((int)keyCode, scanCode, (int)eventType, isExtended)) || _local.SimulateKeyboardEvent(keyCode, scanCode, eventType, isExtended);

    /// <summary>
    /// GetCursorPos fails from this process while the secure desktop is up, so the helper answers. The
    /// query travels the same ordered path as the moves before it.
    /// </summary>
    public (int X, int Y) GetCursorPosition()
    {
        if (!_active)
            return _local.GetCursorPosition();

        lock (_queryLock)
        {
            _cursorReply.Reset();
            if (TrySend(new PipeMessage(PipeOpcode.QueryCursor)) && _cursorReply.Wait(CursorQueryTimeoutMs))
                return _cursor;
        }
        return _local.GetCursorPosition();
    }

    private void SetState(DesktopServiceState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Enabled = false;
        _cursorReply.Dispose();
    }
}
