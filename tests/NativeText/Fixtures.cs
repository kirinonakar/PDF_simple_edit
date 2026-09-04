using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Geom;
using System.Text;

internal static class Fixtures
{
    public static byte[] Create()
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            font.GetPdfObject().MakeIndirect(doc);
            var fonts = new PdfDictionary(); fonts.Put(new PdfName("F1"), font.GetPdfObject());
            var resources = new PdfDictionary(); resources.Put(PdfName.Font, fonts);
            var form = new PdfStream(Encoding.ASCII.GetBytes("q .1 .6 .8 rg 0 0 180 35 re f Q BT /F1 13 Tf 1 0 0 1 4 12 Tm (Shared form) Tj ET"));
            form.Put(PdfName.Type, PdfName.XObject); form.Put(PdfName.Subtype, PdfName.Form);
            form.Put(PdfName.BBox, new PdfArray(new float[] { 0, 0, 180, 35 }));
            form.Put(PdfName.Resources, resources); form.MakeIndirect(doc);
            var xObjects = new PdfDictionary(); xObjects.Put(new PdfName("Fm"), form);
            var pageResources = new PdfDictionary(); pageResources.Put(PdfName.Font, fonts); pageResources.Put(PdfName.XObject, xObjects);
            string content = """
                % preserve this graphic and comment verbatim
                q .9 .85 .7 rg 20 20 350 450 re f Q
                BT /F1 12 Tf 1.4 Tc 2.5 Tw 80 Tz 17 TL
                1 0 0 1 30 430 Tm [(AB) -135 (C DE) 80 (FG)] TJ ( tail) Tj
                (quote one) ' 3 2 (quote two) " ( after) Tj
                T* (next line) Tj ET
                q 1.1 .2 -.1 .9 30 250 cm BT /F1 16 Tf 1 0 0 1 5 10 Tm (Skewed) Tj ( neighbor) Tj ET Q
                q 1 0 0 1 30 140 cm /Fm Do Q
                q 1 0 0 1 200 80 cm /Fm Do Q
                """;
            var stream = new PdfStream(Encoding.ASCII.GetBytes(content)); stream.MakeIndirect(doc);
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var page = doc.AddNewPage(new PageSize(400, 500));
                page.SetCropBox(new Rectangle(10, 15, 375, 475)); page.SetRotation(rotation);
                page.GetPdfObject().Put(PdfName.Resources, pageResources);
                page.GetPdfObject().Put(PdfName.Contents, stream);
            }
            font.Flush();
        }
        return output.ToArray();
    }
}
