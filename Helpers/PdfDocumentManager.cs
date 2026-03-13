using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Helpers
{
    /// <summary>
    /// Manages PDF document operations: open, save, merge, split, text operations.
    /// </summary>
    public class PdfDocumentManager
    {
        private PdfDocument? _document;
        private string? _filePath;
        private bool _isModified;

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

        /// <summary>
        /// Opens a PDF file from the given path.
        /// </summary>
        public async Task<bool> OpenAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);
                    _document = doc;
                    _filePath = filePath;
                    _isModified = false;
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                    ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error opening PDF: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Saves the current document to the original file path.
        /// </summary>
        public async Task<bool> SaveAsync()
        {
            if (_document == null || string.IsNullOrEmpty(_filePath))
                return false;

            return await SaveAsAsync(_filePath, true);
        }

        /// <summary>
        /// Saves the current document to a new file path.
        /// </summary>
        /// <param name="filePath">Target path.</param>
        /// <param name="isUserSave">If true, updates the primary file path and resets its modification state.</param>
        public async Task<bool> SaveAsAsync(string filePath, bool isUserSave = true)
        {
            if (_document == null) return false;

            return await Task.Run(() =>
            {
                try
                {
                    _document.Save(filePath);
                    
                    // PDFsharp "freezes" the document after Save. 
                    // To continue editing, we must reload it.
                    var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);
                    
                    // It's safe to assign new document here. 
                    // The old _document's content stream is already saved.
                    _document = doc;

                    if (isUserSave)
                    {
                        _filePath = filePath;
                        _isModified = false;
                        ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    
                    // We DO NOT fire DocumentChanged here to avoid recursive rendering loops 
                    // when this is called from within a rendering pass.
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error saving PDF: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Adds text to a page at a specific position with font settings.
        /// </summary>
        public void AddText(int pageIndex, double x, double y, string text,
            string fontFamily, double fontSize, XColor color,
            bool isBold = false, bool isItalic = false, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount)
                return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var style = XFontStyleEx.Regular;
            if (isBold && isItalic) style = XFontStyleEx.BoldItalic;
            else if (isBold) style = XFontStyleEx.Bold;
            else if (isItalic) style = XFontStyleEx.Italic;

            try
            {
                // Add Unicode option for better Korean/International character support
                var font = new XFont(fontFamily, fontSize, style, new XPdfFontOptions(PdfFontEncoding.Unicode));
                var brush = new XSolidBrush(color);

                // Handle multi-line text by splitting into lines
                string[] lines = text.Replace("\r", "").Split('\n');
                double lineSpacing = font.GetHeight(); // Use font height for line spacing

                for (int i = 0; i < lines.Length; i++)
                {
                    // Using TopLeft format directly with the provided Y coordinate
                    // ensures alignment with the UI overlay.
                    gfx.DrawString(lines[i], font, brush, new XPoint(x, y + (i * lineSpacing)), XStringFormats.TopLeft);
                }

                if (targetDoc == null)
                {
                    _isModified = true;
                    ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding text to PDF: {ex.Message}");
                throw; // Rethrow to let the UI know if needed, or handle it
            }
        }

        /// <summary>
        /// Adds a highlight rectangle on a page.
        /// </summary>
        public void AddHighlight(int pageIndex, double x, double y, double width, double height,
            XColor color, double opacity = 0.3, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount)
                return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var brush = new XSolidBrush(XColor.FromArgb((int)(opacity * 255), color));
            
            // Top-down coordinates
            gfx.DrawRectangle(brush, x, y, width, height);

            if (targetDoc == null)
            {
                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Adds an image to a page at a specific position.
        /// </summary>
        public void AddImage(int pageIndex, string imagePath, double x, double y,
            double width, double height, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount)
                return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var image = XImage.FromFile(imagePath);
            // Top-down coordinates
            gfx.DrawImage(image, x, y, width, height);

            if (targetDoc == null)
            {
                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Adds a sticky note annotation to a page.
        /// </summary>
        public void AddStickyNote(int pageIndex, double x, double y, string noteText,
            string fontFamily = "맑은 고딕", double fontSize = 10, PdfDocument? targetDoc = null)
        {
            var doc = targetDoc ?? _document;
            if (doc == null || pageIndex < 0 || pageIndex >= doc.PageCount)
                return;

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            // Draw sticky note background
            double noteWidth = 150;
            double noteHeight = 100;
            double padding = 8;

            // Shadow
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(40, 0, 0, 0)),
                x + 3, y + 3, noteWidth, noteHeight);

            // Note background (yellow)
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 255, 255, 200)),
                x, y, noteWidth, noteHeight);
            gfx.DrawRectangle(XPens.DarkGoldenrod, x, y, noteWidth, noteHeight);

            // Note header
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 255, 230, 100)),
                x, y, noteWidth, 20);
            gfx.DrawLine(XPens.DarkGoldenrod, x, y + 20, x + noteWidth, y + 20);

            // Header text
            var headerFont = new XFont(fontFamily, 8, XFontStyleEx.Bold);
            gfx.DrawString("📝 메모", headerFont, XBrushes.DarkSlateGray,
                new XPoint(x + padding, y + 14), XStringFormats.TopLeft);

            // Note text
            var font = new XFont(fontFamily, fontSize, XFontStyleEx.Regular);
            var textRect = new XRect(x + padding, y + 24,
                noteWidth - padding * 2, noteHeight - 28);

            var tf = new PdfSharp.Drawing.Layout.XTextFormatter(gfx);
            tf.DrawString(noteText, font, XBrushes.Black, textRect);

            if (targetDoc == null)
            {
                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Merges multiple PDF files into the current document.
        /// </summary>
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
                        for (int i = 0; i < src.PageCount; i++)
                        {
                            _document.AddPage(src.Pages[i]);
                        }
                    }
                    _isModified = true;
                    ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error merging PDFs: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Merges multiple PDF files into a new document.
        /// </summary>
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
                        for (int i = 0; i < src.PageCount; i++)
                        {
                            output.AddPage(src.Pages[i]);
                        }
                    }
                    output.Save(outputPath);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error merging PDFs: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Splits the current document into multiple files.
        /// </summary>
        public async Task<bool> SplitAsync(string outputDir, int pagesPerFile = 1)
        {
            if (_document == null) return false;

            return await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                    int totalPages = _document.PageCount;
                    int fileIndex = 1;

                    for (int i = 0; i < totalPages; i += pagesPerFile)
                    {
                        using var newDoc = new PdfDocument();
                        int endPage = Math.Min(i + pagesPerFile, totalPages);

                        for (int j = i; j < endPage; j++)
                        {
                            // Re-open for import
                            using var src = PdfReader.Open(_filePath!, PdfDocumentOpenMode.Import);
                            newDoc.AddPage(src.Pages[j]);
                        }

                        string outputPath = Path.Combine(outputDir,
                            $"{Path.GetFileNameWithoutExtension(_filePath)}_part{fileIndex:D3}.pdf");
                        newDoc.Save(outputPath);
                        fileIndex++;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error splitting PDF: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Splits the current document by page ranges.
        /// </summary>
        public async Task<bool> SplitByRangesAsync(string outputDir, List<(int start, int end)> ranges)
        {
            if (_document == null || string.IsNullOrEmpty(_filePath)) return false;

            return await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                    int fileIndex = 1;

                    foreach (var (start, end) in ranges)
                    {
                        using var newDoc = new PdfDocument();
                        using var src = PdfReader.Open(_filePath!, PdfDocumentOpenMode.Import);

                        for (int j = start - 1; j < end && j < src.PageCount; j++)
                        {
                            newDoc.AddPage(src.Pages[j]);
                        }

                        string outputPath = Path.Combine(outputDir,
                            $"{Path.GetFileNameWithoutExtension(_filePath)}_{start}-{end}.pdf");
                        newDoc.Save(outputPath);
                        fileIndex++;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error splitting PDF: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Deletes a page from the document.
        /// </summary>
        public void DeletePage(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return;

            _document.Pages.RemoveAt(pageIndex);
            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Creates a new empty document.
        /// </summary>
        public void NewDocument()
        {
            _document = new PdfDocument();
            _document.AddPage();
            _filePath = null;
            _isModified = false;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Closes the current document.
        /// </summary>
        public void Close()
        {
            _document?.Close();
            _document = null;
            _filePath = null;
            _isModified = false;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Gets page dimensions.
        /// </summary>
        public (double width, double height) GetPageSize(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return (0, 0);

            var page = _document.Pages[pageIndex];
            return (page.Width.Point, page.Height.Point);
        }

        public void MarkModified()
        {
            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Extracts all text objects from a page with their bounding boxes.
        /// </summary>
        public async Task<List<SearchResult>> ExtractTextObjectsAsync(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return new List<SearchResult>();

            return await Task.Run(() =>
            {
                var results = new List<SearchResult>();
                try
                {
                    var page = _document.Pages[pageIndex];
                    var sequence = ContentReader.ReadContent(page);
                    var textState = new PdfTextState(page.Height.Point);

                    ProcessContent(sequence, textState, results);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting text: {ex.Message}");
                }
                return results;
            });
        }

        private void ProcessContent(CSequence sequence, PdfTextState state, List<SearchResult> results)
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
                                state.FontSize = (op.Operands[1] is CReal || op.Operands[1] is CInteger)
                                    ? double.Parse(op.Operands[1].ToString() ?? "12") : 12;
                            break;
                        case "Tm":
                            if (op.Operands.Count >= 6)
                            {
                                state.TextMatrix = state.LineMatrix = new XMatrix(
                                    double.Parse(op.Operands[0].ToString() ?? "0"), double.Parse(op.Operands[1].ToString() ?? "0"),
                                    double.Parse(op.Operands[2].ToString() ?? "0"), double.Parse(op.Operands[3].ToString() ?? "0"),
                                    double.Parse(op.Operands[4].ToString() ?? "0"), double.Parse(op.Operands[5].ToString() ?? "0"));
                            }
                            break;
                        case "Td":
                            if (op.Operands.Count >= 2)
                            {
                                var tx = double.Parse(op.Operands[0].ToString() ?? "0");
                                var ty = double.Parse(op.Operands[1].ToString() ?? "0");
                                var m = new XMatrix(); m.TranslateAppend(tx, ty);
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                            }
                            break;
                        case "TD":
                            if (op.Operands.Count >= 2)
                            {
                                var tx = double.Parse(op.Operands[0].ToString() ?? "0");
                                var ty = double.Parse(op.Operands[1].ToString() ?? "0");
                                // Sets leading as well, but we ignore it for now
                                var m = new XMatrix(); m.TranslateAppend(tx, ty);
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                            }
                            break;
                        case "T*":
                            {
                                var m = new XMatrix(); m.TranslateAppend(0, -state.FontSize * 1.2); // Default leading
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                            }
                            break;
                        case "Tj":
                            if (op.Operands.Count >= 1)
                            {
                                AddTextToResults(op.Operands[0].ToString(), state, results);
                            }
                            break;
                        case "TJ":
                            if (op.Operands.Count >= 1 && op.Operands[0] is CArray array)
                            {
                                var sb = new System.Text.StringBuilder();
                                foreach (var item in array) if (item is CString) sb.Append(item.ToString());
                                AddTextToResults(sb.ToString(), state, results);
                            }
                            break;
                        case "'":
                            {
                                var m = new XMatrix(); m.TranslateAppend(0, -state.FontSize * 1.2);
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                                if (op.Operands.Count >= 1) AddTextToResults(op.Operands[0].ToString(), state, results);
                            }
                            break;
                    }
                }
                else if (obj is CSequence inner)
                {
                    ProcessContent(inner, state, results);
                }
            }
        }

        private void AddTextToResults(string? text, PdfTextState state, List<SearchResult> results)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            // Basic cleaning of PDF string (remove parentheses if they are there)
            if (text.StartsWith("(") && text.EndsWith(")")) text = text.Substring(1, text.Length - 2);
            // PDF Coordinate (x, y) is at the bottom-left of the text base line.
            // Our UI Coordinate (x, y) is at the top-left of the text.
            double pdfX = state.TextMatrix.OffsetX;
            double pdfY = state.TextMatrix.OffsetY;
            double height = state.FontSize;

            // Convert PDF y (bottom-up) to UI y (top-down)
            double uiY = state.PageHeight - pdfY - height;

            // Simple width estimate. 0.6 is a magic number, but Malgun Gothic is usually around 0.5-0.7
            // Multi-byte characters (Korean) are wider.
            double width = text.Length * state.FontSize * 0.6;
            if (text.Any(c => c > 255)) width = text.Length * state.FontSize * 1.0; 

            results.Add(new SearchResult
            {
                FoundText = text,
                X = pdfX,
                Y = uiY,
                Width = width,
                Height = height,
                PageIndex = 0 // Will be set by caller
            });

            // Advance text matrix roughly (assume horizontal text)
            var m = new XMatrix(); m.TranslateAppend(width, 0);
            state.TextMatrix *= m;
        }

        /// <summary>
        /// Removes text at a specific coordinate from the PDF content stream.
        /// </summary>
        public async Task<bool> RemoveTextAsync(int pageIndex, double x, double y, string text)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return false;

            return await Task.Run(() =>
            {
                try
                {
                    var page = _document.Pages[pageIndex];
                    var sequence = ContentReader.ReadContent(page);
                    var state = new PdfTextState(page.Height.Point);

                    if (RemoveTextFromSequence(sequence, state, x, y, text))
                    {
                        using var ms = new MemoryStream();
                        
                        // ContentWriter is internal in PDFsharp 6.0, use reflection as a workaround
                        var contentWriterType = typeof(ContentReader).Assembly.GetType("PdfSharp.Pdf.Content.ContentWriter");
                        if (contentWriterType != null)
                        {
                            var writer = Activator.CreateInstance(contentWriterType, new object[] { ms });
                            var writeMethod = contentWriterType.GetMethod("Write", new[] { typeof(CSequence) });
                            if (writeMethod != null)
                            {
                                writeMethod.Invoke(writer, new object[] { sequence });
                                
                                var closeMethod = contentWriterType.GetMethod("Close");
                                closeMethod?.Invoke(writer, null);
                                
                                page.Contents.CreateSingleContent().Stream.Value = ms.ToArray();
                                _isModified = true;
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error removing text: {ex.Message}");
                }
                return false;
            });
        }

        private bool RemoveTextFromSequence(CSequence sequence, PdfTextState state, double targetX, double targetY, string targetText)
        {
            for (int i = 0; i < sequence.Count; i++)
            {
                var obj = sequence[i];
                if (obj is COperator op)
                {
                    // Update state (Tm, Td, etc.) - same as in ExtractTextObjects
                    switch (op.Name)
                    {
                        case "BT": state.InTextObject = true; state.TextMatrix = state.LineMatrix = new XMatrix(); break;
                        case "ET": state.InTextObject = false; break;
                        case "Tf": 
                            if (op.Operands.Count >= 2) 
                                state.FontSize = (op.Operands[1] is CReal || op.Operands[1] is CInteger)
                                    ? double.Parse(op.Operands[1].ToString() ?? "12") : 12;
                            break;
                        case "Tm":
                            if (op.Operands.Count >= 6)
                            {
                                state.TextMatrix = state.LineMatrix = new XMatrix(
                                    double.Parse(op.Operands[0].ToString() ?? "0"), double.Parse(op.Operands[1].ToString() ?? "0"),
                                    double.Parse(op.Operands[2].ToString() ?? "0"), double.Parse(op.Operands[3].ToString() ?? "0"),
                                    double.Parse(op.Operands[4].ToString() ?? "0"), double.Parse(op.Operands[5].ToString() ?? "0"));
                            }
                            break;
                        case "Td":
                        case "TD":
                            if (op.Operands.Count >= 2)
                            {
                                var tx = double.Parse(op.Operands[0].ToString() ?? "0");
                                var ty = double.Parse(op.Operands[1].ToString() ?? "0");
                                var m = new XMatrix(); m.TranslateAppend(tx, ty);
                                state.LineMatrix *= m;
                                state.TextMatrix = state.LineMatrix;
                            }
                            break;
                        case "Tj":
                        case "TJ":
                        case "'":
                            {
                                string? opText = "";
                                if (op.Name == "TJ")
                                {
                                    var sb = new System.Text.StringBuilder();
                                    if (op.Operands.Count >= 1 && op.Operands[0] is CArray array)
                                        foreach (var item in array) if (item is CString) sb.Append(item.ToString());
                                    opText = sb.ToString();
                                }
                                else
                                {
                                    opText = op.Operands.Count >= 1 ? op.Operands[0].ToString() : "";
                                }

                                if (!string.IsNullOrEmpty(opText))
                                {
                                    if (opText.StartsWith("(") && opText.EndsWith(")")) opText = opText.Substring(1, opText.Length - 2);
                                    
                                    double pdfX = state.TextMatrix.OffsetX;
                                    double pdfY = state.TextMatrix.OffsetY;
                                    double uiY = state.PageHeight - pdfY - state.FontSize;

                                    // Match by text and approximate position
                                    // Being more lenient: if the coordinates are almost identical, remove it even if text slightly different
                                    bool posMatch = Math.Abs(pdfX - targetX) < 2 && Math.Abs(uiY - targetY) < 5;
                                    bool textMatch = !string.IsNullOrEmpty(targetText) && opText.Contains(targetText);
                                    
                                    // If text is broken (contains many non-printable chars), rely more on position
                                    bool isBroken = opText.Any(c => c < 32 && c != 10 && c != 13);

                                    if ((textMatch && posMatch) || (posMatch && isBroken))
                                    {
                                        sequence.RemoveAt(i);
                                        return true;
                                    }

                                    // Advance matrix
                                    var m = new XMatrix(); m.TranslateAppend(opText.Length * state.FontSize * 0.6, 0);
                                    state.TextMatrix *= m;
                                }
                            }
                            break;
                    }
                }
                else if (obj is CSequence inner)
                {
                    if (RemoveTextFromSequence(inner, state, targetX, targetY, targetText)) return true;
                }
            }
            return false;
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
