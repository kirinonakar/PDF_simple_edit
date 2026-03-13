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
        private readonly ObservableCollection<PdfDocumentTab> _tabs = new();
        private PdfDocumentTab? _activeTab;

        private int _currentPageIndex
        {
            get => _activeTab?.CurrentPageIndex ?? 0;
            set { if (_activeTab != null) _activeTab.CurrentPageIndex = value; }
        }
        private double _zoomLevel
        {
            get => _activeTab?.ZoomLevel ?? 1.0;
            set { if (_activeTab != null) _activeTab.ZoomLevel = value; }
        }
        private double _renderScale = 2.0;
        private EditToolMode _currentTool = EditToolMode.None;
        private string? _renderTempPath
        {
            get => _activeTab?.RenderTempPath;
            set { if (_activeTab != null) _activeTab.RenderTempPath = value; }
        }
        private bool _isFirstLoad
        {
            get => _activeTab?.IsFirstLoad ?? false;
            set { if (_activeTab != null) _activeTab.IsFirstLoad = value; }
        }
        private PdfAnnotation? _selectedAnnotation;
        private bool _isMovingAnnotation = false;
        private bool _isResizingAnnotation = false;
        private string? _resizeHandle = null; // "NW", "N", "NE", "W", "E", "SW", "S", "SE"
        private Windows.Foundation.Point _lastMousePos;

        private readonly List<string> _recentFiles = new();
        private const int MaxRecentFiles = 10;

        // For highlight drag
        private bool _isDragging;
        private Windows.Foundation.Point _dragStart;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _dragRect;
        private bool _isDialogOpen = false;
        private bool _isInlineEditing = false;

        private string? _lastSearchQuery;
        private int _lastFoundPage = -1;
        private int _lastFoundWordIndex = -1;
        
        private readonly PrintHelper _printHelper = new();
        private readonly TextFontSettings _fontSettings = new();
        
        // Aliases to active tab for easier migration
        private PdfDocumentManager _pdfManager => _activeTab?.PdfManager ?? new PdfDocumentManager();
        private ObservableCollection<PageThumbnailData> _pageThumbnails => _activeTab?.PageThumbnails ?? new ObservableCollection<PageThumbnailData>();
        private List<PdfAnnotation> _annotations => _activeTab?.Annotations ?? new List<PdfAnnotation>();

        // PDF는 72 DPI, Windows 논리 픽셀은 96 DPI입니다.
        private const double PdfToPixels = 96.0 / 72.0;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                DocTabView.TabItemsSource = _tabs;

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

        #region Tab Management

        private void DocTabView_AddTabButtonClick(TabView sender, object args)
        {
            NewDocument_Click(this, null);
        }

        private async void DocTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is PdfDocumentTab tab)
            {
                if (tab.IsModified)
                {
                    // Optionally ask to save
                }
                _tabs.Remove(tab);
            }
        }

        private async void DocTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Unhook old events if any
            if (_activeTab != null)
            {
                _activeTab.PdfManager.DocumentChanged -= PdfManager_DocumentChanged;
                _activeTab.PdfManager.ModifiedStateChanged -= PdfManager_ModifiedStateChanged;
            }

            _activeTab = DocTabView.SelectedItem as PdfDocumentTab;

            if (_activeTab != null)
            {
                // Hook new events
                _activeTab.PdfManager.DocumentChanged += PdfManager_DocumentChanged;
                _activeTab.PdfManager.ModifiedStateChanged += PdfManager_ModifiedStateChanged;

                PageListView.ItemsSource = _pageThumbnails;
                
                UpdateUIState();
                UpdateTitleBar();
                
                if (_activeTab.PdfManager.IsLoaded)
                {
                    // Manually trigger the document change handler to update thumbnails and render
                    PdfManager_DocumentChanged(_activeTab.PdfManager, EventArgs.Empty);
                }
            }
            else
            {
                PageListView.ItemsSource = null;
                UpdateUIState();
                UpdateTitleBar();
            }
        }

        #endregion

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
            DispatcherQueue.TryEnqueue(() => {
                UpdateTitleBar();
                UpdateTabHeader();
            });
        }

        private void UpdateTabHeader()
        {
            if (_activeTab != null)
            {
                string header = _activeTab.PdfManager.FilePath != null
                    ? Path.GetFileName(_activeTab.PdfManager.FilePath) : "새 문서";
                if (_activeTab.IsModified) header = "● " + header;
                _activeTab.Header = header;
            }
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
            MenuSelect.IsEnabled = hasDoc;
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

        private async void NewDocument_Click(object sender, RoutedEventArgs? e)
        {
            var newTab = new PdfDocumentTab { Header = "새 문서" };
            newTab.PdfManager.NewDocument();
            
            _tabs.Add(newTab);
            DocTabView.SelectedItem = newTab;
            
            // SelectionChanged will handle the rest
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs? e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".pdf");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    await OpenPdfFileInNewTabAsync(file);
                }
            }
        }

        private async Task OpenPdfFileInNewTabAsync(StorageFile file)
        {
            LoadingRing.IsActive = true;
            TxtStatus.Text = "파일을 여는 중...";

            try
            {
                var newTab = new PdfDocumentTab 
                { 
                    Header = file.Name,
                    FilePath = file.Path
                };
                
                bool success = await newTab.PdfManager.OpenAsync(file.Path);
                if (success)
                {
                    _tabs.Add(newTab);
                    DocTabView.SelectedItem = newTab;
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
            }
        }

        // Keep this for compatibility if called elsewhere, but update to create tab
        private async Task OpenPdfFileAsync(StorageFile file)
        {
            await OpenPdfFileInNewTabAsync(file);
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

        private async void SaveFile_Click(object sender, RoutedEventArgs? e)
        {
            if (!_pdfManager.IsLoaded) return;

            if (_pdfManager.FilePath != null)
            {
                await PerformSaveAsync(_pdfManager.FilePath, true);
                AddToRecentFiles(_pdfManager.FilePath);
            }
            else
            {
                SaveAsFile_Click(sender, e);
            }
        }

        private async void SaveAsFile_Click(object sender, RoutedEventArgs? e)
        {
            if (!_pdfManager.IsLoaded) return;

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
            picker.SuggestedFileName = _pdfManager.FilePath != null
                ? Path.GetFileName(_pdfManager.FilePath) : "새문서.pdf";

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await PerformSaveAsync(file.Path, true);
                AddToRecentFiles(file.Path);
            }
        }

        private void CloseFile_Click(object sender, RoutedEventArgs? e)
        {
            if (_activeTab != null)
            {
                _tabs.Remove(_activeTab);
            }
        }

        private async void Print_Click(object sender, RoutedEventArgs? e)
        {
            if (!_pdfManager.IsLoaded || _pdfManager.Document == null) return;

            try
            {
                // 인쇄 시에는 현재 모든 어노테이션이 반영된 상태여야 하므로 임시 파일로 플래트닝하여 저장
                string tempPath = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid()}.pdf");
                await PerformSaveAsync(tempPath, false);

                var hwnd = WindowNative.GetWindowHandle(this);
                // PerformSaveAsync가 성공하면 tempPath에 정적 PDF가 생성됨
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
            
            if (MenuSelect != null) MenuSelect.IsChecked = mode == EditToolMode.Select;
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
            _selectedAnnotation = null;
            RenderAnnotationOverlays();
        }

        private void UpdateCursor(EditToolMode mode)
        {
            if (OverlayCanvas == null) return;
        }

        private void SelectTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(_currentTool == EditToolMode.Select ? EditToolMode.None : EditToolMode.Select);
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
            if (focused is TextBox || focused is NumberBox || focused is ComboBox || _isInlineEditing)
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
                double x = (pageSize.width - 150) / 2;
                double y = (pageSize.height - 150) / 2;
                double w = 150, h = 150;

                _annotations.Add(new PdfAnnotation
                {
                    Type = AnnotationType.Image,
                    PageIndex = _currentPageIndex,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    ImagePath = file.Path,
                    IsApplied = false
                });
                _pdfManager.MarkModified();

                TxtStatus.Text = "이미지가 추가되었습니다 (저장 시 반영)";
                RenderAnnotationOverlays();
            }
        }

        #endregion

        #region Canvas Interaction

private async void OverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
{
    if (!_pdfManager.IsLoaded || _isInlineEditing) return;

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
        case EditToolMode.Select:
            // 리사이즈 핸들 클릭 확인
            if (e.OriginalSource is Microsoft.UI.Xaml.Shapes.Rectangle handle && handle.Tag is string dir)
            {
                _isResizingAnnotation = true;
                _resizeHandle = dir;
                _lastMousePos = pos;
                OverlayCanvas.CapturePointer(e.Pointer);
                TxtStatus.Text = "크기 조정 중...";
                return;
            }

            var found = FindAnnotationAt(pdfX, pdfY);

            if (found == null)
            {
                // 원본 콘텐츠(텍스트/이미지) 추출 및 히트 테스트
                var pageContents = await _pdfManager.ExtractPageContentsAsync(_currentPageIndex);
                var match = GetBestContentMatch(pageContents, pdfX, pdfY);
                if (match != null)
                {
                    if (match.Type == PageContentType.Text)
                    {
                        // 텍스트는 기존 로직(OperatorId 연동)을 위해 한번 더 정밀 매칭 시도 가능
                        var existingTexts = await _pdfManager.ExtractTextObjectsAsync(_currentPageIndex);
                        var textMatch = GetBestMatch(existingTexts, pdfX, pdfY);
                        if (textMatch != null)
                        {
                            found = ConvertExistingTextToAnnotation(textMatch);
                        }
                        else
                        {
                            found = ConvertExistingContentToAnnotation(match);
                        }
                    }
                    else
                    {
                        found = ConvertExistingContentToAnnotation(match);
                    }

                    if (found != null)
                    {
                        _annotations.Add(found);
                        _pdfManager.MarkModified();
                    }
                }
            }

            if (found != _selectedAnnotation)
            {
                _selectedAnnotation = found;
                RenderAnnotationOverlays();
            }

            if (_selectedAnnotation != null)
            {
                _isMovingAnnotation = true;
                _lastMousePos = pos;
                OverlayCanvas.CapturePointer(e.Pointer);
                
                // 아직 원래 콘텐츠가 제거되지 않았다면 매번 클릭 시마다 제거 시도 (재시도 기회 제공)
                if (_selectedAnnotation.IsOriginalTextReplacement || (_selectedAnnotation.IsOriginalImageReplacement && _selectedAnnotation.OriginalImageName != null))
                {
                    var targetAnn = _selectedAnnotation;
                    bool isText = targetAnn.IsOriginalTextReplacement;

                    _ = Task.Run(async () => {
                        bool removed = false;
                        if (isText)
                        {
                            removed = await _pdfManager.RemoveTextAsync(_currentPageIndex, targetAnn.OriginalPdfX, targetAnn.OriginalPdfY, targetAnn.OriginalText.Trim(), targetAnn.OperatorId);
                        }
                        else
                        {
                            removed = await _pdfManager.RemoveImageAsync(_currentPageIndex, targetAnn.OriginalImageName!);
                        }
                        
                        DispatcherQueue.TryEnqueue(async () => {
                            if (removed)
                            {
                                if (isText) targetAnn.IsOriginalTextReplacement = false;
                                else targetAnn.IsOriginalImageReplacement = false;

                                await RenderCurrentPageAsync();
                                TxtStatus.Text = (isText ? "기존 텍스트" : "기존 이미지") + " 제거 성공";
                            }
                            else
                            {
                                TxtStatus.Text = "제거 재시도 중...";
                            }
                        });
                    });
                }
                else
                {
                    TxtStatus.Text = "객체 선택됨 (드래그하여 이동)";
                }
            }
            else
            {
                TxtStatus.Text = "준비";
            }
            break;

        case EditToolMode.AddText:
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

private PdfAnnotation? FindAnnotationAt(double pdfX, double pdfY)
{
    // 탐색 순서를 역순으로 하여 가장 위에 있는 것부터 찾음
    for (int i = _annotations.Count - 1; i >= 0; i--)
    {
        var ann = _annotations[i];
        if (ann.PageIndex != _currentPageIndex) continue;

        if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText)
        {
            // 텍스트의 경우 대략적인 범위 계산 (패딩 넉넉히)
            double width = (ann.Content.Length * ann.FontSize * 0.8) + 10;
            double height = ann.FontSize * 1.4;
            if (pdfX >= ann.X - 5 && pdfX <= ann.X + width &&
                pdfY >= ann.Y - 5 && pdfY <= ann.Y + height)
            {
                return ann;
            }
        }
        else if (ann.Type == AnnotationType.Highlight || ann.Type == AnnotationType.Image)
        {
            if (pdfX >= ann.X && pdfX <= ann.X + ann.Width &&
                pdfY >= ann.Y && pdfY <= ann.Y + ann.Height)
            {
                return ann;
            }
        }
        else if (ann.Type == AnnotationType.StickyNote)
        {
            if (pdfX >= ann.X && pdfX <= ann.X + 30 &&
                pdfY >= ann.Y && pdfY <= ann.Y + 30) // 영역 약간 확장
            {
                return ann;
            }
        }
    }
    return null;
}

private SearchResult? GetBestMatch(List<SearchResult> texts, double x, double y)
{
    // Find text that contains the point, or is very close
    return texts.FirstOrDefault(t => 
        x >= t.X - 2 && x <= t.X + t.Width + 2 &&
        y >= t.Y - 2 && y <= t.Y + t.Height + 2);
}

private PdfPageContent? GetBestContentMatch(List<PdfPageContent> contents, double x, double y)
{
    return contents.FirstOrDefault(c => 
        x >= c.X - 2 && x <= c.X + c.Width + 2 &&
        y >= c.Y - 2 && y <= c.Y + c.Height + 2);
}

private PdfAnnotation ConvertExistingContentToAnnotation(PdfPageContent content)
{
    bool isText = content.Type == PageContentType.Text;
    return new PdfAnnotation
    {
        Type = isText ? AnnotationType.Text : AnnotationType.Image,
        PageIndex = _currentPageIndex,
        X = content.X,
        Y = content.Y,
        Content = content.Text,
        Width = content.Width,
        Height = content.Height,
        IsOriginalTextReplacement = isText,
        IsOriginalImageReplacement = !isText,
        OriginalPdfX = content.OriginalPdfX,
        OriginalPdfY = content.OriginalPdfY,
        OriginalText = content.Text,
        OriginalImageName = isText ? null : content.ImageId,
        ImagePath = isText ? null : (string.IsNullOrEmpty(content.Text) ? null : content.Text),
        OperatorId = content.OperatorId,
        FontSize = isText && content.Height > 0 ? content.Height : 12,
        IsApplied = false
    };
}

private PdfAnnotation ConvertExistingTextToAnnotation(SearchResult textObj)
{
    return new PdfAnnotation
    {
        Type = AnnotationType.Text,
        PageIndex = _currentPageIndex,
        X = textObj.X,
        Y = textObj.Y,
        Content = textObj.FoundText,
        Width = textObj.Width,
        Height = textObj.Height,
        IsOriginalTextReplacement = true,
        OriginalPdfX = textObj.X,
        OriginalPdfY = textObj.Y,
        OriginalText = textObj.FoundText,
        OperatorId = textObj.OperatorId,
        FontSize = textObj.Height > 0 ? textObj.Height : 12,
        IsApplied = false
    };
}

        private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(OverlayCanvas).Position;

            if (_isResizingAnnotation && _selectedAnnotation != null)
            {
                double dx = (pos.X - _lastMousePos.X) / PdfToPixels;
                double dy = (pos.Y - _lastMousePos.Y) / PdfToPixels;

                var ann = _selectedAnnotation;
                double minSize = 10;

                switch (_resizeHandle)
                {
                    case "NW":
                        if (ann.Width - dx > minSize) { ann.X += dx; ann.Width -= dx; }
                        if (ann.Height - dy > minSize) { ann.Y += dy; ann.Height -= dy; }
                        break;
                    case "N":
                        if (ann.Height - dy > minSize) { ann.Y += dy; ann.Height -= dy; }
                        break;
                    case "NE":
                        if (ann.Width + dx > minSize) { ann.Width += dx; }
                        if (ann.Height - dy > minSize) { ann.Y += dy; ann.Height -= dy; }
                        break;
                    case "W":
                        if (ann.Width - dx > minSize) { ann.X += dx; ann.Width -= dx; }
                        break;
                    case "E":
                        if (ann.Width + dx > minSize) { ann.Width += dx; }
                        break;
                    case "SW":
                        if (ann.Width - dx > minSize) { ann.X += dx; ann.Width -= dx; }
                        if (ann.Height + dy > minSize) { ann.Height += dy; }
                        break;
                    case "S":
                        if (ann.Height + dy > minSize) { ann.Height += dy; }
                        break;
                    case "SE":
                        if (ann.Width + dx > minSize) { ann.Width += dx; }
                        if (ann.Height + dy > minSize) { ann.Height += dy; }
                        break;
                }

                ann.IsApplied = false;
                _lastMousePos = pos;
                RenderAnnotationOverlays();
            }
            else if (_isMovingAnnotation && _selectedAnnotation != null)
            {
                double dx = (pos.X - _lastMousePos.X) / PdfToPixels;
                double dy = (pos.Y - _lastMousePos.Y) / PdfToPixels;

                _selectedAnnotation.X += dx;
                _selectedAnnotation.Y += dy;
                _selectedAnnotation.IsApplied = false;

                _lastMousePos = pos;
                RenderAnnotationOverlays();
            }
            else if (_isDragging && _dragRect != null)
            {
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
            if (_isResizingAnnotation)
            {
                _isResizingAnnotation = false;
                _resizeHandle = null;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                _pdfManager.MarkModified();
                TxtStatus.Text = "크기 조정됨 (저장 시 반영)";
                RenderAnnotationOverlays();
            }
            else if (_isMovingAnnotation)
            {
                var movedAnn = _selectedAnnotation;
                _isMovingAnnotation = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                
                // If it's a replacement for original PDF content and it hasn't been removed yet
                if (movedAnn != null)
                {
                    if (movedAnn.IsOriginalTextReplacement)
                    {
                        // Remove from original content stream immediately since it moved
                        await _pdfManager.RemoveTextAsync(_currentPageIndex, movedAnn.OriginalPdfX, movedAnn.OriginalPdfY, movedAnn.OriginalText.Trim(), movedAnn.OperatorId);
                        movedAnn.IsOriginalTextReplacement = false; // Now it's a normal annotation
                    }
                    else if (movedAnn.IsOriginalImageReplacement && movedAnn.OriginalImageName != null)
                    {
                        await _pdfManager.RemoveImageAsync(_currentPageIndex, movedAnn.OriginalImageName);
                        movedAnn.IsOriginalImageReplacement = false;
                    }
                }

                _pdfManager.MarkModified();
                TxtStatus.Text = "위치 이동됨 (저장 시 반영)";
                
                // 인라인 편집 중이 아닐 때만 리렌더링 (편집창 소멸 방지)
                if (!_isInlineEditing)
                {
                    await RenderCurrentPageAsync(); // Re-render to show original is gone
                    RenderAnnotationOverlays();
                }
            }
            else if (_isDragging && _dragRect != null && _currentTool == EditToolMode.Highlight)
            {
                _isDragging = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);

                double x = Canvas.GetLeft(_dragRect) / PdfToPixels;
                double y = Canvas.GetTop(_dragRect) / PdfToPixels;
                double w = _dragRect.Width / PdfToPixels;
                double h = _dragRect.Height / PdfToPixels;

                if (w > 5 && h > 5)
                {
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
                        IsApplied = false
                    });

                    _pdfManager.MarkModified();
                    TxtStatus.Text = "텍스트 강조가 추가되었습니다 (저장 시 반영)";
                    RenderAnnotationOverlays();
                }

                OverlayCanvas.Children.Remove(_dragRect);
                _dragRect = null;
            }
        }

        private void OverlayCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isInlineEditing) return;

            var pos = e.GetPosition(OverlayCanvas);
            double pdfX = pos.X / PdfToPixels;
            double pdfY = pos.Y / PdfToPixels;

            var ann = FindAnnotationAt(pdfX, pdfY);
            if (ann != null)
            {
                EditAnnotationContent(ann);
            }
        }

        private void SetElementCursor(UIElement element, Microsoft.UI.Input.InputCursor cursor)
        {
            try
            {
                var type = typeof(UIElement);
                var property = type.GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                property?.SetValue(element, cursor);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting cursor: {ex.Message}");
            }
        }

        private void RenderAnnotationOverlays()
        {
            // 기존 TextBox(편집창)가 있으면 제거하지 않고 유지하여 포커스 상실(편집 종료) 방지
            var activeBox = OverlayCanvas.Children.OfType<TextBox>().FirstOrDefault();
            var toRemove = OverlayCanvas.Children.Where(c => !(c is TextBox)).ToList();
            foreach (var child in toRemove)
            {
                OverlayCanvas.Children.Remove(child);
            }

            // 편집 중인 어노테이션 객체 식별 (skip rendering base text while editing)
            var editingAnn = activeBox?.Tag as PdfAnnotation;

            var pageAnnotations = _annotations.Where(a => a.PageIndex == _currentPageIndex).ToList();
            int insertIndex = 0;

            foreach (var ann in pageAnnotations)
            {
                if (ann == editingAnn) continue;
                FrameworkElement? element = null;

                switch (ann.Type)
                {
                    case AnnotationType.Text:
                    case AnnotationType.FreeText:
                        element = new TextBlock
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
                            Margin = new Thickness(0),
                            IsHitTestVisible = false
                        };
                        break;

                    case AnnotationType.Highlight:
                        element = new Microsoft.UI.Xaml.Shapes.Rectangle
                        {
                            Width = ann.Width * PdfToPixels,
                            Height = ann.Height * PdfToPixels,
                            Fill = new SolidColorBrush(ParseColor(ann.Color)),
                            Opacity = ann.Opacity,
                            IsHitTestVisible = false
                        };
                        break;

                    case AnnotationType.StickyNote:
                        var snGrid = new Grid
                        {
                            Width = 24 * PdfToPixels,
                            Height = 24 * PdfToPixels,
                            Background = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                            BorderThickness = new Thickness(1),
                            IsHitTestVisible = false
                        };
                        // ToolTipService.SetToolTip(snGrid, ann.Content); // HitTestVisible=false면 툴팁도 안 뜰 수 있음
                        snGrid.Children.Add(new FontIcon { Glyph = "\uE70B", FontSize = 12 * PdfToPixels, IsHitTestVisible = false });
                        element = snGrid;
                        break;

                    case AnnotationType.Image:
                        if (ann.ImagePath != null)
                        {
                            element = new Image
                            {
                                Source = new BitmapImage(new Uri(ann.ImagePath)),
                                Width = ann.Width * PdfToPixels,
                                Height = ann.Height * PdfToPixels,
                                Stretch = Stretch.Fill,
                                IsHitTestVisible = false
                            };
                        }
                        else if (ann.IsApplied == false)
                        {
                            // Original image or newly added but no path yet
                            // Render a placeholder or just a transparent box for selection border
                            element = new Microsoft.UI.Xaml.Shapes.Rectangle
                            {
                                Width = ann.Width * PdfToPixels,
                                Height = ann.Height * PdfToPixels,
                                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                                IsHitTestVisible = false
                            };
                        }
                        break;
                }

                    if (element != null)
                    {
                        Canvas.SetLeft(element, ann.X * PdfToPixels);
                        Canvas.SetTop(element, ann.Y * PdfToPixels);
                        
                        // 선택 시 시각적 표시
                        if (ann == _selectedAnnotation)
                        {
                            var border = new Border
                            {
                                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                                BorderThickness = new Thickness(1),
                                Margin = new Thickness(-2),
                                Child = element,
                                IsHitTestVisible = false
                            };

                            Canvas.SetLeft(border, ann.X * PdfToPixels);
                            Canvas.SetTop(border, ann.Y * PdfToPixels);
                            OverlayCanvas.Children.Insert(insertIndex++, border);

                            // 이미지나 하이라이트는 크기 조정 핸들 표시
                            if (ann.Type == AnnotationType.Image || ann.Type == AnnotationType.Highlight)
                            {
                                AddResizeHandles(ann, ref insertIndex);
                            }
                        }
                        else
                        {
                            OverlayCanvas.Children.Insert(insertIndex++, element);
                        }
                    }
            }
        }

        private void AddResizeHandles(PdfAnnotation ann, ref int insertIndex)
        {
            double x = ann.X * PdfToPixels;
            double y = ann.Y * PdfToPixels;
            double w = ann.Width * PdfToPixels;
            double h = ann.Height * PdfToPixels;
            double handleSize = 8;
            double offset = handleSize / 2;

            var handles = new Dictionary<string, Windows.Foundation.Point>
            {
                { "NW", new Windows.Foundation.Point(x - offset, y - offset) },
                { "N",  new Windows.Foundation.Point(x + w/2 - offset, y - offset) },
                { "NE", new Windows.Foundation.Point(x + w - offset, y - offset) },
                { "W",  new Windows.Foundation.Point(x - offset, y + h/2 - offset) },
                { "E",  new Windows.Foundation.Point(x + w - offset, y + h/2 - offset) },
                { "SW", new Windows.Foundation.Point(x - offset, y + h - offset) },
                { "S",  new Windows.Foundation.Point(x + w/2 - offset, y + h - offset) },
                { "SE", new Windows.Foundation.Point(x + w - offset, y + h - offset) }
            };

            foreach (var kvp in handles)
            {
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = handleSize,
                    Height = handleSize,
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                    StrokeThickness = 1,
                    Tag = kvp.Key,
                    IsHitTestVisible = true // 핸들은 클릭 가능해야 함
                };

                Canvas.SetLeft(rect, kvp.Value.X);
                Canvas.SetTop(rect, kvp.Value.Y);
                
                // 핸들에 마우스 커서 설정
                rect.PointerEntered += (s, e) => {
                    string dir = (string)((FrameworkElement)s).Tag;
                    SetElementCursor(OverlayCanvas, Microsoft.UI.Input.InputSystemCursor.Create(dir switch {
                        "NW" or "SE" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
                        "NE" or "SW" => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
                        "N" or "S" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
                        "E" or "W" => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
                        _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow
                    }));
                };
                rect.PointerExited += (s, e) => SetElementCursor(OverlayCanvas, Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow));

                OverlayCanvas.Children.Insert(insertIndex++, rect);
            }
        }

        private void EditAnnotationContent(PdfAnnotation ann)
        {
            if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText || ann.Type == AnnotationType.StickyNote)
            {
                // 레이스 컨디션 방지를 위해 플래그를 즉시 설정
                _isInlineEditing = true;
                
                DispatcherQueue.TryEnqueue(() =>
                {
                    AddInlineTextBox(ann.X, ann.Y, ann);
                });
            }
        }

        #endregion

        #region Text Input (Inline & Dialog)

        private void AddInlineTextBox(double pdfX, double pdfY, PdfAnnotation? existingAnn = null)
        {
            // 실제 TextBox가 이미 있는지 확인하여 중복 생성 방지
            if (OverlayCanvas.Children.OfType<TextBox>().Any()) return;
            _isInlineEditing = true;

            var canvasX = pdfX * PdfToPixels;
            var canvasY = pdfY * PdfToPixels;

            // 기존 어노테이션 정보 또는 현재 폰트 설정 사용
            string fontFamily = existingAnn?.FontFamily ?? _fontSettings.FontFamily;
            double fontSize = existingAnn?.FontSize ?? _fontSettings.FontSize;
            string color = existingAnn?.Color ?? _fontSettings.Color;
            bool isBold = existingAnn?.IsBold ?? _fontSettings.IsBold;
            bool isItalic = existingAnn?.IsItalic ?? _fontSettings.IsItalic;
            string initialText = existingAnn?.Content ?? "";

            var textBox = new TextBox
            {
                Text = initialText,
                AcceptsReturn = existingAnn?.Type == AnnotationType.StickyNote,
                TextWrapping = existingAnn?.Type == AnnotationType.StickyNote ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinWidth = 60,
                MinHeight = 24,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(-1, -1, 0, 0),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                FontSize = fontSize * PdfToPixels,
                FontFamily = new FontFamily(fontFamily),
                Foreground = new SolidColorBrush(ParseColor(color)),
                FontWeight = isBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                Tag = existingAnn != null ? (object)existingAnn : (object)new Windows.Foundation.Point(pdfX, pdfY),
                VerticalAlignment = VerticalAlignment.Top,
                VerticalContentAlignment = VerticalAlignment.Top,
                MaxWidth = 4000
            };

    Canvas.SetLeft(textBox, canvasX);
    Canvas.SetTop(textBox, canvasY);

            textBox.Loaded += (s, e) => 
            {
                // 포커스 강제 부여 및 전역 상태 보호
                textBox.Focus(FocusState.Programmatic);
            };

            // 텍스트 박스 내부의 포인터 이벤트가 캔버스 등으로 전달되어 편집이 꼬이는 것 방지
            textBox.PointerPressed += (s, e) => e.Handled = true;
            textBox.PointerReleased += (s, e) => e.Handled = true;
            textBox.DoubleTapped += (s, e) => e.Handled = true;

            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter && !((TextBox)s).AcceptsReturn)
                {
                    ApplyInlineText((TextBox)s);
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    CancelInlineEdit(textBox);
                    e.Handled = true;
                }
            };

            textBox.LostFocus += (s, e) =>
            {
                ApplyInlineText((TextBox)s);
            };

            OverlayCanvas.Children.Add(textBox);
        }

        private void CancelInlineEdit(TextBox textBox)
        {
            if (!_isInlineEditing || !OverlayCanvas.Children.Contains(textBox)) return;
            
            OverlayCanvas.Children.Remove(textBox);
            _isInlineEditing = false;
            
            // 포커스 복구 (단축키 작동을 위해 중요)
            DispatcherQueue.TryEnqueue(() =>
            {
                DocTabView.Focus(FocusState.Programmatic);
            });
        }

        private async void ApplyInlineText(TextBox textBox)
        {
            if (!_isInlineEditing || !OverlayCanvas.Children.Contains(textBox)) return;

            string text = textBox.Text;
            object tag = textBox.Tag;
            bool textWasRemoved = false;

            textBox.Text = ""; // 텍스트 상자 내용 지우기
            OverlayCanvas.Children.Remove(textBox);
            _isInlineEditing = false;

            try
            {
                if (tag is PdfAnnotation existingAnn)
                {
                    // 편집 모드
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // 텍스트를 모두 지우면 삭제로 간주
                        if (existingAnn.IsOriginalTextReplacement)
                        {
                            textWasRemoved = await _pdfManager.RemoveTextAsync(_currentPageIndex, 
                                existingAnn.OriginalPdfX, existingAnn.OriginalPdfY, existingAnn.OriginalText.Trim(), existingAnn.OperatorId);
                        }
                        _annotations.Remove(existingAnn);
                        if (_selectedAnnotation == existingAnn) _selectedAnnotation = null;
                        TxtStatus.Text = "텍스트가 삭제되었습니다";
                    }
                    else if (existingAnn.Content != text)
                    {
                        if (existingAnn.IsOriginalTextReplacement)
                        {
                            textWasRemoved = await _pdfManager.RemoveTextAsync(_currentPageIndex, 
                                existingAnn.OriginalPdfX, existingAnn.OriginalPdfY, existingAnn.OriginalText.Trim(), existingAnn.OperatorId);
                            existingAnn.IsOriginalTextReplacement = false;
                        }
                        
                        existingAnn.Content = text;
                        existingAnn.IsApplied = false;
                        _pdfManager.MarkModified();
                        TxtStatus.Text = "텍스트가 수정되었습니다 (저장 시 반영)";
                    }
                }
                else if (tag is Windows.Foundation.Point pdfPos)
                {
                    // 추가 모드
                    if (string.IsNullOrWhiteSpace(text)) return;

                    var newAnn = new PdfAnnotation
                    {
                        Type = AnnotationType.Text,
                        PageIndex = _currentPageIndex,
                        X = pdfPos.X,
                        Y = pdfPos.Y,
                        Content = text,
                        FontFamily = _fontSettings.FontFamily,
                        FontSize = _fontSettings.FontSize,
                        Color = _fontSettings.Color,
                        IsBold = _fontSettings.IsBold,
                        IsItalic = _fontSettings.IsItalic,
                        IsApplied = false
                    };
                    _annotations.Add(newAnn);
                    _pdfManager.MarkModified();
                    TxtStatus.Text = "텍스트가 추가되었습니다 (저장 시 반영)";
                }

                if (textWasRemoved)
                {
                    await RenderCurrentPageAsync();
                }

                RenderAnnotationOverlays();

                // 편집 종료 후 포커스를 메인 영역으로 돌림 (단축키 작동 중요)
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 탭이나 메인 레이아웃 중 하나에 포커스를 주어 엑셀러레이터가 다시 작동하게 함
                    BtnSelect.Focus(FocusState.Programmatic);
                    this.Content.Focus(FocusState.Programmatic);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyInlineText Error: {ex}");
                _isInlineEditing = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    this.Content.Focus(FocusState.Programmatic);
                });
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

            var cmbSize = new ComboBox { Header = "크기", Width = 100 };
            foreach (var size in new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "72" })
            {
                cmbSize.Items.Add(size);
            }
            cmbSize.SelectedItem = _fontSettings.FontSize.ToString();

            var chkBold = new CheckBox { Content = "굵게", IsChecked = _fontSettings.IsBold };
            var chkItalic = new CheckBox { Content = "기울임", IsChecked = _fontSettings.IsItalic };

            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            fontRow.Children.Add(cmbFont);
            fontRow.Children.Add(cmbSize);
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
                double fontSize = double.TryParse(cmbSize.SelectedItem?.ToString(), out double sizeVal) ? sizeVal : _fontSettings.FontSize;
                bool isBold = chkBold.IsChecked == true;
                bool isItalic = chkItalic.IsChecked == true;

                _fontSettings.FontFamily = fontFamily;
                _fontSettings.FontSize = fontSize;
                _fontSettings.IsBold = isBold;
                _fontSettings.IsItalic = isItalic;
                _fontSettings.Color = selectedColor;

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
                    IsApplied = false
                };
                
                _annotations.Add(newAnn);
                _pdfManager.MarkModified();

                TxtStatus.Text = "텍스트가 추가되었습니다 (저장 시 반영)";
                txtContent.Text = ""; // 텍스트 상자 내용 지우기
                RenderAnnotationOverlays();
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
                _annotations.Add(new PdfAnnotation
                {
                    Type = AnnotationType.StickyNote,
                    PageIndex = _currentPageIndex,
                    X = pdfX,
                    Y = pdfY,
                    Content = txtContent.Text,
                    FontFamily = _fontSettings.FontFamily,
                    FontSize = _fontSettings.FontSize,
                    IsApplied = false
                });
                _pdfManager.MarkModified();

                TxtStatus.Text = "스티커 노트가 추가되었습니다 (저장 시 반영)";
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

        private void Find_Click(object sender, RoutedEventArgs e) => OpenFindPanel();

        private void OpenFindPanel()
        {
            FindPanel.Visibility = Visibility.Visible;
            FindPanelColumn.Width = new GridLength(300);
            TxtFindText.Focus(FocusState.Programmatic);
        }

        private void CloseFindPanel_Click(object sender, RoutedEventArgs e)
        {
            FindPanel.Visibility = Visibility.Collapsed;
            FindPanelColumn.Width = new GridLength(0);
        }

        private void FindText_Changed(object sender, TextChangedEventArgs e)
        {
            TxtFindCount.Text = "";
            // 검색어가 바뀌면 상태 초기화
            _lastSearchQuery = null;
            _lastFoundPage = -1;
            _lastFoundWordIndex = -1;
        }

        private async void TxtFindText_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await SearchInDocumentAsync(true);
                e.Handled = true;
            }
        }

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
                string query = searchText.ToLower().Trim();

                // 현재 페이지부터 시작하여 한 바퀴 돌며 검색
                for (int i = 0; i < totalPages; i++)
                {
                    int pageIdx = forward 
                        ? (startPage + i) % totalPages 
                        : (startPage - i + totalPages) % totalPages;

                    var pigPage = _pdfManager.GetPigPage(pageIdx + 1);
                    if (pigPage != null)
                    {
                        var words = pigPage.GetWords().ToList();
                        
                        int startIndex;
                        int step = forward ? 1 : -1;
                        
                        if (pageIdx == _lastFoundPage && query == _lastSearchQuery)
                        {
                            // 같은 페이지에서 다음/이전 단어 찾기
                            startIndex = forward ? _lastFoundWordIndex + 1 : _lastFoundWordIndex - 1;
                        }
                        else
                        {
                            // 새 페이지 진입 시 시작 위치
                            startIndex = forward ? 0 : words.Count - 1;
                        }

                        // 범위 체크 및 루프
                        for (int wIdx = startIndex; forward ? (wIdx < words.Count) : (wIdx >= 0); wIdx += step)
                        {
                            var word = words[wIdx];
                            if (word.Text.ToLower().Contains(query))
                            {
                                _lastSearchQuery = query;
                                _lastFoundPage = pageIdx;
                                _lastFoundWordIndex = wIdx;

                                _currentPageIndex = pageIdx;
                                await RenderCurrentPageAsync();
                                SyncPageListSelection();

                                // 시각적 강조 (Canvas 좌표로 변환)
                                HighlightSearchMatch(word.BoundingBox);

                                TxtFindCount.Text = $"페이지 {pageIdx + 1}";
                                TxtStatus.Text = forward ? $"'{searchText}' 검색 완료" : $"'{searchText}' 이전 검색 완료";
                                return;
                            }
                        }
                    }
                }

                // 끝까지 갔는데 못 찾았으면 상태 초기화
                _lastFoundPage = -1;
                _lastFoundWordIndex = -1;
                
                TxtFindCount.Text = "결과 없음";
                TxtStatus.Text = "텍스트를 찾을 수 없습니다";
            }
            catch (Exception ex)
            {
                TxtFindCount.Text = "검색 오류";
                System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
            }
        }

        private void HighlightSearchMatch(UglyToad.PdfPig.Core.PdfRectangle rect)
        {
            // PDF 좌표 (Bottom-Up) -> Canvas 좌표 (Top-Down) 변환
            var pageSize = _pdfManager.GetPageSize(_currentPageIndex);
            double x = rect.Left * PdfToPixels;
            double height = (rect.Top - rect.Bottom) * PdfToPixels;
            double y = (pageSize.height - rect.Top) * PdfToPixels;
            double width = (rect.Right - rect.Left) * PdfToPixels;

            var highlight = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                Opacity = 0.5,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(highlight, x);
            Canvas.SetTop(highlight, y);
            OverlayCanvas.Children.Add(highlight);

            // 해당 위치로 스크롤
            PdfScrollViewer.ChangeView(x * _zoomLevel, y * _zoomLevel, null);
        }

        #endregion

        #region Keyboard Shortcuts Extension
        private async void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete && _selectedAnnotation != null && !_isInlineEditing && !_isDialogOpen)
            {
                var ann = _selectedAnnotation;
                if (ann.IsOriginalTextReplacement)
                {
                    await _pdfManager.RemoveTextAsync(_currentPageIndex, ann.OriginalPdfX, ann.OriginalPdfY, ann.OriginalText);
                }

                _annotations.Remove(ann);
                _selectedAnnotation = null;
                
                await RenderCurrentPageAsync();
                RenderAnnotationOverlays();
                e.Handled = true;
                TxtStatus.Text = "객체 삭제됨";
            }
        }
        #endregion

        #region Font Settings

        private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFontFamily.SelectedItem is ComboBoxItem item)
                _fontSettings.FontFamily = item.Content?.ToString() ?? "맑은 고딕";
        }

        private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFontSize.SelectedItem is ComboBoxItem item && 
                double.TryParse(item.Content?.ToString(), out double size))
            {
                _fontSettings.FontSize = size;
            }
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

            var defaultSize = new ComboBox { Header = "기본 글자 크기", Width = 150 };
            foreach (var size in new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "72" })
            {
                defaultSize.Items.Add(size);
            }
            defaultSize.SelectedItem = _fontSettings.FontSize.ToString();
            defaultSize.SelectionChanged += (sender, args) =>
            {
                if (double.TryParse(defaultSize.SelectedItem?.ToString(), out double sizeVal))
                    _fontSettings.FontSize = sizeVal;
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

        private async Task PerformSaveAsync(string filePath, bool isUserSave)
        {
            if (!_pdfManager.IsLoaded || _pdfManager.Document == null) return;

            TxtStatus.Text = "저장 중...";
            LoadingRing.IsActive = true;

            try
            {
                // [핵심] 현재 문서의 복사본을 만들어 모든 어노테이션을 반영하고 저장합니다.
                // 원본 메모리 문서는 '깨끗한' 상태로 유지하여 이동/재저장 시 DOUBLING을 방지합니다.
                using var ms = new MemoryStream();
                _pdfManager.Document.Save(ms);
                ms.Position = 0;
                using var outputDoc = PdfSharp.Pdf.IO.PdfReader.Open(ms, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

                foreach (var ann in _annotations)
                {
                    // If it's still marked as replacement (though usually should be handled by now)
                    if (ann.IsOriginalTextReplacement)
                    {
                        // Note: RemoveTextAsync normally works on internal _document. 
                        // We need a version that can work on outputDoc or handle it differently.
                        // For simplicity, we ensure it's removed from internal _document before save.
                    }
                    ApplyAnnotationToDocument(ann, outputDoc);
                }

                outputDoc.Save(filePath);

                if (isUserSave)
                {
                    _pdfManager.SetFilePath(filePath);
                    _pdfManager.MarkModified(false);
                    UpdateTitleBar();
                }
                
                TxtStatus.Text = isUserSave ? "저장 완료" : "준비 완료";
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("저장 오류", $"저장 중 오류가 발생했습니다: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
                UpdateUIState();
            }
        }

        private void ApplyAnnotationToDocument(PdfAnnotation ann, PdfDocument? targetDoc = null)
        {
            if (!_pdfManager.IsLoaded) return;

            var xColor = ConvertToXColor(ann.Color);
            switch (ann.Type)
            {
                case AnnotationType.Text:
                case AnnotationType.FreeText:
                    // 텍스트 위치 보정이 필요하다면 여기서 수행
                    _pdfManager.AddText(ann.PageIndex, ann.X, ann.Y, ann.Content,
                        ann.FontFamily, ann.FontSize, xColor, ann.IsBold, ann.IsItalic, targetDoc);
                    break;
                case AnnotationType.Highlight:
                    _pdfManager.AddHighlight(ann.PageIndex, ann.X, ann.Y,
                        ann.Width, ann.Height, XColor.FromArgb(255, 255, 255, 0), 0.3, targetDoc);
                    break;
                case AnnotationType.StickyNote:
                    _pdfManager.AddStickyNote(ann.PageIndex, ann.X, ann.Y, ann.Content,
                        ann.FontFamily, ann.FontSize, targetDoc);
                    break;
                case AnnotationType.Image:
                    if (ann.ImagePath != null)
                        _pdfManager.AddImage(ann.PageIndex, ann.ImagePath,
                            ann.X, ann.Y, ann.Width, ann.Height, targetDoc);
                    break;
            }
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