using PDF_simple_edit.Services;
using PDF_simple_edit.Models;
using iText.Kernel.Pdf;
using System.Text.Json;

var path = args.FirstOrDefault() ?? "D:/ASUNA/test/3D knee.pdf";
var bytes = File.ReadAllBytes(path);
var service = new NativePdfTextService();
Directory.CreateDirectory("tmp/pdfs");
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
List<NativePdfGlyph> Glyphs(byte[] pdf, int page) => service.Extract(pdf, page).SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual).ToList();
void SameGlyphs(IEnumerable<NativePdfGlyph> expected, IEnumerable<NativePdfGlyph> actual, double dx = 0, double dy = 0)
{
    var a = expected.ToList(); var b = actual.ToList();
    Check(a.Count == b.Count, $"Glyph count {a.Count} != {b.Count}");
    for (int i = 0; i < a.Count; i++)
    {
        Check(a[i].Text == b[i].Text, $"Glyph text mismatch {i}: {a[i].Text} != {b[i].Text}");
        Check(Math.Abs(a[i].Origin.X + dx - b[i].Origin.X) < .003 && Math.Abs(a[i].Origin.Y + dy - b[i].Origin.Y) < .003,
            $"Glyph origin mismatch {i} '{a[i].Text}' {a[i].Origin} != {b[i].Origin}, expected delta {dx},{dy}");
    }
}

int pages;
using (var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)))) pages = doc.GetNumberOfPages();
for (int page = 0; page < pages; page++)
{
    var blocks = service.Extract(bytes, page);
    Check(blocks.Count > 0, $"Page {page + 1} has no extracted text");
    var target = blocks.Where(b => b.Text.Length > 4 && b.Runs.All(r => !r.IsVertical && r.RenderMode < 4))
        .OrderByDescending(b => b.Glyphs.Count).First();
    Check(ReferenceEquals(service.Edit(bytes, page, new(target, target.Text)).Bytes, bytes), "No-op must preserve exact bytes");
    var ids = target.Runs.Select(r => r.Id).ToHashSet();
    var outside = blocks.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual && !ids.Contains(g.RunId)).ToList();
    var deletion = service.Edit(bytes, page, new(target, ""));
    SameGlyphs(outside, Glyphs(deletion.Bytes, page));
    using var originalDoc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
    using var editedDoc = new PdfDocument(new PdfReader(new MemoryStream(deletion.Bytes)));
    for (int other = 0; other < pages; other++)
        if (other != page) Check(originalDoc.GetPage(other + 1).GetContentBytes().SequenceEqual(editedDoc.GetPage(other + 1).GetContentBytes()), "Unselected page content changed");
    Console.WriteLine($"PASS page {page + 1}: {blocks.Count} blocks; no-op and paragraph deletion preserve other text/pages");
}

var first = service.Extract(bytes, 0);
File.WriteAllText("tmp/pdfs/native-original.json", JsonSerializer.Serialize(first, new JsonSerializerOptions { WriteIndented = true }));
var title = first.First(b => b.Text.Contains("Internal Knee"));
var originalTitleGlyphs = title.Glyphs.Where(g => !g.IsVirtual).ToList();
var moved = service.Edit(bytes, 0, new(title, title.Text, 10, 5));
SameGlyphs(originalTitleGlyphs, Glyphs(moved.Bytes, 0).Take(originalTitleGlyphs.Count), 10, 5);
File.WriteAllBytes("tmp/pdfs/native-move.pdf", moved.Bytes);
var edited = service.Edit(bytes, 0, new(title, title.Text.Replace("Knee", "Keen", StringComparison.Ordinal)));
File.WriteAllBytes("tmp/pdfs/native-edit.pdf", edited.Bytes);
Check(service.Extract(edited.Bytes, 0).Any(b => b.Text.Contains("Keen")), "Reopened PDF must contain edited word");
var again = service.Extract(edited.Bytes, 0).First(b => b.Text.Contains("Keen"));
var secondEdit = service.Edit(edited.Bytes, 0, new(again, again.Text.Replace("Keen", "Knee")));
Check(service.Extract(secondEdit.Bytes, 0).Any(b => b.Text.Contains("Knee")), "Repeat edit after save/reopen");
File.WriteAllBytes("tmp/pdfs/native-delete.pdf", service.Edit(bytes, 0, new(title, "")).Bytes);
try { service.Edit(bytes, 0, new(title, title.Text.Replace("Knee", "knee"))); throw new Exception("Missing subset glyph was silently accepted"); }
catch (InvalidOperationException) { checks++; }
Console.WriteLine("PASS attachment: exact translated glyph positions, edit, repeat edit, missing CFF glyph rejection");

var fixture = Fixtures.Create();
File.WriteAllBytes("tmp/pdfs/fixture.pdf", fixture);
for (int page = 0; page < 4; page++)
{
    var blocks = service.Extract(fixture, page);
    var shared = blocks.First(b => b.Text.Contains("Shared form"));
    var before = Glyphs(fixture, page);
    var ids = shared.Runs.Select(r => r.Id).ToHashSet();
    var result = service.Edit(fixture, page, new(shared, shared.Text, 7, -3));
    var after = Glyphs(result.Bytes, page);
    var expected = before.Select(g => ids.Contains(g.RunId) ? g with { Origin = new(g.Origin.X + 7, g.Origin.Y - 3) } : g).ToList();
    SameGlyphs(expected, after);
    foreach (int other in Enumerable.Range(0, 4).Where(p => p != page)) SameGlyphs(Glyphs(fixture, other), Glyphs(result.Bytes, other));
    File.WriteAllBytes($"tmp/pdfs/fixture-form-{page}.pdf", result.Bytes);
    Console.WriteLine($"PASS fixture: CropBox, {page * 90} degree rotation, shared Form and page isolation");
}
var normalBlocks = service.Extract(fixture, 0);
var operandBlock = normalBlocks.First(b => b.Text.StartsWith("AB"));
var allBefore = Glyphs(fixture, 0);
var operandIds = operandBlock.Runs.Select(r => r.Id).ToHashSet();
var removed = service.Edit(fixture, 0, new(operandBlock, ""));
SameGlyphs(allBefore.Where(g => !operandIds.Contains(g.RunId)), Glyphs(removed.Bytes, 0));
File.WriteAllBytes("tmp/pdfs/fixture-delete.pdf", removed.Bytes);
// Replacement with nonzero Tc/Tw/Tz must agree with the actual saved glyphs,
// and quote operators must leave the next T* / relative Tj origin unchanged.
var spacingEdit = service.Edit(fixture, 0, new(operandBlock, operandBlock.Text.Replace("AB", "BA")));
var spacingActual = Glyphs(spacingEdit.Bytes, 0);
foreach (var glyph in spacingEdit.Layout.Glyphs.Where(g => !g.IsVirtual))
    Check(spacingActual.Any(a => a.Text == glyph.Text && Math.Abs(a.Origin.X - glyph.Origin.X) < .003 &&
        Math.Abs(a.Origin.Y - glyph.Origin.Y) < .003), $"Saved glyph differs from live layout: {glyph.Text} {glyph.Origin}");
File.WriteAllBytes("tmp/pdfs/fixture-spacing.pdf", spacingEdit.Bytes);

// Separate modifications must not restyle untouched spans between them.
var multi = service.Edit(bytes, 0, new(title, title.Text.Replace("Knee", "Keen").Replace("Study", "Study Study")));
foreach (var glyph in multi.Layout.Glyphs.Where(g => !g.IsVirtual && g.TextIndex > 30 && g.TextIndex < 80))
    Check(title.Runs.Any(r => r.Id == glyph.RunId), "Original run style was lost");
Check(multi.Layout.Glyphs.Any(g => g.RunId == title.Runs.Last().Id), "Mixed final font was lost");
File.WriteAllBytes("tmp/pdfs/native-reflow.pdf", multi.Bytes);
using (var originalDoc = new PdfDocument(new PdfReader(new MemoryStream(bytes))))
using (var editedDoc = new PdfDocument(new PdfReader(new MemoryStream(multi.Bytes))))
{
    for (int i = 1; i < originalDoc.GetNumberOfPdfObjects(); i++)
        if (originalDoc.GetPdfObject(i) is PdfStream stream)
            Check(editedDoc.GetPdfObject(i) is PdfStream copy && stream.GetBytes().SequenceEqual(copy.GetBytes()),
                $"Original stream {i} was changed (font/image/content)");
}
// Edit one string element of a TJ array, preserving its siblings and following operators.
var oneRun = operandBlock.Runs.First();
var runGlyphs = oneRun.Glyphs.Select((g, i) => g with { TextIndex = i }).ToList();
var oneBlock = new NativePdfTextBlock { Runs = new() { oneRun }, Glyphs = runGlyphs,
    Text = string.Concat(runGlyphs.Select(g => g.Text)), Bounds = PdfTextBox.Union(runGlyphs.Select(g => g.Bounds)),
    Lines = new() { PdfTextBox.Union(runGlyphs.Select(g => g.Bounds)) }, LineHeight = operandBlock.LineHeight };
var oneDeleted = service.Edit(fixture, 0, new(oneBlock, ""));
SameGlyphs(allBefore.Where(g => g.RunId != oneRun.Id), Glyphs(oneDeleted.Bytes, 0));
Console.WriteLine("PASS: character/word spacing, horizontal scaling, mixed-font reflow, partial TJ, original font/image stream bytes");
Console.WriteLine($"PASS: {checks} assertions (glyph identity/coordinates, all attachment pages, forms, no-op, encoding)");
