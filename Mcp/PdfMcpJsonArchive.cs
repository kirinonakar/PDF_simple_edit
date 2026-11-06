using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using iText.Kernel.Pdf;

namespace PDF_simple_edit.Mcp;

internal static class PdfMcpJsonArchive
{
    internal const string Format = "pdf-simple-editor/archive-v1";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static async Task<JsonElement> ExpandRequestAsync(JsonElement request, CancellationToken cancellation)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("jsonPath", out _)) return request;
        var input = new PdfMcpArguments(request);
        foreach (var property in request.EnumerateObject())
            if (property.Name is not ("jsonPath" or "outputPath" or "overwrite")) throw new ArgumentException("Only outputPath and overwrite may accompany jsonPath.");
        string text = await File.ReadAllTextAsync(input.Path("jsonPath"), Encoding.UTF8, cancellation);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("arguments", out var nested))
        {
            if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "pdf_create")
                throw new ArgumentException("JSON tool envelope must name pdf_create.");
            root = nested;
        }
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("JSON creation input must be an object.");
        if (root.TryGetProperty("jsonPath", out _)) throw new ArgumentException("Nested jsonPath references are not supported.");
        var merged = root.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone());
        foreach (string key in new[] { "outputPath", "overwrite" })
            if (request.TryGetProperty(key, out var value)) merged[key] = value;
        return JsonSerializer.SerializeToElement(merged);
    }

    internal static byte[] Restore(PdfMcpArguments arguments, CancellationToken cancellation)
    {
        foreach (var property in arguments.Value.EnumerateObject())
            if (property.Name is not ("outputPath" or "overwrite" or "format" or "pdfBase64" or "sha256" or "sourceInfo"))
                throw new ArgumentException("An archive cannot be combined with creation content or style settings. sourceInfo is read-only; the original PDF bytes are restored.");
        if (arguments.Text("format") != Format) throw new ArgumentException("Unsupported PDF JSON archive format.");
        byte[] pdf;
        try { pdf = Convert.FromBase64String(arguments.Text("pdfBase64")); }
        catch (FormatException) { throw new ArgumentException("Archive contains invalid pdfBase64."); }
        cancellation.ThrowIfCancellationRequested();
        string hash = Convert.ToHexStringLower(SHA256.HashData(pdf));
        if (!hash.Equals(arguments.Text("sha256"), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Archive PDF checksum does not match sha256. The archive is damaged or was modified.");
        return pdf;
    }

    internal static async Task<byte[]> ExportAsync(byte[] bytes, string revision, PdfMcpArguments arguments,
        Func<PdfMcpArguments, byte[], string, Task<object>> inspect, CancellationToken cancellation)
    {
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        var pages = new List<object>();
        for (int number = 1; number <= pdf.GetNumberOfPages(); number++)
        {
            cancellation.ThrowIfCancellationRequested();
            var pageArguments = new PdfMcpArguments(JsonSerializer.SerializeToElement(new { inputPath = arguments.Path("inputPath"), page = number }));
            try
            {
                var information = JsonSerializer.SerializeToElement(await inspect(pageArguments, bytes, revision));
                var size = information.GetProperty("pages")[number - 1];
                pages.Add(new
                {
                    page = number, width = size.GetProperty("width"), height = size.GetProperty("height"), rotation = size.GetProperty("rotation"),
                    objects = information.GetProperty("objects"), formFields = information.GetProperty("formFields")
                });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Inspection is advisory. A valid PDF with an unsupported graphic
                // must still be exportable and restorable without losing bytes.
                pages.Add(new { page = number, inspectionError = error.Message });
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = Format, sha256 = revision, pdfBase64 = Convert.ToBase64String(bytes),
            sourceInfo = new { readOnly = true, title = pdf.GetDocumentInfo().GetTitle(), pageCount = pdf.GetNumberOfPages(), pages }
        }, Options);
    }
}
