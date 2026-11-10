using Microsoft.Extensions.Logging.Abstractions;

namespace Streaming.Tests;

public sealed class AlsaVolumeServiceTests
{
    [Fact]
    public void CreateStartInfo_ClampsLevelAndPassesMixerSettingsAsSeparateArguments()
    {
        var options = CreateOptions(mixerDevice: "pulse; do not execute", mixerControl: "USB Mixer; do not execute");

        var startInfo = AlsaVolumeService.CreateStartInfo(options, 140);

        Assert.Equal("amixer", startInfo.FileName);
        Assert.Equal(["-D", "pulse; do not execute", "sset", "USB Mixer; do not execute", "playback", "100%"], startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_UsesDefaultDeviceAndClampsNegativeLevel()
    {
        var startInfo = AlsaVolumeService.CreateStartInfo(CreateOptions(), -10);

        Assert.Equal(["-D", "default", "sset", "Master", "playback", "0%"], startInfo.ArgumentList);
    }

    [Fact]
    public async Task SetVolumeAsync_SucceedsWhenMixerCommandSucceeds()
    {
        var service = new AlsaVolumeService(CreateOptions(mixerPath: "/bin/true"), NullLogger<AlsaVolumeService>.Instance);

        await service.SetVolumeAsync(40);
    }

    [Fact]
    public async Task SetVolumeAsync_ThrowsWhenMixerCommandFails()
    {
        var service = new AlsaVolumeService(
            CreateOptions(mixerPath: "/bin/false"),
            NullLogger<AlsaVolumeService>.Instance);

        var exception = await Assert.ThrowsAsync<AlsaVolumeException>(() => service.SetVolumeAsync(40));

        Assert.Contains("exit code 1", exception.Message);
    }

    private static StreamingOptions CreateOptions(string mixerPath = "amixer", string mixerDevice = "default", string mixerControl = "Master") => new()
    {
        MusicRoot = Path.GetTempPath(),
        DatabasePath = Path.Combine(Path.GetTempPath(), "streaming.db"),
        AlsaMixerPath = mixerPath,
        AlsaMixerDevice = mixerDevice,
        AlsaVolumeControl = mixerControl,
        SmtpHost = "smtp.example.com",
        SmtpUsername = "user",
        SmtpPassword = "password"
    };
}
