using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using PDF_simple_edit.Models;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

// Extraction collects images, paths and shared forms separately. Restore their
// actual paint order so a click picks the visible foreground regardless of size.
internal static class PdfGraphicPaintOrder
{
    private readonly record struct Key(int ObjectNumber, int StreamIndex, int OperationIndex, string Kind);

    public static void Assign(PdfPage page, IEnumerable<PdfPageContent> contents)
    {
        var orders = new Dictionary<Key, int>();
        var active = new HashSet<PdfStream>();
        int order = 0;
        void Visit(PdfStream stream, PdfResources resources, int streamIndex)
        {
            if (!active.Add(stream)) return;
            try
            {
                int objectNumber = stream.GetIndirectReference()?.GetObjNumber() ?? -1;
                int pathIndex = -1, imageIndex = -1, textIndex = -1, shadingIndex = -1;
                var parser = new PdfCanvasParser(new PdfTokenizer(new RandomAccessFileOrArray(
                    new RandomAccessSourceFactory().CreateSource(stream.GetBytes()))));
                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation.Count == 0) break;
                    string name = operation[^1].ToString()!;
                    string kind;
                    int index;
                    switch (name)
                    {
                        case "S": case "s": case "f": case "F": case "f*":
                        case "B": case "B*": case "b": case "b*": case "n":
                            kind = "path"; index = ++pathIndex; break;
                        case "Do": kind = "image"; index = ++imageIndex; break;
                        case "Tj": case "TJ": case "'": case "\"":
                            kind = "text"; index = ++textIndex; break;
                        case "sh": kind = "shading"; index = ++shadingIndex; break;
                        default: continue;
                    }
                    orders[new(objectNumber, streamIndex, index, kind)] = order++;
                    if (name == "Do" && operation[0] is PdfName resourceName &&
                        resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName) is { } form &&
                        PdfName.Form.Equals(form.GetAsName(PdfName.Subtype)))
                    {
                        var dictionary = form.GetAsDictionary(PdfName.Resources);
                        Visit(form, dictionary == null ? resources : new PdfResources(dictionary), -1);
                    }
                }
            }
            finally { active.Remove(stream); }
        }

        for (int i = 0; i < page.GetContentStreamCount(); i++)
            Visit(page.GetContentStream(i), page.GetResources(), i);
        foreach (var content in contents.Where(c => c.Type == PageContentType.Image))
        {
            var keys = content.GraphicOperations.Count > 0
                ? content.GraphicOperations.Select(op => new Key(op.StreamObjectNumber, op.StreamIndex,
                    op.OperationIndex, op.IsImageOperation ? "image" : op.IsTextOperation ? "text" :
                    op.IsShadingOperation ? "shading" : "path"))
                : new[] { new Key(content.ContentStreamObjectNumber, content.ContentStreamIndex, content.OperationIndex, "image") };
            content.GraphicPaintOrder = keys.Select(key => orders.GetValueOrDefault(key, -1)).Max();
        }
    }
}
