using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using PageSize = iText.Kernel.Geom.PageSize;

namespace PDF_simple_edit.Services;

/// <summary>Creates one fitted A4 PDF page per image, in the supplied order.</summary>
public sealed class PdfImageConversionService
{
    public Task CreateAsync(IReadOnlyList<string> imagePaths, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (imagePaths.Count == 0)
            throw new ArgumentException("변환할 이미지를 선택해 주세요.", nameof(imagePaths));

        return Task.Run(() => Create(imagePaths, outputPath));
    }

    private static void Create(IReadOnlyList<string> imagePaths, string outputPath)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var pdf = new PdfDocument(new PdfWriter(temporaryPath)))
            {
                foreach (string imagePath in imagePaths)
                {
                    ImageData data;
                    try
                    {
                        data = ImageDataFactory.Create(imagePath);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException(
                            $"'{Path.GetFileName(imagePath)}' 이미지를 읽을 수 없습니다.", ex);
                    }

                    if (data.GetWidth() <= 0 || data.GetHeight() <= 0)
                        throw new InvalidDataException($"'{Path.GetFileName(imagePath)}' 이미지의 크기가 올바르지 않습니다.");

                    PageSize pageSize = data.GetWidth() > data.GetHeight()
                        ? PageSize.A4.Rotate()
                        : PageSize.A4;
                    var page = pdf.AddNewPage(pageSize);

                    const float margin = 18f;
                    float scale = Math.Min(
                        (pageSize.GetWidth() - 2 * margin) / data.GetWidth(),
                        (pageSize.GetHeight() - 2 * margin) / data.GetHeight());
                    float width = data.GetWidth() * scale;
                    float height = data.GetHeight() * scale;
                    float left = (pageSize.GetWidth() - width) / 2;
                    float bottom = (pageSize.GetHeight() - height) / 2;

                    var image = new Image(data);
                    image.SetFixedPosition(left, bottom, width);
                    image.SetHeight(height);
                    using var canvas = new Canvas(new PdfCanvas(page), pageSize);
                    canvas.Add(image);
                }
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
