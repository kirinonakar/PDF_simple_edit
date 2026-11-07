using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;

namespace PDF_simple_edit.Controllers;

/// <summary>Transient, read-only character selection on the displayed PDF page.</summary>
public sealed class PdfTextSelectionController(
    Canvas canvas, TextBlock statusText, Func<PdfDocumentManager> getManager,
    Func<int> getPageIndex, Func<EditToolMode> getToolMode)
{
    private const double PdfToPixels = 96.0 / 72.0;
    private sealed class Selection(PdfDocumentManager manager, byte[] source, int page, long version, Point start, uint pointerId)
    {
        public PdfDocumentManager Manager { get; } = manager;
        public byte[] Source { get; } = source;
        public int Page { get; } = page;
        public long Version { get; } = version;
        public Point Start { get; } = start;
        public Point End { get; set; } = start;
        public uint? PointerId { get; set; } = pointerId;
        public List<NativePdfTextBlock>? Blocks { get; set; }
        public string Text { get; set; } = "";
    }

    private Selection? _selection;
    private Canvas? _preview;
    private byte[]? _cachedSource;
    private int _cachedPage;
    private Task<List<NativePdfTextBlock>>? _cachedExtraction;

    private bool IsCurrent(Selection selection) => ReferenceEquals(selection, _selection) &&
        getToolMode() == EditToolMode.None && selection.Manager == getManager() &&
        selection.Manager.IsLoaded && selection.Page == getPageIndex() &&
        selection.Version == selection.Manager.EditVersion &&
        ReferenceEquals(selection.Source, selection.Manager.GetPdfBytes());

    public string SelectedText => _selection is { } selection && IsCurrent(selection) ? selection.Text : "";
    public bool HasSelection => SelectedText.Length > 0;

    public void Clear()
    {
        bool captured = _selection?.PointerId != null;
        _selection = null;
        if (_preview != null) canvas.Children.Remove(_preview);
        _preview = null;
        if (captured) canvas.ReleasePointerCaptures();
    }

    public void CancelGesture()
    {
        if (_selection?.PointerId != null) Clear();
    }

    private Point Clip(Point point) => new(Math.Clamp(point.X, 0, canvas.Width), Math.Clamp(point.Y, 0, canvas.Height));

    public async Task PressedAsync(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(canvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        Clear();
        var manager = getManager();
        if (manager.GetPdfBytes() is not { } source) return;
        var selection = _selection = new Selection(manager, source, getPageIndex(), manager.EditVersion,
            Clip(point.Position), e.Pointer.PointerId);
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
        if (!ReferenceEquals(source, _cachedSource) || selection.Page != _cachedPage || _cachedExtraction == null)
        {
            _cachedSource = source;
            _cachedPage = selection.Page;
            _cachedExtraction = Task.Run(() => new NativePdfTextService().Extract(source, selection.Page));
        }
        var extraction = _cachedExtraction;
        try
        {
            var blocks = await extraction;
            if (!IsCurrent(selection)) return;
            selection.Blocks = blocks;
            Update(selection);
        }
        catch (Exception error)
        {
            if (ReferenceEquals(extraction, _cachedExtraction)) _cachedExtraction = null;
            if (IsCurrent(selection))
            {
                Clear();
                statusText.Text = error.Message;
            }
        }
    }

    public bool Moved(PointerRoutedEventArgs e)
    {
        if (_selection is not { PointerId: not null } selection) return false;
        if (selection.PointerId != e.Pointer.PointerId) return true;
        var point = e.GetCurrentPoint(canvas);
        if (!IsCurrent(selection) || !point.Properties.IsLeftButtonPressed) { Clear(); return true; }
        selection.End = Clip(point.Position);
        Update(selection);
        e.Handled = true;
        return true;
    }

    public bool Released(PointerRoutedEventArgs e)
    {
        if (_selection is not { PointerId: not null } selection) return false;
        if (selection.PointerId != e.Pointer.PointerId) return true;
        selection.End = Clip(e.GetCurrentPoint(canvas).Position);
        // CaptureLost also fires on normal release. Keep the completed range (and
        // its pending extraction) while cancelling genuinely interrupted drags.
        selection.PointerId = null;
        canvas.ReleasePointerCapture(e.Pointer);
        if (IsCurrent(selection)) Update(selection);
        else Clear();
        e.Handled = true;
        return true;
    }

    private void Update(Selection selection)
    {
        if (selection.Blocks == null) return;
        if (_preview != null) canvas.Children.Remove(_preview);
        _preview = null;
        selection.Text = "";
        if (Math.Abs(selection.End.X - selection.Start.X) + Math.Abs(selection.End.Y - selection.Start.Y) < 4) return;
        var selected = PdfTextSelectionService.SelectRange(selection.Blocks,
            new(selection.Start.X / PdfToPixels, selection.Start.Y / PdfToPixels),
            new(selection.End.X / PdfToPixels, selection.End.Y / PdfToPixels));
        selection.Text = PdfTextSelectionService.GetSelectedText(selected);
        PdfCommentService.SelectText(selected, new(-1e8, -1e8, 2e8, 2e8), out _, out var quads);
        _preview = new Canvas { IsHitTestVisible = false };
        foreach (var quad in quads)
        {
            var polygon = new Polygon { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 120, 215)), IsHitTestVisible = false };
            foreach (int index in new[] { 0, 1, 3, 2 })
                polygon.Points.Add(new(quad[index].X * PdfToPixels, quad[index].Y * PdfToPixels));
            _preview.Children.Add(polygon);
        }
        canvas.Children.Add(_preview);
    }

    public void Render()
    {
        if (_selection is { } selection && !IsCurrent(selection)) Clear();
        if (_preview != null && !canvas.Children.Contains(_preview)) canvas.Children.Add(_preview);
    }
}
