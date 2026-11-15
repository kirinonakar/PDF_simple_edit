using iText.Kernel.Pdf;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Services;

internal static class PdfPageCoordinates
{
    public static PdfTextBox GetDisplayBounds(PdfPage page)
    {
        var crop = page.GetCropBox();
        return ToDisplay(page).Map(new(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight()));
    }

    public static (double Width, double Height) GetPhysicalSize(PdfPage page)
    {
        var bounds = GetDisplayBounds(page);
        double unit = page.GetPdfObject().GetAsNumber(PdfName.UserUnit)?.DoubleValue() ?? 1;
        if (!double.IsFinite(unit) || unit <= 0) unit = 1;
        return (bounds.Width * unit, bounds.Height * unit);
    }

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
