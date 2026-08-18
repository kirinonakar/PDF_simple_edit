using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed record EditorPreferences(double RenderScale, string FontFamily, double FontSize);

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
        TextFontSettings fontSettings)
    {
        var renderQuality = new ComboBox { Header = "렌더링 품질", Width = 200 };
        renderQuality.Items.Add(new ComboBoxItem { Content = "낮음 (빠름)", Tag = 1.0 });
        renderQuality.Items.Add(new ComboBoxItem { Content = "보통", Tag = 1.5 });
        renderQuality.Items.Add(new ComboBoxItem { Content = "높음", Tag = 2.0 });
        renderQuality.Items.Add(new ComboBoxItem { Content = "최고 (느림)", Tag = 3.0 });
        renderQuality.SelectedIndex = renderScale switch { 1.0 => 0, 1.5 => 1, 3.0 => 3, _ => 2 };

        var fontFamily = new ComboBox { Header = "기본 폰트", Width = 200 };
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

        var fontSize = new ComboBox { Header = "기본 글자 크기", Width = 150 };
        foreach (string size in FontSizes)
            fontSize.Items.Add(size);
        fontSize.SelectedItem = fontSettings.FontSize.ToString("0.##");

        var panel = new StackPanel { Spacing = 16, MinWidth = 400 };
        panel.Children.Add(renderQuality);
        panel.Children.Add(fontFamily);
        panel.Children.Add(fontSize);
        panel.Children.Add(new TextBlock
        {
            Text = "설정은 자동으로 저장되며 다음 실행에도 적용됩니다.",
            Opacity = 0.5,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var dialog = new ContentDialog
        {
            Title = "환경 설정",
            Content = panel,
            CloseButtonText = "닫기",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();

        double selectedScale = (renderQuality.SelectedItem as ComboBoxItem)?.Tag as double? ?? renderScale;
        string selectedFamily = (fontFamily.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? fontSettings.FontFamily;
        double selectedSize = double.TryParse(fontSize.SelectedItem?.ToString(), out double parsedSize)
            ? parsedSize
            : fontSettings.FontSize;
        return new EditorPreferences(selectedScale, selectedFamily, selectedSize);
    }

    public async Task ShowAboutAsync(XamlRoot xamlRoot)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "PDF Simple Editor",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        panel.Children.Add(new TextBlock { Text = "버전 1.0.0" });
        panel.Children.Add(new TextBlock { Text = "WinUI 3 + iText 9 기반 PDF 편집기", Opacity = 0.7 });
        panel.Children.Add(new TextBlock { Text = "한글 폰트 지원", Opacity = 0.7 });
        panel.Children.Add(new HyperlinkButton
        {
            Content = "GitHub: kirinonakar/PDF_simple_edit",
            NavigateUri = new Uri("https://github.com/kirinonakar/PDF_simple_edit"),
            Padding = new Thickness(0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "\n기능:\n• PDF 열기/저장/인쇄\n• 텍스트 추가/편집/바꾸기\n• 텍스트 강조 표시\n• 이미지 삽입\n• PDF 합치기/나누기\n• 찾기 및 바꾸기\n• 한글 폰트 지원",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        panel.Children.Add(new TextBlock
        {
            Text = "\nLibraries: iText 9 Core & pdfSweep (AGPL v3, © iText Group NV)",
            FontSize = 11,
            Opacity = 0.6
        });

        await new ContentDialog
        {
            Title = "PDF Simple Editor 정보",
            Content = panel,
            CloseButtonText = "닫기",
            XamlRoot = xamlRoot
        }.ShowAsync();
    }

    public Task ShowErrorAsync(XamlRoot xamlRoot, string title, string message) =>
        new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "확인",
            XamlRoot = xamlRoot
        }.ShowAsync().AsTask();
}