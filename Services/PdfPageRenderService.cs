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
        using MemoryStream? stream = await document.RenderAsync(pageIndex, renderScale);
        if (stream == null || !ReferenceEquals(snapshot, manager.GetPdfBytes()))
            return null;

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        var pageSize = document.GetPageSize(pageIndex);
        return new RenderedPdfPage(
            bitmap,
            pageSize.Width,
            pageSize.Height);
    }

    public async Task<BitmapImage?> RenderRegionAsync(PdfDocumentManager manager, int pageIndex,
        Windows.Foundation.Rect region, double scale, CancellationToken cancellationToken)
    {
        byte[]? snapshot = manager.GetPdfBytes();
        if (snapshot == null) return null;
        var document = await GetDocumentAsync(manager, snapshot);
        using var stream = await document.RenderAsync(pageIndex, scale, cancellationToken, region);
        if (stream == null || !ReferenceEquals(snapshot, manager.GetPdfBytes())) return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    public async IAsyncEnumerable<PageThumbnailData> RenderThumbnailsAsync(
        PdfDocumentManager manager,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        byte[]? annotatedSnapshot = null)
    {
        byte[]? sourceVersion = manager.GetPdfBytes();
        if (sourceVersion == null)
            yield break;
        byte[] snapshot = annotatedSnapshot ?? sourceVersion;

        cancellationToken.ThrowIfCancellationRequested();
        PdfRenderDocument document = await GetDocumentAsync(manager, snapshot);
        int pageCount = document.PageCount;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(sourceVersion, manager.GetPdfBytes()))
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
            if (!ReferenceEquals(sourceVersion, manager.GetPdfBytes()))
                yield break;

            yield return new PageThumbnailData { PageNumber = pageIndex + 1, Thumbnail = bitmap };
        }
    }

    public async Task<BitmapImage?> RenderThumbnailAsync(PdfDocumentManager manager, int pageIndex,
        CancellationToken cancellationToken, byte[]? annotatedSnapshot = null)
    {
        byte[]? sourceVersion = manager.GetPdfBytes();
        if (sourceVersion == null) return null;
        byte[] snapshot = annotatedSnapshot ?? sourceVersion;
        var document = await GetDocumentAsync(manager, snapshot);
        using var stream = await document.RenderAsync(pageIndex, 0.4, cancellationToken);
        if (stream == null || !ReferenceEquals(sourceVersion, manager.GetPdfBytes())) return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}
