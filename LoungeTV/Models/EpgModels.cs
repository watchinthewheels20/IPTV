using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LoungeTV.Models;

public sealed record EpgProgramme(
    string ChannelId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Title,
    string Description);

public sealed class EpgSnapshot
{
    public static EpgSnapshot Empty { get; } = new(new Dictionary<string, List<EpgProgramme>>(
        StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, List<EpgProgramme>> ByChannel { get; }
    public IReadOnlyDictionary<string, string> ChannelIdByName { get; }
    public int ProgrammeCount { get; }

    public EpgSnapshot(Dictionary<string, List<EpgProgramme>> programmes,
                       Dictionary<string, string> channelIdsByName)
    {
        foreach (var items in programmes.Values)
            items.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        ByChannel = programmes;
        ChannelIdByName = channelIdsByName;
        foreach (var items in programmes.Values) ProgrammeCount += items.Count;
    }

    public static string NormaliseChannelName(string value)
    {
        var name = Regex.Replace(value.Trim(), @"^(UK|GB)\\s*[:|]\\s*", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"\\s+(FHD|UHD|HD|SD|4K)$", "", RegexOptions.IgnoreCase).Trim();
    }

    public IReadOnlyList<EpgProgramme> ForChannel(LiveChannel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.EpgChannelId) &&
            ByChannel.TryGetValue(channel.EpgChannelId, out var direct)) return direct;

        var numericId = channel.Id.ToString(CultureInfo.InvariantCulture);
        if (ByChannel.TryGetValue(numericId, out var numeric)) return numeric;

        if (ChannelIdByName.TryGetValue(channel.Name.Trim(), out var id) &&
            ByChannel.TryGetValue(id, out var byName)) return byName;

        var normalised = NormaliseChannelName(channel.Name);
        if (ChannelIdByName.TryGetValue(normalised, out id) &&
            ByChannel.TryGetValue(id, out var fallback)) return fallback;

        return Array.Empty<EpgProgramme>();
    }
}
