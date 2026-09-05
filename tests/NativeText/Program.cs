using PDF_simple_edit.Services;
using PDF_simple_edit.Models;
using iText.Kernel.Pdf;
using System.Text.Json;

var path = args.FirstOrDefault() ?? "D:/ASUNA/test/3D knee.pdf";
var bytes = File.ReadAllBytes(path);
var service = new NativePdfTextService();
var pageExtractor = new PdfPageContentExtractor();
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
var blankLine = service.Edit(bytes, 0, new(title, title.Text + "\n\n"));
Check(ReferenceEquals(blankLine.Bytes, bytes), "Blank-line input must not rewrite unchanged painted content");
Check(blankLine.Layout.Text.EndsWith("\n\n") && blankLine.Layout.Glyphs[^1].End.Y > title.Glyphs[^1].Origin.Y + title.LineHeight,
    "Blank-line caret positions were not retained in the live edit buffer");
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
var multi = service.Edit(bytes, 0, new(title, title.Text.Replace("Knee", "Keen").Replace("Study", "Stud")));
foreach (var glyph in multi.Layout.Glyphs.Where(g => !g.IsVirtual && g.TextIndex > 30 && g.TextIndex < 80))
    Check(title.Runs.Any(r => r.Id == glyph.RunId), "Original run style was lost");
Check(multi.Layout.Glyphs.Any(g => g.RunId == title.Runs.Last().Id), "Mixed final font was lost");
File.WriteAllBytes("tmp/pdfs/native-fixed-layout.pdf", multi.Bytes);
try { service.Edit(bytes, 0, new(title, title.Text.Replace("Study", "Study Study Study Study"))); throw new Exception("Overflow was accepted"); }
catch (InvalidOperationException) { checks++; }
var sourceLines = title.Glyphs.Where(g => !g.IsVirtual && g.Origin.Y > title.Glyphs[0].Origin.Y + 1).ToList();
var editedLines = edited.Layout.Glyphs.Where(g => !g.IsVirtual && g.Origin.Y > title.Glyphs[0].Origin.Y + 1).ToList();
SameGlyphs(sourceLines, editedLines);
using (var originalDoc = new PdfDocument(new PdfReader(new MemoryStream(bytes))))
using (var editedDoc = new PdfDocument(new PdfReader(new MemoryStream(multi.Bytes))))
{
    for (int i = 1; i < originalDoc.GetNumberOfPdfObjects(); i++)
        if (originalDoc.GetPdfObject(i) is PdfStream stream && !PdfName.Metadata.Equals(stream.GetAsName(PdfName.Type)))
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
Console.WriteLine("PASS: spacing/scaling, mixed fonts, fixed line origins, overflow rejection, partial TJ, original font/image stream bytes");
foreach (var mode in new[] { TextEditingMode.Legacy, TextEditingMode.PreserveOriginal })
{
    var contents = await pageExtractor.ExtractAsync(bytes, 0, mode);
    var content = contents.First(c => c.Type == PageContentType.Text && c.Text.Contains("Internal Knee"));
    Check((content.NativeText != null) == (mode == TextEditingMode.PreserveOriginal), "Mode dispatch returned the wrong edit model");
    if (mode == TextEditingMode.Legacy)
    {
        Check(content.TextFragments.Count > 0, "Legacy operator targets are missing");
        Check(content.Color != "#FFFFFF" && content.Color != "#000000", "Spot color tint was treated as gray");
        Check(content.Text.Contains("Preliminary"), "Legacy selection must include the mixed-style line suffix to avoid overlap");
        Check(Math.Abs(content.FontSize - 26) < .01, "Legacy font size must come from the PDF transform");
        using var buffer = new MemoryStream();
        using (var document = new PdfDocument(new PdfReader(new MemoryStream(bytes)), new PdfWriter(buffer)))
        {
            var target = new PdfAnnotation { TextFragments = content.TextFragments, IsOriginalTextReplacement = true };
            Check(new PdfContentStreamEditor().RemoveTextAnnotations(document, 0, new[] { target }), "Legacy removal failed after mode switch");
            var lineGroups = content.TextFragments.GroupBy(f => f.LineIndex).OrderBy(g => g.Key).ToList();
            new PdfContentWriter().AddText(document, 0, content.X, content.Y,
                content.Text.Replace("Knee", "Keen"), content.FontFamily, content.FontSize,
                new iText.Kernel.Colors.DeviceRgb(Convert.ToInt32(content.Color[1..3], 16), Convert.ToInt32(content.Color[3..5], 16), Convert.ToInt32(content.Color[5..7], 16)),
                lineHeight: Math.Max(content.LineHeight, content.FontSize * 1.2),
                baselineOffset: content.BaselineOffset, originalFontObjectNumber: content.OriginalFontObjectNumber,
                originalFontObjectNumbersByLine: lineGroups.Select(g => g.First().OriginalFontObjectNumber).ToList(),
                originalLines: lineGroups.Select(g => string.Concat(g.Select(f => f.Text))).ToList());
        }
        var legacyBytes = buffer.ToArray();
        File.WriteAllBytes("tmp/pdfs/legacy-edit.pdf", legacyBytes);
        Check(service.Extract(legacyBytes, 0).Any(b => b.Text.Contains("Keen")), "Legacy redraw did not save the edit");
        // Legacy grouping includes the title's superscript footnote; the native
        // engine keeps that raised run as a separate object. Compare precisely
        // the operators selected by the legacy model, not the native block.
        Check(content.TextFragments.All(f => f.ContentStreamIndex == 0), "Sample title fixture changed content streams");
        var legacyTargets = content.TextFragments.Select(f => $"p0:{f.OperationIndex}:{f.TextOperandIndex}").ToHashSet();
        var untouched = first.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual && !legacyTargets.Contains(g.RunId)).ToList();
        var sourceCopy = (byte[])bytes.Clone();
        var background = await new LegacyTextPreviewService().CreateAsync(bytes, 0,
            new() { TextFragments = content.TextFragments, IsOriginalTextReplacement = true });
        Check(bytes.SequenceEqual(sourceCopy), "Opening legacy preview mutated the source PDF");
        SameGlyphs(untouched, Glyphs(background, 0));
        File.WriteAllBytes("tmp/pdfs/legacy-background.pdf", background);
        SameGlyphs(untouched, Glyphs(legacyBytes, 0).Take(untouched.Count));
        var redrawRuns = service.Extract(legacyBytes, 0).Where(b => b.Text.Contains("Keen")).SelectMany(b => b.Runs);
        Check(redrawRuns.All(r => r.CharacterSpacing >= 0), "Legacy save compressed glyphs with negative tracking");
    }
    Console.WriteLine($"PASS edit mode: {mode}");
}
// Restoring preferences must not change the user's real settings file.
var settingsPath = Path.GetFullPath("tmp/pdfs/edit-mode-settings.txt");
var settingsService = new EditorSettingsService(settingsPath);
foreach (var mode in Enum.GetValues<TextEditingMode>())
{
    settingsService.Save(new(10, 20, 1100, 800), new() { TextEditingMode = mode });
    var loaded = new TextFontSettings();
    var placement = settingsService.Load(loaded);
    Check(loaded.TextEditingMode == mode && placement?.Width == 1100, "Edit mode preference did not round-trip");
}
File.WriteAllText(settingsPath, "TextEditingMode=invalid");
var defaults = new TextFontSettings(); settingsService.Load(defaults);
Check(defaults.TextEditingMode == TextEditingMode.PreserveOriginal, "Invalid mode must preserve the default");
var collisionFixture = Fixtures.CreateCollision();
var collisionBlock = service.Extract(collisionFixture, 0).First(b => b.Text.Contains("Editable"));
try
{
    service.Edit(collisionFixture, 0, new(collisionBlock, collisionBlock.Text + "\nBLOCKER"));
    throw new Exception("New line collided with unselected text");
}
catch (InvalidOperationException error) when (error.Message.Contains("겹칩니다")) { checks++; }
Console.WriteLine("PASS: legacy redraw, saved tracking, mode preference persistence, new-line collision rejection");
Console.WriteLine($"PASS: {checks} assertions (glyph identity/coordinates, all attachment pages, forms, no-op, encoding)");
