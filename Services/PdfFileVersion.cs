using System;
using System.IO;
using System.Security.Cryptography;

namespace PDF_simple_edit.Services;

internal sealed record PdfFileVersion(byte[]? Hash)
{
    internal static PdfFileVersion Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(SHA256.HashData(stream));
        }
        catch (FileNotFoundException) { return new((byte[]?)null); }
    }

    internal bool Matches(PdfFileVersion other) => Hash == null ? other.Hash == null
        : other.Hash != null && Hash.AsSpan().SequenceEqual(other.Hash);
}
