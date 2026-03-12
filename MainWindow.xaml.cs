using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace PDF_simple_edit
{
    public sealed partial class MainWindow : Window
    {
        private readonly PdfDocumentManager _pdfManager = new();
        private readonly PrintHelper _printHelper = new();
        private readonly ObservableCollection<PageThumbnailData> _pageThumbnails = new();
        private readonly List<PdfAnnotation> _annotations = new();
        private readonly TextFontSettings _fontSettings = new();

        private int _currentPageIndex = 0;
        private double _zoomLevel = 1.0;
        private double _renderScale = 2.0;
        private EditToolMode _currentTool = EditToolMode.None;
        private string? _renderTempPath;
        private readonly List<string> _recentFiles = new();
        private const int MaxRecentFiles = 10;

        // For highlight drag
        private bool _isDragging;
        private Windows.Foundation.Point _dragStart;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _dragRect;
        private bool _isDialogOpen = false;
        private bool _isInlineEditing = false;
        private bool _isFirstLoad = false;
        
        // PDF는 72 DPI, Windows 논리 픽셀은 96 DPI입니다.
        private const double PdfToPixels = 96.0 / 72.0;

        public MainWindow()
        {
            try
            {
                InitializeComponent();

                // Set up events
                _pdfManager.DocumentChanged += PdfManager_DocumentChanged;
                _pdfManager.ModifiedStateChanged += PdfManager_ModifiedStateChanged;

                PageListView.ItemsSource = _pageThumbnails;

                // Initialize font settings
                _fontSettings.FontFamily = "맑은 고딕";
                _fontSettings.FontSize = 12;
                _fontSettings.Color = "#000000";

                // Initialize color palette programmatically
                InitializeColorPalette();

                LoadRecentFiles();
                UpdateRecentFilesMenu();
                InitializeZoomAccelerators();

                Activated += MainWindow_Activated;
                Closed += MainWindow_Closed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MainWindow constructor error: {ex}");
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                // Only run once
                Activated -= MainWindow_Activated;

                try
                {
                    // Set title bar
                    ExtendsContentIntoTitleBar = true;
                    SetTitleBar(AppTitleBar);

                    // Set window icon and position
                    var hwnd = WindowNative.GetWindowHandle(this);
                    if (hwnd != IntPtr.Zero)
                    {
                        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = AppWindow.GetFromWindowId(windowId);
                        if (appWindow != null)
                        {
                            appWindow.SetIcon("Assets/app.ico");
                        }
                        LoadWindowPosition();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MainWindow activation error: {ex}");
                }
            }
        }

        private void InitializeColorPalette()
        {
            if (ColorPalette == null) return;

            var colors = new[]
            {
                "#000000", "#FFFFFF", "#FF0000", "#FF6600", "#FFCC00", "#00CC00",
                "#0066FF", "#9900FF", "#333333", "#666666", "#999999", "#CC3333",
                "#FF9933", "#FFFF66", "#66CC66", "#3399FF", "#CC66FF", "#FF6699"
            };

            foreach (var color in colors)
            {
                var border = new Border
                {
                    Width = 28, Height = 28,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(ParseColor(color)),
                    Tag = color
                };
                if (color == "#FFFFFF")
                {
                    border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                    border.BorderThickness = new Thickness(1);
                }
                ColorPalette.Items.Add(border);
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (_renderTempPath != null && _renderTempPath.Contains(Path.GetTempPath()) && File.Exists(_renderTempPath))
            {
                try { File.Delete(_renderTempPath); } catch { }
            }

            try { SaveWindowPosition(); } catch { }
        }

        #region Document Events

private void PdfManager_DocumentChanged(object? sender, EventArgs e)
{
    DispatcherQueue.TryEnqueue(async () =>
    {
        UpdateUIState();
        if (_pdfManager.IsLoaded)
        {
            if (_pageThumbnails.Count != _pdfManager.PageCount)
            {
                await LoadThumbnailsAsync();
            }
            
            await RenderCurrentPageAsync();

            if (_isFirstLoad)
            {
                _isFirstLoad = false;
                
                // [핵심 3] WinUI 레이아웃 엔진이 크기 할당을 끝낼 때까지 대기
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(100);
                    // ScrollViewer와 Canvas에 실제 크기가 부여되었을 때 꽉 채우기 실행
                    if (PdfScrollViewer.ViewportWidth > 0 && OverlayCanvas.Width > 0)
                    {
                        FitToPage();
                        break;
                    }
                }
            }
        }
    });
}

        private void PdfManager_ModifiedStateChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(UpdateTitleBar);
        }

        private void UpdateUIState()
        {
            bool hasDoc = _pdfManager.IsLoaded;

            WelcomePanel.Visibility = hasDoc ? Visibility.Collapsed : Visibility.Visible;
            PdfScrollViewer.Visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;

            MenuSave.IsEnabled = hasDoc;
            MenuSaveAs.IsEnabled = hasDoc;
            MenuPrint.IsEnabled = hasDoc;
            MenuClose.IsEnabled = hasDoc;
            MenuFind.IsEnabled = hasDoc;
            MenuFindReplace.IsEnabled = hasDoc;
            MenuAddText.IsEnabled = hasDoc;
            MenuHighlight.IsEnabled = hasDoc;
            MenuStickyNote.IsEnabled = hasDoc;
            MenuAddImage.IsEnabled = hasDoc;
            MenuSplitPdf.IsEnabled = hasDoc;
            MenuDeletePage.IsEnabled = hasDoc;

            BtnSave.IsEnabled = hasDoc;
            BtnPrint.IsEnabled = hasDoc;
            BtnSelect.IsEnabled = hasDoc;
            BtnAddText.IsEnabled = hasDoc;
            BtnHighlight.IsEnabled = hasDoc;
            BtnStickyNote.IsEnabled = hasDoc;
            BtnAddImage.IsEnabled = hasDoc;

            BtnPrevPage.IsEnabled = hasDoc && _currentPageIndex > 0;
            BtnNextPage.IsEnabled = hasDoc && _currentPageIndex < _pdfManager.PageCount - 1;

            if (hasDoc)
            {
                TxtPageInfo.Text = $"페이지: {_currentPageIndex + 1} / {_pdfManager.PageCount}";
                TxtStatus.Text = "준비";

                if (_pdfManager.FilePath != null && File.Exists(_pdfManager.FilePath))
                {
                    var fi = new FileInfo(_pdfManager.FilePath);
                    TxtFileInfo.Text = $"{fi.Name} ({FormatFileSize(fi.Length)})";
                }
                else
                {
                    TxtFileInfo.Text = "새 문서";
                }
            }
            else
            {
                TxtPageInfo.Text = "페이지: 0 / 0";
                TxtStatus.Text = "준비";
                TxtFileInfo.Text = "";
            }

            TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void UpdateTitleBar()
        {
            string title = "PDF Editor";
            if (_pdfManager.IsLoaded)
            {
                string fileName = _pdfManager.FilePath != null
                    ? Path.GetFileName(_pdfManager.FilePath) : "새 문서";
                title = $"{(_pdfManager.IsModified ? "● " : "")}{fileName} - PDF Editor";
            }
            TitleText.Text = title;
            Title = title;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1048576.0:F1} MB";
        }

        #endregion

        #region File Operations

        private async void NewDocument_Click(object sender, RoutedEventArgs e)
        {
            _pdfManager.NewDocument();
            _currentPageIndex = 0;
            _annotations.Clear();
            await SaveToTempAndRenderAsync();
            UpdateUIState();
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".pdf");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await OpenPdfFileAsync(file);
            }
        }

        private async Task OpenPdfFileAsync(StorageFile file)
        {
            LoadingRing.IsActive = true;
            TxtStatus.Text = "파일을 여는 중...";

            try
            {
                bool success = await _pdfManager.OpenAsync(file.Path);
                if (success)
                {
                    _currentPageIndex = 0;
                    _annotations.Clear();
                    _isFirstLoad = true;
                    _renderTempPath = null;
                    AddToRecentFiles(file.Path);
                }
                else
                {
                    await ShowErrorDialogAsync("오류", "PDF 파일을 열 수 없습니다.");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("오류", $"파일을 여는 중 오류가 발생했습니다: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
                UpdateUIState();
            }
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "PDF 열기";
            e.DragUIOverride.IsCaptionVisible = true;
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    await OpenPdfFileAsync(file);
                }
            }
        }

        private async void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded) return;

            ApplyAllAnnotations();

            if (_pdfManager.FilePath != null)
            {
                TxtStatus.Text = "저장 중...";
                bool success = await _pdfManager.SaveAsync();
                TxtStatus.Text = success ? "저장됨" : "저장 실패";

                if (success)
                {
                    _renderTempPath = _pdfManager.FilePath;
                    await RenderCurrentPageAsync();
                    AddToRecentFiles(_pdfManager.FilePath);
                }
            }
            else
            {
                SaveAsFile_Click(sender, e);
            }
        }

        private async void SaveAsFile_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded) return;

            ApplyAllAnnotations();

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
            picker.SuggestedFileName = _pdfManager.FilePath != null
                ? Path.GetFileName(_pdfManager.FilePath) : "새문서.pdf";

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                TxtStatus.Text = "저장 중...";
                try
                {
                    bool success = await _pdfManager.SaveAsAsync(file.Path);
                    if (success)
                    {
                        await RenderCurrentPageAsync();
                        TxtStatus.Text = "다른 이름으로 저장됨";
                        AddToRecentFiles(file.Path);
                    }
                    else
                    {
                        TxtStatus.Text = "저장 실패";
                        await ShowErrorDialogAsync("오류", "파일을 저장할 수 없습니다. 다른 프로그램에서 사용 중인지 확인하세요.");
                    }
                }
                catch (Exception ex)
                {
                    TxtStatus.Text = "저장 오류";
                    await ShowErrorDialogAsync("오류", $"저장 중 오류가 발생했습니다: {ex.Message}");
                }
                finally
                {
                    UpdateUIState();
                }
            }
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e)
        {
            _pdfManager.Close();
            _currentPageIndex = 0;
            _annotations.Clear();
            _pageThumbnails.Clear();
            SetToolMode(EditToolMode.None);
            UpdateUIState();
            UpdateTitleBar();
        }

        private async void Print_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded || _pdfManager.Document == null) return;

            try
            {
                string tempPath = _renderTempPath ?? _pdfManager.FilePath ?? "";
                if (string.IsNullOrEmpty(tempPath))
                {
                    tempPath = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid()}.pdf");
                    await _pdfManager.SaveAsAsync(tempPath, false);
                }

                var hwnd = WindowNative.GetWindowHandle(this);
                await _printHelper.PrintAsync(_pdfManager.Document, tempPath, hwnd);
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("인쇄 오류", $"인쇄 중 오류가 발생했습니다: {ex.Message}");
            }
        }

        #endregion

        #region Page Rendering

private async Task RenderCurrentPageAsync()
{
    if (!_pdfManager.IsLoaded) return;

    string oldStatus = TxtStatus.Text;
    try
    {
        TxtStatus.Text = "페이지 렌더링 중...";
        if (_pdfManager.IsModified || _renderTempPath == null || !File.Exists(_renderTempPath))
        {
            string? oldPath = _renderTempPath;
            _renderTempPath = Path.Combine(Path.GetTempPath(), $"pdfedit_render_{Guid.NewGuid()}.pdf");
            
            bool saved = await _pdfManager.SaveAsAsync(_renderTempPath, false);
            if (!saved) _renderTempPath = _pdfManager.FilePath;
            else if (oldPath != null && oldPath.Contains(Path.GetTempPath()))
            {
                try { File.Delete(oldPath); } catch { }
            }
        }

        if (_renderTempPath == null) return;

        var ms = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(
            _renderTempPath, _currentPageIndex, _renderScale * PdfToPixels);

        if (ms != null)
        {
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
            PdfPageImage.Source = bitmap;

            // [절대 법칙 적용] 비트맵의 픽셀을 무시하고, PDF 종이 크기를 강제로 UI 픽셀로 환산
            var pageSize = _pdfManager.GetPageSize(_currentPageIndex);
            double logicalWidth = pageSize.width * PdfToPixels;
            double logicalHeight = pageSize.height * PdfToPixels;

            // 정렬을 '좌상단'으로 묶어버림 (중앙 정렬 시 발생하는 좌표 틀어짐 방지)
            PdfPageImage.HorizontalAlignment = HorizontalAlignment.Left;
            PdfPageImage.VerticalAlignment = VerticalAlignment.Top;
            OverlayCanvas.HorizontalAlignment = HorizontalAlignment.Left;
            OverlayCanvas.VerticalAlignment = VerticalAlignment.Top;

            PdfPageImage.Margin = new Thickness(0);
            OverlayCanvas.Margin = new Thickness(0);

            // 이미지와 캔버스의 크기를 소수점 단위까지 100% 동일하게 강제
            PdfPageImage.Width = logicalWidth;
            PdfPageImage.Height = logicalHeight;
            OverlayCanvas.Width = logicalWidth;
            OverlayCanvas.Height = logicalHeight;
            
            PdfPageImage.Stretch = Stretch.Fill;

            RenderAnnotationOverlays();
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
    }
    finally
    {
        TxtStatus.Text = oldStatus == "페이지 렌더링 중..." ? "준비" : oldStatus;
    }

    UpdateUIState();
    SyncPageListSelection();
}

        private async Task SaveToTempAndRenderAsync()
        {
            await RenderCurrentPageAsync();
        }

        private async Task LoadThumbnailsAsync()
        {
            _pageThumbnails.Clear();

            if (!_pdfManager.IsLoaded) return;

            string? filePath = _renderTempPath ?? _pdfManager.FilePath;
            if (filePath == null) return;

            for (int i = 0; i < _pdfManager.PageCount; i++)
            {
                try
                {
                    var ms = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(filePath, i, 0.4);
                    if (ms != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                        _pageThumbnails.Add(new PageThumbnailData { PageNumber = i + 1, Thumbnail = bitmap });
                    }
                    else
                    {
                        _pageThumbnails.Add(new PageThumbnailData { PageNumber = i + 1 });
                    }
                }
                catch
                {
                    _pageThumbnails.Add(new PageThumbnailData { PageNumber = i + 1 });
                }
            }
        }

        #endregion

        #region Page Navigation

        private async void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPageIndex > 0)
            {
                _currentPageIndex--;
                await RenderCurrentPageAsync();
                SyncPageListSelection();
            }
        }

        private async void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPageIndex < _pdfManager.PageCount - 1)
            {
                _currentPageIndex++;
                await RenderCurrentPageAsync();
                SyncPageListSelection();
            }
        }

        private async void GoToPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (int.TryParse(TxtGoToPage.Text, out int pageNum) &&
                    pageNum >= 1 && pageNum <= _pdfManager.PageCount)
                {
                    _currentPageIndex = pageNum - 1;
                    await RenderCurrentPageAsync();
                    SyncPageListSelection();
                    TxtGoToPage.Text = "";
                }
            }
        }

        private async void PageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageListView.SelectedItem is PageThumbnailData thumb)
            {
                if (_currentPageIndex != thumb.PageIndex)
                {
                    _currentPageIndex = thumb.PageIndex;
                    await RenderCurrentPageAsync();
                }
            }
        }

        private void SyncPageListSelection()
        {
            if (_currentPageIndex < _pageThumbnails.Count)
            {
                PageListView.SelectedIndex = _currentPageIndex;
                PageListView.ScrollIntoView(PageListView.SelectedItem);
            }
        }

        #endregion

        #region Zoom

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = Math.Min(_zoomLevel + 0.25, 5.0);
            ApplyZoom();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoomLevel = Math.Max(_zoomLevel - 0.25, 0.25);
            ApplyZoom();
        }

        private void FitToPage_Click(object sender, RoutedEventArgs e)
        {
            FitToPage();
        }

private void FitToPage()
{
    if (PdfScrollViewer == null || !_pdfManager.IsLoaded || OverlayCanvas.Width <= 0) return;

    double contentW = OverlayCanvas.Width;
    double contentH = OverlayCanvas.Height;

    double viewportW = PdfScrollViewer.ViewportWidth;
    double viewportH = PdfScrollViewer.ViewportHeight;
    
    if (viewportW <= 0) viewportW = PdfScrollViewer.ActualWidth;
    if (viewportH <= 0) viewportH = PdfScrollViewer.ActualHeight;

    if (viewportW <= 0 || viewportH <= 0) return;

    // 계산 여백을 20px로 줄여 화면에 최대한 꽉 차게 배율 계산
    double zoomW = (viewportW - 20) / contentW;
    double zoomH = (viewportH - 20) / contentH;
    
    _zoomLevel = Math.Min(zoomW, zoomH);
    _zoomLevel = Math.Clamp(_zoomLevel, 0.1, 5.0);
    
    ApplyZoom();
}

        private void ApplyZoom()
        {
            PdfScrollViewer?.ChangeView(null, null, (float)_zoomLevel);
            TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void PdfScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (PdfScrollViewer != null)
            {
                _zoomLevel = PdfScrollViewer.ZoomFactor;
                TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
            }
        }

        private void InitializeZoomAccelerators()
        {
            // 확대 (+ or =)
            var keysIn = new[] { Windows.System.VirtualKey.Add, (Windows.System.VirtualKey)187 };
            foreach (var key in keysIn)
            {
                var acc = new KeyboardAccelerator { Key = key };
                acc.Invoked += ToolAccelerator_Invoked;
                MenuZoomIn.KeyboardAccelerators.Add(acc);
            }

            // 축소 (-)
            var keysOut = new[] { Windows.System.VirtualKey.Subtract, (Windows.System.VirtualKey)189 };
            foreach (var key in keysOut)
            {
                var acc = new KeyboardAccelerator { Key = key };
                acc.Invoked += ToolAccelerator_Invoked;
                MenuZoomOut.KeyboardAccelerators.Add(acc);
            }
        }

        #endregion

        #region Tool Modes

        private void SetToolMode(EditToolMode mode)
        {
            _currentTool = mode;

            BtnSelect.IsChecked = mode == EditToolMode.Select;
            BtnAddText.IsChecked = mode == EditToolMode.AddText;
            BtnHighlight.IsChecked = mode == EditToolMode.Highlight;
            BtnStickyNote.IsChecked = mode == EditToolMode.AddStickyNote;

            if (MenuAddText != null) MenuAddText.IsChecked = mode == EditToolMode.AddText;
            if (MenuHighlight != null) MenuHighlight.IsChecked = mode == EditToolMode.Highlight;
            if (MenuStickyNote != null) MenuStickyNote.IsChecked = mode == EditToolMode.AddStickyNote;

            TxtToolMode.Text = mode switch
            {
                EditToolMode.AddText => "도구: 텍스트 추가",
                EditToolMode.Highlight => "도구: 텍스트 강조",
                EditToolMode.AddStickyNote => "도구: 스티커 노트",
                EditToolMode.AddImage => "도구: 이미지 추가",
                EditToolMode.Select => "도구: 선택",
                _ => ""
            };

            UpdateCursor(mode);
        }

        private void UpdateCursor(EditToolMode mode)
        {
            if (OverlayCanvas == null) return;
        }

        private void SelectTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(BtnSelect.IsChecked == true ? EditToolMode.Select : EditToolMode.None);
        }

        private void AddTextTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(_currentTool == EditToolMode.AddText ? EditToolMode.None : EditToolMode.AddText);
        }

        private void HighlightTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(_currentTool == EditToolMode.Highlight ? EditToolMode.None : EditToolMode.Highlight);
        }

        private void StickyNoteTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(_currentTool == EditToolMode.AddStickyNote ? EditToolMode.None : EditToolMode.AddStickyNote);
        }

        private void ToolAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            // Focus check to prevent tool activation while typing
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox || focused is NumberBox || _isInlineEditing)
            {
                args.Handled = true; // Consume the accelerator so it doesn't trigger the click, but let the key event continue if needed
                return;
            }
        }

        private async void AddImageTool_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded) return;

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var pageSize = _pdfManager.GetPageSize(_currentPageIndex);

                _pdfManager.AddImage(_currentPageIndex, file.Path,
                    (pageSize.width - 150) / 2, (pageSize.height - 150) / 2, 150, 150);

                await SaveToTempAndRenderAsync();
                TxtStatus.Text = "이미지가 추가되었습니다";
            }
        }

        #endregion

        #region Canvas Interaction

private async void OverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
{
    if (!_pdfManager.IsLoaded) return;

    // 안전장치: 캔버스 크기가 없을 경우 이미지 크기로 동기화
    if (OverlayCanvas.Width == 0 || double.IsNaN(OverlayCanvas.Width))
    {
        if (PdfPageImage.ActualWidth > 0)
        {
            OverlayCanvas.Width = PdfPageImage.ActualWidth;
            OverlayCanvas.Height = PdfPageImage.ActualHeight;
        }
    }

    var pos = e.GetCurrentPoint(OverlayCanvas).Position;
    double pdfX = pos.X / PdfToPixels;
    double pdfY = pos.Y / PdfToPixels;

    switch (_currentTool)
    {
        case EditToolMode.AddText:
            // [수정] 폰트 크기에 따른 임의의 Y좌표 보정을 완전히 제거하여
            // 클릭한 위치에 정확하게 텍스트 박스가 생성되도록 합니다.
            AddInlineTextBox(pdfX, pdfY);
            break;

        case EditToolMode.AddStickyNote:
            await ShowStickyNoteDialogAsync(pdfX, pdfY);
            break;

        case EditToolMode.Highlight:
            _isDragging = true;
            _dragStart = pos;
            _dragRect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                Opacity = 0.3,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                StrokeThickness = 1
            };
            Canvas.SetLeft(_dragRect, pos.X);
            Canvas.SetTop(_dragRect, pos.Y);
            OverlayCanvas.Children.Add(_dragRect);
            OverlayCanvas.CapturePointer(e.Pointer);
            break;
    }
}

        private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging && _dragRect != null)
            {
                var pos = e.GetCurrentPoint(OverlayCanvas).Position;
                double x = Math.Min(pos.X, _dragStart.X);
                double y = Math.Min(pos.Y, _dragStart.Y);
                double w = Math.Abs(pos.X - _dragStart.X);
                double h = Math.Abs(pos.Y - _dragStart.Y);

                Canvas.SetLeft(_dragRect, x);
                Canvas.SetTop(_dragRect, y);
                _dragRect.Width = w;
                _dragRect.Height = h;
            }
        }

        private async void OverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging && _dragRect != null && _currentTool == EditToolMode.Highlight)
            {
                _isDragging = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);

                double x = Canvas.GetLeft(_dragRect) / PdfToPixels;
                double y = Canvas.GetTop(_dragRect) / PdfToPixels;
                double w = _dragRect.Width / PdfToPixels;
                double h = _dragRect.Height / PdfToPixels;

                if (w > 5 && h > 5)
                {
                    _pdfManager.AddHighlight(_currentPageIndex, x, y, w, h,
                        XColor.FromArgb(255, 255, 255, 0), 0.3);

                    _annotations.Add(new PdfAnnotation
                    {
                        Type = AnnotationType.Highlight,
                        PageIndex = _currentPageIndex,
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h,
                        Color = "#FFFF00",
                        Opacity = 0.3,
                        IsApplied = true
                    });

                    TxtStatus.Text = "텍스트 강조가 추가되었습니다";
                    RenderAnnotationOverlays();
                }

                OverlayCanvas.Children.Remove(_dragRect);
                _dragRect = null;
            }
        }

        private void RenderAnnotationOverlays()
        {
            OverlayCanvas.Children.Clear();

            var pageAnnotations = _annotations.Where(a => a.PageIndex == _currentPageIndex).ToList();

            foreach (var ann in pageAnnotations)
            {
                switch (ann.Type)
                {
                    case AnnotationType.Text:
                    case AnnotationType.FreeText:
                        var tb = new TextBlock
                        {
                            Text = ann.Content,
                            TextWrapping = TextWrapping.NoWrap,
                            FontSize = ann.FontSize * PdfToPixels,
                            Foreground = new SolidColorBrush(ParseColor(ann.Color)),
                            FontWeight = ann.IsBold
                                ? Microsoft.UI.Text.FontWeights.Bold
                                : Microsoft.UI.Text.FontWeights.Normal,
                            FontStyle = ann.IsItalic
                                ? Windows.UI.Text.FontStyle.Italic
                                : Windows.UI.Text.FontStyle.Normal,
                            Padding = new Thickness(0),
                            Margin = new Thickness(0)
                        };
                        Canvas.SetLeft(tb, ann.X * PdfToPixels);
                        Canvas.SetTop(tb, ann.Y * PdfToPixels);
                        OverlayCanvas.Children.Add(tb);
                        break;

                    case AnnotationType.Highlight:
                        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                        {
                            Width = ann.Width * PdfToPixels,
                            Height = ann.Height * PdfToPixels,
                            Fill = new SolidColorBrush(ParseColor(ann.Color)),
                            Opacity = ann.Opacity
                        };
                        Canvas.SetLeft(rect, ann.X * PdfToPixels);
                        Canvas.SetTop(rect, ann.Y * PdfToPixels);
                        OverlayCanvas.Children.Add(rect);
                        break;

                    case AnnotationType.StickyNote:
                        var snGrid = new Grid
                        {
                            Width = 24 * PdfToPixels,
                            Height = 24 * PdfToPixels,
                            Background = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                            BorderThickness = new Thickness(1)
                        };
                        ToolTipService.SetToolTip(snGrid, ann.Content);
                        snGrid.Children.Add(new FontIcon { Glyph = "\uE70B", FontSize = 12 * PdfToPixels });
                        Canvas.SetLeft(snGrid, ann.X * PdfToPixels);
                        Canvas.SetTop(snGrid, ann.Y * PdfToPixels);
                        OverlayCanvas.Children.Add(snGrid);
                        break;
                }
            }
        }

        #endregion

        #region Text Input (Inline & Dialog)

private void AddInlineTextBox(double pdfX, double pdfY)
{
    if (_isInlineEditing) return;
    _isInlineEditing = true;

    var canvasX = pdfX * PdfToPixels;
    var canvasY = pdfY * PdfToPixels;

    var textBox = new TextBox
    {
        AcceptsReturn = false,
        TextWrapping = TextWrapping.NoWrap,
        // [핵심] WinUI TextBox의 기본 여백 공간 강제 초기화
        MinWidth = 0,
        MinHeight = 0, 
        Padding = new Thickness(0),
        Margin = new Thickness(-1, -1, 0, 0),
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
        FontSize = _fontSettings.FontSize * PdfToPixels,
        FontFamily = new FontFamily(_fontSettings.FontFamily),
        Foreground = new SolidColorBrush(ParseColor(_fontSettings.Color)),
        FontWeight = _fontSettings.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        FontStyle = _fontSettings.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        Tag = new Windows.Foundation.Point(pdfX, pdfY),
        VerticalAlignment = VerticalAlignment.Top,
        VerticalContentAlignment = VerticalAlignment.Top,
        MaxWidth = 4000
    };

    Canvas.SetLeft(textBox, canvasX);
    Canvas.SetTop(textBox, canvasY);

    textBox.Loaded += (s, e) => textBox.Focus(FocusState.Programmatic);

    textBox.KeyDown += (s, e) =>
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyInlineText((TextBox)s);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _isInlineEditing = false;
            OverlayCanvas.Children.Remove(textBox);
            e.Handled = true;
        }
    };

    textBox.LostFocus += (s, e) =>
    {
        ApplyInlineText((TextBox)s);
    };

    OverlayCanvas.Children.Add(textBox);
}

        private async void ApplyInlineText(TextBox textBox)
        {
            if (!_isInlineEditing || !OverlayCanvas.Children.Contains(textBox)) return;

            string text = textBox.Text;
            var pdfPos = (Windows.Foundation.Point)textBox.Tag;

            OverlayCanvas.Children.Remove(textBox);
            _isInlineEditing = false;

            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                string fontFamily = _fontSettings.FontFamily;
                double fontSize = _fontSettings.FontSize;
                bool isBold = _fontSettings.IsBold;
                bool isItalic = _fontSettings.IsItalic;
                string selectedColor = _fontSettings.Color;

                var newAnn = new PdfAnnotation
                {
                    Type = AnnotationType.Text,
                    PageIndex = _currentPageIndex,
                    X = pdfPos.X,
                    Y = pdfPos.Y,
                    Content = text,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    Color = selectedColor,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    IsApplied = true
                };
                _annotations.Add(newAnn);

                var xColor = ConvertToXColor(selectedColor);

                // [추가] PDF 엔진이 글자를 밑으로 밀어내며 그리는 현상을 상쇄하기 위해
// PDF 파일에 기록할 때만 Y좌표를 폰트 크기의 약 1.8배만큼 위로 끌어올립니다.
double pdfEngineY = pdfPos.Y - (fontSize * 2.21);

// pdfPos.Y 대신 pdfEngineY를 전달
_pdfManager.AddText(_currentPageIndex, pdfPos.X, pdfEngineY, text,
    fontFamily, fontSize, xColor, isBold, isItalic);


                TxtStatus.Text = "텍스트가 추가되었습니다";

                RenderAnnotationOverlays();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyInlineText Error: {ex}");
                await ShowErrorDialogAsync("오류", $"텍스트 추가 중 오류가 발생했습니다: {ex.Message}");
            }
        }

        private async Task ShowTextInputDialogAsync(double pdfX, double pdfY)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "텍스트 추가",
                    PrimaryButtonText = "추가",
                    CloseButtonText = "취소",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

            var panel = new StackPanel { Spacing = 8, MinWidth = 400 };

            var txtContent = new TextBox
            {
                PlaceholderText = "텍스트 입력...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
                MaxHeight = 200
            };
            txtContent.Loaded += (s, args) => txtContent.Focus(FocusState.Programmatic);
            panel.Children.Add(new TextBlock { Text = "텍스트:" });
            panel.Children.Add(txtContent);

            var cmbFont = new ComboBox { PlaceholderText = "폰트", Width = 200 };
            foreach (var font in new[] { "맑은 고딕", "굴림", "돋움", "바탕", "궁서",
                "나눔고딕", "나눔명조", "Arial", "Times New Roman", "Courier New", "Calibri" })
            {
                cmbFont.Items.Add(font);
            }
            cmbFont.SelectedItem = _fontSettings.FontFamily;

            var nbSize = new NumberBox
            {
                Value = _fontSettings.FontSize, Minimum = 6, Maximum = 144,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 100
            };

            var chkBold = new CheckBox { Content = "굵게", IsChecked = _fontSettings.IsBold };
            var chkItalic = new CheckBox { Content = "기울임", IsChecked = _fontSettings.IsItalic };

            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            fontRow.Children.Add(cmbFont);
            fontRow.Children.Add(nbSize);
            fontRow.Children.Add(chkBold);
            fontRow.Children.Add(chkItalic);

            panel.Children.Add(new TextBlock { Text = "폰트 설정:", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(fontRow);

            string selectedColor = _fontSettings.Color;
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            colorRow.Children.Add(new TextBlock { Text = "색상:", VerticalAlignment = VerticalAlignment.Center });

            var colorValues = new[] { "#000000", "#FF0000", "#0066FF", "#00CC00", "#FF6600", "#9900FF", "#CC3333" };
            foreach (var color in colorValues)
            {
                var colorBtn = new Button
                {
                    Width = 28, Height = 28,
                    Background = new SolidColorBrush(ParseColor(color)),
                    Tag = color, Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = color == selectedColor ? new Thickness(2) : new Thickness(0),
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White)
                };
                colorBtn.Click += (s, _) =>
                {
                    selectedColor = (string)((Button)s).Tag;
                    foreach (var child in colorRow.Children.OfType<Button>())
                    {
                        child.BorderThickness = new Thickness(
                            (string)child.Tag == selectedColor ? 2 : 0);
                    }
                };
                colorRow.Children.Add(colorBtn);
            }
            panel.Children.Add(colorRow);

            dialog.Content = panel;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(txtContent.Text))
            {
                string fontFamily = cmbFont.SelectedItem?.ToString() ?? _fontSettings.FontFamily;
                double fontSize = nbSize.Value;
                bool isBold = chkBold.IsChecked == true;
                bool isItalic = chkItalic.IsChecked == true;

                var xColor = ConvertToXColor(selectedColor);

                _pdfManager.AddText(_currentPageIndex, pdfX, pdfY, txtContent.Text,
                    fontFamily, fontSize, xColor, isBold, isItalic);

                var newAnn = new PdfAnnotation
                {
                    Type = AnnotationType.Text,
                    PageIndex = _currentPageIndex,
                    X = pdfX,
                    Y = pdfY,
                    Content = txtContent.Text,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    Color = selectedColor,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    IsApplied = true
                };
                
                _annotations.Add(newAnn);

                _fontSettings.FontFamily = fontFamily;
                _fontSettings.FontSize = fontSize;
                _fontSettings.IsBold = isBold;
                _fontSettings.IsItalic = isItalic;
                _fontSettings.Color = selectedColor;

                TxtStatus.Text = "텍스트가 추가되었습니다";
                RenderAnnotationOverlays();

                await RenderCurrentPageAsync();
            }
        }
        finally
            {
                _isDialogOpen = false;
            }
        }

        private async Task ShowStickyNoteDialogAsync(double pdfX, double pdfY)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "스티커 노트 추가",
                    PrimaryButtonText = "추가",
                    CloseButtonText = "취소",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

            var txtContent = new TextBox
            {
                PlaceholderText = "메모 내용을 입력하세요...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 100,
                MaxHeight = 200,
                MinWidth = 300
            };
            txtContent.Loaded += (s, args) => txtContent.Focus(FocusState.Programmatic);

            dialog.Content = txtContent;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(txtContent.Text))
            {
                _pdfManager.AddStickyNote(_currentPageIndex, pdfX, pdfY, txtContent.Text,
                    _fontSettings.FontFamily, _fontSettings.FontSize);

                _annotations.Add(new PdfAnnotation
                {
                    Type = AnnotationType.StickyNote,
                    PageIndex = _currentPageIndex,
                    X = pdfX,
                    Y = pdfY,
                    Content = txtContent.Text,
                    FontFamily = _fontSettings.FontFamily,
                    FontSize = _fontSettings.FontSize,
                    IsApplied = true
                });

                TxtStatus.Text = "스티커 노트가 추가되었습니다";
                RenderAnnotationOverlays();
            }
        }
        finally
            {
                _isDialogOpen = false;
            }
        }

        #endregion

        #region Find & Replace

        private void Find_Click(object sender, RoutedEventArgs e) => OpenFindPanel(false);
        private void FindReplace_Click(object sender, RoutedEventArgs e) => OpenFindPanel(true);

        private void OpenFindPanel(bool showReplace)
        {
            FindPanel.Visibility = Visibility.Visible;
            FindPanelColumn.Width = new GridLength(300);
            TxtFindText.Focus(FocusState.Programmatic);
            TxtReplaceText.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CloseFindPanel_Click(object sender, RoutedEventArgs e)
        {
            FindPanel.Visibility = Visibility.Collapsed;
            FindPanelColumn.Width = new GridLength(0);
        }

        private void FindText_Changed(object sender, TextChangedEventArgs e) => TxtFindCount.Text = "";

        private async void FindNext_Click(object sender, RoutedEventArgs e) => await SearchInDocumentAsync(true);
        private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await SearchInDocumentAsync(false);

        private async Task SearchInDocumentAsync(bool forward)
        {
            if (!_pdfManager.IsLoaded || string.IsNullOrWhiteSpace(TxtFindText.Text)) return;

            string searchText = TxtFindText.Text;
            string? filePath = _renderTempPath ?? _pdfManager.FilePath;
            if (filePath == null) return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

                int startPage = _currentPageIndex;
                int totalPages = (int)pdfDoc.PageCount;

                if (totalPages > 1)
                {
                    int pageIdx = forward
                        ? (startPage + 1) % totalPages
                        : (startPage - 1 + totalPages) % totalPages;

                    _currentPageIndex = pageIdx;
                    await RenderCurrentPageAsync();
                    SyncPageListSelection();
                    TxtFindCount.Text = $"페이지 {pageIdx + 1}";
                    TxtStatus.Text = $"'{searchText}' - 페이지 {pageIdx + 1}(으)로 이동";
                }
                else
                {
                    TxtFindCount.Text = "결과 없음";
                }
            }
            catch (Exception ex)
            {
                TxtFindCount.Text = "검색 오류";
                System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
            }
        }

        private async void Replace_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded || _pdfManager.Document == null) return;
            if (string.IsNullOrEmpty(TxtFindText.Text)) return;

            TxtStatus.Text = "텍스트 바꾸기 중...";
            try
            {
                await PerformTextReplacementAsync(TxtFindText.Text, TxtReplaceText.Text ?? "", false);
                TxtStatus.Text = "바꾸기 완료";
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("바꾸기 오류", ex.Message);
            }
        }

        private async void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded || _pdfManager.Document == null) return;
            if (string.IsNullOrEmpty(TxtFindText.Text)) return;

            TxtStatus.Text = "모두 바꾸기 중...";
            try
            {
                await PerformTextReplacementAsync(TxtFindText.Text, TxtReplaceText.Text ?? "", true);
                TxtStatus.Text = "모두 바꾸기 완료";
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("바꾸기 오류", ex.Message);
            }
        }

        private async Task PerformTextReplacementAsync(string findText, string replaceText, bool replaceAll)
        {
            if (_pdfManager.Document == null) return;

            bool found = false;

            for (int i = 0; i < _pdfManager.Document.PageCount; i++)
            {
                var page = _pdfManager.Document.Pages[i];
                try
                {
                    var contentStream = page.Contents.CreateSingleContent();
                    if (contentStream?.Stream?.Value != null)
                    {
                        string contentStr = System.Text.Encoding.Latin1.GetString(contentStream.Stream.Value);
                        if (contentStr.Contains(findText))
                        {
                            contentStr = replaceAll
                                ? contentStr.Replace(findText, replaceText)
                                : ReplaceFirst(contentStr, findText, replaceText);
                            contentStream.Stream.Value = System.Text.Encoding.Latin1.GetBytes(contentStr);
                            found = true;
                            _pdfManager.MarkModified();
                            if (!replaceAll) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Replace error page {i}: {ex.Message}");
                }
            }

            if (found)
                await SaveToTempAndRenderAsync();
            else
                TxtStatus.Text = "텍스트를 찾을 수 없습니다";
        }

        private static string ReplaceFirst(string text, string search, string replace)
        {
            int pos = text.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0) return text;
            return text.Substring(0, pos) + replace + text.Substring(pos + search.Length);
        }

        #endregion

        #region Font Settings

        private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFontFamily.SelectedItem is ComboBoxItem item)
                _fontSettings.FontFamily = item.Content?.ToString() ?? "맑은 고딕";
        }

        private void FontSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!double.IsNaN(args.NewValue))
                _fontSettings.FontSize = args.NewValue;
        }

        private void FontBold_Click(object sender, RoutedEventArgs e) =>
            _fontSettings.IsBold = BtnBold.IsChecked == true;

        private void FontItalic_Click(object sender, RoutedEventArgs e) =>
            _fontSettings.IsItalic = BtnItalic.IsChecked == true;

        private void FontColor_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ColorPalette.SelectedItem is Border border && border.Tag is string color)
            {
                _fontSettings.Color = color;
                FontColorIndicator.Background = new SolidColorBrush(ParseColor(color));
            }
        }

        #endregion

        #region PDF Merge / Split

        private async void MergePdf_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".pdf");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                if (_pdfManager.IsLoaded)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "PDF 합치기",
                        Content = $"선택한 {files.Count}개 파일을 현재 문서에 합치시겠습니까?",
                        PrimaryButtonText = "현재 문서에 합치기",
                        SecondaryButtonText = "새 파일로 저장",
                        CloseButtonText = "취소",
                        XamlRoot = Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        TxtStatus.Text = "PDF 합치기 중...";
                        LoadingRing.IsActive = true;

                        bool success = await _pdfManager.MergeAsync(files.Select(f => f.Path));
                        if (success)
                        {
                            await SaveToTempAndRenderAsync();
                            await LoadThumbnailsAsync();
                            TxtStatus.Text = "PDF 합치기 완료";
                        }
                        else
                        {
                            await ShowErrorDialogAsync("오류", "PDF 합치기에 실패했습니다.");
                        }

                        LoadingRing.IsActive = false;
                        UpdateUIState();
                    }
                    else if (result == ContentDialogResult.Secondary)
                    {
                        await MergeToNewFileAsync(files.Select(f => f.Path).ToList());
                    }
                }
                else
                {
                    await MergeToNewFileAsync(files.Select(f => f.Path).ToList());
                }
            }
        }

        private async Task MergeToNewFileAsync(List<string> filePaths)
        {
            var savePicker = new FileSavePicker();
            savePicker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
            savePicker.SuggestedFileName = "merged.pdf";

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                TxtStatus.Text = "PDF 합치기 중...";
                LoadingRing.IsActive = true;

                bool success = await PdfDocumentManager.MergeFilesAsync(filePaths, file.Path);
                if (success)
                {
                    await _pdfManager.OpenAsync(file.Path);
                    _currentPageIndex = 0;
                    _renderTempPath = file.Path;
                    TxtStatus.Text = "PDF 합치기 완료";
                }
                else
                {
                    await ShowErrorDialogAsync("오류", "PDF 합치기에 실패했습니다.");
                }

                LoadingRing.IsActive = false;
                UpdateUIState();
            }
        }

        private async void SplitPdf_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded) return;

            var dialog = new ContentDialog
            {
                Title = "PDF 나누기",
                PrimaryButtonText = "나누기",
                CloseButtonText = "취소",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            var panel = new StackPanel { Spacing = 12, MinWidth = 350 };
            panel.Children.Add(new TextBlock { Text = $"총 {_pdfManager.PageCount} 페이지" });

            var rbEveryPage = new RadioButton { Content = "페이지별로 나누기", IsChecked = true, GroupName = "SplitMode" };
            var rbByPages = new RadioButton { Content = "지정 페이지 수로 나누기", GroupName = "SplitMode" };
            var rbByRange = new RadioButton { Content = "페이지 범위로 나누기", GroupName = "SplitMode" };

            var nbPagesPerFile = new NumberBox
            {
                Value = 1, Minimum = 1, Maximum = _pdfManager.PageCount,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Header = "파일당 페이지 수:", Width = 150,
                Visibility = Visibility.Collapsed
            };

            var txtRanges = new TextBox
            {
                PlaceholderText = "예: 1-3, 4-6, 7-10",
                Header = "페이지 범위 (쉼표로 구분):",
                Visibility = Visibility.Collapsed
            };

            rbEveryPage.Checked += (s, _) =>
            {
                nbPagesPerFile.Visibility = Visibility.Collapsed;
                txtRanges.Visibility = Visibility.Collapsed;
            };
            rbByPages.Checked += (s, _) =>
            {
                nbPagesPerFile.Visibility = Visibility.Visible;
                txtRanges.Visibility = Visibility.Collapsed;
            };
            rbByRange.Checked += (s, _) =>
            {
                nbPagesPerFile.Visibility = Visibility.Collapsed;
                txtRanges.Visibility = Visibility.Visible;
            };

            panel.Children.Add(rbEveryPage);
            panel.Children.Add(rbByPages);
            panel.Children.Add(nbPagesPerFile);
            panel.Children.Add(rbByRange);
            panel.Children.Add(txtRanges);
            dialog.Content = panel;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var folderPicker = new FolderPicker();
                folderPicker.FileTypeFilter.Add("*");
                var hwnd = WindowNative.GetWindowHandle(this);
                InitializeWithWindow.Initialize(folderPicker, hwnd);

                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null)
                {
                    TxtStatus.Text = "PDF 나누기 중...";
                    LoadingRing.IsActive = true;

                    bool success;
                    if (rbByRange.IsChecked == true)
                    {
                        var ranges = ParsePageRanges(txtRanges.Text);
                        success = await _pdfManager.SplitByRangesAsync(folder.Path, ranges);
                    }
                    else
                    {
                        int pagesPerFile = rbEveryPage.IsChecked == true ? 1 : (int)nbPagesPerFile.Value;
                        success = await _pdfManager.SplitAsync(folder.Path, pagesPerFile);
                    }

                    TxtStatus.Text = success ? "PDF 나누기 완료" : "PDF 나누기 실패";
                    LoadingRing.IsActive = false;
                }
            }
        }

        private static List<(int start, int end)> ParsePageRanges(string input)
        {
            var ranges = new List<(int start, int end)>();
            if (string.IsNullOrWhiteSpace(input)) return ranges;

            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Contains('-'))
                {
                    var parts = trimmed.Split('-');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0].Trim(), out int start) &&
                        int.TryParse(parts[1].Trim(), out int end))
                    {
                        ranges.Add((start, end));
                    }
                }
                else if (int.TryParse(trimmed, out int page))
                {
                    ranges.Add((page, page));
                }
            }
            return ranges;
        }

        private async void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded || _pdfManager.PageCount <= 1) return;

            var dialog = new ContentDialog
            {
                Title = "페이지 삭제",
                Content = $"페이지 {_currentPageIndex + 1}을(를) 삭제하시겠습니까?",
                PrimaryButtonText = "삭제",
                CloseButtonText = "취소",
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _pdfManager.DeletePage(_currentPageIndex);
                if (_currentPageIndex >= _pdfManager.PageCount)
                    _currentPageIndex = _pdfManager.PageCount - 1;

                await SaveToTempAndRenderAsync();
                await LoadThumbnailsAsync();
                UpdateUIState();
                TxtStatus.Text = "페이지가 삭제되었습니다";
            }
        }

        #endregion

        #region View

        private void TogglePagePanel_Click(object sender, RoutedEventArgs e)
        {
            bool show = MenuShowPagePanel.IsChecked;
            PagePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            PagePanelColumn.Width = show ? new GridLength(200) : new GridLength(0);
        }

        #endregion

        #region Settings & About

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "환경 설정",
                CloseButtonText = "닫기",
                XamlRoot = Content.XamlRoot,
            };

            var panel = new StackPanel { Spacing = 16, MinWidth = 400 };

            var renderQuality = new ComboBox { Header = "렌더링 품질", Width = 200 };
            renderQuality.Items.Add(new ComboBoxItem { Content = "낮음 (빠름)", Tag = 1.0 });
            renderQuality.Items.Add(new ComboBoxItem { Content = "보통", Tag = 1.5 });
            renderQuality.Items.Add(new ComboBoxItem { Content = "높음", Tag = 2.0 });
            renderQuality.Items.Add(new ComboBoxItem { Content = "최고 (느림)", Tag = 3.0 });
            renderQuality.SelectedIndex = _renderScale switch { 1.0 => 0, 1.5 => 1, 3.0 => 3, _ => 2 };
            renderQuality.SelectionChanged += (s, _) =>
            {
                if (renderQuality.SelectedItem is ComboBoxItem item && item.Tag is double scale)
                    _renderScale = scale;
            };
            panel.Children.Add(renderQuality);

            var defaultFont = new ComboBox { Header = "기본 폰트", Width = 200 };
            foreach (var font in new[] { "맑은 고딕", "굴림", "돋움", "바탕", "궁서",
                "나눔고딕", "나눔명조", "Arial", "Times New Roman" })
            {
                var item = new ComboBoxItem { Content = font };
                if (font == _fontSettings.FontFamily) item.IsSelected = true;
                defaultFont.Items.Add(item);
            }
            defaultFont.SelectionChanged += (s, _) =>
            {
                if (defaultFont.SelectedItem is ComboBoxItem item)
                    _fontSettings.FontFamily = item.Content?.ToString() ?? "맑은 고딕";
            };
            panel.Children.Add(defaultFont);

            var defaultSize = new NumberBox
            {
                Header = "기본 글자 크기", Value = _fontSettings.FontSize,
                Minimum = 6, Maximum = 144,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 150
            };
            defaultSize.ValueChanged += (s, args) =>
            {
                if (!double.IsNaN(args.NewValue))
                    _fontSettings.FontSize = args.NewValue;
            };
            panel.Children.Add(defaultSize);

            panel.Children.Add(new TextBlock
            {
                Text = "설정은 현재 세션에만 적용됩니다.",
                Opacity = 0.5, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });

            dialog.Content = panel;
            await dialog.ShowAsync();

            if (_pdfManager.IsLoaded)
                await RenderCurrentPageAsync();
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var aboutPanel = new StackPanel { Spacing = 8 };
            aboutPanel.Children.Add(new TextBlock { Text = "PDF Editor", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            aboutPanel.Children.Add(new TextBlock { Text = "버전 1.0.0" });
            aboutPanel.Children.Add(new TextBlock { Text = "WinUI 3 + PDFsharp 기반 PDF 편집기", Opacity = 0.7 });
            aboutPanel.Children.Add(new TextBlock { Text = "한글 폰트 지원", Opacity = 0.7 });
            aboutPanel.Children.Add(new TextBlock
            {
                Text = "\n기능:\n• PDF 열기/저장/인쇄\n• 텍스트 추가/편집/바꾸기\n• 텍스트 강조 표시\n• 스티커 노트\n• 이미지 삽입\n• PDF 합치기/나누기\n• 찾기 및 바꾸기\n• 한글 폰트 지원",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            });

            var dialog = new ContentDialog
            {
                Title = "PDF Editor 정보",
                Content = aboutPanel,
                CloseButtonText = "닫기",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        #endregion

        #region Helpers

        private void ApplyAllAnnotations()
        {
            foreach (var ann in _annotations.Where(a => !a.IsApplied))
                ApplyAnnotationToDocument(ann);
        }

        private void ApplyAnnotationToDocument(PdfAnnotation ann)
        {
            if (!_pdfManager.IsLoaded) return;

            var xColor = ConvertToXColor(ann.Color);
            switch (ann.Type)
            {
                case AnnotationType.Text:
                case AnnotationType.FreeText:
                    _pdfManager.AddText(ann.PageIndex, ann.X, ann.Y, ann.Content,
                        ann.FontFamily, ann.FontSize, xColor, ann.IsBold, ann.IsItalic);
                    break;
                case AnnotationType.Highlight:
                    _pdfManager.AddHighlight(ann.PageIndex, ann.X, ann.Y,
                        ann.Width, ann.Height, XColor.FromArgb(255, 255, 255, 0), 0.3);
                    break;
                case AnnotationType.StickyNote:
                    _pdfManager.AddStickyNote(ann.PageIndex, ann.X, ann.Y, ann.Content,
                        ann.FontFamily, ann.FontSize);
                    break;
                case AnnotationType.Image:
                    if (ann.ImagePath != null)
                        _pdfManager.AddImage(ann.PageIndex, ann.ImagePath,
                            ann.X, ann.Y, ann.Width, ann.Height);
                    break;
            }
            ann.IsApplied = true;
        }

        private static XColor ConvertToXColor(string hexColor)
        {
            try
            {
                hexColor = hexColor.TrimStart('#');
                if (hexColor.Length == 6)
                {
                    int r = Convert.ToInt32(hexColor.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hexColor.Substring(2, 2), 16);
                    int b = Convert.ToInt32(hexColor.Substring(4, 2), 16);
                    return XColor.FromArgb(255, (byte)r, (byte)g, (byte)b);
                }
            }
            catch { }
            return XColors.Black;
        }

        private static Windows.UI.Color ParseColor(string hexColor)
        {
            try
            {
                hexColor = hexColor.TrimStart('#');
                if (hexColor.Length == 6)
                {
                    byte r = Convert.ToByte(hexColor.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hexColor.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hexColor.Substring(4, 2), 16);
                    return Windows.UI.Color.FromArgb(255, r, g, b);
                }
            }
            catch { }
            return Windows.UI.Color.FromArgb(255, 0, 0, 0);
        }

        private async Task ShowErrorDialogAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "확인",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private string GetSettingsFilePath()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF_simple_edit");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "window_settings.txt");
        }

        private void SaveWindowPosition()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow != null)
                {
                    var pos = appWindow.Position;
                    var size = appWindow.Size;
                    
                    var lines = new[]
                    {
                        pos.X.ToString(),
                        pos.Y.ToString(),
                        size.Width.ToString(),
                        size.Height.ToString()
                    };
                    File.WriteAllLines(GetSettingsFilePath(), lines);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveWindowPosition error: {ex.Message}");
            }
        }

        private void LoadWindowPosition()
        {
            try
            {
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length >= 4 &&
                        int.TryParse(lines[0], out int x) &&
                        int.TryParse(lines[1], out int y) &&
                        int.TryParse(lines[2], out int width) &&
                        int.TryParse(lines[3], out int height))
                    {
                        var hwnd = WindowNative.GetWindowHandle(this);
                        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = AppWindow.GetFromWindowId(windowId);
                        if (appWindow != null)
                        {
                            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, (int)width, (int)height));
                            return;
                        }
                    }
                }

                var hwndDefault = WindowNative.GetWindowHandle(this);
                var windowIdDefault = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwndDefault);
                var appWindowDefault = AppWindow.GetFromWindowId(windowIdDefault);
                if (appWindowDefault != null)
                {
                    appWindowDefault.Resize(new Windows.Graphics.SizeInt32(1400, 900));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadWindowPosition error: {ex.Message}");
            }
        }

        private string GetRecentFilesFilePath()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF_simple_edit");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, "recent_files.txt");
        }

        private void LoadRecentFiles()
        {
            try
            {
                string path = GetRecentFilesFilePath();
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    _recentFiles.Clear();
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                        {
                            _recentFiles.Add(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadRecentFiles error: {ex.Message}");
            }
        }

        private void SaveRecentFiles()
        {
            try
            {
                File.WriteAllLines(GetRecentFilesFilePath(), _recentFiles);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveRecentFiles error: {ex.Message}");
            }
        }

        private void AddToRecentFiles(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            // Remove if already exists to move to top
            _recentFiles.Remove(filePath);
            
            // Insert at top
            _recentFiles.Insert(0, filePath);

            // Keep only max items
            while (_recentFiles.Count > MaxRecentFiles)
            {
                _recentFiles.RemoveAt(_recentFiles.Count - 1);
            }

            SaveRecentFiles();
            UpdateRecentFilesMenu();
        }

        private void UpdateRecentFilesMenu()
        {
            if (MenuRecentFiles == null) return;

            MenuRecentFiles.Items.Clear();

            if (_recentFiles.Count == 0)
            {
                MenuRecentFiles.Items.Add(new MenuFlyoutItem { Text = "최근 파일 없음", IsEnabled = false });
                return;
            }

            foreach (var filePath in _recentFiles)
            {
                var item = new MenuFlyoutItem
                {
                    Text = Path.GetFileName(filePath),
                    Tag = filePath
                };
                ToolTipService.SetToolTip(item, filePath);
                item.Click += RecentFileItem_Click;
                MenuRecentFiles.Items.Add(item);
            }

            MenuRecentFiles.Items.Add(new MenuFlyoutSeparator());
            var clearItem = new MenuFlyoutItem { Text = "최근 파일 목록 지우기" };
            clearItem.Click += (s, e) =>
            {
                _recentFiles.Clear();
                SaveRecentFiles();
                UpdateRecentFilesMenu();
            };
            MenuRecentFiles.Items.Add(clearItem);
        }

        private async void RecentFileItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string filePath)
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
                        await OpenPdfFileAsync(storageFile);
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialogAsync("오류", $"파일을 여는 중 오류가 발생했습니다: {ex.Message}");
                        // Optional: remove non-existent file from list
                        _recentFiles.Remove(filePath);
                        SaveRecentFiles();
                        UpdateRecentFilesMenu();
                    }
                }
                else
                {
                    await ShowErrorDialogAsync("오류", "파일을 찾을 수 없습니다.");
                    _recentFiles.Remove(filePath);
                    SaveRecentFiles();
                    UpdateRecentFilesMenu();
                }
            }
        }

        #endregion
    }
}