using DirectDrop.Core.Models;
using Xunit;

namespace DirectDrop.Tests;

/// <summary>
/// UpdateProgress is called concurrently by several chunk-upload requests
/// at once (parallel multi-connection upload), and those calls do not
/// necessarily arrive in byte order - these tests exist specifically to
/// pin down that the transferred-byte count can only ever move forward,
/// which is what keeps the PC's progress bar from visibly regressing or
/// diverging from the phone's own count mid-transfer.
/// </summary>
public sealed class TransferItemTests
{
    private static TransferItem NewTransfer(long totalBytes = 1_000_000) => new()
    {
        Id = "t1",
        FileName = "video.mp4",
        TotalBytes = totalBytes,
        Direction = TransferDirection.Upload,
    };

    [Fact]
    public void UpdateProgress_advances_the_total()
    {
        var transfer = NewTransfer();
        transfer.UpdateProgress(1000);
        Assert.Equal(1000, transfer.TransferredBytes);
        transfer.UpdateProgress(2000);
        Assert.Equal(2000, transfer.TransferredBytes);
    }

    [Fact]
    public void UpdateProgress_a_smaller_value_arriving_after_a_larger_one_does_not_regress_the_total()
    {
        // Simulates a slower parallel chunk's completion being observed
        // here after a faster one has already pushed the total further
        // ahead - the exact scenario several concurrent chunk uploads
        // produce in practice.
        var transfer = NewTransfer();
        transfer.UpdateProgress(5000);
        transfer.UpdateProgress(3000); // "stale" - smaller than what's already recorded

        Assert.Equal(5000, transfer.TransferredBytes);
    }

    [Fact]
    public void UpdateProgress_first_call_moves_status_out_of_queued()
    {
        var transfer = NewTransfer();
        Assert.Equal(TransferStatus.Queued, transfer.Status);

        transfer.UpdateProgress(1);

        Assert.Equal(TransferStatus.InProgress, transfer.Status);
    }

    [Fact]
    public async Task UpdateProgress_survives_many_concurrent_out_of_order_callers_at_the_max_value()
    {
        var transfer = NewTransfer(totalBytes: 10_000_000);

        // 200 concurrent "chunk completions" reporting cumulative totals in
        // a shuffled (not ascending) order - representative of several
        // parallel connections finishing in whatever order the network
        // happens to deliver them.
        var random = new Random(42);
        long[] cumulativeTotals = Enumerable.Range(1, 200).Select(i => (long)i * 50_000).ToArray();
        for (int i = cumulativeTotals.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (cumulativeTotals[i], cumulativeTotals[j]) = (cumulativeTotals[j], cumulativeTotals[i]);
        }

        var tasks = cumulativeTotals.Select(value => Task.Run(() => transfer.UpdateProgress(value)));
        await Task.WhenAll(tasks);

        Assert.Equal(cumulativeTotals.Max(), transfer.TransferredBytes);
    }

    [Fact]
    public void MarkCompleted_sets_transferred_bytes_to_total_and_status_to_completed()
    {
        var transfer = NewTransfer(totalBytes: 42);
        transfer.UpdateProgress(10);

        transfer.MarkCompleted();

        Assert.Equal(42, transfer.TransferredBytes);
        Assert.Equal(TransferStatus.Completed, transfer.Status);
        Assert.NotNull(transfer.CompletedAt);
    }
}
