using System.Net;
using System.Net.Http;
using System.Text;
using LoungeTV.Models;
using LoungeTV.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("EPG check failed: " + message);
}

// XMLTV carries explicit time zone offsets; conversion to UK clock is separate.
Check(XmlTvService.TryParseTimestamp("20260618180000 +0100", out var bst) &&
      bst == new DateTimeOffset(2026, 6, 18, 17, 0, 0, TimeSpan.Zero), "BST offset");
Check(XmlTvService.TryParseTimestamp("20260118180000 +0000", out var gmt) &&
      gmt == new DateTimeOffset(2026, 1, 18, 18, 0, 0, TimeSpan.Zero), "GMT offset");
Check(XmlTvService.TryParseTimestamp("20261016183000", out var noZone) &&
      noZone.Offset == TimeSpan.Zero && noZone.Hour == 18, "missing offset is UTC");
Check(!XmlTvService.TryParseTimestamp("invalid", out _), "invalid EPG timestamp");

var zone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
Check(TimeZoneInfo.ConvertTime(bst, zone).Hour == 18, "UK summer display");
Check(TimeZoneInfo.ConvertTime(gmt, zone).Hour == 18, "UK winter display");
Check(bst.AddMinutes(60) == new DateTimeOffset(2026, 6, 18, 18, 0, 0, TimeSpan.Zero),
    "manual +1 hour adjustment");

var now = DateTimeOffset.UtcNow;
var start = now.AddMinutes(-15).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
var end = now.AddMinutes(45).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
var xml = $"""
<tv><channel id="bbc.one"><display-name>BBC One HD</display-name></channel>
<programme channel="bbc.one" start="{start} +0000" stop="{end} +0000">
<title>Sample Show</title><desc>Test programme</desc></programme>
</tv>
""";

using var client = new HttpClient(new FakeHandler(xml));
var snapshot = await XmlTvService.LoadAsync(client, new Uri("https://example.org/xmltv.php"));
var channel = new LiveChannel(1, 101, "BBC One HD", "uk", "", "bbc.one");
Check(snapshot.ProgrammeCount == 1, "XMLTV count");
Check(snapshot.ForChannel(channel).Single().Title == "Sample Show", "EPG channel id");
var fallback = channel with { EpgChannelId = "" };
Check(snapshot.ForChannel(fallback).Count == 1, "XMLTV name fallback");
Console.WriteLine("EPG timestamp, UK timezone, shift, parse and mapping checks passed.");

sealed class FakeHandler(string xml) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        });
}
