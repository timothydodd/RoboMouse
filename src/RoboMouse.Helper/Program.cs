using System.IO.Pipes;
using System.Runtime.Versioning;
using RoboMouse.Contracts;
using RoboMouse.Core.Input;

namespace RoboMouse.Helper;

/// <summary>
/// Launched by the service into the console session as: <c>RoboMouse.Helper --pipe &lt;name&gt;</c>.
/// Connects back to the service and applies the injection commands it relays from the app, on
/// whichever desktop is receiving input (including the secure desktop). It injects and reports the
/// cursor position; it installs no hooks and never reads input.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>How often the inject thread checks that it is still on the input desktop.</summary>
    private const long DesktopCheckMs = 100;

    public static int Main(string[] args)
    {
        var pipeName = GetArg(args, "--pipe");
        if (pipeName == null)
        {
            Console.Error.WriteLine("Usage: RoboMouse.Helper --pipe <name>");
            return 1;
        }

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);
        using var pipe = new PipeConnection(client);

        // Everything runs on this one plain thread so SetThreadDesktop is allowed and commands are
        // applied strictly in the order the app sent them.
        var thread = new Thread(() => Run(pipe)) { IsBackground = false, Name = "Inject" };
        thread.Start();
        thread.Join();
        return 0;
    }

    private static void Run(PipeConnection pipe)
    {
        using var desktop = new InputDesktop();
        var heldKeys = new Dictionary<int, (uint Scan, bool Extended)>();
        var heldButtons = new HashSet<MouseEventType>();
        long lastCheck = 0;

        try
        {
            desktop.Follow();
            Send(pipe, PipeMessage.Hello());
            Send(pipe, new PipeMessage(PipeOpcode.HelperReady));

            while (pipe.IsConnected)
            {
                var received = pipe.ReceiveAsync().GetAwaiter().GetResult();
                if (received is null)
                    break;
                var message = received.Value;

                var now = Environment.TickCount64;
                if (now - lastCheck >= DesktopCheckMs)
                {
                    lastCheck = now;
                    desktop.Follow();
                }

                switch (message.Opcode)
                {
                    case PipeOpcode.InjectMotion:
                    {
                        var (dx, dy) = message.ReadMotion();
                        // A failed call right after a desktop switch means we are still on the old one.
                        if (!InputSimulator.MoveRelative(dx, dy) && desktop.Follow())
                            InputSimulator.MoveRelative(dx, dy);
                        break;
                    }
                    case PipeOpcode.MoveTo:
                    {
                        var (x, y) = message.ReadMotion();
                        InputSimulator.MoveTo(x, y);
                        break;
                    }
                    case PipeOpcode.InjectButton:
                    {
                        var (eventType, wheelDelta) = message.ReadButton();
                        var type = (MouseEventType)eventType;
                        TrackButton(heldButtons, type);
                        if (!InputSimulator.SimulateMouseEvent(type, wheelDelta: wheelDelta) && desktop.Follow())
                            InputSimulator.SimulateMouseEvent(type, wheelDelta: wheelDelta);
                        break;
                    }
                    case PipeOpcode.InjectKey:
                    {
                        var (vk, scan, eventType, extended) = message.ReadKey();
                        var type = (KeyboardEventType)eventType;
                        if (type is KeyboardEventType.KeyUp or KeyboardEventType.SysKeyUp)
                            heldKeys.Remove(vk);
                        else
                            heldKeys[vk] = (scan, extended);
                        if (!InputSimulator.SimulateKeyboardEvent((Keys)vk, scan, type, extended) && desktop.Follow())
                            InputSimulator.SimulateKeyboardEvent((Keys)vk, scan, type, extended);
                        break;
                    }
                    case PipeOpcode.QueryCursor:
                    {
                        var (x, y) = InputSimulator.GetCursorPosition();
                        Send(pipe, PipeMessage.Motion(PipeOpcode.CursorPosition, x, y));
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // The service went away or the pipe broke; fall through and let go of anything held.
        }
        finally
        {
            // Never leave a key or button stuck down on a desktop nobody else can reach.
            desktop.Follow();
            foreach (var (vk, (scan, extended)) in heldKeys)
                InputSimulator.SimulateKeyboardEvent((Keys)vk, scan, KeyboardEventType.KeyUp, extended);
            foreach (var button in heldButtons)
                InputSimulator.SimulateMouseEvent(ReleaseOf(button));
        }
    }

    private static void TrackButton(HashSet<MouseEventType> held, MouseEventType type)
    {
        switch (type)
        {
            case MouseEventType.LeftDown:
            case MouseEventType.RightDown:
            case MouseEventType.MiddleDown:
            case MouseEventType.XButton1Down:
            case MouseEventType.XButton2Down:
                held.Add(type);
                break;
            case MouseEventType.LeftUp: held.Remove(MouseEventType.LeftDown); break;
            case MouseEventType.RightUp: held.Remove(MouseEventType.RightDown); break;
            case MouseEventType.MiddleUp: held.Remove(MouseEventType.MiddleDown); break;
            case MouseEventType.XButton1Up: held.Remove(MouseEventType.XButton1Down); break;
            case MouseEventType.XButton2Up: held.Remove(MouseEventType.XButton2Down); break;
        }
    }

    private static MouseEventType ReleaseOf(MouseEventType down) => down switch
    {
        MouseEventType.LeftDown => MouseEventType.LeftUp,
        MouseEventType.RightDown => MouseEventType.RightUp,
        MouseEventType.MiddleDown => MouseEventType.MiddleUp,
        MouseEventType.XButton1Down => MouseEventType.XButton1Up,
        _ => MouseEventType.XButton2Up
    };

    private static void Send(PipeConnection pipe, PipeMessage message) =>
        pipe.SendAsync(message).GetAwaiter().GetResult();

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
