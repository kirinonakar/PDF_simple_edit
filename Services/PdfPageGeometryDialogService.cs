using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loc = PDF_simple_edit.Services.LocalizationService;

namespace PDF_simple_edit.Services;

public sealed class PdfPageGeometryDialogService
{
    private const double PointsPerMm = 72 / 25.4;

    public async Task<PdfPageGeometryOptions?> ShowAsync(XamlRoot root, bool resize, int pageCount,
        int currentPage, IReadOnlyList<int> selectedPages, double currentWidth, double currentHeight)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };
        var scope = new ComboBox { Header = Loc.Get("PageGeometryScope"), HorizontalAlignment = HorizontalAlignment.Stretch };
        scope.Items.Add(new ComboBoxItem { Content = Loc.Format("PageGeometryCurrent", currentPage + 1), Tag = "current" });
        scope.Items.Add(new ComboBoxItem { Content = Loc.Format("SelectedPages", selectedPages.Count), Tag = "selected", IsEnabled = selectedPages.Count > 0 });
        scope.Items.Add(new ComboBoxItem { Content = Loc.Get("ImageExportAllPages"), Tag = "all" });
        scope.Items.Add(new ComboBoxItem { Content = Loc.Get("ImageExportSpecifiedPages"), Tag = "specified" });
        scope.SelectedIndex = selectedPages.Count > 1 ? 1 : 0;
        var pages = new TextBox { Header = Loc.Get("ImageExportPageNumbers"), PlaceholderText = "1,3,5-8", Visibility = Visibility.Collapsed };
        scope.SelectionChanged += (_, _) => pages.Visibility = scope.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(scope); panel.Children.Add(pages);

        var rotation = new ComboBox { Header = Loc.Get("PageRotation"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (key, degrees) in new[] { ("RotateClockwise90", 90), ("Rotate180", 180), ("RotateCounterclockwise90", -90) })
            rotation.Items.Add(new ComboBoxItem { Content = Loc.Get(key), Tag = degrees });
        rotation.SelectedIndex = 0;
        var width = new NumberBox { Header = Loc.Get("PageWidthMm"), Value = Math.Round(currentWidth / PointsPerMm, 2),
            Minimum = .36, Maximum = 5080, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var height = new NumberBox { Header = Loc.Get("PageHeightMm"), Value = Math.Round(currentHeight / PointsPerMm, 2),
            Minimum = .36, Maximum = 5080, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var fit = new CheckBox { Content = Loc.Get("FitPageContent"), IsChecked = true };
        if (resize)
        {
            var preset = new ComboBox { Header = Loc.Get("PageSizePreset"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (name, w, h) in new[] { (Loc.Get("CustomPageSize"), 0d, 0d), ("A3", 297d, 420d), ("A4", 210d, 297d),
                ("A5", 148d, 210d), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6) })
                preset.Items.Add(new ComboBoxItem { Content = name, Tag = (w, h) });
            preset.SelectedIndex = 0;
            var landscape = new CheckBox { Content = Loc.Get("LandscapePage"), IsChecked = currentWidth > currentHeight };
            bool updatingDimensions = false;
            void ApplyPreset()
            {
                if (preset.SelectedIndex == 0) return;
                var (w, h) = ((double, double))((ComboBoxItem)preset.SelectedItem).Tag;
                SetDimensions(landscape.IsChecked == true ? h : w, landscape.IsChecked == true ? w : h);
            }
            void SetDimensions(double w, double h)
            {
                updatingDimensions = true;
                width.Value = w; height.Value = h;
                updatingDimensions = false;
            }
            preset.SelectionChanged += (_, _) => ApplyPreset();
            landscape.Checked += (_, _) => { if (width.Value < height.Value) SetDimensions(height.Value, width.Value); };
            landscape.Unchecked += (_, _) => { if (width.Value > height.Value) SetDimensions(height.Value, width.Value); };
            // Editing either dimension switches the preset label back to Custom.
            width.ValueChanged += (_, _) => UpdatePresetLabel();
            height.ValueChanged += (_, _) => UpdatePresetLabel();
            void UpdatePresetLabel()
            {
                if (updatingDimensions || preset.SelectedIndex == 0) return;
                var (w, h) = ((double, double))((ComboBoxItem)preset.SelectedItem).Tag;
                if (landscape.IsChecked == true) (w, h) = (h, w);
                if (Math.Abs(width.Value - w) > .01 || Math.Abs(height.Value - h) > .01) preset.SelectedIndex = 0;
            }
            panel.Children.Add(preset); panel.Children.Add(landscape);
            panel.Children.Add(width); panel.Children.Add(height); panel.Children.Add(fit);
            panel.Children.Add(new TextBlock { Text = Loc.Get("PageSizeDescription"), TextWrapping = TextWrapping.Wrap });
        }
        else panel.Children.Add(rotation);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red) };
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = Loc.Get(resize ? "PageSizeSettings" : "PageRotation"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(180, root.Size.Height - 220) },
            PrimaryButtonText = Loc.Get("ApplyPageGeometry"), CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary, XamlRoot = root
        };
        PdfPageGeometryOptions? options = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                // Commit typed NumberBox input before reading Value.
                dialog.Focus(FocusState.Programmatic);
                var indices = scope.SelectedIndex switch
                {
                    1 => selectedPages,
                    2 => Enumerable.Range(0, pageCount).ToArray(),
                    3 => PdfPageImageExportService.ParsePageIndices(pages.Text, pageCount),
                    _ => new[] { currentPage }
                };
                double w = width.Value * PointsPerMm, h = height.Value * PointsPerMm;
                if (resize && (!double.IsFinite(w) || !double.IsFinite(h) || w < 1 || h < 1 || w > 14400 || h > 14400))
                    throw new ArgumentException(Loc.Get("InvalidPageDimensions"));
                options = resize ? new(indices, Width: w, Height: h, FitContent: fit.IsChecked == true)
                    : new(indices, Rotation: (int)((ComboBoxItem)rotation.SelectedItem).Tag);
            }
            catch (ArgumentException ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? options : null;
    }
}
