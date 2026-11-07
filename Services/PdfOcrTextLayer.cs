using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services;

public sealed record PdfOcrWord(string Text, double X, double Y, double Width, double Height);
public sealed record PdfOcrPage(int PixelWidth, int PixelHeight, double TextAngle,
    IReadOnlyList<IReadOnlyList<PdfOcrWord>> Lines);

/// <summary>An isolated, replaceable PDF text layer, with embedded Unicode fonts.</summary>
internal sealed class PdfOcrTextLayer
{
    private static readonly PdfName Marker = new("PDFSimpleEditOcr");
    private readonly Dictionary<string, PdfFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, PdfFont> _characters = new();
    private IReadOnlyList<string>? _fontPaths;

    public static bool Remove(PdfPage page)
    {
        bool removed = false;
        var contents = page.GetPdfObject().Get(PdfName.Contents);
        if (contents is PdfStream stream && stream.GetAsBoolean(Marker)?.GetValue() == true)
        {
            page.GetPdfObject().Remove(PdfName.Contents);
            removed = true;
        }
        else if (contents is PdfArray streams)
        {
            var kept = new PdfArray();
            for (int i = 0; i < streams.Size(); i++)
            {
                if (streams.GetAsStream(i)?.GetAsBoolean(Marker)?.GetValue() == true) removed = true;
                else kept.Add(streams.Get(i, false));
            }
            if (removed) page.GetPdfObject().Put(PdfName.Contents, kept);
        }
        if (!removed) return false;

        // Resource dictionaries can be inherited or shared by several pages.
        var resources = new PdfDictionary(page.GetResources().GetPdfObject());
        if (resources.GetAsDictionary(PdfName.XObject) is { } objects)
        {
            var kept = new PdfDictionary(objects);
            foreach (var name in objects.KeySet())
                if (objects.GetAsStream(name)?.GetAsBoolean(Marker)?.GetValue() == true) kept.Remove(name);
            resources.Put(PdfName.XObject, kept);
        }
        page.SetResources(new PdfResources(resources));
        return true;
    }

    internal static Rectangle VisibleBox(PdfPage page)
    {
        var crop = page.GetCropBox();
        var media = page.GetMediaBox();
        float left = Math.Max(crop.GetLeft(), media.GetLeft()), bottom = Math.Max(crop.GetBottom(), media.GetBottom());
        return new(left, bottom, Math.Min(crop.GetRight(), media.GetRight()) - left,
            Math.Min(crop.GetTop(), media.GetTop()) - bottom);
    }

    internal static PdfAffineTransform PixelToPdf(PdfPage page, PdfOcrPage result)
    {
        var crop = VisibleBox(page);
        double left = crop.GetLeft(), bottom = crop.GetBottom(), width = crop.GetWidth(), height = crop.GetHeight();
        int rotation = ((page.GetRotation() % 360) + 360) % 360;
        var displayToPdf = (rotation switch
        {
            90 => new PdfAffineTransform(0, 1, 1, 0, -bottom, -left),
            180 => new(-1, 0, 0, 1, width + left, -bottom),
            270 => new(0, -1, -1, 0, height + bottom, width + left),
            _ => new(1, 0, 0, -1, -left, height + bottom)
        }).Inverse();
        if (rotation % 180 != 0) (width, height) = (height, width);
        var scale = new PdfAffineTransform(width / result.PixelWidth, 0, 0, height / result.PixelHeight, 0, 0);
        // Windows reports word boxes in the deskewed image. Rotate them about
        // the image center to put them back over the original raster.
        double radians = result.TextAngle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double cx = result.PixelWidth / 2.0, cy = result.PixelHeight / 2.0;
        var deskew = new PdfAffineTransform(cos, sin, -sin, cos,
            cx - cos * cx + sin * cy, cy - sin * cx - cos * cy);
        return displayToPdf.After(scale).After(deskew);
    }

    public int Add(PdfDocument document, PdfPage page, PdfOcrPage result)
    {
        var words = result.Lines.SelectMany(line => line).Where(w =>
            !string.IsNullOrWhiteSpace(w.Text) && w.Width > 0 && w.Height > 0).ToList();
        if (words.Count == 0) return 0;
        var form = new PdfFormXObject(VisibleBox(page));
        form.GetPdfObject().Put(Marker, PdfBoolean.TRUE);
        var canvas = new PdfCanvas(form, document);
        var map = PixelToPdf(page, result);
        canvas.BeginText().SetTextRenderingMode(PdfCanvasConstants.TextRenderingMode.INVISIBLE);
        int count = 0;
        foreach (var line in result.Lines)
        {
            var validWords = JoinCjkWords(line).ToArray();
            var layouts = new List<(PdfOcrWord Word, List<(PdfFont Font, string Text)> Runs, double Sx, double Sy, double Baseline)>();
            foreach (var word in validWords)
            {
                var runs = new List<(PdfFont Font, string Text)>();
                foreach (var rune in word.Text.EnumerateRunes())
                {
                    var font = FontFor(rune.Value);
                    if (runs.Count > 0 && ReferenceEquals(runs[^1].Font, font))
                        runs[^1] = (font, runs[^1].Text + rune);
                    else runs.Add((font, rune.ToString()));
                }
                double advance = runs.Sum(r => r.Font.GetWidth(r.Text, 1));
                double ascent = runs.Max(r => r.Font.GetAscent(r.Text, 1));
                double descent = runs.Min(r => r.Font.GetDescent(r.Text, 1));
                double sx = word.Width / Math.Max(.001, advance), sy = word.Height / Math.Max(.001, ascent - descent);
                layouts.Add((word, runs, sx, sy, word.Y + ascent * sy));
            }
            // Keep one baseline per OCR line. Per-word font metrics otherwise
            // split mixed Latin/CJK lines into different search/copy lines.
            double baseline = layouts.Count == 0 ? 0 : layouts.OrderBy(l => l.Baseline).ElementAt(layouts.Count / 2).Baseline;
            for (int wordIndex = 0; wordIndex < layouts.Count; wordIndex++)
            {
                var (word, runs, sx, sy, _) = layouts[wordIndex];
                double x = word.X;
                for (int runIndex = 0; runIndex < runs.Count; runIndex++)
                {
                    var run = runs[runIndex];
                    // Each word keeps its measured box; explicit spaces preserve
                    // phrase search/copy even for narrow OCR word gaps.
                    var origin = map.Map(x, baseline);
                    canvas.SetFontAndSize(run.Font, 1).SetTextMatrix(
                        (float)(map.A * sx), (float)(map.B * sx), (float)(-map.C * sy), (float)(-map.D * sy),
                        (float)origin.X, (float)origin.Y).ShowText(run.Text +
                            (wordIndex + 1 < validWords.Length && runIndex + 1 == runs.Count ? " " : ""));
                    x += run.Font.GetWidth(run.Text, 1) * sx;
                }
                count++;
            }
        }
        canvas.EndText();
        // Clone before adding the form to avoid changing other pages' resources.
        var resources = new PdfDictionary(page.GetResources().GetPdfObject());
        if (resources.GetAsDictionary(PdfName.XObject) is { } objects)
            resources.Put(PdfName.XObject, new PdfDictionary(objects));
        page.SetResources(new PdfResources(resources));
        // Restore the page's initial graphics state before drawing the layer;
        // original content is allowed to finish with a non-identity CTM.
        var before = page.NewContentStreamBefore();
        before.Put(Marker, PdfBoolean.TRUE);
        before.SetData(Encoding.ASCII.GetBytes("q\n"));
        var after = page.NewContentStreamAfter();
        after.Put(Marker, PdfBoolean.TRUE);
        after.SetData(Encoding.ASCII.GetBytes("\nQ\n"));
        var content = page.NewContentStreamAfter();
        content.Put(Marker, PdfBoolean.TRUE);
        new PdfCanvas(content, page.GetResources(), document).SaveState()
            .AddXObjectWithTransformationMatrix(form, 1, 0, 0, 1, 0, 0).RestoreState();
        return count;
    }

    private static IEnumerable<PdfOcrWord> JoinCjkWords(IReadOnlyList<PdfOcrWord> line)
    {
        static bool Unspaced(int c) => c is >= 0x3040 and <= 0x30ff or >= 0x3400 and <= 0x9fff
            or >= 0xf900 and <= 0xfaff or >= 0xff65 and <= 0xff9f or >= 0x20000 and <= 0x323af;
        PdfOcrWord? previous = null;
        foreach (var word in line.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Width > 0 && w.Height > 0))
        {
            // Windows can return one "word" per Japanese/Chinese character.
            // Join adjacent characters before PDF placement so text extraction
            // does not invent spaces between every ideograph or kana.
            if (previous != null && Unspaced(previous.Text.EnumerateRunes().Last().Value) &&
                Unspaced(Rune.GetRuneAt(word.Text, 0).Value) && word.X >= previous.X &&
                word.X - previous.X - previous.Width < Math.Max(previous.Height, word.Height) * .5 &&
                Math.Abs(word.Y - previous.Y) < Math.Max(previous.Height, word.Height) * .5)
            {
                double top = Math.Min(previous.Y, word.Y), right = Math.Max(previous.X + previous.Width, word.X + word.Width);
                previous = previous with { Text = previous.Text + word.Text, Y = top, Width = right - previous.X,
                    Height = Math.Max(previous.Y + previous.Height, word.Y + word.Height) - top };
            }
            else
            {
                if (previous != null) yield return previous;
                previous = word;
            }
        }
        if (previous != null) yield return previous;
    }

    private PdfFont FontFor(int character)
    {
        if (_characters.TryGetValue(character, out var found)) return found;
        foreach (var font in _fonts.Values)
            if (font?.ContainsGlyph(character) == true) return _characters[character] = font;
        _fontPaths ??= InstalledFontService.GetSimilarFontFiles("Malgun Gothic", 400, false);
        foreach (string path in _fontPaths)
        {
            if (!_fonts.TryGetValue(path, out var font))
            {
                try { font = PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED); }
                catch (Exception) { font = null; } // Other installed faces can cover this character.
                _fonts[path] = font;
            }
            if (font?.ContainsGlyph(character) == true) return _characters[character] = font;
        }
        throw new InvalidOperationException(LocalizationService.Format("OcrMissingFont", char.ConvertFromUtf32(character)));
    }
}
