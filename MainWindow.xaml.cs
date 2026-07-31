using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage;
using Microsoft.UI.Windowing;
using Microsoft.UI;
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
        private readonly List<PdfAnnotation> _selectedAnnotations = new();
        private bool _isMovingAnnotation = false;
        private bool _hasMovedAnnotation = false;
        private readonly Dictionary<PdfAnnotation, Windows.Foundation.Point> _moveStartPositions = new();
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
        private bool _isInitializing = true;
        private bool _isRestoringSettings;
        private bool _controlKeyIsDown;
        private System.Threading.CancellationTokenSource? _thumbnailCts;

        private sealed class InlineTextEditSession
        {
            public required PdfAnnotation Annotation { get; init; }
            public required string OriginalContent { get; init; }
            public bool HasLiveChanges { get; set; }
            public bool SuppressTextChanged { get; set; }
            public bool IsFinishing { get; set; }
            public long TextChangeVersion { get; set; }
            public bool OriginalRemovalCommitted { get; set; }
            public Task<bool>? RemovalTask { get; set; }
        }

#pragma warning disable CS0414
        private string? _lastSearchQuery;
        private int _lastFoundPage = -1;
        private int _lastFoundWordIndex = -1;
#pragma warning restore CS0414

        private readonly PrintHelper _printHelper = new();
        private readonly TextFontSettings _fontSettings = new();


        
        // Aliases to active tab for easier migration
        private PdfDocumentManager _pdfManager => _activeTab?.PdfManager ?? new PdfDocumentManager();
        private ObservableCollection<PageThumbnailData> _pageThumbnails => _activeTab?.PageThumbnails ?? new ObservableCollection<PageThumbnailData>();
        private List<PdfAnnotation> _annotations => _activeTab?.Annotations ?? new List<PdfAnnotation>();

        // PDF는 72 DPI, Windows 논리 픽셀은 96 DPI입니다.
        private const double PdfToPixels = 96.0 / 72.0;

        private const int VirtualKeyControl = 0x11;
        private const int VirtualKeyLeftControl = 0xA2;
        private const int VirtualKeyRightControl = 0xA3;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        public MainWindow()
        {
            try
            {
                // XAML 컨트롤이 생성될 때 SelectionChanged가 발생할 수 있으므로
                // 이벤트 처리보다 먼저 기본값을 준비합니다.
                _fontSettings.FontFamily = "맑은 고딕";
                _fontSettings.FontSize = 12;
                _fontSettings.Color = "#000000";

                InitializeComponent();
                DocTabView.TabItemsSource = _tabs;
                // TextBox 내부 처리로 이미 Handled 된 키도 편집 확정 로직에서
                // 확인할 수 있도록 캔버스에 handledEventsToo 핸들러를 등록합니다.
                OverlayCanvas.AddHandler(
                    UIElement.KeyDownEvent,
                    new KeyEventHandler(MainWindow_KeyDown),
                    true);

                // 1. 현재 윈도우의 핸들(HWND) 가져오기
                IntPtr hWnd = WindowNative.GetWindowHandle(this);

                // 2. WindowId 및 AppWindow 객체 가져오기
                WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

                // 3. 아이콘 설정 (파일명이 정확해야 하며, 프로젝트 루트에 있어야 함)
                // .ico 파일이 빌드 결과물 폴더에 복사되도록 설정되어 있어야 합니다.
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                appWindow.SetIcon(iconPath);

                // Initialize color palette programmatically
                InitializeColorPalette();

                // Initialize Highlight UI
                HighlightColorPicker.Color = ParseColor(_fontSettings.HighlightColor);
                SldHighlightOpacity.Value = _fontSettings.HighlightOpacity;
                HighlightColorIndicator.Background = new SolidColorBrush(ParseColor(_fontSettings.HighlightColor));
                TxtHighlightOpacity.Text = $"{(int)(_fontSettings.HighlightOpacity * 100)}%";

                LoadRecentFiles();
                UpdateRecentFilesMenu();
                InitializeZoomAccelerators();

                Activated += MainWindow_Activated;
                Closed += MainWindow_Closed;
                _isInitializing = false;
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
                            string iconPath2 = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                            appWindow.SetIcon(iconPath2);
                            appWindow.Closing += AppWindow_Closing;
                        }
                    }
                    LoadWindowPosition();
                    UpdateTitleBar();
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
                    var dialog = new ContentDialog
                    {
                        Title = "변경 사항 저장",
                        Content = $"'{tab.Header}'의 내용이 변경되었습니다. 저장하시겠습니까?",
                        PrimaryButtonText = "저장",
                        SecondaryButtonText = "저장하지 않음",
                        CloseButtonText = "취소",
                        XamlRoot = this.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        _activeTab = tab; // Temporarily set as active to save
                        SaveFile_Click(this, null);
                    }
                    else if (result == ContentDialogResult.None)
                    {
                        return; // Cancel close
                    }
                }
                _tabs.Remove(tab);
            }
        }

        private async void DocTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Unhook old events
            if (_activeTab != null)
            {
                _activeTab.PdfManager.DocumentChanged -= PdfManager_DocumentChanged;
                _activeTab.PdfManager.PageStructureChanged -= PdfManager_PageStructureChanged;
                _activeTab.PdfManager.ModifiedStateChanged -= PdfManager_ModifiedStateChanged;
                _activeTab.PdfManager.UndoRedoPerformed -= PdfManager_UndoRedoPerformed;
                _activeTab.PdfManager.GetUIStateFunc = null;
            }

            _activeTab = DocTabView.SelectedItem as PdfDocumentTab;

            if (_activeTab != null)
            {
                // Hook new events
                _activeTab.PdfManager.DocumentChanged += PdfManager_DocumentChanged;
                _activeTab.PdfManager.PageStructureChanged += PdfManager_PageStructureChanged;
                _activeTab.PdfManager.ModifiedStateChanged += PdfManager_ModifiedStateChanged;
                _activeTab.PdfManager.UndoRedoPerformed += PdfManager_UndoRedoPerformed;
                
                // 설정: 원복 시 복원할 UI 상태(어노테이션 목록) 제공 함수
                _activeTab.PdfManager.GetUIStateFunc = () => {
                    return _activeTab.Annotations.Select(a => a.Clone()).ToList();
                };

                PageListView.ItemsSource = _pageThumbnails;
                
                UpdateUIState();
                UpdateTitleBar();
                
                if (_activeTab.PdfManager.IsLoaded)
                {
                    // Manually trigger refresh logic for current view
                    PdfManager_DocumentChanged(_activeTab.PdfManager, EventArgs.Empty);
                    PdfManager_PageStructureChanged(_activeTab.PdfManager, EventArgs.Empty);
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

        private void ApplyFontSettingsToControls()
        {
            if (CmbFontFamily != null)
            {
                var fontItem = CmbFontFamily.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.Content?.ToString(),
                        _fontSettings.FontFamily,
                        StringComparison.Ordinal));

                if (fontItem != null && !ReferenceEquals(CmbFontFamily.SelectedItem, fontItem))
                    CmbFontFamily.SelectedItem = fontItem;
            }

            if (CmbFontSize != null)
            {
                string sizeText = _fontSettings.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
                var sizeItem = CmbFontSize.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.Content?.ToString(),
                        sizeText,
                        StringComparison.Ordinal));

                if (sizeItem != null && !ReferenceEquals(CmbFontSize.SelectedItem, sizeItem))
                    CmbFontSize.SelectedItem = sizeItem;
            }

            if (BtnBold != null)
                BtnBold.IsChecked = _fontSettings.IsBold;
            if (BtnItalic != null)
                BtnItalic.IsChecked = _fontSettings.IsItalic;
            if (FontColorIndicator != null)
                FontColorIndicator.Background = new SolidColorBrush(ParseColor(_fontSettings.Color));
        }

        private static void SetPersistedSetting(List<string> lines, string key, string value)
        {
            string prefix = key + "=";
            int index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            string setting = prefix + value;

            if (index >= 0)
                lines[index] = setting;
            else
                lines.Add(setting);
        }

        private static Dictionary<string, string> ReadPersistedSettings(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                values[key] = value;
            }

            return values;
        }

        private void LoadPersistedFontSettings(IReadOnlyDictionary<string, string> values)
        {
            if (values.TryGetValue("FontFamily", out string? fontFamily) && !string.IsNullOrWhiteSpace(fontFamily))
                _fontSettings.FontFamily = fontFamily;

            if (values.TryGetValue("FontSize", out string? fontSizeText) &&
                double.TryParse(fontSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double fontSize) &&
                fontSize > 0)
            {
                _fontSettings.FontSize = fontSize;
            }

            if (values.TryGetValue("FontColor", out string? color) && !string.IsNullOrWhiteSpace(color))
                _fontSettings.Color = color;

            if (values.TryGetValue("IsBold", out string? boldText) && bool.TryParse(boldText, out bool isBold))
                _fontSettings.IsBold = isBold;

            if (values.TryGetValue("IsItalic", out string? italicText) && bool.TryParse(italicText, out bool isItalic))
                _fontSettings.IsItalic = isItalic;
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
                    await RenderCurrentPageAsync();

                    if (_isFirstLoad)
                    {
                        _isFirstLoad = false;
                        for (int i = 0; i < 10; i++)
                        {
                            await Task.Delay(100);
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

        private void PdfManager_PageStructureChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_pdfManager.IsLoaded)
                {
                    await LoadThumbnailsAsync();
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

        private void PdfManager_UndoRedoPerformed(object? sender, object? state)
        {
            if (_activeTab != null && state is List<PdfAnnotation> savedAnnotations)
            {
                DispatcherQueue.TryEnqueue(async () => {
                    _activeTab.Annotations.Clear();
                    foreach (var ann in savedAnnotations)
                    {
                        _activeTab.Annotations.Add(ann);
                    }
                    
                    // UI 갱신
                    RenderAnnotationOverlays();
                    
                    // 만약 Undo로 인해 원본 PDF 데이터가 바뀌었다면 렌더링 다시 수행
                    _renderTempPath = null;
                    await RenderCurrentPageAsync();
                });
            }
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
            MenuAddImage.IsEnabled = hasDoc;
            MenuSplitPdf.IsEnabled = hasDoc;
            MenuDeletePage.IsEnabled = hasDoc;

            BtnSave.IsEnabled = hasDoc;
            BtnSaveAs.IsEnabled = hasDoc;
            BtnPrint.IsEnabled = hasDoc;
            BtnUndo.IsEnabled = hasDoc && _pdfManager.CanUndo;
            BtnRedo.IsEnabled = hasDoc && _pdfManager.CanRedo;
            BtnSelect.IsEnabled = hasDoc;
            BtnAddText.IsEnabled = hasDoc;
            BtnHighlight.IsEnabled = hasDoc;
            BtnHighlightSettings.IsEnabled = hasDoc;
            BtnColorPicker.IsEnabled = hasDoc;
            BtnAddImage.IsEnabled = hasDoc;

            MenuUndo.IsEnabled = hasDoc && _pdfManager.CanUndo;
            MenuRedo.IsEnabled = hasDoc && _pdfManager.CanRedo;

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

        private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            // AppWindow.Closing은 최종 Closed 이벤트보다 먼저 발생하므로,
            // 일반 상태의 현재 위치와 크기를 이 시점에 확실히 기록합니다.
            SaveWindowPosition();

            if (_isBypassingClosingCheck) return;

            var modifiedTabs = _tabs.Where(t => t.IsModified).ToList();
            if (modifiedTabs.Count > 0)
            {
                args.Cancel = true; // Stop closing initially

                var dialog = new ContentDialog
                {
                    Title = "종료 확인",
                    Content = $"저장되지 않은 변경 사항이 있는 {modifiedTabs.Count}개의 탭이 있습니다. 그래도 종료하시겠습니까?",
                    PrimaryButtonText = "저장하고 종료 (순회)",
                    SecondaryButtonText = "그냥 종료",
                    CloseButtonText = "취소",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Secondary)
                {
                    _isBypassingClosingCheck = true;
                    this.Close();
                }
                else if (result == ContentDialogResult.Primary)
                {
                    foreach (var tab in modifiedTabs)
                    {
                        var oldActive = _activeTab;
                        _activeTab = tab;
                        SaveFile_Click(this, null);
                        // Restoring _activeTab might be tricky if we are closing, 
                        // but since we close after the loop it's fine.
                    }
                    _isBypassingClosingCheck = true;
                    this.Close();
                }
            }
        }

        private bool _isBypassingClosingCheck = false;

        private void UpdateTitleBar()
        {
            string title = "PDF Simple Editor";
            if (_pdfManager.IsLoaded)
            {
                string fileName = _pdfManager.FilePath != null
                    ? Path.GetFileName(_pdfManager.FilePath) : "새 문서";
                title = $"PDF Simple Editor - {fileName}{(_pdfManager.IsModified ? " ●" : "")}";
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
            if (!_pdfManager.IsLoaded) return;

            try
            {
                // 인쇄 시에는 현재 모든 어노테이션이 반영된 상태여야 하므로 임시 파일로 플래트닝하여 저장
                string tempPath = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid()}.pdf");
                await PerformSaveAsync(tempPath, false);

                var hwnd = WindowNative.GetWindowHandle(this);
                // PerformSaveAsync handles iText 9 document flushing
                await _printHelper.PrintAsync(tempPath, hwnd);
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
            // Cancel any existing thumbnail loading
            _thumbnailCts?.Cancel();
            _thumbnailCts = new System.Threading.CancellationTokenSource();
            var token = _thumbnailCts.Token;

            try
            {
                _pageThumbnails.Clear();
                if (!_pdfManager.IsLoaded) return;

                int totalPages = _pdfManager.PageCount;
                string? filePath = _renderTempPath ?? _pdfManager.FilePath;
                byte[]? pdfBytes = null;

                if (string.IsNullOrEmpty(filePath))
                {
                    pdfBytes = _pdfManager.GetPdfBytes();
                    if (pdfBytes == null) return;
                }

                for (int i = 0; i < totalPages; i++)
                {
                    if (token.IsCancellationRequested) return;

                    try
                    {
                        MemoryStream? ms = null;
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            // Retry a few times if file is locked
                            for (int retry = 0; retry < 3; retry++)
                            {
                                ms = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(filePath, i, 0.4);
                                if (ms != null) break;
                                await Task.Delay(100);
                            }
                        }
                        
                        if (ms == null)
                        {
                            // Fallback to memory if file failed or not available
                            pdfBytes ??= _pdfManager.GetPdfBytes();
                            if (pdfBytes != null)
                            {
                                using var memStream = new MemoryStream(pdfBytes);
                                ms = await PdfRenderHelper.RenderPageWithWindowsPdfStreamAsync(memStream.AsRandomAccessStream(), i, 0.4);
                            }
                        }

                        if (ms != null)
                        {
                            var bitmap = new BitmapImage();
                            await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                            _pageThumbnails.Add(new PageThumbnailData { PageNumber = i + 1, Thumbnail = bitmap });
                        }
                        else
                        {
                            // If still null after fallback, add with grey placeholder (handled by XAML background)
                            _pageThumbnails.Add(new PageThumbnailData { PageNumber = i + 1 });
                        }
                    }
                    catch
                    {
                        _pageThumbnails.Add(new PageThumbnailData { PageNumber = i + 1 });
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadThumbnails error: {ex.Message}");
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

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_pdfManager.CanUndo)
            {
                _pdfManager.Undo();
                TxtStatus.Text = "실행 취소됨";
            }
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_pdfManager.CanRedo)
            {
                _pdfManager.Redo();
                TxtStatus.Text = "다시 실행됨";
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
            BtnColorPicker.IsChecked = mode == EditToolMode.ColorPicker;
            
            if (MenuSelect != null) MenuSelect.IsChecked = mode == EditToolMode.Select;
            if (MenuAddText != null) MenuAddText.IsChecked = mode == EditToolMode.AddText;
            if (MenuHighlight != null) MenuHighlight.IsChecked = mode == EditToolMode.Highlight;

            TxtToolMode.Text = mode switch
            {
                EditToolMode.AddText => "도구: 텍스트 추가",
                EditToolMode.Highlight => "도구: 텍스트 강조",
                EditToolMode.AddImage => "도구: 이미지 추가",
                EditToolMode.Select => "도구: 선택",
                EditToolMode.ColorPicker => "도구: 색상 추출",
                _ => ""
            };

            UpdateCursor(mode);
            _selectedAnnotation = null;
            _selectedAnnotations.Clear();
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

        private void HighlightSettings_Click(object sender, RoutedEventArgs e)
        {
            FlyoutBase.ShowAttachedFlyout(BtnHighlight);
        }

        private void ColorPickerTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(_currentTool == EditToolMode.ColorPicker ? EditToolMode.None : EditToolMode.ColorPicker);
        }

        private void HighlightColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            _fontSettings.HighlightColor = args.NewColor.ToString();
            HighlightColorIndicator.Background = new SolidColorBrush(args.NewColor);
        }

        private void HighlightOpacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _fontSettings.HighlightOpacity = e.NewValue;
            if (TxtHighlightOpacity != null)
                TxtHighlightOpacity.Text = $"{(int)(e.NewValue * 100)}%";
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

    var ptrPt = e.GetCurrentPoint(OverlayCanvas);
    var pos = ptrPt.Position;
    bool isLeft = ptrPt.Properties.IsLeftButtonPressed;

    double pdfX = pos.X / PdfToPixels;
    double pdfY = pos.Y / PdfToPixels;

    if (!isLeft && _currentTool == EditToolMode.Select) return;

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
                TxtStatus.Text = "페이지 콘텐츠 분석 중...";
                var pageContents = await _pdfManager.ExtractPageContentsAsync(_currentPageIndex);
                var match = GetBestContentMatch(pageContents, pdfX, pdfY);
                if (match != null)
                {
                    found = ConvertExistingContentToAnnotation(match);

                    if (found != null)
                    {
                        // 선택만으로 PDF 원문을 변경하지 않습니다. 원본은 그대로 렌더링하고
                        // 편집 구역 테두리만 오버레이하여 글꼴/크기/좌표 변화를 막습니다.
                        _annotations.Add(found);
                        TxtStatus.Text = found.IsOriginalTextReplacement
                            ? "텍스트 편집 구역이 선택되었습니다. 두 번 클릭하여 편집하세요."
                            : "원본 콘텐츠가 선택되었습니다.";
                    }
                }
                else
                {
                    TxtStatus.Text = "선택된 개체 없음";
                }
            }

            var ctrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (found != null)
            {
                if (ctrlPressed)
                {
                    if (_selectedAnnotations.Contains(found))
                    {
                        _selectedAnnotations.Remove(found);
                        if (_selectedAnnotation == found) _selectedAnnotation = _selectedAnnotations.LastOrDefault();
                    }
                    else
                    {
                        _selectedAnnotations.Add(found);
                        _selectedAnnotation = found;
                    }
                }
                else
                {
                    _selectedAnnotations.Clear();
                    _selectedAnnotations.Add(found);
                    _selectedAnnotation = found;
                }
                RenderAnnotationOverlays();
            }
            else
            {
                if (!ctrlPressed)
                {
                    _selectedAnnotations.Clear();
                    _selectedAnnotation = null;
                    RenderAnnotationOverlays();
                }
            }

            if (_selectedAnnotation != null)
            {
                _isMovingAnnotation = true;
                _hasMovedAnnotation = false;
                _moveStartPositions.Clear();
                foreach (var annotation in _selectedAnnotations)
                    _moveStartPositions[annotation] = new Windows.Foundation.Point(annotation.X, annotation.Y);
                _lastMousePos = pos;
                OverlayCanvas.CapturePointer(e.Pointer);
                
                // 기존의 이 부분(백그라운드 Task에서 RemoveTextAsync를 호출하던 로직)은 
                // 위에서 선택 즉시 처리하도록 변경했으므로 제거하거나 중복 방지 처리를 합니다.
                if (_selectedAnnotation.IsOriginalTextReplacement || (_selectedAnnotation.IsOriginalImageReplacement && _selectedAnnotation.OriginalImageName != null))
                {
                   // 이미 위에서 처리됨
                }
                else
                {
                    TxtStatus.Text = _selectedAnnotations.Count > 1 ? $"{_selectedAnnotations.Count}개 객체 선택됨" : "객체 선택됨 (드래그하여 이동)";
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


        case EditToolMode.Highlight:
            _isDragging = true;
            _dragStart = pos;
            _dragRect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(ParseColor(_fontSettings.HighlightColor)),
                Opacity = _fontSettings.HighlightOpacity,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                StrokeThickness = 1
            };
            Canvas.SetLeft(_dragRect, pos.X);
            Canvas.SetTop(_dragRect, pos.Y);
            OverlayCanvas.Children.Add(_dragRect);
            OverlayCanvas.CapturePointer(e.Pointer);
            break;

        case EditToolMode.ColorPicker:
            TxtStatus.Text = "색상 추출 중...";
            var pickedColor = await GetColorAtPointAsync(PdfPageImage, e.GetCurrentPoint(PdfPageImage).Position);
            if (pickedColor.HasValue)
            {
                var color = pickedColor.Value;
                _fontSettings.HighlightColor = color.ToString();
                HighlightColorPicker.Color = color;
                HighlightColorIndicator.Background = new SolidColorBrush(color);
                TxtStatus.Text = $"색상이 추출되었습니다: {color}";
                
                // Automatically switch back to Highlight tool if desired, 
                // or just stay in ColorPicker. User asked for "automatically selected", 
                // so let's switch to Highlight tool to be helpful.
                SetToolMode(EditToolMode.Highlight);
            }
            break;
    }
}

private async Task<Windows.UI.Color?> GetColorAtPointAsync(UIElement element, Windows.Foundation.Point point)
{
    try
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var buffer = await rtb.GetPixelsAsync();
        
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
        {
            byte[] pixels = new byte[buffer.Length];
            reader.ReadBytes(pixels);
            
            // RenderTargetBitmap size might be larger than element's logical size due to DPI or scaling
            // We need to map logical point to pixel coordinates
            int pixelWidth = rtb.PixelWidth;
            int pixelHeight = rtb.PixelHeight;
            
            double scaleX = pixelWidth / element.RenderSize.Width;
            double scaleY = pixelHeight / element.RenderSize.Height;
            
            int x = (int)(point.X * scaleX);
            int y = (int)(point.Y * scaleY);
            
            if (x < 0) x = 0; if (x >= pixelWidth) x = pixelWidth - 1;
            if (y < 0) y = 0; if (y >= pixelHeight) y = pixelHeight - 1;

            int index = (y * pixelWidth + x) * 4;
            if (index >= 0 && index + 3 < pixels.Length)
            {
                byte b = pixels[index];
                byte g = pixels[index + 1];
                byte r = pixels[index + 2];
                byte a = pixels[index + 3];
                return Windows.UI.Color.FromArgb(a, r, g, b);
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Error picking color: {ex.Message}");
    }
    return null;
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
            double width = ann.Width > 0 ? ann.Width : (ann.Content.Length * ann.FontSize * 0.8) + 10;
            double height = ann.Height > 0 ? ann.Height : ann.FontSize * 1.4;
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
    // Hit test with a small buffer for easier selection
    int matchIndex = contents.FindIndex(c =>
        x >= c.X - 5 && x <= c.X + c.Width + 5 &&
        y >= c.Y - 5 && y <= c.Y + c.Height + 5);

    if (matchIndex < 0)
        return null;

    var match = contents[matchIndex];
    if (match.Type != PageContentType.Text)
        return match;

    // A PDF can split one visible paragraph into several text regions even
    // when the font and layout are continuous. Expand the selected region so
    // editing the first line does not hide the following lines.
    var expanded = ClonePageContentForEditing(match);
    int startIndex = matchIndex;
    while (startIndex > 0 && CanJoinTextRegions(contents[startIndex - 1], expanded, out bool prependNewLine))
    {
        expanded = MergeTextRegions(contents[startIndex - 1], expanded, prependNewLine);
        startIndex--;
    }

    int nextIndex = matchIndex + 1;
    while (nextIndex < contents.Count && CanJoinTextRegions(expanded, contents[nextIndex], out bool appendNewLine))
    {
        expanded = MergeTextRegions(expanded, contents[nextIndex], appendNewLine);
        nextIndex++;
    }

    return expanded;
}

private static PdfPageContent ClonePageContentForEditing(PdfPageContent source)
{
    return new PdfPageContent
    {
        Type = source.Type,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height,
        Text = source.Text,
        OriginalPdfX = source.OriginalPdfX,
        OriginalPdfY = source.OriginalPdfY,
        OperatorId = source.OperatorId,
        FontSize = source.FontSize,
        FontFamily = source.FontFamily,
        Color = source.Color,
        IsBold = source.IsBold,
        IsItalic = source.IsItalic,
        ContentStreamIndex = source.ContentStreamIndex,
        ContentStreamObjectNumber = source.ContentStreamObjectNumber,
        OperationIndex = source.OperationIndex,
        TextRenderMode = source.TextRenderMode,
        LineHeight = source.LineHeight,
        BaselineOffset = source.BaselineOffset,
        OriginalFontObjectNumber = source.OriginalFontObjectNumber,
        TextFragments = source.TextFragments.Select(fragment => fragment.Clone()).ToList(),
        ImageId = source.ImageId
    };
}

private static bool CanJoinTextRegions(
    PdfPageContent first,
    PdfPageContent second,
    out bool startsNewLine)
{
    startsNewLine = false;
    if (first.Type != PageContentType.Text || second.Type != PageContentType.Text ||
        first.TextFragments.Count == 0 || second.TextFragments.Count == 0)
        return false;

    double referenceSize = Math.Max(first.FontSize, 1);
    if (!string.Equals(first.FontFamily, second.FontFamily, StringComparison.OrdinalIgnoreCase) ||
        Math.Abs(first.FontSize - second.FontSize) > Math.Max(0.75, referenceSize * 0.2) ||
        !string.Equals(first.Color, second.Color, StringComparison.OrdinalIgnoreCase) ||
        first.IsBold != second.IsBold || first.IsItalic != second.IsItalic)
        return false;

    var previous = first.TextFragments[^1];
    var current = second.TextFragments[0];
    double fontSize = Math.Max(Math.Max(previous.FontSize, current.FontSize), 1);
    double topDelta = Math.Abs(previous.Y - current.Y);
    if (topDelta <= Math.Max(1.25, fontSize * 0.4))
    {
        double gap = current.X - (previous.X + previous.Width);
        return gap >= -fontSize && gap <= fontSize * 2.75;
    }

    if (current.Y <= previous.Y || current.Y - previous.Y > Math.Max(fontSize * 1.8, 16))
        return false;

    double overlap = Math.Min(first.X + first.Width, current.X + current.Width)
                   - Math.Max(first.X, current.X);
    double minWidth = Math.Max(Math.Min(first.Width, current.Width), 1);
    bool sameColumn = overlap / minWidth >= 0.25 ||
        Math.Abs(current.X - first.X) <= Math.Max(24, fontSize * 2.5);
    startsNewLine = sameColumn;
    return sameColumn;
}

private static PdfPageContent MergeTextRegions(
    PdfPageContent first,
    PdfPageContent second,
    bool startsNewLine)
{
    var merged = ClonePageContentForEditing(first);
    var previous = merged.TextFragments[^1];
    var current = second.TextFragments[0];

    if (startsNewLine)
    {
        merged.Text = NormalizeLineEndings(merged.Text) + "\r\n" + NormalizeLineEndings(second.Text);
        double detectedLineHeight = Math.Abs(current.Y - previous.Y);
        if (detectedLineHeight > 0.1)
            merged.LineHeight = merged.LineHeight > 0.1
                ? (merged.LineHeight + detectedLineHeight) / 2.0
                : detectedLineHeight;
    }
    else
    {
        double gap = current.X - (previous.X + previous.Width);
        bool needsSpace = gap > Math.Max(previous.FontSize, current.FontSize) * 0.15 &&
            !merged.Text.EndsWith(" ", StringComparison.Ordinal) &&
            !second.Text.StartsWith(" ", StringComparison.Ordinal);
        merged.Text = merged.Text + (needsSpace ? " " : string.Empty) + second.Text;
    }

    int sourceLine = second.TextFragments.Min(fragment => fragment.LineIndex);
    int targetLine = merged.TextFragments.Max(fragment => fragment.LineIndex) +
        (startsNewLine ? 1 : 0);
    foreach (var fragment in second.TextFragments.Select(fragment => fragment.Clone()))
    {
        fragment.LineIndex += targetLine - sourceLine;
        merged.TextFragments.Add(fragment);
    }

    double left = Math.Min(merged.X, second.X);
    double top = Math.Min(merged.Y, second.Y);
    double right = Math.Max(merged.X + merged.Width, second.X + second.Width);
    double bottom = Math.Max(merged.Y + merged.Height, second.Y + second.Height);
    merged.X = left;
    merged.Y = top;
    merged.Width = right - left;
    merged.Height = bottom - top;
    return merged;
}

private PdfAnnotation ConvertExistingContentToAnnotation(PdfPageContent content)
{
    bool isText = content.Type == PageContentType.Text;
    string fontFamily = isText && !string.IsNullOrEmpty(content.FontFamily) ? content.FontFamily : "맑은 고딕";
    double fontSize = isText && content.FontSize > 0 ? content.FontSize : (content.Height > 0 ? content.Height : 12);
    bool isBold = isText && content.IsBold;
    bool isItalic = isText && content.IsItalic;

    double width = content.Width;
    double height = content.Height;
    string editableText = isText ? BuildEditableText(content) : content.Text ?? string.Empty;

    return new PdfAnnotation
    {
        Type = isText ? AnnotationType.Text : AnnotationType.Image,
        PageIndex = _currentPageIndex,
        X = content.X,
        Y = content.Y,
        Content = editableText,
        Width = width,
        Height = height,
        IsOriginalTextReplacement = isText,
        IsOriginalImageReplacement = !isText,
        OriginalPdfX = content.OriginalPdfX,
        OriginalPdfY = content.OriginalPdfY,
        OriginalText = editableText,
        OriginalImageName = isText ? null : content.ImageId,
        ImagePath = isText ? null : (string.IsNullOrEmpty(content.Text) ? null : content.Text),
        OperatorId = content.OperatorId,
        ContentStreamIndex = content.ContentStreamIndex,
        ContentStreamObjectNumber = content.ContentStreamObjectNumber,
        OperationIndex = content.OperationIndex,
        TextRenderMode = content.TextRenderMode,
        LineHeight = content.LineHeight,
        BaselineOffset = content.BaselineOffset,
        OriginalFontObjectNumber = content.OriginalFontObjectNumber,
        TextFragments = content.TextFragments.Select(fragment => fragment.Clone()).ToList(),
        FontSize = fontSize,
        FontFamily = fontFamily,
        Color = content.Color,
        IsBold = isBold,
        IsItalic = isItalic,
        IsApplied = false
    };
}

private static string BuildEditableText(PdfPageContent content)
{
    string fallback = NormalizeLineEndings(content.Text ?? string.Empty);
    if (content.TextFragments.Count < 2)
        return fallback;

    var indexedLines = content.TextFragments
        .GroupBy(fragment => fragment.LineIndex)
        .OrderBy(group => group.Key)
        .Select(group => group.OrderBy(fragment => fragment.X).ToList())
        .ToList();

    // 일부 PDF는 여러 줄을 하나의 텍스트 그룹으로 추출하면서 모든 조각의
    // LineIndex를 0으로 남깁니다. 이 경우 실제 화면 Y 좌표로 다시 나눕니다.
    if (indexedLines.Count == 1)
    {
        indexedLines = new List<List<PdfTextFragment>>();
        foreach (var fragment in content.TextFragments.OrderBy(fragment => fragment.Y).ThenBy(fragment => fragment.X))
        {
            var line = indexedLines.LastOrDefault();
            double lineTolerance = Math.Max(1.25, Math.Max(fragment.FontSize, content.FontSize) * 0.35);
            if (line == null || Math.Abs(fragment.Y - line.Average(item => item.Y)) > lineTolerance)
                indexedLines.Add(new List<PdfTextFragment>());

            indexedLines[^1].Add(fragment);
        }
    }

    if (indexedLines.Count < 2)
        return fallback;

    var lines = indexedLines
        .Select(line => JoinTextFragments(line.OrderBy(fragment => fragment.X)))
        .Where(line => line.Length > 0)
        .ToList();

    return lines.Count >= 2 ? string.Join("\r\n", lines) : fallback;
}

private static string BuildEditableText(PdfAnnotation annotation)
{
    return BuildEditableText(new PdfPageContent
    {
        Text = annotation.Content,
        FontSize = annotation.FontSize,
        TextFragments = annotation.TextFragments
    });
}

private static string JoinTextFragments(IEnumerable<PdfTextFragment> fragments)
{
    string result = string.Empty;
    PdfTextFragment? previous = null;

    foreach (var fragment in fragments)
    {
        if (previous != null)
        {
            double gap = fragment.X - (previous.X + previous.Width);
            bool needsSpace = gap > Math.Max(previous.FontSize, fragment.FontSize) * 0.15
                && !result.EndsWith(" ", StringComparison.Ordinal)
                && !fragment.Text.StartsWith(" ", StringComparison.Ordinal);
            if (needsSpace)
                result += " ";
        }

        result += fragment.Text;
        previous = fragment;
    }

    return result;
}

private PdfAnnotation ConvertExistingTextToAnnotation(SearchResult textObj)
{
    string fontFamily = !string.IsNullOrEmpty(textObj.FontFamily) ? textObj.FontFamily : "맑은 고딕";
    double fontSize = textObj.FontSize > 0 ? textObj.FontSize : (textObj.Height > 0 ? textObj.Height : 12);
    bool isBold = textObj.IsBold;
    bool isItalic = textObj.IsItalic;
    
    return new PdfAnnotation
    {
        Type = AnnotationType.Text,
        PageIndex = _currentPageIndex,
        X = textObj.X,
        Y = textObj.Y,
        Content = textObj.FoundText ?? string.Empty,
        Width = textObj.Width,
        Height = textObj.Height,
        IsOriginalTextReplacement = true,
        OriginalPdfX = textObj.OriginalPdfX,
        OriginalPdfY = textObj.OriginalPdfY,
        OriginalText = textObj.FoundText ?? string.Empty,
        OperatorId = textObj.OperatorId,
        ContentStreamIndex = textObj.ContentStreamIndex,
        OperationIndex = textObj.OperationIndex,
        TextRenderMode = textObj.TextRenderMode,
        FontSize = fontSize,
        FontFamily = fontFamily,
        Color = textObj.Color,
        IsBold = isBold,
        IsItalic = isItalic,
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

                if (!_hasMovedAnnotation)
                {
                    double pixelDistance = Math.Sqrt(
                        Math.Pow(pos.X - _lastMousePos.X, 2) +
                        Math.Pow(pos.Y - _lastMousePos.Y, 2));
                    if (pixelDistance < 3)
                        return;
                    _hasMovedAnnotation = true;
                }

                foreach (var ann in _selectedAnnotations)
                {
                    ann.X += dx;
                    ann.Y += dy;
                    ann.IsApplied = false;
                }

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
                _isMovingAnnotation = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);

                if (!_hasMovedAnnotation)
                {
                    _moveStartPositions.Clear();
                    RenderAnnotationOverlays();
                    return;
                }

                var originalTextTargets = _selectedAnnotations
                    .Where(annotation => annotation.IsOriginalTextReplacement)
                    .ToList();
                if (originalTextTargets.Count > 0)
                {
                    bool removed = await _pdfManager.RemoveOriginalTextAnnotationsAsync(
                        _currentPageIndex, originalTextTargets);
                    if (!removed)
                    {
                        foreach (var pair in _moveStartPositions)
                        {
                            pair.Key.X = pair.Value.X;
                            pair.Key.Y = pair.Value.Y;
                        }
                        TxtStatus.Text = "이 PDF의 텍스트는 배경을 보존한 상태로 이동할 수 없습니다.";
                        _moveStartPositions.Clear();
                        RenderAnnotationOverlays();
                        return;
                    }

                    foreach (var annotation in originalTextTargets)
                        annotation.IsOriginalTextReplacement = false;
                }

                _moveStartPositions.Clear();
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
                        Color = _fontSettings.HighlightColor,
                        Opacity = _fontSettings.HighlightOpacity,
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
            var editingAnn = activeBox?.Tag switch
            {
                InlineTextEditSession session => session.Annotation,
                PdfAnnotation annotation => annotation,
                _ => null
            };

            var pageAnnotations = _annotations.Where(a => a.PageIndex == _currentPageIndex).ToList();
            int insertIndex = 0;

            foreach (var ann in pageAnnotations)
            {
                if (ann == editingAnn) continue;
                if (ann.IsOriginalTextReplacement && !_selectedAnnotations.Contains(ann))
                    continue;
                FrameworkElement? element = null;

                switch (ann.Type)
                {
                    case AnnotationType.Text:
                    case AnnotationType.FreeText:
                        if (ann.IsOriginalTextReplacement)
                        {
                            // 원문은 PDF 페이지 이미지가 그대로 보여 주므로 선택 테두리용
                            // 투명 영역만 만듭니다. 텍스트를 다시 그리면 폰트와 크기가 달라집니다.
                            element = new Microsoft.UI.Xaml.Shapes.Rectangle
                            {
                                Width = Math.Max(ann.Width * PdfToPixels, 1),
                                Height = Math.Max(ann.Height * PdfToPixels, 1),
                                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                                IsHitTestVisible = false
                            };
                        }
                        else
                        {
                            bool hasOriginalLineLayout = ann.TextFragments.Count > 1;
                            bool containsLineBreak = ContainsLineBreak(ann.Content);
                            double displayFontSize = hasOriginalLineLayout
                                ? GetOriginalLayoutFontSize(ann, ann.Content)
                                : ann.FontSize;
                            element = new TextBlock
                            {
                                Text = ann.Content,
                                // 원문에서 가져온 텍스트는 PDF의 줄 경계를 그대로 사용합니다.
                                // WinUI가 다시 폭을 계산하면 수정 후 줄이 추가되어 박스가 커집니다.
                                TextWrapping = hasOriginalLineLayout
                                    ? TextWrapping.NoWrap
                                    : containsLineBreak ? TextWrapping.Wrap : TextWrapping.NoWrap,
                                Width = hasOriginalLineLayout || containsLineBreak
                                    ? Math.Max(ann.Width * PdfToPixels, 1)
                                    : double.NaN,
                                Height = hasOriginalLineLayout
                                    ? Math.Max(ann.Height * PdfToPixels, 1)
                                    : containsLineBreak
                                        ? GetLineAwareHeight(ann, ann.Content, ann.FontSize)
                                        : double.NaN,
                                FontFamily = new FontFamily(ann.FontFamily),
                                FontSize = displayFontSize * PdfToPixels,
                                Foreground = new SolidColorBrush(ParseColor(ann.Color)),
                                FontWeight = ann.IsBold
                                    ? Microsoft.UI.Text.FontWeights.Bold
                                    : Microsoft.UI.Text.FontWeights.Normal,
                                FontStyle = ann.IsItalic
                                    ? Windows.UI.Text.FontStyle.Italic
                                    : Windows.UI.Text.FontStyle.Normal,
                                RenderTransform = hasOriginalLineLayout
                                    ? new TranslateTransform
                                    {
                                        Y = GetOriginalLayoutTopOffset(ann, displayFontSize)
                                    }
                                    : null,
                                Padding = new Thickness(0),
                                Margin = new Thickness(0),
                                IsHitTestVisible = false
                            };
                        }
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
                        if (_selectedAnnotations.Contains(ann))
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

                            // 이미지나 하이라이트는 크기 조정 핸들 표시 (단일 선택 시에만 또는 가장 최근 선택 항목)
                            if (ann == _selectedAnnotation && (ann.Type == AnnotationType.Image || ann.Type == AnnotationType.Highlight))
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

        private static double GetOriginalLayoutFontSize(PdfAnnotation annotation, string text)
        {
            if (annotation.FontSize <= 0 || annotation.Width <= 0 || annotation.TextFragments.Count < 2)
                return annotation.FontSize;

            try
            {
                // MeasureText는 PDF 포인트 단위의 실제 WinUI 측정 폭을 반환합니다.
                // TextBox의 좌우 1px 테두리를 제외하고 원문 bounds 안에 들어오도록
                // 표시용 크기만 보정합니다. PDF에 저장되는 ann.FontSize는 바꾸지 않습니다.
                var measured = MeasureTextForOriginalLayout(
                    text,
                    annotation.FontFamily,
                    annotation.FontSize,
                    annotation.IsBold,
                    annotation.IsItalic);
                double availableWidth = Math.Max(annotation.Width - (2.0 / PdfToPixels), 1);
                if (measured <= availableWidth)
                    return annotation.FontSize;

                double scale = availableWidth / measured;
                return Math.Max(annotation.FontSize * scale, 1);
            }
            catch
            {
                return annotation.FontSize;
            }
        }

        private static double GetOriginalLayoutTopOffset(PdfAnnotation annotation, double displayFontSize)
        {
            if (annotation.BaselineOffset <= 0.1)
                return 0;

            double uiBaselineOffset = displayFontSize * PdfToPixels * 0.8;
            try
            {
                var sample = new TextBlock
                {
                    Text = "Ag",
                    FontFamily = new FontFamily(annotation.FontFamily),
                    FontSize = displayFontSize * PdfToPixels,
                    FontWeight = annotation.IsBold
                        ? Microsoft.UI.Text.FontWeights.Bold
                        : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = annotation.IsItalic
                        ? Windows.UI.Text.FontStyle.Italic
                        : Windows.UI.Text.FontStyle.Normal,
                    TextWrapping = TextWrapping.NoWrap,
                    Padding = new Thickness(0)
                };
                sample.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

                var baselineProperty = typeof(TextBlock).GetProperty("BaselineOffset");
                if (baselineProperty?.GetValue(sample) is double measuredBaseline && measuredBaseline > 0.1)
                    uiBaselineOffset = measuredBaseline;
            }
            catch
            {
                // Use the font-size estimate when the platform does not expose the baseline.
            }

            // PDF bounds are top-based, while the text control positions its glyphs
            // from the baseline. Account for the TextBox's 1px border as well.
            return annotation.BaselineOffset * PdfToPixels - (uiBaselineOffset + 1);
        }

        private static double MeasureTextForOriginalLayout(
            string text,
            string fontFamily,
            double fontSize,
            bool isBold,
            bool isItalic)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(fontFamily),
                FontSize = fontSize * PdfToPixels,
                FontWeight = isBold
                    ? Microsoft.UI.Text.FontWeights.Bold
                    : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = isItalic
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal,
                TextWrapping = TextWrapping.NoWrap,
                Padding = new Thickness(0)
            };
            textBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max((textBlock.DesiredSize.Width - 2.0) / PdfToPixels, 1);
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

                rect.PointerPressed += (s, e) =>
                {
                    if (s is Microsoft.UI.Xaml.Shapes.Rectangle r && r.Tag is string dir)
                    {
                        _isResizingAnnotation = true;
                        _resizeHandle = dir;
                        _lastMousePos = e.GetCurrentPoint(OverlayCanvas).Position;
                        OverlayCanvas.CapturePointer(e.Pointer);
                        e.Handled = true;
                    }
                };
                rect.PointerEntered += (s, e) =>
                {
                    if (s is Microsoft.UI.Xaml.Shapes.Rectangle r && r.Tag is string dir)
                    {
                        SetElementCursor(OverlayCanvas, Microsoft.UI.Input.InputSystemCursor.Create(dir switch
                        {
                            "NW" or "SE" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
                            "NE" or "SW" => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
                            "N" or "S" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
                            "E" or "W" => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
                            _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow
                        }));
                    }
                };
                rect.PointerExited += (s, e) => SetElementCursor(OverlayCanvas, Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow));

                OverlayCanvas.Children.Insert(insertIndex++, rect);
            }
        }

        private void EditAnnotationContent(PdfAnnotation ann)
        {
            if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText)
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

        private static bool ContainsLineBreak(string? text)
        {
            return !string.IsNullOrEmpty(text) &&
                (text.Contains('\r') || text.Contains('\n'));
        }

        private static int GetLineCount(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 1;

            return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n').Length;
        }

        private static double GetLineAwareHeight(PdfAnnotation annotation, string text, double fontSize)
        {
            // PDF의 LineHeight는 일부 파일에서 실제 UI 글꼴 높이보다 작게
            // 추출됩니다. 실제 편집 글꼴 기준으로 필요한 높이를 계산합니다.
            double uiLineHeight = Math.Max(
                fontSize * PdfToPixels * 1.35,
                annotation.LineHeight > 0.1 ? annotation.LineHeight * PdfToPixels : 0);
            double requiredUiHeight = GetLineCount(text) * uiLineHeight + 8;
            return Math.Max(annotation.Height * PdfToPixels, requiredUiHeight);
        }

        private static double MeasureInlineTextHeight(
            string text,
            double width,
            string fontFamily,
            double fontSize,
            bool isBold,
            bool isItalic)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                Width = Math.Max(width, 1),
                FontFamily = new FontFamily(fontFamily),
                FontSize = fontSize * PdfToPixels,
                FontWeight = isBold
                    ? Microsoft.UI.Text.FontWeights.Bold
                    : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = isItalic
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0)
            };
            textBlock.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            return textBlock.DesiredSize.Height + 8;
        }
        private static string NormalizeLineEndings(string text)
        {
            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        private static bool IsControlKeyDown()
        {
            try
            {
                if ((GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VirtualKeyLeftControl) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VirtualKeyRightControl) & 0x8000) != 0)
                    return true;
            }
            catch
            {
                // InputKeyboardSource fallback below handles restricted environments.
            }

            return new[]
            {
                Windows.System.VirtualKey.Control,
                Windows.System.VirtualKey.LeftControl,
                Windows.System.VirtualKey.RightControl
            }.Any(key => Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));
        }

        private static bool IsControlVirtualKey(Windows.System.VirtualKey key)
        {
            return key == Windows.System.VirtualKey.Control ||
                key == Windows.System.VirtualKey.LeftControl ||
                key == Windows.System.VirtualKey.RightControl;
        }

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
            string initialText = existingAnn != null
                ? BuildEditableText(existingAnn)
                : string.Empty;
            double inlineWidth = existingAnn != null
                ? Math.Max(existingAnn.Width * PdfToPixels, 1)
                : double.NaN;
            // 기존 PDF 텍스트는 추출된 줄바꿈을 그대로 보여 주어야 합니다.
            // 자동 줄바꿈을 다시 계산하면 PDF의 실제 글꼴 메트릭과 WinUI 메트릭의
            // 아주 작은 차이만으로도 첫 줄 끝(naso-)이 다음 줄로 밀립니다.
            TextWrapping inlineWrapping = TextWrapping.NoWrap;
            double inlineHeight = existingAnn != null
                ? Math.Max(existingAnn.Height * PdfToPixels, 1)
                : 24;
            double displayFontSize = existingAnn != null
                ? GetOriginalLayoutFontSize(existingAnn, initialText)
                : fontSize;
            double inlineTopOffset = existingAnn != null
                ? GetOriginalLayoutTopOffset(existingAnn, displayFontSize)
                : 0;

            var textBox = new TextBox
            {
                // 줄바꿈이 포함된 원문을 대입하기 전에 여러 줄 입력을
                // 활성화해야 WinUI가 초기 줄바꿈을 제거하지 않습니다.
                AcceptsReturn = existingAnn != null,
                // 기존 원문의 줄바꿈은 PDF에서 추출한 실제 줄 경계입니다.
                // NoWrap은 그 경계를 유지하고 WinUI의 재배치만 막습니다.
                TextWrapping = inlineWrapping,
                Text = initialText,
                MinWidth = existingAnn != null ? 0 : 60,
                // Canvas 안의 TextBox는 Height가 자동인 경우 내부 ScrollViewer가
                // 한 줄 높이로 측정되는 경우가 있습니다. 여러 줄 원문은 필요한
                // 높이를 처음부터 지정해 편집 시작 시 전체 내용을 표시합니다.
                MinHeight = existingAnn != null ? 0 : 24,
                Width = inlineWidth,
                Height = existingAnn != null ? inlineHeight : double.NaN,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                FontSize = displayFontSize * PdfToPixels,
                FontFamily = new FontFamily(fontFamily),
                Foreground = new SolidColorBrush(ParseColor(color)),
                FontWeight = isBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                Tag = existingAnn != null
                    ? new InlineTextEditSession
                    {
                        Annotation = existingAnn,
                        OriginalContent = initialText
                    }
                    : (object)new Windows.Foundation.Point(pdfX, pdfY),
                VerticalAlignment = VerticalAlignment.Top,
                VerticalContentAlignment = VerticalAlignment.Top,
                MaxWidth = 4000,
                UseLayoutRounding = false
            };

            Canvas.SetLeft(textBox, canvasX);
            // Align the editable glyph baseline to the original PDF baseline.
            Canvas.SetTop(textBox, canvasY + inlineTopOffset);

            textBox.Loaded += (s, e) => 
            {
                // 포커스 강제 부여 및 전역 상태 보호
                if (existingAnn != null)
                {
                    textBox.UpdateLayout();
                    // 선택 영역(PDF 원문 bounds)과 편집 박스의 높이를 동일하게 유지합니다.
                    textBox.Height = inlineHeight;
                }
                textBox.Focus(FocusState.Programmatic);
            };

            // 텍스트 박스 내부의 포인터 이벤트가 캔버스 등으로 전달되어 편집이 꼬이는 것 방지
            textBox.PointerPressed += (s, e) => e.Handled = true;
            textBox.PointerReleased += (s, e) => e.Handled = true;
            textBox.DoubleTapped += (s, e) => e.Handled = true;
            if (existingAnn != null)
                textBox.TextChanged += InlineTextBox_TextChanged;

            textBox.KeyDown += async (s, e) =>
            {
                var controlDown = IsControlKeyDown();
                if (e.Key == Windows.System.VirtualKey.Enter &&
                    (!((TextBox)s).AcceptsReturn || controlDown))
                {
                    // TextBox가 Enter를 기본 처리하여 줄바꿈을 넣기 전에 먼저 소비합니다.
                    // await 뒤에 설정하면 기본 동작이 이미 실행될 수 있습니다.
                    e.Handled = true;
                    await ApplyInlineTextAsync((TextBox)s);
                }
                else if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    CancelInlineEdit(textBox);
                }
            };

            textBox.LostFocus += async (s, e) =>
            {
                await ApplyInlineTextAsync((TextBox)s);
            };

            OverlayCanvas.Children.Add(textBox);
        }

        private async void InlineTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox ||
                textBox.Tag is not InlineTextEditSession session ||
                session.SuppressTextChanged ||
                session.IsFinishing)
                return;

            string changedText = textBox.Text;
            long changeVersion = ++session.TextChangeVersion;
            session.HasLiveChanges = true;

            if (session.Annotation.TextFragments.Count > 1)
            {
                // 원문 bounds를 유지하면서도 편집 중 긴 문자열이 오른쪽에서
                // 잘리지 않도록 표시용 글꼴 크기를 현재 내용에 맞춰 조정합니다.
                double displayFontSize = GetOriginalLayoutFontSize(session.Annotation, changedText);
                textBox.FontSize = displayFontSize * PdfToPixels;
                Canvas.SetTop(
                    textBox,
                    session.Annotation.Y * PdfToPixels +
                    GetOriginalLayoutTopOffset(session.Annotation, displayFontSize));
            }

            if (session.Annotation.IsOriginalTextReplacement && session.RemovalTask == null)
            {
                session.RemovalTask = RemoveOriginalTextForLiveEditAsync(session);
                bool removed = await session.RemovalTask;
                if (!removed && OverlayCanvas.Children.Contains(textBox))
                {
                    session.SuppressTextChanged = true;
                    session.Annotation.Content = session.OriginalContent;
                    textBox.Text = session.OriginalContent;
                    textBox.SelectionStart = textBox.Text.Length;
                    session.SuppressTextChanged = false;
                    session.HasLiveChanges = false;
                    TxtStatus.Text = "배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.";
                    return;
                }
            }

            if (session.RemovalTask != null && !await session.RemovalTask)
                return;

            // 여러 키 입력이 원문 삭제 작업을 기다리는 동안 누적될 수 있습니다.
            // 최신 이벤트가 아니면 오래된 문자열로 내용을 되돌리지 않습니다.
            if (session.IsFinishing || changeVersion != session.TextChangeVersion ||
                !OverlayCanvas.Children.Contains(textBox))
                return;

            session.Annotation.Content = changedText;
            session.Annotation.IsApplied = false;

            TxtStatus.Text = string.IsNullOrEmpty(changedText)
                ? "텍스트가 삭제되었습니다 (편집 중)"
                : "텍스트 편집 내용이 실시간 반영 중입니다.";
        }

        private async Task<bool> RemoveOriginalTextForLiveEditAsync(InlineTextEditSession session)
        {
            bool removed = await _pdfManager.RemoveOriginalTextAnnotationsAsync(
                _currentPageIndex, new[] { session.Annotation });
            if (!removed)
                return false;

            session.Annotation.IsOriginalTextReplacement = false;
            session.OriginalRemovalCommitted = true;
            _renderTempPath = null;
            await RenderCurrentPageAsync();
            RenderAnnotationOverlays();
            return true;
        }

        private async void CancelInlineEdit(TextBox textBox)
        {
            if (!_isInlineEditing || !OverlayCanvas.Children.Contains(textBox)) return;

            if (textBox.Tag is InlineTextEditSession sessionToCancel)
            {
                sessionToCancel.IsFinishing = true;
                sessionToCancel.TextChangeVersion++;
            }

            _isInlineEditing = false;
            OverlayCanvas.Children.Remove(textBox);

            if (textBox.Tag is InlineTextEditSession session)
            {
                if (session.RemovalTask != null)
                    await session.RemovalTask;

                if (session.OriginalRemovalCommitted && _pdfManager.CanUndo)
                {
                    _pdfManager.Undo();
                }
                else
                {
                    session.Annotation.Content = session.OriginalContent;
                    session.Annotation.IsApplied = false;
                    RenderAnnotationOverlays();
                }
            }
            
            // 포커스 복구 (단축키 작동을 위해 중요)
            DispatcherQueue.TryEnqueue(() =>
            {
                DocTabView.Focus(FocusState.Programmatic);
            });
        }

        private async Task ApplyInlineTextAsync(TextBox textBox)
        {
            if (!_isInlineEditing || !OverlayCanvas.Children.Contains(textBox)) return;
            if (textBox.Tag is InlineTextEditSession activeSession && activeSession.IsFinishing)
                return;

            string text = textBox.Text;
            object tag = textBox.Tag;
            bool textWasRemoved = false;
            var editSession = tag as InlineTextEditSession;

            if (editSession != null)
            {
                editSession.IsFinishing = true;
                editSession.TextChangeVersion++;
                editSession.SuppressTextChanged = true;
            }

            // TextChanged가 실행 중인 편집 내용을 빈 문자열로 덮어쓰지 않도록
            // 이벤트를 억제한 뒤 편집창을 제거합니다.
            textBox.Text = "";
            OverlayCanvas.Children.Remove(textBox);
            if (editSession != null)
                editSession.SuppressTextChanged = false;
            _isInlineEditing = false;

            try
            {
                PdfAnnotation? existingAnn = tag switch
                {
                    InlineTextEditSession session => session.Annotation,
                    PdfAnnotation annotation => annotation,
                    _ => null
                };

                if (existingAnn != null)
                {
                    if (editSession?.RemovalTask != null && !await editSession.RemovalTask)
                    {
                        existingAnn.Content = editSession.OriginalContent;
                        RenderAnnotationOverlays();
                        return;
                    }

                    // 편집 모드
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // 텍스트를 모두 지우면 삭제로 간주
                        if (existingAnn.IsOriginalTextReplacement)
                        {
                            textWasRemoved = await _pdfManager.RemoveOriginalTextAnnotationsAsync(
                                _currentPageIndex, new[] { existingAnn });
                            if (!textWasRemoved)
                            {
                                TxtStatus.Text = "배경을 보존하면서 삭제할 수 없는 PDF 텍스트입니다.";
                                return;
                            }
                        }
                        _annotations.Remove(existingAnn);
                        if (_selectedAnnotation == existingAnn) _selectedAnnotation = null;
                        _selectedAnnotations.Remove(existingAnn);
                        _pdfManager.MarkModified();
                        TxtStatus.Text = "텍스트가 삭제되었습니다";
                    }
                    else if (editSession?.HasLiveChanges == true || existingAnn.Content != text)
                    {
                        if (existingAnn.IsOriginalTextReplacement)
                        {
                            textWasRemoved = await _pdfManager.RemoveOriginalTextAnnotationsAsync(
                                _currentPageIndex, new[] { existingAnn });
                            if (!textWasRemoved)
                            {
                                TxtStatus.Text = "배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.";
                                return;
                            }
                            existingAnn.IsOriginalTextReplacement = false;
                        }
                        
                        existingAnn.Content = text;
                        if (existingAnn.TextFragments.Count == 0)
                        {
                            var size = MeasureText(text, existingAnn.FontFamily, existingAnn.FontSize, existingAnn.IsBold, existingAnn.IsItalic);
                            existingAnn.Width = size.width;
                            existingAnn.Height = size.height;
                        }
                        existingAnn.IsApplied = false;
                        _pdfManager.MarkModified();
                        TxtStatus.Text = "텍스트가 수정되었습니다 (저장 시 반영)";
                    }
                }
                else if (tag is Windows.Foundation.Point pdfPos)
                {
                    // 추가 모드
                    if (string.IsNullOrWhiteSpace(text)) return;

                    var size = MeasureText(text, _fontSettings.FontFamily, _fontSettings.FontSize, _fontSettings.IsBold, _fontSettings.IsItalic);
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
                        Width = size.width,
                        Height = size.height,
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
            ClearSearchHighlights();
        }

        private void FindText_Changed(object sender, TextChangedEventArgs e)
        {
            TxtFindCount.Text = "";
            // 검색어가 바뀌면 상태 초기화
            _lastSearchQuery = null;
            _lastFoundPage = -1;
            _lastFoundWordIndex = -1;
            ClearSearchHighlights();
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
            TxtStatus.Text = "검색 중...";
            LoadingRing.IsActive = true;
            
            try
            {
                var result = await Task.Run(() => {
                    var pdfBytes = _pdfManager.GetPdfBytes();
                    if (pdfBytes == null) return null;

                    using (var ms = new MemoryStream(pdfBytes))
                    using (var reader = new PdfReader(ms))
                    using (var doc = new PdfDocument(reader))
                    {
                        int totalPages = doc.GetNumberOfPages();
                        int startPage = _currentPageIndex + 1;
                        
                        for (int i = 0; i < totalPages; i++)
                        {
                            int pageNum = forward
                                ? ((startPage + i - 1) % totalPages) + 1
                                : ((startPage - i - 1 + totalPages) % totalPages) + 1;
                            
                            var page = doc.GetPage(pageNum);
                            var strategy = new SimpleTextLocationStrategy(searchText);
                            var processor = new iText.Kernel.Pdf.Canvas.Parser.PdfCanvasProcessor(strategy);
                            processor.ProcessPageContent(page);
                            strategy.FindMatches();
                            
                            if (strategy.ResultRects.Count > 0)
                            {
                                return new { 
                                    PageNum = pageNum, 
                                    Found = true, 
                                    Rects = strategy.ResultRects.Select(r => new { L = r.GetLeft(), B = r.GetBottom(), W = r.GetWidth(), H = r.GetHeight(), T = r.GetTop() }).ToList() 
                                };
                            }
                        }
                    }
                    return null;
                });

                if (result != null && result.Found)
                {
                    if (_currentPageIndex != result.PageNum - 1)
                    {
                        _currentPageIndex = result.PageNum - 1;
                        await RenderCurrentPageAsync();
                        SyncPageListSelection();
                    }
                    
                    ClearSearchHighlights();
                    foreach (var r in result.Rects)
                    {
                        HighlightSearchMatch(new iText.Kernel.Geom.Rectangle((float)r.L, (float)r.B, (float)r.W, (float)r.H));
                    }

                    TxtStatus.Text = $"{result.PageNum} 페이지에서 {result.Rects.Count}개의 일치 항목을 찾았습니다.";
                }
                else
                {
                    TxtStatus.Text = "텍스트를 찾을 수 없습니다.";
                    await ShowErrorDialogAsync("검색 결과", $"'{searchText}'를 찾을 수 없습니다.");
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "검색 오류: " + ex.Message;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private void ClearSearchHighlights()
        {
            var toRemove = OverlayCanvas.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>()
                .Where(r => r.Tag?.ToString() == "SearchHighlight").ToList();
            foreach (var r in toRemove) OverlayCanvas.Children.Remove(r);
        }

        private void HighlightSearchMatch(iText.Kernel.Geom.Rectangle rect)
        {
            // PDF 좌표 (Bottom-Up) -> Canvas 좌표 (Top-Down) 변환
            var pageSize = _pdfManager.GetPageSize(_currentPageIndex);
            
            // PDF coordinates are relative to the page size. 
            // We need to ensure we use the same scale as other annotations.
            double x = rect.GetLeft() * PdfToPixels;
            double width = rect.GetWidth() * PdfToPixels;
            
            // rect.GetTop() is higher than GetBottom() in PDF coordinates.
            double pdfTop = rect.GetTop();
            double y = (pageSize.height - pdfTop) * PdfToPixels;
            double height = rect.GetHeight() * PdfToPixels;

            var highlight = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                Opacity = 0.4,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };

            Canvas.SetLeft(highlight, x);
            Canvas.SetTop(highlight, y);
            OverlayCanvas.Children.Add(highlight);

            // 해당 위치로 스크롤 (첫 번째 일치 항목만 스크롤하도록 호출자가 제어할 수도 있지만 여기서는 일단 이동)
            PdfScrollViewer.ChangeView(x * _zoomLevel, y * _zoomLevel, null);
        }

        private class SimpleTextLocationStrategy : iText.Kernel.Pdf.Canvas.Parser.Listener.ITextExtractionStrategy
        {
            private readonly string _query;
            private readonly List<iText.Kernel.Pdf.Canvas.Parser.Data.TextRenderInfo> _infos = new();
            public List<iText.Kernel.Geom.Rectangle> ResultRects { get; } = new();

            public SimpleTextLocationStrategy(string query)
            {
                _query = query.Normalize(System.Text.NormalizationForm.FormC).ToLower();
            }

            public void EventOccurred(iText.Kernel.Pdf.Canvas.Parser.Data.IEventData data, iText.Kernel.Pdf.Canvas.Parser.EventType type)
            {
                if (type == iText.Kernel.Pdf.Canvas.Parser.EventType.RENDER_TEXT)
                {
                    var textInfo = (iText.Kernel.Pdf.Canvas.Parser.Data.TextRenderInfo)data;
                    textInfo.PreserveGraphicsState();
                    _infos.Add(textInfo);
                }
            }

            public ICollection<iText.Kernel.Pdf.Canvas.Parser.EventType> GetSupportedEvents() => 
                new[] { iText.Kernel.Pdf.Canvas.Parser.EventType.RENDER_TEXT };

            public string GetResultantText() => "";

            public void FindMatches()
            {
                if (string.IsNullOrEmpty(_query)) return;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                List<int> charToInfoIndex = new List<int>();

                for (int i = 0; i < _infos.Count; i++)
                {
                    string text = _infos[i].GetText();
                    if (string.IsNullOrEmpty(text)) continue;
                    
                    text = text.Normalize(System.Text.NormalizationForm.FormC).ToLower();
                    foreach (char c in text)
                    {
                        sb.Append(c);
                        charToInfoIndex.Add(i);
                    }
                    // Add a space to separate tokens if they are logically separate, 
                    // but iText usually provides spaces as separate RENDER_TEXT events.
                }

                string fullText = sb.ToString();
                int idx = fullText.IndexOf(_query);
                while (idx != -1)
                {
                    int lastCharIdx = idx + _query.Length - 1;
                    if (lastCharIdx < charToInfoIndex.Count)
                    {
                        int startInfo = charToInfoIndex[idx];
                        int endInfo = charToInfoIndex[lastCharIdx];

                        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                        for (int i = startInfo; i <= endInfo; i++)
                        {
                            var info = _infos[i];
                            var baseline = info.GetBaseline().GetStartPoint();
                            var ascent = info.GetAscentLine().GetEndPoint();
                            var descent = info.GetDescentLine().GetStartPoint();

                            minX = Math.Min(minX, Math.Min(baseline.Get(0), info.GetAscentLine().GetStartPoint().Get(0)));
                            minY = Math.Min(minY, descent.Get(1));
                            maxX = Math.Max(maxX, Math.Max(info.GetAscentLine().GetEndPoint().Get(0), info.GetBaseline().GetEndPoint().Get(0)));
                            maxY = Math.Max(maxY, ascent.Get(1));
                        }

                        if (minX != float.MaxValue)
                        {
                            ResultRects.Add(new iText.Kernel.Geom.Rectangle(minX, minY, maxX - minX, maxY - minY));
                        }
                    }
                    idx = fullText.IndexOf(_query, idx + 1);
                }
            }
        }

        #endregion

        #region Keyboard Shortcuts Extension
        private async void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (IsControlVirtualKey(e.Key))
            {
                _controlKeyIsDown = true;
                return;
            }

            // TextBox의 KeyDown에서 Ctrl 상태를 놓치는 경우에도 창 레벨에서
            // Enter를 먼저 소비하여 줄바꿈 대신 편집을 확정합니다.
            if (_isInlineEditing &&
                e.Key == Windows.System.VirtualKey.Enter &&
                (_controlKeyIsDown || IsControlKeyDown()))
            {
                var activeTextBox = OverlayCanvas.Children.OfType<TextBox>().FirstOrDefault();
                if (activeTextBox == null ||
                    activeTextBox.Tag is not InlineTextEditSession { IsFinishing: true })
                {
                    e.Handled = true;
                    if (activeTextBox != null)
                        await ApplyInlineTextAsync(activeTextBox);
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Delete && _selectedAnnotation != null && !_isInlineEditing && !_isDialogOpen)
            {
                await DeleteSelectedAnnotationsAsync();
                e.Handled = true;
            }
        }

        private void MainWindow_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (IsControlVirtualKey(e.Key))
                _controlKeyIsDown = false;
        }

        private async Task<bool> DeleteSelectedAnnotationsAsync()
        {
            var targets = _selectedAnnotations.Count > 0
                ? _selectedAnnotations.ToList()
                : (_selectedAnnotation != null ? new List<PdfAnnotation> { _selectedAnnotation } : new List<PdfAnnotation>());
            if (targets.Count == 0)
                return false;

            var originalTextTargets = targets
                .Where(annotation => annotation.IsOriginalTextReplacement)
                .ToList();
            if (originalTextTargets.Count > 0)
            {
                bool removed = await _pdfManager.RemoveOriginalTextAnnotationsAsync(
                    _currentPageIndex, originalTextTargets);
                if (!removed)
                {
                    TxtStatus.Text = "배경을 보존하면서 삭제할 수 없는 PDF 텍스트입니다.";
                    return false;
                }
            }

            foreach (var annotation in targets)
                _annotations.Remove(annotation);

            _selectedAnnotations.Clear();
            _selectedAnnotation = null;
            _pdfManager.MarkModified();
            if (originalTextTargets.Count > 0)
                await RenderCurrentPageAsync();
            RenderAnnotationOverlays();
            TxtStatus.Text = targets.Count > 1 ? $"{targets.Count}개 객체 삭제됨" : "텍스트가 삭제되었습니다.";
            return true;
        }
        #endregion

        #region Font Settings

        private async Task<bool> PrepareOriginalTextForReplacementAsync(PdfAnnotation annotation)
        {
            if (!annotation.IsOriginalTextReplacement)
                return true;

            bool removed = await _pdfManager.RemoveOriginalTextAnnotationsAsync(
                _currentPageIndex, new[] { annotation });
            if (!removed)
            {
                TxtStatus.Text = "배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.";
                return false;
            }

            annotation.IsOriginalTextReplacement = false;
            await RenderCurrentPageAsync();
            return true;
        }

        private async void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFontFamily.SelectedItem is ComboBoxItem item)
            {
                string font = item.Content?.ToString() ?? "맑은 고딕";
                _fontSettings.FontFamily = font;
                SaveWindowPosition();
                
                if (_selectedAnnotation != null && (_selectedAnnotation.Type == AnnotationType.Text || _selectedAnnotation.Type == AnnotationType.FreeText))
                {
                    if (!await PrepareOriginalTextForReplacementAsync(_selectedAnnotation))
                        return;
                    _selectedAnnotation.FontFamily = font;
                    _selectedAnnotation.OriginalFontObjectNumber = -1;
                    var size = MeasureText(_selectedAnnotation.Content, font, _selectedAnnotation.FontSize, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
                    _selectedAnnotation.Width = size.width;
                    _selectedAnnotation.Height = size.height;
                    _pdfManager.MarkModified();
                    RenderAnnotationOverlays();
                }
            }
        }

        private async void FontSize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFontSize.SelectedItem is ComboBoxItem item && 
                double.TryParse(item.Content?.ToString(), out double sizeVal))
            {
                _fontSettings.FontSize = sizeVal;
                SaveWindowPosition();
                
                if (_selectedAnnotation != null && (_selectedAnnotation.Type == AnnotationType.Text || _selectedAnnotation.Type == AnnotationType.FreeText))
                {
                    if (!await PrepareOriginalTextForReplacementAsync(_selectedAnnotation))
                        return;
                    _selectedAnnotation.FontSize = sizeVal;
                    var size = MeasureText(_selectedAnnotation.Content, _selectedAnnotation.FontFamily, sizeVal, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
                    _selectedAnnotation.Width = size.width;
                    _selectedAnnotation.Height = size.height;
                    _pdfManager.MarkModified();
                    RenderAnnotationOverlays();
                }
            }
        }

        private async void FontBold_Click(object sender, RoutedEventArgs e)
        {
            _fontSettings.IsBold = BtnBold.IsChecked == true;
            SaveWindowPosition();
            if (_selectedAnnotation != null && (_selectedAnnotation.Type == AnnotationType.Text || _selectedAnnotation.Type == AnnotationType.FreeText))
            {
                if (!await PrepareOriginalTextForReplacementAsync(_selectedAnnotation))
                    return;
                _selectedAnnotation.IsBold = _fontSettings.IsBold;
                _selectedAnnotation.OriginalFontObjectNumber = -1;
                var size = MeasureText(_selectedAnnotation.Content, _selectedAnnotation.FontFamily, _selectedAnnotation.FontSize, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
                _selectedAnnotation.Width = size.width;
                _selectedAnnotation.Height = size.height;
                _pdfManager.MarkModified();
                RenderAnnotationOverlays();
            }
        }

        private async void FontItalic_Click(object sender, RoutedEventArgs e)
        {
            _fontSettings.IsItalic = BtnItalic.IsChecked == true;
            SaveWindowPosition();
            if (_selectedAnnotation != null && (_selectedAnnotation.Type == AnnotationType.Text || _selectedAnnotation.Type == AnnotationType.FreeText))
            {
                if (!await PrepareOriginalTextForReplacementAsync(_selectedAnnotation))
                    return;
                _selectedAnnotation.IsItalic = _fontSettings.IsItalic;
                _selectedAnnotation.OriginalFontObjectNumber = -1;
                var size = MeasureText(_selectedAnnotation.Content, _selectedAnnotation.FontFamily, _selectedAnnotation.FontSize, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
                _selectedAnnotation.Width = size.width;
                _selectedAnnotation.Height = size.height;
                _pdfManager.MarkModified();
                RenderAnnotationOverlays();
            }
        }

        private async void FontColor_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ColorPalette.SelectedItem is Border border && border.Tag is string color)
            {
                _fontSettings.Color = color;
                FontColorIndicator.Background = new SolidColorBrush(ParseColor(color));
                SaveWindowPosition();

                if (_selectedAnnotation != null)
                {
                    if ((_selectedAnnotation.Type == AnnotationType.Text || _selectedAnnotation.Type == AnnotationType.FreeText) &&
                        !await PrepareOriginalTextForReplacementAsync(_selectedAnnotation))
                        return;
                    _selectedAnnotation.Color = color;
                    _pdfManager.MarkModified();
                    RenderAnnotationOverlays();
                }
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
                        // 1. 현재 문서에 합치기
                        TxtStatus.Text = "PDF 합치기 중...";
                        LoadingRing.IsActive = true;

                        try
                        {
                            // 현재 편집 중인 내용을 임시 저장 (고유한 파일명 사용으로 캐시 문제 방지)
                            string originPath = _pdfManager.FilePath ?? Path.Combine(ApplicationData.Current.TemporaryFolder.Path, $"merging_{Guid.NewGuid()}.pdf");
                            await _pdfManager.SaveAsAsync(originPath, false);

                            // 합치기 실행
                            bool success = await _pdfManager.MergeFilesAsync(files.Select(f => f.Path).ToList(), originPath);
                            
                            if (success)
                            {
                                await _pdfManager.OpenAsync(originPath);
                                _currentPageIndex = 0;
                                
                                // [추가] 렌더링 캐시 초기화 및 탭 정보 동기화
                                _renderTempPath = null;
                                if (_activeTab != null) _activeTab.FilePath = originPath;

                                // LoadThumbnailsAsync will be triggered by OpenAsync -> DocumentChanged event
                                TxtStatus.Text = "PDF 합치기 완료";
                            }
                            else
                            {
                                await ShowErrorDialogAsync("오류", "PDF 합치기에 실패했습니다.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await ShowErrorDialogAsync("오류", $"합치기 중 에러 발생: {ex.Message}");
                        }
                        finally
                        {
                            LoadingRing.IsActive = false;
                        }
                    }
                    else if (result == ContentDialogResult.Secondary)
                    {
                        // 2. 새 파일로 저장하며 합치기
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

                bool success = await _pdfManager.MergeFilesAsync(filePaths, file.Path);
                if (success)
                {
                    await _pdfManager.OpenAsync(file.Path);
                    _currentPageIndex = 0;
                    _renderTempPath = null; // Reset temp path to use the new file path
                    // LoadThumbnailsAsync will be triggered by OpenAsync -> DocumentChanged event
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

                    try
                    {
                        var ranges = new List<(int start, int end)>();
                        int totalPages = _pdfManager.PageCount;

                        if (rbEveryPage.IsChecked == true)
                        {
                            for (int i = 1; i <= totalPages; i++)
                                ranges.Add((i, i));
                        }
                        else if (rbByPages.IsChecked == true)
                        {
                            int perFile = (int)nbPagesPerFile.Value;
                            for (int i = 1; i <= totalPages; i += perFile)
                                ranges.Add((i, Math.Min(i + perFile - 1, totalPages)));
                        }
                        else if (rbByRange.IsChecked == true)
                        {
                            ranges = ParsePageRanges(txtRanges.Text);
                        }

                        if (ranges.Count > 0)
                        {
                            int resultCount = await _pdfManager.SplitFileAsync(folder.Path, ranges);
                            TxtStatus.Text = $"PDF 나누기 완료: {resultCount}개 파일 생성됨";
                            
                            // 폴더 열기 제안 등은 생략
                        }
                        else
                        {
                            TxtStatus.Text = "나눌 페이지 범위가 올바르지 않습니다.";
                        }
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialogAsync("나누기 오류", $"나누기 중 에러 발생: {ex.Message}");
                    }
                    finally
                    {
                        LoadingRing.IsActive = false;
                    }
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

                // [중요] 렌더링 캐시 초기화
                _renderTempPath = null;
                
                // PdfManager.DeletePage()가 DocumentChanged를 호출하고, 
                // 이는 PdfManager_DocumentChanged 핸들러에 의해 자동으로 Render 및 Thumbnail 로드를 수행합니다.
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
                defaultFont.Items.Add(item);
            }
            defaultFont.SelectedItem = defaultFont.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Content?.ToString(),
                    _fontSettings.FontFamily,
                    StringComparison.Ordinal));
            defaultFont.SelectionChanged += (s, _) =>
            {
                if (defaultFont.SelectedItem is ComboBoxItem item)
                {
                    _fontSettings.FontFamily = item.Content?.ToString() ?? "맑은 고딕";
                    SaveWindowPosition();
                    ApplyFontSettingsToControls();
                }
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
                {
                    _fontSettings.FontSize = sizeVal;
                    SaveWindowPosition();
                    ApplyFontSettingsToControls();
                }
            };
            panel.Children.Add(defaultSize);

            panel.Children.Add(new TextBlock
            {
                Text = "설정은 자동으로 저장되며 다음 실행에도 적용됩니다.",
                Opacity = 0.5, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });

            dialog.Content = panel;
            await dialog.ShowAsync();

            ApplyFontSettingsToControls();
            SaveWindowPosition();

            if (_pdfManager.IsLoaded)
                await RenderCurrentPageAsync();
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var aboutPanel = new StackPanel { Spacing = 8 };
            aboutPanel.Children.Add(new TextBlock { Text = "PDF Simple Editor", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            aboutPanel.Children.Add(new TextBlock { Text = "버전 1.0.0" });
            aboutPanel.Children.Add(new TextBlock { Text = "WinUI 3 + iText 9 기반 PDF 편집기", Opacity = 0.7 });
            aboutPanel.Children.Add(new TextBlock { Text = "한글 폰트 지원", Opacity = 0.7 });
            aboutPanel.Children.Add(new HyperlinkButton 
            { 
                Content = "GitHub: kirinonakar/PDF_simple_edit", 
                NavigateUri = new Uri("https://github.com/kirinonakar/PDF_simple_edit"),
                Padding = new Thickness(0)
            });
            aboutPanel.Children.Add(new TextBlock
            {
                Text = "\n기능:\n• PDF 열기/저장/인쇄\n• 텍스트 추가/편집/바꾸기\n• 텍스트 강조 표시\n• 이미지 삽입\n• PDF 합치기/나누기\n• 찾기 및 바꾸기\n• 한글 폰트 지원",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            });

            var dialog = new ContentDialog
            {
                Title = "PDF Simple Editor 정보",
                Content = aboutPanel,
                CloseButtonText = "닫기",
                XamlRoot = Content.XamlRoot
            };
            aboutPanel.Children.Add(new TextBlock 
            { 
                Text = "\nLibraries: iText 7 Core & pdfSweep (AGPL v3, © iText Group NV)", 
                FontSize = 11, Opacity = 0.6 
            });
            await dialog.ShowAsync();
        }

        #endregion

        #region Helpers

        private async Task PerformSaveAsync(string filePath, bool isUserSave)
        {
            if (!_pdfManager.IsLoaded) return;

            // 저장 전 활성화된 인라인 편집이 있다면 강제로 적용
            if (_isInlineEditing)
            {
                var activeBox = OverlayCanvas.Children.OfType<TextBox>().FirstOrDefault();
                if (activeBox != null)
                {
                    await ApplyInlineTextAsync(activeBox);
                }
            }

            TxtStatus.Text = isUserSave ? "저장 중..." : "렌더링 준비 중...";
            LoadingRing.IsActive = true;

            try
            {
                if (isUserSave)
                {
                    // For user save, permanently burn ALL annotations into _pdfBytes
                    _pdfManager.ApplyBatchEdit(doc =>
                    {
                        foreach (var ann in _annotations)
                        {
                            ApplyAnnotationToDocumentInternal(doc, ann);
                            ann.IsApplied = true;
                        }
                    });

                    bool successFinal = await _pdfManager.SaveAsAsync(filePath, true);
                    if (!successFinal) throw new Exception("저장에 실패했습니다.");
                    
                    // Once permanently saved, clear the list as they are now part of the PDF background
                    _annotations.Clear();

                    // [추가] 렌더링 캐시 초기화 (저장된 상태로 새로고침 강제)
                    _renderTempPath = null;
                    if (_activeTab != null) _activeTab.FilePath = filePath;

                    // [추가] 저장된 파일을 다시 불러와서 상태 동기화
                    await _pdfManager.OpenAsync(filePath);
                    _currentPageIndex = 0; // 첫 페이지로 이동 (또는 현재 페이지 유지)
                }
                else
                {
                    // For temporary rendering, DO NOT modify the master _pdfBytes.
                    // Instead, get a temporary byte array with all annotations applied.
                    var tempBytes = _pdfManager.GetPdfBytesWithEdits(doc =>
                    {
                        foreach (var ann in _annotations)
                        {
                            ApplyAnnotationToDocumentInternal(doc, ann);
                        }
                    });

                    if (tempBytes != null)
                    {
                        await File.WriteAllBytesAsync(filePath, tempBytes);
                    }
                    else
                    {
                        // Fallback to basic save if edit failed
                        await _pdfManager.SaveAsAsync(filePath, false);
                    }
                }
                
                return;
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

        private (double width, double height) MeasureText(string text, string fontFamily, double fontSize, bool isBold, bool isItalic)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(fontFamily),
                FontSize = fontSize * PdfToPixels,
                FontWeight = isBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                TextWrapping = TextWrapping.NoWrap,
                Padding = new Thickness(0)
            };
            textBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            // 폰트 렌더링 시 오른쪽에 미세하게 잘리는 현상을 방지하기 위해 2px 여유 공간 추가
            return ((textBlock.DesiredSize.Width + 2.0) / PdfToPixels, textBlock.DesiredSize.Height / PdfToPixels);
        }

        private void ApplyAnnotationToDocument(PdfAnnotation ann)
        {
            if (!_pdfManager.IsLoaded) return;
            _pdfManager.ApplyBatchEdit(doc => ApplyAnnotationToDocumentInternal(doc, ann));
        }

        private void ApplyAnnotationToDocumentInternal(PdfDocument doc, PdfAnnotation ann)
        {
            // PdfDocumentManager의 메서드들이 이미 UI(Top-Left) -> PDF(Bottom-Left) 좌표 변환을 수행하므로,
            // 여기서는 UI 좌표(ann.X, ann.Y)를 그대로 전달해야 합니다. 중복 변환 시 텍스트가 사라질 수 있습니다.

            // Color parsing
            Color iTextColor = ColorConstants.BLACK;
            try 
            {
                if (!string.IsNullOrEmpty(ann.Color))
                {
                    var uColor = ParseColor(ann.Color);
                    iTextColor = new DeviceRgb(uColor.R, uColor.G, uColor.B);
                }
            } catch { }
            
            switch (ann.Type)
            {
                case AnnotationType.Text:
                case AnnotationType.FreeText:
                    if (ann.IsOriginalTextReplacement)
                        break;
                    var lineGroups = ann.TextFragments
                        .GroupBy(fragment => fragment.LineIndex)
                        .OrderBy(group => group.Key)
                        .ToList();
                    var lineFontObjectNumbers = lineGroups
                        .Select(group => group
                            .Select(fragment => fragment.OriginalFontObjectNumber)
                            .FirstOrDefault(objectNumber => objectNumber > 0, ann.OriginalFontObjectNumber))
                        .ToList();
                    var lineXOffsets = lineGroups
                        .Select(group => group.Min(fragment => fragment.X) - ann.X)
                        .ToList();
                    var lineBaselineOffsets = lineGroups
                        .Select(group =>
                        {
                            var firstFragment = group.OrderBy(fragment => fragment.X).First();
                            return (firstFragment.Y + firstFragment.BaselineOffset) - ann.Y;
                        })
                        .ToList();
                    _pdfManager.AddTextInternal(doc, ann.PageIndex, ann.X, ann.Y, ann.Content,
                        ann.FontFamily, ann.FontSize, iTextColor, ann.IsBold, ann.IsItalic,
                        ann.LineHeight, ann.BaselineOffset, ann.OriginalFontObjectNumber,
                        lineFontObjectNumbers, lineXOffsets, lineBaselineOffsets);
                    break;
                case AnnotationType.Highlight:
                    _pdfManager.AddHighlightInternal(doc, ann.PageIndex, ann.X, ann.Y,
                        ann.Width, ann.Height, iTextColor, (float)ann.Opacity);
                    break;
                case AnnotationType.Image:
                    if (ann.ImagePath != null)
                        _pdfManager.AddImageInternal(doc, ann.PageIndex, ann.ImagePath,
                            ann.X, ann.Y, ann.Width, ann.Height);
                    break;
            }
        }


        private static Windows.UI.Color ParseColor(string hexColor)
        {
            try
            {
                hexColor = hexColor.TrimStart('#');
                if (hexColor.Length == 8)
                {
                    byte a = Convert.ToByte(hexColor.Substring(0, 2), 16);
                    byte r = Convert.ToByte(hexColor.Substring(2, 2), 16);
                    byte g = Convert.ToByte(hexColor.Substring(4, 2), 16);
                    byte b = Convert.ToByte(hexColor.Substring(6, 2), 16);
                    return Windows.UI.Color.FromArgb(a, r, g, b);
                }
                else if (hexColor.Length == 6)
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
            // InitializeComponent 및 설정 복원 중에는 컨트롤 이벤트가 발생해도
            // 아직 저장하지 않습니다. 이 시점에 저장하면 기존 설정을 기본값으로
            // 덮어쓸 수 있습니다.
            if (_isInitializing || _isRestoringSettings)
                return;

            try
            {
                string path = GetSettingsFilePath();
                var lines = File.Exists(path)
                    ? File.ReadAllLines(path).ToList()
                    : new List<string>();

                // 이전 버전은 위치와 크기를 숫자 4줄로 저장했습니다. 기존 값이
                // 있으면 새 키 형식으로 옮긴 뒤, 이후에는 한 가지 형식만 유지합니다.
                var existingValues = ReadPersistedSettings(lines);
                bool hasKeyBounds = existingValues.ContainsKey("WindowX") &&
                                    existingValues.ContainsKey("WindowY") &&
                                    existingValues.ContainsKey("WindowWidth") &&
                                    existingValues.ContainsKey("WindowHeight");
                if (!hasKeyBounds && lines.Count >= 4 &&
                    int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyX) &&
                    int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyY) &&
                    int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyWidth) &&
                    int.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyHeight))
                {
                    SetPersistedSetting(lines, "WindowX", legacyX.ToString(CultureInfo.InvariantCulture));
                    SetPersistedSetting(lines, "WindowY", legacyY.ToString(CultureInfo.InvariantCulture));
                    SetPersistedSetting(lines, "WindowWidth", legacyWidth.ToString(CultureInfo.InvariantCulture));
                    SetPersistedSetting(lines, "WindowHeight", legacyHeight.ToString(CultureInfo.InvariantCulture));
                }

                if (lines.Any(line => line.IndexOf('=') <= 0))
                    lines = lines.Where(line => line.IndexOf('=') > 0).ToList();

                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                bool isMaximized = appWindow?.Presenter is OverlappedPresenter presenter &&
                                   presenter.State == OverlappedPresenterState.Maximized;

                // 최대화 중에는 현재 최대화된 크기를 저장하지 않습니다. 기존의
                // 마지막 일반 창 크기를 보존하여 다음 실행 때 창이 과도하게 커지지
                // 않도록 합니다.
                if (appWindow != null && !isMaximized)
                {
                    var pos = appWindow.Position;
                    var size = appWindow.Size;

                    SetPersistedSetting(lines, "WindowX", pos.X.ToString(CultureInfo.InvariantCulture));
                    SetPersistedSetting(lines, "WindowY", pos.Y.ToString(CultureInfo.InvariantCulture));
                    SetPersistedSetting(lines, "WindowWidth", size.Width.ToString(CultureInfo.InvariantCulture));
                    SetPersistedSetting(lines, "WindowHeight", size.Height.ToString(CultureInfo.InvariantCulture));
                }

                SetPersistedSetting(lines, "FontFamily", _fontSettings.FontFamily);
                SetPersistedSetting(lines, "FontSize", _fontSettings.FontSize.ToString("0.##", CultureInfo.InvariantCulture));
                SetPersistedSetting(lines, "FontColor", _fontSettings.Color);
                SetPersistedSetting(lines, "IsBold", _fontSettings.IsBold.ToString(CultureInfo.InvariantCulture));
                SetPersistedSetting(lines, "IsItalic", _fontSettings.IsItalic.ToString(CultureInfo.InvariantCulture));

                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveWindowPosition error: {ex.Message}");
            }
        }

        private void LoadWindowPosition()
        {
            _isRestoringSettings = true;
            try
            {
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    var values = ReadPersistedSettings(lines);
                    LoadPersistedFontSettings(values);

                    int x = 0, y = 0, width = 0, height = 0;
                    bool hasBounds =
                        values.TryGetValue("WindowX", out string? xText) &&
                        values.TryGetValue("WindowY", out string? yText) &&
                        values.TryGetValue("WindowWidth", out string? widthText) &&
                        values.TryGetValue("WindowHeight", out string? heightText) &&
                        int.TryParse(xText, NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                        int.TryParse(yText, NumberStyles.Integer, CultureInfo.InvariantCulture, out y) &&
                        int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
                        int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);

                    // 이전 버전의 4줄 형식도 읽을 수 있도록 유지합니다.
                    if (!hasBounds && lines.Length >= 4 &&
                        int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                        int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) &&
                        int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
                        int.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
                    {
                        hasBounds = true;
                    }

                    if (hasBounds)
                    {
                        var hwnd = WindowNative.GetWindowHandle(this);
                        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = AppWindow.GetFromWindowId(windowId);
                        if (appWindow != null)
                        {
                            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, (int)width, (int)height));
                        }
                    }

                    ApplyFontSettingsToControls();
                    if (hasBounds)
                        return;
                }

                var hwndDefault = WindowNative.GetWindowHandle(this);
                var windowIdDefault = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwndDefault);
                var appWindowDefault = AppWindow.GetFromWindowId(windowIdDefault);
                if (appWindowDefault != null)
                {
                    appWindowDefault.Resize(new Windows.Graphics.SizeInt32(1400, 900));
                }

                ApplyFontSettingsToControls();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadWindowPosition error: {ex.Message}");
            }
            finally
            {
                _isRestoringSettings = false;
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

        #region Alignment & Editing

        private async void AlignLeft_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAnnotations.Count < 2) return;
            
            if (!await HandleOriginalContentRemovalForSelectedAsync()) return;
            
            double minX = _selectedAnnotations.Min(a => a.X);
            foreach (var ann in _selectedAnnotations)
            {
                ann.X = minX;
                ann.IsApplied = false;
            }
            _pdfManager.MarkModified();
            RenderAnnotationOverlays();
        }

        private async void AlignRight_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAnnotations.Count < 2) return;
            
            if (!await HandleOriginalContentRemovalForSelectedAsync()) return;
            
            double maxX = _selectedAnnotations.Max(a => a.X + a.Width);
            foreach (var ann in _selectedAnnotations)
            {
                // Align the visual right edge accurately
                ann.X = maxX - ann.Width;
                ann.IsApplied = false;
            }
            _pdfManager.MarkModified();
            RenderAnnotationOverlays();
        }

        private async void AlignTop_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAnnotations.Count < 2) return;
            
            if (!await HandleOriginalContentRemovalForSelectedAsync()) return;
            
            double minY = _selectedAnnotations.Min(a => a.Y);
            foreach (var ann in _selectedAnnotations)
            {
                ann.Y = minY;
                ann.IsApplied = false;
            }
            _pdfManager.MarkModified();
            RenderAnnotationOverlays();
        }

        private async void AlignBottom_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAnnotations.Count < 2) return;
            
            if (!await HandleOriginalContentRemovalForSelectedAsync()) return;
            
            double maxY = _selectedAnnotations.Max(a => a.Y + a.Height);
            foreach (var ann in _selectedAnnotations)
            {
                // Align the visual bottom edge accurately
                ann.Y = maxY - ann.Height;
                ann.IsApplied = false;
            }
            _pdfManager.MarkModified();
            RenderAnnotationOverlays();
        }

        private async Task<bool> HandleOriginalContentRemovalForSelectedAsync()
        {
            var targets = _selectedAnnotations
                .Where(annotation => annotation.IsOriginalTextReplacement)
                .ToList();

            if (targets.Count == 0) return true;

            bool success = await _pdfManager.RemoveOriginalTextAnnotationsAsync(_currentPageIndex, targets);
            
            if (!success)
            {
                TxtStatus.Text = "배경을 보존하면서 이동할 수 없는 PDF 텍스트입니다.";
                return false;
            }

            foreach (var ann in targets)
                ann.IsOriginalTextReplacement = false;

            _renderTempPath = null;
            await RenderCurrentPageAsync();
            return true;
        }

        private async void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
        {
            await DeleteSelectedAnnotationsAsync();
        }

        #endregion
    }
}
