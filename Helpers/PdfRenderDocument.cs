using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;

namespace PDF_simple_edit.Helpers;

/// <summary>A renderer for one immutable PDF snapshot, shared by pages and thumbnails.</summary>
public sealed class PdfRenderDocument
{
    private readonly PdfDocument _document;
    private readonly Windows.Storage.Streams.IRandomAccessStream _source;
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    private PdfRenderDocument(PdfDocument document, Windows.Storage.Streams.IRandomAccessStream source)
    {
        _document = document;
        _source = source;
    }

    public Windows.Foundation.Size GetPageSize(int pageIndex)
    {
        using var page = _document.GetPage((uint)pageIndex);
        return page.Size;
    }

    public int PageCount => (int)_document.PageCount;

    public static async Task<PdfRenderDocument> LoadAsync(byte[] bytes)
    {
        var input = new MemoryStream(bytes, writable: false);
        var stream = input.AsRandomAccessStream();
        try
        {
            // Windows PDF reads page contents lazily. Keep the memory-backed
            // source alive for as long as this renderer is used.
            return new PdfRenderDocument(await PdfDocument.LoadFromStreamAsync(stream), stream);
        }
        catch
        {
            stream.Dispose();
            input.Dispose();
            throw;
        }
    }

    public async Task<MemoryStream?> RenderAsync(
        int pageIndex, double scale, CancellationToken cancellationToken = default, Windows.Foundation.Rect? region = null)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            return null;

        // Share the native document without rendering concurrently or queueing
        // an entire thumbnail batch ahead of the current page.
        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = _document.GetPage((uint)pageIndex);
            // Finish native page preparation before rasterizing image-heavy PDFs;
            // otherwise a first render can omit content added over a scanned page.
            await page.PreparePageAsync().AsTask(cancellationToken);
            var output = new MemoryStream();
            try
            {
                var stream = output.AsRandomAccessStream();
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, (region?.Width ?? page.Size.Width) * scale),
                    DestinationHeight = (uint)Math.Max(1, (region?.Height ?? page.Size.Height) * scale),
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                };
                if (region is { } sourceRect) options.SourceRect = sourceRect;
                await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken);
                await stream.FlushAsync();
                cancellationToken.ThrowIfCancellationRequested();
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        finally
        {
            GC.KeepAlive(_source);
            _renderGate.Release();
        }
    }
}
