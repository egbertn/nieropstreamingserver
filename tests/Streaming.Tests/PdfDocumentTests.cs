using System.Text;

namespace Streaming.Tests;

public sealed class PdfDocumentTests
{
    [Fact]
    public void Build_GeneratesValidPdfStructure()
    {
        var doc = new PdfDocument();
        doc.DrawText("Titel van het rapport", 50, 50, PdfDocument.FontType.HelveticaBold, 16);
        doc.DrawText("Een gewone regel tekst met speciale tekens: (haakjes) & \\backslashes\\", 50, 80, PdfDocument.FontType.Helvetica, 10);
        doc.FillRect(50, 100, 200, 30, PdfDocument.PdfColor.PrimaryGreen);
        doc.DrawLine(50, 150, 300, 150, PdfDocument.PdfColor.BorderGray);
        doc.DrawBadge("FREE", 50, 160, PdfDocument.PdfColor.BadgeGreenBg, PdfDocument.PdfColor.BadgeGreenFg);

        var bytes = doc.Build();

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);

        var pdfString = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", pdfString);
        Assert.Contains("%%EOF", pdfString);
        Assert.Contains("/Type /Catalog", pdfString);
        Assert.Contains("/Type /Pages", pdfString);
        Assert.Contains("/Type /Font", pdfString);
    }

    [Fact]
    public void Build_MultiPage_GeneratesMultiplePages()
    {
        var doc = new PdfDocument();
        doc.DrawText("Pagina 1 tekst", 50, 50);
        doc.NewPage();
        doc.DrawText("Pagina 2 tekst", 50, 50);

        Assert.Equal(2, doc.PageCount);

        var bytes = doc.Build();
        var pdfString = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Count 2", pdfString);
    }

    [Fact]
    public void WrapText_SplitsLongLinesCorrectly()
    {
        var doc = new PdfDocument();
        var longText = "Dit is een hele lange regel tekst die zeker gesplitst moet worden over meerdere regels in het gegenereerde PDF document.";
        var lines = doc.WrapText(longText, 150f, PdfDocument.FontType.Helvetica, 10f);

        Assert.True(lines.Count > 1);
    }
}
