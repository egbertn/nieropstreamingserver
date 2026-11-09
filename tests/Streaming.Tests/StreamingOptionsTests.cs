using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Streaming.Tests;

public sealed class StreamingOptionsTests
{
    [Fact]
    public void FromConfiguration_ReadsSonosAddressesFromJsonArray()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Streaming":{"Sonos":{"Addresses":["192.168.1.211","192.168.1.212"],"DiscoveryPort":1901}}}"""));
        var configuration = CreateConfigurationBuilder().AddJsonStream(json).Build();

        var options = StreamingOptions.FromConfiguration(configuration, new TestEnvironment());

        Assert.Equal(["192.168.1.211", "192.168.1.212"], options.SonosAddresses);
        Assert.Equal(1901, options.SonosDiscoveryPort);
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
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Streaming":{"Sonos":{"Addresses":["192.168.1.211","192.168.1.212"]}}}"""));
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
