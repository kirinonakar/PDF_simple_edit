using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using EditorAnnotation = PDF_simple_edit.Models.PdfAnnotation;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .02, $"{message}: {actual} != {expected}");
var save = new PdfSaveService(new PdfAnnotationDocumentService());
string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PDFSimpleEditor-PageGeometry-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    // Asymmetric pages, nonzero crop origin, text, drawing, links, form widgets,
    // and highlight/ink geometry catch axis swaps and accidental flattening.
    for (int rotation = 0; rotation < 360; rotation += 90)
    {
        string path = System.IO.Path.Combine(directory, $"source-{rotation}.pdf");
        using (var document = new PdfDocument(new PdfWriter(path)))
        {
            var page = document.AddNewPage(new PageSize(400, 600));
            page.SetCropBox(new Rectangle(40, 60, 200, 300)).SetRotation(rotation);
            var canvas = new PdfCanvas(page);
            canvas.SetFillColor(ColorConstants.RED).Rectangle(70, 100, 30, 50).Fill();
            canvas.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(), 16).MoveText(65, 180).ShowText("Geometry test").EndText();
            page.AddAnnotation(new PdfLinkAnnotation(new Rectangle(80, 130, 40, 20)));
            page.AddAnnotation(PdfTextMarkupAnnotation.CreateHighLight(new Rectangle(70, 170, 100, 20),
                new float[] { 70, 190, 170, 190, 70, 170, 170, 170 }));
            var inkList = new PdfArray(); inkList.Add(new PdfArray(new float[] { 100, 200, 130, 230 }));
            page.AddAnnotation(new PdfInkAnnotation(new Rectangle(100, 200, 30, 30), inkList));
            page.AddAnnotation(new PdfWidgetAnnotation(new Rectangle(130, 100, 40, 20)));
            document.AddNewPage(new PageSize(100, 150));
        }
        var manager = new PdfDocumentManager();
        Check(await manager.OpenAsync(path), "Open fixture");
        var annotations = new List<EditorAnnotation>();
        manager.GetUIStateFunc = () => annotations.Select(a => a.Clone()).ToList();
        manager.UndoRedoPerformed += (_, state) =>
        {
            annotations.Clear(); annotations.AddRange(((List<EditorAnnotation>)state!).Select(a => a.Clone()));
        };
        var oldSize = manager.GetPageSize(0); // Prime the geometry cache before editing.
        Near(oldSize.width, rotation % 180 == 0 ? 200 : 300, "Original width");
        byte[] original = manager.GetPdfBytes()!;
        annotations.Add(new EditorAnnotation { Type = AnnotationType.Signature, PageIndex = 0,
            X = 30, Y = 50, Width = 40, Height = 20, Rotation = 15, LineWidth = 2,
            SignaturePoints = new() { new() { X = 30, Y = 50 }, new() { X = 70, Y = 70 } } });
        var originalPath = annotations[0].Clone();
        await save.ChangePageGeometryAsync(manager, annotations, new(new[] { 0 }, Rotation: 90));
        Near(manager.GetPageSize(0).width, oldSize.height, "Rotated width cache");
        Near(manager.GetPageSize(0).height, oldSize.width, "Rotated height cache");
        Near(annotations[0].Rotation, 105, "Signature rotation");
        Near(annotations[0].X + annotations[0].Width / 2, oldSize.height - 60, "Rotated signature center X");
        Near(annotations[0].Y + annotations[0].Height / 2, 50, "Rotated signature center Y");
        manager.Undo();
        Check(manager.GetPdfBytes()!.SequenceEqual(original), "Exact rotation undo");
        Near(annotations[0].Rotation, originalPath.Rotation, "Signature undo");
        manager.Redo(); Near(manager.GetPageSize(0).width, oldSize.height, "Rotation redo");
        manager.Undo();

        await save.ChangePageGeometryAsync(manager, annotations,
            new(new[] { 0 }, Width: 500, Height: 700, FitContent: true));
        Near(manager.GetPageSize(0).width, 500, "Resized width cache");
        Near(manager.GetPageSize(0).height, 700, "Resized height cache");
        double scale = Math.Min(500 / oldSize.width, 700 / oldSize.height);
        Near(annotations[0].Width, 40 * scale, "Path scales proportionally");
        Near(annotations[0].LineWidth, 2 * scale, "Path stroke scales");
        Near(annotations[0].X + annotations[0].Width / 2, 50 * scale + (500 - oldSize.width * scale) / 2, "Scaled path center");
        using (var document = new PdfDocument(new PdfReader(new MemoryStream(manager.GetPdfBytes()!))))
        {
            var page = document.GetPage(1);
            Check(PdfTextExtractor.GetTextFromPage(page).Contains("Geometry test"), "Text remains selectable");
            Check(page.GetAnnotations().Count == 4, "Native annotations retained");
            Near(page.GetRotation(), rotation, "Existing rotation retained");
            Near(document.GetPage(2).GetPageSize().GetWidth(), 100, "Unselected page untouched");
            var link = page.GetAnnotations().Single(a => PdfName.Link.Equals(a.GetSubtype()));
            Near(link.GetRectangle().ToRectangle().GetWidth(), 40 * scale, "Link hitbox scales");
            var highlight = page.GetAnnotations().Single(a => PdfName.Highlight.Equals(a.GetSubtype()));
            var quad = highlight.GetPdfObject().GetAsArray(PdfName.QuadPoints);
            Near(quad.GetAsNumber(2).DoubleValue() - quad.GetAsNumber(0).DoubleValue(), 100 * scale, "Markup quads scale");
            var ink = page.GetAnnotations().Single(a => PdfName.Ink.Equals(a.GetSubtype())).GetPdfObject().GetAsArray(PdfName.InkList).GetAsArray(0);
            Near(ink.GetAsNumber(2).DoubleValue() - ink.GetAsNumber(0).DoubleValue(), 30 * scale, "Ink points scale");
        }
        byte[] resized = manager.GetPdfBytes()!;
        manager.Undo(); Check(manager.GetPdfBytes()!.SequenceEqual(original), "Exact resize undo");
        Near(manager.GetPageSize(0).width, oldSize.width, "Resize undo cache");
        manager.Redo(); Check(manager.GetPdfBytes()!.SequenceEqual(resized), "Exact resize redo");
        manager.Undo();
        await save.ChangePageGeometryAsync(manager, annotations, new(new[] { 0, 1 }, Width: 400, Height: 400, FitContent: false));
        Near(annotations[0].Width, 40, "Canvas-only keeps content scale");
        Near(annotations[0].LineWidth, 2, "Canvas-only keeps stroke");
        Near(manager.GetPageSize(1).width, 400, "Batch targets second page");
        manager.Undo();
        foreach (var invalid in new[] { new PdfPageGeometryOptions(new[] { 0 }, Width: double.NaN, Height: 100),
            new(new[] { 0 }, Width: 100, Height: -1), new(new[] { 0 }, Rotation: 45), new(new[] { 5 }, Rotation: 90) })
        {
            byte[] before = manager.GetPdfBytes()!;
            try { await save.ChangePageGeometryAsync(manager, annotations, invalid); Check(false, "Invalid request accepted"); }
            catch (ArgumentException) { Check(ReferenceEquals(before, manager.GetPdfBytes()), "Invalid request is atomic"); }
        }
        // Native rendering is exercised without opening the app or automating its UI.
        await save.ChangePageGeometryAsync(manager, annotations, new(new[] { 0 }, Width: 500, Height: 700));
        using var rendered = await PdfRenderDocument.LoadAsync(manager.GetPdfBytes()!);
        using var png = await rendered.RenderAsync(0, 1);
        Check(png is { Length: > 0 }, "Resized PDF renders");
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory, $"resized-{rotation}.png"), png!.ToArray());
        // A saved, reopened document keeps both the requested size and editable paths.
        string saved = System.IO.Path.Combine(directory, $"saved-{rotation}.pdf");
        Check(await save.SaveAsync(manager, annotations, saved, true), "Save succeeds");
        var reopened = new PdfDocumentManager();
        Check(await reopened.OpenAsync(saved), "Reopen saved file");
        Near(reopened.GetPageSize(0).width, 500, "Saved width");
        var paths = reopened.LoadSavedSignatures();
        Check(paths.Count == 1, "Saved path restores once");
        Near(paths[0].Width, annotations[0].Width, "Saved path geometry");
    }
    var pendingManager = new PdfDocumentManager();
    Check(await pendingManager.OpenAsync(System.IO.Path.Combine(directory, "source-0.pdf")), "Open pending-overlay fixture");
    var pending = new List<EditorAnnotation>
    {
        new() { Type = AnnotationType.Highlight, PageIndex = 0, X = 10, Y = 20, Width = 50, Height = 10, Color = "#FFFF00" },
        new() { Type = AnnotationType.Highlight, PageIndex = 1, X = 5, Y = 5, Width = 20, Height = 10, Color = "#FFFF00" }
    };
    pendingManager.GetUIStateFunc = () => pending.Select(a => a.Clone()).ToList();
    pendingManager.UndoRedoPerformed += (_, state) => { pending.Clear(); pending.AddRange((List<EditorAnnotation>)state!); };
    await save.ChangePageGeometryAsync(pendingManager, pending, new(new[] { 0 }, Rotation: -90));
    Near(pendingManager.GetPageSize(0).width, 300, "Counterclockwise rotation");
    Check(pending.Count == 1 && pending[0].PageIndex == 1, "Only target overlays are baked");
    byte[] baked = pendingManager.GetPdfBytes()!;
    var saveSnapshot = await save.CreateAnnotatedBytesAsync(pendingManager, pending);
    Check(saveSnapshot.Length > baked.Length, "Untargeted overlay remains pending for save");
    pendingManager.Undo(); Check(pending.Count == 2, "Undo restores pending overlays");
    await save.ChangePageGeometryAsync(pendingManager, pending, new(new[] { 0 }, Rotation: 180));
    Near(pendingManager.GetPageSize(0).width, 200, "180 degree width");
    using (var document = new PdfDocument(new PdfReader(new MemoryStream(pendingManager.GetPdfBytes()!))))
        Near(document.GetPage(1).GetRotation(), 180, "180 degree rotation stored");

    string unitFile = System.IO.Path.Combine(directory, "user-unit.pdf");
    using (var document = new PdfDocument(new PdfReader(System.IO.Path.Combine(directory, "source-0.pdf")), new PdfWriter(unitFile)))
        document.GetPage(1).GetPdfObject().Put(PdfName.UserUnit, new PdfNumber(2));
    var unitManager = new PdfDocumentManager();
    Check(await unitManager.OpenAsync(unitFile), "Open UserUnit fixture");
    Near(unitManager.GetPhysicalPageSize(0).width, 400, "Physical size includes UserUnit");
    await save.ChangePageGeometryAsync(unitManager, new(), new(new[] { 0 }, Width: 800, Height: 1000, FitContent: false));
    Near(unitManager.GetPhysicalPageSize(0).width, 800, "Requested physical size with UserUnit");
    using (var document = new PdfDocument(new PdfReader(new MemoryStream(unitManager.GetPdfBytes()!))))
    {
        Check(document.GetPage(1).GetPdfObject().GetAsNumber(PdfName.UserUnit) == null, "UserUnit normalized");
        var link = document.GetPage(1).GetAnnotations().Single(a => PdfName.Link.Equals(a.GetSubtype()));
        Near(link.GetRectangle().ToRectangle().GetWidth(), 80, "Canvas-only retains physical content size");
    }
    Console.WriteLine($"PASS: {checks} checks. Rendered fixtures: {directory}");
}
catch
{
    Console.Error.WriteLine($"Fixtures retained: {directory}");
    throw;
}
