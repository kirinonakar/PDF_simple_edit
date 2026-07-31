using System;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Graphics.Printing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Printing;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using System.Collections.Generic;

namespace PDF_simple_edit.Helpers
{
    /// <summary>
    /// Helper class for rendering PDF pages using Windows.Data.Pdf API.
    /// </summary>
    public class PdfRenderHelper
    {
        // ... (rest of the class remains same as it uses Windows.Data.Pdf)
        public static async Task<MemoryStream?> RenderPageWithWindowsPdfAsync(
            string filePath, int pageIndex, double scale = 2.0)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                using var stream = await file.OpenReadAsync();
                return await RenderPageWithWindowsPdfStreamAsync(stream, pageIndex, scale);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows PDF render file error: {ex.Message}");
                return null;
            }
        }

        public static async Task<MemoryStream?> RenderPageWithWindowsPdfStreamAsync(
            Windows.Storage.Streams.IRandomAccessStream inputStream, int pageIndex, double scale = 2.0)
        {
            try
            {
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(inputStream);

                if (pageIndex < 0 || pageIndex >= (int)pdfDoc.PageCount)
                    return null;

                using var page = pdfDoc.GetPage((uint)pageIndex);
                var ms = new MemoryStream();
                var outputStream = ms.AsRandomAccessStream();

                var options = new Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)(page.Size.Width * scale),
                    DestinationHeight = (uint)(page.Size.Height * scale),
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                };

                await page.RenderToStreamAsync(outputStream, options);
                await outputStream.FlushAsync();
                ms.Position = 0;
                return ms;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows PDF render stream error: {ex.Message}");
                return null;
            }
        }

        public static async Task<(double width, double height)> GetPageSizeAsync(
            string filePath, int pageIndex)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

                if (pageIndex < 0 || pageIndex >= (int)pdfDoc.PageCount)
                    return (0, 0);

                using var page = pdfDoc.GetPage((uint)pageIndex);
                return (page.Size.Width, page.Size.Height);
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    public class PrintHelper
    {
        private PrintDocument? _printDocument;
        private IPrintDocumentSource? _printDocumentSource;
        private PrintManager? _printManager;
        private string? _filePath;
        private IntPtr _windowHandle;
        private Dictionary<int, Image> _pageCache = new();
        private int _totalPageCount = 0;

        public async Task PrintAsync(string filePath, IntPtr windowHandle)
        {
            try
            {
                if (!PrintManager.IsSupported())
                    throw new InvalidOperationException("이 장치에서는 인쇄를 지원하지 않습니다.");

                // Paginate 이벤트는 동기적으로 페이지 수를 받아야 하므로,
                // 인쇄 UI를 표시하기 전에 PDF의 페이지 수를 준비합니다.
                _totalPageCount = await GetPageCountAsync(filePath);
                if (_totalPageCount <= 0)
                    throw new InvalidOperationException("인쇄할 PDF 페이지를 찾을 수 없습니다.");

                _filePath = filePath;
                _windowHandle = windowHandle;
                _pageCache.Clear();

                // Register for printing
                _printManager = PrintManagerInterop.GetForWindow(_windowHandle);
                _printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;

                // Initialize PrintDocument
                _printDocument = new PrintDocument();
                _printDocumentSource = _printDocument.DocumentSource;
                _printDocument.Paginate += PrintDocument_Paginate;
                _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
                _printDocument.AddPages += PrintDocument_AddPages;

                // Show print UI
                await PrintManagerInterop.ShowPrintUIForWindowAsync(_windowHandle);
            }
            catch (Exception ex)
            {
                Cleanup();
                System.Diagnostics.Debug.WriteLine($"Print error: {ex.Message}");
                throw;
            }
        }

        private static async Task<int> GetPageCountAsync(string filePath)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
            var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            return (int)pdfDoc.PageCount;
        }

        private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            var printTask = args.Request.CreatePrintTask("PDF Simple Editor", sourceRequested =>
            {
                sourceRequested.SetSource(_printDocumentSource);
            });

            printTask.Completed += (s, e) =>
            {
                // Clean up after printing
                Cleanup();
            };
        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            if (_totalPageCount <= 0) return;

            // 페이지 수를 미리 알고 있으므로 최종 개수로 즉시 알립니다.
            ((PrintDocument)sender).SetPreviewPageCount(
                _totalPageCount,
                PreviewPageCountType.Final);
        }

        private async void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            if (_filePath == null) return;

            int pageIdx = e.PageNumber - 1;
            if (pageIdx < 0 || pageIdx >= _totalPageCount) return;

            try
            {
                if (!_pageCache.ContainsKey(pageIdx))
                {
                    // Render page on demand (use 2.0 scale for preview to save memory)
                    var ms = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(_filePath, pageIdx, 2.0);
                    if (ms != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                        var image = new Image
                        {
                            Source = bitmap,
                            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                            Width = bitmap.PixelWidth / 2.0,
                            Height = bitmap.PixelHeight / 2.0,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        _pageCache[pageIdx] = image;
                    }
                }

                if (_pageCache.TryGetValue(pageIdx, out var cachedImage))
                {
                    ((PrintDocument)sender).SetPreviewPage(e.PageNumber, cachedImage);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetPreviewPage error: {ex.Message}");
            }
        }

        private async void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
        {
            if (_filePath == null) return;

            for (int i = 0; i < _totalPageCount; i++)
            {
                try
                {
                    // For final printing, ensure we have the best quality
                    // We can reuse cache if already rendered at high enough quality
                    if (!_pageCache.ContainsKey(i))
                    {
                        var ms = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(_filePath, i, 3.0);
                        if (ms != null)
                        {
                            var bitmap = new BitmapImage();
                            await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                            var image = new Image
                            {
                                Source = bitmap,
                                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                                Width = bitmap.PixelWidth / 3.0,
                                Height = bitmap.PixelHeight / 3.0,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            _pageCache[i] = image;
                        }
                    }

                    if (_pageCache.TryGetValue(i, out var page))
                    {
                        _printDocument?.AddPage(page);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AddPage error at index {i}: {ex.Message}");
                }
            }
            _printDocument?.AddPagesComplete();
        }

        private void Cleanup()
        {
            if (_printDocument != null)
            {
                _printDocument.Paginate -= PrintDocument_Paginate;
                _printDocument.GetPreviewPage -= PrintDocument_GetPreviewPage;
                _printDocument.AddPages -= PrintDocument_AddPages;
            }

            if (_printManager != null)
                _printManager.PrintTaskRequested -= PrintManager_PrintTaskRequested;

            _pageCache.Clear();
            _printDocument = null;
            _printDocumentSource = null;
            _printManager = null;
            _filePath = null;
            _totalPageCount = 0;
        }
    }
}
