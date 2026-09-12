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

        // 수정된 문서는 FilePath의 원본 파일과 메모리상의 PDF 내용이 다를 수 있습니다.
        // 원본 경로를 사용하면 페이지 삭제/순서 변경 후에도 이전 썸네일이 표시되므로,
        // 수정 상태에서는 메모리 바이트를 렌더링 소스로 사용합니다.
        // 암호로 보호된 문서의 원본 파일은 암호 없이 렌더링할 수 없으므로
        // 항상 메모리의 평문 바이트를 렌더링 소스로 사용한다.
        string? sourcePath = renderPath ?? (!manager.IsModified && !manager.IsPasswordProtected ? manager.FilePath : null);
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
