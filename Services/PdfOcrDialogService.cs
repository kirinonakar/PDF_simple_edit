using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Controls;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Services;

public sealed class PdfOcrDialogService
{
    private static string? _lastLanguage;

    public async Task<PdfOcrOptions?> ShowAsync(XamlRoot root, int pageCount, int currentPage)
    {
        var languages = PdfOcrService.GetLanguages();
        if (languages.Count == 0) throw new InvalidOperationException(Loc.Get("OcrNoLanguages"));
        string preferred = _lastLanguage ?? CultureInfo.CurrentUICulture.Name;
        var language = new ComboBox
        {
            Header = Loc.Get("OcrLanguage"), HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = languages, DisplayMemberPath = nameof(PdfOcrLanguage.Name),
            SelectedItem = languages.FirstOrDefault(l => l.Tag.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault(l => l.Tag.Split('-')[0].Equals(preferred.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                ?? languages[0]
        };
        var current = new RadioButton { Content = Loc.Format("OcrCurrentPage", currentPage + 1), GroupName = "OcrScope", IsChecked = true };
        var all = new RadioButton { Content = Loc.Format("OcrAllPages", pageCount), GroupName = "OcrScope" };
        var skip = new CheckBox { Content = Loc.Get("OcrSkipTextPages"), IsChecked = true };
        var panel = new StackPanel { Spacing = 12, MinWidth = 300, MaxWidth = 450 };
        panel.Children.Add(language);
        panel.Children.Add(current);
        panel.Children.Add(all);
        panel.Children.Add(skip);
        panel.Children.Add(new TextBlock { Text = Loc.Get("OcrDescription"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.Get("OcrLanguageHelp"), TextWrapping = TextWrapping.Wrap });
        var dialog = new EditorContentDialog(root)
        {
            Title = Loc.Get("OcrTitle"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(180, root.Size.Height - 220) },
            PrimaryButtonText = Loc.Get("OcrStart"), CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        _lastLanguage = ((PdfOcrLanguage)language.SelectedItem).Tag;
        return new(_lastLanguage, all.IsChecked == true ? Enumerable.Range(0, pageCount).ToArray() : new[] { currentPage },
            skip.IsChecked == true);
    }
}
