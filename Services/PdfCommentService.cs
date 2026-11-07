using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;
using iText.Kernel.Font;
using iText.IO.Font;
using iText.IO.Font.Constants;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EditorAnnotation = PDF_simple_edit.Models.PdfAnnotation;
using NativeAnnotation = iText.Kernel.Pdf.Annot.PdfAnnotation;

namespace PDF_simple_edit.Services;

/// <summary>PDF markup annotations, kept separate from editable page text and graphics.</summary>
public static class PdfCommentService
{
    public static bool IsComment(NativeAnnotation annotation) => annotation is PdfMarkupAnnotation;

    // Semantic keys survive PDF rewrites that renumber indirect objects. The occurrence
    // suffix distinguishes identical annotations without relying on the /Annots index.
    private static IEnumerable<(NativeAnnotation Annotation, string Key)> Enumerate(PdfPage page)
    {
        var counts = new Dictionary<string, int>();
        foreach (var annotation in page.GetAnnotations().Where(IsComment))
        {
            var dictionary = annotation.GetPdfObject();
            var identity = new StringBuilder();
            var names = !string.IsNullOrEmpty(dictionary.GetAsString(PdfName.NM)?.ToUnicodeString())
                ? new[] { PdfName.Subtype, PdfName.NM } : new[] { PdfName.Subtype, PdfName.Rect, PdfName.QuadPoints,
                PdfName.Contents, PdfName.T, PdfName.CreationDate, PdfName.InkList, PdfName.Vertices, PdfName.L };
            foreach (var name in names)
            {
                string value = dictionary.Get(name)?.ToString() ?? "";
                identity.Append(value.Length).Append(':').Append(value);
            }
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
            counts.TryGetValue(hash, out int occurrence);
            counts[hash] = occurrence + 1;
            yield return (annotation, hash + ":" + occurrence);
        }
    }

    public static List<EditorAnnotation> Read(byte[] bytes, int pageIndex)
    {
        using var document = new PdfDocument(new PdfReader(new MemoryStream(bytes, writable: false)));
        return ReadPage(document, pageIndex);
    }

    public static List<EditorAnnotation> ReadAll(byte[] bytes)
    {
        using var document = new PdfDocument(new PdfReader(new MemoryStream(bytes, writable: false)));
        return Enumerable.Range(0, document.GetNumberOfPages()).SelectMany(index => ReadPage(document, index)).ToList();
    }

    private static List<EditorAnnotation> ReadPage(PdfDocument document, int pageIndex)
    {
        var page = document.GetPage(pageIndex + 1);
        var display = PdfPageCoordinates.ToDisplay(page);
        var result = new List<EditorAnnotation>();
        foreach (var (annotation, key) in Enumerate(page))
        {
            if (annotation.HasFlag(NativeAnnotation.HIDDEN) || annotation.HasFlag(NativeAnnotation.INVISIBLE) ||
                annotation.HasFlag(NativeAnnotation.NO_VIEW)) continue;
            var rectangle = annotation.GetRectangle();
            if (rectangle == null || rectangle.Size() != 4 || Enumerable.Range(0, 4).Any(i => rectangle.GetAsNumber(i) == null)) continue;
            var rect = rectangle.ToRectangle();
            if (rect == null) continue;
            var bounds = display.Map(new PdfTextBox(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
            var hitBounds = new List<PdfTextBox>();
            var quads = annotation.GetPdfObject().GetAsArray(PdfName.QuadPoints);
            if (quads != null && quads.Size() % 8 == 0 && Enumerable.Range(0, quads.Size()).All(i => quads.GetAsNumber(i) != null))
                for (int i = 0; i + 7 < quads.Size(); i += 8)
                {
                    var points = Enumerable.Range(0, 4).Select(n => display.Map(
                        quads.GetAsNumber(i + n * 2).DoubleValue(), quads.GetAsNumber(i + n * 2 + 1).DoubleValue())).ToList();
                    hitBounds.Add(new(points.Min(p => p.X), points.Min(p => p.Y),
                        points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y)));
                }
            result.Add(new EditorAnnotation
            {
                Id = "PdfComment:" + pageIndex + ":" + key, PdfCommentKey = key, Type = AnnotationType.PdfComment,
                PageIndex = pageIndex, X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
                Content = annotation.GetContents()?.ToUnicodeString() ?? "", IsApplied = true,
                PdfCommentSubtype = annotation.GetSubtype().GetValue(),
                Color = CommentColor(annotation),
                FontSize = annotation.GetPdfObject().GetAsNumber(new PdfName("PDFSimpleFontSize"))?.DoubleValue() ?? 12,
                Opacity = (annotation as PdfMarkupAnnotation)?.GetOpacity()?.DoubleValue() ?? 1,
                IsCommentOpen = annotation.GetPdfObject().GetAsBoolean(PdfName.Open)?.GetValue() == true ||
                    (annotation as PdfMarkupAnnotation)?.GetPopup()?.GetPdfObject().GetAsBoolean(PdfName.Open)?.GetValue() == true,
                PdfCommentHitBounds = hitBounds.Count > 0 ? hitBounds : new() { bounds }
            });
        }
        return result;
    }

    public static void SetNoteOpen(PdfDocument document, EditorAnnotation target, bool open)
    {
        var annotation = Enumerate(document.GetPage(target.PageIndex + 1))
            .FirstOrDefault(a => a.Key == target.PdfCommentKey).Annotation as PdfTextAnnotation;
        if (annotation == null) throw new InvalidOperationException(LocalizationService.Get("TheSelectedAnnotationChangedSelectItAgain"));
        annotation.SetOpen(open);
        annotation.GetPopup()?.SetOpen(open);
    }

    private static string CommentColor(NativeAnnotation annotation)
    {
        var color = annotation.GetPdfObject().GetAsArray(new PdfName("PDFSimpleBorderColor")) ?? annotation.GetPdfObject().GetAsArray(PdfName.C);
        return color?.Size() == 3 ? "#" + string.Concat(Enumerable.Range(0, 3).Select(i => Math.Clamp((int)Math.Round((color.GetAsNumber(i)?.DoubleValue() ?? 0) * 255), 0, 255).ToString("X2"))) : "#FF0000";
    }

    public static void UpdateNoteOrTextBox(PdfDocument document, EditorAnnotation target, PdfTextBox bounds, string? contents = null)
    {
        var page = document.GetPage(target.PageIndex + 1);
        var annotation = Enumerate(page).FirstOrDefault(a => a.Key == target.PdfCommentKey).Annotation;
        if (annotation == null || !(PdfName.Text.Equals(annotation.GetSubtype()) || PdfName.FreeText.Equals(annotation.GetSubtype())))
            throw new InvalidOperationException(LocalizationService.Get("TheSelectedAnnotationChangedSelectItAgain"));
        string text = contents ?? annotation.GetContents()?.ToUnicodeString() ?? "";
        if (PdfName.FreeText.Equals(annotation.GetSubtype()))
        {
            var replacement = AddNoteOrTextBox(document, target.PageIndex, bounds, false, text,
                PdfAnnotationDocumentService.ParseCommentColor(target.Color), (float)target.Opacity, target.FontSize);
            foreach (var name in new[] { PdfName.Rect, PdfName.Contents, PdfName.AP, PdfName.DA, new PdfName("PDFSimpleBorderColor"), new PdfName("PDFSimpleFontSize") })
                annotation.GetPdfObject().Put(name, replacement.GetPdfObject().Get(name));
            annotation.GetPdfObject().Remove(PdfName.C);
            page.RemoveAnnotation(replacement);
        }
        else
        {
            var mapped = PdfPageCoordinates.ToDisplay(page).Inverse().Map(bounds);
            var previous = annotation.GetRectangle().ToRectangle();
            var popup = (annotation as PdfMarkupAnnotation)?.GetPopup();
            if (popup?.GetRectangle()?.ToRectangle() is { } popupRectangle)
            {
                float dx = (float)mapped.X - previous.GetX(), dy = (float)mapped.Y - previous.GetY();
                popup.SetRectangle(new PdfArray(new float[] { popupRectangle.GetLeft() + dx, popupRectangle.GetBottom() + dy,
                    popupRectangle.GetRight() + dx, popupRectangle.GetTop() + dy }));
            }
            annotation.SetRectangle(new PdfArray(new float[] { (float)mapped.X, (float)mapped.Y, (float)mapped.Right, (float)mapped.Bottom }));
            annotation.SetContents(new PdfString(text, PdfEncodings.UNICODE_BIG));
        }
        annotation.SetDate(new PdfDate().GetPdfObject());
    }

    public static PdfMarkupAnnotation AddNoteOrTextBox(PdfDocument document, int pageIndex, PdfTextBox box, bool sticky,
        string contents, Color color, float opacity, double fontSize)
    {
        var page = document.GetPage(pageIndex + 1);
        var toPdf = PdfPageCoordinates.ToDisplay(page).Inverse();
        var bounds = toPdf.Map(box);
        var rectangle = new Rectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
        PdfMarkupAnnotation annotation;
        if (sticky)
        {
            var note = new PdfTextAnnotation(rectangle);
            note.SetIconName(new PdfName("Comment"));
            note.SetOpen(false);
            annotation = note;
            var size = PdfPageCoordinates.ToDisplay(page).Map(new PdfTextBox(page.GetCropBox().GetX(),
                page.GetCropBox().GetY(), page.GetCropBox().GetWidth(), page.GetCropBox().GetHeight()));
            var popupBox = toPdf.Map(new PdfTextBox(Math.Max(0, Math.Min(box.Right + 4, size.Width - 220)),
                Math.Max(0, Math.Min(box.Y, size.Height - 130)), Math.Min(220, size.Width), Math.Min(130, size.Height)));
            var popup = new PdfPopupAnnotation(new Rectangle((float)popupBox.X, (float)popupBox.Y, (float)popupBox.Width, (float)popupBox.Height));
            popup.SetOpen(false);
            note.SetPopup(popup);
            page.AddAnnotation(popup);
        }
        else
        {
            annotation = new PdfFreeTextAnnotation(rectangle, new PdfString(contents, PdfEncodings.UNICODE_BIG));
            annotation.GetPdfObject().Put(PdfName.DA, new PdfString("/Helv " + fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Tf 0 g"));
        }
        annotation.SetContents(new PdfString(contents, PdfEncodings.UNICODE_BIG));
        if (sticky) annotation.SetColor(color);
        else
        {
            annotation.GetPdfObject().Put(new PdfName("PDFSimpleBorderColor"), new PdfArray(color.GetColorValue()));
            annotation.GetPdfObject().Put(new PdfName("PDFSimpleFontSize"), new PdfNumber(fontSize));
        }
        annotation.SetOpacity(new PdfNumber(Math.Clamp(opacity, 0, 1)));
        annotation.SetFlag(NativeAnnotation.PRINT);
        annotation.SetName(new PdfString(Guid.NewGuid().ToString()));
        annotation.SetCreationDate(new PdfDate().GetPdfObject());
        annotation.SetDate(new PdfDate().GetPdfObject());
        var appearance = new PdfFormXObject(rectangle);
        var canvas = new PdfCanvas(appearance, document);
        var transform = toPdf.After(new PdfAffineTransform(1, 0, 0, -1, box.X, box.Bottom));
        canvas.SaveState().ConcatMatrix(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F);
        canvas.SetExtGState(new PdfExtGState().SetFillOpacity((float)Math.Clamp(opacity, 0, 1)).SetStrokeOpacity((float)Math.Clamp(opacity, 0, 1)));
        canvas.SetFillColor(color).SetStrokeColor(sticky ? ColorConstants.DARK_GRAY : color).SetLineWidth(.6f)
            .Rectangle(.5, .5, Math.Max(1, box.Width - 1), Math.Max(1, box.Height - 1));
        if (sticky) canvas.FillStroke(); else canvas.Stroke();
        if (sticky)
        {
            canvas.SetStrokeColor(ColorConstants.BLACK).SetLineWidth(.8f);
            for (int i = 1; i <= 3; i++) canvas.MoveTo(4, box.Height - 4 - i * 3).LineTo(box.Width - 4, box.Height - 4 - i * 3).Stroke();
        }
        else
        {
            string fontPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf");
            var font = File.Exists(fontPath) ? PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H,
                PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED) : PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var lines = new List<string>();
            for (double size = Math.Clamp(fontSize, 4, 72); ; size = Math.Max(4, size - .5))
            {
                lines.Clear();
                foreach (string line in contents.Replace("\r", "").Split('\n'))
                {
                    string current = "";
                    foreach (var rune in line.EnumerateRunes())
                    {
                        string next = rune.ToString();
                        if (current.Length > 0 && font.GetWidth(current + next, (float)size) > box.Width - 8)
                        { lines.Add(current); current = ""; }
                        current += next;
                    }
                    lines.Add(current);
                }
                if (lines.Count * size * 1.25 <= box.Height - 8 || size <= 4)
                { fontSize = size; break; }
            }
            annotation.GetPdfObject().Put(PdfName.DA, new PdfString("/Helv " + fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Tf 0 g"));
            canvas.Rectangle(1, 1, box.Width - 2, box.Height - 2).Clip().EndPath();
            var fontName = appearance.GetResources().AddFont(document, font);
            annotation.GetPdfObject().Put(PdfName.DA, new PdfString("/" + fontName.GetValue() + " " + fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Tf 0 g"));
            canvas.SetFillColor(ColorConstants.BLACK);
            double y = box.Height - 4 - fontSize;
            foreach (string line in lines)
            {
                canvas.BeginText().SetFontAndSize(font, (float)fontSize).MoveText(4, y).ShowText(line).EndText();
                y -= fontSize * 1.25;
            }
        }
        canvas.RestoreState(); canvas.Release();
        annotation.SetNormalAppearance(appearance.GetPdfObject());
        page.AddAnnotation(annotation);
        return annotation;
    }

    public static List<PdfTextBox> SelectText(IEnumerable<NativePdfTextBlock> blocks, PdfTextBox region,
        out string contents) => SelectText(blocks, region, out contents, out _);

    public static List<PdfTextBox> SelectText(IEnumerable<NativePdfTextBlock> blocks, PdfTextBox region,
        out string contents, out List<PdfTextPoint[]> quads)
    {
        var boxes = new List<PdfTextBox>();
        quads = new();
        var text = new List<string>();
        foreach (var block in blocks)
        {
            // Whitespace retains unreliable font ascent/descent in some PDFs.
            // It belongs in the contents, but never determines the painted bounds.
            var glyphs = block.Glyphs.Where(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text) && g.Bounds.Width > 0 && g.Bounds.Height > 0 &&
                region.Contains(g.Bounds.X + g.Bounds.Width / 2, g.Bounds.Y + g.Bounds.Height / 2)).ToList();
            // Split each text line at unselected gaps; the spaces between columns
            // and lines must never become clickable highlighted rectangles.
            var groups = new List<List<NativePdfGlyph>>();
            foreach (var glyph in glyphs)
            {
                var group = groups.LastOrDefault();
                var previous = group?.LastOrDefault();
                double dx = previous == null ? 1 : previous.End.X - previous.Origin.X;
                double dy = previous == null ? 0 : previous.End.Y - previous.Origin.Y;
                double length = Math.Max(.001, Math.Sqrt(dx * dx + dy * dy));
                double across = previous == null ? 0 : Math.Abs((glyph.Origin.X - previous.Origin.X) * dy - (glyph.Origin.Y - previous.Origin.Y) * dx) / length;
                double gap = previous == null ? 0 : ((glyph.Origin.X - previous.End.X) * dx + (glyph.Origin.Y - previous.End.Y) * dy) / length;
                if (previous == null || across > 1 || gap > Math.Max(Math.Max(previous.Bounds.Height, previous.Bounds.Width), Math.Max(glyph.Bounds.Height, glyph.Bounds.Width)) * 1.5)
                    groups.Add(group = new());
                group!.Add(glyph);
            }
            foreach (var group in groups)
            {
                // Orient QuadPoints along the baseline, including on rotated pages.
                double dx = group[0].End.X - group[0].Origin.X, dy = group[0].End.Y - group[0].Origin.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                double ux = length > .001 ? dx / length : 1, uy = length > .001 ? dy / length : 0;
                double vx = -uy, vy = ux;
                var points = group.SelectMany(g => new[] { new PdfTextPoint(g.Bounds.X, g.Bounds.Y), new(g.Bounds.Right, g.Bounds.Y),
                    new(g.Bounds.X, g.Bounds.Bottom), new(g.Bounds.Right, g.Bounds.Bottom) }).ToList();
                double left = points.Min(p => p.X * ux + p.Y * uy) - .5, right = points.Max(p => p.X * ux + p.Y * uy) + .5;
                double top = points.Min(p => p.X * vx + p.Y * vy) - .5, bottom = points.Max(p => p.X * vx + p.Y * vy) + .5;
                PdfTextPoint Point(double u, double v) => new(ux * u + vx * v, uy * u + vy * v);
                var quad = new[] { Point(left, top), Point(right, top), Point(left, bottom), Point(right, bottom) };
                quads.Add(quad);
                boxes.Add(new(quad.Min(p => p.X), quad.Min(p => p.Y), quad.Max(p => p.X) - quad.Min(p => p.X), quad.Max(p => p.Y) - quad.Min(p => p.Y)));
                int start = block.Glyphs.IndexOf(group[0]), end = block.Glyphs.IndexOf(group[^1]);
                text.Add(string.Concat(block.Glyphs.Skip(start).Take(end - start + 1).Select(g => g.Text)));
            }
        }
        contents = string.Join("\n", text);
        return boxes;
    }

    public static void AddTextMarkup(PdfDocument document, int pageIndex, IReadOnlyList<PdfTextBox> boxes,
        bool underline, Color color, float opacity, string contents, IReadOnlyList<PdfTextPoint[]>? displayQuads = null)
    {
        if (boxes.Count == 0) return;
        var page = document.GetPage(pageIndex + 1);
        var toPdf = PdfPageCoordinates.ToDisplay(page).Inverse();
        var mapped = displayQuads != null ? displayQuads.Select(points => points.Select(p => toPdf.Map(p.X, p.Y)).ToArray()).ToList() :
            boxes.Select(box => new[] { toPdf.Map(box.X, box.Y), toPdf.Map(box.Right, box.Y),
                toPdf.Map(box.X, box.Bottom), toPdf.Map(box.Right, box.Bottom) }).ToList();
        var all = mapped.SelectMany(p => p).ToList();
        var rectangle = new Rectangle((float)all.Min(p => p.X), (float)all.Min(p => p.Y),
            (float)(all.Max(p => p.X) - all.Min(p => p.X)), (float)(all.Max(p => p.Y) - all.Min(p => p.Y)));
        float[] quads = mapped.SelectMany(points => points.SelectMany(p => new[] { (float)p.X, (float)p.Y })).ToArray();
        var annotation = underline ? PdfTextMarkupAnnotation.CreateUnderline(rectangle, quads)
            : PdfTextMarkupAnnotation.CreateHighLight(rectangle, quads);
        opacity = Math.Clamp(opacity, 0, 1);
        annotation.SetColor(color);
        annotation.SetOpacity(new PdfNumber(opacity));
        annotation.SetFlag(NativeAnnotation.PRINT);
        annotation.SetName(new PdfString(Guid.NewGuid().ToString()));
        annotation.SetContents(contents);
        annotation.SetCreationDate(new PdfDate().GetPdfObject());
        annotation.SetDate(new PdfDate().GetPdfObject());
        annotation.SetSubject(new PdfString(underline ? "Underline" : "Highlight"));
        // Supply an appearance as well as QuadPoints so the Windows renderer and
        // other viewers show exactly the same non-flattened markup.
        var appearance = new PdfFormXObject(rectangle);
        var canvas = new PdfCanvas(appearance, document);
        canvas.SaveState().SetExtGState(new PdfExtGState().SetFillOpacity(opacity).SetStrokeOpacity(opacity)
            .SetBlendMode(PdfName.Multiply)).SetFillColor(color).SetStrokeColor(color);
        foreach (var points in mapped)
        {
            if (underline)
            {
                double thickness = Math.Clamp(Math.Sqrt(Math.Pow(points[0].X - points[2].X, 2) +
                    Math.Pow(points[0].Y - points[2].Y, 2)) * .065, .5, 2);
                canvas.SetLineWidth((float)thickness).MoveTo(points[2].X, points[2].Y).LineTo(points[3].X, points[3].Y).Stroke();
            }
            else
                canvas.MoveTo(points[0].X, points[0].Y).LineTo(points[1].X, points[1].Y)
                    .LineTo(points[3].X, points[3].Y).LineTo(points[2].X, points[2].Y).ClosePath().Fill();
        }
        canvas.RestoreState();
        canvas.Release();
        annotation.SetNormalAppearance(appearance.GetPdfObject());
        page.AddAnnotation(annotation);
    }

    public static int Remove(PdfDocument document, IReadOnlyCollection<EditorAnnotation>? selection = null)
    {
        int count = 0;
        var targets = new List<(PdfPage Page, NativeAnnotation Annotation)>();
        for (int index = 0; index < document.GetNumberOfPages(); index++)
        {
            var page = document.GetPage(index + 1);
            foreach (var (annotation, key) in Enumerate(page))
                if (selection == null || selection.Any(a => a.PageIndex == index && a.PdfCommentKey == key))
                { targets.Add((page, annotation)); count++; }
        }
        if (selection != null && count != selection.Select(a => (a.PageIndex, a.PdfCommentKey)).Distinct().Count())
            throw new InvalidOperationException(LocalizationService.Get("TheSelectedAnnotationChangedSelectItAgain"));
        // Popups and reply threads belong to their parent markup. Remove the
        // related dictionaries too, without touching links or form widgets.
        var dictionaries = targets.Select(t => t.Annotation.GetPdfObject()).ToHashSet();
        bool added;
        do
        {
            added = false;
            for (int index = 1; index <= document.GetNumberOfPages(); index++)
            {
                var page = document.GetPage(index);
                foreach (var annotation in page.GetAnnotations())
                {
                    var dictionary = annotation.GetPdfObject();
                    if (dictionaries.Contains(dictionary)) continue;
                    if ((PdfName.Popup.Equals(annotation.GetSubtype()) && dictionaries.Contains(dictionary.GetAsDictionary(PdfName.Parent))) ||
                        (IsComment(annotation) && dictionaries.Contains(dictionary.GetAsDictionary(PdfName.IRT))))
                    { targets.Add((page, annotation)); dictionaries.Add(dictionary); added = true; }
                }
            }
        } while (added);
        foreach (var target in targets) target.Page.RemoveAnnotation(target.Annotation);
        return count;
    }
}
