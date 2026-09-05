using System.Globalization;
using System.Text;

namespace Streaming;

/// <summary>
/// A lightweight, zero-dependency PDF document generator that produces standard PDF 1.4 documents.
/// Fully compatible with Native AOT and trimming.
/// </summary>
public sealed class PdfDocument
{
    public const float PageWidth = 595.28f;  // A4 width in points
    public const float PageHeight = 841.89f; // A4 height in points
    public const float MarginLeft = 40f;
    public const float MarginRight = 40f;
    public const float MarginTop = 45f;
    public const float MarginBottom = 45f;
    public const float ContentWidth = PageWidth - MarginLeft - MarginRight;

    public enum FontType
    {
        Helvetica = 1,
        HelveticaBold = 2,
        HelveticaOblique = 3
    }

    public readonly record struct PdfColor(byte R, byte G, byte B)
    {
        public static readonly PdfColor Black = new(0, 0, 0);
        public static readonly PdfColor White = new(255, 255, 255);
        public static readonly PdfColor DarkGray = new(55, 65, 81);
        public static readonly PdfColor Gray = new(107, 114, 128);
        public static readonly PdfColor LightGray = new(243, 244, 246);
        public static readonly PdfColor BorderGray = new(229, 231, 235);
        public static readonly PdfColor PrimaryGreen = new(45, 125, 70);
        public static readonly PdfColor PrimaryDark = new(30, 41, 59);
        public static readonly PdfColor BadgeGreenBg = new(220, 252, 231);
        public static readonly PdfColor BadgeGreenFg = new(22, 101, 52);
        public static readonly PdfColor BadgeYellowBg = new(254, 243, 199);
        public static readonly PdfColor BadgeYellowFg = new(146, 64, 14);
        public static readonly PdfColor BadgeRedBg = new(254, 226, 226);
        public static readonly PdfColor BadgeRedFg = new(153, 27, 27);
        public static readonly PdfColor BadgeGrayBg = new(243, 244, 246);
        public static readonly PdfColor BadgeGrayFg = new(75, 85, 99);

        public string ToFillString() =>
            string.Create(CultureInfo.InvariantCulture, $"{R / 255f:0.###} {G / 255f:0.###} {B / 255f:0.###} rg");

        public string ToStrokeString() =>
            string.Create(CultureInfo.InvariantCulture, $"{R / 255f:0.###} {G / 255f:0.###} {B / 255f:0.###} RG");
    }

    private sealed class Page
    {
        public StringBuilder Stream { get; } = new();
    }

    private readonly List<Page> _pages = [];
    private Page? _currentPage;
    public int PageCount => _pages.Count;

    public PdfDocument()
    {
        NewPage();
    }

    public void NewPage()
    {
        _currentPage = new Page();
        _pages.Add(_currentPage);
    }

    public void SetCurrentPage(int pageIndex)
    {
        if (pageIndex >= 0 && pageIndex < _pages.Count)
        {
            _currentPage = _pages[pageIndex];
        }
    }

    public void FillRect(float x, float y, float width, float height, PdfColor color)
    {
        // PDF coordinate origin is bottom-left
        var pdfY = PageHeight - y - height;
        var sb = GetStream();
        sb.AppendLine("q");
        sb.AppendLine(color.ToFillString());
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{x:0.##} {pdfY:0.##} {width:0.##} {height:0.##} re f"));
        sb.AppendLine("Q");
    }

    public void DrawRect(float x, float y, float width, float height, PdfColor strokeColor, float lineWidth = 1f)
    {
        var pdfY = PageHeight - y - height;
        var sb = GetStream();
        sb.AppendLine("q");
        sb.AppendLine(strokeColor.ToStrokeString());
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{lineWidth:0.##} w"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{x:0.##} {pdfY:0.##} {width:0.##} {height:0.##} re S"));
        sb.AppendLine("Q");
    }

    public void DrawRoundedRect(float x, float y, float width, float height, float radius, PdfColor fillColor, PdfColor? strokeColor = null, float lineWidth = 1f)
    {
        // Simplified rounded rect approximation with filled background
        FillRect(x, y, width, height, fillColor);
        if (strokeColor.HasValue)
        {
            DrawRect(x, y, width, height, strokeColor.Value, lineWidth);
        }
    }

    public void DrawLine(float x1, float y1, float x2, float y2, PdfColor color, float width = 1f)
    {
        var pdfY1 = PageHeight - y1;
        var pdfY2 = PageHeight - y2;
        var sb = GetStream();
        sb.AppendLine("q");
        sb.AppendLine(color.ToStrokeString());
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{width:0.##} w"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{x1:0.##} {pdfY1:0.##} m {x2:0.##} {pdfY2:0.##} l S"));
        sb.AppendLine("Q");
    }

    public void DrawText(string text, float x, float y, FontType font = FontType.Helvetica, float fontSize = 10f, PdfColor? color = null)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var fontColor = color ?? PdfColor.Black;
        var fontName = font switch
        {
            FontType.HelveticaBold => "/F2",
            FontType.HelveticaOblique => "/F3",
            _ => "/F1"
        };

        // Text baseline in PDF
        var pdfY = PageHeight - y - fontSize * 0.82f;
        var escaped = EscapePdfString(text);

        var sb = GetStream();
        sb.AppendLine("BT");
        sb.AppendLine(fontColor.ToFillString());
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{fontName} {fontSize:0.##} Tf"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{x:0.##} {pdfY:0.##} Td"));
        sb.AppendLine($"({escaped}) Tj");
        sb.AppendLine("ET");
    }

    public void DrawBadge(string text, float x, float y, PdfColor bgColor, PdfColor fgColor, FontType font = FontType.HelveticaBold, float fontSize = 8f)
    {
        var textWidth = MeasureTextWidth(text, font, fontSize);
        var paddingH = 6f;
        var paddingV = 3f;
        var badgeWidth = textWidth + (paddingH * 2);
        var badgeHeight = fontSize + (paddingV * 2);

        FillRect(x, y, badgeWidth, badgeHeight, bgColor);
        DrawText(text, x + paddingH, y + paddingV, font, fontSize, fgColor);
    }

    public float MeasureTextWidth(string text, FontType font, float fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return 0f;

        var scale = fontSize / 1000f;
        var isBold = font == FontType.HelveticaBold;
        var totalWidth = 0f;

        foreach (var ch in text)
        {
            totalWidth += GetCharWidth(ch, isBold) * scale;
        }

        return totalWidth;
    }

    private static float GetCharWidth(char ch, bool isBold)
    {
        if (ch == ' ') return 278f;
        if (ch >= '0' && ch <= '9') return 556f;
        if (ch is '.' or ',' or ':' or ';' or '!' or '\'' or '|' or '/' or '\\') return 278f;
        if (ch is '(' or ')' or '[' or ']' or '{' or '}') return 333f;
        if (ch is '-' or '_' or '+') return 333f;
        if (ch is 'i' or 'l' or 'j' or 't' or 'f' or 'r') return isBold ? 333f : 278f;
        if (ch is 'm' or 'w') return isBold ? 889f : 833f;
        if (ch is 'M' or 'W') return isBold ? 944f : 889f;
        if (ch is 'I') return 333f;
        if (char.IsUpper(ch)) return isBold ? 722f : 667f;
        if (char.IsLower(ch)) return isBold ? 556f : 500f;

        return 500f;
    }

    public List<string> WrapText(string text, float maxWidth, FontType font, float fontSize)
    {
        List<string> lines = [];
        if (string.IsNullOrEmpty(text))
            return lines;

        var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            var testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
            if (MeasureTextWidth(testLine, font, fontSize) <= maxWidth)
            {
                if (currentLine.Length > 0) currentLine.Append(' ');
                currentLine.Append(word);
            }
            else
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                }

                // If a single word is wider than maxWidth, hard wrap it
                if (MeasureTextWidth(word, font, fontSize) > maxWidth)
                {
                    var chunk = new StringBuilder();
                    foreach (var ch in word)
                    {
                        var testChunk = chunk.ToString() + ch;
                        if (MeasureTextWidth(testChunk, font, fontSize) <= maxWidth)
                        {
                            chunk.Append(ch);
                        }
                        else
                        {
                            lines.Add(chunk.ToString());
                            chunk.Clear();
                            chunk.Append(ch);
                        }
                    }
                    if (chunk.Length > 0)
                        currentLine.Append(chunk);
                }
                else
                {
                    currentLine.Append(word);
                }
            }
        }

        if (currentLine.Length > 0)
            lines.Add(currentLine.ToString());

        return lines;
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII);

        var objects = new List<long>();

        void WriteObjHeader(int objNum)
        {
            while (objects.Count < objNum)
                objects.Add(0);
            objects[objNum - 1] = ms.Position;
            writer.Write(Encoding.ASCII.GetBytes($"{objNum} 0 obj\n"));
        }

        void WriteObjEnd()
        {
            writer.Write(Encoding.ASCII.GetBytes("endobj\n"));
        }

        // 1. PDF Header
        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n%\xe2\xe3\xcf\xd3\n"));

        // Catalog (Obj 1)
        WriteObjHeader(1);
        writer.Write(Encoding.ASCII.GetBytes("<<\n  /Type /Catalog\n  /Pages 2 0 R\n>>\n"));
        WriteObjEnd();

        // Fonts (Objs 3, 4, 5)
        WriteObjHeader(3);
        writer.Write(Encoding.ASCII.GetBytes("<<\n  /Type /Font\n  /Subtype /Type1\n  /BaseFont /Helvetica\n  /Encoding /WinAnsiEncoding\n>>\n"));
        WriteObjEnd();

        WriteObjHeader(4);
        writer.Write(Encoding.ASCII.GetBytes("<<\n  /Type /Font\n  /Subtype /Type1\n  /BaseFont /Helvetica-Bold\n  /Encoding /WinAnsiEncoding\n>>\n"));
        WriteObjEnd();

        WriteObjHeader(5);
        writer.Write(Encoding.ASCII.GetBytes("<<\n  /Type /Font\n  /Subtype /Type1\n  /BaseFont /Helvetica-Oblique\n  /Encoding /WinAnsiEncoding\n>>\n"));
        WriteObjEnd();

        // Pages List & Content streams
        var pageObjNums = new List<int>();
        var contentObjNums = new List<int>();
        var nextObj = 6;

        for (var i = 0; i < _pages.Count; i++)
        {
            pageObjNums.Add(nextObj++);
            contentObjNums.Add(nextObj++);
        }

        // Pages Catalog (Obj 2)
        WriteObjHeader(2);
        var kidsStr = string.Join(" ", pageObjNums.Select(n => $"{n} 0 R"));
        writer.Write(Encoding.ASCII.GetBytes($"<<\n  /Type /Pages\n  /Kids [ {kidsStr} ]\n  /Count {_pages.Count}\n>>\n"));
        WriteObjEnd();

        // Page and Content objects
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var pageObj = pageObjNums[i];
            var contentObj = contentObjNums[i];

            // Page Object
            WriteObjHeader(pageObj);
            writer.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"<<\n  /Type /Page\n  /Parent 2 0 R\n  /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}]\n  /Resources <<\n    /Font <<\n      /F1 3 0 R\n      /F2 4 0 R\n      /F3 5 0 R\n    >>\n  >>\n  /Contents {contentObj} 0 R\n>>\n")));
            WriteObjEnd();

            // Content Object
            var contentBytes = Encoding.Latin1.GetBytes(page.Stream.ToString());
            WriteObjHeader(contentObj);
            writer.Write(Encoding.ASCII.GetBytes($"<<\n  /Length {contentBytes.Length}\n>>\nstream\n"));
            writer.Write(contentBytes);
            writer.Write(Encoding.ASCII.GetBytes("\nendstream\n"));
            WriteObjEnd();
        }

        // Cross Reference Table
        var xrefOffset = ms.Position;
        var totalObjects = nextObj;

        writer.Write(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var i = 1; i < totalObjects; i++)
        {
            var offset = objects[i - 1];
            writer.Write(Encoding.ASCII.GetBytes($"{offset:D10} 00000 n \n"));
        }

        // Trailer
        writer.Write(Encoding.ASCII.GetBytes($"trailer\n<<\n  /Size {totalObjects}\n  /Root 1 0 R\n>>\nstartxref\n{xrefOffset}\n%%EOF\n"));

        return ms.ToArray();
    }

    private StringBuilder GetStream()
    {
        if (_currentPage == null)
            NewPage();
        return _currentPage!.Stream;
    }

    private static string EscapePdfString(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '(':
                    sb.Append(@"\(");
                    break;
                case ')':
                    sb.Append(@"\)");
                    break;
                case '\r':
                case '\n':
                    sb.Append(' ');
                    break;
                default:
                    // Check if char is supported in Windows-1252 / WinAnsiEncoding
                    if (ch <= 0x7E)
                    {
                        sb.Append(ch);
                    }
                    else if (ch is >= (char)0xA0 and <= (char)0xFF)
                    {
                        sb.Append(ch);
                    }
                    else
                    {
                        // Map common characters
                        sb.Append(ch switch
                        {
                            '“' or '”' => '"',
                            '‘' or '’' => '\'',
                            '–' or '—' => '-',
                            '…' => "...",
                            '•' => "*",
                            _ => '?'
                        });
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
