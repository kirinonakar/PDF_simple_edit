using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using PDF_simple_edit.Models;
using PDF_simple_edit.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>Text decorations stored as page content, independently of PDF annotations.</summary>
internal static class PdfTextUnderlineService
{
    internal static readonly PdfName StyleTag = new("PDFSimpleTextStyle");
    internal static readonly PdfName Underline = new("Underline");
    private static readonly PdfName DecorationTag = new("PDFSimpleTextUnderlines");
    internal static readonly PdfName PageFlag = new("PDFSimpleHasTextUnderline");
    private static readonly PdfName DocumentFlag = new("PDFSimpleHasTextUnderlines");

    internal static void MarkPage(PdfPage page)
    {
        page.GetPdfObject().Put(PageFlag, PdfBoolean.TRUE);
        page.GetDocument().GetCatalog().GetPdfObject().Put(DocumentFlag, PdfBoolean.TRUE);
    }

    // Older files have only page flags. Discover them once in the already open
    // writer and persist a document flag so undecorated edits need no second parse.
    internal static bool HasUnderlines(PdfDocument document)
    {
        var catalog = document.GetCatalog().GetPdfObject();
        if (catalog.GetAsBoolean(DocumentFlag) is { } known) return known.GetValue();
        bool found = Enumerable.Range(1, document.GetNumberOfPages())
            .Any(i => document.GetPage(i).GetPdfObject().GetAsBoolean(PageFlag)?.GetValue() == true);
        if (document.GetWriter() != null) catalog.Put(DocumentFlag, new PdfBoolean(found));
        return found;
    }

    internal static bool Read(TextRenderInfo info) => info.GetCanvasTagHierarchy()
        .FirstOrDefault(t => StyleTag.Equals(t.GetRole()))?.GetProperty(Underline) is PdfBoolean flag && flag.GetValue();

    internal static void RefreshDocument(PdfDocument document)
    {
        bool hasUnderlines = false;
        for (int i = 1; i <= document.GetNumberOfPages(); i++)
        {
            var page = document.GetPage(i);
            if (page.GetPdfObject().GetAsBoolean(PageFlag)?.GetValue() == true) RefreshPage(page);
            hasUnderlines |= page.GetPdfObject().GetAsBoolean(PageFlag)?.GetValue() == true;
        }
        document.GetCatalog().GetPdfObject().Put(DocumentFlag, new PdfBoolean(hasUnderlines));
    }

    internal static byte[] RefreshBytes(byte[] bytes, bool hasUnderlines)
    {
        if (!hasUnderlines) return bytes;
        using var output = new MemoryStream();
        // Fonts added by the writer are complete only after its first close.
        using (var document = new PdfDocument(new PdfEditingReader(new MemoryStream(bytes, writable: false)), new PdfWriter(output)))
            RefreshDocument(document);
        return output.ToArray();
    }

    internal static void RefreshPage(PdfPage page)
    {
        RemoveDecoration(page);
        var runs = NativePdfTextService.ReadUnderlineRuns(page).Where(r => r.IsUnderline).ToList();
        if (runs.Count == 0) { page.GetPdfObject().Remove(PageFlag); return; }
        page.GetPdfObject().Put(PageFlag, PdfBoolean.TRUE);
        var toPdf = PdfPageCoordinates.ToDisplay(page).Inverse();
        var canvas = new PdfCanvas(page, true);
        canvas.BeginMarkedContent(DecorationTag).SaveState();
        foreach (var run in runs)
        {
            canvas.SetStrokeColor(PdfAnnotationDocumentService.ParseCommentColor(run.Color))
                .SetLineWidth((float)Math.Max(.4, run.DisplayFontSize * .05));
            foreach (var glyph in run.Glyphs.Where(g => g.IsUnderline && !g.IsVirtual))
            {
                double dx = glyph.End.X - glyph.Origin.X, dy = glyph.End.Y - glyph.Origin.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < .001) continue;
                double offset = run.DisplayFontSize * .12;
                var a = toPdf.Map(glyph.Origin.X - dy / length * offset, glyph.Origin.Y + dx / length * offset);
                var b = toPdf.Map(glyph.End.X - dy / length * offset, glyph.End.Y + dx / length * offset);
                canvas.MoveTo(a.X, a.Y).LineTo(b.X, b.Y).Stroke();
            }
        }
        canvas.RestoreState().EndMarkedContent(); canvas.Release();
    }

    // Native text edits combine Contents streams. Recognize our own decoration
    // block by its marked-content tag instead of relying on stream numbers.
    private static void RemoveDecoration(PdfPage page)
    {
        byte[] bytes = page.GetContentBytes();
        var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(new RandomAccessSourceFactory().CreateSource(bytes)));
        var ranges = new List<(int Start, int End)>();
        int depth = 0, start = -1;
        try
        {
            var parser = new PdfCanvasParser(tokenizer, page.GetResources());
            while (true)
            {
                int position = checked((int)tokenizer.GetPosition());
                var items = parser.Parse(new List<PdfObject>());
                if (items.Count == 0) break;
                string op = items[^1].ToString()!;
                if (start < 0 && op == "BMC" && DecorationTag.Equals(items[0])) { start = position; depth = 1; }
                else if (start >= 0 && op is "BMC" or "BDC") depth++;
                else if (start >= 0 && op == "EMC" && --depth == 0)
                { ranges.Add((start, checked((int)tokenizer.GetPosition()))); start = -1; }
            }
        }
        finally { tokenizer.Close(); }
        if (ranges.Count == 0) return;
        using var result = new MemoryStream();
        int offset = 0;
        foreach (var range in ranges)
        { result.Write(bytes, offset, range.Start - offset); offset = range.End; }
        result.Write(bytes, offset, bytes.Length - offset);
        page.GetPdfObject().Put(PdfName.Contents, new PdfStream(result.ToArray()));
    }
}
