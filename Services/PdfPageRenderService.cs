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
    private readonly ConditionalWeakTable<PdfDocumentManager, PdfSnapshotCache<PdfRenderDocument>> _documents = new();

    private Task<PdfSnapshotCache<PdfRenderDocument>.Lease> GetDocumentAsync(PdfDocumentManager manager, byte[] snapshot)
    {
        var cache = _documents.GetValue(manager, owner =>
        {
            var created = new PdfSnapshotCache<PdfRenderDocument>(PdfRenderDocument.LoadAsync);
            owner.Closed += (_, _) => created.Clear();
            return created;
        });
        return cache.AcquireAsync(snapshot);
    }

    public async Task<RenderedPdfPage?> RenderPageAsync(
        PdfDocumentManager manager,
        int pageIndex,
        double renderScale)
    {
        byte[]? snapshot = manager.GetPdfBytes();
        if (snapshot == null)
            return null;

        using var lease = await GetDocumentAsync(manager, snapshot);
        PdfRenderDocument document = lease.Value;
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
        using var lease = await GetDocumentAsync(manager, snapshot);
        var document = lease.Value;
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
        using var lease = await GetDocumentAsync(manager, snapshot);
        PdfRenderDocument document = lease.Value;
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
        using var lease = await GetDocumentAsync(manager, snapshot);
        var document = lease.Value;
        using var stream = await document.RenderAsync(pageIndex, 0.4, cancellationToken);
        if (stream == null || !ReferenceEquals(sourceVersion, manager.GetPdfBytes())) return null;
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}
