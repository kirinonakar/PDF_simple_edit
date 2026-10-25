using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;

namespace PDF_simple_edit.Helpers;

/// <summary>A renderer for one immutable PDF snapshot, shared by pages and thumbnails.</summary>
public sealed class PdfRenderDocument : IDisposable
{
    private readonly PdfDocument _document;
    private readonly MemoryStream _input;
    private readonly Windows.Storage.Streams.IRandomAccessStream _source;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private int _users;
    private bool _disposeRequested;

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
            if (_users == 0) DisposeResources();
        }
    }

    private void DisposeResources()
    {
        try { _source.Dispose(); }
        finally { _input.Dispose(); _renderGate.Dispose(); }
    }

    private PdfRenderDocument(PdfDocument document, Windows.Storage.Streams.IRandomAccessStream source, MemoryStream input)
    {
        _document = document;
        _source = source;
        _input = input;
    }

    public Windows.Foundation.Size GetPageSize(int pageIndex)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            using var page = _document.GetPage((uint)pageIndex);
            return page.Size;
        }
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
            return new PdfRenderDocument(await PdfDocument.LoadFromStreamAsync(stream), stream, input);
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
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (pageIndex < 0 || pageIndex >= PageCount) return null;
            _users++;
        }
        bool entered = false;
        try
        {
            // Share the native document without queueing a whole batch.
            await _renderGate.WaitAsync(cancellationToken);
            entered = true;
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
            if (entered) _renderGate.Release();
            lock (_lifetimeGate)
                if (--_users == 0 && _disposeRequested) DisposeResources();
        }
    }
}
