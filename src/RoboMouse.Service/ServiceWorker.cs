using System.Diagnostics;
using System.Runtime.Versioning;
using RoboMouse.Contracts;

namespace RoboMouse.Service;

/// <summary>One injection helper process as the worker sees it; <see cref="HelperHost"/> in production.</summary>
internal interface IInjectionHelper : IDisposable
{
    uint SessionId { get; }

    /// <summary>Raised when the helper has attached and commands sent from now on will be applied.</summary>
    event Action? Ready;

    /// <summary>Messages from the helper that belong to the app (cursor positions).</summary>
    event Action<PipeMessage>? MessageReceived;

    /// <summary>Runs the helper until it exits or <see cref="Stop"/> is called.</summary>
    Task RunAsync(CancellationToken ct);

    Task SendAsync(PipeMessage message, CancellationToken ct);

    void Stop();
}

/// <summary>
/// The running service: hosts the control pipe and, while the app is connected, keeps an injection
/// helper alive in the console session and relays the app's injection commands to it. The helper
/// only exists while an app is connected, so nothing SYSTEM-level sits in the user's session when
/// RoboMouse is not running. See plans/uac-service.md for the security boundary.
/// </summary>
internal sealed class ServiceWorker : IDisposable
{
    private readonly ControlPipeServer? _pipe;
    private readonly SessionMonitor? _sessions;
    private readonly Func<uint, IInjectionHelper> _createHelper;
    private readonly Func<uint> _consoleSession;
    private volatile AppSession? _current;

    /// <summary>First wait before restarting a helper that died; doubles up to <see cref="HelperRetryMax"/>.</summary>
    internal TimeSpan HelperRetryMin { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan HelperRetryMax { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>A helper that was ready for this long counts as healthy, so the next restart is quick again.</summary>
    internal TimeSpan HelperHealthyAfter { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How often to look again while another session owns the console.</summary>
    internal TimeSpan SessionPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <param name="appPath">Path of the app allowed to connect; defaults to RoboMouse.App.exe beside the service.</param>
    /// <param name="packageFamily">Package family name of the Store app, when it may connect too.</param>
    [SupportedOSPlatform("windows")]
    public ServiceWorker(string? appPath, string? packageFamily)
        : this(session => new HelperHost(session), ServiceNative.WTSGetActiveConsoleSessionId)
    {
        appPath = Path.GetFullPath(appPath ?? Path.Combine(AppContext.BaseDirectory, CallerPolicy.AppExeName));
        Log.Write($"Accepting the app at '{appPath}'" + (packageFamily != null ? $" or package family '{packageFamily}'" : ""));

        var signatures = new AuthenticodeReader();
        var serviceSigner = new Lazy<string?>(() =>
        {
            var signer = Environment.ProcessPath is { } self ? signatures.GetTrustedSigner(self) : null;
            Log.Write(signer != null
                ? $"Service is signed by '{signer}'; the app must be signed by the same publisher"
                : "Service exe is not signed (a dev build); the app is checked by path only");
            return signer;
        });

        _pipe = new ControlPipeServer(new CallerPolicy(appPath, packageFamily, serviceSigner, new Win32ProcessInspector(), signatures));
        _pipe.ClientConnected += HandleAppAsync;
        _sessions = new SessionMonitor(_consoleSession);
        _sessions.Changed += OnConsoleSessionChanged;
    }

    /// <summary>For tests: no pipe server and no session polling; drive <see cref="HandleAppAsync"/> directly.</summary>
    internal ServiceWorker(Func<uint, IInjectionHelper> createHelper, Func<uint> consoleSession)
    {
        _createHelper = createHelper;
        _consoleSession = consoleSession;
    }

    public void Start()
    {
        _sessions?.Start();
        _pipe?.Start();
        Log.Write("Service started");
    }

    /// <summary>
    /// Serves one verified app connection until it closes. The app must open with a Hello of this
    /// protocol version; nothing is relayed and no helper is started before that.
    /// </summary>
    internal async Task HandleAppAsync(PipeConnection connection, uint appSession, CancellationToken ct)
    {
        Log.Write($"App connected to the control pipe from session {appSession}");
        using var session = new AppSession(connection, appSession, ct);
        _current = session;
        var token = session.Token;
        Task? helperLoop = null;
        try
        {
            await connection.SendAsync(PipeMessage.Hello(), token).ConfigureAwait(false);

            var first = await connection.ReceiveAsync(token).ConfigureAwait(false);
            if (first is null)
                return;
            if (first.Value.Opcode != PipeOpcode.Hello || !first.Value.IsWellFormed)
            {
                Log.Write($"App sent {first.Value.Opcode} before Hello; disconnecting");
                return;
            }
            if (first.Value.ReadHelloVersion() != PipeNames.ProtocolVersion)
            {
                Log.Write($"App speaks pipe protocol {first.Value.ReadHelloVersion()}, expected {PipeNames.ProtocolVersion}; disconnecting");
                return;
            }

            helperLoop = Task.Run(() => KeepHelperAliveAsync(session), CancellationToken.None);

            while (connection.IsConnected && !token.IsCancellationRequested)
            {
                var message = await connection.ReceiveAsync(token).ConfigureAwait(false);
                if (message is null)
                    break;
                await RelayAsync(session, message.Value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"App connection error: {ex.Message}"); }
        finally
        {
            if (_current == session)
                _current = null;
            session.Cancel();
            session.Helper?.Stop();
            if (helperLoop != null)
            {
                try { await helperLoop.ConfigureAwait(false); } catch { }
            }
            Log.Write("App disconnected");
        }
    }

    /// <summary>Relays one app message to the helper, in arrival order, so a cursor query reflects every move sent before it.</summary>
    private static async Task RelayAsync(AppSession session, PipeMessage message)
    {
        switch (message.Opcode)
        {
            case PipeOpcode.Hello:
                return;

            case PipeOpcode.InjectMotion:
            case PipeOpcode.InjectButton:
            case PipeOpcode.InjectKey:
            case PipeOpcode.MoveTo:
            case PipeOpcode.QueryCursor:
                if (!message.IsWellFormed)
                {
                    Log.WriteLimited("bad-app-message", $"Dropped a malformed {message.Opcode} from the app ({message.Payload.Length} bytes)");
                    return;
                }

                await session.Gate.WaitAsync(session.Token).ConfigureAwait(false);
                try
                {
                    session.Held.Track(message);
                    var helper = session.Helper;
                    if (helper != null)
                    {
                        try { await helper.SendAsync(message, session.Token).ConfigureAwait(false); }
                        catch (IOException) { helper.Stop(); }
                    }
                }
                finally
                {
                    session.Gate.Release();
                }
                return;

            default:
                Log.WriteLimited("unknown-app-message", $"Ignored opcode {message.Opcode} from the app");
                return;
        }
    }

    /// <summary>
    /// Runs a helper in the app's own session for as long as the app stays connected, restarting it
    /// with backoff if it dies. Input from this app must never reach another user's session, so while a
    /// different session owns the console (fast user switching) there is no helper at all.
    /// </summary>
    private async Task KeepHelperAliveAsync(AppSession session)
    {
        var token = session.Token;
        var delay = HelperRetryMin;
        while (!token.IsCancellationRequested)
        {
            if (_consoleSession() != session.SessionId)
            {
                try { await Task.Delay(SessionPollInterval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var helper = _createHelper(session.SessionId);
            long readyAt = 0;
            helper.Ready += () =>
            {
                readyAt = Stopwatch.GetTimestamp();
                _ = OnHelperReadyAsync(session, helper);
            };
            helper.MessageReceived += message => Notify(session.Connection, message);
            session.Helper = helper;
            try
            {
                await helper.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (FileNotFoundException ex)
            {
                // Retrying cannot help until someone repairs the install.
                Log.Write($"Helper not started: {ex.Message} ({ex.FileName}); not retrying while this app stays connected");
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"Helper failed: {ex.Message}");
            }
            finally
            {
                session.Helper = null;
                helper.Dispose();
            }

            if (token.IsCancellationRequested)
                return;
            Log.Write("Helper exited");
            Notify(session.Connection, new PipeMessage(PipeOpcode.HelperLost));

            if (readyAt != 0 && Stopwatch.GetElapsedTime(readyAt) >= HelperHealthyAfter)
                delay = HelperRetryMin;
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, HelperRetryMax.Ticks));
        }
    }

    /// <summary>
    /// A new helper first lets go of whatever the app still has pressed through the service (a helper
    /// that crashed could not), then the app is told injection lands again.
    /// </summary>
    private static async Task OnHelperReadyAsync(AppSession session, IInjectionHelper helper)
    {
        try
        {
            await session.Gate.WaitAsync(session.Token).ConfigureAwait(false);
            try
            {
                var releases = session.Held.TakeReleases();
                if (releases.Count > 0)
                    Log.Write($"Releasing {releases.Count} key(s)/button(s) left down by the previous helper");
                foreach (var release in releases)
                    await helper.SendAsync(release, session.Token).ConfigureAwait(false);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Write($"Could not release held input on the new helper: {ex.Message}"); }

        Notify(session.Connection, new PipeMessage(PipeOpcode.HelperReady));
    }

    private static void Notify(PipeConnection app, PipeMessage message)
    {
        if (!app.IsConnected)
            return;
        _ = app.SendAsync(message).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Fast user switching: the connected app's session no longer owns the console, so drop it. Its
    /// helper stops with it, and the pipe (one instance) is free for the new console user's app.
    /// </summary>
    internal void OnConsoleSessionChanged(uint consoleSession)
    {
        var session = _current;
        if (session == null || session.SessionId == consoleSession)
            return;
        Log.Write($"Console session is now {consoleSession}; dropping the app connection from session {session.SessionId}");
        session.Cancel();
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _sessions?.Dispose();
        _current?.Cancel();
    }

    /// <summary>State for one app connection.</summary>
    private sealed class AppSession : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public AppSession(PipeConnection connection, uint sessionId, CancellationToken ct)
        {
            Connection = connection;
            SessionId = sessionId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Token = _cts.Token;
        }

        public PipeConnection Connection { get; }
        public uint SessionId { get; }
        public CancellationToken Token { get; }

        /// <summary>What the app has pressed through the service; guarded by <see cref="Gate"/>.</summary>
        public HeldPipeInput Held { get; } = new();

        /// <summary>Serializes relaying with the release burst sent to a new helper, so order holds.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile IInjectionHelper? Helper;

        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _cts.Dispose();
            Gate.Dispose();
        }
    }
}
