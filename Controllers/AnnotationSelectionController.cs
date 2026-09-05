using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Controllers;

public sealed record AnnotationSelectionResult(
    PdfAnnotation? PrimarySelection,
    bool SelectionChanged,
    PdfAnnotation? AddedFromPageContent);

public sealed class AnnotationSelectionController
{
    private readonly AnnotationContentService _contentService;

    public AnnotationSelectionController(AnnotationContentService contentService)
    {
        _contentService = contentService;
    }

    public async Task<AnnotationSelectionResult> SelectAtAsync(
        PdfDocumentManager manager,
        List<PdfAnnotation> annotations,
        List<PdfAnnotation> selectedAnnotations,
        PdfAnnotation? primarySelection,
        int pageIndex,
        double x,
        double y,
        bool controlPressed,
        Action analysisStarted)
    {
        PdfAnnotation? found = _contentService.FindAnnotationAt(
            annotations, pageIndex, x, y);
        PdfAnnotation? addedFromPageContent = null;
        if (found == null || found.IsOriginalImageReplacement)
        {
            analysisStarted();
            List<PdfPageContent> pageContents = await manager.ExtractPageContentsAsync(pageIndex);
            PdfPageContent? content = _contentService.FindEditableContent(pageContents, x, y);
            if (_contentService.ShouldPreferPageContent(found, content) && content != null)
            {
                PdfAnnotation? converted = _contentService.ConvertToAnnotation(content, pageIndex);
                if (converted != null)
                {
                    found = converted;
                    annotations.Add(converted);
                    addedFromPageContent = converted;
                }
            }
        }

        if (found == null)
        {
            if (controlPressed)
                return new AnnotationSelectionResult(primarySelection, false, null);
            bool changed = selectedAnnotations.Count > 0 || primarySelection != null;
            selectedAnnotations.Clear();
            return new AnnotationSelectionResult(null, changed, null);
        }

        if (!controlPressed)
        {
            selectedAnnotations.Clear();
            selectedAnnotations.Add(found);
            return new AnnotationSelectionResult(found, true, addedFromPageContent);
        }

        if (selectedAnnotations.Contains(found))
        {
            selectedAnnotations.Remove(found);
            PdfAnnotation? nextPrimary = primarySelection == found
                ? selectedAnnotations.LastOrDefault()
                : primarySelection;
            return new AnnotationSelectionResult(nextPrimary, true, addedFromPageContent);
        }

        selectedAnnotations.Add(found);
        return new AnnotationSelectionResult(found, true, addedFromPageContent);
    }
}
