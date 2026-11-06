using System;
using System.Text.Json;

namespace PDF_simple_edit.Mcp;

internal static class PdfMcpCreationGuide
{
    internal static object Get(string topic, object schema) => topic switch
    {
        "schema" => new { inputSchema = schema },
        "flow" => new
        {
            rules = new[]
            {
                "Use content for automatic layout/pagination. The content array contains generic blocks; there are no domain-specific templates.",
                "Every block needs type. heading/paragraph require text; table requires rectangular rows of strings; image requires absolute imagePath; section requires content; columns requires columns[].content; spacer requires height.",
                "section and columns can nest the same block types. pageBreak works only at the top level of content.",
                "Text styles: fontFamily, fontSize, color (#RRGGBB), bold, italic, alignment (left/center/right/justify). Styles on each text/table block override document defaults.",
                "Box styles: backgroundColor, borderColor, borderWidth, padding, marginTop, marginBottom, keepTogether. Table borderWidth styles cell borders; 0 makes them borderless.",
                "Table options: headers (same column count), columnWidths (positive relative weights), columnAlignments (one per column), cellPadding, headerBackgroundColor.",
                "columns options: gap in points, columns[].weight (positive relative width). Each column holds its own content array.",
                "Do not supply x/y in flow blocks. Use pages for exact positions. width on a flow image preserves aspect ratio and fits the current column.",
                "title is metadata. Add a heading block to show a title. Body text wraps/paginates; headings default to 24pt, body to 12pt. Blank creation uses pageCount only when text/content/pages are absent."
            },
            arguments = JsonSerializer.Deserialize<JsonElement>(FlowExample)
        },
        "positioned" => new
        {
            rules = new[]
            {
                "Use pages for exact page composition. Each page has width/height in points, optional rotation (multiple of 90), backgroundColor, and elements.",
                "Each element uses type, not kind. Supported types: text, image, rectangle, ellipse, line, polygon, freehand, highlight. Array order is paint order: backgrounds first, text last.",
                "x/y are from the displayed page's top-left, after page rotation. width/height are points. A4 defaults to 595.276 by 841.89. One point is 1/72 inch.",
                "Text requires text; width/height define the wrapping box, defaulting to remaining page space. A text box that does not fit is an error: enlarge it or reduce fontSize. Text content is never silently truncated.",
                "Text styles: fontFamily, fontSize, color, bold, italic, alignment. Optional rotation rotates around the box center.",
                "Images require absolute imagePath and support x/y/width/height/rotation/opacity. Image color/fillColor/lineWidth do not apply.",
                "Shapes support color (stroke), fillColor, lineWidth, opacity, rotation. polygon/freehand require points [{x,y},...]; a polygon needs 3 or more points. Line defaults to the box diagonal.",
                "No pageCount with pages. Do not put content/text beside pages. Choose document-specific text, colors, dimensions and positions in the JSON rather than inventing preset fields."
            },
            arguments = JsonSerializer.Deserialize<JsonElement>(PositionedExample)
        },
        "roundtrip" => new
        {
            steps = new object[]
            {
                new { tool = "pdf_to_json", arguments = new { inputPath = @"D:\Documents\original.pdf", outputPath = @"D:\Documents\archive.json" } },
                new { tool = "pdf_create", arguments = new { jsonPath = @"D:\Documents\archive.json", outputPath = @"D:\Documents\restored.pdf" } }
            },
            rules = new[]
            {
                "Archive format is pdf-simple-editor/archive-v1. pdfBase64 contains the entire original PDF; sha256 validates it. Do not author or alter these fields manually.",
                "Restoration returns exactly the original PDF bytes, retaining fonts, vectors, images, annotations, metadata and object ordering.",
                "sourceInfo is a read-only page/object inspection snapshot, not editable creation data. Changing sourceInfo does not edit the restored PDF. For edits, use PDF editing tools after restoration, or author a new text/content/pages document.",
                "When using jsonPath, only outputPath and overwrite may accompany it. Explicit output arguments override those in the JSON file. Nested jsonPath references are rejected.",
                "JSON file may be a raw pdf_create arguments object or {name:'pdf_create',arguments:{...}} (use proper double-quoted JSON). All asset/output paths inside must be absolute.",
                "CLI: PDFSimpleEditor.exe --create input.json --output output.pdf; PDFSimpleEditor.exe input.json --output output.pdf; PDFSimpleEditor.exe --to-json input.pdf --output archive.json. Add --overwrite only when replacement is intended."
            }
        },
        _ => new
        {
            recommendedWorkflow = new[]
            {
                "Read the user's content and design requirements. Choose content for flowing documents or pages for exact layout. Use text for simple plain body text.",
                "Call pdf_create_help with flow or positioned for working examples; call schema for the exact live schema. Do not invent fields.",
                "Write UTF-8 JSON without comments/trailing commas. Use literal JSON newlines as \\n, absolute paths, #RRGGBB colors, and PDF-point dimensions.",
                "Call pdf_create with the JSON inline, or {jsonPath:absolute_file,outputPath:absolute_pdf}. Do not wrap a raw arguments JSON file in a tools/call protocol message.",
                "On validation/layout errors, use the reported field/type and adjust JSON before retrying. Failed creation does not replace the output. Existing destinations need explicit overwrite=true.",
                "Verify created text with pdf_read and geometry with pdf_inspect. Use pdf_to_images to review the visual result.",
                "For exact PDF→JSON→PDF preservation, use pdf_to_json then pdf_create jsonPath; see topic=roundtrip."
            },
            simpleArguments = new { outputPath = @"D:\Documents\document.pdf", text = "Document body\n한글 본문", fontSize = 12, margin = 36 },
            modes = new { text = "Automatic plain text", content = "Automatic structured blocks", pages = "Explicit page elements", jsonPath = "Load creation JSON or lossless archive", blank = "No content fields; optional pageCount" },
            nextTopics = new[] { "flow", "positioned", "roundtrip", "schema" }
        }
    };

    internal const string FlowExample = """
    {
      "outputPath": "D:\\Documents\\document.pdf",
      "title": "Sample Document",
      "fontFamily": "Malgun Gothic",
      "fontSize": 12,
      "margin": 36,
      "content": [
        {"type":"section","backgroundColor":"#20242A","padding":20,"content":[
          {"type":"heading","text":"Sample Document","fontSize":28,"color":"#FFFFFF","alignment":"center"},
          {"type":"paragraph","text":"문서의 부제 또는 설명","color":"#D4B36A","alignment":"center"}
        ]},
        {"type":"columns","gap":20,"columns":[
          {"weight":1,"content":[
            {"type":"heading","text":"Section A","fontSize":18,"color":"#92392F"},
            {"type":"rule","color":"#92392F"},
            {"type":"table","borderWidth":0,"cellPadding":5,"columnWidths":[3,1],"columnAlignments":["left","right"],"rows":[["First item","100"],["Second item","200"]]}
          ]},
          {"weight":1,"content":[
            {"type":"heading","text":"Section B","fontSize":18,"color":"#92392F"},
            {"type":"paragraph","text":"오른쪽 열의 본문입니다. 내용에 따라 줄바꿈과 페이지 나눔이 적용됩니다."}
          ]}
        ]},
        {"type":"paragraph","text":"Additional information","fontSize":10,"alignment":"center"}
      ]
    }
    """;

    internal const string PositionedExample = """
    {
      "outputPath":"D:\\Documents\\composed.pdf",
      "pages":[{
        "width":612,"height":792,"backgroundColor":"#F4F0E8",
        "elements":[
          {"type":"rectangle","x":0,"y":0,"width":612,"height":150,"fillColor":"#20242A","color":"#20242A"},
          {"type":"text","text":"Sample Document","x":48,"y":40,"width":516,"height":64,"fontSize":36,"color":"#FFFFFF","alignment":"center"},
          {"type":"line","x":48,"y":190,"width":516,"height":1,"color":"#92392F","lineWidth":1},
          {"type":"text","text":"Left column\n왼쪽 내용","x":48,"y":210,"width":244,"height":360,"fontSize":14},
          {"type":"text","text":"Right column\n오른쪽 내용","x":320,"y":210,"width":244,"height":360,"fontSize":14},
          {"type":"text","text":"Footer information","x":48,"y":710,"width":516,"height":30,"fontSize":11,"alignment":"center"}
        ]
      }]
    }
    """;
}
