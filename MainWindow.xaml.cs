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
        private string? _tempFilePath;

        // For highlight drag
        private bool _isDragging;
        private Windows.Foundation.Point _dragStart;
        private Microsoft.UI.Xaml.Shapes.Rectangle? _dragRect;

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

                Activated += MainWindow_Activated;
                Closed += MainWindow_Closed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MainWindow constructor error: {ex}");
                // In a real app we might want to show a message box here using native Win32 if XAML fails
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

                    // Set minimum window size
                    var hwnd = WindowNative.GetWindowHandle(this);
                    if (hwnd != IntPtr.Zero)
                    {
                        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = AppWindow.GetFromWindowId(windowId);
                        if (appWindow != null)
                        {
                            appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
                        }
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
            if (_tempFilePath != null && File.Exists(_tempFilePath))
            {
                try { File.Delete(_tempFilePath); } catch { }
            }
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
                    await LoadThumbnailsAsync();
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
                LoadingRing.IsActive = true;
                TxtStatus.Text = "파일을 여는 중...";

                bool success = await _pdfManager.OpenAsync(file.Path);
                if (success)
                {
                    _currentPageIndex = 0;
                    _annotations.Clear();
                    _tempFilePath = file.Path;
                    await RenderCurrentPageAsync();
                    await LoadThumbnailsAsync();
                }
                else
                {
                    await ShowErrorDialogAsync("오류", "PDF 파일을 열 수 없습니다.");
                }

                LoadingRing.IsActive = false;
                UpdateUIState();
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
                    _tempFilePath = _pdfManager.FilePath;
                    await RenderCurrentPageAsync();
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
                bool success = await _pdfManager.SaveAsAsync(file.Path);
                TxtStatus.Text = success ? "저장됨" : "저장 실패";

                if (success)
                {
                    _tempFilePath = file.Path;
                    await RenderCurrentPageAsync();
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
                string tempPath = _tempFilePath ?? _pdfManager.FilePath ?? "";
                if (string.IsNullOrEmpty(tempPath))
                {
                    tempPath = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid()}.pdf");
                    await _pdfManager.SaveAsAsync(tempPath);
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

            string? filePath = _tempFilePath ?? _pdfManager.FilePath;
            if (filePath == null || !File.Exists(filePath))
            {
                filePath = Path.Combine(Path.GetTempPath(), $"render_{Guid.NewGuid()}.pdf");
                await _pdfManager.SaveAsAsync(filePath);
                _tempFilePath = filePath;
            }

            try
            {
                var ms = await PdfRenderHelper.RenderPageWithWindowsPdfAsync(
                    filePath, _currentPageIndex, _renderScale);

                if (ms != null)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
                    PdfPageImage.Source = bitmap;

                    var pageSize = await PdfRenderHelper.GetPageSizeAsync(filePath, _currentPageIndex);
                    OverlayCanvas.Width = pageSize.width * _renderScale;
                    OverlayCanvas.Height = pageSize.height * _renderScale;

                    RenderAnnotationOverlays();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
            }

            UpdateUIState();
        }

        private async Task SaveToTempAndRenderAsync()
        {
            if (!_pdfManager.IsLoaded) return;

            _tempFilePath = Path.Combine(Path.GetTempPath(), $"pdfedit_{Guid.NewGuid()}.pdf");
            await _pdfManager.SaveAsAsync(_tempFilePath);
            await RenderCurrentPageAsync();
        }

        private async Task LoadThumbnailsAsync()
        {
            _pageThumbnails.Clear();

            if (!_pdfManager.IsLoaded) return;

            string? filePath = _tempFilePath ?? _pdfManager.FilePath;
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
            _zoomLevel = 1.0;
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

        #endregion

        #region Tool Modes

        private void SetToolMode(EditToolMode mode)
        {
            _currentTool = mode;

            BtnSelect.IsChecked = mode == EditToolMode.Select;
            BtnAddText.IsChecked = mode == EditToolMode.AddText;
            BtnHighlight.IsChecked = mode == EditToolMode.Highlight;
            BtnStickyNote.IsChecked = mode == EditToolMode.AddStickyNote;

            TxtToolMode.Text = mode switch
            {
                EditToolMode.AddText => "도구: 텍스트 추가",
                EditToolMode.Highlight => "도구: 텍스트 강조",
                EditToolMode.AddStickyNote => "도구: 스티커 노트",
                EditToolMode.AddImage => "도구: 이미지 추가",
                EditToolMode.Select => "도구: 선택",
                _ => ""
            };
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

            var pos = e.GetCurrentPoint(OverlayCanvas).Position;
            double pdfX = pos.X / _renderScale;
            double pdfY = pos.Y / _renderScale;

            switch (_currentTool)
            {
                case EditToolMode.AddText:
                    await ShowTextInputDialogAsync(pdfX, pdfY);
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

                double x = Canvas.GetLeft(_dragRect) / _renderScale;
                double y = Canvas.GetTop(_dragRect) / _renderScale;
                double w = _dragRect.Width / _renderScale;
                double h = _dragRect.Height / _renderScale;

                if (w > 5 && h > 5)
                {
                    _pdfManager.AddHighlight(_currentPageIndex, x, y, w, h,
                        XColor.FromArgb(255, 255, 255, 0), 0.3);

                    await SaveToTempAndRenderAsync();
                    TxtStatus.Text = "텍스트 강조가 추가되었습니다";
                }

                OverlayCanvas.Children.Remove(_dragRect);
                _dragRect = null;
            }
        }

        private void RenderAnnotationOverlays()
        {
            OverlayCanvas.Children.Clear();

            var pageAnnotations = _annotations.Where(a =>
                a.PageIndex == _currentPageIndex && !a.IsApplied).ToList();

            foreach (var ann in pageAnnotations)
            {
                if (ann.Type == AnnotationType.Text || ann.Type == AnnotationType.FreeText)
                {
                    var tb = new TextBlock
                    {
                        Text = ann.Content,
                        FontSize = ann.FontSize * _renderScale,
                        Foreground = new SolidColorBrush(ParseColor(ann.Color)),
                        FontWeight = ann.IsBold
                            ? Microsoft.UI.Text.FontWeights.Bold
                            : Microsoft.UI.Text.FontWeights.Normal,
                        FontStyle = ann.IsItalic
                            ? Windows.UI.Text.FontStyle.Italic
                            : Windows.UI.Text.FontStyle.Normal,
                    };
                    Canvas.SetLeft(tb, ann.X * _renderScale);
                    Canvas.SetTop(tb, ann.Y * _renderScale);
                    OverlayCanvas.Children.Add(tb);
                }
            }
        }

        #endregion

        #region Text Input Dialog

        private async Task ShowTextInputDialogAsync(double pdfX, double pdfY)
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
            panel.Children.Add(new TextBlock { Text = "텍스트:" });
            panel.Children.Add(txtContent);

            // Font family
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

            // Color
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

                _fontSettings.FontFamily = fontFamily;
                _fontSettings.FontSize = fontSize;
                _fontSettings.IsBold = isBold;
                _fontSettings.IsItalic = isItalic;
                _fontSettings.Color = selectedColor;

                await SaveToTempAndRenderAsync();
                TxtStatus.Text = "텍스트가 추가되었습니다";
            }
        }

        private async Task ShowStickyNoteDialogAsync(double pdfX, double pdfY)
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

            dialog.Content = txtContent;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(txtContent.Text))
            {
                _pdfManager.AddStickyNote(_currentPageIndex, pdfX, pdfY, txtContent.Text,
                    _fontSettings.FontFamily, _fontSettings.FontSize);

                await SaveToTempAndRenderAsync();
                TxtStatus.Text = "스티커 노트가 추가되었습니다";
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
            string? filePath = _tempFilePath ?? _pdfManager.FilePath;
            if (filePath == null) return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

                int startPage = _currentPageIndex;
                int totalPages = (int)pdfDoc.PageCount;

                // Navigate to next/previous page
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
                    // Direct content stream byte replacement
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
                    _tempFilePath = file.Path;
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

        #endregion
    }
}
