using iText.IO.Font;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PDF_simple_edit.Services;

/// <summary>
/// Measures embedded CFF Type 2 outlines where iText's Glyph.GetBbox() is absent.
/// CFF dictionaries/names come from iText; only outline measurement is implemented here.
/// See Adobe technical notes 5176 (CFF) and 5177 (Type 2 charstrings).
/// Unsupported or malformed outlines return null so the caller retains iText's metrics.
/// </summary>
internal sealed class CffGlyphBounds : CFFFont
{
    private readonly byte[] _data;
    private readonly int[] _charStrings, _globalSubrs;
    private readonly int[][] _localSubrs;
    private readonly int[] _fdSelect;
    private readonly double[][] _matrices;
    private readonly Dictionary<int, int> _cids = new();
    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<int, double[]?> _cache = new();
    private readonly bool _cid;

    public CffGlyphBounds(byte[] data) : base(data)
    {
        _data = data;
        try
        {
            if (data[0] != 1 || fonts.Length != 1 || fonts[0].GetCharstringType() != 2)
                throw new InvalidDataException("Unsupported CFF font format.");
            var font = fonts[0];
            _cid = font.IsCID();
            _charStrings = Index(font.GetCharstringsOffset());
            int count = _charStrings.Length - 1;
            _globalSubrs = Index(gsubrIndexOffset);
            _fdSelect = new int[count];
            var top = Dictionary(topdictOffsets[0], topdictOffsets[1]);
            double[] topMatrix = top.GetValueOrDefault(1207) ?? new double[] { .001, 0, 0, .001, 0, 0 };
            if (_cid)
            {
                int[] fdArray = Index(font.GetFdarrayOffset());
                _localSubrs = new int[fdArray.Length - 1][];
                _matrices = new double[_localSubrs.Length][];
                for (int i = 0; i < _localSubrs.Length; i++)
                {
                    var fd = Dictionary(fdArray[i], fdArray[i + 1]);
                    _localSubrs[i] = Subrs(fd);
                    _matrices[i] = fd.TryGetValue(1207, out var fm) ? Multiply(fm, topMatrix) : topMatrix;
                }
                int p = font.GetFdselectOffset();
                int format = Byte(ref p);
                if (format == 0) for (int i = 0; i < count; i++) _fdSelect[i] = Byte(ref p);
                else if (format == 3)
                {
                    int ranges = UInt(ref p, 2), first = UInt(ref p, 2);
                    for (int r = 0; r < ranges; r++)
                    {
                        int fd = Byte(ref p), next = UInt(ref p, 2);
                        if (first > next || next > count) throw new InvalidDataException("Invalid CFF FDSelect.");
                        for (int i = first; i < next; i++) _fdSelect[i] = fd;
                        first = next;
                    }
                }
                else throw new InvalidDataException("Unsupported CFF FDSelect.");
            }
            else { _localSubrs = new[] { Subrs(top) }; _matrices = new[] { topMatrix }; }
            int charset = font.GetCharsetOffset();
            int[] sids = new int[count];
            if (charset == 0) for (int i = 1; i < count; i++) sids[i] = i;
            else if (charset is 1 or 2) throw new InvalidDataException("Predefined expert CFF charset.");
            else
            {
                int p = charset, format = Byte(ref p), glyph = 1;
                while (glyph < count)
                {
                    int sid = UInt(ref p, 2);
                    int remaining = format switch { 0 => 0, 1 => Byte(ref p), 2 => UInt(ref p, 2),
                        _ => throw new InvalidDataException("Invalid CFF charset.") };
                    if (remaining >= count - glyph) throw new InvalidDataException("Invalid CFF charset range.");
                    for (int j = 0; j <= remaining; j++) sids[glyph++] = sid + j;
                }
            }
            for (int i = 0; i < count; i++)
                if (_cid) _cids[sids[i]] = i;
                else if (GetString((char)sids[i]) is { } name) _names[name] = i;
        }
        finally { buf.Close(); }
    }

    public double[]? Get(int code, string? name, bool codeIsGlyphId = false)
    {
        int gid = _cid ? _cids.GetValueOrDefault(code, -1) : codeIsGlyphId ? code :
            name == null ? -1 : _names.GetValueOrDefault(name, -1);
        if (gid < 0 || gid + 1 >= _charStrings.Length) return null;
        if (_cache.TryGetValue(gid, out var box)) return box;
        try
        {
            int fd = _fdSelect[gid];
            var outline = new Outline(_data, _globalSubrs, _localSubrs[fd], _matrices[fd]);
            outline.Execute(_charStrings[gid], _charStrings[gid + 1]);
            box = outline.Bounds;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Debug.WriteLine($"CFF glyph bounds unavailable: {ex.Message}"); box = null; }
        return _cache[gid] = box;
    }

    private int Byte(ref int p) => _data[p++];
    private int UInt(ref int p, int size)
    {
        int value = 0;
        for (int i = 0; i < size; i++) value = checked(value * 256 + Byte(ref p));
        return value;
    }
    private int[] Index(int offset)
    {
        int p = offset, count = UInt(ref p, 2);
        if (count == 0) return Array.Empty<int>();
        int size = Byte(ref p);
        if (size is < 1 or > 4) throw new InvalidDataException("Invalid CFF INDEX size.");
        int start = checked(p + (count + 1) * size - 1);
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            offsets[i] = checked(start + UInt(ref p, size));
            if (offsets[i] < start + 1 || offsets[i] > _data.Length || (i > 0 && offsets[i] < offsets[i - 1]))
                throw new InvalidDataException("Invalid CFF INDEX offset.");
        }
        return offsets;
    }
    private Dictionary<int, double[]> Dictionary(int start, int end)
    {
        var result = new Dictionary<int, double[]>();
        var values = new List<double>();
        for (int p = start; p < end;)
        {
            int b = Byte(ref p);
            if (b == 30)
            {
                var number = new System.Text.StringBuilder(); bool done = false;
                while (!done)
                {
                    int pair = Byte(ref p);
                    foreach (int nibble in new[] { pair >> 4, pair & 15 })
                    {
                        if (nibble == 15) { done = true; break; }
                        number.Append(nibble switch { < 10 => nibble.ToString(CultureInfo.InvariantCulture),
                            10 => ".", 11 => "E", 12 => "E-", 14 => "-", _ => throw new InvalidDataException() });
                    }
                }
                values.Add(double.Parse(number.ToString(), CultureInfo.InvariantCulture));
            }
            else if (b == 29) values.Add(unchecked((int)ReadUnsigned32(ref p)));
            else if (b >= 32 || b == 28) values.Add(Number(_data, ref p, b));
            else { int op = b == 12 ? 1200 + Byte(ref p) : b; result[op] = values.ToArray(); values.Clear(); }
        }
        return result;
    }
    private uint ReadUnsigned32(ref int p)
    { uint value = 0; for (int i = 0; i < 4; i++) value = (value << 8) | (uint)Byte(ref p); return value; }
    private int[] Subrs(Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(18, out var region)) return Array.Empty<int>();
        int offset = checked((int)region[1]), end = checked(offset + (int)region[0]);
        var local = Dictionary(offset, end);
        return local.TryGetValue(19, out var subr) ? Index(checked(offset + (int)subr[0])) : Array.Empty<int>();
    }
    private static double[] Multiply(double[] a, double[] b) => new[]
    { a[0]*b[0]+a[1]*b[2], a[0]*b[1]+a[1]*b[3], a[2]*b[0]+a[3]*b[2], a[2]*b[1]+a[3]*b[3],
        a[4]*b[0]+a[5]*b[2]+b[4], a[4]*b[1]+a[5]*b[3]+b[5] };
    private static double Number(byte[] data, ref int p, int b)
    {
        if (b == 28) { int n = (short)((data[p] << 8) | data[p + 1]); p += 2; return n; }
        if (b == 255) { int n = (data[p] << 24) | (data[p+1] << 16) | (data[p+2] << 8) | data[p+3]; p += 4; return n / 65536.0; }
        if (b < 247) return b - 139;
        if (b < 251) return (b - 247) * 256 + data[p++] + 108;
        return -(b - 251) * 256 - data[p++] - 108;
    }

    private sealed class Outline(byte[] data, int[] globalSubrs, int[] localSubrs, double[] matrix)
    {
        private readonly List<double> _stack = new();
        private readonly double[] _transient = new double[32];
        private double _x, _y, _left = double.PositiveInfinity, _bottom = double.PositiveInfinity,
            _right = double.NegativeInfinity, _top = double.NegativeInfinity;
        private int _hints, _budget = 100000;
        private bool _width, _ended;
        public double[]? Bounds => double.IsFinite(_left) && double.IsFinite(_bottom) &&
            double.IsFinite(_right) && double.IsFinite(_top) ? new[] { _left, _bottom, _right, _top } : null;

        private double Pop() { double v = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return v; }
        private (double X, double Y) Map(double x, double y) =>
            ((x * matrix[0] + y * matrix[2] + matrix[4]) * 1000, (x * matrix[1] + y * matrix[3] + matrix[5]) * 1000);
        private void Point(double x, double y)
        { _left = Math.Min(_left, x); _bottom = Math.Min(_bottom, y); _right = Math.Max(_right, x); _top = Math.Max(_top, y); }
        private void Line(double dx, double dy)
        {
            var a = Map(_x, _y); Point(a.X, a.Y); _x += dx; _y += dy;
            var b = Map(_x, _y); Point(b.X, b.Y);
        }
        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var a = Map(_x, _y); var b = Map(_x + dx1, _y + dy1);
            var c = Map(_x + dx1 + dx2, _y + dy1 + dy2);
            _x += dx1 + dx2 + dx3; _y += dy1 + dy2 + dy3;
            var d = Map(_x, _y); Point(a.X, a.Y); Point(d.X, d.Y);
            void Extrema(double p0, double p1, double p2, double p3)
            {
                double qa = -p0 + 3*p1 - 3*p2 + p3, qb = 2*(p0 - 2*p1 + p2), qc = p1 - p0;
                void At(double t)
                {
                    if (t <= 0 || t >= 1) return;
                    double s = 1 - t;
                    Point(s*s*s*a.X + 3*s*s*t*b.X + 3*s*t*t*c.X + t*t*t*d.X,
                        s*s*s*a.Y + 3*s*s*t*b.Y + 3*s*t*t*c.Y + t*t*t*d.Y);
                }
                if (Math.Abs(qa) < 1e-12) { if (Math.Abs(qb) > 1e-12) At(-qc/qb); }
                else
                {
                    double disc = qb*qb - 4*qa*qc;
                    if (disc >= 0) { double root = Math.Sqrt(disc); At((-qb+root)/(2*qa)); At((-qb-root)/(2*qa)); }
                }
            }
            Extrema(a.X, b.X, c.X, d.X); Extrema(a.Y, b.Y, c.Y, d.Y);
        }
        private void Width(int required)
        { if (!_width && _stack.Count > required) _stack.RemoveAt(0); _width = true; }

        public void Execute(int start, int end, int depth = 0)
        {
            if (depth > 10 || start < 0 || end > data.Length || end < start) throw new InvalidDataException("Invalid CFF subroutine.");
            for (int p = start; p < end && !_ended;)
            {
                if (--_budget < 0 || _stack.Count > 48) throw new InvalidDataException("CFF outline limit exceeded.");
                int op = data[p++];
                if (op >= 32 || op == 28) { _stack.Add(Number(data, ref p, op)); continue; }
                if (op is 10 or 29)
                {
                    var subrs = op == 10 ? localSubrs : globalSubrs;
                    int count = subrs.Length - 1, bias = count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
                    int index = checked((int)Pop() + bias);
                    if (index < 0 || index >= count) throw new InvalidDataException("Invalid CFF subroutine index.");
                    Execute(subrs[index], subrs[index + 1], depth + 1); continue;
                }
                if (op == 11) return;
                if (op == 12) { Escape(data[p++]); continue; }
                if (op is 1 or 3 or 18 or 23 or 19 or 20)
                {
                    if (!_width && _stack.Count % 2 != 0) _stack.RemoveAt(0);
                    _width = true; _hints += _stack.Count / 2;
                    if (op is 19 or 20) { p += (_hints + 7) / 8; if (p > end) throw new InvalidDataException("Invalid CFF hint mask."); }
                    _stack.Clear(); continue;
                }
                if (op == 14)
                {
                    Width(0);
                    // Deprecated seac composites need the base/accent outlines; use metrics rather than a partial box.
                    if (_stack.Count != 0) throw new InvalidDataException("Composite CFF endchar.");
                    _ended = true; return;
                }
                if (op is 4 or 21 or 22)
                {
                    Width(op == 21 ? 2 : 1);
                    if (op != 4) _x += _stack[0];
                    if (op != 22) _y += _stack[op == 21 ? 1 : 0];
                    _stack.Clear(); continue;
                }
                double[] s = _stack.ToArray(); int i = 0;
                switch (op)
                {
                    case 5: for (; i < s.Length; i += 2) Line(s[i], s[i+1]); break;
                    case 6: case 7:
                        bool horizontal = op == 6;
                        foreach (double v in s) { Line(horizontal ? v : 0, horizontal ? 0 : v); horizontal = !horizontal; } break;
                    case 8: for (; i < s.Length; i += 6) Curve(s[i],s[i+1],s[i+2],s[i+3],s[i+4],s[i+5]); break;
                    case 24:
                        for (; i < s.Length-2; i += 6) Curve(s[i],s[i+1],s[i+2],s[i+3],s[i+4],s[i+5]);
                        Line(s[i],s[i+1]); break;
                    case 25:
                        for (; i < s.Length-6; i += 2) Line(s[i],s[i+1]);
                        Curve(s[i],s[i+1],s[i+2],s[i+3],s[i+4],s[i+5]); break;
                    case 26: case 27:
                        double first = s.Length % 4 == 1 ? s[i++] : 0;
                        for (; i < s.Length; i += 4)
                        {
                            if (op == 26) Curve(first,s[i],s[i+1],s[i+2],0,s[i+3]);
                            else Curve(s[i],first,s[i+1],s[i+2],s[i+3],0);
                            first = 0;
                        } break;
                    case 30: case 31:
                        bool h = op == 31;
                        while (i < s.Length)
                        {
                            double last = s.Length-i == 5 ? s[i+4] : 0;
                            if (h) Curve(s[i],0,s[i+1],s[i+2],last,s[i+3]);
                            else Curve(0,s[i],s[i+1],s[i+2],s[i+3],last);
                            i += s.Length-i == 5 ? 5 : 4; h = !h;
                        } break;
                    default: throw new InvalidDataException("Unsupported CFF charstring operator.");
                }
                _stack.Clear();
            }
        }

        private void Escape(int op)
        {
            double a, b;
            switch (op)
            {
                case 3: b=Pop(); a=Pop(); _stack.Add(a!=0 && b!=0 ? 1:0); break;
                case 4: b=Pop(); a=Pop(); _stack.Add(a!=0 || b!=0 ? 1:0); break;
                case 5: _stack.Add(Pop()==0 ? 1:0); break;
                case 9: _stack.Add(Math.Abs(Pop())); break;
                case 10: b=Pop(); a=Pop(); _stack.Add(a+b); break;
                case 11: b=Pop(); a=Pop(); _stack.Add(a-b); break;
                case 12: b=Pop(); a=Pop(); _stack.Add(a/b); break;
                case 14: _stack.Add(-Pop()); break;
                case 15: b=Pop(); a=Pop(); _stack.Add(a==b ? 1:0); break;
                case 18: Pop(); break;
                case 20: int slot=(int)Pop(); _transient[slot]=Pop(); break;
                case 21: _stack.Add(_transient[(int)Pop()]); break;
                case 22: b=Pop(); a=Pop(); double v2=Pop(), v1=Pop(); _stack.Add(a<=b ? v1:v2); break;
                case 23: throw new InvalidDataException("Nondeterministic CFF outline.");
                case 24: b=Pop(); a=Pop(); _stack.Add(a*b); break;
                case 26: _stack.Add(Math.Sqrt(Pop())); break;
                case 27: _stack.Add(_stack[^1]); break;
                case 28: b=Pop(); a=Pop(); _stack.Add(b); _stack.Add(a); break;
                case 29: int index=Math.Clamp((int)Pop(),0,_stack.Count-1); _stack.Add(_stack[_stack.Count-1-index]); break;
                case 30:
                    int shift=(int)Pop(), n=(int)Pop();
                    if (n<0 || n>_stack.Count) throw new InvalidDataException("Invalid CFF roll.");
                    if (n==0) break;
                    shift=((shift%n)+n)%n;
                    var tail=_stack.GetRange(_stack.Count-n,n); _stack.RemoveRange(_stack.Count-n,n);
                    _stack.AddRange(tail.Skip(n-shift).Concat(tail.Take(n-shift))); break;
                case 34: case 35: case 36: case 37:
                    var s=_stack.ToArray();
                    if (op==34) { Curve(s[0],0,s[1],s[2],s[3],0); Curve(s[4],0,s[5],-s[2],s[6],0); }
                    else if (op==35) { Curve(s[0],s[1],s[2],s[3],s[4],s[5]); Curve(s[6],s[7],s[8],s[9],s[10],s[11]); }
                    else if (op==36) { Curve(s[0],s[1],s[2],s[3],s[4],0); Curve(s[5],0,s[6],s[7],s[8],-s[1]-s[3]-s[7]); }
                    else
                    {
                        double dx=s[0]+s[2]+s[4]+s[6]+s[8], dy=s[1]+s[3]+s[5]+s[7]+s[9];
                        Curve(s[0],s[1],s[2],s[3],s[4],s[5]);
                        Curve(s[6],s[7],s[8],s[9],Math.Abs(dx)>Math.Abs(dy) ? s[10]:-dx,Math.Abs(dx)>Math.Abs(dy) ? -dy:s[10]);
                    }
                    _stack.Clear(); break;
                default: throw new InvalidDataException("Unsupported CFF escaped operator.");
            }
        }
    }
}
