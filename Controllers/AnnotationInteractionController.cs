using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace PDF_simple_edit.Controllers;

public enum AnnotationMoveResult
{
    NotActive,
    NotMoved,
    Completed,
    OriginalTextRemovalFailed
}

public sealed class AnnotationInteractionController
{
    private const double PdfToPixels = 96.0 / 72.0;
    private readonly Dictionary<PdfAnnotation, Point> _moveStartPositions = new();
    private bool _hasMoved;
    private string? _resizeHandle;
    private Point _lastPointerPosition;
    private Point _highlightStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _highlightRectangle;

    public bool IsMoving { get; private set; }
    public bool IsResizing { get; private set; }
    public bool IsDrawingHighlight => _highlightRectangle != null;

    public void BeginResize(string direction, Point position)
    {
        IsResizing = true;
        _resizeHandle = direction;
        _lastPointerPosition = position;
    }

    public void BeginMove(IReadOnlyList<PdfAnnotation> annotations, Point position)
    {
        IsMoving = true;
        _hasMoved = false;
        _moveStartPositions.Clear();
        foreach (PdfAnnotation annotation in annotations)
            _moveStartPositions[annotation] = new Point(annotation.X, annotation.Y);
        _lastPointerPosition = position;
    }

    public void BeginHighlight(
        Canvas canvas,
        Point position,
        string color,
        double opacity)
    {
        _highlightStart = position;
        _highlightRectangle = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(EditorColorService.Parse(color)),
            Opacity = opacity,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
            StrokeThickness = 1
        };
        Canvas.SetLeft(_highlightRectangle, position.X);
        Canvas.SetTop(_highlightRectangle, position.Y);
        canvas.Children.Add(_highlightRectangle);
    }

    public bool UpdatePointer(
        Canvas canvas,
        Point position,
        PdfAnnotation? primarySelection,
        IReadOnlyList<PdfAnnotation> selectedAnnotations)
    {
        if (IsResizing && primarySelection != null)
        {
            Resize(primarySelection, position);
            return true;
        }
        if (IsMoving && primarySelection != null)
        {
            if (!Move(selectedAnnotations, position))
                return false;
            return true;
        }
        if (_highlightRectangle != null)
        {
            double x = Math.Min(position.X, _highlightStart.X);
            double y = Math.Min(position.Y, _highlightStart.Y);
            Canvas.SetLeft(_highlightRectangle, x);
            Canvas.SetTop(_highlightRectangle, y);
            _highlightRectangle.Width = Math.Abs(position.X - _highlightStart.X);
            _highlightRectangle.Height = Math.Abs(position.Y - _highlightStart.Y);
        }
        return false;
    }

    public bool CompleteResize()
    {
        if (!IsResizing)
            return false;
        IsResizing = false;
        _resizeHandle = null;
        return true;
    }

    public async Task<AnnotationMoveResult> CompleteMoveAsync(
        PdfDocumentManager manager,
        int pageIndex,
        IReadOnlyList<PdfAnnotation> selectedAnnotations)
    {
        if (!IsMoving)
            return AnnotationMoveResult.NotActive;
        IsMoving = false;
        if (!_hasMoved)
        {
            _moveStartPositions.Clear();
            return AnnotationMoveResult.NotMoved;
        }

        var originalText = selectedAnnotations
            .Where(annotation => annotation.IsOriginalTextReplacement)
            .ToList();
        if (originalText.Count > 0 &&
            !await manager.RemoveOriginalTextAnnotationsAsync(pageIndex, originalText))
        {
            foreach ((PdfAnnotation annotation, Point position) in _moveStartPositions)
            {
                annotation.X = position.X;
                annotation.Y = position.Y;
            }
            _moveStartPositions.Clear();
            return AnnotationMoveResult.OriginalTextRemovalFailed;
        }

        foreach (PdfAnnotation annotation in originalText)
            annotation.IsOriginalTextReplacement = false;
        _moveStartPositions.Clear();
        return AnnotationMoveResult.Completed;
    }

    public PdfAnnotation? CompleteHighlight(
        Canvas canvas,
        int pageIndex,
        TextFontSettings settings)
    {
        if (_highlightRectangle == null)
            return null;

        var rectangle = _highlightRectangle;
        _highlightRectangle = null;
        canvas.Children.Remove(rectangle);
        double width = rectangle.Width / PdfToPixels;
        double height = rectangle.Height / PdfToPixels;
        if (width <= 5 || height <= 5)
            return null;

        return new PdfAnnotation
        {
            Type = AnnotationType.Highlight,
            PageIndex = pageIndex,
            X = Canvas.GetLeft(rectangle) / PdfToPixels,
            Y = Canvas.GetTop(rectangle) / PdfToPixels,
            Width = width,
            Height = height,
            Color = settings.HighlightColor,
            Opacity = settings.HighlightOpacity,
            IsApplied = false
        };
    }

    private void Resize(PdfAnnotation annotation, Point position)
    {
        double dx = (position.X - _lastPointerPosition.X) / PdfToPixels;
        double dy = (position.Y - _lastPointerPosition.Y) / PdfToPixels;
        const double minimumSize = 10;
        switch (_resizeHandle)
        {
            case "NW":
                if (annotation.Width - dx > minimumSize) { annotation.X += dx; annotation.Width -= dx; }
                if (annotation.Height - dy > minimumSize) { annotation.Y += dy; annotation.Height -= dy; }
                break;
            case "N":
                if (annotation.Height - dy > minimumSize) { annotation.Y += dy; annotation.Height -= dy; }
                break;
            case "NE":
                if (annotation.Width + dx > minimumSize) annotation.Width += dx;
                if (annotation.Height - dy > minimumSize) { annotation.Y += dy; annotation.Height -= dy; }
                break;
            case "W":
                if (annotation.Width - dx > minimumSize) { annotation.X += dx; annotation.Width -= dx; }
                break;
            case "E":
                if (annotation.Width + dx > minimumSize) annotation.Width += dx;
                break;
            case "SW":
                if (annotation.Width - dx > minimumSize) { annotation.X += dx; annotation.Width -= dx; }
                if (annotation.Height + dy > minimumSize) annotation.Height += dy;
                break;
            case "S":
                if (annotation.Height + dy > minimumSize) annotation.Height += dy;
                break;
            case "SE":
                if (annotation.Width + dx > minimumSize) annotation.Width += dx;
                if (annotation.Height + dy > minimumSize) annotation.Height += dy;
                break;
        }
        annotation.IsApplied = false;
        _lastPointerPosition = position;
    }

    private bool Move(IReadOnlyList<PdfAnnotation> annotations, Point position)
    {
        if (!_hasMoved)
        {
            double distance = Math.Sqrt(
                Math.Pow(position.X - _lastPointerPosition.X, 2) +
                Math.Pow(position.Y - _lastPointerPosition.Y, 2));
            if (distance < 3)
                return false;
            _hasMoved = true;
        }

        double dx = (position.X - _lastPointerPosition.X) / PdfToPixels;
        double dy = (position.Y - _lastPointerPosition.Y) / PdfToPixels;
        foreach (PdfAnnotation annotation in annotations)
        {
            annotation.X += dx;
            annotation.Y += dy;
            annotation.IsApplied = false;
        }
        _lastPointerPosition = position;
        return true;
    }
}
