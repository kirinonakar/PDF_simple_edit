using iText.IO.Source;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using iText.Kernel.Pdf.Xobject;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services
{
    internal sealed class PdfPageContentExtractor
    {
        public async Task<List<PdfPageContent>> ExtractAsync(byte[] pdfBytes, int pageIndex, TextEditingMode mode = TextEditingMode.PreserveOriginal)
        {
            ArgumentNullException.ThrowIfNull(pdfBytes);
            byte[] pdfSnapshot = (byte[])pdfBytes.Clone();

            List<PdfPageContent> contents = await Task.Run(() =>
            {
                var extractedContents = new List<PdfPageContent>();
                try
                {
                    using var reader = new PdfReader(new MemoryStream(pdfSnapshot));
                    using var doc = new PdfDocument(reader);
                    if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages())
                        return extractedContents;

                    var page = doc.GetPage(pageIndex + 1);
                    var pageSize = page.GetPageSize();
                    
                    var operationTargets = ExtractTextOperationDescriptors(page);
                    var imageTargets = ExtractImageOperationDescriptors(page);
                    var pathTargets = ExtractPathOperationDescriptors(page);
                    var shadingTargets = ExtractShadingOperationDescriptors(page);
                    var listener = new ContentExtractionListener(
                        pageSize.GetHeight(), imageTargets);
                    PdfCanvasProcessor processor = new PdfCanvasProcessor(listener);
                    listener.Processor = processor;
                    var tracker = new TextOperationTracker(operationTargets);
                    foreach (string operatorName in new[] { "Tj", "TJ", "'", "\"" })
                    {
                        var trackingOperator = new TrackingTextContentOperator(listener, tracker);
                        trackingOperator.InnerOperator = processor.RegisterContentOperator(
                            operatorName, trackingOperator);
                    }
                    var pathTracker = new PathOperationTracker(pathTargets);
                    foreach (string operatorName in new[]
                    {
                        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"
                    })
                    {
                        var trackingOperator = new TrackingPathContentOperator(listener, pathTracker);
                        trackingOperator.InnerOperator = processor.RegisterContentOperator(
                            operatorName, trackingOperator);
                    }
                    var shadingTracker = new ShadingOperationTracker(shadingTargets);
                    var shadingOperator = new TrackingShadingContentOperator(listener, shadingTracker);
                    shadingOperator.InnerOperator = processor.RegisterContentOperator("sh", shadingOperator);
                    processor.ProcessPageContent(page);
                    
                    // The native model owns text geometry and editing identity. The
                    // legacy listener remains responsible for image/vector selection.
                    extractedContents = mode == TextEditingMode.Legacy
                        ? GroupTextIntoEditRegions(listener.Contents.Where(content => content.Type == PageContentType.Text))
                        : new NativePdfTextService().Extract(pdfSnapshot, pageIndex)
                        .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                        .Select(NativePdfTextService.ToPageContent).ToList();
                    extractedContents.AddRange(listener.Contents.Where(content => content.Type == PageContentType.Image));
                    extractedContents.AddRange(GroupVectorPathsIntoGraphics(
                        listener.VectorPaths, pageSize.GetWidth(), pageSize.GetHeight()));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting page contents: {ex.Message}");
                }

                return extractedContents;
            });

            await PopulateRenderedImagePreviewsAsync(pdfSnapshot, pageIndex, contents);
            return contents;
        }

        private static async Task PopulateRenderedImagePreviewsAsync(
            byte[] pdfBytes,
            int pageIndex,
            IEnumerable<PdfPageContent> contents)
        {
            const double pdfToPixels = 96.0 / 72.0;
            foreach (PdfPageContent content in contents.Where(content =>
                content.Type == PageContentType.Image &&
                string.IsNullOrEmpty(content.Text)))
            {
                using var input = new MemoryStream(pdfBytes);
                using MemoryStream? rendered = await PdfRenderHelper.RenderRegionWithWindowsPdfStreamAsync(
                    input.AsRandomAccessStream(),
                    pageIndex,
                    new Windows.Foundation.Rect(
                        content.X * pdfToPixels,
                        content.Y * pdfToPixels,
                        content.Width * pdfToPixels,
                        content.Height * pdfToPixels),
                    3.0);
                if (rendered == null || rendered.Length == 0)
                    continue;

                byte[] bytes = rendered.ToArray();
                string directory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "PDF_simple_edit", "images");
                Directory.CreateDirectory(directory);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string path = System.IO.Path.Combine(directory, $"{hash}.png");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, bytes);
                content.Text = path;
            }
        }


        private static (float x, float y, float width, float height, float originalPdfX, float originalPdfY) GetTextBounds(TextRenderInfo textInfo, float pageHeight)
        {
            var ascentRect = textInfo.GetAscentLine().GetBoundingRectangle();
            var descentRect = textInfo.GetDescentLine().GetBoundingRectangle();
            var baselineRect = textInfo.GetBaseline().GetBoundingRectangle();

            float minX = new[] { ascentRect.GetLeft(), descentRect.GetLeft(), baselineRect.GetLeft() }.Min();
            float maxX = new[] { ascentRect.GetRight(), descentRect.GetRight(), baselineRect.GetRight() }.Max();
            float minY = new[] { ascentRect.GetBottom(), descentRect.GetBottom(), baselineRect.GetBottom() }.Min();
            float maxY = new[] { ascentRect.GetTop(), descentRect.GetTop(), baselineRect.GetTop() }.Max();

            float width = Math.Max(maxX - minX, 0.5f);
            float height = Math.Max(maxY - minY, 0.5f);
            float uiY = pageHeight - maxY;

            return (minX, uiY, width, height, minX, minY);
        }

        private static (float x, float y, float width, float height, float originalPdfX, float originalPdfY) NormalizePathologicalTextBounds(
            TextRenderInfo textInfo,
            (float x, float y, float width, float height, float originalPdfX, float originalPdfY) bounds,
            float pageHeight,
            float fontSize)
        {
            // Some embedded fonts report ascent/descent boxes almost three times the
            // rendered font size. Those boxes overlap adjacent lines and make a click
            // select text that is visibly above or below the pointer. For horizontal
            // text, replace only clearly pathological vertical metrics with a
            // conservative typographic box anchored to the actual baseline.
            if (fontSize <= 0.1f ||
                bounds.height <= Math.Max(fontSize * 1.8f, fontSize + 4f))
            {
                return bounds;
            }

            var baseline = textInfo.GetBaseline();
            var start = baseline.GetStartPoint();
            var end = baseline.GetEndPoint();
            float deltaX = end.Get(0) - start.Get(0);
            float deltaY = end.Get(1) - start.Get(1);
            if (Math.Abs(deltaY) > Math.Max(0.5f, Math.Abs(deltaX) * 0.1f))
                return bounds;

            const float ascentRatio = 0.85f;
            const float descentRatio = 0.25f;
            float baselinePdfY = start.Get(1);
            float normalizedTopPdfY = baselinePdfY + fontSize * ascentRatio;
            float normalizedBottomPdfY = baselinePdfY - fontSize * descentRatio;
            return (
                bounds.x,
                pageHeight - normalizedTopPdfY,
                bounds.width,
                normalizedTopPdfY - normalizedBottomPdfY,
                bounds.originalPdfX,
                normalizedBottomPdfY);
        }

        private static void MergeBounds(PdfPageContent content, (float x, float y, float width, float height, float originalPdfX, float originalPdfY) bounds)
        {
            float left = Math.Min((float)content.X, bounds.x);
            float top = Math.Min((float)content.Y, bounds.y);
            float right = Math.Max((float)(content.X + content.Width), bounds.x + bounds.width);
            float bottom = Math.Max((float)(content.Y + content.Height), bounds.y + bounds.height);

            content.X = left;
            content.Y = top;
            content.Width = right - left;
            content.Height = bottom - top;
            content.OriginalPdfX = left;
            content.OriginalPdfY = bounds.originalPdfY;
        }

        private static float ResolveFontSize(TextRenderInfo textInfo, float actualHeight)
        {
            float reportedFontSize = textInfo.GetFontSize();
            if (reportedFontSize > 0.1f)
            {
                try
                {
                    var font = textInfo.GetFont();
                    string sample = textInfo.GetText();
                    float metricHeight = font.GetAscent(sample, reportedFontSize)
                                       - font.GetDescent(sample, reportedFontSize);
                    if (actualHeight > 0.1f && metricHeight > 0.01f)
                        return reportedFontSize * (actualHeight / metricHeight);
                }
                catch
                {
                    // Fall through to the reported size when the embedded font has incomplete metrics.
                }

                return reportedFontSize;
            }

            if (actualHeight > 0.1f)
                return actualHeight;

            return 12f;
        }

        private static PdfFontMetadata GetFontMetadata(
            TextRenderInfo textInfo,
            IDictionary<int, PdfFontMetadata> cache)
        {
            PdfFont? font = textInfo.GetFont();
            int objectNumber = font?.GetPdfObject()?.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (objectNumber > 0 && cache.TryGetValue(objectNumber, out PdfFontMetadata cached))
                return cached;

            PdfFontMetadata metadata = PdfFontMetadataResolver.Resolve(font);
            if (objectNumber > 0)
                cache[objectNumber] = metadata;
            return metadata;
        }

        private static string NormalizeFontFamily(string rawFontName)
        {
            if (string.IsNullOrWhiteSpace(rawFontName))
                return "맑은 고딕";

            string cleanFontName = rawFontName;
            if (cleanFontName.Contains("+"))
                cleanFontName = cleanFontName[(cleanFontName.IndexOf('+') + 1)..];

            cleanFontName = cleanFontName.Replace("-Bold", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("-Italic", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("-Oblique", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Bold", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Italic", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Oblique", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("MT", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("PS", "", StringComparison.OrdinalIgnoreCase)
                                         .Trim();

            string lowerFont = cleanFontName.ToLowerInvariant();

            if (lowerFont.Contains("malgun")) return "맑은 고딕";
            if (lowerFont.Contains("gulim")) return "굴림";
            if (lowerFont.Contains("dotum")) return "돋움";
            if (lowerFont.Contains("batang")) return "바탕";
            if (lowerFont.Contains("gungsuh")) return "궁서";
            if (lowerFont.Contains("nanumgothic")) return "나눔고딕";
            // Keep the legacy CJK family distinct from the newer language-specific
            // Noto family. They are compatible fallbacks, but they are not the same
            // font build and can have visibly different Latin and Korean glyphs.
            if (lowerFont.Contains("notosanscjkkr")) return "Noto Sans CJK KR";
            if (lowerFont.Contains("notoserifcjkkr")) return "Noto Serif CJK KR";
            if (lowerFont.Contains("notosanscjkjp") || lowerFont.Contains("noto sans cjk jp")) return "Noto Sans CJK JP";
            if (lowerFont.Contains("notoserifcjkjp") || lowerFont.Contains("noto serif cjk jp")) return "Noto Serif CJK JP";
            if (lowerFont.Contains("notosanskr")) return "Noto Sans KR";
            if (lowerFont.Contains("notoserifkr")) return "Noto Serif KR";
            if (lowerFont.Contains("cambriamath")) return "Cambria Math";
            if (lowerFont.Contains("cambria")) return "Cambria";
            if (lowerFont.Contains("arial")) return "Arial";
            if (lowerFont.Contains("times")) return "Times New Roman";
            if (lowerFont.Contains("helvetica")) return "Arial";
            if (lowerFont.Contains("courier")) return "Courier New";
            if (lowerFont.Contains("tahoma")) return "Tahoma";
            if (lowerFont.Contains("verdana")) return "Verdana";
            if (lowerFont.Contains("segoe")) return "Segoe UI";
            if (lowerFont.Contains("consolas")) return "Consolas";

            return string.IsNullOrWhiteSpace(cleanFontName) ? "맑은 고딕" : cleanFontName;
        }

        private static (bool isBold, bool isItalic) InferFontStyle(string rawFontName, int fontWeight)
        {
            string lower = rawFontName?.ToLowerInvariant() ?? string.Empty;
            bool isBold = fontWeight >= 600 || lower.Contains("bold");
            bool isItalic = lower.Contains("italic") || lower.Contains("oblique");
            return (isBold, isItalic);
        }

        private static string ColorToHex(Color? color)
        {
            return PdfDisplayColorService.ToHex(color);
        }

        private sealed class TextOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
            public int TextRenderMode { get; init; }
        }

        private sealed class ImageOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
            public string ResourceName { get; init; } = string.Empty;
        }

        private sealed class PathOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
        }

        private sealed class ShadingOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
        }

        private sealed class VectorPathFragment
        {
            public double X { get; init; }
            public double Y { get; init; }
            public double Width { get; init; }
            public double Height { get; init; }
            public int ShapeCount { get; init; }
            public int HorizontalRuleCount { get; init; }
            public int VerticalRuleCount { get; init; }
            public double LongestHorizontalRule { get; init; }
            public double LongestVerticalRule { get; init; }
            public PathOperationDescriptor Target { get; init; } = new();
            public TextOperationDescriptor? TextTarget { get; init; }
            public ShadingOperationDescriptor? ShadingTarget { get; init; }
        }

        private static bool IsPathPaintingOperator(string operatorName) =>
            operatorName is "S" or "s" or "f" or "F" or "f*" or
                "B" or "B*" or "b" or "b*" or "n";

        private static bool IsTextShowingOperator(string operatorName) =>
            operatorName is "Tj" or "TJ" or "'" or "\"";

        private static List<PathOperationDescriptor> ExtractPathOperationDescriptors(PdfPage page)
        {
            var result = new List<PathOperationDescriptor>();
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;
                AppendPathOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }
            return result;
        }

        private static List<ShadingOperationDescriptor> ExtractShadingOperationDescriptors(PdfPage page)
        {
            var result = new List<ShadingOperationDescriptor>();
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;
                AppendShadingOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }
            return result;
        }

        private static void AppendShadingOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<ShadingOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;
            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new PdfCanvasParser(tokenizer);
                int shadingOperationIndex = -1;
                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;
                    if (operation[^1] is not PdfLiteral literal)
                        continue;
                    string operatorName = literal.ToString();
                    if (operatorName == "sh")
                    {
                        shadingOperationIndex++;
                        result.Add(new ShadingOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = shadingOperationIndex
                        });
                    }
                    else if (operatorName == "Do" && operation.Count > 1 &&
                        operation[0] is PdfName resourceName)
                    {
                        PdfStream? xObject = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                        if (xObject?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) == true)
                        {
                            PdfDictionary? formResourceDictionary = xObject.GetAsDictionary(PdfName.Resources);
                            PdfResources formResources = formResourceDictionary != null
                                ? new PdfResources(formResourceDictionary)
                                : resources;
                            AppendShadingOperationDescriptors(
                                xObject, formResources, -1, result, recursionStack);
                        }
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static void AppendPathOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<PathOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;

            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new PdfCanvasParser(tokenizer);
                int pathOperationIndex = -1;

                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;
                    if (operation[^1] is not PdfLiteral literal)
                        continue;

                    string operatorName = literal.ToString();
                    if (IsPathPaintingOperator(operatorName))
                    {
                        pathOperationIndex++;
                        result.Add(new PathOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = pathOperationIndex
                        });
                    }
                    else if (operatorName == "Do" && operation.Count > 1 &&
                        operation[0] is PdfName resourceName)
                    {
                        PdfStream? xObject = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                        if (xObject?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) == true)
                        {
                            PdfDictionary? formResourceDictionary = xObject.GetAsDictionary(PdfName.Resources);
                            PdfResources formResources = formResourceDictionary != null
                                ? new PdfResources(formResourceDictionary)
                                : resources;
                            AppendPathOperationDescriptors(
                                xObject, formResources, -1, result, recursionStack);
                        }
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static List<ImageOperationDescriptor> ExtractImageOperationDescriptors(PdfPage page)
        {
            var result = new List<ImageOperationDescriptor>();
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;

                AppendImageOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }

            return result;
        }

        private static void AppendImageOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<ImageOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;

            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new PdfCanvasParser(tokenizer);
                int doOperationIndex = -1;

                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;
                    if (operation[^1] is not PdfLiteral literal ||
                        literal.ToString() != "Do" ||
                        operation.Count <= 1 ||
                        operation[0] is not PdfName resourceName)
                    {
                        continue;
                    }

                    doOperationIndex++;
                    PdfStream? xObject = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                    PdfName? subtype = xObject?.GetAsName(PdfName.Subtype);
                    if (PdfName.Image.Equals(subtype))
                    {
                        result.Add(new ImageOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = doOperationIndex,
                            ResourceName = resourceName.GetValue()
                        });
                    }
                    else if (xObject != null && PdfName.Form.Equals(subtype))
                    {
                        PdfDictionary? formResourceDictionary = xObject.GetAsDictionary(PdfName.Resources);
                        PdfResources formResources = formResourceDictionary != null
                            ? new PdfResources(formResourceDictionary)
                            : resources;
                        AppendImageOperationDescriptors(
                            xObject, formResources, -1, result, recursionStack);
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static List<TextOperationDescriptor> ExtractTextOperationDescriptors(PdfPage page)
        {
            var result = new List<TextOperationDescriptor>();

            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                var contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;

                AppendTextOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }

            return result;
        }

        private static void AppendTextOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<TextOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;

            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new iText.Kernel.Pdf.Canvas.Parser.Util.PdfCanvasParser(tokenizer);
                var renderModeStack = new Stack<int>();
                int renderMode = 0;
                int textOperationIndex = -1;

                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;

                    if (operation[^1] is not PdfLiteral literal)
                        continue;

                    string operatorName = literal.ToString();
                    if (operatorName == "q")
                    {
                        renderModeStack.Push(renderMode);
                    }
                    else if (operatorName == "Q")
                    {
                        renderMode = renderModeStack.Count > 0 ? renderModeStack.Pop() : 0;
                    }
                    else if (operatorName == "Tr" && operation.Count > 1 && operation[0] is PdfNumber number)
                    {
                        renderMode = number.IntValue();
                    }
                    else if (IsTextShowingOperator(operatorName))
                    {
                        textOperationIndex++;
                        result.Add(new TextOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = textOperationIndex,
                            TextRenderMode = renderMode
                        });
                    }
                    else if (operatorName == "Do" && operation.Count > 1 && operation[0] is PdfName resourceName)
                    {
                        var xObjects = resources.GetResource(PdfName.XObject);
                        var formStream = xObjects?.GetAsStream(resourceName);
                        if (formStream?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) == true)
                        {
                            var formResourceDictionary = formStream.GetAsDictionary(PdfName.Resources);
                            var formResources = formResourceDictionary != null
                                ? new PdfResources(formResourceDictionary)
                                : resources;
                            AppendTextOperationDescriptors(
                                formStream, formResources, -1, result, recursionStack);
                        }
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static bool HasSameEditableStyle(PdfPageContent region, PdfPageContent next)
        {
            double referenceSize = Math.Max(region.FontSize, 1);
            return string.Equals(region.FontFamily, next.FontFamily, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(region.FontSize - next.FontSize) <= Math.Max(0.75, referenceSize * 0.15)
                && string.Equals(region.Color, next.Color, StringComparison.OrdinalIgnoreCase)
                && region.FontWeight == next.FontWeight
                && region.IsBold == next.IsBold
                && region.IsItalic == next.IsItalic;
        }

        private static bool CanMergeIntoEditRegion(PdfPageContent region, PdfPageContent next, out bool startsNewLine)
        {
            startsNewLine = false;
            if (region.TextFragments.Count == 0 || next.TextFragments.Count == 0)
                return false;

            var previous = region.TextFragments[^1];
            var current = next.TextFragments[0];
            double fontSize = Math.Max(Math.Max(previous.FontSize, current.FontSize), 1);
            // 일부 임베디드 폰트는 같은 PDF 줄에서도 글리프별 ascent/descent
            // 메트릭이 달라 OriginalPdfY가 흔들립니다. 그 값을 기준으로 하면
            // 한 줄의 단어가 여러 편집 영역으로 잘립니다. 화면 좌표의 상단
            // 위치는 실제 줄 배치를 반영하므로 줄 판정에는 Y를 사용합니다.
            double lineTopDelta = Math.Abs(previous.Y - current.Y);
            bool sameLine = lineTopDelta <= Math.Max(1.25, fontSize * 0.35);

            if (!HasSameEditableStyle(region, next))
            {
                // A style change inside the same physical line is part of the
                // same replacement area. Leaving its suffix outside the edit
                // makes a fallback font grow directly into that original text.
                double baselineDelta = Math.Abs(previous.Y + previous.BaselineOffset - current.Y - current.BaselineOffset);
                double styleGap = current.X - (previous.X + previous.Width);
                return baselineDelta <= fontSize * .4 && styleGap >= -fontSize * .2 && styleGap <= fontSize * .65 &&
                    Math.Min(previous.FontSize, current.FontSize) >= fontSize * .4;
            }

            if (sameLine)
            {
                double gap = current.X - (previous.X + previous.Width);
                return gap >= -fontSize && gap <= fontSize * 2.25;
            }

            double verticalGap = current.Y - (previous.Y + previous.Height);
            if (current.Y <= previous.Y || verticalGap > fontSize * 1.35)
                return false;

            double overlap = Math.Min(region.X + region.Width, current.X + current.Width)
                           - Math.Max(region.X, current.X);
            double minWidth = Math.Max(Math.Min(region.Width, current.Width), 1);
            bool horizontallyRelated = overlap / minWidth >= 0.35
                || Math.Abs(current.X - region.X) <= Math.Max(18, fontSize * 1.75);

            startsNewLine = horizontallyRelated;
            return horizontallyRelated;
        }

        private static List<PdfPageContent> GroupTextIntoEditRegions(IEnumerable<PdfPageContent> rawContents)
        {
            var regions = new List<PdfPageContent>();

            foreach (var item in rawContents)
            {
                var last = regions.LastOrDefault();
                if (last == null || !CanMergeIntoEditRegion(last, item, out bool startsNewLine))
                {
                    regions.Add(item);
                    continue;
                }

                var previous = last.TextFragments[^1];
                var current = item.TextFragments[0];
                if (startsNewLine)
                {
                    last.Text += "\r\n" + item.Text;
                    double detectedLineHeight = Math.Abs(current.Y - previous.Y);
                    if (detectedLineHeight > 0.1)
                    {
                        last.LineHeight = last.LineHeight > 0.1
                            ? (last.LineHeight + detectedLineHeight) / 2.0
                            : detectedLineHeight;
                    }
                }
                else
                {
                    double gap = current.X - (previous.X + previous.Width);
                    bool needsSpace = gap > Math.Max(previous.FontSize, current.FontSize) * 0.15
                        && !last.Text.EndsWith(" ", StringComparison.Ordinal)
                        && !item.Text.StartsWith(" ", StringComparison.Ordinal);
                    last.Text += (needsSpace ? " " : string.Empty) + item.Text;
                }

                double left = Math.Min(last.X, item.X);
                double top = Math.Min(last.Y, item.Y);
                double right = Math.Max(last.X + last.Width, item.X + item.Width);
                double bottom = Math.Max(last.Y + last.Height, item.Y + item.Height);
                last.X = left;
                last.Y = top;
                last.Width = right - left;
                last.Height = bottom - top;
                int targetLineIndex = last.TextFragments.Max(fragment => fragment.LineIndex)
                    + (startsNewLine ? 1 : 0);
                foreach (var fragment in item.TextFragments)
                    fragment.LineIndex = targetLineIndex;
                last.TextFragments.AddRange(item.TextFragments);
            }

            foreach (var region in regions)
            {
                if (region.LineHeight <= 0.1)
                    region.LineHeight = Math.Max(region.FontSize * 1.2, region.Height);
                if (region.TextFragments.Count > 0)
                {
                    var first = region.TextFragments[0];
                    region.BaselineOffset = (first.Y - region.Y) + first.BaselineOffset;
                    region.OriginalFontObjectNumber = first.OriginalFontObjectNumber;
                }
            }

            return regions;
        }

        private static List<PdfPageContent> GroupVectorPathsIntoGraphics(
            IReadOnlyCollection<VectorPathFragment> rawPaths,
            double pageWidth,
            double pageHeight)
        {
            var duplicateTargets = rawPaths
                .GroupBy(GetVectorTargetKey)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            var graphics = new List<PdfPageContent>();

            List<VectorPathFragment> remaining = rawPaths
                .Where(path => !duplicateTargets.Contains(GetVectorTargetKey(path)))
                .ToList();
            while (remaining.Count > 0)
            {
                var component = new List<VectorPathFragment> { remaining[0] };
                remaining.RemoveAt(0);
                bool added;
                do
                {
                    added = false;
                    for (int index = remaining.Count - 1; index >= 0; index--)
                    {
                        if (!component.Any(path => AreVectorPathsNear(path, remaining[index])))
                            continue;
                        component.Add(remaining[index]);
                        remaining.RemoveAt(index);
                        added = true;
                    }
                }
                while (added);

                double left = component.Min(path => path.X);
                double top = component.Min(path => path.Y);
                double right = component.Max(path => path.X + path.Width);
                double bottom = component.Max(path => path.Y + path.Height);
                double width = right - left;
                double height = bottom - top;
                if (component.Sum(path => path.ShapeCount) < 3 || width < 8 || height < 5)
                    continue;

                // Page furniture (borders, crop marks and rules) can connect
                // through their bounding boxes into a page-sized component.
                // Its raster preview would include unrelated text and images.
                // Keep these paths in the PDF, but never expose that whole-page
                // crop as an editable graphic. Real image XObjects are unaffected.
                if (width >= pageWidth * 0.8 && height >= pageHeight * 0.8)
                    continue;

                // Connected table borders can span most of a page. Exposing their
                // bounding box as one editable image makes a click on any border
                // select the whole form and then intercept later text clicks.
                // Keep layout grids in the page background instead.
                int horizontalRules = component.Sum(path => path.HorizontalRuleCount);
                int verticalRules = component.Sum(path => path.VerticalRuleCount);
                double longestHorizontalRule = component.Max(path => path.LongestHorizontalRule);
                double longestVerticalRule = component.Max(path => path.LongestVerticalRule);
                if (horizontalRules >= 2 && verticalRules >= 2 &&
                    horizontalRules + verticalRules >= 6 &&
                    longestHorizontalRule >= width * 0.25 &&
                    longestVerticalRule >= height * 0.25)
                {
                    continue;
                }

                const double cropPadding = 1.0;
                left -= cropPadding;
                top -= cropPadding;
                width += cropPadding * 2;
                height += cropPadding * 2;
                List<PdfGraphicOperationTarget> operationTargets = component
                    .Select(GetVectorTargetKey)
                    .Distinct()
                    .Select(key => new PdfGraphicOperationTarget
                    {
                        StreamObjectNumber = key.StreamObjectNumber,
                        StreamIndex = key.StreamIndex,
                        OperationIndex = key.OperationIndex,
                        IsTextOperation = key.IsText,
                        IsShadingOperation = key.IsShading
                    })
                    .OrderBy(target => target.StreamObjectNumber)
                    .ThenBy(target => target.StreamIndex)
                    .ThenBy(target => target.OperationIndex)
                    .ToList();
                PdfGraphicOperationTarget firstTarget = operationTargets[0];
                graphics.Add(new PdfPageContent
                {
                    Type = PageContentType.Image,
                    X = left,
                    Y = top,
                    Width = width,
                    Height = height,
                    OriginalPdfX = left,
                    OriginalPdfY = pageHeight - (top + height),
                    ImageId = "vector:" + string.Join(";", operationTargets.Select(target =>
                        $"{target.StreamObjectNumber}:{target.OperationIndex}:" +
                        $"{target.IsTextOperation}:{target.IsShadingOperation}")),
                    ContentStreamIndex = firstTarget.StreamIndex,
                    ContentStreamObjectNumber = firstTarget.StreamObjectNumber,
                    OperationIndex = firstTarget.OperationIndex,
                    GraphicOperations = operationTargets
                });
            }

            return graphics;
        }

        private static (
            int StreamObjectNumber,
            int StreamIndex,
            int OperationIndex,
            bool IsText,
            bool IsShading) GetVectorTargetKey(VectorPathFragment path) =>
            path.ShadingTarget != null
                ? (
                    path.ShadingTarget.StreamObjectNumber,
                    path.ShadingTarget.StreamIndex,
                    path.ShadingTarget.OperationIndex,
                    false,
                    true)
                : path.TextTarget != null
                ? (
                    path.TextTarget.StreamObjectNumber,
                    path.TextTarget.StreamIndex,
                    path.TextTarget.OperationIndex,
                    true,
                    false)
                : (
                    path.Target.StreamObjectNumber,
                    path.Target.StreamIndex,
                    path.Target.OperationIndex,
                    false,
                    false);

        private static bool AreVectorPathsNear(
            VectorPathFragment first,
            VectorPathFragment second)
        {
            const double maximumGap = 10;
            return first.X <= second.X + second.Width + maximumGap &&
                first.X + first.Width + maximumGap >= second.X &&
                first.Y <= second.Y + second.Height + maximumGap &&
                first.Y + first.Height + maximumGap >= second.Y;
        }

        private sealed class TextOperationTracker
        {
            private readonly IReadOnlyList<TextOperationDescriptor> _targets;
            private int _index;

            public TextOperationTracker(IReadOnlyList<TextOperationDescriptor> targets)
            {
                _targets = targets;
            }

            public TextOperationDescriptor? Next()
            {
                return _index < _targets.Count ? _targets[_index++] : null;
            }
        }

        private sealed class PathOperationTracker
        {
            private readonly IReadOnlyList<PathOperationDescriptor> _targets;
            private int _index;

            public PathOperationTracker(IReadOnlyList<PathOperationDescriptor> targets)
            {
                _targets = targets;
            }

            public PathOperationDescriptor? Next() =>
                _index < _targets.Count ? _targets[_index++] : null;
        }

        private sealed class ShadingOperationTracker
        {
            private readonly IReadOnlyList<ShadingOperationDescriptor> _targets;
            private int _index;

            public ShadingOperationTracker(IReadOnlyList<ShadingOperationDescriptor> targets)
            {
                _targets = targets;
            }

            public ShadingOperationDescriptor? Next() =>
                _index < _targets.Count ? _targets[_index++] : null;
        }

        private sealed class TrackingTextContentOperator : IContentOperator
        {
            private readonly ContentExtractionListener _listener;
            private readonly TextOperationTracker _tracker;

            public IContentOperator? InnerOperator { get; set; }

            public TrackingTextContentOperator(
                ContentExtractionListener listener,
                TextOperationTracker tracker)
            {
                _listener = listener;
                _tracker = tracker;
            }

            public void Invoke(
                PdfCanvasProcessor processor,
                PdfLiteral operatorLiteral,
                IList<PdfObject> operands)
            {
                ContentExtractionListener.TextOperationState previousState = _listener.CaptureTextOperationState();
                _listener.BeginTextOperation(_tracker.Next(), operands);
                try
                {
                    InnerOperator?.Invoke(processor, operatorLiteral, operands);
                }
                finally
                {
                    _listener.RestoreTextOperationState(previousState);
                }
            }
        }

        private sealed class TrackingPathContentOperator : IContentOperator
        {
            private readonly ContentExtractionListener _listener;
            private readonly PathOperationTracker _tracker;

            public IContentOperator? InnerOperator { get; set; }

            public TrackingPathContentOperator(
                ContentExtractionListener listener,
                PathOperationTracker tracker)
            {
                _listener = listener;
                _tracker = tracker;
            }

            public void Invoke(
                PdfCanvasProcessor processor,
                PdfLiteral operatorLiteral,
                IList<PdfObject> operands)
            {
                PathOperationDescriptor? previous = _listener.CurrentPathTarget;
                _listener.CurrentPathTarget = _tracker.Next();
                try
                {
                    InnerOperator?.Invoke(processor, operatorLiteral, operands);
                }
                finally
                {
                    _listener.CurrentPathTarget = previous;
                }
            }
        }

        private sealed class TrackingShadingContentOperator : IContentOperator
        {
            private readonly ContentExtractionListener _listener;
            private readonly ShadingOperationTracker _tracker;

            public IContentOperator? InnerOperator { get; set; }

            public TrackingShadingContentOperator(
                ContentExtractionListener listener,
                ShadingOperationTracker tracker)
            {
                _listener = listener;
                _tracker = tracker;
            }

            public void Invoke(
                PdfCanvasProcessor processor,
                PdfLiteral operatorLiteral,
                IList<PdfObject> operands)
            {
                _listener.AddShading(_tracker.Next());
                InnerOperator?.Invoke(processor, operatorLiteral, operands);
            }
        }

        private class ContentExtractionListener : IEventListener
        {
            public PdfCanvasProcessor Processor { get; set; } = null!;
            public List<PdfPageContent> Contents { get; } = new();
            public List<VectorPathFragment> VectorPaths { get; } = new();
            private readonly float _pageHeight;
            private readonly Dictionary<int, PdfFontMetadata> _fontMetadataCache = new();
            private readonly IReadOnlyList<ImageOperationDescriptor> _imageTargets;
            private readonly HashSet<(int StreamObjectNumber, int StreamIndex, int OperationIndex)> _ambiguousImageTargets;
            private int _imageTargetIndex;
            public TextOperationDescriptor? CurrentTarget { get; set; }
            public PathOperationDescriptor? CurrentPathTarget { get; set; }
            private List<(int Index, byte[] Bytes)> _textOperands = new();
            private int _nextTextOperand;
            private (double X, double Y, double Width, double Height)? _pendingClipBounds;

            public sealed record TextOperationState(
                TextOperationDescriptor? Target,
                List<(int Index, byte[] Bytes)> TextOperands,
                int NextTextOperand);

            public ContentExtractionListener(
                float pageHeight,
                IReadOnlyList<ImageOperationDescriptor> imageTargets)
            {
                _pageHeight = pageHeight;
                _imageTargets = imageTargets;
                _ambiguousImageTargets = imageTargets
                    .GroupBy(target => (
                        target.StreamObjectNumber,
                        target.StreamIndex,
                        target.OperationIndex))
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet();
            }

            public TextOperationState CaptureTextOperationState() => new(
                CurrentTarget, _textOperands, _nextTextOperand);

            public void RestoreTextOperationState(TextOperationState state)
            {
                CurrentTarget = state.Target;
                _textOperands = state.TextOperands;
                _nextTextOperand = state.NextTextOperand;
            }

            public void BeginTextOperation(
                TextOperationDescriptor? target,
                IList<PdfObject> operands)
            {
                CurrentTarget = target;
                _textOperands = new List<(int Index, byte[] Bytes)>();
                _nextTextOperand = 0;

                int stringIndex = 0;
                foreach (PdfObject operand in operands)
                {
                    if (operand is PdfString text)
                    {
                        _textOperands.Add((stringIndex++, text.GetValueBytes()));
                    }
                    else if (operand is PdfArray array)
                    {
                        foreach (PdfObject element in array)
                        {
                            if (element is PdfString arrayText)
                                _textOperands.Add((stringIndex++, arrayText.GetValueBytes()));
                        }
                    }
                }
            }

            private int ResolveTextOperandIndex(PdfString renderedText)
            {
                byte[] renderedBytes = renderedText.GetValueBytes();
                for (int i = _nextTextOperand; i < _textOperands.Count; i++)
                {
                    if (!_textOperands[i].Bytes.SequenceEqual(renderedBytes))
                        continue;

                    _nextTextOperand = i + 1;
                    return _textOperands[i].Index;
                }

                return -1;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type == EventType.RENDER_PATH && data is PathRenderInfo pathInfo)
                {
                    AddVectorPath(pathInfo);
                    return;
                }
                if (type == EventType.RENDER_IMAGE && data is ImageRenderInfo imageInfo)
                {
                    AddImageContent(imageInfo);
                    return;
                }
                if (type != EventType.RENDER_TEXT || data is not TextRenderInfo textInfo)
                    return;

                var text = textInfo.GetText();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                var bounds = GetTextBounds(textInfo, _pageHeight);
                var fontVector = new Vector(0, textInfo.GetFontSize(), 0).Cross(
                    textInfo.GetTextMatrix().Multiply(Processor.GetGraphicsState().GetCtm()));
                float fontSize = (float)Math.Sqrt(fontVector.Get(0) * fontVector.Get(0) + fontVector.Get(1) * fontVector.Get(1));
                bounds = NormalizePathologicalTextBounds(
                    textInfo, bounds, _pageHeight, fontSize);
                var baseline = textInfo.GetBaseline().GetStartPoint();
                double baselineOffset = (_pageHeight - baseline.Get(1)) - bounds.y;
                PdfFontMetadata fontMetadata = GetFontMetadata(textInfo, _fontMetadataCache);
                string rawFontName = fontMetadata.RawName;
                string fontFamily = NormalizeFontFamily(rawFontName);
                var (isBold, isItalic) = InferFontStyle(rawFontName, fontMetadata.Weight);
                string color = ColorToHex(textInfo.GetFillColor());
                int originalFontObjectNumber = textInfo.GetFont()?.GetPdfObject()
                    ?.GetIndirectReference()?.GetObjNumber() ?? -1;

                TextOperationDescriptor? target = CurrentTarget;
                int textOperandIndex = ResolveTextOperandIndex(textInfo.GetPdfString());
                double? textAdvanceAdjustment = Math.Abs(textInfo.GetFontSize()) > 0.0001f
                    ? -1000.0 * textInfo.GetUnscaledWidth() / textInfo.GetFontSize()
                    : null;

                var fragment = new PdfTextFragment
                {
                    Text = text,
                    X = bounds.x,
                    Y = bounds.y,
                    Width = bounds.width,
                    Height = bounds.height,
                    OriginalPdfX = bounds.originalPdfX,
                    OriginalPdfY = bounds.originalPdfY,
                    FontSize = fontSize,
                    FontFamily = fontFamily,
                    Color = color,
                    FontWeight = fontMetadata.Weight,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    ContentStreamIndex = target?.StreamIndex ?? -1,
                    ContentStreamObjectNumber = target?.StreamObjectNumber ?? -1,
                    OperationIndex = target?.OperationIndex ?? -1,
                    TextOperandIndex = textOperandIndex,
                    TextAdvanceAdjustment = textAdvanceAdjustment,
                    TextRenderMode = target?.TextRenderMode ?? 0,
                    BaselineOffset = baselineOffset,
                    OriginalFontObjectNumber = originalFontObjectNumber
                };

                Contents.Add(new PdfPageContent
                {
                    Type = PageContentType.Text,
                    Text = text,
                    X = bounds.x,
                    Y = bounds.y,
                    Width = bounds.width,
                    Height = bounds.height,
                    FontSize = fontSize,
                    FontFamily = fontFamily,
                    Color = color,
                    FontWeight = fontMetadata.Weight,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    OriginalPdfX = bounds.originalPdfX,
                    OriginalPdfY = bounds.originalPdfY,
                    ContentStreamIndex = fragment.ContentStreamIndex,
                    ContentStreamObjectNumber = fragment.ContentStreamObjectNumber,
                    OperationIndex = fragment.OperationIndex,
                    TextRenderMode = fragment.TextRenderMode,
                    BaselineOffset = baselineOffset,
                    OriginalFontObjectNumber = originalFontObjectNumber,
                    TextFragments = new List<PdfTextFragment> { fragment }
                });
            }

            private void AddVectorPath(PathRenderInfo pathInfo)
            {
                PathOperationDescriptor? target = CurrentPathTarget;
                bool isClippingPath = pathInfo.IsPathModifiesClippingPath();
                if ((target == null && CurrentTarget == null) ||
                    (pathInfo.GetOperation() == PathRenderInfo.NO_OP && !isClippingPath))
                {
                    return;
                }

                Matrix matrix = pathInfo.GetCtm();
                float a = matrix.Get(Matrix.I11);
                float b = matrix.Get(Matrix.I12);
                float c = matrix.Get(Matrix.I21);
                float d = matrix.Get(Matrix.I22);
                float e = matrix.Get(Matrix.I31);
                float f = matrix.Get(Matrix.I32);
                var subpaths = pathInfo.GetPath().GetSubpaths();
                int horizontalRuleCount = 0;
                int verticalRuleCount = 0;
                double longestHorizontalRule = 0;
                double longestVerticalRule = 0;
                const double maximumRuleThickness = 2.0;
                const double minimumRuleLength = 3.0;
                foreach (var subpath in subpaths)
                {
                    var subpathPoints = subpath.GetSegments()
                        .SelectMany(segment => segment.GetBasePoints())
                        .Select(point => (
                            X: (double)(a * point.GetX() + c * point.GetY() + e),
                            Y: (double)(b * point.GetX() + d * point.GetY() + f)))
                        .ToList();
                    if (subpathPoints.Count == 0)
                        continue;

                    double subpathWidth = subpathPoints.Max(point => point.X) -
                        subpathPoints.Min(point => point.X);
                    double subpathHeight = subpathPoints.Max(point => point.Y) -
                        subpathPoints.Min(point => point.Y);
                    if (subpathWidth >= minimumRuleLength && subpathHeight <= maximumRuleThickness)
                    {
                        horizontalRuleCount++;
                        longestHorizontalRule = Math.Max(longestHorizontalRule, subpathWidth);
                    }
                    else if (subpathHeight >= minimumRuleLength && subpathWidth <= maximumRuleThickness)
                    {
                        verticalRuleCount++;
                        longestVerticalRule = Math.Max(longestVerticalRule, subpathHeight);
                    }
                }
                var points = subpaths
                    .SelectMany(subpath => subpath.GetSegments())
                    .SelectMany(segment => segment.GetBasePoints())
                    .Select(point => (
                        X: (double)(a * point.GetX() + c * point.GetY() + e),
                        Y: (double)(b * point.GetX() + d * point.GetY() + f)))
                    .ToList();
                if (points.Count == 0)
                    return;

                double padding = Math.Max(pathInfo.GetLineWidth() / 2.0, 0.1);
                double left = points.Min(point => point.X) - padding;
                double right = points.Max(point => point.X) + padding;
                double bottom = points.Min(point => point.Y) - padding;
                double top = points.Max(point => point.Y) + padding;
                if (right - left <= 0.1 || top - bottom <= 0.1)
                    return;

                if (isClippingPath)
                {
                    _pendingClipBounds = (
                        left,
                        _pageHeight - top,
                        right - left,
                        top - bottom);
                    return;
                }

                VectorPaths.Add(new VectorPathFragment
                {
                    X = left,
                    Y = _pageHeight - top,
                    Width = right - left,
                    Height = top - bottom,
                    ShapeCount = subpaths.Count,
                    HorizontalRuleCount = horizontalRuleCount,
                    VerticalRuleCount = verticalRuleCount,
                    LongestHorizontalRule = longestHorizontalRule,
                    LongestVerticalRule = longestVerticalRule,
                    Target = target ?? new PathOperationDescriptor(),
                    TextTarget = CurrentTarget
                });
            }

            public void AddShading(ShadingOperationDescriptor? target)
            {
                if (target == null || _pendingClipBounds is not { } bounds)
                    return;
                VectorPaths.Add(new VectorPathFragment
                {
                    X = bounds.X,
                    Y = bounds.Y,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    ShapeCount = 1,
                    Target = new PathOperationDescriptor(),
                    ShadingTarget = target
                });
                _pendingClipBounds = null;
            }

            private void AddImageContent(ImageRenderInfo imageInfo)
            {
                // Inline images do not have a removable Do operation. Keep them in
                // the rendered page, but do not expose a control that cannot be saved safely.
                if (imageInfo.IsInline() || _imageTargetIndex >= _imageTargets.Count)
                    return;

                ImageOperationDescriptor target = _imageTargets[_imageTargetIndex++];
                if (_ambiguousImageTargets.Contains((
                    target.StreamObjectNumber,
                    target.StreamIndex,
                    target.OperationIndex)))
                {
                    // A shared form XObject can place the same image operation more
                    // than once. Removing that shared operation would delete every
                    // placement, so those instances are intentionally not editable.
                    return;
                }
                try
                {
                    Matrix matrix = imageInfo.GetImageCtm();
                    float a = matrix.Get(Matrix.I11);
                    float b = matrix.Get(Matrix.I12);
                    float c = matrix.Get(Matrix.I21);
                    float d = matrix.Get(Matrix.I22);
                    float e = matrix.Get(Matrix.I31);
                    float f = matrix.Get(Matrix.I32);
                    float[] xs = { e, a + e, c + e, a + c + e };
                    float[] ys = { f, b + f, d + f, b + d + f };
                    float left = xs.Min();
                    float right = xs.Max();
                    float bottom = ys.Min();
                    float top = ys.Max();
                    if (right - left <= 0.1f || top - bottom <= 0.1f)
                        return;

                    PdfImageXObject image = imageInfo.GetImage();
                    PdfStream imageStream = image.GetPdfObject();
                    bool requiresRenderedPreview =
                        imageStream.ContainsKey(PdfName.SMask) ||
                        imageStream.ContainsKey(PdfName.Mask);
                    string imagePath = string.Empty;
                    if (!requiresRenderedPreview)
                    {
                        byte[] imageBytes = image.GetImageBytes(true);
                        string extension = NormalizeImageExtension(image.IdentifyImageFileExtension());
                        imagePath = SaveExtractedImage(imageBytes, extension);
                    }

                    Contents.Add(new PdfPageContent
                    {
                        Type = PageContentType.Image,
                        X = left,
                        Y = _pageHeight - top,
                        Width = right - left,
                        Height = top - bottom,
                        OriginalPdfX = left,
                        OriginalPdfY = bottom,
                        // iText's decoded image bytes do not include a separate
                        // PDF soft/color-key mask. Leave the preview empty so the
                        // page renderer rebuilds the image with its transparency.
                        Text = imagePath,
                        ImageId = target.ResourceName,
                        ContentStreamIndex = target.StreamIndex,
                        ContentStreamObjectNumber = target.StreamObjectNumber,
                        OperationIndex = target.OperationIndex
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting PDF image: {ex.Message}");
                }
            }

            private static string NormalizeImageExtension(string? extension) =>
                extension?.ToLowerInvariant() switch
                {
                    "jpg" or "jpeg" => "jpg",
                    "jp2" => "jp2",
                    "tif" or "tiff" => "tif",
                    "jbig2" => "jbig2",
                    _ => "png"
                };

            private static string SaveExtractedImage(byte[] bytes, string extension)
            {
                string directory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "PDF_simple_edit", "images");
                Directory.CreateDirectory(directory);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string path = System.IO.Path.Combine(directory, $"{hash}.{extension}");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, bytes);
                return path;
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new[]
                {
                    EventType.RENDER_TEXT,
                    EventType.RENDER_IMAGE,
                    EventType.RENDER_PATH
                };
            }
        }
    }
}
