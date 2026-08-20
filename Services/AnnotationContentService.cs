using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

public sealed class AnnotationContentService
{
    public PdfAnnotation? FindAnnotationAt(
        IReadOnlyList<PdfAnnotation> annotations,
        int pageIndex,
        double x,
        double y)
    {
        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            PdfAnnotation annotation = annotations[i];
            if (annotation.PageIndex != pageIndex)
                continue;

            if (annotation.Type is AnnotationType.Text or AnnotationType.FreeText)
            {
                double width = annotation.Width > 0
                    ? annotation.Width
                    : (annotation.Content.Length * annotation.FontSize * 0.8) + 10;
                double height = annotation.Height > 0 ? annotation.Height : annotation.FontSize * 1.4;
                if (x >= annotation.X - 5 && x <= annotation.X + width &&
                    y >= annotation.Y - 5 && y <= annotation.Y + height)
                    return annotation;
            }
            else if (annotation.Type == AnnotationType.Signature)
            {
                double tolerance = Math.Max(annotation.LineWidth * 2, 8);
                if (x >= annotation.X - tolerance &&
                    x <= annotation.X + Math.Max(annotation.Width, 1) + tolerance &&
                    y >= annotation.Y - tolerance &&
                    y <= annotation.Y + Math.Max(annotation.Height, 1) + tolerance)
                    return annotation;
            }
            else if (x >= annotation.X && x <= annotation.X + annotation.Width &&
                     y >= annotation.Y && y <= annotation.Y + annotation.Height)
            {
                return annotation;
            }
        }

        return null;
    }

    public PdfPageContent? FindEditableContent(List<PdfPageContent> contents, double x, double y)
    {
        int matchIndex = contents.FindIndex(content =>
            x >= content.X - 5 && x <= content.X + content.Width + 5 &&
            y >= content.Y - 5 && y <= content.Y + content.Height + 5);
        if (matchIndex < 0)
            return null;

        PdfPageContent match = contents[matchIndex];
        if (match.Type != PageContentType.Text)
            return match;

        PdfPageContent expanded = Clone(match);
        int startIndex = matchIndex;
        while (startIndex > 0 && CanJoin(contents[startIndex - 1], expanded, out bool prependNewLine))
        {
            expanded = Merge(contents[startIndex - 1], expanded, prependNewLine);
            startIndex--;
        }

        int nextIndex = matchIndex + 1;
        while (nextIndex < contents.Count && CanJoin(expanded, contents[nextIndex], out bool appendNewLine))
        {
            expanded = Merge(expanded, contents[nextIndex], appendNewLine);
            nextIndex++;
        }

        return expanded;
    }

    public PdfAnnotation ConvertToAnnotation(PdfPageContent content, int pageIndex)
    {
        bool isText = content.Type == PageContentType.Text;
        string editableText = isText ? BuildEditableText(content) : content.Text ?? string.Empty;
        return new PdfAnnotation
        {
            Type = isText ? AnnotationType.Text : AnnotationType.Image,
            PageIndex = pageIndex,
            X = content.X,
            Y = content.Y,
            Content = editableText,
            Width = content.Width,
            Height = content.Height,
            IsOriginalTextReplacement = isText,
            IsOriginalImageReplacement = !isText,
            OriginalPdfX = content.OriginalPdfX,
            OriginalPdfY = content.OriginalPdfY,
            OriginalText = editableText,
            OriginalImageName = isText ? null : content.ImageId,
            GraphicOperationIndexes = new List<int>(content.GraphicOperationIndexes),
            GraphicTextOperationIndexes = new List<int>(content.GraphicTextOperationIndexes),
            GraphicOperations = content.GraphicOperations.Select(target => target.Clone()).ToList(),
            ImagePath = isText || string.IsNullOrEmpty(content.Text) ? null : content.Text,
            OperatorId = content.OperatorId,
            ContentStreamIndex = content.ContentStreamIndex,
            ContentStreamObjectNumber = content.ContentStreamObjectNumber,
            OperationIndex = content.OperationIndex,
            TextRenderMode = content.TextRenderMode,
            LineHeight = content.LineHeight,
            BaselineOffset = content.BaselineOffset,
            OriginalFontObjectNumber = content.OriginalFontObjectNumber,
            TextFragments = content.TextFragments.Select(fragment => fragment.Clone()).ToList(),
            FontSize = isText && content.FontSize > 0
                ? content.FontSize
                : content.Height > 0 ? content.Height : 12,
            FontFamily = isText && !string.IsNullOrEmpty(content.FontFamily) ? content.FontFamily : "맑은 고딕",
            Color = content.Color,
            FontWeight = isText ? content.FontWeight : 400,
            IsBold = isText && content.IsBold,
            IsItalic = isText && content.IsItalic,
            IsApplied = false
        };
    }

    public string BuildEditableText(PdfAnnotation annotation) => BuildEditableText(new PdfPageContent
    {
        Text = annotation.Content,
        FontSize = annotation.FontSize,
        TextFragments = annotation.TextFragments
    });

    private static PdfPageContent Clone(PdfPageContent source) => new()
    {
        Type = source.Type,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height,
        Text = source.Text,
        OriginalPdfX = source.OriginalPdfX,
        OriginalPdfY = source.OriginalPdfY,
        OperatorId = source.OperatorId,
        FontSize = source.FontSize,
        FontFamily = source.FontFamily,
        Color = source.Color,
        FontWeight = source.FontWeight,
        IsBold = source.IsBold,
        IsItalic = source.IsItalic,
        ContentStreamIndex = source.ContentStreamIndex,
        ContentStreamObjectNumber = source.ContentStreamObjectNumber,
        OperationIndex = source.OperationIndex,
        TextRenderMode = source.TextRenderMode,
        LineHeight = source.LineHeight,
        BaselineOffset = source.BaselineOffset,
        OriginalFontObjectNumber = source.OriginalFontObjectNumber,
        TextFragments = source.TextFragments.Select(fragment => fragment.Clone()).ToList(),
        ImageId = source.ImageId,
        GraphicOperationIndexes = new List<int>(source.GraphicOperationIndexes),
        GraphicTextOperationIndexes = new List<int>(source.GraphicTextOperationIndexes),
        GraphicOperations = source.GraphicOperations.Select(target => target.Clone()).ToList()
    };

    private static bool CanJoin(PdfPageContent first, PdfPageContent second, out bool startsNewLine)
    {
        startsNewLine = false;
        if (first.Type != PageContentType.Text || second.Type != PageContentType.Text ||
            first.TextFragments.Count == 0 || second.TextFragments.Count == 0)
            return false;

        double referenceSize = Math.Max(first.FontSize, 1);
        if (!string.Equals(first.FontFamily, second.FontFamily, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(first.FontSize - second.FontSize) > Math.Max(0.75, referenceSize * 0.2) ||
            !string.Equals(first.Color, second.Color, StringComparison.OrdinalIgnoreCase) ||
            first.FontWeight != second.FontWeight ||
            first.IsBold != second.IsBold || first.IsItalic != second.IsItalic)
            return false;

        PdfTextFragment previous = first.TextFragments[^1];
        PdfTextFragment current = second.TextFragments[0];
        double fontSize = Math.Max(Math.Max(previous.FontSize, current.FontSize), 1);
        if (Math.Abs(previous.Y - current.Y) <= Math.Max(1.25, fontSize * 0.4))
        {
            double gap = current.X - (previous.X + previous.Width);
            return gap >= -fontSize && gap <= fontSize * 2.75;
        }

        if (current.Y <= previous.Y || current.Y - previous.Y > Math.Max(fontSize * 1.8, 16))
            return false;

        double overlap = Math.Min(first.X + first.Width, current.X + current.Width) - Math.Max(first.X, current.X);
        double minWidth = Math.Max(Math.Min(first.Width, current.Width), 1);
        startsNewLine = overlap / minWidth >= 0.25 ||
            Math.Abs(current.X - first.X) <= Math.Max(24, fontSize * 2.5);
        return startsNewLine;
    }

    private static PdfPageContent Merge(PdfPageContent first, PdfPageContent second, bool startsNewLine)
    {
        PdfPageContent merged = Clone(first);
        PdfTextFragment previous = merged.TextFragments[^1];
        PdfTextFragment current = second.TextFragments[0];

        if (startsNewLine)
        {
            merged.Text = NormalizeLineEndings(merged.Text) + "\r\n" + NormalizeLineEndings(second.Text);
            double detectedLineHeight = Math.Abs(current.Y - previous.Y);
            if (detectedLineHeight > 0.1)
                merged.LineHeight = merged.LineHeight > 0.1
                    ? (merged.LineHeight + detectedLineHeight) / 2.0
                    : detectedLineHeight;
        }
        else
        {
            double gap = current.X - (previous.X + previous.Width);
            bool needsSpace = gap > Math.Max(previous.FontSize, current.FontSize) * 0.15 &&
                !merged.Text.EndsWith(" ", StringComparison.Ordinal) &&
                !second.Text.StartsWith(" ", StringComparison.Ordinal);
            merged.Text += (needsSpace ? " " : string.Empty) + second.Text;
        }

        int sourceLine = second.TextFragments.Min(fragment => fragment.LineIndex);
        int targetLine = merged.TextFragments.Max(fragment => fragment.LineIndex) + (startsNewLine ? 1 : 0);
        foreach (PdfTextFragment fragment in second.TextFragments.Select(fragment => fragment.Clone()))
        {
            fragment.LineIndex += targetLine - sourceLine;
            merged.TextFragments.Add(fragment);
        }

        double left = Math.Min(merged.X, second.X);
        double top = Math.Min(merged.Y, second.Y);
        double right = Math.Max(merged.X + merged.Width, second.X + second.Width);
        double bottom = Math.Max(merged.Y + merged.Height, second.Y + second.Height);
        merged.X = left;
        merged.Y = top;
        merged.Width = right - left;
        merged.Height = bottom - top;
        return merged;
    }

    private static string BuildEditableText(PdfPageContent content)
    {
        string fallback = NormalizeLineEndings(content.Text ?? string.Empty);
        if (content.TextFragments.Count < 2)
            return fallback;

        var lines = content.TextFragments
            .GroupBy(fragment => fragment.LineIndex)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(fragment => fragment.X).ToList())
            .ToList();
        if (lines.Count == 1)
        {
            lines = new List<List<PdfTextFragment>>();
            foreach (PdfTextFragment fragment in content.TextFragments.OrderBy(fragment => fragment.Y).ThenBy(fragment => fragment.X))
            {
                List<PdfTextFragment>? line = lines.LastOrDefault();
                double tolerance = Math.Max(1.25, Math.Max(fragment.FontSize, content.FontSize) * 0.35);
                if (line == null || Math.Abs(fragment.Y - line.Average(item => item.Y)) > tolerance)
                    lines.Add(new List<PdfTextFragment>());
                lines[^1].Add(fragment);
            }
        }

        if (lines.Count < 2)
            return fallback;

        List<string> textLines = lines
            .Select(line => JoinFragments(line.OrderBy(fragment => fragment.X)))
            .Where(line => line.Length > 0)
            .ToList();
        return textLines.Count >= 2 ? string.Join("\r\n", textLines) : fallback;
    }

    private static string JoinFragments(IEnumerable<PdfTextFragment> fragments)
    {
        string result = string.Empty;
        PdfTextFragment? previous = null;
        foreach (PdfTextFragment fragment in fragments)
        {
            if (previous != null)
            {
                double gap = fragment.X - (previous.X + previous.Width);
                if (gap > Math.Max(previous.FontSize, fragment.FontSize) * 0.15 &&
                    !result.EndsWith(" ", StringComparison.Ordinal) &&
                    !fragment.Text.StartsWith(" ", StringComparison.Ordinal))
                    result += " ";
            }
            result += fragment.Text;
            previous = fragment;
        }
        return result;
    }

    private static string NormalizeLineEndings(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Replace("\n", "\r\n", StringComparison.Ordinal);
}
