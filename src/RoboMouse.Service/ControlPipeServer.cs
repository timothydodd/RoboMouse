using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RoboMouse.Contracts;

namespace RoboMouse.Service;

/// <summary>
/// Hosts the control pipe the normal-user app connects to. The pipe is ACL'd to the interactive user
/// and SYSTEM; each accepted connection is additionally checked to be the installed RoboMouse.App
/// running in the active console session before any message is honoured (see the security boundary in
/// plans/uac-service.md). One app connection at a time.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ControlPipeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _expectedAppPath;
    private Task? _loop;

    /// <summary>Raised with a connected, verified app connection. The handler owns it until it closes.</summary>
    public event Func<PipeConnection, CancellationToken, Task>? ClientConnected;

    public ControlPipeServer(string expectedAppPath) => _expectedAppPath = expectedAppPath;

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

                if (!IsCallerAllowed(server))
                {
                    Log.Write("Rejected a pipe client that is not the app in the console session");
                    server.Disconnect();
                    continue;
                }

                using var connection = new PipeConnection(server);
                var handler = ClientConnected;
                if (handler != null)
                    await handler(connection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"Control pipe error: {ex.Message}");
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
            }
        }
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

        return NamedPipeServerStreamAcl.Create(
            PipeNames.Control, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, security);
    }

    /// <summary>The connecting process must be RoboMouse.App in the active console session.</summary>
    private bool IsCallerAllowed(NamedPipeServerStream server)
    {
        try
        {
            var pid = GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var clientPid)
                ? clientPid : 0u;
            if (pid == 0)
                return false;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            if (path == null || !string.Equals(path, _expectedAppPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return (uint)process.SessionId == ServiceNative.WTSGetActiveConsoleSessionId();
        }
        catch (Exception ex)
        {
            Log.Write($"Caller check failed: {ex.Message}");
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
