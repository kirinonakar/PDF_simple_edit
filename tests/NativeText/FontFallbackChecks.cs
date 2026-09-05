using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using PDF_simple_edit.Services;

internal static class FontFallbackChecks
{
    public static async Task Run()
    {
        Directory.CreateDirectory("tmp/pdfs");
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf"),
                PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
            new PdfCanvas(doc.AddNewPage()).BeginText().SetFontAndSize(font, 20).MoveText(40, 700).ShowText("한").EndText();
        }
        var bytes = output.ToArray();
        var service = new NativePdfTextService();
        var source = service.Extract(bytes, 0).Single();
        // Every IME preview uses the original snapshot, never the preceding fallback PDF.
        foreach (string text in new[] { "한ㅎ", "한하", "한ㅎㅏㄴ", "한한" })
        {
            var result = service.Edit(bytes, 0, new(source, text));
            bool fallback = text != "한한";
            if ((result.FontSubstitutionStatus != null) != fallback) throw new Exception("Incorrect composition fallback status: " + text);
            var reopened = service.Extract(result.Bytes, 0);
            if (string.Concat(reopened.Select(b => b.Text)) != text) throw new Exception("Fallback text lost after reopen: " + text);
            if (!fallback && reopened.SelectMany(b => b.Runs).Any(r => r.FontObjectNumber != source.Runs[0].FontObjectNumber))
                throw new Exception("Completed Hangul did not return to original font");
            await WindowsRenderingProbe.Render(result.Bytes, "fallback-" + text, result.Layout);
            var restored = service.Edit(bytes, 0, new(source, source.Text));
            if (restored.FontSubstitutionStatus != null || !ReferenceEquals(restored.Bytes, bytes))
                throw new Exception("Removing composition did not clear fallback");
        }
        Console.WriteLine("PASS: Hangul composition fallback, original glyph restoration, status, save/reopen and Windows rendering");
    }
}
