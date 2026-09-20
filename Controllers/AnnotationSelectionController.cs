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

    public List<PdfAnnotation> SelectGraphicsInRegion(
        List<PdfAnnotation> annotations, IReadOnlyList<PdfPageContent> contents,
        int pageIndex, PdfTextBox region)
    {
        bool Contains(double x, double y, double width, double height) =>
            region.Contains(x, y, 2) && region.Contains(x + width, y + height, 2);
        foreach (var content in contents.Where(content => content.Type == PageContentType.Image &&
            Contains(content.X, content.Y, content.Width, content.Height)))
        {
            if (annotations.Any(a => a.PageIndex == pageIndex && a.IsOriginalImageReplacement &&
                a.OriginalImageName == content.ImageId)) continue;
            annotations.Add(_contentService.ConvertToAnnotation(content, pageIndex));
        }
        return annotations.Where(a => a.PageIndex == pageIndex &&
            AnnotationContentService.MatchesSelectionMode(a, EditToolMode.SelectGraphics) &&
            Contains(a.X, a.Y, a.Width, a.Height)).ToList();
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
        Action analysisStarted,
        EditToolMode selectionMode = EditToolMode.Select,
        Func<bool>? isCurrent = null)
    {
        PdfAnnotation? found = _contentService.FindAnnotationAt(
            annotations, pageIndex, x, y, selectionMode);
        PdfAnnotation? addedFromPageContent = null;
        if (found == null || found.IsOriginalImageReplacement)
        {
            analysisStarted();
            List<PdfPageContent> pageContents = await manager.ExtractPageContentsAsync(pageIndex);
            if (isCurrent?.Invoke() == false) return new(primarySelection, false, null);
            PdfPageContent? content = _contentService.FindEditableContent(pageContents, x, y, selectionMode);
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
