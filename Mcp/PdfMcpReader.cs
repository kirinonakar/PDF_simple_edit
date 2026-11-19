using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

/// <summary>Reads page text directly, without graphic extraction or rendering.</summary>
internal static class PdfMcpReader
{
    private sealed record ReadPage(int page, string text, bool hasText, double width, double height, int rotation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PdfMcpColumnReader.Column[]? columns);

    public static object Read(PdfMcpArguments arguments, byte[] snapshot, string revision, CancellationToken cancellation)
    {
        if (arguments.Has("page") && arguments.Has("pages"))
            throw new ArgumentException("Supply either page or pages, not both.");
        string layout = arguments.Text("layout", "auto");
        if (layout is not ("auto" or "single" or "two-column"))
            throw new ArgumentException("layout must be auto, single, or two-column.");
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(snapshot, writable: false)));
        int pageCount = pdf.GetNumberOfPages();
        IReadOnlyList<int> indices = arguments.Has("pages")
            ? arguments.Text("pages").Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                ? Enumerable.Range(0, pageCount).ToArray()
                : PdfPageImageExportService.ParsePageIndices(arguments.Text("pages"), pageCount)
            : new[] { arguments.Integer("page", 1, 1, pageCount) - 1 };

        var pages = new List<ReadPage>(indices.Count);
        foreach (int index in indices)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = pdf.GetPage(index + 1);
            var bounds = PdfPageCoordinates.GetDisplayBounds(page);
            var strategy = new PdfReadTextExtractionStrategy();
            (string text, PdfMcpColumnReader.Column[]? columns) = layout == "single"
                ? (Normalize(PdfTextExtractor.GetTextFromPage(page, strategy)), null)
                : PdfMcpColumnReader.Read(page, bounds, strategy, layout == "two-column", cancellation);
            pages.Add(new(index + 1, text, text.Length > 0, bounds.Width, bounds.Height, page.GetRotation(), columns));
        }
        cancellation.ThrowIfCancellationRequested();
        return new
        {
            inputPath = arguments.Path("inputPath"), revision, pageCount, pages,
            extraction = "Existing PDF text, including saved OCR layers. No image recognition is performed here. For image-only pages use pdf_vlm_ocr with your own vision and pdf_vlm_ocr_apply to add recognized text. Pages without extractable text return empty text. Two-column pages include columns in left-to-right order. Known HYhwpEQ glyphs are decoded to Unicode. Reading order is inferred from text positions and may differ in complex layouts."
        };
    }

    internal static string Normalize(string text) => string.IsNullOrWhiteSpace(text)
        ? string.Empty
        : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
