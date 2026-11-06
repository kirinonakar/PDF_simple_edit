using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Mcp;

internal static class McpConfigurationDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        string configuration = McpConfiguration.Create();
        var panel = new StackPanel { Spacing = 12, MinWidth = 450, MaxWidth = 700 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("McpDescription"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.Get("McpStandardConfiguration") });
        var configurationText = new TextBlock
        {
            Text = configuration, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        panel.Children.Add(new ScrollViewer
        {
            Content = configurationText, MaxHeight = 320,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("McpSettings"), XamlRoot = root, Content = panel,
            PrimaryButtonText = Loc.Get("McpCopyConfiguration"), CloseButtonText = Loc.Get("Close")
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                var data = new DataPackage(); data.SetText(configuration);
                Clipboard.SetContent(data); Clipboard.Flush();
                status.Text = Loc.Get("McpConfigurationCopied");
            }
            catch (Exception error) { status.Text = Loc.Format("McpCopyFailed", error.Message); }
        };
        await dialog.ShowAsync();
    }
}
