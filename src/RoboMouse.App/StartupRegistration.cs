using System.Runtime.InteropServices;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>Whether Windows starts RoboMouse at sign-in, and who turned it off when it does not.</summary>
public enum StartupState
{
    /// <summary>Could not be read (no registry access, startup task API failed).</summary>
    Unknown,
    Off,
    On,
    /// <summary>The user turned it off in Task Manager or Settings > Apps > Startup; only they can turn it back on.</summary>

    DisabledByUser,
    /// <summary>Store build: turned off by group policy.</summary>
    DisabledByPolicy
}

/// <summary>
/// "Start with Windows" for both distribution shapes. The Store (MSIX) build has a virtualised
/// registry and must use the package's startup task; the plain executable uses the Run key.
/// </summary>
internal static class StartupRegistration
{
    private const string TaskId = "RoboMouseStartup";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RoboMouse";
    private const string ApprovedKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>True when running from an installed MSIX package.</summary>
    public static bool IsPackaged
    {
        get
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != APPMODEL_ERROR_NO_PACKAGE;
        }
    }

    /// <summary>The Run key value for this executable: its quoted path.</summary>
    private static string RunCommand => $"\"{Environment.ProcessPath}\"";

    /// <summary>Turns startup on or off and returns the state Windows reports afterwards.</summary>
    public static async Task<StartupState> ApplyAsync(bool enabled)
    {
        try
        {
            if (IsPackaged)
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
                if (enabled)
                {
                    var state = await task.RequestEnableAsync();
                    if (state == global::Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                        SimpleLogger.Log("Startup", "Startup was disabled by the user in Windows Settings > Apps > Startup; it must be re-enabled there.");
                    return Map(state);
                }
                task.Disable();
                return Map(task.State);
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
                return StartupState.Unknown;

            if (enabled)
                key.SetValue(RunValueName, RunCommand);
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return !enabled ? StartupState.Off : IsTurnedOffByUser() ? StartupState.DisabledByUser : StartupState.On;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Startup", $"Could not update startup setting: {ex.Message}");
            return StartupState.Unknown;
        }
    }

    /// <summary>Reads the current state without changing it.</summary>
    public static async Task<StartupState> GetStateAsync()
    {
        try
        {
            if (IsPackaged)
            {
                var task = await global::Windows.ApplicationModel.StartupTask.GetAsync(TaskId);
                return Map(task.State);
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not string ? StartupState.Off
                : IsTurnedOffByUser() ? StartupState.DisabledByUser
                : StartupState.On;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Startup", $"Could not read startup setting: {ex.Message}");
            return StartupState.Unknown;
        }
    }

    /// <summary>
    /// At launch, for the plain executable: when startup is on but the Run value points somewhere else
    /// (the app was moved, or reinstalled to another folder), point it at this copy again.
    /// </summary>
    public static void SyncOnLaunch(bool enabled)
    {
        if (!enabled || IsPackaged || Environment.ProcessPath == null)
            return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null || key.GetValue(RunValueName) as string == RunCommand)
                return;
            key.SetValue(RunValueName, RunCommand);
            SimpleLogger.Log("Startup", $"Startup entry updated to {RunCommand}");
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Startup", $"Could not check the startup entry: {ex.Message}");
        }
    }

    /// <summary>
    /// Task Manager and Settings > Apps > Startup switch a Run entry off with a flag (first byte odd)
    /// under StartupApproved rather than deleting it, and Windows then skips it at sign-in.
    /// </summary>
    private static bool IsTurnedOffByUser()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
        return key?.GetValue(RunValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
    }

    private static StartupState Map(global::Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => StartupState.On,
        global::Windows.ApplicationModel.StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        global::Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
        _ => StartupState.Off
    };

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
