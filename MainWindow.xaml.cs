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
        private Windows.Foundation.Point _lastMousePos;

        private readonly List<string> _recentFiles = new();
        private const int MaxRecentFiles = 10;

        // For highlight drag
        private bool _isDragging;
        private Windows.Foundation.Point _dragStart;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _dragRect;
        private bool _isDialogOpen = false;
        private bool _isInlineEditing = false;
        
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
        case EditToolMode.Select:
            _selectedAnnotation = FindAnnotationAt(pdfX, pdfY);
            if (_selectedAnnotation != null)
            {
                _isMovingAnnotation = true;
                _lastMousePos = pos;
                OverlayCanvas.CapturePointer(e.Pointer);
                TxtStatus.Text = "객체 선택됨 (드래그하여 이동)";
            }
            else
            {
                TxtStatus.Text = "준비";
            }
            RenderAnnotationOverlays();
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
            // 텍스트의 경우 대략적인 범위 계산 (정확한 높이는 폰트 크기에 따라 다름)
            double width = (ann.Content.Length * ann.FontSize * 0.6); // 근사치
            double height = ann.FontSize * 1.2;
            // UI상으로는 [ann.X, ann.Y] ~ [ann.X+W, ann.Y+H] 범위에 그려집니다.
            if (pdfX >= ann.X && pdfX <= ann.X + width &&
                pdfY >= ann.Y && pdfY <= ann.Y + height)
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
            if (pdfX >= ann.X && pdfX <= ann.X + 24 &&
                pdfY >= ann.Y && pdfY <= ann.Y + 24)
            {
                return ann;
            }
        }
    }
    return null;
}

        private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(OverlayCanvas).Position;

            if (_isMovingAnnotation && _selectedAnnotation != null)
            {
                double dx = (pos.X - _lastMousePos.X) / PdfToPixels;
                double dy = (pos.Y - _lastMousePos.Y) / PdfToPixels;

                _selectedAnnotation.X += dx;
                _selectedAnnotation.Y += dy;
                _selectedAnnotation.IsApplied = false; // 위치가 바뀌었으므로 문서 재적용 필요

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
            if (_isMovingAnnotation)
            {
                _isMovingAnnotation = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                
                _pdfManager.MarkModified();
                TxtStatus.Text = "위치 이동됨 (저장 시 반영)";
                RenderAnnotationOverlays();
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

        private void RenderAnnotationOverlays()
        {
            OverlayCanvas.Children.Clear();

            var pageAnnotations = _annotations.Where(a => a.PageIndex == _currentPageIndex).ToList();

            foreach (var ann in pageAnnotations)
            {
                FrameworkElement? element = null;

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
                        element = tb;
                        break;

                    case AnnotationType.Highlight:
                        element = new Microsoft.UI.Xaml.Shapes.Rectangle
                        {
                            Width = ann.Width * PdfToPixels,
                            Height = ann.Height * PdfToPixels,
                            Fill = new SolidColorBrush(ParseColor(ann.Color)),
                            Opacity = ann.Opacity
                        };
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
                                Stretch = Stretch.Fill
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
                            Child = element
                        };
                        
                        // 더블 클릭 시 편집 (텍스트만)
                        if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText)
                        {
                            border.DoubleTapped += (s, e) => EditAnnotationContent(ann);
                        }

                        Canvas.SetLeft(border, ann.X * PdfToPixels);
                        Canvas.SetTop(border, ann.Y * PdfToPixels);
                        OverlayCanvas.Children.Add(border);
                    }
                    else
                    {
                        OverlayCanvas.Children.Add(element);
                    }
                }
            }
        }

        private async void EditAnnotationContent(PdfAnnotation ann)
        {
            if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText)
            {
                var dialog = new ContentDialog
                {
                    Title = "텍스트 편집",
                    PrimaryButtonText = "확인",
                    SecondaryButtonText = "삭제",
                    CloseButtonText = "취소",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };

                var textBox = new TextBox
                {
                    Text = ann.Content,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 100,
                    MinWidth = 300
                };
                textBox.Loaded += (s, e) => textBox.Focus(FocusState.Programmatic);
                dialog.Content = textBox;

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    ann.Content = textBox.Text;
                    ann.IsApplied = false; // 변경됨
                    _pdfManager.MarkModified();
                    TxtStatus.Text = "텍스트가 수정되었습니다 (저장 시 반영)";
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    _annotations.Remove(ann);
                    _selectedAnnotation = null;
                    _pdfManager.MarkModified();
                    TxtStatus.Text = "객체가 삭제되었습니다";
                }
                RenderAnnotationOverlays();
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

            textBox.Text = ""; // 텍스트 상자 내용 지우기
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
                    IsApplied = false // 저장 시 반영
                };
                _annotations.Add(newAnn);
                _pdfManager.MarkModified();

                TxtStatus.Text = "텍스트가 추가되었습니다 (저장 시 반영)";
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

        #region Keyboard Shortcuts Extension
        private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete && _selectedAnnotation != null && !_isInlineEditing && !_isDialogOpen)
            {
                _annotations.Remove(_selectedAnnotation);
                _selectedAnnotation = null;
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