using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Colorspace;
using iText.Kernel.Pdf.Colorspace.Shading;
using PDF_simple_edit.Models;
using System;
using System.Globalization;

namespace PDF_simple_edit.Services;

internal static class PdfGradientPattern
{
    private static readonly PdfName GradientMarker = new("PSEFillGradient");

    internal static PatternColor Create(PdfFillGradient gradient, PdfTextBox bounds, PdfAffineTransform shapeToStream)
    {
        var first = Rgb(gradient.StartColor);
        var last = Rgb(gradient.EndColor);
        var extended = new[] { true, true };
        double width = Math.Max(.001, bounds.Width), height = Math.Max(.001, bounds.Height);
        var position = shapeToStream.After(new(1, 0, 0, 1, bounds.X, bounds.Y));
        AbstractPdfShading shading;
        if (gradient.Kind == PdfGradientKind.Radial)
        {
            shading = new PdfRadialShading(new PdfDeviceCs.Rgb(), .5f, .5f, 0, first, .5f, .5f, .5f, last, extended);
            position = position.After(new(width, 0, 0, height, 0, 0));
        }
        else
        {
            var (start, end) = gradient.LinearAxis(width, height);
            shading = new PdfAxialShading(new PdfDeviceCs.Rgb(), (float)start.X, (float)start.Y, first,
                (float)end.X, (float)end.Y, last, extended);
        }
        var pattern = new PdfPattern.Shading(shading);
        pattern.GetPdfObject().Put(GradientMarker, PdfBoolean.TRUE);
        pattern.SetMatrix(MatrixArray(position));
        return new PatternColor(pattern);
    }

    internal static PdfName? Transform(PdfResources resources, PdfName name, PdfAffineTransform transform)
    {
        var source = resources.GetResource(PdfName.Pattern)?.GetAsDictionary(name);
        if (source?.GetAsBoolean(GradientMarker)?.GetValue() != true ||
            source.GetAsArray(PdfName.Matrix)?.ToDoubleArray() is not { Length: 6 } m)
            return null;
        // The pattern matrix is relative to the content stream's default user space.
        var matrix = new PdfAffineTransform(m[0], m[1], m[2], m[3], m[4], m[5]);
        var result = new PdfDictionary();
        result.PutAll(source);
        result.Put(PdfName.Matrix, MatrixArray(transform.After(matrix)));
        return resources.AddPattern(result);
    }

    private static PdfArray MatrixArray(PdfAffineTransform matrix) =>
        new(new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F });

    private static float[] Rgb(string color)
    {
        int rgb = int.Parse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return new[] { (rgb >> 16) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f };
    }
}
