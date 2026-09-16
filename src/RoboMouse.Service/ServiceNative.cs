using System.Runtime.InteropServices;

namespace RoboMouse.Service;

/// <summary>Service control manager and desktop/session P/Invoke used by the service host.</summary>
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

    // --- desktop / session ----------------------------------------------------------------------

    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseDesktop(nint hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetUserObjectInformationW(nint hObj, int nIndex, char* pvInfo, uint nLength, uint* lpnLengthNeeded);

    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();

    /// <summary>Name of the desktop currently receiving input ("Default", "Winlogon", "Screen-saver"), or null.</summary>
    public static string? GetInputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == 0)
            return null;
        try
        {
            var name = stackalloc char[256];
            uint needed;
            if (GetUserObjectInformationW(desktop, UOI_NAME, name, 256 * sizeof(char), &needed))
                return new string(name);
            return null;
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }
}
