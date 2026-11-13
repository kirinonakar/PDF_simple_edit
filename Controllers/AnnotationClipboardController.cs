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
    private const string GraphicClipboardFormat = "PDFSimpleEditor.Graphics.v1";

    public async Task<bool> CutSelectedObjectsAsync()
    {
        var manager = getManager();
        int pageIndex = getCurrentPageIndex();
        long version = manager.EditVersion;
        List<PdfAnnotation> selectedObjects = GetCopyableSelection();
        if (selectedObjects.Count == 0 ||
            selectedObjects.Count != canvasController.SelectedAnnotations.Count)
        {
            return false;
        }

        if (!await CopySelectedObjectsAsync())
            return false;

        // Copy can yield while another process holds the clipboard. Do not cut a
        // different selection if the user changed it during that wait.
        if (manager != getManager() || pageIndex != getCurrentPageIndex() || version != manager.EditVersion ||
            !selectedObjects.SequenceEqual(GetCopyableSelection()) ||
            selectedObjects.Count != canvasController.SelectedAnnotations.Count)
            return false;

        if (!await deleteSelectionAsync())
            return false;

        statusText.Text = selectedObjects.Count > 1 ? Loc.Format("CutObjects", selectedObjects.Count) : Loc.Get(
            selectedObjects[0] switch
            {
                { IsOriginalVectorGraphic: true } => "CutGraphics",
                { Type: AnnotationType.Image } => "CutImage",
                { IsEditablePath: true } => "CutDrawing",
                _ => "CutText"
            });
        return true;
    }

    public async Task<bool> CopySelectedObjectsAsync()
    {
        string selectedPdfText = canvasController.TextSelection.SelectedText;
        if (selectedPdfText.Length > 0)
        {
            try
            {
                var textPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                textPackage.SetText(selectedPdfText);
                await ClipboardCopyService.SetContentAsync(textPackage);
                statusText.Text = Loc.Get("CopiedTextToTheClipboard");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard copy error: {ex}");
                statusText.Text = Loc.Get("CouldNotCopyToTheClipboard");
                return false;
            }
        }
        List<PdfAnnotation> selectedObjects = GetCopyableSelection();
        if (selectedObjects.Count == 0)
            return false;

        try
        {
            var dataPackage = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy
            };
            var nativeGraphics = selectedObjects.Where(CanCopyNativeGraphic).Select(a => a.Clone()).ToList();
            var overlayObjects = selectedObjects.Where(a => !CanCopyNativeGraphic(a)).Select(a => a.Clone()).ToList();
            if (overlayObjects.Count > 0)
                ClipboardObjectData.Set(dataPackage, AnnotationClipboardFormat, overlayObjects);
            if (nativeGraphics.Count > 0)
            {
                var manager = getManager();
                int pageIndex = getCurrentPageIndex();
                byte[] bytes = manager.GetPdfBytes() ?? throw new InvalidOperationException(Loc.Get("NoPDFDocumentIsOpen"));
                var contents = await manager.ExtractPageContentsAsync(pageIndex);
                if (!ReferenceEquals(bytes, manager.GetPdfBytes()))
                    throw new InvalidOperationException(Loc.Get("TheGraphicsChangedSelectThemAgain"));
                var targets = new PdfAnnotationTargetResolver().ResolveImages(nativeGraphics, contents)
                    ?? throw new InvalidOperationException(Loc.Get("TheGraphicsChangedSelectThemAgain"));
                targets = targets.OrderBy(a => contents.FirstOrDefault(c => AnnotationContentService.IsSameOriginalGraphic(a, c)) is { } content
                    ? content.GraphicPaintOrder : int.MaxValue).ToList();
                var graphics = await Task.Run(() => PdfGraphicClipboard.Copy(bytes, pageIndex, targets));
                ClipboardObjectData.Set(dataPackage, GraphicClipboardFormat, graphics);
            }

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

            await ClipboardCopyService.SetContentAsync(dataPackage);
            statusText.Text = selectedObjects.Count > 1 ? Loc.Format("CopiedObjectsToTheClipboard", selectedObjects.Count) : Loc.Get(
                selectedObjects[0] switch
                {
                    { IsOriginalVectorGraphic: true } => "CopiedGraphicsToTheClipboard",
                    { Type: AnnotationType.Image } => "CopiedImageToTheClipboard",
                    { IsEditablePath: true } => "CopiedDrawingToTheClipboard",
                    _ => "CopiedTextToTheClipboard"
                });
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard copy error: {ex}");
            statusText.Text = Loc.Get("CouldNotCopyToTheClipboard");
            return false;
        }
    }

    public async Task<bool> PasteClipboardContentAsync()
    {
        var manager = getManager();
        int pageIndex = getCurrentPageIndex();
        if (manager.GetPdfBytes() is not { } snapshot)
            return false;
        bool IsCurrentPage() => manager == getManager() && pageIndex == getCurrentPageIndex();

        try
        {
            DataPackageView clipboardContent = Clipboard.GetContent();
            var pasted = new List<PdfAnnotation>();
            if (clipboardContent.Contains(AnnotationClipboardFormat))
            {
                var annotations = await ClipboardObjectData.ReadAsync<List<PdfAnnotation>>(clipboardContent, AnnotationClipboardFormat);
                if (!IsCurrentPage()) return false;
                if (annotations != null) pasted.AddRange(CreatePastedAnnotations(annotations));
            }
            bool hasNativeGraphics = clipboardContent.Contains(GraphicClipboardFormat);
            if (hasNativeGraphics)
            {
                if (await ClipboardObjectData.ReadAsync<PdfGraphicClipboardData>(clipboardContent, GraphicClipboardFormat)
                    is not { Bounds.Length: > 0, Pdf.Length: > 0 } graphics)
                    throw new InvalidDataException();
                if (!IsCurrentPage()) return false;
                var result = await Task.Run(() => PdfGraphicClipboard.Paste(snapshot, pageIndex, graphics));
                if (!IsCurrentPage()) return false;
                manager.CommitNativeTextEdit(snapshot, result.Bytes, pageIndex);
                // Prepending the graphics-state guard changes existing stream indexes.
                getAnnotations().RemoveAll(a => a.PageIndex == pageIndex && (a.IsOriginalImageReplacement || a.NativeText != null));
                var contentService = new AnnotationContentService();
                pasted.AddRange(result.Contents.Select(c => contentService.ConvertToAnnotation(c, pageIndex)));
            }
            if (pasted.Count > 0)
            {
                AddPastedAnnotations(pasted, markModified: !hasNativeGraphics);
                statusText.Text = pasted.Count > 1 ? Loc.Format("PastedObjects", pasted.Count) : Loc.Get(
                    pasted[0] switch
                    {
                        { IsOriginalVectorGraphic: true } => "PastedGraphicsAsANewObject",
                        { Type: AnnotationType.Image } => "PastedImageAsANewObject",
                        { IsEditablePath: true } => "PastedDrawingAsANewObject",
                        _ => "PastedTextAsANewObject"
                    });
                return true;
            }

            if (clipboardContent.Contains(StandardDataFormats.Bitmap))
            {
                RandomAccessStreamReference bitmapReference =
                    await clipboardContent.GetBitmapAsync();
                ClipboardImageData image = await SaveClipboardImageAsync(bitmapReference);
                if (!IsCurrentPage()) return false;
                AddPastedAnnotations(new[] { CreateImageAnnotation(image) });
                statusText.Text = Loc.Get("PastedImageAsANewObject");
                return true;
            }

            if (clipboardContent.Contains(StandardDataFormats.Text))
            {
                string text = await clipboardContent.GetTextAsync();
                if (!IsCurrentPage()) return false;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    AddPastedAnnotations(new[] { CreateTextAnnotation(text) });
                    statusText.Text = Loc.Get("PastedTextAsANewObject");
                    return true;
                }
            }

            statusText.Text = Loc.Get("NoTextOrImagesAvailableToPaste");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard paste error: {ex}");
            statusText.Text = Loc.Get("CouldNotPasteTheClipboardContents");
            return false;
        }
    }

    public List<PdfAnnotation> GetCopyableSelection() =>
        canvasController.SelectedAnnotations
            .Where(annotation =>
                IsTextAnnotation(annotation)
                    ? !string.IsNullOrWhiteSpace(annotation.Content)
                    : CanCopyNativeGraphic(annotation) || annotation.IsEditablePath || annotation.Type == AnnotationType.Image &&
                      !string.IsNullOrWhiteSpace(annotation.ImagePath) &&
                      File.Exists(annotation.ImagePath))
            .OrderBy(annotation => annotation.PageIndex)
            .ThenBy(annotation => annotation.Y)
            .ThenBy(annotation => annotation.X)
            .ToList();

    private static bool IsTextAnnotation(PdfAnnotation annotation) =>
        annotation.Type is AnnotationType.Text or AnnotationType.FreeText;

    private static bool CanCopyNativeGraphic(PdfAnnotation annotation) =>
        annotation.IsOriginalImageReplacement && (annotation.GraphicOperations.Count > 0
            ? annotation.GraphicOperations.All(op => !op.IsTextOperation)
            : annotation.OperationIndex >= 0);

    public static bool CanPasteClipboardContent()
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            return content.Contains(AnnotationClipboardFormat) ||
                content.Contains(GraphicClipboardFormat) ||
                content.Contains(StandardDataFormats.Text) ||
                content.Contains(StandardDataFormats.Bitmap);
        }
        catch
        {
            return false;
        }
    }

    private List<PdfAnnotation> CreatePastedAnnotations(IEnumerable<PdfAnnotation> sourceAnnotations)
    {
        (double pageWidth, double pageHeight) = getManager().GetPageSize(getCurrentPageIndex());
        var pasted = new List<PdfAnnotation>();
        foreach (PdfAnnotation source in sourceAnnotations.Where(annotation =>
            IsTextAnnotation(annotation) || annotation.IsEditablePath || annotation.Type == AnnotationType.Image))
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
            foreach (var point in annotation.SignaturePoints)
            {
                point.X += annotation.X - source.X;
                point.Y += annotation.Y - source.Y;
            }
            pasted.Add(annotation);
        }

        return pasted;
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
            IsUnderline = fontSettings.IsUnderline,
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

        StorageFolder clipboardFolder = await StorageFolder.GetFolderFromPathAsync(PdfTemporaryFiles.GetDirectory("clipboard"));
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

    private void AddPastedAnnotations(IEnumerable<PdfAnnotation> annotations, bool markModified = true)
    {
        List<PdfAnnotation> pasted = annotations.ToList();
        if (pasted.Count == 0)
            return;

        bool graphics = !IsTextAnnotation(pasted[^1]);
        setToolMode(graphics ? EditToolMode.SelectGraphics : EditToolMode.Select);
        getAnnotations().AddRange(pasted);
        canvasController.ClearSelection();
        foreach (PdfAnnotation annotation in pasted.Where(a => IsTextAnnotation(a) != graphics))
            canvasController.SelectedAnnotations.Add(annotation);
        canvasController.PrimarySelection = pasted[^1];
        if (markModified) getManager().MarkModified(pageIndex: getCurrentPageIndex());
        canvasController.Render();
    }

    private sealed record ClipboardImageData(string Path, uint PixelWidth, uint PixelHeight);
}
