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
            getManager().MarkModified();

            // 이미지를 추가한 뒤 바로 핸들이 보이도록 선택 도구로 전환하고
            // 새 이미지를 선택 상태로 둡니다.
            setToolMode(EditToolMode.Select);
            canvasController.SelectOnly(imageAnnotation);
            statusText.Text = "이미지가 추가되었습니다 (저장 시 반영)";
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

        statusText.Text = "이미지 추출 중...";
        loadingIndicator.IsActive = true;
        try
        {
            PdfImageExtractionResult result = await extractionService.ExtractAllAsync(
                pdfBytes,
                folder.Path,
                documentName);

            statusText.Text = result switch
            {
                { SavedCount: 0, FailedCount: 0 } => "추출할 이미지가 없습니다.",
                { FailedCount: 0 } => $"이미지 추출 완료: {result.SavedCount}개",
                _ => $"이미지 {result.SavedCount}개 추출, {result.FailedCount}개 실패"
            };
        }
        catch (Exception ex)
        {
            await showErrorAsync(
                "이미지 추출 오류",
                $"이미지 추출 중 오류가 발생했습니다: {ex.Message}");
            statusText.Text = "이미지 추출에 실패했습니다.";
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
            statusText.Text = "저장할 이미지 파일을 찾을 수 없습니다.";
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
            statusText.Text = $"이미지 저장 완료: {targetFile.Name}";
        }
        catch (Exception ex)
        {
            await showErrorAsync(
                "이미지 저장 오류",
                $"이미지를 저장하는 중 오류가 발생했습니다: {ex.Message}");
            statusText.Text = "이미지 저장에 실패했습니다.";
        }
    }

    private static string GetImageFileTypeDescription(string extension) =>
        extension switch
        {
            ".jpg" or ".jpeg" => "JPEG 이미지",
            ".tif" or ".tiff" => "TIFF 이미지",
            ".jp2" => "JPEG 2000 이미지",
            ".bmp" => "BMP 이미지",
            ".gif" => "GIF 이미지",
            ".jbig2" => "JBIG2 이미지",
            _ => "PNG 이미지"
        };
}
