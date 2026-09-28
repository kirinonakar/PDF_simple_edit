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
using System.Text;

namespace PDF_simple_edit.Services;

public enum PdfFormInputKind { Text, CheckBox }

public sealed record PdfFormInput(
    string Name, int PageIndex, PdfTextBox Bounds, PdfFormInputKind Kind,
    string Value, string OnValue, bool IsMultiline, int MaxLength);

public sealed class PdfFormService
{
    public bool NeedsAppearanceRepair(byte[] pdfBytes)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var document = new PdfDocument(reader);
        var form = PdfAcroForm.GetAcroForm(document, false);
        return form != null && form.GetAllFormFields().Values
            .OfType<PdfButtonFormField>()
            .Any(field => !field.IsPushButton() &&
                field.GetValueAsString() is { Length: > 0 } value && value != "Off" &&
                field.GetWidgets().Any(widget =>
                    FindMatchingAuthorAppearance(form, widget, value) != null));
    }

    public void RepairAppearances(PdfDocument document)
    {
        var form = PdfAcroForm.GetAcroForm(document, false);
        if (form == null) return;
        foreach (var field in form.GetAllFormFields().Values.OfType<PdfButtonFormField>()
            .Where(button => !button.IsPushButton()))
        {
            string value = field.GetValueAsString();
            if (!string.IsNullOrEmpty(value) && value != "Off")
                RestoreOriginalCheckAppearance(form, field, value);
        }
    }

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
            if (field is PdfButtonFormField button && !button.IsPushButton())
            {
                // Existing widgets already have the form author's checked and
                // unchecked artwork. Regenerating it changes the check shape.
                RestoreOriginalCheckAppearance(PdfAcroForm.GetAcroForm(document, false)!,
                    field, value);
                field.SetValue(value, false);
                foreach (var widget in field.GetWidgets())
                {
                    var normal = widget.GetPdfObject().GetAsDictionary(PdfName.AP)?
                        .GetAsDictionary(PdfName.N);
                    string onValue = normal?.KeySet()
                        .FirstOrDefault(name => name.GetValue() != "Off")?.GetValue() ?? value;
                    widget.GetPdfObject().Put(PdfName.AS,
                        new PdfName(value == onValue ? onValue : "Off"));
                }
            }
            else if (field is PdfTextFormField && value.Any(character => character > 127))
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
        });
    }

    private static void RestoreOriginalCheckAppearance(PdfAcroForm form,
        PdfFormField field, string value)
    {
        if (value == "Off") return;
        foreach (var widget in field.GetWidgets())
        {
            PdfStream? original = FindMatchingAuthorAppearance(form, widget, value);
            if (original == null) continue;
            widget.GetPdfObject().GetAsDictionary(PdfName.AP)!
                .GetAsDictionary(PdfName.N)!.Put(new PdfName(value), original);
        }
    }

    private static PdfStream? FindMatchingAuthorAppearance(PdfAcroForm form,
        iText.Kernel.Pdf.Annot.PdfWidgetAnnotation widget, string value)
    {
        PdfStream? current = widget.GetPdfObject().GetAsDictionary(PdfName.AP)?
            .GetAsDictionary(PdfName.N)?.GetAsStream(new PdfName(value));
        if (current == null || !IsRegeneratedCheck(current)) return null;
        var box = current.GetAsArray(PdfName.BBox)?.ToRectangle();
        if (box == null) return null;

        return form.GetAllFormFields().Values
                .OfType<PdfButtonFormField>()
                .SelectMany(button => button.GetWidgets())
                .Where(other => !ReferenceEquals(other.GetPdfObject(), widget.GetPdfObject()))
                .Select(other => other.GetPdfObject().GetAsDictionary(PdfName.AP)?
                    .GetAsDictionary(PdfName.N)?.GetAsStream(new PdfName(value)))
                .FirstOrDefault(candidate => candidate != null &&
                    IsAuthorCheck(candidate) &&
                    candidate.GetAsArray(PdfName.BBox)?.ToRectangle() is { } candidateBox &&
                    Math.Abs(candidateBox.GetWidth() - box.GetWidth()) < .1 &&
                    Math.Abs(candidateBox.GetHeight() - box.GetHeight()) < .1);
    }

    private static bool IsRegeneratedCheck(PdfStream appearance)
    {
        string content = Encoding.ASCII.GetString(appearance.GetBytes());
        return content.Contains("/F1 ", StringComparison.Ordinal) &&
            content.Contains(")Tj", StringComparison.Ordinal);
    }

    private static bool IsAuthorCheck(PdfStream appearance)
    {
        string content = Encoding.ASCII.GetString(appearance.GetBytes());
        return content.Contains("/ZaDb ", StringComparison.Ordinal) &&
            content.Contains("(n) Tj", StringComparison.Ordinal);
    }
}
