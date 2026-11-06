using System;
using System.IO;
using System.Text.Json;

namespace PDF_simple_edit.Mcp;

public static class McpConfiguration
{
    public static string Create()
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PDFSimpleEditor.exe");
        // Also support launching the assembly with dotnet during development.
        string[] args = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { Path.Combine(AppContext.BaseDirectory, "PDFSimpleEditor.dll"), "--mcp" }
            : new[] { "--mcp" };
        return JsonSerializer.Serialize(new
        {
            mcpServers = new System.Collections.Generic.Dictionary<string, object>
            {
                ["pdf-simple-editor"] = new { command = executable, args }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
