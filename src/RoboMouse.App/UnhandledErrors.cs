using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.Views;
using RoboMouse.Core.Logging;

namespace RoboMouse.App;

/// <summary>
/// Keeps a bug in a UI handler or a forgotten task from silently taking the app (and with it the
/// user's way back from a remote screen) down: the exception is logged and shown, and the app runs on.
/// </summary>
internal static class UnhandledErrors
{
    private static int _dialogShowing;

    /// <summary>Hooks the UI dispatcher and unobserved task exceptions. Call once the dispatcher exists.</summary>
    public static void Install()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report("UI", e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report("Task", e.Exception);
        };
    }

    private static void Report(string source, Exception ex)
    {
        SimpleLogger.Log("Unhandled", $"{source}: {ex}");

        // One dialog at a time: a handler that throws on every timer tick must not stack up windows.
        if (Interlocked.Exchange(ref _dialogShowing, 1) != 0)
            return;
        var message = ex is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerException!.Message : ex.GetBaseException().Message;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await new WindowDialogService(null, null).ErrorAsync(
                    $"{message}\n\nRoboMouse is still running. The details were written to its log (%AppData%\\RoboMouse\\debug.log).");
            }
            catch (Exception dialogError)
            {
                SimpleLogger.Log("Unhandled", $"Could not show the error: {dialogError.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _dialogShowing, 0);
            }
        });
    }
}
