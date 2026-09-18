using System.Runtime.Versioning;
using RoboMouse.Contracts;

namespace RoboMouse.Service;

/// <summary>
/// The running service: hosts the control pipe and, while the app is connected, keeps an injection
/// helper alive in the console session and relays the app's injection commands to it. The helper
/// only exists while an app is connected, so nothing SYSTEM-level sits in the user's session when
/// RoboMouse is not running. See plans/uac-service.md for the security boundary.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceWorker : IDisposable
{
    private readonly DesktopMonitor _desktop = new();
    private readonly ControlPipeServer _pipe;
    private PipeConnection? _app;
    private HelperHost? _helper;

    /// <param name="appPath">Path of the app allowed to connect; defaults to RoboMouse.App.exe beside the service.</param>
    /// <param name="packageFamily">Package family name of the Store app, when it may connect too.</param>
    public ServiceWorker(string? appPath, string? packageFamily)
    {
        appPath = Path.GetFullPath(appPath ?? Path.Combine(AppContext.BaseDirectory, "RoboMouse.App.exe"));
        Log.Write($"Accepting the app at '{appPath}'" + (packageFamily != null ? $" or package family '{packageFamily}'" : ""));
        _pipe = new ControlPipeServer(appPath, packageFamily);
        _pipe.ClientConnected += OnAppConnectedAsync;
        _desktop.Changed += OnDesktopChanged;
    }

    public void Start()
    {
        _desktop.Start();
        _pipe.Start();
        Log.Write("Service started");
    }

    private async Task OnAppConnectedAsync(PipeConnection connection, uint appSession, CancellationToken ct)
    {
        Log.Write($"App connected to the control pipe from session {appSession}");
        _app = connection;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var helperLoop = Task.Run(() => KeepHelperAliveAsync(connection, appSession, session.Token));
        try
        {
            await connection.SendAsync(PipeMessage.Hello(), ct).ConfigureAwait(false);
            var state = _desktop.Current;
            await connection.SendAsync(PipeMessage.DesktopChanged(state.IsSecure, state.DesktopName), ct).ConfigureAwait(false);

            while (connection.IsConnected && !ct.IsCancellationRequested)
            {
                var message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (message is null)
                    break;
                if (!await HandleAppMessageAsync(message.Value, ct).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"App connection error: {ex.Message}"); }
        finally
        {
            _app = null;
            session.Cancel();
            _helper?.Stop();
            try { await helperLoop.ConfigureAwait(false); } catch { }
            Log.Write("App disconnected");
        }
    }

    /// <summary>Relays one app message. Returns false to drop the connection.</summary>
    private async Task<bool> HandleAppMessageAsync(PipeMessage message, CancellationToken ct)
    {
        switch (message.Opcode)
        {
            case PipeOpcode.Hello:
                if (message.ReadHelloVersion() == PipeNames.ProtocolVersion)
                    return true;
                Log.Write($"App speaks pipe protocol {message.ReadHelloVersion()}, expected {PipeNames.ProtocolVersion}; disconnecting");
                return false;

            // Forwarded in arrival order, so a cursor query reflects every move sent before it.
            case PipeOpcode.InjectMotion:
            case PipeOpcode.InjectButton:
            case PipeOpcode.InjectKey:
            case PipeOpcode.MoveTo:
            case PipeOpcode.QueryCursor:
                var helper = _helper;
                if (helper != null)
                {
                    try { await helper.SendAsync(message, ct).ConfigureAwait(false); }
                    catch (IOException) { helper.Stop(); }
                }
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Runs a helper in the app's own session for as long as the app stays connected, restarting it if
    /// it dies. Input from this app must never reach another user's session, so while a different
    /// session owns the console (fast user switching) there is no helper at all.
    /// </summary>
    private async Task KeepHelperAliveAsync(PipeConnection app, uint sessionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (ServiceNative.WTSGetActiveConsoleSessionId() == sessionId)
            {
                using var helper = new HelperHost(sessionId);
                helper.Ready += () => Notify(app, new PipeMessage(PipeOpcode.HelperReady));
                helper.MessageReceived += message => Notify(app, message);
                _helper = helper;
                try
                {
                    await helper.RunAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Write($"Helper failed: {ex.Message}");
                }
                _helper = null;
                if (!ct.IsCancellationRequested)
                {
                    Log.Write("Helper exited");
                    Notify(app, new PipeMessage(PipeOpcode.HelperLost));
                }
            }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static void Notify(PipeConnection app, PipeMessage message)
    {
        if (!app.IsConnected)
            return;
        _ = app.SendAsync(message).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnDesktopChanged(DesktopMonitor.DesktopState state)
    {
        Log.Write($"Input desktop is now '{state.DesktopName}' (session {state.SessionId}, secure={state.IsSecure})");

        // Fast user switching: the app's session no longer owns the console, so stop injecting for it.
        var helper = _helper;
        if (helper != null && helper.SessionId != state.SessionId)
            helper.Stop();

        var app = _app;
        if (app != null)
            Notify(app, PipeMessage.DesktopChanged(state.IsSecure, state.DesktopName));
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _desktop.Dispose();
        _helper?.Dispose();
        _app?.Dispose();
    }
}
