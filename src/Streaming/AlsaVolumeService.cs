using System.ComponentModel;
using System.Diagnostics;

namespace Streaming;

public sealed class AlsaVolumeService(StreamingOptions options, ILogger<AlsaVolumeService> logger)
{
    public async Task SetVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        level = Math.Clamp(level, 0, 100);
        using var process = new Process { StartInfo = CreateStartInfo(options, level) };

        try
        {
            if (!process.Start())
                throw new AlsaVolumeException("The ALSA mixer could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new AlsaVolumeException(
                $"Could not start ALSA mixer '{options.AlsaMixerPath}'. Install alsa-utils and check the configured mixer path.",
                ex);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode == 0)
            return;

        var details = string.Join(Environment.NewLine, new[] { error, output }.Where(text => !string.IsNullOrWhiteSpace(text)));
        logger.LogWarning(
            "ALSA mixer {Mixer} on device {Device} failed to set {Control} to {Level}% (exit code {ExitCode}): {Details}",
            options.AlsaMixerPath,
            options.AlsaMixerDevice,
            options.AlsaVolumeControl,
            level,
            process.ExitCode,
            details);
        throw new AlsaVolumeException(
            $"Could not set ALSA mixer control '{options.AlsaVolumeControl}' on device '{options.AlsaMixerDevice}' to {level}% (exit code {process.ExitCode}): {details.Trim()}");
    }

    internal static ProcessStartInfo CreateStartInfo(StreamingOptions options, int level)
        => new(options.AlsaMixerPath, ["-D", options.AlsaMixerDevice, "sset", options.AlsaVolumeControl, "playback", $"{Math.Clamp(level, 0, 100)}%"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
}

public sealed class AlsaVolumeException(string message, Exception? innerException = null)
    : Exception(message, innerException);
