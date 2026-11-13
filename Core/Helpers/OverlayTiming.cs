using System.Diagnostics;
using System.IO;

namespace WinCal.Core.Helpers;

internal static class OverlayTiming
{
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable("WINCAL_TIMING_DIRECTORY");
    private static readonly object Sync = new();

    internal static void Write(string message)
    {
        if (string.IsNullOrEmpty(DirectoryPath)) return;
        try
        {
            lock (Sync)
                File.AppendAllText(Path.Combine(DirectoryPath, $"timing-{Environment.ProcessId}.log"),
                    $"{DateTime.UtcNow:HH:mm:ss.fff} {Stopwatch.GetTimestamp()} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
