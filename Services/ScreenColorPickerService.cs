using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace PDF_simple_edit.Services;

public sealed class ScreenColorPickerService
{
    public async Task<Windows.UI.Color?> PickAsync(UIElement element, Point point)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            var pixels = new byte[buffer.Length];
            reader.ReadBytes(pixels);

            int pixelWidth = bitmap.PixelWidth;
            int pixelHeight = bitmap.PixelHeight;
            int x = Math.Clamp(
                (int)(point.X * pixelWidth / element.RenderSize.Width),
                0,
                pixelWidth - 1);
            int y = Math.Clamp(
                (int)(point.Y * pixelHeight / element.RenderSize.Height),
                0,
                pixelHeight - 1);
            int index = (y * pixelWidth + x) * 4;
            if (index < 0 || index + 3 >= pixels.Length)
                return null;

            return Windows.UI.Color.FromArgb(
                pixels[index + 3],
                pixels[index + 2],
                pixels[index + 1],
                pixels[index]);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error picking color: {ex.Message}");
            return null;
        }
    }
}
