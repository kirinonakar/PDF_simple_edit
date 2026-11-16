using Loc = PDF_simple_edit.Services.LocalizationService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.System;

namespace PDF_simple_edit.Controllers;

/// <summary>Owns an unfinished drawing; only a completed gesture changes the document.</summary>
public sealed class DrawingInteractionController
{
    private const double PdfToPixels = 96.0 / 72;
    private readonly Canvas _canvas;
    private readonly TextBlock _status;
    private readonly Func<PdfDocumentManager> _getManager;
    private readonly Func<int> _getPage;
    private readonly Func<EditToolMode> _getMode;
    private readonly Func<string> _getColor;
    private readonly Func<double> _getWidth;
    private readonly Func<bool> _getFill;
    private readonly Action<PdfAnnotation> _commit;
    private readonly List<PdfPathPoint> _points = new();
    private PdfDocumentManager? _manager;
    private int _page;
    private DrawingKind _kind;
    private string _color = "#000000";
    private double _width;
    private bool _isFilled;
    private uint? _pointerId;
    private PdfPathPoint? _hover;
    private FrameworkElement? _preview;

    public DrawingInteractionController(Canvas canvas, TextBlock status,
        Func<PdfDocumentManager> getManager, Func<int> getPage, Func<EditToolMode> getMode,
        Func<string> getColor, Func<double> getWidth, Func<bool> getFill, Action<PdfAnnotation> commit)
    {
        _canvas = canvas; _status = status; _getManager = getManager; _getPage = getPage;
        _getMode = getMode; _getColor = getColor; _getWidth = getWidth; _getFill = getFill; _commit = commit;
        canvas.PointerCaptureLost += (_, e) => { if (_pointerId == e.Pointer.PointerId) Cancel(); };
        canvas.PointerCanceled += (_, _) => Cancel();
    }

    private bool IsCurrent => _manager == _getManager() && _page == _getPage() &&
        PdfDrawingGeometry.IsDrawingTool(_getMode()) && PdfDrawingGeometry.GetKind(_getMode()) == _kind;

    private PdfPathPoint GetPoint(Point position) => new()
    {
        X = Math.Clamp(position.X / PdfToPixels, 0, _canvas.Width / PdfToPixels),
        Y = Math.Clamp(position.Y / PdfToPixels, 0, _canvas.Height / PdfToPixels)
    };

    public void Pressed(PointerRoutedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(_canvas);
        if (!pointer.Properties.IsLeftButtonPressed) return;
        if (_manager != null && !IsCurrent) Cancel();
        if (_pointerId != null) return;
        var point = GetPoint(pointer.Position);
        if (_points.Count == 0)
        {
            _manager = _getManager(); _page = _getPage();
            _kind = PdfDrawingGeometry.GetKind(_getMode()); _color = _getColor(); _width = _getWidth();
            _isFilled = _getFill();
        }
        if (_kind == DrawingKind.Bezier)
        {
            _points.Add(point);
            _hover = null;
            e.Handled = true;
            if (_points.Count == 4) { Complete(); return; }
            _status.Text = Loc.Get(_points.Count switch
            {
                1 => "BezierPlaceFirstControl",
                2 => "BezierPlaceSecondControl",
                _ => "BezierPlaceEnd"
            });
        }
        else if (_kind == DrawingKind.Polygon)
        {
            if (_points.Count >= 3 && Distance(point, _points[0]) < 6)
            { Complete(); e.Handled = true; return; }
            if (_points.Count == 0 || Distance(point, _points[^1]) > 1)
                _points.Add(point);
            _hover = null;
            _status.Text = Loc.Get("PolygonClickToAddVerticesDoubleClickEnterTo");
        }
        else
        {
            _points.Add(point); _hover = point;
            _pointerId = e.Pointer.PointerId;
            _canvas.CapturePointer(e.Pointer);
            _status.Text = Loc.Get("DrawingEscCancel");
        }
        Redraw();
        // Keep DoubleTapped available for polygon completion.
    }

    public void Moved(PointerRoutedEventArgs e)
    {
        if (_manager == null) return;
        if (!IsCurrent) { Cancel(); return; }
        if (_pointerId != null && _pointerId != e.Pointer.PointerId) return;
        var pointer = e.GetCurrentPoint(_canvas);
        if (_kind is not (DrawingKind.Polygon or DrawingKind.Bezier) && !pointer.Properties.IsLeftButtonPressed)
        { Cancel(); return; }
        _hover = GetPoint(pointer.Position);
        if (_kind == DrawingKind.Freehand)
            foreach (var sample in e.GetIntermediatePoints(_canvas).OrderBy(p => p.Timestamp))
            {
                var point = GetPoint(sample.Position);
                if (Distance(_points[^1], point) >= .5) _points.Add(point);
            }
        Redraw();
    }

    public void Released(PointerRoutedEventArgs e)
    {
        if (_manager == null || _kind == DrawingKind.Polygon || _pointerId != e.Pointer.PointerId) return;
        if (!IsCurrent) { Cancel(); return; }
        _hover = GetPoint(e.GetCurrentPoint(_canvas).Position);
        if (_kind == DrawingKind.Freehand && Distance(_points[^1], _hover) > .01)
            _points.Add(_hover);
        _pointerId = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        Complete();
        e.Handled = true;
    }

    public bool HandleKey(VirtualKey key)
    {
        if (_manager == null) return false;
        if (key == VirtualKey.Escape) { Cancel(); _status.Text = Loc.Get("DrawingCanceled"); return true; }
        if (key == VirtualKey.Enter && _kind == DrawingKind.Polygon) { Complete(); return true; }
        return false;
    }

    public void DoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        if (_kind == DrawingKind.Polygon && _manager != null) { Complete(); e.Handled = true; }
    }

    public void Cancel()
    {
        bool hadCapture = _pointerId.HasValue;
        _pointerId = null; _manager = null; _points.Clear(); _hover = null;
        if (_preview != null) _canvas.Children.Remove(_preview);
        _preview = null;
        if (hadCapture) _canvas.ReleasePointerCaptures();
    }

    public void Redraw()
    {
        if (_manager == null) return;
        if (!IsCurrent) { Cancel(); return; }
        if (_preview != null) _canvas.Children.Remove(_preview);
        _preview = null;
        if (_kind == DrawingKind.Bezier)
        {
            _preview = CreateBezierPreview();
            _canvas.Children.Add(_preview);
            return;
        }
        var points = GetPreviewPoints();
        if (points.Count < 2) return;
        var drawing = PdfDrawingGeometry.Create(_kind, points, _page, _color, _width, _isFilled);
        _preview = CreatePositionedElement(drawing);
        _canvas.Children.Add(_preview);
    }

    private Canvas CreateBezierPreview()
    {
        var preview = new Canvas { IsHitTestVisible = false };
        var points = _points.ToList();
        if (_hover != null) points.Add(_hover);
        var guideBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        if (points.Count >= 2)
        {
            var guide = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = guideBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            foreach (var point in points) guide.Points.Add(new Point(point.X * PdfToPixels, point.Y * PdfToPixels));
            preview.Children.Add(guide);
        }
        if (points.Count == 4)
        {
            var drawing = PdfDrawingGeometry.Create(DrawingKind.Bezier, points, _page, _color, _width);
            preview.Children.Add(CreatePositionedElement(drawing));
        }
        foreach (var point in _points)
        {
            var marker = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = guideBrush,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1
            };
            Canvas.SetLeft(marker, point.X * PdfToPixels - marker.Width / 2);
            Canvas.SetTop(marker, point.Y * PdfToPixels - marker.Height / 2);
            preview.Children.Add(marker);
        }
        return preview;
    }

    private static FrameworkElement CreatePositionedElement(PdfAnnotation drawing)
    {
        var element = CreateElement(drawing);
        Canvas.SetLeft(element, drawing.X * PdfToPixels);
        Canvas.SetTop(element, drawing.Y * PdfToPixels);
        return element;
    }

    private List<PdfPathPoint> GetPreviewPoints()
    {
        var points = _points.ToList();
        if (_kind is DrawingKind.Line or DrawingKind.Rectangle or DrawingKind.Ellipse || PdfDrawingGeometry.IsArrow(_kind))
            return new() { points[0], _hover ?? points[0] };
        if (_kind == DrawingKind.Polygon && _hover != null) points.Add(_hover);
        return points;
    }

    private void Complete()
    {
        if (!IsCurrent) { Cancel(); return; }
        var points = _kind is DrawingKind.Polygon or DrawingKind.Bezier ? _points.ToList() : GetPreviewPoints();
        int minimumPoints = _kind switch { DrawingKind.Bezier => 4, DrawingKind.Polygon => 3, _ => 2 };
        bool valid = points.Count >= minimumPoints &&
            points.Any(p => Distance(points[0], p) >= 1);
        if (_kind is DrawingKind.Rectangle or DrawingKind.Ellipse)
            valid &= Math.Abs(points[^1].X - points[0].X) >= 1 && Math.Abs(points[^1].Y - points[0].Y) >= 1;
        if (_kind == DrawingKind.Polygon && valid)
        {
            double area = 0;
            for (int i = 0; i < points.Count; i++)
                area += points[i].X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * points[i].Y;
            valid = Math.Abs(area) >= 1;
        }
        if (!valid && _kind == DrawingKind.Polygon)
        { _status.Text = Loc.Get("AddAtLeastThreeNonCollinearVerticesToThe"); return; }
        var drawing = valid ? PdfDrawingGeometry.Create(_kind, points, _page, _color, _width, _isFilled) : null;
        Cancel();
        if (drawing != null) { _commit(drawing); _status.Text = Loc.Get("DrawingAddedUseSelectGraphicsGToEditIt"); }
        else if (_kind == DrawingKind.Bezier) _status.Text = Loc.Get("BezierPointsTooClose");
    }

    private static double Distance(PdfPathPoint a, PdfPathPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public static FrameworkElement CreateElement(PdfAnnotation drawing)
    {
        double width = drawing.Width * PdfToPixels;
        double height = drawing.Height * PdfToPixels;
        double strokeWidth = drawing.LineWidth * PdfToPixels;
        // A Path clips its stroke to its layout bounds. Reserve half the stroke
        // plus an antialiasing pixel on every edge, including pointed corners.
        double padding = strokeWidth / 2 + 1;
        var geometry = new PathGeometry();
        Point Local(PdfPathPoint p) => new(
            (p.X - drawing.X) * PdfToPixels + padding,
            (p.Y - drawing.Y) * PdfToPixels + padding);
        if (drawing.DrawingKind == DrawingKind.Bezier)
        {
            var points = PdfDrawingGeometry.GetPathPoints(drawing);
            var figure = new PathFigure { StartPoint = Local(points[0]), IsFilled = false };
            figure.Segments.Add(new BezierSegment { Point1 = Local(points[1]), Point2 = Local(points[2]), Point3 = Local(points[3]) });
            geometry.Figures.Add(figure);
        }
        else
        {
            var strokes = PdfDrawingGeometry.GetStrokes(drawing);
            for (int i = 0; i < strokes.Count; i++)
            {
                var stroke = strokes[i];
                if (stroke.Count < 2) continue;
                var figure = new PathFigure { StartPoint = Local(stroke[0]), IsFilled = PdfDrawingGeometry.IsFilledStroke(drawing, i) };
                foreach (var point in stroke.Skip(1)) figure.Segments.Add(new LineSegment { Point = Local(point) });
                geometry.Figures.Add(figure);
            }
        }
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry, Width = width + padding * 2, Height = height + padding * 2,
            Stroke = drawing.DrawingKind == DrawingKind.TriangleArrow ? null : new SolidColorBrush(EditorColorService.Parse(drawing.Color)),
            Fill = new SolidColorBrush(EditorColorService.Parse(drawing.Color)),
            StrokeThickness = strokeWidth, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        // Keep the outer bounds and rotation center in PDF coordinates. Canvas
        // allows the padded child to extend beyond those logical bounds.
        var container = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        Canvas.SetLeft(path, -padding);
        Canvas.SetTop(path, -padding);
        container.Children.Add(path);
        return container;
    }
}
