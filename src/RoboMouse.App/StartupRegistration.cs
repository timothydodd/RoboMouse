using System.Runtime.InteropServices;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>
/// "Start with Windows" for both distribution shapes. The Store (MSIX) build has a virtualised
/// registry and must use the package's startup task; the plain executable uses the Run key.
/// </summary>
internal static class StartupRegistration
{
    private const string TaskId = "RoboMouseStartup";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RoboMouse";

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

    public static async Task ApplyAsync(bool enabled)
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
                    {
                        SimpleLogger.Log("Startup", "Startup was disabled by the user in Windows Settings > Apps > Startup; it must be re-enabled there.");
                    }
                }
                else
                {
                    task.Disable();
                }
                return;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
                return;

            if (enabled)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Startup", $"Could not update startup setting: {ex.Message}");
        }
    }

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
