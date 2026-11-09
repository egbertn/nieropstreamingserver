namespace Streaming;

public sealed class PlaybackCoordinator(
    StreamingOptions options,
    IGenreScheduleService schedule,
    IAuditStorage storage,
    SonosService sonos,
    IBroadcastPlaybackService broadcast,
    ILogger<PlaybackCoordinator> logger) : BackgroundService
{
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".flac", ".wav", ".ogg"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ScheduledPlayback> _scheduled = new(StringComparer.Ordinal);

    private sealed class ScheduledPlayback(SonosSpeaker? speaker, bool shuffle, string baseUrl)
    {
        public SonosSpeaker? Speaker { get; } = speaker;
        public bool Shuffle { get; } = shuffle;
        public string BaseUrl { get; } = baseUrl;
        public string[]? PlayingTracks { get; set; }
        public bool Paused { get; set; }
    }

    private static string Key(SonosSpeaker? speaker) => speaker?.Uuid ?? "local";

    public static string[] EnumerateTracks(string musicRoot) =>
        Directory.Exists(musicRoot)
            ? Directory.EnumerateFiles(musicRoot, "*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(musicRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public async Task<int> StartAsync(SonosSpeaker? speaker, string[] tracks, bool shuffle, bool useSchedule, string baseUrl, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_scheduled.ContainsKey(Key(speaker)))
                await StopOutputAsync(speaker, ct);
            _scheduled.Remove(Key(speaker));
            if (!useSchedule)
            {
                await PlayAsync(speaker, tracks, shuffle, baseUrl, ct);
                return tracks.Length;
            }

            await StopOutputAsync(speaker, ct);
            var session = new ScheduledPlayback(speaker, shuffle, baseUrl);
            await ApplyScheduleAsync(session, ct);
            _scheduled[Key(speaker)] = session;
            return session.PlayingTracks?.Length ?? 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelScheduleAndExecuteAsync(SonosSpeaker? speaker, Func<Task> command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_scheduled.TryGetValue(Key(speaker), out var session) || session.PlayingTracks is not { Length: 0 })
                await command();
            _scheduled.Remove(Key(speaker));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(SonosSpeaker? speaker, Func<Task> ordinaryPause, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_scheduled.TryGetValue(Key(speaker), out var session))
            {
                await ordinaryPause();
                return;
            }

            if (!session.Paused)
            {
                if (session.PlayingTracks is { Length: > 0 })
                    await ordinaryPause();
                session.Paused = true;
            }
            else
            {
                await StopOutputAsync(speaker, ct);
                session.PlayingTracks = null;
                await ApplyScheduleAsync(session, ct);
                session.Paused = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> NavigateAsync(SonosSpeaker? speaker, Func<Task> command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_scheduled.TryGetValue(Key(speaker), out var session)
                && (session.Paused || session.PlayingTracks is not { Length: > 0 }))
                return false;
            await command();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StateResponse> GetStateAsync(SonosSpeaker? speaker, Func<Task<StateResponse>> readState, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await readState();
            if (!_scheduled.TryGetValue(Key(speaker), out var session))
                return state;
            return state with
            {
                WaitingForSchedule = session.PlayingTracks is { Length: 0 } && !session.Paused,
                SchedulePaused = session.Paused
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CheckSchedulesAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var session in _scheduled.Values)
            {
                try { await StopOutputAsync(session.Speaker, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Schema-afspelen stoppen bij afsluiten mislukt voor {Speaker}", session.Speaker?.Name ?? "local");
                }
            }
            _scheduled.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CheckSchedulesAsync(CancellationToken stoppingToken = default)
    {
        await _gate.WaitAsync(stoppingToken);
        try
        {
            foreach (var session in _scheduled.Values)
            {
                if (session.Paused)
                    continue;
                try
                {
                    await ApplyScheduleAsync(session, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Schema toepassen mislukt voor {Speaker}; afspelen wordt gestopt", session.Speaker?.Name ?? "local");
                    session.PlayingTracks = null;
                    try { await StopOutputAsync(session.Speaker, stoppingToken); }
                    catch (Exception stopException) when (stopException is not OperationCanceledException)
                    {
                        logger.LogError(stopException, "Afspelen stoppen na schemafout is mislukt voor {Speaker}", session.Speaker?.Name ?? "local");
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyScheduleAsync(ScheduledPlayback session, CancellationToken ct)
    {
        var genres = await schedule.GetActiveGenresAsync(DateTimeOffset.Now, ct);
        var tracks = Array.Empty<string>();
        if (genres.Count > 0)
        {
            var effectiveGenres = await storage.GetEffectiveGenresAsync(ct);
            tracks = EnumerateTracks(options.MusicRoot)
                .Where(path => GenreScheduleService.TrackMatchesGenres(effectiveGenres.GetValueOrDefault(path), genres))
                .ToArray();
        }

        if (session.PlayingTracks is not null && session.PlayingTracks.SequenceEqual(tracks, StringComparer.Ordinal))
            return;

        if (tracks.Length == 0)
            await StopOutputAsync(session.Speaker, ct);
        else
            await PlayAsync(session.Speaker, tracks, session.Shuffle, session.BaseUrl, ct);
        session.PlayingTracks = tracks;
    }

    private async Task PlayAsync(SonosSpeaker? speaker, string[] tracks, bool shuffle, string baseUrl, CancellationToken ct)
    {
        if (speaker is null)
        {
            await broadcast.StartAsync(tracks, shuffle, ct);
            return;
        }

        await sonos.ClearQueueAsync(speaker, ct);
        foreach (var path in tracks)
        {
            var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            await sonos.QueueUriAsync(speaker, $"{baseUrl}/music/{encoded}", Path.GetFileNameWithoutExtension(path), ct: ct);
        }
        await sonos.SelectQueueAsync(speaker, ct);
        await sonos.SetPlayModeAsync(speaker, shuffle ? "SHUFFLE" : "REPEAT_ALL", ct);
        await sonos.PlayAsync(speaker, ct);
    }

    private async Task StopOutputAsync(SonosSpeaker? speaker, CancellationToken ct)
    {
        if (speaker is null)
            await broadcast.StopAsync(ct);
        else if (await sonos.GetTransportStateAsync(speaker, ct) is not ("STOPPED" or "NO_MEDIA_PRESENT"))
            await sonos.StopAsync(speaker, ct);
    }
}
