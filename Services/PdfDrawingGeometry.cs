using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>Shared vector geometry for canvas previews, hit testing and PDF ink.</summary>
public static class PdfDrawingGeometry
{
    public static bool IsDrawingTool(EditToolMode mode) => mode is EditToolMode.DrawFreehand or
        EditToolMode.DrawRectangle or EditToolMode.DrawEllipse or EditToolMode.DrawArrow or EditToolMode.DrawPolygon or
        EditToolMode.DrawTriangleArrow or EditToolMode.DrawOutlineArrow;

    public static bool IsArrow(DrawingKind kind) => kind is DrawingKind.Arrow or DrawingKind.TriangleArrow or DrawingKind.OutlineArrow;

    public static bool IsFilledStroke(PdfAnnotation annotation, int strokeIndex) =>
        annotation.DrawingKind == DrawingKind.TriangleArrow && strokeIndex == 1;

    public static DrawingKind GetKind(EditToolMode mode) => mode switch
    {
        EditToolMode.DrawRectangle => DrawingKind.Rectangle,
        EditToolMode.DrawEllipse => DrawingKind.Ellipse,
        EditToolMode.DrawArrow => DrawingKind.Arrow,
        EditToolMode.DrawTriangleArrow => DrawingKind.TriangleArrow,
        EditToolMode.DrawOutlineArrow => DrawingKind.OutlineArrow,
        EditToolMode.DrawPolygon => DrawingKind.Polygon,
        _ => DrawingKind.Freehand
    };

    public static PdfAnnotation Create(DrawingKind kind, IReadOnlyList<PdfPathPoint> points,
        int pageIndex, string color, double lineWidth)
    {
        var anchors = points.Select(p => p.Clone()).ToList();
        if (IsArrow(kind) && anchors.Count >= 2)
        {
            var start = anchors[0]; var end = anchors[^1];
            double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            double length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
            double head = Math.Min(Math.Max(8, lineWidth * 4), length * .4);
            anchors = new() { start, end,
                new() { X = end.X - head * Math.Cos(angle - Math.PI / 6), Y = end.Y - head * Math.Sin(angle - Math.PI / 6) },
                new() { X = end.X - head * Math.Cos(angle + Math.PI / 6), Y = end.Y - head * Math.Sin(angle + Math.PI / 6) } };
            if (kind == DrawingKind.OutlineArrow)
            {
                var left = anchors[2]; var right = anchors[3];
                var neck = Midpoint(left, right);
                double halfBody = Math.Min(Math.Max(lineWidth * 1.5, 3), head * .25);
                double nx = -Math.Sin(angle) * halfBody, ny = Math.Cos(angle) * halfBody;
                anchors = new() { new() { X = start.X + nx, Y = start.Y + ny },
                    new() { X = neck.X + nx, Y = neck.Y + ny }, left, end, right,
                    new() { X = neck.X - nx, Y = neck.Y - ny },
                    new() { X = start.X - nx, Y = start.Y - ny } };
            }
        }
        double x = anchors.Min(p => p.X), y = anchors.Min(p => p.Y);
        double width = Math.Max(anchors.Max(p => p.X) - x, 1);
        double height = Math.Max(anchors.Max(p => p.Y) - y, 1);
        return new PdfAnnotation
        {
            Type = AnnotationType.Drawing, DrawingKind = kind, PageIndex = pageIndex,
            X = x, Y = y, Width = width, Height = height, Color = color, LineWidth = lineWidth,
            DrawingPoints = anchors.Select(p => new PdfPathPoint { X = (p.X - x) / width, Y = (p.Y - y) / height }).ToList()
        };
    }

    public static List<List<PdfPathPoint>> GetStrokes(PdfAnnotation annotation)
    {
        PdfPathPoint Point(double x, double y) => new()
        { X = annotation.X + x * annotation.Width, Y = annotation.Y + y * annotation.Height };
        if (annotation.DrawingKind == DrawingKind.Rectangle)
            return new() { new() { Point(0, 0), Point(1, 0), Point(1, 1), Point(0, 1), Point(0, 0) } };
        if (annotation.DrawingKind == DrawingKind.Ellipse)
            return new() { Enumerable.Range(0, 129).Select(i =>
                Point((1 + Math.Cos(i * Math.Tau / 128)) / 2, (1 + Math.Sin(i * Math.Tau / 128)) / 2)).ToList() };
        var points = annotation.DrawingPoints.Select(p => Point(p.X, p.Y)).ToList();
        if (annotation.DrawingKind == DrawingKind.Arrow && points.Count >= 4)
            return new() { new() { points[0], points[1] }, new() { points[2], points[1], points[3] } };
        if (annotation.DrawingKind == DrawingKind.TriangleArrow && points.Count >= 4)
            return new() { new() { points[0], Midpoint(points[2], points[3]) },
                new() { points[2], points[1], points[3], points[2] } };
        if (annotation.DrawingKind is DrawingKind.Polygon or DrawingKind.OutlineArrow && points.Count >= 3)
            points.Add(points[0].Clone());
        if (annotation.DrawingKind == DrawingKind.Freehand)
            points = SmoothFreehand(points);
        return new() { points };
    }

    private static PdfPathPoint Midpoint(PdfPathPoint a, PdfPathPoint b) =>
        new() { X = (a.X + b.X) / 2, Y = (a.Y + b.Y) / 2 };

    private static List<PdfPathPoint> SmoothFreehand(List<PdfPathPoint> points)
    {
        if (points.Count < 3) return points;
        // Quadratic midpoint splines have continuous tangents and stay within
        // the sampled stroke bounds. Keep the original endpoints and metadata.
        var result = new List<PdfPathPoint> { points[0] };
        var start = Midpoint(points[0], points[1]);
        result.Add(start);
        for (int i = 1; i < points.Count - 1; i++)
        {
            var end = Midpoint(points[i], points[i + 1]);
            FlattenQuadratic(start, points[i], end, result, 0);
            start = end;
        }
        result.Add(points[^1]);
        return result;
    }

    private static void FlattenQuadratic(PdfPathPoint start, PdfPathPoint control, PdfPathPoint end,
        List<PdfPathPoint> output, int depth)
    {
        // The curve/chord deviation is half the distance between the control
        // and chord midpoint. Subdivide to 0.1 PDF point for smooth zoomed output.
        var middle = Midpoint(start, end);
        double dx = control.X - middle.X, dy = control.Y - middle.Y;
        if (depth >= 12 || dx * dx + dy * dy <= .04)
        { output.Add(end); return; }
        var left = Midpoint(start, control); var right = Midpoint(control, end);
        var split = Midpoint(left, right);
        FlattenQuadratic(start, left, split, output, depth + 1);
        FlattenQuadratic(split, right, end, output, depth + 1);
    }

    public static bool HitTest(PdfAnnotation annotation, double x, double y)
    {
        double tolerance = Math.Max(5, annotation.LineWidth / 2 + 3);
        var strokes = GetStrokes(annotation);
        if (annotation.DrawingKind == DrawingKind.TriangleArrow && strokes.Count >= 2)
        {
            var head = strokes[1];
            double Cross(PdfPathPoint a, PdfPathPoint b) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
            double a = Cross(head[0], head[1]), b = Cross(head[1], head[2]), c = Cross(head[2], head[0]);
            double area = (head[1].X - head[0].X) * (head[2].Y - head[0].Y) -
                (head[1].Y - head[0].Y) * (head[2].X - head[0].X);
            if (Math.Abs(area) > .000001 && !((a < 0 || b < 0 || c < 0) && (a > 0 || b > 0 || c > 0)))
                return true;
        }
        foreach (var stroke in strokes)
            for (int i = 1; i < stroke.Count; i++)
            {
                var a = stroke[i - 1]; var b = stroke[i];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / Math.Max(dx * dx + dy * dy, .000001), 0, 1);
                if (Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2) <= tolerance * tolerance)
                    return true;
            }
        return false;
    }
}
