using System;
using System.IO;
using System.Linq;
using System.Text;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Services;

internal static class PdfAppearanceFont
{
    public static PdfFont Resolve(string text, string? fontFamily, PdfFont? existing = null)
    {
        int[] characters = text.EnumerateRunes().Where(rune => !Rune.IsControl(rune))
            .Select(rune => rune.Value).Distinct().ToArray();
        if (fontFamily != null)
        {
            var font = LoadFamily(fontFamily.Trim()) ??
                throw new ArgumentException(Loc.Format("PdfAppearanceFontUnavailable", fontFamily));
            foreach (int character in characters)
                if (!font.ContainsGlyph(character))
                    throw new ArgumentException(Loc.Format("PdfAppearanceFontMissingGlyph", fontFamily, $"U+{character:X4}"));
            return font;
        }

        if (existing != null && characters.All(character => CanReuseGlyph(existing, character))) return existing;

        // Keep the current face when its embedded subset lacks newly entered characters.
        if (existing?.GetFontProgram()?.GetFontNames()?.GetFontName() is { Length: > 0 } existingName &&
            LoadFamily(existingName) is { } fullFont && characters.All(fullFont.ContainsGlyph)) return fullFont;

        if (LoadFamily("Malgun Gothic") is { } malgun && characters.All(malgun.ContainsGlyph)) return malgun;
        var helvetica = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        if (characters.All(helvetica.ContainsGlyph)) return helvetica;

        foreach (string path in InstalledFontService.GetSimilarFontFiles("Segoe UI", 400))
            if (LoadFile(path) is { } candidate && characters.All(candidate.ContainsGlyph)) return candidate;

        throw new InvalidOperationException(Loc.Get("NoPdfAppearanceFontSupportsText"));
    }

    private static bool CanReuseGlyph(PdfFont font, int character)
    {
        if (!font.ContainsGlyph(character)) return false;
        string value = char.ConvertFromUtf32(character);
        byte[] code = font.ConvertToBytes(value);
        return code.Length > 0 && font.Decode(new PdfString(code)) == value;
    }

    private static PdfFont? LoadFamily(string family)
    {
        if (Path.IsPathFullyQualified(family)) return LoadFile(family);
        string? path = PdfContentWriter.GetSystemFontPath(family);
        return path == null ? null : LoadFile(path);
    }

    private static PdfFont? LoadFile(string path)
    {
        try
        {
            if (path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) path += ",0";
            return PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error.Message);
            return null;
        }
    }
}
