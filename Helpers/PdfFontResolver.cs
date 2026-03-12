using PdfSharp.Fonts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Helpers
{
    /// <summary>
    /// Custom font resolver that supports Korean fonts and system fonts.
    /// </summary>
    public class PdfFontResolver : IFontResolver
    {
        private static readonly Dictionary<string, string> _fontFamilyMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _fontFileCache = new(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized = false;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Scan system fonts
            var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (Directory.Exists(fontsDir))
            {
                foreach (var file in Directory.GetFiles(fontsDir, "*.ttf"))
                {
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    _fontFileCache[name] = file;
                }
                foreach (var file in Directory.GetFiles(fontsDir, "*.ttc"))
                {
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    _fontFileCache[name] = file;
                }
            }

            // Map common Korean font families
            MapFont("맑은 고딕", "malgun");
            MapFont("Malgun Gothic", "malgun");
            MapFont("MalgunGothic", "malgun");
            MapFont("맑은고딕", "malgun");
            MapFont("굴림", "gulim");
            MapFont("Gulim", "gulim");
            MapFont("돋움", "dotum");
            MapFont("Dotum", "dotum");
            MapFont("바탕", "batang");
            MapFont("Batang", "batang");
            MapFont("궁서", "gungsuh");
            MapFont("Gungsuh", "gungsuh");
            MapFont("나눔고딕", "NanumGothic");
            MapFont("NanumGothic", "NanumGothic");
            MapFont("나눔바른고딕", "NanumBarunGothic");
            MapFont("NanumBarunGothic", "NanumBarunGothic");
            MapFont("나눔명조", "NanumMyeongjo");
            MapFont("NanumMyeongjo", "NanumMyeongjo");

            // Standard fonts
            MapFont("Arial", "arial");
            MapFont("Times New Roman", "times");
            MapFont("Courier New", "cour");
            MapFont("Verdana", "verdana");
            MapFont("Tahoma", "tahoma");
            MapFont("Segoe UI", "segoeui");
            MapFont("Consolas", "consola");
            MapFont("Calibri", "calibri");
            MapFont("Cambria", "cambria");

            GlobalFontSettings.FontResolver = new PdfFontResolver();
        }

        private static void MapFont(string familyName, string fileBaseName)
        {
            _fontFamilyMap[familyName] = fileBaseName.ToLowerInvariant();
        }

        public byte[]? GetFont(string faceName)
        {
            try
            {
                // Try direct file path
                if (File.Exists(faceName))
                    return File.ReadAllBytes(faceName);

                var lower = faceName.ToLowerInvariant();

                // Try from cache
                if (_fontFileCache.TryGetValue(lower, out var path) && File.Exists(path))
                    return File.ReadAllBytes(path);

                // Try with bold/italic variants
                foreach (var suffix in new[] { "", "b", "i", "bi", "bd", "z" })
                {
                    var key = lower + suffix;
                    if (_fontFileCache.TryGetValue(key, out var p) && File.Exists(p))
                        return File.ReadAllBytes(p);
                }

                // Try fonts directory directly
                var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                var directPath = Path.Combine(fontsDir, faceName);
                if (File.Exists(directPath))
                    return File.ReadAllBytes(directPath);

                directPath = Path.Combine(fontsDir, faceName + ".ttf");
                if (File.Exists(directPath))
                    return File.ReadAllBytes(directPath);

                directPath = Path.Combine(fontsDir, faceName + ".ttc");
                if (File.Exists(directPath))
                    return File.ReadAllBytes(directPath);

                // Fallback: malgun gothic
                if (_fontFileCache.TryGetValue("malgun", out var fallback) && File.Exists(fallback))
                    return File.ReadAllBytes(fallback);

                return null;
            }
            catch
            {
                return null;
            }
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string baseName;

            if (_fontFamilyMap.TryGetValue(familyName, out var mapped))
            {
                baseName = mapped;
            }
            else
            {
                baseName = familyName.ToLowerInvariant().Replace(" ", "");
            }

            // Build variant name
            string suffix = "";
            if (isBold && isItalic) suffix = "bi";
            else if (isBold) suffix = "b";
            else if (isItalic) suffix = "i";

            string faceName = baseName + suffix;
            if (_fontFileCache.ContainsKey(faceName))
                return new FontResolverInfo(faceName);

            // Bold variant sometimes uses "bd"
            if (isBold && !isItalic)
            {
                faceName = baseName + "bd";
                if (_fontFileCache.ContainsKey(faceName))
                    return new FontResolverInfo(faceName);
            }

            // Fallback to base
            if (_fontFileCache.ContainsKey(baseName))
                return new FontResolverInfo(baseName);

            // Ultimate fallback: malgun gothic
            if (_fontFileCache.ContainsKey("malgun"))
                return new FontResolverInfo("malgun");

            return null;
        }
    }
}
