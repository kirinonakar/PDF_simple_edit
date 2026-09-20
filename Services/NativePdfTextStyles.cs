using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

public sealed partial class NativePdfTextService
{
    private static byte[] ColorCommands(PdfDocument doc, PdfResources resources, string? hex, Color fill, Color stroke)
    {
        var stream = new PdfStream();
        var canvas = new PdfCanvas(stream, resources, doc);
        // Force explicit restoration even when the saved color is the canvas's
        // default black. No q/Q: renderers disagree about its text-cursor scope.
        canvas.SetFillColorRgb(1, 0, 1).SetStrokeColorRgb(1, 0, 1);
        if (hex != null)
        {
            int rgb = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            fill = stroke = new DeviceRgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }
        canvas.SetFillColor(fill).SetStrokeColor(stroke);
        return stream.GetBytes();
    }

    private static NativePdfTextBlock ApplyStyle(PdfDocument doc, PageData data, NativePdfTextBlock block, NativePdfTextStyle style)
    {
        if (style.FontSize is double size && (!double.IsFinite(size) || size <= 0))
            throw new ArgumentOutOfRangeException(nameof(style), "글자 크기는 0보다 큰 숫자여야 합니다.");
        if (style.Color is string color && (color.Length != 7 || color[0] != '#' ||
            !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException("올바른 RGB 글자색을 선택해 주세요.", nameof(style));

        var runs = block.Runs.ToDictionary(r => r.Id);
        var fonts = new Dictionary<string, PdfFont?>();
        var candidates = new Dictionary<string, IReadOnlyList<string>>();
        (PdfFont Font, bool Italic) ResolveFont(NativePdfRun run, string text)
        {
            bool bold = style.IsBold ?? run.IsBold, italic = style.IsItalic ?? run.IsItalic;
            string family = style.FontFamily ?? run.FontName;
            // PDF's standard faces have metrically compatible Windows families.
            // Prefer those over an arbitrary installed sans-serif/monospace face.
            family = family.ToLowerInvariant() switch
            {
                "helvetica" or "helvetica-bold" or "helvetica-oblique" or "helvetica-boldoblique" => "Arial",
                "times-roman" or "times-bold" or "times-italic" or "times-bolditalic" => "Times New Roman",
                "courier" or "courier-bold" or "courier-oblique" or "courier-boldoblique" => "Courier New",
                _ => family
            };
            if (!candidates.TryGetValue(run.Id, out var paths))
            {
                string? exact = InstalledFontService.FindFontFile(family, bold ? 700 : 400, italic);
                candidates[run.Id] = paths = (exact == null ? Array.Empty<string>() : new[] { exact })
                    .Concat(InstalledFontService.GetSimilarFontFiles(family, bold ? 700 : 400, italic))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            // Some CJK families have no italic face. Use their upright outline
            // with a PDF shear after trying faces with the requested slant.
            foreach (bool synthetic in italic ? new[] { false, true } : new[] { false })
            foreach (string path in paths)
            {
                string key = path + ":" + synthetic;
                if (!fonts.TryGetValue(key, out var font))
                {
                    var descriptor = FontProgramDescriptorFactory.FetchDescriptor(path);
                    if (descriptor == null || descriptor.IsItalic() != (italic && !synthetic) || (descriptor.GetFontWeight() >= 600) != bold)
                    { fonts[key] = null; continue; }
                    try { font = PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED); }
                    catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
                    fonts[key] = font;
                }
                if (font == null || !text.EnumerateRunes().All(r => font.ContainsGlyph(r.Value))) continue;
                font.GetPdfObject().MakeIndirect(doc); doc.AddFont(font);
                return (font, italic && !synthetic);
            }
            throw new InvalidOperationException($"'{text}'에 요청한 굵기·기울임을 지원하는 글꼴이 설치되어 있지 않습니다. 다른 글꼴을 선택해 주세요.");
        }

        bool metricsChanged = false;
        var tokens = new List<NativePdfGlyph>();
        foreach (var glyph in block.Glyphs)
        {
            var run = runs[glyph.RunId];
            double newSize = style.FontSize is double displaySize ? run.FontSize * displaySize / run.DisplayFontSize : run.FontSize;
            double ratio = newSize / run.FontSize;
            bool replaceFace = style.FontFamily != null || style.IsBold is bool bold && bold != run.IsBold ||
                style.IsItalic is bool italic && italic != run.IsItalic;
            bool resize = Math.Abs(ratio - 1) > .000001;
            metricsChanged |= resize || replaceFace;
            var token = glyph with { StyleColor = style.Color ?? glyph.StyleColor,
                StyleFontSize = resize ? newSize : glyph.StyleFontSize };
            if (replaceFace && !glyph.IsVirtual)
            {
                var face = ResolveFont(run, glyph.Text);
                var font = face.Font;
                byte[] code = font.ConvertToBytes(glyph.Text);
                int glyphCount = font.DecodeIntoGlyphLine(new PdfString(code)).Size();
                double advance = (font.GetWidth(glyph.Text) * newSize / 1000 + run.CharacterSpacing * glyphCount +
                    (code.Length == 1 && code[0] == 32 ? run.WordSpacing : 0)) * run.HorizontalScale;
                token = token with { Code = code, Advance = advance,
                    ReplacementFontObjectNumber = font.GetPdfObject().GetIndirectReference().GetObjNumber(),
                    ReplacementFontName = font.GetFontProgram().GetFontNames().GetFontName(),
                    StyleSlantDelta = ((style.IsItalic ?? run.IsItalic) && !face.Italic ? .21255656 : 0) - run.Slant };
            }
            else if (resize)
            {
                double spacing = (run.CharacterSpacing + (glyph.Code.Length == 1 && glyph.Code[0] == 32 ? run.WordSpacing : 0)) * run.HorizontalScale;
                token = token with { Advance = (glyph.Advance - spacing) * ratio + spacing };
            }
            if (resize || replaceFace)
                token = token with { End = new(glyph.Origin.X + (glyph.End.X - glyph.Origin.X) * ratio, glyph.End.Y),
                    Bounds = new(glyph.Origin.X + (glyph.Bounds.X - glyph.Origin.X) * ratio,
                        glyph.Origin.Y + (glyph.Bounds.Y - glyph.Origin.Y) * ratio, glyph.Bounds.Width * ratio, glyph.Bounds.Height * ratio) };
            if (glyph.IsSoftBreak && (resize || replaceFace))
            {
                var font = replaceFace ? ResolveFont(run, " ").Font : PdfFontFactory.CreateFont((PdfDictionary)doc.GetPdfObject(run.FontObjectNumber));
                double space = Math.Max(font.GetWidth(' ') * newSize / 1000, newSize * .2);
                double width = DisplayAdvance(run, (space + run.CharacterSpacing + run.WordSpacing) * run.HorizontalScale, data.Map);
                token = token with { End = new(glyph.Origin.X + width, glyph.Origin.Y) };
            }
            tokens.Add(token);
        }
        if (!metricsChanged) return block with { Glyphs = tokens };
        if (block.Glyphs.Any(g => !g.IsVirtual && (Math.Abs(g.End.Y - g.Origin.Y) > .1 || g.End.X < g.Origin.X)))
            throw new InvalidOperationException("회전되거나 오른쪽에서 왼쪽으로 배치된 텍스트는 현재 글자색만 변경할 수 있습니다.");
        double Width(NativePdfGlyph g) => g.Text == "\n" ? 0 : g.IsVirtual
            ? Math.Max(g.End.X - g.Origin.X, 0) : DisplayAdvance(runs[g.RunId], g.Advance, data.Map);
        var tokenIndexes = tokens.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i);
        // Retain deliberate character spacing, but don't use the old face's
        // advances to squeeze a larger or heavier replacement into the old width.
        double Kern(NativePdfGlyph a, NativePdfGlyph b)
        {
            int index = tokenIndexes[b];
            if (index <= 0) return 0;
            var before = block.Glyphs[index - 1]; var after = block.Glyphs[index];
            if (before.IsVirtual || after.IsVirtual || Math.Abs(before.Origin.Y - after.Origin.Y) > .1) return 0;
            double gap = after.Origin.X - before.End.X;
            return Math.Abs(gap) < runs[b.RunId].DisplayFontSize * .5 ? gap : 0;
        }
        var layoutBlock = block with { Runs = block.Runs.Select(r => r with
            { DisplayFontSize = style.FontSize ?? r.DisplayFontSize }).ToList(),
            LineHeight = Math.Max(block.LineHeight, style.FontSize.GetValueOrDefault() * 1.2) };
        return NativePdfParagraphLayout.Place(layoutBlock, block.Text, tokens, Width, Kern,
            g => g.IsVirtual ? 0 : DisplayAdvance(runs[g.RunId], runs[g.RunId].CharacterSpacing * runs[g.RunId].HorizontalScale, data.Map),
            0, 0, preservePlacement: false, minimumLeading: style.FontSize.GetValueOrDefault() * 1.2);
    }

    // Rewriting a Tj/TJ changes operation IDs. Rebind by the glyphs that were
    // actually edited, never by the containing paragraph or its bounding box.
    public NativePdfTextBlock? RefreshSelection(byte[] bytes, int pageIndex, NativePdfTextBlock layout,
        int start = 0, int length = int.MaxValue)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        var data = Read(doc.GetPage(pageIndex + 1));
        var wanted = layout.Glyphs.Where(g => !g.IsVirtual && g.TextIndex < (long)start + length &&
            g.TextIndex + g.Text.Length > start).ToList();
        var slices = data.Runs.Select(run => run with { Glyphs = run.Glyphs.Where(g => wanted.Any(w =>
            w.Text == g.Text && Math.Abs(w.Origin.X - g.Origin.X) < .01 && Math.Abs(w.Origin.Y - g.Origin.Y) < .01)).ToList() })
            .Where(r => r.Glyphs.Count > 0).OrderBy(r => Math.Round(r.Glyphs[0].Origin.Y, 1)).ThenBy(r => r.Glyphs[0].Origin.X).ToList();
        if (slices.Count == 0) return null;
        NativePdfTextBlock selected;
        if (layout.Glyphs.Any(g => g.ShapeTransform.HasValue))
        {
            // Geometric edits keep logical reading order even when rotation puts
            // each glyph at a different visual baseline (or reverses screen X).
            var available = slices.SelectMany(r => r.Glyphs).ToList();
            var ordered = layout.Glyphs.Select(w => w.IsVirtual ? w : available.First(g =>
                w.Text == g.Text && Math.Abs(w.Origin.X - g.Origin.X) < .01 &&
                Math.Abs(w.Origin.Y - g.Origin.Y) < .01) with { TextIndex = w.TextIndex }).ToList();
            selected = layout with
            {
                Glyphs = ordered,
                Runs = ordered.Where(g => !g.IsVirtual).Select(g => data.Runs.Single(r => r.Id == g.RunId)).DistinctBy(r => r.Id).ToList()
            };
        }
        else selected = BuildBlock(slices) with { Runs = slices.Select(s => data.Runs.Single(r => r.Id == s.Id)).ToList() };
        return ApplyInkBounds(bytes, pageIndex, new() { selected }, data.Map)[0];
    }

    public static void UpdateAnnotation(PdfAnnotation annotation, NativePdfTextBlock block)
    {
        var content = ToPageContent(block);
        annotation.NativeText = block; annotation.Content = block.Text;
        annotation.X = content.X; annotation.Y = content.Y; annotation.Width = content.Width; annotation.Height = content.Height;
        annotation.FontFamily = content.FontFamily; annotation.FontSize = content.FontSize;
        annotation.IsBold = content.IsBold; annotation.IsItalic = content.IsItalic; annotation.FontWeight = content.FontWeight;
        annotation.Color = content.Color; annotation.LineHeight = content.LineHeight;
        annotation.BaselineOffset = content.BaselineOffset; annotation.OriginalFontObjectNumber = content.OriginalFontObjectNumber;
        annotation.IsOriginalTextReplacement = true; annotation.IsApplied = false;
    }
}
