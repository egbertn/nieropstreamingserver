namespace Streaming;

public sealed class PlaybackAuditWorker(IServiceProvider _services, StreamingOptions _options, ILogger<PlaybackAuditWorker> _logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumPlayDuration = TimeSpan.FromSeconds(30);

    private string? _candidateUri;
    private DateTime _candidateStartedUtc;
    private string? _recordedUri;
    private DateOnly? _lastSummaryDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.UseSonos)
                    await PollSonosAsync(stoppingToken);
                else
                    await PollBroadcastAsync(stoppingToken);

                var storage = _services.GetRequiredService<IAuditStorage>();
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (_lastSummaryDate != today)
                {
                    await storage.RebuildMonthlySummaryAsync(stoppingToken);
                    _lastSummaryDate = today;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kon de speelstatus voor de audit niet verwerken.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollSonosAsync(CancellationToken cancellationToken)
    {
        var sonos = _services.GetRequiredService<SonosService>();
        var speaker = await sonos.ResolveAsync(null, cancellationToken);
        if (speaker is null || !string.Equals(await sonos.GetTransportStateAsync(speaker, cancellationToken), "PLAYING", StringComparison.Ordinal))
        {
            ResetCandidate();
            return;
        }

        var position = await sonos.GetPositionInfoAsync(speaker, cancellationToken);
        if (position is null || string.IsNullOrWhiteSpace(position.Uri))
        {
            ResetCandidate();
            return;
        }

        var now = DateTime.UtcNow;
        if (!string.Equals(_candidateUri, position.Uri, StringComparison.Ordinal))
        {
            _candidateUri = position.Uri;
            _candidateStartedUtc = now - position.Elapsed;
            _recordedUri = null;
            return;
        }

        if (_recordedUri is not null || now - _candidateStartedUtc < MinimumPlayDuration)
            return;

        var storage = _services.GetRequiredService<IAuditStorage>();
        var trackId = await storage.FindTrackIdByMusicUriAsync(position.Uri, cancellationToken);
        if (trackId is null)
            return;

        await storage.RecordPlayAsync(trackId.Value, _candidateStartedUtc, "Sonos", cancellationToken);
        _recordedUri = position.Uri;
    }

    private async Task PollBroadcastAsync(CancellationToken cancellationToken)
    {
        var playback = _services.GetRequiredService<IBroadcastPlaybackService>().GetState();
        if (playback.State != "PLAYING" || string.IsNullOrWhiteSpace(playback.RelativePath))
        {
            ResetCandidate();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = playback.ElapsedSeconds;
        if (elapsed is null)
        {
            ResetCandidate();
            return;
        }

        if (!string.Equals(_candidateUri, playback.RelativePath, StringComparison.Ordinal))
        {
            _candidateUri = playback.RelativePath;
            _candidateStartedUtc = now - TimeSpan.FromSeconds(elapsed.Value);
            _recordedUri = null;
            return;
        }

        if (_recordedUri is not null || elapsed.Value < MinimumPlayDuration.TotalSeconds)
            return;

        var storage = _services.GetRequiredService<IAuditStorage>();
        var trackId = await storage.FindTrackIdByRelativePathAsync(playback.RelativePath, cancellationToken);
        if (trackId is null)
            return;

        await storage.RecordPlayAsync(trackId.Value, _candidateStartedUtc, "Broadcast", cancellationToken);
        _recordedUri = playback.RelativePath;
    }

    private void ResetCandidate()
    {
        _candidateUri = null;
        _recordedUri = null;
    }
}
