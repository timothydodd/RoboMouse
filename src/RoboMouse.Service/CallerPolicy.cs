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

/// <summary>How a file's Authenticode signature checked out.</summary>
internal enum SignatureStatus
{
    /// <summary>The signature verifies and chains to a trusted root; <see cref="SignatureCheck.Signer"/> says who signed.</summary>
    Signed,

    /// <summary>The file carries no signature at all (<c>TRUST_E_NOSIGNATURE</c>).</summary>
    Unsigned,

    /// <summary>
    /// Anything else: a bad or untrusted signature, or the check itself failed (a chain that could not
    /// be built yet, say). Never taken to mean "unsigned".
    /// </summary>
    Invalid
}

/// <summary>
/// The parts of a signer certificate the policy compares. Azure Artifact (Trusted) Signing issues a new
/// short-lived leaf every few days for the same verified identity, so the thumbprint changes; the
/// subject, the issuing CA and the identity-validation EKU do not.
/// </summary>
/// <param name="Subject">Subject of the leaf certificate.</param>
/// <param name="Issuer">Subject of the CA that issued it (the leaf's issuer name).</param>
/// <param name="ValidationOids">
/// The leaf's enhanced key usages under <see cref="IdentityValidationOidPrefix"/>: Artifact Signing puts
/// the id of the validated identity there, unique to the signing account.
/// </param>
internal sealed record SignerIdentity(string Subject, string Issuer, IReadOnlyList<string> ValidationOids)
{
    /// <summary>Microsoft's arc for Artifact/Trusted Signing identity-validation EKUs.</summary>
    public const string IdentityValidationOidPrefix = "1.3.6.1.4.1.311.97.";

    public SignerIdentity(string subject, string issuer) : this(subject, issuer, []) { }
}

/// <summary>The result of checking a file's signature.</summary>
internal readonly record struct SignatureCheck(SignatureStatus Status, SignerIdentity? Signer = null, string? Error = null)
{
    public static SignatureCheck Unsigned => new(SignatureStatus.Unsigned);

    public static SignatureCheck SignedBy(SignerIdentity signer) => new(SignatureStatus.Signed, signer);

    public static SignatureCheck Failed(string error) => new(SignatureStatus.Invalid, Error: error);
}

/// <summary>Reads who signed an executable.</summary>
internal interface ISignatureReader
{
    /// <summary>
    /// Checks the Authenticode signature of <paramref name="path"/>: signed (verifies and chains to a
    /// trusted root) with the signer's details, unsigned, or invalid. Never throws.
    /// </summary>
    SignatureCheck Check(string path);
}

/// <summary>
/// The service exe's own signature, checked on first use and kept once the answer is definite (signed
/// or unsigned). A failed check is not kept: the next connection tries again, and until then callers
/// are refused rather than waved through as if the service were an unsigned dev build.
/// </summary>
internal sealed class ServiceSignature
{
    private readonly Func<SignatureCheck> _check;
    private readonly Lock _lock = new();
    private SignatureCheck? _known;

    public ServiceSignature(Func<SignatureCheck> check) => _check = check;

    /// <summary>A signature that is already known (for tests and fixed setups).</summary>
    public static ServiceSignature Known(SignerIdentity? signer) =>
        new(() => signer != null ? SignatureCheck.SignedBy(signer) : SignatureCheck.Unsigned);

    public SignatureCheck Get()
    {
        lock (_lock)
        {
            if (_known is { } known)
                return known;

            var result = _check();
            switch (result.Status)
            {
                case SignatureStatus.Signed:
                    Log.Write($"Service is signed by '{result.Signer!.Subject}' (issued by '{result.Signer.Issuer}'); the app must be signed the same way");
                    _known = result;
                    break;
                case SignatureStatus.Unsigned:
                    Log.Write("Service exe is not signed (a dev build); the app is checked by path only");
                    _known = result;
                    break;
                default:
                    Log.WriteLimited("service-signature", $"The service's own signature could not be checked ({result.Error}); refusing the app until it can");
                    break;
            }
            return result;
        }
    }
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
/// is Authenticode-signed, signed by the same publisher (same subject, same issuing CA, and the same
/// Artifact Signing identity-validation EKU when the service's certificate has one);</item>
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
    private readonly ServiceSignature _serviceSignature;
    private readonly IProcessInspector _processes;
    private readonly ISignatureReader _signatures;

    /// <param name="expectedAppPath">Full path of the directly installed RoboMouse.App.exe.</param>
    /// <param name="expectedPackageFamily">Package family name of the Store app, when that is allowed too.</param>
    /// <param name="serviceSigner">
    /// Signer of the service's own exe, or null when it is unsigned (a dev build from
    /// Install-DevService.ps1); then the installed app is checked by path only.
    /// </param>
    public CallerPolicy(string expectedAppPath, string? expectedPackageFamily, SignerIdentity? serviceSigner,
        IProcessInspector processes, ISignatureReader signatures)
        : this(expectedAppPath, expectedPackageFamily, ServiceSignature.Known(serviceSigner), processes, signatures) { }

    /// <summary>
    /// With the service's own signature worked out on first use: verifying a signature can build a
    /// certificate chain, which must not hold up the service's start.
    /// </summary>
    public CallerPolicy(string expectedAppPath, string? expectedPackageFamily, ServiceSignature serviceSignature,
        IProcessInspector processes, ISignatureReader signatures)
    {
        _expectedAppPath = expectedAppPath;
        _expectedPackageFamily = expectedPackageFamily;
        _serviceSignature = serviceSignature;
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
            var service = _serviceSignature.Get();
            if (service.Status == SignatureStatus.Invalid)
            {
                reason = $"the service's own signature could not be checked ({service.Error})";
                return false;
            }
            if (service.Status == SignatureStatus.Signed && !IsSignedLike(path, service.Signer!, out var signerProblem))
            {
                reason = signerProblem;
                return false;
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

    /// <summary>Whether <paramref name="path"/> is validly signed by the same publisher, through the same CA, as the service.</summary>
    private bool IsSignedLike(string path, SignerIdentity expected, out string problem)
    {
        var check = _signatures.Check(path);
        problem = string.Empty;
        if (check.Status != SignatureStatus.Signed || check.Signer is not { } signer)
        {
            problem = check.Status == SignatureStatus.Invalid && check.Error != null
                ? $"'{path}' has no valid signature ({check.Error})"
                : $"'{path}' has no valid signature";
            return false;
        }
        if (!string.Equals(signer.Subject, expected.Subject, StringComparison.Ordinal))
        {
            problem = $"'{path}' is signed by '{signer.Subject}', not '{expected.Subject}'";
            return false;
        }
        if (!string.Equals(signer.Issuer, expected.Issuer, StringComparison.Ordinal))
        {
            problem = $"'{path}' is signed through '{signer.Issuer}', not '{expected.Issuer}'";
            return false;
        }
        var missing = expected.ValidationOids.FirstOrDefault(oid => !signer.ValidationOids.Contains(oid, StringComparer.Ordinal));
        if (missing != null)
        {
            problem = $"'{path}' is signed by '{signer.Subject}' but without the service's validated identity {missing}";
            return false;
        }
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
