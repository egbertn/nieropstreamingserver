using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Streaming.Tests;

public sealed class AudioConverterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("streaming-converter-test-").FullName;

    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("ogg", "libvorbis")]
    [InlineData("mp4", "aac")]
    [InlineData("wmv", "wmav2")]
    public async Task ConvertAsync_ProducesNormalizedMp3(string extension, string codec)
    {
        var input = Path.Combine(_directory, $"input.{extension}");
        var output = Path.Combine(_directory, "output.tmp");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i",
            "sine=frequency=1000:sample_rate=48000:duration=8", "-metadata", "title=Test title",
            "-c:a", codec, input);

        await new AudioConverter(NullLogger<AudioConverter>.Instance).ConvertAsync(input, output, CancellationToken.None);

        var info = await RunAsync("ffprobe", "-v", "error", "-show_entries",
            "stream=codec_name:format_tags=title", "-of", "default=noprint_wrappers=1", output);
        Assert.Contains("codec_name=mp3", info);
        Assert.Contains("TAG:title=Test title", info);

        var measured = await RunAsync("ffmpeg", "-hide_banner", "-nostats", "-i", output,
            "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-");
        var match = System.Text.RegularExpressions.Regex.Match(measured, "\"input_i\"\\s*:\\s*\"([^\"]+)\"");
        Assert.True(match.Success, measured);
        var loudness = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(loudness, -16.8, -15.2);
    }

    [Fact]
    public async Task ConvertAsync_RejectsInvalidInput()
    {
        var input = Path.Combine(_directory, "invalid.mp4");
        await File.WriteAllTextAsync(input, "not audio");
        var converter = new AudioConverter(NullLogger<AudioConverter>.Instance);

        await Assert.ThrowsAsync<AudioConversionException>(() =>
            converter.ConvertAsync(input, Path.Combine(_directory, "output.tmp"), CancellationToken.None));
    }

    [Theory]
    [InlineData("mp4", "mpeg4", "aac")]
    [InlineData("wmv", "wmv2", "wmav2")]
    public async Task ConvertAsync_ExtractsAudioFromVideo(string extension, string videoCodec, string audioCodec)
    {
        var input = Path.Combine(_directory, $"video.{extension}");
        var output = Path.Combine(_directory, "output.tmp");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=size=16x16:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=1000:duration=2", "-c:v", videoCodec,
            "-c:a", audioCodec, "-shortest", input);

        await new AudioConverter(NullLogger<AudioConverter>.Instance).ConvertAsync(input, output, CancellationToken.None);

        var info = await RunAsync("ffprobe", "-v", "error", "-show_entries", "stream=codec_name,codec_type",
            "-of", "default=noprint_wrappers=1", output);
        Assert.Contains("codec_name=mp3", info);
        Assert.DoesNotContain("codec_type=video", info);
    }

    [Fact]
    public async Task ConvertAsync_RejectsVideoWithoutAudio()
    {
        var input = Path.Combine(_directory, "silent.mp4");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i",
            "color=size=16x16:duration=1", "-c:v", "mpeg4", input);

        await Assert.ThrowsAsync<AudioConversionException>(() =>
            new AudioConverter(NullLogger<AudioConverter>.Instance)
                .ConvertAsync(input, Path.Combine(_directory, "output.tmp"), CancellationToken.None));
    }

    [Fact]
    public async Task ConvertAsync_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var converter = new AudioConverter(NullLogger<AudioConverter>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            converter.ConvertAsync("input", "output", cancellation.Token));
    }

    private static async Task<string> RunAsync(string command, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {command}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
