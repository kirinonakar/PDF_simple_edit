using System;
using System.IO;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

internal static class PdfAtomicFile
{
    public static async Task WriteAsync(string filePath, byte[] contents)
    {
        string fullPath = Path.GetFullPath(filePath);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"저장할 폴더를 찾을 수 없습니다: {directory}");
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents);
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(temporaryPath, fullPath, true); return; }
                catch (IOException) when (attempt < 5) { await Task.Delay(attempt * 100); }
            }
        }
        finally { PdfTemporaryFiles.TryDelete(temporaryPath); }
    }
}
