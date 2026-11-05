using PDF_simple_edit.Models;
using System;
using System.Linq;

namespace PDF_simple_edit.Services;

public static class TextInputLayoutService
{
    public static PdfTextBox GetEditingBounds(NativePdfTextBlock block)
    {
        // Ink bounds deliberately omit spaces and empty lines. The input host
        // and its border must also contain every caret position, including the
        // advance after trailing whitespace and the line after a final Enter.
        double fontSize = block.Runs.Select(r => r.DisplayFontSize).DefaultIfEmpty(12).Max();
        var boxes = block.Glyphs.Select(g =>
        {
            double height = g.Bounds.Height > 0 ? g.Bounds.Height : fontSize;
            double top = g.Bounds.Height > 0 ? g.Bounds.Y - g.Origin.Y : -height;
            return new PdfTextBox(Math.Min(g.Origin.X, g.End.X), Math.Min(g.Origin.Y, g.End.Y) + top,
                Math.Abs(g.End.X - g.Origin.X), Math.Abs(g.End.Y - g.Origin.Y) + height);
        });
        if (block.Bounds.Height > 0) boxes = boxes.Append(block.Bounds);
        return PdfTextBox.Union(boxes);
    }

    public static double MeasureLineWidth(string line, Func<string, double> measure)
    {
        double width = measure(line);
        // TextBlock can omit trailing whitespace from its desired width. A
        // visible marker makes those spaces interior; subtract only the marker.
        return line.Length > 0 && char.IsWhiteSpace(line[^1])
            ? Math.Max(width, measure(line + "|") - measure("|")) : width;
    }
}
