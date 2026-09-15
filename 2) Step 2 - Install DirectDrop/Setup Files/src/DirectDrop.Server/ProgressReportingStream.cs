using DirectDrop.Core.Models;

namespace DirectDrop.Server;

public sealed class ProgressReportingStream : Stream
{
    private readonly Stream _inner;
    private readonly TransferItem _transfer;
    private readonly CancellationTokenSource _linkedCancellation;
    private long _lastReportedBytes;
    private long _lastReportTimestamp;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    public ProgressReportingStream(
        Stream inner,
        TransferItem transfer,
        CancellationToken transferCancellation = default,
        CancellationToken requestAborted = default)
    {
        _inner = inner;
        _transfer = transfer;
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            transferCancellation, requestAborted);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), _linkedCancellation.Token);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, _linkedCancellation.Token);
        Report(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Report(int read)
    {
        if (read <= 0) return;

        long absolutePosition = _inner.Position;
        long now = Environment.TickCount64;

        // Progress is UI telemetry, not part of the data path. Avoid taking the
        // TransferItem speed lock on every filesystem/network read while still
        // keeping the UI responsive.
        if (absolutePosition < _transfer.TotalBytes &&
            absolutePosition - Interlocked.Read(ref _lastReportedBytes) < 256 * 1024 &&
            now - Interlocked.Read(ref _lastReportTimestamp) < 100)
            return;

        Interlocked.Exchange(ref _lastReportedBytes, absolutePosition);
        Interlocked.Exchange(ref _lastReportTimestamp, now);

        _transfer.UpdateProgress(absolutePosition);
        if (absolutePosition >= _transfer.TotalBytes)
            _transfer.MarkCompleted();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _linkedCancellation.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
