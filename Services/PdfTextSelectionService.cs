using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>Logical character ranges between two PDF-coordinate caret positions.</summary>
public static class PdfTextSelectionService
{
    public static List<NativePdfTextBlock> SelectRange(IReadOnlyList<NativePdfTextBlock> blocks,
        PdfTextPoint start, PdfTextPoint end)
    {
        var offsets = new int[blocks.Count];
        int total = 0;
        for (int i = 0; i < blocks.Count; i++) { offsets[i] = total; total += blocks[i].Text.Length + 1; }
        int Hit(PdfTextPoint point)
        {
            var candidates = blocks.SelectMany((block, index) => block.Glyphs
                .Where(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text))
                .Select(g => (Glyph: g, Offset: offsets[index]))).ToList();
            double Distance(NativePdfGlyph glyph)
            {
                double dx = Math.Max(Math.Max(glyph.Bounds.X - point.X, point.X - glyph.Bounds.Right), 0);
                double dy = Math.Max(Math.Max(glyph.Bounds.Y - point.Y, point.Y - glyph.Bounds.Bottom), 0);
                double ux = glyph.End.X - glyph.Origin.X, uy = glyph.End.Y - glyph.Origin.Y;
                // Prefer the closest line when dragging into its horizontal margin.
                return Math.Abs(ux) >= Math.Abs(uy) ? dx * dx + dy * dy * 16 : dx * dx * 16 + dy * dy;
            }
            if (candidates.Count == 0) return 0;
            var nearest = candidates.OrderBy(c => Distance(c.Glyph)).First();
            var glyph = nearest.Glyph;
            double along = (point.X - (glyph.Origin.X + glyph.End.X) / 2) * (glyph.End.X - glyph.Origin.X) +
                (point.Y - (glyph.Origin.Y + glyph.End.Y) / 2) * (glyph.End.Y - glyph.Origin.Y);
            return nearest.Offset + glyph.TextIndex + (along >= 0 ? glyph.Text.Length : 0);
        }
        int a = Hit(start), b = Hit(end);
        int left = Math.Min(a, b), right = Math.Max(a, b);
        var selected = new List<NativePdfTextBlock>();
        if (left == right) return selected;
        for (int i = 0; i < blocks.Count; i++)
        {
            int offset = offsets[i];
            var glyphs = blocks[i].Glyphs.Where(g => offset + g.TextIndex < right && offset + g.TextIndex + g.Text.Length > left).ToList();
            if (glyphs.Any(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text))) selected.Add(blocks[i] with { Glyphs = glyphs });
        }
        return selected;
    }
}
