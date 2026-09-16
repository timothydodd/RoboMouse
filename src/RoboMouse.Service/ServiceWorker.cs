using System.Runtime.Versioning;
using RoboMouse.Contracts;

namespace RoboMouse.Service;

/// <summary>
/// The running service: watches the active desktop and hosts the control pipe. Phase 1 wires these
/// together and reports desktop changes to a connected app; launching a per-desktop input helper and
/// relaying input across it is Phase 3 (see plans/uac-service.md), marked with TODOs below.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceWorker : IDisposable
{
    private readonly DesktopMonitor _desktop = new();
    private readonly ControlPipeServer _pipe;
    private PipeConnection? _app;

    public ServiceWorker()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "RoboMouse.App.exe");
        _pipe = new ControlPipeServer(appPath);
        _pipe.ClientConnected += OnAppConnectedAsync;
        _desktop.Changed += OnDesktopChanged;
    }

    public void Start()
    {
        Log.Write($"Service starting; input desktop is '{_desktop.Current.DesktopName}'");
        _desktop.Start();
        _pipe.Start();
    }

    private async Task OnAppConnectedAsync(PipeConnection connection, CancellationToken ct)
    {
        Log.Write("App connected to the control pipe");
        _app = connection;
        try
        {
            await connection.SendAsync(PipeMessage.Hello(), ct).ConfigureAwait(false);
            // Tell the app where input is going right now.
            var state = _desktop.Current;
            await connection.SendAsync(PipeMessage.DesktopChanged(state.IsSecure, state.DesktopName), ct).ConfigureAwait(false);

            while (connection.IsConnected && !ct.IsCancellationRequested)
            {
                var message = await connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (message is null)
                    break;
                HandleAppMessage(message.Value);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"App connection error: {ex.Message}"); }
        finally
        {
            _app = null;
            Log.Write("App disconnected");
        }
    }

    private void HandleAppMessage(PipeMessage message)
    {
        switch (message.Opcode)
        {
            case PipeOpcode.Hello:
                Log.Write($"App hello, protocol {message.ReadHelloVersion()}");
                break;
            // TODO Phase 3: BeginControlling/EndControlling/Inject*/BeginControlled/EndControlled
            //   relay these to the helper on the active desktop.
            default:
                Log.Write($"App message {message.Opcode} (not handled yet)");
                break;
        }
    }

    private void OnDesktopChanged(DesktopMonitor.DesktopState state)
    {
        Log.Write($"Input desktop is now '{state.DesktopName}' (session {state.SessionId}, secure={state.IsSecure})");
        // TODO Phase 3: ensure a helper is running on this desktop (CreateProcessAsUser with the SYSTEM
        //   token bound to winsta0\<desktop>), and re-point routing at it.
        var app = _app;
        if (app is { IsConnected: true })
            _ = app.SendAsync(PipeMessage.DesktopChanged(state.IsSecure, state.DesktopName));
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _desktop.Dispose();
        _app?.Dispose();
    }
}
