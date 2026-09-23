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
/// signer is compared by certificate subject, not thumbprint, because Azure Artifact Signing issues a
/// new short-lived leaf certificate every few days for the same verified identity. Revocation is not
/// checked (no CRL/OCSP round trips); the chain may still fetch a missing root through Windows' root
/// update, which is why the service's own signer is only worked out on the first connection.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AuthenticodeReader : ISignatureReader
{
    public string? GetTrustedSigner(string path)
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
                if (WinVerifyTrust(-1, &action, &data) != 0)
                    return null;

                var provider = WTHelperProvDataFromStateData(data.hWVTStateData);
                if (provider == 0)
                    return null;
                var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
                if (signer == null || signer->csCertChain == 0 || signer->pasCertChain == null)
                    return null;
                var cert = signer->pasCertChain[0].pCert;
                if (cert == null || cert->pbCertEncoded == null)
                    return null;

                using var certificate = X509CertificateLoader.LoadCertificate(
                    new ReadOnlySpan<byte>(cert->pbCertEncoded, (int)cert->cbCertEncoded));
                return certificate.Subject;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"Signature check of '{path}' failed: {ex.Message}");
                return null;
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(-1, &action, &data);
            }
        }
    }
}
