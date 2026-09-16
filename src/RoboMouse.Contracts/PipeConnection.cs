using System.Buffers.Binary;
using System.IO.Pipes;

namespace RoboMouse.Contracts;

/// <summary>
/// Framed message transport over a connected pipe stream. Reads and writes the 4-byte length prefix
/// defined by <see cref="PipeMessage"/>. One writer and one reader at a time; writes are serialized.
/// </summary>
public sealed class PipeConnection : IDisposable
{
    private readonly PipeStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public PipeConnection(PipeStream stream) => _stream = stream;

    public bool IsConnected => !_disposed && _stream.IsConnected;

    public async Task SendAsync(PipeMessage message, CancellationToken ct = default)
    {
        var frame = message.ToFrame();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads one message, or null when the pipe closes.</summary>
    public async Task<PipeMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > MaxFrameLength)
            throw new InvalidDataException($"Pipe frame length {length} is out of range.");

        var body = new byte[length];
        if (!await ReadExactlyAsync(body, ct).ConfigureAwait(false))
            return null;

        return new PipeMessage((PipeOpcode)body[0], body[1..]);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private const int MaxFrameLength = 64 * 1024;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writeLock.Dispose();
        _stream.Dispose();
    }
}
