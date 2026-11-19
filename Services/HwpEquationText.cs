using System;

namespace PDF_simple_edit.Services;

/// <summary>Decodes private-use characters from the HYhwpEQ equation font for plain-text output.</summary>
internal static class HwpEquationText
{
    public static bool IsFont(string? name)
    {
        if (name == null) return false;
        ReadOnlySpan<char> family = name.AsSpan();
        // PDF subset names prepend six uppercase letters and '+'.
        if (family.Length > 7 && family[6] == '+' &&
            family[..6].IndexOfAnyExceptInRange('A', 'Z') < 0)
            family = family[7..];
        if (family.EndsWith("-Identity-H", StringComparison.OrdinalIgnoreCase) ||
            family.EndsWith("-Identity-V", StringComparison.OrdinalIgnoreCase))
            family = family[..^11];
        return family.Equals("HyhwpEQ", StringComparison.OrdinalIgnoreCase);
    }

    public static string Decode(string text)
    {
        char[]? decoded = null;
        for (int i = 0; i < text.Length; i++)
        {
            char symbol = DecodeSymbol(text[i]);
            if (symbol == text[i]) continue;
            decoded ??= text.ToCharArray();
            decoded[i] = symbol;
        }
        return decoded == null ? text : new string(decoded);
    }

    private static char DecodeSymbol(char symbol) => symbol switch
    {
        >= '\uE000' and <= '\uE019' => (char)('A' + symbol - '\uE000'),
        >= '\uE01A' and <= '\uE033' => (char)('a' + symbol - '\uE01A'),
        >= '\uE034' and <= '\uE03D' => "1234567890"[symbol - '\uE034'],
        >= '\uE03E' and <= '\uE05C' => "!@#$%*()−=+[]{}|;:′″,./<>?←−→∫√"[symbol - '\uE03E'],
        >= '\uE067' and <= '\uE06E' => "∑∏∐∩∪←―→"[symbol - '\uE067'],
        >= '\uE072' and <= '\uE077' => "∫∬∭∮∯∰"[symbol - '\uE072'],
        >= '\uE078' and <= '\uE084' => "⎧⎨⎩⎪⎛⎜⎝⎞⎟⎠⎫⎬⎭"[symbol - '\uE078'],
        >= '\uE085' and <= '\uE09C' => "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ"[symbol - '\uE085'],
        >= '\uE09D' and <= '\uE0B4' => "αβγδεζηθικλμνξοπρστυφχψω"[symbol - '\uE09D'],
        >= '\uE0E5' and <= '\uE0FE' => (char)('a' + symbol - '\uE0E5'),
        >= '\uE100' and <= '\uE105' => "⎡⎢⎤⎣⎦⎥"[symbol - '\uE100'],
        _ => symbol
    };
}
