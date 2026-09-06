using PDF_simple_edit.Services;
using PDF_simple_edit.Models;
using Windows.Graphics.Imaging;

// Run on Windows with the user-supplied regression fixture; never modify it.
if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--knee")
    throw new ArgumentException("Pass a fixture path and optionally --knee for 3D knee.pdf.");
bool knee = args.Length == 2;
int pageIndex = knee ? 1 : 0;
int otherPageIndex = knee ? 0 : 1;
var bytes = File.ReadAllBytes(args[0]);
var service = new NativePdfTextService();
var blocks = service.Extract(bytes, pageIndex);
var block = blocks.Single(b => b.Text.StartsWith(knee ? "Between" : "The recently revised"));
if (knee && (!block.Text.StartsWith("Between February 2005 and January 2006,") ||
    !block.Text.Contains("surgical intervention that pre-vented") || block.Lines.Count != 17))
    throw new Exception("Justified words were split out of the knee paragraph.");
var artifactDirectory = Path.GetFullPath(knee ? "tmp/pdfs/knee-text-regression" : "tmp/pdfs/native-text-regression");
Directory.CreateDirectory(artifactDirectory);
var original = await Render(bytes, pageIndex, "original");
var otherPage = await Render(bytes, otherPageIndex, "other-original");
foreach (var (label, selected, text) in new[] {
    ("delete", block, ""),
    ("backspace", block, block.Text[1..]),
    ("replace", block, "Test " + block.Text[4..]),
    ("region-delete", NativePdfTextService.SelectRegion(blocks, block.Bounds).Single(), "") })
{
    var result = service.Edit(bytes, pageIndex, new(selected, text));
    if (result.FontSubstitutionStatus != null)
        throw new Exception(label + ": Existing abstract characters unnecessarily use a fallback font.");
    string savedPath = Path.Combine(artifactDirectory, label + ".pdf");
    File.WriteAllBytes(savedPath, result.Bytes);
    var saved = File.ReadAllBytes(savedPath);
    var rendered = await Render(saved, pageIndex, label);
    if (original.Pixels.AsSpan().SequenceEqual(rendered.Pixels))
        throw new Exception(label + ": Windows PDF still displays the original text.");
    if (original.Width != rendered.Width || original.Height != rendered.Height)
        throw new Exception(label + ": Page dimensions changed.");
    // Compare decoded pixels, not PNG metadata. The rest of the page must stay intact.
    for (int y = 0; y < original.Height; y++)
    for (int x = 0; x < original.Width; x++)
    {
        double pdfX = x / original.Scale, pdfY = y / original.Scale;
        if (block.Bounds.Contains(pdfX, pdfY, 3)) continue;
        int offset = (y * original.Width + x) * 4;
        if (!original.Pixels.AsSpan(offset, 4).SequenceEqual(rendered.Pixels.AsSpan(offset, 4)))
            throw new Exception($"{label}: Pixels outside the abstract changed at {pdfX}, {pdfY}.");
    }
    var nextPage = await Render(saved, otherPageIndex, label + "-other");
    if (!otherPage.Pixels.AsSpan().SequenceEqual(nextPage.Pixels))
        throw new Exception(label + ": Unedited page changed.");
    var reopened = service.Extract(saved, pageIndex);
    string actual = string.Concat(reopened.SelectMany(b => b.Glyphs)
        .Where(g => !g.IsVirtual && block.Bounds.Contains(g.Origin.X, g.Origin.Y, 3)).Select(g => g.Text));
    string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    if (Normalize(actual) != Normalize(text))
        throw new Exception(label + ": Saved abstract text does not match the edit.");
    Console.WriteLine($"PASS {label}: Windows rendering, saved text, surrounding pixels and unedited page.");
}
var sampleGlyphs = block.Glyphs.Where(g => !g.IsVirtual && g.Text.Length == 1 && !char.IsWhiteSpace(g.Text[0]))
    .DistinctBy(g => g.Text).ToList();
foreach (var glyph in sampleGlyphs)
{
    var result = service.Edit(bytes, pageIndex, new(block, block.Text + glyph.Text));
    var added = result.Layout.Glyphs.Single(g => g.TextIndex == block.Text.Length);
    if (result.FontSubstitutionStatus != null || !added.Code.AsSpan().SequenceEqual(glyph.Code))
        throw new Exception($"Adding '{glyph.Text}' did not reuse the original font/code.");
    if (ReferenceEquals(added.Code, glyph.Code))
        throw new Exception("Inserted glyph must have its own identity for kerning.");
}
Console.WriteLine($"PASS {sampleGlyphs.Count} existing characters appended without font substitution.");

// Evidence may be outside the marquee, but must belong to the same PDF font.
var firstGlyph = block.Glyphs.First(g => !g.IsVirtual);
var singleLetter = NativePdfTextService.SelectRegion(blocks, firstGlyph.Bounds).Single();
var regionResult = service.Edit(bytes, pageIndex, new(singleLetter, "h"));
if (singleLetter.Text != (knee ? "B" : "T") || regionResult.FontSubstitutionStatus != null)
    throw new Exception("A character outside the selection was not reused from the same font.");
await Render(regionResult.Bytes, pageIndex, "region-existing-glyph");
var missing = service.Edit(bytes, pageIndex, new(block, block.Text + "한"));
if (missing.FontSubstitutionStatus == null)
    throw new Exception("A genuinely unavailable glyph must still use fallback.");
Console.WriteLine("PASS partial selection and missing-glyph fallback.");

if (knee)
{
    var obstacle = blocks.Single(b => b.Text == "Patients").Glyphs.First(g => !g.IsVirtual);
    bool rejected = false;
    try
    {
        service.Edit(bytes, pageIndex, new(singleLetter, "h",
            obstacle.Origin.X - firstGlyph.Origin.X, obstacle.Origin.Y - firstGlyph.Origin.Y));
    }
    catch (InvalidOperationException e) when (e.Message.Contains("다른 원본 텍스트와 겹칩니다")) { rejected = true; }
    if (!rejected) throw new Exception("A real collision with the Patients heading was accepted.");
    Console.WriteLine("PASS actual collision with unselected text is still rejected.");
}

if (!File.ReadAllBytes(args[0]).AsSpan().SequenceEqual(bytes))
    throw new Exception("Source fixture was modified.");

async Task<(byte[] Pixels, int Width, int Height, double Scale)> Render(byte[] content, int index, string? label)
{
    using var input = new MemoryStream(content);
    var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input.AsRandomAccessStream());
    using var page = pdf.GetPage((uint)index);
    using var image = new MemoryStream();
    await page.RenderToStreamAsync(image.AsRandomAccessStream());
    if (label != null) File.WriteAllBytes(Path.Combine(artifactDirectory, label + ".png"), image.ToArray());
    image.Position = 0;
    var decoder = await BitmapDecoder.CreateAsync(image.AsRandomAccessStream());
    var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
        new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    return (pixels.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight,
        decoder.PixelWidth / (page.Size.Width * 72 / 96));
}

