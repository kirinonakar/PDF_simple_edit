using Loc = PDF_simple_edit.Services.LocalizationService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDF_simple_edit.Controls;
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
    Action<PdfDocumentTab, PdfDocumentManager, List<PdfAnnotation>> replaceTabDocument,
    Func<string, string, Task> showErrorAsync)
{
    private bool _isSaveInProgress;
    private bool _isReopenInProgress;
    private bool _isProtectionDialogOpen;
    private readonly PdfProtectionDialogService _protectionDialog = new();

    public async Task EditProtectionAsync(bool removeAll = false)
    {
        if (_isSaveInProgress || _isProtectionDialogOpen || !getManager().IsLoaded) return;
        var manager = getManager();
        try
        {
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            if (await RequestProtectionAsync(manager, removeAll))
            {
                updateUIState();
                statusText.Text = Loc.Get("ProtectionSettingsChangedSaveThePDFToApplyThem");
            }
        }
        catch (Exception ex)
        {
            await showErrorAsync(Loc.Get("ProtectionSettingsError"), ex.Message);
        }
    }

    private async Task<bool> RequestProtectionAsync(PdfDocumentManager manager, bool removeAll = false)
    {
        if (_isProtectionDialogOpen) return false;
        _isProtectionDialogOpen = true;
        try
        {
            PdfProtectionSettings? settings = removeAll
                ? await _protectionDialog.ConfirmRemoveAsync(getXamlRoot()) ? new PdfProtectionSettings() : null
                : await _protectionDialog.ShowAsync(getXamlRoot(), manager.Protection);
            if (settings == null || !manager.IsLoaded || !ReferenceEquals(manager, getManager())) return false;
            manager.SetProtection(settings);
            return true;
        }
        finally { _isProtectionDialogOpen = false; }
    }

    public void NewDocument()
    {
        var newTab = new PdfDocumentTab { Header = Loc.Get("NewDocument") };
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
        statusText.Text = Loc.Get("OpeningFile");

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
                statusText.Text = Loc.Get("WaitingForPassword");
                string? password = await dialogService.ShowPasswordPromptAsync(getXamlRoot(), file.Name, isRetry);
                if (password == null)
                {
                    // 사용자가 암호 입력을 취소하면 파일을 열지 않는다.
                    statusText.Text = Loc.Get("FileOpeningCanceled");
                    return;
                }

                status = await newTab.PdfManager.OpenWithPasswordAsync(file.Path, password);
                isRetry = true;
            }

            if (status == PdfOpenStatus.Success)
            {
                if (newTab.PdfManager.HasDigitalSignatures)
                    await showErrorAsync(Loc.Get("DigitallySignedDocument"), Loc.Get("ThisDocumentIsDigitallySignedTheSignatureOnAn"));
                newTab.Annotations.AddRange(await Task.Run(newTab.PdfManager.LoadSavedSignatures));
                addTab(newTab);
                addRecentFile(file.Path);
            }
            else
            {
                await showErrorAsync(Loc.Get("Error"), Loc.Get("CannotOpenThePDFFile"));
            }
        }
        catch (Exception ex)
        {
            await showErrorAsync(Loc.Get("Error"), Loc.Format("AnErrorOccurredWhileOpeningTheFile", ex.Message));
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    public async Task ReopenAsync()
    {
        if (_isReopenInProgress || _isSaveInProgress || _isProtectionDialogOpen ||
            !getManager().IsLoaded || string.IsNullOrEmpty(getManager().FilePath) || getActiveTab() is not { } tab) return;
        var manager = getManager();
        PdfDocumentManager? reopened = null;
        _isReopenInProgress = true;
        bool started = false;
        try
        {
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager()) || !ReferenceEquals(tab, getActiveTab())) return;
            long version = manager.EditVersion;
            if (manager.IsModified)
            {
                var dialog = new EditorContentDialog(getXamlRoot())
                {
                    Title = Loc.Get("SaveChanges"), Content = Loc.Format("HasChangedSaveYourChanges", tab.Header),
                    PrimaryButtonText = Loc.Get("Save"), SecondaryButtonText = Loc.Get("DonTSave"),
                    CloseButtonText = Loc.Get("Cancel")
                };
                var result = await dialog.ShowAsync();
                if (!ReferenceEquals(manager, getManager()) || manager.EditVersion != version || result == ContentDialogResult.None) return;
                if (result == ContentDialogResult.Primary && !await SaveAsync()) return;
            }
            if (!ReferenceEquals(manager, getManager()) || !ReferenceEquals(tab, getActiveTab())) return;
            // Save As may have changed the file path (for example a signed document).
            string path = manager.FilePath!;
            version = manager.EditVersion;
            started = true;
            loadingIndicator.IsActive = true;
            statusText.Text = Loc.Get("ReopeningFile");
            // Prepare a separate document first. Failed reads and cancelled password
            // prompts leave the current tab, its overlays and undo history intact.
            reopened = new PdfDocumentManager { TextEditingMode = manager.TextEditingMode };
            PdfOpenStatus status = await reopened.OpenWithPasswordAsync(path, manager.SavePassword);
            while (status is PdfOpenStatus.PasswordRequired or PdfOpenStatus.WrongPassword)
            {
                if (!ReferenceEquals(manager, getManager()) || manager.EditVersion != version) return;
                string? password = await dialogService.ShowPasswordPromptAsync(getXamlRoot(), Path.GetFileName(path),
                    status == PdfOpenStatus.WrongPassword);
                if (password == null) { statusText.Text = Loc.Get("FileOpeningCanceled"); return; }
                status = await reopened.OpenWithPasswordAsync(path, password);
            }
            if (status != PdfOpenStatus.Success)
            {
                statusText.Text = Loc.Get("CannotOpenThePDFFile");
                await showErrorAsync(Loc.Get("Error"), Loc.Get("CannotOpenThePDFFile"));
                return;
            }
            var annotations = await Task.Run(reopened.LoadSavedSignatures);
            if (!ReferenceEquals(manager, getManager()) || !ReferenceEquals(tab, getActiveTab()) || manager.EditVersion != version) return;
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager()) || !ReferenceEquals(tab, getActiveTab()) || manager.EditVersion != version) return;
            replaceTabDocument(tab, reopened, annotations);
            reopened = null; // Ownership transferred to the existing tab.
            addRecentFile(path);
            statusText.Text = Loc.Get("FileReopened");
        }
        catch (Exception ex)
        {
            await showErrorAsync(Loc.Get("Error"), Loc.Format("AnErrorOccurredWhileOpeningTheFile", ex.Message));
        }
        finally
        {
            reopened?.Close();
            _isReopenInProgress = false;
            if (started) loadingIndicator.IsActive = false;
            updateUIState();
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (!getManager().IsLoaded || _isSaveInProgress || _isProtectionDialogOpen) return false;

        if (getManager().RequiresSignedCopySave) return await SaveAsAsync();
        if (getManager().FilePath is string filePath)
        {
            if (await PerformSaveAsync(filePath, true))
            {
                addRecentFile(filePath);
                return true;
            }
            return false;
        }
        else
        {
            return await SaveAsAsync();
        }
    }

    public async Task<bool> SaveAsAsync()
    {
        if (!getManager().IsLoaded || _isSaveInProgress || _isProtectionDialogOpen) return false;
        var manager = getManager();

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(Loc.Get("PDFFile"), new List<string> { ".pdf" });
        picker.SuggestedFileName = getManager().FilePath != null
            ? Path.GetFileName(getManager().FilePath) : Loc.Get("NewDocumentPdf");

        var hwnd = getWindowHandle();
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file != null && ReferenceEquals(manager, getManager()))
        {
            if (await PerformSaveAsync(file.Path, true))
            {
                addRecentFile(file.Path);
                return true;
            }
        }
        return false;
    }

    public async Task PrintAsync()
    {
        if (!getManager().IsLoaded || _isSaveInProgress || printHelper.IsPrinting) return;
        string? tempPath = null;
        try
        {
            var manager = getManager();
            var annotations = getAnnotations();
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            byte[] bytes = await saveService.CreateAnnotatedBytesAsync(manager, annotations);
            tempPath = Path.Combine(PdfTemporaryFiles.GetDirectory("print"), $"print_{Guid.NewGuid():N}.pdf");
            await File.WriteAllBytesAsync(tempPath, bytes);
            await printHelper.PrintAsync(tempPath, getWindowHandle());
        }
        catch (Exception ex)
        {
            await showErrorAsync(Loc.Get("PrintingError"), Loc.Format("AnErrorOccurredWhilePrinting", ex.Message));
        }
        finally { PdfTemporaryFiles.TryDelete(tempPath); }
    }

    private async Task<bool> PerformSaveAsync(string filePath, bool isUserSave)
    {
        if (!getManager().IsLoaded || _isSaveInProgress || _isProtectionDialogOpen)
            return false;

        _isSaveInProgress = true;
        var manager = getManager();
        var annotations = getAnnotations();
        var tab = getActiveTab();
        statusText.Text = isUserSave ? Loc.Get("Saving") : Loc.Get("PreparingToRender");
        loadingIndicator.IsActive = true;

        try
        {
            // LostFocus에서 시작된 비동기 확정도 끝까지 기다려야 마지막 입력
            // (특히 IME 입력 직후의 문장부호)이 저장에서 빠지지 않습니다.
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return false;

            if (isUserSave && manager.Protection.NeedsPasswords &&
                !await RequestProtectionAsync(manager))
                return false;

            bool savedCurrent = await saveService.SaveAsync(manager, annotations, filePath, isUserSave);
            if (!savedCurrent)
            {
                await showErrorAsync(Loc.Get("SaveAgain"), Loc.Get("TheFileWasSavedButMoreEditsWereMade"));
                return false;
            }

            if (isUserSave)
            {
                if (tab != null)
                    tab.FilePath = filePath;

                // 이벤트 큐의 실행 순서와 무관하게 최종 화면이 방금 저장한
                // PDF를 사용하도록 한 번 더 명시적으로 갱신합니다.
                if (ReferenceEquals(manager, getManager()))
                    await renderCurrentPageAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            await showErrorAsync(Loc.Get("SaveError"), Loc.Format("AnErrorOccurredWhileSaving", ex.Message));
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
