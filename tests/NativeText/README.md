# PDF text editing regression checks

Run from the repository root on Windows with .NET 10:

```powershell
dotnet run --project tests/NativeText -- "D:/ASUNA/test/3D knee.pdf"
python tests/NativeText/verify_windows_rendering.py
python tests/NativeText/verify_paragraph_rendering.py
python tests/NativeText/verify_rendering.py
```

The Python checks require Pillow, pypdf and Poppler (`pdftoppm`). The supplied attachment is a required fixture; it is never overwritten. PDFs and page images are produced in `tmp/pdfs`, and review examples in `output/pdf`.

The C# runner compiles the production text engines. It checks glyph identity and positions, no-op bytes, deletion on all 12 attachment pages, new glyph advances, repeated typing after reopening, embedded subset coverage, paragraph wrapping and pull-up on deletion, narrow/interleaved columns, exact marquee glyph boundaries (including partial PDF operands), collisions with adjacent text, mixed fonts, shared Forms, rotated CropBoxes, text state and unchanged streams. Legacy extraction, removal, detached background preparation, redraw and mode preference persistence are also exercised.

`WindowsRenderingProbe` calls the same Windows.Data.Pdf rendering API and options as the app, without operating a window. `verify_windows_rendering.py` checks that each expected glyph actually has visible ink at its location, and that pixels outside the edited line remain identical. This is essential: the previous per-glyph `q/cm/Tj/Q` output passed iText coordinate and Poppler image checks but collapsed glyphs in Windows.Data.Pdf. The engine now uses explicit `TJ` cursor adjustments and `Ts` baseline offsets, restoring the original text advance and rise after each replaced operand.

The suite does not automate the WinUI window, focus, mouse capture or IME interaction. It validates the PDF model, saving and actual rendering backend.

Selection regression checks also cover both editing modes, page-sized vector
decoration exclusion, text selection through previously selected original images,
and retention of actual images and local vector figures. Run only those checks with
`dotnet run --project tests/NativeText -- "D:/ASUNA/3D knee.pdf" --selection-only`.

In original-preservation mode, click selects an inferred paragraph; drag selects only glyphs whose centers lie in the rectangle. Columns remain separate, including columns encoded inside one TJ operand. Explicit selection joins selected pieces within a column without including surrounding text. Double-click edits the selection; Alt+drag moves it. Single-line selections expand to the right as text is inserted, while multiline paragraph edits use original font advances for wrapping. Explicit newlines remain supported. Overflow into unselected text is rejected. The legacy mode remains available.
