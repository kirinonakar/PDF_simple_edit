using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Font;
using iText.IO.Font.Constants;
using iText.Forms;
using iText.Forms.Fields;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using AnnotationType = PDF_simple_edit.Models.AnnotationType;
using EditorAnnotation = PDF_simple_edit.Models.PdfAnnotation;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
byte[] Rewrite(byte[] bytes, Action<PdfDocument> action) => PdfDocumentManager.RewritePdfBytes(bytes, action);
byte[] Create(int rotation = 0)
{
    using var output = new MemoryStream();
    using (var document = new PdfDocument(new PdfWriter(output)))
    {
        var page = document.AddNewPage(new PageSize(500, 700));
        page.SetCropBox(new Rectangle(20, 30, 450, 630)); page.SetRotation(rotation);
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        new PdfCanvas(page).BeginText().SetFontAndSize(font, 12).MoveText(50, 600).ShowText("First author name")
            .MoveText(0, -24).ShowText("Second line stays readable").EndText();
        document.AddNewPage();
        page.AddAnnotation(new PdfLinkAnnotation(new Rectangle(50, 500, 80, 15))
            .SetAction(PdfAction.CreateURI("https://example.com")));
        PdfAcroForm.GetAcroForm(document, true).AddField(new TextFormFieldBuilder(document, "KeepField")
            .SetWidgetRectangle(new Rectangle(50, 450, 100, 24)).CreateText(), page);
        var root = document.GetOutlines(false);
        var first = root.AddOutline("First chapter");
        first.AddDestination(PdfExplicitDestination.CreateFit(page));
        first.AddOutline("Child chapter").AddDestination(PdfExplicitDestination.CreateFit(document.GetPage(2)));
        document.AddNamedDestination("named", PdfExplicitDestination.CreateFit(document.GetPage(2)).GetPdfObject());
        root.AddOutline("Named chapter").AddDestination(new PdfStringDestination("named"));
    }
    return output.ToArray();
}

string outputDirectory = System.IO.Path.GetFullPath("tmp/pdfs/annotations");
Directory.CreateDirectory(outputDirectory);
foreach (int rotation in new[] { 0, 90, 180, 270 })
{
    byte[] source = Create(rotation);
    var box = new PdfTextBox(70, 80, 110, 13);
    var box2 = new PdfTextBox(70, 105, 145, 13);
    byte[] marked = Rewrite(source, document =>
    {
        PdfCommentService.AddTextMarkup(document, 0, new[] { box, box2 }, false, ColorConstants.YELLOW, .65f, "Two lines");
        PdfCommentService.AddTextMarkup(document, 0, new[] { box }, true, ColorConstants.RED, 1, "Underline");
        PdfCommentService.AddNoteOrTextBox(document, 0, new(220, 80, 20, 20), true, "한글 메모\nSecond line", ColorConstants.YELLOW, 1, 12);
        PdfCommentService.AddNoteOrTextBox(document, 0, new(70, 170, 200, 75), false, "한글 텍스트 상자\nComment text box", new DeviceRgb(240, 245, 255), 1, 12);
        PdfCommentService.AddTextMarkup(document, 1, new[] { box }, false, ColorConstants.YELLOW, 1, "Other page");
    });
    var comments = PdfCommentService.Read(marked, 0);
    Check(comments.Count == 4, $"read four real annotations at {rotation}");
    var highlight = comments.Single(a => a.PdfCommentSubtype == "Highlight");
    Check(highlight.PdfCommentHitBounds.Count == 2 && Math.Abs(highlight.PdfCommentHitBounds[0].X - box.X) < .01 &&
        Math.Abs(highlight.PdfCommentHitBounds[0].Y - box.Y) < .01, $"rotated crop coordinates {rotation}");
    var content = new AnnotationContentService();
    Check(content.FindAnnotationAt(comments, 0, 100, 85, EditToolMode.SelectAnnotations)?.PdfCommentSubtype == "Underline", "foreground comment wins");
    Check(content.FindAnnotationAt(comments, 0, 100, 100, EditToolMode.SelectAnnotations) == null, "gap between quads is not selectable");
    Check(content.FindAnnotationAt(comments, 0, 100, 85, EditToolMode.Select) == null &&
        content.FindAnnotationAt(comments, 0, 100, 85, EditToolMode.SelectGraphics) == null, "page tools do not edit comments");
    var note = comments.Single(a => a.PdfCommentSubtype == "Text");
    Check(note.Content == "한글 메모\nSecond line" && !note.IsCommentOpen, "Unicode note starts closed");
    byte[] opened = Rewrite(marked, doc => PdfCommentService.SetNoteOpen(doc, note, true));
    var openedNote = PdfCommentService.Read(opened, 0).Single(a => a.PdfCommentSubtype == "Text");
    Check(openedNote.IsCommentOpen && openedNote.PdfCommentKey == note.PdfCommentKey, "note visibility is persistent with stable identity");
    var bookmarks = PdfBookmarkService.Read(opened);
    Check(bookmarks.Count == 2 && bookmarks[0].PageIndex == 0 && bookmarks[0].Children.Single().PageIndex == 1 &&
        bookmarks[1].PageIndex == 1, "nested and named bookmarks resolve");
    byte[] deleted = Rewrite(opened, doc => Check(PdfCommentService.Remove(doc, new[] { openedNote }) == 1, "individual deletion count"));
    using (var document = new PdfDocument(new PdfReader(new MemoryStream(deleted))))
        Check(!document.GetPage(1).GetAnnotations().Any(a => PdfName.Text.Equals(a.GetSubtype()) || PdfName.Popup.Equals(a.GetSubtype())), "individual deletion removes attached popup");
    byte[] cleared = Rewrite(deleted, doc => Check(PdfCommentService.Remove(doc) == 4, "all-page deletion count"));
    using (var document = new PdfDocument(new PdfReader(new MemoryStream(cleared))))
    {
        Check(document.GetPage(1).GetAnnotations().Count == 2 && document.GetPage(2).GetAnnotations().Count == 0, "links/widgets preserved, all-page comments removed");
        Check(PdfAcroForm.GetAcroForm(document, false).GetField("KeepField") != null, "form remains intact");
        using var original = new PdfDocument(new PdfReader(new MemoryStream(source)));
        Check(document.GetPage(1).GetContentBytes().AsSpan().SequenceEqual(original.GetPage(1).GetContentBytes()), "page content is unchanged by comments and deletion");
    }
    Check(PdfBookmarkService.Read(cleared).Count == 2, "bookmarks survive deletion");
    var actual = new NativePdfTextService().Extract(source, 0).First(b => b.Text.Contains("First author"));
    var actualBoxes = PdfCommentService.SelectText(new[] { actual }, actual.Bounds, out string actualText, out var actualQuads);
    var firstGlyph = actual.Glyphs.First(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text));
    var q = actualQuads[0];
    double cross = (q[1].X - q[0].X) * (firstGlyph.End.Y - firstGlyph.Origin.Y) -
        (q[1].Y - q[0].Y) * (firstGlyph.End.X - firstGlyph.Origin.X);
    Check(actualBoxes.Count > 0 && Math.Abs(cross) < .01 && actualText.Contains("First author"), $"quad follows actual rotated baseline {rotation}");
    byte[] aligned = Rewrite(source, doc => PdfCommentService.AddTextMarkup(doc, 0, actualBoxes, true, ColorConstants.RED, 1, actualText, actualQuads));
    File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, $"native-underline-{rotation}.pdf"), aligned);
    File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, $"comments-{rotation}.pdf"), marked);
}

// Actual PDF text drives the manager gesture path, save/reopen and undo/redo.
string fixture = System.IO.Path.Combine(outputDirectory, "fixture.pdf");
File.WriteAllBytes(fixture, Create());
var manager = new PdfDocumentManager();
Check(await manager.OpenAsync(fixture), "manager opens fixture");
var overlays = new List<EditorAnnotation>();
manager.GetUIStateFunc = () => overlays.Select(a => a.Clone()).ToList();
var blocks = new NativePdfTextService().Extract(manager.GetPdfBytes()!, 0);
var region = blocks.First(b => b.Text.Contains("First author")).Bounds;
Check(await manager.AddTextMarkupAsync(0, region, false, "#FFFF00", .6), "manager adds text-aligned highlight");
Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Single().PdfCommentSubtype == "Highlight", "native highlight subtype");
manager.Undo(); Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Count == 0, "undo highlight");
manager.Redo(); Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Count == 1, "redo highlight");
Check(await manager.AddTextMarkupAsync(0, region, true, "#FF0000", 1), "manager adds underline separately");
Check(!await manager.AddTextMarkupAsync(0, new(0, 0, 10, 10), false, "#FFFF00", 1), "empty region does not create a comment");
string savedFile = System.IO.Path.Combine(outputDirectory, "saved.pdf");
Check(await new PdfSaveService(new PdfAnnotationDocumentService()).SaveAsync(manager, overlays, savedFile, true), "save real comments");
overlays.AddRange(PdfCommentService.Read(manager.GetPdfBytes()!, 0));
Check(await new PdfSaveService(new PdfAnnotationDocumentService()).SaveAsync(manager, overlays, savedFile, true) &&
    PdfCommentService.Read(File.ReadAllBytes(savedFile), 0).Count == 2, "saving selected comments repeatedly does not duplicate markup");
var reopened = new PdfDocumentManager();
Check(await reopened.OpenAsync(savedFile) && PdfCommentService.Read(reopened.GetPdfBytes()!, 0).Count == 2, "comments remain selectable after reopening");
Check(await manager.RemovePdfCommentsAsync() == 2, "manager clears comments");
manager.Undo(); Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Count == 2, "undo delete all");
manager.Redo(); Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Count == 0, "redo delete all");
manager.AddHighlight(0, 200, 250, 60, 20, ColorConstants.YELLOW, .3f);
byte[] areaHighlight = manager.GetPdfBytes()!;
Check(PdfCommentService.Read(areaHighlight, 0).Count == 0, "original rectangular highlight remains page content");
Check(await manager.RemovePdfCommentsAsync() == 0 && ReferenceEquals(areaHighlight, manager.GetPdfBytes()), "delete comments preserves original rectangular highlight");
await manager.AddNoteOrTextBoxAsync(0, new(220, 200, 20, 20), true, "Persisted note", "#FFFF00", 1, 12);
var managerNote = PdfCommentService.Read(manager.GetPdfBytes()!, 0).Single();
await manager.SetNoteOpenAsync(managerNote, true);
Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Single().IsCommentOpen, "manager persists note visibility");
manager.Undo(); Check(!PdfCommentService.Read(manager.GetPdfBytes()!, 0).Single().IsCommentOpen, "undo note visibility");
manager.Redo(); Check(PdfCommentService.Read(manager.GetPdfBytes()!, 0).Single().IsCommentOpen, "redo note visibility");
byte[] duplicates = Rewrite(Create(), doc =>
{
    doc.GetPage(1).AddAnnotation(new PdfTextAnnotation(new Rectangle(100, 100, 20, 20)).SetContents("Same"));
    doc.GetPage(1).AddAnnotation(new PdfTextAnnotation(new Rectangle(100, 100, 20, 20)).SetContents("Same"));
});
var duplicateComments = PdfCommentService.Read(duplicates, 0);
Check(duplicateComments.Count == 2 && duplicateComments[0].PdfCommentKey != duplicateComments[1].PdfCommentKey, "identical unnamed notes have distinct keys");
byte[] oneDuplicate = Rewrite(duplicates, doc => PdfCommentService.Remove(doc, new[] { duplicateComments[1] }));
Check(PdfCommentService.Read(oneDuplicate, 0).Count == 1, "deleting an identical note preserves its sibling");
var pendingManager = new PdfDocumentManager();
Check(await pendingManager.OpenAsync(fixture), "open overlay-only undo fixture");
var pending = new List<EditorAnnotation> { new() { Type = AnnotationType.Signature, PageIndex = 0,
    X = 20, Y = 20, Width = 30, Height = 10, SignaturePoints = new() { new() { X = 20, Y = 20 }, new() { X = 50, Y = 30 } } } };
pendingManager.GetUIStateFunc = () => pending.Select(a => a.Clone()).ToList();
List<EditorAnnotation>? restored = null;
pendingManager.UndoRedoPerformed += (_, state) => restored = state as List<EditorAnnotation>;
Check(await pendingManager.RemovePdfCommentsAsync(hasPendingPaths: true) == 0 && pendingManager.CanUndo, "pending signatures create a delete-all undo checkpoint");
pending.Clear(); pendingManager.Undo();
Check(restored?.Single().Type == AnnotationType.Signature, "undo delete-all restores pending ink UI state");

// The supplied example has a real author highlight; remove only that markup.
if (args.Length > 0)
{
    byte[] sample = File.ReadAllBytes(args[0]);
    var comments = PdfCommentService.Read(sample, 0);
    var author = comments.Single(a => a.PdfCommentSubtype == "Highlight");
    Check(author.Width > 0 && author.Height > 0, "find supplied PDF highlight");
    byte[] deleted = Rewrite(sample, doc => Check(PdfCommentService.Remove(doc, new[] { author }) == 1, "remove supplied author highlight"));
    using var before = new PdfDocument(new PdfReader(new MemoryStream(sample)));
    using var after = new PdfDocument(new PdfReader(new MemoryStream(deleted)));
    Check(before.GetPage(1).GetContentBytes().AsSpan().SequenceEqual(after.GetPage(1).GetContentBytes()), "supplied PDF page text/graphics unchanged");
    File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, "sample-deleted.pdf"), deleted);
    var sampleManager = new PdfDocumentManager();
    Check(await sampleManager.OpenAsync(args[0]), "open supplied PDF");
    var sampleBlocks = new NativePdfTextService().Extract(sampleManager.GetPdfBytes()!, 0);
    var authorBlock = sampleBlocks.First(b => b.Text.StartsWith("Da Hyun Lee"));
    int firstAuthorEnd = authorBlock.Text.IndexOf("Yun", StringComparison.Ordinal);
    var firstAuthorBounds = PdfTextBox.Union(authorBlock.Glyphs.Where(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text) &&
        (firstAuthorEnd < 0 || g.TextIndex < firstAuthorEnd)).Select(g => g.Bounds));
    var authorBoxes = PdfCommentService.SelectText(sampleBlocks, firstAuthorBounds, out string authorText);
    Check(authorBoxes.Count == 1 && authorBoxes[0].Height < 13 && authorText.StartsWith("Da Hyun Lee, MD"), "oversized whitespace metrics do not enlarge/split the author highlight");
    Check(await sampleManager.AddTextMarkupAsync(0, firstAuthorBounds, false, "#00FF00", .5), "add text-aligned comment to supplied author");
    Check(await sampleManager.AddTextMarkupAsync(0, firstAuthorBounds, true, "#FF0000", 1), "underline supplied author");
    File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, "sample-added.pdf"), sampleManager.GetPdfBytes()!);
}
// Text underline is editable page content, never a PDF markup annotation.
var textService = new NativePdfTextService();
foreach (int rotation in new[] { 0, 90, 180, 270 })
{
    byte[] plain = Create(rotation);
    var before = textService.Extract(plain, 0);
    var target = before.First(b => b.Text.Contains("First author"));
    var underlined = textService.Edit(plain, 0, new(target, target.Text, Style: new(IsUnderline: true)));
    var selected = textService.RefreshSelection(underlined.Bytes, 0, underlined.Layout)!;
    Check(selected.Glyphs.Where(g => !g.IsVirtual).All(g => g.IsUnderline), "text underline persists in glyph metadata " + rotation);
    Check(PdfCommentService.Read(underlined.Bytes, 0).Count == 0, "text underline creates no annotations " + rotation);
    var plainGlyphs = before.SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual).ToList();
    var newGlyphs = textService.Extract(underlined.Bytes, 0).SelectMany(b => b.Glyphs).Where(g => !g.IsVirtual).ToList();
    Check(plainGlyphs.Count == newGlyphs.Count && plainGlyphs.All(g => newGlyphs.Any(n => n.Text == g.Text &&
        Math.Abs(g.Origin.X - n.Origin.X) < .01 && Math.Abs(g.Origin.Y - n.Origin.Y) < .01)), "text underline preserves text positions " + rotation);
    var removed = textService.Edit(underlined.Bytes, 0, new(selected, selected.Text, Style: new(IsUnderline: false)));
    Check(!textService.Extract(removed.Bytes, 0).SelectMany(b => b.Glyphs).Any(g => g.IsUnderline), "text underline can be removed after re-extraction " + rotation);
    using (var doc = new PdfDocument(new PdfReader(new MemoryStream(removed.Bytes))))
        Check(!System.Text.Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes()).Contains("/PDFSimpleTextUnderlines BMC"), "removed underline leaves no decoration " + rotation);
    byte[] afterCommentDeletion = Rewrite(underlined.Bytes, doc => PdfCommentService.Remove(doc));
    Check(textService.Extract(afterCommentDeletion, 0).Any(b => b.Glyphs.Any(g => g.IsUnderline)), "delete-all comments preserves text underline " + rotation);
    File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, $"text-underline-{rotation}.pdf"), underlined.Bytes);
    if (rotation == 0)
    {
        var changed = textService.Edit(underlined.Bytes, 0, new(selected, selected.Text + " extra"));
        Check(textService.Extract(changed.Bytes, 0).First(b => b.Text.Contains("extra")).Glyphs.Where(g => !g.IsVirtual).All(g => g.IsUnderline), "inserted text inherits underline");
        var colored = textService.Edit(underlined.Bytes, 0, new(selected, selected.Text, Style: new(Color: "#0000FF")));
        var coloredSelection = textService.RefreshSelection(colored.Bytes, 0, colored.Layout)!;
        Check(coloredSelection.Glyphs.All(g => g.IsVirtual || g.IsUnderline) && coloredSelection.Runs.All(r => r.Color == "#0000FF"), "color change preserves text underline");
        var moved = textService.Edit(underlined.Bytes, 0, new(selected, selected.Text, 30, 20));
        Check(moved.Layout.Glyphs.Where(g => !g.IsVirtual).All(g => g.IsUnderline), "moved text retains underline");
        var partialBox = PdfTextBox.Union(selected.Glyphs.Where(g => g.TextIndex >= 6 && g.TextIndex < 12).Select(g => g.Bounds));
        var partial = NativePdfTextService.SelectRegion(new[] { selected }, partialBox).Single();
        var partialOff = textService.Edit(underlined.Bytes, 0, new(partial, partial.Text, Style: new(IsUnderline: false)));
        var mixed = textService.Extract(partialOff.Bytes, 0).First(b => b.Text.Contains("First author"));
        Check(mixed.Glyphs.Any(g => g.IsUnderline) && mixed.Glyphs.Any(g => !g.IsVirtual && !g.IsUnderline), "partial underline removal preserves neighboring formatting");
    }
}
var newTextManager = new PdfDocumentManager();
Check(await newTextManager.OpenAsync(fixture), "open added-underlined-text fixture");
var newTexts = new List<EditorAnnotation> { new() { Type = AnnotationType.Text, PageIndex = 0, X = 60, Y = 180,
    Width = 180, Height = 25, Content = "새 텍스트 밑줄", FontFamily = "맑은 고딕", FontSize = 12, IsUnderline = true } };
string newTextFile = System.IO.Path.Combine(outputDirectory, "added-text-underline.pdf");
Check(await new PdfSaveService(new PdfAnnotationDocumentService()).SaveAsync(newTextManager, newTexts, newTextFile, true), "save new text underline");
Check(textService.Extract(File.ReadAllBytes(newTextFile), 0).First(b => b.Text.Contains("새 텍스트")).Glyphs.Where(g => !g.IsVirtual).All(g => g.IsUnderline), "new text underline survives save/reopen");

// Character selection includes the tail/head of neighboring lines, without a marquee.
var syntheticGlyphs = new List<NativePdfGlyph>();
string syntheticText = "ABCDE\nFGHIJ";
for (int i = 0; i < syntheticText.Length; i++)
{
    if (syntheticText[i] == '\n') continue;
    int column = i < 5 ? i : i - 6; double y = i < 5 ? 20 : 50;
    syntheticGlyphs.Add(new() { Text = syntheticText[i].ToString(), TextIndex = i, Bounds = new(20 + column * 10, y, 10, 10),
        Origin = new(20 + column * 10, y + 10), End = new(30 + column * 10, y + 10) });
}
var syntheticBlocks = new[] { new NativePdfTextBlock { Text = syntheticText, Glyphs = syntheticGlyphs } };
var range = PdfTextSelectionService.SelectRange(syntheticBlocks, new(42, 25), new(48, 55));
Check(string.Concat(range.SelectMany(b => b.Glyphs).Select(g => g.Text)) == "CDEFGH", "multi-line logical selection includes first line tail and second line head");
var reverseRange = PdfTextSelectionService.SelectRange(syntheticBlocks, new(48, 55), new(42, 25));
Check(string.Concat(reverseRange.SelectMany(b => b.Glyphs).Select(g => g.Text)) == "CDEFGH", "reverse text dragging selects the same range");
var rangeBoxes = PdfCommentService.SelectText(range, new(-1000, -1000, 2000, 2000), out string rangeText);
Check(rangeBoxes.Count == 2 && rangeText == "CDE\nFGH", "selected range becomes separate line quads");
Check(PdfTextSelectionService.SelectRange(syntheticBlocks, new(42, 25), new(42, 25)).Count == 0, "single caret creates no annotation");
Check(PdfCommentService.ReadAll(File.ReadAllBytes(System.IO.Path.Combine(outputDirectory, "comments-0.pdf"))).Select(a => a.PageIndex).Distinct().Count() == 2, "document comment listing includes multiple page groups");

// Existing note and text-box dictionaries keep their identity through editing.
var editing = new PdfDocumentManager();
Check(await editing.OpenAsync(fixture), "open editable comment fixture");
await editing.AddNoteOrTextBoxAsync(0, new(200, 180, 20, 20), true, "Original note", "#FFFF00", 1, 12);
var editNote = PdfCommentService.Read(editing.GetPdfBytes()!, 0).Single();
await editing.UpdatePdfCommentAsync(editNote, new(250, 220, 20, 20), "수정한 메모");
var movedNote = PdfCommentService.Read(editing.GetPdfBytes()!, 0).Single();
Check(movedNote.Content == "수정한 메모" && Math.Abs(movedNote.X - 250) < .01 && movedNote.PdfCommentKey == editNote.PdfCommentKey, "note move/edit retains annotation identity");
editing.Undo(); Check(PdfCommentService.Read(editing.GetPdfBytes()!, 0).Single().Content == "Original note", "undo note edit/move");
editing.Redo(); Check(PdfCommentService.Read(editing.GetPdfBytes()!, 0).Single().Content == "수정한 메모", "redo note edit/move");
await editing.AddNoteOrTextBoxAsync(0, new(60, 270, 150, 60), false, "Text box", "#FF0000", 1, 12);
var editBox = PdfCommentService.Read(editing.GetPdfBytes()!, 0).Single(a => a.PdfCommentSubtype == "FreeText");
await editing.UpdatePdfCommentAsync(editBox, new(80, 250, 250, 90), "수정한 텍스트 상자");
var movedBox = PdfCommentService.Read(editing.GetPdfBytes()!, 0).Single(a => a.PdfCommentSubtype == "FreeText");
Check(movedBox.Content == "수정한 텍스트 상자" && Math.Abs(movedBox.Width - 250) < .01 && movedBox.Color == "#FF0000" && movedBox.PdfCommentKey == editBox.PdfCommentKey, "text box move/resize/edit keeps red border and identity");
using (var doc = new PdfDocument(new PdfReader(new MemoryStream(editing.GetPdfBytes()!))))
{
    var freeText = doc.GetPage(1).GetAnnotations().Single(a => PdfName.FreeText.Equals(a.GetSubtype()));
    Check(freeText.GetPdfObject().Get(PdfName.C) == null, "text box has no background fill color");
    Check(doc.GetPage(1).GetAnnotations().Count(a => PdfCommentService.IsComment(a)) == 2, "editing does not duplicate notes or text boxes");
}
File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, "edited-comments.pdf"), editing.GetPdfBytes()!);
byte[] encryptedUnderline = Rewrite(File.ReadAllBytes(System.IO.Path.Combine(outputDirectory, "text-underline-0.pdf")), _ => { });
encryptedUnderline = PdfDocumentManager.RewritePdfBytes(encryptedUnderline, _ => { }, new WriterProperties().SetStandardEncryption(
    System.Text.Encoding.UTF8.GetBytes("reader-test"), System.Text.Encoding.UTF8.GetBytes("owner-test"), EncryptionConstants.ALLOW_PRINTING, EncryptionConstants.ENCRYPTION_AES_256));
using (var reader = new PdfReader(new MemoryStream(encryptedUnderline), new ReaderProperties().SetPassword(System.Text.Encoding.UTF8.GetBytes("reader-test"))))
using (var doc = new PdfDocument(reader))
    Check(reader.IsEncrypted() && doc.GetPage(1).GetPdfObject().GetAsBoolean(new PdfName("PDFSimpleHasTextUnderline"))?.GetValue() == true, "text decoration regeneration preserves requested PDF encryption");
var rangeManager = new PdfDocumentManager();
Check(await rangeManager.OpenAsync(fixture), "open character-range annotation fixture");
var rangeBlock = textService.Extract(rangeManager.GetPdfBytes()!, 0).First(b => b.Text.Contains("First author"));
var real = rangeBlock.Glyphs.Where(g => !g.IsVirtual).ToList();
Check(await rangeManager.AddTextMarkupRangeAsync(0, real[1].Origin with { Y = real[1].Bounds.Y + 2 }, real[4].End with { Y = real[4].Bounds.Y + 2 }, false, "#FFFF00", 1), "same-line character drag adds highlight without a box");

var bookmarkManager = new PdfDocumentManager();
Check(await bookmarkManager.OpenAsync(fixture), "open bookmark mutation fixture");
await bookmarkManager.AddBookmarkAsync(1, "새 책갈피 - 긴 제목 전체 표시");
var newBookmarks = PdfBookmarkService.Read(bookmarkManager.GetPdfBytes()!);
Check(newBookmarks.Count == 3 && newBookmarks[2].PageIndex == 1 && newBookmarks[2].Title.StartsWith("새 책갈피"), "add current-page Unicode bookmark");
await bookmarkManager.RemoveBookmarkAsync(newBookmarks[0].Children.Single());
Check(PdfBookmarkService.Read(bookmarkManager.GetPdfBytes()!)[0].Children.Count == 0, "delete child bookmark without deleting its parent");
bookmarkManager.Undo();
Check(PdfBookmarkService.Read(bookmarkManager.GetPdfBytes()!)[0].Children.Count == 1, "undo child bookmark deletion");
var parentBookmark = PdfBookmarkService.Read(bookmarkManager.GetPdfBytes()!)[0];
await bookmarkManager.RemoveBookmarkAsync(parentBookmark);
Check(PdfBookmarkService.Read(bookmarkManager.GetPdfBytes()!).Count == 2 && PdfBookmarkService.Read(bookmarkManager.GetPdfBytes()!)[0].Title == "Named chapter", "delete bookmark subtree preserves siblings");
using (var doc = new PdfDocument(new PdfReader(new MemoryStream(bookmarkManager.GetPdfBytes()!))))
using (var original = new PdfDocument(new PdfReader(new MemoryStream(File.ReadAllBytes(fixture)))))
    Check(doc.GetPage(1).GetContentBytes().AsSpan().SequenceEqual(original.GetPage(1).GetContentBytes()) && doc.GetPage(1).GetAnnotations().Count == 2, "bookmark mutations preserve page text, links and form fields");
string bookmarkFile = System.IO.Path.Combine(outputDirectory, "bookmarks-edited.pdf");
Check(await new PdfSaveService(new PdfAnnotationDocumentService()).SaveAsync(bookmarkManager, new(), bookmarkFile, true), "save edited bookmarks");
Check(PdfBookmarkService.Read(File.ReadAllBytes(bookmarkFile)).Count == 2, "edited bookmarks survive reopening");
using (var output = new MemoryStream())
{
    using (var doc = new PdfDocument(new PdfWriter(output))) doc.AddNewPage();
    byte[] bare = output.ToArray();
    Check(PdfBookmarkService.Read(bare).Count == 0, "read document without bookmarks");
    byte[] firstBookmark = Rewrite(bare, doc => PdfBookmarkService.Add(doc, 0, "First bookmark"));
    Check(PdfBookmarkService.Read(firstBookmark).Single().PageIndex == 0, "add the first bookmark in a document");
    byte[] noBookmarks = Rewrite(firstBookmark, doc => PdfBookmarkService.Remove(doc, PdfBookmarkService.Read(firstBookmark).Single()));
    Check(PdfBookmarkService.Read(noBookmarks).Count == 0, "remove last bookmark");
}

// Render through the same Windows PDF API as the application, without opening UI.
foreach (string file in new[] { "comments-0.pdf", "native-underline-90.pdf", "sample-added.pdf", "text-underline-0.pdf", "text-underline-90.pdf", "added-text-underline.pdf", "edited-comments.pdf" })
{
    string path = System.IO.Path.Combine(outputDirectory, file);
    if (!File.Exists(path)) continue;
    using var renderer = await PdfRenderDocument.LoadAsync(File.ReadAllBytes(path));
    using var image = await renderer.RenderAsync(0, 1.6);
    Check(image != null && image.Length > 0, "Windows PDF renderer: " + file);
    File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, file + ".windows.png"), image!.ToArray());
}
Console.WriteLine($"Passed {checks} PDF comment/bookmark checks.");
