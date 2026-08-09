using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Models;
using System;

namespace PDF_simple_edit.Services;

public static class AnnotationTextLayoutService
{
    private const double PdfToPixels = 96.0 / 72.0;
    public const double InlineEditorTopInset = 0.0;

    public static bool ContainsLineBreak(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains('\r') || text.Contains('\n'));

    public static double GetDisplayFontSize(PdfAnnotation annotation, string text)
    {
        if (annotation.FontSize <= 0 || annotation.Width <= 0 || annotation.TextFragments.Count < 2)
            return annotation.FontSize;

        try
        {
            double measured = MeasureText(
                text,
                annotation.FontFamily,
                annotation.FontSize,
                annotation.IsBold,
                annotation.IsItalic,
                annotation.FontWeight);
            double availableWidth = Math.Max(annotation.Width - (2.0 / PdfToPixels), 1);
            return measured <= availableWidth
                ? annotation.FontSize
                : Math.Max(annotation.FontSize * availableWidth / measured, 1);
        }
        catch
        {
            return annotation.FontSize;
        }
    }

    public static double GetTopOffset(PdfAnnotation annotation, double displayFontSize)
    {
        if (annotation.BaselineOffset <= 0.1)
            return 0;

        double baselineOffset = GetDisplayBaselineOffset(
            annotation.FontFamily,
            displayFontSize,
            annotation.IsBold,
            annotation.IsItalic,
            annotation.FontWeight) * PdfToPixels;

        return annotation.BaselineOffset * PdfToPixels - baselineOffset;
    }

    public static double GetDisplayBaselineOffset(
        string fontFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        int fontWeight = 0)
    {
        // The PDF annotation Y coordinate is the top of the WinUI text layout box.
        // Save against the same baseline instead of the PDF glyph's ink bounds so
        // the rendered PDF does not appear a few pixels above the preview.
        double baselineOffset = Math.Max(fontSize, 1) * PdfToPixels;
        try
        {
            var sample = CreateTextBlock(
                "가Ag",
                fontFamily,
                fontSize,
                isBold,
                isItalic,
                fontWeight);
            sample.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var baselineProperty = typeof(TextBlock).GetProperty("BaselineOffset");
            if (baselineProperty?.GetValue(sample) is double measuredBaseline && measuredBaseline > 0.1)
                baselineOffset = measuredBaseline;
        }
        catch
        {
            // The font-size estimate is sufficient when the platform omits BaselineOffset.
        }

        return (baselineOffset + InlineEditorTopInset) / PdfToPixels;
    }

    public static double GetMultilineHeight(PdfAnnotation annotation, string text, double fontSize)
    {
        double uiLineHeight = Math.Max(
            fontSize * PdfToPixels * 1.35,
            annotation.LineHeight > 0.1 ? annotation.LineHeight * PdfToPixels : 0);
        double requiredHeight = GetLineCount(text) * uiLineHeight + 8;
        return Math.Max(annotation.Height * PdfToPixels, requiredHeight);
    }

    public static (double width, double height) MeasureBounds(
        string text,
        string fontFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        int fontWeight = 0)
    {
        TextBlock textBlock = CreateTextBlock(text, fontFamily, fontSize, isBold, isItalic, fontWeight);
        textBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return (
            (textBlock.DesiredSize.Width + 2.0) / PdfToPixels,
            textBlock.DesiredSize.Height / PdfToPixels);
    }

    private static int GetLineCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n').Length;
    }

    private static double MeasureText(
        string text,
        string fontFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        int fontWeight)
    {
        TextBlock textBlock = CreateTextBlock(text, fontFamily, fontSize, isBold, isItalic, fontWeight);
        textBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max((textBlock.DesiredSize.Width - 2.0) / PdfToPixels, 1);
    }

    private static TextBlock CreateTextBlock(
        string text,
        string fontFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        int fontWeight = 0) => new()
    {
        Text = text,
        FontFamily = new FontFamily(fontFamily),
        FontSize = fontSize * PdfToPixels,
        FontWeight = ResolveFontWeight(fontWeight, isBold),
        FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        TextWrapping = TextWrapping.NoWrap,
        Padding = new Thickness(0)
    };

    public static Windows.UI.Text.FontWeight ResolveFontWeight(int fontWeight, bool isBold)
    {
        int resolvedWeight = fontWeight is >= 1 and <= 999 ? fontWeight : 400;
        if (isBold && resolvedWeight < 600)
            resolvedWeight = 700;
        return new Windows.UI.Text.FontWeight { Weight = (ushort)resolvedWeight };
    }
}
