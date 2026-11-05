using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace PDF_simple_edit.Services;

public static class LocalizationService
{
    public const string SystemLanguage = "system";
    public const string Korean = "ko-KR";
    public const string English = "en-US";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> KoreanStrings = new(() => Load(Korean));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnglishStrings = new(() => Load(English));

    public static string CurrentLanguage { get; private set; } = ResolveLanguage(SystemLanguage, GetWindowsDisplayLanguage());

    // Called before constructing XAML. A preference change is applied at the next launch,
    // keeping existing documents, dialogs and status messages in a single language.
    public static void Initialize(string? preference) =>
        CurrentLanguage = ResolveLanguage(preference, GetWindowsDisplayLanguage());

    public static string NormalizePreference(string? preference) => preference?.Trim().ToLowerInvariant() switch
    {
        "ko" or "ko-kr" => Korean,
        "en" or "en-us" => English,
        _ => SystemLanguage
    };

    public static string ResolveLanguage(string? preference, string windowsDisplayLanguage)
    {
        string normalized = NormalizePreference(preference);
        if (normalized != SystemLanguage)
            return normalized;
        return windowsDisplayLanguage.Equals("ko", StringComparison.OrdinalIgnoreCase) ||
               windowsDisplayLanguage.StartsWith("ko-", StringComparison.OrdinalIgnoreCase)
            ? Korean : English;
    }

    public static string Get(string key)
    {
        var strings = CurrentLanguage == Korean ? KoreanStrings.Value : EnglishStrings.Value;
        if (strings.TryGetValue(key, out string? value) || EnglishStrings.Value.TryGetValue(key, out value))
            return value;
        throw new KeyNotFoundException($"Missing localization resource: {key}");
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(CurrentLanguage), Get(key), arguments);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        // Embed the same .resw files used by the Windows resource build so lookup also
        // works in unpackaged/single-file deployments without external resource files.
        using Stream stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(
            $"PDF_simple_edit.Strings.{language}.Resources.resw")
            ?? throw new InvalidOperationException($"Missing resources for {language}.");
        return XDocument.Load(stream).Root!.Elements("data").ToDictionary(
            item => (string)item.Attribute("name")!,
            item => (string)item.Element("value")!,
            StringComparer.Ordinal);
    }

    private static string GetWindowsDisplayLanguage()
    {
        if (OperatingSystem.IsWindows())
            return CultureInfo.GetCultureInfo((int)GetUserDefaultUILanguage()).Name;
        return CultureInfo.CurrentUICulture.Name;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}
