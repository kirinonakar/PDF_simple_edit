using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Controls;
using PDF_simple_edit.Controllers;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        private readonly RecentFilesService _recentFilesService = new();
        private readonly EditorSettingsService _settingsService = new();
        private readonly DocumentSearchController _documentSearchController = new(new PdfSearchService());
        private readonly AnnotationContentService _annotationContentService = new();
        private readonly PdfPageRenderService _pageRenderService = new();
        private readonly PdfOperationService _pdfOperationService = new();
        private readonly EditorDialogService _dialogService = new();
        private readonly AnnotationOverlayController _annotationOverlayController = new();
        private readonly AnnotationAlignmentService _annotationAlignmentService = new();
        private readonly ScreenColorPickerService _screenColorPickerService = new();
        private readonly AnnotationInteractionController _annotationInteractionController = new();
        private readonly InlineTextEditorController _inlineTextEditorController = new();
        private readonly PdfSaveService _pdfSaveService;
        private readonly AnnotationSelectionController _annotationSelectionController;
        private bool _isInitializing = true;
        private bool _isRestoringSettings;
        private bool _controlKeyIsDown;
        private System.Threading.CancellationTokenSource? _thumbnailCts;

        private readonly PrintHelper _printHelper = new();
        private readonly TextFontSettings _fontSettings = new();


        
        // Aliases to active tab for easier migration
        private PdfDocumentManager _pdfManager => _activeTab?.PdfManager ?? new PdfDocumentManager();
        private ObservableCollection<PageThumbnailData> _pageThumbnails => _activeTab?.PageThumbnails ?? new ObservableCollection<PageThumbnailData>();
        private List<PdfAnnotation> _annotations => _activeTab?.Annotations ?? new List<PdfAnnotation>();

        // PDF는 72 DPI, Windows 논리 픽셀은 96 DPI입니다.
        private const double PdfToPixels = 96.0 / 72.0;

        // MainWindow composes focused UserControls. These aliases keep the
        // document workflow independent from the controls' internal XAML names.
        private Button BtnOpen => EditorToolbar.OpenButton;
        private Button BtnSave => EditorToolbar.SaveButton;
        private Button BtnSaveAs => EditorToolbar.SaveAsButton;
        private Button BtnPrint => EditorToolbar.PrintButton;
        private Button BtnUndo => EditorToolbar.UndoButton;
        private Button BtnRedo => EditorToolbar.RedoButton;
        private ToggleButton BtnSelect => EditorToolbar.SelectButton;
        private ToggleButton BtnAddText => EditorToolbar.AddTextButton;
        private ToggleButton BtnHighlight => EditorToolbar.HighlightButton;
        private Border HighlightColorIndicator => EditorToolbar.HighlightIndicator;
        private Flyout HighlightFlyout => EditorToolbar.HighlightSettingsFlyout;
        private ColorPicker HighlightColorPicker => EditorToolbar.HighlightPicker;
        private Slider SldHighlightOpacity => EditorToolbar.HighlightOpacitySlider;
        private TextBlock TxtHighlightOpacity => EditorToolbar.HighlightOpacityText;
        private Button BtnHighlightSettings => EditorToolbar.HighlightSettingsButton;
        private ToggleButton BtnColorPicker => EditorToolbar.ColorPickerButton;
        private Button BtnAddImage => EditorToolbar.AddImageButton;
        private StackPanel FontToolbar => EditorToolbar.FontControls;
        private ComboBox CmbFontFamily => EditorToolbar.FontFamilyComboBox;
        private ComboBox CmbFontSize => EditorToolbar.FontSizeComboBox;
        private ToggleButton BtnBold => EditorToolbar.BoldButton;
        private ToggleButton BtnItalic => EditorToolbar.ItalicButton;
        private DropDownButton BtnFontColor => EditorToolbar.FontColorButton;
        private Border FontColorIndicator => EditorToolbar.FontColorIndicatorElement;
        private GridView ColorPalette => EditorToolbar.ColorPaletteGrid;
        private TextBlock TxtZoom => EditorToolbar.ZoomText;

        private ListView PageListView => PagePanel.ListView;
        private StackPanel WelcomePanel => PdfSurface.Welcome;
        private ScrollViewer PdfScrollViewer => PdfSurface.Viewer;
        private Grid PdfContentGrid => PdfSurface.ContentGrid;
        private Image PdfPageImage => PdfSurface.PageImage;
        private Canvas OverlayCanvas => PdfSurface.AnnotationCanvas;
        private ProgressRing LoadingRing => PdfSurface.LoadingIndicator;

        private TextBox TxtFindText => FindPanel.QueryTextBox;
        private TextBlock TxtFindCount => FindPanel.ResultCountText;
        private CheckBox ChkMatchCase => FindPanel.MatchCaseCheckBox;
        private Button BtnPrevPage => StatusBar.PreviousPageButton;
        private TextBlock TxtPageInfo => StatusBar.PageInfoText;
        private Button BtnNextPage => StatusBar.NextPageButton;
        private TextBox TxtGoToPage => StatusBar.GoToPageTextBox;
        private TextBlock TxtStatus => StatusBar.StatusText;
        private TextBlock TxtToolMode => StatusBar.ToolModeText;
        private TextBlock TxtFileInfo => StatusBar.FileInfoText;

        public MainWindow()
        {
            _annotationSelectionController = new AnnotationSelectionController(_annotationContentService);
            _pdfSaveService = new PdfSaveService(new PdfAnnotationDocumentService());
            try
            {
                // XAML 컨트롤이 생성될 때 SelectionChanged가 발생할 수 있으므로
                // 이벤트 처리보다 먼저 기본값을 준비합니다.
                _fontSettings.FontFamily = "맑은 고딕";
                _fontSettings.FontSize = 12;
                _fontSettings.Color = "#000000";

                InitializeComponent();
                WireChildControlEvents();
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

                // 창이 활성화되기 전에 저장된 위치와 크기를 복원해야 첫 프레임부터
                // 저장된 위치에 표시됩니다. Activated에서 복원하면 기본 위치에서
                // 한 번 그려진 뒤 이동하는 현상이 발생합니다.
                LoadWindowPosition();

                // Initialize color palette programmatically
                EditorColorService.PopulatePalette(ColorPalette);

                // Initialize Highlight UI
                HighlightColorPicker.Color = ParseColor(_fontSettings.HighlightColor);
                SldHighlightOpacity.Value = _fontSettings.HighlightOpacity;
                HighlightColorIndicator.Background = new SolidColorBrush(ParseColor(_fontSettings.HighlightColor));
                TxtHighlightOpacity.Text = $"{(int)(_fontSettings.HighlightOpacity * 100)}%";

                _recentFilesService.Load();
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

                    // Set window icon
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

        private void WireChildControlEvents()
        {
            BtnOpen.Click += OpenFile_Click;
            BtnSave.Click += SaveFile_Click;
            BtnSaveAs.Click += SaveAsFile_Click;
            BtnPrint.Click += Print_Click;
            BtnUndo.Click += Undo_Click;
            BtnRedo.Click += Redo_Click;
            BtnSelect.Click += SelectTool_Click;
            BtnAddText.Click += AddTextTool_Click;
            BtnHighlight.Click += HighlightTool_Click;
            BtnHighlightSettings.Click += HighlightSettings_Click;
            BtnColorPicker.Click += ColorPickerTool_Click;
            BtnAddImage.Click += AddImageTool_Click;
            HighlightColorPicker.ColorChanged += HighlightColorPicker_ColorChanged;
            SldHighlightOpacity.ValueChanged += HighlightOpacity_Changed;
            CmbFontFamily.SelectionChanged += FontFamily_Changed;
            CmbFontSize.SelectionChanged += FontSize_Changed;
            BtnBold.Click += FontBold_Click;
            BtnItalic.Click += FontItalic_Click;
            ColorPalette.SelectionChanged += FontColor_Changed;
            EditorToolbar.ZoomOutButton.Click += ZoomOut_Click;
            EditorToolbar.ZoomInButton.Click += ZoomIn_Click;
            EditorToolbar.FitToPageButton.Click += FitToPage_Click;

            PageListView.SelectionChanged += PageListView_SelectionChanged;
            PdfSurface.OpenButton.Click += OpenFile_Click;
            PdfSurface.NewButton.Click += NewDocument_Click;
            PdfScrollViewer.ViewChanged += PdfScrollViewer_ViewChanged;
            OverlayCanvas.PointerPressed += OverlayCanvas_PointerPressed;
            OverlayCanvas.PointerMoved += OverlayCanvas_PointerMoved;
            OverlayCanvas.PointerReleased += OverlayCanvas_PointerReleased;
            OverlayCanvas.DoubleTapped += OverlayCanvas_DoubleTapped;
            PdfSurface.AlignLeftItem.Click += AlignLeft_Click;
            PdfSurface.AlignRightItem.Click += AlignRight_Click;
            PdfSurface.AlignTopItem.Click += AlignTop_Click;
            PdfSurface.AlignBottomItem.Click += AlignBottom_Click;
            PdfSurface.DeleteItem.Click += DeleteAnnotation_Click;

            FindPanel.CloseButton.Click += CloseFindPanel_Click;
            FindPanel.QueryTextBox.TextChanged += FindText_Changed;
            FindPanel.QueryTextBox.KeyDown += TxtFindText_KeyDown;
            FindPanel.PreviousButton.Click += FindPrevious_Click;
            FindPanel.NextButton.Click += FindNext_Click;

            BtnPrevPage.Click += PrevPage_Click;
            BtnNextPage.Click += NextPage_Click;
            TxtGoToPage.KeyDown += GoToPage_KeyDown;
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

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _pageRenderService.DeleteTemporaryFile(_renderTempPath);

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
                string filePath = _pdfManager.FilePath ?? "새 문서";
                title = $"PDF Simple Editor - {filePath}{(_pdfManager.IsModified ? " ●" : "")}";
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
                RenderedPdfPage? page = await _pageRenderService.RenderPageAsync(
                    _pdfManager, _currentPageIndex, _renderScale, _renderTempPath);
                if (page == null) return;

                _renderTempPath = page.RenderPath;
                PdfPageImage.Source = page.Bitmap;
                PdfPageImage.HorizontalAlignment = HorizontalAlignment.Left;
                PdfPageImage.VerticalAlignment = VerticalAlignment.Top;
                OverlayCanvas.HorizontalAlignment = HorizontalAlignment.Left;
                OverlayCanvas.VerticalAlignment = VerticalAlignment.Top;
                PdfPageImage.Margin = new Thickness(0);
                OverlayCanvas.Margin = new Thickness(0);
                PdfPageImage.Width = page.LogicalWidth;
                PdfPageImage.Height = page.LogicalHeight;
                OverlayCanvas.Width = page.LogicalWidth;
                OverlayCanvas.Height = page.LogicalHeight;
                PdfPageImage.Stretch = Stretch.Fill;
                RenderAnnotationOverlays();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
            }
            finally
            {
                TxtStatus.Text = oldStatus == "페이지 렌더링 중..." ? "준비" : oldStatus;
                UpdateUIState();
                SyncPageListSelection();
            }
        }

        private async Task LoadThumbnailsAsync()
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts = new System.Threading.CancellationTokenSource();
            var token = _thumbnailCts.Token;

            try
            {
                _pageThumbnails.Clear();
                await foreach (PageThumbnailData thumbnail in _pageRenderService.RenderThumbnailsAsync(
                    _pdfManager, _renderTempPath, token))
                    _pageThumbnails.Add(thumbnail);
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

        private void PdfScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
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
            if (focused is TextBox || focused is NumberBox || focused is ComboBox || _inlineTextEditorController.IsEditing)
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

                var imageAnnotation = new PdfAnnotation
                {
                    Type = AnnotationType.Image,
                    PageIndex = _currentPageIndex,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    ImagePath = file.Path,
                    IsApplied = false
                };
                _annotations.Add(imageAnnotation);
                _pdfManager.MarkModified();

                // 이미지를 추가한 뒤 바로 핸들이 보이도록 선택 도구로 전환하고
                // 새 이미지를 선택 상태로 둡니다.
                SetToolMode(EditToolMode.Select);
                _selectedAnnotations.Clear();
                _selectedAnnotations.Add(imageAnnotation);
                _selectedAnnotation = imageAnnotation;
                TxtStatus.Text = "이미지가 추가되었습니다 (저장 시 반영)";
                RenderAnnotationOverlays();
            }
        }

        #endregion

        #region Canvas Interaction

private async void OverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
{
    if (!_pdfManager.IsLoaded || _inlineTextEditorController.IsEditing) return;

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
                _annotationInteractionController.BeginResize(dir, pos);
                OverlayCanvas.CapturePointer(e.Pointer);
                TxtStatus.Text = "크기 조정 중...";
                return;
            }

            var ctrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var selection = await _annotationSelectionController.SelectAtAsync(
                _pdfManager,
                _annotations,
                _selectedAnnotations,
                _selectedAnnotation,
                _currentPageIndex,
                pdfX,
                pdfY,
                ctrlPressed,
                () => TxtStatus.Text = "페이지 콘텐츠 분석 중...");

            _selectedAnnotation = selection.PrimarySelection;
            if (selection.AddedFromPageContent is PdfAnnotation added)
            {
                TxtStatus.Text = added.IsOriginalTextReplacement
                    ? "텍스트 편집 구역이 선택되었습니다. 두 번 클릭하여 편집하세요."
                    : "원본 콘텐츠가 선택되었습니다.";
            }

            if (selection.SelectionChanged)
            {
                RenderAnnotationOverlays();
            }

            if (_selectedAnnotation != null)
            {
                _annotationInteractionController.BeginMove(_selectedAnnotations, pos);
                OverlayCanvas.CapturePointer(e.Pointer);
                if (!_selectedAnnotation.IsOriginalTextReplacement &&
                    !(_selectedAnnotation.IsOriginalImageReplacement &&
                      _selectedAnnotation.OriginalImageName != null))
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
            _annotationInteractionController.BeginHighlight(
                OverlayCanvas,
                pos,
                _fontSettings.HighlightColor,
                _fontSettings.HighlightOpacity);
            OverlayCanvas.CapturePointer(e.Pointer);
            break;

        case EditToolMode.ColorPicker:
            TxtStatus.Text = "색상 추출 중...";
            var pickedColor = await _screenColorPickerService.PickAsync(
                PdfPageImage, e.GetCurrentPoint(PdfPageImage).Position);
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

        private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(OverlayCanvas).Position;
            if (_annotationInteractionController.UpdatePointer(
                OverlayCanvas,
                pos,
                _selectedAnnotation,
                _selectedAnnotations))
                RenderAnnotationOverlays();
        }

        private async void OverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_annotationInteractionController.CompleteResize())
            {
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                _pdfManager.MarkModified();
                TxtStatus.Text = "크기 조정됨 (저장 시 반영)";
                RenderAnnotationOverlays();
            }
            else if (_annotationInteractionController.IsMoving)
            {
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                AnnotationMoveResult result = await _annotationInteractionController.CompleteMoveAsync(
                    _pdfManager, _currentPageIndex, _selectedAnnotations);
                if (result == AnnotationMoveResult.NotMoved)
                {
                    RenderAnnotationOverlays();
                    return;
                }
                if (result == AnnotationMoveResult.OriginalTextRemovalFailed)
                {
                    TxtStatus.Text = "이 PDF의 텍스트는 배경을 보존한 상태로 이동할 수 없습니다.";
                    RenderAnnotationOverlays();
                    return;
                }

                _pdfManager.MarkModified();
                TxtStatus.Text = "위치 이동됨 (저장 시 반영)";
                if (!_inlineTextEditorController.IsEditing)
                {
                    await RenderCurrentPageAsync();
                    RenderAnnotationOverlays();
                }
            }
            else if (_annotationInteractionController.IsDrawingHighlight &&
                     _currentTool == EditToolMode.Highlight)
            {
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                PdfAnnotation? highlight = _annotationInteractionController.CompleteHighlight(
                    OverlayCanvas, _currentPageIndex, _fontSettings);
                if (highlight != null)
                {
                    _annotations.Add(highlight);
                    _pdfManager.MarkModified();
                    TxtStatus.Text = "텍스트 강조가 추가되었습니다 (저장 시 반영)";
                    RenderAnnotationOverlays();
                }
            }
        }

        private void OverlayCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_inlineTextEditorController.IsEditing) return;

            var pos = e.GetPosition(OverlayCanvas);
            double pdfX = pos.X / PdfToPixels;
            double pdfY = pos.Y / PdfToPixels;

            var ann = _annotationContentService.FindAnnotationAt(
                _annotations, _currentPageIndex, pdfX, pdfY);
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
            var activeBox = OverlayCanvas.Children.OfType<TextBox>().FirstOrDefault();
            var editingAnn = activeBox?.Tag switch
            {
                InlineTextEditSession session => session.Annotation,
                PdfAnnotation annotation => annotation,
                _ => null
            };
            _annotationOverlayController.Render(
                OverlayCanvas,
                _annotations,
                _currentPageIndex,
                _selectedAnnotations,
                _selectedAnnotation,
                editingAnn,
                ParseColor,
                (direction, position) =>
                {
                    _annotationInteractionController.BeginResize(direction, position);
                },
                shape => SetElementCursor(
                    OverlayCanvas,
                    Microsoft.UI.Input.InputSystemCursor.Create(shape)));
        }

        private void EditAnnotationContent(PdfAnnotation ann)
        {
            if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText)
            {
                if (_inlineTextEditorController.Reserve())
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddInlineTextBox(ann.X, ann.Y, ann);
                    });
                }
            }
        }

        #endregion

        #region Text Input (Inline & Dialog)

        private void AddInlineTextBox(double pdfX, double pdfY, PdfAnnotation? existingAnn = null)
        {
            _inlineTextEditorController.TryStart(
                OverlayCanvas,
                pdfX,
                pdfY,
                _fontSettings,
                existingAnn,
                _pdfManager,
                _annotations,
                _selectedAnnotations,
                _currentPageIndex,
                _annotationContentService.BuildEditableText,
                async () =>
                {
                    _renderTempPath = null;
                    await RenderCurrentPageAsync();
                },
                RenderAnnotationOverlays,
                status => TxtStatus.Text = status,
                removed =>
                {
                    if (_selectedAnnotation == removed)
                        _selectedAnnotation = null;
                },
                cancelled => DispatcherQueue.TryEnqueue(() =>
                {
                    if (cancelled)
                    {
                        DocTabView.Focus(FocusState.Programmatic);
                    }
                    else
                    {
                        BtnSelect.Focus(FocusState.Programmatic);
                        Content.Focus(FocusState.Programmatic);
                    }
                }));
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
            _documentSearchController.ClearHighlights(OverlayCanvas);
        }

        private void FindText_Changed(object sender, TextChangedEventArgs e)
        {
            TxtFindCount.Text = "";
            _documentSearchController.ClearHighlights(OverlayCanvas);
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
                await _documentSearchController.SearchAsync(
                    _pdfManager,
                    OverlayCanvas,
                    PdfScrollViewer,
                    searchText,
                    _currentPageIndex,
                    forward,
                    ChkMatchCase.IsChecked == true,
                    _zoomLevel,
                    async pageIndex =>
                    {
                        _currentPageIndex = pageIndex;
                        await RenderCurrentPageAsync();
                        SyncPageListSelection();
                    },
                    status => TxtStatus.Text = status,
                    ShowErrorDialogAsync);
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        #endregion

        #region Keyboard Shortcuts Extension
        private async void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (KeyboardStateService.IsControlKey(e.Key))
            {
                _controlKeyIsDown = true;
                return;
            }

            // TextBox의 KeyDown에서 Ctrl 상태를 놓치는 경우에도 창 레벨에서
            // Enter를 먼저 소비하여 줄바꿈 대신 편집을 확정합니다.
            if (_inlineTextEditorController.IsEditing &&
                e.Key == Windows.System.VirtualKey.Enter &&
                (_controlKeyIsDown || KeyboardStateService.IsControlDown()))
            {
                var activeTextBox = OverlayCanvas.Children.OfType<TextBox>().FirstOrDefault();
                if (activeTextBox == null ||
                    activeTextBox.Tag is not InlineTextEditSession { IsFinishing: true })
                {
                    e.Handled = true;
                    if (activeTextBox != null)
                        await _inlineTextEditorController.FinishActiveEditAsync();
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Delete && _selectedAnnotation != null && !_inlineTextEditorController.IsEditing)
            {
                await DeleteSelectedAnnotationsAsync();
                e.Handled = true;
            }
        }

        private void MainWindow_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (KeyboardStateService.IsControlKey(e.Key))
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
                    var size = AnnotationTextLayoutService.MeasureBounds(_selectedAnnotation.Content, font, _selectedAnnotation.FontSize, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
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
                    var size = AnnotationTextLayoutService.MeasureBounds(_selectedAnnotation.Content, _selectedAnnotation.FontFamily, sizeVal, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
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
                var size = AnnotationTextLayoutService.MeasureBounds(_selectedAnnotation.Content, _selectedAnnotation.FontFamily, _selectedAnnotation.FontSize, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
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
                var size = AnnotationTextLayoutService.MeasureBounds(_selectedAnnotation.Content, _selectedAnnotation.FontFamily, _selectedAnnotation.FontSize, _selectedAnnotation.IsBold, _selectedAnnotation.IsItalic);
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
            var hwnd = WindowNative.GetWindowHandle(this);
            PdfMergeRequest? request = await _pdfOperationService.CreateMergeRequestAsync(
                Content.XamlRoot, hwnd, _pdfManager.IsLoaded);
            if (request == null)
                return;

            PdfDocumentTab? newTab = null;
            PdfDocumentManager manager = _pdfManager;
            if (!_pdfManager.IsLoaded)
            {
                newTab = new PdfDocumentTab();
                manager = newTab.PdfManager;
            }

            TxtStatus.Text = "PDF 합치기 중...";
            LoadingRing.IsActive = true;
            try
            {
                string? outputPath = await _pdfOperationService.ExecuteMergeAsync(manager, request);
                if (outputPath == null)
                {
                    await ShowErrorDialogAsync("오류", "PDF 합치기에 실패했습니다.");
                    return;
                }

                if (newTab != null)
                {
                    newTab.FilePath = outputPath;
                    newTab.Header = Path.GetFileName(outputPath);
                    _tabs.Add(newTab);
                    DocTabView.SelectedItem = newTab;
                }
                else if (_activeTab != null)
                    _activeTab.FilePath = outputPath;

                _currentPageIndex = 0;
                _renderTempPath = null;
                TxtStatus.Text = "PDF 합치기 완료";
                UpdateUIState();
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

        private async void SplitPdf_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded) return;

            PdfSplitRequest? request = await _pdfOperationService.CreateSplitRequestAsync(
                Content.XamlRoot,
                WindowNative.GetWindowHandle(this),
                _pdfManager.PageCount);
            if (request == null)
                return;
            if (request.Ranges.Count == 0)
            {
                TxtStatus.Text = "나눌 페이지 범위가 올바르지 않습니다.";
                return;
            }

            TxtStatus.Text = "PDF 나누기 중...";
            LoadingRing.IsActive = true;
            try
            {
                int resultCount = await _pdfManager.SplitFileAsync(
                    request.OutputFolder, request.Ranges.ToList());
                TxtStatus.Text = $"PDF 나누기 완료: {resultCount}개 파일 생성됨";
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
            EditorPreferences preferences = await _dialogService.ShowSettingsAsync(
                Content.XamlRoot, _renderScale, _fontSettings);
            _renderScale = preferences.RenderScale;
            _fontSettings.FontFamily = preferences.FontFamily;
            _fontSettings.FontSize = preferences.FontSize;
            ApplyFontSettingsToControls();
            SaveWindowPosition();
            if (_pdfManager.IsLoaded)
                await RenderCurrentPageAsync();
        }

        private async void About_Click(object sender, RoutedEventArgs e) =>
            await _dialogService.ShowAboutAsync(Content.XamlRoot);

        #endregion

        #region Helpers

        private async Task PerformSaveAsync(string filePath, bool isUserSave)
        {
            if (!_pdfManager.IsLoaded) return;

            // 저장 전 활성화된 인라인 편집이 있다면 강제로 적용
            if (_inlineTextEditorController.IsEditing)
                await _inlineTextEditorController.FinishActiveEditAsync();

            TxtStatus.Text = isUserSave ? "저장 중..." : "렌더링 준비 중...";
            LoadingRing.IsActive = true;

            try
            {
                await _pdfSaveService.SaveAsync(
                    _pdfManager,
                    _annotations,
                    filePath,
                    isUserSave);

                if (isUserSave)
                {
                    _renderTempPath = null;
                    if (_activeTab != null)
                        _activeTab.FilePath = filePath;
                    _currentPageIndex = 0;
                }
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

        private static Windows.UI.Color ParseColor(string hexColor) =>
            EditorColorService.Parse(hexColor);

        private async Task ShowErrorDialogAsync(string title, string message)
        {
            await _dialogService.ShowErrorAsync(Content.XamlRoot, title, message);
        }

        private void SaveWindowPosition()
        {
            if (_isInitializing || _isRestoringSettings)
                return;

            AppWindow? appWindow = GetAppWindow();
            bool isMaximized = appWindow?.Presenter is OverlappedPresenter presenter &&
                               presenter.State == OverlappedPresenterState.Maximized;
            WindowPlacement? placement = appWindow != null && !isMaximized
                ? new WindowPlacement(
                    appWindow.Position.X,
                    appWindow.Position.Y,
                    appWindow.Size.Width,
                    appWindow.Size.Height)
                : null;
            _settingsService.Save(placement, _fontSettings);
        }

        private void LoadWindowPosition()
        {
            _isRestoringSettings = true;
            try
            {
                WindowPlacement? placement = _settingsService.Load(_fontSettings);
                AppWindow? appWindow = GetAppWindow();
                if (appWindow != null && placement != null)
                    appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                        placement.X, placement.Y, placement.Width, placement.Height));
                else
                    appWindow?.Resize(new Windows.Graphics.SizeInt32(1400, 900));
                ApplyFontSettingsToControls();
            }
            finally
            {
                _isRestoringSettings = false;
            }
        }

        private AppWindow? GetAppWindow()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        }

        private void AddToRecentFiles(string filePath)
        {
            _recentFilesService.Add(filePath);
            UpdateRecentFilesMenu();
        }

        private void UpdateRecentFilesMenu()
        {
            if (MenuRecentFiles == null) return;

            MenuRecentFiles.Items.Clear();

            if (_recentFilesService.Files.Count == 0)
            {
                MenuRecentFiles.Items.Add(new MenuFlyoutItem { Text = "최근 파일 없음", IsEnabled = false });
                return;
            }

            foreach (var filePath in _recentFilesService.Files)
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
                _recentFilesService.Clear();
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
                        _recentFilesService.Remove(filePath);
                        UpdateRecentFilesMenu();
                    }
                }
                else
                {
                    await ShowErrorDialogAsync("오류", "파일을 찾을 수 없습니다.");
                    _recentFilesService.Remove(filePath);
                    UpdateRecentFilesMenu();
                }
            }
        }

        #endregion

        #region Alignment & Editing

        private async void AlignLeft_Click(object sender, RoutedEventArgs e) =>
            await AlignSelectedAnnotationsAsync(AnnotationAlignment.Left);

        private async void AlignRight_Click(object sender, RoutedEventArgs e) =>
            await AlignSelectedAnnotationsAsync(AnnotationAlignment.Right);

        private async void AlignTop_Click(object sender, RoutedEventArgs e) =>
            await AlignSelectedAnnotationsAsync(AnnotationAlignment.Top);

        private async void AlignBottom_Click(object sender, RoutedEventArgs e) =>
            await AlignSelectedAnnotationsAsync(AnnotationAlignment.Bottom);

        private async Task AlignSelectedAnnotationsAsync(AnnotationAlignment alignment)
        {
            if (_selectedAnnotations.Count < 2 ||
                !await HandleOriginalContentRemovalForSelectedAsync())
                return;

            _annotationAlignmentService.Align(_selectedAnnotations, alignment);
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
