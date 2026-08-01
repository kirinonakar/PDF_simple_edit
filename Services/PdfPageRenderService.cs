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
    double LogicalHeight,
    string RenderPath);

public sealed class PdfPageRenderService
{
    private const double PdfToPixels = 96.0 / 72.0;

    public async Task<RenderedPdfPage?> RenderPageAsync(
        PdfDocumentManager manager,
        int pageIndex,
        double renderScale,
        string? currentRenderPath)
    {
        if (!manager.IsLoaded)
            return null;

        string? renderPath = await PrepareRenderPathAsync(manager, currentRenderPath);
        if (renderPath == null)
            return null;

        using MemoryStream? stream = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(
            renderPath,
            pageIndex,
            renderScale * PdfToPixels);
        if (stream == null)
            return null;

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        var pageSize = manager.GetPageSize(pageIndex);
        return new RenderedPdfPage(
            bitmap,
            pageSize.width * PdfToPixels,
            pageSize.height * PdfToPixels,
            renderPath);
    }

    public async IAsyncEnumerable<PageThumbnailData> RenderThumbnailsAsync(
        PdfDocumentManager manager,
        string? renderPath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!manager.IsLoaded)
            yield break;

        string? sourcePath = renderPath ?? manager.FilePath;
        byte[]? pdfBytes = string.IsNullOrEmpty(sourcePath) ? manager.GetPdfBytes() : null;
        if (string.IsNullOrEmpty(sourcePath) && pdfBytes == null)
            yield break;

        for (int pageIndex = 0; pageIndex < manager.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapImage? bitmap = await RenderThumbnailAsync(sourcePath, pdfBytes, pageIndex, cancellationToken);
            yield return new PageThumbnailData
            {
                PageNumber = pageIndex + 1,
                Thumbnail = bitmap
            };
        }
    }

    public void DeleteTemporaryFile(string? filePath)
    {
        if (!IsTemporaryRenderPath(filePath) || !File.Exists(filePath))
            return;

        try { File.Delete(filePath!); }
        catch { }
    }

    private async Task<string?> PrepareRenderPathAsync(
        PdfDocumentManager manager,
        string? currentRenderPath)
    {
        if (!manager.IsModified && currentRenderPath != null && File.Exists(currentRenderPath))
            return currentRenderPath;

        string newPath = Path.Combine(Path.GetTempPath(), $"pdfedit_render_{Guid.NewGuid()}.pdf");
        bool saved = await manager.SaveAsAsync(newPath, false);
        if (!saved)
            return manager.FilePath;

        DeleteTemporaryFile(currentRenderPath);
        return newPath;
    }

    private static async Task<BitmapImage?> RenderThumbnailAsync(
        string? filePath,
        byte[]? pdfBytes,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            MemoryStream? stream = null;
            if (!string.IsNullOrEmpty(filePath))
            {
                for (int retry = 0; retry < 3 && stream == null; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(filePath, pageIndex, 0.4);
                    if (stream == null)
                        await Task.Delay(100, cancellationToken);
                }
            }

            if (stream == null && pdfBytes != null)
            {
                using var input = new MemoryStream(pdfBytes);
                stream = await PdfRenderHelper.RenderPageWithWindowsPdfStreamAsync(
                    input.AsRandomAccessStream(),
                    pageIndex,
                    0.4);
            }

            if (stream == null)
                return null;

            using (stream)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                return bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTemporaryRenderPath(string? filePath) =>
        filePath != null && filePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
}
