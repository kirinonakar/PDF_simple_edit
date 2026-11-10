using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PDF_simple_edit.Controls;

internal sealed class EditorContentDialog : ContentDialog
{
    public EditorContentDialog(XamlRoot root)
    {
        XamlRoot = root;
        ApplyWindowTheme();
    }

    public void ApplyWindowTheme()
    {
        // Content dialogs open in a popup outside the window content's theme scope.
        RequestedTheme = (XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
    }
}
