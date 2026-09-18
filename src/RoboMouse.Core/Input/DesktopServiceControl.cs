using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RoboMouse.Core.Logging;

namespace RoboMouse.Core.Input;

/// <summary>
/// Detects the separately installed RoboMouse desktop service and starts or stops it. The app stays a
/// normal user: querying needs no rights, and changing the service is one elevated <c>sc</c> command
/// (a single UAC prompt on that click, see plans/uac-service.md).
/// </summary>
public static partial class DesktopServiceControl
{
    public const string ServiceName = "RoboMouseService";

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SERVICE_RUNNING = 0x04;
    private const int SERVICE_START_PENDING = 0x02;

    /// <summary>True when the service is registered on this machine.</summary>
    public static bool IsInstalled => Query(out _);

    /// <summary>True when the service is running or starting.</summary>
    public static bool IsRunning => Query(out var state) && state is SERVICE_RUNNING or SERVICE_START_PENDING;

    private static bool Query(out int state)
    {
        state = 0;
        try
        {
            var manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (manager == 0)
                return false;
            try
            {
                var service = OpenServiceW(manager, ServiceName, SERVICE_QUERY_STATUS);
                if (service == 0)
                    return false;
                try
                {
                    if (QueryServiceStatus(service, out var status))
                        state = status.dwCurrentState;
                    return true;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Service", $"Service query failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the service to start with Windows and starts it (true), or stops it and returns it to manual
    /// start (false). Shows one UAC prompt. Returns false if the user declined or the command failed.
    /// </summary>
    public static Task<bool> SetRunningAsync(bool run) => Task.Run(() =>
    {
        var commands = run
            ? $"sc config {ServiceName} start= auto & sc start {ServiceName}"
            : $"sc stop {ServiceName} & sc config {ServiceName} start= demand";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commands}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit(15000);

            // "sc start" returns before the service is up; give it a moment before judging.
            for (var i = 0; i < 20 && IsRunning != run; i++)
                Thread.Sleep(250);
            return IsRunning == run;
        }
        catch (Win32Exception ex)
        {
            SimpleLogger.Log("Service", $"Elevated service command was not run: {ex.Message}");
            return false;
        }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
