using PDF_simple_edit.Models;
using Windows.Graphics.Imaging;

internal static class WindowsRenderingProbe
{
    public static async Task Render(byte[] bytes, string name, NativePdfTextBlock? layout = null)
    {
        using var input = new MemoryStream(bytes);
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input.AsRandomAccessStream());
        using var page = document.GetPage(0);
        using var output = new MemoryStream();
        var stream = output.AsRandomAccessStream();
        await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)(page.Size.Width * 2),
            DestinationHeight = (uint)(page.Size.Height * 2),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
        });
        await stream.FlushAsync();
        File.WriteAllBytes($"tmp/pdfs/windows-{name}.png", output.ToArray());
        if (layout == null) return;
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = provider.DetachPixelData();
        using var pdf = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(new MemoryStream(bytes)));
        var box = pdf.GetPage(1).GetCropBox();
        double sx = decoder.PixelWidth / box.GetWidth(), sy = decoder.PixelHeight / box.GetHeight();
        foreach (var glyph in layout.Glyphs.Where(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text)))
        {
            int left = Math.Clamp((int)Math.Floor(glyph.Bounds.X * sx), 0, (int)decoder.PixelWidth);
            int right = Math.Clamp((int)Math.Ceiling(glyph.Bounds.Right * sx), 0, (int)decoder.PixelWidth);
            int top = Math.Clamp((int)Math.Floor(glyph.Bounds.Y * sy), 0, (int)decoder.PixelHeight);
            int bottom = Math.Clamp((int)Math.Ceiling(glyph.Bounds.Bottom * sy), 0, (int)decoder.PixelHeight);
            int ink = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int index = (y * (int)decoder.PixelWidth + x) * 4;
                if (Math.Min(pixels[index], Math.Min(pixels[index + 1], pixels[index + 2])) < 200) ink++;
            }
            if (ink <= Math.Max(2, (right - left) * (bottom - top) * .02))
                throw new Exception($"Windows renderer misplaced '{glyph.Text}' at text index {glyph.TextIndex} in {name}");
        }
    }
}
