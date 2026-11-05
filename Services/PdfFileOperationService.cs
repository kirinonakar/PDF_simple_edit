using Loc = PDF_simple_edit.Services.LocalizationService;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

internal sealed class PdfFileOperationService
{
    public Task<byte[]> CreateMergedBytesAsync(byte[]? currentPdfBytes, IReadOnlyCollection<string> sourceFiles,
        WriterProperties? encryptionProperties = null) => Task.Run(() =>
    {
        if (sourceFiles.Count == 0) throw new ArgumentException(Loc.Get("SelectPDFFilesToMerge"));
        using var output = new MemoryStream();
        using (var destination = new PdfDocument(new PdfWriter(output, encryptionProperties ?? new WriterProperties())))
        {
            var merger = new PdfMerger(destination);
            if (currentPdfBytes != null)
            {
                using var source = Open(currentPdfBytes);
                merger.Merge(source, 1, source.GetNumberOfPages());
            }
            foreach (string filePath in sourceFiles)
            {
                try
                {
                    using var source = new PdfDocument(new PdfReader(filePath));
                    merger.Merge(source, 1, source.GetNumberOfPages());
                }
                catch (Exception ex) { throw new InvalidDataException(Loc.Format("CannotMerge", Path.GetFileName(filePath), ex.Message), ex); }
            }
        }
        return output.ToArray();
    });

    public async Task<bool> MergeAsync(byte[]? currentPdfBytes, IReadOnlyCollection<string> sourceFiles,
        string outputPath, WriterProperties? encryptionProperties = null)
    {
        byte[] bytes = await CreateMergedBytesAsync(currentPdfBytes, sourceFiles, encryptionProperties);
        await PdfAtomicFile.WriteAsync(outputPath, bytes);
        return true;
    }

    public Task<int> SplitAsync(byte[] pdfBytes, string? sourceFilePath, string outputFolder,
        IReadOnlyList<(int start, int end)> ranges, WriterProperties? encryptionProperties = null) => Task.Run(() =>
    {
        if (!Directory.Exists(outputFolder)) throw new DirectoryNotFoundException(Loc.Get("CannotFindTheOutputFolder"));
        if (ranges.Count == 0) throw new ArgumentException(Loc.Get("EnterThePageRangesToSplit"));
        using var source = Open(pdfBytes);
        foreach (var (start, end) in ranges) ValidateRange(start, end, source.GetNumberOfPages());
        string baseName = string.IsNullOrEmpty(sourceFilePath) ? "split" : Path.GetFileNameWithoutExtension(sourceFilePath);
        var staged = new List<(string Temp, string Output)>();
        var published = new List<string>();
        try
        {
            for (int index = 0; index < ranges.Count; index++)
            {
                string output = Path.Combine(outputFolder, $"{baseName}_{index + 1}.pdf");
                if (Path.Exists(output)) throw new IOException(Loc.Format("TheFileAlreadyExistsSelectAnotherFolder", output));
                staged.Add((Path.Combine(outputFolder, $".{Guid.NewGuid():N}.tmp"), output));
            }
            for (int index = 0; index < ranges.Count; index++)
            {
                using var destination = new PdfDocument(new PdfWriter(staged[index].Temp, encryptionProperties ?? new WriterProperties()));
                source.CopyPagesTo(ranges[index].start, ranges[index].end, destination);
                source.FlushCopiedObjects(destination);
            }
            foreach (var item in staged)
            {
                File.Move(item.Temp, item.Output, overwrite: false);
                published.Add(item.Output);
            }
            return published.Count;
        }
        catch (Exception ex)
        {
            foreach (string path in published) PdfTemporaryFiles.TryDelete(path);
            string[] remaining = published.Where(File.Exists).ToArray();
            throw new IOException(Loc.Format("FailedToSplitThePDF2", ex.Message) +
                (remaining.Length == 0 ? "" : Loc.Format("OutputsThatCouldNotBeDeleted", string.Join(", ", remaining))), ex);
        }
        finally { foreach (var item in staged) PdfTemporaryFiles.TryDelete(item.Temp); }
    });

    public async Task<bool> ExportPagesAsync(byte[] pdfBytes, string outputPath, IEnumerable<int> pageIndices,
        WriterProperties? encryptionProperties = null)
    {
        int[] indices = pageIndices.Distinct().Order().ToArray();
        if (indices.Length == 0) throw new ArgumentException(Loc.Get("SelectPagesToExtract"));
        byte[] bytes = await Task.Run(() =>
        {
            using var source = Open(pdfBytes);
            foreach (int index in indices) ValidateRange(index + 1, index + 1, source.GetNumberOfPages());
            using var output = new MemoryStream();
            using (var destination = new PdfDocument(new PdfWriter(output, encryptionProperties ?? new WriterProperties())))
                source.CopyPagesTo(indices.Select(i => i + 1).ToList(), destination);
            return output.ToArray();
        });
        await PdfAtomicFile.WriteAsync(outputPath, bytes);
        return true;
    }

    private static PdfDocument Open(byte[] bytes) => new(new PdfReader(new MemoryStream(bytes, writable: false)));

    internal static void ValidateRange(int start, int end, int pageCount)
    {
        if (start < 1 || end < start || end > pageCount)
            throw new ArgumentException(Loc.Format("InvalidPageRangeEnterAnAscendingRangeBetween1", start, end, pageCount));
    }
}
