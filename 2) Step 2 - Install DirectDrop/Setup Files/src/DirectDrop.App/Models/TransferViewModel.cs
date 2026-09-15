using System.IO;
using DirectDrop.Core.Models;

namespace DirectDrop.App.Models;

public sealed class TransferViewModel
{
    public required string FileName { get; init; }
    public required string FileIcon { get; init; }
    public required double PercentComplete { get; init; }
    public required string DirectionArrow { get; init; }
    public required string DetailLine { get; init; }
    public string? LocalPath { get; init; }
    public bool IsOpenable { get; init; }
    public required string TransferId { get; init; }
    public bool IsCancellable { get; init; }
    public bool IsBrowserManaged { get; init; }

    public static TransferViewModel From(TransferItem item)
    {
        bool isUpload = item.Direction == TransferDirection.Upload;
        bool isBrowserManaged = !isUpload && item.TotalBytes > 1024L * 1024L * 1024L;
        string direction = isUpload ? "Phone → PC" : "PC → Phone";
        string detail = item.Status switch
        {
            TransferStatus.Completed => $"{FormatBytes(item.TotalBytes)} · Complete",
            TransferStatus.Failed => $"Failed{(item.ErrorMessage is null ? "" : $" · {item.ErrorMessage}")}",
            TransferStatus.Cancelled => "Cancelled",
            TransferStatus.Paused => $"{FormatBytes(item.TransferredBytes)} / {FormatBytes(item.TotalBytes)} · Paused",
            _ => BuildInProgressDetail(item),
        };

        return new TransferViewModel
        {
            FileName = item.FileName,
            FileIcon = GetFileIcon(item.FileName),
            PercentComplete = item.PercentComplete,
            DirectionArrow = direction,
            DetailLine = isBrowserManaged ? "Downloads in phone browser" : detail,
            LocalPath = item.LocalPath,
            IsOpenable = !string.IsNullOrWhiteSpace(item.LocalPath) && File.Exists(item.LocalPath),
            TransferId = item.Id,
            IsCancellable = !isBrowserManaged && (item.Status is TransferStatus.Queued or TransferStatus.InProgress or TransferStatus.Paused),
            IsBrowserManaged = isBrowserManaged,
        };
    }

    private static string GetFileIcon(string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" or "png" or "heic" or "gif" or "webp" or "bmp" => "▧",
            "mp4" or "mov" or "m4v" or "avi" or "mkv" or "webm" => "▶",
            "mp3" or "m4a" or "wav" or "aac" or "flac" => "♪",
            "pdf" => "PDF",
            "zip" or "rar" or "7z" or "tar" or "gz" => "▣",
            _ => "□",
        };
    }

    private static string BuildInProgressDetail(TransferItem item)
    {
        string size = $"{FormatBytes(item.TransferredBytes)} / {FormatBytes(item.TotalBytes)}";
        string speed = FormatBytes((long)item.CurrentBytesPerSecond) + "/s";
        string eta = item.EstimatedTimeRemaining is { } remaining ? FormatEta(remaining) : "—";
        return $"{size} · {speed} · ETA {eta}";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatEta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m" :
        eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m {eta.Seconds}s" : $"{eta.Seconds}s";
}
