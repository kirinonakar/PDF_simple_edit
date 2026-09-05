using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

public static class InstalledFontService
{
    private const string FontsRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static readonly (string Family, string[] FileNames)[] SupportedNotoFamilies =
    {
        ("Noto Sans CJK KR", new[] { "NotoSansCJKkr-Regular.otf", "NotoSansCJKkr-Regular.ttf" }),
        ("Noto Serif CJK KR", new[] { "NotoSerifCJKkr-Regular.otf", "NotoSerifCJKkr-Regular.ttf" }),
        ("Noto Sans CJK JP", new[] { "NotoSansCJKjp-VF.ttf", "NotoSansCJKjp-Regular.otf", "NotoSansCJKjp-Regular.ttf" }),
        ("Noto Serif CJK JP", new[] { "NotoSerifCJKjp-VF.ttf", "NotoSerifCJKjp-Regular.otf", "NotoSerifCJKjp-Regular.ttf" }),
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

    /// <summary>
    /// Finds the installed font file registered for a PDF font family. PDF font
    /// names are often not covered by the editor's small built-in alias table,
    /// so looking at the Windows font registry is required to keep edited and
    /// newly inserted characters on the same face.
    /// </summary>
    public static string? FindFontFile(string familyName, int fontWeight, bool isItalic)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return null;

        var candidates = new List<(string Path, int Score)>();
        ReadRegisteredFontFiles(
            Registry.LocalMachine,
            familyName,
            fontWeight,
            isItalic,
            candidates);
        ReadRegisteredFontFiles(
            Registry.CurrentUser,
            familyName,
            fontWeight,
            isItalic,
            candidates);

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Path)
            .FirstOrDefault(File.Exists);
    }

    // Rank installed faces by family, typographic class, weight and slant.
    internal static IReadOnlyList<string> GetSimilarFontFiles(string originalName, int weight)
    {
        static string Normalize(string name)
        {
            int subset = name.IndexOf('+');
            if (subset >= 0) name = name[(subset + 1)..];
            string result = new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            foreach (string suffix in new[] { "psmt", "mt", "bolditalic", "boldoblique", "semibold", "regular", "italic", "oblique", "bold" })
                if (result.EndsWith(suffix, StringComparison.Ordinal)) result = result[..^suffix.Length];
            return result;
        }
        static int Kind(string name) => name.Contains("mono") || name.Contains("courier") || name.Contains("consolas") ? 2
            : !name.Contains("sans") && (name.Contains("serif") || name.Contains("times") || name.Contains("mincho") || name.Contains("batang") || name.Contains("명조") || name.Contains("바탕")) ? 1 : 0;
        string family = Normalize(originalName);
        bool italic = originalName.Contains("italic", StringComparison.OrdinalIgnoreCase) || originalName.Contains("oblique", StringComparison.OrdinalIgnoreCase);
        var candidates = new List<(string Path, int Score)>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(FontsRegistryPath);
                if (key == null) continue;
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string file || ResolveRegisteredFontPath(file) is not string path) continue;
                    int suffix = name.LastIndexOf(" (", StringComparison.Ordinal);
                    string face = suffix >= 0 ? name[..suffix] : name;
                    string normalized = Normalize(face);
                    int score = (normalized == family ? 100000 : 0) + (Kind(normalized) == Kind(family) ? 10000 : 0)
                        + ScoreFace(face, weight, italic);
                    candidates.Add((path, score));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            using RegistryKey? fontsKey = root.OpenSubKey(FontsRegistryPath);
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

    private static void ReadRegisteredFontFiles(
        RegistryKey root,
        string familyName,
        int requestedWeight,
        bool requestedItalic,
        ICollection<(string Path, int Score)> destination)
    {
        try
        {
            using RegistryKey? fontsKey = root.OpenSubKey(FontsRegistryPath);
            if (fontsKey == null)
                return;

            foreach (string valueName in fontsKey.GetValueNames())
            {
                int typeSuffix = valueName.LastIndexOf(" (", StringComparison.Ordinal);
                string faceName = typeSuffix > 0 ? valueName[..typeSuffix] : valueName;
                if (!IsFaceForFamily(faceName, familyName))
                    continue;

                if (fontsKey.GetValue(valueName) is not string registeredFile ||
                    string.IsNullOrWhiteSpace(registeredFile))
                    continue;

                string? path = ResolveRegisteredFontPath(registeredFile);
                if (path == null)
                    continue;

                destination.Add((path, ScoreFace(faceName, requestedWeight, requestedItalic)));
            }
        }
        catch
        {
            // The caller retains the built-in aliases and generic fallbacks.
        }
    }

    private static string? ResolveRegisteredFontPath(string registeredFile)
    {
        string cleanedFile = registeredFile.Trim().Trim('"');
        if (Path.IsPathRooted(cleanedFile))
            return File.Exists(cleanedFile) ? cleanedFile : null;

        string windowsFontDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts");
        string userFontDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "Fonts");
        string windowsPath = Path.Combine(windowsFontDirectory, cleanedFile);
        if (File.Exists(windowsPath))
            return windowsPath;

        string userPath = Path.Combine(userFontDirectory, cleanedFile);
        return File.Exists(userPath) ? userPath : null;
    }

    private static int ScoreFace(string faceName, int requestedWeight, bool requestedItalic)
    {
        string normalized = faceName.ToLowerInvariant();
        bool isItalic = normalized.Contains("italic") || normalized.Contains("oblique");
        int score = isItalic == requestedItalic ? 1000 : 0;
        int faceWeight = ResolveFaceWeight(normalized);
        score -= Math.Abs(Math.Clamp(requestedWeight, 1, 999) - faceWeight);
        if (!FaceSuffixes.Any(suffix => normalized.EndsWith(
            " " + suffix.ToLowerInvariant(),
            StringComparison.Ordinal)))
        {
            score += requestedWeight is >= 350 and <= 550 ? 100 : 0;
        }

        return score;
    }

    private static int ResolveFaceWeight(string normalizedFaceName)
    {
        if (normalizedFaceName.Contains("thin") || normalizedFaceName.Contains("hairline")) return 100;
        if (normalizedFaceName.Contains("extralight") || normalizedFaceName.Contains("ultralight")) return 200;
        if (normalizedFaceName.Contains("semilight") || normalizedFaceName.Contains("demilight")) return 350;
        if (normalizedFaceName.Contains("light")) return 300;
        if (normalizedFaceName.Contains("medium")) return 500;
        if (normalizedFaceName.Contains("semibold") || normalizedFaceName.Contains("demibold")) return 600;
        if (normalizedFaceName.Contains("extrabold") || normalizedFaceName.Contains("ultrabold")) return 800;
        if (normalizedFaceName.Contains("black") || normalizedFaceName.Contains("heavy")) return 900;
        if (normalizedFaceName.Contains("bold")) return 700;
        return 400;
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
