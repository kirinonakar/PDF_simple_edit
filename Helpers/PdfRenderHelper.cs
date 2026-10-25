using System;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Graphics.Printing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Printing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using System.Collections.Generic;
using PDF_simple_edit.Services;
using Microsoft.UI.Dispatching;
using System.Linq;

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
                await page.PreparePageAsync();
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

        public static async Task<MemoryStream?> RenderRegionWithWindowsPdfStreamAsync(
            Windows.Storage.Streams.IRandomAccessStream inputStream,
            int pageIndex,
            Windows.Foundation.Rect sourceRect,
            double scale = 3.0)
        {
            try
            {
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(inputStream);
                if (pageIndex < 0 || pageIndex >= (int)pdfDoc.PageCount ||
                    sourceRect.Width <= 0 || sourceRect.Height <= 0)
                {
                    return null;
                }

                using var page = pdfDoc.GetPage((uint)pageIndex);
                await page.PreparePageAsync();
                var ms = new MemoryStream();
                var outputStream = ms.AsRandomAccessStream();
                var options = new Windows.Data.Pdf.PdfPageRenderOptions
                {
                    SourceRect = sourceRect,
                    DestinationWidth = (uint)Math.Max(1, Math.Ceiling(sourceRect.Width * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Ceiling(sourceRect.Height * scale)),
                    BackgroundColor = Windows.UI.Color.FromArgb(0, 255, 255, 255)
                };

                await page.RenderToStreamAsync(outputStream, options);
                await outputStream.FlushAsync();
                ms.Position = 0;
                return ms;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows PDF region render error: {ex.Message}");
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
        private PdfRenderDocument? _renderer;
        private IPrintDocumentSource? _printDocumentSource;
        private PrintManager? _printManager;
        private string? _filePath;
        private IntPtr _windowHandle;
        private readonly Dictionary<int, (UIElement Page, double Scale)> _pageCache = new();
        private DispatcherQueue? _dispatcher;
        private TaskCompletionSource? _completion;
        private int _generation;
        private bool _taskRequested;
        public bool IsPrinting => _completion != null;
        private int _totalPageCount = 0;
        private PrintPageDescription _printPageDescription;
        private bool _hasPrintPageDescription;

        public async Task PrintAsync(string filePath, IntPtr windowHandle)
        {
            if (IsPrinting) throw new InvalidOperationException("이전 인쇄 작업이 아직 진행 중입니다.");
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _filePath = filePath;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task completion = _completion.Task;
            _taskRequested = false;
            try
            {
                if (!PrintManager.IsSupported())
                    throw new InvalidOperationException("이 장치에서는 인쇄를 지원하지 않습니다.");

                // Paginate 이벤트는 동기적으로 페이지 수를 받아야 하므로,
                // 인쇄 UI를 표시하기 전에 PDF의 페이지 수를 준비합니다.
                _renderer = await PdfRenderDocument.LoadAsync(await File.ReadAllBytesAsync(filePath));
                _totalPageCount = _renderer.PageCount;
                if (_totalPageCount <= 0)
                    throw new InvalidOperationException("인쇄할 PDF 페이지를 찾을 수 없습니다.");

                _filePath = filePath;
                _windowHandle = windowHandle;
                _pageCache.Clear();
                _hasPrintPageDescription = false;

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
                bool shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(_windowHandle);
                if (!shown || !_taskRequested) Cleanup();
                await completion;
            }
            catch (Exception ex)
            {
                Cleanup();
                System.Diagnostics.Debug.WriteLine($"Print error: {ex.Message}");
                throw;
            }
        }

        private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            _taskRequested = true;
            var documentSource = _printDocumentSource;
            var taskCompletion = _completion;
            var dispatcher = _dispatcher;
            var printTask = args.Request.CreatePrintTask("PDF Simple Editor", sourceRequested =>
            {
                sourceRequested.SetSource(documentSource);
            });

            printTask.Completed += (s, e) =>
            {
                void Complete()
                {
                    if (!ReferenceEquals(taskCompletion, _completion)) return;
                    if (e.Completion == PrintTaskCompletion.Failed)
                        _completion?.TrySetException(new IOException("프린터가 인쇄 작업을 완료하지 못했습니다."));
                    Cleanup();
                }
                if (dispatcher?.HasThreadAccess == true) Complete();
                else if (dispatcher?.TryEnqueue(Complete) != true)
                {
                    // The window is closing; no UI cleanup can be dispatched.
                    PdfTemporaryFiles.TryDelete(_filePath);
                    taskCompletion?.TrySetResult();
                }
            };
        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            if (_totalPageCount <= 0) return;

            // PrintPageDescription의 PageSize/ImageableRect는 모두 DIPs입니다.
            // PDF 페이지도 Windows.Data.Pdf의 DIPs 기준으로 배치해야 실제 용지
            // 크기와 위치가 어긋나지 않습니다.
            _printPageDescription = e.PrintTaskOptions.GetPageDescription(0);
            _hasPrintPageDescription = true;
            ++_generation;
            _pageCache.Clear();

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

            int generation = _generation;
            try
            {
                if (!_pageCache.ContainsKey(pageIdx))
                {
                    var previewPage = await CreatePrintPageAsync(pageIdx, 2.0);
                    if (generation != _generation || !ReferenceEquals(sender, _printDocument)) return;
                    if (previewPage != null)
                    {
                        if (_pageCache.Count >= 3) _pageCache.Remove(_pageCache.Keys.First());
                        _pageCache[pageIdx] = (previewPage, 2.0);
                    }
                }

                if (_pageCache.TryGetValue(pageIdx, out var cachedImage))
                {
                    ((PrintDocument)sender).SetPreviewPage(e.PageNumber, cachedImage.Page);
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

            var printDocument = (PrintDocument)sender;
            int generation = _generation;
            try
            {
                for (int i = 0; i < _totalPageCount; i++)
                {
                    UIElement? page = _pageCache.TryGetValue(i, out var cached) && cached.Scale >= 3.0
                        ? cached.Page : await CreatePrintPageAsync(i, 3.0);
                    if (generation != _generation || !ReferenceEquals(printDocument, _printDocument)) return;
                    if (page == null) throw new IOException($"{i + 1}페이지를 인쇄용으로 렌더링할 수 없습니다.");
                    printDocument.AddPage(page);
                    _pageCache.Remove(i);
                }
                _pageCache.Clear();
                printDocument.AddPagesComplete();
            }
            catch (Exception ex)
            {
                _completion?.TrySetException(ex);
                Cleanup();
            }
        }

        private async Task<UIElement?> CreatePrintPageAsync(int pageIndex, double renderScale)
        {
            var renderer = _renderer;
            if (renderer == null) return null;
            using var ms = await renderer.RenderAsync(pageIndex, renderScale);
            if (ms == null)
                return null;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(ms.AsRandomAccessStream());

            // Bitmap pixel density may include display scaling. Page.Size is
            // the logical paper size, independent of preview/print raster quality.
            if (!ReferenceEquals(renderer, _renderer)) return null;
            var logicalSize = renderer.GetPageSize(pageIndex);
            double documentWidth = logicalSize.Width;
            double documentHeight = logicalSize.Height;
            double pageWidth = documentWidth;
            double pageHeight = documentHeight;
            double printableX = 0;
            double printableY = 0;
            double printableWidth = pageWidth;
            double printableHeight = pageHeight;

            if (_hasPrintPageDescription &&
                _printPageDescription.PageSize.Width > 0 &&
                _printPageDescription.PageSize.Height > 0)
            {
                pageWidth = _printPageDescription.PageSize.Width;
                pageHeight = _printPageDescription.PageSize.Height;

                var imageable = _printPageDescription.ImageableRect;
                printableX = imageable.X;
                printableY = imageable.Y;
                printableWidth = imageable.Width > 0 ? imageable.Width : pageWidth;
                printableHeight = imageable.Height > 0 ? imageable.Height : pageHeight;
            }

            // Keep the PDF at its real size whenever it fits. If the selected
            // printer paper is smaller, reduce it just enough to avoid cropping.
            double fitScale = Math.Min(
                1.0,
                Math.Min(printableWidth / documentWidth, printableHeight / documentHeight));
            if (double.IsNaN(fitScale) || double.IsInfinity(fitScale) || fitScale <= 0)
                fitScale = 1.0;

            double imageWidth = documentWidth * fitScale;
            double imageHeight = documentHeight * fitScale;
            double imageX = printableX + Math.Max(0, (printableWidth - imageWidth) / 2.0);
            double imageY = printableY + Math.Max(0, (printableHeight - imageHeight) / 2.0);

            var pageCanvas = new Canvas
            {
                Width = pageWidth,
                Height = pageHeight,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            var image = new Image
            {
                Source = bitmap,
                Width = imageWidth,
                Height = imageHeight,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Canvas.SetLeft(image, imageX);
            Canvas.SetTop(image, imageY);
            pageCanvas.Children.Add(image);
            return pageCanvas;
        }

        private void Cleanup()
        {
            ++_generation;
            string? temporaryPath = _filePath;
            var completion = _completion;
            _completion = null;
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
            _renderer?.Dispose();
            _renderer = null;
            _printDocumentSource = null;
            _printManager = null;
            _filePath = null;
            _totalPageCount = 0;
            _hasPrintPageDescription = false;
            PdfTemporaryFiles.TryDelete(temporaryPath);
            completion?.TrySetResult();
        }
    }
}
