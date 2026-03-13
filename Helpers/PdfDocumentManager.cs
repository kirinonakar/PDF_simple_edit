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
            double uiY = state.PageHeight - pdfY - height;

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
                OperatorId = id
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

                        object? targetOp = null;
                        if (operatorId.HasValue) _operatorMap.TryGetValue(operatorId.Value, out targetOp);

                        bool anyRemoved = false;
                        if (targetOp != null)
                        {
                            ReplaceWithEmptyString(targetOp as COperator);
                            anyRemoved = true;
                        }

                        if (!anyRemoved)
                            anyRemoved = RemoveAllTextFromSequence(sequence, new PdfTextState(page.Height.Point), x, y, text);

                        if (anyRemoved)
                        {
                            // [핵심 해결] 강제 스트림 덮어쓰기
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
                        op.Operands[textIdx] = new CString { Value = "" };
                    break;
                case "TJ":
                    if (op.Operands.Count >= 1 && op.Operands[0] is CArray array)
                        array.Clear();
                    break;
            }
        }

        private void ApplyForceOverwrite(PdfPage page, byte[] newData)
        {
            // CreateSingleContent는 기존 컨텐츠를 하나로 합치고 스트림을 제공함
            var content = page.Contents.CreateSingleContent();
            content.Stream.Value = newData;
            
            // 압축 필터 제거 및 길이 업데이트
            content.Elements.Remove("/Filter");
            content.Elements.SetInteger("/Length", newData.Length);
            
            // [핵심] 페이지의 /Contents를 해당 객체로 강제 재지정하여 캐싱 무시
            page.Elements["/Contents"] = content.Reference;
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

        private bool RemoveAllTextFromSequence(CSequence sequence, PdfTextState state, double targetX, double targetY, string targetText)
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
                                double uiY = state.PageHeight - pdfY - state.FontSize;

                                bool posMatch = Math.Abs(pdfX - targetX) < 40 && Math.Abs(uiY - targetY) < 50;
                                string cleanTargetText = targetText.Trim();
                                bool textMatch = !string.IsNullOrEmpty(cleanTargetText) && 
                                               (rawText.Contains(cleanTargetText) || cleanTargetText.Contains(rawText));

                                bool veryCloseMatch = Math.Abs(pdfX - targetX) < 10 && Math.Abs(uiY - targetY) < 15;

                                if ((posMatch && textMatch) || veryCloseMatch || (posMatch && rawText.Length == 0))
                                {
                                    ReplaceWithEmptyString(op);
                                    removed = true;
                                    continue; 
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
                    if (RemoveAllTextFromSequence(inner, state, targetX, targetY, targetText)) removed = true;
                }
            }
            return removed;
        }

        /// <summary>
        /// [이동 수정] 텍스트를 새 위치로 이동합니다.
        /// 원본 operator의 Tm 행렬을 직접 수정하여 복사 없이 이동합니다.
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

                        // BT..ET 블록에서 타겟 op를 포함하는 Tm을 찾아 새 좌표로 수정
                        bool moved = UpdatePositionInSequence(sequence, targetOp, newUIX, newUIY, pageHeight);

                        if (moved)
                        {
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
        /// 원래 텍스트를 빈 문자열로 만든 뒤 새 위치에 텍스트를 복제하여 추가합니다.
        /// </summary>
        private bool UpdatePositionInSequence(CSequence sequence, object targetOp, double newUIX, double newUIY, double pageHeight)
        {
            for (int i = 0; i < sequence.Count; i++)
            {
                var obj = sequence[i];

                if (obj is COperator op && op.Name == "BT")
                {
                    // BT..ET 블록 범위 추출
                    double blockFontSize = 12;
                    bool foundTarget = false;
                    int etIndex = i;
                    COperator? targetOperator = null;

                    for (int j = i + 1; j < sequence.Count; j++)
                    {
                        if (sequence[j] is COperator inner)
                        {
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

                    if (foundTarget && targetOperator != null)
                    {
                        // UI Y → PDF Y 변환: pdfY = pageHeight - uiY - fontSize
                        double pdfNewX = newUIX;
                        double pdfNewY = pageHeight - newUIY - blockFontSize;

                        // 1. 타겟 오퍼레이터(Tj, TJ 등) 복제하여 새 텍스트 객체 생성
                        var clonedOp = CloneOperator(targetOperator);

                        // 2. 원래 오퍼레이터의 텍스트를 빈 문자열로 변경 (사용자 제안 반영: 잔상/복사 방지)
                        ReplaceWithEmptyString(targetOperator);

                        // 3. 새 위치를 지정할 새로운 Tm 오퍼레이터 생성
                        var newTm = PdfSharp.Pdf.Content.Objects.OpCodes.OperatorFromName("Tm");
                        newTm.Operands.Add(new CReal { Value = 1 });
                        newTm.Operands.Add(new CReal { Value = 0 });
                        newTm.Operands.Add(new CReal { Value = 0 });
                        newTm.Operands.Add(new CReal { Value = 1 });
                        newTm.Operands.Add(new CReal { Value = pdfNewX });
                        newTm.Operands.Add(new CReal { Value = pdfNewY });

                        // 4. ET(블록 종료) 바로 앞에 새 좌표(Tm)와 복제한 텍스트(Tj) 삽입
                        sequence.Insert(etIndex, newTm);
                        sequence.Insert(etIndex + 1, clonedOp);

                        return true;
                    }

                    i = etIndex; // BT..ET 블록 건너뜀
                }
                else if (obj is CSequence innerSeq)
                {
                    if (UpdatePositionInSequence(innerSeq, targetOp, newUIX, newUIY, pageHeight))
                        return true;
                }
            }
            return false;
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