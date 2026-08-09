using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public bool OriginalRemovalCommitted { get; set; }
    public Task<bool>? RemovalTask { get; set; }
}

public sealed class InlineTextEditorController
{
    private const double PdfToPixels = 96.0 / 72.0;

    private Canvas? _canvas;
    private TextBox? _activeTextBox;
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

    public bool IsEditing { get; private set; }

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
        Action<bool> restoreFocus)
    {
        if (canvas.Children.OfType<TextBox>().Any())
        {
            IsEditing = _activeTextBox != null;
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
        string initialText = existingAnnotation != null
            ? buildEditableText(existingAnnotation)
            : string.Empty;
        double width = existingAnnotation != null
            ? Math.Max(existingAnnotation.Width * PdfToPixels, 1)
            : double.NaN;
        double height = existingAnnotation != null
            ? Math.Max(existingAnnotation.Height * PdfToPixels, 1)
            : 24;
        double displayFontSize = existingAnnotation != null
            ? AnnotationTextLayoutService.GetDisplayFontSize(existingAnnotation, initialText)
            : fontSize;
        double topOffset = existingAnnotation != null
            ? AnnotationTextLayoutService.GetTopOffset(existingAnnotation, displayFontSize)
            : 0;

        var textBox = new TextBox
        {
            AcceptsReturn = existingAnnotation != null,
            TextWrapping = TextWrapping.NoWrap,
            Text = initialText,
            MinWidth = existingAnnotation != null ? 0 : 60,
            MinHeight = existingAnnotation != null ? 0 : 24,
            Width = width,
            Height = existingAnnotation != null ? height : double.NaN,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            // The editing border is rendered as a separate overlay. Keeping the
            // TextBox borderless prevents its content presenter from shifting the
            // text by one pixel when edit mode starts.
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            FontSize = displayFontSize * PdfToPixels,
            FontFamily = new FontFamily(fontFamily),
            Foreground = new SolidColorBrush(EditorColorService.Parse(color)),
            FontWeight = isBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Tag = existingAnnotation != null
                ? new InlineTextEditSession
                {
                    Annotation = existingAnnotation,
                    OriginalContent = initialText
                }
                : new Point(pdfX, pdfY),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Top,
            MaxWidth = 4000,
            UseLayoutRounding = false
        };
        Canvas.SetLeft(textBox, pdfX * PdfToPixels);
        Canvas.SetTop(textBox, pdfY * PdfToPixels + topOffset);

        textBox.Loaded += (_, _) =>
        {
            if (existingAnnotation != null)
            {
                textBox.UpdateLayout();
                textBox.Height = height;
            }
            textBox.Focus(FocusState.Programmatic);
        };
        textBox.PointerPressed += (_, args) => args.Handled = true;
        textBox.PointerReleased += (_, args) => args.Handled = true;
        textBox.DoubleTapped += (_, args) => args.Handled = true;
        if (existingAnnotation != null)
            textBox.TextChanged += InlineTextBox_TextChanged;
        textBox.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter &&
                (!textBox.AcceptsReturn || KeyboardStateService.IsControlDown()))
            {
                args.Handled = true;
                await ApplyAsync(textBox);
            }
            else if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                await CancelAsync(textBox);
            }
        };
        textBox.LostFocus += async (_, _) => await ApplyAsync(textBox);
        canvas.Children.Add(textBox);
        _activeTextBox = textBox;
        _renderOverlays?.Invoke();
        return true;
    }

    public async Task FinishActiveEditAsync()
    {
        if (_activeTextBox != null)
            await ApplyAsync(_activeTextBox);
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
            double displayFontSize = AnnotationTextLayoutService.GetDisplayFontSize(session.Annotation, changedText);
            textBox.FontSize = displayFontSize * PdfToPixels;
            Canvas.SetTop(
                textBox,
                session.Annotation.Y * PdfToPixels +
                AnnotationTextLayoutService.GetTopOffset(session.Annotation, displayFontSize));
        }

        if (session.Annotation.IsOriginalTextReplacement && session.RemovalTask == null)
        {
            session.RemovalTask = RemoveOriginalTextForLiveEditAsync(session);
            bool removed = await session.RemovalTask;
            if (!removed && _canvas?.Children.Contains(textBox) == true)
            {
                session.SuppressTextChanged = true;
                session.Annotation.Content = session.OriginalContent;
                textBox.Text = session.OriginalContent;
                textBox.SelectionStart = textBox.Text.Length;
                session.SuppressTextChanged = false;
                session.HasLiveChanges = false;
                _setStatus?.Invoke("배경을 보존하면서 수정할 수 없는 PDF 텍스트입니다.");
                return;
            }
        }

        if (session.RemovalTask != null && !await session.RemovalTask)
            return;

        if (session.IsFinishing || changeVersion != session.TextChangeVersion ||
            _canvas?.Children.Contains(textBox) != true)
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

    private async Task CancelAsync(TextBox textBox)
    {
        if (!IsEditing || _canvas?.Children.Contains(textBox) != true)
            return;

        if (textBox.Tag is InlineTextEditSession sessionToCancel)
        {
            sessionToCancel.IsFinishing = true;
            sessionToCancel.TextChangeVersion++;
        }

        IsEditing = false;
        _activeTextBox = null;
        _canvas.Children.Remove(textBox);

        if (textBox.Tag is InlineTextEditSession session)
        {
            if (session.RemovalTask != null)
                await session.RemovalTask;

            if (session.OriginalRemovalCommitted && _manager?.CanUndo == true)
            {
                _manager.Undo();
            }
            else
            {
                session.Annotation.Content = session.OriginalContent;
                session.Annotation.IsApplied = false;
                _renderOverlays?.Invoke();
            }
        }

        _restoreFocus?.Invoke(true);
    }

    private async Task ApplyAsync(TextBox textBox)
    {
        if (!IsEditing || _canvas?.Children.Contains(textBox) != true ||
            _manager == null || _annotations == null || _selectedAnnotations == null || _settings == null)
            return;
        if (textBox.Tag is InlineTextEditSession { IsFinishing: true })
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

        textBox.Text = string.Empty;
        _canvas.Children.Remove(textBox);
        if (editSession != null)
            editSession.SuppressTextChanged = false;
        IsEditing = false;
        _activeTextBox = null;

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
                    if (existingAnnotation.TextFragments.Count == 0)
                    {
                        var size = AnnotationTextLayoutService.MeasureBounds(
                            text,
                            existingAnnotation.FontFamily,
                            existingAnnotation.FontSize,
                            existingAnnotation.IsBold,
                            existingAnnotation.IsItalic);
                        existingAnnotation.Width = size.width;
                        existingAnnotation.Height = size.height;
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
