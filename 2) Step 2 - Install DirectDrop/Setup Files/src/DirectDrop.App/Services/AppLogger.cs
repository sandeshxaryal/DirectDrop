using System;
using System.IO;

namespace DirectDrop.App.Services;

/// <summary>
/// Writes to %LOCALAPPDATA%\DirectDrop\Logs\directdrop.log. Deliberately
/// dumb (no external logging framework/NuGet dependency needed) - this is a
/// troubleshooting aid, not telemetry, and nothing here ever leaves the PC.
///
/// Hard rule: never pass file contents, filenames from inside a user's
/// personal files, or anything that could be sensitive into these methods.
/// Log *that* a transfer happened, an IP address, a port, a byte count - not
/// file contents.
/// </summary>
public static class AppLogger
{
    private static readonly object Lock = new();
    private static readonly string LogPath;

    static AppLogger()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DirectDrop", "Logs");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "directdrop.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";

        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never crash the app. If the log file is
                // locked or the disk is full, we just lose this line.
            }
        }

        // Also mirror to Debug output so it shows up while running under a
        // debugger - harmless in a Release build (Debug.WriteLine no-ops).
        System.Diagnostics.Debug.WriteLine(line);
    }
}
