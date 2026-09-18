using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RoboMouse.Contracts;

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
internal sealed class HelperHost : IDisposable
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

    /// <summary>Starts the helper and relays its messages until it exits or <see cref="Stop"/> is called.</summary>
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

            if (!ControlPipeServer.TryGetClientProcessId(server, out var clientPid) || clientPid != (uint)_process.Id)
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
                    case PipeOpcode.CursorPosition:
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
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: PipeNames.BufferSize, outBufferSize: PipeNames.BufferSize, security);
    }

    public void Dispose()
    {
        Stop();
        KillProcess();
        _cts.Dispose();
    }
}

/// <summary>Starts a process in another session under this process's identity.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class HelperLauncher
{
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint CREATE_NO_WINDOW = 0x08000000;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public char* lpReserved;
        public char* lpDesktop;
        public char* lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public byte* lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(nint token, int tokenInformationClass, void* tokenInformation, int length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUserW(
        nint token, char* applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        char* currentDirectory, STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);
}
