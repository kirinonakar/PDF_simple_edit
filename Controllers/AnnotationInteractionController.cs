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
    OriginalTextRemovalFailed,
    OriginalImageRemovalFailed,
    MixedOriginalContentRemovalFailed
}

public enum AnnotationResizeResult
{
    NotActive,
    Completed,
    OriginalTextRemovalFailed,
    OriginalImageRemovalFailed
}

public sealed class AnnotationInteractionController
{
    private const double PdfToPixels = 96.0 / 72.0;
    private readonly Dictionary<PdfAnnotation, Point> _moveStartPositions = new();
    private readonly Dictionary<PdfAnnotation, bool> _moveStartAppliedStates = new();
    private bool _hasMoved;
    private string? _resizeHandle;
    private PdfAnnotation? _resizeAnnotation;
    private (double X, double Y, double Width, double Height)? _resizeStartBounds;
    private bool _resizeWasApplied;
    private Point _lastPointerPosition;
    private Point _highlightStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _highlightRectangle;

    public bool IsMoving { get; private set; }
    public bool IsResizing { get; private set; }
    public bool IsDrawingHighlight => _highlightRectangle != null;

    public void BeginResize(PdfAnnotation annotation, string direction, Point position)
    {
        IsResizing = true;
        _resizeAnnotation = annotation;
        _resizeStartBounds = (annotation.X, annotation.Y, annotation.Width, annotation.Height);
        _resizeWasApplied = annotation.IsApplied;
        _resizeHandle = direction;
        _lastPointerPosition = position;
    }

    public void BeginMove(IReadOnlyList<PdfAnnotation> annotations, Point position)
    {
        IsMoving = true;
        _hasMoved = false;
        _moveStartPositions.Clear();
        _moveStartAppliedStates.Clear();
        foreach (PdfAnnotation annotation in annotations)
        {
            _moveStartPositions[annotation] = new Point(annotation.X, annotation.Y);
            _moveStartAppliedStates[annotation] = annotation.IsApplied;
        }
        _lastPointerPosition = position;
    }

    public bool CancelMove()
    {
        if (!IsMoving)
            return false;

        foreach ((PdfAnnotation annotation, Point position) in _moveStartPositions)
        {
            double dx = position.X - annotation.X;
            double dy = position.Y - annotation.Y;
            annotation.X = position.X;
            annotation.Y = position.Y;
            ShiftSignaturePoints(annotation, dx, dy);
            if (_moveStartAppliedStates.TryGetValue(annotation, out bool wasApplied))
                annotation.IsApplied = wasApplied;
        }

        IsMoving = false;
        _hasMoved = false;
        _moveStartPositions.Clear();
        _moveStartAppliedStates.Clear();
        return true;
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

    public async Task<AnnotationResizeResult> CompleteResizeAsync(
        PdfDocumentManager manager,
        int pageIndex)
    {
        if (!IsResizing)
            return AnnotationResizeResult.NotActive;
        IsResizing = false;
        _resizeHandle = null;

        PdfAnnotation? annotation = _resizeAnnotation;
        _resizeAnnotation = null;
        if (annotation != null && _resizeWasApplied &&
            annotation.Type is AnnotationType.Text or AnnotationType.FreeText &&
            _resizeStartBounds is { } savedBounds &&
            !await manager.RemoveAppliedTextAnnotationAsync(
                pageIndex,
                annotation,
                savedBounds.X,
                savedBounds.Y,
                savedBounds.Width,
                savedBounds.Height))
        {
            annotation.X = savedBounds.X;
            annotation.Y = savedBounds.Y;
            annotation.Width = savedBounds.Width;
            annotation.Height = savedBounds.Height;
            annotation.IsApplied = true;
            _resizeStartBounds = null;
            _resizeWasApplied = false;
            return AnnotationResizeResult.OriginalTextRemovalFailed;
        }
        if (annotation != null && _resizeWasApplied &&
            annotation.Type is AnnotationType.Text or AnnotationType.FreeText)
        {
            annotation.IsApplied = false;
            annotation.IsOriginalTextReplacement = false;
        }
        if (annotation?.IsOriginalImageReplacement == true)
        {
            bool removed = await manager.RemoveOriginalImageAnnotationsAsync(
                pageIndex, new[] { annotation });
            if (!removed)
            {
                if (_resizeStartBounds is { } bounds)
                {
                    annotation.X = bounds.X;
                    annotation.Y = bounds.Y;
                    annotation.Width = bounds.Width;
                    annotation.Height = bounds.Height;
                }
                _resizeStartBounds = null;
                return AnnotationResizeResult.OriginalImageRemovalFailed;
            }
            annotation.IsOriginalImageReplacement = false;
        }

        _resizeStartBounds = null;
        _resizeWasApplied = false;
        return AnnotationResizeResult.Completed;
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
            _moveStartAppliedStates.Clear();
            return AnnotationMoveResult.NotMoved;
        }

        var native = selectedAnnotations.Where(a => a.NativeText != null).ToList();
        if (native.Count > 0)
        {
            if (selectedAnnotations.Any(a => a.NativeText == null && (a.IsOriginalImageReplacement || a.IsOriginalTextReplacement || a.IsApplied)))
            { RestoreMoveStartPositions(); return AnnotationMoveResult.MixedOriginalContentRemovalFailed; }
            try
            {
                await manager.ApplyNativeTextEditsAsync(pageIndex, native.Select(a => new NativePdfTextEdit(a.NativeText!,
                    a.NativeText!.Text, a.X - a.NativeText.Bounds.X, a.Y - a.NativeText.Bounds.Y)).ToList());
                _moveStartPositions.Clear(); _moveStartAppliedStates.Clear();
                return AnnotationMoveResult.Completed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex); RestoreMoveStartPositions();
                return AnnotationMoveResult.OriginalTextRemovalFailed;
            }
        }

        var appliedText = selectedAnnotations
            .Where(annotation =>
                _moveStartAppliedStates.TryGetValue(annotation, out bool wasApplied) &&
                wasApplied &&
                annotation.Type is AnnotationType.Text or AnnotationType.FreeText)
            .ToList();
        var originalText = selectedAnnotations
            .Where(annotation => annotation.IsOriginalTextReplacement && !annotation.IsApplied)
            .ToList();
        var originalImages = selectedAnnotations
            .Where(annotation => annotation.IsOriginalImageReplacement)
            .ToList();
        if (originalText.Count > 0 && originalImages.Count > 0)
        {
            RestoreMoveStartPositions();
            return AnnotationMoveResult.MixedOriginalContentRemovalFailed;
        }
        foreach (PdfAnnotation annotation in appliedText)
        {
            if (!_moveStartPositions.TryGetValue(annotation, out Point startPosition) ||
                !await manager.RemoveAppliedTextAnnotationAsync(
                    pageIndex,
                    annotation,
                    startPosition.X,
                    startPosition.Y,
                    annotation.Width,
                    annotation.Height))
            {
                RestoreMoveStartPositions();
                return AnnotationMoveResult.OriginalTextRemovalFailed;
            }
        }
        if (originalText.Count > 0 &&
            !await manager.RemoveOriginalTextAnnotationsAsync(pageIndex, originalText))
        {
            RestoreMoveStartPositions();
            return AnnotationMoveResult.OriginalTextRemovalFailed;
        }
        if (originalImages.Count > 0 &&
            !await manager.RemoveOriginalImageAnnotationsAsync(pageIndex, originalImages))
        {
            RestoreMoveStartPositions();
            return AnnotationMoveResult.OriginalImageRemovalFailed;
        }

        foreach (PdfAnnotation annotation in originalText)
            annotation.IsOriginalTextReplacement = false;
        foreach (PdfAnnotation annotation in appliedText)
        {
            annotation.IsApplied = false;
            annotation.IsOriginalTextReplacement = false;
        }
        foreach (PdfAnnotation annotation in originalImages)
            annotation.IsOriginalImageReplacement = false;
        _moveStartPositions.Clear();
        _moveStartAppliedStates.Clear();
        return AnnotationMoveResult.Completed;
    }

    private void RestoreMoveStartPositions()
    {
        foreach ((PdfAnnotation annotation, Point position) in _moveStartPositions)
        {
            double dx = position.X - annotation.X;
            double dy = position.Y - annotation.Y;
            annotation.X = position.X;
            annotation.Y = position.Y;
            ShiftSignaturePoints(annotation, dx, dy);
            if (_moveStartAppliedStates.TryGetValue(annotation, out bool wasApplied))
                annotation.IsApplied = wasApplied;
        }
        _moveStartPositions.Clear();
        _moveStartAppliedStates.Clear();
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
        if (annotation.Type == AnnotationType.Signature)
        {
            ResizeSignature(annotation, dx, dy, minimumSize);
            annotation.IsApplied = false;
            _lastPointerPosition = position;
            return;
        }

        switch (_resizeHandle)
        {
            case "NW":
                ResizeCorner(annotation, dx, dy, moveLeft: true, moveTop: true, minimumSize: minimumSize);
                break;
            case "N":
                if (annotation.Height - dy > minimumSize) { annotation.Y += dy; annotation.Height -= dy; }
                break;
            case "NE":
                ResizeCorner(annotation, dx, dy, moveLeft: false, moveTop: true, minimumSize: minimumSize);
                break;
            case "W":
                if (annotation.Width - dx > minimumSize) { annotation.X += dx; annotation.Width -= dx; }
                break;
            case "E":
                if (annotation.Width + dx > minimumSize) annotation.Width += dx;
                break;
            case "SW":
                ResizeCorner(annotation, dx, dy, moveLeft: true, moveTop: false, minimumSize: minimumSize);
                break;
            case "S":
                if (annotation.Height + dy > minimumSize) annotation.Height += dy;
                break;
            case "SE":
                ResizeCorner(annotation, dx, dy, moveLeft: false, moveTop: false, minimumSize: minimumSize);
                break;
        }
        // The original applied state is kept separately until completion so the
        // pointer preview can render at its temporary bounds without losing the
        // information needed to remove the baked PDF text.
        annotation.IsApplied = false;
        _lastPointerPosition = position;
    }

    private void ResizeSignature(PdfAnnotation annotation, double dx, double dy, double minimumSize)
    {
        double oldX = annotation.X;
        double oldY = annotation.Y;
        double oldWidth = Math.Max(annotation.Width, 0.1);
        double oldHeight = Math.Max(annotation.Height, 0.1);
        double newX = oldX;
        double newY = oldY;
        double newWidth = oldWidth;
        double newHeight = oldHeight;

        bool isCorner = _resizeHandle is "NW" or "NE" or "SW" or "SE";
        if (isCorner)
        {
            bool moveLeft = _resizeHandle is "NW" or "SW";
            bool moveTop = _resizeHandle is "NW" or "NE";
            double widthDelta = moveLeft ? -dx : dx;
            double heightDelta = moveTop ? -dy : dy;
            double widthScale = (oldWidth + widthDelta) / oldWidth;
            double heightScale = (oldHeight + heightDelta) / oldHeight;
            double scale = Math.Abs(widthScale - 1) >= Math.Abs(heightScale - 1)
                ? widthScale
                : heightScale;
            double minimumScale = Math.Max(
                minimumSize / oldWidth,
                minimumSize / oldHeight);
            scale = Math.Max(scale, minimumScale);

            double right = oldX + oldWidth;
            double bottom = oldY + oldHeight;
            newWidth = oldWidth * scale;
            newHeight = oldHeight * scale;
            if (moveLeft)
                newX = right - newWidth;
            if (moveTop)
                newY = bottom - newHeight;
        }
        else
        {
            switch (_resizeHandle)
            {
                case "N":
                    if (oldHeight - dy > minimumSize)
                    {
                        newY += dy;
                        newHeight -= dy;
                    }
                    break;
                case "W":
                    if (oldWidth - dx > minimumSize)
                    {
                        newX += dx;
                        newWidth -= dx;
                    }
                    break;
                case "E":
                    if (oldWidth + dx > minimumSize)
                        newWidth += dx;
                    break;
                case "S":
                    if (oldHeight + dy > minimumSize)
                        newHeight += dy;
                    break;
            }
        }

        foreach (PdfPathPoint point in annotation.SignaturePoints)
        {
            double normalizedX = (point.X - oldX) / oldWidth;
            double normalizedY = (point.Y - oldY) / oldHeight;
            point.X = newX + normalizedX * newWidth;
            point.Y = newY + normalizedY * newHeight;
        }

        annotation.X = newX;
        annotation.Y = newY;
        annotation.Width = newWidth;
        annotation.Height = newHeight;
    }

    private static void ResizeCorner(
        PdfAnnotation annotation,
        double dx,
        double dy,
        bool moveLeft,
        bool moveTop,
        double minimumSize)
    {
        if (annotation.Type != AnnotationType.Image)
        {
            double resizedWidth = annotation.Width + (moveLeft ? -dx : dx);
            if (resizedWidth > minimumSize)
            {
                if (moveLeft)
                    annotation.X += dx;
                annotation.Width = resizedWidth;
            }

            double resizedHeight = annotation.Height + (moveTop ? -dy : dy);
            if (resizedHeight > minimumSize)
            {
                if (moveTop)
                    annotation.Y += dy;
                annotation.Height = resizedHeight;
            }
            return;
        }

        if (annotation.Width <= 0 || annotation.Height <= 0)
            return;

        double widthDelta = moveLeft ? -dx : dx;
        double heightDelta = moveTop ? -dy : dy;
        double widthScale = (annotation.Width + widthDelta) / annotation.Width;
        double heightScale = (annotation.Height + heightDelta) / annotation.Height;
        double scale = Math.Abs(widthScale - 1) >= Math.Abs(heightScale - 1)
            ? widthScale
            : heightScale;
        double minimumScale = Math.Max(
            minimumSize / annotation.Width,
            minimumSize / annotation.Height);
        scale = Math.Max(scale, minimumScale);

        double right = annotation.X + annotation.Width;
        double bottom = annotation.Y + annotation.Height;
        double newWidth = annotation.Width * scale;
        double newHeight = annotation.Height * scale;

        if (moveLeft)
            annotation.X = right - newWidth;
        if (moveTop)
            annotation.Y = bottom - newHeight;
        annotation.Width = newWidth;
        annotation.Height = newHeight;
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
            ShiftSignaturePoints(annotation, dx, dy);
            // The original applied state is tracked in _moveStartAppliedStates.
            // Clearing the display flag here keeps the drag preview responsive;
            // completion still knows which baked PDF text must be removed.
            annotation.IsApplied = false;
        }
        _lastPointerPosition = position;
        return true;
    }

    private static void ShiftSignaturePoints(PdfAnnotation annotation, double dx, double dy)
    {
        if (annotation.Type != AnnotationType.Signature ||
            (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon))
            return;

        foreach (PdfPathPoint point in annotation.SignaturePoints)
        {
            point.X += dx;
            point.Y += dy;
        }
    }
}
