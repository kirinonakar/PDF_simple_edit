using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using iText.IO.Font.Cmap;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal sealed class PdfMcpPageDetails(PdfPage page, bool diagnostic)
{
    internal sealed record Bounds(double x, double y, double width, double height)
    {
        public static Bounds From(PdfTextBox box) => new(Math.Round(box.X, 3), Math.Round(box.Y, 3),
            Math.Round(box.Width, 3), Math.Round(box.Height, 3));
    }

    internal sealed record Span(string text, Bounds bounds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? font,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? pdfBytes);

    internal sealed record Mapping(string pdfCode, string rawText, string text, string[] codePoints,
        string[] privateUseCodePoints, string? toUnicode, string? repair);
    internal sealed record Font(int id, string? name, int? objectNumber, string? subtype, string? encoding,
        bool hasToUnicode, IReadOnlyCollection<Mapping> mappings);

    private sealed class FontDetails(PdfFont source, int id)
    {
        private readonly PdfDictionary _dictionary = source.GetPdfObject();
        private readonly Dictionary<string, Mapping> _mappings = new(StringComparer.Ordinal);
        private readonly CMapToUnicode? _toUnicode = FontUtil.ProcessToUnicode(source.GetPdfObject().Get(PdfName.ToUnicode));

        public int Id { get; } = id;
        public Font Describe() => new(Id, _dictionary.GetAsName(PdfName.BaseFont)?.GetValue(),
            _dictionary.GetIndirectReference()?.GetObjNumber(), _dictionary.GetAsName(PdfName.Subtype)?.GetValue(),
            _dictionary.GetAsName(PdfName.Encoding)?.GetValue() ?? (_dictionary.Get(PdfName.Encoding) switch
            {
                PdfStream => "embedded CMap",
                PdfDictionary encoding => encoding.GetAsName(PdfName.BaseEncoding)?.GetValue() ?? "custom",
                _ => null
            }), _dictionary.ContainsKey(PdfName.ToUnicode), _mappings.Values);

        public void ReadMapping(TextRenderInfo glyph, string? fontName)
        {
            byte[] bytes = glyph.GetPdfString().GetValueBytes();
            string code = Convert.ToHexString(bytes);
            if (_mappings.ContainsKey(code)) return;
            string raw = glyph.GetText();
            string text = PdfReadTextExtractionStrategy.Decode(raw, fontName);
            var unicode = _toUnicode?.Lookup(bytes);
            var codePoints = raw.EnumerateRunes().ToArray();
            _mappings.Add(code, new(code, raw, text, codePoints.Select(FormatCodePoint).ToArray(),
                codePoints.Where(IsPrivateUse).Select(FormatCodePoint).ToArray(),
                unicode == null ? null : new string(unicode), raw != text ? "HYhwpEQ" : null));
        }
    }

    private readonly PdfAffineTransform _toDisplay = PdfPageCoordinates.ToDisplay(page);
    private readonly List<Span> _spans = [];
    private readonly List<PdfMcpMath.Glyph> _math = [];
    private readonly List<PdfTextBox> _rules = [];
    private readonly Dictionary<PdfFont, FontDetails> _fonts = [];
    private readonly StringBuilder? _raw = diagnostic ? new() : null;

    public IReadOnlyList<Span> Spans => _spans;
    public string? RawText => _raw?.ToString();
    public IReadOnlyList<Font>? Fonts => diagnostic ? _fonts.Values.Select(font => font.Describe()).ToArray() : null;

    public void ReadText(TextRenderInfo info)
    {
        string raw = info.GetText();
        string? fontName = PdfReadTextExtractionStrategy.GetFontName(info);
        string text = PdfReadTextExtractionStrategy.Decode(raw, fontName);
        FontDetails? font = null;
        if (_raw != null)
        {
            _raw.Append(raw);
            var source = info.GetFont();
            if (!_fonts.TryGetValue(source, out font))
                _fonts.Add(source, font = new(source, _fonts.Count + 1));
        }

        if (text.Length > 0 || font != null)
        {
            string? bytes = font == null ? null : Convert.ToHexString(info.GetPdfString().GetValueBytes());
            _spans.Add(new(text, Bounds.From(TextBounds(info)), font?.Id, bytes));
        }

        bool mathFont = PdfMcpMath.IsMathFont(fontName);
        bool mathRun = mathFont || (PdfMcpMath.ContainsMathSymbol(text) &&
            text.EnumerateRunes().All(rune => rune.IsAscii || !Rune.IsLetter(rune) || rune.Value is >= 0x391 and <= 0x3C9) &&
            !text.Split(' ').Any(word => word.Length > 3 && word.All(char.IsAsciiLetter)));
        if (font == null && !mathRun) return;
        bool namedOperator = mathRun && PdfMcpMath.IsNamedOperator(text);
        bool hwpEquation = HwpEquationText.IsFont(fontName);
        if (namedOperator) AddMathGlyph(info, text, hwpEquation);
        if (namedOperator && font == null) return;
        foreach (var glyph in info.GetCharacterRenderInfos())
        {
            font?.ReadMapping(glyph, fontName);
            if (!mathRun || namedOperator) continue;
            string rawGlyph = glyph.GetText();
            string symbol = PdfReadTextExtractionStrategy.Decode(rawGlyph, fontName);
            if (!string.IsNullOrWhiteSpace(symbol))
                AddMathGlyph(glyph, symbol, hwpEquation, hwpEquation && rawGlyph == symbol && symbol.All(char.IsAsciiLetter));
        }
    }

    private void AddMathGlyph(TextRenderInfo glyph, string symbol, bool hwpEquation, bool roman = false)
    {
        var baseline = glyph.GetBaseline();
        PdfTextPoint origin = Map(baseline.GetStartPoint()), end = Map(baseline.GetEndPoint());
        if (end.X < origin.X || Math.Abs(end.Y - origin.Y) > Math.Max(.1, (end.X - origin.X) * .02)) return;

        var state = glyph.GetGraphicsState();
        var matrix = glyph.GetTextMatrix().Multiply(state.GetCtm());
        var vertical = new Vector(0, state.GetFontSize(), 0).Cross(matrix);
        var em = Math.Sqrt(vertical.Get(0) * vertical.Get(0) + vertical.Get(1) * vertical.Get(1));
        if (!double.IsFinite(em) || em <= 0) return;
        double? ruleY = null;
        if (hwpEquation && symbol == "―")
        {
            var glyphRun = glyph.GetFont().DecodeIntoGlyphLine(glyph.GetPdfString());
            int[]? box = glyphRun.Size() > 0 ? glyphRun.Get(0).GetBbox() : null;
            ruleY = box is { Length: 4 } && box[3] > box[1]
                ? origin.Y + (_toDisplay.B * vertical.Get(0) + _toDisplay.D * vertical.Get(1)) * (box[1] + box[3]) / 2000
                : origin.Y - em * .25;
        }
        _math.Add(new(symbol, TextBounds(glyph), origin.X, end.X, origin.Y, em, ruleY, roman));
    }

    public void ReadPath(PathRenderInfo path)
    {
        foreach (var box in PdfMcpColumnReader.GetRuleBounds(path, _toDisplay))
            ReadRule(box);
    }

    public void ReadRule(PdfTextBox box)
    {
        if (box.Width >= 2 && box.Height <= Math.Min(2, box.Width / 5))
            _rules.Add(box);
    }

    public IReadOnlyList<PdfMcpMath.Equation> GetEquations(CancellationToken cancellation) =>
        PdfMcpMath.Read(_math, _rules, cancellation);

    private PdfTextBox TextBounds(TextRenderInfo info)
    {
        var ascent = info.GetAscentLine();
        var descent = info.GetDescentLine();
        var a = Map(ascent.GetStartPoint());
        var b = Map(ascent.GetEndPoint());
        var c = Map(descent.GetStartPoint());
        var d = Map(descent.GetEndPoint());
        double x = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        double y = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        return new(x, y, Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) - x,
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) - y);
    }

    private PdfTextPoint Map(Vector point) => _toDisplay.Map(point.Get(0), point.Get(1));
    private static string FormatCodePoint(Rune rune) => $"U+{rune.Value:X4}";
    internal static bool IsPrivateUse(Rune rune) => Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.PrivateUse;
}
