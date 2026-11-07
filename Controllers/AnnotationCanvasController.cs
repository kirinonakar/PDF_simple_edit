using Loc = PDF_simple_edit.Services.LocalizationService;
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
    private readonly Func<Task> _renderCurrentPageAsync;
    private readonly Action<bool> _focusAfterInlineEdit;

    private readonly AnnotationContentService _contentService = new();
    private readonly AnnotationOverlayController _overlayController = new();
    private readonly ScreenColorPickerService _screenColorPickerService = new();
    private readonly AnnotationInteractionController _interactionController = new();
    private readonly InlineTextEditorController _inlineTextEditorController = new();
    private readonly AnnotationSelectionController _selectionController;
    private readonly DrawingInteractionController _drawingController;

    private uint? _pendingSelectPointerId;
    private long _selectPointerPressSequence;
    private readonly List<PdfPathPoint> _signaturePoints = new();
    private Polyline? _signaturePreview;
    private int _movePreviewVersion;
    private ImageSource? _moveOriginalImage;
    private readonly SemaphoreSlim _movePreviewGate = new(1, 1);
    private PdfTextPoint? _pendingInlineCaret;
    private Point? _regionStart;
    private Rectangle? _regionPreview;
    private (PdfDocumentManager Manager, int Page, long Version, uint PointerId)? _redactionGesture;
    private (PdfDocumentManager Manager, int Page, long Version, uint PointerId, EditToolMode Mode, Point Start,
        string Color, double Opacity, double FontSize)? _commentGesture;
    private Rectangle? _commentPreview;
    private Task<List<NativePdfTextBlock>>? _commentTextSource;
    private Canvas? _commentTextPreview;
    private sealed record CommentTransform(PdfDocumentManager Manager, int Page, long Version, PdfAnnotation Target,
        PdfAnnotation Original, Point Start, string Direction);
    private CommentTransform? _commentTransform;

    public string CommentColor { get; set; } = "#FFFF00";
    public double CommentOpacity { get; set; } = 1;
    public double CommentFontSize { get; set; } = 12;
    public Func<bool, Task<string?>>? RequestCommentText { get; set; }
    public Action<PdfAnnotation>? ToggleNoteRequested { get; set; }
    public Action<PdfAnnotation>? EditCommentRequested { get; set; }

    public Func<PdfTextBox, Task>? RegionRedactionRequested { get; set; }

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
        Func<string> getDrawingColor,
        Func<double> getDrawingLineWidth,
        Func<bool> getDrawingFill,
        Action<Windows.UI.Color> applyPickedHighlightColor,
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
        _renderCurrentPageAsync = renderCurrentPageAsync;
        _focusAfterInlineEdit = focusAfterInlineEdit;
        _selectionController = new AnnotationSelectionController(_contentService);
        _drawingController = new DrawingInteractionController(canvas, statusText, getManager, getPageIndex,
            getToolMode, getDrawingColor, getDrawingLineWidth, getDrawingFill, annotation =>
            {
                _getAnnotations().Add(annotation);
                _getManager().MarkModified(pageIndex: annotation.PageIndex);
                Render();
            });
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

    public void RefreshSelectionControls() => PrimarySelectionChanged?.Invoke(PrimarySelection);

    public bool IsInlineEditing => _inlineTextEditorController.IsEditing;

    public Task FinishActiveInlineEditAsync() =>
        _inlineTextEditorController.FinishActiveEditAsync();

    public void ClearSelection()
    {
        PrimarySelection = null;
        SelectedAnnotations.Clear();
    }

    public void CancelSelectionGesture()
    {
        if (_commentTransform is { } transform)
            SetCommentBounds(transform.Target, new(transform.Original.X, transform.Original.Y, transform.Original.Width, transform.Original.Height));
        _commentTransform = null;
        _commentTextSource = null;
        if (_commentTextPreview != null) _canvas.Children.Remove(_commentTextPreview);
        _commentTextPreview = null;
        _commentGesture = null;
        if (_commentPreview != null) _canvas.Children.Remove(_commentPreview);
        _commentPreview = null;
        _drawingController.Cancel();
        if (_signaturePreview != null) _canvas.Children.Remove(_signaturePreview);
        _signaturePreview = null;
        _signaturePoints.Clear();
        _selectPointerPressSequence++;
        _pendingSelectPointerId = null;
        _regionStart = null;
        _redactionGesture = null;
        if (_regionPreview != null) _canvas.Children.Remove(_regionPreview);
        _regionPreview = null;
        bool cancelled = _interactionController.CancelMove();
        cancelled |= _interactionController.CancelResize();
        if (cancelled) CancelNativeMovePreview();
        _canvas.ReleasePointerCaptures();
    }

    public void CancelRegionGesture()
    {
        if (_regionStart != null || _commentGesture != null || _commentTransform != null) CancelSelectionGesture();
    }

    private Point ClipRegionPoint(Point point) => new(
        Math.Clamp(point.X, 0, _canvas.Width), Math.Clamp(point.Y, 0, _canvas.Height));

    public void SelectOnly(PdfAnnotation annotation)
    {
        SelectedAnnotations.Clear();
        SelectedAnnotations.Add(annotation);
        PrimarySelection = annotation;
    }

    public async Task PointerPressedAsync(PointerRoutedEventArgs e)
    {
        PdfDocumentManager manager = _getManager();
        manager.TextEditingMode = _fontSettings.TextEditingMode;
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
        if (toolMode == EditToolMode.SelectAnnotations) selectPressSequence = ++_selectPointerPressSequence;

        if (toolMode is EditToolMode.TextHighlight or EditToolMode.TextUnderline or EditToolMode.StickyNote or EditToolMode.CommentTextBox)
        {
            if (!isLeftButton) return;
            CancelSelectionGesture(); ClearSelection(); Render();
            _focusAfterInlineEdit(false);
            _commentGesture = (manager, _getPageIndex(), manager.EditVersion, e.Pointer.PointerId, toolMode,
                ClipRegionPoint(position), CommentColor, CommentOpacity, CommentFontSize);
            _canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            if (toolMode is EditToolMode.TextHighlight or EditToolMode.TextUnderline)
            {
                byte[] source = manager.GetPdfBytes()!;
                int page = _getPageIndex();
                var task = _commentTextSource = Task.Run(() => new NativePdfTextService().Extract(source, page));
                try
                {
                    var blocks = await task;
                    if (task == _commentTextSource && _commentGesture is { } gesture && manager == _getManager() && page == _getPageIndex())
                        DrawCommentTextSelection(blocks, gesture.Start, e.GetCurrentPoint(_canvas).Position);
                }
                catch (Exception error) { if (task == _commentTextSource) { CancelRegionGesture(); _statusText.Text = error.Message; } }
            }
            return;
        }

        if (PdfDrawingGeometry.IsDrawingTool(toolMode))
        {
            _drawingController.Pressed(e);
            return;
        }

        if (!isLeftButton && toolMode is EditToolMode.Select or EditToolMode.SelectGraphics or EditToolMode.SelectAnnotations or EditToolMode.Signature or EditToolMode.RedactRegion)
            return;

        if (isLeftButton && toolMode is EditToolMode.Select or EditToolMode.SelectGraphics)
        {
            if (_interactionController.CancelMove())
                _canvas.ReleasePointerCapture(e.Pointer);
            _pendingSelectPointerId = e.Pointer.PointerId;
            selectPressSequence = ++_selectPointerPressSequence;
        }

        switch (toolMode)
        {
            case EditToolMode.SelectAnnotations:
                await BeginSelectionAsync(e, position, pdfX, pdfY, selectPressSequence);
                _focusAfterInlineEdit(false);
                if (PrimarySelection != null) _statusText.Text = Loc.Get("AnnotationSelectedDeleteToRemove");
                e.Handled = true;
                break;
            case EditToolMode.RedactRegion:
                CancelSelectionGesture();
                ClearSelection();
                Render();
                _focusAfterInlineEdit(false);
                _regionStart = ClipRegionPoint(position);
                _redactionGesture = (manager, _getPageIndex(), manager.EditVersion, e.Pointer.PointerId);
                _canvas.CapturePointer(e.Pointer);
                _statusText.Text = Loc.Get("DragARegionToRedactReleaseToRemoveContent");
                e.Handled = true;
                break;
            case EditToolMode.Select:
            case EditToolMode.SelectGraphics:
                bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var hit = _contentService.FindAnnotationAt(_getAnnotations(), _getPageIndex(), pdfX, pdfY, toolMode);
                if (hit?.IsOriginalImageReplacement == true && !alt)
                {
                    int page = _getPageIndex();
                    var contents = await manager.ExtractPageContentsAsync(page,
                        textOnly: toolMode != EditToolMode.SelectGraphics);
                    if (selectPressSequence != _selectPointerPressSequence ||
                        manager != _getManager() || page != _getPageIndex()) return;
                    if (_contentService.ShouldPreferPageContent(hit,
                        _contentService.FindEditableContent(contents, pdfX, pdfY, toolMode))) hit = null;
                    if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
                    {
                        await BeginSelectionAsync(e, position, pdfX, pdfY, selectPressSequence);
                        return;
                    }
                }
                if ((toolMode == EditToolMode.SelectGraphics || _fontSettings.TextEditingMode == TextEditingMode.PreserveOriginal) && !alt &&
                    !(e.OriginalSource is Rectangle { Tag: string }) && (toolMode == EditToolMode.SelectGraphics || hit == null || hit.NativeText != null))
                {
                    _regionStart = position;
                    _canvas.CapturePointer(e.Pointer);
                    return;
                }
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
        if (_commentTransform is { } transform)
        {
            var point = e.GetCurrentPoint(_canvas);
            if (transform.Manager != _getManager() || transform.Page != _getPageIndex() || !point.Properties.IsLeftButtonPressed)
            { CancelSelectionGesture(); Render(); return; }
            double dx = (point.Position.X - transform.Start.X) / PdfToPixels, dy = (point.Position.Y - transform.Start.Y) / PdfToPixels;
            var original = transform.Original;
            double left = original.X, top = original.Y, right = left + original.Width, bottom = top + original.Height;
            double pageWidth = _canvas.Width / PdfToPixels, pageHeight = _canvas.Height / PdfToPixels;
            if (transform.Direction == "Move")
            {
                left = Math.Clamp(left + dx, 0, Math.Max(0, pageWidth - original.Width));
                top = Math.Clamp(top + dy, 0, Math.Max(0, pageHeight - original.Height));
                right = left + original.Width; bottom = top + original.Height;
            }
            else
            {
                if (transform.Direction.Contains('W')) left = Math.Clamp(left + dx, 0, Math.Max(0, right - 30));
                if (transform.Direction.Contains('E')) right = Math.Clamp(right + dx, Math.Min(pageWidth, left + 30), pageWidth);
                if (transform.Direction.Contains('N')) top = Math.Clamp(top + dy, 0, Math.Max(0, bottom - 20));
                if (transform.Direction.Contains('S')) bottom = Math.Clamp(bottom + dy, Math.Min(pageHeight, top + 20), pageHeight);
            }
            SetCommentBounds(transform.Target, new(left, top, right - left, bottom - top)); Render(); e.Handled = true; return;
        }
        if (_commentGesture is { } comment)
        {
            if (comment.PointerId != e.Pointer.PointerId) return;
            var point = e.GetCurrentPoint(_canvas);
            if (!point.Properties.IsLeftButtonPressed) { CancelRegionGesture(); return; }
            var end = ClipRegionPoint(point.Position);
            if (comment.Mode is EditToolMode.TextHighlight or EditToolMode.TextUnderline)
            {
                if (_commentTextSource?.IsCompletedSuccessfully == true)
                    DrawCommentTextSelection(_commentTextSource.Result, comment.Start, end);
                return;
            }
            if (_commentPreview == null)
            {
                _commentPreview = new Rectangle { Stroke = new SolidColorBrush(EditorColorService.Parse(comment.Color)),
                    StrokeThickness = 1, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 80, 150, 240)), IsHitTestVisible = false };
                _canvas.Children.Add(_commentPreview);
            }
            Canvas.SetLeft(_commentPreview, Math.Min(comment.Start.X, end.X));
            Canvas.SetTop(_commentPreview, Math.Min(comment.Start.Y, end.Y));
            _commentPreview.Width = Math.Abs(end.X - comment.Start.X);
            _commentPreview.Height = Math.Abs(end.Y - comment.Start.Y);
            return;
        }
        if (PdfDrawingGeometry.IsDrawingTool(_getToolMode()))
        {
            _drawingController.Moved(e);
            return;
        }
        var pointerPoint = e.GetCurrentPoint(_canvas);

        if (_regionStart is Point start)
        {
            if (_redactionGesture is { } gesture && gesture.PointerId != e.Pointer.PointerId) return;
            if (!pointerPoint.Properties.IsLeftButtonPressed) return;
            var end = _redactionGesture.HasValue ? ClipRegionPoint(pointerPoint.Position) : pointerPoint.Position;
            if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 4) return;
            if (_regionPreview == null)
            {
                bool redacting = _redactionGesture.HasValue;
                _regionPreview = new Rectangle { Stroke = new SolidColorBrush(redacting ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.DodgerBlue),
                    StrokeThickness = 1, Fill = new SolidColorBrush(redacting ? Windows.UI.Color.FromArgb(45, 255, 0, 0) : Windows.UI.Color.FromArgb(30, 30, 120, 255)), IsHitTestVisible = false };
                _canvas.Children.Add(_regionPreview);
            }
            Canvas.SetLeft(_regionPreview, Math.Min(start.X, end.X)); Canvas.SetTop(_regionPreview, Math.Min(start.Y, end.Y));
            _regionPreview.Width = Math.Abs(end.X - start.X); _regionPreview.Height = Math.Abs(end.Y - start.Y);
            return;
        }

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
                _statusText.Text = Loc.Get("ObjectSelectedDragToMove");
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
            if ((_interactionController.IsMoving || _interactionController.IsResizing) &&
                (SelectedAnnotations.Any(a => a.NativeText != null || a.IsOriginalImageReplacement) ||
                    (_interactionController.IsResizing && _interactionController.IsReflowingTextResize &&
                        _interactionController.GetTransformPreview().Any(e => TextResizeService.IsPdfText(e.Source)))))
                _ = PreviewNativeMoveAsync(++_movePreviewVersion);
        }
    }

    public async Task PointerReleasedAsync(PointerRoutedEventArgs e)
    {
        if (_commentTransform is { } transform)
        {
            _commentTransform = null; _canvas.ReleasePointerCapture(e.Pointer); e.Handled = true;
            var target = transform.Target;
            var bounds = new PdfTextBox(target.X, target.Y, target.Width, target.Height);
            if (transform.Manager != _getManager() || transform.Page != _getPageIndex() || transform.Version != transform.Manager.EditVersion)
            { SetCommentBounds(target, new(transform.Original.X, transform.Original.Y, transform.Original.Width, transform.Original.Height)); Render(); return; }
            if (Math.Abs(target.X - transform.Original.X) + Math.Abs(target.Y - transform.Original.Y) +
                Math.Abs(target.Width - transform.Original.Width) + Math.Abs(target.Height - transform.Original.Height) < .2) return;
            try
            {
                await transform.Manager.UpdatePdfCommentAsync(transform.Original, bounds);
                if (transform.Manager == _getManager() && transform.Page == _getPageIndex()) await _renderCurrentPageAsync();
            }
            catch (Exception error)
            {
                SetCommentBounds(target, new(transform.Original.X, transform.Original.Y, transform.Original.Width, transform.Original.Height));
                if (transform.Manager == _getManager()) { _statusText.Text = error.Message; Render(); }
            }
            return;
        }
        if (_commentGesture is { } comment)
        {
            if (comment.PointerId != e.Pointer.PointerId) return;
            var end = ClipRegionPoint(e.GetCurrentPoint(_canvas).Position);
            var textSource = _commentTextSource;
            CancelRegionGesture();
            e.Handled = true;
            bool IsCurrent() => comment.Manager == _getManager() && comment.Page == _getPageIndex() &&
                comment.Version == comment.Manager.EditVersion && comment.Mode == _getToolMode();
            if (!IsCurrent()) return;
            var box = new PdfTextBox(Math.Min(comment.Start.X, end.X) / PdfToPixels, Math.Min(comment.Start.Y, end.Y) / PdfToPixels,
                Math.Abs(end.X - comment.Start.X) / PdfToPixels, Math.Abs(end.Y - comment.Start.Y) / PdfToPixels);
            try
            {
                if (comment.Mode is EditToolMode.TextHighlight or EditToolMode.TextUnderline)
                {
                    if (Math.Abs(end.X - comment.Start.X) + Math.Abs(end.Y - comment.Start.Y) < 2) return;
                    if (textSource != null) await textSource;
                    if (!IsCurrent()) return;
                    bool added = await comment.Manager.AddTextMarkupRangeAsync(comment.Page,
                        new(comment.Start.X / PdfToPixels, comment.Start.Y / PdfToPixels), new(end.X / PdfToPixels, end.Y / PdfToPixels),
                        comment.Mode == EditToolMode.TextUnderline, comment.Color, comment.Opacity);
                    if (comment.Manager != _getManager() || comment.Page != _getPageIndex()) return;
                    _statusText.Text = Loc.Get(added ? "AnnotationAdded" : "NoTextInTheSelectedRegion");
                }
                else
                {
                    string? text = RequestCommentText == null ? null : await RequestCommentText(comment.Mode == EditToolMode.StickyNote);
                    if (string.IsNullOrWhiteSpace(text) || !IsCurrent()) return;
                    bool sticky = comment.Mode == EditToolMode.StickyNote;
                    var size = comment.Manager.GetPageSize(comment.Page);
                    double width = sticky ? 20 : Math.Max(80, box.Width), height = sticky ? 20 : Math.Max(40, box.Height);
                    width = Math.Min(width, size.width); height = Math.Min(height, size.height);
                    box = new(Math.Clamp(box.X, 0, size.width - width), Math.Clamp(box.Y, 0, size.height - height), width, height);
                    await comment.Manager.AddNoteOrTextBoxAsync(comment.Page, box, sticky, text, comment.Color, comment.Opacity, comment.FontSize);
                    if (comment.Manager != _getManager() || comment.Page != _getPageIndex()) return;
                    _statusText.Text = Loc.Get("AnnotationAdded");
                }
                await _renderCurrentPageAsync();
            }
            catch (Exception error) { if (comment.Manager == _getManager()) _statusText.Text = error.Message; }
            return;
        }
        if (PdfDrawingGeometry.IsDrawingTool(_getToolMode()))
        {
            _drawingController.Released(e);
            return;
        }
        if (_regionStart is Point start)
        {
            var redaction = _redactionGesture;
            if (redaction.HasValue && redaction.Value.PointerId != e.Pointer.PointerId) return;
            _regionStart = null;
            _redactionGesture = null;
            var end = e.GetCurrentPoint(_canvas).Position;
            if (redaction.HasValue) end = ClipRegionPoint(end);
            if (_regionPreview != null) _canvas.Children.Remove(_regionPreview);
            _regionPreview = null;
            _canvas.ReleasePointerCapture(e.Pointer);
            _pendingSelectPointerId = null;
            long sequence = ++_selectPointerPressSequence;
            if (redaction is { } gesture)
            {
                e.Handled = true;
                if (_getToolMode() != EditToolMode.RedactRegion || gesture.Manager != _getManager() ||
                    gesture.Page != _getPageIndex() || gesture.Version != gesture.Manager.EditVersion) return;
                if (Math.Abs(end.X - start.X) < 3 || Math.Abs(end.Y - start.Y) < 3)
                {
                    _statusText.Text = Loc.Get("DragARectangleAroundTheRegionToRedact");
                    return;
                }
                if (RegionRedactionRequested != null)
                    await RegionRedactionRequested(new PdfTextBox(Math.Min(start.X, end.X) / PdfToPixels,
                        Math.Min(start.Y, end.Y) / PdfToPixels, Math.Abs(end.X - start.X) / PdfToPixels,
                        Math.Abs(end.Y - start.Y) / PdfToPixels));
                return;
            }
            if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 4)
                await BeginSelectionAsync(e, end, end.X / PdfToPixels, end.Y / PdfToPixels, sequence);
            else
            {
                var regionManager = _getManager(); int page = _getPageIndex();
                EditToolMode selectionMode = _getToolMode();
                var contents = await regionManager.ExtractPageContentsAsync(page,
                    textOnly: selectionMode != EditToolMode.SelectGraphics);
                if (sequence != _selectPointerPressSequence || regionManager != _getManager() || page != _getPageIndex() || selectionMode != _getToolMode()) return;
                var region = new PdfTextBox(Math.Min(start.X, end.X) / PdfToPixels, Math.Min(start.Y, end.Y) / PdfToPixels,
                    Math.Abs(end.X - start.X) / PdfToPixels, Math.Abs(end.Y - start.Y) / PdfToPixels);
                if (selectionMode == EditToolMode.SelectGraphics)
                {
                    SelectGraphicsInRegion(contents, region, page);
                    Render();
                    return;
                }
                var blocks = NativePdfTextService.SelectRegion(contents.Where(c => c.NativeText != null).Select(c => c.NativeText!), region);
                _getAnnotations().RemoveAll(a => a.PageIndex == page && a.NativeText != null);
                ClearSelection();
                foreach (var block in blocks)
                {
                    var annotation = _contentService.ConvertToAnnotation(NativePdfTextService.ToPageContent(block), page);
                    if (annotation == null) continue;
                    _getAnnotations().Add(annotation); SelectedAnnotations.Add(annotation); PrimarySelection ??= annotation;
                }
                _statusText.Text = blocks.Count > 0 ? Loc.Get("TextRegionSelectedDoubleClickEditAltDragMove") : Loc.Get("NoTextInTheSelectedRegion");
                Render();
            }
            return;
        }
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
                manager.MarkModified(pageIndex: _getPageIndex());
                _statusText.Text = Loc.Get("TextHighlightAddedAppliedWhenSaved");
                Render();
            }
        }
    }

    private void DrawCommentTextSelection(List<NativePdfTextBlock> blocks, Point start, Point end)
    {
        if (_commentTextPreview != null) _canvas.Children.Remove(_commentTextPreview);
        var selected = PdfTextSelectionService.SelectRange(blocks, new(start.X / PdfToPixels, start.Y / PdfToPixels), new(end.X / PdfToPixels, end.Y / PdfToPixels));
        PdfCommentService.SelectText(selected, new(-1e8, -1e8, 2e8, 2e8), out _, out var quads);
        _commentTextPreview = new Canvas { IsHitTestVisible = false };
        foreach (var quad in quads)
        {
            var polygon = new Polygon { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 120, 215)), IsHitTestVisible = false };
            foreach (int index in new[] { 0, 1, 3, 2 }) polygon.Points.Add(new(quad[index].X * PdfToPixels, quad[index].Y * PdfToPixels));
            _commentTextPreview.Children.Add(polygon);
        }
        _canvas.Children.Add(_commentTextPreview);
    }

    public void UpdateToolCursor(EditToolMode mode) => SetElementCursor(_canvas, InputSystemCursor.Create(
        mode is EditToolMode.TextHighlight or EditToolMode.TextUnderline ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Arrow));

    public void DoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        if (_getToolMode() == EditToolMode.SelectAnnotations)
        {
            if (PrimarySelection is { PdfCommentSubtype: "Text" } note) ToggleNoteRequested?.Invoke(note);
            else if (PrimarySelection is { PdfCommentSubtype: "FreeText" } textBox) EditCommentRequested?.Invoke(textBox);
            e.Handled = true;
            return;
        }
        if (PdfDrawingGeometry.IsDrawingTool(_getToolMode()))
        {
            _drawingController.DoubleTapped(e);
            return;
        }
        if (IsInlineEditing || _getToolMode() is EditToolMode.SelectGraphics or EditToolMode.RedactRegion or
            EditToolMode.TextHighlight or EditToolMode.TextUnderline or EditToolMode.StickyNote or EditToolMode.CommentTextBox)
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
                if (PrimarySelection is { Type: AnnotationType.PdfComment } comment)
                    StartCommentTransform(comment, direction, position);
                else if (PrimarySelection != null)
                    _interactionController.BeginResize(
                        PrimarySelection, direction, position, _fontSettings.AdjustTextWidth);
            },
            shape => SetElementCursor(
                _canvas,
                InputSystemCursor.Create(shape)),
            note => ToggleNoteRequested?.Invoke(note));
        if (_commentPreview != null && !_canvas.Children.Contains(_commentPreview)) _canvas.Children.Add(_commentPreview);
        if (_commentTextPreview != null && !_canvas.Children.Contains(_commentTextPreview)) _canvas.Children.Add(_commentTextPreview);
        _drawingController.Redraw();
    }

    public bool HandleDrawingKey(Windows.System.VirtualKey key)
    {
        if (key == Windows.System.VirtualKey.Escape && (_commentGesture != null || _commentTransform != null))
        { CancelRegionGesture(); Render(); return true; }
        return _drawingController.HandleKey(key);
    }
    public void CancelDrawing() => _drawingController.Cancel();

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
            if (PrimarySelection is { Type: AnnotationType.PdfComment } comment)
                StartCommentTransform(comment, direction, position);
            else if (PrimarySelection != null)
                _interactionController.BeginResize(PrimarySelection, direction, position, _fontSettings.AdjustTextWidth);
            _canvas.CapturePointer(e.Pointer);
            _statusText.Text = Loc.Get("Resizing");
            return;
        }

        bool controlPressed = InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var selectionManager = _getManager();
        int selectionPage = _getPageIndex();
        var selectionMode = _getToolMode();
        bool IsCurrent() => selectPressSequence == _selectPointerPressSequence && selectionManager == _getManager() &&
            selectionPage == _getPageIndex() && selectionMode == _getToolMode();
        AnnotationSelectionResult selection = await _selectionController.SelectAtAsync(
            _getManager(),
            _getAnnotations(),
            SelectedAnnotations,
            PrimarySelection,
            _getPageIndex(),
            pdfX,
            pdfY,
            controlPressed,
            () => _statusText.Text = Loc.Get("AnalyzingPageContent"),
            selectionMode, IsCurrent);
        if (!IsCurrent()) return;

        PrimarySelection = selection.PrimarySelection;
        if (selection.AddedFromPageContent is PdfAnnotation added)
        {
            _statusText.Text = added.IsOriginalTextReplacement
                ? _fontSettings.TextEditingMode == TextEditingMode.Legacy
                    ? Loc.Get("ReplacementTextRegionSelectedDoubleClickEditDragMove")
                    : Loc.Get("TextRegionSelectedDoubleClickEditAltDragMove")
                : added.IsOriginalVectorGraphic
                    ? Loc.Get("GraphicsSelectedAltDragMoveHandlesResizeRotateDelete")
                    : Loc.Get("ImageSelectedDragToMoveOrUseTheHandles");
        }

        if (selection.SelectionChanged)
            Render();

        var currentPointerPoint = e.GetCurrentPoint(_canvas);
        PdfAnnotation? moveTarget = PrimarySelection;
        if (selectionMode == EditToolMode.SelectAnnotations && moveTarget?.PdfCommentSubtype is "Text" or "FreeText" && currentPointerPoint.Properties.IsLeftButtonPressed)
        {
            StartCommentTransform(moveTarget, "Move", position); _canvas.CapturePointer(e.Pointer); return;
        }
        bool canStartMove = moveTarget != null &&
            _getToolMode() is EditToolMode.Select or EditToolMode.SelectGraphics &&
            (_getToolMode() != EditToolMode.SelectGraphics ||
                InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) &&
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
                    ? Loc.Format("ObjectsSelected", SelectedAnnotations.Count)
                    : Loc.Get("ObjectSelectedDragToMove");
            }
        }
        else
        {
            if (_pendingSelectPointerId == e.Pointer.PointerId)
                _pendingSelectPointerId = null;
            if (PrimarySelection == null)
                _statusText.Text = Loc.Get("Ready");
        }
    }

    private void StartCommentTransform(PdfAnnotation annotation, string direction, Point position)
    {
        if (annotation.PageIndex != _getPageIndex() || annotation.PdfCommentSubtype is not ("Text" or "FreeText")) return;
        _commentTransform = new(_getManager(), _getPageIndex(), _getManager().EditVersion, annotation, annotation.Clone(), position, direction);
    }

    private static void SetCommentBounds(PdfAnnotation annotation, PdfTextBox box)
    {
        annotation.X = box.X; annotation.Y = box.Y; annotation.Width = box.Width; annotation.Height = box.Height;
        annotation.PdfCommentHitBounds = new() { box };
    }

    private void SelectGraphicsInRegion(List<PdfPageContent> contents, PdfTextBox region, int page)
    {
        bool controlPressed = KeyboardStateService.IsControlDown();
        var selected = _selectionController.SelectGraphicsInRegion(
            _getAnnotations(), contents, page, region);
        if (!controlPressed) ClearSelection();
        foreach (var annotation in selected)
            if (!SelectedAnnotations.Contains(annotation)) SelectedAnnotations.Add(annotation);
        PrimarySelection = SelectedAnnotations.LastOrDefault();
        _statusText.Text = SelectedAnnotations.Count > 0
            ? Loc.Format("GraphicsSelectedDeleteRemove", SelectedAnnotations.Count)
            : Loc.Get("NoGraphicsInTheRegionEncloseEntireObjectsTo");
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
        _statusText.Text = Loc.Get("DrawingSignature");
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
            _getManager().MarkModified(pageIndex: _getPageIndex());
            _statusText.Text = Loc.Get("SignatureAddedAppliedWhenSaved");
            Render();
        }
        else
        {
            _statusText.Text = Loc.Get("TheSignatureIsTooShort");
        }

        _signaturePoints.Clear();
    }

    private async Task PickHighlightColorAsync(PointerRoutedEventArgs e)
    {
        _statusText.Text = Loc.Get("PickingAColor");
        Windows.UI.Color? pickedColor = await _screenColorPickerService.PickAsync(
            _pageImage, e.GetCurrentPoint(_pageImage).Position);
        if (!pickedColor.HasValue)
            return;

        _applyPickedHighlightColor(pickedColor.Value);
        _statusText.Text = Loc.Format("ColorPicked", pickedColor.Value);
        _setToolMode(EditToolMode.Highlight);
    }

    private async Task CompleteResizeAsync(
        PointerRoutedEventArgs e,
        PdfDocumentManager manager,
        AnnotationResizeResult result)
    {
        ++_movePreviewVersion;
        _canvas.ReleasePointerCapture(e.Pointer);
        if (result == AnnotationResizeResult.OriginalTextRemovalFailed)
        {
            CancelNativeMovePreview();
            _statusText.Text = Loc.Get("TheTextInThisPDFCannotBeTransformedWhile");
            Render();
            return;
        }
        if (result == AnnotationResizeResult.OriginalImageRemovalFailed)
        {
            CancelNativeMovePreview();
            _statusText.Text = Loc.Get("TheGraphicsInThisPDFCannotBeTransformedWhile");
            Render();
            return;
        }

        manager.MarkModified(pageIndex: _getPageIndex());
        _moveOriginalImage = null;
        if (SelectedAnnotations.Any(a => a.NativeText != null))
            _getAnnotations().RemoveAll(a => a.PageIndex == _getPageIndex() && a.NativeText != null && !SelectedAnnotations.Contains(a));
        RefreshSelectionControls();
        _statusText.Text = Loc.Get("SizeRotationChanged");
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
                Loc.Get("TheTextInThisPDFCannotBeMovedWhile"),
            AnnotationMoveResult.OriginalImageRemovalFailed =>
                Loc.Get("TheImagesInThisPDFCannotBeMovedWhile"),
            AnnotationMoveResult.MixedOriginalContentRemovalFailed =>
                Loc.Get("OriginalTextAndImagesCannotBeMovedTogether"),
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

        manager.MarkModified(pageIndex: _getPageIndex());
        _moveOriginalImage = null;
        if (SelectedAnnotations.Any(a => a.NativeText != null))
            await RefreshNativeSelectionAsync(pageIndex);
        _statusText.Text = Loc.Get("PositionChangedAppliedWhenSaved");
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
            if (version != _movePreviewVersion || (!_interactionController.IsMoving && !_interactionController.IsResizing)) return;
            byte[]? bytes = _getManager().GetPdfBytes();
            if (bytes == null) return;
            int pageIndex = _getPageIndex();
            var requests = _interactionController.GetTransformPreview();
            var textRequests = requests.Where(e => TextResizeService.IsPdfText(e.Source)).ToList();
            bool reflow = _interactionController.IsResizing && _interactionController.IsReflowingTextResize;
            var graphicEdits = requests.Where(e => e.Source.IsOriginalImageReplacement).Select(e =>
                (e.Source, PdfAffineTransform.Between(new(e.Source.X, e.Source.Y, e.Source.Width, e.Source.Height),
                    new(e.Target.X, e.Target.Y, e.Target.Width, e.Target.Height), e.Target.Rotation - e.Source.Rotation))).ToList();
            byte[] previewBytes = await Task.Run(() =>
            {
                var service = new NativePdfTextService();
                var blocks = textRequests.Any(e => e.Source.NativeText == null) ? service.Extract(bytes, pageIndex) : new();
                var nativeEdits = textRequests.Select(e => TextResizeService.CreateEdit(
                    TextResizeService.ResolveBlock(e.Source, blocks), e.Source, e.Target, reflow)).ToList();
                byte[] result = nativeEdits.Count > 0 ? service.EditMany(bytes, pageIndex, nativeEdits).Bytes : bytes;
                return graphicEdits.Count > 0 ? PdfGraphicTransformService.Transform(result, pageIndex, graphicEdits) : result;
            });
            if (version != _movePreviewVersion) return;
            using var input = new MemoryStream(previewBytes);
            using var rendered = await PdfRenderHelper.RenderPageWithWindowsPdfStreamAsync(input.AsRandomAccessStream(), pageIndex, 2);
            if (rendered == null || version != _movePreviewVersion) return;
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(rendered.AsRandomAccessStream());
            if (version == _movePreviewVersion && (_interactionController.IsMoving || _interactionController.IsResizing)) _pageImage.Source = bitmap;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { _movePreviewGate.Release(); }
    }

    private async Task RefreshNativeSelectionAsync(int pageIndex)
    {
        var native = SelectedAnnotations.Where(a => a.NativeText != null).ToList();
        var contents = await _getManager().ExtractPageContentsAsync(pageIndex, textOnly: true);
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
