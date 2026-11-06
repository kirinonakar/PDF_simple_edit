using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Mcp;

internal readonly struct PdfMcpArguments(JsonElement value)
{
    public JsonElement Value => value;
    public bool Has(string key) => value.TryGetProperty(key, out _);
    public string Text(string key, string? fallback = null)
    {
        if (!value.TryGetProperty(key, out var item)) return fallback ?? throw new ArgumentException($"Missing {key}.");
        if (item.ValueKind != JsonValueKind.String) throw new ArgumentException($"{key} must be a string.");
        return item.GetString()!;
    }
    public string Path(string key)
    {
        string path = Text(key);
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException($"{key} must be an absolute path.");
        return System.IO.Path.GetFullPath(path);
    }
    public double Number(string key, double fallback = 0, double min = -1_000_000, double max = 1_000_000)
    {
        if (!value.TryGetProperty(key, out var item)) return fallback;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double number) || !double.IsFinite(number) || number < min || number > max)
            throw new ArgumentException($"{key} must be a finite number between {min} and {max}.");
        return number;
    }
    public int Integer(string key, int fallback = 1, int min = 1, int max = 100000)
    {
        double number = Number(key, fallback, min, max);
        if (number != Math.Truncate(number)) throw new ArgumentException($"{key} must be an integer.");
        return (int)number;
    }
    public bool Flag(string key, bool fallback = false)
    {
        if (!value.TryGetProperty(key, out var item)) return fallback;
        if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{key} must be boolean.");
        return item.GetBoolean();
    }
    public PdfTextBox Bounds(PdfTextBox? fallback = null) => new(
        Number("x", fallback?.X ?? 0), Number("y", fallback?.Y ?? 0),
        Number("width", fallback?.Width ?? 100, .001), Number("height", fallback?.Height ?? 100, .001));
    public string Color(string key = "color", string fallback = "#000000")
    {
        string color = Text(key, fallback);
        if (color.Length != 7 || color[0] != '#' || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"{key} must be #RRGGBB.");
        return color;
    }
    public static iText.Kernel.Colors.DeviceRgb PdfColor(string color) => new(
        int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber), int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber), int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber));
}
