using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using iText.IO.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Font;
using iText.Layout.Borders;
using iText.Layout.Layout;
using iText.Layout.Properties;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

/// <summary>Content authoring with automatic layout or explicit page composition.</summary>
internal sealed partial class PdfMcpCreationService
{
    private readonly FontProvider _fonts = new();
    private readonly Dictionary<string, string> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly PdfMcpArguments _defaults;

    private PdfMcpCreationService(PdfMcpArguments defaults) => _defaults = defaults;

    internal static Dictionary<string, object> Schema(Dictionary<string, object> graphics)
    {
        object String(string description) => new { type = "string", description };
        object Number(string description) => new { type = "number", description };
        object Array(object items, int min = 0, int max = 10000) => new { type = "array", items, minItems = min, maxItems = max };
        object Object(Dictionary<string, object> properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
        var style = new Dictionary<string, object>
        {
            ["fontFamily"] = String("Installed font family or absolute font-file path; inherits document default (Malgun Gothic)."),
            ["fontSize"] = Number("Font size in points; inherits document default (12), headings default to 24."),
            ["color"] = String("#RRGGBB text color, default #000000."),
            ["bold"] = new { type = "boolean" }, ["italic"] = new { type = "boolean" },
            ["alignment"] = new { type = "string", @enum = new[] { "left", "center", "right", "justify" } }
        };
        var elements = graphics.Where(pair => pair.Key != "kind").ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in style) elements[pair.Key] = pair.Value;
        foreach (string key in new[] { "x", "y", "width", "height" }) elements[key] = Number("Points from displayed page's top-left; text width/height define its wrapping box.");
        elements["type"] = new { type = "string", @enum = new[] { "text", "image", "rectangle", "ellipse", "line", "polygon", "freehand", "highlight" } };
        elements["text"] = String("Required for type=text; wraps inside width/height. Overflow is an error rather than silently losing text.");
        var page = new Dictionary<string, object>
        {
            ["width"] = Number("Unrotated page width in points; inherits document width."),
            ["height"] = Number("Unrotated page height in points; inherits document height."),
            ["rotation"] = Number("Page rotation, a multiple of 90 degrees."),
            ["backgroundColor"] = String("Optional page background #RRGGBB."),
            ["elements"] = Array(Object(elements, "type"))
        };
        return new()
        {
            ["jsonPath"] = String("Absolute path to a UTF-8 JSON file containing pdf_create arguments. Only outputPath/overwrite may accompany jsonPath; explicit output arguments override the file."),
            ["text"] = String("Plain body text, automatically wrapped and paginated. Mutually exclusive with content/pages."),
            ["content"] = Array(new Dictionary<string, object> { ["$ref"] = "#/$defs/block" }, 1),
            ["pages"] = Array(Object(page), 1),
            ["pageCount"] = new { type = "integer", minimum = 1, maximum = 10000, description = "Only for legacy blank documents; omit when text/content/pages is supplied." },
            ["width"] = Number("Default page width, 595.276 points (A4)."),
            ["height"] = Number("Default page height, 841.89 points (A4)."),
            ["margin"] = Number("Flow layout margin on each side, default 36 points."),
            ["title"] = String("PDF metadata title. Use a heading in content to put a visible title on the page.")
        };
    }

    internal static byte[] Create(PdfMcpArguments arguments, CancellationToken cancellation)
    {
        int modes = new[] { "text", "content", "pages" }.Count(arguments.Has);
        if (modes > 1) throw new ArgumentException("Supply only one of text, content or pages.");
        if (modes != 0 && arguments.Has("pageCount")) throw new ArgumentException("pageCount is only for blank documents; content determines the page count.");
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(buffer)))
        {
            if (arguments.Has("title")) pdf.GetDocumentInfo().SetTitle(arguments.Text("title"));
            var creator = new PdfMcpCreationService(arguments);
            var size = creator.Size(arguments);
            if (arguments.Has("pages")) creator.ComposePages(pdf, cancellation);
            else if (modes != 0) creator.Flow(pdf, size, cancellation);
            else for (int i = 0, count = arguments.Integer("pageCount", 1, 1, 10000); i < count; i++)
            { cancellation.ThrowIfCancellationRequested(); pdf.AddNewPage(size); }
        }
        cancellation.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    private PageSize Size(PdfMcpArguments page) => new(
        (float)page.Number("width", _defaults.Number("width", 595.276, 1, 14400), 1, 14400),
        (float)page.Number("height", _defaults.Number("height", 841.89, 1, 14400), 1, 14400));

    private void Flow(PdfDocument pdf, PageSize size, CancellationToken cancellation)
    {
        float margin = (float)_defaults.Number("margin", 36, 0, 7200);
        if (margin * 2 >= Math.Min(size.GetWidth(), size.GetHeight())) throw new ArgumentException("Margins must leave a positive page content area.");
        using var document = new Document(pdf, size);
        document.SetMargins(margin, margin, margin, margin);
        document.SetFontProvider(_fonts);
        // Keep even an intentionally empty text block as a valid one-page PDF.
        pdf.AddNewPage(size);
        if (_defaults.Has("text")) document.Add(Paragraph(_defaults, _defaults.Text("text")));
        else foreach (var value in _defaults.Value.GetProperty("content").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var block = new PdfMcpArguments(value);
            if (block.Text("type") == "pageBreak")
            { Allow(block, "type"); document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE)); }
            else document.Add(BuildBlock(block, size.GetWidth() - 2 * margin, size.GetHeight() - 2 * margin, cancellation));
        }
    }

    private Table Table(PdfMcpArguments block, CancellationToken cancellation)
    {
        if (!block.Has("rows")) throw new ArgumentException("Table rows are required.");
        var rows = block.Value.GetProperty("rows").EnumerateArray().ToArray();
        int count = rows[0].GetArrayLength();
        if (rows.Any(row => row.GetArrayLength() != count)) throw new ArgumentException("Every table row must have the same number of cells.");
        float[] widths = block.Has("columnWidths") ? block.Value.GetProperty("columnWidths").EnumerateArray().Select(value => (float)value.GetDouble()).ToArray() : Enumerable.Repeat(1f, count).ToArray();
        if (widths.Length != count || widths.Any(width => !float.IsFinite(width) || width <= 0)) throw new ArgumentException("columnWidths must contain one positive weight per column.");
        var table = new Table(UnitValue.CreatePercentArray(widths)).UseAllAvailableWidth().SetMarginBottom(10);
        string[]? alignments = block.Has("columnAlignments") ? block.Value.GetProperty("columnAlignments").EnumerateArray().Select(value => value.GetString()!).ToArray() : null;
        if (alignments != null && alignments.Length != count) throw new ArgumentException("columnAlignments must contain one alignment per column.");
        float thickness = (float)block.Number("borderWidth", .75, 0, 100), cellPadding = (float)block.Number("cellPadding", 6, 0, 100);
        Border border = thickness == 0 ? Border.NO_BORDER : new SolidBorder(PdfMcpArguments.PdfColor(block.Color("borderColor")), thickness);
        Cell Cell(string text, int column, bool header)
        {
            var paragraph = Paragraph(block, text, header, header ? 12 : null).SetMarginBottom(0);
            if (alignments != null) paragraph.SetTextAlignment(alignments[column] switch
            { "left" => TextAlignment.LEFT, "right" => TextAlignment.RIGHT, "center" => TextAlignment.CENTER, _ => TextAlignment.JUSTIFIED });
            var cell = new Cell().Add(paragraph).SetPadding(cellPadding).SetBorder(border);
            if (header && block.Has("headerBackgroundColor")) cell.SetBackgroundColor(PdfMcpArguments.PdfColor(block.Color("headerBackgroundColor")));
            return cell;
        }
        if (block.Has("headers"))
        {
            var headers = block.Value.GetProperty("headers");
            if (headers.GetArrayLength() != count) throw new ArgumentException("Table headers must match the number of columns.");
            int column = 0;
            foreach (var header in headers.EnumerateArray()) table.AddHeaderCell(Cell(header.GetString()!, column++, true));
        }
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            int column = 0;
            foreach (var cell in row.EnumerateArray()) table.AddCell(Cell(cell.GetString()!, column++, false));
        }
        return table;
    }

    private Paragraph Paragraph(PdfMcpArguments style, string text, bool heading = false, double? size = null)
    {
        string family = style.Text("fontFamily", _defaults.Text("fontFamily", "Malgun Gothic"));
        string alias = RegisterFont(family);
        string fallback = RegisterFont("Malgun Gothic");
        var paragraph = new Paragraph(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
            .SetFontFamily(alias, fallback).SetFontSize((float)style.Number("fontSize", size ?? (heading ? 24 : _defaults.Number("fontSize", 12, .1, 10000)), .1, 10000))
            .SetFontColor(PdfMcpArguments.PdfColor(style.Color(fallback: _defaults.Color())))
            .SetMultipliedLeading(1.3f).SetMargin(0).SetMarginBottom(8);
        if (style.Flag("bold", heading || _defaults.Flag("bold"))) paragraph.SimulateBold();
        if (style.Flag("italic", _defaults.Flag("italic"))) paragraph.SimulateItalic();
        paragraph.SetTextAlignment(style.Text("alignment", "left") switch
        { "left" => TextAlignment.LEFT, "center" => TextAlignment.CENTER, "right" => TextAlignment.RIGHT, "justify" => TextAlignment.JUSTIFIED, _ => throw new ArgumentException("Invalid text alignment.") });
        return paragraph;
    }

    private string RegisterFont(string family)
    {
        if (_families.TryGetValue(family, out string? alias)) return alias;
        if (File.Exists(family) && !System.IO.Path.IsPathFullyQualified(family)) throw new ArgumentException("Font-file paths must be absolute.");
        string path = File.Exists(family) ? family : InstalledFontService.FindFontFile(family, 400, false)
            ?? throw new ArgumentException($"Cannot find installed font: {family}");
        alias = $"pse-create-{_families.Count}";
        if (!_fonts.GetFontSet().AddFont(path, PdfEncodings.IDENTITY_H, alias)) throw new ArgumentException($"Cannot load font: {family}");
        _families.Add(family, alias);
        return alias;
    }

    private void ComposePages(PdfDocument pdf, CancellationToken cancellation)
    {
        foreach (var value in _defaults.Value.GetProperty("pages").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var definition = new PdfMcpArguments(value);
            var page = pdf.AddNewPage(Size(definition));
            int rotation = definition.Integer("rotation", 0, -36000, 36000);
            if (rotation % 90 != 0) throw new ArgumentException("Page rotation must be a multiple of 90.");
            page.SetRotation((rotation % 360 + 360) % 360);
            if (definition.Has("backgroundColor"))
            {
                var background = new PdfCanvas(page.NewContentStreamBefore(), page.GetResources(), pdf);
                background.SetFillColor(PdfMcpArguments.PdfColor(definition.Color("backgroundColor"))).Rectangle(page.GetMediaBox()).Fill();
                background.Release();
            }
            if (!definition.Has("elements")) continue;
            foreach (var item in definition.Value.GetProperty("elements").EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                var element = new PdfMcpArguments(item);
                string type = element.Text("type");
                if (type == "text")
                {
                    Allow(element, "type", "text", "x", "y", "width", "height", "rotation", "fontFamily", "fontSize", "color", "bold", "italic", "alignment");
                    PositionedText(pdf, page, element);
                }
                else
                {
                    // Reuse exactly the same graphic writer as editing existing PDFs.
                    var graphic = item.EnumerateObject().Where(pair => pair.Name != "type").ToDictionary(pair => pair.Name, pair => (object)pair.Value);
                    foreach (string key in new[] { "text", "fontFamily", "fontSize", "bold", "italic", "alignment" })
                        if (graphic.ContainsKey(key)) throw new ArgumentException($"{key} is not a graphic property.");
                    graphic["kind"] = type;
                    PdfMcpTools.AddGraphic(pdf, pdf.GetNumberOfPages() - 1, new(JsonSerializer.SerializeToElement(graphic)));
                }
            }
        }
    }

    private void PositionedText(PdfDocument pdf, PdfPage page, PdfMcpArguments element)
    {
        var crop = page.GetCropBox();
        var display = PdfPageCoordinates.ToDisplay(page);
        var pageBounds = display.Map(new PdfTextBox(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight()));
        double x = element.Number("x"), y = element.Number("y");
        var bounds = element.Bounds(new(x, y, pageBounds.Width - x, pageBounds.Height - y));
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Right > pageBounds.Width + .001 || bounds.Bottom > pageBounds.Height + .001)
            throw new ArgumentException("Text box must fit inside its displayed page.");
        var rectangle = new Rectangle(0, 0, (float)bounds.Width, (float)bounds.Height);
        var transform = display.Inverse().After(PdfAffineTransform.Between(bounds, bounds, element.Number("rotation")))
            .After(new(1, 0, 0, -1, bounds.X, bounds.Bottom));
        var pdfCanvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdf);
        pdfCanvas.SaveState().ConcatMatrix(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F);
        using (var canvas = new Canvas(pdfCanvas, rectangle))
        {
            canvas.SetFontProvider(_fonts);
            var paragraph = Paragraph(element, element.Text("text")).SetMarginBottom(0);
            var renderer = paragraph.CreateRendererSubTree().SetParent(canvas.GetRenderer());
            var layout = renderer.Layout(new LayoutContext(new LayoutArea(pdf.GetNumberOfPages(), rectangle)));
            if (layout.GetStatus() != LayoutResult.FULL) throw new ArgumentException("Text does not fit its page box. Increase width/height, reduce fontSize, or use text/content mode for automatic pagination.");
            canvas.Add(paragraph);
        }
        pdfCanvas.RestoreState(); pdfCanvas.Release();
    }

    private static void Allow(PdfMcpArguments arguments, params string[] names)
    {
        foreach (var property in arguments.Value.EnumerateObject())
            if (!names.Contains(property.Name)) throw new ArgumentException($"{property.Name} does not apply to {arguments.Text("type")}.");
    }
}
