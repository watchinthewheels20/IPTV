using System;
using System.IO;

namespace LoungeTV.Services;

/// <summary>
/// Stores only the numeric ID of the most recently requested channel.
/// No credentials, server name, or video URI are written.
/// </summary>
internal static class RecentChannelStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LoungeTV", "recent-channel.txt");

    public static long Load()
    {
        try
        {
            return File.Exists(FilePath) && long.TryParse(File.ReadAllText(FilePath), out var id)
                ? id : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public static void Save(long channelId)
    {
        if (channelId <= 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, channelId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
