using iText.IO.Font;
using PDF_simple_edit.Services;

internal static class InstalledFontChecks
{
    public static async Task Run(string path)
    {
        Directory.CreateDirectory("tmp/pdfs");
        if (InstalledFontService.FindFontFile("맑은 고딕", 400, false) == null)
            throw new Exception("Localized installed family name no longer resolves");
        foreach (var (suffix, weight) in new[] { ("Regular", 400), ("Medium", 500), ("DemiLight", 350), ("Light", 300), ("Bold", 700) })
        {
            string original = "QUNXSR+NotoSansCJKkr-" + suffix;
            string installed = InstalledFontService.FindFontFile("Noto Sans CJK KR", weight, false)
                ?? throw new Exception("Install Noto Sans CJK KR to run this regression.");
            string candidate = InstalledFontService.GetSimilarFontFiles(original, weight).First();
            var descriptor = FontProgramDescriptorFactory.FetchDescriptor(candidate);
            if (candidate != installed || descriptor.GetFontName() != "NotoSansCJKkr-" + suffix)
                throw new Exception($"Incorrect face for {original}: {candidate} / {descriptor.GetFontName()}");
            Console.WriteLine($"PASS: {original} => {descriptor.GetFontName()} ({candidate})");
        }
        if (!InstalledFontService.GetInstalledNotoFamilies().Contains("Noto Sans CJK KR"))
            throw new Exception("Collection family missing from installed font list");

        var service = new NativePdfTextService();
        byte[] bytes = File.ReadAllBytes(path);
        var blocks = service.Extract(bytes, 0);
        var source = blocks.First(b => b.Runs.All(r => r.FontName.Contains("NotoSansCJKkr")) && b.Text.Length > 10);
        // Replace the beginning to exercise a glyph absent from the embedded subset.
        var result = service.Edit(bytes, 0, new(source, "뾟" + source.Text[1..]));
        var replacements = result.Layout.Glyphs.Where(g => g.ReplacementFontName != null).ToList();
        if (replacements.Count == 0 || replacements.Any(g => !g.ReplacementFontName!.Contains("NotoSansCJKkr")))
            throw new Exception("Attachment edit did not retain installed Noto Sans CJK KR");
        if (!service.Extract(result.Bytes, 0).Any(b => b.Text.Contains("뾟")))
            throw new Exception("New Korean glyph lost on reopen");
        await WindowsRenderingProbe.Render(result.Bytes, "noto-cjk-installed", result.Layout);
        Console.WriteLine("PASS: attachment edit, same-family fallback, save/reopen and Windows rendering");
    }
}
