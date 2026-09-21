using RoboMouse.Core.Input;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Power;

/// <summary>
/// Watches this machine's display and sleep state so peers can be told. Uses a message-only window:
/// power-setting and suspend/resume notifications registered for a window handle are sent straight to
/// it, unlike the broadcast WM_POWERBROADCAST that message-only windows never see. Create it on a
/// thread that pumps messages.
/// </summary>
public sealed unsafe class PowerMonitor : IDisposable
{
    private readonly MessageWindow _window;
    private nint _displayNotification;
    private nint _suspendNotification;
    private bool _displayOn = true;
    private volatile PeerPowerState _state = PeerPowerState.DisplayOn;

    /// <summary>Raised on the window's thread when <see cref="State"/> changes.</summary>
    public event Action<PeerPowerState>? Changed;

    public PeerPowerState State => _state;

    public PowerMonitor()
    {
        _window = new MessageWindow();
        _window.Message += OnMessage;

        // Windows answers the registration with the current display state.
        var setting = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
        _displayNotification = NativeMethods.RegisterPowerSettingNotification(_window.Handle, &setting, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        _suspendNotification = NativeMethods.RegisterSuspendResumeNotification(_window.Handle, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_displayNotification == 0 || _suspendNotification == 0)
            SimpleLogger.Log("Power", "Could not register for power notifications; peers will see this machine as always on");
    }

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != NativeMethods.WM_POWERBROADCAST)
            return;

        switch ((int)wParam)
        {
            case NativeMethods.PBT_POWERSETTINGCHANGE when lParam != 0:
                var setting = (NativeMethods.POWERBROADCAST_SETTING*)lParam;
                if (setting->PowerSetting != NativeMethods.GUID_CONSOLE_DISPLAY_STATE || setting->DataLength < 1)
                    return;
                _displayOn = setting->Data != 0;
                // The display is turned off on the way into sleep; that must not undo "suspending".
                if (_state != PeerPowerState.Suspending || _displayOn)
                    Set(_displayOn ? PeerPowerState.DisplayOn : PeerPowerState.DisplayOff);
                break;

            case NativeMethods.PBT_APMSUSPEND:
                Set(PeerPowerState.Suspending);
                break;

            // A wake with nobody there (a timer, Wake-on-LAN) leaves the display off, so peers stay as they are.
            case NativeMethods.PBT_APMRESUMEAUTOMATIC:
            case NativeMethods.PBT_APMRESUMESUSPEND:
                Set(_displayOn ? PeerPowerState.DisplayOn : PeerPowerState.DisplayOff);
                break;
        }
    }

    private void Set(PeerPowerState state)
    {
        if (_state == state)
            return;
        _state = state;
        SimpleLogger.Log("Power", $"This machine: {state}");
        Changed?.Invoke(state);
    }

    public void Dispose()
    {
        if (_displayNotification != 0)
            NativeMethods.UnregisterPowerSettingNotification(_displayNotification);
        if (_suspendNotification != 0)
            NativeMethods.UnregisterSuspendResumeNotification(_suspendNotification);
        _displayNotification = 0;
        _suspendNotification = 0;
        _window.Dispose();
    }
}
