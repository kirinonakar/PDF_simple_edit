using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using PDF_simple_edit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PDF_simple_edit.Services;

internal static class PdfGraphicTransformService
{
    public static byte[] Transform(byte[] bytes, int pageIndex,
        IReadOnlyList<(PdfAnnotation Source, PdfAffineTransform Transform)> edits)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(bytes)), new PdfWriter(output)))
        {
            var page = doc.GetPage(pageIndex + 1);
            var display = PdfPageCoordinates.ToDisplay(page);
            var targets = edits.SelectMany(edit =>
            {
                var operations = edit.Source.GraphicOperations.Count > 0 ? edit.Source.GraphicOperations : new()
                {
                    new() { StreamIndex = edit.Source.ContentStreamIndex, StreamObjectNumber = edit.Source.ContentStreamObjectNumber,
                        OperationIndex = edit.Source.OperationIndex, IsImageOperation = true, Ctm = edit.Source.GraphicCtm }
                };
                var pageTransform = display.Inverse().After(edit.Transform).After(display);
                return operations.Select(op => (Operation: op, Transform: op.Ctm.Inverse().After(pageTransform).After(op.Ctm)));
            }).ToList();
            foreach (var group in targets.GroupBy(t => (t.Operation.StreamObjectNumber, t.Operation.StreamIndex)))
            {
                var stream = group.Key.StreamObjectNumber > 0 ? doc.GetPdfObject(group.Key.StreamObjectNumber) as PdfStream
                    : page.GetContentStream(group.Key.StreamIndex);
                if (stream == null) throw new InvalidOperationException("그래픽 원본을 찾을 수 없습니다.");
                var parser = new PdfCanvasParser(new PdfTokenizer(new RandomAccessFileOrArray(new RandomAccessSourceFactory().CreateSource(stream.GetBytes()))));
                var operations = new List<List<PdfObject>>();
                while (true)
                {
                    var operation = parser.Parse(new List<PdfObject>());
                    if (operation.Count == 0) break;
                    operations.Add(operation.ToList());
                }
                var before = new Dictionary<int, string>();
                var after = new Dictionary<int, string>();
                int pathIndex = -1, imageIndex = -1, shadingIndex = -1;
                var pathCommands = new List<int>();
                int handled = 0;
                for (int i = 0; i < operations.Count; i++)
                {
                    string name = operations[i][^1].ToString()!;
                    if (name is "m" or "l" or "c" or "v" or "y" or "re" or "h") pathCommands.Add(i);
                    bool paint = name is "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n";
                    if (paint) pathIndex++;
                    if (name == "Do") imageIndex++;
                    if (name == "sh") shadingIndex++;
                    var matches = group.Where(t => !t.Operation.IsTextOperation &&
                        (t.Operation.IsImageOperation ? name == "Do" && t.Operation.OperationIndex == imageIndex
                        : t.Operation.IsShadingOperation ? name == "sh" && t.Operation.OperationIndex == shadingIndex
                        : paint && t.Operation.OperationIndex == pathIndex)).ToList();
                    if (matches.Count > 1) throw new InvalidOperationException("겹치는 그래픽 선택입니다.");
                    if (matches.Count == 1)
                    {
                        var target = matches[0];
                        if (target.Operation.IsClipping || target.Operation.IsShadingOperation)
                            throw new InvalidOperationException("공유 클리핑 영역이 있는 그래픽은 변환할 수 없습니다.");
                        if (paint)
                        {
                            if (pathCommands.Count == 0) throw new InvalidOperationException("그래픽 경로를 찾을 수 없습니다.");
                            // Path operands are local user coordinates. Conjugation
                            // includes the containing form's full placement matrix.
                            foreach (int command in pathCommands)
                                operations[command] = TransformPath(operations[command], target.Transform);
                            double scale = Math.Sqrt(Math.Abs(target.Transform.A * target.Transform.D - target.Transform.B * target.Transform.C));
                            before[i] = Number(target.Operation.LineWidth * scale) + " w\n";
                            after[i] = Number(target.Operation.LineWidth) + " w\n";
                        }
                        else
                        {
                            before[i] = target.Transform.Command;
                            after[i] = target.Transform.Inverse().Command;
                        }
                        handled++;
                    }
                    if (paint) pathCommands.Clear();
                }
                if (handled != group.Count()) throw new InvalidOperationException("그래픽이 변경되었습니다. 다시 선택해 주세요.");
                using var buffer = new MemoryStream();
                var writer = new PdfOutputStream(buffer);
                for (int i = 0; i < operations.Count; i++)
                {
                    if (before.TryGetValue(i, out var prefix)) buffer.Write(Encoding.ASCII.GetBytes(prefix));
                    foreach (var operand in operations[i]) { writer.Write(operand); writer.WriteSpace(); }
                    writer.WriteNewLine();
                    if (after.TryGetValue(i, out var suffix)) buffer.Write(Encoding.ASCII.GetBytes(suffix));
                }
                stream.SetData(buffer.ToArray());
            }
        }
        return output.ToArray();
    }

    private static string Number(double value) => value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
    private static List<PdfObject> TransformPath(List<PdfObject> source, PdfAffineTransform transform)
    {
        string name = source[^1].ToString()!;
        if (name == "h") return source;
        var result = new List<PdfObject>();
        void Point(double x, double y) { var p = transform.Map(x, y); result.Add(new PdfNumber(p.X)); result.Add(new PdfNumber(p.Y)); }
        double Value(int i) => ((PdfNumber)source[i]).DoubleValue();
        if (name == "re")
        {
            double x = Value(0), y = Value(1), w = Value(2), h = Value(3);
            Point(x, y); result.Add(new PdfLiteral("m"));
            Point(x + w, y); result.Add(new PdfLiteral("l"));
            Point(x + w, y + h); result.Add(new PdfLiteral("l"));
            Point(x, y + h); result.Add(new PdfLiteral("l")); result.Add(new PdfLiteral("h"));
        }
        else
        {
            for (int i = 0; i < source.Count - 1; i += 2) Point(Value(i), Value(i + 1));
            result.Add(source[^1]);
        }
        return result;
    }
}
