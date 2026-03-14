using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
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
                    using var msInput = new MemoryStream(_pdfBytes);
                    using var msOutput = new MemoryStream();
                    
                    using (var reader = new PdfReader(msInput))
                    using (var writer = new PdfWriter(msOutput))
                    using (var doc = new PdfDocument(reader, writer))
                    {
                        editAction(doc);
                    }
                    
                    _pdfBytes = msOutput.ToArray();
                    _isModified = true;
                    ModifiedStateChanged?.Invoke(this, EventArgs.Empty);
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error applying edit: {ex.Message}");
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
            var canvas = new PdfCanvas(page);
            
            PdfFont font;
            try
            {
                // Try to use a Korean font if text contains Hangeul
                // Windows standard: Malgun Gothic
                string fontPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "malgun.ttf");
                if (File.Exists(fontPath))
                {
                    font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);
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
            
            canvas.BeginText()
                  .SetFontAndSize(font, (float)fontSize)
                  .SetFillColor(color)
                  .MoveText(x, y)
                  .ShowText(text)
                  .EndText();
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
            
            canvas.Rectangle(x, y, width, height);
            canvas.Fill();
            
            canvas.RestoreState();
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
            ImageData data = ImageDataFactory.Create(imagePath);
            iText.Layout.Element.Image img = new iText.Layout.Element.Image(data);
            
            // Use iText.Layout.Canvas for easy positioning
            using var canvasLayout = new iText.Layout.Canvas(new PdfCanvas(page), page.GetPageSize());
            img.SetFixedPosition((float)x, (float)y, (float)width);
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
                    var strategy = new LocationTextExtractionStrategy();
                    PdfCanvasProcessor processor = new PdfCanvasProcessor(strategy);
                    processor.ProcessPageContent(page);
                    
                    // Simple extraction for now
                    var text = strategy.GetResultantText();
                    // ... further processing could go here
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
            // Implementation of text removal/redaction would go here
            // For now, let's just add a white highlight as a placeholder
            AddHighlight(pageIndex, x, y, 100, 20, ColorConstants.WHITE, 1.0f);
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

        public PdfDocument? GetReadOnlyDocument()
        {
            if (_pdfBytes == null) return null;
            var reader = new PdfReader(new MemoryStream(_pdfBytes));
            return new PdfDocument(reader);
        }
    }
}