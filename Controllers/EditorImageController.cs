using Loc = PDF_simple_edit.Services.LocalizationService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>Imports images and exports selected or embedded PDF images.</summary>
public sealed class EditorImageController(
    AnnotationCanvasController canvasController,
    PdfImageExtractionService extractionService,
    TextBlock statusText,
    ProgressRing loadingIndicator,
    Func<PdfDocumentManager> getManager,
    Func<List<PdfAnnotation>> getAnnotations,
    Func<int> getCurrentPageIndex,
    Func<IntPtr> getWindowHandle,
    Action<EditToolMode> setToolMode,
    Func<string, string, Task> showErrorAsync)
{
    public async Task AddImageAsync()
    {
        if (!getManager().IsLoaded) return;

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");

        var hwnd = getWindowHandle();
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            var pageSize = getManager().GetPageSize(getCurrentPageIndex());
            var imageProperties = await file.Properties.GetImagePropertiesAsync();
            (double w, double h) = ImageAnnotationLayoutService.CalculateInitialSize(
                imageProperties.Width,
                imageProperties.Height,
                pageSize.width,
                pageSize.height);
            double x = (pageSize.width - w) / 2;
            double y = (pageSize.height - h) / 2;

            var imageAnnotation = new PdfAnnotation
            {
                Type = AnnotationType.Image,
                PageIndex = getCurrentPageIndex(),
                X = x,
                Y = y,
                Width = w,
                Height = h,
                ImagePath = file.Path,
                IsApplied = false
            };
            getAnnotations().Add(imageAnnotation);
            getManager().MarkModified(pageIndex: getCurrentPageIndex());

            // 이미지를 추가한 뒤 바로 핸들이 보이도록 선택 도구로 전환하고
            // 새 이미지를 선택 상태로 둡니다.
            setToolMode(EditToolMode.SelectGraphics);
            canvasController.SelectOnly(imageAnnotation);
            statusText.Text = Loc.Get("ImageAddedAppliedWhenSaved");
            canvasController.Render();
        }
    }

    public async Task ExtractAllImagesAsync()
    {
        byte[]? pdfBytes = getManager().GetPdfBytes();
        if (pdfBytes == null)
            return;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, getWindowHandle());

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder == null)
            return;

        string documentName = !string.IsNullOrWhiteSpace(getManager().FilePath)
            ? Path.GetFileNameWithoutExtension(getManager().FilePath) ?? "document"
            : "document";

        statusText.Text = Loc.Get("ExtractingImages");
        loadingIndicator.IsActive = true;
        try
        {
            PdfImageExtractionResult result = await extractionService.ExtractAllAsync(
                pdfBytes,
                folder.Path,
                documentName);

            statusText.Text = result switch
            {
                { SavedCount: 0, FailedCount: 0 } => Loc.Get("NoImagesToExtract"),
                { FailedCount: 0 } => Loc.Format("ExtractedImages", result.SavedCount),
                _ => Loc.Format("ExtractedImagesFailed", result.SavedCount, result.FailedCount)
            };
        }
        catch (Exception ex)
        {
            await showErrorAsync(
                Loc.Get("ImageExtractionError"),
                Loc.Format("AnErrorOccurredWhileExtractingImages", ex.Message));
            statusText.Text = Loc.Get("FailedToExtractImages");
        }
        finally
        {
            loadingIndicator.IsActive = false;
        }
    }

    public async Task SaveSelectedImageAsync()
    {
        PdfAnnotation? selectedImage = canvasController.SelectedAnnotations.Count == 1
            ? canvasController.SelectedAnnotations[0]
            : null;
        string? sourcePath = selectedImage?.Type == AnnotationType.Image
            ? selectedImage.ImagePath
            : null;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            statusText.Text = Loc.Get("CannotFindTheImageFileToSave");
            return;
        }

        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";

        string documentName = !string.IsNullOrWhiteSpace(getManager().FilePath)
            ? Path.GetFileNameWithoutExtension(getManager().FilePath) ?? "document"
            : "document";
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"{documentName}_page_{selectedImage!.PageIndex + 1}_image{extension}"
        };
        picker.FileTypeChoices.Add(
            GetImageFileTypeDescription(extension),
            new List<string> { extension });
        InitializeWithWindow.Initialize(picker, getWindowHandle());

        StorageFile? targetFile = await picker.PickSaveFileAsync();
        if (targetFile == null)
            return;

        try
        {
            string sourceFullPath = Path.GetFullPath(sourcePath);
            string targetFullPath = Path.GetFullPath(targetFile.Path);
            if (!string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => File.Copy(sourceFullPath, targetFullPath, true));
            statusText.Text = Loc.Format("ImageSaved", targetFile.Name);
        }
        catch (Exception ex)
        {
            await showErrorAsync(
                Loc.Get("ImageSaveError"),
                Loc.Format("AnErrorOccurredWhileSavingTheImage", ex.Message));
            statusText.Text = Loc.Get("FailedToSaveTheImage");
        }
    }

    private static string GetImageFileTypeDescription(string extension) =>
        extension switch
        {
            ".jpg" or ".jpeg" => Loc.Get("JPEGImage"),
            ".tif" or ".tiff" => Loc.Get("TIFFImage"),
            ".jp2" => Loc.Get("JPEG2000Image"),
            ".bmp" => Loc.Get("BMPImage"),
            ".gif" => Loc.Get("GIFImage"),
            ".jbig2" => Loc.Get("JBIG2Image"),
            _ => Loc.Get("PNGImage")
        };
}
