using iText.Kernel.Pdf;
using PDF_simple_edit.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

internal sealed class LegacyTextPreviewService
{
    // Render the page beneath the input control without its selected text. This
    // is a detached preview; opening/cancelling an editor never changes the PDF
    // or creates an undo entry. Graphics behind the text are preserved.
    public async Task<byte[]> CreateAsync(byte[] source, int pageIndex, PdfAnnotation annotation)
    {
        var target = annotation.Clone();
        if (target.IsApplied)
        {
            target.OriginalText = target.Content;
            target.TextFragments.Clear();
        }
        var contents = await new PdfPageContentExtractor().ExtractAsync(source, pageIndex, TextEditingMode.Legacy);
        var targets = new PdfAnnotationTargetResolver().ResolveText(new[] { target }, contents)
            ?? throw new InvalidOperationException("원본 텍스트 위치를 확인할 수 없습니다. 다시 선택해 주세요.");
        return await Task.Run(() =>
        {
            using var output = new MemoryStream();
            using (var document = new PdfDocument(new PdfReader(new MemoryStream(source)), new PdfWriter(output)))
                if (!new PdfContentStreamEditor().RemoveTextAnnotations(document, pageIndex, targets))
                    throw new InvalidOperationException("배경을 보존하면서 편집할 수 없는 텍스트입니다.");
            return output.ToArray();
        });
    }
}
