using Loc = PDF_simple_edit.Services.LocalizationService;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

internal static class PdfAtomicFile
{
    public static async Task WriteAsync(string filePath, byte[] contents, PdfFileVersion? expected = null)
    {
        string fullPath = Path.GetFullPath(filePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(Loc.Format("CannotFindTheSaveFolder", directory));
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(contents).ConfigureAwait(false);
                await Task.Run(() => stream.Flush(flushToDisk: true)).ConfigureAwait(false);
            }
            for (int attempt = 1; ; attempt++)
            {
                // Check outside the retry block: a conflict must never turn
                // into an unconditional overwrite on the next attempt.
                if (expected != null && !expected.Matches(PdfFileVersion.Read(fullPath)))
                    throw new IOException(Loc.Get("AnotherProgramChangedTheDestinationFileSaveUnderA"));
                try
                {
                    if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
                    else File.Move(temporaryPath, fullPath);
                    return;
                }
                catch (IOException) when (attempt < 5) { await Task.Delay(attempt * 100).ConfigureAwait(false); }
            }
        }
        finally { PdfTemporaryFiles.TryDelete(temporaryPath); }
    }
}
