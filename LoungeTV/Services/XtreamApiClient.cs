using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LoungeTV.Models;

namespace LoungeTV.Services;

/// <summary>
/// Implements the common Xtream-compatible Player API. Provider behaviour varies.
/// Never log complete API requests or playback URIs: they contain credentials.
/// </summary>
public sealed class XtreamApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IptvConnection _connection;

    public XtreamApiClient(IptvConnection connection)
    {
        _connection = connection;
        if (!Uri.TryCreate(connection.ServerUrl.Trim(), UriKind.Absolute, out var server) ||
            (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment) ||
            !string.IsNullOrEmpty(server.UserInfo))
        {
            throw new ArgumentException("Enter a valid http:// or https:// server URL, including its port if required.");
        }

        _baseUrl = connection.ServerUrl.Trim().TrimEnd('/');
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        }) { Timeout = TimeSpan.FromSeconds(35) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LoungeTV/1.0");
    }

    public async Task<IptvAccount> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(null, cancellationToken);
        var root = document.RootElement;
        if (!TryObjectProperty(root, "user_info", out var user))
            throw new InvalidDataException("Server did not return Xtream-compatible account information.");

        if (GetString(user, "auth") is not "1" and not "true")
            throw new UnauthorizedAccessException("Incorrect login details or account not authorised.");

        var status = GetString(user, "status") ?? "Unknown";
        if (!status.Equals("Active", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Subscription status: {status}.");

        var formats = new List<string>();
        if (user.TryGetProperty("allowed_output_formats", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in choices.EnumerateArray())
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value)
                    formats.Add(value.ToLowerInvariant());
        }
        return new IptvAccount(status, formats);
    }

    public async Task<IReadOnlyList<ChannelCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("get_live_categories", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Server returned an unexpected live-category response.");
        var categories = new List<ChannelCategory>();
        foreach (var category in document.RootElement.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(category, "category_id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            categories.Add(new ChannelCategory(id, GetString(category, "category_name") ?? $"Category {id}"));
        }
        return categories;
    }

    public async Task<IReadOnlyList<LiveChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("get_live_streams", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Server returned an unexpected live-channel response.");

        var channels = new List<LiveChannel>();
        foreach (var channel in document.RootElement.EnumerateArray())
        {
            if (channel.ValueKind != JsonValueKind.Object) continue;
            if (!long.TryParse(GetString(channel, "stream_id"), out var id) || id <= 0) continue;
            var name = GetString(channel, "name") ?? $"Channel {id}";
            _ = int.TryParse(GetString(channel, "num"), out var number);
            channels.Add(new LiveChannel(id, number, name, GetString(channel, "category_id") ?? "",
                GetString(channel, "stream_icon") ?? ""));
        }
        return channels.OrderBy(c => c.Number <= 0 ? int.MaxValue : c.Number).ThenBy(c => c.Name).ToArray();
    }

    public Uri GetStreamUri(long streamId, string extension)
    {
        if (streamId <= 0) throw new ArgumentOutOfRangeException(nameof(streamId));
        if (extension is not ("ts" or "m3u8")) throw new ArgumentException("Unsupported output format.");
        return new Uri($"{_baseUrl}/live/{Uri.EscapeDataString(_connection.Username)}/" +
                       $"{Uri.EscapeDataString(_connection.Password)}/{streamId}.{extension}");
    }

    private async Task<JsonDocument> GetJsonAsync(string? action, CancellationToken cancellationToken)
    {
        var query = $"username={Uri.EscapeDataString(_connection.Username)}" +
                    $"&password={Uri.EscapeDataString(_connection.Password)}" +
                    (action is null ? "" : $"&action={Uri.EscapeDataString(action)}");
        // The API uses credentials in the query string by design. Prefer HTTPS providers.
        var url = $"{_baseUrl}/player_api.php?{query}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryObjectProperty(JsonElement root, string name, out JsonElement element)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out element))
            return element.ValueKind == JsonValueKind.Object;
        element = default;
        return false;
    }

    private static string? GetString(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public void Dispose() => _http.Dispose();
}
