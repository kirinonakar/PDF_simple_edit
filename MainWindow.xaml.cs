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
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;

namespace PDF_simple_edit
{
    public sealed partial class MainWindow : Window
    {
        private const string AnnotationClipboardFormat = "PDFSimpleEditor.Annotations.v1";

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
        private AnnotationCanvasController _annotationCanvasController = null!;
        private AnnotationEditController _annotationEditController = null!;
        private PageViewController _pageViewController = null!;
        private WindowSettingsController _windowSettingsController = null!;
        private RecentFilesController _recentFilesController = null!;

        private readonly DocumentSearchController _documentSearchController = new(new PdfSearchService());
        private readonly PdfPageRenderService _pageRenderService = new();
        private readonly PdfOperationService _pdfOperationService = new();
        private readonly PdfImageExtractionService _pdfImageExtractionService = new();
        private readonly EditorDialogService _dialogService = new();
        private readonly PdfSaveService _pdfSaveService;
        private bool _isSaveInProgress;
        private bool _controlKeyIsDown;
        private readonly PrintHelper _printHelper = new();
        private readonly TextFontSettings _fontSettings = new();
        private string _signatureColor = "#000000";
        private double _signatureLineWidth = 2.0;
        private bool _isSyncingFontControls;


        
        // Aliases to active tab for easier migration
        private PdfDocumentManager _pdfManager => _activeTab?.PdfManager ?? new PdfDocumentManager();
        private ObservableCollection<PageThumbnailData> _pageThumbnails => _activeTab?.PageThumbnails ?? new ObservableCollection<PageThumbnailData>();
        private List<PdfAnnotation> _annotations => _activeTab?.Annotations ?? new List<PdfAnnotation>();

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
        private ToggleButton BtnSignature => EditorToolbar.SignatureButton;
        private Border SignatureColorIndicator => EditorToolbar.SignatureColorIndicator;
        private Flyout SignatureSettingsFlyout => EditorToolbar.SignatureSettingsFlyout;
        private ColorPicker SignatureColorPicker => EditorToolbar.SignatureColorPicker;
        private Slider SldSignatureWidth => EditorToolbar.SignatureWidthSlider;
        private TextBlock TxtSignatureWidth => EditorToolbar.SignatureWidthText;
        private Button BtnSignatureSettings => EditorToolbar.SignatureSettingsButton;
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
            _pdfSaveService = new PdfSaveService(new PdfAnnotationDocumentService());
            try
            {
                // XAML 컨트롤이 생성될 때 SelectionChanged가 발생할 수 있으므로
                // 이벤트 처리보다 먼저 기본값을 준비합니다.
                _fontSettings.FontFamily = "맑은 고딕";
                _fontSettings.FontSize = 12;
                _fontSettings.Color = "#000000";

                InitializeComponent();
                _annotationCanvasController = new AnnotationCanvasController(
                    OverlayCanvas,
                    PdfPageImage,
                    TxtStatus,
                    DispatcherQueue,
                    () => _pdfManager,
                    () => _annotations,
                    () => _currentPageIndex,
                    () => _currentTool,
                    SetToolMode,
                    _fontSettings,
                    () => _signatureColor,
                    () => _signatureLineWidth,
                    color =>
                    {
                        _fontSettings.HighlightColor = color.ToString();
                        HighlightColorPicker.Color = color;
                        HighlightColorIndicator.Background = new SolidColorBrush(color);
                    },
                    () => _renderTempPath = null,
                    RenderCurrentPageAsync,
                    cancelled =>
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
                    });
                _annotationCanvasController.PrimarySelectionChanged +=
                    SyncFontControlsWithSelection;
                _pageViewController = new PageViewController(
                    _pageRenderService,
                    PageListView,
                    PdfPageImage,
                    OverlayCanvas,
                    PdfScrollViewer,
                    TxtStatus,
                    TxtZoom,
                    TxtGoToPage,
                    () => _pdfManager,
                    () => _pageThumbnails,
                    () => _annotations,
                    () => _currentPageIndex,
                    value => _currentPageIndex = value,
                    () => _zoomLevel,
                    value => _zoomLevel = value,
                    () => _renderTempPath,
                    value => _renderTempPath = value,
                    () => _renderScale,
                    RenderAnnotationOverlays,
                    UpdateUIState);
                _annotationEditController = new AnnotationEditController(
                    _annotationCanvasController,
                    new AnnotationAlignmentService(),
                    _fontSettings,
                    TxtStatus,
                    () => _pdfManager,
                    () => _annotations,
                    () => _currentPageIndex,
                    () => _renderTempPath = null,
                    RenderCurrentPageAsync);
                _windowSettingsController = new WindowSettingsController(
                    this,
                    new EditorSettingsService(),
                    _fontSettings,
                    ApplyFontSettingsToControls);
                _recentFilesController = new RecentFilesController(
                    new RecentFilesService(),
                    MenuRecentFiles,
                    OpenPdfFileAsync,
                    ShowErrorDialogAsync);
                AddInstalledNotoFonts();
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
                SignatureColorPicker.Color = ParseColor(_signatureColor);
                SldSignatureWidth.Value = _signatureLineWidth;
                SignatureColorIndicator.Background = new SolidColorBrush(ParseColor(_signatureColor));
                TxtSignatureWidth.Text = $"{_signatureLineWidth:0.#} pt";

                _recentFilesController.Load();
                InitializeZoomAccelerators();

                Activated += MainWindow_Activated;
                Closed += MainWindow_Closed;
                _windowSettingsController.CompleteInitialization();
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
            EditorToolbar.TextEditingModeComboBox.SelectionChanged += TextEditingMode_Changed;
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
            BtnSignature.Click += SignatureTool_Click;
            BtnSignatureSettings.Click += SignatureSettings_Click;
            BtnColorPicker.Click += ColorPickerTool_Click;
            BtnAddImage.Click += AddImageTool_Click;
            HighlightColorPicker.ColorChanged += HighlightColorPicker_ColorChanged;
            SldHighlightOpacity.ValueChanged += HighlightOpacity_Changed;
            SignatureColorPicker.ColorChanged += SignatureColorPicker_ColorChanged;
            SldSignatureWidth.ValueChanged += SignatureWidth_Changed;
            CmbFontFamily.SelectionChanged += FontFamily_Changed;
            CmbFontSize.SelectionChanged += FontSize_Changed;
            BtnBold.Click += FontBold_Click;
            BtnItalic.Click += FontItalic_Click;
            ColorPalette.SelectionChanged += FontColor_Changed;
            EditorToolbar.ZoomOutButton.Click += ZoomOut_Click;
            EditorToolbar.ZoomInButton.Click += ZoomIn_Click;
            EditorToolbar.FitToPageButton.Click += FitToPage_Click;

            PageListView.SelectionChanged += PageListView_SelectionChanged;
            PagePanel.ExtractMenuItem.Click += ExtractSelectedPages_Click;
            PagePanel.DeleteMenuItem.Click += DeletePage_Click;
            PageListView.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(PageListView_PointerPressed),
                true);
            PageListView.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(PageListView_PointerReleased),
                true);
            PageListView.AddHandler(
                UIElement.DragOverEvent,
                new DragEventHandler(PageListView_DragOver),
                true);
            PageListView.AddHandler(
                UIElement.DragLeaveEvent,
                new DragEventHandler(PageListView_DragLeave),
                true);
            PageListView.DragItemsStarting += PageListView_DragItemsStarting;
            PageListView.DragItemsCompleted += PageListView_DragItemsCompleted;
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
            PdfSurface.AnnotationContextMenu.Opening += AnnotationContextMenu_Opening;
            PdfSurface.CopyItem.Click += CopySelectedObjects_Click;
            PdfSurface.CutItem.Click += CutSelectedObjects_Click;
            PdfSurface.PasteItem.Click += Paste_Click;
            PdfSurface.SaveImageItem.Click += SaveSelectedImage_Click;
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

        private void AddInstalledNotoFonts()
        {
            var existingFamilies = CmbFontFamily.Items
                .OfType<ComboBoxItem>()
                .Select(item => item.Content?.ToString())
                .Where(family => !string.IsNullOrWhiteSpace(family))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string family in InstalledFontService.GetInstalledNotoFamilies())
            {
                if (existingFamilies.Add(family))
                    CmbFontFamily.Items.Add(new ComboBoxItem { Content = family });
            }
        }

        private void ApplyFontSettingsToControls()
        {
            if (!_changingTextMode)
                EditorToolbar.TextEditingModeComboBox.SelectedIndex = (int)_fontSettings.TextEditingMode;
            _pdfManager.TextEditingMode = _fontSettings.TextEditingMode;
            ApplyFontValuesToControls(
                _fontSettings.FontFamily,
                _fontSettings.FontSize,
                _fontSettings.IsBold,
                _fontSettings.IsItalic,
                _fontSettings.Color);
        }

        private bool _changingTextMode;
        private async void TextEditingMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_changingTextMode) return;
            var mode = (TextEditingMode)EditorToolbar.TextEditingModeComboBox.SelectedIndex;
            if (!Enum.IsDefined(mode)) return;
            EditorToolbar.TextEditingModeDescription.Text = mode == TextEditingMode.PreserveOriginal
                ? "원본 글꼴로 문단 편집 · 드래그 범위 선택 · Alt+드래그 이동"
                : "글꼴·크기 변경 가능 · 원본 텍스트를 교체하여 편집";
            if (_fontSettings.TextEditingMode == mode) return;
            _changingTextMode = true;
            EditorToolbar.TextEditingModeComboBox.IsEnabled = false;
            try
            {
                if (_annotationCanvasController != null)
                    await _annotationCanvasController.FinishActiveInlineEditAsync();
                _fontSettings.TextEditingMode = mode;
                _pdfManager.TextEditingMode = mode;
                // Selection handles describe one extraction mode. Pending rewritten
                // annotations are edits and must survive a mode change.
                _annotations.RemoveAll(a => a.IsOriginalTextReplacement);
                _annotationCanvasController?.ClearSelection();
                _annotationCanvasController?.Render();
                _windowSettingsController?.Save();
                TxtStatus.Text = mode == TextEditingMode.PreserveOriginal
                    ? "원본 보존 방식으로 전환했습니다. 텍스트를 다시 선택해 주세요."
                    : "기존 방식으로 전환했습니다. 텍스트를 다시 선택해 주세요.";
            }
            catch (Exception error) { TxtStatus.Text = error.Message; }
            finally
            {
                _changingTextMode = false;
                EditorToolbar.TextEditingModeComboBox.SelectedIndex = (int)_fontSettings.TextEditingMode;
                EditorToolbar.TextEditingModeComboBox.IsEnabled = true;
            }
        }

        private void SyncFontControlsWithSelection(PdfAnnotation? annotation)
        {
            if (annotation?.Type is AnnotationType.Text or AnnotationType.FreeText)
            {
                ApplyFontValuesToControls(
                    annotation.FontFamily,
                    annotation.FontSize,
                    annotation.IsBold,
                    annotation.IsItalic,
                    annotation.Color);
                return;
            }

            ApplyFontSettingsToControls();
        }

        private void ApplyFontValuesToControls(
            string fontFamily,
            double fontSize,
            bool isBold,
            bool isItalic,
            string color)
        {
            _isSyncingFontControls = true;
            try
            {
                if (CmbFontFamily != null && !string.IsNullOrWhiteSpace(fontFamily))
                {
                    ComboBoxItem? fontItem = CmbFontFamily.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(
                            item.Content?.ToString(),
                            fontFamily,
                            StringComparison.OrdinalIgnoreCase));
                    if (fontItem == null)
                    {
                        fontItem = new ComboBoxItem { Content = fontFamily };
                        CmbFontFamily.Items.Add(fontItem);
                    }

                    if (!ReferenceEquals(CmbFontFamily.SelectedItem, fontItem))
                        CmbFontFamily.SelectedItem = fontItem;
                }

                if (CmbFontSize != null && double.IsFinite(fontSize) && fontSize > 0)
                {
                    string sizeText = fontSize.ToString("0.##", CultureInfo.InvariantCulture);
                    ComboBoxItem? sizeItem = CmbFontSize.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => double.TryParse(
                                item.Content?.ToString(),
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out double itemSize) &&
                            Math.Abs(itemSize - fontSize) < 0.005);
                    if (sizeItem == null)
                    {
                        sizeItem = new ComboBoxItem { Content = sizeText };
                        CmbFontSize.Items.Add(sizeItem);
                    }

                    if (!ReferenceEquals(CmbFontSize.SelectedItem, sizeItem))
                        CmbFontSize.SelectedItem = sizeItem;
                }

                if (BtnBold != null)
                    BtnBold.IsChecked = isBold;
                if (BtnItalic != null)
                    BtnItalic.IsChecked = isItalic;
                if (FontColorIndicator != null)
                    FontColorIndicator.Background = new SolidColorBrush(ParseColor(color));
            }
            finally
            {
                _isSyncingFontControls = false;
            }
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
                // MovePage에서 이미 현재 썸네일 컬렉션을 같은 순서로 이동했으므로,
                // 직후의 재로드가 컬렉션을 원래 순서로 덮어쓰지 않게 합니다.
                bool preserveCurrentThumbnails =
                    _pageViewController.ConsumePreserveThumbnailsAfterReorder();

                if (_pdfManager.IsLoaded && !preserveCurrentThumbnails)
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
            MenuSignature.IsEnabled = hasDoc;
            MenuAddImage.IsEnabled = hasDoc;
            MenuExtractImages.IsEnabled = hasDoc;
            MenuSplitPdf.IsEnabled = hasDoc;
            MenuDeletePage.IsEnabled = hasDoc;

            int selectedPageCount = GetSelectedPageIndices().Count;
            PagePanel.ExtractMenuItem.IsEnabled = hasDoc && selectedPageCount > 0;
            PagePanel.DeleteMenuItem.IsEnabled = hasDoc &&
                selectedPageCount > 0 && selectedPageCount < _pdfManager.PageCount;

            BtnSave.IsEnabled = hasDoc;
            BtnSaveAs.IsEnabled = hasDoc;
            BtnPrint.IsEnabled = hasDoc;
            BtnUndo.IsEnabled = hasDoc && _pdfManager.CanUndo;
            BtnRedo.IsEnabled = hasDoc && _pdfManager.CanRedo;
            BtnSelect.IsEnabled = hasDoc;
            BtnAddText.IsEnabled = hasDoc;
            BtnHighlight.IsEnabled = hasDoc;
            BtnHighlightSettings.IsEnabled = hasDoc;
            BtnSignature.IsEnabled = hasDoc;
            BtnSignatureSettings.IsEnabled = hasDoc;
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
                    newTab.Annotations.AddRange(newTab.PdfManager.LoadSavedSignatures());
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

        private bool IsPageListDrag(DragEventArgs e) =>
            _pageViewController.IsPageListDrag(e);

        private void PageListView_PointerPressed(object sender, PointerRoutedEventArgs e) =>
            _pageViewController.PointerPressed(e);

        private void PageListView_PointerReleased(object sender, PointerRoutedEventArgs e) =>
            _pageViewController.PointerReleased();

        private void PageListView_DragOver(object sender, DragEventArgs e) =>
            _pageViewController.DragOver(e);

        private void PageListView_DragLeave(object sender, DragEventArgs e)
        {
            // DragLeave가 Drop 직전에 발생할 수 있으므로 여기서 드롭 위치를
            // 초기화하지 않습니다. DragItemsCompleted에서 상태를 정리합니다.
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            // 페이지 썸네일을 재정렬하는 내부 드래그는 파일 열기 드롭과
            // 구분해야 합니다. 내부 드래그를 Copy로 처리하면 ListView의
            // 기본 재정렬 동작이 취소될 수 있습니다.
            if (IsPageListDrag(e) ||
                !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.Caption = "페이지 순서 변경";
                e.DragUIOverride.IsCaptionVisible = true;
                e.Handled = true;
                return;
            }

            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "PDF 열기";
            e.DragUIOverride.IsCaptionVisible = true;
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            // 내부 페이지 드래그는 여기서 파일 열기로 처리하지 않습니다.
            if (IsPageListDrag(e))
            {
                e.Handled = true;
                return;
            }

            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0 && items[0] is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                await OpenPdfFileAsync(file);
            }
        }

        private async void SaveFile_Click(object sender, RoutedEventArgs? e)
        {
            if (!_pdfManager.IsLoaded) return;

            if (_pdfManager.FilePath != null)
            {
                string filePath = _pdfManager.FilePath;
                if (await PerformSaveAsync(filePath, true))
                    AddToRecentFiles(filePath);
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
                if (await PerformSaveAsync(file.Path, true))
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
                if (!await PerformSaveAsync(tempPath, false))
                    return;

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

        private Task RenderCurrentPageAsync() =>
            _pageViewController.RenderCurrentPageAsync();

        private Task LoadThumbnailsAsync() =>
            _pageViewController.LoadThumbnailsAsync();

        #endregion

        #region Page Navigation

        private async void PrevPage_Click(object sender, RoutedEventArgs e) =>
            await _pageViewController.MovePreviousAsync();

        private async void NextPage_Click(object sender, RoutedEventArgs e) =>
            await _pageViewController.MoveNextAsync();

        private async void GoToPage_KeyDown(object sender, KeyRoutedEventArgs e) =>
            await _pageViewController.HandleGoToPageKeyAsync(e);

        private async void PageListView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            await _pageViewController.HandleSelectionChangedAsync();

        private void PageListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
            _pageViewController.DragItemsStarting(e);

        private void PageListView_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e) =>
            _pageViewController.DragItemsCompleted();

        private void SyncPageListSelection() =>
            _pageViewController.SyncSelection();

        private List<int> GetSelectedPageIndices() =>
            _pageViewController.GetSelectedPageIndices();

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

        private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
            _pageViewController.ZoomIn();

        private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
            _pageViewController.ZoomOut();

        private void FitToPage_Click(object sender, RoutedEventArgs e) =>
            FitToPage();

        private void FitToPage() => _pageViewController.FitToPage();

        private void PdfScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
            _pageViewController.ViewChanged();

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
            BtnSignature.IsChecked = mode == EditToolMode.Signature;
            BtnColorPicker.IsChecked = mode == EditToolMode.ColorPicker;
            
            if (MenuSelect != null) MenuSelect.IsChecked = mode == EditToolMode.Select;
            if (MenuAddText != null) MenuAddText.IsChecked = mode == EditToolMode.AddText;
            if (MenuHighlight != null) MenuHighlight.IsChecked = mode == EditToolMode.Highlight;
            if (MenuSignature != null) MenuSignature.IsChecked = mode == EditToolMode.Signature;

            TxtToolMode.Text = mode switch
            {
                EditToolMode.AddText => "도구: 텍스트 추가",
                EditToolMode.Highlight => "도구: 텍스트 강조",
                EditToolMode.Signature => "도구: 서명 그리기",
                EditToolMode.AddImage => "도구: 이미지 추가",
                EditToolMode.Select => "도구: 선택",
                EditToolMode.ColorPicker => "도구: 색상 추출",
                _ => ""
            };

            UpdateCursor(mode);
            _annotationCanvasController.ClearSelection();
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

        private void SignatureTool_Click(object sender, RoutedEventArgs e)
        {
            SetToolMode(_currentTool == EditToolMode.Signature ? EditToolMode.None : EditToolMode.Signature);
        }

        private void SignatureSettings_Click(object sender, RoutedEventArgs e)
        {
            FlyoutBase.ShowAttachedFlyout(BtnSignature);
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

        private void SignatureColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            _signatureColor = args.NewColor.ToString();
            SignatureColorIndicator.Background = new SolidColorBrush(args.NewColor);
        }

        private void SignatureWidth_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _signatureLineWidth = e.NewValue;
            if (TxtSignatureWidth != null)
                TxtSignatureWidth.Text = $"{e.NewValue:0.#} pt";
        }


        private void ToolAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            // Focus check to prevent tool activation while typing
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox || focused is NumberBox || focused is ComboBox || _annotationCanvasController.IsInlineEditing)
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
                var imageProperties = await file.Properties.GetImagePropertiesAsync();
                (double w, double h) = CalculateInitialImageSize(
                    imageProperties.Width,
                    imageProperties.Height,
                    pageSize.width,
                    pageSize.height);
                double x = (pageSize.width - w) / 2;
                double y = (pageSize.height - h) / 2;

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
                _annotationCanvasController.SelectOnly(imageAnnotation);
                TxtStatus.Text = "이미지가 추가되었습니다 (저장 시 반영)";
                RenderAnnotationOverlays();
            }
        }

        private async void ExtractAllImages_Click(object sender, RoutedEventArgs e)
        {
            byte[]? pdfBytes = _pdfManager.GetPdfBytes();
            if (pdfBytes == null)
                return;

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null)
                return;

            string documentName = !string.IsNullOrWhiteSpace(_pdfManager.FilePath)
                ? Path.GetFileNameWithoutExtension(_pdfManager.FilePath)
                : "document";

            TxtStatus.Text = "이미지 추출 중...";
            LoadingRing.IsActive = true;
            try
            {
                PdfImageExtractionResult result = await _pdfImageExtractionService.ExtractAllAsync(
                    pdfBytes,
                    folder.Path,
                    documentName);

                TxtStatus.Text = result switch
                {
                    { SavedCount: 0, FailedCount: 0 } => "추출할 이미지가 없습니다.",
                    { FailedCount: 0 } => $"이미지 추출 완료: {result.SavedCount}개",
                    _ => $"이미지 {result.SavedCount}개 추출, {result.FailedCount}개 실패"
                };
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(
                    "이미지 추출 오류",
                    $"이미지 추출 중 오류가 발생했습니다: {ex.Message}");
                TxtStatus.Text = "이미지 추출에 실패했습니다.";
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private void AnnotationContextMenu_Opening(object? sender, object e)
        {
            int selectedCount = _annotationCanvasController.SelectedAnnotations.Count;
            int copyableCount = GetCopyableSelection().Count;
            bool hasCopyableSelection = copyableCount > 0;
            PdfSurface.CopyItem.Visibility = hasCopyableSelection
                ? Visibility.Visible
                : Visibility.Collapsed;
            PdfSurface.CutItem.Visibility = hasCopyableSelection && copyableCount == selectedCount
                ? Visibility.Visible
                : Visibility.Collapsed;
            PdfSurface.PasteItem.IsEnabled = CanPasteClipboardContent();

            PdfAnnotation? selectedImage = _annotationCanvasController.SelectedAnnotations.Count == 1
                ? _annotationCanvasController.SelectedAnnotations[0]
                : null;
            PdfSurface.SaveImageItem.IsEnabled = selectedImage?.Type == AnnotationType.Image &&
                !string.IsNullOrWhiteSpace(selectedImage.ImagePath) &&
                File.Exists(selectedImage.ImagePath);
        }

        private async void CopySelectedObjects_Click(object sender, RoutedEventArgs e) =>
            await CopySelectedObjectsAsync();

        private async void CutSelectedObjects_Click(object sender, RoutedEventArgs e) =>
            await CutSelectedObjectsAsync();

        private async Task<bool> CutSelectedObjectsAsync()
        {
            List<PdfAnnotation> selectedObjects = GetCopyableSelection();
            if (selectedObjects.Count == 0 ||
                selectedObjects.Count != _annotationCanvasController.SelectedAnnotations.Count)
            {
                return false;
            }

            if (!await CopySelectedObjectsAsync())
                return false;

            if (!await DeleteSelectedAnnotationsAsync())
                return false;

            TxtStatus.Text = selectedObjects.Count > 1
                ? $"{selectedObjects.Count}개 객체를 잘라냈습니다."
                : selectedObjects[0].Type == AnnotationType.Image
                    ? "이미지를 잘라냈습니다."
                    : "텍스트를 잘라냈습니다.";
            return true;
        }

        private async Task<bool> CopySelectedObjectsAsync()
        {
            List<PdfAnnotation> selectedObjects = GetCopyableSelection();
            if (selectedObjects.Count == 0)
                return false;

            try
            {
                var dataPackage = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy
                };
                dataPackage.SetData(
                    AnnotationClipboardFormat,
                    JsonSerializer.Serialize(selectedObjects.Select(annotation => annotation.Clone())));

                List<PdfAnnotation> selectedText = selectedObjects
                    .Where(IsTextAnnotation)
                    .ToList();
                if (selectedText.Count > 0)
                {
                    dataPackage.SetText(string.Join(
                        Environment.NewLine,
                        selectedText.Select(annotation => annotation.Content)));
                }

                PdfAnnotation? selectedImage = selectedObjects.FirstOrDefault(annotation =>
                    annotation.Type == AnnotationType.Image &&
                    !string.IsNullOrWhiteSpace(annotation.ImagePath) &&
                    File.Exists(annotation.ImagePath));
                if (selectedImage?.ImagePath != null)
                {
                    StorageFile imageFile = await StorageFile.GetFileFromPathAsync(selectedImage.ImagePath);
                    dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromFile(imageFile));
                }

                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                TxtStatus.Text = selectedObjects.Count > 1
                    ? $"{selectedObjects.Count}개 객체를 클립보드에 복사했습니다."
                    : selectedObjects[0].Type == AnnotationType.Image
                        ? "이미지를 클립보드에 복사했습니다."
                        : "텍스트를 클립보드에 복사했습니다.";
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard copy error: {ex}");
                TxtStatus.Text = "클립보드에 복사하지 못했습니다.";
                return false;
            }
        }

        private async void Paste_Click(object sender, RoutedEventArgs e) =>
            await PasteClipboardContentAsync();

        private async Task<bool> PasteClipboardContentAsync()
        {
            if (!_pdfManager.IsLoaded)
                return false;

            try
            {
                DataPackageView clipboardContent = Clipboard.GetContent();
                if (clipboardContent.Contains(AnnotationClipboardFormat))
                {
                    object data = await clipboardContent.GetDataAsync(AnnotationClipboardFormat);
                    if (data is string json)
                    {
                        List<PdfAnnotation>? annotations =
                            JsonSerializer.Deserialize<List<PdfAnnotation>>(json);
                        if (annotations != null && PasteAnnotationObjects(annotations))
                            return true;
                    }
                }

                if (clipboardContent.Contains(StandardDataFormats.Bitmap))
                {
                    RandomAccessStreamReference bitmapReference =
                        await clipboardContent.GetBitmapAsync();
                    ClipboardImageData image = await SaveClipboardImageAsync(bitmapReference);
                    AddPastedAnnotations(new[] { CreateImageAnnotation(image) });
                    TxtStatus.Text = "이미지를 새 객체로 붙여넣었습니다.";
                    return true;
                }

                if (clipboardContent.Contains(StandardDataFormats.Text))
                {
                    string text = await clipboardContent.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AddPastedAnnotations(new[] { CreateTextAnnotation(text) });
                        TxtStatus.Text = "텍스트를 새 객체로 붙여넣었습니다.";
                        return true;
                    }
                }

                TxtStatus.Text = "붙여넣을 수 있는 텍스트나 이미지가 없습니다.";
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard paste error: {ex}");
                TxtStatus.Text = "클립보드 내용을 붙여넣지 못했습니다.";
                return false;
            }
        }

        private List<PdfAnnotation> GetCopyableSelection() =>
            _annotationCanvasController.SelectedAnnotations
                .Where(annotation =>
                    IsTextAnnotation(annotation)
                        ? !string.IsNullOrWhiteSpace(annotation.Content)
                        : annotation.Type == AnnotationType.Image &&
                          !string.IsNullOrWhiteSpace(annotation.ImagePath) &&
                          File.Exists(annotation.ImagePath))
                .OrderBy(annotation => annotation.PageIndex)
                .ThenBy(annotation => annotation.Y)
                .ThenBy(annotation => annotation.X)
                .ToList();

        private static bool IsTextAnnotation(PdfAnnotation annotation) =>
            annotation.Type is AnnotationType.Text or AnnotationType.FreeText;

        private static bool CanPasteClipboardContent()
        {
            try
            {
                DataPackageView content = Clipboard.GetContent();
                return content.Contains(AnnotationClipboardFormat) ||
                    content.Contains(StandardDataFormats.Text) ||
                    content.Contains(StandardDataFormats.Bitmap);
            }
            catch
            {
                return false;
            }
        }

        private bool PasteAnnotationObjects(IEnumerable<PdfAnnotation> sourceAnnotations)
        {
            (double pageWidth, double pageHeight) = _pdfManager.GetPageSize(_currentPageIndex);
            var pasted = new List<PdfAnnotation>();
            foreach (PdfAnnotation source in sourceAnnotations.Where(annotation =>
                IsTextAnnotation(annotation) || annotation.Type == AnnotationType.Image))
            {
                if (source.Type == AnnotationType.Image &&
                    (string.IsNullOrWhiteSpace(source.ImagePath) || !File.Exists(source.ImagePath)))
                {
                    continue;
                }

                PdfAnnotation annotation = source.Clone();
                PreparePastedAnnotation(annotation);
                annotation.X = Math.Clamp(
                    source.X + 12,
                    0,
                    Math.Max(pageWidth - Math.Max(annotation.Width, 1), 0));
                annotation.Y = Math.Clamp(
                    source.Y + 12,
                    0,
                    Math.Max(pageHeight - Math.Max(annotation.Height, 1), 0));
                pasted.Add(annotation);
            }

            if (pasted.Count == 0)
                return false;

            AddPastedAnnotations(pasted);
            TxtStatus.Text = pasted.Count > 1
                ? $"{pasted.Count}개 객체를 붙여넣었습니다."
                : pasted[0].Type == AnnotationType.Image
                    ? "이미지를 새 객체로 붙여넣었습니다."
                    : "텍스트를 새 객체로 붙여넣었습니다.";
            return true;
        }

        private void PreparePastedAnnotation(PdfAnnotation annotation)
        {
            // A clipboard copy is a new text object, never a handle to another
            // document's content stream operators.
            annotation.NativeText = null;
            annotation.Id = Guid.NewGuid().ToString();
            annotation.PageIndex = _currentPageIndex;
            annotation.CreatedAt = DateTime.Now;
            annotation.IsApplied = false;
            annotation.IsOriginalTextReplacement = false;
            annotation.IsOriginalImageReplacement = false;
            annotation.OriginalPdfX = 0;
            annotation.OriginalPdfY = 0;
            annotation.OriginalText = string.Empty;
            annotation.OriginalImageName = null;
            annotation.OperatorId = null;
            annotation.ContentStreamIndex = -1;
            annotation.ContentStreamObjectNumber = -1;
            annotation.OperationIndex = -1;
            annotation.OriginalFontObjectNumber = -1;
            annotation.GraphicOperationIndexes.Clear();
            annotation.GraphicTextOperationIndexes.Clear();
            annotation.GraphicOperations.Clear();
            if (IsTextAnnotation(annotation))
                annotation.TextFragments.Clear();
        }

        private PdfAnnotation CreateTextAnnotation(string text)
        {
            (double pageWidth, double pageHeight) = _pdfManager.GetPageSize(_currentPageIndex);
            (double width, double height) = AnnotationTextLayoutService.MeasureBounds(
                text,
                _fontSettings.FontFamily,
                _fontSettings.FontSize,
                _fontSettings.IsBold,
                _fontSettings.IsItalic,
                _fontSettings.IsBold ? 700 : 400);
            return new PdfAnnotation
            {
                Type = AnnotationType.Text,
                PageIndex = _currentPageIndex,
                X = Math.Max((pageWidth - width) / 2, 0),
                Y = Math.Max((pageHeight - height) / 2, 0),
                Width = Math.Max(width, 1),
                Height = Math.Max(height, 1),
                Content = text,
                FontFamily = _fontSettings.FontFamily,
                FontSize = _fontSettings.FontSize,
                Color = _fontSettings.Color,
                FontWeight = _fontSettings.IsBold ? 700 : 400,
                IsBold = _fontSettings.IsBold,
                IsItalic = _fontSettings.IsItalic,
                IsApplied = false
            };
        }

        private PdfAnnotation CreateImageAnnotation(ClipboardImageData image)
        {
            (double pageWidth, double pageHeight) = _pdfManager.GetPageSize(_currentPageIndex);
            (double width, double height) = CalculateInitialImageSize(
                image.PixelWidth,
                image.PixelHeight,
                pageWidth,
                pageHeight);
            return new PdfAnnotation
            {
                Type = AnnotationType.Image,
                PageIndex = _currentPageIndex,
                X = Math.Max((pageWidth - width) / 2, 0),
                Y = Math.Max((pageHeight - height) / 2, 0),
                Width = width,
                Height = height,
                ImagePath = image.Path,
                IsApplied = false
            };
        }

        private static async Task<ClipboardImageData> SaveClipboardImageAsync(
            RandomAccessStreamReference bitmapReference)
        {
            using IRandomAccessStreamWithContentType sourceStream =
                await bitmapReference.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(sourceStream);

            StorageFolder tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
            StorageFolder appFolder = await tempFolder.CreateFolderAsync(
                "PDF_simple_edit",
                CreationCollisionOption.OpenIfExists);
            StorageFolder clipboardFolder = await appFolder.CreateFolderAsync(
                "clipboard",
                CreationCollisionOption.OpenIfExists);
            StorageFile imageFile = await clipboardFolder.CreateFileAsync(
                $"clipboard_{Guid.NewGuid():N}.png",
                CreationCollisionOption.GenerateUniqueName);

            using IRandomAccessStream outputStream = await imageFile.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(
                outputStream,
                decoder);
            await encoder.FlushAsync();
            return new ClipboardImageData(imageFile.Path, decoder.PixelWidth, decoder.PixelHeight);
        }

        private void AddPastedAnnotations(IEnumerable<PdfAnnotation> annotations)
        {
            List<PdfAnnotation> pasted = annotations.ToList();
            if (pasted.Count == 0)
                return;

            SetToolMode(EditToolMode.Select);
            _annotations.AddRange(pasted);
            _annotationCanvasController.ClearSelection();
            foreach (PdfAnnotation annotation in pasted)
                _annotationCanvasController.SelectedAnnotations.Add(annotation);
            _annotationCanvasController.PrimarySelection = pasted[^1];
            _pdfManager.MarkModified();
            RenderAnnotationOverlays();
        }

        private sealed record ClipboardImageData(string Path, uint PixelWidth, uint PixelHeight);

        private async void SaveSelectedImage_Click(object sender, RoutedEventArgs e)
        {
            PdfAnnotation? selectedImage = _annotationCanvasController.SelectedAnnotations.Count == 1
                ? _annotationCanvasController.SelectedAnnotations[0]
                : null;
            string? sourcePath = selectedImage?.Type == AnnotationType.Image
                ? selectedImage.ImagePath
                : null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                TxtStatus.Text = "저장할 이미지 파일을 찾을 수 없습니다.";
                return;
            }

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".png";

            string documentName = !string.IsNullOrWhiteSpace(_pdfManager.FilePath)
                ? Path.GetFileNameWithoutExtension(_pdfManager.FilePath)
                : "document";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = $"{documentName}_page_{selectedImage!.PageIndex + 1}_image{extension}"
            };
            picker.FileTypeChoices.Add(
                GetImageFileTypeDescription(extension),
                new List<string> { extension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? targetFile = await picker.PickSaveFileAsync();
            if (targetFile == null)
                return;

            try
            {
                string sourceFullPath = Path.GetFullPath(sourcePath);
                string targetFullPath = Path.GetFullPath(targetFile.Path);
                if (!string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                    await Task.Run(() => File.Copy(sourceFullPath, targetFullPath, true));
                TxtStatus.Text = $"이미지 저장 완료: {targetFile.Name}";
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(
                    "이미지 저장 오류",
                    $"이미지를 저장하는 중 오류가 발생했습니다: {ex.Message}");
                TxtStatus.Text = "이미지 저장에 실패했습니다.";
            }
        }

        private static string GetImageFileTypeDescription(string extension) =>
            extension switch
            {
                ".jpg" or ".jpeg" => "JPEG 이미지",
                ".tif" or ".tiff" => "TIFF 이미지",
                ".jp2" => "JPEG 2000 이미지",
                ".bmp" => "BMP 이미지",
                ".gif" => "GIF 이미지",
                ".jbig2" => "JBIG2 이미지",
                _ => "PNG 이미지"
            };

        private static (double width, double height) CalculateInitialImageSize(
            uint pixelWidth,
            uint pixelHeight,
            double pageWidth,
            double pageHeight)
        {
            const double preferredMaximumSize = 150;
            if (pixelWidth == 0 || pixelHeight == 0)
                return (preferredMaximumSize, preferredMaximumSize);

            double maximumWidth = Math.Min(preferredMaximumSize, Math.Max(pageWidth * 0.8, 1));
            double maximumHeight = Math.Min(preferredMaximumSize, Math.Max(pageHeight * 0.8, 1));
            double scale = Math.Min(maximumWidth / pixelWidth, maximumHeight / pixelHeight);
            return (
                Math.Max(pixelWidth * scale, 1),
                Math.Max(pixelHeight * scale, 1));
        }

        #endregion

        #region Canvas Interaction

        private async void OverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e) =>
            await _annotationCanvasController.PointerPressedAsync(e);

        private void OverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e) =>
            _annotationCanvasController.PointerMoved(e);

        private async void OverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e) =>
            await _annotationCanvasController.PointerReleasedAsync(e);

        private void OverlayCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
            _annotationCanvasController.DoubleTapped(e);

        private void RenderAnnotationOverlays() =>
            _annotationCanvasController.Render();

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

            // 편집기의 KeyDown에서 Ctrl 상태를 놓치는 경우에도 창 레벨에서
            // Enter를 먼저 소비하여 줄바꿈 대신 편집을 확정합니다.
            if (_annotationCanvasController.IsInlineEditing &&
                e.Key == Windows.System.VirtualKey.Enter &&
                (_controlKeyIsDown || KeyboardStateService.IsControlDown()))
            {
                var activeEditor = OverlayCanvas.Children.OfType<RichEditBox>().FirstOrDefault();
                if (activeEditor == null ||
                    activeEditor.Tag is not InlineTextEditSession { IsFinishing: true })
                {
                    e.Handled = true;
                    if (activeEditor != null)
                        await _annotationCanvasController.FinishActiveInlineEditAsync();
                }
                return;
            }

            bool controlDown = _controlKeyIsDown || KeyboardStateService.IsControlDown();
            object? focusedElement = Content.XamlRoot == null
                ? null
                : FocusManager.GetFocusedElement(Content.XamlRoot);
            bool textInputFocused = focusedElement is TextBox or RichEditBox or
                NumberBox or ComboBox;
            if (controlDown && !textInputFocused &&
                !_annotationCanvasController.IsInlineEditing)
            {
                if (e.Key == Windows.System.VirtualKey.C &&
                    GetCopyableSelection().Count > 0)
                {
                    e.Handled = true;
                    await CopySelectedObjectsAsync();
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.X &&
                    GetCopyableSelection().Count ==
                        _annotationCanvasController.SelectedAnnotations.Count &&
                    _annotationCanvasController.SelectedAnnotations.Count > 0)
                {
                    e.Handled = true;
                    await CutSelectedObjectsAsync();
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.V && _pdfManager.IsLoaded)
                {
                    e.Handled = true;
                    await PasteClipboardContentAsync();
                    return;
                }
            }

            if (e.Key == Windows.System.VirtualKey.Delete &&
                _annotationCanvasController.PrimarySelection != null &&
                !_annotationCanvasController.IsInlineEditing)
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

        private Task<bool> DeleteSelectedAnnotationsAsync() =>
            _annotationEditController.DeleteSelectionAsync();
        #endregion

        #region Font Settings

        private async void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingFontControls)
                return;

            if (CmbFontFamily.SelectedItem is ComboBoxItem item)
            {
                string font = item.Content?.ToString() ?? "맑은 고딕";
                await _annotationEditController.ApplyFontFamilyAsync(font);
                SaveWindowPosition();
            }
        }

        private async void FontSize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingFontControls)
                return;

            if (CmbFontSize.SelectedItem is ComboBoxItem item &&
                double.TryParse(
                    item.Content?.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double sizeVal))
            {
                await _annotationEditController.ApplyFontSizeAsync(sizeVal);
                SaveWindowPosition();
            }
        }

        private async void FontBold_Click(object sender, RoutedEventArgs e)
        {
            await _annotationEditController.ApplyBoldAsync(BtnBold.IsChecked == true);
            SaveWindowPosition();
        }

        private async void FontItalic_Click(object sender, RoutedEventArgs e)
        {
            await _annotationEditController.ApplyItalicAsync(BtnItalic.IsChecked == true);
            SaveWindowPosition();
        }

        private async void FontColor_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ColorPalette.SelectedItem is Border border && border.Tag is string color)
            {
                FontColorIndicator.Background = new SolidColorBrush(ParseColor(color));
                await _annotationEditController.ApplyColorAsync(color);
                SaveWindowPosition();
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
            if (!_pdfManager.IsLoaded)
                return;

            List<int> pageIndices = GetSelectedPageIndices();
            if (pageIndices.Count == 0)
                pageIndices.Add(_currentPageIndex);
            if (pageIndices.Count == 0 || pageIndices.Count >= _pdfManager.PageCount)
                return;

            int pageCountBeforeDelete = _pdfManager.PageCount;
            int currentPageBeforeDelete = _currentPageIndex;
            string pageDescription = pageIndices.Count == 1
                ? $"페이지 {pageIndices[0] + 1}"
                : $"선택한 {pageIndices.Count}개 페이지";

            var dialog = new ContentDialog
            {
                Title = "페이지 삭제",
                Content = $"{pageDescription}를 삭제하시겠습니까?",
                PrimaryButtonText = "삭제",
                CloseButtonText = "취소",
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                int deletedBeforeCurrent = pageIndices.Count(index => index < currentPageBeforeDelete);
                _pdfManager.DeletePages(pageIndices);
                PageListView.SelectedItems.Clear();
                _currentPageIndex = currentPageBeforeDelete - deletedBeforeCurrent;
                if (_currentPageIndex >= _pdfManager.PageCount)
                    _currentPageIndex = _pdfManager.PageCount - 1;
                if (_currentPageIndex < 0)
                    _currentPageIndex = 0;

                // [중요] 렌더링 캐시 초기화
                _renderTempPath = null;
                
                // PdfManager.DeletePage()가 DocumentChanged를 호출하고, 
                // 이는 PdfManager_DocumentChanged 핸들러에 의해 자동으로 Render 및 Thumbnail 로드를 수행합니다.
                UpdateUIState();
                TxtStatus.Text = pageCountBeforeDelete == _pdfManager.PageCount + pageIndices.Count
                    ? "페이지가 삭제되었습니다"
                    : "페이지 삭제에 실패했습니다";
            }
        }

        private async void ExtractSelectedPages_Click(object sender, RoutedEventArgs e)
        {
            if (!_pdfManager.IsLoaded)
                return;

            List<int> pageIndices = GetSelectedPageIndices();
            if (pageIndices.Count == 0)
                return;

            var picker = new FileSavePicker
            {
                SuggestedFileName = GetSuggestedExtractFileName()
            };
            picker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null)
                return;

            TxtStatus.Text = "페이지 추출 중...";
            LoadingRing.IsActive = true;
            try
            {
                bool success = await _pdfManager.ExportPagesAsync(file.Path, pageIndices);
                TxtStatus.Text = success
                    ? $"페이지 추출 완료: {pageIndices.Count}개 페이지"
                    : "페이지 추출에 실패했습니다";
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("페이지 추출 오류", $"페이지 추출 중 오류가 발생했습니다: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private string GetSuggestedExtractFileName()
        {
            string baseName = !string.IsNullOrWhiteSpace(_pdfManager.FilePath)
                ? Path.GetFileNameWithoutExtension(_pdfManager.FilePath)
                : "문서";
            return $"{baseName}_추출.pdf";
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

        private async Task<bool> PerformSaveAsync(string filePath, bool isUserSave)
        {
            if (!_pdfManager.IsLoaded || _isSaveInProgress)
                return false;

            _isSaveInProgress = true;
            string? previousRenderPath = null;
            if (isUserSave)
            {
                // OpenAsync에서 발생하는 DocumentChanged가 저장 완료 직후 렌더링을
                // 예약합니다. 이때 이전 임시 파일이 남아 있으면 저장 전 화면을
                // 다시 사용하므로, 실제 저장을 시작하기 전에 캐시를 무효화합니다.
                previousRenderPath = _renderTempPath;
                _renderTempPath = null;
            }
            TxtStatus.Text = isUserSave ? "저장 중..." : "렌더링 준비 중...";
            LoadingRing.IsActive = true;

            try
            {
                // LostFocus에서 시작된 비동기 확정도 끝까지 기다려야 마지막 입력
                // (특히 IME 입력 직후의 문장부호)이 저장에서 빠지지 않습니다.
                await _annotationCanvasController.FinishActiveInlineEditAsync();

                await _pdfSaveService.SaveAsync(
                    _pdfManager,
                    _annotations,
                    filePath,
                    isUserSave);

                if (isUserSave)
                {
                    _pageRenderService.DeleteTemporaryFile(previousRenderPath);
                    previousRenderPath = null;
                    if (_activeTab != null)
                        _activeTab.FilePath = filePath;

                    // 이벤트 큐의 실행 순서와 무관하게 최종 화면이 방금 저장한
                    // PDF를 사용하도록 한 번 더 명시적으로 갱신합니다.
                    await RenderCurrentPageAsync();
                }

                return true;
            }
            catch (Exception ex)
            {
                if (isUserSave && _renderTempPath == null &&
                    previousRenderPath != null && File.Exists(previousRenderPath))
                {
                    _renderTempPath = previousRenderPath;
                }
                await ShowErrorDialogAsync("저장 오류", $"저장 중 오류가 발생했습니다: {ex.Message}");
                return false;
            }
            finally
            {
                LoadingRing.IsActive = false;
                _isSaveInProgress = false;
                UpdateUIState();
            }
        }

        private static Windows.UI.Color ParseColor(string hexColor) =>
            EditorColorService.Parse(hexColor);

        private async Task ShowErrorDialogAsync(string title, string message)
        {
            await _dialogService.ShowErrorAsync(Content.XamlRoot, title, message);
        }

        private void SaveWindowPosition() => _windowSettingsController.Save();

        private void LoadWindowPosition() => _windowSettingsController.Load();

        private void AddToRecentFiles(string filePath) =>
            _recentFilesController.Add(filePath);

        #endregion

        #region Alignment & Editing

        private async void AlignLeft_Click(object sender, RoutedEventArgs e) =>
            await _annotationEditController.AlignSelectionAsync(AnnotationAlignment.Left);

        private async void AlignRight_Click(object sender, RoutedEventArgs e) =>
            await _annotationEditController.AlignSelectionAsync(AnnotationAlignment.Right);

        private async void AlignTop_Click(object sender, RoutedEventArgs e) =>
            await _annotationEditController.AlignSelectionAsync(AnnotationAlignment.Top);

        private async void AlignBottom_Click(object sender, RoutedEventArgs e) =>
            await _annotationEditController.AlignSelectionAsync(AnnotationAlignment.Bottom);

        private async void DeleteAnnotation_Click(object sender, RoutedEventArgs e)
        {
            await DeleteSelectedAnnotationsAsync();
        }

        #endregion
    }
}
