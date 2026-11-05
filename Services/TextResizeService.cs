using PDF_simple_edit.Models;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

public static class TextResizeService
{
    public static bool IsPdfText(PdfAnnotation source) => source.NativeText != null || source.IsOriginalTextReplacement ||
        (source.IsApplied && source.Type is AnnotationType.Text or AnnotationType.FreeText);

    public static NativePdfTextBlock ResolveBlock(PdfAnnotation source, List<NativePdfTextBlock> allText)
    {
        if (source.NativeText != null) return source.NativeText;
        var box = new PdfTextBox(source.X, source.Y, source.Width, source.Height);
        var selected = NativePdfTextService.SelectRegion(allText,
            PdfAffineTransform.Between(box, box, source.Rotation).Map(box));
        return selected.Count == 1 ? selected[0] : new NativePdfTextBlock
        {
            Text = string.Join("\n", selected.Select(b => b.Text)),
            Runs = selected.SelectMany(b => b.Runs).DistinctBy(r => r.Id).ToList(),
            Glyphs = selected.SelectMany(b => b.Glyphs).ToList(),
            Lines = selected.SelectMany(b => b.Lines).ToList(),
            Bounds = PdfTextBox.Union(selected.Select(b => b.Bounds))
        };
    }

    public static NativePdfTextEdit CreateEdit(NativePdfTextBlock block, PdfAnnotation source,
        PdfAnnotation target, bool reflow)
    {
        var before = new PdfTextBox(source.X, source.Y, source.Width, source.Height);
        var after = new PdfTextBox(target.X, target.Y, target.Width, target.Height);
        return reflow
            ? new(block, block.Text, ReflowBounds: after)
            : new(block, block.Text, Transform: PdfAffineTransform.Between(before, after, target.Rotation - source.Rotation));
    }
}
