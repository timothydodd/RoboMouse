namespace RoboMouse.Service;

/// <summary>
/// Polls the console session and the input desktop and raises an event when either changes. The
/// session is what the service acts on (a helper belongs to one session). The desktop name is only
/// informational: run by the SCM this process is in session 0 and sees session 0's desktops, not the
/// user's, which is why the helper follows the input desktop itself. Polling avoids needing a window
/// and a message pump inside the service.
/// </summary>
internal sealed class DesktopMonitor : IDisposable
{
    public readonly record struct DesktopState(string DesktopName, uint SessionId)
    {
        /// <summary>The secure desktop hosts UAC, the lock screen and sign-in; only SYSTEM can drive it.</summary>
        public bool IsSecure => DesktopName.Equals("Winlogon", StringComparison.OrdinalIgnoreCase);
    }

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised on a background thread whenever the desktop name or console session changes.</summary>
    public event Action<DesktopState>? Changed;

    public DesktopState Current { get; private set; }

    public void Start()
    {
        if (_loop != null)
            return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        DesktopState? last = null;
        while (!ct.IsCancellationRequested)
        {
            var state = new DesktopState(
                ServiceNative.GetInputDesktopName() ?? "Winlogon", // failing to open usually means the secure desktop
                ServiceNative.WTSGetActiveConsoleSessionId());

            if (last != state)
            {
                last = state;
                Current = state;
                Changed?.Invoke(state);
            }

            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
