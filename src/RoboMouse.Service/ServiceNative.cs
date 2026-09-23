using System.Runtime.InteropServices;

namespace RoboMouse.Service;

/// <summary>Service control manager, session, process, Authenticode and token P/Invoke used by the service.</summary>
internal static unsafe partial class ServiceNative
{
    public const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    public const int SERVICE_RUNNING = 0x04;
    public const int SERVICE_STOP_PENDING = 0x03;
    public const int SERVICE_STOPPED = 0x01;
    public const int SERVICE_START_PENDING = 0x02;
    public const int SERVICE_CONTROL_STOP = 0x01;
    public const int SERVICE_CONTROL_SHUTDOWN = 0x05;
    public const int SERVICE_ACCEPT_STOP = 0x01;
    public const int SERVICE_ACCEPT_SHUTDOWN = 0x04;
    public const int NO_ERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_TABLE_ENTRY
    {
        public char* lpServiceName;
        public delegate* unmanaged[Stdcall]<int, char**, void> lpServiceProc;
    }

    public delegate int HandlerEx(int control, int eventType, nint eventData, nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRY* lpServiceStartTable);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint RegisterServiceCtrlHandlerExW(string lpServiceName, nint lpHandlerProc, nint lpContext);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetServiceStatus(nint hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

    // --- session -------------------------------------------------------------------------------

    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();

    // --- pipes -----------------------------------------------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientSessionId(nint pipe, out uint clientSessionId);

    // --- processes -------------------------------------------------------------------------------

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint WAIT_OBJECT_0 = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* exeName, uint* size);

    [LibraryImport("kernel32.dll")]
    public static partial int GetPackageFamilyName(nint process, uint* packageFamilyNameLength, char* packageFamilyName);

    [LibraryImport("kernel32.dll")]
    public static partial int GetPackageFullName(nint process, uint* packageFullNameLength, char* packageFullName);

    [LibraryImport("kernel32.dll")]
    public static partial int GetPackagePathByFullName(char* packageFullName, uint* pathLength, char* path);

    // --- Authenticode ----------------------------------------------------------------------------

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x10;

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2: Authenticode policy.</summary>
    public static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    /// <summary>Leading fields of CRYPT_PROVIDER_SGNR; only read through a pointer wintrust owns.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CRYPT_PROVIDER_SGNR
    {
        public uint cbStruct;
        public uint sftVerifyAsOfLow;
        public uint sftVerifyAsOfHigh;
        public uint csCertChain;
        public CRYPT_PROVIDER_CERT* pasCertChain;
    }

    /// <summary>Leading fields of CRYPT_PROVIDER_CERT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CRYPT_PROVIDER_CERT
    {
        public uint cbStruct;
        public CERT_CONTEXT* pCert;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CERT_CONTEXT
    {
        public uint dwCertEncodingType;
        public byte* pbCertEncoded;
        public uint cbCertEncoded;
        public nint pCertInfo;
        public nint hCertStore;
    }

    [LibraryImport("wintrust.dll")]
    public static partial int WinVerifyTrust(nint hwnd, Guid* actionId, WINTRUST_DATA* data);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    public static partial CRYPT_PROVIDER_SGNR* WTHelperGetProvSignerFromChain(nint provData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    // --- tokens (helper launch) ------------------------------------------------------------------

    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const int TokenSessionId = 12;
    public const uint SE_PRIVILEGE_REMOVED = 0x00000004;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
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
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTokenInformation(nint token, int tokenInformationClass, void* tokenInformation, int length);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        TOKEN_PRIVILEGES* newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessAsUserW(
        nint token, char* applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        char* currentDirectory, STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);
}
