using iText.Kernel.Pdf;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Services;

internal static class PdfPageCoordinates
{
    public static PdfAffineTransform ToDisplay(PdfPage page)
    {
        var box = page.GetCropBox();
        double left = box.GetLeft(), bottom = box.GetBottom(), width = box.GetWidth(), height = box.GetHeight();
        return (((page.GetRotation() % 360) + 360) % 360) switch
        {
            90 => new(0, 1, 1, 0, -bottom, -left),
            180 => new(-1, 0, 0, 1, width + left, -bottom),
            270 => new(0, -1, -1, 0, height + bottom, width + left),
            _ => new(1, 0, 0, -1, -left, height + bottom)
        };
    }
}
