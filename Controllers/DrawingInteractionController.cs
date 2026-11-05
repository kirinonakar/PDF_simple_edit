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
    private readonly Action<PdfAnnotation> _commit;
    private readonly List<PdfPathPoint> _points = new();
    private PdfDocumentManager? _manager;
    private int _page;
    private DrawingKind _kind;
    private string _color = "#000000";
    private double _width;
    private uint? _pointerId;
    private PdfPathPoint? _hover;
    private FrameworkElement? _preview;

    public DrawingInteractionController(Canvas canvas, TextBlock status,
        Func<PdfDocumentManager> getManager, Func<int> getPage, Func<EditToolMode> getMode,
        Func<string> getColor, Func<double> getWidth, Action<PdfAnnotation> commit)
    {
        _canvas = canvas; _status = status; _getManager = getManager; _getPage = getPage;
        _getMode = getMode; _getColor = getColor; _getWidth = getWidth; _commit = commit;
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
        }
        if (_kind == DrawingKind.Polygon)
        {
            if (_points.Count >= 3 && Distance(point, _points[0]) < 6)
            { Complete(); e.Handled = true; return; }
            if (_points.Count == 0 || Distance(point, _points[^1]) > 1)
                _points.Add(point);
            _hover = null;
            _status.Text = "다각형: 클릭으로 꼭짓점 추가 · 더블클릭/Enter로 완성 · Esc로 취소";
        }
        else
        {
            _points.Add(point); _hover = point;
            _pointerId = e.Pointer.PointerId;
            _canvas.CapturePointer(e.Pointer);
            _status.Text = "그리는 중... (Esc: 취소)";
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
        if (_kind != DrawingKind.Polygon && !pointer.Properties.IsLeftButtonPressed)
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
        if (key == VirtualKey.Escape) { Cancel(); _status.Text = "그리기가 취소되었습니다."; return true; }
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
        var points = GetPreviewPoints();
        if (points.Count < 2) return;
        var drawing = PdfDrawingGeometry.Create(_kind, points, _page, _color, _width);
        _preview = CreateElement(drawing);
        Canvas.SetLeft(_preview, drawing.X * PdfToPixels);
        Canvas.SetTop(_preview, drawing.Y * PdfToPixels);
        _canvas.Children.Add(_preview);
    }

    private List<PdfPathPoint> GetPreviewPoints()
    {
        var points = _points.ToList();
        if (_kind is DrawingKind.Rectangle or DrawingKind.Ellipse || PdfDrawingGeometry.IsArrow(_kind))
            return new() { points[0], _hover ?? points[0] };
        if (_kind == DrawingKind.Polygon && _hover != null) points.Add(_hover);
        return points;
    }

    private void Complete()
    {
        if (!IsCurrent) { Cancel(); return; }
        var points = _kind == DrawingKind.Polygon ? _points.ToList() : GetPreviewPoints();
        bool valid = points.Count >= (_kind == DrawingKind.Polygon ? 3 : 2) &&
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
        { _status.Text = "다각형에 서로 일직선이 아닌 꼭짓점 3개 이상을 추가하세요."; return; }
        var drawing = valid ? PdfDrawingGeometry.Create(_kind, points, _page, _color, _width) : null;
        Cancel();
        if (drawing != null) { _commit(drawing); _status.Text = "그리기가 추가되었습니다. 그래픽 선택(G)으로 편집할 수 있습니다."; }
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
        var strokes = PdfDrawingGeometry.GetStrokes(drawing);
        for (int i = 0; i < strokes.Count; i++)
        {
            var stroke = strokes[i];
            if (stroke.Count < 2) continue;
            Point Local(PdfPathPoint p) => new(
                (p.X - drawing.X) * PdfToPixels + padding,
                (p.Y - drawing.Y) * PdfToPixels + padding);
            var figure = new PathFigure { StartPoint = Local(stroke[0]), IsFilled = PdfDrawingGeometry.IsFilledStroke(drawing, i) };
            foreach (var point in stroke.Skip(1)) figure.Segments.Add(new LineSegment { Point = Local(point) });
            geometry.Figures.Add(figure);
        }
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry, Width = width + padding * 2, Height = height + padding * 2,
            Stroke = new SolidColorBrush(EditorColorService.Parse(drawing.Color)),
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
