using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Extgstate; // Added this line
using iText.Kernel.Geom;
using iText.Kernel.Colors;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.IO.Image;
using iText.Kernel.Font;
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
        private PdfDocument? _document;
        private string? _filePath;
        private bool _isModified;
        private readonly object _docLock = new();

        public PdfDocument? Document => _document;
        public string? FilePath => _filePath;
        public void SetFilePath(string path) => _filePath = path;
        
        public bool IsModified => _isModified;
        public void MarkModified(bool modified = true)
        {
            _isModified = modified;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }
        public int PageCount => _document?.GetNumberOfPages() ?? 0;
        public bool IsLoaded => _document != null;

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
                        var reader = new PdfReader(filePath);
                        var writer = new PdfWriter(new MemoryStream()); // Use memory stream for intermediate modifications
                        var doc = new PdfDocument(reader, writer);
                        
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
                        _document.Close();
                        
                        // Re-open for modification
                        var reader = new PdfReader(_filePath);
                        var writer = new PdfWriter(filePath);
                        _document = new PdfDocument(reader, writer);
                        
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
            string fontFamily, double fontSize, Color color,
            bool isBold = false, bool isItalic = false)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages()) return;

            var page = _document.GetPage(pageIndex + 1);
            var canvas = new PdfCanvas(page);
            
            // Note: Simplified font handling. iText requires proper font registration for Unicode/Hangeul.
            PdfFont font = PdfFontFactory.CreateFont(); // Default font
            
            canvas.BeginText()
                  .SetFontAndSize(font, (float)fontSize)
                  .SetFillColor(color)
                  .MoveText(x, y)
                  .ShowText(text)
                  .EndText();

            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddHighlight(int pageIndex, double x, double y, double width, double height, 
            Color? color = null, float opacity = 0.5f)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages()) return;

            var page = _document.GetPage(pageIndex + 1);
            var canvas = new PdfCanvas(page);
            
            canvas.SaveState();
            
            var gs = new PdfExtGState().SetFillOpacity(opacity);
            canvas.SetExtGState(gs);
            canvas.SetFillColor(color ?? ColorConstants.YELLOW);
            
            canvas.Rectangle(x, y, width, height);
            canvas.Fill();
            
            canvas.RestoreState();
            MarkModified(true);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddImage(int pageIndex, string imagePath, double x, double y,
            double width, double height)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages()) return;

            var page = _document.GetPage(pageIndex + 1);
            ImageData data = ImageDataFactory.Create(imagePath);
            iText.Layout.Element.Image img = new iText.Layout.Element.Image(data);
            img.SetFixedPosition(pageIndex + 1, (float)x, (float)y, (float)width);
            
            using var canvasLayout = new iText.Layout.Canvas(new PdfCanvas(page), page.GetPageSize());
            canvasLayout.Add(img);

            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DeletePage(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages()) return;
            _document.RemovePage(pageIndex + 1);
            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void NewDocument()
        {
            var writer = new PdfWriter(new MemoryStream());
            _document = new PdfDocument(writer);
            _document.AddNewPage();
            _filePath = null;
            _isModified = false;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Close()
        {
            _document?.Close();
            _document = null;
            _filePath = null;
            _isModified = false;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public (double width, double height) GetPageSize(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages()) return (0, 0);
            var size = _document.GetPage(pageIndex + 1).GetPageSize();
            return (size.GetWidth(), size.GetHeight());
        }

        public async Task<List<PdfPageContent>> ExtractPageContentsAsync(int pageIndex)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages())
                return new List<PdfPageContent>();

            return await Task.Run(() =>
            {
                var contents = new List<PdfPageContent>();
                try
                {
                    var page = _document.GetPage(pageIndex + 1);
                    var strategy = new LocationTextExtractionStrategy();
                    PdfCanvasProcessor processor = new PdfCanvasProcessor(strategy);
                    processor.ProcessPageContent(page);

                    // Custom listener to get word bounding boxes would be better for iText.
                    // For now, using simplified strategy.
                    
                    var results = strategy.GetResultantText();
                    // iText extraction needs custom implementation of IEventListener to get words with bounding boxes.
                    // This is a simplified version.
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error extracting page contents: {ex.Message}");
                }
                return contents;
            });
        }

        public async Task<bool> RemoveTextAsync(int pageIndex, double x, double y, string text)
        {
            if (_document == null || pageIndex < 0 || pageIndex >= _document.GetNumberOfPages()) return false;

            // iText 9 uses PdfCleanup for redaction.
            // Simplified: Draw a white rectangle over the text area.
            AddHighlight(pageIndex, x, y, 100, 20, ColorConstants.WHITE, 1.0f);
            
            _isModified = true;
            ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public async Task<bool> MoveTextAsync(int pageIndex, string text, double oldX, double oldY, double newX, double newY)
        {
            if (await RemoveTextAsync(pageIndex, oldX, oldY, text))
            {
                AddText(pageIndex, newX, newY, text, "Arial", 12, ColorConstants.BLACK);
                return true;
            }
            return false;
        }
    }
}