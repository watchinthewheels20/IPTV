using System;
using System.Globalization;
using System.IO;

namespace LoungeTV.Services;

public static class EpgOffsetStore
{
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoungeTV", "epg-offset.txt");

    public static int LoadMinutes()
    {
        try
        {
            return File.Exists(PathName) &&
                   int.TryParse(File.ReadAllText(PathName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
                   minutes >= -360 && minutes <= 360 && minutes % 30 == 0 ? minutes : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public static void SaveMinutes(int minutes)
    {
        if (minutes is < -360 or > 360 || minutes % 30 != 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, minutes.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
