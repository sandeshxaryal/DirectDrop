using DirectDrop.Core;
using DirectDrop.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DirectDrop.Server.Endpoints;

/// <summary>
/// Phone -> PC transfer path.
///
/// The data plane is intentionally one contiguous HTTP request, like the
/// simple LAN transfer model used by LocalSend-style applications. There is
/// no application-level chunk scheduler and no repackaging step. The browser
/// streams the selected File into this endpoint; ASP.NET Core streams the
/// request body directly into a temporary file. A dropped connection resumes
/// from the exact byte offset already present in that file.
/// </summary>
public static class UploadEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/upload");

        group.MapPost("/init", async (HttpRequest request, SessionState session) =>
        {
            InitUploadRequest? body;
            try { body = await request.ReadFromJsonAsync<InitUploadRequest>(); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest("Malformed JSON body."); }

            if (body is null || string.IsNullOrWhiteSpace(body.FileName) || body.FileSize <= 0)
                return Results.BadRequest("fileName and a positive fileSize are required.");

            string safeName = PathSecurity.SanitizeFileName(body.FileName);
            string uploadId = Guid.NewGuid().ToString("N");
            string tmpDir = Path.Combine(session.UploadDestination, ".directdrop-tmp");
            Directory.CreateDirectory(tmpDir);
            string partPath = Path.Combine(tmpDir, uploadId + ".part");

            // Start empty. We never pre-size or write holes into the file.
            await using (var fs = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 4 * 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            { await fs.FlushAsync(); }

            var pending = new PendingUpload(uploadId, safeName, body.FileSize, partPath);
            session.PendingUploads[uploadId] = pending;
            session.GetOrAddTransfer(uploadId, safeName, body.FileSize, TransferDirection.Upload);

            return Results.Ok(new { uploadId, offset = 0L, totalBytes = body.FileSize });
        });

        group.MapGet("/status/{uploadId}", (string uploadId, SessionState session) =>
        {
            if (!session.PendingUploads.TryGetValue(uploadId, out PendingUpload? pending))
                return Results.NotFound(new { message = "Unknown uploadId - call /api/upload/init again." });

            long offset = pending.BytesReceived;
            return Results.Ok(new { uploadId, totalBytes = pending.TotalBytes, offset, complete = offset == pending.TotalBytes });
        });

        // ONE contiguous stream. X-Upload-Offset is the only resume metadata.
        group.MapPost("/stream/{uploadId}", async (HttpRequest request, string uploadId, SessionState session, CancellationToken cancellationToken) =>
        {
            if (!session.PendingUploads.TryGetValue(uploadId, out PendingUpload? pending))
                return Results.NotFound(new { message = "Unknown uploadId." });

            if (!long.TryParse(request.Headers["X-Upload-Offset"], out long offset) || offset < 0 || offset > pending.TotalBytes)
                return Results.BadRequest("Missing or invalid X-Upload-Offset.");

            TransferItem transfer = session.GetOrAddTransfer(uploadId, pending.SafeFileName, pending.TotalBytes, TransferDirection.Upload);

            long requestedLength = request.ContentLength ?? -1;
            if (requestedLength > pending.TotalBytes - offset)
                return Results.BadRequest("The request body is larger than the remaining file size.");

            using var gate = await pending.AcquireStreamAsync(cancellationToken);
            long actualOffset = pending.BytesReceived;
            if (offset != actualOffset)
                return Results.Conflict(new { message = "Resume offset does not match the server's file length.", offset = actualOffset });

            if (actualOffset == pending.TotalBytes)
            {
                transfer.MarkCompleted();
                return Results.Ok(new { offset = actualOffset, totalBytes = pending.TotalBytes, complete = true });
            }

            TransferDirectionGate.Lease? directionLease = null;
            try
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pending.CancellationToken);
                CancellationToken transferCancellationToken = linkedCancellation.Token;
                directionLease = await session.AcquireTransferDirectionAsync(TransferDirection.Upload, transferCancellationToken);
                await using var fileStream = new FileStream(
                    pending.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.Read,
                    bufferSize: 4 * 1024 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                fileStream.Seek(offset, SeekOrigin.Begin);

                await using var progressBody = new UploadProgressReadStream(request.Body, transfer, offset);
                await progressBody.CopyToAsync(fileStream, 4 * 1024 * 1024, transferCancellationToken);
                await fileStream.FlushAsync(transferCancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (transfer.Status == TransferStatus.Cancelled)
                {
                    try { File.Delete(pending.PartFilePath); } catch { }
                    pending.Dispose();
                    return Results.StatusCode(499);
                }
                // Keep the bytes already written. The next request resumes from
                // the actual file length instead of restarting the transfer.
                transfer.Status = TransferStatus.Paused;
                return Results.StatusCode(499);
            }
            catch (IOException ex)
            {
                transfer.MarkFailed(ex.Message);
                return Results.Problem("Could not write the upload stream to disk.", statusCode: 500);
            }
            finally
            {
                directionLease?.Dispose();
            }

            long received = pending.BytesReceived;
            if (received >= pending.TotalBytes)
                transfer.MarkCompleted();
            else
                transfer.Status = TransferStatus.Paused;

            return Results.Ok(new { offset = received, totalBytes = pending.TotalBytes, complete = received == pending.TotalBytes });
        });

        group.MapPost("/complete/{uploadId}", (string uploadId, SessionState session) =>
        {
            if (!session.PendingUploads.TryGetValue(uploadId, out PendingUpload? pending))
                return Results.NotFound(new { message = "Unknown uploadId." });

            if (!pending.IsComplete)
                return Results.BadRequest(new { message = $"Expected {pending.TotalBytes} bytes but only {pending.BytesReceived} have arrived.", offset = pending.BytesReceived });

            session.PendingUploads.TryRemove(uploadId, out _);
            string destination = PathSecurity.MakeUniquePath(Path.Combine(session.UploadDestination, pending.SafeFileName));
            File.Move(pending.PartFilePath, destination, overwrite: false);

            if (session.Transfers.TryGetValue(uploadId, out TransferItem? transfer))
            {
                transfer.LocalPath = destination;
                transfer.MarkCompleted();
            }

            return Results.Ok(new { fileName = Path.GetFileName(destination) });
        });

        group.MapPost("/cancel/{uploadId}", (string uploadId, SessionState session) =>
        {
            if (!session.Transfers.TryGetValue(uploadId, out TransferItem? transfer))
                return Results.NotFound(new { message = "Unknown uploadId." });

            if (transfer.Direction != TransferDirection.Upload)
                return Results.BadRequest(new { message = "Transfer is not a phone-to-PC upload." });

            if (transfer.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
                return transfer.Status == TransferStatus.Cancelled
                    ? Results.Ok()
                    : Results.Conflict(new { message = "Transfer is no longer cancellable." });

            session.CancelTransfer(uploadId);
            return Results.Ok();
        });
    }

    private sealed class UploadProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly TransferItem _transfer;
        private readonly long _baseOffset;
        private long _read;
        private long _lastReportedBytes;
        private long _lastReportTimestamp;
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

        public UploadProgressReadStream(Stream inner, TransferItem transfer, long baseOffset)
        {
            _inner = inner; _transfer = transfer; _baseOffset = baseOffset;
            _transfer.UpdateProgress(baseOffset);
            _lastReportedBytes = baseOffset;
            _lastReportTimestamp = Environment.TickCount64;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await _inner.ReadAsync(buffer, cancellationToken);
            if (n > 0)
            {
                _read += n;
                long absolute = _baseOffset + _read;
                long now = Environment.TickCount64;
                if (absolute - Interlocked.Read(ref _lastReportedBytes) >= 256 * 1024 ||
                    now - Interlocked.Read(ref _lastReportTimestamp) >= 100)
                {
                    Interlocked.Exchange(ref _lastReportedBytes, absolute);
                    Interlocked.Exchange(ref _lastReportTimestamp, now);
                    _transfer.UpdateProgress(absolute);
                }
            }
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
