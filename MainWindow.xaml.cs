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
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        private bool _isFirstLoad
        {
            get => _activeTab?.IsFirstLoad ?? false;
            set { if (_activeTab != null) _activeTab.IsFirstLoad = value; }
        }
        private AnnotationCanvasController _annotationCanvasController = null!;
        private AnnotationEditController _annotationEditController = null!;
        private AnnotationClipboardController _clipboardController = null!;
        private EditorImageController _imageController = null!;
        private EditorFontController _fontController = null!;
        private PdfPageOperationsController _pageOperationsController = null!;
        private PageViewController _pageViewController = null!;
        private WindowSettingsController _windowSettingsController = null!;
        private RecentFilesController _recentFilesController = null!;

        private readonly DocumentSearchController _documentSearchController = new(new PdfSearchService());
        private readonly PdfPageRenderService _pageRenderService = new();
        private readonly EditorDialogService _dialogService = new();
        private DocumentFileController _fileController = null!;
        private bool _controlKeyIsDown;
        private readonly TextFontSettings _fontSettings = new();
        private string _signatureColor = "#000000";
        private double _signatureLineWidth = 2.0;

        // Resolve the selected document when a composed controller invokes a command.
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
        private ColorPicker SignatureColorPicker => EditorToolbar.SignatureColorPicker;
        private Slider SldSignatureWidth => EditorToolbar.SignatureWidthSlider;
        private TextBlock TxtSignatureWidth => EditorToolbar.SignatureWidthText;
        private Button BtnSignatureSettings => EditorToolbar.SignatureSettingsButton;
        private Border HighlightColorIndicator => EditorToolbar.HighlightIndicator;
        private ColorPicker HighlightColorPicker => EditorToolbar.HighlightPicker;
        private Slider SldHighlightOpacity => EditorToolbar.HighlightOpacitySlider;
        private TextBlock TxtHighlightOpacity => EditorToolbar.HighlightOpacityText;
        private Button BtnHighlightSettings => EditorToolbar.HighlightSettingsButton;
        private ToggleButton BtnColorPicker => EditorToolbar.ColorPickerButton;
        private Button BtnAddImage => EditorToolbar.AddImageButton;
        private TextBlock TxtZoom => EditorToolbar.ZoomText;

        private ListView PageListView => PagePanel.ListView;
        private StackPanel WelcomePanel => PdfSurface.Welcome;
        private ScrollViewer PdfScrollViewer => PdfSurface.Viewer;
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
                    RenderCurrentPageAsync);
                _clipboardController = new AnnotationClipboardController(
                    _annotationCanvasController, _fontSettings, TxtStatus,
                    () => _pdfManager, () => _annotations, () => _currentPageIndex,
                    DeleteSelectedAnnotationsAsync, SetToolMode);
                _imageController = new EditorImageController(
                    _annotationCanvasController, new PdfImageExtractionService(), TxtStatus, LoadingRing,
                    () => _pdfManager, () => _annotations, () => _currentPageIndex,
                    () => WindowNative.GetWindowHandle(this), SetToolMode, ShowErrorDialogAsync);
                _fontController = new EditorFontController(
                    EditorToolbar, _annotationCanvasController, _annotationEditController,
                    _fontSettings, TxtStatus, () => _pdfManager, () => _annotations,
                    () => _windowSettingsController?.Save());
                _pageOperationsController = new PdfPageOperationsController(
                    new PdfOperationService(), TxtStatus, LoadingRing,
                    () => _pdfManager, () => _activeTab, () => _currentPageIndex,
                    value => _currentPageIndex = value, GetSelectedPageIndices,
                    () => PageListView.SelectedItems.Clear(),
                    AddDocumentTab,
                    () => WindowNative.GetWindowHandle(this), () => Content.XamlRoot,
                    UpdateUIState, ShowErrorDialogAsync);
                _windowSettingsController = new WindowSettingsController(
                    this,
                    new EditorSettingsService(),
                    _fontSettings,
                    _fontController.ApplySettings);
                _fileController = new DocumentFileController(
                    _dialogService, new PdfSaveService(new PdfAnnotationDocumentService()),
                    new PrintHelper(), TxtStatus, LoadingRing,
                    () => _pdfManager, () => _annotations, () => _activeTab,
                    AddDocumentTab, AddToRecentFiles,
                    () => WindowNative.GetWindowHandle(this), () => Content.XamlRoot,
                    _annotationCanvasController.FinishActiveInlineEditAsync,
                    RenderCurrentPageAsync, UpdateUIState, ShowErrorDialogAsync);
                _recentFilesController = new RecentFilesController(
                    new RecentFilesService(),
                    EditorMenu.RecentFilesMenu,
                    _fileController.OpenPdfFileAsync,
                    ShowErrorDialogAsync);
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
            _pageViewController?.CancelPendingOperations();
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

        private void ExecuteMenuCommand(EditorMenuCommand command)
        {
            switch (command)
            {
                case EditorMenuCommand.NewDocument: NewDocument_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.OpenFile: OpenFile_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.SaveFile: SaveFile_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.SaveAsFile: SaveAsFile_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.Print: Print_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.CloseFile: CloseFile_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.Undo: Undo_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.Redo: Redo_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.Find: Find_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.ZoomIn: ZoomIn_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.ZoomOut: ZoomOut_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.FitToPage: FitToPage_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.TogglePagePanel: TogglePagePanel_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.SelectTool: SelectTool_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.AddTextTool: AddTextTool_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.HighlightTool: HighlightTool_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.SignatureTool: SignatureTool_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.AddImageTool: AddImageTool_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.ExtractAllImages: ExtractAllImages_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.MergePdf: MergePdf_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.SplitPdf: SplitPdf_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.DeletePage: DeletePage_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.Settings: Settings_Click(EditorMenu, new RoutedEventArgs()); break;
                case EditorMenuCommand.About: About_Click(EditorMenu, new RoutedEventArgs()); break;
            }
        }

        private void WireChildControlEvents()
        {
            EditorMenu.CommandRequested += ExecuteMenuCommand;
            EditorMenu.ToolAcceleratorInvoked += ToolAccelerator_Invoked;
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

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _pageViewController.CancelPendingOperations();

            try { SaveWindowPosition(); } catch { }
        }

        #region Document Events

        private void PdfManager_DocumentChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!ReferenceEquals(sender, _activeTab?.PdfManager)) return;
                PdfDocumentTab? tab = _activeTab;
                UpdateUIState();
                if (_pdfManager.IsLoaded)
                {
                    await RenderCurrentPageAsync();
                    if (!ReferenceEquals(tab, _activeTab)) return;

                    if (_isFirstLoad)
                    {
                        _isFirstLoad = false;
                        for (int i = 0; i < 60; i++)
                        {
                            if (PdfScrollViewer.ViewportWidth > 0 && OverlayCanvas.Width > 0)
                            {
                                FitToPage();
                                break;
                            }
                            await Task.Delay(16);
                            if (!ReferenceEquals(tab, _activeTab)) return;
                        }
                    }
                }
            });
        }

        private void PdfManager_PageStructureChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!ReferenceEquals(sender, _activeTab?.PdfManager)) return;
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
            EditorMenu.UpdateDocumentState(hasDoc, _pdfManager.CanUndo, _pdfManager.CanRedo);

            WelcomePanel.Visibility = hasDoc ? Visibility.Collapsed : Visibility.Visible;
            PdfScrollViewer.Visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;

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

        private void AddDocumentTab(PdfDocumentTab tab)
        {
            _tabs.Add(tab);
            DocTabView.SelectedItem = tab;
        }

        private void NewDocument_Click(object sender, RoutedEventArgs? e) => _fileController.NewDocument();
        private async void OpenFile_Click(object sender, RoutedEventArgs? e) => await _fileController.OpenFilesAsync();
        private async void SaveFile_Click(object sender, RoutedEventArgs? e) => await _fileController.SaveAsync();
        private async void SaveAsFile_Click(object sender, RoutedEventArgs? e) => await _fileController.SaveAsAsync();
        private async void Print_Click(object sender, RoutedEventArgs? e) => await _fileController.PrintAsync();

        #region File Operations

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
                await _fileController.OpenPdfFileAsync(file);
            }
        }

        private void CloseFile_Click(object sender, RoutedEventArgs? e)
        {
            if (_activeTab != null)
            {
                _tabs.Remove(_activeTab);
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

        #endregion

        #region Tool Modes

        private void SetToolMode(EditToolMode mode)
        {
            _currentTool = mode;
            EditorMenu.SetToolMode(mode);

            BtnSelect.IsChecked = mode == EditToolMode.Select;
            BtnAddText.IsChecked = mode == EditToolMode.AddText;
            BtnHighlight.IsChecked = mode == EditToolMode.Highlight;
            BtnSignature.IsChecked = mode == EditToolMode.Signature;
            BtnColorPicker.IsChecked = mode == EditToolMode.ColorPicker;

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

        private void AnnotationContextMenu_Opening(object? sender, object e)
        {
            int selectedCount = _annotationCanvasController.SelectedAnnotations.Count;
            int copyableCount = _clipboardController.GetCopyableSelection().Count;
            bool hasCopyableSelection = copyableCount > 0;
            PdfSurface.CopyItem.Visibility = hasCopyableSelection
                ? Visibility.Visible
                : Visibility.Collapsed;
            PdfSurface.CutItem.Visibility = hasCopyableSelection && copyableCount == selectedCount
                ? Visibility.Visible
                : Visibility.Collapsed;
            PdfSurface.PasteItem.IsEnabled = AnnotationClipboardController.CanPasteClipboardContent();

            PdfAnnotation? selectedImage = _annotationCanvasController.SelectedAnnotations.Count == 1
                ? _annotationCanvasController.SelectedAnnotations[0]
                : null;
            PdfSurface.SaveImageItem.IsEnabled = selectedImage?.Type == AnnotationType.Image &&
                !string.IsNullOrWhiteSpace(selectedImage.ImagePath) &&
                File.Exists(selectedImage.ImagePath);
        }

        private async void CopySelectedObjects_Click(object sender, RoutedEventArgs e) =>
            await _clipboardController.CopySelectedObjectsAsync();

        private async void Paste_Click(object sender, RoutedEventArgs e) =>
            await _clipboardController.PasteClipboardContentAsync();

        private async void CutSelectedObjects_Click(object sender, RoutedEventArgs e) =>
            await _clipboardController.CutSelectedObjectsAsync();

        #endregion

        private async void AddImageTool_Click(object sender, RoutedEventArgs e) =>
            await _imageController.AddImageAsync();

        private async void ExtractAllImages_Click(object sender, RoutedEventArgs e) =>
            await _imageController.ExtractAllImagesAsync();

        private async void SaveSelectedImage_Click(object sender, RoutedEventArgs e) =>
            await _imageController.SaveSelectedImageAsync();

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
            // Ctrl+Enter를 소비하여 줄바꿈 대신 편집을 확정합니다.
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
                    _clipboardController.GetCopyableSelection().Count > 0)
                {
                    e.Handled = true;
                    await _clipboardController.CopySelectedObjectsAsync();
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.X &&
                    _clipboardController.GetCopyableSelection().Count ==
                        _annotationCanvasController.SelectedAnnotations.Count &&
                    _annotationCanvasController.SelectedAnnotations.Count > 0)
                {
                    e.Handled = true;
                    await _clipboardController.CutSelectedObjectsAsync();
                    return;
                }

                if (e.Key == Windows.System.VirtualKey.V && _pdfManager.IsLoaded)
                {
                    e.Handled = true;
                    await _clipboardController.PasteClipboardContentAsync();
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

        private async void MergePdf_Click(object sender, RoutedEventArgs e) =>
            await _pageOperationsController.MergeAsync();

        private async void SplitPdf_Click(object sender, RoutedEventArgs e) =>
            await _pageOperationsController.SplitAsync();

        private async void DeletePage_Click(object sender, RoutedEventArgs e) =>
            await _pageOperationsController.DeletePagesAsync();

        private async void ExtractSelectedPages_Click(object sender, RoutedEventArgs e) =>
            await _pageOperationsController.ExtractSelectedPagesAsync();

        #region View

        private void TogglePagePanel_Click(object sender, RoutedEventArgs e)
        {
            bool show = EditorMenu.ShowPagePanel;
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
            _fontController.ApplySettings();
            SaveWindowPosition();
            if (_pdfManager.IsLoaded)
                await RenderCurrentPageAsync();
        }

        private async void About_Click(object sender, RoutedEventArgs e) =>
            await _dialogService.ShowAboutAsync(Content.XamlRoot);

        #endregion

        #region Helpers

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
