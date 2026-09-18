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
    private readonly string? _expectedPackageFamily;
    private Task? _loop;

    /// <summary>
    /// Raised with a connected, verified app connection and the session the app runs in. The handler
    /// owns the connection until it closes.
    /// </summary>
    public event Func<PipeConnection, uint, CancellationToken, Task>? ClientConnected;

    /// <param name="expectedAppPath">Full path of the directly installed RoboMouse.App.exe.</param>
    /// <param name="expectedPackageFamily">Package family name of the Store app, when that is allowed too.</param>
    public ControlPipeServer(string expectedAppPath, string? expectedPackageFamily)
    {
        _expectedAppPath = expectedAppPath;
        _expectedPackageFamily = expectedPackageFamily;
    }

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

                if (!IsCallerAllowed(server, out var appSession))
                {
                    Log.Write("Rejected a pipe client that is not the app in the console session");
                    server.Disconnect();
                    continue;
                }

                using var connection = new PipeConnection(server);
                var handler = ClientConnected;
                if (handler != null)
                    await handler(connection, appSession, ct).ConfigureAwait(false);
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

    /// <summary>
    /// The connecting process must be RoboMouse.App in the active console session: either the exe at the
    /// installed path, or (for the Store app, which lives under WindowsApps) RoboMouse.App.exe running
    /// with our package identity. Neither location is writable by a normal user.
    /// </summary>
    private bool IsCallerAllowed(NamedPipeServerStream server, out uint appSession)
    {
        appSession = 0;
        try
        {
            if (!TryGetClientProcessId(server, out var pid))
                return false;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            appSession = (uint)process.SessionId;
            if (appSession != ServiceNative.WTSGetActiveConsoleSessionId())
                return false;

            var path = process.MainModule?.FileName;
            if (path == null)
                return false;
            if (string.Equals(path, _expectedAppPath, StringComparison.OrdinalIgnoreCase))
                return true;
            Log.Write($"Pipe client is '{path}', expected '{_expectedAppPath}'");

            return _expectedPackageFamily != null
                && string.Equals(Path.GetFileName(path), "RoboMouse.App.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(GetPackageFamily(pid), _expectedPackageFamily, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Write($"Caller check failed: {ex.Message}");
            return false;
        }
    }

    public static bool TryGetClientProcessId(NamedPipeServerStream server, out uint pid) =>
        GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out pid) && pid != 0;

    private static unsafe string? GetPackageFamily(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0)
            return null;
        try
        {
            var buffer = stackalloc char[256];
            uint length = 256;
            return GetPackageFamilyName(handle, &length, buffer) == 0 ? new string(buffer) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFamilyName(nint process, uint* packageFamilyNameLength, char* packageFamilyName);

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
