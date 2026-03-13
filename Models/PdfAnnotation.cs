using System;
using System.Collections.Generic;

namespace PDF_simple_edit.Models
{
    /// <summary>
    /// Types of annotations that can be added to a PDF page.
    /// </summary>
    public enum AnnotationType
    {
        Text,
        Highlight,
        StickyNote,
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
        
        // Fields for original text replacement
        public bool IsOriginalTextReplacement { get; set; } = false;
        public double OriginalPdfX { get; set; }
        public double OriginalPdfY { get; set; }
        public string OriginalText { get; set; } = string.Empty;
        public Guid? OperatorId { get; set; }
    }

    /// <summary>
    /// Represents an edit tool mode.
    /// </summary>
    public enum EditToolMode
    {
        None,
        Select,
        AddText,
        AddStickyNote,
        AddImage,
        Highlight,
        MoveText
    }

    /// <summary>
    /// Represents a text search result in the PDF.
    /// </summary>
    public class SearchResult
    {
        public int PageIndex { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string FoundText { get; set; } = string.Empty;
        public Guid? OperatorId { get; set; }
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

        public TextFontSettings Clone()
        {
            return new TextFontSettings
            {
                FontFamily = FontFamily,
                FontSize = FontSize,
                Color = Color,
                IsBold = IsBold,
                IsItalic = IsItalic
            };
        }
    }

}
