using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PDF_simple_edit.Mcp;

/// <summary>Newline-delimited UTF-8 JSON-RPC over inherited standard streams.</summary>
internal sealed class PdfMcpServer
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _tools = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();
    private bool _initialized;
    private bool _ready;

    public async Task RunAsync(TextReader input, TextWriter output)
    {
        var pending = new List<Task>();
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement message;
            try { using var json = JsonDocument.Parse(line); message = json.RootElement.Clone(); }
            catch (JsonException) { await Error(output, null, -32700, "Parse error"); continue; }
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            {
                await Error(output, null, -32600, "Invalid request"); continue;
            }
            if (!message.TryGetProperty("id", out var id))
            {
                if (method.GetString() == "notifications/initialized" && _initialized) _ready = true;
                if (method.GetString() == "notifications/cancelled" && message.TryGetProperty("params", out var p) &&
                    p.ValueKind == JsonValueKind.Object && p.TryGetProperty("requestId", out var requestId) &&
                    _requests.TryGetValue(requestId.GetRawText(), out var cancellation)) cancellation.Cancel();
                continue; // Notifications never receive responses.
            }
            if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            { await Error(output, null, -32600, "Request id must be a string or number"); continue; }
            var cts = new CancellationTokenSource();
            if (!_requests.TryAdd(id.GetRawText(), cts))
            { cts.Dispose(); await Error(output, id, -32600, "Duplicate request id"); continue; }
            pending.RemoveAll(task => task.IsCompleted);
            pending.Add(HandleAsync(output, message, id, method.GetString()!, cts));
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task HandleAsync(TextWriter output, JsonElement message, JsonElement id, string method, CancellationTokenSource cts)
    {
        try
        {
            var p = message.TryGetProperty("params", out var parameters) ? parameters : default;
            if (p.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            { await Error(output, id, -32602, "params must be an object"); return; }
            if (method == "ping") { await Result(output, id, new { }); return; }
            if (method == "initialize")
            {
                if (_initialized) { await Error(output, id, -32600, "Already initialized"); return; }
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("protocolVersion", out var requested) || requested.ValueKind != JsonValueKind.String ||
                    !p.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object ||
                    !p.TryGetProperty("clientInfo", out var client) || client.ValueKind != JsonValueKind.Object)
                { await Error(output, id, -32602, "initialize requires protocolVersion, capabilities and clientInfo"); return; }
                string protocol = requested.GetString() is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25"
                    ? requested.GetString()! : "2025-11-25";
                _initialized = true;
                await Result(output, id, new
                {
                    protocolVersion = protocol, capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "pdf-simple-editor", version = typeof(PdfMcpServer).Assembly.GetName().Version?.ToString() ?? "1.0.0" },
                    instructions = "Local saved PDFs only. Paths absolute; pages 1-based; coordinates in PDF points from displayed CropBox top-left (except crop/OCR). IDs are revision-specific: use returned page + ID; never reuse after edits. Use pdf_batch for multiple edits or pdf_delete_objects for multiple deletions with original IDs. Existing destinations require overwrite=true. Creation details/examples: pdf_create_help on demand."
                });
                return;
            }
            if (!_ready) { await Error(output, id, -32600, "Initialize and send notifications/initialized first"); return; }
            if (method == "tools/list")
            {
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("cursor", out _))
                { await Error(output, id, -32602, "This tool list has no pagination cursor"); return; }
                await Result(output, id, new { tools = PdfMcpTools.Definitions }); return;
            }
            if (method != "tools/call") { await Error(output, id, -32601, "Method not found"); return; }
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !PdfMcpTools.Exists(name.GetString()!))
            { await Error(output, id, -32602, "Unknown or missing tool name"); return; }
            var arguments = p.TryGetProperty("arguments", out var supplied) ? supplied : JsonSerializer.SerializeToElement(new { });
            if (arguments.ValueKind != JsonValueKind.Object)
            { await Error(output, id, -32602, "arguments must be an object"); return; }
            bool acquired = false;
            try
            {
                await _tools.WaitAsync(cts.Token); acquired = true;
                object result = await PdfMcpTools.ExecuteAsync(name.GetString()!, arguments, cts.Token);
                object[] content = result is PdfMcpImageResult image ? image.Content
                    : new object[] { new { type = "text", text = JsonSerializer.Serialize(result) } };
                await Result(output, id, new { content, isError = false });
            }
            catch (Exception error)
            {
                await Result(output, id, new { content = new[] { new { type = "text", text = error is OperationCanceledException ? "Operation cancelled." : error.Message } }, isError = true });
            }
            finally { if (acquired) _tools.Release(); }
        }
        finally { _requests.TryRemove(id.GetRawText(), out _); cts.Dispose(); }
    }

    private Task Result(TextWriter output, JsonElement id, object result) => Send(output, new { jsonrpc = "2.0", id, result });
    private Task Error(TextWriter output, JsonElement? id, int code, string message) => Send(output, new { jsonrpc = "2.0", id, error = new { code, message } });
    private async Task Send(TextWriter output, object message)
    {
        await _writer.WaitAsync();
        try { await output.WriteLineAsync(JsonSerializer.Serialize(message)); await output.FlushAsync(); }
        finally { _writer.Release(); }
    }
}
