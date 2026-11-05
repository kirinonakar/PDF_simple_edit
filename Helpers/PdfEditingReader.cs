using iText.Kernel.Pdf;
using System;
using System.Diagnostics;
using System.IO;

namespace PDF_simple_edit.Helpers;

/// <summary>Allows editing readable pages when only the optional Info dictionary is corrupt.</summary>
internal sealed class PdfEditingReader(Stream input) : PdfReader(input)
{
    protected override void ReadPdf()
    {
        base.ReadPdf();
        // Stamping opens document Info eagerly, unlike page extraction and the
        // page-only preview. A broken metadata reference must not block edits to
        // otherwise readable pages. Leave all page/catalog errors untouched.
        try
        {
            if (trailer.ContainsKey(PdfName.Info) && trailer.GetAsDictionary(PdfName.Info) == null)
                trailer.Remove(PdfName.Info);
        }
        catch (Exception error) when (error is iText.IO.Exceptions.IOException or iText.Kernel.Exceptions.PdfException)
        {
            Debug.WriteLine($"Ignoring unreadable PDF document Info: {error}");
            trailer.Remove(PdfName.Info);
        }
    }
}
