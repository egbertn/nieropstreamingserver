using System.IO.Compression;
using System.Text.Json;

namespace Streaming;

public interface IPlayAuditReportService
{
    Task<byte[]> GenerateZipAsync(CancellationToken cancellationToken = default);
}

public sealed class PlayAuditReportService(IAuditStorage storage) : IPlayAuditReportService
{
    public async Task<byte[]> GenerateZipAsync(CancellationToken cancellationToken = default)
    {
        await storage.RebuildMonthlySummaryAsync(cancellationToken);
        var report = await storage.GetPlayAuditReportAsync(cancellationToken);

        await using var jsonStream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(jsonStream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("generatedAtUtc", DateTime.UtcNow);
            writer.WriteStartArray("tracks");
            foreach (var track in report)
            {
                writer.WriteStartObject();
                writer.WriteString("title", track.Title);
                writer.WriteStartArray("playedAtUtc");
                foreach (var playedAt in track.PlayedAtUtc)
                    writer.WriteStringValue(playedAt);
                writer.WriteEndArray();
                writer.WriteStartArray("monthly");
                foreach (var month in track.Monthly)
                {
                    writer.WriteStartObject();
                    writer.WriteString("month", month.Month);
                    writer.WriteNumber("playCount", month.PlayCount);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("speel-audit.json", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            jsonStream.Position = 0;
            await jsonStream.CopyToAsync(entryStream, cancellationToken);
        }
        return zipStream.ToArray();
    }
}
