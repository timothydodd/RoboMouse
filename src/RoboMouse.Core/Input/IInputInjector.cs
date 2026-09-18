namespace RoboMouse.Core.Input;

/// <summary>
/// Applies a remote controller's input on this machine. This is the only part of the input layer
/// that depends on which desktop is active: the default implementation injects from this process,
/// which Windows ignores on the secure desktop (UAC, lock screen, sign-in); the desktop service
/// supplies one that injects from a helper running on that desktop. Capture (hooks, raw input) and
/// moving our own cursor while controlling a remote never go through here.
/// </summary>
public interface IInputInjector
{
    /// <summary>Injects relative motion. Returns false when Windows rejected it (UIPI).</summary>
    bool MoveRelative(int deltaX, int deltaY);

    /// <summary>Places the cursor at absolute virtual-screen coordinates.</summary>
    void MoveTo(int x, int y);

    /// <summary>Current cursor position on the desktop that input is being applied to.</summary>
    (int X, int Y) GetCursorPosition();

    bool SimulateMouseEvent(MouseEventType eventType, int wheelDelta = 0);

    bool SimulateKeyboardEvent(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended);
}

/// <summary>Injects from this process through <see cref="InputSimulator"/>.</summary>
public sealed class InProcessInjector : IInputInjector
{
    public bool MoveRelative(int deltaX, int deltaY) => InputSimulator.MoveRelative(deltaX, deltaY);

    public void MoveTo(int x, int y) => InputSimulator.MoveTo(x, y);

    public (int X, int Y) GetCursorPosition() => InputSimulator.GetCursorPosition();

    public bool SimulateMouseEvent(MouseEventType eventType, int wheelDelta = 0) =>
        InputSimulator.SimulateMouseEvent(eventType, wheelDelta: wheelDelta);

    public bool SimulateKeyboardEvent(Keys keyCode, uint scanCode, KeyboardEventType eventType, bool isExtended) =>
        InputSimulator.SimulateKeyboardEvent(keyCode, scanCode, eventType, isExtended);
}
