using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>
/// Shared forms cannot be edited through their internal paths: that would change
/// every placement. Expose each page-level Do instead, with its own placement bounds.
/// </summary>
internal static class PdfRepeatedFormExtractor
{
    public sealed record Result(List<PdfPageContent> Contents, HashSet<int> SharedStreams);

    public static Result Extract(PdfPage page)
    {
        var placements = new List<(PdfStream Form, PdfPageContent Content)>();
        var occurrences = new Dictionary<PdfStream, int>();
        var ctm = PdfAffineTransform.Identity;
        var stack = new Stack<PdfAffineTransform>();
        var resources = page.GetResources().GetResource(PdfName.XObject);
        if (resources == null) return new(new(), new());
        var crop = page.GetCropBox();
        double pageHeight = page.GetPageSize().GetHeight();
        for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
        {
            var stream = page.GetContentStream(streamIndex);
            var parser = new PdfCanvasParser(new PdfTokenizer(new RandomAccessFileOrArray(
                new RandomAccessSourceFactory().CreateSource(stream.GetBytes()))));
            int doIndex = -1;
            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation.Count == 0) break;
                string name = operation[^1].ToString()!;
                if (name == "q") stack.Push(ctm);
                else if (name == "Q") ctm = stack.Count > 0 ? stack.Pop() : PdfAffineTransform.Identity;
                else if (name == "cm" && operation.Count == 7)
                    ctm = ctm.After(new(
                        Number(operation[0]), Number(operation[1]), Number(operation[2]),
                        Number(operation[3]), Number(operation[4]), Number(operation[5])));
                else if (name == "Do")
                {
                    doIndex++;
                    if (operation[0] is not PdfName resourceName ||
                        resources.GetAsStream(resourceName) is not { } form ||
                        !PdfName.Form.Equals(form.GetAsName(PdfName.Subtype)) ||
                        form.GetAsArray(PdfName.BBox) is not { } box || box.Size() != 4) continue;
                    occurrences[form] = occurrences.GetValueOrDefault(form) + 1;
                    var formMatrix = form.GetAsArray(PdfName.Matrix);
                    var matrix = formMatrix?.Size() == 6 ? new PdfAffineTransform(
                        Number(formMatrix.Get(0)), Number(formMatrix.Get(1)), Number(formMatrix.Get(2)),
                        Number(formMatrix.Get(3)), Number(formMatrix.Get(4)), Number(formMatrix.Get(5))) : PdfAffineTransform.Identity;
                    double x = Number(box.Get(0)), y = Number(box.Get(1));
                    var bounds = ctm.After(matrix).Map(new PdfTextBox(x, y, Number(box.Get(2)) - x, Number(box.Get(3)) - y));
                    // A form may extend past the page edge. A marquee drawn on the
                    // visible page must be able to enclose its visible bounds.
                    double left = Math.Max(crop.GetLeft(), bounds.X), bottom = Math.Max(crop.GetBottom(), bounds.Y);
                    double right = Math.Min(crop.GetRight(), bounds.Right), top = Math.Min(crop.GetTop(), bounds.Bottom);
                    if (right <= left || top <= bottom) continue;
                    var uiBounds = new PdfTextBox(left, pageHeight - top, right - left, top - bottom);
                    int streamNumber = stream.GetIndirectReference()?.GetObjNumber() ?? -1;
                    var target = new PdfGraphicOperationTarget
                    {
                        StreamIndex = streamIndex, StreamObjectNumber = streamNumber, OperationIndex = doIndex,
                        IsImageOperation = true, Ctm = ctm, Bounds = uiBounds, HitBounds = new() { uiBounds }
                    };
                    placements.Add((form, new PdfPageContent
                    {
                        Type = PageContentType.Image, X = uiBounds.X, Y = uiBounds.Y, Width = uiBounds.Width, Height = uiBounds.Height,
                        OriginalPdfX = left, OriginalPdfY = bottom, GraphicCtm = ctm,
                        ImageId = $"form:{resourceName.GetValue()}:{streamNumber}:{doIndex}",
                        ContentStreamIndex = streamIndex, ContentStreamObjectNumber = streamNumber, OperationIndex = doIndex,
                        GraphicOperations = new() { target }, GraphicHitBounds = new() { uiBounds }
                    }));
                }
            }
        }
        var document = page.GetDocument();
        bool SharedOnAnotherPage(PdfStream form)
        {
            var reference = form.GetIndirectReference();
            if (reference == null) return false;
            for (int i = 1; i <= document.GetNumberOfPages(); i++)
            {
                var otherPage = document.GetPage(i);
                if (otherPage.GetPdfObject().Equals(page.GetPdfObject())) continue;
                var others = otherPage.GetResources().GetResource(PdfName.XObject);
                if (others != null && others.KeySet().Any(name =>
                    reference.Equals(others.GetAsStream(name)?.GetIndirectReference()))) return true;
            }
            return false;
        }
        var shared = occurrences.Keys.Where(form => occurrences[form] > 1 || SharedOnAnotherPage(form)).ToHashSet();
        var sharedStreams = new HashSet<int>();
        void AddStreams(PdfStream form)
        {
            int number = form.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (number <= 0 || !sharedStreams.Add(number)) return;
            var children = form.GetAsDictionary(PdfName.Resources)?.GetAsDictionary(PdfName.XObject);
            if (children == null) return;
            foreach (var name in children.KeySet())
                if (children.GetAsStream(name) is { } child && PdfName.Form.Equals(child.GetAsName(PdfName.Subtype)))
                    AddStreams(child);
        }
        foreach (var form in shared) AddStreams(form);
        return new(placements.Where(p => shared.Contains(p.Form)).Select(p => p.Content).ToList(), sharedStreams);
    }

    private static double Number(PdfObject value) => ((PdfNumber)value).DoubleValue();
}
