using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using iText.Forms;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using PDF_simple_edit.Helpers;

namespace PDF_simple_edit.Mcp;

internal static class PdfMcpTools
{
    private sealed record Tool(string name, string description, object inputSchema, object annotations);
    private static object Field(string type, string description) => new { type, description };
    private static readonly object Text = Field("string", "UTF-8 text; newlines are supported.");
    private static readonly object Number = Field("number", "PDF points; origin is the displayed CropBox's top-left.");
    private static readonly object Page = new { type = "integer", minimum = 1, description = "1-based page number (default 1)." };
    private static readonly object PageArray = new { type = "array", minItems = 1, items = new { type = "integer", minimum = 1 } };
    private static readonly object Paths = new { type = "array", minItems = 1, items = new { type = "string" } };
    private static readonly Dictionary<string, object> Input = new()
    {
        ["inputPath"] = Field("string", "Absolute path to an existing local PDF."),
        ["expectedRevision"] = Field("string", "Optional SHA-256 revision from pdf_inspect; reject if the source changed.")
    };
    private static readonly Dictionary<string, object> Output = new()
    {
        ["outputPath"] = Field("string", "Absolute destination PDF path; its parent directory must exist."),
        ["overwrite"] = new { type = "boolean", description = "Default false. Set true explicitly to replace an existing output, including in-place edits." }
    };
    private static readonly Dictionary<string, object> Geometry = new()
    { ["x"] = Number, ["y"] = Number, ["width"] = Number, ["height"] = Number };
    private static readonly Dictionary<string, object> Style = new()
    {
        ["color"] = Field("string", "#RRGGBB stroke/text color; default #000000."),
        ["fontSize"] = Field("number", "Font size in points (default 12)."),
        ["fontFamily"] = Field("string", "Installed font family or font file path; default Malgun Gothic."),
        ["bold"] = Field("boolean", "Bold text."), ["italic"] = Field("boolean", "Italic text.")
    };
    private static readonly Dictionary<string, object> Graphic = new()
    {
        ["kind"] = new { type = "string", @enum = new[] { "rectangle", "ellipse", "line", "polygon", "freehand", "image", "highlight" } },
        ["imagePath"] = Field("string", "Absolute path to image; required for kind=image or to replace an existing graphic with an image."),
        ["color"] = Style["color"], ["fillColor"] = Field("string", "Optional #RRGGBB fill for closed shapes."),
        ["lineWidth"] = Field("number", "Stroke width in points (default 2)."),
        ["opacity"] = Field("number", "Opacity from 0 to 1 (default 1; highlights 0.3)."),
        ["rotation"] = Field("number", "Clockwise rotation in degrees around the object's center (default 0)."),
        ["points"] = new { type = "array", minItems = 2, items = new { type = "object", properties = new { x = Number, y = Number }, required = new[] { "x", "y" }, additionalProperties = false }, description = "Absolute top-left page coordinates for line, polygon, or freehand. Line defaults to the bounds diagonal." }
    };
    private static readonly Dictionary<string, Tool> Catalog = BuildCatalog();
    public static IEnumerable<object> Definitions => Catalog.Values;
    public static bool Exists(string name) => Catalog.ContainsKey(name);

    private static Dictionary<string, Tool> BuildCatalog()
    {
        var result = new Dictionary<string, Tool>();
        void Add(string name, string description, string[] required, bool readOnly, params Dictionary<string, object>[] groups)
        {
            var properties = groups.SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value);
            var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
            if (name == "pdf_create") schema["$defs"] = PdfMcpCreationService.BlockDefinitions();
            result.Add(name, new(name, description, schema,
                new { readOnlyHint = readOnly, destructiveHint = !readOnly, openWorldHint = false }));
        }
        Dictionary<string, object> Fields(params (string key, object field)[] values) => values.ToDictionary(p => p.key, p => p.field);
        var objectId = Fields(("objectId", Field("string", "Exact object ID returned by pdf_inspect for this file revision and page.")));
        Add("pdf_create", "Create a complete PDF in one call. First call pdf_create_help for JSON authoring rules and working examples. Choose text (automatic wrapping/pagination), content (generic heading/paragraph/table/image/section/columns/rule/spacer/pageBreak blocks), pages (positioned text/images/shapes), or jsonPath (UTF-8 creation JSON or pdf_to_json lossless archive). Modes cannot be combined. Layout, colors and wording come from the supplied JSON, with no document-specific presets. title is metadata only. Paths must be absolute; units are PDF points; coordinates start at the displayed page's top-left. Atomic output; existing destinations require overwrite=true.", new[] { "outputPath" }, false,
            Output, Style, PdfMcpCreationService.Schema(Graphic));
        Add("pdf_create_help", "Read this before authoring creation JSON. Returns a mode-selection guide, exact field rules, executable generic examples and lossless roundtrip instructions. topic=schema returns the actual current pdf_create input schema including nested block definitions. Other topics: overview (default), flow, positioned, roundtrip. This tool does not read or write files.", Array.Empty<string>(), true,
            Fields(("topic", new { type = "string", @enum = new[] { "overview", "flow", "positioned", "roundtrip", "schema" } })));
        Add("pdf_to_json", "Export a PDF to a lossless JSON archive containing its original PDF bytes, SHA-256 and readable page/object information. Pass the resulting JSON file as pdf_create jsonPath to restore a byte-for-byte identical PDF, preserving fonts, vectors, images, annotations and metadata. sourceInfo is a read-only inspection snapshot; use text/content/pages creation modes to author new content.", new[] { "inputPath", "outputPath" }, false,
            Input, new Dictionary<string, object>(Output) { ["outputPath"] = Field("string", "Absolute destination JSON archive path. Parent directory must exist.") });
        Add("pdf_inspect", "Read document revision, page sizes, text, graphics, annotations and form fields. Returns editable object IDs for one page (default 1). Scanned text requires OCR outside this tool.", new[] { "inputPath" }, true,
            Input, Fields(("page", Page)));
        Add("pdf_read", "Read PDF body text page by page, including Unicode text. Returns total page count and separate text/size/rotation for each selected page. Use page for one 1-based page (default 1), or pages for a range such as 1,3,5-8 or all. Do not supply both. Extracts existing PDF text only; image-only/scanned pages require external OCR. Complex layouts may have imperfect reading order. Use pdf_inspect when object IDs are needed for editing.", new[] { "inputPath" }, true,
            Input, Fields(("page", Page), ("pages", Field("string", "1-based page range, e.g. 1,3,5-8, or all. Mutually exclusive with page."))));
        Add("pdf_add_text", "Add Unicode text, including Korean, at x/y. Font, size, color, bold and italic are configurable. Use pdf_edit_text for existing text.", new[] { "inputPath", "outputPath", "text" }, false,
            Input, Output, Style, Fields(("page", Page), ("text", Text), ("x", Number), ("y", Number)));
        Add("pdf_edit_text", "Replace/delete, move, resize, rotate, reflow or restyle an existing text block without covering it with a white rectangle. Supply only the properties to change; text is optional. Vertical or clipping text can be unsupported and returns an error.", new[] { "inputPath", "outputPath", "objectId" }, false,
            Input, Output, Geometry, Style, objectId, Fields(("page", Page), ("text", Text), ("rotation", Number), ("reflow", Field("boolean", "Reflow text into supplied width/height instead of scaling."))));
        Add("pdf_add_graphic", "Add a rectangle, ellipse, line, polygon, freehand path, image or highlight. All content is saved into real PDF content streams and can be inspected/edited again.", new[] { "inputPath", "outputPath", "kind" }, false,
            Input, Output, Geometry, Graphic, Fields(("page", Page)));
        Add("pdf_edit_graphic", "Move, resize, rotate or recolor an original vector/image graphic; optional imagePath replaces it with an image, or kind replaces it with a new shape. Shared clipping/shading transformations may return an unsupported error. Annotation objects support moving/resizing; to change their artwork use kind or imagePath to replace them.", new[] { "inputPath", "outputPath", "objectId" }, false,
            Input, Output, Geometry, Graphic, objectId, Fields(("page", Page)));
        Add("pdf_delete_object", "Permanently delete an inspected text, graphic, image or annotation. Surrounding content is retained. Use pdf_redact_region to erase all content intersecting an area.", new[] { "inputPath", "outputPath", "objectId" }, false,
            Input, Output, objectId, Fields(("page", Page)));
        Add("pdf_redact_region", "Permanently remove text, graphics, images and form widgets intersecting a rectangle using pdfSweep. This removes content rather than covering it.", new[] { "inputPath", "outputPath", "x", "y", "width", "height" }, false,
            Input, Output, Geometry, Fields(("page", Page)));
        Add("pdf_manage_pages", "Insert blank pages, delete pages, reorder all pages, rotate pages, or set an unrotated PDF-space crop rectangle. Delete cannot remove every page. Reorder requires a complete permutation of all 1-based pages.", new[] { "inputPath", "outputPath", "action" }, false,
            Input, Output, Fields(("action", new { type = "string", @enum = new[] { "insert", "delete", "reorder", "rotate", "crop" } }),
                ("pages", PageArray), ("page", Page), ("count", Page), ("width", Number), ("height", Number), ("rotation", Number), ("x", Number), ("y", Number)));
        Add("pdf_set_form_fields", "Set named AcroForm field values. Checkboxes use their onValue from pdf_inspect or Off. Supports Unicode text and keeps fields interactive.", new[] { "inputPath", "outputPath", "values" }, false,
            Input, Output, Fields(("values", new { type = "object", additionalProperties = new { type = "string" } })));
        Add("pdf_merge", "Merge PDFs in inputPaths order into one PDF.", new[] { "inputPaths", "outputPath" }, false,
            Output, Fields(("inputPaths", Paths)));
        Add("pdf_extract_pages", "Extract selected pages to a new PDF in the given order (duplicates allowed). Can also split a PDF by calling with different selections.", new[] { "inputPath", "outputPath", "pages" }, false,
            Input, Output, Fields(("pages", PageArray)));
        Add("pdf_to_images", "Render all or selected PDF pages to PNG at 72-600 DPI (default 150). pages is a range string such as 1,3,5-8. Returns exact output paths. Existing image files are never overwritten.", new[] { "inputPath", "outputFolder" }, false,
            Input, Fields(("outputFolder", Text), ("pages", Field("string", "Optional 1-based page range; omit to render all.")), ("dpi", new { type = "integer", minimum = 72, maximum = 600 })));
        Add("pdf_extract_images", "Extract original embedded raster images, including images inside forms, to files. This is different from rendering whole pages. Reports partial failures and exact saved paths.", new[] { "inputPath", "outputFolder" }, false,
            Input, Fields(("outputFolder", Text)));
        Add("images_to_pdf", "Create a PDF from PNG, JPEG, BMP, GIF, TIFF or WebP files in the specified order. Each image is fitted without cropping onto one portrait/landscape A4 page.", new[] { "imagePaths", "outputPath" }, false,
            Output, Fields(("imagePaths", Paths)));
        return result;
    }

    public static async Task<object> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellation)
    {
        if (name == "pdf_create") arguments = await PdfMcpJsonArchive.ExpandRequestAsync(arguments, cancellation);
        Validate(arguments, JsonSerializer.SerializeToElement(Catalog[name].inputSchema), "arguments");
        cancellation.ThrowIfCancellationRequested();
        var a = new PdfMcpArguments(arguments);
        if (name == "pdf_create_help") return PdfMcpCreationGuide.Get(a.Text("topic", "overview"), Catalog["pdf_create"].inputSchema);
        PdfFileVersion? destinationVersion = null;
        if (a.Has("outputPath"))
        {
            string destination = a.Path("outputPath");
            string extension = name == "pdf_to_json" ? ".json" : ".pdf";
            if (!System.IO.Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"outputPath must end in {extension}.");
            destinationVersion = PdfFileVersion.Read(destination);
            if (destinationVersion.Hash != null && !a.Flag("overwrite")) throw new IOException("Output already exists. Choose a new path or set overwrite=true explicitly.");
        }
        if (name == "pdf_create")
        {
            byte[] created = await Task.Run(() => PdfMcpCreationService.Create(a, cancellation), cancellation);
            return await Save(a, created, cancellation, expected: destinationVersion);
        }
        if (name is "images_to_pdf" or "pdf_merge")
        {
            string key = name == "images_to_pdf" ? "imagePaths" : "inputPaths";
            string[] paths = a.Value.GetProperty(key).EnumerateArray().Select(v => Absolute(v.GetString()!)).ToArray();
            if (name == "pdf_merge")
            {
                using var buffer = new MemoryStream();
                using (var pdf = new PdfDocument(new PdfWriter(buffer)))
                    foreach (string path in paths)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        using var source = new PdfDocument(new PdfReader(path)); source.CopyPagesTo(1, source.GetNumberOfPages(), pdf);
                    }
                return await Save(a, buffer.ToArray(), cancellation, expected: destinationVersion);
            }
            // The conversion service writes a complete temporary PDF first. Commit
            // only after success and the same conflict/overwrite checks as edits.
            string temporary = System.IO.Path.Combine(PdfTemporaryFiles.GetDirectory("mcp"), $"{Guid.NewGuid():N}.pdf");
            try
            {
                await new PdfImageConversionService().CreateAsync(paths, temporary);
                return await Save(a, await File.ReadAllBytesAsync(temporary, cancellation), cancellation, expected: destinationVersion);
            }
            finally { PdfTemporaryFiles.TryDelete(temporary); }
        }

        string inputPath = a.Path("inputPath");
        byte[] bytes = await File.ReadAllBytesAsync(inputPath, cancellation);
        string revision = Hash(bytes);
        if (a.Has("expectedRevision") && !a.Text("expectedRevision").Equals(revision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source file changed. Inspect it again before editing.");
        if (name == "pdf_inspect") return await Inspect(a, bytes, revision);
        if (name == "pdf_to_json")
        {
            var archive = await PdfMcpJsonArchive.ExportAsync(bytes, revision, a, Inspect, cancellation);
            cancellation.ThrowIfCancellationRequested();
            await PdfAtomicFile.WriteAsync(a.Path("outputPath"), archive, destinationVersion);
            return new { outputPath = a.Path("outputPath"), pdfRevision = revision, format = PdfMcpJsonArchive.Format };
        }
        if (name == "pdf_read") return await Task.Run(() => PdfMcpReader.Read(a, bytes, revision, cancellation), cancellation);
        if (name is "pdf_to_images" or "pdf_extract_images")
        {
            string parent = a.Path("outputFolder");
            Directory.CreateDirectory(parent);
            // Reserve a unique subfolder per call, preventing any accidental
            // overwrite and making partial output easy to identify on failure.
            string folder = System.IO.Path.Combine(parent, $"pdf-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                if (name == "pdf_to_images")
                {
                    using var pdf = Open(bytes);
                    var pages = a.Has("pages") ? PdfPageImageExportService.ParsePageIndices(a.Text("pages"), pdf.GetNumberOfPages())
                        : Enumerable.Range(0, pdf.GetNumberOfPages()).ToArray();
                    cancellation.ThrowIfCancellationRequested();
                    await new PdfPageImageExportService().ExportPagesAsync(bytes, pages, folder, "page", a.Integer("dpi", 150, 72, 600));
                    return new { outputFolder = folder, files = Directory.GetFiles(folder).Order().ToArray(), pageCount = pages.Count };
                }
                var result = await new PdfImageExtractionService().ExtractAllAsync(bytes, folder, "image");
                return new { outputFolder = folder, files = Directory.GetFiles(folder).Order().ToArray(), result.SavedCount, result.FailedCount };
            }
            catch (Exception error) { throw new IOException($"Export failed; any completed images are in {folder}. {error.Message}", error); }
        }

        int pageIndex;
        using (var pdf = Open(bytes)) pageIndex = a.Integer("page", 1, 1,
            pdf.GetNumberOfPages() + (name == "pdf_manage_pages" && a.Text("action") == "insert" ? 1 : 0)) - 1;
        byte[] edited;
        if (name is "pdf_edit_text" or "pdf_edit_graphic" or "pdf_delete_object")
        {
            string[] id = a.Text("objectId").Split(':');
            if (id.Length != 4 || id[0] != revision || !int.TryParse(id[1], out int objectPage) || objectPage != pageIndex + 1 ||
                !int.TryParse(id[3], out int index) || index < 0)
                throw new ArgumentException("Object ID is stale or belongs to another page. Run pdf_inspect again.");
            if (id[2] == "annotation")
            {
                if (name == "pdf_edit_text") throw new ArgumentException("Use pdf_edit_graphic for annotation objects.");
                edited = Rewrite(bytes, pdf =>
                {
                    var page = pdf.GetPage(pageIndex + 1);
                    var annotations = page.GetAnnotations();
                    if (index >= annotations.Count) throw new ArgumentException("Annotation no longer exists.");
                    var annotation = annotations[index];
                    var rectangle = annotation.GetRectangle().ToRectangle();
                    var original = PdfPageCoordinates.ToDisplay(page).Map(new PdfTextBox(rectangle.GetX(), rectangle.GetY(), rectangle.GetWidth(), rectangle.GetHeight()));
                    if (name == "pdf_delete_object") RemoveAnnotation(pdf, page, annotation);
                    else if (a.Has("kind") || a.Has("imagePath"))
                    { RemoveAnnotation(pdf, page, annotation); AddGraphic(pdf, pageIndex, a, original); }
                    else
                    {
                        if (a.Has("rotation") || a.Has("color") || a.Has("fillColor") || a.Has("lineWidth") || a.Has("opacity") || a.Has("points"))
                            throw new ArgumentException("Annotation artwork changes require kind or imagePath to replace the artwork.");
                        var bounds = PdfPageCoordinates.ToDisplay(page).Inverse().Map(a.Bounds(original));
                        annotation.SetRectangle(new PdfArray(new[] { (float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom }));
                    }
                });
            }
            else if (id[2] == "content")
            {
                var contents = await new PdfPageContentExtractor().ExtractAsync(bytes, pageIndex, strict: true, includePreviews: false);
                if (index >= contents.Count) throw new ArgumentException("Object no longer exists.");
                var content = contents[index];
                if (content.NativeText is { } block)
                {
                    if (name == "pdf_edit_graphic") throw new ArgumentException("Use pdf_edit_text for text objects.");
                    NativePdfTextStyle? style = Style.Keys.Any(a.Has) ? new(
                        a.Has("color") ? a.Color() : null, a.Has("fontSize") ? a.Number("fontSize", 12, .1, 10000) : null,
                        a.Has("bold") ? a.Flag("bold") : null, a.Has("italic") ? a.Flag("italic") : null,
                        a.Has("fontFamily") ? a.Text("fontFamily") : null) : null;
                    var target = a.Bounds(block.Bounds);
                    bool transform = a.Has("width") || a.Has("height") || a.Has("rotation");
                    bool reflow = a.Flag("reflow");
                    if (reflow && a.Number("rotation") != 0) throw new ArgumentException("Reflow and rotation require separate edits; reflow first, inspect again, then rotate.");
                    var edit = new NativePdfTextEdit(block, name == "pdf_delete_object" ? "" : a.Text("text", block.Text),
                        transform || reflow ? 0 : target.X - block.Bounds.X, transform || reflow ? 0 : target.Y - block.Bounds.Y,
                        style, transform && !reflow ? PdfAffineTransform.Between(block.Bounds, target, a.Number("rotation")) : null,
                        reflow ? target : null);
                    edited = new NativePdfTextService().Edit(bytes, pageIndex, edit).Bytes;
                }
                else
                {
                    if (name == "pdf_edit_text") throw new ArgumentException("Use pdf_edit_graphic for graphic objects.");
                    var annotation = new AnnotationContentService().ConvertToAnnotation(content, pageIndex);
                    var original = new PdfTextBox(content.X, content.Y, content.Width, content.Height);
                    if (name == "pdf_delete_object" || a.Has("kind") || a.Has("imagePath"))
                        edited = Rewrite(bytes, pdf =>
                        {
                            if (!new PdfContentStreamEditor().RemoveGraphics(pdf, pageIndex, new[] { annotation }))
                                throw new InvalidOperationException("The original graphic could not be removed safely.");
                            if (name != "pdf_delete_object") AddGraphic(pdf, pageIndex, a, original);
                        });
                    else
                    {
                        if (a.Has("points")) throw new ArgumentException("Changing path points requires kind to replace the graphic.");
                        var transform = PdfAffineTransform.Between(original, a.Bounds(original), a.Number("rotation"));
                        var paint = new PdfGraphicStyle(a.Has("color") ? a.Color() : null, a.Has("fillColor") ? a.Color("fillColor") : null,
                            a.Has("lineWidth") ? a.Number("lineWidth", 2, .1, 100) : null,
                            a.Has("opacity") ? a.Number("opacity", 1, 0, 1) : null);
                        edited = PdfGraphicTransformService.Transform(bytes, pageIndex, new[] { (annotation, transform) }, paint);
                    }
                }
            }
            else throw new ArgumentException("Invalid object ID.");
        }
        else edited = Rewrite(bytes, pdf =>
        {
            switch (name)
            {
                case "pdf_add_text":
                    new PdfContentWriter().AddText(pdf, pageIndex, a.Number("x"), a.Number("y"), a.Text("text"),
                        a.Text("fontFamily", "Malgun Gothic"), a.Number("fontSize", 12, .1, 10000), PdfMcpArguments.PdfColor(a.Color()), a.Flag("bold"), a.Flag("italic"));
                    break;
                case "pdf_add_graphic": AddGraphic(pdf, pageIndex, a); break;
                case "pdf_redact_region": PdfRegionRedactionService.Apply(pdf, pageIndex, a.Bounds()); break;
                case "pdf_manage_pages": ManagePages(pdf, a); break;
                case "pdf_set_form_fields": SetFormFields(pdf, a); break;
                case "pdf_extract_pages": throw new InvalidOperationException("Handled separately below.");
                default: throw new ArgumentException("Unknown tool.");
            }
        }, name == "pdf_extract_pages" ? a : null);
        return await Save(a, edited, cancellation, inputPath, revision, destinationVersion);
    }

    private static async Task<object> Inspect(PdfMcpArguments a, byte[] bytes, string revision)
    {
        using var pdf = Open(bytes);
        int number = a.Integer("page", 1, 1, pdf.GetNumberOfPages());
        var page = pdf.GetPage(number);
        var contents = await new PdfPageContentExtractor().ExtractAsync(bytes, number - 1, strict: true, includePreviews: false);
        var objects = contents.Select((content, index) => (object)new
        {
            objectId = $"{revision}:{number}:content:{index}", kind = content.NativeText != null ? "text" : content.GraphicOperations.Any(op => !op.IsImageOperation) ? "vector" : "image",
            x = content.X, y = content.Y, width = content.Width, height = content.Height,
            text = content.NativeText?.Text, fontFamily = content.FontFamily, fontSize = content.FontSize, color = content.Color
        }).ToList();
        objects.AddRange(page.GetAnnotations().Select((annotation, index) =>
        {
            var rect = annotation.GetRectangle()?.ToRectangle();
            var box = rect == null ? default : PdfPageCoordinates.ToDisplay(page).Map(new PdfTextBox(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
            return (object)new { objectId = $"{revision}:{number}:annotation:{index}", kind = "annotation", subtype = annotation.GetSubtype()?.GetValue(),
                x = box.X, y = box.Y, width = box.Width, height = box.Height, contents = annotation.GetContents()?.ToUnicodeString() };
        }));
        var pages = Enumerable.Range(1, pdf.GetNumberOfPages()).Select(i =>
        {
            var p = pdf.GetPage(i); var rect = p.GetCropBox();
            var bounds = PdfPageCoordinates.ToDisplay(p).Map(new PdfTextBox(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
            return new { page = i, width = bounds.Width, height = bounds.Height, rotation = p.GetRotation() };
        }).ToArray();
        return new { inputPath = a.Path("inputPath"), revision, pageCount = pdf.GetNumberOfPages(), pages, page = number,
            coordinates = "PDF points, displayed CropBox top-left; page numbers are 1-based", objects,
            formFields = new PdfFormService().ReadPage(bytes, number - 1) };
    }

    internal static void AddGraphic(PdfDocument pdf, int pageIndex, PdfMcpArguments a, PdfTextBox? original = null)
    {
        var bounds = a.Bounds(original);
        string kind = a.Has("imagePath") && !a.Has("kind") ? "image" : a.Text("kind");
        if (kind == "image")
        {
            if (a.Has("color") || a.Has("fillColor") || a.Has("lineWidth") || a.Has("points"))
                throw new ArgumentException("Image artwork is controlled by imagePath; color, fillColor, lineWidth and points do not apply.");
            new PdfContentWriter().AddImage(pdf, pageIndex, a.Path("imagePath"), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                rotation: a.Number("rotation"), opacity: a.Number("opacity", 1, 0, 1)); return;
        }
        var page = pdf.GetPage(pageIndex + 1);
        var matrix = PdfPageCoordinates.ToDisplay(page).Inverse().After(PdfAffineTransform.Between(bounds, bounds, a.Number("rotation")));
        var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdf);
        canvas.SaveState();
        canvas.ConcatMatrix(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
        var color = PdfMcpArguments.PdfColor(a.Color());
        var fill = a.Has("fillColor") ? PdfMcpArguments.PdfColor(a.Color("fillColor")) : color;
        canvas.SetStrokeColor(color).SetFillColor(fill).SetLineWidth((float)a.Number("lineWidth", 2, .1, 100));
        float opacity = (float)a.Number("opacity", kind == "highlight" ? .3 : 1, 0, 1);
        canvas.SetExtGState(new PdfExtGState().SetFillOpacity(opacity).SetStrokeOpacity(opacity));
        switch (kind)
        {
            case "rectangle": case "highlight": canvas.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height); break;
            case "ellipse": canvas.Ellipse(bounds.X, bounds.Y, bounds.Right, bounds.Bottom); break;
            case "line": case "polygon": case "freehand":
                var points = a.Has("points") ? a.Value.GetProperty("points").EnumerateArray().Select(p =>
                { var point = new PdfMcpArguments(p); return new PdfTextPoint(point.Number("x"), point.Number("y")); }).ToArray()
                    : kind == "line" ? new[] { new PdfTextPoint(bounds.X, bounds.Y), new PdfTextPoint(bounds.Right, bounds.Bottom) }
                    : throw new ArgumentException("points are required for polygons and freehand paths.");
                if (kind == "polygon" && points.Length < 3) throw new ArgumentException("A polygon needs at least 3 points.");
                canvas.MoveTo(points[0].X, points[0].Y);
                foreach (var point in points.Skip(1)) canvas.LineTo(point.X, point.Y);
                if (kind == "polygon") canvas.ClosePath();
                break;
            default: throw new ArgumentException("Unsupported graphic kind.");
        }
        if (kind == "highlight") canvas.Fill();
        else if (a.Has("fillColor") && kind is "rectangle" or "ellipse" or "polygon") canvas.FillStroke();
        else canvas.Stroke();
        canvas.RestoreState(); canvas.Release();
    }

    private static void ManagePages(PdfDocument pdf, PdfMcpArguments a)
    {
        int total = pdf.GetNumberOfPages();
        string action = a.Text("action");
        int[] pages = action == "insert" ? Array.Empty<int>() : a.Has("pages") ? ReadPages(a, total) : new[] { a.Integer("page", 1, 1, total) };
        switch (action)
        {
            case "insert":
                int at = a.Integer("page", total + 1, 1, total + 1);
                for (int i = 0, count = a.Integer("count", 1, 1, 10000); i < count; i++) pdf.AddNewPage(at + i, Size(a));
                break;
            case "delete":
                if (pages.Distinct().Count() >= total) throw new ArgumentException("At least one page must remain.");
                foreach (int page in pages.Distinct().OrderDescending()) pdf.RemovePage(page);
                break;
            case "reorder":
                if (!a.Has("pages") || pages.Length != total || pages.Distinct().Count() != total) throw new ArgumentException("pages must be a complete permutation.");
                var originals = pages.Select(pdf.GetPage).ToArray();
                for (int i = 0; i < originals.Length; i++) pdf.MovePage(originals[i], i + 1);
                break;
            case "rotate":
                int degrees = a.Integer("rotation", 90, -36000, 36000);
                if (degrees % 90 != 0) throw new ArgumentException("rotation must be a multiple of 90 degrees.");
                foreach (int number in pages.Distinct()) { var page = pdf.GetPage(number); page.SetRotation(((page.GetRotation() + degrees) % 360 + 360) % 360); }
                break;
            case "crop":
                var bounds = a.Bounds();
                foreach (int number in pages.Distinct())
                {
                    var page = pdf.GetPage(number); var media = page.GetMediaBox();
                    if (bounds.X < media.GetLeft() || bounds.Y < media.GetBottom() || bounds.Right > media.GetRight() || bounds.Bottom > media.GetTop())
                        throw new ArgumentException("Crop rectangle must be inside the unrotated MediaBox.");
                    page.SetCropBox(new Rectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height));
                }
                break;
            default: throw new ArgumentException("Unknown page action.");
        }
    }

    private static void SetFormFields(PdfDocument pdf, PdfMcpArguments a)
    {
        var form = PdfAcroForm.GetAcroForm(pdf, false) ?? throw new ArgumentException("PDF contains no AcroForm fields.");
        foreach (var pair in a.Value.GetProperty("values").EnumerateObject())
        {
            var field = form.GetField(pair.Name) ?? throw new ArgumentException($"Unknown field: {pair.Name}");
            if (field.GetFieldFlag(1)) throw new ArgumentException($"Field is read-only: {pair.Name}");
            string value = pair.Value.GetString()!;
            if (field.GetFormType().Equals(PdfName.Btn) && !field.GetAppearanceStates().Contains(value))
                throw new ArgumentException($"Invalid button value for {pair.Name}. Use an appearance state from pdf_inspect, such as Yes or Off.");
            if (value.Any(ch => ch > 127) && field.GetFormType().Equals(PdfName.Tx))
            {
                string fontPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf");
                var font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
                field.SetValue(value, font, 0);
            }
            else field.SetValue(value);
        }
    }

    private static void RemoveAnnotation(PdfDocument pdf, PdfPage page, iText.Kernel.Pdf.Annot.PdfAnnotation annotation)
    {
        var form = PdfAcroForm.GetAcroForm(pdf, false);
        if (form != null && PdfName.Widget.Equals(annotation.GetSubtype()))
        {
            foreach (var pair in form.GetAllFormFields().OrderByDescending(pair => pair.Key.Length).ToArray())
            {
                var field = pair.Value;
                if (!field.GetWidgets().Any(widget => widget.GetPdfObject().Equals(annotation.GetPdfObject()))) continue;
                if (field.GetWidgets().Count == 1) { form.RemoveField(pair.Key); return; }
                foreach (var child in field.GetChildFormAnnotations().Where(child => child.GetWidget().GetPdfObject().Equals(annotation.GetPdfObject())).ToArray())
                    field.RemoveChild(child);
            }
        }
        page.RemoveAnnotation(annotation);
    }

    private static byte[] Rewrite(byte[] bytes, Action<PdfDocument> edit, PdfMcpArguments? extract = null)
    {
        using var buffer = new MemoryStream();
        if (extract is { } a)
        {
            using var source = Open(bytes);
            using (var output = new PdfDocument(new PdfWriter(buffer))) source.CopyPagesTo(ReadPages(a, source.GetNumberOfPages()), output);
        }
        else using (var pdf = new PdfDocument(new PdfEditingReader(new MemoryStream(bytes)), new PdfWriter(buffer))) edit(pdf);
        return buffer.ToArray();
    }
    private static PdfDocument Open(byte[] bytes) => new(new PdfReader(new MemoryStream(bytes)));
    private static PageSize Size(PdfMcpArguments a) => new((float)a.Number("width", 595.276, 1, 14400), (float)a.Number("height", 841.89, 1, 14400));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Absolute(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("All paths must be absolute.");
        return System.IO.Path.GetFullPath(path);
    }
    private static int[] ReadPages(PdfMcpArguments a, int total) => a.Value.GetProperty("pages").EnumerateArray().Select(p =>
        p.TryGetInt32(out int page) && page >= 1 && page <= total ? page : throw new ArgumentException($"Pages must be integers from 1 to {total}.")).ToArray();
    private static async Task<object> Save(PdfMcpArguments a, byte[] bytes, CancellationToken cancellation, string? inputPath = null, string? inputRevision = null, PdfFileVersion? expected = null)
    {
        string path = a.Path("outputPath");
        if (!System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end in .pdf.");
        expected ??= PdfFileVersion.Read(path);
        if (expected.Hash != null && !a.Flag("overwrite")) throw new IOException("Output already exists. Choose a new path or set overwrite=true explicitly.");
        if (inputPath != null && path.Equals(inputPath, StringComparison.OrdinalIgnoreCase) &&
            (expected.Hash == null || Convert.ToHexStringLower(expected.Hash) != inputRevision))
            throw new IOException("Source file changed during editing. Inspect it again.");
        using var pdf = Open(bytes);
        int pages = pdf.GetNumberOfPages();
        cancellation.ThrowIfCancellationRequested();
        await PdfAtomicFile.WriteAsync(path, bytes, expected);
        return new { outputPath = path, revision = Hash(bytes), pageCount = pages };
    }

    // Enforce the advertised schema before touching files, including nested arrays.
    private static void Validate(JsonElement value, JsonElement schema, string path, JsonElement? rootSchema = null)
    {
        rootSchema ??= schema;
        if (schema.TryGetProperty("$ref", out var reference))
        { Validate(value, rootSchema.Value.GetProperty("$defs").GetProperty(reference.GetString()!.Split('/')[^1]), path, rootSchema); return; }
        string type = schema.GetProperty("type").GetString()!;
        bool valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), _ => false
        };
        if (!valid) throw new ArgumentException($"{path} must be {type}.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(item => item.GetRawText() == value.GetRawText()))
            throw new ArgumentException($"Invalid {path}.");
        if (type is "number" or "integer")
        {
            double number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() || schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
                throw new ArgumentException($"{path} is out of range.");
        }
        if (type == "array")
        {
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32()) throw new ArgumentException($"{path} cannot be empty.");
            if (schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw new ArgumentException($"{path} contains too many items.");
            foreach (var item in value.EnumerateArray()) Validate(item, schema.GetProperty("items"), path + "[]", rootSchema);
        }
        if (type == "object")
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var key in required.EnumerateArray()) if (!value.TryGetProperty(key.GetString()!, out _)) throw new ArgumentException($"Missing {path}.{key.GetString()}.");
            foreach (var item in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(item.Name, out var child)) Validate(item.Value, child, path + "." + item.Name, rootSchema);
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    if (additional.ValueKind == JsonValueKind.False) throw new ArgumentException($"Unknown argument {path}.{item.Name}.");
                    if (additional.ValueKind == JsonValueKind.Object) Validate(item.Value, additional, path + "." + item.Name, rootSchema);
                }
            }
        }
    }
}
