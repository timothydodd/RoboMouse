using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RoboMouse.Service;

/// <summary>
/// Entry point. Run by the SCM it registers as a service; with <c>--console</c> it runs the same
/// worker in the foreground for local testing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Program
{
    private const string ServiceName = "RoboMouseService";

    private static ServiceWorker? s_worker;
    private static nint s_statusHandle;
    private static readonly ManualResetEventSlim s_stop = new(false);
    private static ServiceNative.SERVICE_STATUS s_status;

    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase)))
            return RunConsole();

        // Hand control to the SCM, which calls ServiceMain back on its own thread.
        fixed (char* name = ServiceName)
        {
            var table = stackalloc ServiceNative.SERVICE_TABLE_ENTRY[2];
            table[0] = new ServiceNative.SERVICE_TABLE_ENTRY { lpServiceName = name, lpServiceProc = &ServiceMain };
            table[1] = default;
            if (!ServiceNative.StartServiceCtrlDispatcherW(table))
            {
                Log.Write($"StartServiceCtrlDispatcher failed ({Marshal.GetLastPInvokeError()}); run with --console for interactive use");
                return 1;
            }
        }
        return 0;
    }

    private static int RunConsole()
    {
        Console.WriteLine("RoboMouse service (console mode). Ctrl+C to stop.");
        s_worker = new ServiceWorker();
        s_worker.Start();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; s_stop.Set(); };
        s_stop.Wait();
        s_worker.Dispose();
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void ServiceMain(int argc, char** argv)
    {
        s_statusHandle = ServiceNative.RegisterServiceCtrlHandlerExW(ServiceName, (nint)(delegate* unmanaged[Stdcall]<int, int, nint, nint, int>)&HandlerEx, 0);
        if (s_statusHandle == 0)
        {
            Log.Write($"RegisterServiceCtrlHandlerEx failed ({Marshal.GetLastPInvokeError()})");
            return;
        }

        s_status.dwServiceType = ServiceNative.SERVICE_WIN32_OWN_PROCESS;
        Report(ServiceNative.SERVICE_START_PENDING, 0);

        try
        {
            s_worker = new ServiceWorker();
            s_worker.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Service failed to start: {ex}");
            Report(ServiceNative.SERVICE_STOPPED, 1);
            return;
        }

        Report(ServiceNative.SERVICE_RUNNING, 0);
        s_stop.Wait();

        s_worker?.Dispose();
        Report(ServiceNative.SERVICE_STOPPED, 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int HandlerEx(int control, int eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ServiceNative.SERVICE_CONTROL_STOP:
            case ServiceNative.SERVICE_CONTROL_SHUTDOWN:
                Report(ServiceNative.SERVICE_STOP_PENDING, 0);
                s_stop.Set();
                break;
        }
        return ServiceNative.NO_ERROR;
    }

    private static void Report(int state, int exitCode)
    {
        s_status.dwCurrentState = state;
        s_status.dwWin32ExitCode = exitCode;
        s_status.dwControlsAccepted = state == ServiceNative.SERVICE_RUNNING
            ? ServiceNative.SERVICE_ACCEPT_STOP | ServiceNative.SERVICE_ACCEPT_SHUTDOWN
            : 0;
        s_status.dwWaitHint = state is ServiceNative.SERVICE_START_PENDING or ServiceNative.SERVICE_STOP_PENDING ? 3000 : 0;
        if (s_statusHandle != 0)
            ServiceNative.SetServiceStatus(s_statusHandle, ref s_status);
    }
}
