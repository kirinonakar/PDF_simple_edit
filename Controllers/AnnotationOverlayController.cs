using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI;

namespace PDF_simple_edit.Controllers;

public sealed class AnnotationOverlayController
{
    private const double PdfToPixels = 96.0 / 72.0;

    public void Render(
        Canvas canvas,
        IReadOnlyList<PdfAnnotation> annotations,
        int pageIndex,
        IReadOnlyCollection<PdfAnnotation> selectedAnnotations,
        PdfAnnotation? primarySelection,
        PdfAnnotation? editingAnnotation,
        Func<string, Color> parseColor,
        Action<string, Point> resizeStarted,
        Action<InputSystemCursorShape> cursorChanged)
    {
        foreach (UIElement child in canvas.Children.Where(child => child is not TextBox).ToList())
            canvas.Children.Remove(child);

        int insertIndex = 0;
        foreach (PdfAnnotation annotation in annotations.Where(item => item.PageIndex == pageIndex))
        {
            if (annotation == editingAnnotation ||
                annotation.IsOriginalTextReplacement && !selectedAnnotations.Contains(annotation))
                continue;

            FrameworkElement? element = CreateElement(annotation, parseColor);
            if (element == null)
                continue;

            Canvas.SetLeft(element, annotation.X * PdfToPixels);
            Canvas.SetTop(element, annotation.Y * PdfToPixels);
            if (!selectedAnnotations.Contains(annotation))
            {
                canvas.Children.Insert(insertIndex++, element);
                continue;
            }

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(-2),
                Child = element,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(border, annotation.X * PdfToPixels);
            Canvas.SetTop(border, annotation.Y * PdfToPixels);
            canvas.Children.Insert(insertIndex++, border);

            if (annotation == primarySelection &&
                annotation.Type is AnnotationType.Image or AnnotationType.Highlight)
            {
                AddResizeHandles(
                    canvas,
                    annotation,
                    ref insertIndex,
                    resizeStarted,
                    cursorChanged);
            }
        }
    }

    private static FrameworkElement? CreateElement(
        PdfAnnotation annotation,
        Func<string, Color> parseColor)
    {
        if (annotation.Type is AnnotationType.Text or AnnotationType.FreeText)
            return CreateTextElement(annotation, parseColor);
        if (annotation.Type == AnnotationType.Highlight)
        {
            return new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = annotation.Width * PdfToPixels,
                Height = annotation.Height * PdfToPixels,
                Fill = new SolidColorBrush(parseColor(annotation.Color)),
                Opacity = annotation.Opacity,
                IsHitTestVisible = false
            };
        }
        if (annotation.Type != AnnotationType.Image)
            return null;
        if (annotation.ImagePath != null)
        {
            return new Image
            {
                Source = new BitmapImage(new Uri(annotation.ImagePath)),
                Width = annotation.Width * PdfToPixels,
                Height = annotation.Height * PdfToPixels,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
        }
        return annotation.IsApplied
            ? null
            : new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = annotation.Width * PdfToPixels,
                Height = annotation.Height * PdfToPixels,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false
            };
    }

    private static FrameworkElement CreateTextElement(
        PdfAnnotation annotation,
        Func<string, Color> parseColor)
    {
        if (annotation.IsOriginalTextReplacement)
        {
            return new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(annotation.Width * PdfToPixels, 1),
                Height = Math.Max(annotation.Height * PdfToPixels, 1),
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsHitTestVisible = false
            };
        }

        bool hasOriginalLineLayout = annotation.TextFragments.Count > 1;
        bool containsLineBreak = AnnotationTextLayoutService.ContainsLineBreak(annotation.Content);
        double displayFontSize = hasOriginalLineLayout
            ? AnnotationTextLayoutService.GetDisplayFontSize(annotation, annotation.Content)
            : annotation.FontSize;
        return new TextBlock
        {
            Text = annotation.Content,
            TextWrapping = hasOriginalLineLayout
                ? TextWrapping.NoWrap
                : containsLineBreak ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Width = hasOriginalLineLayout || containsLineBreak
                ? Math.Max(annotation.Width * PdfToPixels, 1)
                : double.NaN,
            Height = hasOriginalLineLayout
                ? Math.Max(annotation.Height * PdfToPixels, 1)
                : containsLineBreak
                    ? AnnotationTextLayoutService.GetMultilineHeight(
                        annotation, annotation.Content, annotation.FontSize)
                    : double.NaN,
            FontFamily = new FontFamily(annotation.FontFamily),
            FontSize = displayFontSize * PdfToPixels,
            Foreground = new SolidColorBrush(parseColor(annotation.Color)),
            FontWeight = annotation.IsBold
                ? Microsoft.UI.Text.FontWeights.Bold
                : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = annotation.IsItalic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
            RenderTransform = hasOriginalLineLayout
                ? new TranslateTransform
                {
                    Y = AnnotationTextLayoutService.GetTopOffset(annotation, displayFontSize)
                }
                : new TranslateTransform
                {
                    // Match the one-pixel text inset of the inline TextBox so the
                    // edit preview, committed overlay and saved PDF stay aligned.
                    Y = AnnotationTextLayoutService.InlineEditorTopInset
                },
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            IsHitTestVisible = false
        };
    }

    private static void AddResizeHandles(
        Canvas canvas,
        PdfAnnotation annotation,
        ref int insertIndex,
        Action<string, Point> resizeStarted,
        Action<InputSystemCursorShape> cursorChanged)
    {
        double x = annotation.X * PdfToPixels;
        double y = annotation.Y * PdfToPixels;
        double width = annotation.Width * PdfToPixels;
        double height = annotation.Height * PdfToPixels;
        const double size = 8;
        const double offset = size / 2;
        var handles = new Dictionary<string, Point>
        {
            ["NW"] = new(x - offset, y - offset),
            ["N"] = new(x + width / 2 - offset, y - offset),
            ["NE"] = new(x + width - offset, y - offset),
            ["W"] = new(x - offset, y + height / 2 - offset),
            ["E"] = new(x + width - offset, y + height / 2 - offset),
            ["SW"] = new(x - offset, y + height - offset),
            ["S"] = new(x + width / 2 - offset, y + height - offset),
            ["SE"] = new(x + width - offset, y + height - offset)
        };

        foreach ((string direction, Point position) in handles)
        {
            var handle = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                StrokeThickness = 1,
                Tag = direction,
                IsHitTestVisible = true
            };
            Canvas.SetLeft(handle, position.X);
            Canvas.SetTop(handle, position.Y);
            handle.PointerPressed += (_, args) =>
            {
                canvas.CapturePointer(args.Pointer);
                resizeStarted(direction, args.GetCurrentPoint(canvas).Position);
                args.Handled = true;
            };
            handle.PointerEntered += (_, _) => cursorChanged(GetResizeCursor(direction));
            handle.PointerExited += (_, _) => cursorChanged(InputSystemCursorShape.Arrow);
            canvas.Children.Insert(insertIndex++, handle);
        }
    }

    private static InputSystemCursorShape GetResizeCursor(string direction) => direction switch
    {
        "NW" or "SE" => InputSystemCursorShape.SizeNorthwestSoutheast,
        "NE" or "SW" => InputSystemCursorShape.SizeNortheastSouthwest,
        "N" or "S" => InputSystemCursorShape.SizeNorthSouth,
        "E" or "W" => InputSystemCursorShape.SizeWestEast,
        _ => InputSystemCursorShape.Arrow
    };
}
