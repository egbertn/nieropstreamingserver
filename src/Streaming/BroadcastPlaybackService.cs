using System.Diagnostics;

namespace Streaming;

public sealed record BroadcastPlaybackState(string State, string? RelativePath, string? Title, double? ElapsedSeconds, double? DurationSeconds);

public interface IBroadcastPlaybackService
{
    BroadcastPlaybackState GetState();
    Task StartAsync(IReadOnlyList<string> tracks, bool shuffle, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task NextAsync(CancellationToken cancellationToken = default);
    Task PreviousAsync(CancellationToken cancellationToken = default);
    void SetVolume(int level);
}

public interface IRandomSource
{
    int Next(int maxValue);
}

internal sealed class SharedRandomSource : IRandomSource
{
    public int Next(int maxValue) => Random.Shared.Next(maxValue);
}

public sealed class BroadcastPlaybackService(StreamingOptions options, ILogger<BroadcastPlaybackService> _logger, IRandomSource random) : IBroadcastPlaybackService
{
    private readonly Lock _sync = new();

    private string[] _queue = [];
    private int _currentIndex;
    private int _volume = 25;
    private string _state = "STOPPED";
    private DateTime _trackStartedUtc;
    private TimeSpan _elapsedBeforePause;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private Process? _player;
    private int? _requestedIndex;
    private TaskCompletionSource<bool>? _resumeSignal;

    public BroadcastPlaybackState GetState()
    {
        lock (_sync)
        {
            var relativePath = _queue.Length > 0 && _currentIndex >= 0 && _currentIndex < _queue.Length ? _queue[_currentIndex] : null;
            var elapsed = relativePath is null ? (double?)null : GetElapsed().TotalSeconds;
            return new BroadcastPlaybackState(_state, relativePath, relativePath is null ? null : Path.GetFileNameWithoutExtension(relativePath), elapsed, null);
        }
    }

    public async Task StartAsync(IReadOnlyList<string> tracks, bool shuffle, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0)
            throw new InvalidOperationException("Geen muziek gevonden");

        await StopAsync(cancellationToken);
        var queue = tracks.ToArray();
        if (shuffle)
            Shuffle(queue, random);

        lock (_sync)
        {
            _queue = queue;
            _currentIndex = 0;
            _requestedIndex = null;
            _state = "PLAYING";
            _elapsedBeforePause = TimeSpan.Zero;
            _playbackCancellation = new CancellationTokenSource();
            _playbackTask = RunPlaylistAsync(_playbackCancellation.Token);
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_player is null)
                return Task.CompletedTask;

            if (_state == "PLAYING")
            {
                _elapsedBeforePause = GetElapsed();
                _state = "PAUSED_PLAYBACK";
                _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TryStopPlayer();
            }
            else if (_state == "PAUSED_PLAYBACK")
            {
                _state = "PLAYING";
                _resumeSignal?.TrySetResult(true);
                _resumeSignal = null;
            }
            else
            {
                return Task.CompletedTask;
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_sync)
        {
            _playbackCancellation?.Cancel();
            TryStopPlayer();
            task = _playbackTask;
        }

        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        lock (_sync)
        {
            _queue = [];
            _currentIndex = 0;
            _requestedIndex = null;
            _state = "STOPPED";
            _elapsedBeforePause = TimeSpan.Zero;
            _playbackTask = null;
            _resumeSignal?.TrySetCanceled(cancellationToken);
            _resumeSignal = null;
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
        }
    }

    public Task NextAsync(CancellationToken cancellationToken = default) => ChangeTrackAsync(1, cancellationToken);
    public Task PreviousAsync(CancellationToken cancellationToken = default) => ChangeTrackAsync(-1, cancellationToken);

    public void SetVolume(int level)
    {
        lock (_sync)
            _volume = Math.Clamp(level, 0, 100);
    }

    private async Task ChangeTrackAsync(int offset, CancellationToken _)
    {
        lock (_sync)
        {
            if (_queue.Length == 0)
                return;
            _requestedIndex = (_currentIndex + offset + _queue.Length) % _queue.Length;
            _state = "PLAYING";
            _elapsedBeforePause = TimeSpan.Zero;
            _resumeSignal?.TrySetResult(true);
            _resumeSignal = null;
            TryStopPlayer();
        }
        await Task.CompletedTask;
    }

    private async Task RunPlaylistAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string relativePath;
            int volume;
            TimeSpan startOffset;
            lock (_sync)
            {
                if (_queue.Length == 0)
                    return;
                relativePath = _queue[_currentIndex];
                volume = _volume;
                startOffset = _elapsedBeforePause;
                _trackStartedUtc = DateTime.UtcNow - startOffset;
                _elapsedBeforePause = TimeSpan.Zero;
                _state = "PLAYING";
            }

            var fullPath = Path.GetFullPath(Path.Combine(options.MusicRoot, relativePath));
            if (!fullPath.StartsWith(options.MusicRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                _logger.LogWarning("Broadcasttrack bestaat niet: {Track}", relativePath);
            }
            else
            {
                using var player = Process.Start(new ProcessStartInfo
                {
                    FileName = options.FfplayPath,
                    Arguments = $"-nodisp -nostats -hide_banner -autoexit -loglevel error -volume {volume} -ss {startOffset.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{fullPath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,

                }) ?? throw new InvalidOperationException($"Lokale audiospeler '{options.FfplayPath}' kon niet worden gestart.");

                lock (_sync)
                    _player = player;
                var errorReader = DrainPlayerErrorsAsync(player, relativePath);
                try
                {
                    await player.WaitForExitAsync(cancellationToken);
                    await errorReader;
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_player == player)
                            _player = null;
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            Task? resumeTask = null;
            lock (_sync)
            {
                if (_state == "PAUSED_PLAYBACK")
                    resumeTask = _resumeSignal?.Task;
            }

            if (resumeTask is not null)
            {
                await resumeTask.WaitAsync(cancellationToken);
                continue;
            }

            lock (_sync)
            {
                if (_requestedIndex is int requestedIndex)
                {
                    _currentIndex = requestedIndex;
                    _requestedIndex = null;
                }
                else
                {
                    _currentIndex = (_currentIndex + 1) % _queue.Length;
                }
            }
        }
    }

    private TimeSpan GetElapsed() => _state == "PLAYING" ? _elapsedBeforePause + (DateTime.UtcNow - _trackStartedUtc) : _elapsedBeforePause;

    private void TryStopPlayer()
    {
        try
        {
            if (_player is { HasExited: false })
                _player.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private async Task DrainPlayerErrorsAsync(Process player, string relativePath)
    {
        while (!player.HasExited)
        {
            var line = await player.StandardError.ReadLineAsync();
            if (line is null)
                break;
            _logger.LogWarning("ffplay voor {Track}: {Message}", relativePath, line);
        }
    }

    internal static void Shuffle(string[] queue, IRandomSource random)
    {
        for (var index = queue.Length - 1; index > 0; index--)
        {
            var replacementIndex = random.Next(index + 1);
            (queue[index], queue[replacementIndex]) = (queue[replacementIndex], queue[index]);
        }
    }
}
