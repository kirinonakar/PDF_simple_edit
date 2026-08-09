using System;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Models
{
    /// <summary>
    /// Types of annotations that can be added to a PDF page.
    /// </summary>
    public enum AnnotationType
    {
        Text,
        Highlight,
        Image,
        FreeText
    }

    /// <summary>
    /// Represents an annotation on a PDF page.
    /// </summary>
    public class PdfAnnotation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public AnnotationType Type { get; set; }
        public int PageIndex { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Content { get; set; } = string.Empty;
        public string FontFamily { get; set; } = "맑은 고딕";
        public double FontSize { get; set; } = 12;
        public string Color { get; set; } = "#000000";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public double Opacity { get; set; } = 1.0;
        public string? ImagePath { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsApplied { get; set; } = false;
        
        // Fields for original content replacement
        public bool IsOriginalTextReplacement { get; set; } = false;
        public bool IsOriginalImageReplacement { get; set; } = false;
        public double OriginalPdfX { get; set; }
        public double OriginalPdfY { get; set; }
        public string OriginalText { get; set; } = string.Empty;
        public string? OriginalImageName { get; set; }
        public Guid? OperatorId { get; set; }
        public int ContentStreamIndex { get; set; } = -1;
        public int ContentStreamObjectNumber { get; set; } = -1;
        public int OperationIndex { get; set; } = -1;
        public int TextRenderMode { get; set; }
        public double LineHeight { get; set; }
        public double BaselineOffset { get; set; }
        public int OriginalFontObjectNumber { get; set; } = -1;
        public List<PdfTextFragment> TextFragments { get; set; } = new();

        public PdfAnnotation Clone()
        {
            return new PdfAnnotation
            {
                Id = this.Id, // Keep same ID for matching if needed, or Guid.NewGuid() if new object
                Type = this.Type,
                PageIndex = this.PageIndex,
                X = this.X,
                Y = this.Y,
                Width = this.Width,
                Height = this.Height,
                Content = this.Content,
                FontFamily = this.FontFamily,
                FontSize = this.FontSize,
                Color = this.Color,
                IsBold = this.IsBold,
                IsItalic = this.IsItalic,
                Opacity = this.Opacity,
                ImagePath = this.ImagePath,
                CreatedAt = this.CreatedAt,
                IsApplied = this.IsApplied,
                IsOriginalTextReplacement = this.IsOriginalTextReplacement,
                IsOriginalImageReplacement = this.IsOriginalImageReplacement,
                OriginalPdfX = this.OriginalPdfX,
                OriginalPdfY = this.OriginalPdfY,
                OriginalText = this.OriginalText,
                OriginalImageName = this.OriginalImageName,
                OperatorId = this.OperatorId,
                ContentStreamIndex = this.ContentStreamIndex,
                ContentStreamObjectNumber = this.ContentStreamObjectNumber,
                OperationIndex = this.OperationIndex,
                TextRenderMode = this.TextRenderMode,
                LineHeight = this.LineHeight,
                BaselineOffset = this.BaselineOffset,
                OriginalFontObjectNumber = this.OriginalFontObjectNumber,
                TextFragments = this.TextFragments.Select(fragment => fragment.Clone()).ToList()
            };
        }
    }

    /// <summary>
    /// Identifies one PDF text-showing operation and its rendered bounds.
    /// Keeping these targets lets the editor remove text without erasing graphics
    /// that happen to be behind the text.
    /// </summary>
    public class PdfTextFragment
    {
        public string Text { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double OriginalPdfX { get; set; }
        public double OriginalPdfY { get; set; }
        public double FontSize { get; set; }
        public string FontFamily { get; set; } = string.Empty;
        public string Color { get; set; } = "#000000";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public int ContentStreamIndex { get; set; } = -1;
        public int ContentStreamObjectNumber { get; set; } = -1;
        public int OperationIndex { get; set; } = -1;
        // Identifies the PdfString element inside a Tj/TJ text-showing operation.
        // The adjustment preserves the original text advance when that element is removed.
        public int TextOperandIndex { get; set; } = -1;
        public double? TextAdvanceAdjustment { get; set; }
        public int TextRenderMode { get; set; }
        public double BaselineOffset { get; set; }
        public int OriginalFontObjectNumber { get; set; } = -1;
        public int LineIndex { get; set; }

        public PdfTextFragment Clone()
        {
            return (PdfTextFragment)MemberwiseClone();
        }
    }

    /// <summary>
    /// Represents an edit tool mode.
    /// </summary>
    public enum EditToolMode
    {
        None,
        Select,
        AddText,
        AddImage,
        Highlight,
        MoveText,
        ColorPicker
    }

    /// <summary>
    /// Represents a text search result in the PDF.
    /// </summary>
    public class SearchResult
    {
        public int PageIndex { get; set; }
        public double X { get; set; } // UI coordinates (Top-Left)
        public double Y { get; set; } // UI coordinates (Top-Left)
        public double Width { get; set; }
        public double Height { get; set; }
        public string FoundText { get; set; } = string.Empty;
        public Guid? OperatorId { get; set; }
        public double OriginalPdfX { get; set; }
        public double OriginalPdfY { get; set; }
        public double FontSize { get; set; }
        public string FontFamily { get; set; } = string.Empty;
        public string Color { get; set; } = "#000000";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public int ContentStreamIndex { get; set; } = -1;
        public int ContentStreamObjectNumber { get; set; } = -1;
        public int OperationIndex { get; set; } = -1;
        public int TextRenderMode { get; set; }
        public double LineHeight { get; set; }
        public double BaselineOffset { get; set; }
        public int OriginalFontObjectNumber { get; set; } = -1;
        public List<PdfTextFragment> TextFragments { get; set; } = new();
    }

    public enum PageContentType
    {
        Text,
        Image
    }

    /// <summary>
    /// Represents selectable content from the original PDF (text or image).
    /// </summary>
    public class PdfPageContent
    {
        public PageContentType Type { get; set; }
        public double X { get; set; } // UI coordinates (Top-Left)
        public double Y { get; set; } // UI coordinates (Top-Left)
        public double Width { get; set; }
        public double Height { get; set; }
        public string Text { get; set; } = string.Empty;
        
        // For text removal/editing
        public double OriginalPdfX { get; set; }
        public double OriginalPdfY { get; set; }
        public Guid? OperatorId { get; set; }
        
        public double FontSize { get; set; }
        public string FontFamily { get; set; } = string.Empty;
        public string Color { get; set; } = "#000000";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public int ContentStreamIndex { get; set; } = -1;
        public int ContentStreamObjectNumber { get; set; } = -1;
        public int OperationIndex { get; set; } = -1;
        public int TextRenderMode { get; set; }
        public double LineHeight { get; set; }
        public double BaselineOffset { get; set; }
        public int OriginalFontObjectNumber { get; set; } = -1;
        public List<PdfTextFragment> TextFragments { get; set; } = new();
        
        // For image handling (future use)
        public string? ImageId { get; set; }
    }

    /// <summary>
    /// Stores font settings for text operations.
    /// </summary>
    public class TextFontSettings
    {
        public string FontFamily { get; set; } = "맑은 고딕";
        public double FontSize { get; set; } = 12;
        public string Color { get; set; } = "#000000";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        
        // Highlight settings
        public string HighlightColor { get; set; } = "#FFFF00";
        public double HighlightOpacity { get; set; } = 0.3;

        public TextFontSettings Clone()
        {
            return new TextFontSettings
            {
                FontFamily = FontFamily,
                FontSize = FontSize,
                Color = Color,
                IsBold = IsBold,
                IsItalic = IsItalic,
                HighlightColor = HighlightColor,
                HighlightOpacity = HighlightOpacity
            };
        }
    }

}
