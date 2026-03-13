using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfDocument = PdfSharp.Pdf.PdfDocument;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PDF_simple_edit.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using System.Globalization;

namespace PDF_simple_edit.Helpers
{
    public class PdfDocumentManager
    {
        private PdfDocument? _document;
        private string? _filePath;
        private bool _isModified;
        private readonly object _docLock = new();
        private readonly Dictionary<Guid, object> _operatorMap = new();
        
        private readonly Dictionary<int, CSequence> _pageSequenceCache = new();

        public PdfDocument? Document => _document;
        public string? FilePath => _filePath;
        public void SetFilePath(string path) => _filePath = path;
        
        public bool IsModified => _isModified;
        public void MarkModified(bool modified = true)
        {
            _isModified = modified;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }
        public int PageCount => _document?.PageCount ?? 0;
        public bool IsLoaded => _document != null;

        public event EventHandler? DocumentChanged;
        public event EventHandler? ModifiedStateChanged;

        public PdfDocumentManager()
        {
            PdfFontResolver.Initialize();
        }

        private void ClearCache()
        {
            _pageSequenceCache.Clear();
            _operatorMap.Clear();
        }

        public async Task<bool> OpenAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);
                        _document = doc;
                        _filePath = filePath;
                        _isModified = false;
                        ClearCache(); 
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
            if (_document == null || string.IsNullOrEmpty(_filePath))
                return false;
            return await SaveAsAsync(_filePath, true);
        }

        public async Task<bool> SaveAsAsync(string filePath, bool isUserSave = true)
        {
            if (_document == null) return false;

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        _document.Save(filePath);
                        var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);
                        _document = doc;
                        ClearCache();

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

        public void AddText(int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, XColor color,
            bool isBold = false, bool isItalic = false, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount) return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var style = XFontStyleEx.Regular;
            if (isBold && isItalic) style = XFontStyleEx.BoldItalic;
            else if (isBold) style = XFontStyleEx.Bold;
            else if (isItalic) style = XFontStyleEx.Italic;

            try
            {
                var font = new XFont(fontFamily, fontSize, style, new XPdfFontOptions(PdfFontEncoding.Unicode));
                var brush = new XSolidBrush(color);
                string[] lines = text.Replace("\r", "").Split('\n');
                double lineSpacing = font.GetHeight();

                for (int i = 0; i < lines.Length; i++)
                    gfx.DrawString(lines[i], font, brush, new XPoint(x, y + (i * lineSpacing)), XStringFormats.TopLeft);

                if (targetDoc == null)
                {
                    _isModified = true;
                    ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding text: {ex.Message}");
                throw;
            }
        }

        public void AddHighlight(int pageIndex, double x, double y, double width, double height,
            XColor color, double opacity = 0.3, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount) return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb((int)(opacity * 255), color)), x, y, width, height);

            if (targetDoc == null)
            {
                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void AddImage(int pageIndex, string imagePath, double x, double y,
            double width, double height, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount) return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.DrawImage(XImage.FromFile(imagePath), x, y, width, height);

            if (targetDoc == null)
            {
                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void AddStickyNote(int pageIndex, double x, double y, string noteText,
            string fontFamily = "맑은 고딕", double fontSize = 10, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount) return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            double noteWidth = 150, noteHeight = 100, padding = 8;
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(40, 0, 0, 0)), x + 3, y + 3, noteWidth, noteHeight);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 255, 255, 200)), x, y, noteWidth, noteHeight);
            gfx.DrawRectangle(XPens.DarkGoldenrod, x, y, noteWidth, noteHeight);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 255, 230, 100)), x, y, noteWidth, 20);
            gfx.DrawLine(XPens.DarkGoldenrod, x, y + 20, x + noteWidth, y + 20);

            var headerFont = new XFont(fontFamily, 8, XFontStyleEx.Bold);
            gfx.DrawString("📝 메모", headerFont, XBrushes.DarkSlateGray, new XPoint(x + padding, y + 14), XStringFormats.TopLeft);

            var font = new XFont(fontFamily, fontSize, XFontStyleEx.Regular);
            var textRect = new XRect(x + padding, y + 24, noteWidth - padding * 2, noteHeight - 28);
            new PdfSharp.Drawing.Layout.XTextFormatter(gfx).DrawString(noteText, font, XBrushes.Black, textRect);

            if (targetDoc == null)
            {
                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task<bool> MergeAsync(IEnumerable<string> filePaths)
        {
            if (_document == null) return false;
            return await Task.Run(() =>
            {
                try
                {
                    foreach (var path in filePaths)
                    {
                        using var src = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                        for (int i = 0; i < src.PageCount; i++) _document.AddPage(src.Pages[i]);
                    }
                    ClearCache();
                    _isModified = true;
                    ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch { return false; }
            });
        }

        public static async Task<bool> MergeFilesAsync(IEnumerable<string> filePaths, string outputPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var output = new PdfDocument();
                    foreach (var path in filePaths)
                    {
                        using var src = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                        for (int i = 0; i < src.PageCount; i++) output.AddPage(src.Pages[i]);
                    }
                    output.Save(outputPath);
                    return true;
                }
                catch { return false; }
            });
        }

        public async Task<bool> SplitAsync(string outputDir, int pagesPerFile = 1)
        {
            if (_document == null) return false;
            return await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                    int fileIndex = 1;
                    for (int i = 0; i < _document.PageCount; i += pagesPerFile)
                    {
                        using var newDoc = new PdfDocument();
                        for (int j = i; j < Math.Min(i + pagesPerFile, _document.PageCount); j++)
                        {
                            using var src = PdfReader.Open(_filePath!, PdfDocumentOpenMode.Import);
                            newDoc.AddPage(src.Pages[j]);
                        }
                        newDoc.Save(Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(_filePath)}_part{fileIndex++:D3}.pdf"));
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        public async Task<bool> SplitByRangesAsync(string outputDir, List<(int start, int end)> ranges)
        {
            if (_document == null || string.IsNullOrEmpty(_filePath)) return false;
            return await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                    foreach (var (start, end) in ranges)
                    {
                        using var newDoc = new PdfDocument();
                        using var src = PdfReader.Open(_filePath!, PdfDocumentOpenMode.Import);
                        for (int j = start - 1; j < end && j < src.PageCount; j++) newDoc.AddPage(src.Pages[j]);
                        newDoc.Save(Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(_filePath)}_{start}-{end}.pdf"));
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        public void DeletePage(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount) return;
            _document.Pages.RemoveAt(pageIndex);
            ClearCache();
            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void NewDocument()
        {
            _document = new PdfDocument();
            _document.AddPage();
            _filePath = null;
            _isModified = false;
            ClearCache();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Close()
        {
            _document?.Close();
            _document = null;
            _filePath = null;
            _isModified = false;
            ClearCache();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public (double width, double height) GetPageSize(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount) return (0, 0);
            return (_document.Pages[pageIndex].Width.Point, _document.Pages[pageIndex].Height.Point);
        }

        // [강화] 현존하는 디코딩 방법을 모두 시도
        private string DecodePdfString(string pdfStr)
        {
            if (string.IsNullOrEmpty(pdfStr)) return "";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                if (pdfStr.StartsWith("<") && pdfStr.EndsWith(">"))
                {
                    string hex = pdfStr.Substring(1, pdfStr.Length - 2);
                    if (hex.Length % 2 != 0) hex += "0";

                    byte[] bytes = new byte[hex.Length / 2];
                    for (int i = 0; i < bytes.Length; i++)
                        bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

                    if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                        return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                        return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

                    string cp949 = System.Text.Encoding.GetEncoding(949).GetString(bytes);
                    if (cp949.Count(c => c >= 0xAC00 && c <= 0xD7A3) > 0) return cp949;

                    if (bytes.Length % 2 == 0)
                    {
                        string utf16 = System.Text.Encoding.BigEndianUnicode.GetString(bytes);
                        if (utf16.Count(c => c >= 0xAC00 && c <= 0xD7A3) > 0) return utf16;
                    }

                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                else if (pdfStr.StartsWith("(") && pdfStr.EndsWith(")"))
                {
                    string text = pdfStr.Substring(1, pdfStr.Length - 2)
                        .Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
                    
                    byte[] bytes = text.Select(c => (byte)c).ToArray();
                    string cp949 = System.Text.Encoding.GetEncoding(949).GetString(bytes);
                    if (cp949.Count(c => c >= 0xAC00 && c <= 0xD7A3) > 0) return cp949;

                    return text;
                }
            }
            catch { }
            return pdfStr;
        }

        public UglyToad.PdfPig.Content.Page? GetPigPage(int pageNumber)
        {
            if (string.IsNullOrEmpty(_filePath)) return null;
            try
            {
                var pigDoc = UglyToad.PdfPig.PdfDocument.Open(_filePath);
                return pigDoc.GetPage(pageNumber);
            }
            catch { return null; }
        }

        public async Task<List<PdfPageContent>> ExtractPageContentsAsync(int pageIndex)
        {
            if (string.IsNullOrEmpty(_filePath) || pageIndex < 0)
                return new List<PdfPageContent>();

            return await Task.Run(() =>
            {
                var contents = new List<PdfPageContent>();
                try
                {
                    using (var pigDoc = UglyToad.PdfPig.PdfDocument.Open(_filePath))
                    {
                        var page = pigDoc.GetPage(pageIndex + 1);
                        double pageHeight = page.Height;

                        // 1. 텍스트 추출 (Words 단위)
                        foreach (var word in page.GetWords())
                        {
                            // CoordinateMapper를 사용하여 UI 좌표계로 변환
                            double uiY = CoordinateMapper.MapToUiY(word.BoundingBox.Top, pageHeight);

                            contents.Add(new PdfPageContent
                            {
                                Type = PageContentType.Text,
                                X = word.BoundingBox.Left,
                                Y = uiY,
                                Width = word.BoundingBox.Width,
                                Height = word.BoundingBox.Height,
                                Text = word.Text,
                                OriginalPdfX = word.BoundingBox.Left,
                                OriginalPdfY = word.BoundingBox.Bottom // PDF 표준 좌표 (Bottom 기준)
                            });
                        }

                        // 2. 이미지 추출
                        foreach (var image in page.GetImages())
                        {
                            string? tempImagePath = null;
                            try
                            {
                                if (image.TryGetPng(out byte[]? pngBytes) && pngBytes != null)
                                {
                                    string tempPath = Path.Combine(Path.GetTempPath(), $"pdf_img_{Guid.NewGuid()}.png");
                                    File.WriteAllBytes(tempPath, pngBytes);
                                    tempImagePath = tempPath;
                                }
                            }
                            catch { }

                            contents.Add(new PdfPageContent
                            {
                                Type = PageContentType.Image,
                                X = image.Bounds.Left,
                                Y = CoordinateMapper.MapToUiY(image.Bounds.Top, pageHeight),
                                Width = image.Bounds.Width,
                                Height = image.Bounds.Height,
                                OriginalPdfX = image.Bounds.Left,
                                OriginalPdfY = image.Bounds.Bottom,
                                ImageId = image.ToString(), 
                                Text = tempImagePath ?? "" 
                            });
                        }
                    }

                    // [한글/매핑 보완] PdfSharp의 Operator와 매핑하기 위해 
                    // 기존 ExtractTextObjectsAsync 로직의 OperatorId를 가져와야 할 수 있음.
                    // 간단한 구현을 위해 여기서는 PdfPig 데이터만 반환하고, 
                    // 필요시 MainWindow에서 매칭 로직을 수행하도록 함.
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting page contents: {ex.Message}");
                }
                return contents;
            });
        }

        public async Task<List<SearchResult>> ExtractTextObjectsAsync(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return new List<SearchResult>();

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    var results = new List<SearchResult>();
                    try
                    {
                        var page = _document.Pages[pageIndex];
                        if (!_pageSequenceCache.TryGetValue(pageIndex, out var sequence))
                        {
                            sequence = ContentReader.ReadContent(page);
                            _pageSequenceCache[pageIndex] = sequence;
                        }

                        // [한글 해결] PdfPig를 사용하여 정확한 유니코드 텍스트 추출
                        List<UglyToad.PdfPig.Content.Word>? pigWords = null;
                        if (!string.IsNullOrEmpty(_filePath))
                        {
                            try
                            {
                                using var pigDoc = UglyToad.PdfPig.PdfDocument.Open(_filePath);
                                var pigPage = pigDoc.GetPage(pageIndex + 1);
                                pigWords = pigPage.GetWords().ToList();
                            }
                            catch { }
                        }
                        
                        var textState = new PdfTextState(page.Height.Point);
                        ProcessContent(sequence, textState, results, pigWords);
                    }
                    catch { }
                    return results;
                }
            });
        }

        private void ProcessContent(CSequence sequence, PdfTextState state, List<SearchResult> results, List<UglyToad.PdfPig.Content.Word>? pigWords)
        {
            foreach (var obj in sequence)
            {
                if (obj is COperator op)
                {
                    switch (op.Name)
                    {
                        case "BT": state.InTextObject = true; state.TextMatrix = state.LineMatrix = new XMatrix(); break;
                        case "ET": state.InTextObject = false; break;
                        case "Tf": 
                            if (op.Operands.Count >= 2) 
                                state.FontSize = (op.Operands[1] is CReal || op.Operands[1] is CInteger) ? double.Parse(op.Operands[1].ToString() ?? "12") : 12;
                            break;
                        case "Tm":
                            if (op.Operands.Count >= 6)
                                state.TextMatrix = state.LineMatrix = new XMatrix(
                                    double.Parse(op.Operands[0].ToString() ?? "0"), double.Parse(op.Operands[1].ToString() ?? "0"),
                                    double.Parse(op.Operands[2].ToString() ?? "0"), double.Parse(op.Operands[3].ToString() ?? "0"),
                                    double.Parse(op.Operands[4].ToString() ?? "0"), double.Parse(op.Operands[5].ToString() ?? "0"));
                            break;
                        case "Td":
                        case "TD":
                            if (op.Operands.Count >= 2)
                            {
                                var m = new XMatrix(); 
                                m.TranslateAppend(double.Parse(op.Operands[0].ToString() ?? "0"), double.Parse(op.Operands[1].ToString() ?? "0"));
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                            }
                            break;
                        case "T*":
                            var mLine = new XMatrix(); mLine.TranslateAppend(0, -state.FontSize * 1.2);
                            state.LineMatrix *= mLine;
                            state.TextMatrix = state.LineMatrix;
                            break;
                        case "Tj":
                            if (op.Operands.Count >= 1) AddTextToResultsWithOp(DecodePdfString(op.Operands[0].ToString() ?? ""), state, results, op, pigWords);
                            break;
                        case "TJ":
                            if (op.Operands.Count >= 1 && op.Operands[0] is CArray array)
                            {
                                var sb = new System.Text.StringBuilder();
                                foreach (var item in array) if (item is CString) sb.Append(DecodePdfString(item.ToString() ?? ""));
                                AddTextToResultsWithOp(sb.ToString(), state, results, op, pigWords);
                            }
                            break;
                        case "'":
                            var mQuote = new XMatrix(); mQuote.TranslateAppend(0, -state.FontSize * 1.2);
                            state.LineMatrix *= mQuote;
                            state.TextMatrix = state.LineMatrix;
                            if (op.Operands.Count >= 1) AddTextToResultsWithOp(DecodePdfString(op.Operands[0].ToString() ?? ""), state, results, op, pigWords);
                            break;
                        case "\"":
                            var mDQuote = new XMatrix(); mDQuote.TranslateAppend(0, -state.FontSize * 1.2);
                            state.LineMatrix *= mDQuote;
                            state.TextMatrix = state.LineMatrix;
                            if (op.Operands.Count >= 3) AddTextToResultsWithOp(DecodePdfString(op.Operands[2].ToString() ?? ""), state, results, op, pigWords);
                            break;
                    }
                }
                else if (obj is CSequence inner)
                {
                    ProcessContent(inner, state, results, pigWords);
                }
            }
        }

        private void AddTextToResultsWithOp(string? text, PdfTextState state, List<SearchResult> results, object op, List<UglyToad.PdfPig.Content.Word>? pigWords)
        {
            if (string.IsNullOrEmpty(text)) return;

            double pdfX = state.TextMatrix.OffsetX;
            double pdfY = state.TextMatrix.OffsetY;
            double height = state.FontSize;
            double uiY = CoordinateMapper.MapToUiY(pdfY + height, state.PageHeight); // Top 기준 UI Y

            // [한글 해결] PdfPig에서 추출한 진짜 텍스트가 있다면 그것을 사용
            string bestText = text;
            if (pigWords != null)
            {
                // PDF 좌표계 (Bottom-Up) 기준으로 매칭 시도
                var match = pigWords.FirstOrDefault(w => 
                    Math.Abs(w.BoundingBox.Left - pdfX) < 5 && 
                    Math.Abs(w.BoundingBox.Bottom - pdfY) < 15);
                
                if (match != null)
                    bestText = match.Text;
            }

            var id = Guid.NewGuid();
            _operatorMap[id] = op;
            
            double width = bestText.Length * state.FontSize * 0.5; 
            if (bestText.Any(c => c > 255)) width = bestText.Length * state.FontSize * 0.9; 

            results.Add(new SearchResult
            {
                FoundText = bestText,
                X = pdfX,
                Y = uiY,
                Width = width,
                Height = height,
                PageIndex = 0, 
                OperatorId = id,
                OriginalPdfX = pdfX,
                OriginalPdfY = pdfY
            });

            var m = new XMatrix(); m.TranslateAppend(width, 0);
            state.TextMatrix *= m;
        }

        // [핵심 해결] 라이브러리 버그 우회: PDF 스트림 수동 직렬화기
        // MemoryStream 기반으로 변경하여 한글 등 비ASCII 문자를 UTF-16 BE hex 형식으로 안전하게 직렬화
        private byte[] SerializeContent(CSequence sequence)
        {
            using var ms = new MemoryStream();

            void WriteAscii(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                var bytes = System.Text.Encoding.Latin1.GetBytes(s);
                ms.Write(bytes, 0, bytes.Length);
            }

            void WriteObj(CObject obj)
            {
                if (obj is COperator op)
                {
                    foreach (var opnd in op.Operands)
                    {
                        WriteObj(opnd);
                        ms.WriteByte((byte)' ');
                    }
                    WriteAscii(op.Name);
                    ms.WriteByte((byte)'\n');
                }
                else if (obj is CSequence seq)
                {
                    foreach (var child in seq) WriteObj(child);
                }
                else if (obj is CString str)
                {
                    // [한글 깨짐 수정] 비ASCII 문자가 있으면 UTF-16 BE hex 형식으로 직렬화
                    string val = str.Value ?? "";
                    bool hasNonAscii = val.Any(c => c > 127);

                    if (hasNonAscii)
                    {
                        // <FEFF xxxx ...> 형식으로 직렬화 (PDF UTF-16 BE 표준)
                        byte[] bom = { 0xFE, 0xFF };
                        byte[] content = System.Text.Encoding.BigEndianUnicode.GetBytes(val);
                        ms.WriteByte((byte)'<');
                        foreach (byte b in bom)   WriteAscii(b.ToString("X2"));
                        foreach (byte b in content) WriteAscii(b.ToString("X2"));
                        ms.WriteByte((byte)'>');
                    }
                    else
                    {
                        // ASCII 범위: 괄호 형식으로 직렬화, 특수문자 이스케이프
                        ms.WriteByte((byte)'(');
                        foreach (char c in val)
                        {
                            byte b = (byte)(c & 0xFF);
                            if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
                                ms.WriteByte((byte)'\\');
                            ms.WriteByte(b);
                        }
                        ms.WriteByte((byte)')');
                    }
                }
                else if (obj is CArray arr)
                {
                    ms.WriteByte((byte)'[');
                    for (int i = 0; i < arr.Count; i++)
                    {
                        WriteObj(arr[i]);
                        if (i < arr.Count - 1) ms.WriteByte((byte)' ');
                    }
                    ms.WriteByte((byte)']');
                }
                else
                {
                    // CNumber, CName 등: ToString()은 ASCII 범위이므로 Latin1 safe
                    WriteAscii(obj.ToString() ?? "");
                }
            }

            WriteObj(sequence);
            return ms.ToArray();
        }

        public async Task<bool> RemoveImageAsync(int pageIndex, string imageName)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount || string.IsNullOrEmpty(imageName)) return false;

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        var page = _document.Pages[pageIndex];
                        if (!_pageSequenceCache.TryGetValue(pageIndex, out var sequence))
                        {
                            sequence = ContentReader.ReadContent(page);
                            _pageSequenceCache[pageIndex] = sequence;
                        }

                        // imageName은 보통 "Im1" 형식이지만 PDF 스트림에는 "/Im1"으로 기록됨
                        string targetName = imageName.StartsWith("/") ? imageName : "/" + imageName;
                        bool found = RemoveImageFromSequence(sequence, targetName);

                        if (found)
                        {
                            byte[] newData = SerializeContent(sequence);
                            ApplyForceOverwrite(page, newData);
                            
                            _isModified = true;
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                            DocumentChanged?.Invoke(this, EventArgs.Empty);
                            return true;
                        }
                    }
                    catch { }
                    return false;
                }
            });
        }

        private bool RemoveImageFromSequence(CSequence sequence, string imageName)
        {
            bool removed = false;
            for (int i = 0; i < sequence.Count; i++)
            {
                var obj = sequence[i];
                if (obj is COperator op && op.Name == "Do" && op.Operands.Count > 0)
                {
                    if (op.Operands[0].ToString() == imageName)
                    {
                        // 이미지 출력 명령 제거
                        sequence.RemoveAt(i);
                        removed = true;
                        i--; // 인덱스 조정
                    }
                }
                else if (obj is CSequence inner)
                {
                    if (RemoveImageFromSequence(inner, imageName)) removed = true;
                }
            }
            return removed;
        }

        public async Task<bool> RemoveTextAsync(int pageIndex, double x, double y, string text, Guid? operatorId = null)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount) return false;

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        var page = _document.Pages[pageIndex];
                        if (!_pageSequenceCache.TryGetValue(pageIndex, out var sequence))
                        {
                            sequence = ContentReader.ReadContent(page);
                            _pageSequenceCache[pageIndex] = sequence;
                        }

                        bool anyRemoved = false;
                        if (operatorId.HasValue && _operatorMap.TryGetValue(operatorId.Value, out var targetOp))
                        {
                            // [핵심 문제 2 해결] Rendering Mode 변경 방식(3 Tr)이 아닌, 컨텐츠 스트림 오퍼레이터 완전 제거 또는 공백 치환
                            anyRemoved = ReplaceOperatorInSequence(sequence, targetOp);
                        }

                        if (!anyRemoved)
                        {
                            // 좌표 및 텍스트 매칭 기반으로 찾아서 삭제
                            anyRemoved = RemoveMatchingTextFromSequence(sequence, new PdfTextState(page.Height.Point), x, y, text);
                        }

                        if (anyRemoved)
                        {
                            // [핵심 해결] 수동 직렬화 후 강제 덮어쓰기
                            byte[] newData = SerializeContent(sequence);
                            ApplyForceOverwrite(page, newData);
                            
                            _isModified = true;
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                            DocumentChanged?.Invoke(this, EventArgs.Empty);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error removing text: {ex.Message}");
                    }
                    return false;
                }
            });
        }

        /// <summary>
        /// 시퀀스 내에서 타겟 오퍼레이터를 찾아 완전히 제거하거나 텍스트를 비웁니다.
        /// </summary>
        private bool ReplaceOperatorInSequence(CSequence sequence, object targetOp)
        {
            for (int i = 0; i < sequence.Count; i++)
            {
                if (object.ReferenceEquals(sequence[i], targetOp))
                {
                    if (sequence[i] is COperator op)
                    {
                        // [True Text Replacement] 텍스트 데이터를 빈 값으로 만들어 시각적/데이터적으로 완전 제거
                        ReplaceWithEmptyString(op);
                        return true;
                    }
                }
                if (sequence[i] is CSequence innerSeq)
                {
                    if (ReplaceOperatorInSequence(innerSeq, targetOp)) return true;
                }
            }
            return false;
        }

// [수정됨] 새 인스턴스를 만들지 않고, 기존 CString 인스턴스의 값 자체(Value)를 조작하여 강제 적용
        private void ReplaceWithEmptyString(COperator? op)
        {
            if (op == null) return;
            switch (op.Name)
            {
                case "Tj":
                case "'":
                case "\"":
                    int textIdx = (op.Name == "\"") ? 2 : 0;
                    if (op.Operands.Count > textIdx)
                    {
                        var s = new CString();
                        s.Value = "";
                        op.Operands[textIdx] = s; 
                    }
                    break;
                case "TJ":
                    if (op.Operands.Count >= 1)
                    {
                        var emptyArray = new CArray();
                        var s = new CString();
                        s.Value = "";
                        emptyArray.Add(s);
                        op.Operands[0] = emptyArray; 
                    }
                    break;
            }
        }

        private void ApplyForceOverwrite(PdfPage page, byte[] newData)
        {
            var document = page.Owner;
            
            // 완전히 새로운 PDF 스트림 딕셔너리를 생성합니다.
            var newContent = new PdfDictionary(document);
            newContent.Elements.SetInteger("/Length", newData.Length);
            newContent.CreateStream(newData);
            
            // 문서의 내부 오브젝트 테이블에 새 스트림을 정식으로 등록합니다.
            document.Internals.AddObject(newContent);
            
            // 페이지의 Contents를 새 스트림으로 강제 지정합니다.
            page.Elements["/Contents"] = newContent.Reference;

            // 캐시와 충돌하지 않도록 해당 페이지의 시퀀스 캐시를 날립니다.
            int pageIndex = -1;
            for (int i = 0; i < document.Pages.Count; i++)
            {
                if (document.Pages[i] == page)
                {
                    pageIndex = i;
                    break;
                }
            }

            if (pageIndex >= 0 && _pageSequenceCache.ContainsKey(pageIndex))
            {
                _pageSequenceCache.Remove(pageIndex);
            }
        }

        private bool RemoveSpecificOperatorFromSequence(CSequence sequence, object targetOp)
        {
            for (int i = 0; i < sequence.Count; i++)
            {
                if (object.ReferenceEquals(sequence[i], targetOp))
                {
                    sequence.RemoveAt(i);
                    return true;
                }
                if (sequence[i] is CSequence inner)
                {
                    if (RemoveSpecificOperatorFromSequence(inner, targetOp)) return true;
                }
            }
            return false;
        }

        private bool RemoveMatchingTextFromSequence(CSequence sequence, PdfTextState state, double targetPdfX, double targetPdfY, string targetText)
        {
            bool removed = false;
            for (int i = 0; i < sequence.Count; i++)
            {
                var obj = sequence[i];
                if (obj is COperator op)
                {
                    switch (op.Name)
                    {
                        case "BT": state.InTextObject = true; state.TextMatrix = state.LineMatrix = new XMatrix(); break;
                        case "ET": state.InTextObject = false; break;
                        case "Tf": 
                            if (op.Operands.Count >= 2) state.FontSize = (op.Operands[1] is CReal || op.Operands[1] is CInteger) ? double.Parse(op.Operands[1].ToString() ?? "12") : 12;
                            break;
                        case "Tm":
                            if (op.Operands.Count >= 6)
                                state.TextMatrix = state.LineMatrix = new XMatrix(
                                    double.Parse(op.Operands[0].ToString() ?? "0"), double.Parse(op.Operands[1].ToString() ?? "0"),
                                    double.Parse(op.Operands[2].ToString() ?? "0"), double.Parse(op.Operands[3].ToString() ?? "0"),
                                    double.Parse(op.Operands[4].ToString() ?? "0"), double.Parse(op.Operands[5].ToString() ?? "0"));
                            break;
                        case "Td":
                        case "TD":
                            if (op.Operands.Count >= 2)
                            {
                                var m = new XMatrix(); m.TranslateAppend(double.Parse(op.Operands[0].ToString() ?? "0"), double.Parse(op.Operands[1].ToString() ?? "0"));
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                            }
                            break;
                        case "T*":
                            var mLine = new XMatrix(); mLine.TranslateAppend(0, -state.FontSize * 1.2);
                            state.LineMatrix *= mLine;
                            state.TextMatrix = state.LineMatrix;
                            break;
                        case "Tj":
                        case "TJ":
                        case "'":
                        case "\"":
                            if (op.Name == "'" || op.Name == "\"")
                            {
                                var mQuote = new XMatrix(); mQuote.TranslateAppend(0, -state.FontSize * 1.2);
                                state.LineMatrix *= mQuote;
                                state.TextMatrix = state.LineMatrix;
                            }

                            string rawText = "";
                            if (op.Name == "TJ")
                            {
                                var sb = new System.Text.StringBuilder();
                                if (op.Operands.Count >= 1 && op.Operands[0] is CArray array)
                                    foreach (var item in array) if (item is CString) sb.Append(DecodePdfString(item.ToString() ?? ""));
                                rawText = sb.ToString();
                            }
                            else
                            {
                                int textIdx = (op.Name == "\"") ? 2 : 0;
                                string txt = op.Operands.Count > textIdx ? op.Operands[textIdx].ToString() ?? "" : "";
                                rawText = DecodePdfString(txt);
                            }

                            if (!string.IsNullOrEmpty(rawText))
                            {
                                double pdfX = state.TextMatrix.OffsetX;
                                double pdfY = state.TextMatrix.OffsetY;

                                // [핵심 해결] PDF 좌표계(Bottom-Up) 기준으로 직접 비교하여 오차 최소화
                                bool posMatch = Math.Abs(pdfX - targetPdfX) < 10 && Math.Abs(pdfY - targetPdfY) < 10;
                                string cleanTargetText = targetText.Trim();
                                bool textMatch = !string.IsNullOrEmpty(cleanTargetText) && 
                                               (rawText.Contains(cleanTargetText) || cleanTargetText.Contains(rawText));

                                if (posMatch || textMatch)
                                {
                                    // [True Text Replacement] 텍스트 완전 제거
                                    ReplaceWithEmptyString(op);
                                    removed = true;
                                }

                                double width = rawText.Length * state.FontSize * 0.5;
                                if (rawText.Any(c => c > 255)) width = rawText.Length * state.FontSize * 0.9;
                                
                                var m = new XMatrix(); m.TranslateAppend(width, 0);
                                state.TextMatrix *= m;
                            }
                            break;
                    }
                }
                else if (obj is CSequence inner)
                {
                    if (RemoveMatchingTextFromSequence(inner, state, targetPdfX, targetPdfY, targetText)) removed = true;
                }
            }
            return removed;
        }

  /// <summary>
        /// 텍스트를 새 위치로 이동합니다.
        /// </summary>
        public async Task<bool> MoveTextAsync(int pageIndex, Guid operatorId, double newUIX, double newUIY)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount) return false;

            return await Task.Run(() =>
            {
                lock (_docLock)
                {
                    try
                    {
                        var page = _document.Pages[pageIndex];
                        if (!_pageSequenceCache.TryGetValue(pageIndex, out var sequence))
                        {
                            sequence = ContentReader.ReadContent(page);
                            _pageSequenceCache[pageIndex] = sequence;
                        }

                        if (!_operatorMap.TryGetValue(operatorId, out var targetOp)) return false;

                        double pageHeight = page.Height.Point;

                        // BT..ET 블록에서 타겟 op를 찾아 새 위치에 복제하고 원본은 비움
                        var newOp = UpdatePositionInSequence(sequence, targetOp, newUIX, newUIY, pageHeight);

                        if (newOp != null)
                        {
                            // [수정됨] 다음번 이동을 위해 맵을 새 오퍼레이터로 업데이트
                            _operatorMap[operatorId] = newOp;

                            // [추가된 핵심 방어 로직] 
                            // 0.1pt 차이로 밑에 깔려있는 유령 텍스트(Simulated Bold) 색출 및 파괴
                            if (targetOp is COperator op && op.Operands.Count > 0)
                            {
                                string targetRawText = "";
                                if (op.Name == "Tj" || op.Name == "'" || op.Name == "\"")
                                    targetRawText = op.Operands[op.Name == "\"" ? 2 : 0].ToString() ?? "";
                                else if (op.Name == "TJ" && op.Operands[0] is CArray arr)
                                    targetRawText = string.Join("", arr.Select(o => o.ToString()));

                                // 좌표 기반 잔여물 싹쓸이 모드 가동 (반경 5 좌표 이내의 동일 텍스트 모두 비우기)
                                // ExtractTextObjectsAsync에서 추출할 때의 state를 기반으로 해야 정확하지만,
                                // 텍스트 내용 자체가 완벽히 일치한다면 RemoveMatchingTextFromSequence의 fallback으로 제거를 유도합니다.
                                RemoveMatchingTextFromSequence(sequence, new PdfTextState(pageHeight), 0, 0, targetRawText); 
                            }

                            byte[] newData = SerializeContent(sequence);
                            ApplyForceOverwrite(page, newData);

                            _isModified = true;
                            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                            DocumentChanged?.Invoke(this, EventArgs.Empty);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error moving text: {ex.Message}");
                    }
                    return false;
                }
            });
        }

/// <summary>
        /// sequence를 순회하며 targetOp를 포함하는 BT..ET 블록을 찾고, 
        /// 새 위치에 텍스트를 복제하여 추가한 뒤, 원본 텍스트 오퍼레이터는 완전히 삭제합니다.
        /// 성공 시 복제된 오퍼레이터를 반환합니다.
        /// </summary>
        private COperator? UpdatePositionInSequence(CSequence sequence, object targetOp, double newUIX, double newUIY, double pageHeight)
        {
            for (int i = 0; i < sequence.Count; i++)
            {
                var obj = sequence[i];

                if (obj is COperator op && op.Name == "BT")
                {
                    double blockFontSize = 12;
                    bool foundTarget = false;
                    int etIndex = -1;
                    COperator? targetOperator = null;

                    for (int j = i + 1; j < sequence.Count; j++)
                    {
                        if (sequence[j] is COperator inner)
                        {
                            // ET 위치 파악
                            if (inner.Name == "ET") { etIndex = j; break; }

                            if (inner.Name == "Tf" && inner.Operands.Count >= 2)
                                double.TryParse(inner.Operands[1].ToString(), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out blockFontSize);

                            if (object.ReferenceEquals(sequence[j], targetOp))
                            {
                                foundTarget = true;
                                targetOperator = inner;
                            }
                        }
                        else if (sequence[j] is CSequence innerSeq)
                        {
                            var found = FindOperatorRef(innerSeq, targetOp);
                            if (found != null)
                            {
                                foundTarget = true;
                                targetOperator = found;
                            }
                        }
                    }

                    if (foundTarget && targetOperator != null && etIndex != -1)
                    {
                        double pdfNewX = newUIX;
                        double pdfNewY = CoordinateMapper.MapToPdfY(newUIY + blockFontSize, pageHeight);

                        // 1. 타겟 오퍼레이터(Tj, TJ 등) 복제본 생성
                        var clonedOp = CloneOperator(targetOperator);

                        // 2. 새 위치를 지정할 새로운 Tm 오퍼레이터 생성
                        var newTm = PdfSharp.Pdf.Content.Objects.OpCodes.OperatorFromName("Tm");
                        newTm.Operands.Add(new CReal { Value = 1 });
                        newTm.Operands.Add(new CReal { Value = 0 });
                        newTm.Operands.Add(new CReal { Value = 0 });
                        newTm.Operands.Add(new CReal { Value = 1 });
                        newTm.Operands.Add(new CReal { Value = pdfNewX });
                        newTm.Operands.Add(new CReal { Value = pdfNewY });

                        // 3. ET(블록 종료) 바로 앞에 새 좌표(Tm)와 복제한 텍스트 삽입
                        sequence.Insert(etIndex, newTm);
                        sequence.Insert(etIndex + 1, clonedOp);

                        // 4. [완전 해결] 기존 텐스트를 투명하게 숨기는 것이 아니라, 연산자 내용을 빈 값으로 치환하여 Ghost Text 방지
                        ReplaceWithEmptyString(targetOperator); 

                        return clonedOp;
                    }

                    if (etIndex != -1) i = etIndex;
                }
                else if (obj is CSequence innerSeq)
                {
                    var result = UpdatePositionInSequence(innerSeq, targetOp, newUIX, newUIY, pageHeight);
                    if (result != null) return result;
                }
            }
            return null;
        }

        // 기존 ContainsOperatorRef를 대체하여 실제 Operator 객체를 반환하도록 수정
        private COperator? FindOperatorRef(CSequence sequence, object targetOp)
        {
            foreach (var obj in sequence)
            {
                if (object.ReferenceEquals(obj, targetOp)) return obj as COperator;
                if (obj is CSequence inner)
                {
                    var result = FindOperatorRef(inner, targetOp);
                    if (result != null) return result;
                }
            }
            return null;
        }

        // 텍스트 오퍼레이터(Tj, TJ)를 안전하게 깊은 복사(Deep Copy)하는 헬퍼 메서드
        private COperator CloneOperator(COperator op)
        {
            var clone = PdfSharp.Pdf.Content.Objects.OpCodes.OperatorFromName(op.Name);
            foreach (var operand in op.Operands)
            {
                if (operand is CString str)
                {
                    clone.Operands.Add(new CString { Value = str.Value });
                }
                else if (operand is CArray arr)
                {
                    var newArr = new CArray();
                    foreach (var item in arr)
                    {
                        if (item is CString s) newArr.Add(new CString { Value = s.Value });
                        else if (item is CReal r) newArr.Add(new CReal { Value = r.Value });
                        else if (item is CInteger i) newArr.Add(new CInteger { Value = i.Value });
                        else newArr.Add(item); // Fallback
                    }
                    clone.Operands.Add(newArr);
                }
                else if (operand is CReal r) clone.Operands.Add(new CReal { Value = r.Value });
                else if (operand is CInteger i) clone.Operands.Add(new CInteger { Value = i.Value });
                else clone.Operands.Add(operand); // Fallback
            }
            return clone;
        }


        private void SetCOperandValue(CSequence operands, int index, double value)
        {
            if (index < 0 || index >= operands.Count) return;
            if (operands[index] is CReal real)
                real.Value = value;
            else
            {
                // CInteger 등 다른 숫자 타입은 CReal로 교체
                operands.RemoveAt(index);
                operands.Insert(index, new CReal { Value = value });
            }
        }

        private class PdfTextState
        {
            public double PageHeight { get; }
            public bool InTextObject { get; set; }
            public XMatrix TextMatrix { get; set; } = new XMatrix();
            public XMatrix LineMatrix { get; set; } = new XMatrix();
            public double FontSize { get; set; } = 12;

            public PdfTextState(double pageHeight)
            {
                this.PageHeight = pageHeight;
            }
        }
    }
}