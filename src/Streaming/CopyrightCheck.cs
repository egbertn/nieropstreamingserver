using System.Diagnostics.CodeAnalysis;

namespace Streaming;

/// <summary>
/// Heuristic screening of uploaded tracks based on embedded metadata.
/// This can only catch obvious cases (an ISRC code, a copyright tag, a label name in the
/// publisher field); it cannot prove a track is license-free, since that information is
/// often missing or wrong. Treat it as a spam filter, not a legal guarantee. Responsibility
/// for the right to use a track stays with whoever uploads it.
/// </summary>
public static class CopyrightCheck
{
    private static readonly string[] FreeLicenseMarkers =
    [
        "cc0", "creative commons", "public domain", "royalty-free", "royalty free", "no rights reserved"
    ];

    private static readonly string[] LabelMarkers =
    [
        "records", "recordings", "music group", "entertainment", "label", "publishing"
    ];

    /// <summary>Returns a human-readable reason the track was flagged, or null if it passed.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "TagLibSharp dynamically selects the audio handler; the dependency is rooted in the project file.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "TagLibSharp uses legacy exception serialization internally; failures are contained by this heuristic check.")]
    public static string? Inspect(string filePath)
    {
        TagLib.Tag tag;
        try
        {
            using var file = TagLib.File.Create(filePath);
            tag = file.Tag;
        }
        catch (Exception)
        {
            // Unreadable or missing tags: nothing to flag on.
            return null;
        }

        // Commercially distributed tracks are almost always assigned an ISRC code.
        if (!string.IsNullOrWhiteSpace(tag.ISRC))
            return $"bevat een ISRC-code ({tag.ISRC}), vrijwel altijd commercieel uitgegeven muziek";

        var copyright = tag.Copyright;
        if (!string.IsNullOrWhiteSpace(copyright) && !ContainsAny(copyright, FreeLicenseMarkers))
            return $"copyright-tag aanwezig: \"{copyright}\"";

        var publisher = tag.Publisher;
        if (!string.IsNullOrWhiteSpace(publisher) && ContainsAny(publisher, LabelMarkers))
            return $"uitgever \"{publisher}\" lijkt een platenlabel";

        return null;
    }

    private static bool ContainsAny(string text, string[] markers) =>
        markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
}
