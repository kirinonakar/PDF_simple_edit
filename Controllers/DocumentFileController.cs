using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PDF_simple_edit.Controllers;

/// <summary>Coordinates file pickers, password prompts, saving and printing.</summary>
public sealed class DocumentFileController(
    EditorDialogService dialogService,
    PdfSaveService saveService,
    PrintHelper printHelper,
    TextBlock statusText,
    ProgressRing loadingIndicator,
    Func<PdfDocumentManager> getManager,
    Func<List<PdfAnnotation>> getAnnotations,
    Func<PdfDocumentTab?> getActiveTab,
    Action<PdfDocumentTab> addTab,
    Action<string> addRecentFile,
    Func<IntPtr> getWindowHandle,
    Func<XamlRoot> getXamlRoot,
    Func<Task> finishInlineEditAsync,
    Func<Task> renderCurrentPageAsync,
    Action updateUIState,
    Func<string, string, Task> showErrorAsync)
{
    private bool _isSaveInProgress;

    public void NewDocument()
    {
        var newTab = new PdfDocumentTab { Header = "새 문서" };
        newTab.PdfManager.NewDocument();

        addTab(newTab);

        // SelectionChanged will handle the rest
    }

    public async Task OpenFilesAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var hwnd = getWindowHandle();
        InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            foreach (var file in files)
            {
                await OpenPdfFileAsync(file);
            }
        }
    }

    public async Task OpenPdfFileAsync(StorageFile file)
    {
        loadingIndicator.IsActive = true;
        statusText.Text = "파일을 여는 중...";

        try
        {
            var newTab = new PdfDocumentTab
            {
                Header = file.Name,
                FilePath = file.Path
            };

            PdfOpenStatus status = await newTab.PdfManager.OpenWithPasswordAsync(file.Path, null);
            bool isRetry = false;
            while (status is PdfOpenStatus.PasswordRequired or PdfOpenStatus.WrongPassword)
            {
                statusText.Text = "암호 입력 대기 중...";
                string? password = await dialogService.ShowPasswordPromptAsync(getXamlRoot(), file.Name, isRetry);
                if (password == null)
                {
                    // 사용자가 암호 입력을 취소하면 파일을 열지 않는다.
                    statusText.Text = "파일 열기가 취소되었습니다.";
                    return;
                }

                status = await newTab.PdfManager.OpenWithPasswordAsync(file.Path, password);
                isRetry = true;
            }

            if (status == PdfOpenStatus.Success)
            {
                newTab.Annotations.AddRange(await Task.Run(newTab.PdfManager.LoadSavedSignatures));
                addTab(newTab);
                addRecentFile(file.Path);
            }
            else
            {
                await showErrorAsync("오류", "PDF 파일을 열 수 없습니다.");
            }
        }
        catch (Exception ex)
        {
            await showErrorAsync("오류", $"파일을 여는 중 오류가 발생했습니다: {ex.Message}");
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    public async Task SaveAsync()
    {
        if (!getManager().IsLoaded) return;

        if (getManager().FilePath is string filePath)
        {
            if (await PerformSaveAsync(filePath, true))
                addRecentFile(filePath);
        }
        else
        {
            await SaveAsAsync();
        }
    }

    public async Task SaveAsAsync()
    {
        if (!getManager().IsLoaded) return;

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("PDF 파일", new List<string> { ".pdf" });
        picker.SuggestedFileName = getManager().FilePath != null
            ? Path.GetFileName(getManager().FilePath) : "새문서.pdf";

        var hwnd = getWindowHandle();
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            if (await PerformSaveAsync(file.Path, true))
                addRecentFile(file.Path);
        }
    }

    public async Task PrintAsync()
    {
        if (!getManager().IsLoaded) return;

        try
        {
            // 인쇄 시에는 현재 모든 어노테이션이 반영된 상태여야 하므로 임시 파일로 플래트닝하여 저장
            string tempPath = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid()}.pdf");
            if (!await PerformSaveAsync(tempPath, false))
                return;

            var hwnd = getWindowHandle();
            // PerformSaveAsync handles iText 9 document flushing
            await printHelper.PrintAsync(tempPath, hwnd);
        }
        catch (Exception ex)
        {
            await showErrorAsync("인쇄 오류", $"인쇄 중 오류가 발생했습니다: {ex.Message}");
        }
    }

    private async Task<bool> PerformSaveAsync(string filePath, bool isUserSave)
    {
        if (!getManager().IsLoaded || _isSaveInProgress)
            return false;

        _isSaveInProgress = true;
        statusText.Text = isUserSave ? "저장 중..." : "렌더링 준비 중...";
        loadingIndicator.IsActive = true;

        try
        {
            // LostFocus에서 시작된 비동기 확정도 끝까지 기다려야 마지막 입력
            // (특히 IME 입력 직후의 문장부호)이 저장에서 빠지지 않습니다.
            await finishInlineEditAsync();

            await saveService.SaveAsync(
                getManager(),
                getAnnotations(),
                filePath,
                isUserSave);

            if (isUserSave)
            {
                if (getActiveTab() is { } activeTab)
                    activeTab.FilePath = filePath;

                // 이벤트 큐의 실행 순서와 무관하게 최종 화면이 방금 저장한
                // PDF를 사용하도록 한 번 더 명시적으로 갱신합니다.
                await renderCurrentPageAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            await showErrorAsync("저장 오류", $"저장 중 오류가 발생했습니다: {ex.Message}");
            return false;
        }
        finally
        {
            loadingIndicator.IsActive = false;
            _isSaveInProgress = false;
            updateUIState();
        }
    }
}
