using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services;

public static class TextReflowService
{
    public static string Wrap(string text, double width, Func<string, double> measure)
    {
        var output = new StringBuilder();
        var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            if (paragraphIndex > 0) output.Append('\n');
            string paragraph = paragraphs[paragraphIndex];
            int[] starts = StringInfo.ParseCombiningCharacters(paragraph).Append(paragraph.Length).ToArray();
            int start = 0;
            while (start < starts.Length - 1)
            {
                int low = start + 1, high = starts.Length - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (measure(paragraph[starts[start]..starts[middle]]) <= width) low = middle;
                    else high = middle - 1;
                }
                int end = low;
                if (end < starts.Length - 1)
                {
                    for (int i = end; i > start; i--)
                    {
                        string element = paragraph[starts[i - 1]..starts[i]];
                        int rune = char.ConvertToUtf32(element, 0);
                        if (string.IsNullOrWhiteSpace(element) || element is "-" or "\u2010" ||
                            rune is >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7AF)
                        { end = i; break; }
                    }
                }
                output.Append(paragraph.AsSpan(starts[start], starts[end] - starts[start]));
                start = end;
                if (start < starts.Length - 1) output.Append('\n');
            }
        }
        return output.ToString();
    }
}
