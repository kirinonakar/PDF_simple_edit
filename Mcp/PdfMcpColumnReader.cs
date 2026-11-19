using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal static class PdfMcpColumnReader
{
    internal sealed record Column(int column, string text);

    private const double BaselineTolerance = .75;
    private readonly record struct Run(double Left, double Right, double Y, bool Horizontal);
    private sealed record Row(double Y, List<Run> Runs);
    private readonly record struct RowSides(bool Left, bool Right, bool Crosses);
    private sealed record Body(double Split, double Top, double Bottom, int Score);

    public static (string Text, Column[]? Columns) Read(PdfPage page, PdfTextBox bounds,
        PdfReadTextExtractionStrategy original, bool force, CancellationToken cancellation)
    {
        var transform = PdfPageCoordinates.ToDisplay(page);
        var runs = new List<Run>();
        var rules = new List<PdfTextBox>();
        Process(page, info =>
        {
            original.EventOccurred(info, EventType.RENDER_TEXT);
            if (string.IsNullOrWhiteSpace(info.GetText())) return;
            var run = GetRun(info, transform);
            if (run.Horizontal && run.Left < bounds.Width && run.Right > 0 && run.Y >= 0 && run.Y <= bounds.Height)
                runs.Add(run);
        }, path => CollectRules(path, transform, bounds, rules));

        var rows = new List<Row>();
        foreach (var run in runs.OrderBy(run => run.Y))
        {
            if (rows.Count == 0 || run.Y - rows[^1].Y > BaselineTolerance)
                rows.Add(new(run.Y, new()));
            rows[^1].Runs.Add(run);
        }

        var body = FindRuledBody(rows, bounds, rules) ?? FindBody(rows, bounds, cancellation);
        if (body == null && force)
            body = Evaluate(rows, bounds, bounds.Width / 2, minimumRows: 1)
                ?? new(bounds.Width / 2, double.NegativeInfinity, double.PositiveInfinity, 0);
        if (body == null) return (PdfMcpReader.Normalize(original.GetResultantText()), null);

        var before = new PdfReadTextExtractionStrategy();
        var left = new PdfReadTextExtractionStrategy();
        var right = new PdfReadTextExtractionStrategy();
        var after = new PdfReadTextExtractionStrategy();
        cancellation.ThrowIfCancellationRequested();
        Process(page, info =>
        {
            var run = GetRun(info, transform);
            var strategy = run.Y < body.Top ? before
                : run.Y > body.Bottom ? after
                : (run.Left + run.Right) / 2 < body.Split ? left : right;
            strategy.EventOccurred(info, EventType.RENDER_TEXT);
        });
        string leftText = PdfMcpReader.Normalize(left.GetResultantText());
        string rightText = PdfMcpReader.Normalize(right.GetResultantText());
        if (leftText.Length == 0 || rightText.Length == 0)
            return (PdfMcpReader.Normalize(original.GetResultantText()), null);
        var sections = new[] { PdfMcpReader.Normalize(before.GetResultantText()), leftText, rightText,
            PdfMcpReader.Normalize(after.GetResultantText()) };
        return (string.Join("\n\n", sections.Where(text => text.Length > 0)),
            new[] { new Column(1, leftText), new Column(2, rightText) });
    }

    private static void CollectRules(PathRenderInfo path, PdfAffineTransform transform, PdfTextBox bounds, List<PdfTextBox> rules)
    {
        if (path.GetOperation() == PathRenderInfo.NO_OP) return;
        var ctm = path.GetCtm();
        var toDisplay = transform.After(new(ctm.Get(0), ctm.Get(1), ctm.Get(3), ctm.Get(4), ctm.Get(6), ctm.Get(7)));
        void Add(PdfTextBox box)
        {
            double split = box.X + box.Width / 2;
            bool vertical = box.Width <= 2 && box.Height >= bounds.Height * .45 &&
                split >= bounds.Width * .35 && split <= bounds.Width * .65;
            bool horizontal = box.Height <= 2 && box.Width >= bounds.Width * .55;
            if ((vertical || horizontal) && box.Y >= 0 && box.Bottom <= bounds.Height)
                rules.Add(box);
        }
        foreach (var subpath in path.GetPath().GetSubpaths())
        {
            var segments = subpath.GetSegments();
            if ((path.GetOperation() & PathRenderInfo.STROKE) != 0 && path.GetLineWidth() <= 3)
            {
                foreach (var line in segments.OfType<Line>())
                    Add(PathBounds(line.GetBasePoints(), toDisplay));
            }
            if ((path.GetOperation() & PathRenderInfo.FILL) != 0 && segments.Count is 3 or 4 && segments.All(s => s is Line))
                Add(PathBounds(segments.SelectMany(s => s.GetBasePoints()), toDisplay));
        }
    }

    private static PdfTextBox PathBounds(IEnumerable<Point> points, PdfAffineTransform transform)
    {
        var mapped = points.Select(point => transform.Map(point.GetX(), point.GetY())).ToArray();
        double left = mapped.Min(point => point.X), top = mapped.Min(point => point.Y);
        return new(left, top, mapped.Max(point => point.X) - left, mapped.Max(point => point.Y) - top);
    }

    private static Body? FindRuledBody(List<Row> rows, PdfTextBox bounds, List<PdfTextBox> rules)
    {
        // A long divider gives an explicit body boundary even if the columns are
        // sparse or use different baselines, as with exam problems and diagrams.
        foreach (var rule in rules.Where(rule => rule.Width <= 2).OrderByDescending(rule => rule.Height))
        {
            double split = rule.X + rule.Width / 2;
            // Some PDFs paint their header over the start of the vertical rule.
            // A page-wide line near that start can mark the actual top of the columns.
            double top = rules.Where(line => line.Height <= 2 && line.Y >= rule.Y &&
                line.Y - rule.Y <= bounds.Height * .1 && line.X < split - bounds.Width * .2 &&
                line.Right > split + bounds.Width * .2)
                .Select(line => line.Bottom).DefaultIfEmpty(rule.Y).Max();
            var sides = rows.Where(row => row.Y >= top && row.Y <= rule.Bottom)
                .Select(row => GetSides(row, split, 0, bounds.Width * .05)).ToArray();
            if (sides.Count(side => side.Left) >= 2 && sides.Count(side => side.Right) >= 2 &&
                sides.Count(side => side.Crosses) * 5 <= sides.Length)
                return new(split, top, rule.Bottom, 0);
        }
        return null;
    }

    private static Body? FindBody(List<Row> rows, PdfTextBox bounds, CancellationToken cancellation)
    {
        Body? best = null;
        double center = bounds.Width / 2;
        // Search for a sustained gutter near the center, allowing unequal column widths.
        for (int offset = -15; offset <= 15; offset++)
        {
            cancellation.ThrowIfCancellationRequested();
            double split = center + offset * bounds.Width / 100;
            var candidate = Evaluate(rows, bounds, split, minimumRows: 4);
            if (candidate != null && (best == null || candidate.Score > best.Score ||
                (candidate.Score == best.Score && Math.Abs(split - center) < Math.Abs(best.Split - center))))
                best = candidate;
        }
        return best;
    }

    private static Body? Evaluate(List<Row> rows, PdfTextBox bounds, double split, int minimumRows)
    {
        if (rows.Count < minimumRows) return null;
        double halfGutter = Math.Clamp(bounds.Width * .006, 2.5, 6);
        double minimumWidth = bounds.Width * .08;
        var sides = new RowSides[rows.Count];
        var left = new List<double>();
        var right = new List<double>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var side = sides[i] = GetSides(row, split, halfGutter, minimumWidth);
            if (side.Crosses) continue;
            if (side.Left) left.Add(row.Y);
            if (side.Right) right.Add(row.Y);
        }

        // Staggered baselines are common. Require nearby rows on both sides of the
        // gutter rather than expecting the two columns to share exact baselines.
        double neighborDistance = bounds.Height * .025;
        var pairedLeft = WithNearbyRows(left, right, neighborDistance);
        var pairedRight = WithNearbyRows(right, left, neighborDistance);
        int support = Math.Min(pairedLeft.Count, pairedRight.Count);
        if (support < minimumRows) return null;
        double top = Math.Min(pairedLeft[0], pairedRight[0]);
        double bottom = Math.Max(pairedLeft[^1], pairedRight[^1]);
        int first = rows.FindIndex(row => row.Y >= top);
        int last = rows.FindLastIndex(row => row.Y <= bottom);
        int crossings = 0;
        for (int i = first; i <= last; i++)
            if (sides[i].Crosses) crossings++;
        if (crossings * 8 > pairedLeft.Count + pairedRight.Count) return null;

        // Include nearby short or indented lines. A spanning row or a larger
        // vertical gap marks the boundary with shared headings or footers.
        double edgeGap = bounds.Height * .02;
        while (first > 0 && !sides[first - 1].Crosses && rows[first].Y - rows[first - 1].Y <= edgeGap)
            first--;
        while (last + 1 < rows.Count && !sides[last + 1].Crosses && rows[last + 1].Y - rows[last].Y <= edgeGap)
            last++;
        return new(split, rows[first].Y - BaselineTolerance, rows[last].Y + BaselineTolerance, support - crossings * 3);
    }

    private static RowSides GetSides(Row row, double split, double halfGutter, double minimumWidth)
    {
        double leftStart = double.PositiveInfinity, leftEnd = double.NegativeInfinity;
        double rightStart = double.PositiveInfinity, rightEnd = double.NegativeInfinity;
        foreach (var run in row.Runs)
        {
            if (run.Right <= split - halfGutter)
            {
                leftStart = Math.Min(leftStart, run.Left);
                leftEnd = Math.Max(leftEnd, run.Right);
            }
            else if (run.Left >= split + halfGutter)
            {
                rightStart = Math.Min(rightStart, run.Left);
                rightEnd = Math.Max(rightEnd, run.Right);
            }
            else
            {
                return new(false, false, true);
            }
        }
        return new(leftEnd - leftStart >= minimumWidth, rightEnd - rightStart >= minimumWidth, false);
    }

    private static List<double> WithNearbyRows(List<double> rows, List<double> other, double distance)
    {
        var paired = new List<double>();
        int j = 0;
        foreach (double y in rows)
        {
            while (j < other.Count && other[j] < y - distance) j++;
            if (j < other.Count && other[j] <= y + distance) paired.Add(y);
        }
        return paired;
    }

    private static Run GetRun(TextRenderInfo info, PdfAffineTransform transform)
    {
        var baseline = info.GetBaseline();
        var start = baseline.GetStartPoint();
        var end = baseline.GetEndPoint();
        var a = transform.Map(start.Get(0), start.Get(1));
        var b = transform.Map(end.Get(0), end.Get(1));
        return new(Math.Min(a.X, b.X), Math.Max(a.X, b.X), (a.Y + b.Y) / 2,
            Math.Abs(a.Y - b.Y) <= Math.Max(BaselineTolerance, Math.Abs(a.X - b.X) * .02));
    }

    private static void Process(PdfPage page, Action<TextRenderInfo> read, Action<PathRenderInfo>? path = null) =>
        new PdfCanvasProcessor(new LayoutListener(read, path)).ProcessPageContent(page);

    private sealed class LayoutListener(Action<TextRenderInfo> read, Action<PathRenderInfo>? path) : IEventListener
    {
        public ICollection<EventType> GetSupportedEvents() => path == null ? new[] { EventType.RENDER_TEXT }
            : new[] { EventType.RENDER_TEXT, EventType.RENDER_PATH };
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is TextRenderInfo text) read(text);
            else if (data is PathRenderInfo rule) path?.Invoke(rule);
        }
    }
}
