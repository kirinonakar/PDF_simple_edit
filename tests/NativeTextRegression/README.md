# Native PDF text regression

Run on Windows from the repository root:

```powershell
dotnet run --project tests/NativeTextRegression -- "D:/ASUNA/test/AJCC9 NPca JKSR-87-12.pdf"
dotnet run --project tests/NativeTextRegression -- "D:/ASUNA/test/3D knee.pdf" --knee
```

The fixture is supplied externally and is never modified or committed. Results are written under `tmp/pdfs/native-text-regression`.

This regression uses Windows.Data.Pdf, the application's display engine. With incremental saving, all edited first-page renders were identical to the original even though iText and Poppler read the edits. It checks abstract deletion, backspace, replacement and marquee deletion against both rendered pixels and reopened text. Pixels outside the abstract and on the next page must remain unchanged. Text extraction alone cannot catch this failure.

Existing-glyph checks append all 41 distinct non-space single characters in the abstract and require the original font/code with no substitution. A one-letter marquee also reuses a character outside its selection. A missing Korean glyph still exercises fallback.

The knee fixture checks the Patients paragraph on page 2. Its justified word spaces must not be mistaken for column gutters: the selection must include `February 2005 and January` and `surgical intervention that pre-vented` in one 17-line paragraph. The same edit/render checks run against it, with artifacts under `tmp/pdfs/knee-text-regression`. A real collision with the Patients heading must still be rejected.
