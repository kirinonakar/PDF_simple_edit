using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Models;

public sealed record PdfGraphicHitContour(List<PdfTextPoint> Points, bool Closed);

// Flattened paint geometry distinguishes ink from empty space within its bounds.
public sealed class PdfGraphicHitGeometry
{
    public List<PdfGraphicHitContour> Contours { get; init; } = new();
    public bool Filled { get; init; }
    public bool EvenOddFill { get; init; }
    public bool Stroked { get; init; }
    public double StrokeRadius { get; init; }

    public bool Contains(double x, double y, double tolerance)
    {
        int winding = 0;
        double radius = tolerance + (Stroked ? StrokeRadius : 0);
        foreach (var (points, closed) in Contours)
        {
            int segmentCount = closed || Filled ? points.Count : points.Count - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
                double distanceX = x - a.X - t * dx, distanceY = y - a.Y - t * dy;
                if ((Stroked || Filled) && distanceX * distanceX + distanceY * distanceY <= radius * radius)
                    return true;
                if (!Filled) continue;
                double cross = dx * (y - a.Y) - dy * (x - a.X);
                if (a.Y <= y && b.Y > y && cross > 0) winding++;
                else if (a.Y > y && b.Y <= y && cross < 0) winding--;
            }
        }
        return Filled && (EvenOddFill ? Math.Abs(winding) % 2 != 0 : winding != 0);
    }

    public PdfGraphicHitGeometry Map(PdfAffineTransform map) => new()
    {
        Filled = Filled, EvenOddFill = EvenOddFill, Stroked = Stroked,
        StrokeRadius = StrokeRadius * Math.Max(Math.Sqrt(map.A * map.A + map.B * map.B), Math.Sqrt(map.C * map.C + map.D * map.D)),
        Contours = Contours.Select(contour => new PdfGraphicHitContour(
            contour.Points.Select(point => map.Map(point.X, point.Y)).ToList(), contour.Closed)).ToList()
    };
}
