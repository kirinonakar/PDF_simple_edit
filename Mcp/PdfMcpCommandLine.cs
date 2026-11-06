using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal static class PdfMcpCommandLine
{
    internal static bool IsRequested(string[] args) => args.Any(arg => arg is "--create" or "--to-json" or "--help") ||
        args.Length > 0 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    internal static int Run(string[] args)
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var errorOutput = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(TextWriter.Null);
        try
        {
            if (args.Length == 1 && args[0] == "--help")
            {
                output.WriteLine("PDFSimpleEditor.exe --mcp\nPDFSimpleEditor.exe --create input.json [--output output.pdf] [--overwrite]\nPDFSimpleEditor.exe input.json [--output output.pdf] [--overwrite]\nPDFSimpleEditor.exe --to-json input.pdf [--output output.json] [--overwrite]\nJSON input can contain pdf_create arguments or a lossless archive from pdf_to_json. Without --output, use outputPath in creation JSON or the input file's name with the destination extension.");
                return 0;
            }
            string mode, input;
            int position;
            if (args.Length >= 2 && args[0] is "--create" or "--to-json")
            { mode = args[0]; input = args[1]; position = 2; }
            else if (args.Length > 0 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            { mode = "--create"; input = args[0]; position = 1; }
            else throw new ArgumentException("Use --create input.json or --to-json input.pdf. See --help.");
            string? destination = null;
            bool overwrite = false;
            while (position < args.Length)
            {
                string option = args[position++];
                if (option == "--output" && position < args.Length && destination == null) destination = Path.GetFullPath(args[position++]);
                else if (option == "--overwrite" && !overwrite) overwrite = true;
                else throw new ArgumentException($"Unknown, duplicate or incomplete option: {option}");
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            LocalizationService.Initialize(LocalizationService.English);
            var request = new Dictionary<string, object> { [mode == "--create" ? "jsonPath" : "inputPath"] = Path.GetFullPath(input) };
            if (destination != null) request["outputPath"] = destination;
            if (overwrite) request["overwrite"] = true;
            JsonElement arguments = JsonSerializer.SerializeToElement(request);
            if (mode == "--create") arguments = PdfMcpJsonArchive.ExpandRequestAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();
            if (!arguments.TryGetProperty("outputPath", out _))
            {
                var merged = arguments.EnumerateObject().ToDictionary(pair => pair.Name, pair => (object)pair.Value);
                merged["outputPath"] = Path.ChangeExtension(Path.GetFullPath(input), mode == "--create" ? ".pdf" : ".json");
                arguments = JsonSerializer.SerializeToElement(merged);
            }
            object result = PdfMcpTools.ExecuteAsync(mode == "--create" ? "pdf_create" : "pdf_to_json", arguments, CancellationToken.None).GetAwaiter().GetResult();
            output.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception error) { errorOutput.WriteLine(error.Message); return 1; }
        finally { PdfTemporaryFiles.CleanupSession(); }
    }
}
