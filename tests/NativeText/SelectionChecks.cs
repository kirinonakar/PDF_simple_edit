using iText.Kernel.Pdf;
using PDF_simple_edit.Models;
using PDF_simple_edit.Services;

internal static class SelectionChecks
{
    public static async Task Run(byte[] bytes)
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var service = new AnnotationContentService();
        var extractor = new PdfPageContentExtractor();
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        int textHits = 0, images = 0, graphics = 0;
        foreach (var mode in new[] { TextEditingMode.PreserveOriginal, TextEditingMode.Legacy })
        for (int p = 0; p < doc.GetNumberOfPages(); p++)
        {
            var size = doc.GetPage(p + 1).GetPageSize();
            var contents = await extractor.ExtractAsync(bytes, p, mode);
            var background = new PdfAnnotation { Type = AnnotationType.Image, PageIndex = p,
                X = -1000, Y = -1000, Width = 5000, Height = 5000, IsOriginalImageReplacement = true };
            foreach (var content in contents)
            {
                if (content.Type == PageContentType.Image)
                {
                    if (content.GraphicOperations.Count == 0) images++;
                    else
                    {
                        graphics++;
                        Check(content.Width < size.GetWidth() * .8 + 2 || content.Height < size.GetHeight() * .8 + 2,
                            $"Page {p + 1}: page furniture exposed as an image");
                    }
                    continue;
                }
                var line = content.NativeText?.Lines.FirstOrDefault();
                double x = line != null ? line.Value.X + line.Value.Width / 2 : content.X + content.Width / 2;
                double y = line != null ? line.Value.Y + line.Value.Height / 2 : content.Y + content.Height / 2;
                var selected = service.FindEditableContent(contents, x, y);
                Check(selected?.Type == PageContentType.Text, $"Page {p + 1}: text click selected an image");
                Check(service.ShouldPreferPageContent(background, selected), "Original image intercepted unselected text");
                var annotation = service.ConvertToAnnotation(selected!, p);
                Check(service.FindAnnotationAt(new[] { annotation, background }, p, x, y) == annotation,
                    "Original image intercepted existing text selection");
                var insertedImage = new PdfAnnotation { Type = AnnotationType.Image, PageIndex = p,
                    X = x - 10, Y = y - 10, Width = 20, Height = 20 };
                Check(!service.ShouldPreferPageContent(insertedImage, selected), "Inserted image lost selection priority");
                textHits++;
            }
            Check(service.FindAnnotationAt(new[] { background }, p, -500, -500) == background,
                "Original image cannot be selected away from text");
        }
        Check(images > 0 && graphics > 0, "Real figures or local vector graphics disappeared");
        Console.WriteLine($"PASS selection: {textHits} text hits, {images} image placements, {graphics} local graphics; both editing modes");
    }
}
