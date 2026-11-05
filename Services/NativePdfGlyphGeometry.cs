using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.IO.Font.Otf;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using PDF_simple_edit.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>Glyph ink bounds using iText and embedded font programs, without a second PDF engine.</summary>
internal static class NativePdfGlyphGeometry
{
    internal sealed record GlyphBox(double X, double Y, PdfTextBox Box);

    public static List<GlyphBox> Read(PdfPage page)
    {
        var listener = new Listener();
        var processor = new PdfCanvasProcessor(listener);
        listener.Processor = processor;
        processor.ProcessPageContent(page);
        return listener.Boxes;
    }

    private sealed class Listener : IEventListener
    {
        public PdfCanvasProcessor Processor = null!;
        public readonly List<GlyphBox> Boxes = new();
        private readonly Dictionary<PdfFont, FontBounds> _fonts = new();
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo info) return;
            var font = info.GetFont();
            if (!_fonts.TryGetValue(font, out var bounds)) _fonts[font] = bounds = new FontBounds(font);
            var state = Processor.GetGraphicsState();
            var matrix = info.GetTextMatrix().Multiply(state.GetCtm());
            double size = state.GetFontSize(), scale = state.GetHorizontalScaling() / 100.0;
            foreach (var character in info.GetCharacterRenderInfos())
            {
                var decoded = font.DecodeIntoGlyphLine(character.GetPdfString());
                if (decoded.Size() == 0) continue;
                var box = bounds.Get(decoded.Get(0), character.GetPdfString().GetValueBytes());
                if (box == null || box[2] <= box[0] || box[3] <= box[1]) continue;
                var origin = character.GetBaseline().GetStartPoint();
                var points = new List<(double X, double Y)>();
                foreach (double x in new[] { box[0], box[2] })
                foreach (double y in new[] { box[1], box[3] })
                {
                    var offset = new Vector((float)(x * size * scale / 1000), (float)(y * size / 1000), 0).Cross(matrix);
                    points.Add((origin.Get(0) + offset.Get(0), origin.Get(1) + offset.Get(1)));
                }
                double left = points.Min(p => p.X), bottom = points.Min(p => p.Y);
                Boxes.Add(new(origin.Get(0), origin.Get(1), new(left, bottom,
                    points.Max(p => p.X) - left, points.Max(p => p.Y) - bottom)));
            }
        }
    }

    private sealed class FontBounds
    {
        private readonly PdfFont _font;
        private readonly FontProgram? _program;
        private readonly CffGlyphBounds? _cff;
        private readonly TrueTypeBounds? _trueType;
        private readonly byte[]? _cidToGid;

        public FontBounds(PdfFont font)
        {
            _font = font;
            var dictionary = font.GetPdfObject();
            var descendant = dictionary.GetAsArray(PdfName.DescendantFonts)?.GetAsDictionary(0);
            var descriptor = (descendant ?? dictionary).GetAsDictionary(PdfName.FontDescriptor);
            _cidToGid = descendant?.GetAsStream(PdfName.CIDToGIDMap)?.GetBytes();
            var stream = descriptor?.GetAsStream(PdfName.FontFile2) ?? descriptor?.GetAsStream(PdfName.FontFile3);
            if (stream == null)
            {
                string? name = dictionary.GetAsName(PdfName.BaseFont)?.GetValue();
                if (name != null && StandardFonts.IsStandardFont(name)) _program = FontProgramFactory.CreateFont(name);
                return;
            }
            // A missing or unsupported font must not make otherwise readable text unselectable.
            try
            {
                byte[] bytes = stream.GetBytes();
                string? subtype = stream.GetAsName(PdfName.Subtype)?.GetValue();
                if (subtype is "Type1C" or "CIDFontType0C") _cff = new CffGlyphBounds(bytes);
                else
                {
                    if (descriptor?.GetAsStream(PdfName.FontFile2) != null) _trueType = new TrueTypeBounds(bytes);
                    _program = FontProgramFactory.CreateFont(bytes, false);
                    if (_program is TrueTypeFont ttf && ttf.IsCff()) _cff = new CffGlyphBounds(ttf.GetFontStreamBytes());
                    else if (_program is TrueTypeFont && _trueType == null) _trueType = new TrueTypeBounds(bytes);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { System.Diagnostics.Debug.WriteLine($"Embedded glyph bounds unavailable: {ex.Message}"); }
        }

        public double[]? Get(Glyph glyph, byte[] code)
        {
            if (_cff != null)
            {
                string? name = _font is PdfType1Font type1 && code.Length == 1
                    ? type1.GetFontEncoding().GetDifference(code[0]) ?? AdobeGlyphList.UnicodeToName(glyph.GetUnicode()) : null;
                var cffBox = _cff.Get(glyph.GetCode(), name, _font is PdfType0Font);
                if (cffBox != null) return cffBox;
            }
            var box = glyph.GetBbox();
            if (box != null) return Array.ConvertAll(box, v => (double)v);
            if ((_program == null && _trueType == null) || _cff != null) return null;
            int gid = glyph.GetCode();
            if (_cidToGid != null)
            {
                if (gid < 0 || gid >= _cidToGid.Length / 2) return null;
                int index = gid * 2;
                gid = (_cidToGid[index] << 8) | _cidToGid[index + 1];
            }
            var actual = _font is PdfType0Font ? _program?.GetGlyphByCode(gid) : _program?.GetGlyph(glyph.GetUnicode());
            box = actual?.GetBbox();
            // Embedded subsets often omit cmap entries for ligatures/unmapped glyphs.
            // iText then supplies no bbox even though loca/glyf still contain it.
            return box == null ? _trueType?.Get(_font is PdfType0Font ? gid : actual?.GetCode() ?? -1) :
                Array.ConvertAll(box, v => (double)v);
        }
    }

    private sealed class TrueTypeBounds
    {
        private readonly byte[] _bytes;
        private readonly int _glyf, _glyfLength, _loca, _count;
        private readonly bool _longOffsets;
        private readonly double _scale;
        public TrueTypeBounds(byte[] bytes)
        {
            _bytes = bytes;
            var tables = new Dictionary<string, (int Offset, int Length)>();
            for (int i = 0, count = U16(4); i < count; i++)
            {
                int p = checked(12 + i * 16);
                int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p + 8, 4)));
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p + 12, 4)));
                if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new System.IO.InvalidDataException("Invalid TrueType table.");
                tables[System.Text.Encoding.ASCII.GetString(bytes, p, 4)] = (offset, length);
            }
            var head = tables["head"]; var loca = tables["loca"]; var glyf = tables["glyf"];
            _longOffsets = U16(head.Offset + 50) != 0;
            int units = U16(head.Offset + 18);
            if (units == 0) throw new System.IO.InvalidDataException("Invalid TrueType units per em.");
            _scale = 1000.0 / units;
            _loca = loca.Offset; _count = loca.Length / (_longOffsets ? 4 : 2) - 1;
            _glyf = glyf.Offset; _glyfLength = glyf.Length;
        }
        private int U16(int p) => BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(p, 2));
        private long Offset(int gid) => _longOffsets
            ? BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(_loca + gid * 4, 4)) : U16(_loca + gid * 2) * 2L;
        public double[]? Get(int gid)
        {
            if (gid < 0 || gid >= _count) return null;
            long offset = Offset(gid), end = Offset(gid + 1);
            if (end < offset + 10 || end > _glyfLength) return null;
            return Enumerable.Range(0, 4).Select(i =>
                BinaryPrimitives.ReadInt16BigEndian(_bytes.AsSpan(_glyf + (int)offset + 2 + i * 2, 2)) * _scale).ToArray();
        }
    }
}
