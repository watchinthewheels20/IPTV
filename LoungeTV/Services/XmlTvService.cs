using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using LoungeTV.Models;

namespace LoungeTV.Services;

/// <summary>Streams and parses Xtream XMLTV without loading the entire XML document into RAM.</summary>
public static class XmlTvService
{
    private const int MaxProgrammes = 250_000;
    private static readonly string[] DateFormats = { "yyyyMMddHHmmss", "yyyyMMddHHmm" };

    public static async Task<EpgSnapshot> LoadAsync(
        HttpClient http, Uri xmltvUri, IReadOnlyCollection<LiveChannel>? activeChannels = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(xmltvUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 750_000_000
        });

        var programmes = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (activeChannels is not null)
        {
            foreach (var channel in activeChannels)
            {
                if (!string.IsNullOrWhiteSpace(channel.EpgChannelId)) ids.Add(channel.EpgChannelId);
                ids.Add(channel.Id.ToString(CultureInfo.InvariantCulture));
                activeNames.Add(channel.Name.Trim());
                activeNames.Add(EpgSnapshot.NormaliseChannelName(channel.Name));
            }
        }

        var min = DateTimeOffset.UtcNow.AddHours(-12);
        var max = DateTimeOffset.UtcNow.AddDays(4);
        var count = 0;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "channel")
            {
                var id = reader.GetAttribute("id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                using var subtree = reader.ReadSubtree();
                var element = await XElement.LoadAsync(subtree, LoadOptions.None, cancellationToken);
                foreach (var name in element.Elements().Where(x => x.Name.LocalName == "display-name"))
                {
                    var text = name.Value.Trim();
                    if (text.Length == 0) continue;
                    var normalised = EpgSnapshot.NormaliseChannelName(text);
                    if (activeChannels is null || activeNames.Contains(text) || activeNames.Contains(normalised))
                    {
                        names.TryAdd(text, id);
                        names.TryAdd(normalised, id);
                        ids.Add(id);
                    }
                }
            }
            else if (reader.LocalName == "programme")
            {
                var id = reader.GetAttribute("channel");
                if (string.IsNullOrWhiteSpace(id) ||
                    (activeChannels is not null && !ids.Contains(id)) ||
                    !TryParseTimestamp(reader.GetAttribute("start"), out var start) ||
                    !TryParseTimestamp(reader.GetAttribute("stop"), out var end) ||
                    end <= start || start > max || end < min)
                {
                    // Do not parse irrelevant programmes or invalid times.
                    continue;
                }
                using var subtree = reader.ReadSubtree();
                var element = await XElement.LoadAsync(subtree, LoadOptions.None, cancellationToken);
                var title = element.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value.Trim();
                var description = element.Elements().FirstOrDefault(x => x.Name.LocalName == "desc")?.Value.Trim();
                if (!programmes.TryGetValue(id, out var list))
                    programmes[id] = list = new List<EpgProgramme>();
                list.Add(new EpgProgramme(id, start, end,
                    string.IsNullOrWhiteSpace(title) ? "Programme information unavailable" : title,
                    description ?? string.Empty));
                if (++count >= MaxProgrammes) break;
            }
        }

        return new EpgSnapshot(programmes, names);
    }

    public static bool TryParseTimestamp(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!DateTime.TryParseExact(parts[0], DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)) return false;

        // XMLTV dates are UTC unless an explicit offset follows the timestamp.
        TimeSpan offset = TimeSpan.Zero;
        if (parts.Length > 1)
        {
            var zone = parts[1].Trim();
            if (zone.Equals("BST", StringComparison.OrdinalIgnoreCase))
                offset = TimeSpan.FromHours(1);
            else if (zone.Equals("GMT", StringComparison.OrdinalIgnoreCase) ||
                     zone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
                offset = TimeSpan.Zero;
            else if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') &&
                     int.TryParse(zone.AsSpan(1, 2), out var h) &&
                     int.TryParse(zone.AsSpan(3, 2), out var m) &&
                     h <= 14 && m < 60)
                offset = new TimeSpan(h, m, 0) * (zone[0] == '-' ? -1 : 1);
            else return false;
        }

        try { utc = new DateTimeOffset(local, offset).ToUniversalTime(); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
