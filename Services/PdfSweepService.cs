using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;
using iText.PdfCleanup;

namespace PDF_simple_edit.Services;

internal static class PdfSweepService
{
    static PdfSweepService()
    {
        // pdfSweep creates its output streams internally. Default iText number
        // formatting rounds small CTM coefficients to zero, hiding otherwise
        // untouched artwork. Keep high precision enabled; temporarily toggling
        // this global setting would race other background PDF writes.
        PdfOutputStream.SetHighPrecision(true);
    }

    public static void CleanUp(PdfDocument document, IList<PdfCleanUpLocation> locations)
    {
        foreach (int page in locations.Select(location => location.GetPage()).Distinct())
            PdfRedactionContentNormalizer.Normalize(document.GetPage(page));
        // A tiny CTM can put ordinary page-sized artwork at very large local
        // coordinates. The fixed 10^14 multiplier overflows Clipper's range;
        // choose the multiplier from each path and its transformed selection.
        var properties = new CleanUpProperties().SetOffsetProperties(
            new PathOffsetApproximationProperties().CalculateOffsetMultiplierDynamically(true));
        new PdfCleanUpTool(document, locations, properties).CleanUp();
    }
}
