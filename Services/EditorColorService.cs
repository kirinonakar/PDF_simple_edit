using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace PDF_simple_edit.Services;

public static class EditorColorService
{
    private static readonly string[] Palette =
    {
        "#000000", "#FFFFFF", "#FF0000", "#FF6600", "#FFCC00", "#00CC00",
        "#0066FF", "#9900FF", "#333333", "#666666", "#999999", "#CC3333",
        "#FF9933", "#FFFF66", "#66CC66", "#3399FF", "#CC66FF", "#FF6699"
    };

    public static void PopulatePalette(GridView gridView)
    {
        foreach (string color in Palette)
        {
            var border = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Parse(color)),
                Tag = color
            };
            if (color == "#FFFFFF")
            {
                border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                border.BorderThickness = new Thickness(1);
            }
            gridView.Items.Add(border);
        }
    }

    public static Windows.UI.Color Parse(string hexColor)
    {
        try
        {
            string value = hexColor.TrimStart('#');
            if (value.Length == 8)
            {
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16),
                    Convert.ToByte(value.Substring(6, 2), 16));
            }
            if (value.Length == 6)
            {
                return Windows.UI.Color.FromArgb(
                    255,
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16));
            }
        }
        catch
        {
        }
        return Windows.UI.Color.FromArgb(255, 0, 0, 0);
    }
}
