using PDF_simple_edit.Helpers;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Services;

public sealed record PdfOcrLanguage(string Tag, string Name);
public sealed record PdfOcrOptions(string LanguageTag, IReadOnlyList<int> PageIndices, bool SkipTextPages);
public sealed record PdfOcrProgress(int Completed, int Total, int PageNumber);
public sealed record PdfOcrResult(byte[] Bytes, IReadOnlyList<int> ChangedPages, int Words, int SkippedPages, int EmptyPages);

/// <summary>Windows' installed, offline OCR engine; one bounded page bitmap at a time.</summary>
public sealed class PdfOcrService
{
    public static IReadOnlyList<PdfOcrLanguage> GetLanguages() => OcrEngine.AvailableRecognizerLanguages
        .Select(language => new PdfOcrLanguage(language.LanguageTag, $"{language.NativeName} ({language.LanguageTag})"))
        .OrderBy(language => language.Name).ToArray();

    public Task<PdfOcrResult> RecognizeAsync(byte[] snapshot, PdfOcrOptions options,
        IProgress<PdfOcrProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = OcrEngine.TryCreateFromLanguage(new Language(options.LanguageTag))
            ?? throw new InvalidOperationException(Loc.Get("OcrLanguageUnavailable"));
        int[] indices = options.PageIndices.Distinct().Order().ToArray();
        using var renderer = await PdfRenderDocument.LoadAsync(snapshot);
        if (indices.Length == 0 || indices.Any(index => index < 0 || index >= renderer.PageCount))
            throw new ArgumentOutOfRangeException(nameof(options));
        using var output = new MemoryStream();
        var changed = new List<int>();
        int words = 0, skipped = 0, empty = 0, completed = 0;
        using (var document = new PdfDocument(new PdfEditingReader(new MemoryStream(snapshot, writable: false)), new PdfWriter(output)))
        {
            var layer = new PdfOcrTextLayer();
            foreach (int index in indices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(completed, indices.Length, index + 1));
                var page = document.GetPage(index + 1);
                var originalContents = page.GetPdfObject().Get(PdfName.Contents, false);
                var originalResources = page.GetResources();
                bool removed = PdfOcrTextLayer.Remove(page);
                if (options.SkipTextPages && !string.IsNullOrWhiteSpace(PdfTextExtractor.GetTextFromPage(page)))
                {
                    if (removed)
                    {
                        page.GetPdfObject().Put(PdfName.Contents, originalContents);
                        page.SetResources(originalResources);
                    }
                    skipped++;
                }
                else
                {
                    var size = renderer.GetPageSize(index);
                    // Limit the raster request as well as the decoded OCR bitmap.
                    // Windows PDF may multiply destination dimensions by display
                    // scaling, so the decoder must enforce the final pixel cap.
                    uint limit = Math.Min(4096u, OcrEngine.MaxImageDimension);
                    double scale = Math.Min(300.0 / 96, (limit - 1.0) / Math.Max(size.Width, size.Height));
                    using var png = await renderer.RenderAsync(index, scale, cancellationToken)
                        ?? throw new IOException(Loc.Format("CannotRenderPageAsImage", index + 1));
                    using var input = png.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
                    double resize = Math.Min(1, (double)limit / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, Math.Floor(decoder.PixelWidth * resize)),
                        ScaledHeight = (uint)Math.Max(1, Math.Floor(decoder.PixelHeight * resize)),
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                        transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
                    var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
                    var lines = result.Lines.Select(line => (IReadOnlyList<PdfOcrWord>)line.Words.Select(word =>
                        new PdfOcrWord(word.Text, word.BoundingRect.X, word.BoundingRect.Y,
                            word.BoundingRect.Width, word.BoundingRect.Height)).ToArray()).ToArray();
                    int count = layer.Add(document, page, new(bitmap.PixelWidth, bitmap.PixelHeight, result.TextAngle ?? 0, lines));
                    words += count;
                    if (count == 0) empty++;
                    if (removed || count > 0) changed.Add(index);
                }
                progress?.Report(new(++completed, indices.Length, index + 1));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PdfOcrResult(changed.Count == 0 ? snapshot : output.ToArray(), changed, words, skipped, empty);
    }, cancellationToken);
}
