using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using iText.IO.Source;
using iText.Kernel.Pdf;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal static partial class PdfMcpTools
{
    private static object InspectionRegionSchema() => new
    {
        type = "object",
        description = "Match displayed bounds intersecting this rectangle, including edges.",
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

    private static async Task<object> FindTextAsync(PdfMcpArguments a, CancellationToken cancellation)
    {
        string query = a.Text("query");
        if (query.Length == 0) throw new ArgumentException("query must not be empty.");
        bool caseSensitive = a.Flag("caseSensitive");
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int limit = a.Integer("maxResults", 100, 1, 10000);
        string inputPath = a.Path("inputPath");
        // Hash and search the same file handle, with writes excluded until the scan ends.
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        string revision = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false));
        CheckExpectedRevision(a, revision);
        return await Task.Run<object>(() =>
        {
            input.Position = 0;
            using var reader = new PdfReader(new RandomAccessSourceFactory().CreateSource(input), new ReaderProperties());
            using var pdf = new PdfDocument(reader);
            int pageCount = pdf.GetNumberOfPages();
            var matches = new List<object>();
            int matchedObjectCount = 0;
            for (int number = 1; number <= pageCount; number++)
            {
                cancellation.ThrowIfCancellationRequested();
                var pageMatches = NativePdfTextService.FindBlocks(pdf.GetPage(number), query, comparison,
                    limit - matches.Count, out int pageMatchCount);
                matchedObjectCount += pageMatchCount;
                foreach (var match in pageMatches)
                {
                    matches.Add(new
                    {
                        objectId = $"{revision}:{number}:content:{match.BlockIndex}", page = number, kind = "text", text = match.Text,
                        x = match.Bounds.X, y = match.Bounds.Y, width = match.Bounds.Width, height = match.Bounds.Height,
                        matchIndex = match.MatchIndex, matchLength = query.Length
                    });
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return new { inputPath, revision, pageCount, query, caseSensitive, matchedObjectCount, returnedCount = matches.Count,
                truncated = matchedObjectCount > matches.Count, matches,
                extraction = "Searches existing editable body text, including saved OCR. Excludes image recognition, annotations, form values and matches across separate text blocks. Returns one result per matching block with its first UTF-16 substring offset." };
        }, cancellation);
    }
}
