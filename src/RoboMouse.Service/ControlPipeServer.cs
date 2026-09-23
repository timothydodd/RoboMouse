using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RoboMouse.Contracts;

namespace RoboMouse.Service;

/// <summary>
/// Hosts the control pipe the normal-user app connects to. The pipe is ACL'd to the interactive user
/// and SYSTEM; each accepted connection is additionally checked by <see cref="CallerPolicy"/> to be the
/// installed RoboMouse.App running in the active console session before any message is honoured (see
/// the security boundary in plans/uac-service.md). One app connection at a time.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ControlPipeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CallerPolicy _policy;
    private Task? _loop;

    /// <summary>
    /// Raised with a connected, verified app connection and the session the app runs in. The handler
    /// owns the connection until it closes.
    /// </summary>
    public event Func<PipeConnection, uint, CancellationToken, Task>? ClientConnected;

    public ControlPipeServer(CallerPolicy policy) => _policy = policy;

    public void Start()
    {
        if (_loop != null)
            return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connectedAt = DateTime.UtcNow.ToFileTimeUtc();

                var reason = "could not identify it";
                if (!TryIdentifyCaller(server, connectedAt, out var caller) || !_policy.IsAllowed(caller, out reason))
                {
                    Log.WriteLimited("rejected-client", $"Rejected a control pipe client: {reason}");
                    server.Disconnect();
                    continue;
                }

                using var connection = new PipeConnection(server);
                var handler = ClientConnected;
                if (handler != null)
                    await handler(connection, caller.PipeSessionId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Includes another process already owning the pipe name (FirstPipeInstance refuses it).
                Log.WriteLimited("pipe-error", $"Control pipe error: {ex.Message}");
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private static bool TryIdentifyCaller(NamedPipeServerStream server, long connectedAt, out PipeCaller caller)
    {
        caller = default;
        var handle = server.SafePipeHandle.DangerousGetHandle();
        if (!ServiceNative.GetNamedPipeClientProcessId(handle, out var pid) || pid == 0
            || !ServiceNative.GetNamedPipeClientSessionId(handle, out var session))
            return false;
        caller = new PipeCaller(pid, session, ServiceNative.WTSGetActiveConsoleSessionId(), connectedAt);
        return true;
    }

    private static NamedPipeServerStream CreateServer()
    {
        // Allow the interactive user (to connect) and SYSTEM (us); deny everyone else.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // FirstPipeInstance: if some other process created the name first, fail instead of joining
        // its instances (the app would otherwise talk to whoever created it; it also checks the owner).
        return NamedPipeServerStreamAcl.Create(
            PipeNames.Control, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: PipeNames.BufferSize, outBufferSize: PipeNames.BufferSize, security);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
