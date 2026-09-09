namespace Streaming.Tests;

public sealed class InternalHelpersTests
{
    [Fact]
    public void EscapeJson_EscapesControlCharactersAndQuotes()
    {
        Assert.Equal("line\\n\\\"quoted\\\"\\\\value", AuditStorage.EscapeJson("line\n\"quoted\"\\value"));
        Assert.Equal(string.Empty, AuditStorage.EscapeJson(null));
    }

    [Fact]
    public void ContainsAny_ReturnsTheFirstMatchingMarkerIgnoringCase()
    {
        var found = MusicReportService.ContainsAny("Licensed under CC-BY-SA", ["cc0", "cc-by"], out var marker);

        Assert.True(found);
        Assert.Equal("cc-by", marker);
    }

    [Fact]
    public void FirstTagValue_PrefersNonEmptyTagValuesThenFallback()
    {
        Assert.Equal("Artist", MusicReportService.FirstTagValue(["", " Artist "], "Fallback"));
        Assert.Equal("Fallback", MusicReportService.FirstTagValue([], "Fallback"));
        Assert.Null(MusicReportService.FirstTagValue(null, " "));
    }

    [Fact]
    public void EscapePdfString_EscapesPdfDelimitersAndBackslashes()
    {
        Assert.Equal("\\(title\\) \\\\ path", PdfDocument.EscapePdfString("(title) \\ path"));
    }
}