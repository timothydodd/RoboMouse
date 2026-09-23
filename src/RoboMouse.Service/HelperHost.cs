using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RoboMouse.Contracts;
using static RoboMouse.Service.ServiceNative;

namespace RoboMouse.Service;

/// <summary>
/// One injection helper, running in a given session under this process's own identity (SYSTEM when
/// run by the SCM). The service lives in session 0 and cannot inject into the interactive session
/// itself, so it starts the helper there with a copy of its token re-targeted at that session. The
/// helper then follows the input desktop on its own, so one helper covers the normal desktop, UAC
/// prompts and the lock screen without a respawn at the moment input is needed.
///
/// The pipe to the helper has a random name, a single instance, and an ACL that admits only this
/// process's identity; the connecting process must also be the one that was just started.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HelperHost : IInjectionHelper
{
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private PipeConnection? _pipe;
    private volatile bool _ready;

    public uint SessionId { get; }

    /// <summary>True once the helper has attached and reported ready.</summary>
    public bool IsReady => _ready;

    /// <summary>Raised when the helper reports ready.</summary>
    public event Action? Ready;

    /// <summary>Replies from the helper that belong to the app (cursor positions).</summary>
    public event Action<PipeMessage>? MessageReceived;

    public HelperHost(uint sessionId) => SessionId = sessionId;

    /// <summary>
    /// Starts the helper and relays its messages until it exits or <see cref="Stop"/> is called.
    /// Throws <see cref="FileNotFoundException"/> when the helper exe is missing.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        var pipeName = PipeNames.HelperPrefix + Guid.NewGuid().ToString("N");
        using var server = CreateServer(pipeName);

        var helperPath = Path.Combine(AppContext.BaseDirectory, "RoboMouse.Helper.exe");
        _process = HelperLauncher.Start(helperPath, $"--pipe {pipeName}", SessionId);
        Log.Write($"Started helper pid {_process.Id} in session {SessionId}");

        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }

            if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var clientPid)
                || clientPid != (uint)_process.Id)
            {
                Log.Write($"Helper pipe was connected by pid {clientPid}, not the helper; dropping it");
                return;
            }

            _pipe = new PipeConnection(server);
            await _pipe.SendAsync(PipeMessage.Hello(), token).ConfigureAwait(false);

            while (_pipe.IsConnected && !token.IsCancellationRequested)
            {
                var message = await _pipe.ReceiveAsync(token).ConfigureAwait(false);
                if (message is null)
                    break;

                switch (message.Value.Opcode)
                {
                    case PipeOpcode.Hello:
                        if (message.Value.ReadHelloVersion() != PipeNames.ProtocolVersion)
                        {
                            Log.Write($"Helper speaks pipe protocol {message.Value.ReadHelloVersion()}, expected {PipeNames.ProtocolVersion}");
                            return;
                        }
                        break;
                    case PipeOpcode.HelperReady:
                        _ready = true;
                        Ready?.Invoke();
                        break;
                    case PipeOpcode.CursorPosition when message.Value.IsWellFormed:
                        MessageReceived?.Invoke(message.Value);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _ready = false;
            _pipe = null;
            KillProcess();
        }
    }

    /// <summary>Forwards an injection command. Dropped when the helper is not attached.</summary>
    public Task SendAsync(PipeMessage message, CancellationToken ct)
    {
        var pipe = _pipe;
        return _ready && pipe != null ? pipe.SendAsync(message, ct) : Task.CompletedTask;
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void KillProcess()
    {
        var process = _process;
        _process = null;
        if (process == null)
            return;
        try
        {
            // Closing the pipe makes the helper release held input and exit; only force it if it hangs.
            if (!process.WaitForExit(1000))
                process.Kill();
        }
        catch { }
        process.Dispose();
    }

    private static NamedPipeServerStream CreateServer(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: PipeNames.BufferSize, outBufferSize: PipeNames.BufferSize, security);
    }

    public void Dispose()
    {
        Stop();
        KillProcess();
        _cts.Dispose();
    }
}

/// <summary>
/// Starts a process in another session under this process's identity, minus the privileges an
/// injection helper never needs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class HelperLauncher
{
    /// <summary>
    /// Removed from the helper's token. SYSTEM identity alone is what lets it open the Winlogon desktop
    /// and inject there; none of these are used for that, and each would make a compromised helper a
    /// far bigger problem.
    /// </summary>
    private static readonly string[] DroppedPrivileges =
    {
        "SeDebugPrivilege", "SeTcbPrivilege", "SeImpersonatePrivilege", "SeLoadDriverPrivilege",
        "SeBackupPrivilege", "SeRestorePrivilege", "SeTakeOwnershipPrivilege",
        "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege", "SeCreateTokenPrivilege",
        "SeSecurityPrivilege", "SeRelabelPrivilege", "SeManageVolumePrivilege", "SeSystemEnvironmentPrivilege"
    };

    public static Process Start(string exePath, string arguments, uint sessionId)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("The helper executable is missing next to the service.", exePath);

        // Console mode in the target session already (testing): an ordinary child process will do.
        if ((uint)Process.GetCurrentProcess().SessionId == sessionId)
        {
            return Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            })!;
        }

        nint own = 0, primary = 0;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out own))
                throw new InvalidOperationException($"OpenProcessToken failed ({Marshal.GetLastPInvokeError()})");
            if (!DuplicateTokenEx(own, TOKEN_ALL_ACCESS, 0, SecurityImpersonation, TokenPrimary, out primary))
                throw new InvalidOperationException($"DuplicateTokenEx failed ({Marshal.GetLastPInvokeError()})");
            // Needs SeTcbPrivilege, which LocalSystem has. This is the step that fails when not run as SYSTEM.
            if (!SetTokenInformation(primary, TokenSessionId, &sessionId, sizeof(uint)))
                throw new InvalidOperationException($"SetTokenInformation(TokenSessionId) failed ({Marshal.GetLastPInvokeError()}); the service must run as LocalSystem");
            DropPrivileges(primary);

            var commandLine = $"\"{exePath}\" {arguments}\0".ToCharArray();
            var desktop = "winsta0\\default\0".ToCharArray();
            var directory = (Path.GetDirectoryName(exePath)! + "\0").ToCharArray();

            fixed (char* pCommandLine = commandLine, pDesktop = desktop, pDirectory = directory)
            {
                var startup = new STARTUPINFOW { cb = sizeof(STARTUPINFOW), lpDesktop = pDesktop };
                PROCESS_INFORMATION info;
                if (!CreateProcessAsUserW(primary, null, pCommandLine, 0, 0, false, CREATE_NO_WINDOW, 0, pDirectory, &startup, &info))
                    throw new InvalidOperationException($"CreateProcessAsUser failed ({Marshal.GetLastPInvokeError()})");

                try
                {
                    return Process.GetProcessById((int)info.dwProcessId);
                }
                finally
                {
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                }
            }
        }
        finally
        {
            if (primary != 0) CloseHandle(primary);
            if (own != 0) CloseHandle(own);
        }
    }

    /// <summary>
    /// Removes each privilege outright (not just disables it, which the process could undo). One call
    /// per privilege: the service may hold only some of them (<c>sc privs</c>), and a missing one must
    /// not stop the rest from being removed.
    /// </summary>
    private static void DropPrivileges(nint token)
    {
        foreach (var name in DroppedPrivileges)
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
                continue;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_REMOVED };
            if (!AdjustTokenPrivileges(token, false, &privileges, (uint)sizeof(TOKEN_PRIVILEGES), 0, 0))
                Log.Write($"Could not remove {name} from the helper token ({Marshal.GetLastPInvokeError()})");
        }
    }
}
