using System.ComponentModel;
using System.Diagnostics;

namespace Streaming;

internal sealed class AudioConverter(ILogger<AudioConverter> logger)
{
    private readonly SemaphoreSlim _conversionLock = new(1, 1);
    private readonly SemaphoreSlim _uploadLock = new(1, 1);

    private volatile UploadProgress _progress = new(0, 0, 0);

    public UploadProgress Progress => IsUploadBusy ? _progress : new(0, 0, 0);

    public void ReportBatch(int total, int current, double fraction) =>
        _progress = new UploadProgress(total, current, Math.Clamp(fraction, 0, 1));

    public bool IsUploadBusy => _uploadLock.CurrentCount == 0;

    // Only one upload batch at a time; returns null when another upload is in progress.
    public IDisposable? TryBeginUpload() => _uploadLock.Wait(0) ? new UploadRelease(_uploadLock) : null;

    private sealed class UploadRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    public Task ConvertAsync(string input, string output, CancellationToken cancellationToken) =>
        ConvertAsync(input, output, null, cancellationToken);

    public async Task ConvertAsync(string input, string output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _conversionLock.WaitAsync(cancellationToken);
        try
        {
            var duration = progress is null ? null : await ProbeDurationAsync(input, cancellationToken);
            string[] arguments =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-nostats",
                "-protocol_whitelist", "file,pipe",
                "-i", input, "-map", "0:a:0", "-map_metadata", "0", "-map_metadata", "0:s:a:0",
                "-vn", "-sn", "-dn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11",
                "-ar", "48000", "-c:a", "libmp3lame", "-b:a", "192k",
                "-f", "mp3", "-y", output
            ];

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("ffmpeg", arguments)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            try
            {
                if (!process.Start())
                    throw new AudioConversionException("FFmpeg could not be started.");
            }
            catch (Win32Exception ex)
            {
                throw new AudioConversionException("FFmpeg could not be started.", ex);
            }

            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var progressReader = ReadProgressAsync(process.StandardOutput, duration, progress);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    throw new AudioConversionException("FFmpeg conversion timed out.");
                throw;
            }

            await progressReader;
            var error = await errors;
            if (process.ExitCode != 0)
            {
                logger.LogWarning("FFmpeg conversion failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                throw new AudioConversionException("FFmpeg could not convert the file; check that it contains valid audio.");
            }
        }
        finally
        {
            _conversionLock.Release();
        }
    }

    // ffmpeg's -progress output reports out_time_us (microseconds of audio processed).
    private static async Task ReadProgressAsync(StreamReader reader, double? durationSeconds, IProgress<double>? progress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (durationSeconds is > 0 && progress is not null
                && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), out var micros))
                progress.Report(Math.Clamp(micros / 1_000_000d / durationSeconds.Value, 0, 1));
        }
    }

    private static async Task<double?> ProbeDurationAsync(string input, CancellationToken cancellationToken)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("ffprobe",
                ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "format=duration", "-of", "csv=p=0", input])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (probe is null) return null;
            var output = probe.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = probe.StandardError.ReadToEndAsync(cancellationToken);
            await probe.WaitForExitAsync(cancellationToken);
            return double.TryParse((await output).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed class AudioConversionException : Exception
{
    public AudioConversionException(string message) : base(message) { }
    public AudioConversionException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed record UploadProgress(int Total, int Current, double Fraction);
