using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PDF_simple_edit.Controllers;

/// <summary>Coordinates merge, split, page deletion and page export workflows.</summary>
public sealed class PdfPageOperationsController(
    PdfOperationService operationService,
    TextBlock statusText,
    ProgressRing loadingIndicator,
    Func<PdfDocumentManager> getManager,
    Func<PdfDocumentTab?> getActiveTab,
    Func<int> getCurrentPageIndex,
    Action<int> setCurrentPageIndex,
    Func<List<int>> getSelectedPageIndices,
    Action clearPageSelection,
    Action<PdfDocumentTab> addTab,
    Func<IntPtr> getWindowHandle,
    Func<XamlRoot> getXamlRoot,
    Action updateUIState,
    Func<string, string, Task> showErrorAsync)
{
    public async Task MergeAsync()
    {
        var hwnd = getWindowHandle();
        PdfMergeRequest? request = await operationService.CreateMergeRequestAsync(
            getXamlRoot(), hwnd, getManager().IsLoaded);
        if (request == null)
            return;

        PdfDocumentTab? newTab = null;
        PdfDocumentManager manager = getManager();
        if (!getManager().IsLoaded)
        {
            newTab = new PdfDocumentTab();
            manager = newTab.PdfManager;
        }

        statusText.Text = "PDF 합치기 중...";
        loadingIndicator.IsActive = true;
        try
        {
            string? outputPath = await operationService.ExecuteMergeAsync(manager, request);
            if (outputPath == null)
            {
                await showErrorAsync("오류", "PDF 합치기에 실패했습니다.");
                return;
            }

            if (newTab != null)
            {
                newTab.FilePath = outputPath;
                newTab.Header = Path.GetFileName(outputPath);
                addTab(newTab);
            }
            else if (getActiveTab() is { } activeTab)
                activeTab.FilePath = outputPath;

            setCurrentPageIndex(0);
            statusText.Text = "PDF 합치기 완료";
            updateUIState();
        }
        catch (Exception ex)
        {
            await showErrorAsync("오류", $"합치기 중 에러 발생: {ex.Message}");
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    public async Task SplitAsync()
    {
        if (!getManager().IsLoaded) return;

        PdfSplitRequest? request = await operationService.CreateSplitRequestAsync(
            getXamlRoot(),
            getWindowHandle(),
            getManager().PageCount);
        if (request == null)
            return;
        if (request.Ranges.Count == 0)
        {
            statusText.Text = "나눌 페이지 범위가 올바르지 않습니다.";
            return;
        }

        statusText.Text = "PDF 나누기 중...";
        loadingIndicator.IsActive = true;
        try
        {
            int resultCount = await getManager().SplitFileAsync(
                request.OutputFolder, request.Ranges.ToList());
            statusText.Text = $"PDF 나누기 완료: {resultCount}개 파일 생성됨";
        }
        catch (Exception ex)
        {
            await showErrorAsync("나누기 오류", $"나누기 중 에러 발생: {ex.Message}");
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    public async Task DeletePagesAsync()
    {
        if (!getManager().IsLoaded)
            return;

        List<int> pageIndices = getSelectedPageIndices();
        if (pageIndices.Count == 0)
            pageIndices.Add(getCurrentPageIndex());
        if (pageIndices.Count == 0 || pageIndices.Count >= getManager().PageCount)
            return;

        int pageCountBeforeDelete = getManager().PageCount;
        int currentPageBeforeDelete = getCurrentPageIndex();
        string pageDescription = pageIndices.Count == 1
            ? $"페이지 {pageIndices[0] + 1}"
            : $"선택한 {pageIndices.Count}개 페이지";

        var dialog = new ContentDialog
        {
            Title = "페이지 삭제",
            Content = $"{pageDescription}를 삭제하시겠습니까?",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            XamlRoot = getXamlRoot()
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            int deletedBeforeCurrent = pageIndices.Count(index => index < currentPageBeforeDelete);
            getManager().DeletePages(pageIndices);
            clearPageSelection();
            setCurrentPageIndex(Math.Clamp(
                currentPageBeforeDelete - deletedBeforeCurrent,
                0, Math.Max(getManager().PageCount - 1, 0)));

            // Document events refresh the page image and thumbnails after deletion.
            updateUIState();
            statusText.Text = pageCountBeforeDelete == getManager().PageCount + pageIndices.Count
                ? "페이지가 삭제되었습니다"
                : "페이지 삭제에 실패했습니다";
        }
    }

    public async Task ExtractSelectedPagesAsync()
    {
        if (!getManager().IsLoaded)
            return;

        List<int> pageIndices = getSelectedPageIndices();
        if (pageIndices.Count == 0)
            return;

        var picker = new FileSavePicker
        {
            SuggestedFileName = GetSuggestedExtractFileName()
        };
        picker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
        InitializeWithWindow.Initialize(picker, getWindowHandle());

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        statusText.Text = "페이지 추출 중...";
        loadingIndicator.IsActive = true;
        try
        {
            bool success = await getManager().ExportPagesAsync(file.Path, pageIndices);
            statusText.Text = success
                ? $"페이지 추출 완료: {pageIndices.Count}개 페이지"
                : "페이지 추출에 실패했습니다";
        }
        catch (Exception ex)
        {
            await showErrorAsync("페이지 추출 오류", $"페이지 추출 중 오류가 발생했습니다: {ex.Message}");
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    private string GetSuggestedExtractFileName()
    {
        string baseName = !string.IsNullOrWhiteSpace(getManager().FilePath)
            ? Path.GetFileNameWithoutExtension(getManager().FilePath) ?? "문서"
            : "문서";
        return $"{baseName}_추출.pdf";
    }
}
