using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace PDF_simple_edit.Controllers;

public sealed class InlineTextEditSession
{
    public required PdfAnnotation Annotation { get; init; }
    public required string OriginalContent { get; init; }
    public bool HasLiveChanges { get; set; }
    public bool SuppressTextChanged { get; set; }
    public bool IsFinishing { get; set; }
    public long TextChangeVersion { get; set; }
    public string LastEditorText { get; set; } = string.Empty;
    public bool OriginalRemovalCommitted { get; set; }
    public Task<bool>? RemovalTask { get; set; }
    public bool WasApplied { get; init; }
    public bool WasOriginalTextReplacement { get; init; }
    public double OriginalWidth { get; init; }
    public int PreservedCharacterSpacing { get; init; }
    public Task<bool>? PreparationTask { get; set; }
    public Image? PageImage { get; set; }
    public ImageSource? OriginalPageImage { get; set; }
}

public sealed class InlineTextEditorController
{
    private const double PdfToPixels = 96.0 / 72.0;

    private Canvas? _canvas;
    private RichEditBox? _activeEditor;
    private PdfDocumentManager? _manager;
    private List<PdfAnnotation>? _annotations;
    private List<PdfAnnotation>? _selectedAnnotations;
    private TextFontSettings? _settings;
    private int _pageIndex;
    private Func<Task>? _renderPage;
    private Action? _renderOverlays;
    private Action<string>? _setStatus;
    private Action<PdfAnnotation>? _annotationRemoved;
    private Action<bool>? _restoreFocus;
    private Task? _finishingTask;
    private readonly NativeInlineTextEditorController _nativeEditor = new();
    private bool _isEditing;

    public bool IsEditing { get => _isEditing || _nativeEditor.IsEditing; private set => _isEditing = value; }

    public bool Reserve()
    {
        if (IsEditing)
            return false;

        IsEditing = true;
        return true;
    }

    public bool TryStart(
        Canvas canvas,
        double pdfX,
        double pdfY,
        TextFontSettings settings,
        PdfAnnotation? existingAnnotation,
        PdfDocumentManager manager,
        List<PdfAnnotation> annotations,
        List<PdfAnnotation> selectedAnnotations,
        int pageIndex,
        Func<PdfAnnotation, string> buildEditableText,
        Func<Task> renderPage,
        Action renderOverlays,
        Action<string> setStatus,
        Action<PdfAnnotation> annotationRemoved,
        Action<bool> restoreFocus,
        Image pageImage,
        PdfTextPoint? initialCaret = null)
    {
        if (existingAnnotation?.NativeText != null)
        {
            _isEditing = false;
            return _nativeEditor.Start(canvas, pageImage, existingAnnotation, manager, annotations,
                selectedAnnotations, pageIndex, renderPage, renderOverlays, setStatus, annotationRemoved, restoreFocus, initialCaret);
        }
        if (canvas.Children.OfType<RichEditBox>().Any())
        {
            IsEditing = _activeEditor != null;
            return false;
        }

        IsEditing = true;
        _canvas = canvas;
        _manager = manager;
        _annotations = annotations;
        _selectedAnnotations = selectedAnnotations;
        _settings = settings;
        _pageIndex = pageIndex;
        _renderPage = renderPage;
        _renderOverlays = renderOverlays;
        _setStatus = setStatus;
        _annotationRemoved = annotationRemoved;
        _restoreFocus = restoreFocus;

        string fontFamily = existingAnnotation?.FontFamily ?? settings.FontFamily;
        double fontSize = existingAnnotation?.FontSize ?? settings.FontSize;
        string color = existingAnnotation?.Color ?? settings.Color;
        bool isBold = existingAnnotation?.IsBold ?? settings.IsBold;
        bool isItalic = existingAnnotation?.IsItalic ?? settings.IsItalic;
        int fontWeight = existingAnnotation?.FontWeight ?? (isBold ? 700 : 400);
        string initialText = existingAnnotation != null
            ? buildEditableText(existingAnnotation)
            : string.Empty;
        double width = existingAnnotation != null
            ? Math.Max(existingAnnotation.Width * PdfToPixels,
                AnnotationTextLayoutService.GetRequiredTextBoxWidth(existingAnnotation, initialText,
                    AnnotationTextLayoutService.GetDisplayCharacterSpacing(existingAnnotation, initialText)) * PdfToPixels)
            : double.NaN;
        int characterSpacing = existingAnnotation != null
            ? AnnotationTextLayoutService.GetDisplayCharacterSpacing(existingAnnotation, initialText)
            : 0;
        double displayFontSize = existingAnnotation != null
            ? AnnotationTextLayoutService.GetDisplayFontSize(existingAnnotation, initialText)
            : fontSize;
        double displayLineHeight = existingAnnotation == null ? 0 :
            AnnotationTextLayoutService.GetDisplayLineHeight(existingAnnotation, initialText);
        double editorHeight = AnnotationTextLayoutService.GetInlineEditorHeight(
            initialText,
            fontFamily,
            displayFontSize,
            isBold,
            isItalic,
            fontWeight,
            displayLineHeight);
        double topOffset = existingAnnotation != null
            ? AnnotationTextLayoutService.GetTopOffset(existingAnnotation, displayFontSize)
            : 0;

        var editor = new RichEditBox
        {
            AcceptsReturn = existingAnnotation != null,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = existingAnnotation != null ? 0 : 60,
            MinHeight = 0,
            Width = width,
            Height = editorHeight,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            // The editing border is rendered as a separate overlay. Keeping the
            // editor borderless prevents its content presenter from shifting the
            // text by one pixel when edit mode starts.
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            FontSize = displayFontSize * PdfToPixels,
            CharacterSpacing = characterSpacing,
            FontFamily = new FontFamily(fontFamily),
            Foreground = new SolidColorBrush(EditorColorService.Parse(color)),
            FontWeight = AnnotationTextLayoutService.ResolveFontWeight(fontWeight, isBold),
            FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Tag = existingAnnotation != null
                ? new InlineTextEditSession
                {
                    Annotation = existingAnnotation,
                    OriginalContent = initialText,
                    LastEditorText = initialText,
                    WasApplied = existingAnnotation.IsApplied,
                    WasOriginalTextReplacement = existingAnnotation.IsOriginalTextReplacement,
                    OriginalWidth = existingAnnotation.Width,
                    PreservedCharacterSpacing = characterSpacing
                }
                : new Point(pdfX, pdfY),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Top,
            MaxWidth = 4000,
            UseLayoutRounding = false,
            IsSpellCheckEnabled = false
        };
        SetEditorText(editor, initialText);
        ApplyEditorLineSpacing(editor, displayLineHeight);
        Canvas.SetLeft(editor, pdfX * PdfToPixels);
        Canvas.SetTop(editor, pdfY * PdfToPixels + topOffset);

        editor.Loaded += async (_, _) =>
        {
            if (editor.Tag is InlineTextEditSession { PreparationTask: not null } session && !await session.PreparationTask)
            { await CancelAsync(editor); return; }
            if (_activeEditor == editor) editor.Focus(FocusState.Programmatic);
        };
        editor.PointerPressed += (_, args) => args.Handled = true;
        editor.PointerReleased += (_, args) => args.Handled = true;
        editor.DoubleTapped += (_, args) => args.Handled = true;
        if (existingAnnotation != null)
            editor.TextChanged += InlineEditor_TextChanged;
        // RichEditBox handles Enter during its normal KeyDown processing. Use
        // the preview event so Ctrl+Enter confirms before RichEdit inserts a
        // paragraph break into the replacement text.
        editor.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter &&
                (!editor.AcceptsReturn || KeyboardStateService.IsControlDown()))
            {
                args.Handled = true;
                await StartApplyAsync(editor);
            }
            else if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                await CancelAsync(editor);
            }
        };
        editor.LostFocus += async (_, _) => await StartApplyAsync(editor);
        bool prepareBackground = existingAnnotation?.IsOriginalTextReplacement == true || existingAnnotation?.IsApplied == true;
        if (prepareBackground) { editor.Opacity = 0; editor.IsReadOnly = true; }
        canvas.Children.Add(editor);
        _activeEditor = editor;
        if (prepareBackground && editor.Tag is InlineTextEditSession previewSession)
        {
            previewSession.PageImage = pageImage;
            previewSession.OriginalPageImage = pageImage.Source;
            previewSession.PreparationTask = PrepareBackgroundAsync(editor, previewSession, manager.GetPdfBytes()!, pageIndex);
        }
        _renderOverlays?.Invoke();
        return true;
    }

    private async Task<bool> PrepareBackgroundAsync(RichEditBox editor, InlineTextEditSession session, byte[] source, int pageIndex)
    {
        try
        {
            byte[] preview = await new LegacyTextPreviewService().CreateAsync(source, pageIndex, session.Annotation);
            using var stream = new MemoryStream(preview);
            using var rendered = await PdfRenderHelper.RenderPageWithWindowsPdfStreamAsync(stream.AsRandomAccessStream(), pageIndex, 2);
            if (rendered == null) throw new InvalidOperationException("편집 배경을 렌더링할 수 없습니다.");
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(rendered.AsRandomAccessStream());
            if (_activeEditor != editor || session.IsFinishing) return true;
            session.PageImage!.Source = bitmap;
            editor.Opacity = 1; editor.IsReadOnly = false;
            return true;
        }
        catch (Exception error) { _setStatus?.Invoke(error.Message); return false; }
    }

    public Task FinishActiveEditAsync()
    {
        if (_nativeEditor.IsEditing) return _nativeEditor.FinishAsync();
        if (_activeEditor != null)
            return StartApplyAsync(_activeEditor);

        return _finishingTask ?? Task.CompletedTask;
    }

    private Task StartApplyAsync(RichEditBox editor)
    {
        if (_finishingTask is { IsCompleted: false })
            return _finishingTask;

        Task task = ApplyAsync(editor);
        _finishingTask = AwaitAndClearFinishingTaskAsync(task);
        return _finishingTask;
    }

    private async Task AwaitAndClearFinishingTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _finishingTask = null;
        }
    }

    private async void InlineEditor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichEditBox editor ||
            editor.Tag is not InlineTextEditSession session ||
            session.SuppressTextChanged ||
            session.IsFinishing)
            return;

        string changedText = GetEditorText(editor);
        // RichEditBox raises TextChanged for document-format updates as well as
        // character edits. Ignore formatting-only notifications so entering edit
        // mode cannot recursively reapply paragraph formatting or remove the
        // original PDF text before the user changes anything.
        if (string.Equals(changedText, session.LastEditorText, StringComparison.Ordinal))
            return;

        session.LastEditorText = changedText;
        long changeVersion = ++session.TextChangeVersion;
        session.HasLiveChanges = true;

        double displayFontSize = AnnotationTextLayoutService.GetDisplayFontSize(
            session.Annotation,
            changedText);
        bool wasSuppressed = session.SuppressTextChanged;
        session.SuppressTextChanged = true;
        try
        {
            editor.FontSize = displayFontSize * PdfToPixels;
            editor.CharacterSpacing = session.PreservedCharacterSpacing;
            double requiredWidth = AnnotationTextLayoutService.GetRequiredTextBoxWidth(
                session.Annotation,
                changedText,
                session.PreservedCharacterSpacing) * PdfToPixels;
            editor.Width = Math.Min(
                editor.MaxWidth,
                Math.Max(session.OriginalWidth * PdfToPixels, requiredWidth));
            Canvas.SetTop(
                editor,
                session.Annotation.Y * PdfToPixels +
                AnnotationTextLayoutService.GetTopOffset(session.Annotation, displayFontSize));
            editor.Height = AnnotationTextLayoutService.GetInlineEditorHeight(
                changedText,
                session.Annotation.FontFamily,
                displayFontSize,
                session.Annotation.IsBold,
                session.Annotation.IsItalic,
                session.Annotation.FontWeight,
                AnnotationTextLayoutService.GetDisplayLineHeight(session.Annotation, changedText));
            ApplyEditorLineSpacing(editor, AnnotationTextLayoutService.GetDisplayLineHeight(session.Annotation, changedText));
        }
        finally
        {
            session.SuppressTextChanged = wasSuppressed;
        }
        _renderOverlays?.Invoke();

        if (session.RemovalTask == null &&
            (session.Annotation.IsOriginalTextReplacement || session.Annotation.IsApplied))
        {
            session.RemovalTask = session.Annotation.IsApplied
                ? RemoveAppliedTextForLiveEditAsync(session)
                : RemoveOriginalTextForLiveEditAsync(session);
            bool removed = await session.RemovalTask;
            if (!removed && _canvas?.Children.Contains(editor) == true)
            {
                session.SuppressTextChanged = true;
                session.Annotation.Content = session.OriginalContent;
                session.LastEditorText = session.OriginalContent;
                SetEditorText(editor, session.OriginalContent);
                editor.CharacterSpacing = session.PreservedCharacterSpacing;
                editor.Width = Math.Max(session.OriginalWidth * PdfToPixels, 1);
                editor.Height = AnnotationTextLayoutService.GetInlineEditorHeight(
                    session.OriginalContent,
                    session.Annotation.FontFamily,
                    session.Annotation.FontSize,
                    session.Annotation.IsBold,
                    session.Annotation.IsItalic,
                    session.Annotation.FontWeight,
                    AnnotationTextLayoutService.GetDisplayLineHeight(session.Annotation, session.OriginalContent));
                ApplyEditorLineSpacing(editor, AnnotationTextLayoutService.GetDisplayLineHeight(session.Annotation, session.OriginalContent));
                MoveCaretToEnd(editor);
                session.SuppressTextChanged = false;
                session.HasLiveChanges = false;
                _setStatus?.Invoke("배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.");
                return;
            }
        }

        if (session.RemovalTask != null && !await session.RemovalTask)
            return;

        if (session.IsFinishing || changeVersion != session.TextChangeVersion ||
            _canvas?.Children.Contains(editor) != true)
            return;

        session.Annotation.Content = changedText;
        session.Annotation.IsApplied = false;
        _setStatus?.Invoke(string.IsNullOrEmpty(changedText)
            ? "텍스트가 삭제되었습니다 (편집 중)"
            : "텍스트 편집 내용이 실시간 반영 중입니다.");
    }

    private async Task<bool> RemoveOriginalTextForLiveEditAsync(InlineTextEditSession session)
    {
        if (_manager == null)
            return false;

        bool removed = await _manager.RemoveOriginalTextAnnotationsAsync(
            _pageIndex, new[] { session.Annotation });
        if (!removed)
            return false;

        session.Annotation.IsOriginalTextReplacement = false;
        session.OriginalRemovalCommitted = true;
        if (_renderPage != null)
            await _renderPage();
        _renderOverlays?.Invoke();
        return true;
    }

    private async Task<bool> RemoveAppliedTextForLiveEditAsync(InlineTextEditSession session)
    {
        if (_manager == null)
            return false;

        bool removed = await _manager.RemoveAppliedTextAnnotationAsync(
            _pageIndex,
            session.Annotation,
            session.Annotation.X,
            session.Annotation.Y,
            session.Annotation.Width,
            session.Annotation.Height);
        if (!removed)
            return false;

        session.Annotation.IsOriginalTextReplacement = false;
        session.Annotation.IsApplied = false;
        session.OriginalRemovalCommitted = true;
        if (_renderPage != null)
            await _renderPage();
        _renderOverlays?.Invoke();
        return true;
    }

    private static void SetEditorText(RichEditBox editor, string text)
    {
        editor.Document.SetText(TextSetOptions.None, text ?? string.Empty);
    }

    private static string GetEditorText(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out string text);
        if (text.EndsWith('\r'))
            text = text[..^1];

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static void ApplyEditorLineSpacing(RichEditBox editor, double lineHeightPoints)
    {
        if (!double.IsFinite(lineHeightPoints) || lineHeightPoints <= 0.1)
            return;

        editor.Document.GetText(TextGetOptions.None, out string text);
        var range = editor.Document.GetRange(0, Math.Max(text.Length - 1, 0));
        // RichEdit's text object model uses points for exact line spacing. This is
        // the same unit stored on the PDF annotation and passed to the PDF writer.
        range.ParagraphFormat.SpaceBefore = 0;
        range.ParagraphFormat.SpaceAfter = 0;
        range.ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, (float)lineHeightPoints);
    }

    private static void MoveCaretToEnd(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out string text);
        int end = Math.Max(text.Length - 1, 0);
        editor.Document.Selection.SetRange(end, end);
    }

    private async Task CancelAsync(RichEditBox editor)
    {
        if (!IsEditing || _canvas?.Children.Contains(editor) != true)
            return;

        if (editor.Tag is InlineTextEditSession sessionToCancel)
        {
            sessionToCancel.IsFinishing = true;
            sessionToCancel.TextChangeVersion++;
        }

        IsEditing = false;
        _activeEditor = null;
        _canvas.Children.Remove(editor);

        if (editor.Tag is InlineTextEditSession session)
        {
            if (session.PreparationTask != null) await session.PreparationTask;
            if (session.PageImage != null) session.PageImage.Source = session.OriginalPageImage;
            if (session.RemovalTask != null)
                await session.RemovalTask;

            if (session.OriginalRemovalCommitted && _manager?.CanUndo == true)
            {
                _manager.Undo();
                session.Annotation.Content = session.OriginalContent;
                session.Annotation.IsApplied = session.WasApplied;
                session.Annotation.IsOriginalTextReplacement = session.WasOriginalTextReplacement;
                _renderOverlays?.Invoke();
            }
            else
            {
                session.Annotation.Content = session.OriginalContent;
                session.Annotation.IsApplied = session.WasApplied;
                session.Annotation.IsOriginalTextReplacement = session.WasOriginalTextReplacement;
                _renderOverlays?.Invoke();
            }
        }

        _restoreFocus?.Invoke(true);
    }

    private async Task ApplyAsync(RichEditBox editor)
    {
        if (!IsEditing || _canvas?.Children.Contains(editor) != true ||
            _manager == null || _annotations == null || _selectedAnnotations == null || _settings == null)
            return;
        if (editor.Tag is InlineTextEditSession { IsFinishing: true })
            return;
        if (editor.Tag is InlineTextEditSession { PreparationTask: not null } preparing && !await preparing.PreparationTask)
        { await CancelAsync(editor); return; }
        if (_activeEditor != editor) return;

        string text = GetEditorText(editor);
        double editedWidth = editor.Width;
        object tag = editor.Tag;
        bool textWasRemoved = false;
        var editSession = tag as InlineTextEditSession;

        if (editSession != null)
        {
            if (editSession.PageImage != null && !editSession.OriginalRemovalCommitted)
                editSession.PageImage.Source = editSession.OriginalPageImage;
            editSession.IsFinishing = true;
            editSession.TextChangeVersion++;
            editSession.SuppressTextChanged = true;
        }

        SetEditorText(editor, string.Empty);
        _canvas.Children.Remove(editor);
        if (editSession != null)
            editSession.SuppressTextChanged = false;
        IsEditing = false;
        _activeEditor = null;

        try
        {
            PdfAnnotation? existingAnnotation = tag switch
            {
                InlineTextEditSession session => session.Annotation,
                PdfAnnotation annotation => annotation,
                _ => null
            };

            if (existingAnnotation != null)
            {
                if (editSession?.RemovalTask != null && !await editSession.RemovalTask)
                {
                    existingAnnotation.Content = editSession.OriginalContent;
                    _renderOverlays?.Invoke();
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    if (existingAnnotation.IsOriginalTextReplacement)
                    {
                        textWasRemoved = await _manager.RemoveOriginalTextAnnotationsAsync(
                            _pageIndex, new[] { existingAnnotation });
                        if (!textWasRemoved)
                        {
                            _setStatus?.Invoke("배경을 보존하면서 삭제할 수 없는 PDF 텍스트입니다.");
                            return;
                        }
                    }

                    _annotations.Remove(existingAnnotation);
                    _selectedAnnotations.Remove(existingAnnotation);
                    _annotationRemoved?.Invoke(existingAnnotation);
                    _manager.MarkModified();
                    _setStatus?.Invoke("텍스트가 삭제되었습니다");
                }
                else if (editSession?.HasLiveChanges == true || existingAnnotation.Content != text)
                {
                    if (existingAnnotation.IsOriginalTextReplacement)
                    {
                        textWasRemoved = await _manager.RemoveOriginalTextAnnotationsAsync(
                            _pageIndex, new[] { existingAnnotation });
                        if (!textWasRemoved)
                        {
                            _setStatus?.Invoke("배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.");
                            return;
                        }
                        existingAnnotation.IsOriginalTextReplacement = false;
                    }

                    existingAnnotation.Content = text;
                    if (double.IsFinite(editedWidth) && editedWidth > 0)
                        existingAnnotation.Width = editedWidth / PdfToPixels;
                    if (editSession != null)
                        existingAnnotation.CharacterSpacing = editSession.PreservedCharacterSpacing;
                    if (existingAnnotation.TextFragments.Count == 0)
                    {
                        var size = AnnotationTextLayoutService.MeasureBounds(
                            text,
                            existingAnnotation.FontFamily,
                            existingAnnotation.FontSize,
                            existingAnnotation.IsBold,
                            existingAnnotation.IsItalic,
                            existingAnnotation.FontWeight);
                        existingAnnotation.Height = size.height;
                    }
                    else
                    {
                        // Original PDF glyph bounds can be shorter than WinUI's
                        // text layout box. Grow the lower edge so the committed
                        // preview cannot clip text; the right edge was already
                        // expanded above to fit the replacement text.
                        existingAnnotation.Height =
                            AnnotationTextLayoutService.GetRequiredTextBoxHeight(
                                existingAnnotation,
                                text);
                    }
                    existingAnnotation.IsApplied = false;
                    _manager.MarkModified();
                    _setStatus?.Invoke("텍스트가 수정되었습니다 (저장 시 반영)");
                }
            }
            else if (tag is Point pdfPosition)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return;

                var size = AnnotationTextLayoutService.MeasureBounds(
                    text,
                    _settings.FontFamily,
                    _settings.FontSize,
                    _settings.IsBold,
                    _settings.IsItalic);
                _annotations.Add(new PdfAnnotation
                {
                    Type = AnnotationType.Text,
                    PageIndex = _pageIndex,
                    X = pdfPosition.X,
                    Y = pdfPosition.Y,
                    Content = text,
                    FontFamily = _settings.FontFamily,
                    FontSize = _settings.FontSize,
                    Color = _settings.Color,
                    FontWeight = _settings.IsBold ? 700 : 400,
                    IsBold = _settings.IsBold,
                    IsItalic = _settings.IsItalic,
                    Width = size.width,
                    Height = size.height,
                    IsApplied = false
                });
                _manager.MarkModified();
                _setStatus?.Invoke("텍스트가 추가되었습니다 (저장 시 반영)");
            }

            if (textWasRemoved && _renderPage != null)
                await _renderPage();

            _renderOverlays?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ApplyInlineText Error: {ex}");
        }
        finally
        {
            _restoreFocus?.Invoke(false);
        }
    }
}
