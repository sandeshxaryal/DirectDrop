using DirectDrop.Core;
using DirectDrop.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DirectDrop.Server.Endpoints;

/// <summary>
/// PC -> iPhone. Listing walks the configured "Shared" directory only (never
/// the whole filesystem - see PROJECT.md / README for why that folder is a
/// deliberately narrow, explicitly-populated directory rather than a raw
/// filesystem browser). Every path from the client is re-validated through
/// PathSecurity even though the list endpoint only ever hands back paths it
/// generated itself - defence in depth against a hand-crafted request.
///
/// enableRangeProcessing: true on Results.File is what gives Safari resumable
/// downloads and the ability to show a native progress/ETA UI - ASP.NET Core
/// handles parsing the Range header and returning 206 Partial Content itself.
/// </summary>
public static class DownloadEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/files", (SessionState session) =>
        {
            Directory.CreateDirectory(session.SharedFolder);
            string root = Path.GetFullPath(session.SharedFolder);

            List<SharedFileEntry> entries = session.PublishedSharedFiles.Keys
                .Select(relative =>
                {
                    string safePath;
                    try { safePath = PathSecurity.ResolveSafePath(root, relative); }
                    catch (UnauthorizedAccessException) { return null; }
                    if (!File.Exists(safePath)) return null;

                    var info = new FileInfo(safePath);
                    string transferId = session.PublishedSharedFiles.TryGetValue(relative, out string? publishedId) && !string.IsNullOrWhiteSpace(publishedId)
                        ? publishedId : "";
                    string? transferStatus = !string.IsNullOrWhiteSpace(transferId) && session.Transfers.TryGetValue(transferId, out TransferItem? publishedTransfer)
                        ? publishedTransfer.Status.ToString()
                        : null;
                    return new SharedFileEntry(relative, info.Length, info.LastWriteTimeUtc,
                        string.IsNullOrWhiteSpace(transferId) ? null : transferId, transferStatus);
                })
                .Where(e => e is not null)
                .Cast<SharedFileEntry>()
                .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(entries);
        });

        // Allows the phone to cancel a PC -> phone transfer. This is deliberately
        // a server-side operation: aborting the browser's request alone is not
        // enough because the desktop UI would otherwise keep seeing the transfer
        // as active until its next state transition.
        app.MapPost("/api/files/cancel/{transferId}", (string transferId, SessionState session) =>
        {
            if (!session.Transfers.TryGetValue(transferId, out TransferItem? transfer))
                return Results.NotFound(new { message = "Unknown transferId." });

            if (transfer.Direction != TransferDirection.Download)
                return Results.BadRequest(new { message = "Transfer is not a PC-to-phone download." });

            if (transfer.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
                return transfer.Status == TransferStatus.Cancelled
                    ? Results.Ok()
                    : Results.Conflict(new { message = "Transfer is no longer cancellable." });

            session.CancelTransfer(transferId);
            return Results.Ok();
        });

        async Task<IResult> DownloadFileAsync(HttpContext context, string path, SessionState session)
        {
            string safePath;
            try
            {
                safePath = PathSecurity.ResolveSafePath(session.SharedFolder, path);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!File.Exists(safePath))
                return Results.NotFound();

            var stream = new FileStream(
                safePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4 * 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            string downloadName = Path.GetFileName(safePath);
            string transferId = context.Request.Query["transferId"].FirstOrDefault()
                ?? ("dl-" + Guid.NewGuid().ToString("N"));

            TransferItem transfer = session.GetOrAddTransfer(transferId, downloadName, stream.Length, TransferDirection.Download);
            transfer.LocalPath = safePath;
            if (transfer.Status == TransferStatus.Cancelled)
            {
                stream.Dispose();
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            CancellationTokenSource transferCts = session.GetOrCreateTransferCancellation(transferId);
            if (transferCts.IsCancellationRequested)
            {
                stream.Dispose();
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            TransferDirectionGate.Lease? directionLease = null;
            try
            {
                using var linkedGateCancellation = CancellationTokenSource.CreateLinkedTokenSource(transferCts.Token, context.RequestAborted);
                directionLease = await session.AcquireTransferDirectionAsync(TransferDirection.Download, linkedGateCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                stream.Dispose();
                return transferCts.IsCancellationRequested
                    ? Results.StatusCode(StatusCodes.Status409Conflict)
                    : Results.StatusCode(499);
            }

            context.RequestAborted.Register(() =>
            {
                if (transfer.Status is TransferStatus.InProgress or TransferStatus.Queued)
                    transfer.Status = TransferStatus.Paused;
            });
            transfer.Status = TransferStatus.InProgress;
            context.Response.OnCompleted(() =>
            {
                directionLease?.Dispose();
                if (!context.RequestAborted.IsCancellationRequested
                    && context.Response.StatusCode == StatusCodes.Status200OK
                    && transfer.Status == TransferStatus.InProgress)
                    transfer.MarkCompleted();
                if (session.TransferCancellation.TryRemove(transferId, out CancellationTokenSource? completedCts))
                    completedCts.Dispose();
                return Task.CompletedTask;
            });

            // Large PC -> phone downloads should take the fastest path Kestrel
            // can provide: PhysicalFile lets ASP.NET Core use optimized file
            // serving/send-file handling instead of wrapping every read in the
            // progress stream. Browser-managed downloads do not need JS-side
            // byte telemetry, and the server still records authoritative start
            // and completion state. This is intentionally limited to large
            // files so the existing small/medium download progress path stays
            // unchanged.
            const long LargeDownloadThreshold = 1024L * 1024L * 1024L;
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";

            if (stream.Length > LargeDownloadThreshold)
            {
                stream.Dispose();
                return TypedResults.PhysicalFile(
                    safePath,
                    "application/octet-stream",
                    downloadName,
                    enableRangeProcessing: true);
            }

            var progressStream = new ProgressReportingStream(stream, transfer, transferCts.Token, context.RequestAborted);
            return Results.File(progressStream, "application/octet-stream", downloadName, enableRangeProcessing: true);
        }

        app.MapGet("/api/files/download", DownloadFileAsync);
    }
}
