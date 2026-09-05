using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Models;

// Coordinates are PDF points in the displayed CropBox (including page rotation).
// These records describe the PDF itself; no platform font measurement is involved.
public readonly record struct PdfTextPoint(double X, double Y);
public readonly record struct PdfTextBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y, double tolerance = 0) =>
        x >= X - tolerance && x <= Right + tolerance && y >= Y - tolerance && y <= Bottom + tolerance;
    public static PdfTextBox Union(IEnumerable<PdfTextBox> boxes)
    {
        var items = boxes.ToArray();
        if (items.Length == 0) return default;
        double x = items.Min(b => b.X), y = items.Min(b => b.Y);
        return new(x, y, items.Max(b => b.Right) - x, items.Max(b => b.Bottom) - y);
    }
}

public sealed record NativePdfGlyph
{
    public string Text { get; init; } = "";
    public byte[] Code { get; init; } = Array.Empty<byte>();
    public string RunId { get; init; } = "";
    public int ReplacementFontObjectNumber { get; init; }
    public string? ReplacementFontName { get; init; }
    public double Advance { get; init; }
    public PdfTextPoint Origin { get; init; }
    public PdfTextPoint End { get; init; }
    public PdfTextBox Bounds { get; init; }
    public int TextIndex { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsSoftBreak { get; init; }
}

public sealed record NativePdfRun
{
    public string Id { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public int OperationIndex { get; init; }
    public int OperandIndex { get; init; }
    public int FontObjectNumber { get; init; }
    public string FontName { get; init; } = "";
    public string Color { get; init; } = "#000000";
    public double FontSize { get; init; }
    public double DisplayFontSize { get; init; }
    public double HorizontalScale { get; init; }
    public double CharacterSpacing { get; init; }
    public double WordSpacing { get; init; }
    public double Rise { get; init; }
    public int RenderMode { get; init; }
    public float[] TextMatrix { get; init; } = Array.Empty<float>();
    public float[] Ctm { get; init; } = Array.Empty<float>();
    public double OriginalAdvance { get; init; }
    public bool IsVertical { get; init; }
    public List<NativePdfGlyph> Glyphs { get; init; } = new();
}

public sealed record NativePdfTextBlock
{
    public string Text { get; init; } = "";
    public List<NativePdfRun> Runs { get; init; } = new();
    public List<NativePdfGlyph> Glyphs { get; init; } = new();
    public PdfTextBox Bounds { get; init; }
    public List<PdfTextBox> Lines { get; init; } = new();
    public double LineHeight { get; init; }
}

public sealed record NativePdfTextEdit(NativePdfTextBlock Block, string Text, double DeltaX = 0, double DeltaY = 0);
public sealed record NativePdfTextResult(byte[] Bytes, NativePdfTextBlock Layout)
{
    public string? FontSubstitutionStatus => Layout.Glyphs.Any(g => !g.IsVirtual && g.ReplacementFontName != null)
        ? "글리프 누락으로 글꼴 대체: " + string.Join(", ", Layout.Glyphs.Where(g => !g.IsVirtual)
            .Select(g => g.ReplacementFontName).Where(n => n != null).Distinct()) : null;
}
