using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using System;
using System.Text;

namespace PDF_simple_edit.Helpers;

internal readonly record struct PdfFontMetadata(string RawName, int Weight);

internal static class PdfFontMetadataResolver
{
    public static PdfFontMetadata Resolve(PdfFont? font)
    {
        string rawName = string.Empty;
        int weight = 400;
        try
        {
            var names = font?.GetFontProgram()?.GetFontNames();
            rawName = names?.GetFontName() ?? string.Empty;
            weight = names?.GetFontWeight() ?? 400;
        }
        catch
        {
            // The PDF wrapper can omit the original name table. Inspect the embedded file next.
        }

        if (font != null && TryGetEmbeddedFontBytes(font, out byte[] embeddedBytes))
        {
            try
            {
                var names = FontProgramFactory.CreateFont(embeddedBytes).GetFontNames();
                string embeddedName = names.GetFontName();
                if (!string.IsNullOrWhiteSpace(embeddedName))
                    rawName = embeddedName;
                if (names.GetFontWeight() > 0)
                    weight = names.GetFontWeight();
            }
            catch
            {
                string embeddedName = ReadTrueTypeName(embeddedBytes);
                if (!string.IsNullOrWhiteSpace(embeddedName))
                    rawName = embeddedName;
            }

            int embeddedWeight = ReadTrueTypeWeight(embeddedBytes);
            if (embeddedWeight > 0)
                weight = embeddedWeight;
        }

        rawName = ResolveAnonymousTimesFamily(font, rawName, ref weight);

        weight = ResolveNamedFontWeight(rawName, weight);
        return new PdfFontMetadata(rawName, Math.Clamp(weight > 0 ? weight : 400, 1, 999));
    }

    private static int ResolveNamedFontWeight(string fontName, int fallbackWeight)
    {
        string normalized = (fontName ?? string.Empty)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (normalized.Contains("hairline") || normalized.Contains("thin")) return 100;
        if (normalized.Contains("extralight") || normalized.Contains("ultralight")) return 200;
        if (normalized.Contains("semilight") || normalized.Contains("demilight")) return 350;
        if (normalized.Contains("light")) return 300;
        if (normalized.Contains("medium")) return 500;
        if (normalized.Contains("semibold") || normalized.Contains("demibold")) return 600;
        if (normalized.Contains("extrabold") || normalized.Contains("ultrabold")) return 800;
        if (normalized.Contains("black") || normalized.Contains("heavy")) return 900;
        if (normalized.Contains("bold") || normalized.EndsWith(".b", StringComparison.Ordinal)) return 700;
        return fallbackWeight;
    }

    private static string ResolveAnonymousTimesFamily(
        PdfFont? font,
        string rawFontName,
        ref int weight)
    {
        if (font == null || string.IsNullOrWhiteSpace(rawFontName))
            return rawFontName;

        string unprefixedName = rawFontName;
        int subsetSeparator = unprefixedName.IndexOf('+');
        if (subsetSeparator >= 0 && subsetSeparator + 1 < unprefixedName.Length)
            unprefixedName = unprefixedName[(subsetSeparator + 1)..];
        if (!unprefixedName.StartsWith("AdvTT", StringComparison.OrdinalIgnoreCase))
            return rawFontName;

        bool isBold = unprefixedName.EndsWith(".B", StringComparison.OrdinalIgnoreCase);
        bool isItalic = unprefixedName.EndsWith(".I", StringComparison.OrdinalIgnoreCase);
        if (!MatchesTimesNewRomanMetrics(font, isBold, isItalic))
            return rawFontName;

        if (isBold)
            weight = 700;
        return isBold && isItalic
            ? "Times New Roman Bold Italic"
            : isBold
                ? "Times New Roman Bold"
                : isItalic
                    ? "Times New Roman Italic"
                    : "Times New Roman";
    }

    private static bool MatchesTimesNewRomanMetrics(PdfFont font, bool isBold, bool isItalic)
    {
        string samples;
        int[] expectedWidths;
        if (isBold)
        {
            samples = "AMWaimst";
            expectedWidths = new[] { 722, 944, 1000, 500, 278, 833, 389, 333 };
        }
        else if (isItalic)
        {
            samples = "AMWaimst";
            expectedWidths = new[] { 611, 833, 833, 500, 278, 722, 389, 278 };
        }
        else
        {
            samples = "AMWaimst";
            expectedWidths = new[] { 722, 889, 944, 444, 278, 778, 389, 278 };
        }

        int compared = 0;
        int totalDifference = 0;
        int maximumDifference = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            try
            {
                int actualWidth = font.GetWidth(samples[index]);
                if (actualWidth <= 0)
                    continue;

                int difference = Math.Abs(actualWidth - expectedWidths[index]);
                totalDifference += difference;
                maximumDifference = Math.Max(maximumDifference, difference);
                compared++;
            }
            catch
            {
                return false;
            }
        }

        return compared >= 4 &&
            maximumDifference <= 55 &&
            totalDifference / compared <= 25;
    }

    internal static bool TryGetEmbeddedFontBytes(PdfFont font, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            PdfDictionary fontDictionary = font.GetPdfObject();
            PdfDictionary? descriptor = fontDictionary.GetAsDictionary(PdfName.FontDescriptor);
            if (descriptor == null)
            {
                PdfArray? descendants = fontDictionary.GetAsArray(PdfName.DescendantFonts);
                descriptor = descendants?.GetAsDictionary(0)?.GetAsDictionary(PdfName.FontDescriptor);
            }

            PdfStream? stream = descriptor?.GetAsStream(PdfName.FontFile)
                ?? descriptor?.GetAsStream(PdfName.FontFile2)
                ?? descriptor?.GetAsStream(PdfName.FontFile3);
            if (stream == null)
                return false;

            bytes = stream.GetBytes();
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadTrueTypeName(byte[] fontBytes)
    {
        if (!TryFindTrueTypeTable(fontBytes, "name", out int tableOffset, out int tableLength) ||
            tableLength < 6 || tableOffset + 6 > fontBytes.Length)
            return string.Empty;

        int recordCount = ReadUInt16BigEndian(fontBytes, tableOffset + 2);
        int stringOffset = ReadUInt16BigEndian(fontBytes, tableOffset + 4);
        int bestScore = int.MinValue;
        string bestName = string.Empty;
        for (int index = 0; index < recordCount; index++)
        {
            int recordOffset = tableOffset + 6 + (index * 12);
            if (recordOffset + 12 > fontBytes.Length)
                break;

            int platformId = ReadUInt16BigEndian(fontBytes, recordOffset);
            int languageId = ReadUInt16BigEndian(fontBytes, recordOffset + 4);
            int nameId = ReadUInt16BigEndian(fontBytes, recordOffset + 6);
            int length = ReadUInt16BigEndian(fontBytes, recordOffset + 8);
            int offset = tableOffset + stringOffset + ReadUInt16BigEndian(fontBytes, recordOffset + 10);
            if (nameId is not (1 or 6 or 16) || length <= 0 || offset < 0 || offset + length > fontBytes.Length)
                continue;

            string value;
            try
            {
                value = platformId is 0 or 3
                    ? Encoding.BigEndianUnicode.GetString(fontBytes, offset, length)
                    : Encoding.Latin1.GetString(fontBytes, offset, length);
            }
            catch
            {
                continue;
            }

            value = value.Trim('\0', ' ');
            if (string.IsNullOrWhiteSpace(value))
                continue;

            int score = nameId == 6 ? 300 : nameId == 16 ? 200 : 100;
            score += platformId == 3 ? 30 : platformId == 0 ? 20 : 0;
            if (languageId is 0x0409 or 0x0412)
                score += 10;
            if (score > bestScore)
            {
                bestScore = score;
                bestName = value;
            }
        }

        return bestName;
    }

    private static int ReadTrueTypeWeight(byte[] fontBytes) =>
        TryFindTrueTypeTable(fontBytes, "OS/2", out int tableOffset, out int tableLength) &&
        tableLength >= 6 && tableOffset + 6 <= fontBytes.Length
            ? ReadUInt16BigEndian(fontBytes, tableOffset + 4)
            : 0;

    private static bool TryFindTrueTypeTable(
        byte[] fontBytes,
        string tag,
        out int tableOffset,
        out int tableLength)
    {
        tableOffset = 0;
        tableLength = 0;
        if (fontBytes.Length < 12)
            return false;

        int tableCount = ReadUInt16BigEndian(fontBytes, 4);
        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = 12 + (index * 16);
            if (recordOffset + 16 > fontBytes.Length)
                return false;
            if (!string.Equals(Encoding.ASCII.GetString(fontBytes, recordOffset, 4), tag, StringComparison.Ordinal))
                continue;

            uint offset = ReadUInt32BigEndian(fontBytes, recordOffset + 8);
            uint length = ReadUInt32BigEndian(fontBytes, recordOffset + 12);
            if (offset > int.MaxValue || length > int.MaxValue || offset + length > fontBytes.Length)
                return false;
            tableOffset = (int)offset;
            tableLength = (int)length;
            return true;
        }

        return false;
    }

    private static ushort ReadUInt16BigEndian(byte[] bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static uint ReadUInt32BigEndian(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) |
        ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) |
        bytes[offset + 3];
}
