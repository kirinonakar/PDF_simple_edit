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
                var xOffsets = lineGroups
                    .Select(group => group.Min(fragment => fragment.X) - annotation.X)
                    .ToList();
                var baselineOffsets = lineGroups
                    .Select(group =>
                    {
                        PdfTextFragment first = group.OrderBy(fragment => fragment.X).First();
                        return first.Y + first.BaselineOffset - annotation.Y;
                    })
                    .ToList();
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
                    annotation.BaselineOffset,
                    annotation.OriginalFontObjectNumber,
                    fontObjectNumbers,
                    xOffsets,
                    baselineOffsets);
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
