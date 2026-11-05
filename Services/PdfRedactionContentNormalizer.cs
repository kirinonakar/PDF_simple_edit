using System;
using System.Collections.Generic;
using System.IO;
using iText.IO.Source;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using iText.Kernel.Pdf.Xobject;

namespace PDF_simple_edit.Services;

internal static class PdfRedactionContentNormalizer
{
    // pdfSweep 5.0.8 can calculate removed-string advances using the previous
    // output font and retain a TL value past Q. Give each string its original
    // absolute text matrix so neither issue can move surviving page content.
    public static void Normalize(PdfPage page)
    {
        var listener = new TextListener();
        var processor = new Processor(listener);
        listener.Processor = processor;
        processor.ProcessPageContent(page);
        var result = processor.Result!;
        if (!result.Changed) return;
        page.Put(PdfName.Contents, new PdfStream(result.Bytes));
        page.SetResources(result.Resources);
    }

    private sealed record Content(byte[] Bytes, PdfResources Resources, bool Changed);

    private sealed class Frame : IDisposable
    {
        public readonly byte[] Bytes;
        public readonly PdfResources Resources;
        public readonly MemoryStream Output = new();
        public readonly PdfOutputStream Writer;
        public readonly PdfTokenizer Tokenizer;
        public readonly PdfCanvasParser Parser;
        public bool Changed;

        public Frame(byte[] bytes, PdfResources resources)
        {
            Bytes = bytes;
            // Resource dictionaries can be inherited or shared by multiple pages
            // and form placements. Never add our private forms to the originals.
            var dictionary = new PdfDictionary(resources.GetPdfObject());
            var xobjects = dictionary.GetAsDictionary(PdfName.XObject);
            if (xobjects != null) dictionary.Put(PdfName.XObject, new PdfDictionary(xobjects));
            Resources = new PdfResources(dictionary);
            Writer = new PdfOutputStream(Output);
            Tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(new RandomAccessSourceFactory().CreateSource(bytes)));
            Parser = new PdfCanvasParser(Tokenizer, resources);
        }

        public void Write(params PdfObject[] operands)
        {
            Writer.WriteNewLine();
            foreach (var operand in operands) Writer.Write(operand).WriteSpace();
            Writer.WriteNewLine();
        }

        public void Dispose() { Tokenizer.Close(); Output.Dispose(); }
    }

    private sealed class Processor(TextListener listener) : PdfCanvasProcessor(listener)
    {
        private readonly Stack<Frame> _frames = new();
        public Content? Result { get; private set; }

        public override void ProcessContent(byte[] contentBytes, PdfResources resources)
        {
            using var frame = new Frame(contentBytes, resources);
            _frames.Push(frame);
            try
            {
                base.ProcessContent(contentBytes, resources);
                int end = (int)Math.Min(frame.Tokenizer.GetPosition(), contentBytes.Length);
                frame.Output.Write(contentBytes.AsSpan(end));
                Result = new(frame.Output.ToArray(), frame.Resources, frame.Changed);
            }
            finally { _frames.Pop(); }
        }

        protected override void InvokeOperator(PdfLiteral oper, IList<PdfObject> operands)
        {
            var frame = _frames.Peek();
            // The tokenizer may advance one byte past EOF for a final token
            // without trailing whitespace. Limit copied ranges to real bytes.
            int start = (int)Math.Min(frame.Tokenizer.GetPosition(), frame.Bytes.Length);
            frame.Parser.Parse(new List<PdfObject>());
            int end = (int)Math.Min(frame.Tokenizer.GetPosition(), frame.Bytes.Length);
            string name = oper.ToString();
            bool text = name is "Tj" or "TJ" or "'" or "\"";
            PdfStream? form = name == "Do" ? GetXObjectStream((PdfName)operands[0]) : null;
            if (form != null && !PdfName.Form.Equals(form.GetAsName(PdfName.Subtype))) form = null;

            if (name == "\"")
            {
                frame.Write(operands[0], new PdfLiteral("Tw"));
                frame.Write(operands[1], new PdfLiteral("Tc"));
            }
            // Simulate the original operators, including relative moves, TJ
            // adjustments and q/Q text state. Only the output uses absolute Tm.
            // This keeps pdfSweep's removed-text shifts and cached leading from
            // affecting the position of the next surviving string.
            base.InvokeOperator(oper, operands);
            if (text)
            {
                frame.Changed = true;
            }
            else if (form != null && Result is { Changed: true } child)
            {
                var clone = new PdfStream(child.Bytes);
                clone.PutAll(form);
                clone.Remove(PdfName.Filter);
                clone.Remove(PdfName.DecodeParms);
                clone.Remove(PdfName.Length);
                clone.Put(PdfName.Resources, child.Resources.GetPdfObject());
                var resourceName = frame.Resources.AddForm(new PdfFormXObject(clone));
                frame.Write(resourceName, new PdfLiteral("Do"));
                frame.Changed = true;
            }
            else
            {
                // Preserve binary inline images and all non-text syntax exactly.
                frame.Output.Write(frame.Bytes.AsSpan(start, end - start));
            }
        }

        public void WriteText(TextRenderInfo text)
        {
            var frame = _frames.Peek();
            var matrix = text.GetTextMatrix();
            frame.Write(new PdfNumber(matrix.Get(Matrix.I11)), new PdfNumber(matrix.Get(Matrix.I12)),
                new PdfNumber(matrix.Get(Matrix.I21)), new PdfNumber(matrix.Get(Matrix.I22)),
                new PdfNumber(matrix.Get(Matrix.I31)), new PdfNumber(matrix.Get(Matrix.I32)), new PdfLiteral("Tm"));
            frame.Write(new PdfString(text.GetPdfString().GetValueBytes()).SetHexWriting(true), new PdfLiteral("Tj"));
        }
    }

    private sealed class TextListener : IEventListener
    {
        public Processor Processor = null!;
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is TextRenderInfo text) Processor.WriteText(text);
        }
    }
}
