using System;
using System.IO;

namespace PDF_simple_edit.Services;

internal static class PdfTemporaryFiles
{
    // Keep images alive while annotations and undo entries may reference them.
    // Other editor processes must never clean up this session's files.
    public static string SessionDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "PDF_simple_edit", $"session_{Environment.ProcessId}_{Guid.NewGuid():N}");

    public static string GetDirectory(string kind)
    {
        string path = Path.Combine(SessionDirectory, kind);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void CleanupSession()
    {
        try { if (Directory.Exists(SessionDirectory)) Directory.Delete(SessionDirectory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void CleanupStaleFiles()
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-1);
        string root = Path.Combine(Path.GetTempPath(), "PDF_simple_edit");
        CleanFiles(Path.GetTempPath(), "print_*.pdf", cutoff, legacyPrint: true);
        CleanFiles(Path.Combine(root, "images"), "*", cutoff);
        CleanFiles(Path.Combine(root, "clipboard"), "*", cutoff);
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root, "session_*"))
            {
                string[] parts = Path.GetFileName(directory).Split('_');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int processId) ||
                    !Guid.TryParseExact(parts[2], "N", out _) || Directory.GetLastWriteTimeUtc(directory) >= cutoff)
                    continue;
                try { using var process = System.Diagnostics.Process.GetProcessById(processId); if (!process.HasExited) continue; }
                catch (ArgumentException) { }
                try { Directory.Delete(directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CleanFiles(string directory, string pattern, DateTime cutoff, bool legacyPrint = false)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, pattern))
            {
                if (legacyPrint && !Guid.TryParse(Path.GetFileNameWithoutExtension(path)[6..], out _)) continue;
                if (File.GetLastWriteTimeUtc(path) < cutoff) TryDelete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
