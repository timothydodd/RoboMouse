namespace RoboMouse.Service;

/// <summary>
/// An open handle on a pipe client's process. Everything is read through the one handle, so the
/// process id cannot be recycled for a different process part-way through the check.
/// </summary>
internal interface IClientProcess : IDisposable
{
    /// <summary>Terminal Services session the process runs in.</summary>
    uint SessionId { get; }

    /// <summary>Creation time as a FILETIME (UTC, 100 ns ticks since 1601).</summary>
    long CreationTime { get; }

    bool HasExited { get; }

    /// <summary>Full Win32 path of the executable, from the kernel (not the process's own, writable PEB).</summary>
    string? ImagePath { get; }

    /// <summary>Package family name when the process has package identity, else null.</summary>
    string? PackageFamilyName { get; }

    /// <summary>Install folder of the process's package, else null.</summary>
    string? PackageInstallPath { get; }
}

/// <summary>Opens client processes; the seam that keeps <see cref="CallerPolicy"/> testable off Windows.</summary>
internal interface IProcessInspector
{
    /// <summary>Opens <paramref name="pid"/>, or returns null when it no longer exists.</summary>
    IClientProcess? Open(uint pid);
}

/// <summary>Reads who signed an executable.</summary>
internal interface ISignatureReader
{
    /// <summary>
    /// Subject of the Authenticode signer when the file's signature verifies and chains to a trusted
    /// root, otherwise null (unsigned, tampered, or untrusted).
    /// </summary>
    string? GetTrustedSigner(string path);
}

/// <summary>What the pipe knows about a connected client.</summary>
/// <param name="ProcessId">From <c>GetNamedPipeClientProcessId</c>.</param>
/// <param name="PipeSessionId">From <c>GetNamedPipeClientSessionId</c>.</param>
/// <param name="ConsoleSessionId">The session attached to the physical console right now.</param>
/// <param name="ConnectedAt">When the connection was accepted, as a FILETIME.</param>
internal readonly record struct PipeCaller(uint ProcessId, uint PipeSessionId, uint ConsoleSessionId, long ConnectedAt);

/// <summary>
/// Decides whether a control-pipe client is the RoboMouse app the service was installed for, running
/// in the console session (see the security boundary in plans/uac-service.md):
/// <list type="bullet">
/// <item>the directly installed app: the exe at the configured path, and, when the service exe itself
/// is Authenticode-signed, signed by the same publisher;</item>
/// <item>the Store app: <c>RoboMouse.App.exe</c> with our package family, inside that package's install
/// folder (WindowsApps; Windows itself checks the package signature, and the files inside it are not
/// signed individually).</item>
/// </list>
/// A client that exited, or whose process id now belongs to a process created after the connection,
/// is refused.
/// </summary>
internal sealed class CallerPolicy
{
    public const string AppExeName = "RoboMouse.App.exe";

    private readonly string _expectedAppPath;
    private readonly string? _expectedPackageFamily;
    private readonly string? _serviceSigner;
    private readonly IProcessInspector _processes;
    private readonly ISignatureReader _signatures;

    /// <param name="expectedAppPath">Full path of the directly installed RoboMouse.App.exe.</param>
    /// <param name="expectedPackageFamily">Package family name of the Store app, when that is allowed too.</param>
    /// <param name="serviceSigner">
    /// Signer of the service's own exe, or null when it is unsigned (a dev build from
    /// Install-DevService.ps1); then the installed app is checked by path only.
    /// </param>
    public CallerPolicy(string expectedAppPath, string? expectedPackageFamily, string? serviceSigner,
        IProcessInspector processes, ISignatureReader signatures)
    {
        _expectedAppPath = expectedAppPath;
        _expectedPackageFamily = expectedPackageFamily;
        _serviceSigner = serviceSigner;
        _processes = processes;
        _signatures = signatures;
    }

    /// <summary>True when the caller may use the service; otherwise <paramref name="reason"/> says why not.</summary>
    public bool IsAllowed(PipeCaller caller, out string reason)
    {
        using var process = _processes.Open(caller.ProcessId);
        if (process == null)
        {
            reason = $"client pid {caller.ProcessId} has already exited";
            return false;
        }

        var created = process.CreationTime;
        if (created > caller.ConnectedAt)
        {
            reason = $"pid {caller.ProcessId} now belongs to a process started after the connection";
            return false;
        }

        var session = process.SessionId;
        if (session != caller.PipeSessionId)
        {
            reason = $"client process is in session {session} but the pipe reports session {caller.PipeSessionId}";
            return false;
        }
        if (session != caller.ConsoleSessionId)
        {
            reason = $"client is in session {session}, not the console session {caller.ConsoleSessionId}";
            return false;
        }

        var path = process.ImagePath;
        if (string.IsNullOrEmpty(path))
        {
            reason = "client image path could not be read";
            return false;
        }

        if (PathEquals(path, _expectedAppPath))
        {
            if (_serviceSigner != null)
            {
                var signer = _signatures.GetTrustedSigner(path);
                if (!string.Equals(signer, _serviceSigner, StringComparison.Ordinal))
                {
                    reason = signer == null
                        ? $"'{path}' has no valid signature"
                        : $"'{path}' is signed by '{signer}', not '{_serviceSigner}'";
                    return false;
                }
            }
        }
        else if (!IsStoreApp(process, path, out var storeProblem))
        {
            reason = storeProblem ?? $"client is '{path}', expected '{_expectedAppPath}'";
            return false;
        }

        // Last: the process we inspected is still the one that connected.
        if (process.HasExited || process.CreationTime != created)
        {
            reason = "client exited during the check";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsStoreApp(IClientProcess process, string path, out string? problem)
    {
        problem = null;
        if (_expectedPackageFamily == null
            || !string.Equals(FileName(path), AppExeName, StringComparison.OrdinalIgnoreCase))
            return false;

        var family = process.PackageFamilyName;
        if (!string.Equals(family, _expectedPackageFamily, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"client '{path}' has package family '{family ?? "(none)"}', expected '{_expectedPackageFamily}'";
            return false;
        }

        var installPath = process.PackageInstallPath;
        if (string.IsNullOrEmpty(installPath) || !IsUnder(path, installPath))
        {
            problem = $"client '{path}' is not inside its package folder '{installPath ?? "(unknown)"}'";
            return false;
        }
        return true;
    }

    // Paths come from the kernel in Win32 form; compare them as Windows does (case-insensitive, either
    // separator), without Path APIs so the checks behave the same when tests run on Linux.
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static string FileName(string path)
    {
        var normalized = Normalize(path);
        return normalized[(normalized.LastIndexOf('\\') + 1)..];
    }

    internal static bool PathEquals(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder) + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && !p.Contains("\\..\\", StringComparison.Ordinal);
    }
}
