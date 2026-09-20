using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using PDF_simple_edit.Controllers;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

string output = System.IO.Path.GetFullPath("output/graphic-selection");
Directory.CreateDirectory(output);
string source = System.IO.Path.Combine(output, "before.pdf");
using (var doc = new PdfDocument(new PdfWriter(source)))
{
    var page = doc.AddNewPage(new PageSize(400, 400));
    var canvas = new PdfCanvas(page);
    canvas.SetLineWidth(0.5);
    for (int x = 40; x <= 220; x += 60) canvas.MoveTo(x, 180).LineTo(x, 300).Stroke();
    for (int y = 180; y <= 300; y += 40) canvas.MoveTo(40, y).LineTo(220, y).Stroke();
    canvas.MoveTo(40, 340).LineTo(220, 340).Stroke();
    canvas.SetFillColorRgb(0.2f, 0.5f, 0.8f).Rectangle(270, 230, 60, 50).Fill();
    canvas.SetFillColorRgb(0, 0, 0).BeginText()
        .SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
        .MoveText(50, 275).ShowText("Keep text").EndText();
    canvas.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
        .MoveText(275, 250).ShowText("Label").EndText();
}

var manager = new PdfDocumentManager();
Check(await manager.OpenAsync(source), "open fixture");
var service = new AnnotationContentService();
var selection = new AnnotationSelectionController(service);
var contents = await manager.ExtractPageContentsAsync(0);
var graphics = contents.Where(c => c.Type == PageContentType.Image).ToList();
Check(graphics.Count == 3, $"table, standalone line and filled shape extracted ({graphics.Count})");
var table = graphics.Single(c => c.GraphicOperations.Count == 8);
var line = graphics.Single(c => c.Height < 5);
var shape = graphics.Single(c => c.X > 250);
Check(service.FindEditableContent(contents, 40, 120) == null, "text mode ignores table borders");
Check(ReferenceEquals(service.FindEditableContent(contents, 40, 120, EditToolMode.SelectGraphics), table), "graphics mode selects table border");
Check(service.FindEditableContent(contents, 70, 130, EditToolMode.SelectGraphics) == null, "empty cell does not select the table");
Check(ReferenceEquals(service.FindEditableContent(contents, 120, 60, EditToolMode.SelectGraphics), line), "thin rule is selectable");
Check(service.FindEditableContent(contents, 285, 146)?.Type == PageContentType.Text, "text mode selects text over a shape");
Check(ReferenceEquals(service.FindEditableContent(contents, 285, 146, EditToolMode.SelectGraphics), shape), "graphics mode selects shape through text");

var annotations = new List<PdfAnnotation>();
var selected = new List<PdfAnnotation>();
var first = await selection.SelectAtAsync(manager, annotations, selected, null, 0, 40, 120, false, () => {}, EditToolMode.SelectGraphics);
var second = await selection.SelectAtAsync(manager, annotations, selected, first.PrimarySelection, 0, 120, 60, true, () => {}, EditToolMode.SelectGraphics);
Check(selected.Count == 2, "Ctrl click selects multiple graphics");
await selection.SelectAtAsync(manager, annotations, selected, second.PrimarySelection, 0, 120, 60, true, () => {}, EditToolMode.SelectGraphics);
Check(selected.Count == 1, "Ctrl click toggles an existing graphic");
Check(service.FindAnnotationAt(annotations, 0, 40, 120) == null, "text mode ignores cached graphics");
Check(service.FindAnnotationAt(annotations, 0, 70, 130, EditToolMode.SelectGraphics) == null, "cached table leaves empty cells alone");
var region = selection.SelectGraphicsInRegion(annotations, contents, 0, new(30, 90, 200, 140));
Check(region.Count == 1 && region[0].IsOriginalVectorGraphic, "region selects the complete table without its text");
Check(selection.SelectGraphicsInRegion(annotations, contents, 0, new(30, 90, 50, 50)).Count == 0, "partial region cannot erase a whole table");
Check(annotations.Count(a => a.OriginalImageName == table.ImageId) == 1, "region reuses the existing table selection");

Check(await manager.RemoveOriginalImageAnnotationsAsync(0, region), "delete table");
Check((await manager.ExtractPageContentsAsync(0)).Count(c => c.Type == PageContentType.Image) == 2, "only table removed");
Check(ReadText(manager.GetPdfBytes()!).Contains("Keep text"), "table text survives graphics deletion");
File.WriteAllBytes(System.IO.Path.Combine(output, "table-deleted.pdf"), manager.GetPdfBytes()!);
manager.Undo();
Check((await manager.ExtractPageContentsAsync(0)).Count(c => c.Type == PageContentType.Image) == 3, "undo restores table");
manager.Redo();
Check((await manager.ExtractPageContentsAsync(0)).Count(c => c.Type == PageContentType.Image) == 2, "redo removes table again");
var remaining = (await manager.ExtractPageContentsAsync(0)).Where(c => c.Type == PageContentType.Image).Select(c => service.ConvertToAnnotation(c, 0)).ToList();
Check(await manager.RemoveOriginalImageAnnotationsAsync(0, remaining), "delete multiple remaining graphics");
Check((await manager.ExtractPageContentsAsync(0)).All(c => c.Type == PageContentType.Text), "all graphics removed");
Check(ReadText(manager.GetPdfBytes()!).Contains("Label"), "overlapping label preserved");
string saved = System.IO.Path.Combine(output, "all-graphics-deleted.pdf");
Check(await manager.SaveAsAsync(saved), "save deletion");
var reopened = new PdfDocumentManager();
Check(await reopened.OpenAsync(saved), "reopen saved PDF");
Check((await reopened.ExtractPageContentsAsync(0)).All(c => c.Type == PageContentType.Text), "deletion persists after reopening");
manager.Undo();
Check((await manager.ExtractPageContentsAsync(0)).Count(c => c.Type == PageContentType.Image) == 2, "one undo restores batch");
Console.WriteLine("All graphic selection regressions passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
static string ReadText(byte[] bytes)
{
    using var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
    return PdfTextExtractor.GetTextFromPage(doc.GetPage(1));
}
