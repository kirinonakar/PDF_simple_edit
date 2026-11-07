using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

int checks = 0;
void Equal(string actual, string expected, string name)
{
    if (actual != expected) throw new Exception($"{name}: expected [{expected}], got [{actual}]");
    checks++;
}

NativePdfTextBlock Line(string text, double y = 0, bool vertical = false)
{
    var glyphs = new List<NativePdfGlyph>();
    for (int i = 0; i < text.Length; i++)
    {
        glyphs.Add(new() { Text = text[i].ToString(), TextIndex = i,
            Origin = vertical ? new(y, i * 10) : new(i * 10, y + 10),
            End = vertical ? new(y, (i + 1) * 10) : new((i + 1) * 10, y + 10),
            Bounds = vertical ? new(y, i * 10, 10, 10) : new(i * 10, y, 10, 10) });
    }
    return new() { Text = text, Glyphs = glyphs };
}

string Select(NativePdfTextBlock[] blocks, PdfTextPoint start, PdfTextPoint end) =>
    PdfTextSelectionService.GetSelectedText(PdfTextSelectionService.SelectRange(blocks, start, end));

var hello = Line("Hello world");
Equal(Select([hello], new(10, 5), new(40, 5)), "ell", "partial characters, not whole block");
Equal(Select([hello], new(40, 5), new(10, 5)), "ell", "backward drag");
Equal(Select([hello], new(0, 5), new(110, 5)), "Hello world", "spaces retained");
Equal(Select([hello], new(10, 5), new(10, 5)), "", "same caret");
Equal(Select([], new(0, 0), new(100, 100)), "", "image-only page");
Equal(Select([Line("   ")], new(0, 5), new(30, 5)), "", "whitespace-only page");
Equal(Select([Line("첫째"), Line("둘째", 30)], new(0, 5), new(20, 35)),
    "첫째" + Environment.NewLine + "둘째", "separate blocks and Unicode");
Equal(Select([Line("ABC", vertical: true)], new(5, 0), new(5, 30)), "ABC", "rotated baseline");

var multiline = Line("AB");
multiline.Glyphs.Add(new() { Text = " ", TextIndex = 2, IsVirtual = true, IsSoftBreak = true });
multiline.Glyphs.AddRange(Line("CD", 30).Glyphs.Select(g => g with { TextIndex = g.TextIndex + 3 }));
multiline = multiline with { Text = "AB CD" };
Equal(Select([multiline], new(0, 5), new(20, 35)), "AB" + Environment.NewLine + "CD", "virtual line breaks");
Equal(Select([multiline], new(10, 35), new(10, 5)), "B" + Environment.NewLine + "C", "backward multiline partial range");

var ligature = Line("X") with { Text = "ffi", Glyphs = [Line("X").Glyphs[0] with { Text = "ffi" }] };
Equal(Select([ligature], new(0, 5), new(10, 5)), "ffi", "multi-character glyph");
Equal(hello.Text, "Hello world", "source text unchanged");
Equal(hello.Glyphs.Count.ToString(), "11", "source glyph list unchanged");
Console.WriteLine($"Passed {checks} text selection checks.");

// The linked geometry models reference localization only for unrelated errors.
namespace PDF_simple_edit.Services
{
    internal static class LocalizationService
    {
        public static string Get(string key) => key;
    }
}
