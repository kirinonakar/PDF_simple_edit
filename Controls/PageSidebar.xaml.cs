using Microsoft.UI.Xaml.Controls;

namespace PDF_simple_edit.Controls;

public sealed partial class PageSidebar : UserControl
{
    public PageSidebar()
    {
        InitializeComponent();
    }

    public ListView ListView => PageList;
}
