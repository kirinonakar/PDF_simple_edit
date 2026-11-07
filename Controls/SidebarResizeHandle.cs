using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.Foundation;

namespace PDF_simple_edit.Controls;

public sealed class SidebarResizeDeltaEventArgs(double change) : EventArgs
{
    public double HorizontalChange { get; } = change;
}

public sealed class SidebarResizeHandle : Control
{
    private uint? _pointer;
    private Point _position;
    public event EventHandler<SidebarResizeDeltaEventArgs>? DragDelta;
    public SidebarResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        IsTabStop = true;
    }
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed) return;
        _pointer = e.Pointer.PointerId; _position = point.Position;
        CapturePointer(e.Pointer); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pointer != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed) { _pointer = null; ReleasePointerCaptures(); return; }
        double change = point.Position.X - _position.X; _position = point.Position;
        DragDelta?.Invoke(this, new(change)); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer != e.Pointer.PointerId) return;
        _pointer = null; ReleasePointerCapture(e.Pointer); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { base.OnPointerCaptureLost(e); _pointer = null; }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
        { DragDelta?.Invoke(this, new(e.Key == Windows.System.VirtualKey.Left ? -8 : 8)); e.Handled = true; }
    }
}
