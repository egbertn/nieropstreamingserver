namespace Streaming.Tests;

public sealed class AuditStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "streaming-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Storage_IndexesTracksLinksEvidenceAndBuildsDeduplicatedAuditReport()
    {
        Directory.CreateDirectory(_root);
        var trackPath = Path.Combine(_root, "album", "first.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(trackPath)!);
        await File.WriteAllBytesAsync(trackPath, [1, 2, 3]);
        var storage = new AuditStorage(new StreamingOptions
        {
            MusicRoot = _root,
            DatabasePath = Path.Combine(_root, "data", "streaming.db"),
            SmtpUsername = "smtpuser",
            SmtpPassword = "smtppass"
        });

        await storage.InitializeAsync();
        Assert.Null(await storage.StorePurchaseEvidenceAsync(new UploadedDocument("empty.pdf", "application/pdf", [])));
        var evidenceId = await storage.StorePurchaseEvidenceAsync(new UploadedDocument("invoice.pdf", "application/pdf", [4, 5]));
        var trackId = await storage.IndexTrackAsync(trackPath, evidenceId, new UploadedDocument("license.txt", "text/plain", [6]));

        Assert.True(File.Exists(trackPath + ".checksum"));
        Assert.Equal(trackId, await storage.FindTrackIdByRelativePathAsync("album/first.mp3"));
        Assert.Equal(trackId, await storage.FindTrackIdByMusicUriAsync("http://music.example/music/album/first.mp3"));
        Assert.Null(await storage.FindTrackIdByMusicUriAsync("http://music.example/not-music/album/first.mp3"));

        var renamedTrackPath = Path.Combine(_root, "album", "renamed.mp3");
        File.Move(trackPath, renamedTrackPath);
        File.Move(trackPath + ".checksum", renamedTrackPath + ".checksum");
        Assert.Equal(trackId, await storage.IndexTrackAsync(renamedTrackPath, evidenceId, null));
        Assert.Equal(trackId, await storage.FindTrackIdByRelativePathAsync("album/renamed.mp3"));

        var playedAt = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
        await storage.RecordPlayAsync(trackId, playedAt, "Sonos");
        await storage.RecordPlayAsync(trackId, playedAt.AddSeconds(30), "Sonos");
        await storage.RecordPlayAsync(trackId, playedAt.AddMonths(1), "Broadcast");
        await storage.RebuildMonthlySummaryAsync();

        var report = Assert.Single(await storage.GetPlayAuditReportAsync());
        Assert.Equal("renamed", report.Title);
        Assert.Equal(2, report.PlayedAtUtc.Count);
        Assert.Equal([new PlayAuditMonth("2026-09", 1), new PlayAuditMonth("2026-10", 1)], report.Monthly);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}