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
                Title = "PDF 합치기",
                Content = $"선택한 {files.Count}개 파일을 현재 문서에 합치시겠습니까?",
                PrimaryButtonText = "현재 문서에 합치기",
                SecondaryButtonText = "새 파일로 저장",
                CloseButtonText = "취소",
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

    public async Task<string?> ExecuteMergeAsync(
        PdfDocumentManager manager,
        PdfMergeRequest request)
    {
        string outputPath;
        if (request.Target == PdfMergeTarget.CurrentDocument)
        {
            outputPath = manager.FilePath ?? System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.TemporaryFolder.Path,
                $"merging_{Guid.NewGuid()}.pdf");
            if (!await manager.SaveAsAsync(outputPath, false))
                return null;
        }
        else
        {
            outputPath = request.OutputPath!;
        }

        bool success = await manager.MergeFilesAsync(request.SourcePaths.ToList(), outputPath);
        if (!success || !await manager.OpenAsync(outputPath))
            return null;
        return outputPath;
    }

    public async Task<PdfSplitRequest?> CreateSplitRequestAsync(
        XamlRoot xamlRoot,
        IntPtr windowHandle,
        int totalPages)
    {
        var everyPage = new RadioButton
        {
            Content = "페이지별로 나누기",
            IsChecked = true,
            GroupName = "SplitMode"
        };
        var byPageCount = new RadioButton
        {
            Content = "지정 페이지 수로 나누기",
            GroupName = "SplitMode"
        };
        var byRange = new RadioButton
        {
            Content = "페이지 범위로 나누기",
            GroupName = "SplitMode"
        };
        var pagesPerFile = new NumberBox
        {
            Value = 1,
            Minimum = 1,
            Maximum = totalPages,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Header = "파일당 페이지 수:",
            Width = 150,
            Visibility = Visibility.Collapsed
        };
        var rangesText = new TextBox
        {
            PlaceholderText = "예: 1-3, 4-6, 7-10",
            Header = "페이지 범위 (쉼표로 구분):",
            Visibility = Visibility.Collapsed
        };

        everyPage.Checked += (_, _) => SetSplitInputVisibility(false, false, pagesPerFile, rangesText);
        byPageCount.Checked += (_, _) => SetSplitInputVisibility(true, false, pagesPerFile, rangesText);
        byRange.Checked += (_, _) => SetSplitInputVisibility(false, true, pagesPerFile, rangesText);

        var panel = new StackPanel { Spacing = 12, MinWidth = 350 };
        panel.Children.Add(new TextBlock { Text = $"총 {totalPages} 페이지" });
        panel.Children.Add(everyPage);
        panel.Children.Add(byPageCount);
        panel.Children.Add(pagesPerFile);
        panel.Children.Add(byRange);
        panel.Children.Add(rangesText);

        var dialog = new ContentDialog
        {
            Title = "PDF 나누기",
            Content = panel,
            PrimaryButtonText = "나누기",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
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
        picker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
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
            for (int page = 1; page <= totalPages; page += pagesPerFile)
                ranges.Add((page, Math.Min(page + pagesPerFile - 1, totalPages)));
            return ranges;
        }

        foreach (string part in rangeText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] bounds = part.Trim().Split('-');
            if (bounds.Length == 1 && int.TryParse(bounds[0], out int page))
                ranges.Add((page, page));
            else if (bounds.Length == 2 &&
                     int.TryParse(bounds[0].Trim(), out int start) &&
                     int.TryParse(bounds[1].Trim(), out int end))
                ranges.Add((start, end));
        }
        return ranges;
    }
}
