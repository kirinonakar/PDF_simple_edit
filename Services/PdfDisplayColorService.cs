using iText.Kernel.Colors;
using iText.Kernel.Pdf.Colorspace;
using System;
using System.Linq;

namespace PDF_simple_edit.Services;

internal static class PdfDisplayColorService
{
    public static string ToHex(Color? color, int depth = 0)
    {
        if (color == null || depth > 8) return "#000000";
        // A Separation's single component is ink tint, not gray luminance.
        // PMS3155 at tint 1 in the sample is teal, not white.
        if (color.GetColorSpace() is PdfSpecialCs.Separation separation)
        {
            var components = separation.GetTintTransformation().Calculate(color.GetColorValue().Select(v => (double)v).ToArray());
            return ToHex(Color.MakeColor(separation.GetBaseCs(), components.Select(v => (float)v).ToArray()), depth + 1);
        }
        var values = color.GetColorValue();
        if (values.Length == 4)
            values = Color.ConvertCmykToRgb(new DeviceCmyk(values[0], values[1], values[2], values[3])).GetColorValue();
        if (values.Length == 1) values = new[] { values[0], values[0], values[0] };
        return values.Length < 3 ? "#000000" : "#" + string.Concat(values.Take(3).Select(v => Math.Clamp((int)Math.Round(v * 255), 0, 255).ToString("X2")));
    }
}
