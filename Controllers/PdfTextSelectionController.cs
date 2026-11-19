using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using PageLayout = PDF_simple_edit.Services.PdfTextSelectionService.PageLayout;

namespace PDF_simple_edit.Controllers;

/// <summary>Transient, read-only character selection on the displayed PDF page.</summary>
public sealed class PdfTextSelectionController(
    Canvas canvas, TextBlock statusText, Func<PdfDocumentManager> getManager,
    Func<int> getPageIndex, Func<EditToolMode> getToolMode)
{
    private const double PdfToPixels = 96.0 / 72.0;
    private sealed class Selection(PdfDocumentManager manager, byte[] source, int page, long version, Point start, uint? pointerId)
    {
        public PdfDocumentManager Manager { get; } = manager;
        public byte[] Source { get; } = source;
        public int Page { get; } = page;
        public long Version { get; } = version;
        public Point Start { get; } = start;
        public Point End { get; set; } = start;
        public uint? PointerId { get; set; } = pointerId;
        public bool AllText { get; init; }
        public PageLayout? Layout { get; set; }
        public int? StartCaret { get; set; }
        public int? EndCaret { get; set; }
        public IReadOnlyList<NativePdfTextBlock>? SelectedBlocks { get; set; }
        public string? Text { get; set; }
    }

    private Selection? _selection;
    private Canvas? _preview;
    private SolidColorBrush? _highlightBrush;
    private List<PdfTextPoint[]> _previewQuads = new();
    private (byte[] Source, int Index, CancellationTokenSource Cancellation, Task<PageLayout> Layout)? _preparedPage;

    private bool IsCurrent(Selection selection) => ReferenceEquals(selection, _selection) &&
        getToolMode() == EditToolMode.None && selection.Manager == getManager() &&
        selection.Manager.IsLoaded && selection.Page == getPageIndex() &&
        selection.Version == selection.Manager.EditVersion &&
        ReferenceEquals(selection.Source, selection.Manager.GetPdfBytes());

    public string SelectedText =>
        _selection is { SelectedBlocks: { } blocks } selection && IsCurrent(selection)
            ? selection.Text ??= PdfTextSelectionService.GetSelectedText(blocks) : "";
    public bool HasSelection => SelectedText.Length > 0;

    public void Clear()
    {
        bool captured = _selection?.PointerId != null;
        _selection = null;
        ClearPreview();
        if (captured) canvas.ReleasePointerCaptures();
    }

    private void ClearPreview()
    {
        if (_preview != null) canvas.Children.Remove(_preview);
        _preview = null;
        _previewQuads.Clear();
    }

    public void ResetPreparedPage()
    {
        if (_preparedPage is not { } page) return;
        _preparedPage = null;
        page.Cancellation.Cancel();
        page.Cancellation.Dispose();
    }

    private Task<PageLayout> GetPageLayoutAsync(byte[] source, int pageIndex)
    {
        if (_preparedPage is { } page && ReferenceEquals(page.Source, source) && page.Index == pageIndex &&
            !page.Layout.IsFaulted && !page.Layout.IsCanceled)
            return page.Layout;

        ResetPreparedPage();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var layout = Task.Run(() =>
        {
            var blocks = new NativePdfTextService().Extract(source, pageIndex, token);
            token.ThrowIfCancellationRequested();
            return new PageLayout(blocks);
        }, token);
        _preparedPage = (source, pageIndex, cancellation, layout);
        return layout;
    }

    public async Task PreparePageAsync(byte[] source, int pageIndex)
    {
        try { await GetPageLayoutAsync(source, pageIndex).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Text selection preparation: {error}"); }
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
        await ExtractSelectionAsync(selection);
    }

    public async Task SelectAllAsync()
    {
        var manager = getManager();
        if (getToolMode() != EditToolMode.None || !manager.IsLoaded || manager.GetPdfBytes() is not { } source) return;
        Clear();
        var selection = _selection = new Selection(manager, source, getPageIndex(), manager.EditVersion, default, null)
        {
            AllText = true
        };
        await ExtractSelectionAsync(selection);
    }

    private async Task ExtractSelectionAsync(Selection selection)
    {
        try
        {
            var layout = await GetPageLayoutAsync(selection.Source, selection.Page);
            if (!IsCurrent(selection)) return;
            selection.Layout = layout;
            Update(selection);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(selection, _selection)) Clear();
        }
        catch (Exception error)
        {
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
        if (selection.Layout is not { } layout) return;
        if (selection.AllText)
        {
            selection.SelectedBlocks = layout.Blocks;
        }
        else
        {
            if (Math.Abs(selection.End.X - selection.Start.X) + Math.Abs(selection.End.Y - selection.Start.Y) < 4)
            {
                selection.EndCaret = null;
                selection.SelectedBlocks = null;
                selection.Text = null;
                ClearPreview();
                return;
            }

            int start = selection.StartCaret ??= layout.Hit(ToPdfPoint(selection.Start));
            int end = layout.Hit(ToPdfPoint(selection.End));
            if (selection.EndCaret == end) return;
            selection.EndCaret = end;
            selection.SelectedBlocks = layout.SelectRange(start, end);
        }
        selection.Text = null;
        UpdatePreview(PdfCommentService.GetTextQuads(selection.SelectedBlocks));
    }

    private static PdfTextPoint ToPdfPoint(Point point) => new(point.X / PdfToPixels, point.Y / PdfToPixels);

    private void UpdatePreview(List<PdfTextPoint[]> quads)
    {
        if (quads.Count == 0)
        {
            ClearPreview();
            return;
        }

        _preview ??= new Canvas { IsHitTestVisible = false };
        _highlightBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 120, 215));
        var children = _preview.Children;
        for (int i = children.Count - 1; i >= quads.Count; i--) children.RemoveAt(i);
        for (int i = 0; i < quads.Count; i++)
        {
            var quad = quads[i];
            if (i < children.Count)
            {
                if (!quad.AsSpan().SequenceEqual(_previewQuads[i]))
                    ((Polygon)children[i]).Points = ToPolygonPoints(quad);
            }
            else
            {
                children.Add(new Polygon { Fill = _highlightBrush, Points = ToPolygonPoints(quad) });
            }
        }
        _previewQuads = quads;
        if (!canvas.Children.Contains(_preview)) canvas.Children.Add(_preview);
    }

    private static PointCollection ToPolygonPoints(PdfTextPoint[] quad) => new()
    {
        ToCanvasPoint(quad[0]), ToCanvasPoint(quad[1]), ToCanvasPoint(quad[3]), ToCanvasPoint(quad[2])
    };

    private static Point ToCanvasPoint(PdfTextPoint point) => new(point.X * PdfToPixels, point.Y * PdfToPixels);

    public void Render()
    {
        if (_selection is { } selection && !IsCurrent(selection)) Clear();
        if (_preview != null && !canvas.Children.Contains(_preview)) canvas.Children.Add(_preview);
    }
}
