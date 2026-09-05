using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

public static class AnnotationTextLayoutService
{
    private const double PdfToPixels = 96.0 / 72.0;
    private const double InlineEditorBottomGuard = 6.0;
    public const double InlineEditorTopInset = 0.0;

    public static bool ContainsLineBreak(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains('\r') || text.Contains('\n'));

    public static double GetDisplayFontSize(PdfAnnotation annotation, string _)
    {
        // Original PDF text must keep its point size. Scaling it down to fit the
        // WinUI fallback font's measured width makes the edit preview smaller than
        // the source even though the PDF font size itself has not changed.
        return annotation.FontSize;
    }

    public static int GetDisplayCharacterSpacing(PdfAnnotation annotation, string? text)
    {
        // A fallback font must use its own advances. Compressing it to the PDF
        // width with negative tracking makes narrow glyphs overlap.
        return Math.Max(annotation.CharacterSpacing ?? 0, 0);
    }

    public static double GetRequiredTextBoxWidth(
        PdfAnnotation annotation,
        string? text,
        int characterSpacing)
    {
        try
        {
            double widestLine = 0;
            foreach (string line in SplitLines(text))
            {
                TextBlock sample = CreateTextBlock(
                    line,
                    annotation.FontFamily,
                    annotation.FontSize,
                    annotation.IsBold,
                    annotation.IsItalic,
                    annotation.FontWeight);
                sample.CharacterSpacing = characterSpacing;
                sample.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity,
                    double.PositiveInfinity));
                widestLine = Math.Max(widestLine, sample.DesiredSize.Width);
            }

            return Math.Max((widestLine + 2.0) / PdfToPixels, 1.0 / PdfToPixels);
        }
        catch
        {
            return Math.Max(annotation.Width, 1.0 / PdfToPixels);
        }
    }

    public static double GetDisplayLineHeight(PdfAnnotation annotation, string? text)
    {
        string[] lines = SplitLines(text);
        try
        {
            TextBlock sample = CreateTextBlock(
                string.Join("\n", lines),
                annotation.FontFamily,
                annotation.FontSize,
                annotation.IsBold,
                annotation.IsItalic,
                annotation.FontWeight);
            sample.CharacterSpacing = GetDisplayCharacterSpacing(annotation, text);
            sample.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity,
                double.PositiveInfinity));
            return Math.Max(sample.DesiredSize.Height / lines.Length / PdfToPixels, Math.Max(annotation.LineHeight, 1));
        }
        catch
        {
            return Math.Max(annotation.LineHeight, Math.Max(annotation.FontSize * 1.2, 1));
        }
    }

    public static double GetVisualBaselineOffset(PdfAnnotation annotation)
    {
        if (annotation.BaselineOffset > 0.1)
            return annotation.BaselineOffset;

        return GetDisplayBaselineOffset(
            annotation.FontFamily,
            annotation.FontSize,
            annotation.IsBold,
            annotation.IsItalic,
            annotation.FontWeight);
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

    public static double GetInlineEditorHeight(
        string? text,
        string fontFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        int fontWeight = 0,
        double lineHeight = 0)
    {
        try
        {
            string measuredText = string.IsNullOrEmpty(text) ? "가Ag" : text;
            TextBlock sample = CreateTextBlock(
                measuredText,
                fontFamily,
                fontSize,
                isBold,
                isItalic,
                fontWeight);
            if (lineHeight > 0.1)
            {
                sample.LineHeight = lineHeight * PdfToPixels;
                sample.LineStackingStrategy = LineStackingStrategy.BaselineToBaseline;
            }
            sample.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity,
                double.PositiveInfinity));
            // RichEditBox needs a little more room below the TextBlock metrics.
            // Some fonts otherwise clip descenders when a context-menu click
            // removes focus and commits the inline edit.
            return Math.Max(
                Math.Ceiling(sample.DesiredSize.Height) + InlineEditorBottomGuard,
                1);
        }
        catch
        {
            return Math.Max(
                Math.Ceiling(fontSize * PdfToPixels) + InlineEditorBottomGuard,
                1);
        }
    }

    public static double GetRequiredTextBoxHeight(PdfAnnotation annotation, string? text)
    {
        double displayFontSize = GetDisplayFontSize(annotation, text ?? string.Empty);
        double contentHeight = GetInlineEditorHeight(
            text,
            annotation.FontFamily,
            displayFontSize,
            annotation.IsBold,
            annotation.IsItalic,
            annotation.FontWeight,
            GetDisplayLineHeight(annotation, text));
        double topOffset = Math.Max(GetTopOffset(annotation, displayFontSize), 0);

        // Return PDF points so the persisted annotation and its selection box use
        // the same safe lower edge as the inline editor.
        return Math.Max(annotation.Height, (topOffset + contentHeight) / PdfToPixels);
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

    private static string[] SplitLines(string? text) => (text ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n');

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
