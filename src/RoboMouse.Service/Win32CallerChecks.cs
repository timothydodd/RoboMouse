using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using static RoboMouse.Service.ServiceNative;

namespace RoboMouse.Service;

/// <summary>Opens client processes with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32ProcessInspector : IProcessInspector
{
    public IClientProcess? Open(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
        return handle == 0 ? null : new Win32ClientProcess(pid, handle);
    }
}

/// <summary>A held process handle; the pid cannot be reused while it is open.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Win32ClientProcess : IClientProcess
{
    private readonly uint _pid;
    private nint _handle;

    public Win32ClientProcess(uint pid, nint handle)
    {
        _pid = pid;
        _handle = handle;
    }

    // The handle keeps the pid from being handed to another process, so a pid lookup is safe here.
    public uint SessionId => ProcessIdToSessionId(_pid, out var session) ? session : uint.MaxValue;

    public long CreationTime => GetProcessTimes(_handle, out var created, out _, out _, out _) ? created : long.MaxValue;

    public bool HasExited => WaitForSingleObject(_handle, 0) == WAIT_OBJECT_0;

    public string? ImagePath
    {
        get
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(_handle, 0, buffer, &size) ? new string(buffer, 0, (int)size) : null;
        }
    }

    public string? PackageFamilyName
    {
        get
        {
            var buffer = stackalloc char[256];
            uint length = 256;
            return GetPackageFamilyName(_handle, &length, buffer) == 0 ? new string(buffer) : null;
        }
    }

    public string? PackageInstallPath
    {
        get
        {
            var fullName = stackalloc char[256];
            uint nameLength = 256;
            if (GetPackageFullName(_handle, &nameLength, fullName) != 0)
                return null;

            var path = stackalloc char[1024];
            uint pathLength = 1024;
            return GetPackagePathByFullName(fullName, &pathLength, path) == 0 ? new string(path) : null;
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
            CloseHandle(_handle);
        _handle = 0;
    }
}

/// <summary>
/// Authenticode through WinVerifyTrust: the signature must verify and chain to a trusted root. The
/// signer is described by subject, issuing CA and identity-validation EKUs rather than thumbprint,
/// because Azure Artifact Signing issues a new short-lived leaf certificate every few days for the same
/// verified identity. Only <c>TRUST_E_NOSIGNATURE</c> counts as unsigned; every other failure is
/// <see cref="SignatureStatus.Invalid"/>. Revocation is not checked (no CRL/OCSP round trips); the chain
/// may still fetch a missing root through Windows' root update, which is why the service's own signer
/// is only worked out on the first connection.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AuthenticodeReader : ISignatureReader
{
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const string EnhancedKeyUsageOid = "2.5.29.37";

    public SignatureCheck Check(string path)
    {
        var action = WintrustActionGenericVerifyV2;
        fixed (char* file = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = file };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = &fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE
            };

            try
            {
                var result = WinVerifyTrust(-1, &action, &data);
                if (result == TRUST_E_NOSIGNATURE)
                    return SignatureCheck.Unsigned;
                if (result != 0)
                    return SignatureCheck.Failed($"WinVerifyTrust returned 0x{result:X8}");

                var provider = WTHelperProvDataFromStateData(data.hWVTStateData);
                if (provider == 0)
                    return SignatureCheck.Failed("no provider data");
                var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
                if (signer == null || signer->csCertChain == 0 || signer->pasCertChain == null)
                    return SignatureCheck.Failed("no signer certificate chain");
                var cert = signer->pasCertChain[0].pCert;
                if (cert == null || cert->pbCertEncoded == null)
                    return SignatureCheck.Failed("no signer certificate");

                using var certificate = X509CertificateLoader.LoadCertificate(
                    new ReadOnlySpan<byte>(cert->pbCertEncoded, (int)cert->cbCertEncoded));
                return SignatureCheck.SignedBy(new SignerIdentity(certificate.Subject, certificate.Issuer, ValidationOids(certificate)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"Signature check of '{path}' failed: {ex.Message}");
                return SignatureCheck.Failed(ex.Message);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(-1, &action, &data);
            }
        }
    }

    /// <summary>The certificate's enhanced key usages under the identity-validation arc.</summary>
    private static List<string> ValidationOids(X509Certificate2 certificate)
    {
        var oids = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != EnhancedKeyUsageOid)
                continue;
            var usages = extension as X509EnhancedKeyUsageExtension ?? new X509EnhancedKeyUsageExtension(extension, extension.Critical);
            foreach (var usage in usages.EnhancedKeyUsages)
            {
                if (usage.Value is { } value && value.StartsWith(SignerIdentity.IdentityValidationOidPrefix, StringComparison.Ordinal))
                    oids.Add(value);
            }
        }
        return oids;
    }
}
