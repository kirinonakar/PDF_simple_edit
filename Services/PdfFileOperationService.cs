using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

internal sealed class PdfFileOperationService
{
    public Task<bool> MergeAsync(
        byte[]? currentPdfBytes,
        IReadOnlyCollection<string> sourceFiles,
        string outputPath,
        WriterProperties? encryptionProperties = null)
    {
        return Task.Run(() =>
        {
            try
            {
                using var writer = encryptionProperties == null
                    ? new PdfWriter(outputPath)
                    : new PdfWriter(outputPath, encryptionProperties);
                using var destination = new PdfDocument(writer);
                var merger = new PdfMerger(destination);

                if (currentPdfBytes != null)
                {
                    using var input = new MemoryStream(currentPdfBytes);
                    using var reader = new PdfReader(input);
                    using var source = new PdfDocument(reader);
                    merger.Merge(source, 1, source.GetNumberOfPages());
                }

                foreach (string filePath in sourceFiles)
                {
                    if (!File.Exists(filePath))
                        continue;

                    using var reader = new PdfReader(filePath);
                    using var source = new PdfDocument(reader);
                    merger.Merge(source, 1, source.GetNumberOfPages());
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Merge error: {exception.Message}");
                return false;
            }
        });
    }

    public Task<int> SplitAsync(
        byte[] pdfBytes,
        string? sourceFilePath,
        string outputFolder,
        IReadOnlyList<(int start, int end)> ranges,
        WriterProperties? encryptionProperties = null)
    {
        return Task.Run(() =>
        {
            int count = 0;
            try
            {
                string baseName = string.IsNullOrEmpty(sourceFilePath)
                    ? "split"
                    : Path.GetFileNameWithoutExtension(sourceFilePath);

                for (int index = 0; index < ranges.Count; index++)
                {
                    (int start, int end) = ranges[index];
                    string outputPath = Path.Combine(
                        outputFolder,
                        $"{baseName}_{index + 1}.pdf");

                    using var input = new MemoryStream(pdfBytes);
                    using var reader = new PdfReader(input);
                    using var source = new PdfDocument(reader);
                    using var writer = encryptionProperties == null
                        ? new PdfWriter(outputPath)
                        : new PdfWriter(outputPath, encryptionProperties);
                    using var destination = new PdfDocument(writer);
                    source.CopyPagesTo(start, end, destination);
                    count++;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Split error: {exception.Message}");
            }

            return count;
        });
    }

    public Task<bool> ExportPagesAsync(
        byte[] pdfBytes,
        string outputPath,
        IEnumerable<int> pageIndices,
        WriterProperties? encryptionProperties = null)
    {
        List<int> indices = pageIndices
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        if (indices.Count == 0)
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            try
            {
                using var input = new MemoryStream(pdfBytes);
                using var reader = new PdfReader(input);
                using var source = new PdfDocument(reader);
                using var writer = encryptionProperties == null
                    ? new PdfWriter(outputPath)
                    : new PdfWriter(outputPath, encryptionProperties);
                using var destination = new PdfDocument(writer);

                foreach (int pageIndex in indices)
                {
                    if (pageIndex >= 0 && pageIndex < source.GetNumberOfPages())
                        source.CopyPagesTo(pageIndex + 1, pageIndex + 1, destination);
                }

                return destination.GetNumberOfPages() > 0;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Export pages error: {exception.Message}");
                return false;
            }
        });
    }
}
