namespace Streaming.Tests;

public sealed class BroadcastPlaybackServiceTests
{
    [Fact]
    public void PlayerStartInfo_SelectsAlsaDeviceForInstalledOutput()
    {
        var options = CreateOptions("plughw:CARD=DAC,DEV=1");

        var startInfo = BroadcastPlaybackService.CreatePlayerStartInfo(options, "/music/a song.mp3", TimeSpan.FromSeconds(1.5));

        Assert.Equal("alsa", startInfo.Environment["SDL_AUDIODRIVER"]);
        Assert.Equal("plughw:CARD=DAC,DEV=1", startInfo.Environment["AUDIODEV"]);
        Assert.False(startInfo.Environment.ContainsKey("SDL_AUDIO_DEVICE_NAME"));
        Assert.Equal("1.5", startInfo.ArgumentList[^2]);
        Assert.Equal("/music/a song.mp3", startInfo.ArgumentList[^1]);
    }

    [Fact]
    public void PlayerStartInfo_LeavesAudioEnvironmentUnchangedWhenNoDeviceIsConfigured()
    {
        var startInfo = BroadcastPlaybackService.CreatePlayerStartInfo(CreateOptions(null), "/music/song.mp3", TimeSpan.Zero);

        startInfo.Environment.TryGetValue("SDL_AUDIODRIVER", out var driver);
        startInfo.Environment.TryGetValue("AUDIODEV", out var device);
        Assert.Equal(Environment.GetEnvironmentVariable("SDL_AUDIODRIVER"), driver);
        Assert.Equal(Environment.GetEnvironmentVariable("AUDIODEV"), device);
    }

    private static StreamingOptions CreateOptions(string? device) => new()
    {
        MusicRoot = "/music",
        DatabasePath = "/tmp/streaming.db",
        SmtpHost = "localhost",
        SmtpUsername = "",
        SmtpPassword = "",
        AlsaPlaybackDevice = device
    };

    [Fact]
    public void Shuffle_UsesFisherYatesWithTheProvidedRandomSource()
    {
        var queue = new[] { "first.mp3", "second.mp3", "third.mp3", "fourth.mp3" };
        var random = new ScriptedRandomSource(1, 0, 1);

        BroadcastPlaybackService.Shuffle(queue, random);

        Assert.Equal(["third.mp3", "fourth.mp3", "first.mp3", "second.mp3"], queue);
        Assert.Equal([4, 3, 2], random.MaximumValues);
    }

    private sealed class ScriptedRandomSource(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public List<int> MaximumValues { get; } = [];

        public int Next(int maxValue)
        {
            MaximumValues.Add(maxValue);
            return _values.Dequeue();
        }
    }
}