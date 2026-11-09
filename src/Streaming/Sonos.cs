using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Streaming;

public sealed record SonosSpeaker(string Name, string IpAddress, string Uuid);

public sealed record TrackPosition(string Title, TimeSpan Elapsed, TimeSpan Duration, string? Uri);

public sealed class SonosException(string action, int errorCode, string description)
    : Exception($"{action}: {description}")
{
    public int ErrorCode { get; } = errorCode;
}

public sealed partial class SonosService(HttpClient http, StreamingOptions options, ILogger<SonosService> logger)
{
    private const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";
    private const string ZoneGroupTopology = "urn:schemas-upnp-org:service:ZoneGroupTopology:1";
    private const string AvTransportControlPath = "/MediaRenderer/AVTransport/Control";
    private const string InstanceId = "<InstanceID>0</InstanceID>";
    private const string SsdpMulticastAddress = "239.255.255.250";
    private static readonly byte[] SsdpSearchPayload = Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {SsdpMulticastAddress}:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 1\r\n" +
        "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    // Known addresses skip SSDP; useful when a firewall blocks the multicast replies.
    private readonly string[] _knownAddresses = options.SonosAddresses;
    private readonly int _discoveryPort = options.SonosDiscoveryPort;

    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private IReadOnlyList<SonosSpeaker> _cached = [];
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    [GeneratedRegex(@"^LOCATION:\s*(?<url>\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LocationHeader();

    [GeneratedRegex(@"<roomName>(?<name>[^<]*)</roomName>", RegexOptions.IgnoreCase)]
    private static partial Regex RoomName();

    [GeneratedRegex(@"<UDN>uuid:(?<uuid>[^<]*)</UDN>", RegexOptions.IgnoreCase)]
    private static partial Regex Udn();

    public async Task<IReadOnlyList<SonosSpeaker>> DiscoverAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cached.Count > 0 && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            return _cached;

        await _discoveryLock.WaitAsync(ct);
        try
        {
            if (!refresh && _cached.Count > 0 && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
                return _cached;

            var speakers = new List<SonosSpeaker>();
            async Task AddSpeakerAsync(Uri location)
            {
                var speaker = await DescribeAsync(location, ct);
                if (speaker is not null && !speakers.Any(s => s.Uuid == speaker.Uuid))
                    speakers.Add(speaker);
            }

            foreach (var address in _knownAddresses)
                await AddSpeakerAsync(new Uri($"http://{address}:1400/xml/device_description.xml")); // NOSONAR

            if (speakers.Count == 0) // NOSONAR
            {
                logger.LogInformation("Geen Sonos-speaker via bekende adressen; zoek via SSDP.");
                foreach (var location in await SearchAsync(ct))
                    await AddSpeakerAsync(location);
            }

            if (speakers.Count == 0) // NOSONAR
            {
                logger.LogWarning("Geen Sonos-speakers gevonden; controleer SSDP-multicast naar 239.255.255.250:1900, UDP-antwoorden op de gelogde lokale zoekpoort en HTTP-bereikbaarheid op poort 1400, of stel Streaming:Sonos:Addresses in.");
                if (_cached.Count > 0)
                {
                    logger.LogWarning("Behoud {SpeakerCount} eerder gevonden Sonos-speaker(s) na een lege discovery.", _cached.Count);
                    return _cached;
                }
            }
            else
                speakers = await ResolveGroupsAsync(speakers, ct);

            _cached = [.. speakers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    public async Task<SonosSpeaker?> ResolveAsync(string? nameOrIp, CancellationToken ct = default)
    {
        var speakers = await DiscoverAsync(ct: ct);
        if (string.IsNullOrWhiteSpace(nameOrIp))
            return speakers.FirstOrDefault();

        return speakers.FirstOrDefault(s =>
            s.IpAddress == nameOrIp ||
            s.Name.Equals(nameOrIp, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Contains(nameOrIp, StringComparison.OrdinalIgnoreCase));
    }

    public Task QueueUriAsync(SonosSpeaker speaker, string uri, string title, bool asNext = false, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "AddURIToQueue",
            $"{InstanceId}<EnqueuedURI>{Esc(uri)}</EnqueuedURI>" +
            $"<EnqueuedURIMetaData>{Esc(BuildMetadata(title))}</EnqueuedURIMetaData>" +
            $"<DesiredFirstTrackNumberEnqueued>{(asNext ? 1 : 0)}</DesiredFirstTrackNumberEnqueued>" +
            "<EnqueueAsNext>0</EnqueueAsNext>", ct);

    public Task ClearQueueAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "RemoveAllTracksFromQueue", InstanceId, ct);

    /// <summary>Points the speaker back at its own queue instead of a standalone stream.</summary>
    public Task SelectQueueAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "SetAVTransportURI",
            $"{InstanceId}<CurrentURI>x-rincon-queue:{speaker.Uuid}#0</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>", ct);

    public Task PlayAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "Play", $"{InstanceId}<Speed>1</Speed>", ct);

    /// <summary>NORMAL, REPEAT_ALL, SHUFFLE_NOREPEAT or SHUFFLE (random and repeating).</summary>
    public Task SetPlayModeAsync(SonosSpeaker speaker, string mode, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "SetPlayMode",
            $"{InstanceId}<NewPlayMode>{mode}</NewPlayMode>", ct);

    public Task PauseAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "Pause", InstanceId, ct);

    public Task StopAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "Stop", InstanceId, ct);

    public Task NextAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "Next", InstanceId, ct);

    public Task PreviousAsync(SonosSpeaker speaker, CancellationToken ct = default) =>
        SoapAsync(speaker, AvTransport, AvTransportControlPath, "Previous", InstanceId, ct);

    public Task SetVolumeAsync(SonosSpeaker speaker, int volume, CancellationToken ct = default) =>
        SoapAsync(speaker, RenderingControl, "/MediaRenderer/RenderingControl/Control", "SetVolume",
            $"{InstanceId}<Channel>Master</Channel><DesiredVolume>{Math.Clamp(volume, 0, 100)}</DesiredVolume>", ct);

    public async Task<string> GetTransportStateAsync(SonosSpeaker speaker, CancellationToken ct = default)
    {
        var xml = await SoapAsync(speaker, AvTransport, AvTransportControlPath, "GetTransportInfo", InstanceId, ct);
        var match = MyRegex().Match(xml);
        return match.Success ? match.Groups["state"].Value : "UNKNOWN";
    }

    public async Task<TrackPosition?> GetPositionInfoAsync(SonosSpeaker speaker, CancellationToken ct = default)
    {
        var xml = await SoapAsync(speaker, AvTransport, AvTransportControlPath, "GetPositionInfo", InstanceId, ct);

        var duration = ParseSonosTime(TrackDuration().Match(xml).Groups["time"].Value);
        var elapsed = ParseSonosTime(RelTime().Match(xml).Groups["time"].Value);
        if (duration is null)
            return null;

        var metadata = WebUtility.HtmlDecode(TrackMetaData().Match(xml).Groups["meta"].Value);
        // The title is DIDL-Lite text and therefore escaped twice: first the metadata, then the title itself.
        var title = TrackTitle().Match(metadata) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups["title"].Value) : "";
        var uri = TrackUri().Match(xml).Groups["uri"].Value;
        return new TrackPosition(title, elapsed ?? TimeSpan.Zero, duration.Value, string.IsNullOrWhiteSpace(uri) ? null : WebUtility.HtmlDecode(uri));
    }

    /// <summary>Sonos reports times as H:MM:SS; "NOT_IMPLEMENTED" means there is no active track.</summary>
    internal static TimeSpan? ParseSonosTime(string value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) ? span : null;

    private async Task<IReadOnlyList<Uri>> SearchAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        logger.LogInformation("SSDP zoekt via {LocalEndpoint} naar {MulticastAddress}:1900; UDP-antwoorden moeten op deze lokale zoekpoort binnenkomen.",
            udp.Client.LocalEndPoint, SsdpMulticastAddress);
        var target = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), 1900);

        var locations = new List<Uri>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        // Send several times; UDP multicast is easily lost on wifi.
        for (var i = 0; i < 3; i++)
            await udp.SendAsync(SsdpSearchPayload, target, ct);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                var text = Encoding.ASCII.GetString(result.Buffer);
                var match = LocationHeader().Match(text);
                if (match.Success && Uri.TryCreate(match.Groups["url"].Value.Trim(), UriKind.Absolute, out var uri)
                    && !locations.Contains(uri))
                {
                    locations.Add(uri);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Search window elapsed.
        }

        return locations;
    }

    /// <summary>
    /// Replaces the discovered speakers with the coordinator of each zone group: only that one accepts
    /// transport commands. If reading the topology fails, the flat list is kept.
    /// </summary>
    private async Task<List<SonosSpeaker>> ResolveGroupsAsync(List<SonosSpeaker> speakers, CancellationToken ct)
    {
        try
        {
            var response = await SoapAsync(speakers[0], ZoneGroupTopology, "/ZoneGroupTopology/Control",
                "GetZoneGroupState", string.Empty, ct);

            var state = WebUtility.HtmlDecode(ZoneGroupState().Match(response).Groups["state"].Value);
            var coordinators = new List<SonosSpeaker>();

            foreach (var group in ZoneGroup().Matches(state).Cast<Match>())
            {
                var coordinatorUuid = group.Groups["coordinator"].Value;
                string[] members = [.. ZoneGroupMember().Matches(group.Groups["members"].Value)
                    .Select(m => m.Groups["attributes"].Value)
                    .Where(attrs => Attribute(attrs, "Invisible") != "1")];

                var coordinator = members.FirstOrDefault(attrs => Attribute(attrs, "UUID") == coordinatorUuid);
                if (coordinator is null || !Uri.TryCreate(Attribute(coordinator, "Location"), UriKind.Absolute, out var location))
                    continue;

                var name = Attribute(coordinator, "ZoneName");
                if (members.Length > 1)
                    name += $" (+{members.Length - 1})";

                coordinators.Add(new SonosSpeaker(name, location.Host, coordinatorUuid));
            }

            return coordinators.Count > 0 ? coordinators : speakers;
        }
        catch (Exception ex) when (ex is SonosException or HttpRequestException)
        {
            logger.LogWarning(ex, "Kon de zonegroepen niet uitlezen; gebruik losse speakers");
            return speakers;
        }
    }

    private async Task<SonosSpeaker?> DescribeAsync(Uri location, CancellationToken ct)
    {
        try
        {
            var xml = await http.GetStringAsync(location, ct);
            var name = RoomName().Match(xml);
            var uuid = Udn().Match(xml);
            if (!name.Success || !uuid.Success)
                return null;

            return new SonosSpeaker(name.Groups["name"].Value, location.Host, uuid.Groups["uuid"].Value);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Kon speakerbeschrijving niet ophalen van {Location}", location);
            return null;
        }
    }

    private async Task<string> SoapAsync(SonosSpeaker speaker, string service, string path, string action, string arguments, CancellationToken ct)
    {
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body><u:{action} xmlns:u=\"{service}\">{arguments}</u:{action}></s:Body></s:Envelope>";

        // Sonos devices expose their local control API over HTTP by design.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{speaker.IpAddress}:1400{path}") // NOSONAR
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var code = int.TryParse(MyRegex1().Match(body).Groups["c"].Value, out var parsed) ? parsed : 0;
                logger.LogWarning("Sonos {Action} gaf {Status}: {Body}", action, (int)response.StatusCode, body);
                throw new SonosException(action, code, Describe(code));
            }
            return body;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Sonos {Action} timed out", action);
            throw new SonosException(action, 0, "Timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonos {Action} failed", action);
            throw new SonosException(action, 0, "Unknown error");
        }



    }

    internal static string Describe(int errorCode) => errorCode switch
    {
        701 => "De speaker kan nu niet van toestand wisselen (speelt er iets anders?)",
        711 => "Geen volgend of vorig nummer in de wachtrij",
        714 => "De speaker herkent dit bestandstype niet",
        716 => "De speaker kan het bestand niet ophalen; controleer de URL en de firewall",
        718 => "Onbekende zone op de speaker",
        _ => "Onbekende fout van de speaker"
    };

    internal static string BuildMetadata(string title) =>
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        "<item id=\"-1\" parentID=\"-1\" restricted=\"true\">" +
        $"<dc:title>{Esc(title)}</dc:title>" +
        "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
        "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">RINCON_AssociatedZPUDN</desc>" +
        "</item></DIDL-Lite>";

    private static string Esc(string value) => SecurityElement.Escape(value) ?? string.Empty;
    [GeneratedRegex("<CurrentTransportState>(?<state>[^<]*)</CurrentTransportState>")]
    private static partial Regex MyRegex();
    [GeneratedRegex("<errorCode>(?<c>[0-9]+)</errorCode>")]
    private static partial Regex MyRegex1();

    [GeneratedRegex("<ZoneGroupState>(?<state>.*?)</ZoneGroupState>", RegexOptions.Singleline)]
    private static partial Regex ZoneGroupState();

    [GeneratedRegex("<ZoneGroup Coordinator=\"(?<coordinator>[^\"]+)\"[^>]*>(?<members>.*?)</ZoneGroup>", RegexOptions.Singleline)]
    private static partial Regex ZoneGroup();

    [GeneratedRegex("<ZoneGroupMember(?<attributes>[^>]*)/?>")]
    private static partial Regex ZoneGroupMember();

    [GeneratedRegex("<TrackDuration>(?<time>[^<]*)</TrackDuration>")]
    private static partial Regex TrackDuration();

    [GeneratedRegex("<RelTime>(?<time>[^<]*)</RelTime>")]
    private static partial Regex RelTime();

    [GeneratedRegex("<TrackURI>(?<uri>[^<]*)</TrackURI>")]
    private static partial Regex TrackUri();

    [GeneratedRegex("<TrackMetaData>(?<meta>.*?)</TrackMetaData>", RegexOptions.Singleline)]
    private static partial Regex TrackMetaData();

    [GeneratedRegex("<dc:title>(?<title>[^<]*)</dc:title>")]
    private static partial Regex TrackTitle();

    private static string Attribute(string attributes, string name) =>
        Regex.Match(attributes, $"{name}=\"(?<v>[^\"]*)\"").Groups["v"].Value;
}
