using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EditorAnnotation = PDF_simple_edit.Models.PdfAnnotation;

namespace PDF_simple_edit.Services;

public sealed record PdfPageGeometryOptions(IReadOnlyList<int> PageIndices, int Rotation = 0,
    double Width = 0, double Height = 0, bool FitContent = true)
{
    public bool IsResize => Width != 0 || Height != 0;
}

/// <summary>Changes page geometry without rasterizing content or flattening PDF annotations.</summary>
internal static class PdfPageGeometryService
{
    public static Dictionary<int, PdfAffineTransform> Apply(PdfDocument document, PdfPageGeometryOptions options)
    {
        if (options.Rotation % 90 != 0) throw new ArgumentOutOfRangeException(nameof(options.Rotation));
        if (options.IsResize && (!double.IsFinite(options.Width) || !double.IsFinite(options.Height) ||
            options.Width < 1 || options.Height < 1 || options.Width > 14400 || options.Height > 14400))
            throw new ArgumentException(LocalizationService.Get("InvalidPageDimensions"));
        int[] indices = options.PageIndices.Distinct().ToArray();
        if (indices.Length == 0 || indices.Any(i => i < 0 || i >= document.GetNumberOfPages()))
            throw new ArgumentOutOfRangeException(nameof(options.PageIndices));
        var transforms = new Dictionary<int, PdfAffineTransform>();
        foreach (int index in indices)
        {
            var page = document.GetPage(index + 1);
            var oldDisplay = PdfPageCoordinates.ToDisplay(page);
            var crop = page.GetCropBox();
            var oldBounds = oldDisplay.Map(new(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight()));
            if (options.IsResize)
            {
                // Keep the existing page rotation. Width/height refer to the visible page.
                double unit = page.GetPdfObject().GetAsNumber(PdfName.UserUnit)?.DoubleValue() ?? 1;
                if (!double.IsFinite(unit) || unit <= 0) unit = 1;
                double scale = options.FitContent
                    ? Math.Min(options.Width / oldBounds.Width, options.Height / oldBounds.Height) : unit;
                var displayTransform = new PdfAffineTransform(scale, 0, 0, scale,
                    (options.Width - oldBounds.Width * scale) / 2, (options.Height - oldBounds.Height * scale) / 2);
                bool sideways = ((page.GetRotation() % 180) + 180) % 180 == 90;
                var box = new Rectangle((float)(sideways ? options.Height : options.Width),
                    (float)(sideways ? options.Width : options.Height));
                page.SetMediaBox(box).SetCropBox(box).SetTrimBox(box).SetBleedBox(box).SetArtBox(box);
                // Normalize to physical PDF points so the requested millimeters
                // are correct even for documents using a custom UserUnit.
                page.GetPdfObject().Remove(PdfName.UserUnit);
                var pdfTransform = PdfPageCoordinates.ToDisplay(page).Inverse().After(displayTransform).After(oldDisplay);
                // Preserve the original crop: resizing must not reveal previously hidden content.
                string prefix = "q\n" + pdfTransform.Command + FormattableString.Invariant(
                    $"{crop.GetX()} {crop.GetY()} {crop.GetWidth()} {crop.GetHeight()} re W n\n");
                page.NewContentStreamBefore().SetData(Encoding.ASCII.GetBytes(prefix));
                page.NewContentStreamAfter().SetData(Encoding.ASCII.GetBytes("\nQ\n"));
                foreach (var annotation in page.GetAnnotations())
                    TransformAnnotation(annotation.GetPdfObject(), pdfTransform, scale);
                transforms[index] = displayTransform;
            }
            else
            {
                page.SetRotation(((page.GetRotation() + options.Rotation) % 360 + 360) % 360);
                transforms[index] = PdfPageCoordinates.ToDisplay(page).After(oldDisplay.Inverse());
            }
        }
        return transforms;
    }

    private static void TransformAnnotation(PdfDictionary annotation, PdfAffineTransform transform, double scale)
    {
        if (annotation.GetAsArray(PdfName.Rect) is { } rect)
        {
            var mapped = transform.Map(new PdfTextBox(rect.GetAsNumber(0).DoubleValue(), rect.GetAsNumber(1).DoubleValue(),
                rect.GetAsNumber(2).DoubleValue() - rect.GetAsNumber(0).DoubleValue(),
                rect.GetAsNumber(3).DoubleValue() - rect.GetAsNumber(1).DoubleValue()));
            annotation.Put(PdfName.Rect, new PdfArray(new[] { mapped.X, mapped.Y, mapped.Right, mapped.Bottom }));
        }
        foreach (var key in new[] { PdfName.QuadPoints, PdfName.Vertices, PdfName.L, PdfName.CL })
            if (annotation.GetAsArray(key) is { } points) annotation.Put(key, MapPoints(points, transform));
        if (annotation.GetAsArray(PdfName.InkList) is { } ink)
        {
            var mapped = new PdfArray();
            for (int i = 0; i < ink.Size(); i++) mapped.Add(MapPoints(ink.GetAsArray(i), transform));
            annotation.Put(PdfName.InkList, mapped);
        }
        // Appearance streams remain intact: PDF viewers fit them to the new Rect.
        // Scale fallback geometry too, for viewers regenerating an appearance.
        foreach (var key in new[] { PdfName.RD })
            if (annotation.GetAsArray(key) is { } distances)
                annotation.Put(key, new PdfArray(Enumerable.Range(0, distances.Size())
                    .Select(i => distances.GetAsNumber(i).DoubleValue() * scale).ToArray()));
        if (annotation.GetAsArray(PdfName.Border) is { } border && border.Size() >= 3)
        {
            var copy = new PdfArray(border);
            for (int i = 0; i < 3; i++) copy.Set(i, new PdfNumber(border.GetAsNumber(i).DoubleValue() * scale));
            annotation.Put(PdfName.Border, copy);
        }
        if (annotation.GetAsDictionary(PdfName.BS) is { } style && style.GetAsNumber(PdfName.W) is { } width)
        {
            var copy = new PdfDictionary(style);
            copy.Put(PdfName.W, new PdfNumber(width.DoubleValue() * scale));
            annotation.Put(PdfName.BS, copy);
        }
    }

    private static PdfArray MapPoints(PdfArray points, PdfAffineTransform transform)
    {
        var mapped = new PdfArray();
        for (int i = 0; i + 1 < points.Size(); i += 2)
        {
            var point = transform.Map(points.GetAsNumber(i).DoubleValue(), points.GetAsNumber(i + 1).DoubleValue());
            mapped.Add(new PdfNumber(point.X)); mapped.Add(new PdfNumber(point.Y));
        }
        return mapped;
    }

    internal static void TransformPath(EditorAnnotation annotation, PdfAffineTransform transform)
    {
        double scale = Math.Sqrt(transform.A * transform.A + transform.B * transform.B);
        double angle = Math.Atan2(transform.B, transform.A) * 180 / Math.PI;
        double oldX = annotation.X + annotation.Width / 2, oldY = annotation.Y + annotation.Height / 2;
        var center = transform.Map(oldX, oldY);
        foreach (var point in annotation.SignaturePoints)
        {
            point.X = center.X + (point.X - oldX) * scale;
            point.Y = center.Y + (point.Y - oldY) * scale;
        }
        annotation.Width *= scale; annotation.Height *= scale;
        annotation.X = center.X - annotation.Width / 2; annotation.Y = center.Y - annotation.Height / 2;
        annotation.LineWidth *= scale;
        annotation.Rotation = (annotation.Rotation + angle + 360) % 360;
    }
}
