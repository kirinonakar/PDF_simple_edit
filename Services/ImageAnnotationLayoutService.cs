using System;

namespace PDF_simple_edit.Services;

public static class ImageAnnotationLayoutService
{
    public static (double width, double height) CalculateInitialSize(
        uint pixelWidth,
        uint pixelHeight,
        double pageWidth,
        double pageHeight)
    {
        const double preferredMaximumSize = 150;
        if (pixelWidth == 0 || pixelHeight == 0)
            return (preferredMaximumSize, preferredMaximumSize);

        double maximumWidth = Math.Min(preferredMaximumSize, Math.Max(pageWidth * 0.8, 1));
        double maximumHeight = Math.Min(preferredMaximumSize, Math.Max(pageHeight * 0.8, 1));
        double scale = Math.Min(maximumWidth / pixelWidth, maximumHeight / pixelHeight);
        return (
            Math.Max(pixelWidth * scale, 1),
            Math.Max(pixelHeight * scale, 1));
    }
}
