using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Streaming.Tests;

public sealed class StreamingOptionsTests
{
    [Fact]
    public void FromConfiguration_ReadsSonosAddressesFromJsonArray()
    {
        var json = """{"Streaming":{"Sonos":{"Addresses":["192.168.1.211","192.168.1.212"],"DiscoveryPort":1901}}}"""u8;
        using var jsonStream = new MemoryStream(json.ToArray());
        var configuration = CreateConfigurationBuilder().AddJsonStream(jsonStream).Build();

        var options = StreamingOptions.FromConfiguration(configuration, new TestEnvironment());

        Assert.Equal([ "192.168.1.211", "192.168.1.212" ], options.SonosAddresses);
        Assert.Equal(1901, options.SonosDiscoveryPort);
    }

    [Fact]
    public void FromConfiguration_ReadsAlsaMixerSettings()
    {
        var configuration = CreateConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Streaming:Playback:AlsaMixerPath"] = "/usr/bin/amixer",
            ["Streaming:Playback:AlsaPlaybackDevice"] = "plughw:CARD=DAC,DEV=0",
            ["Streaming:Playback:AlsaMixerDevice"] = "pulse",
            ["Streaming:Playback:AlsaVolumeControl"] = "PCM"
        }).Build();

        var options = StreamingOptions.FromConfiguration(configuration, new TestEnvironment());

        Assert.Equal("/usr/bin/amixer", options.AlsaMixerPath);
        Assert.Equal("plughw:CARD=DAC,DEV=0", options.AlsaPlaybackDevice);
        Assert.Equal("pulse", options.AlsaMixerDevice);
        Assert.Equal("PCM", options.AlsaVolumeControl);
    }

    [Fact]
    public void FromConfiguration_UsesDefaultAlsaMixerSettings()
    {
        var options = StreamingOptions.FromConfiguration(CreateConfigurationBuilder().Build(), new TestEnvironment());

        Assert.Equal("amixer", options.AlsaMixerPath);
        Assert.Null(options.AlsaPlaybackDevice);
        Assert.Equal("default", options.AlsaMixerDevice);
        Assert.Equal("Master", options.AlsaVolumeControl);
    }

    [Theory]
    [InlineData("192.168.1.211", new[] { "192.168.1.211" })]
    [InlineData(" 192.168.1.211, ,192.168.1.212 ", new[] { "192.168.1.211", "192.168.1.212" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void FromConfiguration_ReadsScalarSonosAddresses(string? addresses, string[] expected)
    {
        var configuration = CreateConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Streaming:Sonos:Addresses"] = addresses }).Build();

        var options = StreamingOptions.FromConfiguration(configuration, new TestEnvironment());

        Assert.Equal(expected, options.SonosAddresses);
        Assert.Equal(0, options.SonosDiscoveryPort);
    }

    [Fact]
    public void FromConfiguration_ScalarOverrideReplacesJsonSonosAddresses()
    {
        using var json = new MemoryStream("""{"Streaming":{"Sonos":{"Addresses":["192.168.1.211","192.168.1.212"]}}}"""u8.ToArray());
        var configuration = CreateConfigurationBuilder()
            .AddJsonStream(json)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Streaming:Sonos:Addresses"] = "192.168.1.213"
            })
            .Build();

        var options = StreamingOptions.FromConfiguration(configuration, new TestEnvironment());

        Assert.Equal(["192.168.1.213"], options.SonosAddresses);
    }

    [Fact]
    public void FromConfiguration_MissingSmtpHost_ThrowsInvalidOperationException()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Username"] = "smtpuser",
            ["Smtp:Password"] = "smtppass"
        }).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StreamingOptions.FromConfiguration(configuration, new TestEnvironment()));

        Assert.Equal("Smtp:Host variable required", exception.Message);
    }

    private static ConfigurationBuilder CreateConfigurationBuilder()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.example.com",
            ["Smtp:Username"] = "smtpuser",
            ["Smtp:Password"] = "smtppass"
        });
        return builder;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Streaming.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
