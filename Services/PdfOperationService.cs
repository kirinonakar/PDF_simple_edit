using Loc = PDF_simple_edit.Services.LocalizationService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PDF_simple_edit.Services;

public enum PdfMergeTarget
{
    CurrentDocument,
    NewFile
}

public sealed record PdfMergeRequest(
    IReadOnlyList<string> SourcePaths,
    PdfMergeTarget Target,
    string? OutputPath);

public sealed record PdfSplitRequest(
    string OutputFolder,
    IReadOnlyList<(int start, int end)> Ranges);

public sealed class PdfOperationService
{
    public async Task<PdfMergeRequest?> CreateMergeRequestAsync(
        XamlRoot xamlRoot,
        IntPtr windowHandle,
        bool hasCurrentDocument)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, windowHandle);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0)
            return null;

        var sourcePaths = files.Select(file => file.Path).ToList();
        PdfMergeTarget target = PdfMergeTarget.NewFile;
        if (hasCurrentDocument)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("MergePDFs"),
                Content = Loc.Format("MergeTheSelectedFilesIntoTheCurrentDocument", files.Count),
                PrimaryButtonText = Loc.Get("MergeIntoCurrentDocument"),
                SecondaryButtonText = Loc.Get("SaveAsANewFile"),
                CloseButtonText = Loc.Get("Cancel"),
                XamlRoot = xamlRoot
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
                return null;
            target = result == ContentDialogResult.Primary
                ? PdfMergeTarget.CurrentDocument
                : PdfMergeTarget.NewFile;
        }

        string? outputPath = target == PdfMergeTarget.NewFile
            ? await PickMergedOutputPathAsync(windowHandle)
            : null;
        return target == PdfMergeTarget.NewFile && outputPath == null
            ? null
            : new PdfMergeRequest(sourcePaths, target, outputPath);
    }

    public async Task ExecuteMergeAsync(PdfDocumentManager manager, PdfMergeRequest request, byte[]? annotatedSnapshot)
    {
        if (request.Target != PdfMergeTarget.NewFile || string.IsNullOrWhiteSpace(request.OutputPath))
            throw new InvalidOperationException(Loc.Get("ASavePathIsRequiredForTheNewMerged"));
        await manager.MergeFilesAsync(request.SourcePaths.ToList(), request.OutputPath, annotatedSnapshot);
    }

    public async Task<PdfSplitRequest?> CreateSplitRequestAsync(
        XamlRoot xamlRoot,
        IntPtr windowHandle,
        int totalPages)
    {
        var everyPage = new RadioButton
        {
            Content = Loc.Get("SplitIntoIndividualPages"),
            IsChecked = true,
            GroupName = "SplitMode"
        };
        var byPageCount = new RadioButton
        {
            Content = Loc.Get("SplitByPageCount"),
            GroupName = "SplitMode"
        };
        var byRange = new RadioButton
        {
            Content = Loc.Get("SplitByPageRanges"),
            GroupName = "SplitMode"
        };
        var pagesPerFile = new NumberBox
        {
            Value = 1,
            Minimum = 1,
            Maximum = totalPages,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Header = Loc.Get("PagesPerFile"),
            Width = 150,
            Visibility = Visibility.Collapsed
        };
        var rangesText = new TextBox
        {
            PlaceholderText = Loc.Get("Example1346710"),
            Header = Loc.Get("PageRangesCommaSeparated"),
            Visibility = Visibility.Collapsed
        };

        everyPage.Checked += (_, _) => SetSplitInputVisibility(false, false, pagesPerFile, rangesText);
        byPageCount.Checked += (_, _) => SetSplitInputVisibility(true, false, pagesPerFile, rangesText);
        byRange.Checked += (_, _) => SetSplitInputVisibility(false, true, pagesPerFile, rangesText);

        var panel = new StackPanel { Spacing = 12, MinWidth = 350 };
        panel.Children.Add(new TextBlock { Text = Loc.Format("PagesInTotal", totalPages) });
        panel.Children.Add(everyPage);
        panel.Children.Add(byPageCount);
        panel.Children.Add(pagesPerFile);
        panel.Children.Add(byRange);
        panel.Children.Add(rangesText);

        var dialog = new ContentDialog
        {
            Title = Loc.Get("SplitPDF"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Split"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        var errorText = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (byPageCount.IsChecked == true && (!double.IsFinite(pagesPerFile.Value) ||
                    pagesPerFile.Value < 1 || pagesPerFile.Value > totalPages || pagesPerFile.Value != Math.Truncate(pagesPerFile.Value)))
                    throw new ArgumentException(Loc.Format("PagesPerFileMustBeAnIntegerBetween1", totalPages));
                BuildRanges(totalPages, everyPage.IsChecked == true, byPageCount.IsChecked == true,
                    (int)pagesPerFile.Value, rangesText.Text);
            }
            catch (ArgumentException ex) { args.Cancel = true; errorText.Text = ex.Message; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var ranges = BuildRanges(
            totalPages,
            everyPage.IsChecked == true,
            byPageCount.IsChecked == true,
            (int)pagesPerFile.Value,
            rangesText.Text);
        if (ranges.Count == 0)
            return new PdfSplitRequest(string.Empty, ranges);

        var folderPicker = new FolderPicker();
        folderPicker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(folderPicker, windowHandle);
        var folder = await folderPicker.PickSingleFolderAsync();
        return folder == null ? null : new PdfSplitRequest(folder.Path, ranges);
    }

    private static async Task<string?> PickMergedOutputPathAsync(IntPtr windowHandle)
    {
        var picker = new FileSavePicker { SuggestedFileName = "merged.pdf" };
        picker.FileTypeChoices.Add(Loc.Get("PDFFile"), new List<string> { ".pdf" });
        InitializeWithWindow.Initialize(picker, windowHandle);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private static void SetSplitInputVisibility(
        bool showPageCount,
        bool showRanges,
        NumberBox pagesPerFile,
        TextBox rangesText)
    {
        pagesPerFile.Visibility = showPageCount ? Visibility.Visible : Visibility.Collapsed;
        rangesText.Visibility = showRanges ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<(int start, int end)> BuildRanges(
        int totalPages,
        bool everyPage,
        bool byPageCount,
        int pagesPerFile,
        string rangeText)
    {
        var ranges = new List<(int start, int end)>();
        if (everyPage)
        {
            for (int page = 1; page <= totalPages; page++)
                ranges.Add((page, page));
            return ranges;
        }

        if (byPageCount)
        {
            if (pagesPerFile < 1) throw new ArgumentException(Loc.Get("PagesPerFileMustBeAtLeast1"));
            for (int page = 1; page <= totalPages; page += pagesPerFile)
                ranges.Add((page, Math.Min(page + pagesPerFile - 1, totalPages)));
            return ranges;
        }

        foreach (string part in rangeText.Split(','))
        {
            string[] bounds = part.Trim().Split('-');
            if (bounds.Length == 1 && int.TryParse(bounds[0], out int page))
                ranges.Add((page, page));
            else if (bounds.Length == 2 &&
                     int.TryParse(bounds[0].Trim(), out int start) &&
                     int.TryParse(bounds[1].Trim(), out int end))
                ranges.Add((start, end));
            else throw new ArgumentException(Loc.Format("CheckThePageRange", part));
        }
        foreach (var (start, end) in ranges) PdfFileOperationService.ValidateRange(start, end, totalPages);
        if (ranges.Count == 0) throw new ArgumentException(Loc.Get("EnterPageRanges"));
        return ranges;
    }
}
