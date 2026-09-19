using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

var service = new NativePdfTextService();
string output = System.IO.Path.GetFullPath(args.FirstOrDefault() ?? "output/pdf/native-styles");
Directory.CreateDirectory(output);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
byte[] Create(bool forms = false)
{
    using var buffer = new MemoryStream();
    using (var doc = new PdfDocument(new PdfWriter(buffer)))
    {
        var page = doc.AddNewPage();
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        var canvas = new PdfCanvas(page);
        canvas.SetFillColorRgb(.9f, .95f, 1).Rectangle(35, 640, 440, 150).Fill();
        if (forms)
        {
            var form = new PdfFormXObject(new Rectangle(0, 0, 240, 45));
            new PdfCanvas(form, doc).BeginText().SetFontAndSize(font, 14).MoveText(5, 18).ShowText("Sample text").EndText();
            canvas.AddXObjectAt(form, 45, 720).AddXObjectAt(form, 45, 660);
        }
        else
        {
            canvas.BeginText().SetFontAndSize(font, 14).SetLeading(32).SetFillColor(new DeviceCmyk(.2f, .1f, 0, .4f))
                .SetStrokeColorRgb(.2f, .3f, .4f).MoveText(50, 755).ShowText("Sample text")
                .MoveText(230, 0).ShowText("NEIGHBOR").MoveText(-230, -40).ShowText("Following line").EndText();
        }
        canvas.SetFillColorRgb(.2f, .7f, .4f).Rectangle(400, 660, 35, 20).Fill();
    }
    return buffer.ToArray();
}
NativePdfTextBlock Select(byte[] bytes, string text) => service.Extract(bytes, 0).Single(b => b.Text == text);
void SameOther(byte[] before, byte[] after, string text)
{
    var a = Select(before, text); var b = Select(after, text);
    Check(a.Text == b.Text && a.Runs[0].Color == b.Runs[0].Color && Math.Abs(a.Runs[0].FontSize - b.Runs[0].FontSize) < .001, text + " style changed");
    foreach (var (x, y) in a.Glyphs.Zip(b.Glyphs))
        Check(Math.Abs(x.Origin.X - y.Origin.X) < .001 && Math.Abs(x.Origin.Y - y.Origin.Y) < .001, text + " moved");
}

byte[] original = Create();
File.WriteAllBytes(System.IO.Path.Combine(output, "before.pdf"), original);
foreach (var (name, style) in new (string, NativePdfTextStyle)[] {
    ("color", new(Color: "#FF0000")), ("size", new(FontSize: 22)),
    ("bold", new(IsBold: true)), ("italic", new(IsItalic: true)),
    ("family", new(FontFamily: "Times New Roman")),
    ("combined", new(Color: "#0066FF", FontSize: 22, IsBold: true, IsItalic: true, FontFamily: "Arial")) })
{
    var source = Select(original, "Sample text");
    var result = service.Edit(original, 0, new(source, source.Text, Style: style));
    var selected = service.RefreshSelection(result.Bytes, 0, result.Layout)!;
    Check(selected != null && selected.Text == source.Text, name + " text/rebind failed");
    Check(selected!.Runs.All(r => style.Color == null || r.Color == style.Color), name + " color failed");
    Check(selected.Runs.All(r => style.FontSize == null || Math.Abs(r.DisplayFontSize - style.FontSize.Value) < .01), name + " size failed");
    Check(selected.Runs.All(r => style.IsBold == null || r.IsBold == style.IsBold), name + " bold failed");
    Check(selected.Runs.All(r => style.IsItalic == null || r.IsItalic == style.IsItalic), name + " italic failed");
    SameOther(original, result.Bytes, "NEIGHBOR"); SameOther(original, result.Bytes, "Following line");
    using (var doc = new PdfDocument(new PdfReader(new MemoryStream(result.Bytes))))
    {
        string stream = System.Text.Encoding.ASCII.GetString(doc.GetFirstPage().GetContentBytes());
        Check(stream.Contains("400 660 35 20 re"), name + " background changed");
    }
    if (name == "combined")
    {
        var reset = service.Edit(result.Bytes, 0, new(selected, selected.Text, Style: new(IsBold: false, IsItalic: false)));
        var resetSelection = service.RefreshSelection(reset.Bytes, 0, reset.Layout)!;
        Check(resetSelection.Runs.All(r => !r.IsBold && !r.IsItalic && r.Color == "#0066FF" && Math.Abs(r.DisplayFontSize - 22) < .01), "toggle off lost other styles");
        var edited = service.Edit(reset.Bytes, 0, new(resetSelection, "Sample revised"));
        Check(edited.Layout.Text == "Sample revised", "text editing after styling failed");
    }
    File.WriteAllBytes(System.IO.Path.Combine(output, name + ".pdf"), result.Bytes);
    Console.WriteLine("PASS " + name);
}

// A partial selection must not recolor or reposition the remainder of the same Tj.
var whole = Select(original, "Sample text");
var partial = service.RefreshSelection(original, 0, whole, 1, 3)!;
var partialResult = service.Edit(original, 0, new(partial, partial.Text, Style: new(Color: "#FF0000", IsBold: true, FontSize: 10)));
var selectedPartial = service.RefreshSelection(partialResult.Bytes, 0, partialResult.Layout)!;
Check(selectedPartial.Text == "amp", "partial selection expanded");
var outsideBefore = whole.Glyphs.Where(g => g.TextIndex < 1 || g.TextIndex >= 4).ToList();
var allAfter = service.Extract(partialResult.Bytes, 0).SelectMany(b => b.Glyphs).ToList();
foreach (var g in outsideBefore)
{
    Check(allAfter.Any(a => a.Text == g.Text && Math.Abs(a.Origin.X - g.Origin.X) < .001 && Math.Abs(a.Origin.Y - g.Origin.Y) < .001), "partial style moved unselected glyph");
    Check(service.Extract(partialResult.Bytes, 0).SelectMany(b => b.Runs).Any(r => r.Color == whole.Runs[0].Color &&
        r.Glyphs.Any(a => a.Text == g.Text && Math.Abs(a.Origin.X - g.Origin.X) < .001)), "partial style recolored unselected glyph");
}
SameOther(original, partialResult.Bytes, "Following line");
Console.WriteLine("PASS partial selection and cursor restoration");
bool rejectedOverlap = false;
try { service.Edit(original, 0, new(partial, partial.Text, Style: new(FontSize: 36))); }
catch (InvalidOperationException error) { rejectedOverlap = error.Message.Contains("겹칩니다"); }
Check(rejectedOverlap, "overlapping style should be rejected before commit");
Console.WriteLine("PASS overlap protection");

byte[] formBytes = Create(forms: true);
var instances = service.Extract(formBytes, 0).Where(b => b.Text == "Sample text").OrderBy(b => b.Bounds.Y).ToList();
Check(instances.Count == 2, "form fixture");
var formResult = service.Edit(formBytes, 0, new(instances[0], instances[0].Text, Style: new(Color: "#FF0000")));
var finalInstances = service.Extract(formResult.Bytes, 0).Where(b => b.Text == "Sample text").OrderBy(b => b.Bounds.Y).ToList();
Check(finalInstances[0].Runs.All(r => r.Color == "#FF0000") && finalInstances[1].Runs.All(r => r.Color != "#FF0000"), "shared Form instance changed");
Console.WriteLine("PASS shared Form isolation");

var batch = service.EditMany(original, 0, new[] { new NativePdfTextEdit(whole, whole.Text, Style: new(Color: "#FF0000")),
    new NativePdfTextEdit(Select(original, "Following line"), "Following line", Style: new(Color: "#0066FF")) });
Check(batch.Layouts.Count == 2 && Select(batch.Bytes, "Sample text").Runs.All(r => r.Color == "#FF0000") &&
    Select(batch.Bytes, "Following line").Runs.All(r => r.Color == "#0066FF"), "batch failed");
Console.WriteLine("PASS multiple selections");

// Korean fonts typically lack an italic face: slant their actual glyph outlines,
// and verify the style can be removed without moving the baseline or changing size.
using (var buffer = new MemoryStream())
{
    using (var doc = new PdfDocument(new PdfWriter(buffer)))
    {
        var font = PdfFontFactory.CreateFont(@"C:\Windows\Fonts\malgun.ttf", iText.IO.Font.PdfEncodings.IDENTITY_H);
        new PdfCanvas(doc.AddNewPage()).BeginText().SetFontAndSize(font, 18).MoveText(50, 740).ShowText("한글 원본 편집").EndText();
    }
    var koreanBytes = buffer.ToArray();
    var korean = service.Extract(koreanBytes, 0).Single();
    var slanted = service.Edit(koreanBytes, 0, new(korean, korean.Text, Style: new(IsItalic: true, IsBold: true, Color: "#0066FF")));
    var selected = service.RefreshSelection(slanted.Bytes, 0, slanted.Layout)!;
    Check(selected.Text == korean.Text && selected.Runs.All(r => r.IsItalic && r.IsBold && Math.Abs(r.DisplayFontSize - 18) < .01), "Korean italic/bold failed");
    var upright = service.Edit(slanted.Bytes, 0, new(selected, selected.Text, Style: new(IsItalic: false)));
    var restored = service.RefreshSelection(upright.Bytes, 0, upright.Layout)!;
    Check(restored.Runs.All(r => !r.IsItalic && r.IsBold && Math.Abs(r.DisplayFontSize - 18) < .01), "Korean italic removal failed");
    Check(Math.Abs(restored.Glyphs[0].Origin.Y - korean.Glyphs[0].Origin.Y) < .01, "Korean baseline shifted");
    File.WriteAllBytes(System.IO.Path.Combine(output, "korean-italic.pdf"), slanted.Bytes);
    File.WriteAllBytes(System.IO.Path.Combine(output, "korean-upright.pdf"), upright.Bytes);
    var resized = service.Edit(slanted.Bytes, 0, new(selected, selected.Text, Style: new(FontSize: 24)));
    Check(service.RefreshSelection(resized.Bytes, 0, resized.Layout)!.Runs.All(r => r.IsItalic && Math.Abs(r.DisplayFontSize - 24) < .01), "resize lost Korean italic");
}
Console.WriteLine("PASS Korean bold/italic and removal");

// PDF text state and TJ numeric adjustments must be restored at the end of
// a styled operand, including following strings and implicit line advances.
using (var buffer = new MemoryStream())
{
    using (var doc = new PdfDocument(new PdfWriter(buffer)))
    {
        var page = doc.AddNewPage();
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        new PdfCanvas(page).BeginText().SetFontAndSize(font, 16).MoveText(50, 750)
            .WriteLiteral("0.5 Tc 2 Tw 85 Tz 3 Ts 30 TL [(Alpha) -350 ( Beta)] TJ 180 0 Td (TAIL) Tj\n(Second) '\n4 .7 (Third) \"\n( End) Tj\n")
            .EndText();
    }
    byte[] bytes = buffer.ToArray();
    var blocks = service.Extract(bytes, 0);
    var block = blocks.First(b => b.Text.Contains("Alpha"));
    var word = service.RefreshSelection(bytes, 0, block, block.Text.IndexOf("Alpha"), 5)!;
    var changed = service.Edit(bytes, 0, new(word, word.Text, Style: new(Color: "#9900FF", FontSize: 12, IsItalic: true)));
    var untouched = blocks.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual &&
        !word.Glyphs.Any(w => w.RunId == g.RunId && Math.Abs(w.Origin.X - g.Origin.X) < .001)).ToList();
    var after = service.Extract(changed.Bytes, 0).SelectMany(b => b.Glyphs).ToList();
    Check(untouched.All(g => after.Any(a => a.Text == g.Text && Math.Abs(a.Origin.X - g.Origin.X) < .002 &&
        Math.Abs(a.Origin.Y - g.Origin.Y) < .002)), "TJ/text state moved following glyphs");
    Console.WriteLine("PASS TJ, spacing, scaling, rise and quote operators");
}

using (var buffer = new MemoryStream())
{
    using (var doc = new PdfDocument(new PdfWriter(buffer)))
    {
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        new PdfCanvas(doc.AddNewPage()).BeginText().SetFontAndSize(font, 14).SetLeading(18).MoveText(50, 750)
            .ShowText("A paragraph with several words").NewlineShowText("and a second line with words").EndText();
    }
    var bytes = buffer.ToArray(); var block = service.Extract(bytes, 0).Single();
    var enlarged = service.Edit(bytes, 0, new(block, block.Text, Style: new(FontSize: 20, IsBold: true)));
    Check(enlarged.Layout.Lines.Count > block.Lines.Count, "enlarged paragraph did not wrap");
    var lines = enlarged.Layout.Lines.OrderBy(b => b.Y).ToList();
    Check(lines.Zip(lines.Skip(1)).All(pair => pair.First.Bottom <= pair.Second.Y + .01), "styled paragraph lines overlap");
    Check(service.RefreshSelection(enlarged.Bytes, 0, enlarged.Layout)!.Text == block.Text, "paragraph lost text or spaces");
    File.WriteAllBytes(System.IO.Path.Combine(output, "paragraph.pdf"), enlarged.Bytes);
    Console.WriteLine("PASS multiline reflow");
}
Check(ReferenceEquals(original, service.Edit(original, 0, new(whole, whole.Text)).Bytes), "no-op rewrote document");
try { service.Edit(original, 0, new(whole, whole.Text, Style: new(FontSize: double.NaN))); throw new Exception("invalid size accepted"); }
catch (ArgumentOutOfRangeException) { }
Console.WriteLine("PASS no-op and invalid size");
foreach (string name in new[] { "before", "combined", "korean-italic", "korean-upright", "paragraph" })
{
    using var input = new MemoryStream(File.ReadAllBytes(System.IO.Path.Combine(output, name + ".pdf")));
    var doc = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input.AsRandomAccessStream());
    using var page = doc.GetPage(0);
    using var rendered = new MemoryStream();
    await page.RenderToStreamAsync(rendered.AsRandomAccessStream(), new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 900 });
    File.WriteAllBytes(System.IO.Path.Combine(output, name + "-windows.png"), rendered.ToArray());
}
Console.WriteLine("PASS Windows PDF rendering");
Console.WriteLine("All native style regression checks passed.");
