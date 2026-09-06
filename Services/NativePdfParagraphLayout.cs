using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services;

internal static class NativePdfParagraphLayout
{
    private sealed class PhysicalLine
    {
        public List<NativePdfRun> Runs = new();
        public double Baseline => Runs[0].Glyphs[0].Origin.Y;
        public double Left => Runs.Min(r => r.Glyphs.Min(g => g.Origin.X));
        public double Right => Runs.Max(r => r.Glyphs.Max(g => g.End.X));
        public double Size => Runs.Max(r => r.DisplayFontSize);
        public string Text => string.Concat(Runs.SelectMany(r => r.Glyphs).Select(g => g.Text));
    }

    public static List<List<NativePdfRun>> GroupRuns(List<NativePdfRun> runs)
    {
        var order = runs.Select((run, index) => (run.Id, index)).ToDictionary(p => p.Id, p => p.index);
        // A single TJ operand can paint cells in several columns. Segment its
        // geometry too; editing later repairs only the selected glyph indexes.
        runs = runs.SelectMany(run =>
        {
            var parts = new List<NativePdfRun>();
            var glyphs = new List<NativePdfGlyph>();
            foreach (var glyph in run.Glyphs)
            {
                if (glyphs.Count > 0 && glyph.Origin.X - glyphs[^1].End.X > run.DisplayFontSize * .6)
                { parts.Add(run with { Glyphs = glyphs }); glyphs = new(); }
                glyphs.Add(glyph);
            }
            if (glyphs.Count > 0) parts.Add(run with { Glyphs = glyphs });
            return parts;
        }).ToList();
        var result = new List<List<NativePdfRun>>();
        bool Horizontal(NativePdfRun run) => !run.IsVertical && run.Glyphs.All(g => Math.Abs(g.End.Y - g.Origin.Y) < .1 && g.End.X >= g.Origin.X);
        foreach (var run in runs.Where(r => r.Glyphs.Count > 0 && !Horizontal(r))) result.Add(new() { run });
        // PDF paint order can alternate between columns. Build physical lines
        // geometrically, within each Form invocation, before linking paragraphs.
        foreach (var scope in runs.Where(r => r.Glyphs.Count > 0 && Horizontal(r)).GroupBy(r => r.SourcePath))
        {
            var rows = new List<PhysicalLine>();
            foreach (var run in scope.OrderBy(r => r.Glyphs[0].Origin.Y).ThenBy(r => r.Glyphs[0].Origin.X))
            {
                var row = rows.LastOrDefault(l => Math.Abs(l.Baseline - run.Glyphs[0].Origin.Y) <= Math.Max(.5, Math.Min(l.Size, run.DisplayFontSize) * .22));
                if (row == null) { row = new(); rows.Add(row); }
                row.Runs.Add(run);
            }
            var gaps = rows.SelectMany(row => row.Runs.OrderBy(r => r.Glyphs[0].Origin.X)
                .Zip(row.Runs.OrderBy(r => r.Glyphs[0].Origin.X).Skip(1), (a, b) =>
                    (Left: a.Glyphs.Max(g => g.End.X), Right: b.Glyphs.Min(g => g.Origin.X), Y: row.Baseline,
                     Size: Math.Max(a.DisplayFontSize, b.DisplayFontSize))))
                .Where(g => g.Right - g.Left >= g.Size * .6).ToList();
            // Repeated whitespace between aligned rows is a column gutter, even
            // when it is narrower than the ordinary same-line joining threshold.
            bool Gutter(double left, double right, double y, double size) => gaps.Any(a =>
                Math.Abs(a.Y - y) < .5 && Math.Min(a.Right, right) - Math.Max(a.Left, left) >= size * .35 &&
                gaps.Any(b =>
                {
                    if (Math.Abs(b.Y - y) < size * .65 || Math.Abs(b.Y - y) > size * 4) return false;
                    double gutterLeft = Math.Max(Math.Max(a.Left, b.Left), left);
                    double gutterRight = Math.Min(Math.Min(a.Right, b.Right), right);
                    if (gutterRight - gutterLeft < size * .35) return false;
                    double middle = (gutterLeft + gutterRight) / 2;
                    // Justified words can have repeated wide spaces too. A
                    // column gutter must remain clear of ink on nearby rows;
                    // otherwise these spaces split words out of the paragraph.
                    return !rows.Where(row => Math.Abs(row.Baseline - y) <= size * 4)
                        .SelectMany(row => row.Runs).SelectMany(run => run.Glyphs)
                        .Any(g => !string.IsNullOrWhiteSpace(g.Text) && g.Origin.X < middle && g.End.X > middle);
                }));
            var lines = new List<PhysicalLine>();
            foreach (var row in rows)
            {
                PhysicalLine? segment = null;
                foreach (var run in row.Runs.OrderBy(r => r.Glyphs[0].Origin.X).ThenBy(r => order[r.Id]))
                {
                    var previous = segment?.Runs.LastOrDefault();
                    double size = Math.Max(previous?.DisplayFontSize ?? 0, run.DisplayFontSize);
                    double gap = run.Glyphs[0].Origin.X - (previous?.Glyphs[^1].End.X ?? 0);
                    if (segment == null || gap < -size * .5 || gap > size * (previous!.FontName == run.FontName ? 1.6 : .8) ||
                        Gutter(previous!.Glyphs[^1].End.X, run.Glyphs[0].Origin.X, row.Baseline, size))
                    { segment = new(); lines.Add(segment); }
                    segment.Runs.Add(run);
                }
            }
            var paragraphs = new List<List<PhysicalLine>>();
            foreach (var line in lines.OrderBy(l => l.Baseline).ThenBy(l => l.Left))
            {
                bool Connect(List<PhysicalLine> paragraph)
                {
                    var previous = paragraph[^1];
                    double size = Math.Max(previous.Size, line.Size);
                    double leading = line.Baseline - previous.Baseline;
                    bool sharedFace = line.Runs.Any(r => previous.Runs.Any(p => p.FontName == r.FontName));
                    if (leading < size * .65 || leading > size * 1.85 || Math.Abs(line.Size - previous.Size) > size * .18 || !sharedFace) return false;
                    double left = paragraph.Min(l => l.Left);
                    bool aligned = Math.Abs(line.Left - left) <= Math.Max(2, size * .55);
                    bool firstIndent = paragraph.Count == 1 && previous.Left > line.Left && previous.Left - line.Left < size * 2.1;
                    if (!aligned && !firstIndent) return false;
                    if (paragraph.Count > 1)
                    {
                        double usualLeading = paragraph.Zip(paragraph.Skip(1), (a, b) => b.Baseline - a.Baseline).Order().ElementAt((paragraph.Count - 2) / 2);
                        if (leading > usualLeading * 1.28) return false;
                        double width = paragraph.Max(l => l.Right) - left;
                        if (previous.Right - previous.Left < width * .7 && previous.Text.TrimEnd().EndsWith('.')) return false;
                    }
                    return Math.Min(previous.Right, line.Right) - Math.Max(previous.Left, line.Left) > 0;
                }
                var target = paragraphs.Where(Connect).OrderBy(p => Math.Abs(p[^1].Left - line.Left)).ThenBy(p => line.Baseline - p[^1].Baseline).FirstOrDefault();
                if (target == null) { target = new(); paragraphs.Add(target); }
                target.Add(line);
            }
            result.AddRange(paragraphs.Select(p => p.SelectMany(l => l.Runs).ToList()));
        }
        return result.OrderBy(p => p.Min(r => order[r.Id])).ToList();
    }

    private sealed record FlowLine(int Start, int End, double Left, double Baseline, bool HardBreak);

    public static NativePdfTextBlock Place(NativePdfTextBlock block, string text, List<NativePdfGlyph> tokens,
        Func<NativePdfGlyph, double> width, Func<NativePdfGlyph, NativePdfGlyph, double> kerning,
        Func<NativePdfGlyph, double> trailingSpacing, double deltaX, double deltaY)
    {
        var originalLines = block.Glyphs.Where(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text))
            .GroupBy(g => Math.Round(g.Origin.Y, 1)).Select(g => g.ToList()).OrderBy(g => g[0].Origin.Y).ToList();
        var anchors = originalLines.Select(line => line[0].Origin).ToList();
        double left = anchors.Min(p => p.X), firstLeft = anchors[0].X;
        double right = Math.Max(block.Bounds.Right, block.Glyphs.Max(g => g.End.X));
        // A selected single line grows horizontally. Only an original multiline
        // selection reflows at its right edge; explicit newlines still work below.
        bool wrap = block.Lines.Count > 1;
        double leading = Math.Max(block.LineHeight, block.Runs.Max(r => r.DisplayFontSize) * .9);
        double Baseline(int i) => i < anchors.Count ? anchors[i].Y : anchors[^1].Y + (i - anchors.Count + 1) * leading;
        bool White(NativePdfGlyph glyph) => string.IsNullOrWhiteSpace(glyph.Text);
        bool BreakAfter(NativePdfGlyph glyph) => White(glyph) || glyph.Text.EndsWith('-') || glyph.Text.EndsWith('\u2010') ||
            glyph.Text.EnumerateRunes().Any(r => r.Value is >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7AF);
        var widths = tokens.Select(width).ToArray();
        var lines = new List<FlowLine>();
        int start = 0;
        double lineLeft = firstLeft;
        while (start < tokens.Count)
        {
            double x = lineLeft;
            int lastBreak = -1, end = tokens.Count;
            bool hard = false;
            for (int i = start; i < tokens.Count; i++)
            {
                if (tokens[i].Text == "\n") { end = i + 1; hard = true; break; }
                double kern = i > start ? kerning(tokens[i - 1], tokens[i]) : 0;
                if (wrap && !White(tokens[i]) && x + kern + widths[i] - trailingSpacing(tokens[i]) > right + .5)
                {
                    end = lastBreak > start ? lastBreak : Math.Max(i, start + 1);
                    break;
                }
                x += kern + widths[i];
                if (BreakAfter(tokens[i])) lastBreak = i + 1;
            }
            lines.Add(new(start, end, lineLeft, Baseline(lines.Count), hard));
            start = end; lineLeft = hard ? firstLeft : left;
        }
        var placed = new List<NativePdfGlyph>();
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var next = lineIndex + 1 < lines.Count ? lines[lineIndex + 1] : new FlowLine(line.End, line.End, line.HardBreak ? firstLeft : left, Baseline(lineIndex + 1), false);
            double x = line.Left;
            int lastInk = line.End - 1;
            while (lastInk >= line.Start && White(tokens[lastInk])) lastInk--;
            int placedStart = placed.Count;
            for (int i = line.Start; i < line.End; i++)
            {
                var token = tokens[i];
                bool wraps = token.Text == "\n" || i > lastInk && lineIndex + 1 < lines.Count;
                if (wraps)
                {
                    placed.Add(token with { Origin = new(x + deltaX, line.Baseline + deltaY), End = new(next.Left + deltaX, next.Baseline + deltaY),
                        Bounds = new(x + deltaX, line.Baseline + deltaY - token.Bounds.Height, 0, token.Bounds.Height), IsSoftBreak = token.Text != "\n" });
                    continue;
                }
                if (i > line.Start) x += kerning(tokens[i - 1], token);
                double dx = x + deltaX - token.Origin.X, dy = line.Baseline + deltaY - token.Origin.Y;
                placed.Add(token with { Origin = new(x + deltaX, line.Baseline + deltaY), End = new(x + widths[i] + deltaX, line.Baseline + deltaY),
                    Bounds = token.Bounds with { X = token.Bounds.X + dx, Y = token.Bounds.Y + dy }, IsSoftBreak = false });
                x += widths[i];
            }
            // Preserve exact original placement if reflow leaves a line's glyph
            // sequence unchanged. This also retains original justification.
            if (lineIndex < originalLines.Count)
            {
                var source = originalLines[lineIndex];
                var indexes = Enumerable.Range(placedStart, placed.Count - placedStart).Where(i => !placed[i].IsVirtual && !White(placed[i])).ToList();
                if (source.Count == indexes.Count && source.Zip(indexes).All(p => ReferenceEquals(p.First.Code, placed[p.Second].Code)))
                    for (int i = 0; i < source.Count; i++)
                    {
                        var glyph = source[i]; int target = indexes[i];
                        placed[target] = glyph with { TextIndex = placed[target].TextIndex,
                            Origin = new(glyph.Origin.X + deltaX, glyph.Origin.Y + deltaY), End = new(glyph.End.X + deltaX, glyph.End.Y + deltaY),
                            Bounds = glyph.Bounds with { X = glyph.Bounds.X + deltaX, Y = glyph.Bounds.Y + deltaY } };
                    }
            }
        }
        var boxes = placed.Where(g => !White(g)).GroupBy(g => Math.Round(g.Origin.Y, 1)).Select(g => PdfTextBox.Union(g.Select(c => c.Bounds))).ToList();
        return block with { Text = text, Glyphs = placed, Lines = boxes, Bounds = PdfTextBox.Union(boxes) };
    }
}
