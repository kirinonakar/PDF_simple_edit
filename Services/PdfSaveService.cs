using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed class PdfSaveService
{
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
        if (isUserSave)
        {
            manager.ApplyBatchEdit(document =>
            {
                foreach (PdfAnnotation annotation in annotations)
                {
                    _annotationDocumentService.Apply(manager, document, annotation);
                    annotation.IsApplied = true;
                }
            });

            if (!await manager.SaveAsAsync(filePath, true))
                throw new InvalidOperationException("저장에 실패했습니다.");

            annotations.Clear();
            await manager.OpenAsync(filePath);
            return;
        }

        byte[]? temporaryBytes = manager.GetPdfBytesWithEdits(document =>
        {
            foreach (PdfAnnotation annotation in annotations)
                _annotationDocumentService.Apply(manager, document, annotation);
        });

        if (temporaryBytes != null)
        {
            await File.WriteAllBytesAsync(filePath, temporaryBytes);
        }
        else
        {
            await manager.SaveAsAsync(filePath, false);
        }
    }
}
