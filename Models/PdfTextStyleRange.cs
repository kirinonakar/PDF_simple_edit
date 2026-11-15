using System;
using System.Collections.Generic;

namespace PDF_simple_edit.Models;

// Offsets count UTF-16 characters excluding line breaks, so reflow can insert
// or remove soft line breaks without moving the character formatting.
public readonly record struct PdfTextStyleRange(int Start, int Length, bool? IsBold, bool? IsItalic, bool? IsUnderline)
{
    public int End => Start + Length;

    public static int CountCharacters(ReadOnlySpan<char> text)
    {
        int count = 0;
        foreach (char character in text)
            if (character is not ('\r' or '\n'))
                count++;
        return count;
    }

    public static IEnumerable<(string Text, PdfTextStyleRange Style)> SplitLine(
        string line, int offset, IReadOnlyList<PdfTextStyleRange> ranges)
    {
        int position = 0;
        foreach (var range in ranges)
        {
            if (range.End <= offset)
                continue;
            if (range.Start >= offset + line.Length)
                break;
            int start = Math.Max(position, range.Start - offset);
            int end = Math.Min(line.Length, range.End - offset);
            if (end <= start)
                continue;
            if (start > position)
                yield return (line[position..start], default);
            yield return (line[start..end], range);
            position = end;
        }
        if (position < line.Length)
            yield return (line[position..], default);
    }
}
