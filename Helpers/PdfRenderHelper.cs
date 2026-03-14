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
        private string? _filePath;
        private IntPtr _windowHandle;
        private Dictionary<int, Image> _pageCache = new();
        private int _totalPageCount = 0;

        public async Task PrintAsync(string filePath, IntPtr windowHandle)
        {
            try
            {
                _filePath = filePath;
                _windowHandle = windowHandle;
                _pageCache.Clear();

                // Register for printing
                PrintManager printManager = PrintManagerInterop.GetForWindow(_windowHandle);
                printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;

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
                System.Diagnostics.Debug.WriteLine($"Print error: {ex.Message}");
                throw;
            }
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
                _pageCache.Clear();
                _filePath = null;
                sender.PrintTaskRequested -= PrintManager_PrintTaskRequested;
            };
        }

        private async void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            if (_filePath == null) return;

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_filePath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
                _totalPageCount = (int)pdfDoc.PageCount;

                _printDocument?.SetPreviewPageCount(_totalPageCount, PreviewPageCountType.Intermediate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Paginate error: {ex.Message}");
            }
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
                        var image = new Image { Source = bitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
                        _pageCache[pageIdx] = image;
                    }
                }

                if (_pageCache.TryGetValue(pageIdx, out var cachedImage))
                {
                    _printDocument?.SetPreviewPage(e.PageNumber, cachedImage);
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
                            var image = new Image { Source = bitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
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
    }
}

