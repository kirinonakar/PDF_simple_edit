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
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PDF_simple_edit.Controllers;

/// <summary>Copies, cuts and pastes annotation objects and Windows clipboard content.</summary>
public sealed class AnnotationClipboardController(
    AnnotationCanvasController canvasController,
    TextFontSettings fontSettings,
    TextBlock statusText,
    Func<PdfDocumentManager> getManager,
    Func<List<PdfAnnotation>> getAnnotations,
    Func<int> getCurrentPageIndex,
    Func<Task<bool>> deleteSelectionAsync,
    Action<EditToolMode> setToolMode)
{
    private const string AnnotationClipboardFormat = "PDFSimpleEditor.Annotations.v1";

    public async Task<bool> CutSelectedObjectsAsync()
    {
        List<PdfAnnotation> selectedObjects = GetCopyableSelection();
        if (selectedObjects.Count == 0 ||
            selectedObjects.Count != canvasController.SelectedAnnotations.Count)
        {
            return false;
        }

        if (!await CopySelectedObjectsAsync())
            return false;

        if (!await deleteSelectionAsync())
            return false;

        statusText.Text = selectedObjects.Count > 1
            ? $"{selectedObjects.Count}개 객체를 잘라냈습니다."
            : selectedObjects[0].Type == AnnotationType.Image
                ? "이미지를 잘라냈습니다."
                : "텍스트를 잘라냈습니다.";
        return true;
    }

    public async Task<bool> CopySelectedObjectsAsync()
    {
        List<PdfAnnotation> selectedObjects = GetCopyableSelection();
        if (selectedObjects.Count == 0)
            return false;

        try
        {
            var dataPackage = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy
            };
            dataPackage.SetData(
                AnnotationClipboardFormat,
                JsonSerializer.Serialize(selectedObjects.Select(annotation => annotation.Clone())));

            List<PdfAnnotation> selectedText = selectedObjects
                .Where(IsTextAnnotation)
                .ToList();
            if (selectedText.Count > 0)
            {
                dataPackage.SetText(string.Join(
                    Environment.NewLine,
                    selectedText.Select(annotation => annotation.Content)));
            }

            PdfAnnotation? selectedImage = selectedObjects.FirstOrDefault(annotation =>
                annotation.Type == AnnotationType.Image &&
                !string.IsNullOrWhiteSpace(annotation.ImagePath) &&
                File.Exists(annotation.ImagePath));
            if (selectedImage?.ImagePath != null)
            {
                StorageFile imageFile = await StorageFile.GetFileFromPathAsync(selectedImage.ImagePath);
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromFile(imageFile));
            }

            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            statusText.Text = selectedObjects.Count > 1
                ? $"{selectedObjects.Count}개 객체를 클립보드에 복사했습니다."
                : selectedObjects[0].Type == AnnotationType.Image
                    ? "이미지를 클립보드에 복사했습니다."
                    : "텍스트를 클립보드에 복사했습니다.";
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard copy error: {ex}");
            statusText.Text = "클립보드에 복사하지 못했습니다.";
            return false;
        }
    }

    public async Task<bool> PasteClipboardContentAsync()
    {
        if (!getManager().IsLoaded)
            return false;

        try
        {
            DataPackageView clipboardContent = Clipboard.GetContent();
            if (clipboardContent.Contains(AnnotationClipboardFormat))
            {
                object data = await clipboardContent.GetDataAsync(AnnotationClipboardFormat);
                if (data is string json)
                {
                    List<PdfAnnotation>? annotations =
                        JsonSerializer.Deserialize<List<PdfAnnotation>>(json);
                    if (annotations != null && PasteAnnotationObjects(annotations))
                        return true;
                }
            }

            if (clipboardContent.Contains(StandardDataFormats.Bitmap))
            {
                RandomAccessStreamReference bitmapReference =
                    await clipboardContent.GetBitmapAsync();
                ClipboardImageData image = await SaveClipboardImageAsync(bitmapReference);
                AddPastedAnnotations(new[] { CreateImageAnnotation(image) });
                statusText.Text = "이미지를 새 객체로 붙여넣었습니다.";
                return true;
            }

            if (clipboardContent.Contains(StandardDataFormats.Text))
            {
                string text = await clipboardContent.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    AddPastedAnnotations(new[] { CreateTextAnnotation(text) });
                    statusText.Text = "텍스트를 새 객체로 붙여넣었습니다.";
                    return true;
                }
            }

            statusText.Text = "붙여넣을 수 있는 텍스트나 이미지가 없습니다.";
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard paste error: {ex}");
            statusText.Text = "클립보드 내용을 붙여넣지 못했습니다.";
            return false;
        }
    }

    public List<PdfAnnotation> GetCopyableSelection() =>
        canvasController.SelectedAnnotations
            .Where(annotation =>
                IsTextAnnotation(annotation)
                    ? !string.IsNullOrWhiteSpace(annotation.Content)
                    : annotation.Type == AnnotationType.Image &&
                      !string.IsNullOrWhiteSpace(annotation.ImagePath) &&
                      File.Exists(annotation.ImagePath))
            .OrderBy(annotation => annotation.PageIndex)
            .ThenBy(annotation => annotation.Y)
            .ThenBy(annotation => annotation.X)
            .ToList();

    private static bool IsTextAnnotation(PdfAnnotation annotation) =>
        annotation.Type is AnnotationType.Text or AnnotationType.FreeText;

    public static bool CanPasteClipboardContent()
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            return content.Contains(AnnotationClipboardFormat) ||
                content.Contains(StandardDataFormats.Text) ||
                content.Contains(StandardDataFormats.Bitmap);
        }
        catch
        {
            return false;
        }
    }

    private bool PasteAnnotationObjects(IEnumerable<PdfAnnotation> sourceAnnotations)
    {
        (double pageWidth, double pageHeight) = getManager().GetPageSize(getCurrentPageIndex());
        var pasted = new List<PdfAnnotation>();
        foreach (PdfAnnotation source in sourceAnnotations.Where(annotation =>
            IsTextAnnotation(annotation) || annotation.Type == AnnotationType.Image))
        {
            if (source.Type == AnnotationType.Image &&
                (string.IsNullOrWhiteSpace(source.ImagePath) || !File.Exists(source.ImagePath)))
            {
                continue;
            }

            PdfAnnotation annotation = source.Clone();
            PreparePastedAnnotation(annotation);
            annotation.X = Math.Clamp(
                source.X + 12,
                0,
                Math.Max(pageWidth - Math.Max(annotation.Width, 1), 0));
            annotation.Y = Math.Clamp(
                source.Y + 12,
                0,
                Math.Max(pageHeight - Math.Max(annotation.Height, 1), 0));
            pasted.Add(annotation);
        }

        if (pasted.Count == 0)
            return false;

        AddPastedAnnotations(pasted);
        statusText.Text = pasted.Count > 1
            ? $"{pasted.Count}개 객체를 붙여넣었습니다."
            : pasted[0].Type == AnnotationType.Image
                ? "이미지를 새 객체로 붙여넣었습니다."
                : "텍스트를 새 객체로 붙여넣었습니다.";
        return true;
    }

    private void PreparePastedAnnotation(PdfAnnotation annotation)
    {
        // A clipboard copy is a new text object, never a handle to another
        // document's content stream operators.
        annotation.NativeText = null;
        annotation.Id = Guid.NewGuid().ToString();
        annotation.PageIndex = getCurrentPageIndex();
        annotation.CreatedAt = DateTime.Now;
        annotation.IsApplied = false;
        annotation.IsOriginalTextReplacement = false;
        annotation.IsOriginalImageReplacement = false;
        annotation.OriginalPdfX = 0;
        annotation.OriginalPdfY = 0;
        annotation.OriginalText = string.Empty;
        annotation.OriginalImageName = null;
        annotation.OperatorId = null;
        annotation.ContentStreamIndex = -1;
        annotation.ContentStreamObjectNumber = -1;
        annotation.OperationIndex = -1;
        annotation.OriginalFontObjectNumber = -1;
        annotation.GraphicOperationIndexes.Clear();
        annotation.GraphicTextOperationIndexes.Clear();
        annotation.GraphicOperations.Clear();
        if (IsTextAnnotation(annotation))
            annotation.TextFragments.Clear();
    }

    private PdfAnnotation CreateTextAnnotation(string text)
    {
        (double pageWidth, double pageHeight) = getManager().GetPageSize(getCurrentPageIndex());
        (double width, double height) = AnnotationTextLayoutService.MeasureBounds(
            text,
            fontSettings.FontFamily,
            fontSettings.FontSize,
            fontSettings.IsBold,
            fontSettings.IsItalic,
            fontSettings.IsBold ? 700 : 400);
        return new PdfAnnotation
        {
            Type = AnnotationType.Text,
            PageIndex = getCurrentPageIndex(),
            X = Math.Max((pageWidth - width) / 2, 0),
            Y = Math.Max((pageHeight - height) / 2, 0),
            Width = Math.Max(width, 1),
            Height = Math.Max(height, 1),
            Content = text,
            FontFamily = fontSettings.FontFamily,
            FontSize = fontSettings.FontSize,
            Color = fontSettings.Color,
            FontWeight = fontSettings.IsBold ? 700 : 400,
            IsBold = fontSettings.IsBold,
            IsItalic = fontSettings.IsItalic,
            IsApplied = false
        };
    }

    private PdfAnnotation CreateImageAnnotation(ClipboardImageData image)
    {
        (double pageWidth, double pageHeight) = getManager().GetPageSize(getCurrentPageIndex());
        (double width, double height) = ImageAnnotationLayoutService.CalculateInitialSize(
            image.PixelWidth,
            image.PixelHeight,
            pageWidth,
            pageHeight);
        return new PdfAnnotation
        {
            Type = AnnotationType.Image,
            PageIndex = getCurrentPageIndex(),
            X = Math.Max((pageWidth - width) / 2, 0),
            Y = Math.Max((pageHeight - height) / 2, 0),
            Width = width,
            Height = height,
            ImagePath = image.Path,
            IsApplied = false
        };
    }

    private static async Task<ClipboardImageData> SaveClipboardImageAsync(
        RandomAccessStreamReference bitmapReference)
    {
        using IRandomAccessStreamWithContentType sourceStream =
            await bitmapReference.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(sourceStream);

        StorageFolder tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        StorageFolder appFolder = await tempFolder.CreateFolderAsync(
            "PDF_simple_edit",
            CreationCollisionOption.OpenIfExists);
        StorageFolder clipboardFolder = await appFolder.CreateFolderAsync(
            "clipboard",
            CreationCollisionOption.OpenIfExists);
        StorageFile imageFile = await clipboardFolder.CreateFileAsync(
            $"clipboard_{Guid.NewGuid():N}.png",
            CreationCollisionOption.GenerateUniqueName);

        using IRandomAccessStream outputStream = await imageFile.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(
            outputStream,
            decoder);
        await encoder.FlushAsync();
        return new ClipboardImageData(imageFile.Path, decoder.PixelWidth, decoder.PixelHeight);
    }

    private void AddPastedAnnotations(IEnumerable<PdfAnnotation> annotations)
    {
        List<PdfAnnotation> pasted = annotations.ToList();
        if (pasted.Count == 0)
            return;

        setToolMode(EditToolMode.Select);
        getAnnotations().AddRange(pasted);
        canvasController.ClearSelection();
        foreach (PdfAnnotation annotation in pasted)
            canvasController.SelectedAnnotations.Add(annotation);
        canvasController.PrimarySelection = pasted[^1];
        getManager().MarkModified();
        canvasController.Render();
    }

    private sealed record ClipboardImageData(string Path, uint PixelWidth, uint PixelHeight);
}
