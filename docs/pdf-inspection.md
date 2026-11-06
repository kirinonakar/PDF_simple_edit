# Finding and inspecting editable objects

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
OCR, annotation contents and form values are not searched.

`matchedObjectCount`, `returnedCount` and `truncated` describe the result limit.
All pages are searched even when returned results are capped. Pass the returned
ID and page directly to `pdf_edit_text`, or use IDs with `pdf_batch` or
`pdf_delete_objects`. IDs remain valid only for the returned `revision`.
`pdf_edit_text` edits the entire block, so preserve any surrounding text when
replacing only a substring. Use `expectedRevision` to reject changed source files.

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

Run `python tests/McpInspection/run.py` after a Release/x64 build for headless
MCP integration checks. The script accepts an optional executable path.
