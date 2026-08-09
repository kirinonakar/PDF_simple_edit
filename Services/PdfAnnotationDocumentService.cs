using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System.Linq;

namespace PDF_simple_edit.Services;

public sealed class PdfAnnotationDocumentService
{
    public void Apply(PdfDocumentManager manager, PdfDocument document, PdfAnnotation annotation)
    {
        Color color = ParseColor(annotation.Color);
        switch (annotation.Type)
        {
            case AnnotationType.Text:
            case AnnotationType.FreeText:
                if (annotation.IsOriginalTextReplacement)
                    return;
                var lineGroups = annotation.TextFragments
                    .GroupBy(fragment => fragment.LineIndex)
                    .OrderBy(group => group.Key)
                    .ToList();
                var fontObjectNumbers = lineGroups
                    .Select(group => group
                        .Select(fragment => fragment.OriginalFontObjectNumber)
                        .FirstOrDefault(number => number > 0, annotation.OriginalFontObjectNumber))
                    .ToList();
                // TextFragments retain the coordinates of the original PDF text so
                // that removal can still identify it after the annotation is moved.
                // Use that immutable source origin for per-line offsets. Computing
                // offsets from annotation.X/Y would cancel the user's move on save.
                double sourceX = annotation.TextFragments.Count > 0
                    ? annotation.TextFragments.Min(fragment => fragment.X)
                    : annotation.X;
                double sourceY = annotation.TextFragments.Count > 0
                    ? annotation.TextFragments.Min(fragment => fragment.Y)
                    : annotation.Y;
                var xOffsets = lineGroups
                    .Select(group => group.Min(fragment => fragment.X) - sourceX)
                    .ToList();
                var baselineOffsets = lineGroups
                    .Select(group =>
                    {
                        PdfTextFragment first = group.OrderBy(fragment => fragment.X).First();
                        return first.Y + first.BaselineOffset - sourceY;
                    })
                    .ToList();
                double baselineOffset = lineGroups.Count > 0
                    ? annotation.BaselineOffset
                    : AnnotationTextLayoutService.GetDisplayBaselineOffset(
                        annotation.FontFamily,
                        annotation.FontSize,
                        annotation.IsBold,
                        annotation.IsItalic);
                manager.AddTextInternal(
                    document,
                    annotation.PageIndex,
                    annotation.X,
                    annotation.Y,
                    annotation.Content,
                    annotation.FontFamily,
                    annotation.FontSize,
                    color,
                    annotation.IsBold,
                    annotation.IsItalic,
                    annotation.LineHeight,
                    baselineOffset,
                    annotation.OriginalFontObjectNumber,
                    fontObjectNumbers,
                    xOffsets,
                    baselineOffsets,
                    annotation.TextFragments.Count > 1 ? annotation.Width : 0);
                break;
            case AnnotationType.Highlight:
                manager.AddHighlightInternal(
                    document,
                    annotation.PageIndex,
                    annotation.X,
                    annotation.Y,
                    annotation.Width,
                    annotation.Height,
                    color,
                    (float)annotation.Opacity);
                break;
            case AnnotationType.Image when annotation.ImagePath != null:
                manager.AddImageInternal(
                    document,
                    annotation.PageIndex,
                    annotation.ImagePath,
                    annotation.X,
                    annotation.Y,
                    annotation.Width,
                    annotation.Height);
                break;
        }
    }

    private static Color ParseColor(string color)
    {
        try
        {
            Windows.UI.Color parsed = EditorColorService.Parse(color);
            return new DeviceRgb(parsed.R, parsed.G, parsed.B);
        }
        catch
        {
            return ColorConstants.BLACK;
        }
    }
}
