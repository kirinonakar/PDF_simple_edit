using System;
using System.IO;
using System.Text;
using PDF_simple_edit.Mcp;
using PDF_simple_edit.Services;

namespace PDF_simple_edit;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (PdfMcpCommandLine.IsRequested(args)) return PdfMcpCommandLine.Run(args);
        if (Array.Exists(args, arg => arg.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            // Branch before constructing any XAML application or window. Inherited
            // pipes work for a WinExe; never allocate a console or print to stdout.
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(TextWriter.Null);
            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                LocalizationService.Initialize(LocalizationService.English);
                new PdfMcpServer().RunAsync(input, output).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
            finally { PdfTemporaryFiles.CleanupSession(); }
        }

        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }
}
