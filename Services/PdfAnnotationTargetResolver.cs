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
                    : FindMatchingTextFragments(pageContents, annotation);
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

    private static IReadOnlyList<PdfTextFragment> FindMatchingTextFragments(
        IEnumerable<PdfPageContent> pageContents,
        PdfAnnotation annotation)
    {
        string expectedText = NormalizeComparableText(
            string.IsNullOrEmpty(annotation.OriginalText)
                ? annotation.Content
                : annotation.OriginalText);
        if (expectedText.Length == 0)
            return Array.Empty<PdfTextFragment>();

        foreach (PdfPageContent content in pageContents
            .Where(content =>
                content.Type == PageContentType.Text &&
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
                Math.Abs(content.Y - annotation.Y)))
        {
            if (string.Equals(
                NormalizeComparableText(content.Text),
                expectedText,
                StringComparison.Ordinal))
            {
                return content.TextFragments;
            }

            // Extracted page regions may merge adjacent PDF operations. Text
            // written by the editor is still represented by a contiguous group
            // of fragments, so match only that group instead of deleting the
            // neighboring text along with it.
            List<PdfTextFragment> orderedFragments = content.TextFragments
                .OrderBy(fragment => fragment.LineIndex)
                .ThenBy(fragment => fragment.X)
                .ToList();
            IReadOnlyList<PdfTextFragment> match = FindContiguousFragmentMatch(
                orderedFragments,
                expectedText,
                useExtractedLineIndexes: true,
                annotation);
            if (match.Count > 0)
                return match;
        }

        // The writer can split one edited text box into multiple extracted
        // regions. This happens when the final line uses a different font or a
        // newly typed glyph such as '!'. Reassemble only regions whose text is
        // the next exact prefix of the saved annotation; unrelated text in a
        // nearby column is skipped even when its bounds overlap.
        IReadOnlyList<PdfTextFragment> regionSequence =
            FindMatchingTextRegionSequence(pageContents, annotation, expectedText);
        if (regionSequence.Count > 0)
            return regionSequence;

        // Font fallback can split a single editor-written string into separate
        // extracted regions (for example Korean text and a newly typed '!').
        // Search nearby fragments across region boundaries as a final precise
        // match, while still returning only the operations that form the text.
        List<PdfTextFragment> nearbyFragments = pageContents
            .Where(content => content.Type == PageContentType.Text)
            .SelectMany(content => content.TextFragments)
            .Where(fragment =>
                Math.Abs(annotation.Y - fragment.Y) <=
                    Math.Max(2, Math.Max(annotation.Height, fragment.Height) * 0.5) &&
                fragment.X + fragment.Width >=
                    annotation.X - Math.Max(2, annotation.FontSize * 0.5))
            .OrderBy(fragment => fragment.Y)
            .ThenBy(fragment => fragment.X)
            .ToList();
        return FindContiguousFragmentMatch(
            nearbyFragments,
            expectedText,
            useExtractedLineIndexes: false,
            annotation);
    }

    private static IReadOnlyList<PdfTextFragment> FindMatchingTextRegionSequence(
        IEnumerable<PdfPageContent> pageContents,
        PdfAnnotation annotation,
        string expectedText)
    {
        List<PdfPageContent> textRegions = pageContents
            .Where(content =>
                content.Type == PageContentType.Text &&
                content.TextFragments.Count > 0 &&
                IsWithinExpandedTextBounds(annotation, content))
            .ToList();

        double startHorizontalTolerance = Math.Max(
            2,
            Math.Max(annotation.FontSize * 2, annotation.Width * 0.1));
        double startVerticalTolerance = Math.Max(
            2,
            Math.Max(annotation.FontSize, annotation.Height * 0.1));
        foreach (PdfPageContent start in textRegions
            .Where(content =>
                Math.Abs(content.X - annotation.X) <= startHorizontalTolerance &&
                Math.Abs(content.Y - annotation.Y) <= startVerticalTolerance)
            .OrderBy(content =>
                Math.Abs(content.X - annotation.X) +
                Math.Abs(content.Y - annotation.Y)))
        {
            string combinedText = start.Text;
            string normalizedCombined = NormalizeComparableText(combinedText);
            if (!expectedText.StartsWith(normalizedCombined, StringComparison.Ordinal))
                continue;

            var selectedRegions = new List<PdfPageContent> { start };
            var unusedRegions = textRegions.Where(content => content != start).ToList();
            while (!string.Equals(normalizedCombined, expectedText, StringComparison.Ordinal))
            {
                PdfPageContent? next = null;
                string? nextCombinedText = null;
                int longestMatch = -1;
                foreach (PdfPageContent candidate in unusedRegions)
                {
                    if (!IsFollowingTextRegion(
                        selectedRegions[^1],
                        candidate,
                        annotation.FontSize))
                    {
                        continue;
                    }

                    foreach (string separator in new[] { string.Empty, "\n" })
                    {
                        string candidateCombined = combinedText + separator + candidate.Text;
                        string normalizedCandidate = NormalizeComparableText(candidateCombined);
                        if (!expectedText.StartsWith(normalizedCandidate, StringComparison.Ordinal) ||
                            normalizedCandidate.Length <= longestMatch)
                        {
                            continue;
                        }

                        next = candidate;
                        nextCombinedText = candidateCombined;
                        longestMatch = normalizedCandidate.Length;
                    }
                }

                if (next == null || nextCombinedText == null)
                    break;

                selectedRegions.Add(next);
                unusedRegions.Remove(next);
                combinedText = nextCombinedText;
                normalizedCombined = NormalizeComparableText(combinedText);
            }

            if (string.Equals(normalizedCombined, expectedText, StringComparison.Ordinal))
            {
                return selectedRegions
                    .SelectMany(content => content.TextFragments)
                    .ToList();
            }
        }

        return Array.Empty<PdfTextFragment>();
    }

    private static bool IsFollowingTextRegion(
        PdfPageContent previous,
        PdfPageContent candidate,
        double fontSize)
    {
        double lineTolerance = Math.Max(2, fontSize * 0.5);
        if (candidate.Y < previous.Y - lineTolerance)
            return false;

        return Math.Abs(candidate.Y - previous.Y) > lineTolerance ||
            candidate.X >= previous.X - lineTolerance;
    }

    private static bool IsWithinExpandedTextBounds(
        PdfAnnotation annotation,
        PdfPageContent content)
    {
        double horizontalTolerance = Math.Max(
            annotation.FontSize * 3,
            annotation.Width * 0.2);
        double verticalTolerance = Math.Max(
            annotation.FontSize * 2,
            annotation.Height * 0.25);
        double annotationRight = annotation.X + annotation.Width;
        double annotationBottom = annotation.Y + annotation.Height;
        double contentRight = content.X + content.Width;
        double contentBottom = content.Y + content.Height;
        return content.X <= annotationRight + horizontalTolerance &&
            contentRight >= annotation.X - horizontalTolerance &&
            content.Y <= annotationBottom + verticalTolerance &&
            contentBottom >= annotation.Y - verticalTolerance;
    }

    private static IReadOnlyList<PdfTextFragment> FindContiguousFragmentMatch(
        List<PdfTextFragment> fragments,
        string expectedText,
        bool useExtractedLineIndexes,
        PdfAnnotation annotation)
    {
        for (int start = 0; start < fragments.Count; start++)
        {
            PdfTextFragment first = fragments[start];
            double startHorizontalTolerance = Math.Max(
                2,
                Math.Max(first.Width, annotation.FontSize));
            double startVerticalTolerance = Math.Max(
                2,
                Math.Max(first.Height, annotation.Height) * 0.5);
            if (Math.Abs(first.X - annotation.X) > startHorizontalTolerance ||
                Math.Abs(first.Y - annotation.Y) > startVerticalTolerance)
            {
                continue;
            }

            string candidateText = string.Empty;
            PdfTextFragment previous = first;
            for (int end = start; end < fragments.Count; end++)
            {
                PdfTextFragment fragment = fragments[end];
                if (end > start)
                {
                    bool startsNewLine = useExtractedLineIndexes
                        ? fragment.LineIndex != previous.LineIndex
                        : Math.Abs(fragment.Y - previous.Y) >
                            Math.Max(2, Math.Max(fragment.Height, previous.Height) * 0.5);
                    if (startsNewLine)
                        candidateText += "\n";
                }
                candidateText += fragment.Text;
                previous = fragment;

                string normalizedCandidate = NormalizeComparableText(candidateText);
                if (string.Equals(normalizedCandidate, expectedText, StringComparison.Ordinal))
                    return fragments.GetRange(start, end - start + 1);
                if (normalizedCandidate.Length > expectedText.Length)
                    break;
            }
        }

        return Array.Empty<PdfTextFragment>();
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
