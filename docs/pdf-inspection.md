# Finding and inspecting editable objects

## Reading pages with `pdf_read`

Pass an absolute `inputPath` and either a 1-based `page` (default 1) or
`pages` with a range such as `1,3,5-8` or `all`.

```json
{
  "inputPath": "C:\\Documents\\math.pdf",
  "pages": "1-3",
  "mode": "structured"
}
```

| Mode | Page output |
| --- | --- |
| `text` (default) | Normalized Unicode `text` with known HYhwpEQ mappings applied. |
| `structured` | Normalized `text`, positioned `spans`, and `equations` with inferred LaTeX and bounds. |
| `raw` | Original decoded `text` in content-stream order. Preserves PUA characters and original whitespace without inserting line breaks or spaces. |
| `diagnostic` | Structured output plus `rawText`, source PDF bytes and font IDs on spans, and per-font mappings with code points, PUA code points, available ToUnicode values, and applied repairs. |

Page entries include the page number, size, rotation and `hasText`. Normalized
text uses `layout: "auto"` by default and includes `columns` when a two-column
layout is detected. Use `single` for line order or `two-column` to request a
split. Raw text always follows content-stream order. Spans follow content-stream
order and their bounds are PDF points from the top-left of the displayed CropBox.

For math PDFs, use `structured`. LaTeX is inferred from recognized glyphs
and geometry, including nearby scripts and limits and stacked terms around a
fraction rule. An equation with an unmapped glyph or unresolved fraction rule
returns `latex: null` and a corresponding `status`. Compare complex equations against the page. Saved OCR
layers are read as PDF text. For pages with an image and no text layer, use
`pdf_vlm_ocr`.

## Finding text

Call `pdf_find_text` with an absolute `inputPath` and a nonempty literal `query`.
It searches editable body text on every page, with ordinal case-insensitive
comparison by default. Set `caseSensitive: true` for exact case.
`maxResults` defaults to 100 (range 1–10000).

```json
{
  "inputPath": "C:\\Documents\\source.pdf",
  "query": "Text to change",
  "caseSensitive": false,
  "maxResults": 20
}
```

Each entry in `matches` contains an `objectId`, 1-based `page`, full block `text`,
bounding box (`x`, `y`, `width`, `height`) and the first occurrence's
`matchIndex`/`matchLength` in UTF-16 code units. There is one entry per matching
editable text block, in page/extraction order. Separate blocks are not joined;
Image recognition is not performed during search; existing OCR text layers are
searched. Annotation contents and form values are not searched. For image-only
pages use `pdf_vlm_ocr` and `pdf_vlm_ocr_apply` before searching.

`matchedObjectCount`, `returnedCount` and `truncated` describe the result limit.
All pages are searched even when returned results are capped. Pass the returned
ID and page directly to `pdf_edit_text`, or use IDs with `pdf_batch` or
`pdf_delete_objects`. IDs remain valid only for the returned `revision`.
Use `expectedRevision` to reject changed source files.

## Replacing part of a text block

Pass `findText` and `replaceText` to `pdf_edit_text` for a literal substring
replacement. The first match is replaced by default. Set `replaceAll` to `true`
for every non-overlapping match in the selected block, and `caseSensitive` to
`true` to require exact case. An empty `replaceText` removes the match. A missing
match returns an error. Use `text` by itself for whole-block replacement.

```json
{
  "inputPath": "C:\\Documents\\source.pdf",
  "outputPath": "C:\\Documents\\edited.pdf",
  "page": 2,
  "objectId": "<objectId from pdf_find_text>",
  "findText": "Draft",
  "replaceText": "Final"
}
```

To update multiple blocks, use `pdf_find_text` to get their IDs and submit one
`pdf_edit_text` operation per block in `pdf_batch`.

## Inspecting objects on one page

For focused inspection, `pdf_inspect` accepts these optional arguments:

- `kind`: `all` (default), `text`, or `graphics`. Graphics include vectors,
  images and annotations. Text means editable body text.
- `region`: a rectangle in PDF points from the displayed CropBox's top-left,
  including page rotation. Objects whose bounding boxes intersect the region,
  including its edges, are returned. Width and height must be at least 0.001.
- `maxObjects`: 1–10000; applies after kind/region filtering. Omit for no limit.
  `matchedObjectCount`, `returnedCount` and `truncated` describe the object list.

```json
{
  "inputPath": "C:\\Documents\\source.pdf",
  "page": 2,
  "kind": "text",
  "region": { "x": 0, "y": 0, "width": 300, "height": 200 },
  "maxObjects": 10
}
```

Filtered objects retain the IDs from a full inspection. Form fields are returned
separately only with `kind: "all"`; region filters also apply to them, while
`maxObjects` limits only the `objects` array. Omitting all filters retains the
full inspection behavior.

`pdf_set_form_fields` accepts an optional `fontFamily` for text fields. Supply
an installed family name or an absolute `.ttf`, `.otf`, or `.ttc` path. The
selected font must support the full value and allow PDF embedding. Omit
`fontFamily` to reuse the field's font when it covers the value or select a
compatible font automatically. Checkbox values use the inspected `onValue` or
`Off`.

Run `python tests/McpInspection/run.py` after a Release/x64 build for headless
MCP integration checks. The script accepts an optional executable path.
