using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Power;

/// <summary>
/// Makes this machine mirror the power state of the machine that controls it: kept awake with its
/// display on while that machine's display is on, display turned off when that one's turns off or it
/// goes to sleep. Holds a power request (visible in <c>powercfg /requests</c>), which is not tied to a
/// thread, so it can be driven from network threads.
/// </summary>
public sealed unsafe class PowerFollower : IDisposable
{
    /// <summary>
    /// Input here this recently means somebody is sitting at this machine, so its display is left on
    /// when the host's merely times out. The host's own input arrives here too, but by the time its
    /// display times out that is older than this.
    /// </summary>
    private const uint LocalUseWindowMs = 60_000;

    private const string Reason = "RoboMouse: the controlling computer is awake";

    private readonly IInputInjector _injector;
    private readonly object _lock = new();
    private nint _request;
    private bool _systemHeld;
    private bool _displayHeld;
    private PeerPowerState? _applied;

    public PowerFollower(IInputInjector injector)
    {
        _injector = injector;
    }

    /// <summary>Mirrors <paramref name="host"/>; null means there is no host to follow, so nothing is held.</summary>
    public void Apply(PeerPowerState? host)
    {
        lock (_lock)
        {
            if (_applied == host)
                return;
            var previous = _applied;
            _applied = host;
            SimpleLogger.Log("Power", $"Following host: {host?.ToString() ?? "released"}");

            Hold(NativeMethods.PowerRequestSystemRequired, ref _systemHeld, host is PeerPowerState.DisplayOn or PeerPowerState.DisplayOff);
            Hold(NativeMethods.PowerRequestDisplayRequired, ref _displayHeld, host == PeerPowerState.DisplayOn);

            if (host == PeerPowerState.DisplayOn)
            {
                // A request keeps a display on but does not turn one on; input does.
                if (_injector.MoveRelative(1, 0))
                    _injector.MoveRelative(-1, 0);
            }
            else if (previous == PeerPowerState.DisplayOn && host != null)
            {
                if (host == PeerPowerState.DisplayOff && MillisecondsSinceLastInput() < LocalUseWindowMs)
                    SimpleLogger.Log("Power", "Leaving the display on: this machine was used in the last minute");
                else
                    NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_MONITORPOWER, NativeMethods.MONITOR_OFF);
            }
        }
    }

    private void Hold(int requestType, ref bool held, bool wanted)
    {
        if (held == wanted)
            return;

        if (_request == 0)
        {
            fixed (char* reason = Reason)
            {
                var context = new NativeMethods.REASON_CONTEXT
                {
                    Version = NativeMethods.POWER_REQUEST_CONTEXT_VERSION,
                    Flags = NativeMethods.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                    SimpleReasonString = reason
                };
                _request = NativeMethods.PowerCreateRequest(&context);
            }
            if (_request is 0 or -1)
            {
                _request = 0;
                SimpleLogger.Log("Power", "PowerCreateRequest failed");
                return;
            }
        }

        var ok = wanted
            ? NativeMethods.PowerSetRequest(_request, requestType)
            : NativeMethods.PowerClearRequest(_request, requestType);
        if (ok)
            held = wanted;
    }

    /// <summary>How long ago this machine last saw input (its own or injected).</summary>
    internal static uint MillisecondsSinceLastInput()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)sizeof(NativeMethods.LASTINPUTINFO) };
        if (!NativeMethods.GetLastInputInfo(&info))
            return uint.MaxValue;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _applied = null;
            if (_request != 0)
                NativeMethods.CloseHandle(_request);
            _request = 0;
            _systemHeld = _displayHeld = false;
        }
    }
}
