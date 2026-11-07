using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

/// <summary>Reads page text directly, without graphic extraction or rendering.</summary>
internal static class PdfMcpReader
{
    public static object Read(PdfMcpArguments arguments, byte[] snapshot, string revision, CancellationToken cancellation)
    {
        if (arguments.Has("page") && arguments.Has("pages"))
            throw new ArgumentException("Supply either page or pages, not both.");
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(snapshot, writable: false)));
        int pageCount = pdf.GetNumberOfPages();
        IReadOnlyList<int> indices = arguments.Has("pages")
            ? arguments.Text("pages").Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                ? Enumerable.Range(0, pageCount).ToArray()
                : PdfPageImageExportService.ParsePageIndices(arguments.Text("pages"), pageCount)
            : new[] { arguments.Integer("page", 1, 1, pageCount) - 1 };

        var pages = new List<object>(indices.Count);
        foreach (int index in indices)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = pdf.GetPage(index + 1);
            // A fresh strategy per page prevents text from leaking across pages.
            string text = PdfTextExtractor.GetTextFromPage(page, new LocationTextExtractionStrategy())
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(text)) text = string.Empty;
            var crop = page.GetCropBox();
            var bounds = PdfPageCoordinates.ToDisplay(page).Map(new PdfTextBox(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight()));
            pages.Add(new
            {
                page = index + 1, text, hasText = !string.IsNullOrWhiteSpace(text),
                width = bounds.Width, height = bounds.Height, rotation = page.GetRotation()
            });
        }
        cancellation.ThrowIfCancellationRequested();
        return new
        {
            inputPath = arguments.Path("inputPath"), revision, pageCount, pages,
            extraction = "Existing PDF text, including saved OCR layers. No image recognition is performed here. For image-only pages use pdf_vlm_ocr with your own vision and pdf_vlm_ocr_apply to add recognized text. Pages without extractable text return empty text. Reading order is inferred from text positions and may differ in complex layouts."
        };
    }
}
