using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

/// <summary>Reads page text directly, without graphic extraction or rendering.</summary>
internal static class PdfMcpReader
{
    public static readonly string[] Modes = ["text", "structured", "raw", "diagnostic"];

    private sealed record ReadPage(int page, string text, bool hasText, double width, double height, int rotation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PdfMcpColumnReader.Column[]? columns = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PdfMcpPageDetails.Span>? spans = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PdfMcpMath.Equation>? equations = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? rawText = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PdfMcpPageDetails.Font>? fonts = null);

    public static object Read(PdfMcpArguments arguments, byte[] snapshot, string revision, CancellationToken cancellation)
    {
        if (arguments.Has("page") && arguments.Has("pages"))
            throw new ArgumentException("Supply either page or pages, not both.");
        string mode = arguments.Text("mode", "text");
        if (!Modes.Contains(mode, StringComparer.Ordinal))
            throw new ArgumentException("mode must be text, structured, raw, or diagnostic.");
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
            pages.Add(ReadPageContent(page, index + 1, mode, layout, cancellation));
        }
        cancellation.ThrowIfCancellationRequested();
        return new
        {
            inputPath = arguments.Path("inputPath"), revision, pageCount, mode, pages,
            extraction = "Existing PDF text, including saved OCR layers. For image-only pages use pdf_vlm_ocr. " + (mode switch
            {
                "raw" => "Decoded characters in content-stream order, without inserted whitespace or font repairs.",
                "text" => "Reading order is inferred from glyph positions. Known HYhwpEQ glyphs are decoded to Unicode.",
                _ => "Coordinates are in PDF points from the displayed CropBox top-left. Spans are in content-stream order. LaTeX is inferred from the glyphs and their positions."
            })
        };
    }

    private static ReadPage ReadPageContent(PdfPage page, int number, string mode, string layout, CancellationToken cancellation)
    {
        PdfTextBox bounds = PdfPageCoordinates.GetDisplayBounds(page);
        if (mode == "raw")
        {
            var raw = new StringBuilder();
            PdfMcpColumnReader.Process(page, info =>
            {
                cancellation.ThrowIfCancellationRequested();
                raw.Append(info.GetText());
            });
            return new(number, raw.ToString(), raw.Length > 0, bounds.Width, bounds.Height, page.GetRotation());
        }

        var strategy = new PdfReadTextExtractionStrategy();
        var details = mode == "text" ? null : new PdfMcpPageDetails(page, mode == "diagnostic");
        string text;
        PdfMcpColumnReader.Column[]? columns = null;
        if (layout == "single")
        {
            PdfMcpColumnReader.Process(page, info =>
            {
                cancellation.ThrowIfCancellationRequested();
                strategy.EventOccurred(info, EventType.RENDER_TEXT);
                details?.ReadText(info);
            }, details == null ? null : path =>
            {
                cancellation.ThrowIfCancellationRequested();
                details.ReadPath(path);
            });
            text = Normalize(strategy.GetResultantText());
        }
        else
        {
            (text, columns) = PdfMcpColumnReader.Read(page, bounds, strategy, layout == "two-column", cancellation, details);
        }

        return new(number, text, text.Length > 0, bounds.Width, bounds.Height, page.GetRotation(),
            columns, details?.Spans, details?.GetEquations(cancellation), details?.RawText, details?.Fonts);
    }

    internal static string Normalize(string text) => string.IsNullOrWhiteSpace(text)
        ? string.Empty
        : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
