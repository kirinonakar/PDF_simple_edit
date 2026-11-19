using System.Collections.Generic;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using PDF_simple_edit.Services;

namespace PDF_simple_edit.Mcp;

internal sealed class PdfReadTextExtractionStrategy : LocationTextExtractionStrategy
{
    public override void EventOccurred(IEventData data, EventType type)
    {
        if (data is TextRenderInfo info &&
            HwpEquationText.IsFont(info.GetFont().GetPdfObject().GetAsName(PdfName.BaseFont)?.GetValue()))
        {
            string text = info.GetText();
            string decoded = HwpEquationText.Decode(text);
            if (!ReferenceEquals(text, decoded)) data = new DecodedText(info, decoded);
        }
        base.EventOccurred(data, type);
    }

    // Override the extracted string while retaining the source glyph positions and spacing.
    private sealed class DecodedText(TextRenderInfo source, string text)
        : TextRenderInfo(source.GetPdfString(), source.GetGraphicsState(), source.GetTextMatrix(), new Stack<CanvasTag>())
    {
        public override string GetText() => text;
    }
}
