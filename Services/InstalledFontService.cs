using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

public static class InstalledFontService
{
    private static readonly (string Family, string[] FileNames)[] SupportedNotoFamilies =
    {
        ("Noto Sans KR", new[] { "NotoSansKR-VF.ttf", "NotoSansKR-Regular.ttf" }),
        ("Noto Serif KR", new[] { "NotoSerifKR-VF.ttf", "NotoSerifKR-Regular.ttf" }),
        ("Noto Sans", new[] { "NotoSans-Regular.ttf" }),
        ("Noto Serif", new[] { "NotoSerif-Regular.ttf" }),
        ("Noto Sans JP", new[] { "NotoSansJP-VF.ttf", "NotoSansJP-Regular.ttf" }),
        ("Noto Serif JP", new[] { "NotoSerifJP-VF.ttf", "NotoSerifJP-Regular.ttf" })
    };

    private static readonly string[] FaceSuffixes =
    {
        "Regular", "Thin", "ExtraLight", "Light", "DemiLight", "SemiLight",
        "Medium", "SemiBold", "DemiBold", "Bold", "ExtraBold", "Black",
        "Italic", "Bold Italic"
    };

    public static IReadOnlyList<string> GetInstalledNotoFamilies()
    {
        HashSet<string> registeredFaces = ReadRegisteredFontFaces();
        string windowsFontDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts");
        string userFontDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "Fonts");

        return SupportedNotoFamilies
            .Where(candidate =>
                registeredFaces.Any(face => IsFaceForFamily(face, candidate.Family)) ||
                candidate.FileNames.Any(fileName =>
                    File.Exists(Path.Combine(windowsFontDirectory, fileName)) ||
                    File.Exists(Path.Combine(userFontDirectory, fileName))))
            .Select(candidate => candidate.Family)
            .ToList();
    }

    private static HashSet<string> ReadRegisteredFontFaces()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadRegisteredFontFaces(Registry.LocalMachine, result);
        ReadRegisteredFontFaces(Registry.CurrentUser, result);
        return result;
    }

    private static void ReadRegisteredFontFaces(RegistryKey root, ISet<string> destination)
    {
        try
        {
            using RegistryKey? fontsKey = root.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (fontsKey == null)
                return;

            foreach (string valueName in fontsKey.GetValueNames())
            {
                int typeSuffix = valueName.LastIndexOf(" (", StringComparison.Ordinal);
                destination.Add(typeSuffix > 0 ? valueName[..typeSuffix] : valueName);
            }
        }
        catch
        {
            // Font-file checks still detect the common machine/user installations.
        }
    }

    private static bool IsFaceForFamily(string faceName, string familyName)
    {
        if (string.Equals(faceName, familyName, StringComparison.OrdinalIgnoreCase))
            return true;

        return FaceSuffixes.Any(suffix => string.Equals(
            faceName,
            $"{familyName} {suffix}",
            StringComparison.OrdinalIgnoreCase));
    }
}
