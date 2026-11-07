using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PDF_simple_edit.Mcp;

internal static class PdfMcpCreationJson
{
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

}
