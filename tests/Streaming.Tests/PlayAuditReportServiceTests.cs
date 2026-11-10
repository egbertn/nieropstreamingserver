using System.IO.Compression;
using System.Text.Json;

namespace Streaming.Tests;

public sealed class PlayAuditReportServiceTests
{
    [Fact]
    public async Task GenerateZipAsync_WritesTracksWithUtcPlaysAndMonthlyTotals()
    {
        var storage = new FakeAuditStorage();
        var service = new PlayAuditReportService(storage);

        using var zipStream = await service.GenerateZipAsync();

        Assert.True(storage.Rebuilt);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("speel-audit.json", entry.Name);
        using var content = entry.Open();
        using var document = await JsonDocument.ParseAsync(content);
        var track = Assert.Single(document.RootElement.GetProperty("tracks").EnumerateArray());
        Assert.Equal("Test track", track.GetProperty("title").GetString());
        Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc), track.GetProperty("playedAtUtc")[0].GetDateTime());
        Assert.Equal(3, track.GetProperty("monthly")[0].GetProperty("playCount").GetInt32());
    }

    private sealed class FakeAuditStorage : IAuditStorage
    {
        public bool Rebuilt { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<long?> StorePurchaseEvidenceAsync(UploadedDocument document, CancellationToken cancellationToken = default) => Task.FromResult<long?>(1);
        public Task<long> IndexTrackAsync(string fullPath, long? purchaseEvidenceId, UploadedDocument? license, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<long?> FindTrackIdByMusicUriAsync(string uri, CancellationToken cancellationToken = default) => Task.FromResult<long?>(1);
        public Task<long?> FindTrackIdByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult<long?>(1);
        public Task RecordPlayAsync(long trackId, DateTime playedAtUtc, string playbackMode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> GetEffectiveGenresAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyDictionary<string, TrackMetadata>> GetTrackMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, TrackMetadata>>(new Dictionary<string, TrackMetadata>());
        public Task SetGenreOverrideAsync(long trackId, string? genre, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RebuildMonthlySummaryAsync(CancellationToken cancellationToken = default)
        {
            Rebuilt = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlayAuditReportItem>> GetPlayAuditReportAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlayAuditReportItem>>(
            [new PlayAuditReportItem(
                "Test track",
                [new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc)],
                [new PlayAuditMonth("2026-09", 3)])]);
    }
}
