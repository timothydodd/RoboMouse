using System.IO.Pipes;
using System.Runtime.Versioning;
using RoboMouse.Contracts;
using RoboMouse.Core.Input;

namespace RoboMouse.Helper;

/// <summary>
/// Launched by the service as: <c>RoboMouse.Helper --pipe &lt;name&gt;</c>. Connects back to the service,
/// completes the handshake and reports the desktop it is running on. Phase 1 stops there; Phase 3 adds
/// the real input relay (install the hooks + raw input, forward captured events up, apply injection
/// commands down) using the existing <see cref="MouseHook"/>, <see cref="KeyboardHook"/>,
/// <see cref="RawMouseInput"/> and <see cref="InputSimulator"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var pipeName = GetArg(args, "--pipe");
        if (pipeName == null)
        {
            Console.Error.WriteLine("Usage: RoboMouse.Helper --pipe <name>");
            return 1;
        }

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000).ConfigureAwait(false);
        using var pipe = new PipeConnection(client);

        await pipe.SendAsync(PipeMessage.Hello()).ConfigureAwait(false);
        await pipe.SendAsync(new PipeMessage(PipeOpcode.HelperReady)).ConfigureAwait(false);

        while (pipe.IsConnected)
        {
            var message = await pipe.ReceiveAsync().ConfigureAwait(false);
            if (message is null)
                break;

            switch (message.Value.Opcode)
            {
                case PipeOpcode.Hello:
                    break;
                // TODO Phase 3: BeginControlling/EndControlling install/remove hooks + raw input and
                //   forward CapturedMotion/CapturedButton/CapturedKey/EdgeHit; Inject*/BeginControlled/
                //   EndControlled drive InputSimulator; HideCursor/RestoreCursor.
                default:
                    break;
            }
        }
        return 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
