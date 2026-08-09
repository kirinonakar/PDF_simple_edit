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
using iText.Kernel.Pdf.Canvas.Parser.Util;

namespace PDF_simple_edit.Helpers
{
    public class PdfDocumentManager
    {
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
            IReadOnlyList<double>? lineBaselineOffsets = null)
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
            PdfFont? fallbackFont = null;

            (PdfFont Font, bool IsFallback) ResolveFontForLine(string line, int lineIndex)
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

                        bool containsAllGlyphs = line
                            .Where(character => !char.IsControl(character) && !char.IsWhiteSpace(character))
                            .All(character => candidate.ContainsGlyph(character));
                        if (containsAllGlyphs)
                            return (candidate, false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Original PDF font reuse error: {ex.Message}");
                    }
                }

                if (fallbackFont != null)
                    return (fallbackFont, true);

                try
                {
                    string? resolvedPath = GetSystemFontPath(fontFamily, isBold);
                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        fallbackFont = PdfFontFactory.CreateFont(
                            resolvedPath,
                            PdfEncodings.IDENTITY_H,
                            PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                    }
                    else
                    {
                        string fallbackPath = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            "Fonts",
                            isBold ? "malgunbd.ttf" : "malgun.ttf");
                        fallbackFont = PdfFontFactory.CreateFont(
                            fallbackPath,
                            PdfEncodings.IDENTITY_H,
                            PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Font load error: {ex.Message}");
                    fallbackFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
                }

                return (fallbackFont, true);
            }

            var resolvedLineFonts = lines
                .Select((line, index) => ResolveFontForLine(line, index))
                .ToList();
            var fallbackBaselineAdjustments = resolvedLineFonts
                .Select((resolved, index) => resolved.IsFallback
                    ? Math.Max(resolved.Font.GetAscent(lines[index], (float)Math.Max(fontSize, 1)), 0)
                    : 0)
                .ToList();

            bool hasOriginalLineBaselines =
                lineBaselineOffsets != null && lineBaselineOffsets.Count > 0;
            float resolvedBaselineOffset = lineBaselineOffsets != null && lineBaselineOffsets.Count > 0
                ? (float)lineBaselineOffsets[0]
                : baselineOffset > 0.1
                    ? (float)baselineOffset
                    : Math.Max(
                        resolvedLineFonts[0].Font.GetAscent(
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
                    previousBaselineOffset += fallbackBaselineAdjustments[i - 1];
                    currentBaselineOffset += fallbackBaselineAdjustments[i];
                    canvas.MoveText(
                        currentXOffset - previousXOffset,
                        -(currentBaselineOffset - previousBaselineOffset));
                }
                canvas.SetFontAndSize(
                    resolvedLineFonts[i].Font,
                    (float)Math.Max(fontSize, 1));
                canvas.ShowText(lines[i]);
            }

            canvas.EndText();
            canvas.RestoreState();
            canvas.Release();
        }

        private string? GetSystemFontPath(string nameOrFile, bool isBold = false)
        {
            if (string.IsNullOrWhiteSpace(nameOrFile)) return null;

            string fontDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

            // 핵심: 굴림/돋움은 gulim.ttc에, 바탕/궁서는 batang.ttc에 묶여 있습니다. 인덱스를 지정해야 합니다.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "맑은 고딕", isBold ? "malgunbd.ttf" : "malgun.ttf" },
                { "Malgun Gothic", isBold ? "malgunbd.ttf" : "malgun.ttf" },
                { "굴림", "gulim.ttc,0" },
                { "굴림체", "gulim.ttc,1" },
                { "돋움", "gulim.ttc,2" },
                { "돋움체", "gulim.ttc,3" },
                { "바탕", "batang.ttc,0" },
                { "바탕체", "batang.ttc,1" },
                { "궁서", "batang.ttc,2" },
                { "궁서체", "batang.ttc,3" },
                { "나눔고딕", "NanumGothic.ttf" },
                { "Noto Sans KR", "NotoSansKR-VF.ttf" },
                { "Noto Serif KR", "NotoSerifKR-VF.ttf" },
                { "Arial", isBold ? "arialbd.ttf" : "arial.ttf" },
                { "Times New Roman", isBold ? "timesbd.ttf" : "times.ttf" },
                { "Tahoma", isBold ? "tahomabd.ttf" : "tahoma.ttf" },
                { "Verdana", isBold ? "verdanab.ttf" : "verdana.ttf" },
                { "Consolas", isBold ? "consolab.ttf" : "consola.ttf" },
                { "Courier New", isBold ? "courbd.ttf" : "cour.ttf" }
            };

            if (map.TryGetValue(nameOrFile, out string? mappedValue))
            {
                string[] parts = mappedValue.Split(',');
                string path = System.IO.Path.Combine(fontDir, parts[0]); // 실제 파일 경로
                
                if (File.Exists(path))
                {
                    // 파일이 존재하면 경로 뒤에 인덱스(,0 ,1 ,2 등)를 붙여서 반환
                    return parts.Length > 1 ? $"{path},{parts[1]}" : path;
                }
            }

            // 파일명 자체가 들어온 경우 처리
            if (nameOrFile.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || 
                nameOrFile.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                string path = System.IO.Path.Combine(fontDir, nameOrFile);
                if (File.Exists(path)) return path;
            }

            // 3. 매핑에 없으면 폰트 이름의 공백을 제거하고 유추 시도
            string guessName = nameOrFile.Replace(" ", "") + (isBold ? "bd.ttf" : ".ttf");
            string guessPath = System.IO.Path.Combine(fontDir, guessName);
            if (File.Exists(guessPath)) return guessPath;

            return null; // 그래도 없으면 null 반환
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

            return await Task.Run(() =>
            {
                var contents = new List<PdfPageContent>();
                try
                {
                    using var reader = new PdfReader(new MemoryStream(_pdfBytes));
                    using var doc = new PdfDocument(reader);
                    if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages())
                        return contents;

                    var page = doc.GetPage(pageIndex + 1);
                    var pageSize = page.GetPageSize();
                    
                    var operationTargets = ExtractTextOperationDescriptors(page);
                    var listener = new ContentExtractionListener(pageSize.GetHeight());
                    PdfCanvasProcessor processor = new PdfCanvasProcessor(listener);
                    var tracker = new TextOperationTracker(operationTargets);
                    foreach (string operatorName in new[] { "Tj", "TJ", "'", "\"" })
                    {
                        var trackingOperator = new TrackingTextContentOperator(listener, tracker);
                        trackingOperator.InnerOperator = processor.RegisterContentOperator(
                            operatorName, trackingOperator);
                    }
                    processor.ProcessPageContent(page);
                    
                    contents = GroupTextIntoEditRegions(listener.Contents);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting page contents: {ex.Message}");
                }

                lock (_docLock)
                {
                    _contentCache[pageIndex] = contents;
                }
                return contents;
            });
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

        private class ContentExtractionListener : IEventListener
        {
            public List<PdfPageContent> Contents { get; } = new();
            private readonly float _pageHeight;
            private readonly Dictionary<int, PdfFontMetadata> _fontMetadataCache = new();
            public TextOperationDescriptor? CurrentTarget { get; set; }
            private List<(int Index, byte[] Bytes)> _textOperands = new();
            private int _nextTextOperand;

            public sealed record TextOperationState(
                TextOperationDescriptor? Target,
                List<(int Index, byte[] Bytes)> TextOperands,
                int NextTextOperand);

            public ContentExtractionListener(float pageHeight)
            {
                _pageHeight = pageHeight;
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

            public ICollection<EventType> GetSupportedEvents()
            {
                return new[] { EventType.RENDER_TEXT };
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
            if (annotations.Any(annotation => annotation.TextFragments.Count > 0
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
                    var preciseTargets = annotations
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
