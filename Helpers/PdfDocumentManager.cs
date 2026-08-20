using iText.Kernel.Pdf;
using iText.PdfCleanup;

using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Geom;
using iText.Kernel.Colors;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Utils;
using iText.IO.Source;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PDF_simple_edit.Models;
using System.Globalization;
using System.Text;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using iText.Kernel.Pdf.Xobject;
using System.Security.Cryptography;

namespace PDF_simple_edit.Helpers
{
    public class PdfDocumentManager
    {
        private readonly record struct TextFontSegment(
            string Text,
            PdfFont Font,
            bool IsFallback);

        private byte[]? _pdfBytes;
        private string? _filePath;
        private bool _isModified;
        private readonly object _docLock = new();
        private readonly Dictionary<int, List<PdfPageContent>> _contentCache = new();

        private readonly Stack<(byte[] Bytes, object? UIState)> _undoStack = new();
        private readonly Stack<(byte[] Bytes, object? UIState)> _redoStack = new();
        private const int MaxUndoSteps = 30;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string? FilePath => _filePath;
        public void SetFilePath(string path) => _filePath = path;
        
        public bool IsModified => _isModified;
        public void MarkModified(bool modified = true)
        {
            _isModified = modified;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public int PageCount
        {
            get
            {
                if (_pdfBytes == null) return 0;
                try
                {
                    using var reader = new PdfReader(new MemoryStream(_pdfBytes));
                    using var doc = new PdfDocument(reader);
                    return doc.GetNumberOfPages();
                }
                catch { return 0; }
            }
        }

        public bool IsLoaded => _pdfBytes != null;
        public byte[]? GetPdfBytes() => _pdfBytes;

        public event EventHandler? DocumentChanged;
        public event EventHandler? PageStructureChanged;
        public event EventHandler? ModifiedStateChanged;
        public event EventHandler<object?>? UndoRedoPerformed;

        public Func<object?>? GetUIStateFunc { get; set; }

        public PdfDocumentManager()
        {
        }

        public async Task<bool> OpenAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        if (!File.Exists(filePath)) return false;
                        _pdfBytes = File.ReadAllBytes(filePath);
                        _filePath = filePath;
                        _isModified = false;
                        _contentCache.Clear();
                        _undoStack.Clear();
                        _redoStack.Clear();
                        
                        DocumentChanged?.Invoke(this, EventArgs.Empty);
                        PageStructureChanged?.Invoke(this, EventArgs.Empty);
                        ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error opening PDF: {ex.Message}");
                        return false;
                    }
                }
            });
        }

        public async Task<bool> SaveAsync()
        {
            if (_pdfBytes == null || string.IsNullOrEmpty(_filePath))
                return false;
            return await SaveAsAsync(_filePath, true);
        }

        public async Task<bool> SaveAsAsync(string filePath, bool isUserSave = true)
        {
            if (_pdfBytes == null) return false;

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        File.WriteAllBytes(filePath, _pdfBytes);
                        
                        if (isUserSave)
                        {
                            _filePath = filePath;
                            _isModified = false;
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error saving PDF: {ex.Message}");
                        return false;
                    }
                }
            });
        }

        public void ApplyBatchEdit(Action<PdfDocument> editAction)
        {
            ApplyEdit(editAction);
        }

        private void ApplyEdit(Action<PdfDocument> editAction)
        {
            if (_pdfBytes == null) return;

            lock (_docLock)
            {
                // Save current state to undo stack before mutation
                _undoStack.Push(((byte[])_pdfBytes.Clone(), GetUIStateFunc?.Invoke()));
                if (_undoStack.Count > MaxUndoSteps)
                {
                    // Remove oldest (inefficient with Stack, but infrequent)
                    var list = _undoStack.ToList();
                    _undoStack.Clear();
                    for (int i = Math.Min(list.Count - 1, MaxUndoSteps - 1); i >= 0; i--)
                        _undoStack.Push(list[i]);
                }
                _redoStack.Clear();

                try
                {
                    using (var msInput = new MemoryStream(_pdfBytes))
                    using (var msOutput = new MemoryStream())
                    {
                        var reader = new PdfReader(msInput);
                        
                        // iText 9 에러 방지: WriterProperties 설정 (BouncyCastle 어댑터가 로드된 상태여야 함)
                        WriterProperties props = new WriterProperties();
                        var writer = new PdfWriter(msOutput, props);
                        
                        // Stamping 모드 사용하여 불필요한 객체 복제 및 직렬화 에러 방지
                        using (var doc = new PdfDocument(reader, writer, new StampingProperties()))
                        {
                            editAction(doc);
                        }
                        
                        byte[] resultBytes = msOutput.ToArray();
                        if (resultBytes.Length > 0)
                        {
                            _pdfBytes = resultBytes;
                            _isModified = true;
                            _contentCache.Clear(); 
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                            DocumentChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 에러 메시지에 더 자세한 정보 포함
                    throw new Exception($"PDF 편집 중 에러 발생: {ex.Message}\n타입: {ex.GetType().Name}\n스택: {ex.StackTrace}");
                }
            }
        }

        public byte[] CreatePdfBytesWithEdits(Action<PdfDocument> editAction)
        {
            ArgumentNullException.ThrowIfNull(editAction);
            if (_pdfBytes == null)
                throw new InvalidOperationException("열린 PDF 문서가 없습니다.");

            lock (_docLock)
            {
                using var msInput = new MemoryStream(_pdfBytes);
                using var msOutput = new MemoryStream();

                using (var reader = new PdfReader(msInput))
                using (var writer = new PdfWriter(msOutput))
                using (var doc = new PdfDocument(reader, writer, new StampingProperties()))
                {
                    editAction(doc);
                }

                byte[] result = msOutput.ToArray();
                if (result.Length == 0)
                    throw new InvalidOperationException("편집된 PDF 데이터가 생성되지 않았습니다.");

                return result;
            }
        }

        public byte[]? GetPdfBytesWithEdits(Action<PdfDocument> editAction)
        {
            try
            {
                return CreatePdfBytesWithEdits(editAction);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting PDF bytes with edits: {ex.Message}");
                return null;
            }
        }

        public void ReplacePdfBytesAfterSave(byte[] pdfBytes, string filePath)
        {
            ArgumentNullException.ThrowIfNull(pdfBytes);
            lock (_docLock)
            {
                _pdfBytes = (byte[])pdfBytes.Clone();
                _filePath = filePath;
                _isModified = false;
                _contentCache.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public List<PdfAnnotation> LoadSavedSignatures()
        {
            var signatures = new List<PdfAnnotation>();
            if (_pdfBytes == null)
                return signatures;

            lock (_docLock)
            {
                try
                {
                    using var input = new MemoryStream(_pdfBytes);
                    using var reader = new PdfReader(input);
                    using var document = new PdfDocument(reader);
                    for (int pageIndex = 0; pageIndex < document.GetNumberOfPages(); pageIndex++)
                    {
                        PdfPage page = document.GetPage(pageIndex + 1);
                        foreach (iText.Kernel.Pdf.Annot.PdfAnnotation pdfAnnotation in page.GetAnnotations())
                        {
                            if (!IsSavedSignatureAnnotation(pdfAnnotation))
                                continue;

                            string? contents = pdfAnnotation.GetContents()?.ToUnicodeString();
                            if (PdfSignatureMetadata.TryDeserialize(contents, pageIndex, out PdfAnnotation? signature) &&
                                signature != null)
                            {
                                signatures.Add(signature);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Saved signature load error: {ex.Message}");
                }
            }

            if (signatures.Count > 0)
            {
                try
                {
                    DetachSavedSignatureAnnotationsForEditing();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Saved signature detach error: {ex.Message}");
                }
            }

            return signatures;
        }

        public void RemoveSavedSignatureAnnotations(PdfDocument document)
        {
            for (int pageIndex = 0; pageIndex < document.GetNumberOfPages(); pageIndex++)
            {
                PdfPage page = document.GetPage(pageIndex + 1);
                foreach (iText.Kernel.Pdf.Annot.PdfAnnotation annotation in page.GetAnnotations().ToList())
                {
                    if (IsSavedSignatureAnnotation(annotation))
                        page.RemoveAnnotation(annotation);
                }
            }
        }

        /// <summary>
        /// Removes the application's saved Ink annotations from the in-memory
        /// document while keeping the editor annotations as the UI source of
        /// truth. The file on disk is not changed and the document remains
        /// unmodified, so the annotations are written back exactly once on
        /// the next save.
        /// </summary>
        public void DetachSavedSignatureAnnotationsForEditing()
        {
            if (_pdfBytes == null)
                return;

            byte[] detachedBytes = CreatePdfBytesWithEdits(RemoveSavedSignatureAnnotations);
            lock (_docLock)
            {
                _pdfBytes = detachedBytes;
                _contentCache.Clear();
            }
        }

        private static bool IsSavedSignatureAnnotation(
            iText.Kernel.Pdf.Annot.PdfAnnotation annotation)
        {
            if (!PdfName.Ink.Equals(annotation.GetSubtype()))
                return false;

            string? contents = annotation.GetContents()?.ToUnicodeString();
            return contents?.StartsWith(PdfSignatureMetadata.Prefix, StringComparison.Ordinal) == true;
        }

        public void AddText(int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, Color color,
            bool isBold = false, bool isItalic = false)
        {
            ApplyEdit(doc => AddTextInternal(doc, pageIndex, x, y, text, fontFamily, fontSize, color, isBold, isItalic));
        }

        public void AddTextInternal(PdfDocument doc, int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, Color color,
            bool isBold = false, bool isItalic = false, double lineHeight = 0,
            double baselineOffset = 0, int originalFontObjectNumber = -1,
            IReadOnlyList<int>? originalFontObjectNumbersByLine = null,
            IReadOnlyList<double>? lineXOffsets = null,
            IReadOnlyList<double>? lineBaselineOffsets = null,
            IReadOnlyList<double>? displayLineWidths = null,
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
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Original PDF font reuse error: {ex.Message}");
                    }
                }
                return fonts;
            }

            PdfFont ResolveFallbackFont(string sampleText)
            {
                string fallbackKey = GetFallbackScript(sampleText);
                if (fallbackFonts.TryGetValue(fallbackKey, out PdfFont? cachedFont))
                    return cachedFont;

                PdfFont? resolvedFallback = null;
                try
                {
                    string? resolvedPath = GetSystemFontPath(
                        fontFamily,
                        resolvedFontWeight,
                        sampleText);
                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        PdfFont candidate = PdfFontFactory.CreateFont(
                            resolvedPath,
                            PdfEncodings.IDENTITY_H,
                            PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);

                        // WinUI renders a missing glyph with a script-specific
                        // fallback font. iText does not do this automatically, so
                        // never keep the selected font when it cannot encode the
                        // character being written.
                        if (FontSupportsText(candidate, sampleText))
                            resolvedFallback = candidate;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Font load error: {ex.Message}");
                }

                if (resolvedFallback == null)
                {
                    string[] fallbackFamilies = fallbackKey switch
                    {
                        "Korean" => new[] { "맑은 고딕", "Noto Sans KR" },
                        "Japanese" => new[] { "Yu Gothic UI", "Meiryo", "Noto Sans JP" },
                        _ => new[] { "맑은 고딕" }
                    };

                    foreach (string fallbackFamily in fallbackFamilies)
                    {
                        try
                        {
                            string? fallbackPath = GetSystemFontPath(
                                fallbackFamily,
                                resolvedFontWeight,
                                sampleText);
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
                float characterSpacing = 0;
                if (displayLineWidths != null &&
                    i < displayLineWidths.Count &&
                    displayLineWidths[i] > 0.1 &&
                    lines[i].Length > 0)
                {
                    float measuredLineWidth = resolvedLineSegments[i]
                        .Sum(segment => segment.Font.GetWidth(
                            segment.Text,
                            (float)Math.Max(fontSize, 1)));
                    characterSpacing = (float)(
                        (displayLineWidths[i] - measuredLineWidth) /
                        Math.Max(lines[i].Length, 1));
                }
                canvas.SetCharacterSpacing(characterSpacing);
                foreach (TextFontSegment segment in resolvedLineSegments[i])
                {
                    canvas.SetFontAndSize(
                        segment.Font,
                        (float)Math.Max(fontSize, 1));
                    canvas.ShowText(segment.Text);
                }
            }

            canvas.EndText();
            canvas.RestoreState();
            canvas.Release();
        }

        private string? GetSystemFontPath(
            string nameOrFile,
            int fontWeight = 400,
            string? sampleText = null)
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

            bool containsNonLatin = sampleText?.Any(character => character > 0x02FF) == true;
            string notoSansKrRegular = containsNonLatin
                ? "NotoSansKR-Regular.ttf|NotoSans-Regular.ttf"
                : "NotoSans-Regular.ttf|NotoSansKR-Regular.ttf";
            string notoSansKrBold = containsNonLatin
                ? "NotoSansKR-Bold.ttf|NanumGothicBold.ttf|NotoSans-Bold.ttf|NotoSansKR-Regular.ttf"
                : "NotoSansKR-Bold.ttf|NotoSans-Bold.ttf|NanumGothicBold.ttf|NotoSansKR-Regular.ttf";
            string notoSansKrExtraBold = containsNonLatin
                ? "NotoSansKR-ExtraBold.ttf|NanumGothicExtraBold.ttf|NanumGothicBold.ttf|NotoSans-Bold.ttf|NotoSansKR-Regular.ttf"
                : "NotoSansKR-ExtraBold.ttf|NotoSans-Bold.ttf|NanumGothicExtraBold.ttf|NanumGothicBold.ttf|NotoSansKR-Regular.ttf";
            string notoSerifKrRegular = containsNonLatin
                ? "NotoSerifKR-Regular.ttf|NotoSerif-Regular.ttf"
                : "NotoSerif-Regular.ttf|NotoSerifKR-Regular.ttf";
            string notoSerifKrBold = containsNonLatin
                ? "NotoSerifKR-Bold.ttf|NanumMyeongjoBold.ttf|NotoSerif-Bold.ttf|NotoSerifKR-Regular.ttf"
                : "NotoSerifKR-Bold.ttf|NotoSerif-Bold.ttf|NanumMyeongjoBold.ttf|NotoSerifKR-Regular.ttf";
            string notoSerifKrExtraBold = containsNonLatin
                ? "NotoSerifKR-ExtraBold.ttf|NanumMyeongjoExtraBold.ttf|NanumMyeongjoBold.ttf|NotoSerif-Bold.ttf|NotoSerifKR-Regular.ttf"
                : "NotoSerifKR-ExtraBold.ttf|NotoSerif-Bold.ttf|NanumMyeongjoExtraBold.ttf|NanumMyeongjoBold.ttf|NotoSerifKR-Regular.ttf";

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
                { "Noto Sans KR", fontWeight >= 800
                    ? notoSansKrExtraBold
                    : fontWeight >= 600
                        ? notoSansKrBold
                        : notoSansKrRegular },
                { "Noto Serif KR", fontWeight >= 800
                    ? notoSerifKrExtraBold
                    : fontWeight >= 600
                        ? notoSerifKrBold
                        : notoSerifKrRegular },
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

        public void AddHighlight(int pageIndex, double x, double y, double width, double height, 
            Color? color = null, float opacity = 0.5f)
        {
            ApplyEdit(doc => AddHighlightInternal(doc, pageIndex, x, y, width, height, color, opacity));
        }

        public void AddHighlightInternal(PdfDocument doc, int pageIndex, double x, double y, double width, double height, 
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

        public void AddSignatureInternal(
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

        public void AddImage(int pageIndex, string imagePath, double x, double y,
            double width, double height)
        {
            ApplyEdit(doc => AddImageInternal(doc, pageIndex, imagePath, x, y, width, height));
        }

        public void AddImageInternal(PdfDocument doc, int pageIndex, string imagePath, double x, double y,
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

        public void DeletePage(int pageIndex)
        {
            ApplyEdit(doc => DeletePageInternal(doc, pageIndex));
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DeletePageInternal(PdfDocument doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;
            doc.RemovePage(pageIndex + 1);
        }

        public void MovePage(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return;

            ApplyEdit(doc => MovePageInternal(doc, fromIndex, toIndex));
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
        }

        public void MovePageInternal(PdfDocument doc, int fromIndex, int toIndex)
        {
            int pageCount = doc.GetNumberOfPages();
            if (fromIndex < 0 || fromIndex >= pageCount ||
                toIndex < 0 || toIndex >= pageCount || fromIndex == toIndex)
                return;

            // iText page numbers are one-based. MovePage's second argument is
            // the new one-based position in the same document.
            doc.MovePage(fromIndex + 1, toIndex + 1);
        }

        public void NewDocument()
        {
            lock (_docLock)
            {
                using var ms = new MemoryStream();
                using (var writer = new PdfWriter(ms))
                using (var doc = new PdfDocument(writer))
                {
                    doc.AddNewPage();
                }
                _pdfBytes = ms.ToArray();
                _filePath = null;
                _isModified = false;
                _undoStack.Clear();
                _redoStack.Clear();
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Close()
        {
            _pdfBytes = null;
            _filePath = null;
            _isModified = false;
            _undoStack.Clear();
            _redoStack.Clear();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            PageStructureChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            if (!CanUndo || _pdfBytes == null) return;

            lock (_docLock)
            {
                var currentState = GetUIStateFunc?.Invoke();
                _redoStack.Push(((byte[])_pdfBytes.Clone(), currentState));
                
                var past = _undoStack.Pop();
                _pdfBytes = past.Bytes;
                
                _isModified = true; 
                _contentCache.Clear();
                
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                UndoRedoPerformed?.Invoke(this, past.UIState);
            }
        }

        public void Redo()
        {
            if (!CanRedo || _pdfBytes == null) return;

            lock (_docLock)
            {
                var currentState = GetUIStateFunc?.Invoke();
                _undoStack.Push(((byte[])_pdfBytes.Clone(), currentState));
                
                var next = _redoStack.Pop();
                _pdfBytes = next.Bytes;
                
                _isModified = true;
                _contentCache.Clear();
                
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                PageStructureChanged?.Invoke(this, EventArgs.Empty);
                UndoRedoPerformed?.Invoke(this, next.UIState);
            }
        }

        public (double width, double height) GetPageSize(int pageIndex)
        {
            if (_pdfBytes == null) return (0, 0);
            try
            {
                using var reader = new PdfReader(new MemoryStream(_pdfBytes));
                using var doc = new PdfDocument(reader);
                if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return (0, 0);
                var size = doc.GetPage(pageIndex + 1).GetPageSize();
                return (size.GetWidth(), size.GetHeight());
            }
            catch { return (0, 0); }
        }

        public async Task<List<PdfPageContent>> ExtractPageContentsAsync(int pageIndex)
        {
            if (_pdfBytes == null) return new List<PdfPageContent>();

            lock (_docLock)
            {
                if (_contentCache.TryGetValue(pageIndex, out var cached))
                    return cached;
            }

            byte[] pdfSnapshot;
            lock (_docLock)
                pdfSnapshot = (byte[])_pdfBytes.Clone();

            List<PdfPageContent> contents = await Task.Run(() =>
            {
                var extractedContents = new List<PdfPageContent>();
                try
                {
                    using var reader = new PdfReader(new MemoryStream(pdfSnapshot));
                    using var doc = new PdfDocument(reader);
                    if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages())
                        return extractedContents;

                    var page = doc.GetPage(pageIndex + 1);
                    var pageSize = page.GetPageSize();
                    
                    var operationTargets = ExtractTextOperationDescriptors(page);
                    var imageTargets = ExtractImageOperationDescriptors(page);
                    var pathTargets = ExtractPathOperationDescriptors(page);
                    var shadingTargets = ExtractShadingOperationDescriptors(page);
                    var listener = new ContentExtractionListener(
                        pageSize.GetHeight(), imageTargets);
                    PdfCanvasProcessor processor = new PdfCanvasProcessor(listener);
                    var tracker = new TextOperationTracker(operationTargets);
                    foreach (string operatorName in new[] { "Tj", "TJ", "'", "\"" })
                    {
                        var trackingOperator = new TrackingTextContentOperator(listener, tracker);
                        trackingOperator.InnerOperator = processor.RegisterContentOperator(
                            operatorName, trackingOperator);
                    }
                    var pathTracker = new PathOperationTracker(pathTargets);
                    foreach (string operatorName in new[]
                    {
                        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"
                    })
                    {
                        var trackingOperator = new TrackingPathContentOperator(listener, pathTracker);
                        trackingOperator.InnerOperator = processor.RegisterContentOperator(
                            operatorName, trackingOperator);
                    }
                    var shadingTracker = new ShadingOperationTracker(shadingTargets);
                    var shadingOperator = new TrackingShadingContentOperator(listener, shadingTracker);
                    shadingOperator.InnerOperator = processor.RegisterContentOperator("sh", shadingOperator);
                    processor.ProcessPageContent(page);
                    
                    extractedContents = GroupTextIntoEditRegions(
                        listener.Contents.Where(content => content.Type == PageContentType.Text));
                    extractedContents.AddRange(listener.Contents.Where(content => content.Type == PageContentType.Image));
                    extractedContents.AddRange(GroupVectorPathsIntoGraphics(
                        listener.VectorPaths, pageSize.GetHeight()));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting page contents: {ex.Message}");
                }

                return extractedContents;
            });

            await PopulateVectorGraphicImagesAsync(pdfSnapshot, pageIndex, contents);
            lock (_docLock)
                _contentCache[pageIndex] = contents;
            return contents;
        }

        private static async Task PopulateVectorGraphicImagesAsync(
            byte[] pdfBytes,
            int pageIndex,
            IEnumerable<PdfPageContent> contents)
        {
            const double pdfToPixels = 96.0 / 72.0;
            foreach (PdfPageContent content in contents.Where(content =>
                content.Type == PageContentType.Image &&
                content.GraphicOperations.Count > 0 &&
                string.IsNullOrEmpty(content.Text)))
            {
                using var input = new MemoryStream(pdfBytes);
                using MemoryStream? rendered = await PdfRenderHelper.RenderRegionWithWindowsPdfStreamAsync(
                    input.AsRandomAccessStream(),
                    pageIndex,
                    new Windows.Foundation.Rect(
                        content.X * pdfToPixels,
                        content.Y * pdfToPixels,
                        content.Width * pdfToPixels,
                        content.Height * pdfToPixels),
                    3.0);
                if (rendered == null || rendered.Length == 0)
                    continue;

                byte[] bytes = rendered.ToArray();
                string directory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "PDF_simple_edit", "images");
                Directory.CreateDirectory(directory);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string path = System.IO.Path.Combine(directory, $"{hash}.png");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, bytes);
                content.Text = path;
            }
        }


        private static (float x, float y, float width, float height, float originalPdfX, float originalPdfY) GetTextBounds(TextRenderInfo textInfo, float pageHeight)
        {
            var ascentRect = textInfo.GetAscentLine().GetBoundingRectangle();
            var descentRect = textInfo.GetDescentLine().GetBoundingRectangle();
            var baselineRect = textInfo.GetBaseline().GetBoundingRectangle();

            float minX = new[] { ascentRect.GetLeft(), descentRect.GetLeft(), baselineRect.GetLeft() }.Min();
            float maxX = new[] { ascentRect.GetRight(), descentRect.GetRight(), baselineRect.GetRight() }.Max();
            float minY = new[] { ascentRect.GetBottom(), descentRect.GetBottom(), baselineRect.GetBottom() }.Min();
            float maxY = new[] { ascentRect.GetTop(), descentRect.GetTop(), baselineRect.GetTop() }.Max();

            float width = Math.Max(maxX - minX, 0.5f);
            float height = Math.Max(maxY - minY, 0.5f);
            float uiY = pageHeight - maxY;

            return (minX, uiY, width, height, minX, minY);
        }

        private static void MergeBounds(PdfPageContent content, (float x, float y, float width, float height, float originalPdfX, float originalPdfY) bounds)
        {
            float left = Math.Min((float)content.X, bounds.x);
            float top = Math.Min((float)content.Y, bounds.y);
            float right = Math.Max((float)(content.X + content.Width), bounds.x + bounds.width);
            float bottom = Math.Max((float)(content.Y + content.Height), bounds.y + bounds.height);

            content.X = left;
            content.Y = top;
            content.Width = right - left;
            content.Height = bottom - top;
            content.OriginalPdfX = left;
            content.OriginalPdfY = bounds.originalPdfY;
        }

        private static float ResolveFontSize(TextRenderInfo textInfo, float actualHeight)
        {
            float reportedFontSize = textInfo.GetFontSize();
            if (reportedFontSize > 0.1f)
            {
                try
                {
                    var font = textInfo.GetFont();
                    string sample = textInfo.GetText();
                    float metricHeight = font.GetAscent(sample, reportedFontSize)
                                       - font.GetDescent(sample, reportedFontSize);
                    if (actualHeight > 0.1f && metricHeight > 0.01f)
                        return reportedFontSize * (actualHeight / metricHeight);
                }
                catch
                {
                    // Fall through to the reported size when the embedded font has incomplete metrics.
                }

                return reportedFontSize;
            }

            if (actualHeight > 0.1f)
                return actualHeight;

            return 12f;
        }

        private static PdfFontMetadata GetFontMetadata(
            TextRenderInfo textInfo,
            IDictionary<int, PdfFontMetadata> cache)
        {
            PdfFont? font = textInfo.GetFont();
            int objectNumber = font?.GetPdfObject()?.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (objectNumber > 0 && cache.TryGetValue(objectNumber, out PdfFontMetadata cached))
                return cached;

            PdfFontMetadata metadata = PdfFontMetadataResolver.Resolve(font);
            if (objectNumber > 0)
                cache[objectNumber] = metadata;
            return metadata;
        }

        private static string NormalizeFontFamily(string rawFontName)
        {
            if (string.IsNullOrWhiteSpace(rawFontName))
                return "맑은 고딕";

            string cleanFontName = rawFontName;
            if (cleanFontName.Contains("+"))
                cleanFontName = cleanFontName[(cleanFontName.IndexOf('+') + 1)..];

            cleanFontName = cleanFontName.Replace("-Bold", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("-Italic", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("-Oblique", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Bold", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Italic", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Oblique", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("MT", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("PS", "", StringComparison.OrdinalIgnoreCase)
                                         .Trim();

            string lowerFont = cleanFontName.ToLowerInvariant();

            if (lowerFont.Contains("malgun")) return "맑은 고딕";
            if (lowerFont.Contains("gulim")) return "굴림";
            if (lowerFont.Contains("dotum")) return "돋움";
            if (lowerFont.Contains("batang")) return "바탕";
            if (lowerFont.Contains("gungsuh")) return "궁서";
            if (lowerFont.Contains("nanumgothic")) return "나눔고딕";
            if (lowerFont.Contains("notosanscjkkr") || lowerFont.Contains("notosanskr")) return "Noto Sans KR";
            if (lowerFont.Contains("notoserifcjkkr") || lowerFont.Contains("notoserifkr")) return "Noto Serif KR";
            if (lowerFont.Contains("cambriamath")) return "Cambria Math";
            if (lowerFont.Contains("cambria")) return "Cambria";
            if (lowerFont.Contains("arial")) return "Arial";
            if (lowerFont.Contains("times")) return "Times New Roman";
            if (lowerFont.Contains("helvetica")) return "Arial";
            if (lowerFont.Contains("courier")) return "Courier New";
            if (lowerFont.Contains("tahoma")) return "Tahoma";
            if (lowerFont.Contains("verdana")) return "Verdana";
            if (lowerFont.Contains("segoe")) return "Segoe UI";
            if (lowerFont.Contains("consolas")) return "Consolas";

            return string.IsNullOrWhiteSpace(cleanFontName) ? "맑은 고딕" : cleanFontName;
        }

        private static (bool isBold, bool isItalic) InferFontStyle(string rawFontName, int fontWeight)
        {
            string lower = rawFontName?.ToLowerInvariant() ?? string.Empty;
            bool isBold = fontWeight >= 600 || lower.Contains("bold");
            bool isItalic = lower.Contains("italic") || lower.Contains("oblique");
            return (isBold, isItalic);
        }

        private static string ColorToHex(Color? color)
        {
            if (color == null)
                return "#000000";

            // DeviceCmyk stores four values (C, M, Y, K). It must be converted
            // before the generic RGB/gray handling below; treating the first
            // three CMYK channels as RGB changes colors such as the red title
            // in the supplied AJCC PDF into cyan.
            if (color is DeviceCmyk cmyk)
            {
                var rgb = Color.ConvertCmykToRgb(cmyk);
                float[] rgbValues = rgb.GetColorValue();
                if (rgbValues.Length >= 3)
                {
                    byte r = (byte)Math.Clamp((int)Math.Round(rgbValues[0] * 255), 0, 255);
                    byte g = (byte)Math.Clamp((int)Math.Round(rgbValues[1] * 255), 0, 255);
                    byte b = (byte)Math.Clamp((int)Math.Round(rgbValues[2] * 255), 0, 255);
                    return $"#{r:X2}{g:X2}{b:X2}";
                }
            }

            float[] values = color.GetColorValue();
            if (values.Length >= 3)
            {
                byte r = (byte)Math.Clamp((int)Math.Round(values[0] * 255), 0, 255);
                byte g = (byte)Math.Clamp((int)Math.Round(values[1] * 255), 0, 255);
                byte b = (byte)Math.Clamp((int)Math.Round(values[2] * 255), 0, 255);
                return $"#{r:X2}{g:X2}{b:X2}";
            }

            if (values.Length == 1)
            {
                byte gray = (byte)Math.Clamp((int)Math.Round(values[0] * 255), 0, 255);
                return $"#{gray:X2}{gray:X2}{gray:X2}";
            }

            return "#000000";
        }

        private sealed class TextOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
            public int TextRenderMode { get; init; }
        }

        private sealed class ImageOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
            public string ResourceName { get; init; } = string.Empty;
        }

        private sealed class PathOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
        }

        private sealed class ShadingOperationDescriptor
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
        }

        private sealed class VectorPathFragment
        {
            public double X { get; init; }
            public double Y { get; init; }
            public double Width { get; init; }
            public double Height { get; init; }
            public int ShapeCount { get; init; }
            public PathOperationDescriptor Target { get; init; } = new();
            public TextOperationDescriptor? TextTarget { get; init; }
            public ShadingOperationDescriptor? ShadingTarget { get; init; }
        }

        private static bool IsPathPaintingOperator(string operatorName) =>
            operatorName is "S" or "s" or "f" or "F" or "f*" or
                "B" or "B*" or "b" or "b*" or "n";

        private static List<PathOperationDescriptor> ExtractPathOperationDescriptors(PdfPage page)
        {
            var result = new List<PathOperationDescriptor>();
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;
                AppendPathOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }
            return result;
        }

        private static List<ShadingOperationDescriptor> ExtractShadingOperationDescriptors(PdfPage page)
        {
            var result = new List<ShadingOperationDescriptor>();
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;
                AppendShadingOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }
            return result;
        }

        private static void AppendShadingOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<ShadingOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;
            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new PdfCanvasParser(tokenizer);
                int shadingOperationIndex = -1;
                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;
                    if (operation[^1] is not PdfLiteral literal)
                        continue;
                    string operatorName = literal.ToString();
                    if (operatorName == "sh")
                    {
                        shadingOperationIndex++;
                        result.Add(new ShadingOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = shadingOperationIndex
                        });
                    }
                    else if (operatorName == "Do" && operation.Count > 1 &&
                        operation[0] is PdfName resourceName)
                    {
                        PdfStream? xObject = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                        if (xObject?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) == true)
                        {
                            PdfDictionary? formResourceDictionary = xObject.GetAsDictionary(PdfName.Resources);
                            PdfResources formResources = formResourceDictionary != null
                                ? new PdfResources(formResourceDictionary)
                                : resources;
                            AppendShadingOperationDescriptors(
                                xObject, formResources, -1, result, recursionStack);
                        }
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static void AppendPathOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<PathOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;

            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new PdfCanvasParser(tokenizer);
                int pathOperationIndex = -1;

                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;
                    if (operation[^1] is not PdfLiteral literal)
                        continue;

                    string operatorName = literal.ToString();
                    if (IsPathPaintingOperator(operatorName))
                    {
                        pathOperationIndex++;
                        result.Add(new PathOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = pathOperationIndex
                        });
                    }
                    else if (operatorName == "Do" && operation.Count > 1 &&
                        operation[0] is PdfName resourceName)
                    {
                        PdfStream? xObject = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                        if (xObject?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) == true)
                        {
                            PdfDictionary? formResourceDictionary = xObject.GetAsDictionary(PdfName.Resources);
                            PdfResources formResources = formResourceDictionary != null
                                ? new PdfResources(formResourceDictionary)
                                : resources;
                            AppendPathOperationDescriptors(
                                xObject, formResources, -1, result, recursionStack);
                        }
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static List<ImageOperationDescriptor> ExtractImageOperationDescriptors(PdfPage page)
        {
            var result = new List<ImageOperationDescriptor>();
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;

                AppendImageOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }

            return result;
        }

        private static void AppendImageOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<ImageOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;

            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new PdfCanvasParser(tokenizer);
                int doOperationIndex = -1;

                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;
                    if (operation[^1] is not PdfLiteral literal ||
                        literal.ToString() != "Do" ||
                        operation.Count <= 1 ||
                        operation[0] is not PdfName resourceName)
                    {
                        continue;
                    }

                    doOperationIndex++;
                    PdfStream? xObject = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                    PdfName? subtype = xObject?.GetAsName(PdfName.Subtype);
                    if (PdfName.Image.Equals(subtype))
                    {
                        result.Add(new ImageOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = doOperationIndex,
                            ResourceName = resourceName.GetValue()
                        });
                    }
                    else if (xObject != null && PdfName.Form.Equals(subtype))
                    {
                        PdfDictionary? formResourceDictionary = xObject.GetAsDictionary(PdfName.Resources);
                        PdfResources formResources = formResourceDictionary != null
                            ? new PdfResources(formResourceDictionary)
                            : resources;
                        AppendImageOperationDescriptors(
                            xObject, formResources, -1, result, recursionStack);
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static List<TextOperationDescriptor> ExtractTextOperationDescriptors(PdfPage page)
        {
            var result = new List<TextOperationDescriptor>();

            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                var contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    continue;

                AppendTextOperationDescriptors(
                    contentStream, page.GetResources(), streamIndex, result, new HashSet<int>());
            }

            return result;
        }

        private static void AppendTextOperationDescriptors(
            PdfStream contentStream,
            PdfResources resources,
            int pageStreamIndex,
            List<TextOperationDescriptor> result,
            HashSet<int> recursionStack)
        {
            int streamObjectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
            if (streamObjectNumber > 0 && !recursionStack.Add(streamObjectNumber))
                return;

            try
            {
                var sourceFactory = new RandomAccessSourceFactory();
                var randomSource = sourceFactory.CreateSource(contentStream.GetBytes());
                var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
                var parser = new iText.Kernel.Pdf.Canvas.Parser.Util.PdfCanvasParser(tokenizer);
                var renderModeStack = new Stack<int>();
                int renderMode = 0;
                int textOperationIndex = -1;

                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation == null || operation.Count == 0)
                        break;

                    if (operation[^1] is not PdfLiteral literal)
                        continue;

                    string operatorName = literal.ToString();
                    if (operatorName == "q")
                    {
                        renderModeStack.Push(renderMode);
                    }
                    else if (operatorName == "Q")
                    {
                        renderMode = renderModeStack.Count > 0 ? renderModeStack.Pop() : 0;
                    }
                    else if (operatorName == "Tr" && operation.Count > 1 && operation[0] is PdfNumber number)
                    {
                        renderMode = number.IntValue();
                    }
                    else if (IsTextShowingOperator(operatorName))
                    {
                        textOperationIndex++;
                        result.Add(new TextOperationDescriptor
                        {
                            StreamIndex = pageStreamIndex,
                            StreamObjectNumber = streamObjectNumber,
                            OperationIndex = textOperationIndex,
                            TextRenderMode = renderMode
                        });
                    }
                    else if (operatorName == "Do" && operation.Count > 1 && operation[0] is PdfName resourceName)
                    {
                        var xObjects = resources.GetResource(PdfName.XObject);
                        var formStream = xObjects?.GetAsStream(resourceName);
                        if (formStream?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) == true)
                        {
                            var formResourceDictionary = formStream.GetAsDictionary(PdfName.Resources);
                            var formResources = formResourceDictionary != null
                                ? new PdfResources(formResourceDictionary)
                                : resources;
                            AppendTextOperationDescriptors(
                                formStream, formResources, -1, result, recursionStack);
                        }
                    }
                }
            }
            finally
            {
                if (streamObjectNumber > 0)
                    recursionStack.Remove(streamObjectNumber);
            }
        }

        private static bool HasSameEditableStyle(PdfPageContent region, PdfPageContent next)
        {
            double referenceSize = Math.Max(region.FontSize, 1);
            return string.Equals(region.FontFamily, next.FontFamily, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(region.FontSize - next.FontSize) <= Math.Max(0.75, referenceSize * 0.15)
                && string.Equals(region.Color, next.Color, StringComparison.OrdinalIgnoreCase)
                && region.FontWeight == next.FontWeight
                && region.IsBold == next.IsBold
                && region.IsItalic == next.IsItalic;
        }

        private static bool CanMergeIntoEditRegion(PdfPageContent region, PdfPageContent next, out bool startsNewLine)
        {
            startsNewLine = false;
            if (region.TextFragments.Count == 0 || next.TextFragments.Count == 0 || !HasSameEditableStyle(region, next))
                return false;

            var previous = region.TextFragments[^1];
            var current = next.TextFragments[0];
            double fontSize = Math.Max(Math.Max(previous.FontSize, current.FontSize), 1);
            // 일부 임베디드 폰트는 같은 PDF 줄에서도 글리프별 ascent/descent
            // 메트릭이 달라 OriginalPdfY가 흔들립니다. 그 값을 기준으로 하면
            // 한 줄의 단어가 여러 편집 영역으로 잘립니다. 화면 좌표의 상단
            // 위치는 실제 줄 배치를 반영하므로 줄 판정에는 Y를 사용합니다.
            double lineTopDelta = Math.Abs(previous.Y - current.Y);
            bool sameLine = lineTopDelta <= Math.Max(1.25, fontSize * 0.35);

            if (sameLine)
            {
                double gap = current.X - (previous.X + previous.Width);
                return gap >= -fontSize && gap <= fontSize * 2.25;
            }

            double verticalGap = current.Y - (previous.Y + previous.Height);
            if (current.Y <= previous.Y || verticalGap > fontSize * 1.35)
                return false;

            double overlap = Math.Min(region.X + region.Width, current.X + current.Width)
                           - Math.Max(region.X, current.X);
            double minWidth = Math.Max(Math.Min(region.Width, current.Width), 1);
            bool horizontallyRelated = overlap / minWidth >= 0.35
                || Math.Abs(current.X - region.X) <= Math.Max(18, fontSize * 1.75);

            startsNewLine = horizontallyRelated;
            return horizontallyRelated;
        }

        private static List<PdfPageContent> GroupTextIntoEditRegions(IEnumerable<PdfPageContent> rawContents)
        {
            var regions = new List<PdfPageContent>();

            foreach (var item in rawContents)
            {
                var last = regions.LastOrDefault();
                if (last == null || !CanMergeIntoEditRegion(last, item, out bool startsNewLine))
                {
                    regions.Add(item);
                    continue;
                }

                var previous = last.TextFragments[^1];
                var current = item.TextFragments[0];
                if (startsNewLine)
                {
                    last.Text += "\r\n" + item.Text;
                    double detectedLineHeight = Math.Abs(current.Y - previous.Y);
                    if (detectedLineHeight > 0.1)
                    {
                        last.LineHeight = last.LineHeight > 0.1
                            ? (last.LineHeight + detectedLineHeight) / 2.0
                            : detectedLineHeight;
                    }
                }
                else
                {
                    double gap = current.X - (previous.X + previous.Width);
                    bool needsSpace = gap > Math.Max(previous.FontSize, current.FontSize) * 0.15
                        && !last.Text.EndsWith(" ", StringComparison.Ordinal)
                        && !item.Text.StartsWith(" ", StringComparison.Ordinal);
                    last.Text += (needsSpace ? " " : string.Empty) + item.Text;
                }

                double left = Math.Min(last.X, item.X);
                double top = Math.Min(last.Y, item.Y);
                double right = Math.Max(last.X + last.Width, item.X + item.Width);
                double bottom = Math.Max(last.Y + last.Height, item.Y + item.Height);
                last.X = left;
                last.Y = top;
                last.Width = right - left;
                last.Height = bottom - top;
                int targetLineIndex = last.TextFragments.Max(fragment => fragment.LineIndex)
                    + (startsNewLine ? 1 : 0);
                foreach (var fragment in item.TextFragments)
                    fragment.LineIndex = targetLineIndex;
                last.TextFragments.AddRange(item.TextFragments);
            }

            foreach (var region in regions)
            {
                if (region.LineHeight <= 0.1)
                    region.LineHeight = Math.Max(region.FontSize * 1.2, region.Height);
                if (region.TextFragments.Count > 0)
                {
                    var first = region.TextFragments[0];
                    region.BaselineOffset = (first.Y - region.Y) + first.BaselineOffset;
                    region.OriginalFontObjectNumber = first.OriginalFontObjectNumber;
                }
            }

            return regions;
        }

        private static List<PdfPageContent> GroupVectorPathsIntoGraphics(
            IReadOnlyCollection<VectorPathFragment> rawPaths,
            double pageHeight)
        {
            var duplicateTargets = rawPaths
                .GroupBy(GetVectorTargetKey)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            var graphics = new List<PdfPageContent>();

            List<VectorPathFragment> remaining = rawPaths
                .Where(path => !duplicateTargets.Contains(GetVectorTargetKey(path)))
                .ToList();
            while (remaining.Count > 0)
            {
                var component = new List<VectorPathFragment> { remaining[0] };
                remaining.RemoveAt(0);
                bool added;
                do
                {
                    added = false;
                    for (int index = remaining.Count - 1; index >= 0; index--)
                    {
                        if (!component.Any(path => AreVectorPathsNear(path, remaining[index])))
                            continue;
                        component.Add(remaining[index]);
                        remaining.RemoveAt(index);
                        added = true;
                    }
                }
                while (added);

                double left = component.Min(path => path.X);
                double top = component.Min(path => path.Y);
                double right = component.Max(path => path.X + path.Width);
                double bottom = component.Max(path => path.Y + path.Height);
                double width = right - left;
                double height = bottom - top;
                if (component.Sum(path => path.ShapeCount) < 3 || width < 8 || height < 5)
                    continue;

                const double cropPadding = 1.0;
                left -= cropPadding;
                top -= cropPadding;
                width += cropPadding * 2;
                height += cropPadding * 2;
                List<PdfGraphicOperationTarget> operationTargets = component
                    .Select(GetVectorTargetKey)
                    .Distinct()
                    .Select(key => new PdfGraphicOperationTarget
                    {
                        StreamObjectNumber = key.StreamObjectNumber,
                        StreamIndex = key.StreamIndex,
                        OperationIndex = key.OperationIndex,
                        IsTextOperation = key.IsText,
                        IsShadingOperation = key.IsShading
                    })
                    .OrderBy(target => target.StreamObjectNumber)
                    .ThenBy(target => target.StreamIndex)
                    .ThenBy(target => target.OperationIndex)
                    .ToList();
                PdfGraphicOperationTarget firstTarget = operationTargets[0];
                graphics.Add(new PdfPageContent
                {
                    Type = PageContentType.Image,
                    X = left,
                    Y = top,
                    Width = width,
                    Height = height,
                    OriginalPdfX = left,
                    OriginalPdfY = pageHeight - (top + height),
                    ImageId = "vector:" + string.Join(";", operationTargets.Select(target =>
                        $"{target.StreamObjectNumber}:{target.OperationIndex}:" +
                        $"{target.IsTextOperation}:{target.IsShadingOperation}")),
                    ContentStreamIndex = firstTarget.StreamIndex,
                    ContentStreamObjectNumber = firstTarget.StreamObjectNumber,
                    OperationIndex = firstTarget.OperationIndex,
                    GraphicOperations = operationTargets
                });
            }

            return graphics;
        }

        private static (
            int StreamObjectNumber,
            int StreamIndex,
            int OperationIndex,
            bool IsText,
            bool IsShading) GetVectorTargetKey(VectorPathFragment path) =>
            path.ShadingTarget != null
                ? (
                    path.ShadingTarget.StreamObjectNumber,
                    path.ShadingTarget.StreamIndex,
                    path.ShadingTarget.OperationIndex,
                    false,
                    true)
                : path.TextTarget != null
                ? (
                    path.TextTarget.StreamObjectNumber,
                    path.TextTarget.StreamIndex,
                    path.TextTarget.OperationIndex,
                    true,
                    false)
                : (
                    path.Target.StreamObjectNumber,
                    path.Target.StreamIndex,
                    path.Target.OperationIndex,
                    false,
                    false);

        private static bool AreVectorPathsNear(
            VectorPathFragment first,
            VectorPathFragment second)
        {
            const double maximumGap = 10;
            return first.X <= second.X + second.Width + maximumGap &&
                first.X + first.Width + maximumGap >= second.X &&
                first.Y <= second.Y + second.Height + maximumGap &&
                first.Y + first.Height + maximumGap >= second.Y;
        }

        private sealed class TextOperationTracker
        {
            private readonly IReadOnlyList<TextOperationDescriptor> _targets;
            private int _index;

            public TextOperationTracker(IReadOnlyList<TextOperationDescriptor> targets)
            {
                _targets = targets;
            }

            public TextOperationDescriptor? Next()
            {
                return _index < _targets.Count ? _targets[_index++] : null;
            }
        }

        private sealed class PathOperationTracker
        {
            private readonly IReadOnlyList<PathOperationDescriptor> _targets;
            private int _index;

            public PathOperationTracker(IReadOnlyList<PathOperationDescriptor> targets)
            {
                _targets = targets;
            }

            public PathOperationDescriptor? Next() =>
                _index < _targets.Count ? _targets[_index++] : null;
        }

        private sealed class ShadingOperationTracker
        {
            private readonly IReadOnlyList<ShadingOperationDescriptor> _targets;
            private int _index;

            public ShadingOperationTracker(IReadOnlyList<ShadingOperationDescriptor> targets)
            {
                _targets = targets;
            }

            public ShadingOperationDescriptor? Next() =>
                _index < _targets.Count ? _targets[_index++] : null;
        }

        private sealed class TrackingTextContentOperator : IContentOperator
        {
            private readonly ContentExtractionListener _listener;
            private readonly TextOperationTracker _tracker;

            public IContentOperator? InnerOperator { get; set; }

            public TrackingTextContentOperator(
                ContentExtractionListener listener,
                TextOperationTracker tracker)
            {
                _listener = listener;
                _tracker = tracker;
            }

            public void Invoke(
                PdfCanvasProcessor processor,
                PdfLiteral operatorLiteral,
                IList<PdfObject> operands)
            {
                ContentExtractionListener.TextOperationState previousState = _listener.CaptureTextOperationState();
                _listener.BeginTextOperation(_tracker.Next(), operands);
                try
                {
                    InnerOperator?.Invoke(processor, operatorLiteral, operands);
                }
                finally
                {
                    _listener.RestoreTextOperationState(previousState);
                }
            }
        }

        private sealed class TrackingPathContentOperator : IContentOperator
        {
            private readonly ContentExtractionListener _listener;
            private readonly PathOperationTracker _tracker;

            public IContentOperator? InnerOperator { get; set; }

            public TrackingPathContentOperator(
                ContentExtractionListener listener,
                PathOperationTracker tracker)
            {
                _listener = listener;
                _tracker = tracker;
            }

            public void Invoke(
                PdfCanvasProcessor processor,
                PdfLiteral operatorLiteral,
                IList<PdfObject> operands)
            {
                PathOperationDescriptor? previous = _listener.CurrentPathTarget;
                _listener.CurrentPathTarget = _tracker.Next();
                try
                {
                    InnerOperator?.Invoke(processor, operatorLiteral, operands);
                }
                finally
                {
                    _listener.CurrentPathTarget = previous;
                }
            }
        }

        private sealed class TrackingShadingContentOperator : IContentOperator
        {
            private readonly ContentExtractionListener _listener;
            private readonly ShadingOperationTracker _tracker;

            public IContentOperator? InnerOperator { get; set; }

            public TrackingShadingContentOperator(
                ContentExtractionListener listener,
                ShadingOperationTracker tracker)
            {
                _listener = listener;
                _tracker = tracker;
            }

            public void Invoke(
                PdfCanvasProcessor processor,
                PdfLiteral operatorLiteral,
                IList<PdfObject> operands)
            {
                _listener.AddShading(_tracker.Next());
                InnerOperator?.Invoke(processor, operatorLiteral, operands);
            }
        }

        private class ContentExtractionListener : IEventListener
        {
            public List<PdfPageContent> Contents { get; } = new();
            public List<VectorPathFragment> VectorPaths { get; } = new();
            private readonly float _pageHeight;
            private readonly Dictionary<int, PdfFontMetadata> _fontMetadataCache = new();
            private readonly IReadOnlyList<ImageOperationDescriptor> _imageTargets;
            private readonly HashSet<(int StreamObjectNumber, int StreamIndex, int OperationIndex)> _ambiguousImageTargets;
            private int _imageTargetIndex;
            public TextOperationDescriptor? CurrentTarget { get; set; }
            public PathOperationDescriptor? CurrentPathTarget { get; set; }
            private List<(int Index, byte[] Bytes)> _textOperands = new();
            private int _nextTextOperand;
            private (double X, double Y, double Width, double Height)? _pendingClipBounds;

            public sealed record TextOperationState(
                TextOperationDescriptor? Target,
                List<(int Index, byte[] Bytes)> TextOperands,
                int NextTextOperand);

            public ContentExtractionListener(
                float pageHeight,
                IReadOnlyList<ImageOperationDescriptor> imageTargets)
            {
                _pageHeight = pageHeight;
                _imageTargets = imageTargets;
                _ambiguousImageTargets = imageTargets
                    .GroupBy(target => (
                        target.StreamObjectNumber,
                        target.StreamIndex,
                        target.OperationIndex))
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet();
            }

            public TextOperationState CaptureTextOperationState() => new(
                CurrentTarget, _textOperands, _nextTextOperand);

            public void RestoreTextOperationState(TextOperationState state)
            {
                CurrentTarget = state.Target;
                _textOperands = state.TextOperands;
                _nextTextOperand = state.NextTextOperand;
            }

            public void BeginTextOperation(
                TextOperationDescriptor? target,
                IList<PdfObject> operands)
            {
                CurrentTarget = target;
                _textOperands = new List<(int Index, byte[] Bytes)>();
                _nextTextOperand = 0;

                int stringIndex = 0;
                foreach (PdfObject operand in operands)
                {
                    if (operand is PdfString text)
                    {
                        _textOperands.Add((stringIndex++, text.GetValueBytes()));
                    }
                    else if (operand is PdfArray array)
                    {
                        foreach (PdfObject element in array)
                        {
                            if (element is PdfString arrayText)
                                _textOperands.Add((stringIndex++, arrayText.GetValueBytes()));
                        }
                    }
                }
            }

            private int ResolveTextOperandIndex(PdfString renderedText)
            {
                byte[] renderedBytes = renderedText.GetValueBytes();
                for (int i = _nextTextOperand; i < _textOperands.Count; i++)
                {
                    if (!_textOperands[i].Bytes.SequenceEqual(renderedBytes))
                        continue;

                    _nextTextOperand = i + 1;
                    return _textOperands[i].Index;
                }

                return -1;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type == EventType.RENDER_PATH && data is PathRenderInfo pathInfo)
                {
                    AddVectorPath(pathInfo);
                    return;
                }
                if (type == EventType.RENDER_IMAGE && data is ImageRenderInfo imageInfo)
                {
                    AddImageContent(imageInfo);
                    return;
                }
                if (type != EventType.RENDER_TEXT || data is not TextRenderInfo textInfo)
                    return;

                var text = textInfo.GetText();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                var bounds = GetTextBounds(textInfo, _pageHeight);
                float fontSize = ResolveFontSize(textInfo, bounds.height);
                var baseline = textInfo.GetBaseline().GetStartPoint();
                double baselineOffset = (_pageHeight - baseline.Get(1)) - bounds.y;
                PdfFontMetadata fontMetadata = GetFontMetadata(textInfo, _fontMetadataCache);
                string rawFontName = fontMetadata.RawName;
                string fontFamily = NormalizeFontFamily(rawFontName);
                var (isBold, isItalic) = InferFontStyle(rawFontName, fontMetadata.Weight);
                string color = ColorToHex(textInfo.GetFillColor());
                int originalFontObjectNumber = textInfo.GetFont()?.GetPdfObject()
                    ?.GetIndirectReference()?.GetObjNumber() ?? -1;

                TextOperationDescriptor? target = CurrentTarget;
                int textOperandIndex = ResolveTextOperandIndex(textInfo.GetPdfString());
                double? textAdvanceAdjustment = Math.Abs(textInfo.GetFontSize()) > 0.0001f
                    ? -1000.0 * textInfo.GetUnscaledWidth() / textInfo.GetFontSize()
                    : null;

                var fragment = new PdfTextFragment
                {
                    Text = text,
                    X = bounds.x,
                    Y = bounds.y,
                    Width = bounds.width,
                    Height = bounds.height,
                    OriginalPdfX = bounds.originalPdfX,
                    OriginalPdfY = bounds.originalPdfY,
                    FontSize = fontSize,
                    FontFamily = fontFamily,
                    Color = color,
                    FontWeight = fontMetadata.Weight,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    ContentStreamIndex = target?.StreamIndex ?? -1,
                    ContentStreamObjectNumber = target?.StreamObjectNumber ?? -1,
                    OperationIndex = target?.OperationIndex ?? -1,
                    TextOperandIndex = textOperandIndex,
                    TextAdvanceAdjustment = textAdvanceAdjustment,
                    TextRenderMode = target?.TextRenderMode ?? 0,
                    BaselineOffset = baselineOffset,
                    OriginalFontObjectNumber = originalFontObjectNumber
                };

                Contents.Add(new PdfPageContent
                {
                    Type = PageContentType.Text,
                    Text = text,
                    X = bounds.x,
                    Y = bounds.y,
                    Width = bounds.width,
                    Height = bounds.height,
                    FontSize = fontSize,
                    FontFamily = fontFamily,
                    Color = color,
                    FontWeight = fontMetadata.Weight,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    OriginalPdfX = bounds.originalPdfX,
                    OriginalPdfY = bounds.originalPdfY,
                    ContentStreamIndex = fragment.ContentStreamIndex,
                    ContentStreamObjectNumber = fragment.ContentStreamObjectNumber,
                    OperationIndex = fragment.OperationIndex,
                    TextRenderMode = fragment.TextRenderMode,
                    BaselineOffset = baselineOffset,
                    OriginalFontObjectNumber = originalFontObjectNumber,
                    TextFragments = new List<PdfTextFragment> { fragment }
                });
            }

            private void AddVectorPath(PathRenderInfo pathInfo)
            {
                PathOperationDescriptor? target = CurrentPathTarget;
                bool isClippingPath = pathInfo.IsPathModifiesClippingPath();
                if ((target == null && CurrentTarget == null) ||
                    (pathInfo.GetOperation() == PathRenderInfo.NO_OP && !isClippingPath))
                {
                    return;
                }

                Matrix matrix = pathInfo.GetCtm();
                float a = matrix.Get(Matrix.I11);
                float b = matrix.Get(Matrix.I12);
                float c = matrix.Get(Matrix.I21);
                float d = matrix.Get(Matrix.I22);
                float e = matrix.Get(Matrix.I31);
                float f = matrix.Get(Matrix.I32);
                var subpaths = pathInfo.GetPath().GetSubpaths();
                var points = subpaths
                    .SelectMany(subpath => subpath.GetSegments())
                    .SelectMany(segment => segment.GetBasePoints())
                    .Select(point => (
                        X: (double)(a * point.GetX() + c * point.GetY() + e),
                        Y: (double)(b * point.GetX() + d * point.GetY() + f)))
                    .ToList();
                if (points.Count == 0)
                    return;

                double padding = Math.Max(pathInfo.GetLineWidth() / 2.0, 0.1);
                double left = points.Min(point => point.X) - padding;
                double right = points.Max(point => point.X) + padding;
                double bottom = points.Min(point => point.Y) - padding;
                double top = points.Max(point => point.Y) + padding;
                if (right - left <= 0.1 || top - bottom <= 0.1)
                    return;

                if (isClippingPath)
                {
                    _pendingClipBounds = (
                        left,
                        _pageHeight - top,
                        right - left,
                        top - bottom);
                    return;
                }

                VectorPaths.Add(new VectorPathFragment
                {
                    X = left,
                    Y = _pageHeight - top,
                    Width = right - left,
                    Height = top - bottom,
                    ShapeCount = subpaths.Count,
                    Target = target ?? new PathOperationDescriptor(),
                    TextTarget = CurrentTarget
                });
            }

            public void AddShading(ShadingOperationDescriptor? target)
            {
                if (target == null || _pendingClipBounds is not { } bounds)
                    return;
                VectorPaths.Add(new VectorPathFragment
                {
                    X = bounds.X,
                    Y = bounds.Y,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    ShapeCount = 1,
                    Target = new PathOperationDescriptor(),
                    ShadingTarget = target
                });
                _pendingClipBounds = null;
            }

            private void AddImageContent(ImageRenderInfo imageInfo)
            {
                // Inline images do not have a removable Do operation. Keep them in
                // the rendered page, but do not expose a control that cannot be saved safely.
                if (imageInfo.IsInline() || _imageTargetIndex >= _imageTargets.Count)
                    return;

                ImageOperationDescriptor target = _imageTargets[_imageTargetIndex++];
                if (_ambiguousImageTargets.Contains((
                    target.StreamObjectNumber,
                    target.StreamIndex,
                    target.OperationIndex)))
                {
                    // A shared form XObject can place the same image operation more
                    // than once. Removing that shared operation would delete every
                    // placement, so those instances are intentionally not editable.
                    return;
                }
                try
                {
                    Matrix matrix = imageInfo.GetImageCtm();
                    float a = matrix.Get(Matrix.I11);
                    float b = matrix.Get(Matrix.I12);
                    float c = matrix.Get(Matrix.I21);
                    float d = matrix.Get(Matrix.I22);
                    float e = matrix.Get(Matrix.I31);
                    float f = matrix.Get(Matrix.I32);
                    float[] xs = { e, a + e, c + e, a + c + e };
                    float[] ys = { f, b + f, d + f, b + d + f };
                    float left = xs.Min();
                    float right = xs.Max();
                    float bottom = ys.Min();
                    float top = ys.Max();
                    if (right - left <= 0.1f || top - bottom <= 0.1f)
                        return;

                    PdfImageXObject image = imageInfo.GetImage();
                    byte[] imageBytes = image.GetImageBytes(true);
                    string extension = NormalizeImageExtension(image.IdentifyImageFileExtension());
                    string imagePath = SaveExtractedImage(imageBytes, extension);

                    Contents.Add(new PdfPageContent
                    {
                        Type = PageContentType.Image,
                        X = left,
                        Y = _pageHeight - top,
                        Width = right - left,
                        Height = top - bottom,
                        OriginalPdfX = left,
                        OriginalPdfY = bottom,
                        Text = imagePath,
                        ImageId = target.ResourceName,
                        ContentStreamIndex = target.StreamIndex,
                        ContentStreamObjectNumber = target.StreamObjectNumber,
                        OperationIndex = target.OperationIndex
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting PDF image: {ex.Message}");
                }
            }

            private static string NormalizeImageExtension(string? extension) =>
                extension?.ToLowerInvariant() switch
                {
                    "jpg" or "jpeg" => "jpg",
                    "jp2" => "jp2",
                    "tif" or "tiff" => "tif",
                    "jbig2" => "jbig2",
                    _ => "png"
                };

            private static string SaveExtractedImage(byte[] bytes, string extension)
            {
                string directory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "PDF_simple_edit", "images");
                Directory.CreateDirectory(directory);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string path = System.IO.Path.Combine(directory, $"{hash}.{extension}");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, bytes);
                return path;
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new[]
                {
                    EventType.RENDER_TEXT,
                    EventType.RENDER_IMAGE,
                    EventType.RENDER_PATH
                };
            }
        }

        private sealed class TextOperationTarget
        {
            public int StreamIndex { get; init; }
            public int StreamObjectNumber { get; init; }
            public int OperationIndex { get; init; }
            public int TextOperandIndex { get; init; } = -1;
            public double? TextAdvanceAdjustment { get; init; }
            public bool RemoveWholeOperation { get; init; }
            public int TextRenderMode { get; init; }
        }

        private static bool IsTextShowingOperator(string operatorName)
        {
            return operatorName == "Tj" || operatorName == "TJ" || operatorName == "'" || operatorName == "\"";
        }

        private static void WriteOperation(PdfOutputStream output, IList<PdfObject> operation)
        {
            for (int i = 0; i < operation.Count; i++)
            {
                output.Write(operation[i]);
                if (i < operation.Count - 1)
                    output.WriteSpace();
            }

            output.WriteNewLine();
        }

        private static void WriteTextAdvance(PdfOutputStream output, double adjustment)
        {
            var advances = new PdfArray();
            advances.Add(new PdfNumber(adjustment));
            WriteOperation(output, new PdfObject[] { advances, new PdfLiteral("TJ") });
        }

        private static bool TryWritePartiallyRemovedTextOperation(
            PdfOutputStream output,
            IList<PdfObject> operation,
            string operatorName,
            IReadOnlyCollection<TextOperationTarget> targets)
        {
            if (targets.Any(target => target.TextOperandIndex < 0 ||
                !target.TextAdvanceAdjustment.HasValue ||
                !double.IsFinite(target.TextAdvanceAdjustment.Value)))
                return false;

            var replacements = targets
                .GroupBy(target => target.TextOperandIndex)
                .ToDictionary(group => group.Key, group => group.First().TextAdvanceAdjustment!.Value);

            if (operatorName == "TJ" && operation.Count > 1 && operation[0] is PdfArray sourceArray)
            {
                var rewrittenArray = new PdfArray();
                var replacedIndexes = new HashSet<int>();
                int stringIndex = 0;
                foreach (PdfObject element in sourceArray)
                {
                    if (element is PdfString && replacements.TryGetValue(stringIndex, out double adjustment))
                    {
                        rewrittenArray.Add(new PdfNumber(adjustment));
                        replacedIndexes.Add(stringIndex);
                    }
                    else
                    {
                        rewrittenArray.Add(element);
                    }

                    if (element is PdfString)
                        stringIndex++;
                }

                if (replacedIndexes.Count != replacements.Count)
                    return false;

                WriteOperation(output, new PdfObject[] { rewrittenArray, new PdfLiteral("TJ") });
                return true;
            }

            if (!replacements.TryGetValue(0, out double singleAdjustment) || replacements.Count != 1)
                return false;

            if (operatorName == "Tj")
            {
                WriteTextAdvance(output, singleAdjustment);
                return true;
            }

            if (operatorName == "'")
            {
                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                WriteTextAdvance(output, singleAdjustment);
                return true;
            }

            if (operatorName == "\"" && operation.Count >= 4)
            {
                WriteOperation(output, new PdfObject[] { operation[0], new PdfLiteral("Tw") });
                WriteOperation(output, new PdfObject[] { operation[1], new PdfLiteral("Tc") });
                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                WriteTextAdvance(output, singleAdjustment);
                return true;
            }

            return false;
        }

        private static bool TryRewriteContentStreamWithoutText(
            byte[] contentBytes,
            IReadOnlyCollection<TextOperationTarget> targets,
            out byte[]? rewrittenBytes)
        {
            rewrittenBytes = null;
            if (targets.Count == 0)
                return false;

            var targetsByOperation = targets
                .GroupBy(target => target.OperationIndex)
                .ToDictionary(group => group.Key, group => (IReadOnlyCollection<TextOperationTarget>)group.ToList());
            var handledOperations = new HashSet<int>();
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new iText.Kernel.Pdf.Canvas.Parser.Util.PdfCanvasParser(tokenizer);

            using var stream = new MemoryStream();
            var output = new PdfOutputStream(stream);
            bool modified = false;
            int textOperationIndex = -1;

            while (true)
            {
                var parsedOperation = parser.Parse(new List<PdfObject>());
                if (parsedOperation == null || parsedOperation.Count == 0)
                    break;

                if (parsedOperation[^1] is PdfLiteral literal && IsTextShowingOperator(literal.ToString()))
                {
                    textOperationIndex++;
                    if (targetsByOperation.TryGetValue(textOperationIndex, out IReadOnlyCollection<TextOperationTarget>? operationTargets))
                    {
                        string operatorName = literal.ToString();
                        bool removeWholeOperation = operationTargets.Any(target => target.RemoveWholeOperation);
                        if (!removeWholeOperation)
                        {
                            if (!TryWritePartiallyRemovedTextOperation(
                                output, parsedOperation, operatorName, operationTargets))
                                return false;
                        }
                        else
                        {
                            if (operatorName == "'")
                            {
                                // ' is equivalent to T* followed by Tj. Preserve only the line move.
                                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                            }
                            else if (operatorName == "\"" && parsedOperation.Count >= 4)
                            {
                                // " sets word/character spacing, moves to the next line, then shows text.
                                WriteOperation(output, new PdfObject[] { parsedOperation[0], new PdfLiteral("Tw") });
                                WriteOperation(output, new PdfObject[] { parsedOperation[1], new PdfLiteral("Tc") });
                                WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                            }
                        }

                        handledOperations.Add(textOperationIndex);
                        modified = true;
                        continue;
                    }
                }

                WriteOperation(output, parsedOperation);
            }

            if (!modified || handledOperations.Count != targetsByOperation.Count)
                return false;

            rewrittenBytes = stream.ToArray();
            return true;
        }

        private static int CountTextShowingOperations(byte[] contentBytes)
        {
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new iText.Kernel.Pdf.Canvas.Parser.Util.PdfCanvasParser(tokenizer);
            int count = 0;

            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation == null || operation.Count == 0)
                    break;
                if (operation[^1] is PdfLiteral literal &&
                    IsTextShowingOperator(literal.ToString()))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool TryRewriteCombinedPageContentStreams(
            PdfPage page,
            IReadOnlyCollection<TextOperationTarget> targets,
            out List<(PdfStream Stream, byte[] Bytes)> rewrites)
        {
            rewrites = new List<(PdfStream Stream, byte[] Bytes)>();
            var pageStreams = new List<PdfStream>();
            var operationOffsetsByObject = new Dictionary<int, int>();
            var operationOffsetsByIndex = new Dictionary<int, int>();
            using var combined = new MemoryStream();
            int operationOffset = 0;

            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    return false;

                byte[] bytes = contentStream.GetBytes();
                pageStreams.Add(contentStream);
                operationOffsetsByIndex[streamIndex] = operationOffset;
                int objectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
                if (objectNumber > 0)
                    operationOffsetsByObject[objectNumber] = operationOffset;

                operationOffset += CountTextShowingOperations(bytes);
                combined.Write(bytes, 0, bytes.Length);
                combined.WriteByte((byte)'\n');
            }

            var combinedTargets = new List<TextOperationTarget>(targets.Count);
            foreach (TextOperationTarget target in targets)
            {
                int streamOperationOffset;
                if (target.StreamObjectNumber > 0)
                {
                    if (!operationOffsetsByObject.TryGetValue(
                        target.StreamObjectNumber,
                        out streamOperationOffset))
                    {
                        return false;
                    }
                }
                else if (!operationOffsetsByIndex.TryGetValue(
                    target.StreamIndex,
                    out streamOperationOffset))
                {
                    return false;
                }

                combinedTargets.Add(new TextOperationTarget
                {
                    StreamIndex = 0,
                    StreamObjectNumber = pageStreams[0].GetIndirectReference()?.GetObjNumber() ?? -1,
                    OperationIndex = streamOperationOffset + target.OperationIndex,
                    TextOperandIndex = target.TextOperandIndex,
                    TextAdvanceAdjustment = target.TextAdvanceAdjustment,
                    RemoveWholeOperation = target.RemoveWholeOperation,
                    TextRenderMode = target.TextRenderMode
                });
            }

            if (!TryRewriteContentStreamWithoutText(
                combined.ToArray(),
                combinedTargets,
                out byte[]? rewrittenBytes) ||
                rewrittenBytes == null)
            {
                return false;
            }

            for (int i = 0; i < pageStreams.Count; i++)
            {
                rewrites.Add((
                    pageStreams[i],
                    i == 0 ? rewrittenBytes : Array.Empty<byte>()));
            }

            return true;
        }

        private static bool TryRemoveTextOperations(PdfDocument doc, int pageIndex, IEnumerable<TextOperationTarget> targets)
        {
            var validTargets = targets
                .Where(target => target.OperationIndex >= 0 &&
                    (target.StreamObjectNumber > 0 || target.StreamIndex >= 0))
                .ToList();

            if (validTargets.Count == 0)
                return false;

            var page = doc.GetPage(pageIndex + 1);
            var rewrites = new List<(PdfStream Stream, byte[] Bytes)>();

            // Page content streams are one logical content sequence. Some PDFs
            // split a TJ array and its TJ operator across adjacent stream objects.
            // Rewriting those objects independently loses the operand array and
            // makes a valid partial deletion fail. Join the page streams for
            // parsing, translate local operation indexes to the joined sequence,
            // then keep the rewritten sequence in the first page stream.
            var pageStreamObjectNumbers = Enumerable.Range(0, page.GetContentStreamCount())
                .Select(index => page.GetContentStream(index)?.GetIndirectReference()?.GetObjNumber() ?? -1)
                .Where(number => number > 0)
                .ToHashSet();
            bool allTargetsArePageStreams = validTargets.All(target =>
                target.StreamObjectNumber > 0
                    ? pageStreamObjectNumbers.Contains(target.StreamObjectNumber)
                    : target.StreamIndex >= 0 && target.StreamIndex < page.GetContentStreamCount());
            if (allTargetsArePageStreams)
            {
                if (!TryRewriteCombinedPageContentStreams(page, validTargets, out rewrites))
                    return false;

                foreach ((PdfStream stream, byte[] bytes) in rewrites)
                    stream.SetData(bytes);
                return rewrites.Count > 0;
            }

            foreach (var group in validTargets
                .Where(target => target.StreamObjectNumber > 0)
                .GroupBy(target => target.StreamObjectNumber))
            {
                if (doc.GetPdfObject(group.Key) is not PdfStream contentStream)
                    return false;

                if (!TryRewriteContentStreamWithoutText(
                    contentStream.GetBytes(), group.ToList(), out byte[]? rewrittenBytes) ||
                    rewrittenBytes == null)
                    return false;

                rewrites.Add((contentStream, rewrittenBytes));
            }

            foreach (var group in validTargets
                .Where(target => target.StreamObjectNumber <= 0)
                .GroupBy(target => target.StreamIndex))
            {
                if (group.Key >= page.GetContentStreamCount())
                    return false;

                var contentStream = page.GetContentStream(group.Key);
                if (contentStream == null)
                    return false;

                if (!TryRewriteContentStreamWithoutText(
                    contentStream.GetBytes(), group.ToList(), out byte[]? rewrittenBytes) ||
                    rewrittenBytes == null)
                    return false;

                rewrites.Add((contentStream, rewrittenBytes));
            }

            foreach ((PdfStream stream, byte[] bytes) in rewrites)
                stream.SetData(bytes);

            return rewrites.Count > 0;
        }

        private static bool TryRewriteContentStreamWithoutGraphics(
            byte[] contentBytes,
            IReadOnlyCollection<int> imageOperationIndexes,
            IReadOnlyCollection<int> pathOperationIndexes,
            IReadOnlyCollection<int> textOperationIndexes,
            IReadOnlyCollection<int> shadingOperationIndexes,
            out byte[]? rewrittenBytes)
        {
            rewrittenBytes = null;
            if (imageOperationIndexes.Count == 0 &&
                pathOperationIndexes.Count == 0 &&
                textOperationIndexes.Count == 0 &&
                shadingOperationIndexes.Count == 0)
                return false;

            var imageTargets = imageOperationIndexes.ToHashSet();
            var pathTargets = pathOperationIndexes.ToHashSet();
            var textTargets = textOperationIndexes.ToHashSet();
            var shadingTargets = shadingOperationIndexes.ToHashSet();
            var handledImages = new HashSet<int>();
            var handledPaths = new HashSet<int>();
            var handledText = new HashSet<int>();
            var handledShadings = new HashSet<int>();
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new PdfCanvasParser(tokenizer);
            using var stream = new MemoryStream();
            var output = new PdfOutputStream(stream);
            int doOperationIndex = -1;
            int pathOperationIndex = -1;
            int textOperationIndex = -1;
            int shadingOperationIndex = -1;

            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation == null || operation.Count == 0)
                    break;

                if (operation[^1] is not PdfLiteral literal)
                {
                    WriteOperation(output, operation);
                    continue;
                }

                string operatorName = literal.ToString();
                if (operatorName == "Do")
                {
                    doOperationIndex++;
                    if (imageTargets.Contains(doOperationIndex))
                    {
                        handledImages.Add(doOperationIndex);
                        continue;
                    }
                }
                else if (IsPathPaintingOperator(operatorName))
                {
                    pathOperationIndex++;
                    if (pathTargets.Contains(pathOperationIndex))
                    {
                        handledPaths.Add(pathOperationIndex);
                        WriteOperation(output, new PdfObject[] { new PdfLiteral("n") });
                        continue;
                    }
                }
                else if (IsTextShowingOperator(operatorName))
                {
                    textOperationIndex++;
                    if (textTargets.Contains(textOperationIndex))
                    {
                        handledText.Add(textOperationIndex);
                        if (operatorName == "'")
                        {
                            WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                        }
                        else if (operatorName == "\"" && operation.Count >= 4)
                        {
                            WriteOperation(output, new PdfObject[] { operation[0], new PdfLiteral("Tw") });
                            WriteOperation(output, new PdfObject[] { operation[1], new PdfLiteral("Tc") });
                            WriteOperation(output, new PdfObject[] { new PdfLiteral("T*") });
                        }
                        continue;
                    }
                }
                else if (operatorName == "sh")
                {
                    shadingOperationIndex++;
                    if (shadingTargets.Contains(shadingOperationIndex))
                    {
                        handledShadings.Add(shadingOperationIndex);
                        continue;
                    }
                }

                WriteOperation(output, operation);
            }

            if (handledImages.Count != imageTargets.Count ||
                handledPaths.Count != pathTargets.Count ||
                handledText.Count != textTargets.Count ||
                handledShadings.Count != shadingTargets.Count)
                return false;

            rewrittenBytes = stream.ToArray();
            return true;
        }

        private readonly record struct GraphicOperationCounts(
            int Images,
            int Paths,
            int Text,
            int Shadings);

        private static GraphicOperationCounts CountGraphicOperations(byte[] contentBytes)
        {
            var sourceFactory = new RandomAccessSourceFactory();
            var randomSource = sourceFactory.CreateSource(contentBytes);
            var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(randomSource));
            var parser = new PdfCanvasParser(tokenizer);
            int images = 0;
            int paths = 0;
            int text = 0;
            int shadings = 0;
            while (true)
            {
                var operation = parser.Parse(new List<PdfObject>());
                if (operation == null || operation.Count == 0)
                    break;
                if (operation[^1] is not PdfLiteral literal)
                    continue;
                string operatorName = literal.ToString();
                if (operatorName == "Do") images++;
                else if (IsPathPaintingOperator(operatorName)) paths++;
                else if (IsTextShowingOperator(operatorName)) text++;
                else if (operatorName == "sh") shadings++;
            }
            return new GraphicOperationCounts(images, paths, text, shadings);
        }

        private static bool TryRewriteCombinedPageContentStreamsWithoutGraphics(
            PdfPage page,
            IReadOnlyCollection<GraphicStreamRemoval> removals,
            out List<(PdfStream Stream, byte[] Bytes)> rewrites)
        {
            rewrites = new List<(PdfStream Stream, byte[] Bytes)>();
            var pageStreams = new List<PdfStream>();
            var offsetsByObject = new Dictionary<int, GraphicOperationCounts>();
            var offsetsByIndex = new Dictionary<int, GraphicOperationCounts>();
            using var combined = new MemoryStream();
            var offset = new GraphicOperationCounts(0, 0, 0, 0);
            for (int streamIndex = 0; streamIndex < page.GetContentStreamCount(); streamIndex++)
            {
                PdfStream? contentStream = page.GetContentStream(streamIndex);
                if (contentStream == null)
                    return false;
                byte[] bytes = contentStream.GetBytes();
                pageStreams.Add(contentStream);
                offsetsByIndex[streamIndex] = offset;
                int objectNumber = contentStream.GetIndirectReference()?.GetObjNumber() ?? -1;
                if (objectNumber > 0)
                    offsetsByObject[objectNumber] = offset;
                GraphicOperationCounts count = CountGraphicOperations(bytes);
                offset = new GraphicOperationCounts(
                    offset.Images + count.Images,
                    offset.Paths + count.Paths,
                    offset.Text + count.Text,
                    offset.Shadings + count.Shadings);
                combined.Write(bytes, 0, bytes.Length);
                combined.WriteByte((byte)'\n');
            }

            var imageTargets = new HashSet<int>();
            var pathTargets = new HashSet<int>();
            var textTargets = new HashSet<int>();
            var shadingTargets = new HashSet<int>();
            foreach (GraphicStreamRemoval removal in removals)
            {
                GraphicOperationCounts streamOffset;
                if (removal.StreamObjectNumber > 0)
                {
                    if (!offsetsByObject.TryGetValue(removal.StreamObjectNumber, out streamOffset))
                        return false;
                }
                else if (!offsetsByIndex.TryGetValue(removal.StreamIndex, out streamOffset))
                {
                    return false;
                }
                imageTargets.UnionWith(removal.ImageOperationIndexes.Select(index => streamOffset.Images + index));
                pathTargets.UnionWith(removal.PathOperationIndexes.Select(index => streamOffset.Paths + index));
                textTargets.UnionWith(removal.TextOperationIndexes.Select(index => streamOffset.Text + index));
                shadingTargets.UnionWith(removal.ShadingOperationIndexes.Select(index => streamOffset.Shadings + index));
            }

            if (!TryRewriteContentStreamWithoutGraphics(
                combined.ToArray(), imageTargets, pathTargets, textTargets, shadingTargets,
                out byte[]? rewrittenBytes) || rewrittenBytes == null)
            {
                return false;
            }
            for (int index = 0; index < pageStreams.Count; index++)
                rewrites.Add((pageStreams[index], index == 0 ? rewrittenBytes : Array.Empty<byte>()));
            return true;
        }

        private sealed class GraphicStreamRemoval
        {
            public int StreamObjectNumber { get; init; }
            public int StreamIndex { get; init; }
            public HashSet<int> ImageOperationIndexes { get; } = new();
            public HashSet<int> PathOperationIndexes { get; } = new();
            public HashSet<int> TextOperationIndexes { get; } = new();
            public HashSet<int> ShadingOperationIndexes { get; } = new();
        }

        public async Task<bool> RemoveOriginalImageAnnotationsAsync(
            int pageIndex,
            IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (annotations == null || annotations.Count == 0)
                return true;

            List<PdfAnnotation>? targets = await ResolveCurrentImageAnnotationsAsync(
                pageIndex, annotations);
            if (targets == null || targets.Count != annotations.Count)
                return false;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages())
                        return;

                    PdfPage page = doc.GetPage(pageIndex + 1);
                    var removals = new Dictionary<(int StreamObjectNumber, int StreamIndex), GraphicStreamRemoval>();
                    foreach (PdfAnnotation annotation in targets)
                    {
                        if (annotation.GraphicOperations.Count == 0)
                        {
                            var key = (annotation.ContentStreamObjectNumber, annotation.ContentStreamIndex);
                            if (!removals.TryGetValue(key, out GraphicStreamRemoval? removal))
                            {
                                removal = new GraphicStreamRemoval
                                {
                                    StreamObjectNumber = key.ContentStreamObjectNumber,
                                    StreamIndex = key.ContentStreamIndex
                                };
                                removals[key] = removal;
                            }
                            removal.ImageOperationIndexes.Add(annotation.OperationIndex);
                            continue;
                        }

                        foreach (PdfGraphicOperationTarget operation in annotation.GraphicOperations)
                        {
                            var key = (operation.StreamObjectNumber, operation.StreamIndex);
                            if (!removals.TryGetValue(key, out GraphicStreamRemoval? removal))
                            {
                                removal = new GraphicStreamRemoval
                                {
                                    StreamObjectNumber = key.StreamObjectNumber,
                                    StreamIndex = key.StreamIndex
                                };
                                removals[key] = removal;
                            }
                            if (operation.IsShadingOperation)
                                removal.ShadingOperationIndexes.Add(operation.OperationIndex);
                            else if (operation.IsTextOperation)
                                removal.TextOperationIndexes.Add(operation.OperationIndex);
                            else
                                removal.PathOperationIndexes.Add(operation.OperationIndex);
                        }
                    }

                    var rewrites = new List<(PdfStream Stream, byte[] Bytes)>();
                    var pageStreamObjectNumbers = Enumerable.Range(0, page.GetContentStreamCount())
                        .Select(index => page.GetContentStream(index)?.GetIndirectReference()?.GetObjNumber() ?? -1)
                        .Where(number => number > 0)
                        .ToHashSet();
                    List<GraphicStreamRemoval> pageRemovals = removals.Values
                        .Where(removal => removal.StreamObjectNumber > 0
                            ? pageStreamObjectNumbers.Contains(removal.StreamObjectNumber)
                            : removal.StreamIndex >= 0 && removal.StreamIndex < page.GetContentStreamCount())
                        .ToList();
                    if (pageRemovals.Count > 0)
                    {
                        if (!TryRewriteCombinedPageContentStreamsWithoutGraphics(
                            page, pageRemovals, out List<(PdfStream Stream, byte[] Bytes)> pageRewrites))
                        {
                            return;
                        }
                        rewrites.AddRange(pageRewrites);
                    }

                    foreach (GraphicStreamRemoval removal in removals.Values.Except(pageRemovals))
                    {
                        PdfStream? contentStream = removal.StreamObjectNumber > 0
                            ? doc.GetPdfObject(removal.StreamObjectNumber) as PdfStream
                            : null;
                        if (contentStream == null ||
                            !TryRewriteContentStreamWithoutGraphics(
                                contentStream.GetBytes(),
                                removal.ImageOperationIndexes,
                                removal.PathOperationIndexes,
                                removal.TextOperationIndexes,
                                removal.ShadingOperationIndexes,
                                out byte[]? rewrittenBytes) || rewrittenBytes == null)
                        {
                            return;
                        }

                        rewrites.Add((contentStream, rewrittenBytes));
                    }

                    if (rewrites.Count == 0)
                        return;
                    foreach ((PdfStream streamToRewrite, byte[] bytes) in rewrites)
                        streamToRewrite.SetData(bytes);
                    success = true;
                });
                return success;
            });
        }

        private async Task<List<PdfAnnotation>?> ResolveCurrentImageAnnotationsAsync(
            int pageIndex,
            IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (annotations.Any(annotation => !annotation.IsOriginalImageReplacement))
                return null;

            List<PdfPageContent> pageContents = await ExtractPageContentsAsync(pageIndex);
            List<PdfPageContent> availableImages = pageContents
                .Where(content => content.Type == PageContentType.Image)
                .ToList();
            var usedImages = new HashSet<PdfPageContent>();
            var resolvedAnnotations = new List<PdfAnnotation>();

            foreach (PdfAnnotation annotation in annotations)
            {
                PdfPageContent? match = availableImages
                    .Where(content => !usedImages.Contains(content) &&
                        IsSameImagePosition(annotation, content))
                    .OrderByDescending(content => HasSameExtractedImage(annotation, content))
                    .ThenByDescending(content => string.Equals(
                        annotation.OriginalImageName,
                        content.ImageId,
                        StringComparison.Ordinal))
                    .ThenBy(content =>
                        Math.Abs(annotation.OriginalPdfX - content.OriginalPdfX) +
                        Math.Abs(annotation.OriginalPdfY - content.OriginalPdfY))
                    .FirstOrDefault();
                if (match == null || match.OperationIndex < 0 ||
                    (match.ContentStreamObjectNumber <= 0 && match.ContentStreamIndex < 0))
                {
                    return null;
                }

                usedImages.Add(match);
                PdfAnnotation resolved = annotation.Clone();
                resolved.ContentStreamIndex = match.ContentStreamIndex;
                resolved.ContentStreamObjectNumber = match.ContentStreamObjectNumber;
                resolved.OperationIndex = match.OperationIndex;
                resolved.OriginalImageName = match.ImageId;
                resolved.GraphicOperationIndexes = new List<int>(match.GraphicOperationIndexes);
                resolved.GraphicTextOperationIndexes = new List<int>(match.GraphicTextOperationIndexes);
                resolved.GraphicOperations = match.GraphicOperations
                    .Select(target => target.Clone())
                    .ToList();
                resolvedAnnotations.Add(resolved);
            }

            return resolvedAnnotations;
        }

        private static bool IsSameImagePosition(
            PdfAnnotation annotation,
            PdfPageContent content)
        {
            double horizontalTolerance = Math.Max(2, content.Width * 0.05);
            double verticalTolerance = Math.Max(2, content.Height * 0.05);
            return Math.Abs(annotation.OriginalPdfX - content.OriginalPdfX) <= horizontalTolerance &&
                Math.Abs(annotation.OriginalPdfY - content.OriginalPdfY) <= verticalTolerance;
        }

        private static bool HasSameExtractedImage(
            PdfAnnotation annotation,
            PdfPageContent content)
        {
            if (string.IsNullOrEmpty(annotation.ImagePath) || string.IsNullOrEmpty(content.Text))
                return false;
            return string.Equals(
                System.IO.Path.GetFileName(annotation.ImagePath),
                System.IO.Path.GetFileName(content.Text),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool RemoveTextByCleanup(PdfDocument doc, int pageIndex, List<(double x, double y, double w, double h)> targets)
        {
            if (targets.Count == 0)
                return false;

            var page = doc.GetPage(pageIndex + 1);
            var rect = page.GetCropBox();
            float offsetLeft = rect.GetLeft();
            float offsetBottom = rect.GetBottom();

            var locations = new List<PdfCleanUpLocation>();
            foreach (var target in targets)
            {
                float pdfX = offsetLeft + (float)target.x;
                float pdfY = offsetBottom + (rect.GetHeight() - (float)target.y - (float)target.h);
                locations.Add(new PdfCleanUpLocation(
                    pageIndex + 1,
                    new Rectangle(pdfX, pdfY + 0.5f, (float)target.w, Math.Max((float)target.h - 1.0f, 0.5f)),
                    null));
            }

            var cleaner = new PdfCleanUpTool(doc, locations, new CleanUpProperties());
            cleaner.CleanUp();
            return true;
        }

        public async Task<bool> RemoveTextAsync(int pageIndex, double x, double y, double width, double height, int contentStreamIndex = -1, int operationIndex = -1, int textRenderMode = 0)
        {
            if (contentStreamIndex < 0 || operationIndex < 0)
                return false;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    success = TryRemoveTextOperations(doc, pageIndex, new[]
                    {
                        new TextOperationTarget
                        {
                            StreamIndex = contentStreamIndex,
                            OperationIndex = operationIndex,
                            RemoveWholeOperation = true,
                            TextRenderMode = textRenderMode
                        }
                    });

                });
                return success;
            });
        }

        public async Task<bool> RemoveOriginalTextAnnotationsAsync(int pageIndex, IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (annotations == null || annotations.Count == 0)
                return true;

            // An annotation can outlive an earlier content-stream rewrite. In that
            // case its operation indexes still look valid, but point at an older TJ
            // layout. Resolve the glyph fragments against the current PDF before
            // deleting so sequential edits do not fail with stale metadata.
            List<PdfAnnotation>? currentAnnotations = await ResolveCurrentTextAnnotationsAsync(
                pageIndex,
                annotations);
            if (currentAnnotations == null)
                return false;

            if (currentAnnotations.Any(annotation => annotation.TextFragments.Count > 0
                ? annotation.TextFragments.Any(fragment => fragment.OperationIndex < 0 ||
                    (fragment.ContentStreamObjectNumber <= 0 && fragment.ContentStreamIndex < 0) ||
                    fragment.TextOperandIndex < 0 ||
                    !fragment.TextAdvanceAdjustment.HasValue ||
                    !double.IsFinite(fragment.TextAdvanceAdjustment.Value))
                : annotation.OperationIndex < 0 ||
                    (annotation.ContentStreamObjectNumber <= 0 && annotation.ContentStreamIndex < 0)))
                return false;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    var preciseTargets = currentAnnotations
                        .SelectMany(a => a.TextFragments.Count > 0
                            ? a.TextFragments.Select(fragment => new TextOperationTarget
                            {
                                StreamIndex = fragment.ContentStreamIndex,
                                StreamObjectNumber = fragment.ContentStreamObjectNumber,
                                OperationIndex = fragment.OperationIndex,
                                TextOperandIndex = fragment.TextOperandIndex,
                                TextAdvanceAdjustment = fragment.TextAdvanceAdjustment,
                                TextRenderMode = fragment.TextRenderMode
                            })
                            : new[]
                            {
                                new TextOperationTarget
                                {
                                    StreamIndex = a.ContentStreamIndex,
                                    StreamObjectNumber = a.ContentStreamObjectNumber,
                                    OperationIndex = a.OperationIndex,
                                    RemoveWholeOperation = true,
                                    TextRenderMode = a.TextRenderMode
                                }
                            })
                        .Where(target => target.OperationIndex >= 0 &&
                            (target.StreamObjectNumber > 0 || target.StreamIndex >= 0))
                        .GroupBy(target => (
                            target.StreamObjectNumber,
                            target.StreamIndex,
                            target.OperationIndex,
                            target.TextOperandIndex,
                            target.RemoveWholeOperation))
                        .Select(group => group.First())
                        .ToList();

                    success = TryRemoveTextOperations(doc, pageIndex, preciseTargets);
                });

                return success;
            });
        }

        private async Task<List<PdfAnnotation>?> ResolveCurrentTextAnnotationsAsync(
            int pageIndex,
            IReadOnlyCollection<PdfAnnotation> annotations)
        {
            List<PdfPageContent> pageContents = await ExtractPageContentsAsync(pageIndex);
            List<PdfTextFragment> availableFragments = pageContents
                .Where(content => content.Type == PageContentType.Text)
                .SelectMany(content => content.TextFragments)
                .ToList();
            var usedFragments = new HashSet<PdfTextFragment>();
            var resolvedAnnotations = new List<PdfAnnotation>();

            foreach (PdfAnnotation annotation in annotations)
            {
                PdfAnnotation resolved = annotation.Clone();
                IReadOnlyList<PdfTextFragment> sourceFragments = annotation.TextFragments.Count > 0
                    ? annotation.TextFragments
                    : (IReadOnlyList<PdfTextFragment>?)FindMatchingTextRegion(pageContents, annotation)?.TextFragments
                        ?? Array.Empty<PdfTextFragment>();
                if (sourceFragments.Count == 0)
                    return null;

                var matchedFragments = new List<PdfTextFragment>();
                foreach (PdfTextFragment source in sourceFragments)
                {
                    PdfTextFragment? match = availableFragments
                        .Where(candidate => !usedFragments.Contains(candidate) &&
                            string.Equals(candidate.Text, source.Text, StringComparison.Ordinal) &&
                            IsSameTextPosition(source, candidate))
                        .OrderBy(candidate =>
                            Math.Abs(candidate.X - source.X) +
                            Math.Abs(candidate.Y - source.Y))
                        .FirstOrDefault();
                    if (match == null)
                        return null;

                    usedFragments.Add(match);
                    matchedFragments.Add(match.Clone());
                }

                resolved.TextFragments = matchedFragments;
                PdfTextFragment first = matchedFragments[0];
                resolved.ContentStreamIndex = first.ContentStreamIndex;
                resolved.ContentStreamObjectNumber = first.ContentStreamObjectNumber;
                resolved.OperationIndex = first.OperationIndex;
                resolved.TextRenderMode = first.TextRenderMode;
                resolvedAnnotations.Add(resolved);
            }

            return resolvedAnnotations;
        }

        private static PdfPageContent? FindMatchingTextRegion(
            IEnumerable<PdfPageContent> pageContents,
            PdfAnnotation annotation)
        {
            string expectedText = NormalizeComparableText(
                string.IsNullOrEmpty(annotation.OriginalText)
                    ? annotation.Content
                    : annotation.OriginalText);
            return pageContents
                .Where(content => content.Type == PageContentType.Text &&
                    string.Equals(
                        NormalizeComparableText(content.Text),
                        expectedText,
                        StringComparison.Ordinal) &&
                    IsSameTextPosition(annotation.X, annotation.Y, annotation.Width, annotation.Height,
                        content.X, content.Y, content.Width, content.Height))
                .OrderBy(content =>
                    Math.Abs(content.X - annotation.X) +
                    Math.Abs(content.Y - annotation.Y))
                .FirstOrDefault();
        }

        private static bool IsSameTextPosition(PdfTextFragment first, PdfTextFragment second) =>
            IsSameTextPosition(
                first.X, first.Y, first.Width, first.Height,
                second.X, second.Y, second.Width, second.Height);

        private static bool IsSameTextPosition(
            double firstX,
            double firstY,
            double firstWidth,
            double firstHeight,
            double secondX,
            double secondY,
            double secondWidth,
            double secondHeight)
        {
            double horizontalTolerance = Math.Max(2, Math.Max(firstWidth, secondWidth) * 0.75);
            double verticalTolerance = Math.Max(2, Math.Max(firstHeight, secondHeight) * 0.5);
            return Math.Abs(firstX - secondX) <= horizontalTolerance &&
                Math.Abs(firstY - secondY) <= verticalTolerance;
        }

        private static string NormalizeComparableText(string? text) =>
            (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

        public async Task<bool> RemoveMultipleTextsAsync(int pageIndex, List<(double x, double y, double w, double h)> targets)
        {
            if (targets == null || targets.Count == 0) return true;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    success = RemoveTextByCleanup(doc, pageIndex, targets);
                });
                return success;
            });
        }

        public async Task<bool> MoveTextAsync(int pageIndex, string text, double oldX, double oldY, double width, double height, double newX, double newY, string fontFamily = "맑은 고딕", double fontSize = 12, Color? color = null)
        {
            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    var page = doc.GetPage(pageIndex + 1);
                    var rect = page.GetCropBox();
                    float offsetLeft = rect.GetLeft();
                    float offsetBottom = rect.GetBottom();
                    
                    float oldPdfX = offsetLeft + (float)oldX;
                    float oldPdfY = offsetBottom + (rect.GetHeight() - (float)oldY - (float)height);
                    
                    // 삭제 영역 확장
                    float padding = 2.0f;
                    var location = new PdfCleanUpLocation(pageIndex + 1, 
                        new Rectangle(oldPdfX - padding, oldPdfY - padding, (float)width + (padding*2), (float)height + (padding*2)), 
                        null);
                    
                    // pdfSweep 실행 (생성자 주입 방식)
                    var locations = new List<PdfCleanUpLocation> { location };
                    PdfCleanUpTool cleaner = new PdfCleanUpTool(doc, locations, new CleanUpProperties());
                    cleaner.CleanUp();

                    // 수정된 부분: 하드코딩된 "맑은 고딕" 대신 파라미터로 받은 폰트와 사이즈 사용
                    AddTextInternal(doc, pageIndex, newX, newY, text, fontFamily, fontSize, color ?? ColorConstants.BLACK);
                    success = true;
                });
                return success;
            });
        }

        public PdfDocument? GetReadOnlyDocument()
        {
            if (_pdfBytes == null) return null;
            var reader = new PdfReader(new MemoryStream(_pdfBytes));
            return new PdfDocument(reader);
        }

        public async Task<bool> MergeFilesAsync(List<string> sourceFiles, string outputPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var writer = new PdfWriter(outputPath))
                    using (var destDoc = new PdfDocument(writer))
                    {
                        var merger = new PdfMerger(destDoc);

                        // 1. 현재 문서가 있다면 먼저 추가
                        if (_pdfBytes != null)
                        {
                            using (var ms = new MemoryStream(_pdfBytes))
                            using (var reader = new PdfReader(ms))
                            using (var sourceDoc = new PdfDocument(reader))
                            {
                                merger.Merge(sourceDoc, 1, sourceDoc.GetNumberOfPages());
                            }
                        }

                        // 2. 선택한 파일들 추가
                        foreach (var file in sourceFiles)
                        {
                            if (!File.Exists(file)) continue;
                            using (var reader = new PdfReader(file))
                            using (var sourceDoc = new PdfDocument(reader))
                            {
                                merger.Merge(sourceDoc, 1, sourceDoc.GetNumberOfPages());
                            }
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Merge error: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<int> SplitFileAsync(string outputFolder, List<(int start, int end)> ranges)
        {
            if (_pdfBytes == null) return 0;

            return await Task.Run(() =>
            {
                int count = 0;
                try
                {
                    string baseName = "split";
                    if (!string.IsNullOrEmpty(_filePath))
                    {
                        baseName = System.IO.Path.GetFileNameWithoutExtension(_filePath);
                    }

                    for (int i = 0; i < ranges.Count; i++)
                    {
                        var range = ranges[i];
                        string outputPath = System.IO.Path.Combine(outputFolder, $"{baseName}_{i + 1}.pdf");

                        using (var ms = new MemoryStream(_pdfBytes))
                        using (var reader = new PdfReader(ms))
                        using (var sourceDoc = new PdfDocument(reader))
                        using (var writer = new PdfWriter(outputPath))
                        using (var destDoc = new PdfDocument(writer))
                        {
                            sourceDoc.CopyPagesTo(range.start, range.end, destDoc);
                        }
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Split error: {ex.Message}");
                }
                return count;
            });
        }
    }
}
