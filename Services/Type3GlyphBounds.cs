using iText.IO.Source;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>Type 3 glyph bounds in text space, including the original FontMatrix.</summary>
internal sealed class Type3GlyphBounds(PdfType3Font font)
{
    private readonly Dictionary<int, (double X, double Y)[]?> _cache = new();

    public (double X, double Y)[]? GetCorners(byte[] code)
    {
        if (code.Length != 1) return null;
        if (_cache.TryGetValue(code[0], out var cached)) return cached;
        (double X, double Y)[]? points = null;
        try
        {
            points = Read(code[0]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed/unsupported glyphs retain the caller's existing metrics.
            System.Diagnostics.Debug.WriteLine($"Type 3 glyph bounds unavailable: {ex.Message}");
        }
        _cache[code[0]] = points;
        return points;
    }

    private (double X, double Y)[]? Read(int code)
    {
        var dictionary = font.GetPdfObject();
        // iText normalizes Type 3 font metrics to 1000 units, but CharProcs keep
        // their original coordinates. Use the PDF's raw matrix, including its
        // Y sign, scale, shear and translation, rather than the normalized API.
        var matrix = dictionary.GetAsArray(PdfName.FontMatrix);
        if (matrix == null || matrix.Size() != 6) return null;
        var m = Enumerable.Range(0, 6).Select(i => matrix.GetAsNumber(i)?.DoubleValue() ?? double.NaN).ToArray();
        if (m.Any(v => !double.IsFinite(v))) return null;
        string? name = font.GetFontEncoding().GetDifference(code);
        if (name == null) return null;
        var stream = dictionary.GetAsDictionary(PdfName.CharProcs)?.GetAsStream(new PdfName(name));
        if (stream == null) return null;
        var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(
            new RandomAccessSourceFactory().CreateSource(stream.GetBytes())));
        IList<PdfObject> operands;
        try
        {
            operands = new PdfCanvasParser(tokenizer).Parse(new List<PdfObject>());
        }
        finally { tokenizer.Close(); }
        double[] box;
        if (operands.Count == 7 && operands[6].ToString() == "d1" && operands.Take(6).All(o => o is PdfNumber))
        {
            box = operands.Skip(2).Take(4).Cast<PdfNumber>().Select(n => n.DoubleValue()).ToArray();
        }
        else if (operands.Count == 3 && operands[2].ToString() == "d0")
        {
            // Colorized glyphs do not declare a per-glyph box. The shared font
            // box is conservative, but must use the same signed FontMatrix.
            var fontBox = dictionary.GetAsArray(PdfName.FontBBox);
            if (fontBox == null || fontBox.Size() != 4) return null;
            box = Enumerable.Range(0, 4).Select(i => fontBox.GetAsNumber(i)?.DoubleValue() ?? double.NaN).ToArray();
        }
        else return null;
        if (box.Any(v => !double.IsFinite(v)) || box[0] == box[2] || box[1] == box[3]) return null;
        var points = new List<(double X, double Y)>();
        foreach (double x in new[] { box[0], box[2] })
        foreach (double y in new[] { box[1], box[3] })
            points.Add((m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]));
        return points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)) ? points.ToArray() : null;
    }
}
