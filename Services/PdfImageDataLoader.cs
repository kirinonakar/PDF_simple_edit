using System;
using System.IO;
using System.Threading.Tasks;
using iText.IO.Image;
using Windows.Graphics.Imaging;

namespace PDF_simple_edit.Services;

/// <summary>Uses the Windows WebP decoder without shipping an extra native image library.</summary>
internal static class PdfImageDataLoader
{
    public static ImageData Create(string imagePath)
    {
        using var input = File.OpenRead(imagePath);
        Span<byte> header = stackalloc byte[12];
        int read = input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        // Detect by contents so renamed WebP files follow the same path.
        if (read != header.Length || !header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..].SequenceEqual("WEBP"u8))
            return ImageDataFactory.Create(imagePath);

        input.Position = 0;
        // Content writing is synchronous; every native await below resumes off
        // the caller's context so this also works when called from the UI thread.
        return DecodeWebPAsync(input).GetAwaiter().GetResult();
    }

    private static async Task<ImageData> DecodeWebPAsync(Stream input)
    {
        using var source = input.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.WebpDecoderId, source)
            .AsTask().ConfigureAwait(false);
        // As with other multi-frame imports, only the first frame becomes a page.
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask().ConfigureAwait(false);

        // Lossless PNG bridges to iText's existing image path and preserves alpha.
        using var output = new MemoryStream();
        using (var target = output.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, target)
                .AsTask().ConfigureAwait(false);
            encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
                decoder.PixelWidth, decoder.PixelHeight, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            return ImageDataFactory.Create(output.ToArray());
        }
    }
}
