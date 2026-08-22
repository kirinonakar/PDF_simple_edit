using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed record PdfImageExtractionResult(int SavedCount, int FailedCount);

/// <summary>
/// Exports every rendered raster-image occurrence in a PDF. Processing page
/// content, rather than only walking page resources, also includes inline
/// images and images nested inside form XObjects.
/// </summary>
public sealed class PdfImageExtractionService
{
    public Task<PdfImageExtractionResult> ExtractAllAsync(
        byte[] pdfBytes,
        string outputFolder,
        string documentName)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        byte[] snapshot = (byte[])pdfBytes.Clone();
        string safeDocumentName = MakeSafeFileName(documentName, "document");
        return Task.Run(() => ExtractAll(snapshot, outputFolder, safeDocumentName));
    }

    private static PdfImageExtractionResult ExtractAll(
        byte[] pdfBytes,
        string outputFolder,
        string documentName)
    {
        Directory.CreateDirectory(outputFolder);
        int savedCount = 0;
        int failedCount = 0;

        using var input = new MemoryStream(pdfBytes);
        using var reader = new PdfReader(input);
        using var document = new PdfDocument(reader);

        for (int pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            var listener = new ImageExtractionListener();
            try
            {
                var processor = new PdfCanvasProcessor(listener);
                processor.ProcessPageContent(document.GetPage(pageNumber));
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Image discovery error on page {pageNumber}: {exception.Message}");
                failedCount++;
            }

            for (int imageIndex = 0; imageIndex < listener.Images.Count; imageIndex++)
            {
                ExtractedImage image = listener.Images[imageIndex];
                string fileName = $"{documentName}_page_{pageNumber:0000}_image_{imageIndex + 1:0000}.{image.Extension}";
                string outputPath = GetAvailablePath(outputFolder, fileName);
                try
                {
                    File.WriteAllBytes(outputPath, image.Bytes);
                    savedCount++;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Image export error for '{outputPath}': {exception.Message}");
                    failedCount++;
                }
            }

            failedCount += listener.FailedCount;
        }

        return new PdfImageExtractionResult(savedCount, failedCount);
    }

    private static string GetAvailablePath(string outputFolder, string fileName)
    {
        string path = Path.Combine(outputFolder, fileName);
        if (!File.Exists(path))
            return path;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 2; ; suffix++)
        {
            path = Path.Combine(outputFolder, $"{name}_{suffix}{extension}");
            if (!File.Exists(path))
                return path;
        }
    }

    private static string MakeSafeFileName(string? value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private sealed record ExtractedImage(byte[] Bytes, string Extension);

    private sealed class ImageExtractionListener : IEventListener
    {
        public List<ExtractedImage> Images { get; } = new();
        public int FailedCount { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_IMAGE || data is not ImageRenderInfo imageInfo)
                return;

            try
            {
                PdfImageXObject image = imageInfo.GetImage();
                byte[] bytes = image.GetImageBytes(true);
                if (bytes.Length == 0)
                {
                    FailedCount++;
                    return;
                }

                Images.Add(new ExtractedImage(
                    bytes,
                    NormalizeImageExtension(image.IdentifyImageFileExtension())));
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Embedded image extraction error: {exception.Message}");
                FailedCount++;
            }
        }

        public ICollection<EventType> GetSupportedEvents() =>
            new[] { EventType.RENDER_IMAGE };
    }

    private static string NormalizeImageExtension(string? extension) =>
        extension?.TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "jpg",
            "jp2" => "jp2",
            "tif" or "tiff" => "tif",
            "jbig2" => "jbig2",
            "bmp" => "bmp",
            "gif" => "gif",
            _ => "png"
        };
}
