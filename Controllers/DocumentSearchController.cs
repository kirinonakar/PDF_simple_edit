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

    private int _searchVersion;
    private readonly PdfSearchService _searchService;

    public DocumentSearchController(PdfSearchService searchService)
    {
        _searchService = searchService;
    }

    public void CancelPendingSearch() { ++_searchVersion; _searchService.Dispose(); }

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
        int version = ++_searchVersion;
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

            if (version != _searchVersion || !ReferenceEquals(pdfBytes, manager.GetPdfBytes())) return;
            if (result == null)
            {
                ClearHighlights(overlayCanvas);
                setStatus("텍스트를 찾을 수 없습니다.");
                await showMessage("검색 결과", $"'{searchText}'를 찾을 수 없습니다.");
                return;
            }

            int resultPageIndex = result.PageNumber - 1;
            if (currentPageIndex != resultPageIndex)
                await navigateToPage(resultPageIndex);

            if (version != _searchVersion || !ReferenceEquals(pdfBytes, manager.GetPdfBytes())) return;
            ClearHighlights(overlayCanvas);
            for (int i = 0; i < result.Matches.Count; i++)
                foreach (var rectangle in result.Matches[i])
                    AddHighlight(overlayCanvas, rectangle, i == result.SelectedIndex);
            var selected = result.Matches[result.SelectedIndex][0];
            scrollViewer.ChangeView(selected.GetX() * PdfToPixels * zoomLevel,
                selected.GetY() * PdfToPixels * zoomLevel, null);
            setStatus($"{result.PageNumber} 페이지: {result.SelectedIndex + 1}/{result.Matches.Count} 일치 항목");
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

    private static void AddHighlight(Canvas overlayCanvas, iText.Kernel.Geom.Rectangle rectangle, bool selected)
    {
        var highlight = new Rectangle
        {
            Width = rectangle.GetWidth() * PdfToPixels,
            Height = rectangle.GetHeight() * PdfToPixels,
            Fill = new SolidColorBrush(selected ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.Yellow),
            Opacity = 0.4,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
            StrokeThickness = selected ? 2 : 1,
            IsHitTestVisible = false,
            Tag = SearchHighlightTag
        };
        Canvas.SetLeft(highlight, rectangle.GetX() * PdfToPixels);
        Canvas.SetTop(highlight, rectangle.GetY() * PdfToPixels);
        overlayCanvas.Children.Add(highlight);
    }
}
