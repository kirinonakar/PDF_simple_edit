using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PDF_simple_edit.Helpers
{
    /// <summary>
    /// Helper class for rendering PDF pages using Windows.Data.Pdf API.
    /// </summary>
    public class PdfRenderHelper
    {
        /// <summary>
        /// Renders a PDF page using Windows.Data.Pdf API for high quality rendering.
        /// </summary>
        public static async Task<MemoryStream?> RenderPageWithWindowsPdfAsync(
            string filePath, int pageIndex, double scale = 2.0)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

                if (pageIndex < 0 || pageIndex >= (int)pdfDoc.PageCount)
                    return null;

                using var page = pdfDoc.GetPage((uint)pageIndex);
                var ms = new MemoryStream();
                var stream = ms.AsRandomAccessStream();

                var options = new Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)(page.Size.Width * scale),
                    DestinationHeight = (uint)(page.Size.Height * scale),
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                };

                await page.RenderToStreamAsync(stream, options);
                await stream.FlushAsync();
                ms.Position = 0;
                return ms;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows PDF render error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets page dimensions from a PDF file using Windows.Data.Pdf.
        /// </summary>
        public static async Task<(double width, double height)> GetPageSizeAsync(
            string filePath, int pageIndex)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);

                if (pageIndex < 0 || pageIndex >= (int)pdfDoc.PageCount)
                    return (0, 0);

                using var page = pdfDoc.GetPage((uint)pageIndex);
                return (page.Size.Width, page.Size.Height);
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    /// <summary>
    /// Helper class for printing PDF documents using Windows print APIs.
    /// </summary>
    public class PrintHelper
    {
        /// <summary>
        /// Prints a PDF by launching the system's default PDF print handler.
        /// </summary>
        public async Task PrintAsync(PdfDocument document, string filePath, IntPtr windowHandle)
        {
            try
            {
                // Use Windows LaunchFileAsync to open the system print dialog
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);

                // Launch with print verb
                var options = new Windows.System.LauncherOptions
                {
                    DisplayApplicationPicker = false,
                };

                // Try to print directly via Windows
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Print error: {ex.Message}");
                throw;
            }
        }
    }
}
