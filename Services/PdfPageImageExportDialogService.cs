using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Services;

public sealed record PdfPageImageExportOptions(IReadOnlyList<int> PageIndices, int Dpi);

public sealed class PdfPageImageExportDialogService
{
    public async Task<PdfPageImageExportOptions?> ShowAsync(XamlRoot xamlRoot, int pageCount)
    {
        var allPages = new RadioButton { Content = Loc.Get("ImageExportAllPages"), GroupName = "ImageExportPages", IsChecked = true };
        var specifiedPages = new RadioButton { Content = Loc.Get("ImageExportSpecifiedPages"), GroupName = "ImageExportPages" };
        var pages = new TextBox
        {
            Header = Loc.Get("ImageExportPageNumbers"), PlaceholderText = "1,3,5-8",
            IsEnabled = false
        };
        allPages.Checked += (_, _) => pages.IsEnabled = false;
        specifiedPages.Checked += (_, _) => { pages.IsEnabled = true; pages.Focus(FocusState.Programmatic); };
        var resolution = new ComboBox { Header = Loc.Get("ImageExportResolution"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (int dpi in new[] { 96, 150, 300, 600 })
            resolution.Items.Add(new ComboBoxItem { Content = $"{dpi} DPI", Tag = dpi });
        resolution.SelectedIndex = 2;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red) };
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = Loc.Format("PagesInTotal", pageCount) });
        panel.Children.Add(allPages);
        panel.Children.Add(specifiedPages);
        panel.Children.Add(pages);
        panel.Children.Add(resolution);
        panel.Children.Add(new TextBlock { Text = Loc.Get("ImageExportDescription"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("PdfToImage"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(180, xamlRoot.Size.Height - 220) },
            PrimaryButtonText = Loc.Get("Save"), CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary, XamlRoot = xamlRoot
        };
        IReadOnlyList<int>? indices = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                indices = allPages.IsChecked == true ? Enumerable.Range(0, pageCount).ToArray()
                    : PdfPageImageExportService.ParsePageIndices(pages.Text, pageCount);
            }
            catch (ArgumentException ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && indices != null
            ? new PdfPageImageExportOptions(indices, (int)((ComboBoxItem)resolution.SelectedItem).Tag)
            : null;
    }
}
