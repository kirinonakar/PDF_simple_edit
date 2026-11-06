using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal static partial class PdfMcpTools
{
    private static object InspectionRegionSchema() => new
    {
        type = "object",
        description = "Return objects whose displayed bounding boxes intersect this rectangle (edges included). PDF points from the displayed CropBox's top-left, including page rotation.",
        properties = new
        {
            x = Number, y = Number,
            width = new { type = "number", minimum = .001 },
            height = new { type = "number", minimum = .001 }
        },
        required = new[] { "x", "y", "width", "height" },
        additionalProperties = false
    };

    private static object DescribeContent(PdfPageContent content, int index, int page, string revision) => new
    {
        objectId = $"{revision}:{page}:content:{index}",
        kind = content.NativeText != null ? "text" : content.GraphicOperations.Any(op => !op.IsImageOperation) ? "vector" : "image",
        x = content.X, y = content.Y, width = content.Width, height = content.Height,
        text = content.NativeText?.Text, fontFamily = content.FontFamily, fontSize = content.FontSize, color = content.Color
    };

    private static bool InRegion(PdfTextBox bounds, PdfTextBox? region) => region is not { } box ||
        bounds.Right >= box.X && bounds.X <= box.Right && bounds.Bottom >= box.Y && bounds.Y <= box.Bottom;

    private static async Task<object> InspectAsync(PdfMcpArguments a, byte[] bytes, string revision, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var pdf = Open(bytes);
        int number = a.Integer("page", 1, 1, pdf.GetNumberOfPages());
        var page = pdf.GetPage(number);
        string kind = a.Text("kind", "all");
        PdfTextBox? region = a.Has("region") ? new PdfMcpArguments(a.Value.GetProperty("region")).Bounds() : null;
        int limit = a.Has("maxObjects") ? a.Integer("maxObjects", max: 10000) : int.MaxValue;
        // Native text is the prefix of the full extraction, in the same order.
        // Using textOnly saves graphic extraction without changing content IDs.
        var contents = await new PdfPageContentExtractor().ExtractAsync(bytes, number - 1,
            textOnly: kind == "text", strict: true, includePreviews: false);
        var objects = new List<object>();
        int matchedObjectCount = 0;
        void Add(object item)
        {
            matchedObjectCount++;
            if (objects.Count < limit) objects.Add(item);
        }
        for (int index = 0; index < contents.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var content = contents[index];
            if (kind == "graphics" && content.NativeText != null) continue;
            if (!InRegion(new(content.X, content.Y, content.Width, content.Height), region)) continue;
            // Always retain the index BEFORE filtering; edits use full extraction.
            Add(DescribeContent(content, index, number, revision));
        }
        if (kind != "text")
        {
            var annotations = page.GetAnnotations();
            for (int index = 0; index < annotations.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var annotation = annotations[index];
                var rect = annotation.GetRectangle()?.ToRectangle();
                var box = rect == null ? default : PdfPageCoordinates.ToDisplay(page).Map(new PdfTextBox(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
                if (!InRegion(box, region)) continue;
                Add(new { objectId = $"{revision}:{number}:annotation:{index}", kind = "annotation", subtype = annotation.GetSubtype()?.GetValue(),
                    x = box.X, y = box.Y, width = box.Width, height = box.Height, contents = annotation.GetContents()?.ToUnicodeString() });
            }
        }
        var pages = Enumerable.Range(1, pdf.GetNumberOfPages()).Select(i =>
        {
            cancellation.ThrowIfCancellationRequested();
            var p = pdf.GetPage(i); var rect = p.GetCropBox();
            var bounds = PdfPageCoordinates.ToDisplay(p).Map(new PdfTextBox(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
            return new { page = i, width = bounds.Width, height = bounds.Height, rotation = p.GetRotation() };
        }).ToArray();
        var formFields = kind == "all"
            ? new PdfFormService().ReadPage(bytes, number - 1).Where(field => InRegion(field.Bounds, region)).ToArray()
            : Array.Empty<PdfFormInput>();
        cancellation.ThrowIfCancellationRequested();
        return new { inputPath = a.Path("inputPath"), revision, pageCount = pdf.GetNumberOfPages(), pages, page = number,
            coordinates = "PDF points, displayed CropBox top-left; page numbers are 1-based", objects,
            matchedObjectCount, returnedCount = objects.Count, truncated = matchedObjectCount > objects.Count, formFields };
    }

    private static async Task<object> FindTextAsync(PdfMcpArguments a, byte[] bytes, string revision, CancellationToken cancellation)
    {
        string query = a.Text("query");
        if (query.Length == 0) throw new ArgumentException("query must not be empty.");
        var comparison = a.Flag("caseSensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int limit = a.Integer("maxResults", 100, 1, 10000);
        using var pdf = Open(bytes);
        var matches = new List<object>();
        int matchedObjectCount = 0;
        var extractor = new PdfPageContentExtractor();
        for (int number = 1; number <= pdf.GetNumberOfPages(); number++)
        {
            cancellation.ThrowIfCancellationRequested();
            var contents = await extractor.ExtractAsync(bytes, number - 1, textOnly: true, strict: true, includePreviews: false);
            for (int index = 0; index < contents.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var content = contents[index];
                string? text = content.NativeText?.Text;
                int matchIndex = text?.IndexOf(query, comparison) ?? -1;
                if (matchIndex < 0) continue;
                matchedObjectCount++;
                if (matches.Count >= limit) continue;
                matches.Add(new
                {
                    objectId = $"{revision}:{number}:content:{index}", page = number, kind = "text", text,
                    x = content.X, y = content.Y, width = content.Width, height = content.Height,
                    matchIndex, matchLength = query.Length
                });
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return new { inputPath = a.Path("inputPath"), revision, pageCount = pdf.GetNumberOfPages(), query,
            caseSensitive = a.Flag("caseSensitive"), matchedObjectCount, returnedCount = matches.Count,
            truncated = matchedObjectCount > matches.Count, matches,
            extraction = "Existing editable body text only; no OCR, annotations, form values or matches across separate text blocks. One result per matching block; matchIndex is its first UTF-16 substring offset." };
    }
}
