using System.Collections.Concurrent;
using System.IO.Pipes;
using RoboMouse.Contracts;
using Xunit;

namespace RoboMouse.Service.Tests;

/// <summary>The relay between an app connection and a (fake) helper.</summary>
public class ServiceWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const uint Session = 1;

    /// <summary>Stands in for a helper process: ready as soon as it runs, records what it is sent.</summary>
    private sealed class FakeHelper(uint sessionId) : IInjectionHelper
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint SessionId { get; } = sessionId;
        public BlockingCollection<PipeMessage> Received { get; } = new();
        public event Action? Ready;
        public event Action<PipeMessage>? MessageReceived;

        public async Task RunAsync(CancellationToken ct)
        {
            Ready?.Invoke();
            await using var _ = ct.Register(() => _exit.TrySetResult());
            await _exit.Task;
        }

        public Task SendAsync(PipeMessage message, CancellationToken ct)
        {
            Received.Add(message, ct);
            if (message.Opcode == PipeOpcode.QueryCursor)
                MessageReceived?.Invoke(PipeMessage.CursorPosition(3, 4, message.ReadQueryCursor()));
            return Task.CompletedTask;
        }

        /// <summary>The helper process dies.</summary>
        public void Die() => _exit.TrySetResult();
        public void Stop() => _exit.TrySetResult();
        public void Dispose() { }

        public PipeMessage Next() =>
            Received.TryTake(out var m, Timeout) ? m : throw new TimeoutException("the helper was sent nothing");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _client;
        private readonly CancellationTokenSource _cts = new();
        public ConcurrentQueue<FakeHelper> Helpers { get; } = new();
        public uint ConsoleSession { get; set; } = Session;
        public ServiceWorker Worker { get; }
        public PipeConnection App { get; private set; } = null!;
        public PipeConnection ServiceEnd { get; private set; } = null!;
        public Task Served { get; private set; } = Task.CompletedTask;
        public Func<uint, IInjectionHelper>? CreateHelper { get; set; }
        public Stream RawClient => _client;

        public Harness()
        {
            var name = "RoboMouse.ServiceTest." + Guid.NewGuid().ToString("N");
            _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                PipeNames.BufferSize, PipeNames.BufferSize);
            _client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Worker = new ServiceWorker(session =>
            {
                if (CreateHelper != null)
                    return CreateHelper(session);
                var helper = new FakeHelper(session);
                Helpers.Enqueue(helper);
                return helper;
            }, () => ConsoleSession)
            {
                HelperRetryMin = TimeSpan.FromMilliseconds(10),
                HelperRetryMax = TimeSpan.FromMilliseconds(40),
                SessionPollInterval = TimeSpan.FromMilliseconds(10)
            };
        }

        public async Task ConnectAsync()
        {
            var accept = _server.WaitForConnectionAsync(Ct);
            await _client.ConnectAsync(Ct);
            await accept;
            App = new PipeConnection(_client);
            ServiceEnd = new PipeConnection(_server);
            Served = Worker.HandleAppAsync(ServiceEnd, Session, _cts.Token);
            Assert.Equal(PipeOpcode.Hello, (await ReceiveAsync()).Opcode);
        }

        /// <summary>Connects, says Hello and waits for the helper to be ready.</summary>
        public async Task<FakeHelper> ConnectReadyAsync()
        {
            await ConnectAsync();
            await App.SendAsync(PipeMessage.Hello(), Ct);
            Assert.Equal(PipeOpcode.HelperReady, (await ReceiveAsync()).Opcode);
            return Helpers.Last();
        }

        public async Task<PipeMessage> ReceiveAsync() =>
            (await App.ReceiveAsync(Ct).WaitAsync(Timeout, Ct)) ?? throw new EndOfStreamException("the service hung up");

        public async Task AssertDisconnectedAsync()
        {
            // The worker returning is the drop; the pipe server then disposes the connection.
            await Served.WaitAsync(Timeout, Ct);
            ServiceEnd.Dispose();
            Assert.Null(await App.ReceiveAsync(Ct).WaitAsync(Timeout, Ct));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await Served.WaitAsync(Timeout); } catch { }
            ServiceEnd?.Dispose();
            App?.Dispose();
            Worker.Dispose();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task NothingIsRelayed_BeforeHello_AndTheAppIsDropped()
    {
        await using var h = new Harness();
        await h.ConnectAsync();

        await h.App.SendAsync(PipeMessage.Motion(PipeOpcode.InjectMotion, 1, 1), Ct);

        await h.AssertDisconnectedAsync();
        Assert.Empty(h.Helpers);
    }

    [Fact]
    public async Task VersionMismatch_Disconnects_WithoutStartingAHelper()
    {
        await using var h = new Harness();
        await h.ConnectAsync();

        await h.App.SendAsync(new PipeMessage(PipeOpcode.Hello, BitConverter.GetBytes(PipeNames.ProtocolVersion - 1)), Ct);

        await h.AssertDisconnectedAsync();
        Assert.Empty(h.Helpers);
    }

    [Fact]
    public async Task RelaysInOrder_AndReturnsCursorReplies()
    {
        await using var h = new Harness();
        var helper = await h.ConnectReadyAsync();

        await h.App.SendAsync(PipeMessage.Motion(PipeOpcode.InjectMotion, 1, 2), Ct);
        await h.App.SendAsync(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.LeftDown, 0), Ct);
        await h.App.SendAsync(PipeMessage.Motion(PipeOpcode.MoveTo, 30, 40), Ct);
        await h.App.SendAsync(PipeMessage.Key(0x41, 0x1E, PipeInput.KeyDown, false), Ct);
        await h.App.SendAsync(PipeMessage.QueryCursor(77), Ct);

        Assert.Equal((1, 2), helper.Next().ReadMotion());
        Assert.Equal(PipeInput.LeftDown, helper.Next().ReadButton().eventType);
        var moveTo = helper.Next();
        Assert.Equal((PipeOpcode.MoveTo, (30, 40)), (moveTo.Opcode, moveTo.ReadMotion()));
        Assert.Equal(0x41, helper.Next().ReadKey().vk);
        Assert.Equal(77u, helper.Next().ReadQueryCursor());

        var reply = await h.ReceiveAsync();
        Assert.Equal(PipeOpcode.CursorPosition, reply.Opcode);
        Assert.Equal((3, 4, 77u), reply.ReadCursorPosition());
    }

    [Fact]
    public async Task MalformedCommands_AreDropped_AndTheConnectionStays()
    {
        await using var h = new Harness();
        var helper = await h.ConnectReadyAsync();

        await h.App.SendAsync(new PipeMessage(PipeOpcode.InjectKey, new byte[5]), Ct);           // truncated
        await h.App.SendAsync(new PipeMessage(PipeOpcode.InjectMotion, new byte[9]), Ct);        // padded
        await h.App.SendAsync(PipeMessage.Button(PipeOpcode.InjectButton, 99, 0), Ct);           // out of range
        await h.App.SendAsync(new PipeMessage(PipeOpcode.HelperReady), Ct);                      // not an app opcode
        await h.App.SendAsync(PipeMessage.Motion(PipeOpcode.InjectMotion, 9, 9), Ct);

        Assert.Equal((9, 9), helper.Next().ReadMotion());
        Assert.Empty(helper.Received);
    }

    [Fact]
    public async Task OversizedFrame_DropsTheConnection()
    {
        await using var h = new Harness();
        await h.ConnectReadyAsync();

        // Write a raw header claiming a frame far over the cap.
        var header = BitConverter.GetBytes(PipeNames.MaxFrameLength + 1);
        await h.RawClient.WriteAsync(header, Ct);
        await h.RawClient.FlushAsync(Ct);

        await h.Served.WaitAsync(Timeout, Ct);
    }

    [Fact]
    public async Task HelperLost_IsSent_WhenTheHelperDies_AndANewOneStarts()
    {
        await using var h = new Harness();
        var first = await h.ConnectReadyAsync();

        first.Die();

        Assert.Equal(PipeOpcode.HelperLost, (await h.ReceiveAsync()).Opcode);
        Assert.Equal(PipeOpcode.HelperReady, (await h.ReceiveAsync()).Opcode);
        Assert.Equal(2, h.Helpers.Count);
    }

    [Fact]
    public async Task HeldInput_IsReleasedOnTheReplacementHelper()
    {
        await using var h = new Harness();
        var first = await h.ConnectReadyAsync();

        await h.App.SendAsync(PipeMessage.Key(0x10, 0x2A, PipeInput.KeyDown, false), Ct);
        await h.App.SendAsync(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.LeftDown, 0), Ct);
        await h.App.SendAsync(PipeMessage.Key(0x41, 0x1E, PipeInput.KeyDown, false), Ct);
        await h.App.SendAsync(PipeMessage.Key(0x41, 0x1E, PipeInput.KeyUp, false), Ct);
        for (var i = 0; i < 4; i++)
            first.Next();

        first.Die();
        Assert.Equal(PipeOpcode.HelperLost, (await h.ReceiveAsync()).Opcode);
        Assert.Equal(PipeOpcode.HelperReady, (await h.ReceiveAsync()).Opcode);

        // Shift and the left button were still down; A was already released.
        var second = h.Helpers.Last();
        var releases = new[] { second.Next(), second.Next() };
        Assert.Empty(second.Received);
        Assert.Contains(releases, r => r.Opcode == PipeOpcode.InjectKey && r.ReadKey() == (0x10, 0x2Au, PipeInput.KeyUp, false));
        Assert.Contains(releases, r => r.Opcode == PipeOpcode.InjectButton && r.ReadButton().eventType == PipeInput.LeftUp);
    }

    [Fact]
    public async Task MissingHelperExe_IsNotRetried()
    {
        await using var h = new Harness();
        var attempts = 0;
        h.CreateHelper = _ =>
        {
            Interlocked.Increment(ref attempts);
            return new ThrowingHelper();
        };
        await h.ConnectAsync();
        await h.App.SendAsync(PipeMessage.Hello(), Ct);

        await Task.Delay(300, Ct);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    private sealed class ThrowingHelper : IInjectionHelper
    {
        public uint SessionId => Session;
        public event Action? Ready { add { } remove { } }
        public event Action<PipeMessage>? MessageReceived { add { } remove { } }
        public Task RunAsync(CancellationToken ct) => throw new FileNotFoundException("missing", "RoboMouse.Helper.exe");
        public Task SendAsync(PipeMessage message, CancellationToken ct) => Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task FastUserSwitch_DropsTheOldSessionsApp()
    {
        await using var h = new Harness();
        await h.ConnectReadyAsync();

        h.ConsoleSession = 7;
        h.Worker.OnConsoleSessionChanged(7);

        await h.AssertDisconnectedAsync();
    }

    [Fact]
    public async Task NoHelper_WhileAnotherSessionOwnsTheConsole()
    {
        await using var h = new Harness { ConsoleSession = 7 };
        await h.ConnectAsync();
        await h.App.SendAsync(PipeMessage.Hello(), Ct);

        await Task.Delay(200, Ct);
        Assert.Empty(h.Helpers);

        h.ConsoleSession = Session;
        Assert.Equal(PipeOpcode.HelperReady, (await h.ReceiveAsync()).Opcode);
    }
}
