using System;
using System.Globalization;

namespace PDF_simple_edit.Models;

public enum PdfGradientKind { Linear, Radial }

public sealed record PdfFillGradient(PdfGradientKind Kind, string StartColor, string EndColor, double Angle = 0)
{
    internal bool IsValid => Enum.IsDefined(Kind) && IsRgb(StartColor) && IsRgb(EndColor) && double.IsFinite(Angle);

    internal (PdfTextPoint Start, PdfTextPoint End) LinearAxis(double width, double height)
    {
        double radians = (Angle % 360) * Math.PI / 180;
        double x = Math.Cos(radians), y = Math.Sin(radians);
        // Project the box onto the gradient axis so the corner extrema reach the two colors.
        double halfSpan = (Math.Abs(x) * width + Math.Abs(y) * height) / 2;
        return (new(width / 2 - x * halfSpan, height / 2 - y * halfSpan),
            new(width / 2 + x * halfSpan, height / 2 + y * halfSpan));
    }

    private static bool IsRgb(string? color) => color is { Length: 7 } && color[0] == '#' &&
        int.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
}
