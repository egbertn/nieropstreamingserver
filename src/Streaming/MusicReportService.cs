using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Streaming;

public enum TrackCopyrightStatus
{
    Free,
    Protected,
    Unspecified
}

public sealed record TrackCopyrightDetails(
    TrackCopyrightStatus Status,
    string BadgeText,
    string Summary,
    string? CopyrightTag,
    string? Publisher,
    string? Isrc,
    string? LicenseMarker
);

public sealed record TrackReportItem(
    string RelativePath,
    string FileName,
    long FileSizeBytes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    TimeSpan Duration,
    string? Title,
    string? Artist,
    string? Album,
    uint? Year,
    string? Genre,
    int? BitrateKbps,
    string? Codec,
    TrackCopyrightDetails Copyright
);

public sealed record MusicReportData(
    DateTime GeneratedAtUtc,
    IReadOnlyList<TrackReportItem> Tracks,
    TimeSpan TotalDuration,
    long TotalSizeBytes,
    int FreeCount,
    int ProtectedCount,
    int UnspecifiedCount
);

public interface IMusicReportService
{
    MusicReportData CollectReportData();
    byte[] GeneratePdfReport(MusicReportData? data = null);
}

public sealed class MusicReportService (StreamingOptions options): IMusicReportService
{
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".flac", ".wav", ".ogg"];

    private static readonly string[] FreeLicenseMarkers =
    [
        "cc0", "creative commons", "public domain", "publiek domein", "royalty-free", "royalty free",
        "no rights reserved", "unlicense", "open audio", "cc-by", "gpl", "mit", "free music"
    ];

    private static readonly string[] LabelMarkers =
    [
        "records", "recordings", "music group", "entertainment", "label", "publishing", "sony", "universal", "warner"
    ];

    public MusicReportData CollectReportData()
    {
        var tracks = new List<TrackReportItem>();

        if (Directory.Exists(options.MusicRoot))
        {
            var files = Directory.EnumerateFiles(options.MusicRoot, "*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in files)
            {
                var item = InspectTrack(filePath);
                if (item is not null)
                {
                    tracks.Add(item);
                }
            }
        }

        var totalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        var totalSize = tracks.Sum(t => t.FileSizeBytes);
        var freeCount = tracks.Count(t => t.Copyright.Status == TrackCopyrightStatus.Free);
        var protectedCount = tracks.Count(t => t.Copyright.Status == TrackCopyrightStatus.Protected);
        var unspecifiedCount = tracks.Count(t => t.Copyright.Status == TrackCopyrightStatus.Unspecified);

        return new MusicReportData(
            DateTime.UtcNow,
            tracks,
            totalDuration,
            totalSize,
            freeCount,
            protectedCount,
            unspecifiedCount
        );
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "TagLibSharp dynamically selects the audio handler; the dependency is rooted in the project file.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "TagLibSharp uses legacy exception serialization internally.")]
    private TrackReportItem? InspectTrack(string fullPath)
    {
        try
        {
            var fileInfo = new FileInfo(fullPath);
            var relativePath = Path.GetRelativePath(options.MusicRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            var fileName = Path.GetFileName(fullPath);

            string? title = null;
            string? artist = null;
            string? album = null;
            uint? year = null;
            string? genre = null;
            var duration = TimeSpan.Zero;
            int? bitrate = null;
            string? codec = null;

            string? copyrightTag = null;
            string? publisher = null;
            string? isrc = null;
            string? comment = null;

            try
            {
                using var tagFile = TagLib.File.Create(fullPath);
                var tag = tagFile.Tag;
                var props = tagFile.Properties;

                if (tag != null)
                {
                    title = string.IsNullOrWhiteSpace(tag.Title) ? null : tag.Title.Trim();
                    artist = FirstTagValue(tag.Performers, tag.FirstPerformer);
                    album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album.Trim();
                    year = tag.Year > 0 ? tag.Year : null;
                    genre = FirstTagValue(tag.Genres, tag.FirstGenre);

                    copyrightTag = string.IsNullOrWhiteSpace(tag.Copyright) ? null : tag.Copyright.Trim();
                    publisher = string.IsNullOrWhiteSpace(tag.Publisher) ? null : tag.Publisher.Trim();
                    isrc = string.IsNullOrWhiteSpace(tag.ISRC) ? null : tag.ISRC.Trim();
                    comment = string.IsNullOrWhiteSpace(tag.Comment) ? null : tag.Comment.Trim();
                }

                if (props != null)
                {
                    duration = props.Duration;
                    bitrate = props.AudioBitrate > 0 ? props.AudioBitrate : null;
                    codec = props.Description;
                }
            }
            catch
            {
                // Unreadable metadata, proceed with basic file info
            }

            var copyrightDetails = AnalyzeCopyright(fullPath, copyrightTag, publisher, isrc, comment);

            return new TrackReportItem(
                relativePath,
                fileName,
                fileInfo.Length,
                fileInfo.CreationTimeUtc,
                fileInfo.LastWriteTimeUtc,
                duration,
                title,
                artist,
                album,
                year,
                genre,
                bitrate,
                codec,
                copyrightDetails
            );
        }
        catch
        {
            return null;
        }
    }

    private static TrackCopyrightDetails AnalyzeCopyright(string fullPath, string? copyrightTag, string? publisher, string? isrc, string? comment)
    {
        // 1. Check explicit open / free license indicators
        string? matchedFreeLicense = null;
        if (!string.IsNullOrWhiteSpace(copyrightTag) && ContainsAny(copyrightTag, FreeLicenseMarkers, out matchedFreeLicense))
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Free,
                "FREE (LICENTIE)",
                $"Free license / public domain detected in copyright tag: \"{copyrightTag}\"",
                copyrightTag, publisher, isrc, matchedFreeLicense);
        }

        if (!string.IsNullOrWhiteSpace(comment) && ContainsAny(comment, FreeLicenseMarkers, out matchedFreeLicense))
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Free,
                "FREE (COMM)",
                $"Free license / public domain detected in comment: \"{comment}\"",
                copyrightTag, publisher, isrc, matchedFreeLicense);
        }

        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        if (ContainsAny(fileName, FreeLicenseMarkers, out matchedFreeLicense))
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Free,
                "FREE (BESTANDSNAAM)",
                $"Free license / public domain detected in file name: \"{fileName}\"",
                copyrightTag, publisher, isrc, matchedFreeLicense);
        }

        // 2. Check commercial / copyright indicators
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Protected,
                "BESCHERMD (ISRC)",
                $"Commerciële track met geregistreerde ISRC-code: {isrc}",
                copyrightTag, publisher, isrc, null);
        }

        if (!string.IsNullOrWhiteSpace(copyrightTag))
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Protected,
                "BESCHERMD (COPYRIGHT)",
                $"Auteursrechtvermelding aanwezig: \"{copyrightTag}\"",
                copyrightTag, publisher, isrc, null);
        }

        if (!string.IsNullOrWhiteSpace(publisher) && ContainsAny(publisher, LabelMarkers, out _))
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Protected,
                "BESCHERMD (LABEL)",
                $"Uitgever duidt op commercieel platenlabel: \"{publisher}\"",
                copyrightTag, publisher, isrc, null);
        }

        // 3. Heuristic check via CopyrightCheck
        var flagged = CopyrightCheck.Inspect(fullPath);
        if (flagged is not null)
        {
            return new TrackCopyrightDetails(
                TrackCopyrightStatus.Protected,
                "MOGELIJK BESCHERMD",
                flagged,
                copyrightTag, publisher, isrc, null);
        }

        // 4. No tags or markers found
        return new TrackCopyrightDetails(
            TrackCopyrightStatus.Unspecified,
            "GEEN VERMELDING",
            "Geen copyrightvermelding of ISRC-code gevonden in bestandstags",
            copyrightTag, publisher, isrc, null);
    }

    internal static bool ContainsAny(string text, string[] markers, out string? matched)
    {
        foreach (var m in markers)
        {
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase))
            {
                matched = m;
                return true;
            }
        }
        matched = null;
        return false;
    }

    internal static string? FirstTagValue(string[]? values, string? firstValue)
    {
        if (values is { Length: > 0 })
            return string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

        return string.IsNullOrWhiteSpace(firstValue) ? null : firstValue.Trim();
    }

    public byte[] GeneratePdfReport(MusicReportData? data = null)
    {
        data ??= CollectReportData();
        var doc = new PdfDocument();

        var currentY = PdfDocument.MarginTop;

        // Draw First Page Header & Overview
        DrawFirstPageHeader(doc, data, ref currentY);

        // Draw Tracks List
        var trackNumber = 1;
        foreach (var track in data.Tracks)
        {
            // Estimate height needed for this track card (~68 points)
            var cardHeight = 66f;
            if (currentY + cardHeight > PdfDocument.PageHeight - PdfDocument.MarginBottom - 20f)
            {
                doc.NewPage();
                currentY = PdfDocument.MarginTop;
                DrawSubsequentPageHeader(doc, ref currentY);
            }

            DrawTrackCard(doc, track, trackNumber++, ref currentY);
        }

        if (data.Tracks.Count == 0)
        {
            doc.DrawText("Geen muziekbestanden gevonden in de music folder.", PdfDocument.MarginLeft + 10f, currentY + 20f, PdfDocument.FontType.HelveticaOblique, 11f, PdfDocument.PdfColor.Gray);
        }

        // Draw Footers on all pages
        var totalPages = doc.PageCount;
        for (var pageIdx = 0; pageIdx < totalPages; pageIdx++)
        {
            doc.SetCurrentPage(pageIdx);
            var footerY = PdfDocument.PageHeight - PdfDocument.MarginBottom + 10f;
            doc.DrawLine(PdfDocument.MarginLeft, footerY - 6f, PdfDocument.MarginLeft + PdfDocument.ContentWidth, footerY - 6f, PdfDocument.PdfColor.BorderGray, 0.5f);
            doc.DrawText("Sonos Streaming Server  -  /report", PdfDocument.MarginLeft, footerY, PdfDocument.FontType.Helvetica, 8f, PdfDocument.PdfColor.Gray);
            var pageText = $"Pagina {pageIdx + 1} van {totalPages}";
            var pageTextWidth = PdfDocument.MeasureTextWidth(pageText, PdfDocument.FontType.Helvetica, 8f);
            doc.DrawText(pageText, PdfDocument.MarginLeft + PdfDocument.ContentWidth - pageTextWidth, footerY, PdfDocument.FontType.Helvetica, 8f, PdfDocument.PdfColor.Gray);
        }

        return doc.Build();
    }

    private static void DrawFirstPageHeader(PdfDocument doc, MusicReportData data, ref float currentY)
    {
        // Top accent bar
        doc.FillRect(PdfDocument.MarginLeft, currentY, PdfDocument.ContentWidth, 6f, PdfDocument.PdfColor.PrimaryGreen);
        currentY += 12f;

        // Title
        doc.DrawText("Muziekcollectie Rapport", PdfDocument.MarginLeft, currentY, PdfDocument.FontType.HelveticaBold, 20f, PdfDocument.PdfColor.PrimaryDark);
        currentY += 24f;

        // Subtitle / Date
        var generatedText = $"Gegenereerd op {data.GeneratedAtUtc.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture)} UTC  -  Sonos Streaming Server";
        doc.DrawText(generatedText, PdfDocument.MarginLeft, currentY, PdfDocument.FontType.Helvetica, 9f, PdfDocument.PdfColor.Gray);
        currentY += 16f;

        // Divider
        doc.DrawLine(PdfDocument.MarginLeft, currentY, PdfDocument.MarginLeft + PdfDocument.ContentWidth, currentY, PdfDocument.PdfColor.BorderGray, 1f);
        currentY += 12f;

        // Summary KPI Box
        var boxHeight = 52f;
        doc.FillRect(PdfDocument.MarginLeft, currentY, PdfDocument.ContentWidth, boxHeight, PdfDocument.PdfColor.LightGray);
        doc.DrawRect(PdfDocument.MarginLeft, currentY, PdfDocument.ContentWidth, boxHeight, PdfDocument.PdfColor.BorderGray, 1f);

        var colWidth = PdfDocument.ContentWidth / 4f;

        // Col 1: Totaal nummers
        DrawStatColumn(doc, PdfDocument.MarginLeft + 10f, currentY + 8f, "TOTAAL NUMMERS", data.Tracks.Count.ToString(CultureInfo.InvariantCulture));

        // Col 2: Totale speelduur
        var totalDurationStr = FormatDuration(data.TotalDuration);
        DrawStatColumn(doc, PdfDocument.MarginLeft + colWidth + 5f, currentY + 8f, "TOTALE SPEELDUUR", totalDurationStr);

        // Col 3: Totale bestandsgrootte
        var totalSizeMb = data.TotalSizeBytes / (1024f * 1024f);
        var totalSizeStr = totalSizeMb >= 1000f
            ? $"{(totalSizeMb / 1024f):0.##} GB"
            : $"{totalSizeMb:0.#} MB";
        DrawStatColumn(doc, PdfDocument.MarginLeft + (colWidth * 2) + 5f, currentY + 8f, "TOTALE GROOTTE", totalSizeStr);

        // Col 4: Copyright status samenvatting
        var crSummary = $"{data.FreeCount} Vrij  /  {data.ProtectedCount} Beschermd  /  {data.UnspecifiedCount} Onbekend";
        DrawStatColumn(doc, PdfDocument.MarginLeft + (colWidth * 3) + 5f, currentY + 8f, "COPYRIGHT STATUS", crSummary, 8.5f);

        currentY += boxHeight + 16f;

        // Section Title
        doc.DrawText("Overzicht van Bestanden & Tags", PdfDocument.MarginLeft, currentY, PdfDocument.FontType.HelveticaBold, 12f, PdfDocument.PdfColor.PrimaryDark);
        currentY += 16f;
    }

    private static void DrawSubsequentPageHeader(PdfDocument doc, ref float currentY)
    {
        doc.FillRect(PdfDocument.MarginLeft, currentY, PdfDocument.ContentWidth, 3f, PdfDocument.PdfColor.PrimaryGreen);
        currentY += 8f;

        doc.DrawText("Muziekcollectie Rapport (Vervolg)", PdfDocument.MarginLeft, currentY, PdfDocument.FontType.HelveticaBold, 11f, PdfDocument.PdfColor.PrimaryDark);
        currentY += 14f;

        doc.DrawLine(PdfDocument.MarginLeft, currentY, PdfDocument.MarginLeft + PdfDocument.ContentWidth, currentY, PdfDocument.PdfColor.BorderGray, 0.5f);
        currentY += 10f;
    }

    private static void DrawStatColumn(PdfDocument doc, float x, float y, string label, string value, float valueSize = 13f)
    {
        doc.DrawText(label, x, y, PdfDocument.FontType.HelveticaBold, 7f, PdfDocument.PdfColor.Gray);
        doc.DrawText(value, x, y + 14f, PdfDocument.FontType.HelveticaBold, valueSize, PdfDocument.PdfColor.PrimaryDark);
    }

    private static void DrawTrackCard(PdfDocument doc, TrackReportItem track, int index, ref float currentY)
    {
        var cardHeight = 62f;
        var x = PdfDocument.MarginLeft;
        var width = PdfDocument.ContentWidth;

        // Card background
        var bg = (index % 2 == 1) ? PdfDocument.PdfColor.White : new PdfDocument.PdfColor(249, 250, 251);
        doc.FillRect(x, currentY, width, cardHeight, bg);
        doc.DrawRect(x, currentY, width, cardHeight, PdfDocument.PdfColor.BorderGray, 0.75f);

        // Header bar of card: Index + Filename + Badge
        var headerY = currentY + 6f;
        var headerText = $"#{index}  {track.RelativePath}";
        if (headerText.Length > 65)
            headerText = headerText[..62] + "...";

        doc.DrawText(headerText, x + 8f, headerY, PdfDocument.FontType.HelveticaBold, 9.5f, PdfDocument.PdfColor.PrimaryDark);

        // Copyright Badge on the right
        var (badgeBg, badgeFg) = track.Copyright.Status switch
        {
            TrackCopyrightStatus.Free => (PdfDocument.PdfColor.BadgeGreenBg, PdfDocument.PdfColor.BadgeGreenFg),
            TrackCopyrightStatus.Protected => (PdfDocument.PdfColor.BadgeRedBg, PdfDocument.PdfColor.BadgeRedFg),
            _ => (PdfDocument.PdfColor.BadgeGrayBg, PdfDocument.PdfColor.BadgeGrayFg)
        };
        var badgeText = track.Copyright.BadgeText;
        var badgeWidth = PdfDocument.MeasureTextWidth(badgeText, PdfDocument.FontType.HelveticaBold, 7.5f) + 10f;
        var badgeX = x + width - badgeWidth - 8f;
        doc.DrawBadge(badgeText, badgeX, headerY - 1f, badgeBg, badgeFg, PdfDocument.FontType.HelveticaBold, 7.5f);

        // Row 1: Duration | Size | Created | Format/Bitrate
        var row1Y = headerY + 14f;
        var durationStr = FormatDuration(track.Duration);
        var sizeMb = track.FileSizeBytes / (1024f * 1024f);
        var sizeStr = $"{sizeMb:0.#} MB";
        var createdStr = track.CreatedUtc.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        var codecStr = track.BitrateKbps.HasValue ? $"{track.Codec ?? Path.GetExtension(track.FileName).TrimStart('.').ToUpperInvariant()} ({track.BitrateKbps} kbps)" : (track.Codec ?? Path.GetExtension(track.FileName).TrimStart('.').ToUpperInvariant());

        var row1Text = $"Duur: {durationStr}  |  Grootte: {sizeStr}  |  Aangemaakt: {createdStr}  |  Formaat: {codecStr}";
        doc.DrawText(row1Text, x + 8f, row1Y, PdfDocument.FontType.Helvetica, 8f, PdfDocument.PdfColor.DarkGray);

        // Row 2: Tags (Titel, Artiest, Album, Jaar, Genre)
        var row2Y = row1Y + 13f;
        var titlePart = track.Title != null ? $"Titel: {track.Title}" : $"Titel: {Path.GetFileNameWithoutExtension(track.FileName)}";
        var artistPart = track.Artist != null ? $"  |  Artiest: {track.Artist}" : "";
        var albumPart = track.Album != null ? $"  |  Album: {track.Album}" : "";
        var yearPart = track.Year.HasValue ? $" ({track.Year})" : "";
        var genrePart = track.Genre != null ? $"  |  Genre: {track.Genre}" : "";

        var row2Text = $"{titlePart}{artistPart}{albumPart}{yearPart}{genrePart}";
        if (row2Text.Length > 95)
            row2Text = row2Text[..92] + "...";
        doc.DrawText(row2Text, x + 8f, row2Y, PdfDocument.FontType.Helvetica, 8f, PdfDocument.PdfColor.DarkGray);

        // Row 3: Copyright toelichting
        var row3Y = row2Y + 13f;
        var crDesc = $"Copyright: {track.Copyright.Summary}";
        if (crDesc.Length > 105)
            crDesc = crDesc[..102] + "...";

        var crColor = track.Copyright.Status switch
        {
            TrackCopyrightStatus.Free => PdfDocument.PdfColor.BadgeGreenFg,
            TrackCopyrightStatus.Protected => PdfDocument.PdfColor.BadgeRedFg,
            _ => PdfDocument.PdfColor.Gray
        };
        doc.DrawText(crDesc, x + 8f, row3Y, PdfDocument.FontType.HelveticaOblique, 7.5f, crColor);

        currentY += cardHeight + 4f;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        }
        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
