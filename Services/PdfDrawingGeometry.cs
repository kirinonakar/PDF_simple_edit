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
        EditToolMode.DrawTriangleArrow or EditToolMode.DrawOutlineArrow or EditToolMode.DrawLine or EditToolMode.DrawBezier;

    public static bool IsArrow(DrawingKind kind) => kind is DrawingKind.Arrow or DrawingKind.TriangleArrow or DrawingKind.OutlineArrow;

    public static bool SupportsFill(DrawingKind kind) => kind is DrawingKind.Rectangle or DrawingKind.Ellipse or
        DrawingKind.Polygon or DrawingKind.Bezier;

    public static bool IsFilledStroke(PdfAnnotation annotation, int strokeIndex) =>
        strokeIndex == 0 && (annotation.DrawingKind == DrawingKind.TriangleArrow ||
            annotation.IsFilled && SupportsFill(annotation.DrawingKind));

    public static DrawingKind GetKind(EditToolMode mode) => mode switch
    {
        EditToolMode.DrawLine => DrawingKind.Line,
        EditToolMode.DrawBezier => DrawingKind.Bezier,
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
        var annotation = new PdfAnnotation
        {
            Type = AnnotationType.Drawing, DrawingKind = kind, PageIndex = pageIndex,
            IsFilled = isFilled && SupportsFill(kind), Color = color, LineWidth = lineWidth
        };
        SetBoundsAndPoints(annotation, anchors, kind == DrawingKind.Bezier ? GetBezierBounds(anchors) : BoundsOf(anchors));
        return annotation;
    }

    private static void SetBoundsAndPoints(PdfAnnotation annotation, List<PdfPathPoint> anchors, PdfTextBox bounds)
    {
        double x = bounds.X, y = bounds.Y;
        double width = Math.Max(bounds.Width, 1);
        double height = Math.Max(bounds.Height, 1);
        annotation.X = x; annotation.Y = y;
        annotation.Width = width; annotation.Height = height;
        annotation.DrawingPoints = anchors.Select(p => new PdfPathPoint { X = (p.X - x) / width, Y = (p.Y - y) / height }).ToList();
    }

    internal static List<PdfPathPoint> GetBezierPagePoints(PdfAnnotation annotation)
    {
        var rotation = GetRotation(annotation);
        return GetPathPoints(annotation).Select(point =>
        {
            var mapped = rotation.Map(point.X, point.Y);
            return new PdfPathPoint { X = mapped.X, Y = mapped.Y };
        }).ToList();
    }

    internal static bool TryMoveBezierPoint(PdfAnnotation source, PdfAnnotation target, int index, PdfPathPoint pagePoint)
    {
        var rotation = GetRotation(source);
        var point = rotation.Inverse().Map(pagePoint.X, pagePoint.Y);
        var points = GetPathPoints(source);
        if (Math.Abs(point.X - points[index].X) <= .000001 && Math.Abs(point.Y - points[index].Y) <= .000001)
            return false;
        points[index] = new PdfPathPoint { X = point.X, Y = point.Y };
        if (!IsDrawableBezier(points)) return false;

        SetBoundsAndPoints(target, points, GetBezierBounds(points));
        // The new bounds shift the rotation center. Place that center where the
        // original transform maps it so the other three points stay in place.
        var center = rotation.Map(target.X + target.Width / 2, target.Y + target.Height / 2);
        target.X = center.X - target.Width / 2;
        target.Y = center.Y - target.Height / 2;
        return true;
    }

    internal static bool IsDrawableBezier(IReadOnlyList<PdfPathPoint> points) => points.Count == 4 &&
        points.Skip(1).Any(p => Math.Abs(p.X - points[0].X) > .000001 || Math.Abs(p.Y - points[0].Y) > .000001);

    private static PdfAffineTransform GetRotation(PdfAnnotation annotation)
    {
        var bounds = new PdfTextBox(annotation.X, annotation.Y, annotation.Width, annotation.Height);
        return PdfAffineTransform.Between(bounds, bounds, annotation.Rotation);
    }

    private static PdfTextBox BoundsOf(List<PdfPathPoint> points)
    {
        double x = points.Min(p => p.X), y = points.Min(p => p.Y);
        return new(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }

    internal static PdfTextBox GetBezierBounds(IReadOnlyList<PdfPathPoint> points)
    {
        // A cubic can reach its extrema between the endpoints where the derivative is zero.
        var (left, right) = BezierRange(points[0].X, points[1].X, points[2].X, points[3].X);
        var (top, bottom) = BezierRange(points[0].Y, points[1].Y, points[2].Y, points[3].Y);
        return new(left, top, right - left, bottom - top);
    }

    private static (double Min, double Max) BezierRange(double start, double first, double second, double end)
    {
        double min = Math.Min(start, end), max = Math.Max(start, end);
        double d0 = first - start, d1 = second - first, d2 = end - second;
        double a = d0 - 2 * d1 + d2, b = 2 * (d1 - d0), c = d0;
        if (a == 0)
        {
            if (b != 0) Include(-c / b);
        }
        else
        {
            double discriminant = b * b - 4 * a * c;
            if (discriminant >= 0)
            {
                double q = -(b + Math.CopySign(Math.Sqrt(discriminant), b)) / 2;
                Include(q / a);
                if (q != 0) Include(c / q);
            }
        }
        return (min, max);

        void Include(double t)
        {
            if (t <= 0 || t >= 1) return;
            double u = 1 - t;
            double value = u * u * u * start + 3 * u * u * t * first + 3 * u * t * t * second + t * t * t * end;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
    }

    internal static List<PdfPathPoint> GetPathPoints(PdfAnnotation annotation) =>
        annotation.DrawingPoints.Select(p => new PdfPathPoint
        {
            X = annotation.X + p.X * annotation.Width,
            Y = annotation.Y + p.Y * annotation.Height
        }).ToList();

    public static List<List<PdfPathPoint>> GetStrokes(PdfAnnotation annotation)
    {
        PdfPathPoint Point(double x, double y) => new()
        { X = annotation.X + x * annotation.Width, Y = annotation.Y + y * annotation.Height };
        if (annotation.DrawingKind == DrawingKind.Rectangle)
            return new() { new() { Point(0, 0), Point(1, 0), Point(1, 1), Point(0, 1), Point(0, 0) } };
        if (annotation.DrawingKind == DrawingKind.Ellipse)
            return new() { Enumerable.Range(0, 129).Select(i =>
                Point((1 + Math.Cos(i * Math.Tau / 128)) / 2, (1 + Math.Sin(i * Math.Tau / 128)) / 2)).ToList() };
        var points = GetPathPoints(annotation);
        if (annotation.DrawingKind == DrawingKind.Bezier)
        {
            var curve = new List<PdfPathPoint> { points[0] };
            FlattenBezier(points[0], points[1], points[2], points[3], curve, 0);
            if (annotation.IsFilled) curve.Add(points[0]);
            return new() { curve };
        }
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

    private static void FlattenBezier(PdfPathPoint start, PdfPathPoint first, PdfPathPoint second,
        PdfPathPoint end, List<PdfPathPoint> output, int depth)
    {
        // Compare with the controls of a straight cubic at 1/3 and 2/3 of the chord.
        // This also detects curves that reverse along the chord or return to the start.
        double dx = (end.X - start.X) / 3, dy = (end.Y - start.Y) / 3;
        if (depth >= 12 || (Near(first, start.X + dx, start.Y + dy) && Near(second, end.X - dx, end.Y - dy)))
        {
            output.Add(end);
            return;
        }
        var left = Midpoint(start, first);
        var middle = Midpoint(first, second);
        var right = Midpoint(second, end);
        var leftControl = Midpoint(left, middle);
        var rightControl = Midpoint(middle, right);
        var split = Midpoint(leftControl, rightControl);
        FlattenBezier(start, left, leftControl, split, output, depth + 1);
        FlattenBezier(split, rightControl, right, end, output, depth + 1);

        static bool Near(PdfPathPoint point, double x, double y) =>
            Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2) <= .01;
    }

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
