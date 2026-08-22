using iText.IO.Source;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using iText.PdfCleanup;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace PDF_simple_edit.Services
{
    internal sealed class PdfContentStreamEditor
    {
        private static bool IsPathPaintingOperator(string operatorName) =>
            operatorName is "S" or "s" or "f" or "F" or "f*" or
                "B" or "B*" or "b" or "b*" or "n";

        private sealed class TextOperationTarget
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
            public int TextOperandIndex { get; init; } = -1;
            public double? TextAdvanceAdjustment { get; init; }
            public bool RemoveWholeOperation { get; init; }
            public int TextRenderMode { get; init; }
        }

        private static bool IsTextShowingOperator(string operatorName)
        {
            return operatorName == "Tj" || operatorName == "TJ" || operatorName == "'" || operatorName == "\"";
        }

        private static void WriteOperation(PdfOutputStream output, IList<PdfObject> operation)
        {
            for (int i = 0; i < operation.Count; i++)
            {
                output.Write(operation[i]);
                if (i < operation.Count - 1)
                    output.WriteSpace();
            }

            output.WriteNewLine();
        }

        private static void WriteTextAdvance(PdfOutputStream output, double adjustment)
        {
            var advances = new PdfArray();
            advances.Add(new PdfNumber(adjustment));
            WriteOperation(output, new PdfObject[] { advances, new PdfLiteral("TJ") });
        }

        private static bool TryWritePartiallyRemovedTextOperation(
            PdfOutputStream output,
            IList<PdfObject> operation,
            string operatorName,
            IReadOnlyCollection<TextOperationTarget> targets)
        {
            if (targets.Any(target => target.TextOperandIndex < 0 ||
                !target.TextAdvanceAdjustment.HasValue ||
                !double.IsFinite(target.TextAdvanceAdjustment.Value)))
                return false;

            var replacements = targets
                .GroupBy(target => target.TextOperandIndex)
                .ToDictionary(group => group.Key, group => group.First().TextAdvanceAdjustment!.Value);

            if (operatorName == "TJ" && operation.Count > 1 && operation[0] is PdfArray sourceArray)
            {
                var rewrittenArray = new PdfArray();
                var replacedIndexes = new HashSet<int>();
                int stringIndex = 0;
                foreach (PdfObject element in sourceArray)
                {
                    if (element is PdfString && replacements.TryGetValue(stringIndex, out double adjustment))
                    {
                        rewrittenArray.Add(new PdfNumber(adjustment));
                        replacedIndexes.Add(stringIndex);
                    }
                    else
                    {
                        rewrittenArray.Add(element);
                    }

                    if (element is PdfString)
                        stringIndex++;
                }

                if (replacedIndexes.Count != replacements.Count)
                    return false;

                WriteOperation(output, new PdfObject[] { rewrittenArray, new PdfLiteral("TJ") });
                return true;
            }

            if (!replacements.TryGetValue(0, out double singleAdjustment) || replacements.Count != 1)
                return false;

            if (operatorName == "Tj")
            {
                WriteTextAdvance(output, singleAdjustment);
                return true;
            }

            if (operatorName == "'")
            {
                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                WriteTextAdvance(output, singleAdjustment);
                return true;
            }

            if (operatorName == "\"" && operation.Count >= 4)
            {
                WriteOperation(output, new PdfObject[] { operation[0], new PdfLiteral("Tw") });
                WriteOperation(output, new PdfObject[] { operation[1], new PdfLiteral("Tc") });
                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                WriteTextAdvance(output, singleAdjustment);
                return true;
            }

            return false;
        }

        private static bool TryRewriteContentStreamWithoutText(
            byte[] contentBytes,
            IReadOnlyCollection<TextOperationTarget> targets,
            out byte[]? rewrittenBytes)
        {
            rewrittenBytes = null;
            if (targets.Count == 0)
                return false;

            var targetsByOperation = targets
                .GroupBy(target => target.OperationIndex)
                .ToDictionary(group => group.Key, group => (IReadOnlyCollection<TextOperationTarget>)group.ToList());
            var handledOperations = new HashSet<int>();
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new iText.Kernel.Pdf.Canvas.Parser.Util.PdfCanvasParser(tokenizer);

            using var stream = new MemoryStream();
            var output = new PdfOutputStream(stream);
            bool modified = false;
            int textOperationIndex = -1;

            while (true)
            {
                var parsedOperation = parser.Parse(new List<PdfObject>());
                if (parsedOperation == null || parsedOperation.Count == 0)
                    break;

                if (parsedOperation[^1] is PdfLiteral literal && IsTextShowingOperator(literal.ToString()))
                {
                    textOperationIndex++;
                    if (targetsByOperation.TryGetValue(textOperationIndex, out IReadOnlyCollection<TextOperationTarget>? operationTargets))
                    {
                        string operatorName = literal.ToString();
                        bool removeWholeOperation = operationTargets.Any(target => target.RemoveWholeOperation);
                        if (!removeWholeOperation)
                        {
                            if (!TryWritePartiallyRemovedTextOperation(
                                output, parsedOperation, operatorName, operationTargets))
                                return false;
                        }
                        else
                        {
                            if (operatorName == "'")
                            {
                                // ' is equivalent to T* followed by Tj. Preserve only the line move.
                                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                            }
                            else if (operatorName == "\"" && parsedOperation.Count >= 4)
                            {
                                // " sets word/character spacing, moves to the next line, then shows text.
                                WriteOperation(output, new PdfObject[] { parsedOperation[0], new PdfLiteral("Tw") });
                                WriteOperation(output, new PdfObject[] { parsedOperation[1], new PdfLiteral("Tc") });
                                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                            }
                        }

                        handledOperations.Add(textOperationIndex);
                        modified = true;
                        continue;
                    }
                }

                WriteOperation(output, parsedOperation);
            }

            if (!modified || handledOperations.Count != targetsByOperation.Count)
                return false;

            rewrittenBytes = stream.ToArray();
            return true;
        }

        private static int CountTextShowingOperations(byte[] contentBytes)
        {
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new iText.Kernel.Pdf.Canvas.Parser.Util.PdfCanvasParser(tokenizer);
            int count = 0;

            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation == null || operation.Count == 0)
                    break;
                if (operation[^1] is PdfLiteral literal &&
                    IsTextShowingOperator(literal.ToString()))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool TryRewriteCombinedPageContentStreams(
            PdfPage page,
            IReadOnlyCollection<TextOperationTarget> targets,
            out List<(PdfStream Stream, byte[] Bytes)> rewrites)
        {
            rewrites = new List<(PdfStream Stream, byte[] Bytes)>();
            var pageStreams = new List<PdfStream>();
            var operationOffsetsByObject = new Dictionary<int, int>();
            var operationOffsetsByIndex = new Dictionary<int, int>();
            using var combined = new MemoryStream();
            int operationOffset = 0;

            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    return false;

                byte[] bytes = contentStream.GetBytes();
                pageStreams.Add(contentStream);
                operationOffsetsByIndex[streamIndex] = operationOffset;
                int objectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
                if (objectNumber > 0)
                    operationOffsetsByObject[objectNumber] = operationOffset;

                operationOffset += CountTextShowingOperations(bytes);
                combined.Write(bytes, 0, bytes.Length);
                combined.WriteByte((byte)'\n');
            }

            var combinedTargets = new List<TextOperationTarget>(targets.Count);
            foreach (TextOperationTarget target in targets)
            {
                int streamOperationOffset;
                if (target.StreamObjectNumber > 0)
                {
                    if (!operationOffsetsByObject.TryGetValue(
                        target.StreamObjectNumber,
                        out streamOperationOffset))
                    {
                        return false;
                    }
                }
                else if (!operationOffsetsByIndex.TryGetValue(
                    target.StreamIndex,
                    out streamOperationOffset))
                {
                    return false;
                }

                combinedTargets.Add(new TextOperationTarget
                {
                    StreamIndex = 0,
                    StreamObjectNumber = pageStreams[0].GetIndirectReference()?.GetObjNumber() ?? -1,
                    OperationIndex = streamOperationOffset + target.OperationIndex,
                    TextOperandIndex = target.TextOperandIndex,
                    TextAdvanceAdjustment = target.TextAdvanceAdjustment,
                    RemoveWholeOperation = target.RemoveWholeOperation,
                    TextRenderMode = target.TextRenderMode
                });
            }

            if (!TryRewriteContentStreamWithoutText(
                combined.ToArray(),
                combinedTargets,
                out byte[]? rewrittenBytes) ||
                rewrittenBytes == null)
            {
                return false;
            }

            for (int i = 0; i < pageStreams.Count; i++)
            {
                rewrites.Add((
                    pageStreams[i],
                    i == 0 ? rewrittenBytes : Array.Empty<byte>()));
            }

            return true;
        }

        private static bool TryRemoveTextOperations(PdfDocument doc, int pageIndex, IEnumerable<TextOperationTarget> targets)
        {
            var validTargets = targets
                .Where(target => target.OperationIndex >= 0 &&
                    (target.StreamObjectNumber > 0 || target.StreamIndex >= 0))
                .ToList();

            if (validTargets.Count == 0)
                return false;

            var page = doc.GetPage(pageIndex + 1);
            var rewrites = new List<(PdfStream Stream, byte[] Bytes)>();

            // Page content streams are one logical content sequence. Some PDFs
            // split a TJ array and its TJ operator across adjacent stream objects.
            // Rewriting those objects independently loses the operand array and
            // makes a valid partial deletion fail. Join the page streams for
            // parsing, translate local operation indexes to the joined sequence,
            // then keep the rewritten sequence in the first page stream.
            var pageStreamObjectNumbers = Enumerable.Range(0, page.GetContentStreamCount())
                .Select(index => page.GetContentStream(index)?.GetIndirectReference()?.GetObjNumber() ?? -1)
                .Where(number => number > 0)
                .ToHashSet();
            bool allTargetsArePageStreams = validTargets.All(target =>
                target.StreamObjectNumber > 0
                    ? pageStreamObjectNumbers.Contains(target.StreamObjectNumber)
                    : target.StreamIndex >= 0 && target.StreamIndex < page.GetContentStreamCount());
            if (allTargetsArePageStreams)
            {
                if (!TryRewriteCombinedPageContentStreams(page, validTargets, out rewrites))
                    return false;

                foreach ((PdfStream stream, byte[] bytes) in rewrites)
                    stream.SetData(bytes);
                return rewrites.Count > 0;
            }

            foreach (var group in validTargets
                .Where(target => target.StreamObjectNumber > 0)
                .GroupBy(target => target.StreamObjectNumber))
            {
                if (doc.GetPdfObject(group.Key) is not PdfStream contentStream)
                    return false;

                if (!TryRewriteContentStreamWithoutText(
                    contentStream.GetBytes(), group.ToList(), out byte[]? rewrittenBytes) ||
                    rewrittenBytes == null)
                    return false;

                rewrites.Add((contentStream, rewrittenBytes));
            }

            foreach (var group in validTargets
                .Where(target => target.StreamObjectNumber <= 0)
                .GroupBy(target => target.StreamIndex))
            {
                if (group.Key >= page.GetContentStreamCount())
                    return false;

                var contentStream = page.GetContentStream(group.Key);
                if (contentStream == null)
                    return false;

                if (!TryRewriteContentStreamWithoutText(
                    contentStream.GetBytes(), group.ToList(), out byte[]? rewrittenBytes) ||
                    rewrittenBytes == null)
                    return false;

                rewrites.Add((contentStream, rewrittenBytes));
            }

            foreach ((PdfStream stream, byte[] bytes) in rewrites)
                stream.SetData(bytes);

            return rewrites.Count > 0;
        }

        private static bool TryRewriteContentStreamWithoutGraphics(
            byte[] contentBytes,
            IReadOnlyCollection<int> imageOperationIndexes,
            IReadOnlyCollection<int> pathOperationIndexes,
            IReadOnlyCollection<int> textOperationIndexes,
            IReadOnlyCollection<int> shadingOperationIndexes,
            out byte[]? rewrittenBytes)
        {
            rewrittenBytes = null;
            if (imageOperationIndexes.Count == 0 &&
                pathOperationIndexes.Count == 0 &&
                textOperationIndexes.Count == 0 &&
                shadingOperationIndexes.Count == 0)
                return false;

            var imageTargets = imageOperationIndexes.ToHashSet();
            var pathTargets = pathOperationIndexes.ToHashSet();
            var textTargets = textOperationIndexes.ToHashSet();
            var shadingTargets = shadingOperationIndexes.ToHashSet();
            var handledImages = new HashSet<int>();
            var handledPaths = new HashSet<int>();
            var handledText = new HashSet<int>();
            var handledShadings = new HashSet<int>();
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new PdfCanvasParser(tokenizer);
            using var stream = new MemoryStream();
            var output = new PdfOutputStream(stream);
            int doOperationIndex = -1;
            int pathOperationIndex = -1;
            int textOperationIndex = -1;
            int shadingOperationIndex = -1;

            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation == null || operation.Count == 0)
                    break;

                if (operation[^1] is not PdfLiteral literal)
                {
                    WriteOperation(output, operation);
                    continue;
                }

                string operatorName = literal.ToString();
                if (operatorName == "Do")
                {
                    doOperationIndex++;
                    if (imageTargets.Contains(doOperationIndex))
                    {
                        handledImages.Add(doOperationIndex);
                        continue;
                    }
                }
                else if (IsPathPaintingOperator(operatorName))
                {
                    pathOperationIndex++;
                    if (pathTargets.Contains(pathOperationIndex))
                    {
                        handledPaths.Add(pathOperationIndex);
                        WriteOperation(output, new PdfObject[] { new PdfLiteral("n") });
                        continue;
                    }
                }
                else if (IsTextShowingOperator(operatorName))
                {
                    textOperationIndex++;
                    if (textTargets.Contains(textOperationIndex))
                    {
                        handledText.Add(textOperationIndex);
                        if (operatorName == "'")
                        {
                            WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                        }
                        else if (operatorName == "\"" && operation.Count >= 4)
                        {
                            WriteOperation(output, new PdfObject[] { operation[0], new PdfLiteral("Tw") });
                            WriteOperation(output, new PdfObject[] { operation[1], new PdfLiteral("Tc") });
                            WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                        }
                        continue;
                    }
                }
                else if (operatorName == "sh")
                {
                    shadingOperationIndex++;
                    if (shadingTargets.Contains(shadingOperationIndex))
                    {
                        handledShadings.Add(shadingOperationIndex);
                        continue;
                    }
                }

                WriteOperation(output, operation);
            }

            if (handledImages.Count != imageTargets.Count ||
                handledPaths.Count != pathTargets.Count ||
                handledText.Count != textTargets.Count ||
                handledShadings.Count != shadingTargets.Count)
                return false;

            rewrittenBytes = stream.ToArray();
            return true;
        }

        private readonly record struct GraphicOperationCounts(
            int Images,
            int Paths,
            int Text,
            int Shadings);

        private static GraphicOperationCounts CountGraphicOperations(byte[] contentBytes)
        {
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new PdfCanvasParser(tokenizer);
            int images = 0;
            int paths = 0;
            int text = 0;
            int shadings = 0;
            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation == null || operation.Count == 0)
                    break;
                if (operation[^1] is not PdfLiteral literal)
                    continue;
                string operatorName = literal.ToString();
                if (operatorName == "Do") images++;
                else if (IsPathPaintingOperator(operatorName)) paths++;
                else if (IsTextShowingOperator(operatorName)) text++;
                else if (operatorName == "sh") shadings++;
            }
            return new GraphicOperationCounts(images, paths, text, shadings);
        }

        private static bool TryRewriteCombinedPageContentStreamsWithoutGraphics(
            PdfPage page,
            IReadOnlyCollection<GraphicStreamRemoval> removals,
            out List<(PdfStream Stream, byte[] Bytes)> rewrites)
        {
            rewrites = new List<(PdfStream Stream, byte[] Bytes)>();
            var pageStreams = new List<PdfStream>();
            var offsetsByObject = new Dictionary<int, GraphicOperationCounts>();
            var offsetsByIndex = new Dictionary<int, GraphicOperationCounts>();
            using var combined = new MemoryStream();
            var offset = new GraphicOperationCounts(0, 0, 0, 0);
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    return false;
                byte[] bytes = contentStream.GetBytes();
                pageStreams.Add(contentStream);
                offsetsByIndex[streamIndex] = offset;
                int objectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
                if (objectNumber > 0)
                    offsetsByObject[objectNumber] = offset;
                GraphicOperationCounts count = CountGraphicOperations(bytes);
                offset = new GraphicOperationCounts(
                    offset.Images + count.Images,
                    offset.Paths + count.Paths,
                    offset.Text + count.Text,
                    offset.Shadings + count.Shadings);
                combined.Write(bytes, 0, bytes.Length);
                combined.WriteByte((byte)'\n');
            }

            var imageTargets = new HashSet<int>();
            var pathTargets = new HashSet<int>();
            var textTargets = new HashSet<int>();
            var shadingTargets = new HashSet<int>();
            foreach (GraphicStreamRemoval removal in removals)
            {
                GraphicOperationCounts streamOffset;
                if (removal.StreamObjectNumber > 0)
                {
                    if (!offsetsByObject.TryGetValue(removal.StreamObjectNumber, out streamOffset))
                        return false;
                }
                else if (!offsetsByIndex.TryGetValue(removal.StreamIndex, out streamOffset))
                {
                    return false;
                }
                imageTargets.UnionWith(removal.ImageOperationIndexes.Select(index => streamOffset.Images + index));
                pathTargets.UnionWith(removal.PathOperationIndexes.Select(index => streamOffset.Paths + index));
                textTargets.UnionWith(removal.TextOperationIndexes.Select(index => streamOffset.Text + index));
                shadingTargets.UnionWith(removal.ShadingOperationIndexes.Select(index => streamOffset.Shadings + index));
            }

            if (!TryRewriteContentStreamWithoutGraphics(
                combined.ToArray(), imageTargets, pathTargets, textTargets, shadingTargets,
                out byte[]? rewrittenBytes) || rewrittenBytes == null)
            {
                return false;
            }
            for (int index = 0; index < pageStreams.Count; index++)
                rewrites.Add((pageStreams[index], index == 0 ? rewrittenBytes : Array.Empty<byte>()));
            return true;
        }

        private sealed class GraphicStreamRemoval
        {
            public int StreamObjectNumber { get; init; }
            public int StreamIndex { get; init; }
            public HashSet<int> ImageOperationIndexes { get; } = new();
            public HashSet<int> PathOperationIndexes { get; } = new();
            public HashSet<int> TextOperationIndexes { get; } = new();
            public HashSet<int> ShadingOperationIndexes { get; } = new();
        }

        public bool RemoveTextOperation(
            PdfDocument document,
            int pageIndex,
            int streamIndex,
            int operationIndex,
            int textRenderMode)
        {
            return TryRemoveTextOperations(document, pageIndex, new[]
            {
                new TextOperationTarget
                {
                    StreamIndex = streamIndex,
                    OperationIndex = operationIndex,
                    RemoveWholeOperation = true,
                    TextRenderMode = textRenderMode
                }
            });
        }

        public bool RemoveTextAnnotations(
            PdfDocument document,
            int pageIndex,
            IEnumerable<PdfAnnotation> annotations)
        {
            var preciseTargets = annotations
                .SelectMany(annotation => annotation.TextFragments.Count > 0
                    ? annotation.TextFragments.Select(fragment => new TextOperationTarget
                    {
                        StreamIndex = fragment.ContentStreamIndex,
                        StreamObjectNumber = fragment.ContentStreamObjectNumber,
                        OperationIndex = fragment.OperationIndex,
                        TextOperandIndex = fragment.TextOperandIndex,
                        TextAdvanceAdjustment = fragment.TextAdvanceAdjustment,
                        TextRenderMode = fragment.TextRenderMode
                    })
                    : new[]
                    {
                        new TextOperationTarget
                        {
                            StreamIndex = annotation.ContentStreamIndex,
                            StreamObjectNumber = annotation.ContentStreamObjectNumber,
                            OperationIndex = annotation.OperationIndex,
                            RemoveWholeOperation = true,
                            TextRenderMode = annotation.TextRenderMode
                        }
                    })
                .Where(target => target.OperationIndex >= 0 &&
                    (target.StreamObjectNumber > 0 || target.StreamIndex >= 0))
                .GroupBy(target => (
                    target.StreamObjectNumber,
                    target.StreamIndex,
                    target.OperationIndex,
                    target.TextOperandIndex,
                    target.RemoveWholeOperation))
                .Select(group => group.First())
                .ToList();

            return TryRemoveTextOperations(document, pageIndex, preciseTargets);
        }

        public bool RemoveGraphics(
            PdfDocument document,
            int pageIndex,
            IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (pageIndex < 0 || pageIndex >= document.GetNumberOfPages())
                return false;

            PdfPage page = document.GetPage(pageIndex + 1);
            var removals =
                new Dictionary<(int StreamObjectNumber, int StreamIndex), GraphicStreamRemoval>();
            foreach (PdfAnnotation annotation in annotations)
            {
                if (annotation.GraphicOperations.Count == 0)
                {
                    var key = (annotation.ContentStreamObjectNumber, annotation.ContentStreamIndex);
                    if (!removals.TryGetValue(key, out GraphicStreamRemoval? removal))
                    {
                        removal = new GraphicStreamRemoval
                        {
                            StreamObjectNumber = key.ContentStreamObjectNumber,
                            StreamIndex = key.ContentStreamIndex
                        };
                        removals[key] = removal;
                    }
                    removal.ImageOperationIndexes.Add(annotation.OperationIndex);
                    continue;
                }

                foreach (PdfGraphicOperationTarget operation in annotation.GraphicOperations)
                {
                    var key = (operation.StreamObjectNumber, operation.StreamIndex);
                    if (!removals.TryGetValue(key, out GraphicStreamRemoval? removal))
                    {
                        removal = new GraphicStreamRemoval
                        {
                            StreamObjectNumber = key.StreamObjectNumber,
                            StreamIndex = key.StreamIndex
                        };
                        removals[key] = removal;
                    }

                    if (operation.IsShadingOperation)
                        removal.ShadingOperationIndexes.Add(operation.OperationIndex);
                    else if (operation.IsTextOperation)
                        removal.TextOperationIndexes.Add(operation.OperationIndex);
                    else
                        removal.PathOperationIndexes.Add(operation.OperationIndex);
                }
            }

            var rewrites = new List<(PdfStream Stream, byte[] Bytes)>();
            var pageStreamObjectNumbers = Enumerable.Range(0, page.GetContentStreamCount())
                .Select(index =>
                    page.GetContentStream(index)?.GetIndirectReference()?.GetObjNumber() ?? -1)
                .Where(number => number > 0)
                .ToHashSet();
            List<GraphicStreamRemoval> pageRemovals = removals.Values
                .Where(removal => removal.StreamObjectNumber > 0
                    ? pageStreamObjectNumbers.Contains(removal.StreamObjectNumber)
                    : removal.StreamIndex >= 0 &&
                        removal.StreamIndex < page.GetContentStreamCount())
                .ToList();
            if (pageRemovals.Count > 0)
            {
                if (!TryRewriteCombinedPageContentStreamsWithoutGraphics(
                    page,
                    pageRemovals,
                    out List<(PdfStream Stream, byte[] Bytes)> pageRewrites))
                {
                    return false;
                }
                rewrites.AddRange(pageRewrites);
            }

            foreach (GraphicStreamRemoval removal in removals.Values.Except(pageRemovals))
            {
                PdfStream? contentStream = removal.StreamObjectNumber > 0
                    ? document.GetPdfObject(removal.StreamObjectNumber) as PdfStream
                    : null;
                if (contentStream == null ||
                    !TryRewriteContentStreamWithoutGraphics(
                        contentStream.GetBytes(),
                        removal.ImageOperationIndexes,
                        removal.PathOperationIndexes,
                        removal.TextOperationIndexes,
                        removal.ShadingOperationIndexes,
                        out byte[]? rewrittenBytes) ||
                    rewrittenBytes == null)
                {
                    return false;
                }

                rewrites.Add((contentStream, rewrittenBytes));
            }

            if (rewrites.Count == 0)
                return false;
            foreach ((PdfStream streamToRewrite, byte[] bytes) in rewrites)
                streamToRewrite.SetData(bytes);
            return true;
        }

        public bool RemoveTextByCleanup(
            PdfDocument document,
            int pageIndex,
            IReadOnlyCollection<(double x, double y, double w, double h)> targets)
        {
            if (targets.Count == 0)
                return false;

            PdfPage page = document.GetPage(pageIndex + 1);
            Rectangle rect = page.GetCropBox();
            float offsetLeft = rect.GetLeft();
            float offsetBottom = rect.GetBottom();

            var locations = new List<PdfCleanUpLocation>();
            foreach ((double x, double y, double width, double height) in targets)
            {
                float pdfX = offsetLeft + (float)x;
                float pdfY = offsetBottom + (rect.GetHeight() - (float)y - (float)height);
                locations.Add(new PdfCleanUpLocation(
                    pageIndex + 1,
                    new Rectangle(
                        pdfX,
                        pdfY + 0.5f,
                        (float)width,
                        Math.Max((float)height - 1.0f, 0.5f)),
                    null));
            }

            var cleaner = new PdfCleanUpTool(document, locations, new CleanUpProperties());
            cleaner.CleanUp();
            return true;
        }
    }
}
