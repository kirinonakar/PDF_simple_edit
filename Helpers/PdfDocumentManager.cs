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
        public bool IsModified => _isModified;
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
            bool isBold = false, bool isItalic = false)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return;

            var page = _document.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var style = XFontStyleEx.Regular;
            if (isBold && isItalic) style = XFontStyleEx.BoldItalic;
            else if (isBold) style = XFontStyleEx.Bold;
            else if (isItalic) style = XFontStyleEx.Italic;

            try
            {
                var font = new XFont(fontFamily, fontSize, style);
                var brush = new XSolidBrush(color);

                // Handle multi-line text by splitting into lines
                string[] lines = text.Replace("\r", "").Split('\n');
                double lineSpacing = font.GetHeight(); // Use font height for line spacing

                for (int i = 0; i < lines.Length; i++)
                {
                    // Shifting Y to match WinUI's Top-Left layout exactly
                    // font.Metrics.Ascent is in font units (usually 1000 or 2048)
                    double fontAscentPoints = font.Metrics.Ascent * font.Size / 1000.0;
                    gfx.DrawString(lines[i], font, brush, new XPoint(x, y + fontAscentPoints + (i * lineSpacing)), XStringFormats.TopLeft);
                }

                _isModified = true;
                ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                DocumentChanged?.Invoke(this, EventArgs.Empty);
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
            XColor color, double opacity = 0.3)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return;

            var page = _document.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var brush = new XSolidBrush(XColor.FromArgb((int)(opacity * 255), color));
            
            // Top-down coordinates
            gfx.DrawRectangle(brush, x, y, width, height);

            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Adds an image to a page at a specific position.
        /// </summary>
        public void AddImage(int pageIndex, string imagePath, double x, double y,
            double width, double height)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return;

            var page = _document.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var image = XImage.FromFile(imagePath);
            // Top-down coordinates
            gfx.DrawImage(image, x, y, width, height);

            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Adds a sticky note annotation to a page.
        /// </summary>
        public void AddStickyNote(int pageIndex, double x, double y, string noteText,
            string fontFamily = "맑은 고딕", double fontSize = 10)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount)
                return;

            var page = _document.Pages[pageIndex];
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

            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
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
    }
}
