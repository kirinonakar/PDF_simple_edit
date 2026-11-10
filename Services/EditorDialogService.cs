using Loc = PDF_simple_edit.Services.LocalizationService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PDF_simple_edit.Controls;
using PDF_simple_edit.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed record EditorPreferences(double RenderScale, string FontFamily, double FontSize, string Language);

public sealed class EditorDialogService
{
    private static readonly string[] FontFamilies =
    {
        "맑은 고딕", "굴림", "돋움", "바탕", "궁서",
        "나눔고딕", "나눔명조", "Arial", "Times New Roman"
    };

    private static readonly string[] FontSizes =
    {
        "8", "9", "10", "11", "12", "14", "16", "18",
        "20", "24", "28", "32", "36", "48", "72"
    };

    public async Task<EditorPreferences> ShowSettingsAsync(
        XamlRoot xamlRoot,
        double renderScale,
        TextFontSettings fontSettings,
        string languagePreference,
        EditorTheme themePreference,
        Action<EditorTheme> changeTheme)
    {
        var theme = new ComboBox { Header = Loc.Get("Theme"), MinWidth = 240 };
        theme.Items.Add(new ComboBoxItem { Content = Loc.Get("FollowWindowsTheme"), Tag = EditorTheme.System });
        theme.Items.Add(new ComboBoxItem { Content = Loc.Get("LightTheme"), Tag = EditorTheme.Light });
        theme.Items.Add(new ComboBoxItem { Content = Loc.Get("DarkTheme"), Tag = EditorTheme.Dark });
        theme.SelectedIndex = themePreference switch
        {
            EditorTheme.Light => 1,
            EditorTheme.Dark => 2,
            _ => 0
        };

        var language = new ComboBox { Header = Loc.Get("LanguageLabel"), MinWidth = 240 };
        language.Items.Add(new ComboBoxItem { Content = Loc.Get("FollowWindowsLanguage"), Tag = Loc.SystemLanguage });
        language.Items.Add(new ComboBoxItem { Content = Loc.Get("LanguageKorean"), Tag = Loc.Korean });
        language.Items.Add(new ComboBoxItem { Content = Loc.Get("LanguageEnglish"), Tag = Loc.English });
        language.SelectedIndex = Loc.NormalizePreference(languagePreference) switch
        {
            Loc.Korean => 1,
            Loc.English => 2,
            _ => 0
        };
        var renderQuality = new ComboBox { Header = Loc.Get("RenderingQuality"), Width = 200 };
        renderQuality.Items.Add(new ComboBoxItem { Content = Loc.Get("LowFast"), Tag = 1.0 });
        renderQuality.Items.Add(new ComboBoxItem { Content = Loc.Get("Normal"), Tag = 1.5 });
        renderQuality.Items.Add(new ComboBoxItem { Content = Loc.Get("High"), Tag = 2.0 });
        renderQuality.Items.Add(new ComboBoxItem { Content = Loc.Get("HighestSlow"), Tag = 3.0 });
        renderQuality.SelectedIndex = renderScale switch { 1.0 => 0, 1.5 => 1, 3.0 => 3, _ => 2 };

        var fontFamily = new ComboBox { Header = Loc.Get("DefaultFont"), Width = 200 };
        foreach (string family in FontFamilies
            .Concat(InstalledFontService.GetInstalledNotoFamilies())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            fontFamily.Items.Add(new ComboBoxItem { Content = family });
        }
        fontFamily.SelectedItem = fontFamily.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Content?.ToString(), fontSettings.FontFamily, StringComparison.OrdinalIgnoreCase));

        var fontSize = new ComboBox { Header = Loc.Get("DefaultFontSize"), Width = 150 };
        foreach (string size in FontSizes)
            fontSize.Items.Add(size);
        fontSize.SelectedItem = fontSettings.FontSize.ToString("0.##");

        var panel = new StackPanel { Spacing = 16, MinWidth = 400 };
        panel.Children.Add(language);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("LanguageRestartNotice"),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(theme);
        panel.Children.Add(renderQuality);
        panel.Children.Add(fontFamily);
        panel.Children.Add(fontSize);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("SettingsAreSavedAutomaticallyAndRestoredTheNextTime"),
            Opacity = 0.5,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var dialog = new EditorContentDialog(xamlRoot)
        {
            Title = Loc.Get("Preferences"),
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(180, Math.Min(580, xamlRoot.Size.Height - 220)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = Loc.Get("Close")
        };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem { Tag: EditorTheme selectedTheme })
            {
                changeTheme(selectedTheme);
                dialog.ApplyWindowTheme();
            }
        };
        await dialog.ShowAsync();

        double selectedScale = (renderQuality.SelectedItem as ComboBoxItem)?.Tag as double? ?? renderScale;
        string selectedFamily = (fontFamily.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? fontSettings.FontFamily;
        double selectedSize = double.TryParse(fontSize.SelectedItem?.ToString(), out double parsedSize)
            ? parsedSize
            : fontSettings.FontSize;
        string selectedLanguage = (language.SelectedItem as ComboBoxItem)?.Tag as string ?? languagePreference;
        return new EditorPreferences(selectedScale, selectedFamily, selectedSize, selectedLanguage);
    }

    public async Task ShowAboutAsync(XamlRoot xamlRoot)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/pdf_edit.png")),
            Width = 96,
            Height = 96,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "PDF Simple Editor",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        // Assembly version is populated from Package.appxmanifest at build time.
        var version = typeof(EditorDialogService).Assembly.GetName().Version;
        panel.Children.Add(new TextBlock { Text = Loc.Format("Version", version) });
        panel.Children.Add(new TextBlock { Text = Loc.Get("PDFEditorBuiltWithWinUI3IText9"), Opacity = 0.7 });
        panel.Children.Add(new HyperlinkButton
        {
            Content = "GitHub: kirinonakar/PDF_simple_edit",
            NavigateUri = new Uri("https://github.com/kirinonakar/PDF_simple_edit"),
            Padding = new Thickness(0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("ThirdPartyLibraries"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "iText 9 Core 9.5.0 — GNU AGPL v3 (© iText Group NV)\n"
                + "iText Bouncy Castle adapter 9.5.0 — GNU AGPL v3 (© iText Group NV)\n"
                + "iText pdfSweep 5.0.5 — GNU AGPL v3 (© iText Group NV)\n"
                + "Bouncy Castle Cryptography 2.6.2 — MIT License\n"
                + "Windows App SDK 1.8.260209005 — Microsoft Software License Terms",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7
        });

        await new EditorContentDialog(xamlRoot)
        {
            Title = Loc.Get("AboutPDFSimpleEditor"),
            Content = panel,
            CloseButtonText = Loc.Get("Close")
        }.ShowAsync();
    }

    public Task ShowErrorAsync(XamlRoot xamlRoot, string title, string message) =>
        new EditorContentDialog(xamlRoot)
        {
            Title = title,
            Content = message,
            CloseButtonText = Loc.Get("OK")
        }.ShowAsync().AsTask();

    /// <summary>
    /// 암호로 보호된 PDF를 열 때 사용할 암호를 입력받는다.
    /// 사용자가 취소하면 null을 반환한다.
    /// </summary>
    public async Task<string?> ShowPasswordPromptAsync(XamlRoot xamlRoot, string fileName, bool isRetry)
    {
        var passwordBox = new PasswordBox
        {
            PlaceholderText = Loc.Get("PDFPassword"),
            Width = 320,
            PasswordRevealMode = PasswordRevealMode.Peek
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = isRetry
                ? Loc.Get("IncorrectPasswordPleaseTryAgain")
                : Loc.Format("IsPasswordProtectedEnterThePasswordToOpenThe", fileName),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(passwordBox);

        var dialog = new EditorContentDialog(xamlRoot)
        {
            Title = Loc.Get("EnterPassword"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Open"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Opened += (_, _) => passwordBox.Focus(FocusState.Programmatic);

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }
}
