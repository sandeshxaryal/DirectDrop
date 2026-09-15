namespace DirectDrop.Core.Models;

public enum TransferDirection { Upload, Download }

public enum TransferStatus { Queued, InProgress, Paused, Completed, Failed, Cancelled }

/// <summary>
/// Tracks one file transfer (in either direction) so the desktop UI and web
/// UI can show the same live progress, speed and ETA. Progress is monotonic
/// so reconnects/resumes can never make the transfer move backwards.
/// </summary>
public sealed class TransferItem
{
    // How far back the "current speed" window looks. Long enough to smooth
    // over the burstiness of individual network reads, short
    // enough to actually track real changes in throughput within a second
    // or two.
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    // UpdateProgress can be called very frequently (every network read on a
    // download can be a few times per second, or more). Coalescing samples
    // that land within this gap into a single "latest" point keeps the
    // sample list tiny without losing accuracy.
    private static readonly TimeSpan MinSampleGap = TimeSpan.FromMilliseconds(200);

    private long _transferredBytes;
    private readonly object _speedLock = new();

    // Rolling buffer of (timestamp, cumulative bytes) samples, oldest first,
    // trimmed to SpeedWindow on every update. This is what CurrentBytesPerSecond
    // is computed from - see its doc comment for why this exists instead of a
    // single lifetime average.
    private readonly List<(DateTimeOffset Time, long Bytes)> _recentSamples = new();

    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required long TotalBytes { get; init; }
    public required TransferDirection Direction { get; init; }
    public TransferStatus Status { get; set; } = TransferStatus.Queued;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LocalPath { get; set; }

    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public double PercentComplete => TotalBytes <= 0
        ? 0
        : Math.Clamp(TransferredBytes * 100.0 / TotalBytes, 0, 100);

    /// <summary>
    /// Bytes-per-second averaged over the *entire* transfer so far. Kept
    /// around as the fallback CurrentBytesPerSecond uses before enough
    /// recent samples exist (e.g. the first fraction of a second), but this
    /// is deliberately NOT what the UI shows as "speed" any more: a
    /// lifetime average can only ever decay towards the long-run rate once
    /// an early burst (buffered bytes flushing quickly, TCP slow-start,
    /// etc.) makes the first reading high, which is exactly the "starts too
    /// fast then keeps dropping" symptom this class used to produce even
    /// while the real network throughput was steady.
    /// </summary>
    public double AverageBytesPerSecond
    {
        get
        {
            double elapsed = (DateTimeOffset.UtcNow - StartedAt).TotalSeconds;
            return elapsed <= 0 ? 0 : TransferredBytes / elapsed;
        }
    }

    /// <summary>
    /// Bytes-per-second measured over just the last few seconds
    /// (<see cref="SpeedWindow"/>), not since the transfer started. This is
    /// the number the UI should display: it reflects what the connection is
    /// doing *right now*, so it can go up as well as down, instead of only
    /// ever trending downward the way a since-the-start average does.
    /// </summary>
    public double CurrentBytesPerSecond
    {
        get
        {
            lock (_speedLock)
            {
                if (_recentSamples.Count < 2) return AverageBytesPerSecond;

                (DateTimeOffset Time, long Bytes) first = _recentSamples[0];
                (DateTimeOffset Time, long Bytes) last = _recentSamples[^1];
                double elapsed = (last.Time - first.Time).TotalSeconds;
                return elapsed <= 0 ? AverageBytesPerSecond : (last.Bytes - first.Bytes) / elapsed;
            }
        }
    }

    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            double speed = CurrentBytesPerSecond;
            if (speed <= 0 || Status != TransferStatus.InProgress) return null;
            long remaining = TotalBytes - TransferredBytes;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / speed);
        }
    }

    /// <summary>
    /// Advances the authoritative transferred-byte count, never backwards.
    /// This is important when a connection is interrupted and resumed or when
    /// a browser and desktop UI observe the same transfer at slightly
    /// different times.
    /// </summary>
    public void UpdateProgress(long transferredBytes)
    {
        long current = Interlocked.Read(ref _transferredBytes);
        while (transferredBytes > current)
        {
            long observed = Interlocked.CompareExchange(ref _transferredBytes, transferredBytes, current);
            if (observed == current) { current = transferredBytes; break; }
            current = observed; // another concurrent call moved it further - retry against that
        }

        if (Status is TransferStatus.Queued or TransferStatus.Paused) Status = TransferStatus.InProgress;

        // Sample the true current total (not the possibly-stale value this
        // particular call arrived with) so the speed window never ingests a
        // backward-looking point either.
        RecordSpeedSample(Interlocked.Read(ref _transferredBytes));
    }

    private void RecordSpeedSample(long transferredBytes)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_speedLock)
        {
            if (_recentSamples.Count > 0 && now - _recentSamples[^1].Time < MinSampleGap)
            {
                // Too soon since the last point - just move it forward
                // rather than growing the list on every single read.
                _recentSamples[^1] = (now, transferredBytes);
            }
            else
            {
                _recentSamples.Add((now, transferredBytes));
            }

            // Trim from the front, but always leave one sample at or before
            // the window cutoff so there's a valid "start" point to measure
            // from - otherwise the window keeps shrinking to nothing.
            DateTimeOffset cutoff = now - SpeedWindow;
            int trim = 0;
            while (trim < _recentSamples.Count - 1 && _recentSamples[trim + 1].Time < cutoff)
                trim++;
            if (trim > 0) _recentSamples.RemoveRange(0, trim);
        }
    }

    public void MarkCompleted()
    {
        Interlocked.Exchange(ref _transferredBytes, TotalBytes);
        Status = TransferStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        Status = TransferStatus.Failed;
        ErrorMessage = reason;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
