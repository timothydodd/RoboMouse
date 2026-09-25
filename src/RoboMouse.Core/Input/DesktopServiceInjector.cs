using System.IO.Pipes;
using System.Security.Principal;
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

    /// <summary>
    /// Commands waiting for the pipe. Past <see cref="MotionDropDepth"/> relative motion is dropped (the
    /// next move carries the cursor on anyway); a full queue sends everything else in-process. Either
    /// only happens when the service stops reading, and keeps memory bounded if it does.
    /// </summary>
    private const int QueueCapacity = 1024;
    private const int MotionDropDepth = 256;

    /// <summary>A lost reply must not stall the network thread; past this the local position is used.</summary>
    internal int CursorQueryTimeoutMs { get; set; } = 50;

    private readonly IInputInjector _local;
    private readonly string _pipeName;
    private readonly Func<NamedPipeClientStream, string?>? _verifyServer;
    private readonly object _gate = new();
    private readonly object _queryLock = new();
    private readonly ManualResetEventSlim _cursorReply = new(false);

    private CancellationTokenSource? _cts;
    private volatile Channel<PipeMessage>? _outbound;
    private volatile bool _active;
    private (int X, int Y) _cursor;
    private uint _querySequence;
    private volatile uint _awaitedSequence;
    private DesktopServiceState _state = DesktopServiceState.Off;
    private string? _lastProblem;

    public DesktopServiceInjector()
        : this(PipeNames.Control, new InProcessInjector(), DesktopServiceControl.VerifyPipeServer) { }

    /// <param name="pipeName">Control pipe to connect to.</param>
    /// <param name="local">Where input goes while the service is not ready.</param>
    /// <param name="verifyServer">
    /// Returns null when the connected pipe's server is the real service, else why not. Only tests pass
    /// null (no check): their stand-in server is not a Windows service.
    /// </param>
    /// <summary>
    /// Reads the cursor from this process; false while the secure desktop is up. Tests make it fail to
    /// exercise the helper's answer.
    /// </summary>
    internal TryGetCursor TryLocalCursor { get; init; } = InputSimulator.TryGetCursorPosition;

    internal delegate bool TryGetCursor(out int x, out int y);

    internal DesktopServiceInjector(string pipeName, IInputInjector local, Func<NamedPipeClientStream, string?>? verifyServer)
    {
        _pipeName = pipeName;
        _local = local;
        _verifyServer = verifyServer;
    }

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
            catch (Exception ex)
            {
                Report(ex is TimeoutException
                    ? "Desktop service is not answering on its pipe (not running, or busy with another client)"
                    : $"Desktop service connection failed: {ex.GetType().Name}: {ex.Message}");
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

    // Retried every few seconds, so only say it when the reason changes.
    private void Report(string problem)
    {
        if (problem != _lastProblem)
            SimpleLogger.Log("Service", problem);
        _lastProblem = problem;
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        // Identification: the service may learn who we are, but can never act as us.
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await client.ConnectAsync(1000, ct).ConfigureAwait(false);

        // Nothing is sent, not even Hello, until the other end is known to be the service.
        if (_verifyServer?.Invoke(client) is { } untrusted)
        {
            Report($"Not using the desktop service pipe: {untrusted}");
            return;
        }

        using var pipe = new PipeConnection(client);
        await pipe.SendAsync(PipeMessage.Hello(), ct).ConfigureAwait(false);
        SimpleLogger.Log("Service", "Connected to the desktop service; waiting for its helper");
        _lastProblem = null;

        // Injection calls arrive on network threads and must not block on the pipe, so they queue here
        // and one writer drains them in order.
        var channel = Channel.CreateBounded<PipeMessage>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
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
                {
                    SimpleLogger.Log("Service", _active
                        ? "Desktop service closed the connection"
                        : "Desktop service dropped the connection before its helper was ready; it rejects any exe other than the one it was installed for (see service.log)");
                    break;
                }
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
                        _outbound = channel;
                        _active = true;
                        SimpleLogger.Log("Service", "Desktop service ready; remote input goes through it");
                        SetState(DesktopServiceState.Active);
                        break;
                    case PipeOpcode.HelperLost:
                        _active = false;
                        SimpleLogger.Log("Service", "Desktop service lost its helper; injecting in-process");
                        SetState(DesktopServiceState.Connecting);
                        break;
                    case PipeOpcode.CursorPosition when message.IsWellFormed:
                        var (x, y, sequence) = message.ReadCursorPosition();
                        // A reply to a query that already timed out must not answer the next one.
                        if (sequence == _awaitedSequence)
                        {
                            _cursor = (x, y);
                            _cursorReply.Set();
                        }
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
        if (!_active || outbound == null)
            return false;
        // Drop surplus motion first: a key or click must never be the thing that gets lost.
        if (message.Opcode == PipeOpcode.InjectMotion && outbound.Reader.Count >= MotionDropDepth)
            return true;
        return outbound.Writer.TryWrite(message);
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
    /// Asked after every motion delta, so it must be cheap: GetCursorPos from this process answers at
    /// once whenever it can. Asking the helper blocks this thread for a pipe round trip behind every
    /// move still queued, which at hundreds of deltas a second backed up the receive thread and made
    /// the cursor lag and then catch up. The local answer can trail the helper's newest moves by a
    /// delta or two, which the crossing checks tolerate. GetCursorPos fails from this process while the
    /// secure desktop is up; only then does the helper answer. That query travels the same ordered path
    /// as the moves before it, and carries a sequence id so a reply that arrives after its query gave up
    /// is ignored rather than taken as the answer to the next.
    /// </summary>
    public (int X, int Y) GetCursorPosition()
    {
        if (!_active)
            return _local.GetCursorPosition();
        if (TryLocalCursor(out var x, out var y))
            return (x, y);

        lock (_queryLock)
        {
            var sequence = ++_querySequence;
            _awaitedSequence = sequence;
            _cursorReply.Reset();
            if (TrySend(PipeMessage.QueryCursor(sequence)) && _cursorReply.Wait(CursorQueryTimeoutMs))
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
