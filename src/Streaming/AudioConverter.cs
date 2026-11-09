using System.ComponentModel;
using System.Diagnostics;

namespace Streaming;

internal sealed class AudioConverter(ILogger<AudioConverter> logger)
{
    private readonly SemaphoreSlim _conversionLock = new(1, 1);

    public async Task ConvertAsync(string input, string output, CancellationToken cancellationToken)
    {
        await _conversionLock.WaitAsync(cancellationToken);
        try
        {
            string[] arguments =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
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
                    UseShellExecute = false,
                    CreateNoWindow = true
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
                await process.WaitForExitAsync(CancellationToken.None);
                if (!cancellationToken.IsCancellationRequested)
                    throw new AudioConversionException("FFmpeg conversion timed out.");
                throw;
            }

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
}

internal sealed class AudioConversionException : Exception
{
    public AudioConversionException(string message) : base(message) { }
    public AudioConversionException(string message, Exception innerException) : base(message, innerException) { }
}
