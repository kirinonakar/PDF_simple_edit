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
        if (sourceFiles.Count == 0) throw new ArgumentException("합칠 PDF 파일을 선택해 주세요.");
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
                catch (Exception ex) { throw new InvalidDataException($"'{Path.GetFileName(filePath)}' 파일을 합칠 수 없습니다: {ex.Message}", ex); }
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
        if (!Directory.Exists(outputFolder)) throw new DirectoryNotFoundException("출력 폴더를 찾을 수 없습니다.");
        if (ranges.Count == 0) throw new ArgumentException("나눌 페이지 범위를 입력해 주세요.");
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
                if (Path.Exists(output)) throw new IOException($"파일이 이미 있습니다. 다른 폴더를 선택해 주세요: {output}");
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
            throw new IOException($"PDF 나누기에 실패했습니다: {ex.Message}" +
                (remaining.Length == 0 ? "" : $"\n삭제하지 못한 출력: {string.Join(", ", remaining)}"), ex);
        }
        finally { foreach (var item in staged) PdfTemporaryFiles.TryDelete(item.Temp); }
    });

    public async Task<bool> ExportPagesAsync(byte[] pdfBytes, string outputPath, IEnumerable<int> pageIndices,
        WriterProperties? encryptionProperties = null)
    {
        int[] indices = pageIndices.Distinct().Order().ToArray();
        if (indices.Length == 0) throw new ArgumentException("추출할 페이지를 선택해 주세요.");
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
            throw new ArgumentException($"페이지 범위 {start}-{end}가 올바르지 않습니다. 1~{pageCount} 사이의 오름차순 범위를 입력해 주세요.");
    }
}
