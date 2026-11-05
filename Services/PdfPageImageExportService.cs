using PDF_simple_edit.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Loc = PDF_simple_edit.Services.LocalizationService;
using iText.Kernel.Pdf;
using Windows.Graphics.Imaging;

namespace PDF_simple_edit.Services;

/// <summary>Renders an immutable, annotated PDF snapshot to individual PNG files.</summary>
public sealed class PdfPageImageExportService
{
    public static IReadOnlyList<int> ParsePageIndices(string text, int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        var pages = new SortedSet<int>();
        foreach (string part in (text ?? string.Empty).Split(','))
        {
            string[] bounds = part.Trim().Split('-');
            if (bounds.Length is < 1 or > 2 ||
                !int.TryParse(bounds[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int start))
                throw new ArgumentException(Loc.Format("InvalidImagePageSelection", pageCount));
            int end = start;
            if (bounds.Length == 2 &&
                !int.TryParse(bounds[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out end))
                throw new ArgumentException(Loc.Format("InvalidImagePageSelection", pageCount));
            if (start < 1 || end < start || end > pageCount)
                throw new ArgumentException(Loc.Format("InvalidImagePageSelection", pageCount));
            for (int index = start - 1; index < end; index++)
                pages.Add(index);
        }
        return pages.ToArray();
    }

    public Task ExportPageAsync(byte[] snapshot, int pageIndex, string path, int dpi = 300) =>
        ExportAsync(snapshot, new[] { pageIndex }, path, null, dpi, null);

    public Task ExportPagesAsync(byte[] snapshot, IReadOnlyList<int> pageIndices,
        string folder, string documentName, int dpi, IProgress<int>? progress = null) =>
        ExportAsync(snapshot, pageIndices, folder, documentName, dpi, progress);

    private static Task ExportAsync(byte[] snapshot, IReadOnlyList<int> pageIndices,
        string outputPath, string? documentName, int dpi, IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Count == 0) throw new ArgumentException(nameof(pageIndices));
        if (dpi < 72 || dpi > 600) throw new ArgumentOutOfRangeException(nameof(dpi));
        int[] indices = pageIndices.Distinct().Order().ToArray();
        if (documentName == null && indices.Length != 1) throw new ArgumentException(nameof(pageIndices));

        // Keep native rasterization and disk I/O off the UI thread; retain only
        // one rendered page at a time, even when exporting the whole document.
        return Task.Run(async () =>
        {
            using var document = await PdfRenderDocument.LoadAsync(snapshot);
            using var input = new MemoryStream(snapshot, writable: false);
            using var reader = new PdfReader(input);
            using var pdf = new PdfDocument(reader);
            if (indices.Any(index => index < 0 || index >= document.PageCount))
                throw new ArgumentOutOfRangeException(nameof(pageIndices));
            string safeName = documentName ?? string.Empty;
            foreach (char invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "document";
            int saved = 0;
            foreach (int index in indices)
            {
                // PDF coordinates are points (72/inch). Native render destination
                // sizes are DIPs, so normalize final pixels independently of the
                // display scale. Also write the selected DPI into the PNG metadata.
                var page = pdf.GetPage(index + 1);
                var crop = page.GetCropBox();
                var media = page.GetMediaBox();
                double width = Math.Min(crop.GetRight(), media.GetRight()) - Math.Max(crop.GetLeft(), media.GetLeft());
                double height = Math.Min(crop.GetTop(), media.GetTop()) - Math.Max(crop.GetBottom(), media.GetBottom());
                double unit = page.GetPdfObject().GetAsNumber(PdfName.UserUnit)?.DoubleValue() ?? 1;
                if (page.GetRotation() % 180 != 0) (width, height) = (height, width);
                width = Math.Round(width * unit * dpi / 72.0);
                height = Math.Round(height * unit * dpi / 72.0);
                if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
                    width > uint.MaxValue || height > uint.MaxValue || width * height > 100_000_000)
                    throw new InvalidDataException(Loc.Format("ImageExportPageTooLarge", index + 1));
                using var png = await document.RenderAsync(index, dpi / 96.0)
                    ?? throw new IOException(Loc.Format("CannotRenderPageAsImage", index + 1));
                string path = documentName == null ? outputPath
                    : Path.Combine(outputPath, $"{safeName}_page_{index + 1:0000}.png");
                await SavePngAsync(png, path, (uint)width, (uint)height, dpi, overwrite: documentName == null);
                progress?.Report(++saved);
            }
        });
    }

    private static async Task SavePngAsync(MemoryStream png, string path, uint width, uint height, int dpi, bool overwrite)
    {
        string fullPath = Path.GetFullPath(path);
        string temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using var input = png.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using var stream = output.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, dpi, dpi,
                    pixels.DetachPixelData());
                await encoder.FlushAsync();
            }
            if (overwrite)
            {
                File.Move(temporaryPath, fullPath, overwrite: true);
                return;
            }
            // Move without overwrite also protects files created after the name
            // check. Each complete page becomes visible atomically.
            for (int suffix = 1; ; suffix++)
            {
                string candidate = suffix == 1 ? fullPath : Path.Combine(Path.GetDirectoryName(fullPath)!,
                    $"{Path.GetFileNameWithoutExtension(fullPath)}_{suffix}.png");
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                try { File.Move(temporaryPath, candidate); return; }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
        }
        finally
        {
            PdfTemporaryFiles.TryDelete(temporaryPath);
        }
    }
}
