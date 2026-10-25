using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Helpers;

/// <summary>Null pages means an unknown/all-page content change; empty means no raster changes.</summary>
public sealed class PdfDocumentChange : EventArgs
{
    public IReadOnlyList<int>? Pages { get; }
    public bool StructureChanged { get; }

    public PdfDocumentChange(IEnumerable<int>? pages = null, bool structureChanged = false)
    {
        Pages = pages?.Distinct().Where(p => p >= 0).ToArray();
        StructureChanged = structureChanged;
    }

    public static PdfDocumentChange Page(int page) => new(new[] { page });
}
