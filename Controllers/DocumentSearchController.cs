using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Controllers;

public sealed class DocumentSearchController
{
    private const double PdfToPixels = 96.0 / 72.0;
    private const string SearchHighlightTag = "SearchHighlight";

    private readonly PdfSearchService _searchService;

    public DocumentSearchController(PdfSearchService searchService)
    {
        _searchService = searchService;
    }

    public async Task SearchAsync(
        PdfDocumentManager manager,
        Canvas overlayCanvas,
        ScrollViewer scrollViewer,
        string searchText,
        int currentPageIndex,
        bool forward,
        bool matchCase,
        double zoomLevel,
        Func<int, Task> navigateToPage,
        Action<string> setStatus,
        Func<string, string, Task> showMessage)
    {
        try
        {
            byte[]? pdfBytes = manager.GetPdfBytes();
            if (pdfBytes == null)
                return;

            PdfSearchMatch? result = await _searchService.FindAsync(
                pdfBytes,
                searchText,
                currentPageIndex,
                forward,
                matchCase);

            if (result == null)
            {
                setStatus("텍스트를 찾을 수 없습니다.");
                await showMessage("검색 결과", $"'{searchText}'를 찾을 수 없습니다.");
                return;
            }

            int resultPageIndex = result.PageNumber - 1;
            if (currentPageIndex != resultPageIndex)
                await navigateToPage(resultPageIndex);

            ClearHighlights(overlayCanvas);
            foreach (var rectangle in result.Rectangles)
            {
                AddHighlight(
                    manager,
                    overlayCanvas,
                    scrollViewer,
                    resultPageIndex,
                    zoomLevel,
                    rectangle);
            }

            setStatus($"{result.PageNumber} 페이지에서 {result.Rectangles.Count}개의 일치 항목을 찾았습니다.");
        }
        catch (Exception ex)
        {
            setStatus("검색 오류: " + ex.Message);
        }
    }

    public void ClearHighlights(Canvas overlayCanvas)
    {
        var highlights = overlayCanvas.Children
            .OfType<Rectangle>()
            .Where(rectangle => Equals(rectangle.Tag, SearchHighlightTag))
            .ToList();

        foreach (Rectangle highlight in highlights)
            overlayCanvas.Children.Remove(highlight);
    }

    private static void AddHighlight(
        PdfDocumentManager manager,
        Canvas overlayCanvas,
        ScrollViewer scrollViewer,
        int pageIndex,
        double zoomLevel,
        iText.Kernel.Geom.Rectangle rectangle)
    {
        var pageSize = manager.GetPageSize(pageIndex);
        double x = rectangle.GetLeft() * PdfToPixels;
        double y = (pageSize.height - rectangle.GetTop()) * PdfToPixels;

        var highlight = new Rectangle
        {
            Width = rectangle.GetWidth() * PdfToPixels,
            Height = rectangle.GetHeight() * PdfToPixels,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
            Opacity = 0.4,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Tag = SearchHighlightTag
        };

        Canvas.SetLeft(highlight, x);
        Canvas.SetTop(highlight, y);
        overlayCanvas.Children.Add(highlight);
        scrollViewer.ChangeView(x * zoomLevel, y * zoomLevel, null);
    }
}
