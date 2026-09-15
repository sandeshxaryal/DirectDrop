using System.Collections.Concurrent;

namespace DirectDrop.Core.Models;

/// <summary>
/// Everything that exists for the lifetime of one "Start Direct Wi-Fi" session.
/// A new instance is created each time the user presses Start, and discarded
/// (along with its token, pending uploads, and transfer history) when they
/// press Stop. Registered as a singleton in the server's DI container so both
/// the ASP.NET Core endpoints and the WPF UI (in-process) share one instance.
/// </summary>
public sealed class SessionState
{
    // Windows' Mobile Hotspot API has no public, settable "max 1 client"
    // knob - NetworkOperatorTetheringManager.MaxClientCount is read-only
    // (it reports a ceiling, it doesn't let an app impose one). So "only one
    // device at a time" is enforced here instead: whichever device
    // authenticates first with the session token holds the slot, and every
    // other IP gets a clear 409 until that device goes quiet for
    // PairedDeviceTimeout (dropped Wi-Fi, closed the tab, etc.), at which
    // point the slot frees up for whoever asks next. This is also what
    // keeps transfer speed from being split: a second phone can join the
    // Wi-Fi radio itself (nothing stops that at the OS level), but it can
    // never get past this gate to actually move bytes.
    //
    // 60s, not the original 25s: this only gets refreshed when a NEW
    // request from the paired IP starts (see TokenAuthMiddleware), not
    // continuously while one is in flight. app.js's own /api/session and
    // /api/files polling (every 4-5s) normally keeps this warm regardless
    // of transfer activity, but a backgrounded/locked phone browser can
    // have its timers throttled by the OS mid-download - and losing this
    // race would 409 the very next chunk/range request from the SAME
    // device, which looks exactly like an unrecoverable stall from the
    // phone's side. 60s gives real margin over that polling interval
    // without meaningfully weakening the single-device guarantee.
    private static readonly TimeSpan PairedDeviceTimeout = TimeSpan.FromSeconds(60);
    private readonly object _pairLock = new();
    private string? _pairedDeviceIp;
    private DateTimeOffset _pairedDeviceLastSeenUtc;

    public required string Token { get; init; }
    public required string SharedFolder { get; init; }
    public required string UploadDestination { get; init; }
    public required string PcDisplayName { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public ConcurrentDictionary<string, PendingUpload> PendingUploads { get; } = new();
    public ConcurrentDictionary<string, TransferItem> Transfers { get; } = new();
    public ConcurrentDictionary<string, ConnectedDevice> ConnectedDevices { get; } = new();

    // Only files explicitly added to DirectDrop during this session are
    // published to the phone. This prevents a fresh session from treating
    // old files left in the Shared folder as new work and auto-downloading
    // them again. The key is a normalized relative path using '/'.
    public ConcurrentDictionary<string, string> PublishedSharedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Server-side cancellation for PC → phone responses. Setting TransferItem.Status
    // alone only changes the UI; this CTS is what actually interrupts the active
    // Kestrel response stream.
    public ConcurrentDictionary<string, CancellationTokenSource> TransferCancellation { get; } = new();

    // Maximize throughput on typical Windows Mobile Hotspot adapters: same-direction
    // transfers may share the link, while opposite directions take turns.
    public TransferDirectionGate TransferDirectionGate { get; } = new();

    public Task<TransferDirectionGate.Lease> AcquireTransferDirectionAsync(TransferDirection direction, CancellationToken cancellationToken) =>
        TransferDirectionGate.AcquireAsync(direction, cancellationToken);

    public string QueueOutgoingTransfer(string fileName, long fileSize)
    {
        string id = "dl-" + Guid.NewGuid().ToString("N");
        GetOrAddTransfer(id, fileName, fileSize, TransferDirection.Download);
        return id;
    }

    public void RegisterSharedFile(string fullPath, string? transferId = null)
    {
        string root = Path.GetFullPath(SharedFolder);
        string full = Path.GetFullPath(fullPath);
        string relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The shared file is outside the DirectDrop Shared folder.");
        PublishedSharedFiles[relative.Replace(Path.DirectorySeparatorChar, '/')] = transferId ?? "";
    }

    /// <summary>Best-effort record of IPs that tried to connect while another device held the slot, so the UI can surface it.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> BlockedDeviceAttempts { get; } = new();

    public string? PairedDeviceIp
    {
        get { lock (_pairLock) return _pairedDeviceIp; }
    }

    public TransferItem GetOrAddTransfer(string id, string fileName, long totalBytes, TransferDirection direction) =>
        Transfers.GetOrAdd(id, _ => new TransferItem
        {
            Id = id,
            FileName = fileName,
            TotalBytes = totalBytes,
            Direction = direction,
        });

    public void NoteDeviceSeen(string remoteIp, string? userAgent)
    {
        ConnectedDevices.AddOrUpdate(
            remoteIp,
            _ => new ConnectedDevice { RemoteIpAddress = remoteIp, UserAgent = userAgent, DeviceName = DeviceNameParser.GetFriendlyName(userAgent) },
            (_, existing) =>
            {
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
                if (userAgent is not null)
                {
                    existing.UserAgent = userAgent;
                    existing.DeviceName = DeviceNameParser.GetFriendlyName(userAgent);
                }
                return existing;
            });
    }

    /// <summary>
    /// Claims the single-device slot for <paramref name="remoteIp"/>, or
    /// confirms it already holds it. Returns false if a different IP holds
    /// the slot and has been seen within <see cref="PairedDeviceTimeout"/> -
    /// the caller should reject the request in that case.
    /// </summary>
    public bool TryAuthorizeDevice(string remoteIp)
    {
        lock (_pairLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool slotFree = _pairedDeviceIp is null || now - _pairedDeviceLastSeenUtc > PairedDeviceTimeout;

            if (!slotFree && _pairedDeviceIp != remoteIp)
                return false;

            _pairedDeviceIp = remoteIp;
            _pairedDeviceLastSeenUtc = now;
            return true;
        }
    }

    public CancellationTokenSource GetOrCreateTransferCancellation(string transferId) =>
        TransferCancellation.GetOrAdd(transferId, _ => new CancellationTokenSource());

    public void CancelTransfer(string transferId)
    {
        if (Transfers.TryGetValue(transferId, out TransferItem? transfer))
        {
            transfer.Status = TransferStatus.Cancelled;
            transfer.CompletedAt = DateTimeOffset.UtcNow;
        }

        if (TransferCancellation.TryGetValue(transferId, out CancellationTokenSource? cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (PendingUploads.TryRemove(transferId, out PendingUpload? pending))
        {
            // Signal the active request and remove the partial file. Do not
            // dispose the PendingUpload here: an in-flight upload endpoint may
            // still be holding its semaphore/CTS and will dispose it after its
            // cancellation path unwinds. Disposing it here creates a race where
            // the request's gate release can throw ObjectDisposedException.
            pending.Cancel();
            try { File.Delete(pending.PartFilePath); } catch (IOException) { }
        }
    }

    /// <summary>Best-effort temp-file cleanup on Stop. Never throws - logging is the caller's job.</summary>
    public void CleanupSessionShareFolder()
    {
        try
        {
            if (Directory.Exists(SharedFolder))
                Directory.Delete(SharedFolder, recursive: true);
        }
        catch
        {
            // Best effort. A later session-start cleanup retries it.
        }
    }

    public void CleanupTempFiles()
    {
        string tmpDir = Path.Combine(UploadDestination, ".directdrop-tmp");
        if (!Directory.Exists(tmpDir)) return;

        foreach (string file in Directory.EnumerateFiles(tmpDir))
        {
            try { File.Delete(file); } catch { /* best-effort */ }
        }
    }
}
