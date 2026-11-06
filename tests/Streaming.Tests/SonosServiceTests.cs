using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Streaming.Tests;

public sealed class SonosServiceTests
{
    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("NOT_IMPLEMENTED", null)]
    public void ParseSonosTime_ParsesOnlyValidDurations(string value, int? expectedSeconds)
    {
        var duration = SonosService.ParseSonosTime(value);

        Assert.Equal(expectedSeconds, duration is null ? null : (int)duration.Value.TotalSeconds);
    }

    [Fact]
    public void BuildMetadata_EscapesTitleForDidlLite()
    {
        var metadata = SonosService.BuildMetadata("Rock <live> & more");

        Assert.Contains("<dc:title>Rock &lt;live&gt; &amp; more</dc:title>", metadata);
    }

    [Fact]
    public void Describe_UsesSpecificAndFallbackSonosErrors()
    {
        Assert.Contains("bestand niet ophalen", SonosService.Describe(716));
        Assert.Equal("Onbekende fout van de speaker", SonosService.Describe(999));
    }

    [Fact]
    public async Task SetVolumeAsync_ClampsVolumeToSonosRange()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);
        var speaker = new SonosSpeaker("Living room", "192.0.2.10", "RINCON-123");

        await service.SetVolumeAsync(speaker, 140);

        Assert.Contains("<DesiredVolume>100</DesiredVolume>", handler.RequestBody);
        Assert.Equal("\"urn:schemas-upnp-org:service:RenderingControl:1#SetVolume\"", handler.SoapAction);
    }

    [Fact]
    public async Task QueueUriAsync_EscapesUriAndTitleInSoapRequest()
    {
        var handler = new RecordingHandler();
        var service = CreateService(handler);
        var speaker = new SonosSpeaker("Living room", "192.0.2.10", "RINCON-123");

        await service.QueueUriAsync(speaker, "http://example.test/a&b.mp3", "Rock <live> & more");

        Assert.Contains("http://example.test/a&amp;b.mp3", handler.RequestBody);
        Assert.Contains("Rock &amp;lt;live&amp;gt; &amp;amp; more", handler.RequestBody);
        Assert.Equal("http://192.0.2.10:1400/MediaRenderer/AVTransport/Control", handler.RequestUri);
    }

    private static SonosService CreateService(RecordingHandler handler)
    {
        var options = new StreamingOptions { MusicRoot = string.Empty, DatabasePath = string.Empty, SmtpPassword = "smtppass", SmtpUsername = "smtpuser" };
        return new SonosService(new HttpClient(handler), options, NullLogger<SonosService>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = null!;
        public string? RequestUri { get; private set; }
        public string? SoapAction { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            SoapAction = request.Headers.GetValues("SOAPACTION").Single();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<response />")
            };
        }
    }
}