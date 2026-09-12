using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace PDF_simple_edit.Controllers;

/// <summary>
/// Coordinates page rendering, navigation, zoom, thumbnail selection, and page
/// reordering for the active document tab.
/// </summary>
public sealed class PageViewController
{
    private readonly PdfPageRenderService _renderService;
    private readonly ListView _pageListView;
    private readonly Image _pageImage;
    private readonly Canvas _overlayCanvas;
    private readonly ScrollViewer _scrollViewer;
    private readonly TextBlock _statusText;
    private readonly TextBlock _zoomText;
    private readonly TextBox _goToPageTextBox;
    private readonly Func<PdfDocumentManager> _getManager;
    private readonly Func<ObservableCollection<PageThumbnailData>> _getThumbnails;
    private readonly Func<List<PdfAnnotation>> _getAnnotations;
    private readonly Func<int> _getCurrentPageIndex;
    private readonly Action<int> _setCurrentPageIndex;
    private readonly Func<double> _getZoomLevel;
    private readonly Action<double> _setZoomLevel;
    private readonly Func<double> _getRenderScale;
    private readonly Action _renderAnnotationOverlays;
    private readonly Action _updateUiState;

    private CancellationTokenSource? _thumbnailCts;
    private int _renderVersion;
    private PageThumbnailData? _draggedThumbnail;
    private int _draggedOriginalIndex = -1;
    private int _pageIndexBeforeReorder = -1;
    private int _dropIndex = -1;
    private bool _isReordering;
    private bool _pointerPressed;
    private bool _preserveThumbnailsAfterReorder;

    public PageViewController(
        PdfPageRenderService renderService,
        ListView pageListView,
        Image pageImage,
        Canvas overlayCanvas,
        ScrollViewer scrollViewer,
        TextBlock statusText,
        TextBlock zoomText,
        TextBox goToPageTextBox,
        Func<PdfDocumentManager> getManager,
        Func<ObservableCollection<PageThumbnailData>> getThumbnails,
        Func<List<PdfAnnotation>> getAnnotations,
        Func<int> getCurrentPageIndex,
        Action<int> setCurrentPageIndex,
        Func<double> getZoomLevel,
        Action<double> setZoomLevel,
        Func<double> getRenderScale,
        Action renderAnnotationOverlays,
        Action updateUiState)
    {
        _renderService = renderService;
        _pageListView = pageListView;
        _pageImage = pageImage;
        _overlayCanvas = overlayCanvas;
        _scrollViewer = scrollViewer;
        _statusText = statusText;
        _zoomText = zoomText;
        _goToPageTextBox = goToPageTextBox;
        _getManager = getManager;
        _getThumbnails = getThumbnails;
        _getAnnotations = getAnnotations;
        _getCurrentPageIndex = getCurrentPageIndex;
        _setCurrentPageIndex = setCurrentPageIndex;
        _getZoomLevel = getZoomLevel;
        _setZoomLevel = setZoomLevel;
        _getRenderScale = getRenderScale;
        _renderAnnotationOverlays = renderAnnotationOverlays;
        _updateUiState = updateUiState;
    }

    public async Task RenderCurrentPageAsync()
    {
        int version = ++_renderVersion;
        PdfDocumentManager manager = _getManager();
        if (!manager.IsLoaded)
            return;

        int pageIndex = _getCurrentPageIndex();
        byte[]? snapshot = manager.GetPdfBytes();
        bool IsCurrentRequest() => version == _renderVersion &&
            ReferenceEquals(manager, _getManager()) &&
            ReferenceEquals(snapshot, manager.GetPdfBytes()) && pageIndex == _getCurrentPageIndex();

        string oldStatus = _statusText.Text;
        try
        {
            _statusText.Text = "페이지 렌더링 중...";
            RenderedPdfPage? page = await _renderService.RenderPageAsync(
                manager,
                pageIndex,
                _getRenderScale());
            if (page == null || !IsCurrentRequest())
                return;

            _pageImage.Source = page.Bitmap;
            _pageImage.HorizontalAlignment = HorizontalAlignment.Left;
            _pageImage.VerticalAlignment = VerticalAlignment.Top;
            _overlayCanvas.HorizontalAlignment = HorizontalAlignment.Left;
            _overlayCanvas.VerticalAlignment = VerticalAlignment.Top;
            _pageImage.Margin = new Thickness(0);
            _overlayCanvas.Margin = new Thickness(0);
            _pageImage.Width = page.LogicalWidth;
            _pageImage.Height = page.LogicalHeight;
            _overlayCanvas.Width = page.LogicalWidth;
            _overlayCanvas.Height = page.LogicalHeight;
            _pageImage.Stretch = Stretch.Fill;
            _renderAnnotationOverlays();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
        }
        finally
        {
            if (IsCurrentRequest())
            {
                _statusText.Text = oldStatus == "페이지 렌더링 중..." ? "준비" : oldStatus;
                _updateUiState();
                SyncSelection();
            }
        }
    }

    public void CancelPendingOperations()
    {
        ++_renderVersion;
        _thumbnailCts?.Cancel();
    }

    public async Task LoadThumbnailsAsync()
    {
        _thumbnailCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        CancellationToken token = cts.Token;
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();

        try
        {
            thumbnails.Clear();
            await foreach (PageThumbnailData thumbnail in _renderService.RenderThumbnailsAsync(
                _getManager(), token))
            {
                token.ThrowIfCancellationRequested();
                thumbnails.Add(thumbnail);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadThumbnails error: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCts, cts))
                _thumbnailCts = null;
        }
    }

    public async Task MovePreviousAsync()
    {
        int currentIndex = _getCurrentPageIndex();
        if (currentIndex <= 0)
            return;

        _setCurrentPageIndex(currentIndex - 1);
        await RenderCurrentPageAsync();
        SyncSelection();
    }

    public async Task MoveNextAsync()
    {
        int currentIndex = _getCurrentPageIndex();
        if (currentIndex >= _getManager().PageCount - 1)
            return;

        _setCurrentPageIndex(currentIndex + 1);
        await RenderCurrentPageAsync();
        SyncSelection();
    }

    public async Task HandleGoToPageKeyAsync(KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter ||
            !int.TryParse(_goToPageTextBox.Text, out int pageNumber) ||
            pageNumber < 1 || pageNumber > _getManager().PageCount)
        {
            return;
        }

        _setCurrentPageIndex(pageNumber - 1);
        await RenderCurrentPageAsync();
        SyncSelection();
        _goToPageTextBox.Text = string.Empty;
    }

    public async Task HandleSelectionChangedAsync()
    {
        if (_isReordering || _pageListView.SelectedItem is not PageThumbnailData thumbnail ||
            _getCurrentPageIndex() == thumbnail.PageIndex)
        {
            return;
        }

        _setCurrentPageIndex(thumbnail.PageIndex);
        await RenderCurrentPageAsync();
    }

    public void PointerPressed(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_pageListView);
        if (point.Properties.IsRightButtonPressed)
        {
            if (FindPageListItem(e.OriginalSource as DependencyObject) is ListViewItem item &&
                item.Content is PageThumbnailData thumbnail &&
                !_pageListView.SelectedItems.Contains(thumbnail))
            {
                _pageListView.SelectedItems.Clear();
                _pageListView.SelectedItems.Add(thumbnail);
            }

            _updateUiState();
            return;
        }

        _pointerPressed = true;
    }

    public void PointerReleased() => _pointerPressed = false;

    public void DragOver(DragEventArgs e)
    {
        if (!_isReordering)
            return;

        _dropIndex = GetDropIndex(e.GetPosition(_pageListView));
        UpdateDropVisual();
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    public void DragItemsStarting(DragItemsStartingEventArgs e)
    {
        PageThumbnailData? thumbnail = e.Items.OfType<PageThumbnailData>().FirstOrDefault();
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();
        int originalIndex = thumbnail == null ? -1 : thumbnails.IndexOf(thumbnail);
        if (thumbnail == null || originalIndex < 0)
        {
            e.Cancel = true;
            return;
        }

        _draggedThumbnail = thumbnail;
        _draggedOriginalIndex = originalIndex;
        _pageIndexBeforeReorder = _getCurrentPageIndex();
        _dropIndex = -1;
        ClearDropVisual();
        _isReordering = true;
    }

    public void DragItemsCompleted()
    {
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();
        PageThumbnailData? draggedPage = _draggedThumbnail;
        int originalIndex = _draggedOriginalIndex;
        int dropIndex = _dropIndex;
        int pageIndexBeforeReorder = _pageIndexBeforeReorder;

        if (draggedPage == null || originalIndex < 0 ||
            dropIndex < 0 || dropIndex > thumbnails.Count)
        {
            ResetReorderState();
            return;
        }

        int newIndex = dropIndex > originalIndex ? dropIndex - 1 : dropIndex;
        newIndex = Math.Clamp(newIndex, 0, thumbnails.Count - 1);
        if (originalIndex == newIndex)
        {
            UpdateThumbnailNumbers();
            ResetReorderState();
            return;
        }

        try
        {
            _setCurrentPageIndex(GetReorderedPageIndex(
                pageIndexBeforeReorder, originalIndex, newIndex));
            thumbnails.Move(originalIndex, newIndex);
            UpdateThumbnailNumbers();
            _preserveThumbnailsAfterReorder = true;
            _getManager().MovePage(originalIndex, newIndex);
            UpdateAnnotationPageIndices(originalIndex, newIndex);
            _statusText.Text = "페이지 순서가 변경되었습니다";
            SyncSelection();
        }
        catch (Exception ex)
        {
            thumbnails.Move(newIndex, originalIndex);
            UpdateThumbnailNumbers();
            _preserveThumbnailsAfterReorder = false;
            _setCurrentPageIndex(pageIndexBeforeReorder);
            _statusText.Text = "페이지 순서 변경에 실패했습니다";
            System.Diagnostics.Debug.WriteLine($"Move page error: {ex.Message}");
            SyncSelection();
        }
        finally
        {
            ClearDropVisual();
            ResetReorderState();
        }
    }

    public bool IsPageListDrag(DragEventArgs e)
    {
        if (_isReordering || _pointerPressed)
            return true;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (ReferenceEquals(source, _pageListView))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    public bool ConsumePreserveThumbnailsAfterReorder()
    {
        bool preserve = _preserveThumbnailsAfterReorder;
        _preserveThumbnailsAfterReorder = false;
        return preserve;
    }

    public List<int> GetSelectedPageIndices() =>
        _pageListView.SelectedItems
            .OfType<PageThumbnailData>()
            .Select(thumbnail => thumbnail.PageIndex)
            .Where(index => index >= 0 && index < _getManager().PageCount)
            .Distinct()
            .OrderBy(index => index)
            .ToList();

    public void SyncSelection()
    {
        int currentPageIndex = _getCurrentPageIndex();
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();
        if (currentPageIndex < thumbnails.Count)
        {
            PageThumbnailData current = thumbnails[currentPageIndex];
            if (_pageListView.SelectedItems.Count <= 1)
                _pageListView.SelectedItem = current;
            _pageListView.ScrollIntoView(current);
        }

        _updateUiState();
    }

    public void ZoomIn()
    {
        _setZoomLevel(Math.Min(_getZoomLevel() + 0.25, 5.0));
        ApplyZoom();
    }

    public void ZoomOut()
    {
        _setZoomLevel(Math.Max(_getZoomLevel() - 0.25, 0.25));
        ApplyZoom();
    }

    public void FitToPage()
    {
        if (!_getManager().IsLoaded || _overlayCanvas.Width <= 0)
            return;

        double viewportWidth = _scrollViewer.ViewportWidth > 0
            ? _scrollViewer.ViewportWidth
            : _scrollViewer.ActualWidth;
        double viewportHeight = _scrollViewer.ViewportHeight > 0
            ? _scrollViewer.ViewportHeight
            : _scrollViewer.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        double zoomWidth = (viewportWidth - 20) / _overlayCanvas.Width;
        double zoomHeight = (viewportHeight - 20) / _overlayCanvas.Height;
        _setZoomLevel(Math.Clamp(Math.Min(zoomWidth, zoomHeight), 0.1, 5.0));
        ApplyZoom();
    }

    public void ViewChanged()
    {
        _setZoomLevel(_scrollViewer.ZoomFactor);
        _zoomText.Text = $"{(int)(_getZoomLevel() * 100)}%";
    }

    private void ApplyZoom()
    {
        _scrollViewer.ChangeView(null, null, (float)_getZoomLevel());
        _zoomText.Text = $"{(int)(_getZoomLevel() * 100)}%";
    }

    private int GetDropIndex(Point position)
    {
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();
        for (int i = 0; i < thumbnails.Count; i++)
        {
            if (_pageListView.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            Point topLeft = container.TransformToVisual(_pageListView)
                .TransformPoint(new Point(0, 0));
            if (position.Y < topLeft.Y + container.ActualHeight / 2)
                return i;
        }

        return thumbnails.Count;
    }

    private void UpdateDropVisual()
    {
        const double normalMargin = 8;
        const double dropGap = 36;
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();
        for (int i = 0; i < thumbnails.Count; i++)
        {
            double top = _dropIndex == i ? dropGap : 0;
            double bottom = _dropIndex == thumbnails.Count && i == thumbnails.Count - 1
                ? dropGap
                : 0;
            thumbnails[i].DropMargin = new Thickness(
                normalMargin,
                normalMargin + top,
                normalMargin,
                normalMargin + bottom);
        }
    }

    private void ClearDropVisual()
    {
        foreach (PageThumbnailData thumbnail in _getThumbnails())
            thumbnail.DropMargin = new Thickness(8);
    }

    private static ListViewItem? FindPageListItem(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ListViewItem item)
                return item;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void UpdateAnnotationPageIndices(int oldIndex, int newIndex)
    {
        foreach (PdfAnnotation annotation in _getAnnotations())
        {
            annotation.PageIndex = GetReorderedPageIndex(
                annotation.PageIndex, oldIndex, newIndex);
        }
    }

    private void UpdateThumbnailNumbers()
    {
        ObservableCollection<PageThumbnailData> thumbnails = _getThumbnails();
        for (int i = 0; i < thumbnails.Count; i++)
            thumbnails[i].PageNumber = i + 1;
    }

    private void ResetReorderState()
    {
        _draggedThumbnail = null;
        _draggedOriginalIndex = -1;
        _pageIndexBeforeReorder = -1;
        _dropIndex = -1;
        _isReordering = false;
        _pointerPressed = false;
    }

    private static int GetReorderedPageIndex(int currentIndex, int oldIndex, int newIndex)
    {
        if (currentIndex == oldIndex)
            return newIndex;
        if (oldIndex < currentIndex && currentIndex <= newIndex)
            return currentIndex - 1;
        if (newIndex <= currentIndex && currentIndex < oldIndex)
            return currentIndex + 1;
        return currentIndex;
    }
}
