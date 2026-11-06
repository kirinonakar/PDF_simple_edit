using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using iText.Kernel.Geom;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal sealed partial class PdfMcpCreationService
{
    private static readonly string[] BoxProperties = { "backgroundColor", "borderColor", "borderWidth", "padding", "marginTop", "marginBottom", "keepTogether" };

    internal static object BlockDefinitions()
    {
        object Text(string description) => new { type = "string", description };
        object Number(string description) => new { type = "number", description };
        object Array(object items, int min = 1) => new { type = "array", items, minItems = min, maxItems = 10000 };
        object Object(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
        var reference = new Dictionary<string, object> { ["$ref"] = "#/$defs/block" };
        var align = new { type = "string", @enum = new[] { "left", "center", "right", "justify" } };
        return new
        {
            block = Object(new
            {
                type = new { type = "string", @enum = new[] { "heading", "paragraph", "table", "image", "section", "columns", "rule", "spacer", "pageBreak" } },
                text = Text("Required for heading/paragraph. Plain Unicode text; newlines are preserved."),
                fontFamily = Text("Installed font family or absolute font-file path. Inherits document default (Malgun Gothic)."),
                fontSize = Number("Points; default 12 or 24 for headings."), color = Text("#RRGGBB text/rule color."),
                bold = new { type = "boolean" }, italic = new { type = "boolean" }, alignment = align,
                imagePath = Text("Absolute image path, required for image."),
                width = Number("Optional flow image width; preserves aspect ratio and fits the current column/page."),
                height = Number("Required spacer height in points."),
                headers = Array(Text("Table column heading.")), rows = Array(Array(Text("Table cell text."))),
                columnWidths = Array(new { type = "number", minimum = .001, description = "Relative width weight, one per table column." }),
                columnAlignments = Array(align), cellPadding = Number("Table cell padding, default 6."),
                headerBackgroundColor = Text("Optional table header background #RRGGBB."),
                content = Array(reference),
                columns = Array(Object(new { weight = new { type = "number", minimum = .001 }, content = Array(reference) }, "content")),
                gap = Number("Space between columns in points, default 16."),
                backgroundColor = Text("Optional block background #RRGGBB."), borderColor = Text("Optional #RRGGBB border color."),
                borderWidth = Number("Border width in points; table default .75. Use 0 for borderless tables."),
                padding = Number("Block padding in points, default 0."),
                marginTop = Number("Space before block in points, default 0."), marginBottom = Number("Space after block in points, default 8."),
                keepTogether = new { type = "boolean", description = "Keep the block on one page if it fits, default false." }
            }, "type")
        };
    }

    private Div BuildBlock(PdfMcpArguments block, float availableWidth, float availableHeight, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        float padding = (float)block.Number("padding", 0, 0, availableWidth / 2);
        float innerWidth = availableWidth - 2 * padding;
        if (innerWidth < 1) throw new ArgumentException("Block padding leaves no room for content.");
        var container = new Div().SetPadding(padding)
            .SetMarginTop((float)block.Number("marginTop", 0, 0, 14400))
            .SetMarginBottom((float)block.Number("marginBottom", 8, 0, 14400)).SetKeepTogether(block.Flag("keepTogether"));
        if (block.Has("backgroundColor")) container.SetBackgroundColor(PdfMcpArguments.PdfColor(block.Color("backgroundColor")));
        if ((block.Has("borderWidth") || block.Has("borderColor")) && block.Text("type") != "table")
        {
            float thickness = (float)block.Number("borderWidth", 1, 0, 100);
            container.SetBorder(thickness == 0 ? Border.NO_BORDER : new SolidBorder(PdfMcpArguments.PdfColor(block.Color("borderColor")), thickness));
        }
        switch (block.Text("type"))
        {
            case "heading": case "paragraph":
                AllowBlock(block, "type", "text", "fontFamily", "fontSize", "color", "bold", "italic", "alignment");
                var paragraph = Paragraph(block, block.Text("text"), block.Text("type") == "heading").SetMarginBottom(0);
                container.Add(paragraph);
                if (block.Text("type") == "heading") container.SetKeepWithNext(true);
                break;
            case "image":
                AllowBlock(block, "type", "imagePath", "width", "alignment");
                var image = new Image(PdfImageDataLoader.Create(block.Path("imagePath")));
                float width = (float)block.Number("width", Math.Min(image.GetImageWidth(), innerWidth), .001, innerWidth);
                image.ScaleToFit(width, Math.Max(1, availableHeight - padding * 2 - 8));
                image.SetHorizontalAlignment(block.Text("alignment", "left") switch
                { "left" => HorizontalAlignment.LEFT, "center" => HorizontalAlignment.CENTER, "right" => HorizontalAlignment.RIGHT, _ => throw new ArgumentException("Image alignment must be left, center or right.") });
                container.Add(image);
                break;
            case "table":
                AllowBlock(block, "type", "headers", "rows", "columnWidths", "columnAlignments", "cellPadding", "headerBackgroundColor", "fontFamily", "fontSize", "color", "bold", "italic", "alignment");
                container.Add(Table(block, cancellation).SetMarginBottom(0));
                break;
            case "section":
                AllowBlock(block, "type", "content");
                if (!block.Has("content")) throw new ArgumentException("section.content is required.");
                foreach (var child in block.Value.GetProperty("content").EnumerateArray()) container.Add(BuildBlock(new(child), innerWidth, availableHeight - 2 * padding, cancellation));
                break;
            case "columns":
                AllowBlock(block, "type", "columns", "gap");
                if (!block.Has("columns")) throw new ArgumentException("columns.columns is required.");
                var columns = block.Value.GetProperty("columns").EnumerateArray().Select(value => new PdfMcpArguments(value)).ToArray();
                float gap = (float)block.Number("gap", 16, 0, innerWidth);
                float usable = innerWidth - gap * (columns.Length - 1);
                if (usable < columns.Length) throw new ArgumentException("Column gaps leave no room for content.");
                var weights = columns.Select(column => (float)column.Number("weight", 1, .001, 10000)).ToArray();
                float totalWeight = weights.Sum();
                var grid = new Table(UnitValue.CreatePercentArray(weights)).UseAllAvailableWidth().SetBorder(Border.NO_BORDER);
                for (int i = 0; i < columns.Length; i++)
                {
                    var cell = new Cell().SetBorder(Border.NO_BORDER).SetPadding(0).SetPaddingRight(i < columns.Length - 1 ? gap : 0);
                    float columnWidth = innerWidth * weights[i] / totalWeight - (i < columns.Length - 1 ? gap : 0);
                    if (columnWidth < 1) throw new ArgumentException("Column weights and gaps leave a column without usable width.");
                    foreach (var child in columns[i].Value.GetProperty("content").EnumerateArray()) cell.Add(BuildBlock(new(child), columnWidth, availableHeight - 2 * padding, cancellation));
                    grid.AddCell(cell);
                }
                container.Add(grid);
                break;
            case "rule":
                AllowBlock(block, "type", "color", "height");
                container.Add(new Div().SetHeight((float)block.Number("height", 1, .1, 100)).SetBackgroundColor(PdfMcpArguments.PdfColor(block.Color())));
                break;
            case "spacer":
                AllowBlock(block, "type", "height");
                if (!block.Has("height")) throw new ArgumentException("spacer.height is required.");
                container.SetHeight((float)block.Number("height", 8, .1, 14400));
                break;
            case "pageBreak": throw new ArgumentException("pageBreak is allowed only at the top level of document content, outside sections/columns.");
            default: throw new ArgumentException("Unknown content type.");
        }
        return container;
    }

    private static void AllowBlock(PdfMcpArguments arguments, params string[] fields) => Allow(arguments, fields.Concat(BoxProperties).ToArray());
}
