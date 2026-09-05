namespace PDF_simple_edit.Helpers;

// The extraction tests exercise the production text branches without displaying
// a window. Image preview rasterization belongs to the desktop app.
internal static class PdfRenderHelper
{
    public static Task<MemoryStream?> RenderRegionWithWindowsPdfStreamAsync(
        Windows.Storage.Streams.IRandomAccessStream stream, int page,
        Windows.Foundation.Rect rect, double scale) => Task.FromResult<MemoryStream?>(null);
}
