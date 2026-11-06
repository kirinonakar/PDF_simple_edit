# PDF creation JSON for agents

Call `pdf_create_help` before composing a complex document. Use `topic: "schema"`
for the live schema, `flow` or `positioned` for complete working examples, and
`roundtrip` for exact restoration. The examples returned by the tool are valid
creation arguments; replace their output paths with an existing local folder.

## Choose a creation mode

| Field | Use it for | Pagination |
| --- | --- | --- |
| `text` | Plain body text | Automatic wrapping and page breaks |
| `content` | Structured documents with generic blocks | Automatic |
| `pages` | Exact typography, backgrounds and geometry | Explicit page list |
| `jsonPath` | Arguments stored in a UTF-8 JSON file, or a lossless archive | As defined in the file |

Supply one mode. Do not combine `text`, `content`, or `pages`. Do not combine
`pageCount` with content: it is only for legacy blank documents. `title` sets
metadata; add a heading or text element for a visible title. There are no
document-specific presets. Derive the wording, structure and visual design from
the user's request, using the generic fields below.

All asset and output paths inside JSON must be absolute. Colors are `#RRGGBB`.
Sizes use PDF points (72 points/inch). Default paper is A4, 595.276 by 841.89
points. Optional document defaults are `fontFamily`, `fontSize`, `color`, `bold`,
`italic`, `width`, `height`, and `margin`. Use an installed font that supports the
requested language, or an absolute font-file path. JSON uses double quotes,
`\n` for line breaks, no comments, and no trailing commas.

## Flowing content

```json
{
  "outputPath": "D:\\Documents\\report.pdf",
  "fontSize": 12,
  "margin": 36,
  "content": [
    {"type": "heading", "text": "Document title", "color": "#234466"},
    {"type": "paragraph", "text": "First paragraph.\n다음 줄의 내용입니다."},
    {"type": "table", "headers": ["Item", "Value"], "rows": [["A", "100"], ["B", "200"]]}
  ]
}
```

Every block needs `type`. Text/table styles override document defaults; styles
on a containing section do not replace styles on its child text blocks.

| Type | Required fields | Optional fields specific to this type |
| --- | --- | --- |
| `heading`, `paragraph` | `text` | `fontFamily`, `fontSize`, `color`, `bold`, `italic`, `alignment` |
| `table` | `rows` | `headers`, `columnWidths`, `columnAlignments`, `cellPadding`, `headerBackgroundColor`, text styles |
| `image` | `imagePath` | `width`, `alignment` |
| `section` | `content` | Common box styles |
| `columns` | `columns`, each containing `content` | `gap`, each column's positive `weight` |
| `rule` | None | `height`, `color` |
| `spacer` | `height` | Common box styles |
| `pageBreak` | None | Allowed only at the document content's top level |

Common box styles are `backgroundColor`, `borderColor`, `borderWidth`, `padding`,
`marginTop`, `marginBottom`, and `keepTogether`. `heading` defaults to 24pt; body
text defaults to 12pt. Alignment is `left`, `center`, `right`, or `justify`
(images support the first three). A flow image preserves its aspect ratio and
fits its available column/page. Flow blocks do not accept x/y positions.

Table `rows` are rectangular arrays of strings. `headers`, `columnWidths` and
`columnAlignments`, when supplied, must match the column count. Widths are positive
relative weights. Set `borderWidth: 0` for borderless rows; set
`columnAlignments: ["left", "right"]` for a two-column label/value list. Table
`borderWidth`/`borderColor` apply to cell borders; `cellPadding` defaults to 6pt.

Nested sections and columns accept the same blocks. For example:

```json
{
  "type": "columns",
  "gap": 20,
  "columns": [
    {"weight": 1, "content": [{"type": "heading", "text": "Left section"}, {"type": "paragraph", "text": "Left body"}]},
    {"weight": 1, "content": [{"type": "heading", "text": "Right section"}, {"type": "paragraph", "text": "Right body"}]}
  ]
}
```

## Positioned pages

Each `pages` entry accepts `width`, `height`, `rotation`, `backgroundColor` and
`elements`. Elements paint in array order: place backgrounds before foreground
text. Coordinates start at the displayed page's top-left after page rotation.
Page rotation must be a multiple of 90 degrees.

```json
{
  "outputPath": "D:\\Documents\\composed.pdf",
  "pages": [{
    "width": 612, "height": 792, "backgroundColor": "#F4F0E8",
    "elements": [
      {"type": "rectangle", "x": 0, "y": 0, "width": 612, "height": 150, "fillColor": "#20242A", "color": "#20242A"},
      {"type": "text", "text": "Document title", "x": 48, "y": 40, "width": 516, "height": 64, "fontSize": 32, "color": "#FFFFFF", "alignment": "center"},
      {"type": "text", "text": "Body text", "x": 48, "y": 190, "width": 516, "height": 500}
    ]
  }]
}
```

Elements use `type`, not the editing tool's `kind` field. Supported types are
`text`, `image`, `rectangle`, `ellipse`, `line`, `polygon`, `freehand`, `highlight`.
All support x/y/width/height and rotation as applicable. Text requires `text` and
supports the text styles above. Its width/height define a wrapping box; omitted
dimensions use the page's remaining space. Overflow produces an error rather
than dropping text. Enlarge the box, reduce font size or use flow content.

Images require `imagePath`; they support rotation and `opacity`, not stroke/fill
colors. Shapes support `color` (stroke), `fillColor`, `lineWidth`, `opacity` and
rotation. Opacity ranges from 0 to 1. `polygon`/`freehand` require absolute page
`points: [{"x": 10, "y": 20}, ...]`; polygons need at least three points. Lines
default to the diagonal of their specified box.

## JSON files and exact PDF roundtrips

Write the creation arguments as UTF-8 JSON, then call:

```json
{"name":"pdf_create","arguments":{"jsonPath":"D:\\Documents\\input.json","outputPath":"D:\\Documents\\result.pdf"}}
```

The file can be a raw arguments object or a `{"name":"pdf_create","arguments":{...}}`
envelope. Do not put a whole JSON-RPC `tools/call` message in the file. Only
`outputPath` and `overwrite` may accompany `jsonPath`; they override the file's
output options. Nested `jsonPath` references are rejected.

For an exact copy of an existing PDF:

1. Call `pdf_to_json` with the PDF `inputPath` and a `.json` `outputPath`.
2. Call `pdf_create` with that archive's `jsonPath` and a new PDF `outputPath`.
3. The restored file has the same bytes and SHA-256 as the original.

The archive's format is `pdf-simple-editor/archive-v1`. It stores `pdfBase64`,
`sha256`, and a readable `sourceInfo` inspection snapshot. Do not manually change
the binary/checksum fields. `sourceInfo` is read-only and does not drive drawing;
changing it does not edit the PDF. Use the PDF editing tools, or author new
`text`/`content`/`pages` JSON, when making changes. Unknown/unsupported graphics
can have an inspection error while their original bytes remain preserved.

CLI equivalents:

```powershell
PDFSimpleEditor.exe --create input.json --output result.pdf
PDFSimpleEditor.exe input.json --output result.pdf
PDFSimpleEditor.exe --to-json original.pdf --output archive.json
PDFSimpleEditor.exe --create archive.json --output restored.pdf
```

Without `--output`, creation uses `outputPath` in the JSON, or the input filename
with `.pdf`; export defaults to the input filename with `.json`. Existing files
are protected unless `overwrite: true` / `--overwrite` is explicit. Invalid input
does not replace the destination. Verify text using `pdf_read`, object geometry
using `pdf_inspect`, and visual output using `pdf_to_images`.
