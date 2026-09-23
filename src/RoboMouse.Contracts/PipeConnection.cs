using System.Buffers.Binary;
using System.IO.Pipes;

namespace RoboMouse.Contracts;

/// <summary>
/// Framed message transport over a connected pipe stream. Reads and writes the 4-byte length prefix
/// defined by <see cref="PipeMessage"/>. One writer and one reader at a time; writes are serialized.
/// Frames are capped at <see cref="PipeNames.MaxFrameLength"/> in both directions.
/// </summary>
public sealed class PipeConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _closed;
    private bool _disposed;

    public PipeConnection(PipeStream stream) : this((Stream)stream) { }

    /// <summary>Any duplex stream; used by tests to feed raw bytes.</summary>
    public PipeConnection(Stream stream) => _stream = stream;

    public bool IsConnected => !_disposed && !_closed && (_stream is not PipeStream pipe || pipe.IsConnected);

    public async Task SendAsync(PipeMessage message, CancellationToken ct = default)
    {
        if (message.Payload.Length + 1 > PipeNames.MaxFrameLength)
            throw new ArgumentException($"Pipe message of {message.Payload.Length} bytes is over the frame limit.", nameof(message));

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

    /// <summary>
    /// Reads one message, or null when the pipe closes (including part-way through a frame). Throws
    /// <see cref="InvalidDataException"/> for a length of zero, below zero or over the cap; the caller
    /// then drops the connection, since the stream can no longer be framed.
    /// </summary>
    public async Task<PipeMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > PipeNames.MaxFrameLength)
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
            {
                _closed = true;
                return false;
            }
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writeLock.Dispose();
        _stream.Dispose();
    }
}
