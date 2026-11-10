using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace Streaming.Tests;

public sealed class PlaybackCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly FakeSchedule _schedule = new();
    private readonly FakeStorage _storage = new();
    private readonly FakeBroadcast _broadcast = new();
    private readonly PlaybackCoordinator _coordinator;

    public PlaybackCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "a.mp3"), "");
        File.WriteAllText(Path.Combine(_directory, "b.mp3"), "");
        _storage.Genres["a.mp3"] = "Jazz";
        _storage.Genres["b.mp3"] = "Rock";
        var options = new StreamingOptions
        {
            MusicRoot = _directory, DatabasePath = Path.Combine(_directory, "db"),
            SmtpHost = "", SmtpUsername = "", SmtpPassword = ""
        };
        var sonos = new SonosService(new HttpClient(), options, NullLogger<SonosService>.Instance);
        _coordinator = new PlaybackCoordinator(options, _schedule, _storage, sonos, _broadcast, NullLogger<PlaybackCoordinator>.Instance);
    }

    [Fact]
    public async Task ScheduleWaitsStartsChangesAndStopsAtBoundaries()
    {
        Assert.Equal(0, await _coordinator.StartAsync(null, [], false, true, "", default));
        Assert.True((await GetStateAsync()).WaitingForSchedule);
        Assert.Empty(_broadcast.Tracks);

        _schedule.Genres = new HashSet<string>(["Jazz"], StringComparer.OrdinalIgnoreCase);
        await _coordinator.CheckSchedulesAsync();
        Assert.Equal(["a.mp3"], _broadcast.Tracks);
        Assert.False((await GetStateAsync()).WaitingForSchedule);

        _schedule.Genres = new HashSet<string>(["Rock"], StringComparer.OrdinalIgnoreCase);
        await _coordinator.CheckSchedulesAsync();
        Assert.Equal(["b.mp3"], _broadcast.Tracks);

        _schedule.Genres.Clear();
        await _coordinator.CheckSchedulesAsync();
        Assert.Empty(_broadcast.Tracks);
        Assert.True((await GetStateAsync()).WaitingForSchedule);
    }

    [Fact]
    public async Task SelectionAndStopCancelScheduledPlayback()
    {
        await _coordinator.StartAsync(null, [], false, true, "", default);
        await _coordinator.StartAsync(null, ["b.mp3"], true, false, "", default);
        _schedule.Genres.Add("Jazz");
        await _coordinator.CheckSchedulesAsync();
        Assert.Equal(["b.mp3"], _broadcast.Tracks);
        Assert.True(_broadcast.Shuffle);

        await _coordinator.StartAsync(null, [], false, true, "", default);
        await _coordinator.CancelScheduleAndExecuteAsync(null, () => _broadcast.StopAsync(), default);
        await _coordinator.CheckSchedulesAsync();
        Assert.Empty(_broadcast.Tracks);
    }

    [Fact]
    public async Task PausedScheduleDoesNotResumeOrSkipUntilExplicitlyResumed()
    {
        _schedule.Genres.Add("Jazz");
        await _coordinator.StartAsync(null, [], false, true, "", default);
        await _coordinator.PauseAsync(null, () => _broadcast.PauseAsync(), default);
        _schedule.Genres.Clear();
        await _coordinator.CheckSchedulesAsync();
        Assert.True(_broadcast.Paused);
        Assert.True((await GetStateAsync()).SchedulePaused);
        Assert.False(await _coordinator.NavigateAsync(null, () => _broadcast.NextAsync(), default));

        await _coordinator.PauseAsync(null, () => _broadcast.PauseAsync(), default);
        Assert.True((await GetStateAsync()).WaitingForSchedule);
        Assert.Empty(_broadcast.Tracks);
        _schedule.Genres.Add("Rock");
        await _coordinator.CheckSchedulesAsync();
        Assert.Equal(["b.mp3"], _broadcast.Tracks);
    }

    [Fact]
    public async Task ScheduleErrorsStopPlaybackAndNextCheckRetries()
    {
        _schedule.Genres.Add("Jazz");
        await _coordinator.StartAsync(null, [], false, true, "", default);
        _schedule.Fail = true;
        await _coordinator.CheckSchedulesAsync();
        Assert.Empty(_broadcast.Tracks);
        _schedule.Fail = false;
        await _coordinator.CheckSchedulesAsync();
        Assert.Equal(["a.mp3"], _broadcast.Tracks);
    }

    [Fact]
    public async Task SonosScheduleBuildsAndStopsQueueAndSelectionOverridesIt()
    {
        var options = new StreamingOptions
        {
            MusicRoot = _directory, DatabasePath = Path.Combine(_directory, "db"),
            SmtpHost = "", SmtpUsername = "", SmtpPassword = ""
        };
        var handler = new SonosHandler();
        var sonos = new SonosService(new HttpClient(handler), options, NullLogger<SonosService>.Instance);
        using var coordinator = new PlaybackCoordinator(options, _schedule, _storage, sonos, _broadcast, NullLogger<PlaybackCoordinator>.Instance);
        var speaker = new SonosSpeaker("Test", "192.0.2.10", "RINCON-123");

        await coordinator.StartAsync(speaker, [], false, true, "http://example.test", default);
        Assert.Equal("STOPPED", handler.State);
        _schedule.Genres.Add("Jazz");
        await coordinator.CheckSchedulesAsync();
        Assert.Equal("PLAYING", handler.State);
        Assert.Contains(handler.Requests, request => request.Contains("http://example.test/music/a.mp3"));
        Assert.Contains(handler.Requests, request => request.Contains("<NewPlayMode>REPEAT_ALL</NewPlayMode>"));

        _schedule.Genres.Clear();
        await coordinator.CheckSchedulesAsync();
        Assert.Equal("STOPPED", handler.State);
        await coordinator.StartAsync(speaker, ["b.mp3"], true, false, "http://example.test", default);
        Assert.Equal("PLAYING", handler.State);
        Assert.Contains(handler.Requests, request => request.Contains("http://example.test/music/b.mp3"));
        Assert.Contains(handler.Requests, request => request.Contains("<NewPlayMode>SHUFFLE</NewPlayMode>"));
        await coordinator.CheckSchedulesAsync();
        Assert.Equal("PLAYING", handler.State);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<StateResponse> GetStateAsync() => _coordinator.GetStateAsync(null,
        () => Task.FromResult(new StateResponse("local", "STOPPED", null, null, null)), default);

    private sealed class FakeSchedule : IGenreScheduleService
    {
        public HashSet<string> Genres { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Fail { get; set; }
        public Task<IReadOnlySet<string>> GetActiveGenresAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("Schedule unavailable") : Task.FromResult<IReadOnlySet<string>>(Genres);
        public Task<ScheduleResponse> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(ScheduleSaveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SonosHandler : HttpMessageHandler
    {
        public string State { get; private set; } = "STOPPED";
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.GetValues("SOAPACTION").Single();
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (action.EndsWith("#Play\"", StringComparison.Ordinal))
                State = "PLAYING";
            else if (action.EndsWith("#Stop\"", StringComparison.Ordinal))
                State = "STOPPED";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<CurrentTransportState>{State}</CurrentTransportState>")
            };
        }
    }

    private sealed class FakeBroadcast : IBroadcastPlaybackService
    {
        public string[] Tracks { get; private set; } = [];
        public bool Shuffle { get; private set; }
        public bool Paused { get; private set; }
        public BroadcastPlaybackState GetState() => new("STOPPED", null, null, null, null);
        public Task StartAsync(IReadOnlyList<string> tracks, bool shuffle, CancellationToken cancellationToken = default)
        {
            Tracks = tracks.ToArray();
            Shuffle = shuffle;
            Paused = false;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { Tracks = []; Paused = false; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { Paused = !Paused; return Task.CompletedTask; }
        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetVolume(int level) { }
    }

    private sealed class FakeStorage : IAuditStorage
    {
        public Dictionary<string, string> Genres { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<string, string>> GetEffectiveGenresAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(Genres);
        public Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> StorePurchaseEvidenceAsync(UploadedDocument document, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> IndexTrackAsync(string fullPath, long? purchaseEvidenceId, UploadedDocument? license, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> FindTrackIdByMusicUriAsync(string uri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> FindTrackIdByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordPlayAsync(long trackId, DateTime playedAtUtc, string playbackMode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RebuildMonthlySummaryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlayAuditReportItem>> GetPlayAuditReportAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, TrackMetadata>> GetTrackMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetGenreOverrideAsync(long trackId, string? genre, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
