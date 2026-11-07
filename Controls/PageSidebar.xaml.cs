using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PDF_simple_edit.Controls;

public sealed partial class PageSidebar : UserControl
{
    public PageSidebar()
    {
        InitializeComponent();
        // ContextFlyout handles ContextRequested before ordinary subscribers.
        // Capture the page from the input instead, including handled item events.
        PageList.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(PageList_PointerPressed), true);
        PageList.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(PageList_KeyDown), true);
        PageContextMenu.Opening += (_, _) =>
            SavePageImageMenuItem.IsEnabled = ContextPageIndex != null;
    }

    public int? ContextPageIndex { get; private set; }
    public MenuFlyoutItem SaveImageMenuItem => SavePageImageMenuItem;

    private void PageList_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        // Record the clicked item even when it is already part of a multi-selection.
        // Clicking empty space clears the target so a previous page cannot leak.
        ContextPageIndex = FindPageIndex(args.OriginalSource as DependencyObject);
    }

    private void PageList_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is Windows.System.VirtualKey.Application or Windows.System.VirtualKey.F10)
            ContextPageIndex = FindPageIndex(args.OriginalSource as DependencyObject)
                ?? (PageList.SelectedItem as PageThumbnailData)?.PageIndex;
    }

    private int? FindPageIndex(DependencyObject? source)
    {
        while (source != null && source != PageList)
        {
            if (source is ListViewItem { Content: PageThumbnailData thumbnail })
                return thumbnail.PageIndex;
            if (source is FrameworkElement { DataContext: PageThumbnailData data })
                return data.PageIndex;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public ListView ListView => PageList;
    public MenuFlyoutItem ExtractMenuItem => ExtractPagesMenuItem;
    public MenuFlyoutItem DeleteMenuItem => DeletePagesMenuItem;
    public MenuFlyoutItem RotateMenuItem => RotatePagesMenuItem;
    public MenuFlyoutItem ResizeMenuItem => ResizePagesMenuItem;
}
