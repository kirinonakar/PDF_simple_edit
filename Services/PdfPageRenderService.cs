using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed record RenderedPdfPage(
    BitmapImage Bitmap,
    double LogicalWidth,
    double LogicalHeight);

public sealed class PdfPageRenderService
{
    private const double PdfToPixels = 96.0 / 72.0;
    private sealed class DocumentCache
    {
        public byte[]? Snapshot;
        public Task<PdfRenderDocument>? Load;
    }

    private readonly ConditionalWeakTable<PdfDocumentManager, DocumentCache> _documents = new();

    private async Task<PdfRenderDocument> GetDocumentAsync(PdfDocumentManager manager, byte[] snapshot)
    {
        // Edits, undo/redo, save and signature detachment produce new snapshots.
        // Retain only the latest renderer per tab, not one per undo entry.
        DocumentCache cache = _documents.GetValue(manager, _ => new DocumentCache());
        Task<PdfRenderDocument> load;
        lock (cache)
        {
            if (!ReferenceEquals(cache.Snapshot, snapshot) || cache.Load == null)
            {
                cache.Snapshot = snapshot;
                cache.Load = PdfRenderDocument.LoadAsync(snapshot);
            }
            load = cache.Load;
        }
        try
        {
            return await load;
        }
        catch
        {
            lock (cache)
            {
                if (ReferenceEquals(cache.Load, load)) cache.Load = null;
            }
            throw;
        }
    }

    public async Task<RenderedPdfPage?> RenderPageAsync(
        PdfDocumentManager manager,
        int pageIndex,
        double renderScale)
    {
        byte[]? snapshot = manager.GetPdfBytes();
        if (snapshot == null)
            return null;

        PdfRenderDocument document = await GetDocumentAsync(manager, snapshot);
        using MemoryStream? stream = await document.RenderAsync(pageIndex, renderScale * PdfToPixels);
        if (stream == null || !ReferenceEquals(snapshot, manager.GetPdfBytes()))
            return null;

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        var pageSize = manager.GetPageSize(pageIndex);
        return new RenderedPdfPage(
            bitmap,
            pageSize.width * PdfToPixels,
            pageSize.height * PdfToPixels);
    }

    public async IAsyncEnumerable<PageThumbnailData> RenderThumbnailsAsync(
        PdfDocumentManager manager,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[]? snapshot = manager.GetPdfBytes();
        if (snapshot == null)
            yield break;

        cancellationToken.ThrowIfCancellationRequested();
        PdfRenderDocument document = await GetDocumentAsync(manager, snapshot);
        int pageCount = document.PageCount;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(snapshot, manager.GetPdfBytes()))
                yield break;

            BitmapImage? bitmap = null;
            try
            {
                using MemoryStream? stream = await document.RenderAsync(pageIndex, 0.4, cancellationToken);
                if (stream != null)
                {
                    bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Thumbnail render error: {ex.Message}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(snapshot, manager.GetPdfBytes()))
                yield break;

            yield return new PageThumbnailData { PageNumber = pageIndex + 1, Thumbnail = bitmap };
        }
    }
}
