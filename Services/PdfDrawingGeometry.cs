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
        EditToolMode.DrawTriangleArrow or EditToolMode.DrawOutlineArrow or EditToolMode.DrawLine;

    public static bool IsArrow(DrawingKind kind) => kind is DrawingKind.Arrow or DrawingKind.TriangleArrow or DrawingKind.OutlineArrow;

    public static bool SupportsFill(DrawingKind kind) => kind is DrawingKind.Rectangle or DrawingKind.Ellipse or DrawingKind.Polygon;

    public static bool IsFilledStroke(PdfAnnotation annotation, int strokeIndex) =>
        strokeIndex == 0 && (annotation.DrawingKind == DrawingKind.TriangleArrow ||
            annotation.IsFilled && SupportsFill(annotation.DrawingKind));

    public static DrawingKind GetKind(EditToolMode mode) => mode switch
    {
        EditToolMode.DrawLine => DrawingKind.Line,
        EditToolMode.DrawRectangle => DrawingKind.Rectangle,
        EditToolMode.DrawEllipse => DrawingKind.Ellipse,
        EditToolMode.DrawArrow => DrawingKind.Arrow,
        EditToolMode.DrawTriangleArrow => DrawingKind.TriangleArrow,
        EditToolMode.DrawOutlineArrow => DrawingKind.OutlineArrow,
        EditToolMode.DrawPolygon => DrawingKind.Polygon,
        _ => DrawingKind.Freehand
    };

    public static PdfAnnotation Create(DrawingKind kind, IReadOnlyList<PdfPathPoint> points,
        int pageIndex, string color, double lineWidth, bool isFilled = false)
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
            if (kind == DrawingKind.TriangleArrow)
            {
                // Reference proportions: a square-ended shaft and a sharp,
                // filled head whose length and base are three shaft widths.
                double headLength = Math.Min(lineWidth * 3, length * .4);
                double bx = end.X - Math.Cos(angle) * headLength;
                double by = end.Y - Math.Sin(angle) * headLength;
                double nx = -Math.Sin(angle) * headLength / 2;
                double ny = Math.Cos(angle) * headLength / 2;
                anchors[2] = new() { X = bx + nx, Y = by + ny };
                anchors[3] = new() { X = bx - nx, Y = by - ny };
            }
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
            IsFilled = isFilled && SupportsFill(kind),
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
        {
            var start = points[0]; var neck = Midpoint(points[2], points[3]);
            double dx = points[1].X - start.X, dy = points[1].Y - start.Y;
            double length = Math.Max(Math.Sqrt(dx * dx + dy * dy), .000001);
            double hx = points[2].X - points[3].X, hy = points[2].Y - points[3].Y;
            double headWidth = Math.Max(Math.Sqrt(hx * hx + hy * hy), .000001);
            double halfWidth = Math.Min(Math.Min(annotation.LineWidth, length / 7.5), headWidth) / 2;
            double nx = -dy / length * halfWidth, ny = dx / length * halfWidth;
            double sx = hx / headWidth * halfWidth, sy = hy / headWidth * halfWidth;
            // Fill one continuous silhouette. Separate overlapping figures in
            // the same Path can cancel under even-odd fill and expose a white seam.
            return new() { new() {
                new() { X = start.X + nx, Y = start.Y + ny },
                new() { X = neck.X + sx, Y = neck.Y + sy },
                points[2], points[1], points[3],
                new() { X = neck.X - sx, Y = neck.Y - sy },
                new() { X = start.X - nx, Y = start.Y - ny },
                new() { X = start.X + nx, Y = start.Y + ny } } };
        }
        if (annotation.DrawingKind is DrawingKind.Polygon or DrawingKind.OutlineArrow && points.Count >= 3)
            points.Add(points[0].Clone());
        if (annotation.DrawingKind == DrawingKind.Freehand)
            points = SmoothFreehand(points);
        return new() { points };
    }

    private static PdfPathPoint Midpoint(PdfPathPoint a, PdfPathPoint b) =>
        new() { X = (a.X + b.X) / 2, Y = (a.Y + b.Y) / 2 };

    public static void UpdateLineWidth(PdfAnnotation annotation, double lineWidth)
    {
        if (annotation.DrawingKind == DrawingKind.TriangleArrow && annotation.DrawingPoints.Count >= 4)
        {
            var ends = annotation.DrawingPoints.Take(2).Select(p => new PdfPathPoint
            { X = annotation.X + p.X * annotation.Width, Y = annotation.Y + p.Y * annotation.Height }).ToList();
            var updated = Create(DrawingKind.TriangleArrow, ends, annotation.PageIndex, annotation.Color, lineWidth);
            // The new head can change the bounds center. Compensate so changing
            // thickness preserves both visible endpoints of a rotated arrow.
            double cx = annotation.X + annotation.Width / 2 - updated.X - updated.Width / 2;
            double cy = annotation.Y + annotation.Height / 2 - updated.Y - updated.Height / 2;
            double angle = annotation.Rotation * Math.PI / 180;
            annotation.X = updated.X + cx - (cx * Math.Cos(angle) - cy * Math.Sin(angle));
            annotation.Y = updated.Y + cy - (cx * Math.Sin(angle) + cy * Math.Cos(angle));
            annotation.Width = updated.Width; annotation.Height = updated.Height;
            annotation.DrawingPoints = updated.DrawingPoints;
        }
        annotation.LineWidth = lineWidth;
    }

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
        for (int strokeIndex = 0; strokeIndex < strokes.Count; strokeIndex++)
        {
            if (!IsFilledStroke(annotation, strokeIndex)) continue;
            var polygon = strokes[strokeIndex];
            bool inside = false;
            for (int i = 1; i < polygon.Count; i++)
            {
                var a = polygon[i - 1]; var b = polygon[i];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            if (inside) return true;
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
