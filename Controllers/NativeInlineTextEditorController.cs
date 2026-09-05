using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;

namespace PDF_simple_edit.Controllers;

public sealed class NativeInlineTextSession
{
    public required PdfAnnotation Annotation { get; init; }
    public bool IsFinishing { get; set; }
}

/// <summary>
/// RichEdit supplies Unicode/IME, clipboard and keyboard editing only. PDF-rendered
/// page pixels and the native glyph geometry supply the text, hit testing, selection
/// and caret. Platform font metrics never enter the saved content or visible text.
/// </summary>
public sealed class NativeInlineTextEditorController
{
    public static readonly object OverlayTag = new();
    private const double Scale = 96.0 / 72.0;
    private readonly NativePdfTextService _service = new();
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private Canvas? _canvas, _decoration;
    private Image? _pageImage;
    private ImageSource? _originalImage;
    private RichEditBox? _input;
    private NativeInlineTextSession? _session;
    private PdfDocumentManager? _manager;
    private byte[]? _snapshot;
    private NativePdfTextBlock? _layout;
    private NativePdfTextResult? _preview;
    private string _previewText = "";
    private int _pageIndex;
    private int _version;
    private bool _suppress, _dragging;
    private int _anchor;
    private DispatcherTimer? _caretTimer;
    private bool _caretVisible = true;
    private Task _pending = Task.CompletedTask;
    private Task? _finishing;
    private Action<string>? _status;
    private Action? _overlays;
    private Func<Task>? _render;
    private Action<bool>? _restoreFocus;
    private Action<PdfAnnotation>? _removed;
    private List<PdfAnnotation>? _annotations, _selected;
    public bool IsEditing => _input != null;

    public bool Start(Canvas canvas, Image pageImage, PdfAnnotation annotation, PdfDocumentManager manager,
        List<PdfAnnotation> annotations, List<PdfAnnotation> selected, int pageIndex,
        Func<Task> render, Action overlays, Action<string> status, Action<PdfAnnotation> removed, Action<bool> restoreFocus,
        PdfTextPoint? initialCaret = null)
    {
        if (IsEditing || annotation.NativeText == null || manager.GetPdfBytes() == null) return false;
        ++_version; _finishing = null; _pending = Task.CompletedTask; _caretVisible = true;
        _canvas = canvas; _pageImage = pageImage; _originalImage = pageImage.Source;
        _manager = manager; _snapshot = manager.GetPdfBytes(); _layout = annotation.NativeText;
        _pageIndex = pageIndex; _render = render; _overlays = overlays; _status = status;
        _restoreFocus = restoreFocus; _removed = removed; _annotations = annotations; _selected = selected;
        _session = new() { Annotation = annotation }; _preview = new(_snapshot!, _layout); _previewText = _layout.Text;
        _input = new RichEditBox
        {
            Tag = _session, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Width = Math.Max(annotation.Width * Scale, 20), Height = Math.Max(annotation.Height * Scale, 20),
            MinWidth = 0, MinHeight = 0, Padding = new(0), BorderThickness = new(0), Margin = new(0),
            FontSize = Math.Max(annotation.FontSize * Scale, 1),
            // Keep the native input host in the accessibility/IME tree. Its glyphs
            // and platform caret are transparent; our PDF-coordinate caret is visible.
            Opacity = 0, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_input, "PDF 원본 텍스트 편집");
        Canvas.SetLeft(_input, annotation.X * Scale); Canvas.SetTop(_input, annotation.Y * Scale);
        _input.Document.SetText(TextSetOptions.None, _layout.Text);
        _input.TextChanged += TextChanged;
        _input.SelectionChanged += (_, _) => { _caretVisible = true; DrawSelection(); };
        _input.PreviewKeyDown += KeyDown;
        _input.LostFocus += async (_, _) => { if (_session?.IsFinishing != true) await FinishAsync(); };
        _input.Loaded += (_, _) =>
        {
            _input?.Focus(FocusState.Programmatic);
            if (initialCaret is { } point && _input != null)
            { int hit = Hit(point.X, point.Y); _input.Document.Selection.SetRange(hit, hit); }
            DrawSelection();
        };
        _decoration = new Canvas
        {
            Tag = OverlayTag, Width = canvas.Width, Height = canvas.Height,
            Background = null
        };
        // Pointer input belongs to native glyph geometry, independent of the
        // offscreen input host's fallback font and RichEdit padding.
        _input.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PointerPressed), true);
        _input.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(PointerMoved), true);
        _input.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PointerReleased), true);
        canvas.Children.Add(_input); canvas.Children.Add(_decoration);
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) => { _caretVisible = !_caretVisible; DrawSelection(); };
        _caretTimer.Start(); _overlays(); DrawSelection();
        status("원본 글꼴로 인라인 편집 · Ctrl+Enter 완료 · Esc 취소");
        return true;
    }

    private string Text()
    {
        _input!.Document.GetText(TextGetOptions.None, out string text);
        if (text.EndsWith('\r')) text = text[..^1];
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private void TextChanged(object sender, RoutedEventArgs args)
    {
        if (_suppress || _session?.IsFinishing != false) return;
        string text = Text();
        int version = ++_version;
        if (text == _previewText) { _pending = Task.CompletedTask; DrawSelection(); return; }
        _pending = RefreshPreviewAsync(text, version);
    }

    private async Task RefreshPreviewAsync(string text, int version)
    {
        await Task.Delay(35);
        if (version != _version || _input == null) return;
        await _previewGate.WaitAsync();
        try
        {
            if (version != _version || _input == null) return;
            byte[] snapshot = _snapshot!; int pageIndex = _pageIndex;
            NativePdfTextBlock source = _session!.Annotation.NativeText!;
            var result = await Task.Run(() => _service.Edit(snapshot, pageIndex, new(source, text)));
            if (version != _version || _input == null) return;
            using var bytes = new MemoryStream(result.Bytes);
            using var rendered = await PdfRenderHelper.RenderPageWithWindowsPdfStreamAsync(bytes.AsRandomAccessStream(), pageIndex, 2);
            if (rendered == null) throw new InvalidOperationException("수정한 PDF 미리보기를 렌더링할 수 없습니다.");
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(rendered.AsRandomAccessStream());
            if (version != _version || _input == null) return;
            _pageImage!.Source = bitmap; _preview = result; _previewText = text; _layout = result.Layout;
            var bounds = _layout.Bounds;
            _input.Width = Math.Max((_session!.Annotation.Width + Math.Max(0, bounds.Right - _session.Annotation.NativeText!.Bounds.Right)) * Scale, 20);
            _input.Height = Math.Max((_session.Annotation.Height + Math.Max(0, bounds.Bottom - _session.Annotation.NativeText!.Bounds.Bottom)) * Scale, 20);
            _status?.Invoke(source.Lines.Count > 1
                ? "원본 글꼴로 문단 자동 줄바꿈 중 · Ctrl+Enter 완료 · Esc 취소"
                : "원본 글꼴로 한 줄 오른쪽 확장 중 · Ctrl+Enter 완료 · Esc 취소");
            DrawSelection();
        }
        catch (Exception error)
        {
            if (version != _version || _input == null) return;
            int caret = Math.Min(_input.Document.Selection.StartPosition, _previewText.Length);
            _suppress = true;
            try { _input.Document.SetText(TextSetOptions.None, _previewText); _input.Document.Selection.SetRange(caret, caret); }
            finally { _suppress = false; }
            _status?.Invoke(error.Message); DrawSelection();
        }
        finally { _previewGate.Release(); }
    }

    private void DrawSelection()
    {
        if (_input == null || _layout == null || _decoration == null) return;
        _decoration.Children.Clear();
        int start = Math.Min(_input.Document.Selection.StartPosition, _input.Document.Selection.EndPosition);
        int end = Math.Max(_input.Document.Selection.StartPosition, _input.Document.Selection.EndPosition);
        if (_layout.Bounds.Height > 0) AddBox(_layout.Bounds, false);
        foreach (var glyph in _layout.Glyphs.Where(g => g.TextIndex < end && g.TextIndex + g.Text.Length > start && g.Text != "\n"))
            AddBox(glyph.Bounds with { Width = glyph.IsSoftBreak ? Math.Max(glyph.Bounds.Width, 1) :
                Math.Max(glyph.Bounds.Width, Math.Abs(glyph.End.X - glyph.Origin.X)) }, true);
        if (start != end || !_caretVisible) return;
        var next = _layout.Glyphs.FirstOrDefault(g => g.TextIndex >= start);
        var last = _layout.Glyphs.LastOrDefault();
        var source = next is { Text: not "\n" } ? next :
            _layout.Glyphs.LastOrDefault(g => g.Text != "\n" && g.TextIndex < start)
            ?? _session!.Annotation.NativeText!.Glyphs.First(g => !g.IsVirtual);
        var point = next?.Origin ?? last?.End ?? source.Origin;
        var lineBounds = _layout.Lines.OrderBy(line => Math.Abs(line.Y + line.Height / 2 - source.Bounds.Y - source.Bounds.Height / 2)).FirstOrDefault();
        double top = lineBounds.Height > 0 ? lineBounds.Y + point.Y - source.Origin.Y : point.Y + source.Bounds.Y - source.Origin.Y;
        var caret = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 1.5, Height = Math.Max((lineBounds.Height > 0 ? lineBounds.Height : source.Bounds.Height) * Scale, 8),
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Black), IsHitTestVisible = false
        };
        Canvas.SetLeft(caret, point.X * Scale); Canvas.SetTop(caret, top * Scale);
        _decoration.Children.Add(caret);
    }

    private void AddBox(PdfTextBox box, bool selection)
    {
        var rectangle = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(box.Width * Scale, 1), Height = Math.Max(box.Height * Scale, 1),
            Stroke = selection ? null : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            StrokeThickness = selection ? 0 : 0.75,
            Fill = selection ? new SolidColorBrush(Windows.UI.Color.FromArgb(70, 30, 144, 255)) : null,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rectangle, box.X * Scale); Canvas.SetTop(rectangle, box.Y * Scale);
        _decoration!.Children.Add(rectangle);
    }

    private int Hit(double x, double y)
    {
        if (_layout == null || _layout.Glyphs.Count == 0) return 0;
        var nearestLine = _layout.Lines.OrderBy(line => Math.Abs(line.Y + line.Height / 2 - y)).FirstOrDefault();
        var glyph = _layout.Glyphs.Where(g => g.Text != "\n" && !g.IsSoftBreak && (nearestLine.Height == 0 ||
            g.Bounds.Y + g.Bounds.Height / 2 >= nearestLine.Y - .1 && g.Bounds.Y + g.Bounds.Height / 2 <= nearestLine.Bottom + .1)).OrderBy(g =>
            Math.Pow(Math.Max(Math.Max(g.Bounds.Y - y, y - g.Bounds.Bottom), 0) * 4, 2) +
            Math.Pow(Math.Min(Math.Abs(x - g.Origin.X), Math.Abs(x - g.End.X)), 2)).FirstOrDefault();
        return glyph == null ? 0 : x > (glyph.Origin.X + glyph.End.X) / 2 ? glyph.TextIndex + glyph.Text.Length : glyph.TextIndex;
    }
    private void PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_input == null) return;
        var pointer = args.GetCurrentPoint(_canvas);
        if (!pointer.Properties.IsLeftButtonPressed) return;
        _anchor = Hit(pointer.Position.X / Scale, pointer.Position.Y / Scale);
        _input.Document.Selection.SetRange(_anchor, _anchor); _input.Focus(FocusState.Pointer);
        _dragging = true; _input.CapturePointer(args.Pointer); args.Handled = true; DrawSelection();
    }
    private void PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging || _input == null) return;
        var point = args.GetCurrentPoint(_canvas).Position;
        _input.Document.Selection.SetRange(_anchor, Hit(point.X / Scale, point.Y / Scale)); args.Handled = true;
    }
    private void PointerReleased(object sender, PointerRoutedEventArgs args)
    { _dragging = false; _input?.ReleasePointerCapture(args.Pointer); args.Handled = true; }

    private async void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; await CancelAsync(); }
        else if (args.Key == VirtualKey.Enter && KeyboardStateService.IsControlDown())
        { args.Handled = true; await FinishAsync(); }
        else if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End && _layout?.Glyphs.Count > 0 && _input != null)
        {
            int position = _input.Document.Selection.EndPosition;
            var glyph = _layout.Glyphs.FirstOrDefault(g => g.TextIndex >= position) ?? _layout.Glyphs[^1];
            int hit = args.Key switch
            {
                VirtualKey.Home => KeyboardStateService.IsControlDown() ? 0 : Hit(-1e6, glyph.Origin.Y - glyph.Bounds.Height / 2),
                VirtualKey.End => KeyboardStateService.IsControlDown() ? _previewText.Length : Hit(1e6, glyph.Origin.Y - glyph.Bounds.Height / 2),
                _ => Hit(glyph.Origin.X, glyph.Origin.Y + (args.Key == VirtualKey.Up ? -1 : 1) * _layout.LineHeight - glyph.Bounds.Height / 2)
            };
            _input.Document.Selection.SetRange(KeyboardStateService.IsShiftDown() ? _input.Document.Selection.StartPosition : hit, hit);
            args.Handled = true;
        }
    }

    public Task FinishAsync()
    {
        if (_finishing?.IsCompleted == true && _session?.IsFinishing == false) _finishing = null;
        return _finishing ??= FinishCoreAsync();
    }

    private async Task FinishCoreAsync()
    {
        if (_input == null || _session == null || _session.IsFinishing) return;
        _session.IsFinishing = true;
        try
        {
            await _pending;
            if (_preview == null || _snapshot == null) return;
            if (!ReferenceEquals(_preview.Bytes, _snapshot))
            {
                _manager!.CommitNativeTextEdit(_snapshot, _preview.Bytes);
                // Stream operation indexes change after replacement. Discard stale
                // handles; the next selection is extracted from committed bytes.
                foreach (var item in _annotations!.Where(a => a.PageIndex == _pageIndex && a.NativeText != null).ToList())
                { _annotations!.Remove(item); _selected!.Remove(item); _removed?.Invoke(item); }
            }
            Cleanup(); if (_render != null) await _render(); _overlays?.Invoke(); _restoreFocus?.Invoke(false);
        }
        catch (Exception error)
        {
            if (_session != null) _session.IsFinishing = false;
            _finishing = null; _status?.Invoke(error.Message); _input?.Focus(FocusState.Programmatic);
        }
    }

    public async Task CancelAsync()
    {
        if (_session == null) return;
        _session.IsFinishing = true; ++_version;
        await _pending;
        if (_pageImage != null) _pageImage.Source = _originalImage;
        Cleanup(); _overlays?.Invoke(); _restoreFocus?.Invoke(true);
    }

    private void Cleanup()
    {
        _caretTimer?.Stop(); _caretTimer = null;
        var input = _input; _input = null;
        if (input != null) _canvas?.Children.Remove(input);
        if (_decoration != null) _canvas?.Children.Remove(_decoration);
        _decoration = null; _session = null; _snapshot = null; _preview = null;
    }
}
