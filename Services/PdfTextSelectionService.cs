using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>Logical character ranges between two PDF-coordinate caret positions.</summary>
public static class PdfTextSelectionService
{
    public static string GetSelectedText(IReadOnlyList<NativePdfTextBlock> selected) =>
        string.Join(Environment.NewLine, selected.Select(GetBlockText));

    private static string GetBlockText(NativePdfTextBlock block)
    {
        var equations = block.Runs.Where(run => HwpEquationText.IsFont(run.FontName))
            .Select(run => run.Id).ToHashSet();
        return string.Concat(block.Glyphs.Select(glyph => glyph.IsSoftBreak ? Environment.NewLine
            : equations.Contains(glyph.RunId) ? HwpEquationText.Decode(glyph.Text) : glyph.Text));
    }

    public static List<NativePdfTextBlock> SelectRange(IReadOnlyList<NativePdfTextBlock> blocks,
        PdfTextPoint start, PdfTextPoint end)
    {
        var layout = new PageLayout(blocks);
        return layout.SelectRange(layout.Hit(start), layout.Hit(end));
    }

    internal sealed class PageLayout
    {
        private readonly record struct CaretGlyph(NativePdfGlyph Glyph, int Offset, bool Horizontal);
        private readonly int[] _offsets;
        private readonly CaretGlyph[] _carets;

        public IReadOnlyList<NativePdfTextBlock> Blocks { get; }

        public PageLayout(IReadOnlyList<NativePdfTextBlock> blocks)
        {
            Blocks = blocks;
            _offsets = new int[blocks.Count + 1];
            var carets = new List<CaretGlyph>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                int offset = _offsets[i];
                foreach (var glyph in block.Glyphs)
                {
                    if (glyph.IsVirtual || string.IsNullOrWhiteSpace(glyph.Text)) continue;
                    bool horizontal = Math.Abs(glyph.End.X - glyph.Origin.X) >= Math.Abs(glyph.End.Y - glyph.Origin.Y);
                    carets.Add(new(glyph, offset + glyph.TextIndex, horizontal));
                }
                _offsets[i + 1] = offset + block.Text.Length + 1;
            }
            _carets = carets.ToArray();
        }

        public int Hit(PdfTextPoint point)
        {
            if (_carets.Length == 0) return 0;
            var nearest = _carets[0];
            double best = double.PositiveInfinity;
            foreach (var candidate in _carets)
            {
                var bounds = candidate.Glyph.Bounds;
                double dx = Math.Max(Math.Max(bounds.X - point.X, point.X - bounds.Right), 0);
                double dy = Math.Max(Math.Max(bounds.Y - point.Y, point.Y - bounds.Bottom), 0);
                // Favor the closest line when dragging into its margin.
                double distance = candidate.Horizontal ? dx * dx + dy * dy * 16 : dx * dx * 16 + dy * dy;
                if (distance.CompareTo(best) >= 0) continue;
                nearest = candidate;
                best = distance;
                if (best == 0) break;
            }
            var glyph = nearest.Glyph;
            double along = (point.X - (glyph.Origin.X + glyph.End.X) / 2) * (glyph.End.X - glyph.Origin.X) +
                (point.Y - (glyph.Origin.Y + glyph.End.Y) / 2) * (glyph.End.Y - glyph.Origin.Y);
            return nearest.Offset + (along >= 0 ? glyph.Text.Length : 0);
        }

        public List<NativePdfTextBlock> SelectRange(int start, int end)
        {
            int left = Math.Min(start, end), right = Math.Max(start, end);
            var selected = new List<NativePdfTextBlock>();
            if (left == right) return selected;
            int first = Array.BinarySearch(_offsets, left);
            if (first < 0) first = ~first - 1;
            for (int i = first; i < Blocks.Count && _offsets[i] < right; i++)
            {
                var block = Blocks[i];
                int offset = _offsets[i];
                var glyphs = block.Glyphs.Where(g => offset + g.TextIndex < right &&
                    offset + g.TextIndex + g.Text.Length > left).ToList();
                if (glyphs.Any(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text))) selected.Add(block with { Glyphs = glyphs });
            }
            return selected;
        }
    }
}
