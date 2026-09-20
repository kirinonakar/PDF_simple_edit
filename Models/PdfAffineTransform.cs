using System;
using System.Globalization;
using System.Linq;

namespace PDF_simple_edit.Models;

public readonly record struct PdfAffineTransform(double A, double B, double C, double D, double E, double F)
{
    public static PdfAffineTransform Identity => new(1, 0, 0, 1, 0, 0);
    public PdfTextPoint Map(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
    public PdfTextBox Map(PdfTextBox box)
    {
        var points = new[] { Map(box.X, box.Y), Map(box.Right, box.Y), Map(box.X, box.Bottom), Map(box.Right, box.Bottom) };
        double x = points.Min(p => p.X), y = points.Min(p => p.Y);
        return new(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }
    // this after other
    public PdfAffineTransform After(PdfAffineTransform other) => new(
        A * other.A + C * other.B, B * other.A + D * other.B,
        A * other.C + C * other.D, B * other.C + D * other.D,
        A * other.E + C * other.F + E, B * other.E + D * other.F + F);
    public PdfAffineTransform Inverse()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("변환할 수 없는 PDF 좌표입니다.");
        return new(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }
    public static PdfAffineTransform Between(PdfTextBox source, PdfTextBox target, double degrees = 0)
    {
        double radians = degrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double sx = target.Width / Math.Max(source.Width, .001), sy = target.Height / Math.Max(source.Height, .001);
        var linear = new PdfAffineTransform(cos * sx, sin * sx, -sin * sy, cos * sy, 0, 0);
        var center = linear.Map(source.X + source.Width / 2, source.Y + source.Height / 2);
        return linear with { E = target.X + target.Width / 2 - center.X, F = target.Y + target.Height / 2 - center.Y };
    }
    public string Command => string.Join(" ", new[] { A, B, C, D, E, F }.Select(v => v.ToString("0.##########", CultureInfo.InvariantCulture))) + " cm\n";
}
