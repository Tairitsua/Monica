namespace Monica.Rpc.Services.Support;

/// <summary>Enforces the limit on decoded bytes, including compressed and lengthless responses.</summary>
internal sealed class BoundedResponseStream(Stream inner, long maximumBytes) : Stream
{
    private long _read;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Limit(count));
        Check(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken);
        Check(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Limit(int requested)
    {
        var remaining = maximumBytes - _read;
        // Read one extra byte to distinguish EOF from overflow without adding to a potentially maximal limit.
        return remaining >= requested ? requested : (int)remaining + 1;
    }
    private void Check(int read)
    {
        _read += read;
        if (_read > maximumBytes) throw new InvalidDataException("The decoded response exceeds the configured limit.");
    }

    // The decoder owns both this wrapper and the decoding stream separately.
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
