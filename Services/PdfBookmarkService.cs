using iText.Kernel.Pdf;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using iText.Kernel.Pdf.Navigation;

namespace PDF_simple_edit.Services;

public sealed record PdfBookmark(string Title, int? PageIndex, List<PdfBookmark> Children)
{
    public int Depth { get; init; }
    public int[] IndexPath { get; init; } = Array.Empty<int>();
    public override string ToString() => Title;
}

public static class PdfBookmarkService
{
    public static void Add(PdfDocument document, int pageIndex, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException(nameof(title));
        if (pageIndex < 0 || pageIndex >= document.GetNumberOfPages()) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        document.GetOutlines(false).AddOutline(title.Trim()).AddDestination(PdfExplicitDestination.CreateFit(document.GetPage(pageIndex + 1)));
    }

    public static void Remove(PdfDocument document, PdfBookmark target)
    {
        var node = document.GetOutlines(false);
        foreach (int index in target.IndexPath)
        {
            var children = node?.GetAllChildren();
            if (children == null || index < 0 || index >= children.Count) throw new InvalidOperationException(LocalizationService.Get("BookmarkChangedSelectAgain"));
            node = children[index];
        }
        if (target.IndexPath.Length == 0 || node == null || node.GetTitle() != target.Title)
            throw new InvalidOperationException(LocalizationService.Get("BookmarkChangedSelectAgain"));
        node.RemoveOutline();
    }

    public static List<PdfBookmark> Read(byte[] bytes)
    {
        using var document = new PdfDocument(new PdfReader(new MemoryStream(bytes, writable: false)));
        var visited = new HashSet<PdfDictionary>();
        int? Resolve(PdfObject? destination)
        {
            for (int depth = 0; depth < 16; depth++)
            {
                if (destination is PdfIndirectReference reference) { destination = reference.GetRefersTo(); continue; }
                if (destination is PdfName name)
                {
                    destination = document.GetCatalog().GetNameTree(PdfName.Dests).GetEntry(name.GetValue()) ??
                        document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Dests)?.Get(name);
                    continue;
                }
                if (destination is PdfString text)
                { destination = document.GetCatalog().GetNameTree(PdfName.Dests).GetEntry(text); continue; }
                if (destination is PdfDictionary dictionary) { destination = dictionary.Get(PdfName.D); continue; }
                if (destination is not PdfArray array || array.Size() == 0) return null;
                int page = array.GetAsDictionary(0) is { } pageDictionary ? document.GetPageNumber(pageDictionary) - 1 :
                    array.GetAsNumber(0)?.IntValue() ?? -1;
                return page >= 0 && page < document.GetNumberOfPages() ? page : null;
            }
            return null;
        }
        List<PdfBookmark> ReadSiblings(PdfDictionary? node, int depth, int[] parentPath)
        {
            var result = new List<PdfBookmark>();
            int index = 0;
            while (node != null && depth < 64 && visited.Count < 10000 && visited.Add(node))
            {
                var action = node.GetAsDictionary(PdfName.A);
                var destination = node.Get(PdfName.Dest) ?? (PdfName.GoTo.Equals(action?.GetAsName(PdfName.S)) ? action?.Get(PdfName.D) : null);
                int[] path = parentPath.Append(index++).ToArray();
                result.Add(new(node.GetAsString(PdfName.Title)?.ToUnicodeString() ?? "", Resolve(destination),
                    ReadSiblings(node.GetAsDictionary(PdfName.First), depth + 1, path)) { Depth = depth, IndexPath = path });
                node = node.GetAsDictionary(PdfName.Next);
            }
            return result;
        }
        return ReadSiblings(document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Outlines)?.GetAsDictionary(PdfName.First), 0, Array.Empty<int>());
    }
}
