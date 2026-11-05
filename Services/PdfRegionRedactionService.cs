using Loc = PDF_simple_edit.Services.LocalizationService;
using System;
using System.Collections.Generic;
using System.Linq;
using iText.Forms;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.PdfCleanup;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Services;

internal static class PdfRegionRedactionService
{
    // The editor uses top-down points in the rotated CropBox; pdfSweep uses
    // unrotated PDF user space. Clip before mapping all four corners.
    public static PdfTextBox Clip(PdfTextBox region, double width, double height)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
            !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException(Loc.Get("DragARegionToRedact"), nameof(region));
        double left = Math.Clamp(region.X, 0, width), top = Math.Clamp(region.Y, 0, height);
        double right = Math.Clamp(region.Right, 0, width), bottom = Math.Clamp(region.Bottom, 0, height);
        return new(left, top, right - left, bottom - top);
    }

    public static void Apply(PdfDocument document, int pageIndex, PdfTextBox region)
    {
        if (pageIndex < 0 || pageIndex >= document.GetNumberOfPages())
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var page = document.GetPage(pageIndex + 1);
        var crop = page.GetCropBox();
        var display = PdfPageCoordinates.ToDisplay(page);
        var bounds = display.Map(new PdfTextBox(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight()));
        region = Clip(region, bounds.Width, bounds.Height);
        if (region.Width <= 0 || region.Height <= 0) return;
        var pdfBounds = display.Inverse().Map(region);
        PdfRedactionContentNormalizer.Normalize(page);
        RemoveFormWidgets(document, page, pdfBounds);
        var locations = new List<PdfCleanUpLocation>
        {
            new(pageIndex + 1, new Rectangle((float)pdfBounds.X, (float)pdfBounds.Y,
                (float)pdfBounds.Width, (float)pdfBounds.Height), null)
        };
        new PdfCleanUpTool(document, locations, new CleanUpProperties().SetProcessAnnotations(true)).CleanUp();
    }

    private static void RemoveFormWidgets(PdfDocument document, PdfPage page, PdfTextBox region)
    {
        var form = PdfAcroForm.GetAcroForm(document, false);
        if (form == null) return;
        var targets = page.GetAnnotations().Where(a =>
        {
            if (!PdfName.Widget.Equals(a.GetSubtype()) || a.GetRectangle() == null) return false;
            var rect = a.GetRectangle().ToRectangle();
            return rect.GetLeft() < region.Right && rect.GetRight() > region.X &&
                rect.GetBottom() < region.Bottom && rect.GetTop() > region.Y;
        }).Select(a => a.GetPdfObject()).ToHashSet();
        if (targets.Count == 0) return;
        // pdfSweep removes widget annotations but leaves their AcroForm values.
        // Remove the matching field too, or detach only its selected widgets if
        // the same field still has visible widgets elsewhere in the document.
        foreach (var pair in form.GetAllFormFields().OrderByDescending(p => p.Key.Length).ToList())
        {
            var field = pair.Value;
            var widgets = field.GetWidgets();
            if (widgets.Count == 0 || !widgets.Any(w => targets.Contains(w.GetPdfObject()))) continue;
            if (widgets.All(w => targets.Contains(w.GetPdfObject())))
            {
                form.RemoveField(pair.Key);
            }
            else
            {
                foreach (var child in field.GetChildFormAnnotations().Where(c => targets.Contains(c.GetWidget().GetPdfObject())).ToList())
                {
                    page.RemoveAnnotation(child.GetWidget());
                    field.RemoveChild(child);
                }
            }
        }
    }

    public static bool Intersects(PdfAnnotation annotation, PdfTextBox region)
    {
        var bounds = new PdfTextBox(annotation.X, annotation.Y, annotation.Width, annotation.Height);
        bounds = PdfAffineTransform.Between(bounds, bounds, annotation.Rotation).Map(bounds);
        double padding = Math.Max(0, annotation.LineWidth) / 2;
        return bounds.X - padding < region.Right && bounds.Right + padding > region.X &&
            bounds.Y - padding < region.Bottom && bounds.Bottom + padding > region.Y;
    }
}
