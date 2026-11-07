using Loc = PDF_simple_edit.Services.LocalizationService;
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

/// <summary>Coordinates page geometry, merge, split, deletion and export workflows.</summary>
public sealed class PdfPageOperationsController(
    PdfOperationService operationService,
    PdfSaveService saveService,
    Func<Task> finishInlineEditAsync,
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
    Action clearObjectSelection,
    Func<string, string, Task> showErrorAsync)
{
    private bool _exportingPageImages;
    private bool _changingGeometry;

    public async Task ChangePageGeometryAsync(bool resize)
    {
        if (_changingGeometry || !getManager().IsLoaded || getActiveTab() is not { } tab) return;
        var manager = getManager();
        _changingGeometry = true;
        bool started = false;
        try
        {
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            long version = manager.EditVersion;
            var size = manager.GetPhysicalPageSize(getCurrentPageIndex());
            var options = await new PdfPageGeometryDialogService().ShowAsync(getXamlRoot(), resize,
                manager.PageCount, getCurrentPageIndex(), getSelectedPageIndices(), size.width, size.height);
            if (options == null || !ReferenceEquals(manager, getManager()) || manager.EditVersion != version) return;
            started = true;
            loadingIndicator.IsActive = true;
            statusText.Text = Loc.Get("ChangingPageGeometry");
            await saveService.ChangePageGeometryAsync(manager, tab.Annotations, options);
            if (!ReferenceEquals(manager, getManager())) return;
            clearObjectSelection();
            updateUIState();
            statusText.Text = Loc.Format(resize ? "PageSizesChanged" : "PagesRotated", options.PageIndices.Count);
        }
        catch (Exception ex)
        {
            statusText.Text = Loc.Get("PageGeometryFailed");
            await showErrorAsync(Loc.Get("PageGeometryFailed"), ex.Message);
        }
        finally
        {
            _changingGeometry = false;
            if (started) loadingIndicator.IsActive = false;
        }
    }

    public async Task ExportPageImagesAsync(int? singlePageIndex = null)
    {
        if (_exportingPageImages || !getManager().IsLoaded || getActiveTab() is not { } tab) return;
        var manager = getManager();
        long version = manager.EditVersion;
        _exportingPageImages = true;
        bool started = false;
        int savedCount = 0;
        try
        {
            PdfPageImageExportOptions? options = singlePageIndex is int index
                ? new PdfPageImageExportOptions(new[] { index }, 300)
                : await new PdfPageImageExportDialogService().ShowAsync(getXamlRoot(), manager.PageCount);
            if (options == null || !ReferenceEquals(manager, getManager()) || version != manager.EditVersion) return;

            string baseName = Path.GetFileNameWithoutExtension(manager.FilePath) ?? Loc.Get("Document");
            string? path;
            if (options.PageIndices.Count == 1)
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = $"{baseName}_page_{options.PageIndices[0] + 1:0000}.png"
                };
                picker.FileTypeChoices.Add(Loc.Get("PNGImage"), new List<string> { ".png" });
                InitializeWithWindow.Initialize(picker, getWindowHandle());
                path = (await picker.PickSaveFileAsync())?.Path;
            }
            else
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, getWindowHandle());
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            if (path == null || !ReferenceEquals(manager, getManager()) || version != manager.EditVersion) return;

            started = true;
            loadingIndicator.IsActive = true;
            statusText.Text = Loc.Format("ExportingPageImages", 0, options.PageIndices.Count);
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            byte[] snapshot = await saveService.CreateAnnotatedBytesAsync(manager, tab.Annotations);
            var exporter = new PdfPageImageExportService();
            if (options.PageIndices.Count == 1)
            {
                await exporter.ExportPageAsync(snapshot, options.PageIndices[0], path, options.Dpi);
                savedCount = 1;
            }
            else
            {
                var progress = new Progress<int>(count =>
                {
                    savedCount = count;
                    statusText.Text = Loc.Format("ExportingPageImages", count, options.PageIndices.Count);
                });
                await exporter.ExportPagesAsync(snapshot, options.PageIndices, path, baseName, options.Dpi, progress);
            }
            statusText.Text = Loc.Format("PageImagesSaved", options.PageIndices.Count);
        }
        catch (Exception ex)
        {
            statusText.Text = Loc.Format("PageImageExportFailed", savedCount);
            await showErrorAsync(Loc.Get("PageImageExportError"), ex.Message);
        }
        finally
        {
            _exportingPageImages = false;
            if (started) loadingIndicator.IsActive = false;
        }
    }

    public async Task ImagesToPdfAsync()
    {
        var imagePicker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" })
            imagePicker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(imagePicker, getWindowHandle());

        var images = await imagePicker.PickMultipleFilesAsync();
        if (images == null || images.Count == 0)
            return;

        var savePicker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(images[0].Name)}.pdf"
        };
        savePicker.FileTypeChoices.Add(Loc.Get("PDFFile"), new List<string> { ".pdf" });
        InitializeWithWindow.Initialize(savePicker, getWindowHandle());
        StorageFile? outputFile = await savePicker.PickSaveFileAsync();
        if (outputFile == null)
            return;

        statusText.Text = Loc.Format("ConvertingImagesToPDF", images.Count);
        loadingIndicator.IsActive = true;
        try
        {
            await new PdfImageConversionService().CreateAsync(
                images.Select(image => image.Path).ToList(), outputFile.Path);

            var newTab = new PdfDocumentTab
            {
                Header = outputFile.Name,
                FilePath = outputFile.Path
            };
            if (await newTab.PdfManager.OpenAsync(outputFile.Path))
            {
                addTab(newTab);
                statusText.Text = Loc.Format("PDFCreatedPages", images.Count);
                updateUIState();
            }
            else
            {
                statusText.Text = Loc.Get("ThePDFWasSavedButCannotBeOpened");
                await showErrorAsync(Loc.Get("PDFOpeningError"), Loc.Format("CannotOpenTheCreatedPDF", outputFile.Path));
            }
        }
        catch (Exception ex)
        {
            statusText.Text = Loc.Get("CouldNotConvertImagesToPDF");
            await showErrorAsync(Loc.Get("ImageToPDFConversionError"), ex.Message);
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    public async Task MergeAsync()
    {
        var sourceTab = getActiveTab();
        var manager = getManager();
        PdfMergeRequest? request = await operationService.CreateMergeRequestAsync(
            getXamlRoot(), getWindowHandle(), manager.IsLoaded);
        if (request == null || !ReferenceEquals(manager, getManager())) return;
        statusText.Text = Loc.Get("MergingPDFs");
        loadingIndicator.IsActive = true;
        try
        {
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            if (request.Target == PdfMergeTarget.CurrentDocument)
            {
                // Keep existing overlays pending, so the merge remains one undoable
                // document edit and does not write to the original file.
                var signatures = await manager.MergeIntoDocumentAsync(request.SourcePaths);
                sourceTab!.Annotations.AddRange(signatures);
            }
            else
            {
                byte[]? annotated = manager.IsLoaded
                    ? await saveService.CreateAnnotatedBytesAsync(manager, sourceTab!.Annotations) : null;
                await operationService.ExecuteMergeAsync(manager, request, annotated);
                var newTab = new PdfDocumentTab { FilePath = request.OutputPath, Header = Path.GetFileName(request.OutputPath)! };
                if (await newTab.PdfManager.OpenWithPasswordAsync(request.OutputPath!, manager.SavePassword) != PdfOpenStatus.Success)
                    throw new IOException(Loc.Get("TheMergedFileWasSavedButCannotBeReopened"));
                newTab.PdfManager.SetProtection(manager.Protection);
                newTab.PdfManager.MarkModified(false);
                newTab.Annotations.AddRange(await Task.Run(newTab.PdfManager.LoadSavedSignatures));
                addTab(newTab);
            }
            statusText.Text = Loc.Get("PDFMergeComplete");
            updateUIState();
        }
        catch (Exception ex)
        {
            statusText.Text = Loc.Get("FailedToMergePDFs");
            await showErrorAsync(Loc.Get("MergeError"), ex.Message);
        }
        finally { loadingIndicator.IsActive = false; }
    }

    public async Task SplitAsync()
    {
        if (!getManager().IsLoaded) return;

        var manager = getManager();
        var tab = getActiveTab();
        PdfSplitRequest? request = await operationService.CreateSplitRequestAsync(
            getXamlRoot(),
            getWindowHandle(),
            getManager().PageCount);
        if (request == null || !ReferenceEquals(manager, getManager()))
            return;
        if (request.Ranges.Count == 0)
        {
            statusText.Text = Loc.Get("ThePageRangesToSplitAreInvalid");
            return;
        }

        statusText.Text = Loc.Get("SplittingPDF");
        loadingIndicator.IsActive = true;
        try
        {
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            byte[] annotated = await saveService.CreateAnnotatedBytesAsync(manager, tab!.Annotations);
            int resultCount = await manager.SplitFileAsync(
                request.OutputFolder, request.Ranges.ToList(), annotated);
            statusText.Text = Loc.Format("PDFSplitCompleteFilesCreated", resultCount);
        }
        catch (Exception ex)
        {
            statusText.Text = Loc.Get("FailedToSplitThePDF");
            await showErrorAsync(Loc.Get("SplitError"), Loc.Format("AnErrorOccurredWhileSplitting", ex.Message));
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

        var requestedManager = getManager();
        var requestedTab = getActiveTab();
        long requestedVersion = requestedManager.EditVersion;
        int pageCountBeforeDelete = requestedManager.PageCount;
        int currentPageBeforeDelete = getCurrentPageIndex();
        string pageDescription = pageIndices.Count == 1
            ? Loc.Format("Page2", pageIndices[0] + 1)
            : Loc.Format("SelectedPages", pageIndices.Count);

        var dialog = new ContentDialog
        {
            Title = Loc.Get("DeletePages"),
            Content = Loc.Format("Delete", pageDescription),
            PrimaryButtonText = Loc.Get("Delete2"),
            CloseButtonText = Loc.Get("Cancel"),
            XamlRoot = getXamlRoot()
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(requestedManager, getManager()) || requestedManager.EditVersion != requestedVersion) return;
            int deletedBeforeCurrent = pageIndices.Count(index => index < currentPageBeforeDelete);
            var manager = requestedManager;
            try { await manager.DeletePagesAsync(pageIndices); }
            catch (Exception ex) { await showErrorAsync(Loc.Get("PageDeletionError"), ex.Message); return; }
            if (requestedTab != null)
            {
                requestedTab.Annotations.RemoveAll(a => pageIndices.Contains(a.PageIndex));
                foreach (var annotation in requestedTab.Annotations)
                    annotation.PageIndex -= pageIndices.Count(index => index < annotation.PageIndex);
                requestedTab.CurrentPageIndex = Math.Clamp(currentPageBeforeDelete - deletedBeforeCurrent,
                    0, Math.Max(manager.PageCount - 1, 0));
            }
            if (!ReferenceEquals(manager, getManager())) return;
            clearPageSelection();
            setCurrentPageIndex(Math.Clamp(currentPageBeforeDelete - deletedBeforeCurrent,
                0, Math.Max(manager.PageCount - 1, 0)));

            // Document events refresh the page image and thumbnails after deletion.
            updateUIState();
            statusText.Text = pageCountBeforeDelete == getManager().PageCount + pageIndices.Count
                ? Loc.Get("PagesDeleted")
                : Loc.Get("FailedToDeletePages");
        }
    }

    public async Task ExtractSelectedPagesAsync()
    {
        if (!getManager().IsLoaded)
            return;

        var manager = getManager();
        var tab = getActiveTab();
        List<int> pageIndices = getSelectedPageIndices();
        if (pageIndices.Count == 0)
            return;

        var picker = new FileSavePicker
        {
            SuggestedFileName = GetSuggestedExtractFileName()
        };
        picker.FileTypeChoices.Add(Loc.Get("PDFFile"), new List<string> { ".pdf" });
        InitializeWithWindow.Initialize(picker, getWindowHandle());

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null || !ReferenceEquals(manager, getManager()))
            return;

        statusText.Text = Loc.Get("ExtractingPages");
        loadingIndicator.IsActive = true;
        try
        {
            await finishInlineEditAsync();
            if (!ReferenceEquals(manager, getManager())) return;
            byte[] annotated = await saveService.CreateAnnotatedBytesAsync(manager, tab!.Annotations);
            bool success = await manager.ExportPagesAsync(file.Path, pageIndices, annotated);
            statusText.Text = success
                ? Loc.Format("ExtractedPages", pageIndices.Count)
                : Loc.Get("FailedToExtractPages");
        }
        catch (Exception ex)
        {
            statusText.Text = Loc.Get("FailedToExtractPages2");
            await showErrorAsync(Loc.Get("PageExtractionError"), Loc.Format("AnErrorOccurredWhileExtractingPages", ex.Message));
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    private string GetSuggestedExtractFileName()
    {
        string baseName = !string.IsNullOrWhiteSpace(getManager().FilePath)
            ? Path.GetFileNameWithoutExtension(getManager().FilePath) ?? Loc.Get("Document")
            : Loc.Get("Document");
        return Loc.Format("ExtractedPdf", baseName);
    }
}
