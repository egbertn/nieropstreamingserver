using System.Text;
using Streaming;

namespace Streaming.Tests;

public sealed class MusicReportServiceTests : IDisposable
{
    private readonly string _tempMusicDir;

    public MusicReportServiceTests()
    {
        _tempMusicDir = Path.Combine(Path.GetTempPath(), "music_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempMusicDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempMusicDir))
        {
            try
            {
                Directory.Delete(_tempMusicDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private  StreamingOptions CreateStreamingOptions()
    {
        return new StreamingOptions
        {
            MusicRoot = _tempMusicDir,
            DatabasePath = Path.Combine(_tempMusicDir, "database.db")
        };
    }

    [Fact]
    public void CollectReportData_EmptyDirectory_ReturnsZeroTracks()
    {
        var service = new MusicReportService(CreateStreamingOptions());

        var data = service.CollectReportData();

        Assert.Empty(data.Tracks);
        Assert.Equal(TimeSpan.Zero, data.TotalDuration);
        Assert.Equal(0, data.TotalSizeBytes);
        Assert.Equal(0, data.FreeCount);
        Assert.Equal(0, data.ProtectedCount);
        Assert.Equal(0, data.UnspecifiedCount);
    }

    [Fact]
    public void CollectReportData_FileNameContainsCc0_ClassifiesTrackAsFree()
    {
        File.WriteAllBytes(Path.Combine(_tempMusicDir, "HoliznaCC0 - Doodles.mp3"), []);
        var service = new MusicReportService(CreateStreamingOptions());

        var data = service.CollectReportData();

        var track = Assert.Single(data.Tracks);
        Assert.Equal(TrackCopyrightStatus.Free, track.Copyright.Status);
        Assert.Equal("FREE (BESTANDSNAAM)", track.Copyright.BadgeText);
        Assert.Equal("cc0", track.Copyright.LicenseMarker);
    }

    [Fact]
    public void GeneratePdfReport_EmptyDirectory_ReturnsValidPdf()
    {
        var service = new MusicReportService(CreateStreamingOptions());

        var pdfBytes = service.GeneratePdfReport();

        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 0);

        var pdfString = Encoding.Latin1.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.4", pdfString);
        Assert.Contains("%%EOF", pdfString);
    }

    [Fact]
    public void GeneratePdfReport_WithExplicitData_IncludesTrackInfo()
    {
        var service = new MusicReportService(CreateStreamingOptions());

        var item = new TrackReportItem(
            "ambient/relax.mp3",
            "relax.mp3",
            1024 * 1024 * 3,
            DateTime.UtcNow.AddDays(-2),
            DateTime.UtcNow.AddDays(-1),
            TimeSpan.FromMinutes(3.5),
            "Relaxing Meditation",
            "Open Artist",
            "Free Sounds",
            2025,
            "Ambient",
            320,
            "MP3",
            new TrackCopyrightDetails(
                TrackCopyrightStatus.Free,
                "FREE (LICENTIE)",
                "Creative Commons Zero (CC0)",
                "CC0 1.0 Universal",
                null,
                null,
                "cc0"
            )
        );

        var reportData = new MusicReportData(
            DateTime.UtcNow,
            [item],
            TimeSpan.FromMinutes(3.5),
            1024 * 1024 * 3,
            1,
            0,
            0
        );

        var pdfBytes = service.GeneratePdfReport(reportData);

        Assert.NotNull(pdfBytes);
        var pdfString = Encoding.Latin1.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.4", pdfString);
        Assert.Contains("%%EOF", pdfString);
        Assert.Contains("ambient/relax.mp3", pdfString);
    }
}
