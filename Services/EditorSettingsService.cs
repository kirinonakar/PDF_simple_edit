using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

public sealed record WindowPlacement(int X, int Y, int Width, int Height);

public sealed class EditorSettingsService
{
    private readonly string _settingsPath;

    public EditorSettingsService(string? settingsPath = null)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDF_simple_edit");
        _settingsPath = settingsPath ?? Path.Combine(folder, "window_settings.txt");
    }

    public WindowPlacement? Load(TextFontSettings fontSettings)
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return null;

            string[] lines = File.ReadAllLines(_settingsPath);
            var values = ReadValues(lines);
            ApplyFontSettings(values, fontSettings);

            if (TryReadPlacement(values, out WindowPlacement? placement))
                return placement;

            return TryReadLegacyPlacement(lines, out placement) ? placement : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Editor settings load error: {ex.Message}");
            return null;
        }
    }

    public void Save(WindowPlacement? placement, TextFontSettings fontSettings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var lines = File.Exists(_settingsPath)
                ? File.ReadAllLines(_settingsPath).ToList()
                : new List<string>();

            MigrateLegacyPlacement(lines);
            lines = lines.Where(line => line.IndexOf('=') > 0).ToList();

            if (placement != null)
            {
                SetValue(lines, "WindowX", placement.X.ToString(CultureInfo.InvariantCulture));
                SetValue(lines, "WindowY", placement.Y.ToString(CultureInfo.InvariantCulture));
                SetValue(lines, "WindowWidth", placement.Width.ToString(CultureInfo.InvariantCulture));
                SetValue(lines, "WindowHeight", placement.Height.ToString(CultureInfo.InvariantCulture));
            }

            SetValue(lines, "FontFamily", fontSettings.FontFamily);
            SetValue(lines, "TextEditingMode", fontSettings.TextEditingMode.ToString());
            SetValue(lines, "FontSize", fontSettings.FontSize.ToString("0.##", CultureInfo.InvariantCulture));
            SetValue(lines, "FontColor", fontSettings.Color);
            SetValue(lines, "IsBold", fontSettings.IsBold.ToString(CultureInfo.InvariantCulture));
            SetValue(lines, "IsItalic", fontSettings.IsItalic.ToString(CultureInfo.InvariantCulture));
            File.WriteAllLines(_settingsPath, lines);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Editor settings save error: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ReadValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');
            if (separator > 0)
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static void ApplyFontSettings(IReadOnlyDictionary<string, string> values, TextFontSettings settings)
    {
        if (values.TryGetValue("TextEditingMode", out string? mode) && Enum.TryParse<TextEditingMode>(mode, out var parsed) && Enum.IsDefined(parsed))
            settings.TextEditingMode = parsed;
        if (values.TryGetValue("FontFamily", out string? family) && !string.IsNullOrWhiteSpace(family))
            settings.FontFamily = family;
        if (values.TryGetValue("FontSize", out string? sizeText) &&
            double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size > 0)
            settings.FontSize = size;
        if (values.TryGetValue("FontColor", out string? color) && !string.IsNullOrWhiteSpace(color))
            settings.Color = color;
        if (values.TryGetValue("IsBold", out string? boldText) && bool.TryParse(boldText, out bool bold))
            settings.IsBold = bold;
        if (values.TryGetValue("IsItalic", out string? italicText) && bool.TryParse(italicText, out bool italic))
            settings.IsItalic = italic;
    }

    private static bool TryReadPlacement(IReadOnlyDictionary<string, string> values, out WindowPlacement? placement)
    {
        placement = null;
        if (!values.TryGetValue("WindowX", out string? xText) ||
            !values.TryGetValue("WindowY", out string? yText) ||
            !values.TryGetValue("WindowWidth", out string? widthText) ||
            !values.TryGetValue("WindowHeight", out string? heightText) ||
            !int.TryParse(xText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(yText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
            return false;

        placement = new WindowPlacement(x, y, width, height);
        return true;
    }

    private static bool TryReadLegacyPlacement(IReadOnlyList<string> lines, out WindowPlacement? placement)
    {
        placement = null;
        if (lines.Count < 4 ||
            !int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
            return false;

        placement = new WindowPlacement(x, y, width, height);
        return true;
    }

    private static void MigrateLegacyPlacement(List<string> lines)
    {
        var values = ReadValues(lines);
        if (TryReadPlacement(values, out _) || !TryReadLegacyPlacement(lines, out WindowPlacement? placement))
            return;

        SetValue(lines, "WindowX", placement!.X.ToString(CultureInfo.InvariantCulture));
        SetValue(lines, "WindowY", placement.Y.ToString(CultureInfo.InvariantCulture));
        SetValue(lines, "WindowWidth", placement.Width.ToString(CultureInfo.InvariantCulture));
        SetValue(lines, "WindowHeight", placement.Height.ToString(CultureInfo.InvariantCulture));
    }

    private static void SetValue(List<string> lines, string key, string value)
    {
        string prefix = key + "=";
        int index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            lines[index] = prefix + value;
        else
            lines.Add(prefix + value);
    }
}
