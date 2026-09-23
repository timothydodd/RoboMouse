using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;
using ProtocolMessage = RoboMouse.Core.Network.Protocol.Message;

namespace RoboMouse.Core.Network;

/// <summary>
/// Pulls offered file bytes from a peer over a dedicated transfer connection, opened on first use so
/// that a copy that is never pasted costs nothing. One request is in flight at a time; Explorer reads
/// files sequentially, and the connection is separate from the input link, so this is enough to keep
/// a LAN busy without any effect on mouse latency.
/// </summary>
public sealed class FileTransferClient : IDisposable
{
    /// <summary>Bytes per request. Large enough to amortise round trips, small enough to keep cancel responsive.</summary>
    public const int ChunkSize = 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<PeerConnection>> _connect;
    private readonly object _lock = new();
    private PeerConnection? _connection;
    private TaskCompletionSource<FileChunkMessage>? _pending;
    private bool _disposed;

    public string PeerName { get; }

    public FileTransferClient(string peerName, Func<CancellationToken, Task<PeerConnection>> connect)
    {
        PeerName = peerName;
        _connect = connect;
    }

    /// <summary>
    /// Fetches up to <paramref name="length"/> bytes. Blocks the calling thread; intended to be called
    /// from the clipboard STA thread while an application reads a virtual file.
    /// </summary>
    public byte[] Fetch(string offerId, int entryIndex, long offset, int length)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new IOException("Transfer cancelled.");

            var connection = EnsureConnected();
            var tcs = new TaskCompletionSource<FileChunkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;

            connection.Post(new FileRequestMessage
            {
                OfferId = offerId,
                EntryIndex = entryIndex,
                Offset = offset,
                Length = Math.Min(length, ChunkSize)
            });

            if (!tcs.Task.Wait(RequestTimeout))
            {
                _pending = null;
                DropConnection();
                throw new IOException($"{PeerName} did not send file data within {RequestTimeout.TotalSeconds:0} seconds.");
            }

            _pending = null;
            var chunk = tcs.Task.Result;
            if (!string.IsNullOrEmpty(chunk.Error))
                throw new IOException($"{PeerName}: {chunk.Error}");

            return chunk.Data;
        }
    }

    private PeerConnection EnsureConnected()
    {
        if (_connection is { IsConnected: true })
            return _connection;

        // This runs on the clipboard STA thread, which has a UI synchronization context. Run the async
        // connect on the pool so its continuations never try to come back to the thread we are blocking.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var connection = Task.Run(() => _connect(token)).GetAwaiter().GetResult();
        connection.MessageReceived += OnMessage;
        connection.Disconnected += (s, e) =>
        {
            // No lock here: Fetch holds it while it waits, and this must be able to wake it.
            Interlocked.CompareExchange(ref _connection, null, connection);
            _pending?.TrySetException(new IOException($"Transfer connection to {PeerName} was lost."));
        };
        connection.Start();
        _connection = connection;
        SimpleLogger.Log("Files", $"Opened transfer connection to {PeerName}");
        return connection;
    }

    private void OnMessage(object? sender, ProtocolMessage message)
    {
        if (message is FileChunkMessage chunk)
            _pending?.TrySetResult(chunk);
    }

    /// <summary>Closes the transfer connection; the next read opens a new one.</summary>
    public void DropConnection()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending?.TrySetException(new IOException("Transfer cancelled."));
            DropConnection();
        }
    }
}
