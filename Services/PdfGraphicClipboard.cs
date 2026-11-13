using iText.IO.Source;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using iText.Kernel.Pdf.Xobject;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services;

internal sealed record PdfGraphicClipboardData(byte[] Pdf, PdfTextBox[] Bounds);

internal static class PdfGraphicClipboard
{
    internal static readonly PdfName PastedGraphic = new("PSEClipboardGraphic");

    public static PdfGraphicClipboardData Copy(byte[] bytes, int pageIndex, IReadOnlyList<PdfAnnotation> graphics)
    {
        using var source = new PdfDocument(new PdfReader(new MemoryStream(bytes, writable: false)));
        var page = source.GetPage(pageIndex + 1);
        var display = PdfPageCoordinates.ToDisplay(page);
        var streams = Enumerable.Range(0, page.GetContentStreamCount()).Select(page.GetContentStream).ToArray();
        var bounds = graphics.Select(a => new PdfTextBox(a.X, a.Y, a.Width, a.Height)).ToArray();
        using var buffer = new MemoryStream();
        using (var clipboard = new PdfDocument(new PdfWriter(buffer)))
        {
            var storage = clipboard.AddNewPage();
            for (int i = 0; i < graphics.Count; i++)
            {
                var graphic = graphics[i];
                var box = bounds[i];
                var targets = graphic.GraphicOperations.Count > 0 ? graphic.GraphicOperations : new()
                {
                    new() { StreamIndex = graphic.ContentStreamIndex, StreamObjectNumber = graphic.ContentStreamObjectNumber,
                        OperationIndex = graphic.OperationIndex, IsImageOperation = true }
                };
                var placement = new PdfAffineTransform(1, 0, 0, -1, -box.X, box.Bottom).After(display);
                var form = new PdfFormXObject(new Rectangle((float)box.Width, (float)box.Height));
                var copy = new GraphicCopy(clipboard, targets);
                var content = copy.CopyStreams(streams, page.GetResources(), placement, pageContent: true, new());
                if (content.Count != targets.Count)
                    throw new InvalidOperationException(LocalizationService.Get("TheGraphicsChangedSelectThemAgain"));
                form.GetPdfObject().SetData(content.Bytes);
                form.GetPdfObject().Put(PdfName.Resources, content.Resources);
                storage.GetResources().AddForm(form, ResourceName(i));
            }
        }
        return new(buffer.ToArray(), bounds);
    }

    public static (byte[] Bytes, List<PdfPageContent> Contents) Paste(byte[] bytes, int pageIndex, PdfGraphicClipboardData data)
    {
        using var clipboard = new PdfDocument(new PdfReader(new MemoryStream(data.Pdf, writable: false)));
        var forms = clipboard.GetPage(1).GetResources().GetResource(PdfName.XObject);
        using var output = new MemoryStream();
        var pasted = new List<PdfPageContent>();
        using (var document = new PdfDocument(new PdfEditingReader(new MemoryStream(bytes, writable: false)), new PdfWriter(output)))
        {
            var page = document.GetPage(pageIndex + 1);
            var display = PdfPageCoordinates.ToDisplay(page);
            var pageBox = page.GetCropBox();
            var pageBounds = display.Map(new PdfTextBox(pageBox.GetX(), pageBox.GetY(), pageBox.GetWidth(), pageBox.GetHeight()));
            // Preserve the original streams and restore the page's initial
            // graphics state before drawing the pasted placements.
            page.NewContentStreamBefore().SetData("q\n"u8.ToArray());
            var stream = page.NewContentStreamAfter();
            var canvas = new PdfCanvas(stream, page.GetResources(), document).WriteLiteral("Q\n");
            int streamIndex = page.GetContentStreamCount() - 1;
            int streamNumber = stream.GetIndirectReference().GetObjNumber();
            for (int i = 0; i < data.Bounds.Length; i++)
            {
                var source = data.Bounds[i];
                if (!double.IsFinite(source.X) || !double.IsFinite(source.Y) ||
                    !double.IsFinite(source.Width) || !double.IsFinite(source.Height) ||
                    source.Width <= 0 || source.Height <= 0 ||
                    forms?.GetAsStream(ResourceName(i)) is not { } original ||
                    !PdfName.Form.Equals(original.GetAsName(PdfName.Subtype)))
                    throw new InvalidDataException();

                double scale = Math.Min(1, Math.Min(pageBounds.Width / source.Width, pageBounds.Height / source.Height));
                double width = source.Width * scale, height = source.Height * scale;
                double x = Math.Clamp(source.X + 12, 0, Math.Max(0, pageBounds.Width - width));
                double y = Math.Clamp(source.Y + 12, 0, Math.Max(0, pageBounds.Height - height));
                var bounds = new PdfTextBox(x, y, width, height);
                var transform = display.Inverse().After(new(scale, 0, 0, -scale, x, bounds.Bottom));
                var form = new PdfFormXObject((PdfStream)original.CopyTo(document, true));
                form.GetPdfObject().Put(PastedGraphic, PdfBoolean.TRUE);
                canvas.AddXObjectWithTransformationMatrix(form, (float)transform.A, (float)transform.B, (float)transform.C,
                    (float)transform.D, (float)transform.E, (float)transform.F);
                var name = page.GetResources().GetResourceName(form.GetPdfObject());
                var pdfBounds = display.Inverse().Map(bounds);
                pasted.Add(new PdfPageContent
                {
                    Type = PageContentType.Image, X = x, Y = y, Width = width, Height = height,
                    OriginalPdfX = pdfBounds.X, OriginalPdfY = pdfBounds.Y,
                    ContentStreamIndex = streamIndex, ContentStreamObjectNumber = streamNumber, OperationIndex = i,
                    ImageId = $"form:{name.GetValue()}:{streamNumber}:{i}", GraphicCtm = transform,
                    GraphicOperations = new()
                    {
                        new() { StreamIndex = streamIndex, StreamObjectNumber = streamNumber, OperationIndex = i,
                            IsImageOperation = true, Ctm = transform, Bounds = bounds, HitBounds = new() { bounds } }
                    },
                    GraphicHitBounds = new() { bounds }
                });
            }
        }
        return (output.ToArray(), pasted);
    }

    private static PdfName ResourceName(int index) => new($"Graphic{index}");

    private sealed class GraphicCopy(PdfDocument destination, IReadOnlyList<PdfGraphicOperationTarget> targets)
    {
        public sealed record Content(byte[] Bytes, PdfDictionary Resources, int Count);

        public Content CopyStreams(PdfStream[] streams, PdfResources resources,
            PdfAffineTransform? placement, bool pageContent, HashSet<PdfStream> activeForms)
        {
            var usedResources = new PdfDictionary();
            // Keep the state and clipping operators that affect the selection.
            // Unselected paint is omitted, including paint inside visited forms.
            var commands = new List<IList<PdfObject>>();
            var path = new List<int>();
            int depth = 0;
            bool clipping = false;
            int count = 0;
            int nextForm = 0;

            void Use(PdfName category, PdfName name, PdfObject? replacement = null)
            {
                var value = replacement ?? resources.GetResource(category)?.Get(name);
                if (value == null) return;
                var entries = usedResources.GetAsDictionary(category);
                if (entries == null)
                {
                    entries = new PdfDictionary();
                    usedResources.Put(category, entries);
                }
                if (entries.ContainsKey(name)) return;
                entries.Put(name, replacement ?? value.CopyTo(destination));
            }

            for (int streamIndex = 0; streamIndex < streams.Length; streamIndex++)
            {
                var stream = streams[streamIndex];
                int number = stream.GetIndirectReference()?.GetObjNumber() ?? -1;
                var streamTargets = targets.Where(t => t.StreamObjectNumber > 0
                    ? t.StreamObjectNumber == number
                    : pageContent && t.StreamIndex == streamIndex).ToList();
                var parser = new PdfCanvasParser(new PdfTokenizer(new RandomAccessFileOrArray(
                    new RandomAccessSourceFactory().CreateSource(stream.GetBytes()))), resources);
                int pathIndex = -1, doIndex = -1, shadingIndex = -1;
                while (parser.Parse(new List<PdfObject>()) is { Count: > 0 } command)
                {
                    string op = command[^1].ToString()!;
                    if (op is "m" or "l" or "c" or "v" or "y" or "re" or "h" or "W" or "W*")
                    {
                        path.Add(commands.Count);
                        commands.Add(command);
                        clipping |= op is "W" or "W*";
                    }
                    else if (op is "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n")
                    {
                        pathIndex++;
                        bool selected = streamTargets.Any(t => !t.IsImageOperation && !t.IsShadingOperation &&
                            !t.IsTextOperation && t.OperationIndex == pathIndex);
                        if (selected) count++;
                        if (selected || clipping)
                            commands.Add(selected ? command : new PdfObject[] { new PdfLiteral("n") });
                        else
                            foreach (int index in path) commands[index] = Array.Empty<PdfObject>();
                        path.Clear();
                        clipping = false;
                    }
                    else if (op == "Do")
                    {
                        doIndex++;
                        var name = (PdfName)command[0];
                        if (streamTargets.Any(t => t.IsImageOperation && t.OperationIndex == doIndex))
                        {
                            Use(PdfName.XObject, name);
                            commands.Add(command);
                            count++;
                        }
                        else if (resources.GetResource(PdfName.XObject)?.GetAsStream(name) is { } form &&
                            PdfName.Form.Equals(form.GetAsName(PdfName.Subtype)) && activeForms.Add(form))
                        {
                            var dictionary = form.GetAsDictionary(PdfName.Resources);
                            var child = CopyStreams(new[] { form }, dictionary == null ? resources : new PdfResources(dictionary),
                                null, pageContent: false, activeForms);
                            activeForms.Remove(form);
                            if (child.Count == 0) continue;
                            var copy = new PdfStream(child.Bytes);
                            foreach (var key in new[] { PdfName.BBox, PdfName.Matrix, PdfName.Group })
                                if (form.Get(key) is { } value) copy.Put(key, value.CopyTo(destination));
                            copy.Put(PdfName.Resources, child.Resources);
                            copy.Put(PdfName.Type, PdfName.XObject);
                            copy.Put(PdfName.Subtype, PdfName.Form);
                            copy.MakeIndirect(destination);
                            PdfName childName;
                            do { childName = new PdfName($"PSEClip{nextForm++}"); }
                            while (resources.GetResource(PdfName.XObject)?.ContainsKey(childName) == true);
                            Use(PdfName.XObject, childName, copy);
                            commands.Add(new PdfObject[] { childName, command[^1] });
                            count += child.Count;
                        }
                    }
                    else if (op == "sh")
                    {
                        shadingIndex++;
                        if (!streamTargets.Any(t => t.IsShadingOperation && t.OperationIndex == shadingIndex)) continue;
                        Use(PdfName.Shading, (PdfName)command[0]);
                        commands.Add(command);
                        count++;
                    }
                    else if (op == "q")
                    {
                        depth++;
                        commands.Add(command);
                    }
                    else if (op == "Q")
                    {
                        if (depth == 0) continue;
                        depth--;
                        commands.Add(command);
                    }
                    else if (op is "cm" or "w" or "J" or "j" or "M" or "d" or "ri" or "i" or
                        "gs" or "CS" or "cs" or "SC" or "SCN" or "sc" or "scn" or "G" or "g" or "RG" or "rg" or "K" or "k")
                    {
                        if (op == "gs") Use(PdfName.ExtGState, (PdfName)command[0]);
                        if (op is "CS" or "cs") Use(PdfName.ColorSpace, (PdfName)command[0]);
                        if ((op is "SCN" or "scn") && command[^2] is PdfName pattern) Use(PdfName.Pattern, pattern);
                        commands.Add(command);
                    }
                }
            }
            if (count == 0) return new(Array.Empty<byte>(), usedResources, 0);
            foreach (int index in path) commands[index] = Array.Empty<PdfObject>();
            for (int i = 0; i < depth; i++) commands.Add(new PdfObject[] { new PdfLiteral("Q") });
            foreach (var name in new[] { PdfName.DefaultGray, PdfName.DefaultRGB, PdfName.DefaultCMYK })
                if (resources.GetResource(PdfName.ColorSpace)?.ContainsKey(name) == true) Use(PdfName.ColorSpace, name);

            using var buffer = new MemoryStream();
            if (placement is { } transform) buffer.Write(Encoding.ASCII.GetBytes(transform.Command));
            var output = new PdfOutputStream(buffer);
            foreach (var command in commands.Where(c => c.Count > 0))
            {
                foreach (var operand in command) output.Write(operand).WriteSpace();
                output.WriteNewLine();
            }
            return new(buffer.ToArray(), usedResources, count);
        }
    }
}
