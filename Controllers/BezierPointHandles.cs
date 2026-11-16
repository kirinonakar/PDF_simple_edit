using Loc = PDF_simple_edit.Services.LocalizationService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace PDF_simple_edit.Controllers;

internal static class BezierPointHandles
{
    private const double PdfToPixels = 96.0 / 72;
    private const double HandleSize = 12;
    private static readonly string[] PointLabels =
        { "BezierStartPoint", "BezierFirstControlPoint", "BezierSecondControlPoint", "BezierEndPoint" };

    internal static void Add(Canvas canvas, IReadOnlyList<PdfPathPoint> points, int placedPoints, bool editable)
    {
        var brush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var guide = new Polyline
        {
            Stroke = brush, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false
        };
        foreach (var point in points) guide.Points.Add(new Point(point.X * PdfToPixels, point.Y * PdfToPixels));
        canvas.Children.Add(guide);

        var centers = new List<Point>(placedPoints);
        for (int i = 0; i < placedPoints; i++)
        {
            var anchor = guide.Points[i];
            var center = editable ? FindHandleCenter(canvas, anchor, centers) : anchor;
            centers.Add(center);
            if (center != anchor)
            {
                canvas.Children.Add(new Line
                {
                    X1 = anchor.X, Y1 = anchor.Y, X2 = center.X, Y2 = center.Y,
                    Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false
                });
            }

            bool endpoint = i is 0 or 3;
            Shape marker = endpoint ? new Ellipse() : new Rectangle();
            double size = editable ? HandleSize : 8;
            marker.Width = marker.Height = size;
            marker.Stroke = endpoint ? new SolidColorBrush(Microsoft.UI.Colors.White) : brush;
            marker.Fill = endpoint ? brush : new SolidColorBrush(Microsoft.UI.Colors.White);
            marker.StrokeThickness = 1.5;
            marker.IsHitTestVisible = editable;
            Canvas.SetLeft(marker, center.X - size / 2);
            Canvas.SetTop(marker, center.Y - size / 2);
            if (editable)
            {
                string label = Loc.Get(PointLabels[i]);
                marker.Tag = i;
                AutomationProperties.SetName(marker, label);
                ToolTipService.SetToolTip(marker, label);
            }
            canvas.Children.Add(marker);
        }
    }

    private static Point FindHandleCenter(Canvas canvas, Point anchor, IReadOnlyList<Point> centers)
    {
        const double inset = HandleSize / 2;
        const double spacing = HandleSize + 3;
        Point Inside(double x, double y) => new(
            Math.Clamp(x, inset, Math.Max(inset, canvas.Width - inset)),
            Math.Clamp(y, inset, Math.Max(inset, canvas.Height - inset)));
        bool Fits(Point point) => centers.All(other =>
            Math.Abs(other.X - point.X) >= HandleSize || Math.Abs(other.Y - point.Y) >= HandleSize);

        var origin = Inside(anchor.X, anchor.Y);
        if (Fits(origin)) return origin;
        // Fan out coincident handles and connect each one to its actual point.
        for (int i = 1; i < PointLabels.Length; i++)
        {
            double shift = i * spacing;
            foreach (var candidate in new[] { Inside(origin.X + shift, origin.Y), Inside(origin.X - shift, origin.Y),
                Inside(origin.X, origin.Y + shift), Inside(origin.X, origin.Y - shift) })
            {
                if (Fits(candidate)) return candidate;
            }
        }
        return origin;
    }
}
