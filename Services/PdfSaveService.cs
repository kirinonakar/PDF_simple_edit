using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed class PdfSaveService
{
    private const int FileReplaceAttempts = 5;
    private readonly PdfAnnotationDocumentService _annotationDocumentService;

    public PdfSaveService(PdfAnnotationDocumentService annotationDocumentService)
    {
        _annotationDocumentService = annotationDocumentService;
    }

    public async Task SaveAsync(
        PdfDocumentManager manager,
        List<PdfAnnotation> annotations,
        string filePath,
        bool isUserSave)
    {
        bool needsAnnotationWrite = annotations.Any(a => a.NativeText == null &&
            (!a.IsApplied && !a.IsOriginalTextReplacement || a.Type == AnnotationType.Signature));
        byte[] editedBytes = !needsAnnotationWrite
            ? manager.GetPdfBytes() ?? throw new InvalidOperationException("열린 PDF 문서가 없습니다.")
            : manager.CreatePdfBytesWithEdits(document =>
        {
            manager.RemoveSavedSignatureAnnotations(document);
            foreach (PdfAnnotation annotation in annotations)
                _annotationDocumentService.Apply(manager, document, annotation);
        });

        if (!isUserSave)
        {
            await File.WriteAllBytesAsync(filePath, editedBytes);
            return;
        }

        // 메모리의 문서는 항상 평문이므로, 사용자 저장일 때만 원래의 보호 설정을
        // 적용한 바이트를 디스크에 기록한다. (인쇄용 임시 파일 등은 평문 유지)
        await WriteAtomicallyAsync(filePath, manager.ProtectBytesForSave(editedBytes));

        manager.ReplacePdfBytesAfterSave(editedBytes, filePath);
        foreach (var pageGroup in annotations.Where(a => a.Type == AnnotationType.Image && !a.IsOriginalImageReplacement).GroupBy(a => a.PageIndex))
        {
            var contents = await manager.ExtractPageContentsAsync(pageGroup.Key);
            var used = new System.Collections.Generic.HashSet<PdfPageContent>();
            foreach (var annotation in pageGroup)
            {
                var box = new PdfTextBox(annotation.X, annotation.Y, annotation.Width, annotation.Height);
                var expected = PdfAffineTransform.Between(box, box, annotation.Rotation).Map(box);
                var match = contents.Where(c => c.Type == PageContentType.Image && !used.Contains(c))
                    .OrderBy(c => Math.Abs(c.X - expected.X) + Math.Abs(c.Y - expected.Y) +
                        Math.Abs(c.Width - expected.Width) + Math.Abs(c.Height - expected.Height)).FirstOrDefault();
                annotation.IsApplied = true;
                if (match == null) continue;
                used.Add(match);
                annotation.X = match.X; annotation.Y = match.Y; annotation.Width = match.Width; annotation.Height = match.Height;
                annotation.Rotation = 0; annotation.IsOriginalImageReplacement = true;
                annotation.OriginalImageName = match.ImageId; annotation.OriginalPdfX = match.OriginalPdfX; annotation.OriginalPdfY = match.OriginalPdfY;
                annotation.GraphicCtm = match.GraphicCtm;
                annotation.ContentStreamIndex = match.ContentStreamIndex; annotation.ContentStreamObjectNumber = match.ContentStreamObjectNumber;
                annotation.OperationIndex = match.OperationIndex;
            }
        }
        foreach (var annotation in annotations.Where(a => a.NativeText == null && !a.IsApplied &&
            !a.IsOriginalTextReplacement && a.Type is AnnotationType.Text or AnnotationType.FreeText && Math.Abs(a.Rotation) > .0001))
        {
            var textService = new NativePdfTextService();
            var box = new PdfTextBox(annotation.X, annotation.Y, annotation.Width, annotation.Height);
            var expected = PdfAffineTransform.Between(box, box, annotation.Rotation).Map(box);
            var blocks = NativePdfTextService.SelectRegion(textService.Extract(editedBytes, annotation.PageIndex),
                new(expected.X - 2, expected.Y - 2, expected.Width + 4, expected.Height + 4));
            if (blocks.Count == 0) continue;
            var selection = new NativePdfTextBlock
            {
                Text = annotation.Content,
                Runs = blocks.SelectMany(b => b.Runs).DistinctBy(r => r.Id).ToList(),
                Glyphs = blocks.SelectMany(b => b.Glyphs).ToList(),
                Lines = blocks.SelectMany(b => b.Lines).ToList(),
                Bounds = PdfTextBox.Union(blocks.Select(b => b.Bounds)),
                LineHeight = annotation.LineHeight
            };
            NativePdfTextService.UpdateAnnotation(annotation, selection);
            annotation.Rotation = 0;
        }
        foreach (PdfAnnotation annotation in annotations.Where(annotation => annotation.NativeText == null &&
            annotation.Type is AnnotationType.Text or AnnotationType.FreeText))
        {
            annotation.IsApplied = true;
        }
        foreach (PdfAnnotation annotation in annotations.Where(annotation => annotation.Type == AnnotationType.Signature))
            annotation.IsApplied = true;

        // Keep the just-saved Ink annotations in the file, but remove them from
        // the in-memory render source. The overlay renders the editor copies;
        // otherwise a moved signature would appear twice until the next save.
        manager.DetachSavedSignatureAnnotationsForEditing();
    }

    private static async Task WriteAtomicallyAsync(string filePath, byte[] contents)
    {
        string fullPath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"저장할 폴더를 찾을 수 없습니다: {directory}");

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents);

            for (int attempt = 1; attempt <= FileReplaceAttempts; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, fullPath, true);
                    return;
                }
                catch (IOException) when (attempt < FileReplaceAttempts)
                {
                    await Task.Delay(attempt * 100);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"파일에 쓸 권한이 없습니다: {fullPath}", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }
}
