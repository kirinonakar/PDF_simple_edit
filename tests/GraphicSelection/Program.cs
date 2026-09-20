using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Xobject;
using PDF_simple_edit.Controllers;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

string output = System.IO.Path.GetFullPath("output/pdf/shared-form-selection");
Directory.CreateDirectory(output);
string fixture = System.IO.Path.Combine(output, "fixture.pdf");
using (var document = new PdfDocument(new PdfWriter(fixture)))
{
    var form = new PdfFormXObject(new Rectangle(0, 0, 80, 80));
    new PdfCanvas(form, document).SetFillColorRgb(.1f, .5f, .8f).Circle(40, 40, 36).Fill();
    for (int i = 0; i < 2; i++)
    {
        var page = document.AddNewPage(new PageSize(400, 400));
        // Preserve the CTM across page content streams, including page-edge clipping.
        new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), document).SaveState().ConcatMatrix(1, 0, 0, 1, 10, 0);
        new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), document)
            .AddXObjectWithTransformationMatrix(form, 1, 0, 0, 1, -20, 330)
            .AddXObjectWithTransformationMatrix(form, 1, 0, 0, 1, 320, 330);
        page.NewContentStreamAfter().SetData(System.Text.Encoding.ASCII.GetBytes("Q\n"));
    }
}
await CheckDocument(fixture, "fixture");
if (args.Length > 0) await CheckDocument(System.IO.Path.GetFullPath(args[0]), "test2");
Console.WriteLine("Shared-form selection regressions passed.");

async Task CheckDocument(string file, string name)
{
    byte[] original = File.ReadAllBytes(file);
    var manager = new PdfDocumentManager();
    Check(await manager.OpenAsync(file), name + ": open");
    var contentService = new AnnotationContentService();
    var selection = new AnnotationSelectionController(contentService);
    var contents = await manager.ExtractPageContentsAsync(0);
    var forms = Forms(contents);
    Check(forms.Count == 2, name + ": both placements selectable");
    var right = forms.MaxBy(c => c.X)!;
    var left = forms.MinBy(c => c.X)!;
    Check(right.Y >= 0 && left.Y >= 0, name + ": bounds clipped to visible page");
    var annotations = new List<PdfAnnotation>();
    var region = new PdfTextBox(right.X - 1, 0, right.Width + 2, right.Y + right.Height + 1);
    var selected = selection.SelectGraphicsInRegion(annotations, contents, 0, region);
    Check(selected.Count == 1 && selected[0].GraphicOperations.Single().IsImageOperation,
        name + ": drag selects only the right placement");
    Check(selection.SelectGraphicsInRegion(new(), contents, 0, new(right.X + 10, 10, 20, 20)).Count == 0,
        name + ": partial enclosure does not erase a whole graphic");
    Check(contentService.FindEditableContent(contents, right.X + right.Width / 2, right.Y + right.Height / 2,
        EditToolMode.SelectGraphics)?.ImageId == right.ImageId, name + ": click selects right placement");
    Check(await manager.RemoveOriginalImageAnnotationsAsync(0, selected), name + ": delete selected placement");
    byte[] changed = manager.GetPdfBytes()!;
    var remaining = Forms(await manager.ExtractPageContentsAsync(0));
    Check(remaining.Count == 1 && Math.Abs(remaining[0].X - left.X) < .01, name + ": left placement remains selectable");
    using (var before = new PdfDocument(new PdfReader(new MemoryStream(original))))
    using (var after = new PdfDocument(new PdfReader(new MemoryStream(changed))))
    {
        Check(before.GetNumberOfPages() == after.GetNumberOfPages(), name + ": page count preserved");
        for (int i = 2; i <= before.GetNumberOfPages(); i++)
            Check(before.GetPage(i).GetContentBytes().SequenceEqual(after.GetPage(i).GetContentBytes()), name + $": page {i} content unchanged");
        var beforeForms = before.GetPage(1).GetResources().GetResource(PdfName.XObject);
        var afterForms = after.GetPage(1).GetResources().GetResource(PdfName.XObject);
        foreach (var resource in beforeForms.KeySet())
            Check(beforeForms.GetAsStream(resource).GetBytes().SequenceEqual(afterForms.GetAsStream(resource).GetBytes()),
                name + ": shared resource remains unchanged");
        Check(BodyText(before.GetPage(1)) == BodyText(after.GetPage(1)),
            name + ": page body text preserved");
    }
    string saved = System.IO.Path.Combine(output, name + "-right-removed.pdf");
    Check(await manager.SaveAsAsync(saved), name + ": save modified copy");
    manager.Undo();
    Check(Forms(await manager.ExtractPageContentsAsync(0)).Count == 2, name + ": undo restores both placements");
    manager.Redo();
    Check(Forms(await manager.ExtractPageContentsAsync(0)).Count == 1, name + ": redo removes only right placement");
    var remainingAnnotation = contentService.ConvertToAnnotation(Forms(await manager.ExtractPageContentsAsync(0)).Single(), 0);
    Check(await manager.RemoveOriginalImageAnnotationsAsync(0, new[] { remainingAnnotation }), name + ": delete remaining placement independently");
    Check(Forms(await manager.ExtractPageContentsAsync(1)).Count == 2, name + ": deleting remaining placement preserves other page");
    Check(await manager.OpenAsync(saved), name + ": reopen modified copy");
    Check(Forms(await manager.ExtractPageContentsAsync(0)).Count == 1, name + ": saved selection state correct");
    Check(File.ReadAllBytes(file).SequenceEqual(original), name + ": supplied source unchanged");
}

static List<PdfPageContent> Forms(IEnumerable<PdfPageContent> contents) =>
    contents.Where(c => c.ImageId?.StartsWith("form:", StringComparison.Ordinal) == true).ToList();
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

static string BodyText(PdfPage page) => PdfTextExtractor.GetTextFromPage(page,
    new iText.Kernel.Pdf.Canvas.Parser.Listener.LocationTextExtractionStrategy(),
    new Dictionary<string, IContentOperator> { ["Do"] = new IgnoreExternalObjects() });

// The selected badge includes decorative lettering. Compare page-body text,
// while separately verifying that every shared external resource is unchanged.
sealed class IgnoreExternalObjects : IContentOperator
{
    public void Invoke(PdfCanvasProcessor processor, PdfLiteral operation, IList<PdfObject> operands) { }
}
