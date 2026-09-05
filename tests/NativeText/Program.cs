using PDF_simple_edit.Services;
using PDF_simple_edit.Models;
using iText.Kernel.Pdf;
using System.Text.Json;

await FontFallbackChecks.Run();
if (args.Contains("--fallback-only")) return;
var path = args.FirstOrDefault() ?? "D:/ASUNA/test/3D knee.pdf";
var bytes = File.ReadAllBytes(path);
var service = new NativePdfTextService();
var pageExtractor = new PdfPageContentExtractor();
await SelectionChecks.Run(bytes);
if (args.Contains("--selection-only"))
{
    return;
}
Directory.CreateDirectory("tmp/pdfs");
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
List<NativePdfGlyph> Glyphs(byte[] pdf, int page) => service.Extract(pdf, page).SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual).ToList();
void SameGlyphs(IEnumerable<NativePdfGlyph> expected, IEnumerable<NativePdfGlyph> actual, double dx = 0, double dy = 0)
{
    var a = expected.OrderBy(g => Math.Round(g.Origin.Y, 2)).ThenBy(g => Math.Round(g.Origin.X, 2)).ThenBy(g => g.Text).ToList(); var b = actual.OrderBy(g => Math.Round(g.Origin.Y - dy, 2)).ThenBy(g => Math.Round(g.Origin.X - dx, 2)).ThenBy(g => g.Text).ToList();
    Check(a.Count == b.Count, $"Glyph count {a.Count} != {b.Count}");
    for (int i = 0; i < a.Count; i++)
    {
        Check(a[i].Text == b[i].Text, $"Glyph text mismatch {i}: {a[i].Text} != {b[i].Text}");
        Check(Math.Abs(a[i].Origin.X + dx - b[i].Origin.X) < .003 && Math.Abs(a[i].Origin.Y + dy - b[i].Origin.Y) < .003,
            $"Glyph origin mismatch {i} '{a[i].Text}' {a[i].Origin} != {b[i].Origin}, expected delta {dx},{dy}");
    }
}

int pages;
var paragraphBytes = Fixtures.CreateParagraphs();
var paragraphs = service.Extract(paragraphBytes, 0);
var leftParagraph = paragraphs.Single(b => b.Text.StartsWith("Alpha beta gamma"));
Check(leftParagraph.Lines.Count == 3 && !leftParagraph.Text.Contains("Separate"), "Interleaved columns merged");
Check(paragraphs.Single(b => b.Text.StartsWith("One two")).Lines.Count == 3, "Short paragraph lines disconnected");
Check(paragraphs.Single(b => b.Text.StartsWith("Right column")).Text == "Right column Other column", "Narrow gutter lost right column");
Check(paragraphs.Any(b => b.Text == "Alpha beta Alpha beta"), "Narrow gutter merged columns");
var cells = paragraphs.Where(b => b.Bounds.Y > 400).ToList();
Check(cells.Count == 2 && cells.Any(b => b.Text == "Left Left") && cells.Any(b => b.Text == "Right Right"), "Columns inside one TJ operand merged");
var bothCellsDeleted = service.EditMany(paragraphBytes, 0, cells.Select(b => new NativePdfTextEdit(b, "")).ToList());
SameGlyphs(Glyphs(paragraphBytes, 0).Where(g => g.Origin.Y < 400), Glyphs(bothCellsDeleted.Bytes, 0));
var flowInsert = service.Edit(paragraphBytes, 0, new(leftParagraph, "Alpha beta " + leftParagraph.Text));
var flowDelete = service.Edit(paragraphBytes, 0, new(leftParagraph, leftParagraph.Text[23..]));
Check(flowInsert.Layout.Lines.Count > leftParagraph.Lines.Count, "Insertion did not flow down");
Check(flowDelete.Layout.Lines.Count < leftParagraph.Lines.Count, "Deletion did not pull up");
await WindowsRenderingProbe.Render(flowInsert.Bytes, "paragraph-insert", flowInsert.Layout);
await WindowsRenderingProbe.Render(flowDelete.Bytes, "paragraph-delete", flowDelete.Layout);
var lineGlyphs = leftParagraph.Glyphs.Where(g => !g.IsVirtual && Math.Abs(g.Origin.Y - leftParagraph.Glyphs[0].Origin.Y) < .1).ToList();
var singleLine = NativePdfTextService.SelectRegion(paragraphs, leftParagraph.Lines[0]).Single();
Check(singleLine.Lines.Count == 1, "Single-line marquee included other lines");
var singleLineCodes = singleLine.Glyphs.Where(g => !g.IsVirtual).Select(g => g.Code).ToHashSet();
var singleLineOutside = paragraphs.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual && !singleLineCodes.Contains(g.Code)).ToList();
foreach (var replacement in new[] { singleLine.Text + " extra words", singleLine.Text.Insert(6, "longwordlongword ") })
{
    var expanded = service.Edit(paragraphBytes, 0, new(singleLine, replacement));
    Check(expanded.Layout.Lines.Count == 1 && expanded.Layout.Bounds.Right > singleLine.Bounds.Right + 10,
        "Single-line insertion must expand right without wrapping");
    Check(Math.Abs(expanded.Layout.Bounds.X - singleLine.Bounds.X) < .003 &&
        expanded.Layout.Glyphs.Where(g => !g.IsVirtual).All(g => Math.Abs(g.Origin.Y - lineGlyphs[0].Origin.Y) < .003),
        "Single-line expansion moved the left edge or baseline");
    SameGlyphs(singleLineOutside.Concat(expanded.Layout.Glyphs.Where(g => !g.IsVirtual)), Glyphs(expanded.Bytes, 0));
}
var shortenedLine = service.Edit(paragraphBytes, 0, new(singleLine, "Alpha"));
Check(shortenedLine.Layout.Lines.Count == 1 && shortenedLine.Layout.Bounds.Right < singleLine.Bounds.Right,
    "Single-line deletion must shrink the layout");
var explicitBreak = service.Edit(paragraphBytes, 0, new(singleLine, singleLine.Text + "\n"));
Check(explicitBreak.Layout.Glyphs[^1].Text == "\n" && explicitBreak.Layout.Glyphs[^1].End.Y > lineGlyphs[0].Origin.Y,
    "Single-line editing must retain explicit newlines");
Console.WriteLine("PASS: single-line right expansion, saved glyph positions, outside text preservation, deletion and explicit newline");
var selectedBox = PdfTextBox.Union(lineGlyphs.Skip(6).Take(4).Select(g => g.Bounds));
var partial = NativePdfTextService.SelectRegion(paragraphs, selectedBox).Single();
Check(partial.Text == "beta", $"Marquee expanded outside requested glyphs: {partial.Text}");
var partialResult = service.Edit(paragraphBytes, 0, new(partial, ""));
var selectedOrigins = partial.Glyphs.Where(g => !g.IsVirtual).Select(g => g.Origin).ToHashSet();
SameGlyphs(Glyphs(paragraphBytes, 0).Where(g => !selectedOrigins.Contains(g.Origin)).OrderBy(g => g.Origin.Y).ThenBy(g => g.Origin.X), Glyphs(partialResult.Bytes, 0).OrderBy(g => g.Origin.Y).ThenBy(g => g.Origin.X));
var multiple = NativePdfTextService.SelectRegion(paragraphs, new(35, 350, 165, 45));
Check(multiple.Count == 2 && multiple.All(b => !b.Text.Contains("beta Right")), "Marquee merged separate columns");
Console.WriteLine("PASS: column gutters, paragraph reflow, exact partial-operand marquee deletion");
using (var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)))) pages = doc.GetNumberOfPages();
for (int page = 0; page < pages; page++)
{
    var blocks = service.Extract(bytes, page);
    Check(blocks.Count > 0, $"Page {page + 1} has no extracted text");
    var target = blocks.Where(b => b.Text.Length > 4 && b.Runs.All(r => !r.IsVertical && r.RenderMode < 4))
        .OrderByDescending(b => b.Glyphs.Count).First();
    Check(ReferenceEquals(service.Edit(bytes, page, new(target, target.Text)).Bytes, bytes), "No-op must preserve exact bytes");
    var ids = target.Runs.Select(r => r.Id).ToHashSet();
    var selectedCodesForPage = target.Glyphs.Where(g => !g.IsVirtual).Select(g => g.Code).ToHashSet();
    var outside = blocks.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual && !selectedCodesForPage.Contains(g.Code)).ToList();
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
var body = first.First(b => b.Text.StartsWith("To prospectively"));
foreach (var (name, text) in new[] { ("body-insert", "the " + body.Text), ("body-delete", body.Text[17..]) })
{
    var result = service.Edit(bytes, 0, new(body, text));
    Check(result.Layout.Glyphs.Any(g => !g.IsVirtual && body.Glyphs.Any(s => ReferenceEquals(s.Code, g.Code) && Math.Abs(s.Origin.Y - g.Origin.Y) > 1)), "Body edit did not move glyphs between lines");
    File.WriteAllBytes($"tmp/pdfs/{name}.pdf", result.Bytes);
    await WindowsRenderingProbe.Render(result.Bytes, name, result.Layout);
}
var bodySelection = NativePdfTextService.SelectRegion(first, body.Lines[1]).Single();
Check(bodySelection.Lines.Count == 1, "Attachment marquee included unselected lines");
var bodyRemoved = service.Edit(bytes, 0, new(bodySelection, ""));
var bodySelectedOrigins = bodySelection.Glyphs.Where(g => !g.IsVirtual).Select(g => (g.RunId, g.Origin)).ToHashSet();
SameGlyphs(first.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual && !bodySelectedOrigins.Contains((g.RunId, g.Origin))), Glyphs(bodyRemoved.Bytes, 0));
File.WriteAllBytes("tmp/pdfs/body-region-delete.pdf", bodyRemoved.Bytes);
await WindowsRenderingProbe.Render(bodyRemoved.Bytes, "body-region-delete");
Check(title.Lines.Count == 4 && title.Text.Contains("FISP MR Sequence"), "Mixed-style title must be one flowing paragraph");
var originalTitleGlyphs = title.Glyphs.Where(g => !g.IsVirtual).ToList();
await WindowsRenderingProbe.Render(bytes, "original");
// Reproduce consecutive new keystrokes, rather than only permutations of glyphs
// already present in an edited span. Each newly encoded glyph needs its own advance.
foreach (var (name, text) in new[]
{
    ("middle-delete", title.Text.Remove(5, 1)),
    ("insert-one", title.Text.Insert(5, "e")),
    ("insert-two", title.Text.Insert(5, "ee")),
    ("insert-three", title.Text.Insert(5, "eee")),
})
{
    var result = service.Edit(bytes, 0, new(title, text));
    File.WriteAllBytes($"tmp/pdfs/{name}.pdf", result.Bytes);
    File.WriteAllText($"tmp/pdfs/{name}-layout.json", JsonSerializer.Serialize(result.Layout));
    await WindowsRenderingProbe.Render(result.Bytes, name, result.Layout);
    var actual = Glyphs(result.Bytes, 0);
    foreach (var glyph in result.Layout.Glyphs.Where(g => !g.IsVirtual))
    {
        Check(glyph.Advance > 0, $"{name}: nonpositive advance for '{glyph.Text}' at {glyph.TextIndex}: {glyph.Advance}");
        Check(actual.Any(g => g.Text == glyph.Text && Math.Abs(g.Origin.X - glyph.Origin.X) < .003 && Math.Abs(g.Origin.Y - glyph.Origin.Y) < .003),
            $"{name}: saved glyph differs from live layout: '{glyph.Text}' {glyph.Origin}");
    }
    Console.WriteLine($"PASS consecutive input: {name}");
}
var reopenedBytes = File.ReadAllBytes("tmp/pdfs/middle-delete.pdf");
var reopenedBlock = service.Extract(reopenedBytes, 0).First(b => b.Text.Contains("Interal Knee"));
var resumedInput = service.Edit(reopenedBytes, 0, new(reopenedBlock, reopenedBlock.Text.Insert(5, "nn")));
File.WriteAllBytes("tmp/pdfs/reopen-insert.pdf", resumedInput.Bytes);
File.WriteAllText("tmp/pdfs/reopen-insert-layout.json", JsonSerializer.Serialize(resumedInput.Layout));
await WindowsRenderingProbe.Render(resumedInput.Bytes, "reopen-insert", resumedInput.Layout);
Check(service.Extract(resumedInput.Bytes, 0).Any(b => b.Text.Contains("Internnal Knee")), "Consecutive typing after reopening lost characters");
var blankLine = service.Edit(bytes, 0, new(title, title.Text + "\n\n"));
Check(ReferenceEquals(blankLine.Bytes, bytes), "Blank-line input must not rewrite unchanged painted content");
Check(blankLine.Layout.Text.EndsWith("\n\n") && blankLine.Layout.Glyphs[^1].End.Y > title.Glyphs[^1].Origin.Y + title.LineHeight,
    "Blank-line caret positions were not retained in the live edit buffer");
var moved = service.Edit(bytes, 0, new(title, title.Text, 10, 5));
SameGlyphs(originalTitleGlyphs, Glyphs(moved.Bytes, 0).Take(originalTitleGlyphs.Count), 10, 5);
File.WriteAllBytes("tmp/pdfs/native-move.pdf", moved.Bytes);
await WindowsRenderingProbe.Render(moved.Bytes, "move", moved.Layout);
var edited = service.Edit(bytes, 0, new(title, title.Text.Replace("Knee", "Keen", StringComparison.Ordinal)));
File.WriteAllBytes("tmp/pdfs/native-edit.pdf", edited.Bytes);
Check(service.Extract(edited.Bytes, 0).Any(b => b.Text.Contains("Keen")), "Reopened PDF must contain edited word");
var again = service.Extract(edited.Bytes, 0).First(b => b.Text.Contains("Keen"));
var secondEdit = service.Edit(edited.Bytes, 0, new(again, again.Text.Replace("Keen", "Knee")));
Check(service.Extract(secondEdit.Bytes, 0).Any(b => b.Text.Contains("Knee")), "Repeat edit after save/reopen");
File.WriteAllBytes("tmp/pdfs/native-delete.pdf", service.Edit(bytes, 0, new(title, "")).Bytes);
var cffFallback = service.Edit(bytes, 0, new(title, title.Text.Replace("Knee", "knee")));
Check(cffFallback.FontSubstitutionStatus != null, "Missing CFF outline must report font substitution");
Check(service.Extract(cffFallback.Bytes, 0).Any(b => b.Text.Contains("knee")), "CFF fallback lost text");
await WindowsRenderingProbe.Render(cffFallback.Bytes, "cff-fallback", cffFallback.Layout);
Console.WriteLine("PASS attachment: exact translated glyph positions, edit, repeat edit, missing CFF glyph fallback");

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
var expandedTitle = service.Edit(bytes, 0, new(title, title.Text.Replace("Study", "Study Study Study Study")));
Check(expandedTitle.Layout.Lines.Count > title.Lines.Count, "Title overflow must wrap to the next line");
await WindowsRenderingProbe.Render(expandedTitle.Bytes, "paragraph-title", expandedTitle.Layout);
File.WriteAllBytes("tmp/pdfs/paragraph-title.pdf", expandedTitle.Bytes);
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
Console.WriteLine("PASS: spacing/scaling, mixed fonts, paragraph wrapping, partial TJ, original font/image stream bytes");
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
