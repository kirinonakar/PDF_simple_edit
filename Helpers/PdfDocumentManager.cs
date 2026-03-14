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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PDF_simple_edit.Models;
using System.Globalization;

namespace PDF_simple_edit.Helpers
{
    public class PdfDocumentManager
    {
        private byte[]? _pdfBytes;
        private string? _filePath;
        private bool _isModified;
        private readonly object _docLock = new();
        private readonly Dictionary<int, List<PdfPageContent>> _contentCache = new();

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

        public event EventHandler? DocumentChanged;
        public event EventHandler? ModifiedStateChanged;

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
                        
                        DocumentChanged?.Invoke(this, EventArgs.Empty);
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

            // 폰트가 구워질 PDF 절대 좌표 계산
            float pdfX = rect.GetLeft() + (float)x;
            float pdfY = rect.GetBottom() + (rect.GetHeight() - (float)y - (float)fontSize);

            // 2. 한글 출력을 위한 폰트 강제 주입 (맑은 고딕)
            PdfFont font;
            try
            {
                // 윈도우 환경에 100% 존재하는 맑은고딕 경로를 강제로 가져와서 한글 깨짐/증발 방지
                string fontPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), isBold ? "malgunbd.ttf" : "malgun.ttf");
                
                if (File.Exists(fontPath))
                {
                    // IDENTITY_H 인코딩과 PREFER_EMBEDDED 옵션을 주어야 한글이 PDF에 정상적으로 구워집니다.
                    font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                }
                else
                {
                    font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
                }
            }
            catch
            {
                font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            }

            // 3. 로우레벨(Low-level) API로 확실하게 텍스트 박아넣기
            // NewContentStreamAfter()를 호출하면 기존 모든 내용(배경 포함)의 가장 '위'에 투명 셀로판지를 얹고 글씨를 씁니다.
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
            string fontDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            
            // If it's already a full path or simple filename with extension
            if (nameOrFile.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || 
                nameOrFile.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                string path = System.IO.Path.Combine(fontDir, nameOrFile);
                if (File.Exists(path)) return path;
            }

            // Map some common names to files
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "맑은 고딕", isBold ? "malgunbd.ttf" : "malgun.ttf" },
                { "Malgun Gothic", isBold ? "malgunbd.ttf" : "malgun.ttf" },
                { "굴림", "gulim.ttc" },
                { "돋움", "dotum.ttc" },
                { "바탕", "batang.ttc" },
                { "궁서", "gungsuh.ttc" },
                { "나눔고딕", "NanumGothic.ttf" }
            };

            if (map.TryGetValue(nameOrFile, out string? filename))
            {
                string path = System.IO.Path.Combine(fontDir, filename);
                if (File.Exists(path)) return path;
            }

            return null;
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
                DocumentChanged?.Invoke(this, EventArgs.Empty);
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Close()
        {
            _pdfBytes = null;
            _filePath = null;
            _isModified = false;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
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
                    float width = textInfo.GetAscentLine().GetEndPoint().Get(0) - textInfo.GetBaseline().GetStartPoint().Get(0);

                    if (width <= 0) width = text.Length * (height > 0 ? height * 0.5f : 10);
                    if (height <= 0) height = 12;

                    Contents.Add(new PdfPageContent
                    {
                        Type = PageContentType.Text,
                        Text = text,
                        X = x,
                        Y = _pageHeight - y - height,
                        Width = width,
                        Height = height,
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

        public async Task<bool> RemoveTextAsync(int pageIndex, double x, double y, double width, double height)
        {
            return await Task.Run(() =>
            {
                bool success = false;
                ApplyEdit(doc =>
                {
                    var page = doc.GetPage(pageIndex + 1);
                    var rect = page.GetCropBox(); // 실제 보이는 영역 기준
                    
                    // PDF의 실제 원점(Left, Bottom) 오프셋을 반영해야 정확한 좌표가 나옵니다.
                    float offsetLeft = rect.GetLeft();
                    float offsetBottom = rect.GetBottom();
                    
                    // UI(Top-Left) -> PDF(Bottom-Left) 변환 + 페이지 오프셋 반영
                    float pdfX = offsetLeft + (float)x;
                    float pdfY = offsetBottom + (rect.GetHeight() - (float)y - (float)height);

                    // 삭제 영역을 텍스트 경계보다 넉넉하게 확장 (매우 중요: 미세하게 어긋나면 삭제 안 됨)
                    float padding = 2.0f; 
                    float expandedX = pdfX - padding;
                    float expandedY = pdfY - padding;
                    float expandedWidth = (float)width + (padding * 2);
                    float expandedHeight = (float)height + (padding * 2);

                    var location = new PdfCleanUpLocation(pageIndex + 1, 
                        new Rectangle(expandedX, expandedY, expandedWidth, expandedHeight), 
                        ColorConstants.WHITE);

                    // 조언에 따른 최적의 pdfSweep 실행 방식: 생성자에 위치 리스트를 직접 전달
                    var locations = new List<PdfCleanUpLocation> { location };
                    PdfCleanUpTool cleaner = new PdfCleanUpTool(doc, locations, new CleanUpProperties());
                    cleaner.CleanUp();
                    
                    success = true;
                });
                return success;
            });
        }

        public async Task<bool> MoveTextAsync(int pageIndex, string text, double oldX, double oldY, double width, double height, double newX, double newY)
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
                        ColorConstants.WHITE);
                    
                    // pdfSweep 실행 (생성자 주입 방식)
                    var locations = new List<PdfCleanUpLocation> { location };
                    PdfCleanUpTool cleaner = new PdfCleanUpTool(doc, locations, new CleanUpProperties());
                    cleaner.CleanUp();

                    // 새 위치에 텍스트 추가 (새 위치 newY는 AddTextInternal에서 자동 변환됨)
                    AddTextInternal(doc, pageIndex, newX, newY, text, "맑은 고딕", 12, ColorConstants.BLACK);
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
    }
}