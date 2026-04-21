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

        public byte[]? GetPdfBytesWithEdits(Action<PdfDocument> editAction)
        {
            if (_pdfBytes == null) return null;

            lock (_docLock)
            {
                try
                {
                    using var msInput = new MemoryStream(_pdfBytes);
                    using var msOutput = new MemoryStream();

                    using (var reader = new PdfReader(msInput))
                    using (var writer = new PdfWriter(msOutput))
                    using (var doc = new PdfDocument(reader, writer))
                    {
                        editAction(doc);
                        doc.Close();
                    }

                    return msOutput.ToArray();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error getting PDF bytes with edits: {ex.Message}");
                    return null;
                }
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
            bool isBold = false, bool isItalic = false)
        {
            if (pageIndex < 0 || pageIndex >= doc.GetNumberOfPages()) return;

            var page = doc.GetPage(pageIndex + 1);
            var rect = page.GetCropBox(); 

            float pdfX = rect.GetLeft() + (float)x;
            float pdfY = rect.GetBottom() + (rect.GetHeight() - (float)y - (float)fontSize);

            PdfFont font;
            try
            {
                // GetSystemFontPath에서 인덱스가 포함된 정확한 경로(예: C:\Windows\Fonts\gulim.ttc,2)를 받아옵니다.
                string? resolvedPath = GetSystemFontPath(fontFamily, isBold);
                
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    font = PdfFontFactory.CreateFont(resolvedPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                }
                else
                {
                    // 폰트 매핑 실패 시 맑은 고딕 폴백
                    string fallbackPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", isBold ? "malgunbd.ttf" : "malgun.ttf");
                    font = PdfFontFactory.CreateFont(fallbackPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Font load error: {ex.Message}");
                font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            }

            // 3. 로우레벨(Low-level) API로 확실하게 텍스트 박아넣기
            PdfStream stream = page.NewContentStreamAfter(); 
            PdfCanvas canvas = new PdfCanvas(stream, page.GetResources(), doc);
            
            canvas.SaveState();
            canvas.BeginText();
            canvas.SetFontAndSize(font, (float)Math.Max(fontSize, 1));
            canvas.SetFillColor(color ?? ColorConstants.BLACK);

            // 텍스트 이동 및 쓰기 (에러 없이 확실하게 그려짐)
            canvas.MoveText(pdfX, pdfY);
            canvas.ShowText(text ?? string.Empty);

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
            var canvas = new PdfCanvas(page);
            
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
            using var canvasLayout = new iText.Layout.Canvas(new PdfCanvas(page), pageSize);
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
                    
                    var listener = new ContentExtractionListener(pageSize.GetHeight());
                    PdfCanvasProcessor processor = new PdfCanvasProcessor(listener);
                    processor.ProcessPageContent(page);
                    
                    contents = listener.Contents;
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
                return reportedFontSize;

            if (actualHeight > 0.1f)
                return actualHeight;

            return 12f;
        }

        private static string GetRawFontName(TextRenderInfo textInfo)
        {
            try
            {
                var fontProgram = textInfo.GetFont()?.GetFontProgram();
                var fontNames = fontProgram?.GetFontNames();
                return fontNames?.GetFontName() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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

        private static (bool isBold, bool isItalic) InferFontStyle(string rawFontName)
        {
            if (string.IsNullOrWhiteSpace(rawFontName))
                return (false, false);

            string lower = rawFontName.ToLowerInvariant();
            bool isBold = lower.Contains("bold");
            bool isItalic = lower.Contains("italic") || lower.Contains("oblique");
            return (isBold, isItalic);
        }

        private static string ColorToHex(Color? color)
        {
            if (color == null)
                return "#000000";

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

            if (color is DeviceCmyk cmyk)
            {
                var rgb = Color.ConvertCmykToRgb(cmyk);
                float[] rgbValues = rgb.GetColorValue();
                byte r = (byte)Math.Clamp((int)Math.Round(rgbValues[0] * 255), 0, 255);
                byte g = (byte)Math.Clamp((int)Math.Round(rgbValues[1] * 255), 0, 255);
                byte b = (byte)Math.Clamp((int)Math.Round(rgbValues[2] * 255), 0, 255);
                return $"#{r:X2}{g:X2}{b:X2}";
            }

            return "#000000";
        }

        private class ContentExtractionListener : IEventListener
        {
            public List<PdfPageContent> Contents { get; } = new List<PdfPageContent>();
            private readonly float _pageHeight;

            public ContentExtractionListener(float pageHeight)
            {
                _pageHeight = pageHeight;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type == EventType.RENDER_TEXT)
                {
                    var textInfo = (TextRenderInfo)data;
                    var text = textInfo.GetText();
                    if (string.IsNullOrWhiteSpace(text)) return;

                    var baseline = textInfo.GetBaseline().GetStartPoint();
                    var ascent = textInfo.GetAscentLine().GetEndPoint();
                    var descent = textInfo.GetDescentLine().GetStartPoint();

                    float x = baseline.Get(0);
                    float y = baseline.Get(1);
                    float height = ascent.Get(1) - descent.Get(1);
                    float width = ascent.Get(0) - baseline.Get(0);

                    if (width <= 0) width = text.Length * (height > 0 ? height * 0.5f : 10);
                    if (height <= 0) height = 12;

                    // [수정 포인트 1] 폰트 크기 버그 수정:
                    // GetFontSize()가 아닌, 실제 화면에 그려진 시각적 높이(height)를 폰트 크기로 사용합니다.
                    float fontSize = height;

                    // --- 폰트 이름 정제 로직 ---
                    string cleanFontName = "맑은 고딕";
                    try
                    {
                        var fontProgram = textInfo.GetFont()?.GetFontProgram();
                        var fontNames = fontProgram?.GetFontNames();
                        string? rawFontName = fontNames?.GetFontName();

                        if (!string.IsNullOrEmpty(rawFontName))
                        {
                            cleanFontName = rawFontName;
                            
                            // 1. 서브셋 프리픽스(예: ABCDEF+) 제거
                            if (cleanFontName.Contains("+"))
                                cleanFontName = cleanFontName.Substring(cleanFontName.IndexOf('+') + 1);

                            // 2. 불필요한 스타일/타입 접미사 제거 (UI가 인식할 수 있는 순수 Family Name만 남김)
                            cleanFontName = cleanFontName.Replace("-Bold", "")
                                                         .Replace("-Italic", "")
                                                         .Replace("Bold", "")
                                                         .Replace("Italic", "")
                                                         .Replace("MT", "")
                                                         .Replace("PS", "");

                            string lowerFont = cleanFontName.ToLower();

                            // 3. UI 프레임워크가 인식할 수 있는 실제 Windows 폰트명으로 강제 매핑
                            if (lowerFont.Contains("malgun")) cleanFontName = "맑은 고딕";
                            else if (lowerFont.Contains("gulim")) cleanFontName = "굴림";
                            else if (lowerFont.Contains("dotum")) cleanFontName = "돋움";
                            else if (lowerFont.Contains("batang")) cleanFontName = "바탕";
                            else if (lowerFont.Contains("gungsuh")) cleanFontName = "궁서";
                            else if (lowerFont.Contains("nanumgothic")) cleanFontName = "나눔고딕";
                            else if (lowerFont.Contains("arial")) cleanFontName = "Arial";
                            else if (lowerFont.Contains("times")) cleanFontName = "Times New Roman";
                            else if (lowerFont.Contains("helvetica")) cleanFontName = "Arial"; // PDF 표준 폰트인 Helvetica는 Arial로 대체
                            else if (lowerFont.Contains("courier")) cleanFontName = "Courier New";
                            else if (lowerFont.Contains("tahoma")) cleanFontName = "Tahoma";
                            else if (lowerFont.Contains("verdana")) cleanFontName = "Verdana";
                            else if (lowerFont.Contains("segoe")) cleanFontName = "Segoe UI";
                            else if (lowerFont.Contains("consolas")) cleanFontName = "Consolas";
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Font parsing error: {ex.Message}");
                    }

                    float convertedY = _pageHeight - y - height;

                    // [수정 포인트 2] 인접한 텍스트 병합(Grouping) 로직
                    var lastContent = Contents.LastOrDefault();
                    if (lastContent != null)
                    {
                        // 같은 줄에 있는지 판별 (Y축 위치 차이가 폰트 높이의 30% 이내면 같은 줄로 간주)
                        float yTolerance = height * 0.3f;
                        bool isSameLine = Math.Abs((float)lastContent.OriginalPdfY - y) <= yTolerance;

                        if (isSameLine)
                        {
                            // 이전 글자의 끝점과 현재 글자의 시작점 사이의 가로 간격 계산
                            float gap = x - (float)(lastContent.OriginalPdfX + lastContent.Width);
                            
                            // 간격이 폰트 크기의 1.5배 이내면 같은 문장으로 간주하고 병합
                            float xTolerance = height * 1.5f;
                            if (gap > -xTolerance && gap < xTolerance)
                            {
                                // 띄어쓰기가 필요한 정도로 떨어져 있으면 스페이스바 추가
                                bool needsSpace = gap > (height * 0.2f) && !text.StartsWith(" ") && !lastContent.Text.EndsWith(" ");
                                lastContent.Text += (needsSpace ? " " : "") + text;
                                
                                // Width 박스 확장 (현재 글자의 끝점에서 이전 문장의 시작점을 뺌)
                                lastContent.Width = (double)((x + width) - (float)lastContent.OriginalPdfX);
                                
                                // 현재 글자가 기존 글자보다 크면 Bounding Box 높이 및 폰트 크기 갱신
                                if ((double)height > lastContent.Height)
                                {
                                    lastContent.Height = (double)height;
                                    lastContent.FontSize = (double)fontSize;
                                    lastContent.Y = (double)(_pageHeight - (float)lastContent.OriginalPdfY - (float)lastContent.Height);
                                }

                                // ⭐ [여기에 추가] 병합 시 기존 폰트가 맑은 고딕이었는데 새 텍스트가 명확한 폰트면 덮어쓰기
                                if (lastContent.FontFamily == "맑은 고딕" && cleanFontName != "맑은 고딕")
                                {
                                    lastContent.FontFamily = cleanFontName;
                                }

                                return; // 병합 완료되었으므로 새 객체로 추가하지 않고 종료
                            }
                        }
                    }

                    // 병합되지 않은 새로운 문장의 시작점인 경우 리스트에 추가
                    Contents.Add(new PdfPageContent
                    {
                        Type = PageContentType.Text,
                        Text = text,
                        X = x,
                        Y = convertedY,
                        Width = width,
                        Height = height,
                        FontSize = fontSize,
                        FontFamily = cleanFontName,
                        OriginalPdfX = x,
                        OriginalPdfY = y
                    });
                }
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new[] { EventType.RENDER_TEXT };
            }
        }

        private sealed class TextOperationTarget
        {
            public int StreamIndex { get; init; }
            public int OperationIndex { get; init; }
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

        private static byte[]? RewriteContentStreamWithInvisibleText(byte[] contentBytes, IReadOnlyDictionary<int, int> targetRenderModes)
        {
            if (targetRenderModes.Count == 0)
                return null;

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
                    if (targetRenderModes.TryGetValue(textOperationIndex, out int originalRenderMode))
                    {
                        WriteOperation(output, new PdfObject[] { new PdfNumber(3), new PdfLiteral("Tr") });
                        WriteOperation(output, parsedOperation);
                        WriteOperation(output, new PdfObject[] { new PdfNumber(originalRenderMode), new PdfLiteral("Tr") });
                        modified = true;
                        continue;
                    }
                }

                WriteOperation(output, parsedOperation);
            }

            return modified ? stream.ToArray() : null;
        }

        private static bool TryHideTextOperations(PdfDocument doc, int pageIndex, IEnumerable<TextOperationTarget> targets)
        {
            var groupedTargets = targets
                .Where(t => t.StreamIndex >= 0 && t.OperationIndex >= 0)
                .GroupBy(t => t.StreamIndex)
                .ToList();

            if (groupedTargets.Count == 0)
                return false;

            var page = doc.GetPage(pageIndex + 1);
            bool modified = false;

            foreach (var group in groupedTargets)
            {
                if (group.Key >= page.GetContentStreamCount())
                    continue;

                var contentStream = page.GetContentStream(group.Key);
                if (contentStream == null)
                    continue;

                var renderModes = group
                    .GroupBy(t => t.OperationIndex)
                    .ToDictionary(g => g.Key, g => g.First().TextRenderMode);

                byte[]? rewrittenBytes = RewriteContentStreamWithInvisibleText(contentStream.GetBytes(), renderModes);
                if (rewrittenBytes == null)
                    continue;

                contentStream.SetData(rewrittenBytes);
                modified = true;
            }

            return modified;
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
            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    success = TryHideTextOperations(doc, pageIndex, new[]
                    {
                        new TextOperationTarget
                        {
                            StreamIndex = contentStreamIndex,
                            OperationIndex = operationIndex,
                            TextRenderMode = textRenderMode
                        }
                    });

                    if (!success)
                    {
                        success = RemoveTextByCleanup(doc, pageIndex, new List<(double x, double y, double w, double h)>
                        {
                            (x, y, width, height)
                        });
                    }
                });
                return success;
            });
        }

        public async Task<bool> RemoveOriginalTextAnnotationsAsync(int pageIndex, IReadOnlyCollection<PdfAnnotation> annotations)
        {
            if (annotations == null || annotations.Count == 0)
                return true;

            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    var preciseTargets = annotations
                        .Where(a => a.ContentStreamIndex >= 0 && a.OperationIndex >= 0)
                        .Select(a => new TextOperationTarget
                        {
                            StreamIndex = a.ContentStreamIndex,
                            OperationIndex = a.OperationIndex,
                            TextRenderMode = a.TextRenderMode
                        })
                        .ToList();

                    success = TryHideTextOperations(doc, pageIndex, preciseTargets);

                    var cleanupTargets = annotations
                        .Where(a => a.ContentStreamIndex < 0 || a.OperationIndex < 0)
                        .Select(a => (a.X, a.Y, a.Width, a.Height))
                        .ToList();

                    if (cleanupTargets.Count > 0)
                    {
                        success = RemoveTextByCleanup(doc, pageIndex, cleanupTargets) || success;
                    }
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

        public async Task<bool> MoveTextAsync(int pageIndex, string text, double oldX, double oldY, double width, double height, double newX, double newY, string fontFamily = "맑은 고딕", double fontSize = 12)
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
                    AddTextInternal(doc, pageIndex, newX, newY, text, fontFamily, fontSize, ColorConstants.BLACK);
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