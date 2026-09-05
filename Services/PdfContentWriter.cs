using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Geom;
using iText.Kernel.Colors;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Pdf;
using PDF_simple_edit.Helpers;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services
{
    internal sealed class PdfContentWriter
    {
        private readonly record struct TextFontSegment(
            string Text,
            PdfFont Font,
            bool IsFallback);

        public void AddText(PdfDocument doc, int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, Color color,
            bool isBold = false, bool isItalic = false, double lineHeight = 0,
            double baselineOffset = 0, int originalFontObjectNumber = -1,
            IReadOnlyList<int>? originalFontObjectNumbersByLine = null,
            IReadOnlyList<double>? lineXOffsets = null,
            IReadOnlyList<double>? lineBaselineOffsets = null,
            int characterSpacing = 0,
            int fontWeight = 400,
            IReadOnlyList<string>? originalLines = null)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;

            var page = doc.GetPage(pageIndex + 1);
            var rect = page.GetCropBox(); 

            float firstLineXOffset = lineXOffsets != null && lineXOffsets.Count > 0
                ? (float)lineXOffsets[0]
                : 0;
            float pdfX = rect.GetLeft() + (float)x + firstLineXOffset;
            string[] lines = (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            var originalFontCache = new Dictionary<int, PdfFont>();
            var embeddedProgramFontCache = new Dictionary<int, PdfFont>();
            var selectedFamilyFonts = new Dictionary<string, PdfFont>(StringComparer.Ordinal);
            // Cache by writing system. Korean and Japanese are both non-Latin,
            // but they must not share the same fallback font.
            var fallbackFonts = new Dictionary<string, PdfFont>(StringComparer.Ordinal);
            int resolvedFontWeight = ResolveFontWeight(fontWeight, isBold);

            List<PdfFont> ResolveOriginalFonts(int lineIndex)
            {
                var candidateObjectNumbers = new List<int>();
                if (originalFontObjectNumbersByLine != null &&
                    lineIndex < originalFontObjectNumbersByLine.Count)
                {
                    candidateObjectNumbers.Add(originalFontObjectNumbersByLine[lineIndex]);
                }
                candidateObjectNumbers.Add(originalFontObjectNumber);
                if (originalFontObjectNumbersByLine != null)
                    candidateObjectNumbers.AddRange(originalFontObjectNumbersByLine);

                var fonts = new List<PdfFont>();
                foreach (int objectNumber in candidateObjectNumbers.Where(number => number > 0).Distinct())
                {
                    try
                    {
                        if (!originalFontCache.TryGetValue(objectNumber, out var candidate))
                        {
                            if (doc.GetPdfObject(objectNumber) is not PdfDictionary originalFontDictionary)
                                continue;
                            candidate = PdfFontFactory.CreateFont(originalFontDictionary);
                            originalFontCache[objectNumber] = candidate;
                        }
                        fonts.Add(candidate);

                        // A PDF font dictionary can restrict its encoding even when
                        // the embedded font program contains more glyphs. Recreate a
                        // Unicode font from that same program so newly typed text can
                        // still use the exact original face whenever possible.
                        if (!embeddedProgramFontCache.TryGetValue(objectNumber, out PdfFont? programFont) &&
                            PdfFontMetadataResolver.TryGetEmbeddedFontBytes(candidate, out byte[] fontBytes))
                        {
                            try
                            {
                                FontProgram fontProgram = FontProgramFactory.CreateFont(fontBytes);
                                programFont = PdfFontFactory.CreateFont(
                                    fontProgram,
                                    PdfEncodings.IDENTITY_H,
                                    PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                                embeddedProgramFontCache[objectNumber] = programFont;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"Embedded original font program reuse error: {ex.Message}");
                            }
                        }

                        if (programFont != null && !ReferenceEquals(candidate, programFont))
                            fonts.Add(programFont);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Original PDF font reuse error: {ex.Message}");
                    }
                }
                return fonts;
            }

            PdfFont? ResolveSelectedFamilyFont(string sampleText)
            {
                string cacheKey = GetFallbackScript(sampleText);
                if (selectedFamilyFonts.TryGetValue(cacheKey, out PdfFont? cachedFont))
                    return FontSupportsText(cachedFont, sampleText) ? cachedFont : null;

                try
                {
                    string? resolvedPath = GetSystemFontPath(
                        fontFamily,
                        resolvedFontWeight,
                        sampleText,
                        isItalic);
                    if (string.IsNullOrEmpty(resolvedPath))
                        return null;

                    PdfFont candidate = PdfFontFactory.CreateFont(
                        resolvedPath,
                        PdfEncodings.IDENTITY_H,
                        PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                    if (!FontSupportsText(candidate, sampleText))
                        return null;

                    selectedFamilyFonts[cacheKey] = candidate;
                    return candidate;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Selected font load error: {ex.Message}");
                    return null;
                }
            }

            PdfFont ResolveFallbackFont(string sampleText)
            {
                string fallbackKey = GetFallbackScript(sampleText);
                if (fallbackFonts.TryGetValue(fallbackKey, out PdfFont? cachedFont))
                    return cachedFont;

                PdfFont? resolvedFallback = ResolveSelectedFamilyFont(sampleText);

                if (resolvedFallback == null)
                {
                    string[] fallbackFamilies = fallbackKey switch
                    {
                        "Korean" => new[] { "Noto Sans JP", "Noto Sans KR", "맑은 고딕" },
                        "Japanese" => new[] { "Noto Sans JP", "Meiryo" },
                        _ => new[] { "맑은 고딕" }
                    };

                    foreach (string fallbackFamily in fallbackFamilies)
                    {
                        try
                        {
                            string? fallbackPath = GetSystemFontPath(
                                fallbackFamily,
                                resolvedFontWeight,
                                sampleText,
                                isItalic);
                            if (string.IsNullOrEmpty(fallbackPath))
                                continue;

                            PdfFont fallbackCandidate = PdfFontFactory.CreateFont(
                                fallbackPath,
                                PdfEncodings.IDENTITY_H,
                                PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                            if (FontSupportsText(fallbackCandidate, sampleText))
                            {
                                resolvedFallback = fallbackCandidate;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"{fallbackFamily} fallback font load error: {ex.Message}");
                        }
                    }
                }

                resolvedFallback ??= PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
                fallbackFonts[fallbackKey] = resolvedFallback;
                return resolvedFallback;
            }

            static string GetFallbackScript(string? value)
            {
                if (!string.IsNullOrEmpty(value) && value.Any(IsKoreanCharacter))
                    return "Korean";
                if (!string.IsNullOrEmpty(value) && value.Any(IsJapaneseCharacter))
                    return "Japanese";
                return "Other";
            }

            static bool IsKoreanCharacter(char character) =>
                character is >= '\u1100' and <= '\u11FF' ||
                character is >= '\u3130' and <= '\u318F' ||
                character is >= '\uA960' and <= '\uA97F' ||
                character is >= '\uAC00' and <= '\uD7FF' ||
                character is >= '\uD7B0' and <= '\uD7FF';

            static bool IsJapaneseCharacter(char character) =>
                character is >= '\u3000' and <= '\u30FF' ||
                character is >= '\u31F0' and <= '\u31FF' ||
                character is >= '\u3400' and <= '\u4DBF' ||
                character is >= '\u4E00' and <= '\u9FFF' ||
                character is >= '\uF900' and <= '\uFAFF' ||
                character is >= '\uFF65' and <= '\uFF9F';

            static bool FontSupportsText(PdfFont font, string? value)
            {
                if (string.IsNullOrEmpty(value))
                    return true;

                foreach (char character in value)
                {
                    if (char.IsControl(character) || char.IsWhiteSpace(character))
                        continue;
                    try
                    {
                        if (!font.ContainsGlyph(character))
                            return false;
                    }
                    catch
                    {
                        return false;
                    }
                }

                return true;
            }

            static float GetMissingSpaceAdvance(PdfFont font, float resolvedFontSize)
            {
                try
                {
                    float encodedSpaceWidth = font.GetWidth(" ", resolvedFontSize);
                    float expectedSpaceWidth = resolvedFontSize * 0.25f;
                    return encodedSpaceWidth < expectedSpaceWidth * 0.2f
                        ? expectedSpaceWidth - Math.Max(encodedSpaceWidth, 0)
                        : 0;
                }
                catch
                {
                    return resolvedFontSize * 0.25f;
                }
            }

            static void ShowTextPreservingSpaces(
                PdfCanvas targetCanvas,
                TextFontSegment segment,
                float resolvedFontSize,
                float missingSpaceAdvance)
            {
                if (missingSpaceAdvance <= 0.001f || !segment.Text.Contains(' '))
                {
                    targetCanvas.ShowText(segment.Text);
                    return;
                }

                var adjustedText = new PdfArray();
                int runStart = 0;
                for (int index = 0; index < segment.Text.Length; index++)
                {
                    if (segment.Text[index] != ' ')
                        continue;

                    if (index > runStart)
                    {
                        adjustedText.Add(new PdfString(
                            segment.Font.ConvertToBytes(segment.Text[runStart..index])));
                    }

                    // Keep an actual U+0020 in the content for searching and text
                    // extraction, then add the advance that the zero-width subset
                    // font omitted. TJ values are subtracted in 1/1000 text units.
                    adjustedText.Add(new PdfString(
                        segment.Font.ConvertToBytes(" ")));
                    adjustedText.Add(new PdfNumber(
                        -missingSpaceAdvance * 1000f / resolvedFontSize));
                    runStart = index + 1;
                }

                if (runStart < segment.Text.Length)
                {
                    adjustedText.Add(new PdfString(
                        segment.Font.ConvertToBytes(segment.Text[runStart..])));
                }

                targetCanvas.ShowText(adjustedText);
            }

            static bool OriginalFontSupportsEditedLine(
                PdfFont font,
                string editedLine,
                string? originalLine)
            {
                foreach (char character in editedLine)
                {
                    if (char.IsControl(character) || char.IsWhiteSpace(character))
                        continue;

                    if (originalLine != null)
                    {
                        // A source PDF font dictionary is often subset-encoded.
                        // ContainsGlyph can still report true for a newly typed
                        // punctuation mark even though ConvertToBytes cannot emit
                        // it. Use the original wrapper for a whole edited line only
                        // when every character was already encoded by the source.
                        if (originalLine.IndexOf(character) >= 0)
                            continue;

                        return false;
                    }

                    try
                    {
                        if (!font.ContainsGlyph(character))
                            return false;
                    }
                    catch
                    {
                        return false;
                    }
                }

                return true;
            }

            HashSet<int>? ResolveMatchedTargetCharacters(string? originalLine, string targetLine)
            {
                if (originalLine == null)
                    return null;
                if (originalLine.Length == 0 || targetLine.Length == 0)
                    return new HashSet<int>();

                var lengths = new int[originalLine.Length + 1, targetLine.Length + 1];
                for (int originalIndex = 1; originalIndex <= originalLine.Length; originalIndex++)
                {
                    for (int targetIndex = 1; targetIndex <= targetLine.Length; targetIndex++)
                    {
                        lengths[originalIndex, targetIndex] = originalLine[originalIndex - 1] == targetLine[targetIndex - 1]
                            ? lengths[originalIndex - 1, targetIndex - 1] + 1
                            : Math.Max(
                                lengths[originalIndex - 1, targetIndex],
                                lengths[originalIndex, targetIndex - 1]);
                    }
                }

                var matchedTargetIndexes = new HashSet<int>();
                int currentOriginalIndex = originalLine.Length;
                int currentTargetIndex = targetLine.Length;
                while (currentOriginalIndex > 0 && currentTargetIndex > 0)
                {
                    if (originalLine[currentOriginalIndex - 1] == targetLine[currentTargetIndex - 1])
                    {
                        matchedTargetIndexes.Add(currentTargetIndex - 1);
                        currentOriginalIndex--;
                        currentTargetIndex--;
                    }
                    else if (lengths[currentOriginalIndex - 1, currentTargetIndex] >=
                        lengths[currentOriginalIndex, currentTargetIndex - 1])
                    {
                        currentOriginalIndex--;
                    }
                    else
                    {
                        currentTargetIndex--;
                    }
                }

                return matchedTargetIndexes;
            }

            List<TextFontSegment> ResolveLineSegments(string line, int lineIndex)
            {
                List<PdfFont> originalFonts = ResolveOriginalFonts(lineIndex);
                PdfFont? preferredOriginalFont = originalFonts.FirstOrDefault();
                string? originalLine = originalLines != null && lineIndex < originalLines.Count
                    ? originalLines[lineIndex]
                    : null;
                HashSet<int>? matchedTargetCharacters =
                    ResolveMatchedTargetCharacters(originalLine, line);
                if (line.Length == 0)
                {
                    PdfFont emptyLineFont = preferredOriginalFont ?? ResolveFallbackFont(line);
                    return new List<TextFontSegment>
                    {
                        new(string.Empty, emptyLineFont, preferredOriginalFont == null)
                    };
                }

                PdfFont? exactOriginalLineFont = originalFonts.FirstOrDefault(font =>
                    OriginalFontSupportsEditedLine(font, line, originalLine));
                if (exactOriginalLineFont != null)
                {
                    return new List<TextFontSegment>
                    {
                        new(line, exactOriginalLineFont, false)
                    };
                }

                // If an embedded subset cannot encode a genuinely new character,
                // use the installed copy of that same family for the whole line.
                // This avoids mixing the source face and a fallback face inside one
                // edited word while retaining the original font family and weight.
                PdfFont? selectedFamilyFont = ResolveSelectedFamilyFont(line);
                if (selectedFamilyFont != null)
                {
                    return new List<TextFontSegment>
                    {
                        new(line, selectedFamilyFont, false)
                    };
                }

                var segments = new List<TextFontSegment>();
                var segmentText = new StringBuilder();
                PdfFont? segmentFont = null;
                bool segmentIsFallback = false;

                void FlushSegment()
                {
                    if (segmentFont == null || segmentText.Length == 0)
                        return;
                    segments.Add(new TextFontSegment(
                        segmentText.ToString(),
                        segmentFont,
                        segmentIsFallback));
                    segmentText.Clear();
                }

                for (int characterIndex = 0; characterIndex < line.Length; characterIndex++)
                {
                    char character = line[characterIndex];
                    PdfFont? selectedFont = null;
                    bool isFallback = false;
                    if (char.IsControl(character) || char.IsWhiteSpace(character))
                    {
                        selectedFont = segmentFont ?? preferredOriginalFont;
                        isFallback = segmentIsFallback;
                    }
                    else
                    {
                        if (matchedTargetCharacters?.Contains(characterIndex) == true)
                        {
                            // Keep characters that survived the edit on the exact
                            // embedded font used by the original PDF. This is more
                            // reliable than ContainsGlyph for subset Type0 fonts,
                            // whose Unicode cmap can report false for valid glyphs.
                            selectedFont = preferredOriginalFont;
                        }
                        else if (originalLine?.IndexOf(character) >= 0)
                        {
                            // Repeated/inserted occurrences of a glyph already used
                            // by the source line are also safe in a subset font.
                            selectedFont = preferredOriginalFont;
                        }
                        else if (matchedTargetCharacters == null)
                        {
                            foreach (PdfFont originalFont in originalFonts)
                            {
                                try
                                {
                                    if (originalFont.ContainsGlyph(character))
                                    {
                                        selectedFont = originalFont;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"Original PDF glyph lookup error: {ex.Message}");
                                }
                            }
                        }

                        if (selectedFont == null)
                        {
                            selectedFont = ResolveFallbackFont(character.ToString());
                            isFallback = true;
                        }
                    }

                    selectedFont ??= ResolveFallbackFont(character.ToString());
                    if (segmentFont != null &&
                        (!ReferenceEquals(segmentFont, selectedFont) ||
                         segmentIsFallback != isFallback))
                    {
                        FlushSegment();
                    }

                    segmentFont = selectedFont;
                    segmentIsFallback = isFallback;
                    segmentText.Append(character);
                }

                FlushSegment();
                return segments;
            }

            var resolvedLineSegments = lines
                .Select((line, index) => ResolveLineSegments(line, index))
                .ToList();
            var fallbackBaselineAdjustments = resolvedLineSegments
                .Select((segments, index) => segments.Count == 1 && segments[0].IsFallback
                    ? Math.Max(segments[0].Font.GetAscent(
                        lines[index],
                        (float)Math.Max(fontSize, 1)), 0)
                    : 0)
                .ToList();

            bool hasOriginalLineBaselines =
                lineBaselineOffsets != null && lineBaselineOffsets.Count > 0;
            float resolvedBaselineOffset = lineBaselineOffsets != null && lineBaselineOffsets.Count > 0
                ? (float)lineBaselineOffsets[0]
                : baselineOffset > 0.1
                    ? (float)baselineOffset
                    : Math.Max(
                        resolvedLineSegments[0][0].Font.GetAscent(
                            lines[0],
                            (float)Math.Max(fontSize, 1)),
                        0);
            // A baseline measured from the on-screen WinUI text box must be used
            // as-is. The fallback adjustment only applies to baselines extracted
            // from original PDF text fragments.
            float firstLineFallbackAdjustment = hasOriginalLineBaselines
                ? (float)fallbackBaselineAdjustments[0]
                : 0;
            float pdfY = rect.GetBottom() +
                (rect.GetHeight() - (float)y - resolvedBaselineOffset);

            // 3. 로우레벨(Low-level) API로 확실하게 텍스트 박아넣기
            // 기존 콘텐츠 스트림이 q/Q 없이 좌표 변환을 남긴 PDF도 있습니다.
            // wrapOldContent=true로 기존 그래픽 상태를 격리해야 새 글자의 위치,
            // 크기와 방향이 화면 좌표 그대로 유지됩니다.
            PdfCanvas canvas = new PdfCanvas(page, true);
            
            canvas.SaveState();
            canvas.BeginText();
            canvas.SetFillColor(color ?? ColorConstants.BLACK);

            // 텍스트 이동 및 쓰기. 줄바꿈은 기존 편집 구역의 줄 간격을 유지합니다.
            canvas.MoveText(pdfX, pdfY - firstLineFallbackAdjustment);
            float resolvedLineHeight = (float)(lineHeight > 0.1 ? lineHeight : Math.Max(fontSize * 1.2, 1));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    float previousXOffset = lineXOffsets != null && i - 1 < lineXOffsets.Count
                        ? (float)lineXOffsets[i - 1]
                        : 0;
                    float currentXOffset = lineXOffsets != null && i < lineXOffsets.Count
                        ? (float)lineXOffsets[i]
                        : previousXOffset;
                    float previousBaselineOffset = lineBaselineOffsets != null && i - 1 < lineBaselineOffsets.Count
                        ? (float)lineBaselineOffsets[i - 1]
                        : resolvedBaselineOffset + ((i - 1) * resolvedLineHeight);
                    float currentBaselineOffset = lineBaselineOffsets != null && i < lineBaselineOffsets.Count
                        ? (float)lineBaselineOffsets[i]
                        : previousBaselineOffset + resolvedLineHeight;
                    if (hasOriginalLineBaselines)
                    {
                        previousBaselineOffset += fallbackBaselineAdjustments[i - 1];
                        currentBaselineOffset += fallbackBaselineAdjustments[i];
                    }
                    canvas.MoveText(
                        currentXOffset - previousXOffset,
                        -(currentBaselineOffset - previousBaselineOffset));
                }
                float resolvedFontSize = (float)Math.Max(fontSize, 1);
                var missingSpaceAdvances = resolvedLineSegments[i]
                    .Select(segment => GetMissingSpaceAdvance(segment.Font, resolvedFontSize))
                    .ToList();
                // Use explicit tracking in the same 1/1000 em units as WinUI.
                // Never derive Tc from a target box width: font substitution or
                // a longer edit otherwise distributes a negative advance into
                // every glyph and can reverse the pen for narrow characters.
                canvas.SetCharacterSpacing(Math.Max(characterSpacing, 0) * resolvedFontSize / 1000f);
                for (int segmentIndex = 0;
                    segmentIndex < resolvedLineSegments[i].Count;
                    segmentIndex++)
                {
                    TextFontSegment segment = resolvedLineSegments[i][segmentIndex];
                    canvas.SetFontAndSize(
                        segment.Font,
                        resolvedFontSize);
                    ShowTextPreservingSpaces(
                        canvas,
                        segment,
                        resolvedFontSize,
                        missingSpaceAdvances[segmentIndex]);
                }
            }

            canvas.EndText();
            canvas.RestoreState();
            canvas.Release();
        }

        private string? GetSystemFontPath(
            string nameOrFile,
            int fontWeight = 400,
            string? sampleText = null,
            bool isItalic = false)
        {
            if (string.IsNullOrWhiteSpace(nameOrFile)) return null;

            string fontDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            string userFontDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Windows",
                "Fonts");

            string SelectFace(
                string regular,
                string bold,
                string? light = null,
                string? extraBold = null)
            {
                if (fontWeight >= 800 && !string.IsNullOrEmpty(extraBold))
                    return extraBold;
                if (fontWeight >= 600)
                    return bold;
                if (fontWeight <= 350 && !string.IsNullOrEmpty(light))
                    return light;
                return regular;
            }

            string SelectNotoKrFace(string stem, string variableFile)
            {
                var faces = new (string Suffix, int Weight)[]
                {
                    ("Thin", 100),
                    ("ExtraLight", 200),
                    ("Light", 300),
                    ("Regular", 400),
                    ("Medium", 500),
                    ("SemiBold", 600),
                    ("Bold", 700),
                    ("ExtraBold", 800),
                    ("Black", 900)
                };

                // Only use files from the selected KR family. The previous list
                // silently substituted Noto Sans, Nanum Gothic, or Nanum Myeongjo
                // depending on the text and weight, so the saved PDF no longer
                // matched the Noto Sans/Serif KR preview in the editor.
                IEnumerable<string> staticFaces = faces
                    .OrderBy(face => Math.Abs(fontWeight - face.Weight))
                    .ThenBy(face => face.Weight == 400 ? 0 : 1)
                    .Select(face => $"{stem}-{face.Suffix}.ttf");
                return string.Join('|', staticFaces.Append(variableFile));
            }

            string notoSansKr = SelectNotoKrFace("NotoSansKR", "NotoSansKR-VF.ttf");
            string notoSerifKr = SelectNotoKrFace("NotoSerifKR", "NotoSerifKR-VF.ttf");
            string notoSansCjkKrRegular =
                $"NotoSansCJKkr-Regular.otf|NotoSansCJKkr-Regular.ttf|{notoSansKr}";
            string notoSansCjkKrBold =
                $"NotoSansCJKkr-Bold.otf|NotoSansCJKkr-Bold.ttf|{notoSansKr}";
            string notoSansCjkKrExtraBold =
                $"NotoSansCJKkr-Black.otf|NotoSansCJKkr-Black.ttf|{notoSansKr}";
            string notoSerifCjkKrRegular =
                $"NotoSerifCJKkr-Regular.otf|NotoSerifCJKkr-Regular.ttf|{notoSerifKr}";
            string notoSerifCjkKrBold =
                $"NotoSerifCJKkr-Bold.otf|NotoSerifCJKkr-Bold.ttf|{notoSerifKr}";
            string notoSerifCjkKrExtraBold =
                $"NotoSerifCJKkr-Black.otf|NotoSerifCJKkr-Black.ttf|{notoSerifKr}";
            string notoSansCjkJp =
                "NotoSansCJKjp-VF.ttf|NotoSansCJKjp-Regular.otf|NotoSansCJKjp-Regular.ttf";
            string notoSerifCjkJp =
                "NotoSerifCJKjp-VF.ttf|NotoSerifCJKjp-Regular.otf|NotoSerifCJKjp-Regular.ttf";

            // 핵심: 굴림/돋움은 gulim.ttc에, 바탕/궁서는 batang.ttc에 묶여 있습니다. 인덱스를 지정해야 합니다.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "맑은 고딕", SelectFace("malgun.ttf", "malgunbd.ttf", "malgunsl.ttf") },
                { "Malgun Gothic", SelectFace("malgun.ttf", "malgunbd.ttf", "malgunsl.ttf") },
                { "굴림", "gulim.ttc,0" },
                { "굴림체", "gulim.ttc,1" },
                { "돋움", "gulim.ttc,2" },
                { "돋움체", "gulim.ttc,3" },
                { "바탕", "batang.ttc,0" },
                { "바탕체", "batang.ttc,1" },
                { "궁서", "batang.ttc,2" },
                { "궁서체", "batang.ttc,3" },
                { "나눔고딕", SelectFace("NanumGothic.ttf", "NanumGothicBold.ttf", extraBold: "NanumGothicExtraBold.ttf") },
                { "Noto Sans KR", notoSansKr },
                { "Noto Serif KR", notoSerifKr },
                { "Noto Sans CJK KR", fontWeight >= 800
                    ? notoSansCjkKrExtraBold
                    : fontWeight >= 600
                        ? notoSansCjkKrBold
                        : notoSansCjkKrRegular },
                { "Noto Serif CJK KR", fontWeight >= 800
                    ? notoSerifCjkKrExtraBold
                    : fontWeight >= 600
                        ? notoSerifCjkKrBold
                        : notoSerifCjkKrRegular },
                { "Noto Sans CJK JP", notoSansCjkJp },
                { "Noto Serif CJK JP", notoSerifCjkJp },
                { "Noto Sans", SelectFace("NotoSans-Regular.ttf", "NotoSans-Bold.ttf") },
                { "Noto Serif", SelectFace("NotoSerif-Regular.ttf", "NotoSerif-Bold.ttf") },
                { "Noto Sans JP", "NotoSansJP-VF.ttf" },
                { "Noto Serif JP", "NotoSerifJP-VF.ttf" },
                { "Yu Gothic UI", SelectFace("YuGothR.ttc,0", "YuGothB.ttc,0", "YuGothL.ttc,0") },
                { "Yu Gothic", SelectFace("YuGothR.ttc,0", "YuGothB.ttc,0", "YuGothL.ttc,0") },
                { "Meiryo", SelectFace("meiryo.ttc,0", "meiryob.ttc,0") },
                { "Meiryo UI", SelectFace("meiryo.ttc,0", "meiryob.ttc,0") },
                { "Arial", SelectFace("arial.ttf", "arialbd.ttf") },
                { "Times New Roman", SelectFace("times.ttf", "timesbd.ttf") },
                { "Segoe UI", SelectFace("segoeui.ttf", "segoeuib.ttf", "segoeuil.ttf") },
                { "Tahoma", SelectFace("tahoma.ttf", "tahomabd.ttf") },
                { "Verdana", SelectFace("verdana.ttf", "verdanab.ttf") },
                { "Consolas", SelectFace("consola.ttf", "consolab.ttf") },
                { "Courier New", SelectFace("cour.ttf", "courbd.ttf") }
            };

            if (map.TryGetValue(nameOrFile, out string? mappedValue))
            {
                foreach (string mappedCandidate in mappedValue.Split('|'))
                {
                    string[] parts = mappedCandidate.Split(',');
                    foreach (string directory in new[] { fontDir, userFontDir })
                    {
                        string path = System.IO.Path.Combine(directory, parts[0]);
                        if (File.Exists(path))
                        {
                            // 파일이 존재하면 경로 뒤에 인덱스(,0 ,1 ,2 등)를 붙여서 반환
                            return parts.Length > 1 ? $"{path},{parts[1]}" : path;
                        }
                    }
                }
            }

            string? registeredFontPath = InstalledFontService.FindFontFile(
                nameOrFile,
                fontWeight,
                isItalic);
            if (!string.IsNullOrEmpty(registeredFontPath))
                return registeredFontPath;

            // 파일명 자체가 들어온 경우 처리
            if (nameOrFile.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || 
                nameOrFile.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                string path = System.IO.Path.Combine(fontDir, nameOrFile);
                if (File.Exists(path)) return path;
                string userPath = System.IO.Path.Combine(userFontDir, nameOrFile);
                if (File.Exists(userPath)) return userPath;
            }

            // 3. 매핑에 없으면 폰트 이름의 공백을 제거하고 유추 시도
            string guessName = nameOrFile.Replace(" ", "") + (fontWeight >= 600 ? "bd.ttf" : ".ttf");
            string guessPath = System.IO.Path.Combine(fontDir, guessName);
            if (File.Exists(guessPath)) return guessPath;

            return null; // 그래도 없으면 null 반환
        }

        private static int ResolveFontWeight(int fontWeight, bool isBold)
        {
            int resolvedWeight = fontWeight is >= 1 and <= 999 ? fontWeight : 400;
            if (isBold && resolvedWeight < 600)
                resolvedWeight = 700;
            return resolvedWeight;
        }

        public void AddHighlight(PdfDocument doc, int pageIndex, double x, double y, double width, double height, 
            Color? color = null, float opacity = 0.5f)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;

            var page = doc.GetPage(pageIndex + 1);
            var canvas = new PdfCanvas(page, true);
            
            canvas.SaveState();
            
            var gs = new PdfExtGState().SetFillOpacity(opacity);
            canvas.SetExtGState(gs);
            canvas.SetFillColor(color ?? ColorConstants.YELLOW);
            
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y) || 
                width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
                return;

            // 핵심: Y축 변환
            var pageSize = page.GetPageSize();
            float pdfY = pageSize.GetHeight() - (float)y - (float)height;

            canvas.Rectangle((float)x, pdfY, (float)width, (float)height); // 변환된 pdfY 사용
            canvas.Fill();
            
            canvas.RestoreState();
            canvas.Release();
        }

        public void AddSignature(
            PdfDocument doc,
            int pageIndex,
            IReadOnlyList<PdfPathPoint> points,
            Color color,
            double lineWidth,
            string? metadata = null)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages() || points.Count < 2)
                return;

            PdfPage page = doc.GetPage(pageIndex + 1);
            Rectangle pageSize = page.GetPageSize();

            var validPoints = points
                .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
                .ToList();
            if (validPoints.Count < 2)
                return;

            float minX = (float)validPoints.Min(point => point.X);
            float maxX = (float)validPoints.Max(point => point.X);
            float minY = (float)validPoints.Min(point => point.Y);
            float maxY = (float)validPoints.Max(point => point.Y);
            float pdfBottom = pageSize.GetHeight() - maxY;
            var rectangle = new Rectangle(
                minX,
                pdfBottom,
                Math.Max(maxX - minX, 1),
                Math.Max(maxY - minY, 1));
            var inkPath = new PdfArray();
            foreach (PdfPathPoint point in validPoints)
            {
                inkPath.Add(new PdfNumber((float)point.X));
                inkPath.Add(new PdfNumber(pageSize.GetHeight() - (float)point.Y));
            }

            var inkList = new PdfArray();
            inkList.Add(inkPath);
            var inkAnnotation = new iText.Kernel.Pdf.Annot.PdfInkAnnotation(rectangle, inkList);
            inkAnnotation.SetColor(color ?? ColorConstants.BLACK);
            var borderStyle = new PdfDictionary();
            borderStyle.Put(
                PdfName.W,
                new PdfNumber((float)Math.Clamp(lineWidth, 0.1, 100)));
            inkAnnotation.SetBorderStyle(borderStyle);
            if (!string.IsNullOrEmpty(metadata))
                inkAnnotation.SetContents(metadata);
            page.AddAnnotation(inkAnnotation);
        }

        public void AddImage(PdfDocument doc, int pageIndex, string imagePath, double x, double y,
            double width, double height)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;

            var page = doc.GetPage(pageIndex + 1);
            var pageSize = page.GetPageSize();

            // UI 좌표(Top-Down)를 PDF 좌표(Bottom-Up)로 변환
            float pdfY = pageSize.GetHeight() - (float)y - (float)height;

            ImageData data = ImageDataFactory.Create(imagePath);
            iText.Layout.Element.Image img = new iText.Layout.Element.Image(data);
            
            // Use iText.Layout.Canvas for easy positioning
            using var canvasLayout = new iText.Layout.Canvas(new PdfCanvas(page, true), pageSize);
            img.SetFixedPosition((float)x, pdfY, (float)width);
            if (height > 0) img.SetHeight((float)height);
            canvasLayout.Add(img);
        }

    }
}
