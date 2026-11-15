using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.Kernel.Pdf;
using iText.Kernel.XMP;

namespace PDF_simple_edit.Services;

internal sealed record PdfDocumentProperties(
    IReadOnlyList<(double Width, double Height)> PageSizes, string Version, string? Title, string? Author, string? Subject, string? Keywords,
    string? Creator, string? Producer, string? CreationDate, string? ModificationDate);

internal static class PdfDocumentPropertiesReader
{
    public static PdfDocumentProperties Read(byte[] source, string language)
    {
        using var document = new PdfDocument(new PdfReader(new MemoryStream(source, writable: false)));
        PdfDocumentInfo info = document.GetDocumentInfo();
        XMPMeta? xmp = TryReadXmp(document.GetXmpMetadata);
        string languagePrefix = language.Split('-')[0];

        string? ReadValue(Func<XMPMeta, string?> readXmp, string? fallback)
        {
            string? value = xmp == null ? null : TryReadXmp(() => readXmp(xmp));
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        var pageSizes = Enumerable.Range(1, document.GetNumberOfPages())
            .Select(page => PdfPageCoordinates.GetPhysicalSize(document.GetPage(page))).ToArray();
        return new PdfDocumentProperties(pageSizes, document.GetPdfVersion().ToString(),
            ReadValue(meta => meta.GetLocalizedText(XMPConst.NS_DC, "title", languagePrefix, language)?.GetValue(), info.GetTitle()),
            ReadValue(meta => ReadArray(meta, "creator"), info.GetAuthor()),
            ReadValue(meta => meta.GetLocalizedText(XMPConst.NS_DC, "description", languagePrefix, language)?.GetValue(), info.GetSubject()),
            ReadValue(meta => meta.GetPropertyString(XMPConst.NS_PDF, "Keywords") ?? ReadArray(meta, "subject"), info.GetKeywords()),
            ReadValue(meta => meta.GetPropertyString(XMPConst.NS_XMP, "CreatorTool"), info.GetCreator()),
            ReadValue(meta => meta.GetPropertyString(XMPConst.NS_PDF, "Producer"), info.GetProducer()),
            ReadValue(meta => meta.GetPropertyString(XMPConst.NS_XMP, "CreateDate"), FormatPdfDate(info.GetMoreInfo("CreationDate"))),
            ReadValue(meta => meta.GetPropertyString(XMPConst.NS_XMP, "ModifyDate"), FormatPdfDate(info.GetMoreInfo("ModDate"))));
    }

    private static T? TryReadXmp<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (XMPException) { return null; }
    }

    private static string ReadArray(XMPMeta metadata, string name) => string.Join(", ",
        Enumerable.Range(1, metadata.CountArrayItems(XMPConst.NS_DC, name))
            .Select(index => metadata.GetArrayItem(XMPConst.NS_DC, name, index)?.GetValue())
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? FormatPdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        ReadOnlySpan<char> digits = text.StartsWith("D:", StringComparison.Ordinal) ? text.AsSpan(2) : text.AsSpan();
        var date = digits[..Math.Min(digits.Length, 14)];
        if (date.Length < 4 || date.Length % 2 != 0 || date.IndexOfAnyExceptInRange('0', '9') >= 0)
            return text;

        string formatted = PdfDate.GetW3CDate(text);
        return digits.Length <= 14 && formatted.EndsWith('Z') ? formatted[..^1] : formatted;
    }
}
