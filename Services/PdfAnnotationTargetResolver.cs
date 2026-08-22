using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

internal sealed class PdfAnnotationTargetResolver
{
    public List<PdfAnnotation>? ResolveImages(
        IReadOnlyCollection<PdfAnnotation> annotations,
        IReadOnlyList<PdfPageContent> pageContents)
    {
        if (annotations.Any(annotation => !annotation.IsOriginalImageReplacement))
            return null;

        List<PdfPageContent> availableImages = pageContents
            .Where(content => content.Type == PageContentType.Image)
            .ToList();
        var usedImages = new HashSet<PdfPageContent>();
        var resolvedAnnotations = new List<PdfAnnotation>();

        foreach (PdfAnnotation annotation in annotations)
        {
            PdfPageContent? match = availableImages
                .Where(content =>
                    !usedImages.Contains(content) &&
                    IsSameImagePosition(annotation, content))
                .OrderByDescending(content => HasSameExtractedImage(annotation, content))
                .ThenByDescending(content => string.Equals(
                    annotation.OriginalImageName,
                    content.ImageId,
                    StringComparison.Ordinal))
                .ThenBy(content =>
                    Math.Abs(annotation.OriginalPdfX - content.OriginalPdfX) +
                    Math.Abs(annotation.OriginalPdfY - content.OriginalPdfY))
                .FirstOrDefault();
            if (match == null ||
                match.OperationIndex < 0 ||
                (match.ContentStreamObjectNumber <= 0 && match.ContentStreamIndex < 0))
            {
                return null;
            }

            usedImages.Add(match);
            PdfAnnotation resolved = annotation.Clone();
            resolved.ContentStreamIndex = match.ContentStreamIndex;
            resolved.ContentStreamObjectNumber = match.ContentStreamObjectNumber;
            resolved.OperationIndex = match.OperationIndex;
            resolved.OriginalImageName = match.ImageId;
            resolved.GraphicOperationIndexes = new List<int>(match.GraphicOperationIndexes);
            resolved.GraphicTextOperationIndexes =
                new List<int>(match.GraphicTextOperationIndexes);
            resolved.GraphicOperations = match.GraphicOperations
                .Select(target => target.Clone())
                .ToList();
            resolvedAnnotations.Add(resolved);
        }

        return resolvedAnnotations;
    }

    public List<PdfAnnotation>? ResolveText(
        IReadOnlyCollection<PdfAnnotation> annotations,
        IReadOnlyList<PdfPageContent> pageContents)
    {
        List<PdfTextFragment> availableFragments = pageContents
            .Where(content => content.Type == PageContentType.Text)
            .SelectMany(content => content.TextFragments)
            .ToList();
        var usedFragments = new HashSet<PdfTextFragment>();
        var resolvedAnnotations = new List<PdfAnnotation>();

        foreach (PdfAnnotation annotation in annotations)
        {
            PdfAnnotation resolved = annotation.Clone();
            IReadOnlyList<PdfTextFragment> sourceFragments =
                annotation.TextFragments.Count > 0
                    ? annotation.TextFragments
                    : (IReadOnlyList<PdfTextFragment>?)FindMatchingTextRegion(
                        pageContents,
                        annotation)?.TextFragments ?? Array.Empty<PdfTextFragment>();
            if (sourceFragments.Count == 0)
                return null;

            var matchedFragments = new List<PdfTextFragment>();
            foreach (PdfTextFragment source in sourceFragments)
            {
                PdfTextFragment? match = availableFragments
                    .Where(candidate =>
                        !usedFragments.Contains(candidate) &&
                        string.Equals(candidate.Text, source.Text, StringComparison.Ordinal) &&
                        IsSameTextPosition(source, candidate))
                    .OrderBy(candidate =>
                        Math.Abs(candidate.X - source.X) +
                        Math.Abs(candidate.Y - source.Y))
                    .FirstOrDefault();
                if (match == null)
                    return null;

                usedFragments.Add(match);
                matchedFragments.Add(match.Clone());
            }

            resolved.TextFragments = matchedFragments;
            PdfTextFragment first = matchedFragments[0];
            resolved.ContentStreamIndex = first.ContentStreamIndex;
            resolved.ContentStreamObjectNumber = first.ContentStreamObjectNumber;
            resolved.OperationIndex = first.OperationIndex;
            resolved.TextRenderMode = first.TextRenderMode;
            resolvedAnnotations.Add(resolved);
        }

        return resolvedAnnotations;
    }

    private static bool IsSameImagePosition(
        PdfAnnotation annotation,
        PdfPageContent content)
    {
        double horizontalTolerance = Math.Max(2, content.Width * 0.05);
        double verticalTolerance = Math.Max(2, content.Height * 0.05);
        return Math.Abs(annotation.OriginalPdfX - content.OriginalPdfX) <= horizontalTolerance &&
            Math.Abs(annotation.OriginalPdfY - content.OriginalPdfY) <= verticalTolerance;
    }

    private static bool HasSameExtractedImage(
        PdfAnnotation annotation,
        PdfPageContent content)
    {
        if (string.IsNullOrEmpty(annotation.ImagePath) || string.IsNullOrEmpty(content.Text))
            return false;

        return string.Equals(
            Path.GetFileName(annotation.ImagePath),
            Path.GetFileName(content.Text),
            StringComparison.OrdinalIgnoreCase);
    }

    private static PdfPageContent? FindMatchingTextRegion(
        IEnumerable<PdfPageContent> pageContents,
        PdfAnnotation annotation)
    {
        string expectedText = NormalizeComparableText(
            string.IsNullOrEmpty(annotation.OriginalText)
                ? annotation.Content
                : annotation.OriginalText);
        return pageContents
            .Where(content =>
                content.Type == PageContentType.Text &&
                string.Equals(
                    NormalizeComparableText(content.Text),
                    expectedText,
                    StringComparison.Ordinal) &&
                IsSameTextPosition(
                    annotation.X,
                    annotation.Y,
                    annotation.Width,
                    annotation.Height,
                    content.X,
                    content.Y,
                    content.Width,
                    content.Height))
            .OrderBy(content =>
                Math.Abs(content.X - annotation.X) +
                Math.Abs(content.Y - annotation.Y))
            .FirstOrDefault();
    }

    private static bool IsSameTextPosition(
        PdfTextFragment first,
        PdfTextFragment second) =>
        IsSameTextPosition(
            first.X,
            first.Y,
            first.Width,
            first.Height,
            second.X,
            second.Y,
            second.Width,
            second.Height);

    private static bool IsSameTextPosition(
        double firstX,
        double firstY,
        double firstWidth,
        double firstHeight,
        double secondX,
        double secondY,
        double secondWidth,
        double secondHeight)
    {
        double horizontalTolerance = Math.Max(2, Math.Max(firstWidth, secondWidth) * 0.75);
        double verticalTolerance = Math.Max(2, Math.Max(firstHeight, secondHeight) * 0.5);
        return Math.Abs(firstX - secondX) <= horizontalTolerance &&
            Math.Abs(firstY - secondY) <= verticalTolerance;
    }

    private static string NormalizeComparableText(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
