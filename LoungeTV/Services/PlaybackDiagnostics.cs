using System;
using System.IO;
using System.Text;

namespace LoungeTV.Services;

/// <summary>
/// Minimal privacy-safe playback trace. Callers must never pass full stream URLs,
/// Xtream credentials, raw LibVLC messages, or exception messages.
/// </summary>
internal static class PlaybackDiagnostics
{
    private static readonly object Sync = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoungeTV", "playback.log");

    public static void Note(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // Prevent an indefinitely growing log.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                    File.Move(LogPath, LogPath + ".previous", true);

                File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never break playback or show secrets.
        }
    }

    public static void Failure(string operation, Exception exception) =>
        Note($"{operation}: {exception.GetType().Name}");
}
