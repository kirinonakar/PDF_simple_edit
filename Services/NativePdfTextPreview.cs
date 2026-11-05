using iText.Kernel.Pdf;
using PDF_simple_edit.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

public sealed partial class NativePdfTextService
{
    /// <summary>
    /// An immutable, page-only editing snapshot. Preview bytes must never be
    /// committed to the document; apply the final text to the original snapshot.
    /// </summary>
    public sealed record TextPreview(byte[] Bytes, NativePdfTextBlock Block,
        List<NativePdfTextBlock> OriginalBlocks);

    public TextPreview CreateTextPreview(byte[] bytes, int pageIndex, NativePdfTextBlock block)
    {
        using var output = new MemoryStream();
        using (var source = new PdfDocument(new PdfReader(new MemoryStream(bytes))))
        using (var target = new PdfDocument(new PdfWriter(output,
            new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION))))
            source.CopyPagesTo(pageIndex + 1, pageIndex + 1, target);

        byte[] pageBytes = output.ToArray();
        using var pageDocument = new PdfDocument(new PdfReader(new MemoryStream(pageBytes)));
        var page = pageDocument.GetPage(1);
        var data = Read(page);
        var runs = data.Runs.ToDictionary(r => r.Id);
        // CopyPagesTo preserves content operators and glyph codes, but remaps
        // indirect font numbers. Keep the selection's glyph identities, virtual
        // spaces and paragraph boundaries exactly as they were selected.
        var selection = block with
        {
            Runs = block.Runs.Select(r => r with { FontObjectNumber = runs[r.Id].FontObjectNumber }).ToList()
        };
        return new(pageBytes, selection, ApplyInkBounds(page, data.Blocks, data.Map));
    }

    public NativePdfTextResult EditTextPreview(TextPreview preview, string text) =>
        EditMany(preview.Bytes, 0, new[] { new NativePdfTextEdit(preview.Block, text) },
            preview.OriginalBlocks, preview: true);
}
