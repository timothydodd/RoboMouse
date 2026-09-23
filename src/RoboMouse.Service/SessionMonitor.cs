namespace RoboMouse.Service;

/// <summary>
/// Polls which session owns the physical console and raises an event when it changes (fast user
/// switching, a remote session taking the console). A helper belongs to one session, and only the
/// console session's app may use the service. Polling avoids needing a window and a message pump
/// inside the service.
///
/// It does not report the input desktop: this process runs in session 0 and only ever sees session
/// 0's desktops, so a desktop name read here was always "Winlogon". The helper follows the real input
/// desktop itself.
/// </summary>
internal sealed class SessionMonitor : IDisposable
{
    private readonly Func<uint> _consoleSession;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SessionMonitor(Func<uint> consoleSession) => _consoleSession = consoleSession;

    /// <summary>Raised on a background thread with the new console session id.</summary>
    public event Action<uint>? Changed;

    public void Start()
    {
        if (_loop != null)
            return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        uint? last = null;
        while (!ct.IsCancellationRequested)
        {
            var session = _consoleSession();
            if (last != session)
            {
                if (last != null)
                    Changed?.Invoke(session);
                last = session;
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
