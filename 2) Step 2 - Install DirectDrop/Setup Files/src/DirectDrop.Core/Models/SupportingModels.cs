namespace DirectDrop.Core.Models;

/// <summary>
/// Server-side state for ONE contiguous upload stream. DirectDrop deliberately
/// does not split/repackage files into application-level chunks: the browser
/// sends one File/Blob stream and the server writes that stream sequentially
/// into a .part file. If the connection drops, the current file length is the
/// resume offset and the browser continues with file.slice(offset) - no chunk
/// map, sparse pre-allocation, or out-of-order writes are involved.
/// </summary>
public sealed class PendingUpload : IDisposable
{
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly CancellationTokenSource _cancelCts = new();

    public PendingUpload(string uploadId, string safeFileName, long totalBytes, string partFilePath)
    {
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        UploadId = uploadId;
        SafeFileName = safeFileName;
        TotalBytes = totalBytes;
        PartFilePath = partFilePath;
    }

    public string UploadId { get; }
    public string SafeFileName { get; }
    public long TotalBytes { get; }
    public string PartFilePath { get; }

    public long BytesReceived => File.Exists(PartFilePath) ? new FileInfo(PartFilePath).Length : 0;
    public bool IsComplete => BytesReceived == TotalBytes;
    public CancellationToken CancellationToken => _cancelCts.Token;
    public void Cancel() => _cancelCts.Cancel();

    public async ValueTask<IDisposable> AcquireStreamAsync(CancellationToken cancellationToken)
    {
        await _streamGate.WaitAsync(cancellationToken);
        return new GateRelease(_streamGate);
    }

    private sealed class GateRelease : IDisposable
    {
        private SemaphoreSlim? _gate;
        public GateRelease(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public void Dispose() { _cancelCts.Cancel(); _cancelCts.Dispose(); _streamGate.Dispose(); }
}

public sealed record SharedFileEntry(string RelativePath, long SizeBytes, DateTimeOffset ModifiedUtc, string? TransferId = null, string? TransferStatus = null);

public sealed class ConnectedDevice
{
    public required string RemoteIpAddress { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? UserAgent { get; set; }
    public string? DeviceName { get; set; }
}

/// <summary>Request body for POST /api/upload/init.</summary>
public sealed class InitUploadRequest
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
}
