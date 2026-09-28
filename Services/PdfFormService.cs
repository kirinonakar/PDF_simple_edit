using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Kernel.Font;
using iText.IO.Font;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;

namespace PDF_simple_edit.Services;

public enum PdfFormInputKind { Text, CheckBox }

public sealed record PdfFormInput(
    string Name, int PageIndex, PdfTextBox Bounds, PdfFormInputKind Kind,
    string Value, string OnValue, bool IsMultiline, int MaxLength);

public sealed class PdfFormService
{
    public IReadOnlyList<PdfFormInput> ReadPage(byte[] pdfBytes, int pageIndex)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var document = new PdfDocument(reader);
        var form = PdfAcroForm.GetAcroForm(document, false);
        if (form == null || pageIndex < 0 || pageIndex >= document.GetNumberOfPages())
            return Array.Empty<PdfFormInput>();

        var page = document.GetPage(pageIndex + 1);
        var widgetNumbers = page.GetAnnotations()
            .Select(annotation => annotation.GetPdfObject().GetIndirectReference()?.GetObjNumber())
            .Where(number => number.HasValue).Select(number => number!.Value).ToHashSet();
        var transform = PdfPageCoordinates.ToDisplay(page);
        var inputs = new List<PdfFormInput>();
        foreach (var pair in form.GetAllFormFields())
        {
            PdfFormField field = pair.Value;
            if (field.GetFieldFlag(1)) continue; // PDF read-only flag
            PdfFormInputKind kind;
            if (field is PdfTextFormField) kind = PdfFormInputKind.Text;
            else if (field is PdfButtonFormField button && !button.IsPushButton())
                kind = PdfFormInputKind.CheckBox;
            else continue;

            foreach (var widget in field.GetWidgets())
            {
                int? number = widget.GetPdfObject().GetIndirectReference()?.GetObjNumber();
                if (number == null || !widgetNumbers.Contains(number.Value)) continue;
                var rect = widget.GetRectangle().ToRectangle();
                var bounds = transform.Map(new PdfTextBox(
                    rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
                string onValue = widget.GetPdfObject().GetAsDictionary(PdfName.AP)?
                    .GetAsDictionary(PdfName.N)?.KeySet()
                    .FirstOrDefault(name => name.GetValue() != "Off")?.GetValue() ?? "Yes";
                inputs.Add(new PdfFormInput(pair.Key, pageIndex, bounds, kind,
                    field.GetValueAsString() ?? string.Empty, onValue,
                    field.GetFieldFlag(1 << 12),
                    field.GetPdfObject().GetAsNumber(PdfName.MaxLen)?.IntValue() ?? 0));
            }
        }
        return inputs;
    }

    public void SetValue(PdfDocumentManager manager, PdfFormInput input, string value)
    {
        manager.ApplyBatchEdit(document =>
        {
            var field = PdfAcroForm.GetAcroForm(document, false)?.GetField(input.Name)
                ?? throw new InvalidOperationException($"양식 필드를 찾을 수 없습니다: {input.Name}");
            if (field.GetFieldFlag(1))
                throw new InvalidOperationException("읽기 전용 양식 필드는 수정할 수 없습니다.");
            if (field is PdfTextFormField && value.Any(character => character > 127))
            {
                string fontPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf");
                if (File.Exists(fontPath))
                {
                    var font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H,
                        PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
                    field.SetValue(value, font, 0);
                }
                else field.SetValue(value);
            }
            else field.SetValue(value);
            if (field is PdfButtonFormField && value != "Off")
                AddPortableCheckAppearance(field, value);
        });
    }

    private static void AddPortableCheckAppearance(PdfFormField field, string value)
    {
        foreach (var widget in field.GetWidgets())
        {
            PdfStream? stream = widget.GetPdfObject().GetAsDictionary(PdfName.AP)?
                .GetAsDictionary(PdfName.N)?.GetAsStream(new PdfName(value));
            if (stream == null) continue;
            var box = stream.GetAsArray(PdfName.BBox)?.ToRectangle();
            if (box == null) continue;
            double w = box.GetWidth(), h = box.GetHeight();
            if (w <= 0 || h <= 0) continue;
            string N(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
            // A vector tick remains visible when the PDF's Symbol/ZapfDingbats
            // font is absent on the machine opening the saved form.
            string tick = $"\nq 0 0 0 RG {N(Math.Max(.7, Math.Min(w, h) * .11))} w " +
                $"{N(w * .2)} {N(h * .48)} m {N(w * .42)} {N(h * .25)} l " +
                $"{N(w * .82)} {N(h * .77)} l S Q\n";
            byte[] original = stream.GetBytes();
            stream.SetData(original.Concat(Encoding.ASCII.GetBytes(tick)).ToArray());
        }
    }
}
