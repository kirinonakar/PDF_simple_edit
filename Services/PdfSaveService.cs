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
        byte[] editedBytes = manager.CreatePdfBytesWithEdits(document =>
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

        await WriteAtomicallyAsync(filePath, editedBytes);

        manager.ReplacePdfBytesAfterSave(editedBytes, filePath);
        foreach (PdfAnnotation annotation in annotations.Where(annotation => annotation.Type == AnnotationType.Signature))
            annotation.IsApplied = true;
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
