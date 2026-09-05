using iText.IO.Source;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services;

/// <summary>
/// Edits encoded glyph operands at their original paint position. Unselected stream
/// bytes, resources, text/line matrices, and final advances remain intact. Forms use
/// copy-on-write per invocation, so editing an instance never edits other instances.
/// </summary>
public sealed class NativePdfTextService
{
    public static PdfPageContent ToPageContent(NativePdfTextBlock block)
    {
        var run = block.Runs[0];
        return new PdfPageContent { NativeText = block, Type = PageContentType.Text, Text = block.Text,
            X = block.Bounds.X, Y = block.Bounds.Y, Width = block.Bounds.Width, Height = block.Bounds.Height,
            FontFamily = run.FontName, FontSize = run.DisplayFontSize, Color = run.Color,
            OriginalFontObjectNumber = run.FontObjectNumber, LineHeight = block.LineHeight,
            BaselineOffset = block.Glyphs[0].Origin.Y - block.Bounds.Y,
            IsBold = run.FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) || run.FontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase),
            IsItalic = run.FontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) || run.FontName.Contains("Obl", StringComparison.OrdinalIgnoreCase) };
    }
    private sealed record Operation(int Start, int End, int TextIndex, List<PdfObject> Items)
    {
        public string Name => Items[^1].ToString()!;
    }
    private sealed class Source
    {
        public required string Path;
        public required PdfStream Stream;
        public required PdfResources Resources;
        public required byte[] Bytes;
        public List<Operation> Operations = new();
        public Dictionary<Operation, Source> Children = new();
    }
    private sealed record Target(Source Source, Operation Operation);
    private sealed record PageMap(double Left, double Bottom, double Width, double Height, int Rotation)
    {
        public PdfTextPoint Map(double x, double y)
        {
            x -= Left; y -= Bottom;
            return Rotation switch { 90 => new(y, x), 180 => new(Width - x, y),
                270 => new(Height - y, Width - x), _ => new(x, Height - y) };
        }
        public PdfTextPoint UnmapVector(double x, double y) => Rotation switch
        { 90 => new(y, x), 180 => new(-x, y), 270 => new(-y, -x), _ => new(x, -y) };
    }

    public List<NativePdfTextBlock> Extract(byte[] bytes, int pageIndex)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        var data = Read(doc.GetPage(pageIndex + 1));
        return ApplyInkBounds(bytes, pageIndex, data.Blocks, data.Map);
    }

    private sealed record PageData(List<Source> Sources, List<NativePdfRun> Runs,
        List<NativePdfTextBlock> Blocks, PageMap Map);

    private static PageData Read(PdfPage page)
    {
        var box = page.GetCropBox();
        var map = new PageMap(box.GetX(), box.GetY(), box.GetWidth(), box.GetHeight(),
            ((page.GetRotation() % 360) + 360) % 360);
        var sources = new List<Source>();
        var targets = new List<Target>();
        // PDF treats a Contents array as one token stream. Operands, arrays and
        // graphics state can cross stream boundaries (including this sample's
        // reprint form). Parse exactly the bytes PdfCanvasProcessor consumes.
        sources.Add(ReadSource(new PdfStream(page.GetContentBytes()), page.GetResources(), "p0", targets, new HashSet<int>()));
        var listener = new Listener(targets, map);
        var processor = new PdfCanvasProcessor(listener);
        listener.Processor = processor;
        foreach (string name in new[] { "Tj", "TJ", "'", "\"" })
        {
            var tracker = new Tracker(listener);
            tracker.Inner = processor.RegisterContentOperator(name, tracker);
        }
        processor.ProcessPageContent(page);
        return new(sources, listener.Runs, Group(listener.Runs), map);
    }

    private static Source ReadSource(PdfStream stream, PdfResources resources, string path,
        List<Target> targets, HashSet<int> ancestors)
    {
        int number = stream.GetIndirectReference()?.GetObjNumber() ?? -1;
        if (number > 0 && !ancestors.Add(number))
            throw new InvalidOperationException("순환 참조된 PDF Form입니다.");
        var source = new Source { Path = path, Stream = stream, Resources = resources, Bytes = stream.GetBytes() };
        var tokenizer = new PdfTokenizer(new RandomAccessFileOrArray(new RandomAccessSourceFactory().CreateSource(source.Bytes)));
        try
        {
            var parser = new PdfCanvasParser(tokenizer, resources);
            int textIndex = -1;
            while (true)
            {
                int start = checked((int)tokenizer.GetPosition());
                var items = parser.Parse(new List<PdfObject>());
                if (items.Count == 0) break;
                string name = items[^1].ToString()!;
                bool text = name is "Tj" or "TJ" or "'" or "\"";
                var op = new Operation(start, checked((int)tokenizer.GetPosition()), text ? ++textIndex : -1, items.ToList());
                source.Operations.Add(op);
                if (text) targets.Add(new(source, op));
                if (name == "Do" && items[0] is PdfName resourceName)
                {
                    var form = resources.GetResource(PdfName.XObject)?.GetAsStream(resourceName);
                    if (form?.GetAsName(PdfName.Subtype)?.Equals(PdfName.Form) != true) continue;
                    var dictionary = form.GetAsDictionary(PdfName.Resources);
                    var child = ReadSource(form, dictionary == null ? resources : new PdfResources(dictionary),
                        $"{path}/d{source.Operations.Count - 1}", targets, ancestors);
                    source.Children[op] = child;
                }
            }
        }
        finally { tokenizer.Close(); if (number > 0) ancestors.Remove(number); }
        return source;
    }

    private sealed class Tracker(Listener listener) : IContentOperator
    {
        public IContentOperator? Inner;
        public void Invoke(PdfCanvasProcessor processor, PdfLiteral oper, IList<PdfObject> operands)
        {
            var previous = listener.Current;
            int previousOperand = listener.OperandIndex;
            listener.Current = listener.Targets[listener.TargetIndex++];
            listener.OperandIndex = 0;
            Inner!.Invoke(processor, oper, operands);
            listener.Current = previous;
            listener.OperandIndex = previousOperand;
        }
    }

    private sealed class Listener(List<Target> targets, PageMap map) : IEventListener
    {
        public List<Target> Targets = targets;
        public int TargetIndex, OperandIndex;
        public Target? Current;
        public PdfCanvasProcessor Processor = null!;
        public List<NativePdfRun> Runs = new();
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo info || Current == null) return;
            var gs = Processor.GetGraphicsState();
            var font = info.GetFont();
            var tm = info.GetTextMatrix();
            var ctm = gs.GetCtm();
            var matrix = tm.Multiply(ctm);
            double size = gs.GetFontSize(), hs = gs.GetHorizontalScaling() / 100.0;
            int operand = OperandIndex++;
            string id = $"{Current.Source.Path}:{Current.Operation.TextIndex}:{operand}";
            var glyphs = new List<NativePdfGlyph>();
            foreach (var character in info.GetCharacterRenderInfos())
            {
                var start = character.GetBaseline().GetStartPoint();
                var end = character.GetBaseline().GetEndPoint();
                var origin = map.Map(start.Get(0), start.Get(1));
                var endPoint = map.Map(end.Get(0), end.Get(1));
                // Glyph bounds from the font program are preferable to the global
                // FontDescriptor ascent/descent (often wrong in old subset fonts).
                var decoded = font.DecodeIntoGlyphLine(character.GetPdfString());
                int[]? bbox = decoded.Size() > 0 ? decoded.Get(0).GetBbox() : null;
                var points = new List<PdfTextPoint>();
                if (bbox is { Length: 4 } && bbox[3] > bbox[1])
                {
                    foreach (double x in new[] { bbox[0] * size * hs / 1000, bbox[2] * size * hs / 1000 })
                    foreach (double y in new[] { bbox[1] * size / 1000, bbox[3] * size / 1000 })
                    {
                        var v = new Vector((float)x, (float)y, 0).Cross(matrix);
                        points.Add(map.Map(start.Get(0) + v.Get(0), start.Get(1) + v.Get(1)));
                    }
                }
                else
                {
                    foreach (var line in new[] { character.GetAscentLine(), character.GetDescentLine() })
                    foreach (var point in new[] { line.GetStartPoint(), line.GetEndPoint() })
                        points.Add(map.Map(point.Get(0), point.Get(1)));
                }
                var bounds = new PdfTextBox(points.Min(p => p.X), points.Min(p => p.Y),
                    points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
                glyphs.Add(new NativePdfGlyph { Text = character.GetText(), Code = character.GetPdfString().GetValueBytes(),
                    RunId = id, Origin = origin, End = endPoint, Bounds = bounds, Advance = character.GetUnscaledWidth() });
            }
            var vertical = new Vector(0, (float)size, 0).Cross(matrix);
            string hex = PdfDisplayColorService.ToHex(info.GetFillColor());
            Runs.Add(new NativePdfRun { Id = id, SourcePath = Current.Source.Path, OperationIndex = Current.Operation.TextIndex,
                OperandIndex = operand, FontObjectNumber = font.GetPdfObject().GetIndirectReference()?.GetObjNumber() ?? -1,
                FontName = font.GetFontProgram().GetFontNames().GetFontName(), Color = hex, FontSize = size,
                DisplayFontSize = Math.Sqrt(vertical.Get(0) * vertical.Get(0) + vertical.Get(1) * vertical.Get(1)),
                HorizontalScale = hs, CharacterSpacing = gs.GetCharSpacing(), WordSpacing = gs.GetWordSpacing(),
                Rise = gs.GetTextRise(), RenderMode = info.GetTextRenderMode(), TextMatrix = Values(tm), Ctm = Values(ctm),
                OriginalAdvance = info.GetUnscaledWidth(), IsVertical = font.GetPdfObject().GetAsName(PdfName.Encoding)?.GetValue().EndsWith("-V", StringComparison.Ordinal) == true, Glyphs = glyphs });
        }
    }

    private static float[] Values(Matrix m) => new[] { m.Get(0), m.Get(1), m.Get(3), m.Get(4), m.Get(6), m.Get(7) };
    private static Matrix MatrixOf(float[] m) => new(m[0], m[1], m[2], m[3], m[4], m[5]);

    private static List<NativePdfTextBlock> Group(List<NativePdfRun> runs)
    {
        var blocks = new List<NativePdfTextBlock>();
        var current = new List<NativePdfRun>();
        foreach (var run in runs.Where(r => r.Glyphs.Count > 0))
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                var a = last.Glyphs[^1]; var b = run.Glyphs[0];
                double size = Math.Max(last.DisplayFontSize, run.DisplayFontSize);
                double dy = b.Origin.Y - a.Origin.Y, dx = b.Origin.X - a.End.X;
                double left = current.Min(r => r.Glyphs.Min(g => g.Origin.X));
                double right = current.Max(r => r.Glyphs.Max(g => g.End.X));
                bool sameLine = Math.Abs(dy) < size * 0.3 && dx >= -size * 0.5 &&
                    dx < size * (last.FontName == run.FontName ? 2 : .8);
                bool nextLine = dy > size * 0.65 && dy < size * 1.8 &&
                    Math.Abs(b.Origin.X - left) < size * 1.8 && right - left > size * 6 &&
                    Math.Abs(last.DisplayFontSize - run.DisplayFontSize) < size * 0.15;
                // Different orientations are independent editing objects.
                bool horizontal = Math.Abs(b.End.Y - b.Origin.Y) < 0.1 && Math.Abs(a.End.Y - a.Origin.Y) < 0.1;
                if (!(horizontal && (sameLine || nextLine))) { blocks.Add(BuildBlock(current)); current = new(); }
            }
            current.Add(run);
        }
        if (current.Count > 0) blocks.Add(BuildBlock(current));
        return blocks;
    }

    private static NativePdfTextBlock BuildBlock(List<NativePdfRun> runs)
    {
        var glyphs = new List<NativePdfGlyph>();
        var text = new StringBuilder();
        var lines = new List<List<PdfTextBox>> { new() };
        var baselines = new List<double>();
        NativePdfGlyph? previous = null;
        foreach (var run in runs)
        foreach (var glyph in run.Glyphs)
        {
            if (previous != null)
            {
                double dy = glyph.Origin.Y - previous.Origin.Y;
                double gap = glyph.Origin.X - previous.End.X;
                bool newLine = previous.RunId != glyph.RunId && dy > run.DisplayFontSize * 0.6;
                string separator = newLine ? "\n" :
                    previous.RunId != glyph.RunId && gap > run.DisplayFontSize * 0.12 && !previous.Text.EndsWith(' ') && !glyph.Text.StartsWith(' ') ? " " : "";
                if (separator.Length > 0)
                {
                    glyphs.Add(new NativePdfGlyph { Text = separator, RunId = previous.RunId,
                        Origin = previous.End, End = glyph.Origin, Bounds = new(previous.End.X, previous.Bounds.Y, Math.Max(gap, 0), previous.Bounds.Height),
                        TextIndex = text.Length, IsVirtual = true, IsSoftBreak = newLine });
                    text.Append(separator);
                }
                if (newLine) lines.Add(new());
            }
            if (lines[^1].Count == 0) baselines.Add(glyph.Origin.Y);
            glyphs.Add(glyph with { TextIndex = text.Length }); text.Append(glyph.Text);
            lines[^1].Add(glyph.Bounds); previous = glyph;
        }
        var boxes = lines.Select(PdfTextBox.Union).ToList();
        double leading = baselines.Count > 1 ? baselines.Zip(baselines.Skip(1), (a, b) => b - a).Order().ElementAt((baselines.Count - 2) / 2) : runs[0].DisplayFontSize * 1.2;
        return new() { Runs = runs.ToList(), Text = text.ToString(), Glyphs = glyphs, Bounds = PdfTextBox.Union(boxes), Lines = boxes, LineHeight = leading };
    }

    public NativePdfTextResult Edit(byte[] bytes, int pageIndex, NativePdfTextEdit edit) =>
        EditMany(bytes, pageIndex, new[] { edit });

    public NativePdfTextResult EditMany(byte[] bytes, int pageIndex, IReadOnlyList<NativePdfTextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (edits.Count == 0) throw new ArgumentException("편집할 텍스트가 없습니다.", nameof(edits));
        if (edits.All(e => e.Text == e.Block.Text && e.DeltaX == 0 && e.DeltaY == 0))
            return new(bytes, edits[0].Block);
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        NativePdfTextBlock? layout = null;
        using (var doc = new PdfDocument(new PdfReader(input), new PdfWriter(output), new StampingProperties().UseAppendMode()))
        {
            var page = doc.GetPage(pageIndex + 1);
            var data = Read(page);
            var replacements = new Dictionary<string, List<NativePdfGlyph>>();
            foreach (var edit in edits)
            {
                var ids = edit.Block.Runs.Select(r => r.Id).ToHashSet();
                var original = data.Runs.Where(r => ids.Contains(r.Id)).ToList();
                if (original.Count != edit.Block.Runs.Count || original.Any(r =>
                    !r.Glyphs.SelectMany(g => g.Code).SequenceEqual(edit.Block.Runs.Single(b => b.Id == r.Id).Glyphs.SelectMany(g => g.Code))))
                    throw new InvalidOperationException("PDF 내용이 변경되었습니다. 텍스트를 다시 선택해 주세요.");
                if (original.Any(r => r.IsVertical || Math.Abs(r.HorizontalScale * r.FontSize) < 0.0001 || r.RenderMode >= 4))
                    throw new InvalidOperationException("세로쓰기 또는 클리핑 텍스트의 원본 보존 편집은 아직 지원하지 않습니다.");
                layout = Layout(doc, data, edit);
                foreach (var run in original)
                {
                    var placed = layout.Glyphs.Where(g => !g.IsVirtual && g.RunId == run.Id).ToList();
                    bool unchanged = run.Glyphs.Count == placed.Count && run.Glyphs.Zip(placed).All(pair =>
                        pair.First.Code.SequenceEqual(pair.Second.Code) && Math.Abs(pair.First.Origin.X - pair.Second.Origin.X) < .00001 &&
                        Math.Abs(pair.First.Origin.Y - pair.Second.Origin.Y) < .00001);
                    if (unchanged) continue;
                    if (!replacements.TryAdd(run.Id, placed))
                        throw new InvalidOperationException("겹치는 텍스트 선택입니다.");
                }
            }
            var contents = new PdfArray();
            bool modified = false;
            foreach (var source in data.Sources)
            {
                var patched = PatchSource(doc, source, data, replacements);
                contents.Add(patched ?? source.Stream);
                modified |= patched != null;
            }
            var pageResources = CopyDictionary(page.GetResources().GetPdfObject());
            var pageXObjects = CopyDictionary(pageResources.GetAsDictionary(PdfName.XObject) ?? new PdfDictionary());
            foreach (var source in data.Sources)
            {
                var xobjects = source.Resources.GetResource(PdfName.XObject);
                if (xobjects != null) foreach (var name in xobjects.KeySet()) pageXObjects.Put(name, xobjects.Get(name));
            }
            pageResources.Put(PdfName.XObject, pageXObjects);
            page.GetPdfObject().Put(PdfName.Resources, pageResources);
            // A trailing line break/space changes the live caret layout without
            // painting any glyph. Keep that edit buffer and the exact source
            // bytes until a subsequent input actually changes painted content.
            if (!modified) return new(bytes, layout!);
            page.GetPdfObject().Put(PdfName.Contents, contents);
            page.SetModified();
        }
        byte[] resultBytes = output.ToArray();
        using var finalDoc = new PdfDocument(new PdfReader(new MemoryStream(resultBytes)));
        var finalPage = finalDoc.GetPage(pageIndex + 1); var finalBox = finalPage.GetCropBox();
        var finalMap = new PageMap(finalBox.GetX(), finalBox.GetY(), finalBox.GetWidth(), finalBox.GetHeight(), ((finalPage.GetRotation() % 360) + 360) % 360);
        layout = ApplyInkBounds(resultBytes, pageIndex, new() { layout! }, finalMap)[0];
        if (edits.Count == 1 && edits[0].Text != edits[0].Block.Text && layout.Bounds.Bottom > edits[0].Block.Bounds.Bottom + .5)
        {
            var original = edits[0].Block;
            var ids = original.Runs.Select(r => r.Id).ToHashSet();
            var other = new NativePdfTextService().Extract(bytes, pageIndex).SelectMany(b => b.Glyphs)
                .Where(g => !g.IsVirtual && !ids.Contains(g.RunId)).ToList();
            foreach (var glyph in layout.Glyphs.Where(g => !g.IsVirtual && g.Bounds.Bottom > original.Bounds.Bottom + .5))
                if (other.Any(g => Math.Min(g.Bounds.Right, glyph.Bounds.Right) - Math.Max(g.Bounds.X, glyph.Bounds.X) > .25 &&
                    Math.Min(g.Bounds.Bottom, glyph.Bounds.Bottom) - Math.Max(g.Bounds.Y, glyph.Bounds.Y) > .25))
                    throw new InvalidOperationException("추가한 줄이 다른 원본 텍스트와 겹칩니다. 내용을 줄이거나 텍스트 영역을 먼저 이동해 주세요.");
        }
        return new(resultBytes, layout);
    }

    private static List<NativePdfTextBlock> ApplyInkBounds(byte[] bytes, int pageIndex, List<NativePdfTextBlock> blocks, PageMap map)
    {
        var boxes = NativePdfGlyphGeometry.Read(bytes, pageIndex).Select(g =>
        {
            var a = map.Map(g.Box.X, g.Box.Y); var b = map.Map(g.Box.Right, g.Box.Bottom);
            return (Origin: map.Map(g.X, g.Y), Box: new PdfTextBox(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
        }).ToLookup(g => ((int)Math.Round(g.Origin.X), (int)Math.Round(g.Origin.Y)));
        NativePdfGlyph Resolve(NativePdfGlyph glyph)
        {
            if (glyph.IsVirtual || string.IsNullOrWhiteSpace(glyph.Text)) return glyph;
            var matches = boxes[((int)Math.Round(glyph.Origin.X), (int)Math.Round(glyph.Origin.Y))]
                .Where(g => Math.Abs(g.Origin.X - glyph.Origin.X) < .1 && Math.Abs(g.Origin.Y - glyph.Origin.Y) < .1).ToList();
            return matches.Count == 0 ? glyph : glyph with { Bounds = PdfTextBox.Union(matches.Select(g => g.Box)) };
        }
        return blocks.Select(block =>
        {
            var glyphs = block.Glyphs.Select(Resolve).ToList();
            var lines = glyphs.Where(g => !g.IsVirtual && !string.IsNullOrWhiteSpace(g.Text))
                .GroupBy(g => Math.Round(g.Origin.Y, 1)).Select(g => PdfTextBox.Union(g.Select(c => c.Bounds))).ToList();
            // For angled text keep the complete transformed object outline.
            if (block.Runs.SelectMany(r => r.Glyphs).Any(g => Math.Abs(g.End.Y - g.Origin.Y) > .1))
                lines = glyphs.Count == 0 ? new() : new() { PdfTextBox.Union(glyphs.Select(g => g.Bounds)) };
            return block with { Glyphs = glyphs, Runs = block.Runs.Select(r => r with { Glyphs = r.Glyphs.Select(Resolve).ToList() }).ToList(),
                Bounds = PdfTextBox.Union(lines), Lines = lines };
        }).ToList();
    }

    // Build the edit using the original font's character codes and advances.
    // Equal prefix/suffix glyphs retain their encoded bytes, including ligatures.
    private static NativePdfTextBlock Layout(PdfDocument doc, PageData data, NativePdfTextEdit edit)
    {
        var block = edit.Block;
        string text = edit.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var original = block.Glyphs;
        var originalOrder = original.Select((glyph, index) => (glyph, index)).Where(p => !p.glyph.IsVirtual)
            .ToDictionary(p => p.glyph.Code, p => p.index);
        var runs = block.Runs.ToDictionary(r => r.Id);
        var fonts = new Dictionary<string, PdfFont>();
        var coverage = new Dictionary<int, HashSet<string>?>();
        PdfFont Font(NativePdfRun run)
        {
            if (!fonts.TryGetValue(run.Id, out var f))
                fonts[run.Id] = f = PdfFontFactory.CreateFont((PdfDictionary)doc.GetPdfObject(run.FontObjectNumber));
            return f;
        }
        var matches = MatchCharacters(block.Text, text);
        var tokens = new List<NativePdfGlyph>();
        int precedingSourceIndex = -1;
        for (int index = 0; index < text.Length;)
        {
            int sourceIndex = matches.GetValueOrDefault(index, -1);
            var existing = sourceIndex < 0 ? null : original.FirstOrDefault(g => g.TextIndex == sourceIndex &&
                index + g.Text.Length <= text.Length && text.AsSpan(index, g.Text.Length).SequenceEqual(g.Text));
            if (existing != null)
            {
                tokens.Add(existing with { TextIndex = index }); precedingSourceIndex = existing.TextIndex + existing.Text.Length - 1;
                index += existing.Text.Length; continue;
            }
            string value = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? text.Substring(index, 2) : text.Substring(index, 1);
            var near = original.LastOrDefault(g => g.TextIndex <= precedingSourceIndex && !g.IsVirtual) ?? original.First(g => !g.IsVirtual);
            var run = runs[near.RunId];
            if (value == "\n")
                tokens.Add(near with { Text = value, TextIndex = index, IsVirtual = true, Code = Array.Empty<byte>() });
            else
            {
                var font = Font(run);
                if (value == " ")
                {
                    // PDF word gaps need no painted glyph. Many CFF subsets omit
                    // the space outline entirely but retain its advance in Widths.
                    double spaceAdvance = (font.GetWidth(' ') * run.FontSize / 1000 + run.CharacterSpacing + run.WordSpacing) * run.HorizontalScale;
                    double spaceWidth = DisplayAdvance(run, spaceAdvance, data.Map);
                    tokens.Add(near with { Text = value, Code = Array.Empty<byte>(), Advance = spaceAdvance,
                        IsVirtual = true, IsSoftBreak = false, TextIndex = index,
                        End = new(near.Origin.X + spaceWidth, near.Origin.Y) });
                    index++; continue;
                }
                int unicode = char.ConvertToUtf32(value, 0);
                if (!font.ContainsGlyph(unicode)) throw new InvalidOperationException($"원본 글꼴 '{run.FontName}'에 '{value}' 글리프가 없습니다. 이 글꼴로는 해당 문자를 보존하여 입력할 수 없습니다.");
                byte[] code = font.ConvertToBytes(value);
                if (code.Length == 0 || font.Decode(new PdfString(code)) != value)
                    throw new InvalidOperationException($"원본 글꼴의 문자 인코딩으로 '{value}'를 표현할 수 없습니다.");
                if (!coverage.TryGetValue(run.FontObjectNumber, out var names))
                {
                    var program = font.GetPdfObject().GetAsDictionary(PdfName.FontDescriptor)?.GetAsStream(PdfName.FontFile3);
                    names = program?.GetAsName(PdfName.Subtype)?.GetValue() == "Type1C"
                        ? new CffCoverage(program.GetBytes()).ReadNames() : null;
                    coverage[run.FontObjectNumber] = names;
                }
                if (names != null && font is PdfType1Font type1 &&
                    (code.Length != 1 || !names.Contains(type1.GetFontEncoding().GetDifference(code[0]) ?? AdobeGlyphList.UnicodeToName(unicode))))
                    throw new InvalidOperationException($"원본 서브셋 글꼴 '{run.FontName}'에는 '{value}'의 실제 윤곽선이 포함되어 있지 않습니다.");
                double advance = (font.GetContentWidth(new PdfString(code)) * run.FontSize / 1000 + run.CharacterSpacing +
                    (code.Length == 1 && code[0] == 32 ? run.WordSpacing : 0)) * run.HorizontalScale;
                tokens.Add(near with { Text = value, Code = code, Advance = advance, TextIndex = index, IsVirtual = false });
            }
            index += value.Length;
        }
        if (text == block.Text)
        {
            var translated = original.Select(g => Translate(g, edit.DeltaX, edit.DeltaY)).ToList();
            return block with { Glyphs = translated, Bounds = Shift(block.Bounds, edit.DeltaX, edit.DeltaY),
                Lines = block.Lines.Select(b => Shift(b, edit.DeltaX, edit.DeltaY)).ToList() };
        }
        if (tokens.Count == 0) return block with { Text = "", Glyphs = new(), Lines = new(), Bounds = default };
        bool horizontal = original.Where(g => !g.IsVirtual).All(g => Math.Abs(g.End.Y - g.Origin.Y) < 0.1 && g.End.X >= g.Origin.X);
        if (!horizontal) throw new InvalidOperationException("회전되거나 오른쪽에서 왼쪽으로 배치된 텍스트는 현재 이동과 전체 삭제만 지원합니다.");
        var anchors = new List<PdfTextPoint>();
        bool startOfLine = true;
        foreach (var glyph in original)
        {
            if (glyph.Text == "\n") { startOfLine = true; continue; }
            if (startOfLine && !glyph.IsVirtual) { anchors.Add(glyph.Origin); startOfLine = false; }
        }
        int lineIndex = 0;
        double left = anchors[0].X, x = left;
        double baseline = anchors[0].Y;
        double right = Math.Max(block.Bounds.Right, original.Max(g => g.End.X));
        var placed = new List<NativePdfGlyph>();
        NativePdfGlyph? previous = null;
        double Width(NativePdfGlyph token)
        {
            var run = runs[token.RunId];
            if (token.Text == "\n") return 0;
            return token.IsVirtual ? Math.Max(token.End.X - token.Origin.X, 0) : DisplayAdvance(run, token.Advance, data.Map);
        }
        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            var run = runs[token.RunId];
            if (token.Text == "\n")
            {
                lineIndex++;
                var next = lineIndex < anchors.Count ? anchors[lineIndex] : new PdfTextPoint(anchors[0].X,
                    baseline + Math.Max(block.LineHeight, runs.Values.Max(r => r.DisplayFontSize) * 1.1));
                placed.Add(token with { Origin = new(x + edit.DeltaX, baseline + edit.DeltaY), End = new(next.X + edit.DeltaX, next.Y + edit.DeltaY) });
                left = next.X; x = left; baseline = next.Y; previous = null; continue;
            }
            double advance = Width(token);
            // Preserve explicit PDF kerning between adjacent unchanged glyphs.
            double kern = previous != null && !previous.IsVirtual && !token.IsVirtual &&
                originalOrder.TryGetValue(previous.Code, out int previousIndex) && originalOrder.TryGetValue(token.Code, out int nextIndex) &&
                nextIndex == previousIndex + 1 &&
                Math.Abs(previous.Origin.Y - token.Origin.Y) < 0.1 ? token.Origin.X - previous.Origin.X - Width(previous) : 0;
            if (Math.Abs(kern) < run.DisplayFontSize * 0.5) x += kern;
            // Preserve line origins; never collapse/reflow a whole PDF paragraph
            // after a single keystroke. Overflow is explicit instead of silently
            // painting into the next line or adjacent column.
            double trailingSpacing = token.IsVirtual ? 0 : DisplayAdvance(run, run.CharacterSpacing * run.HorizontalScale, data.Map);
            if (x + advance - trailingSpacing > right + .5 && !string.IsNullOrWhiteSpace(token.Text))
                throw new InvalidOperationException("원래 텍스트 영역의 줄 폭을 넘었습니다. 줄바꿈을 추가하거나 '기존 방식'으로 전환해 주세요.");
            double dx = x - token.Origin.X + edit.DeltaX, dy = baseline - token.Origin.Y + edit.DeltaY;
            placed.Add(Translate(token, dx, dy) with { End = new(x + advance + edit.DeltaX, baseline + edit.DeltaY) });
            x += advance; previous = token;
        }
        // Unedited physical lines keep their exact source operands/coordinates;
        // accumulating float advances needlessly changes their sub-pixel layout.
        var sourceLines = block.Text.Split('\n');
        var editedLines = text.Split('\n');
        var preserved = new Dictionary<int, NativePdfGlyph>();
        int sourceStart = 0, editedStart = 0;
        for (int i = 0; i < Math.Min(sourceLines.Length, editedLines.Length); i++)
        {
            if (sourceLines[i] == editedLines[i])
                foreach (var glyph in original.Where(g => g.TextIndex >= sourceStart && g.TextIndex < sourceStart + sourceLines[i].Length))
                {
                    int textIndex = editedStart + glyph.TextIndex - sourceStart;
                    preserved[textIndex] = Translate(glyph, edit.DeltaX, edit.DeltaY) with { TextIndex = textIndex };
                }
            sourceStart += sourceLines[i].Length + 1; editedStart += editedLines[i].Length + 1;
        }
        placed = placed.Select(g => preserved.GetValueOrDefault(g.TextIndex, g)).ToList();
        if (text.StartsWith(block.Text, StringComparison.Ordinal) && text[block.Text.Length..].All(char.IsWhiteSpace))
        {
            // Extending the insertion area with blank lines/spaces must preserve
            // the original prefix, including sub-pixel advances and kerning.
            var prefix = original.ToDictionary(g => g.TextIndex);
            placed = placed.Select(g => prefix.TryGetValue(g.TextIndex, out var source)
                ? Translate(source, edit.DeltaX, edit.DeltaY) : g).ToList();
        }
        var lineBoxes = placed.Where(g => g.Text != "\n").GroupBy(g => Math.Round(g.Origin.Y, 2))
            .Select(g => PdfTextBox.Union(g.Select(c => c.Bounds))).ToList();
        return block with { Text = text, Glyphs = placed, Lines = lineBoxes, Bounds = PdfTextBox.Union(lineBoxes) };
    }

    private static PdfTextBox Shift(PdfTextBox box, double x, double y) => box with { X = box.X + x, Y = box.Y + y };

    // Hirschberg LCS: preserve style/bytes of unchanged spans even when several
    // separate edits occur in one session. Memory stays linear in paragraph size.
    private static Dictionary<int, int> MatchCharacters(string source, string text)
    {
        var result = new Dictionary<int, int>();
        int prefix = 0, suffix = 0;
        while (prefix < source.Length && prefix < text.Length && source[prefix] == text[prefix])
        { result[prefix] = prefix; prefix++; }
        while (suffix < source.Length - prefix && suffix < text.Length - prefix && source[^(suffix + 1)] == text[^(suffix + 1)])
        { result[text.Length - suffix - 1] = source.Length - suffix - 1; suffix++; }
        int[] Scores(int sa, int length, int ta, int width, bool reverse)
        {
            var row = new int[width + 1];
            for (int i = 0; i < length; i++)
            {
                int diagonal = 0;
                for (int j = 1; j <= width; j++)
                {
                    int above = row[j];
                    char a = source[reverse ? sa + length - 1 - i : sa + i];
                    char b = text[reverse ? ta + width - j : ta + j - 1];
                    row[j] = a == b ? diagonal + 1 : Math.Max(row[j], row[j - 1]); diagonal = above;
                }
            }
            return row;
        }
        void Match(int sa, int n, int ta, int m)
        {
            if (n == 0 || m == 0) return;
            if (n == 1)
            {
                for (int i = 0; i < m; i++) if (source[sa] == text[ta + i]) { result[ta + i] = sa; break; }
                return;
            }
            int half = n / 2;
            var a = Scores(sa, half, ta, m, false); var b = Scores(sa + half, n - half, ta, m, true);
            int split = 0, best = -1;
            for (int i = 0; i <= m; i++) if (a[i] + b[m - i] > best) { split = i; best = a[i] + b[m - i]; }
            Match(sa, half, ta, split); Match(sa + half, n - half, ta + split, m - split);
        }
        Match(prefix, source.Length - prefix - suffix, prefix, text.Length - prefix - suffix);
        return result;
    }
    private static NativePdfGlyph Translate(NativePdfGlyph g, double x, double y) => g with
    { Origin = new(g.Origin.X + x, g.Origin.Y + y), End = new(g.End.X + x, g.End.Y + y), Bounds = Shift(g.Bounds, x, y) };
    private static double DisplayAdvance(NativePdfRun run, double advance, PageMap map)
    {
        var v = new Vector((float)advance, 0, 0).Cross(MatrixOf(run.TextMatrix).Multiply(MatrixOf(run.Ctm)));
        var a = map.Map(0, 0); var b = map.Map(v.Get(0), v.Get(1));
        return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
    }

    private static PdfStream? PatchSource(PdfDocument doc, Source source, PageData data,
        Dictionary<string, List<NativePdfGlyph>> replacements)
    {
        var patches = new Dictionary<Operation, byte[]>();
        PdfDictionary? resources = null;
        foreach (var (op, child) in source.Children)
        {
            var changed = PatchSource(doc, child, data, replacements);
            if (changed == null) continue;
            resources ??= CopyDictionary(source.Resources.GetPdfObject());
            var xobjects = CopyDictionary(resources.GetAsDictionary(PdfName.XObject) ?? new PdfDictionary());
            var name = new PdfName("NativeEdit" + changed.GetIndirectReference().GetObjNumber());
            xobjects.Put(name, changed); resources.Put(PdfName.XObject, xobjects);
            patches[op] = Encoding.ASCII.GetBytes($"\n{name} Do\n");
        }
        foreach (var op in source.Operations.Where(o => o.TextIndex >= 0))
        {
            var runs = data.Runs.Where(r => r.SourcePath == source.Path && r.OperationIndex == op.TextIndex).ToList();
            if (!runs.Any(r => replacements.ContainsKey(r.Id))) continue;
            patches[op] = RewriteOperation(op, runs, replacements, data.Map);
        }
        if (patches.Count == 0) return null;
        using var buffer = new MemoryStream(); int position = 0;
        foreach (var (op, patch) in patches.OrderBy(p => p.Key.Start))
        {
            buffer.Write(source.Bytes, position, op.Start - position); buffer.Write(patch); position = op.End;
        }
        buffer.Write(source.Bytes, position, source.Bytes.Length - position);
        var stream = new PdfStream(buffer.ToArray());
        foreach (var key in source.Stream.KeySet())
            if (!key.Equals(PdfName.Length) && !key.Equals(PdfName.Filter) && !key.Equals(PdfName.DecodeParms))
                stream.Put(key, source.Stream.Get(key));
        if (resources != null) stream.Put(PdfName.Resources, resources);
        // Root streams have no resources: page resources are updated separately.
        if (!source.Path.Contains('/') && resources != null)
        {
            // Store the detached resource dictionary for the caller (never mutate inherited resources).
            source.Resources = new PdfResources(resources);
        }
        stream.MakeIndirect(doc);
        return stream;
    }

    private static PdfDictionary CopyDictionary(PdfDictionary dictionary)
    { var result = new PdfDictionary(); foreach (var key in dictionary.KeySet()) result.Put(key, dictionary.Get(key)); return result; }
    private static string N(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private sealed class CffCoverage : CFFFont
    {
        private readonly byte[] bytes;
        public CffCoverage(byte[] data) : base(data) { bytes = data; }
        public HashSet<string> ReadNames()
        {
            try
            {
                var f = fonts[0];
                int position = f.GetCharstringsOffset();
                int Read8() => bytes[position++];
                int Read16() => (Read8() << 8) | Read8();
                int count = Read16();
                int offset = f.GetCharsetOffset();
                var names = new HashSet<string>(StringComparer.Ordinal) { ".notdef" };
                if (offset == 0) { for (int i = 1; i < count; i++) names.Add(GetString((char)i)); return names; }
                if (offset is 1 or 2) throw new InvalidOperationException("Expert CFF 문자셋은 아직 편집할 수 없습니다.");
                position = offset; int format = Read8();
                int glyph = 1;
                while (glyph < count)
                {
                    int sid = Read16();
                    int remaining = format switch { 0 => 0, 1 => Read8(), 2 => Read16(), _ => throw new InvalidOperationException("잘못된 CFF 문자셋입니다.") };
                    for (int j = 0; j <= remaining && glyph < count; j++, glyph++) names.Add(GetString((char)(sid + j)));
                }
                return names;
            }
            finally { buf.Close(); }
        }
    }

    private static byte[] RewriteOperation(Operation op, List<NativePdfRun> runs,
        Dictionary<string, List<NativePdfGlyph>> replacements, PageMap map)
    {
        using var buffer = new MemoryStream();
        var output = new PdfOutputStream(buffer);
        void Write(string value) => buffer.Write(Encoding.ASCII.GetBytes(value));
        Write("\n");
        if (op.Name == "\"") { output.Write(op.Items[0]); Write(" Tw\n"); output.Write(op.Items[1]); Write(" Tc\nT*\n"); }
        else if (op.Name == "'") Write("T*\n");
        var operands = op.Name == "TJ" ? ((PdfArray)op.Items[0]).ToList() : new List<PdfObject> { op.Items[op.Name == "\"" ? 2 : 0] };
        int index = 0;
        foreach (var operand in operands)
        {
            if (operand is PdfNumber number) { Write($"[{N(number.DoubleValue())}] TJ\n"); continue; }
            if (operand is not PdfString str) continue;
            int operandIndex = index++;
            var run = runs.SingleOrDefault(r => r.OperandIndex == operandIndex);
            if (run == null || !replacements.TryGetValue(run.Id, out var glyphs)) { output.Write(str); Write(" Tj\n"); continue; }
            double cursor = 0;
            double rise = run.Rise;
            var matrix = MatrixOf(run.TextMatrix).Multiply(MatrixOf(run.Ctm));
            double a = matrix.Get(0), b = matrix.Get(1), c = matrix.Get(3), d = matrix.Get(4);
            double determinant = a * d - b * c;
            if (Math.Abs(determinant) < 1e-12) throw new InvalidOperationException("역변환할 수 없는 PDF 텍스트 행렬입니다.");
            foreach (var glyph in glyphs)
            {
                var originalOrigin = run.Glyphs[0].Origin;
                var delta = map.UnmapVector(glyph.Origin.X - originalOrigin.X, glyph.Origin.Y - originalOrigin.Y);
                double textX = (delta.X * d - delta.Y * c) / determinant;
                double textY = (delta.Y * a - delta.X * b) / determinant;
                // Position with text operators only. Windows.Data.Pdf restores
                // its text cursor across q/Q while other engines advance it;
                // putting every glyph inside q/cm/Tj/Q collapses text there.
                // TJ advances the actual text cursor and Ts places the baseline
                // without replacing the original text or line matrix.
                double shift = -(textX - cursor) * 1000 / (run.FontSize * run.HorizontalScale);
                if (Math.Abs(shift) > .000001) Write($"[{N(shift)}] TJ\n");
                double nextRise = run.Rise + textY;
                if (Math.Abs(nextRise - rise) > .000001) { Write($"{N(nextRise)} Ts\n"); rise = nextRise; }
                Write($"<{Convert.ToHexString(glyph.Code)}> Tj\n");
                cursor = textX + glyph.Advance;
            }
            if (Math.Abs(rise - run.Rise) > .000001) Write($"{N(run.Rise)} Ts\n");
            // Leave following strings, relative positioning and T* at exactly
            // their original origin, independently of the replacement's length.
            double adjustment = -(run.OriginalAdvance - cursor) * 1000 / (run.FontSize * run.HorizontalScale);
            Write($"[{N(adjustment)}] TJ\n");
        }
        return buffer.ToArray();
    }
}
