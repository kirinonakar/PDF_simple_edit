using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace PDF_simple_edit.Controllers;

/// <summary>
/// Owns annotation interaction state and coordinates the annotation canvas.
/// The window supplies the active document through delegates so tab changes do
/// not require rebuilding this controller.
/// </summary>
public sealed class AnnotationCanvasController
{
    private const double PdfToPixels = 96.0 / 72.0;

    private readonly Canvas _canvas;
    private readonly Image _pageImage;
    private readonly TextBlock _statusText;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<PdfDocumentManager> _getManager;
    private readonly Func<List<PdfAnnotation>> _getAnnotations;
    private readonly Func<int> _getPageIndex;
    private readonly Func<EditToolMode> _getToolMode;
    private readonly Action<EditToolMode> _setToolMode;
    private readonly TextFontSettings _fontSettings;
    private readonly Func<string> _getSignatureColor;
    private readonly Func<double> _getSignatureLineWidth;
    private readonly Action<Windows.UI.Color> _applyPickedHighlightColor;
    private readonly Action _invalidateRenderPath;
    private readonly Func<Task> _renderCurrentPageAsync;
    private readonly Action<bool> _focusAfterInlineEdit;

    private readonly AnnotationContentService _contentService = new();
    private readonly AnnotationOverlayController _overlayController = new();
    private readonly ScreenColorPickerService _screenColorPickerService = new();
    private readonly AnnotationInteractionController _interactionController = new();
    private readonly InlineTextEditorController _inlineTextEditorController = new();
    private readonly AnnotationSelectionController _selectionController;

    private uint? _pendingSelectPointerId;
    private long _selectPointerPressSequence;
    private readonly List<PdfPathPoint> _signaturePoints = new();
    private Polyline? _signaturePreview;
    private int _movePreviewVersion;
    private ImageSource? _moveOriginalImage;
    private readonly SemaphoreSlim _movePreviewGate = new(1, 1);
    private PdfTextPoint? _pendingInlineCaret;

    public AnnotationCanvasController(
        Canvas canvas,
        Image pageImage,
        TextBlock statusText,
        DispatcherQueue dispatcherQueue,
        Func<PdfDocumentManager> getManager,
        Func<List<PdfAnnotation>> getAnnotations,
        Func<int> getPageIndex,
        Func<EditToolMode> getToolMode,
        Action<EditToolMode> setToolMode,
        TextFontSettings fontSettings,
        Func<string> getSignatureColor,
        Func<double> getSignatureLineWidth,
        Action<Windows.UI.Color> applyPickedHighlightColor,
        Action invalidateRenderPath,
        Func<Task> renderCurrentPageAsync,
        Action<bool> focusAfterInlineEdit)
    {
        _canvas = canvas;
        _pageImage = pageImage;
        _statusText = statusText;
        _dispatcherQueue = dispatcherQueue;
        _getManager = getManager;
        _getAnnotations = getAnnotations;
        _getPageIndex = getPageIndex;
        _getToolMode = getToolMode;
        _setToolMode = setToolMode;
        _fontSettings = fontSettings;
        _getSignatureColor = getSignatureColor;
        _getSignatureLineWidth = getSignatureLineWidth;
        _applyPickedHighlightColor = applyPickedHighlightColor;
        _invalidateRenderPath = invalidateRenderPath;
        _renderCurrentPageAsync = renderCurrentPageAsync;
        _focusAfterInlineEdit = focusAfterInlineEdit;
        _selectionController = new AnnotationSelectionController(_contentService);
    }

    private PdfAnnotation? _primarySelection;

    public event Action<PdfAnnotation?>? PrimarySelectionChanged;

    public PdfAnnotation? PrimarySelection
    {
        get => _primarySelection;
        set
        {
            if (ReferenceEquals(_primarySelection, value))
                return;

            _primarySelection = value;
            PrimarySelectionChanged?.Invoke(value);
        }
    }

    public List<PdfAnnotation> SelectedAnnotations { get; } = new();

    public bool IsInlineEditing => _inlineTextEditorController.IsEditing;

    public Task FinishActiveInlineEditAsync() =>
        _inlineTextEditorController.FinishActiveEditAsync();

    public void ClearSelection()
    {
        PrimarySelection = null;
        SelectedAnnotations.Clear();
    }

    public void SelectOnly(PdfAnnotation annotation)
    {
        SelectedAnnotations.Clear();
        SelectedAnnotations.Add(annotation);
        PrimarySelection = annotation;
    }

    public async Task PointerPressedAsync(PointerRoutedEventArgs e)
    {
        PdfDocumentManager manager = _getManager();
        if (!manager.IsLoaded || IsInlineEditing)
            return;

        EnsureCanvasSize();

        var pointerPoint = e.GetCurrentPoint(_canvas);
        Point position = pointerPoint.Position;
        bool isLeftButton = pointerPoint.Properties.IsLeftButtonPressed;
        long selectPressSequence = _selectPointerPressSequence;
        double pdfX = position.X / PdfToPixels;
        double pdfY = position.Y / PdfToPixels;
        EditToolMode toolMode = _getToolMode();

        if (!isLeftButton && toolMode is EditToolMode.Select or EditToolMode.Signature)
            return;

        if (isLeftButton && toolMode == EditToolMode.Select)
        {
            if (_interactionController.CancelMove())
                _canvas.ReleasePointerCapture(e.Pointer);
            _pendingSelectPointerId = e.Pointer.PointerId;
            selectPressSequence = ++_selectPointerPressSequence;
        }

        switch (toolMode)
        {
            case EditToolMode.Select:
                await BeginSelectionAsync(e, position, pdfX, pdfY, selectPressSequence);
                break;
            case EditToolMode.AddText:
                AddInlineTextBox(pdfX, pdfY);
                break;
            case EditToolMode.Highlight:
                _interactionController.BeginHighlight(
                    _canvas,
                    position,
                    _fontSettings.HighlightColor,
                    _fontSettings.HighlightOpacity);
                _canvas.CapturePointer(e.Pointer);
                break;
            case EditToolMode.Signature:
                BeginSignature(e, position, pdfX, pdfY);
                break;
            case EditToolMode.ColorPicker:
                await PickHighlightColorAsync(e);
                break;
        }
    }

    public void PointerMoved(PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(_canvas);

        if (_signaturePreview != null && _getToolMode() == EditToolMode.Signature)
        {
            ContinueSignature(pointerPoint);
            return;
        }

        if (_interactionController.IsMoving &&
            !pointerPoint.Properties.IsLeftButtonPressed)
        {
            _pendingSelectPointerId = null;
            _selectPointerPressSequence++;
            if (_interactionController.CancelMove())
            {
                CancelNativeMovePreview();
                _canvas.ReleasePointerCapture(e.Pointer);
                _statusText.Text = "객체 선택됨 (드래그하여 이동)";
                Render();
            }
            return;
        }

        if (_interactionController.UpdatePointer(
            _canvas,
            pointerPoint.Position,
            PrimarySelection,
            SelectedAnnotations))
        {
            Render();
            if (_interactionController.IsMoving && SelectedAnnotations.Any(a => a.NativeText != null))
                _ = PreviewNativeMoveAsync(++_movePreviewVersion);
        }
    }

    public async Task PointerReleasedAsync(PointerRoutedEventArgs e)
    {
        if (_signaturePreview != null && _getToolMode() == EditToolMode.Signature)
        {
            CompleteSignature(e);
            return;
        }

        if (_pendingSelectPointerId == e.Pointer.PointerId)
        {
            _pendingSelectPointerId = null;
            _selectPointerPressSequence++;
        }

        PdfDocumentManager manager = _getManager();
        int pageIndex = _getPageIndex();
        AnnotationResizeResult resizeResult = await _interactionController
            .CompleteResizeAsync(manager, pageIndex);
        if (resizeResult != AnnotationResizeResult.NotActive)
        {
            await CompleteResizeAsync(e, manager, resizeResult);
            return;
        }

        if (_interactionController.IsMoving)
        {
            await CompleteMoveAsync(e, manager, pageIndex);
            return;
        }

        if (_interactionController.IsDrawingHighlight &&
            _getToolMode() == EditToolMode.Highlight)
        {
            _canvas.ReleasePointerCapture(e.Pointer);
            PdfAnnotation? highlight = _interactionController.CompleteHighlight(
                _canvas, pageIndex, _fontSettings);
            if (highlight != null)
            {
                _getAnnotations().Add(highlight);
                manager.MarkModified();
                _statusText.Text = "텍스트 강조가 추가되었습니다 (저장 시 반영)";
                Render();
            }
        }
    }

    public void DoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        if (IsInlineEditing)
            return;

        Point position = e.GetPosition(_canvas);
        PdfAnnotation? annotation = _contentService.FindAnnotationAt(
            _getAnnotations(),
            _getPageIndex(),
            position.X / PdfToPixels,
            position.Y / PdfToPixels);
        if (annotation != null)
        {
            _pendingInlineCaret = new(position.X / PdfToPixels, position.Y / PdfToPixels);
            EditAnnotationContent(annotation);
        }
    }

    public void Render()
    {
        var activeBox = _canvas.Children.OfType<RichEditBox>().FirstOrDefault();
        PdfAnnotation? editingAnnotation = activeBox?.Tag switch
        {
            InlineTextEditSession session => session.Annotation,
            NativeInlineTextSession session => session.Annotation,
            PdfAnnotation annotation => annotation,
            _ => null
        };

        _overlayController.Render(
            _canvas,
            _getAnnotations(),
            _getPageIndex(),
            SelectedAnnotations,
            PrimarySelection,
            editingAnnotation,
            EditorColorService.Parse,
            (direction, position) =>
            {
                if (PrimarySelection != null)
                    _interactionController.BeginResize(
                        PrimarySelection, direction, position);
            },
            shape => SetElementCursor(
                _canvas,
                InputSystemCursor.Create(shape)));
    }

    private void EnsureCanvasSize()
    {
        if ((_canvas.Width == 0 || double.IsNaN(_canvas.Width)) &&
            _pageImage.ActualWidth > 0)
        {
            _canvas.Width = _pageImage.ActualWidth;
            _canvas.Height = _pageImage.ActualHeight;
        }
    }

    private async Task BeginSelectionAsync(
        PointerRoutedEventArgs e,
        Point position,
        double pdfX,
        double pdfY,
        long selectPressSequence)
    {
        if (e.OriginalSource is Rectangle handle && handle.Tag is string direction)
        {
            if (PrimarySelection != null)
                _interactionController.BeginResize(PrimarySelection, direction, position);
            _canvas.CapturePointer(e.Pointer);
            _statusText.Text = "크기 조정 중...";
            return;
        }

        bool controlPressed = InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        AnnotationSelectionResult selection = await _selectionController.SelectAtAsync(
            _getManager(),
            _getAnnotations(),
            SelectedAnnotations,
            PrimarySelection,
            _getPageIndex(),
            pdfX,
            pdfY,
            controlPressed,
            () => _statusText.Text = "페이지 콘텐츠 분석 중...");

        PrimarySelection = selection.PrimarySelection;
        if (selection.AddedFromPageContent is PdfAnnotation added)
        {
            _statusText.Text = added.IsOriginalTextReplacement
                ? "텍스트 편집 구역이 선택되었습니다. 두 번 클릭하여 편집하세요."
                : "이미지가 선택되었습니다. 드래그하여 이동하거나 핸들로 크기를 조정하세요.";
        }

        if (selection.SelectionChanged)
            Render();

        var currentPointerPoint = e.GetCurrentPoint(_canvas);
        PdfAnnotation? moveTarget = PrimarySelection;
        bool canStartMove = moveTarget != null &&
            _getToolMode() == EditToolMode.Select &&
            _pendingSelectPointerId == e.Pointer.PointerId &&
            selectPressSequence == _selectPointerPressSequence &&
            currentPointerPoint.Properties.IsLeftButtonPressed;
        if (canStartMove && moveTarget != null)
        {
            _interactionController.BeginMove(SelectedAnnotations, position);
            _canvas.CapturePointer(e.Pointer);
            if (!moveTarget.IsOriginalTextReplacement)
            {
                _statusText.Text = SelectedAnnotations.Count > 1
                    ? $"{SelectedAnnotations.Count}개 객체 선택됨"
                    : "객체 선택됨 (드래그하여 이동)";
            }
        }
        else
        {
            if (_pendingSelectPointerId == e.Pointer.PointerId)
                _pendingSelectPointerId = null;
            if (PrimarySelection == null)
                _statusText.Text = "준비";
        }
    }

    private void BeginSignature(
        PointerRoutedEventArgs e,
        Point position,
        double pdfX,
        double pdfY)
    {
        _signaturePoints.Clear();
        _signaturePoints.Add(new PdfPathPoint { X = pdfX, Y = pdfY });
        _signaturePreview = new Polyline
        {
            Stroke = new SolidColorBrush(EditorColorService.Parse(_getSignatureColor())),
            StrokeThickness = Math.Max(_getSignatureLineWidth() * PdfToPixels, 1),
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _signaturePreview.Points.Add(position);
        _canvas.Children.Add(_signaturePreview);
        _canvas.CapturePointer(e.Pointer);
        _statusText.Text = "서명을 그리는 중...";
    }

    private void ContinueSignature(PointerPoint pointerPoint)
    {
        if (!pointerPoint.Properties.IsLeftButtonPressed)
            return;

        Point position = pointerPoint.Position;
        PdfPathPoint last = _signaturePoints[^1];
        double pdfX = position.X / PdfToPixels;
        double pdfY = position.Y / PdfToPixels;
        if (Math.Sqrt(Math.Pow(pdfX - last.X, 2) + Math.Pow(pdfY - last.Y, 2)) < 0.75)
            return;

        _signaturePoints.Add(new PdfPathPoint { X = pdfX, Y = pdfY });
        _signaturePreview!.Points.Add(position);
    }

    private void CompleteSignature(PointerRoutedEventArgs e)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        _canvas.Children.Remove(_signaturePreview!);
        _signaturePreview = null;

        if (_signaturePoints.Count >= 2)
        {
            double minX = _signaturePoints.Min(point => point.X);
            double minY = _signaturePoints.Min(point => point.Y);
            double maxX = _signaturePoints.Max(point => point.X);
            double maxY = _signaturePoints.Max(point => point.Y);
            var signature = new PdfAnnotation
            {
                Type = AnnotationType.Signature,
                PageIndex = _getPageIndex(),
                X = minX,
                Y = minY,
                Width = Math.Max(maxX - minX, 1),
                Height = Math.Max(maxY - minY, 1),
                Color = _getSignatureColor(),
                LineWidth = _getSignatureLineWidth(),
                SignaturePoints = _signaturePoints.Select(point => point.Clone()).ToList()
            };
            _getAnnotations().Add(signature);
            _getManager().MarkModified();
            _statusText.Text = "서명이 추가되었습니다 (저장 시 반영)";
            Render();
        }
        else
        {
            _statusText.Text = "서명이 너무 짧습니다.";
        }

        _signaturePoints.Clear();
    }

    private async Task PickHighlightColorAsync(PointerRoutedEventArgs e)
    {
        _statusText.Text = "색상 추출 중...";
        Windows.UI.Color? pickedColor = await _screenColorPickerService.PickAsync(
            _pageImage, e.GetCurrentPoint(_pageImage).Position);
        if (!pickedColor.HasValue)
            return;

        _applyPickedHighlightColor(pickedColor.Value);
        _statusText.Text = $"색상이 추출되었습니다: {pickedColor.Value}";
        _setToolMode(EditToolMode.Highlight);
    }

    private async Task CompleteResizeAsync(
        PointerRoutedEventArgs e,
        PdfDocumentManager manager,
        AnnotationResizeResult result)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        if (result == AnnotationResizeResult.OriginalTextRemovalFailed)
        {
            _statusText.Text = "이 PDF의 텍스트는 배경을 보존한 상태로 크기를 조정할 수 없습니다.";
            Render();
            return;
        }
        if (result == AnnotationResizeResult.OriginalImageRemovalFailed)
        {
            _statusText.Text = "이 PDF의 이미지는 원본을 보존한 상태로 크기를 조정할 수 없습니다.";
            Render();
            return;
        }

        manager.MarkModified();
        _statusText.Text = "크기 조정됨 (저장 시 반영)";
        if (!IsInlineEditing)
            await _renderCurrentPageAsync();
        Render();
    }

    private async Task CompleteMoveAsync(
        PointerRoutedEventArgs e,
        PdfDocumentManager manager,
        int pageIndex)
    {
        ++_movePreviewVersion;
        _canvas.ReleasePointerCapture(e.Pointer);
        AnnotationMoveResult result = await _interactionController.CompleteMoveAsync(
            manager, pageIndex, SelectedAnnotations);
        string? failureMessage = result switch
        {
            AnnotationMoveResult.OriginalTextRemovalFailed =>
                "이 PDF의 텍스트는 배경을 보존한 상태로 이동할 수 없습니다.",
            AnnotationMoveResult.OriginalImageRemovalFailed =>
                "이 PDF의 이미지는 원본을 보존한 상태로 이동할 수 없습니다.",
            AnnotationMoveResult.MixedOriginalContentRemovalFailed =>
                "원본 텍스트와 이미지는 한 번에 함께 이동할 수 없습니다.",
            _ => null
        };

        if (result == AnnotationMoveResult.NotMoved || failureMessage != null)
        {
            CancelNativeMovePreview();
            if (failureMessage != null)
                _statusText.Text = failureMessage;
            Render();
            return;
        }

        manager.MarkModified();
        _moveOriginalImage = null;
        if (SelectedAnnotations.Any(a => a.NativeText != null))
            await RefreshNativeSelectionAsync(pageIndex);
        _invalidateRenderPath();
        _statusText.Text = "위치 이동됨 (저장 시 반영)";
        if (!IsInlineEditing)
        {
            await _renderCurrentPageAsync();
            Render();
        }
    }

    private void CancelNativeMovePreview()
    {
        ++_movePreviewVersion;
        if (_moveOriginalImage != null) _pageImage.Source = _moveOriginalImage;
        _moveOriginalImage = null;
    }

    private async Task PreviewNativeMoveAsync(int version)
    {
        _moveOriginalImage ??= _pageImage.Source;
        await Task.Delay(25);
        if (version != _movePreviewVersion) return;
        await _movePreviewGate.WaitAsync();
        try
        {
            if (version != _movePreviewVersion || !_interactionController.IsMoving) return;
            byte[]? bytes = _getManager().GetPdfBytes();
            if (bytes == null) return;
            int pageIndex = _getPageIndex();
            var edits = SelectedAnnotations.Where(a => a.NativeText != null).Select(a =>
                new NativePdfTextEdit(a.NativeText!, a.NativeText!.Text, a.X - a.NativeText.Bounds.X, a.Y - a.NativeText.Bounds.Y)).ToList();
            var result = await Task.Run(() => new NativePdfTextService().EditMany(bytes, pageIndex, edits));
            if (version != _movePreviewVersion) return;
            using var input = new MemoryStream(result.Bytes);
            using var rendered = await PdfRenderHelper.RenderPageWithWindowsPdfStreamAsync(input.AsRandomAccessStream(), pageIndex, 2);
            if (rendered == null || version != _movePreviewVersion) return;
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(rendered.AsRandomAccessStream());
            if (version == _movePreviewVersion && _interactionController.IsMoving) _pageImage.Source = bitmap;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { _movePreviewGate.Release(); }
    }

    private async Task RefreshNativeSelectionAsync(int pageIndex)
    {
        var native = SelectedAnnotations.Where(a => a.NativeText != null).ToList();
        var contents = await _getManager().ExtractPageContentsAsync(pageIndex);
        foreach (var annotation in native)
        {
            var found = contents.Where(c => c.NativeText != null).OrderBy(c =>
                Math.Pow(c.X - annotation.X, 2) + Math.Pow(c.Y - annotation.Y, 2)).FirstOrDefault();
            if (found?.NativeText == null) continue;
            annotation.NativeText = found.NativeText; annotation.Content = found.NativeText.Text;
            annotation.X = found.X; annotation.Y = found.Y; annotation.Width = found.Width; annotation.Height = found.Height;
            annotation.IsOriginalTextReplacement = true; annotation.IsApplied = false;
        }
        _getAnnotations().RemoveAll(a => a.PageIndex == pageIndex && a.NativeText != null && !native.Contains(a));
    }

    private void EditAnnotationContent(PdfAnnotation annotation)
    {
        if (annotation.Type is not (AnnotationType.Text or AnnotationType.FreeText) ||
            !_inlineTextEditorController.Reserve())
        {
            return;
        }

        // Editing a text object also makes it the primary selection so the
        // toolbar can show the font metadata extracted from the PDF.
        SelectOnly(annotation);
        _dispatcherQueue.TryEnqueue(() =>
            AddInlineTextBox(annotation.X, annotation.Y, annotation));
    }

    private void AddInlineTextBox(
        double pdfX,
        double pdfY,
        PdfAnnotation? existingAnnotation = null)
    {
        _inlineTextEditorController.TryStart(
            _canvas,
            pdfX,
            pdfY,
            _fontSettings,
            existingAnnotation,
            _getManager(),
            _getAnnotations(),
            SelectedAnnotations,
            _getPageIndex(),
            _contentService.BuildEditableText,
            async () =>
            {
                _invalidateRenderPath();
                await _renderCurrentPageAsync();
            },
            Render,
            status => _statusText.Text = status,
            removed =>
            {
                if (PrimarySelection == removed)
                    PrimarySelection = null;
            },
            cancelled => _dispatcherQueue.TryEnqueue(() =>
                _focusAfterInlineEdit(cancelled)),
            _pageImage, _pendingInlineCaret);
        _pendingInlineCaret = null;
    }

    private static void SetElementCursor(UIElement element, InputCursor cursor)
    {
        try
        {
            PropertyInfo? property = typeof(UIElement).GetProperty(
                "ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic);
            property?.SetValue(element, cursor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting cursor: {ex.Message}");
        }
    }
}
