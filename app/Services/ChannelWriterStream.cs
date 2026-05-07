using System.Threading.Channels;

namespace app.Services;

/// <summary>
/// A write-only <see cref="Stream"/> that feeds byte[] chunks into a
/// <see cref="ChannelWriter{T}"/>. Used to bridge the camera toolkit's
/// stream-based recording API with a bounded Channel that a background
/// consumer task drains to a FileStream, keeping peak RAM constant.
/// </summary>
internal sealed class ChannelWriterStream : Stream
{
    private readonly ChannelWriter<byte[]> _writer;

    public ChannelWriterStream(ChannelWriter<byte[]> writer)
    {
        _writer = writer;
    }

    // ── Stream contract ──────────────────────────────────────────────────────

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;

    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // ── Write paths ──────────────────────────────────────────────────────────

    public override void Write(byte[] buffer, int offset, int count)
    {
        var chunk = new byte[count];
        buffer.AsSpan(offset, count).CopyTo(chunk);
        _writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        var chunk = new byte[count];
        buffer.AsSpan(offset, count).CopyTo(chunk);
        await _writer.WriteAsync(chunk, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var chunk = buffer.ToArray();
        await _writer.WriteAsync(chunk, cancellationToken);
    }

    // ── Flush: no-op — channel has no internal buffer on the writer side ─────

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    // ── Unsupported ──────────────────────────────────────────────────────────

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();
    public override void SetLength(long value) =>
        throw new NotSupportedException();

    // ── Disposal: complete the channel so the consumer loop exits ────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _writer.TryComplete();
        base.Dispose(disposing);
    }
}
