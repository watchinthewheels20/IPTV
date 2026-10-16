using System;
using System.Collections.Generic;

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

    public IReadOnlyList<EpgProgramme> ForChannel(LiveChannel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.EpgChannelId) &&
            ByChannel.TryGetValue(channel.EpgChannelId, out var direct)) return direct;

        if (ChannelIdByName.TryGetValue(channel.Name.Trim(), out var id) &&
            ByChannel.TryGetValue(id, out var byName)) return byName;

        return Array.Empty<EpgProgramme>();
    }
}
