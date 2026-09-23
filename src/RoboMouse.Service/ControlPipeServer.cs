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
/// the security boundary in plans/uac-service.md). One app connection at a time, and the pipe name is
/// held for as long as the service runs: each instance is replaced only after the next exists.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ControlPipeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CallerPolicy _policy;
    private readonly Func<bool, NamedPipeServerStream> _createServer;
    private Task? _loop;

    /// <summary>
    /// Raised with a connected, verified app connection and the session the app runs in. The handler
    /// owns the connection until it closes.
    /// </summary>
    public event Func<PipeConnection, uint, CancellationToken, Task>? ClientConnected;

    public ControlPipeServer(CallerPolicy policy) : this(policy, CreateServer) { }

    /// <summary>For tests: <paramref name="createServer"/> makes each instance (argument: whether it is the first one held).</summary>
    internal ControlPipeServer(CallerPolicy policy, Func<bool, NamedPipeServerStream> createServer)
    {
        _policy = policy;
        _createServer = createServer;
    }

    public void Start()
    {
        if (_loop != null)
            return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // The instance waiting for (or serving) a client. The next one is created before this one is
        // let go (Handoff), so the name never lapses while the service runs and no other process can
        // create it in between.
        NamedPipeServerStream? server = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Holding no instance (first pass, or a handoff failed): claim the name afresh.
                    server ??= _createServer(true);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var connectedAt = DateTime.UtcNow.ToFileTimeUtc();

                    var reason = "could not identify it";
                    if (!TryIdentifyCaller(server, connectedAt, out var caller) || !_policy.IsAllowed(caller, out reason))
                    {
                        Log.WriteLimited("rejected-client", $"Rejected a control pipe client: {reason}");
                        server = Handoff(server);
                        continue;
                    }

                    var connection = new PipeConnection(server);
                    try
                    {
                        var handler = ClientConnected;
                        if (handler != null)
                            await handler(connection, caller.PipeSessionId, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        // The connection closes its stream, so it is only disposed inside the handoff,
                        // once the next instance exists.
                        if (ct.IsCancellationRequested)
                            connection.Dispose();
                        else
                            server = Handoff(server, connection);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Includes another process already owning the pipe name (FirstPipeInstance refuses it).
                    Log.WriteLimited("pipe-error", $"Control pipe error: {ex.Message}");
                    if (server != null)
                        server = Handoff(server);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
                }
            }
        }
        finally
        {
            server?.Dispose();
        }
    }

    /// <summary>
    /// Creates the next instance, then disposes <paramref name="current"/> (and the
    /// <paramref name="connection"/> over it), so there is no moment without one of ours. Returns the
    /// new instance, or null when it could not be created (the next pass then claims the name again
    /// with <c>FirstPipeInstance</c>).
    /// </summary>
    private NamedPipeServerStream? Handoff(NamedPipeServerStream current, IDisposable? connection = null)
    {
        NamedPipeServerStream? next = null;
        try
        {
            next = _createServer(false);
        }
        catch (Exception ex)
        {
            Log.WriteLimited("pipe-handoff", $"Could not create the next control pipe instance: {ex.Message}");
        }
        try { connection?.Dispose(); } catch { }
        try { current.Dispose(); } catch { }
        return next;
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

    /// <param name="firstInstance">
    /// True when this service holds no instance: then the name must not exist yet
    /// (<c>FirstPipeInstance</c>). False for the next instance created while the current one is still
    /// open, which is what keeps the name ours between connections.
    /// </param>
    private static NamedPipeServerStream CreateServer(bool firstInstance)
    {
        // Allow the interactive user (to connect) and SYSTEM (us); deny everyone else. The user gets
        // no FILE_CREATE_PIPE_INSTANCE right, so only we can add instances.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // FirstPipeInstance: if some other process created the name first, fail instead of joining
        // its instances (the app would otherwise talk to whoever created it; it also checks the owner).
        // Two instances at most: the one in use and the next, which only overlap during a handoff;
        // the next is created after a connection ends, so one app is served at a time.
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            PipeNames.Control, PipeDirection.InOut, MaxInstances,
            PipeTransmissionMode.Byte, options,
            inBufferSize: PipeNames.BufferSize, outBufferSize: PipeNames.BufferSize, security);
    }

    private const int MaxInstances = 2;

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
