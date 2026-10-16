namespace LoungeTV.Models;

public sealed record IptvAccount(string Status, IReadOnlyList<string> AllowedFormats);

public sealed record ChannelCategory(string Id, string Name)
{
    public string DisplayName => Name;
}

public sealed record LiveChannel(long Id, int Number, string Name, string CategoryId, string IconUrl, string EpgChannelId = "")
{
    public string DisplayName => Number > 0 ? $"{Number:000}   {Name}" : Name;
}

public sealed record IptvConnection(string ServerUrl, string Username, string Password);
