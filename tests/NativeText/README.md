# PDF text editing regression checks

Run from the repository root on Windows with .NET 10:

```powershell
dotnet run --project tests/NativeText -- "D:/ASUNA/test/3D knee.pdf"
python tests/NativeText/verify_windows_rendering.py
python tests/NativeText/verify_rendering.py
```

The Python checks require Pillow, pypdf and Poppler (`pdftoppm`). The supplied attachment is a required fixture; it is never overwritten. PDFs and page images are produced in `tmp/pdfs`, and review examples in `output/pdf`.

The C# runner compiles the production text engines. It checks glyph identity and positions, no-op bytes, deletion on all 12 attachment pages, new glyph advances, repeated typing after reopening, embedded subset coverage, line overflow, collisions with adjacent text, mixed fonts, shared Forms, rotated CropBoxes, text state and unchanged streams. Legacy extraction, removal, detached background preparation, redraw and mode preference persistence are also exercised.

`WindowsRenderingProbe` calls the same Windows.Data.Pdf rendering API and options as the app, without operating a window. `verify_windows_rendering.py` checks that each expected glyph actually has visible ink at its location, and that pixels outside the edited line remain identical. This is essential: the previous per-glyph `q/cm/Tj/Q` output passed iText coordinate and Poppler image checks but collapsed glyphs in Windows.Data.Pdf. The engine now uses explicit `TJ` cursor adjustments and `Ts` baseline offsets, restoring the original text advance and rise after each replaced operand.

The suite does not automate the WinUI window, focus, mouse capture or IME interaction. It validates the PDF model, saving and actual rendering backend.
