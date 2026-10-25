using iText.Kernel.Geom;
using PDF_simple_edit.Models;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed record PdfSearchMatch(int PageNumber, IReadOnlyList<IReadOnlyList<Rectangle>> Matches, int SelectedIndex);

public sealed class PdfSearchService : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _snapshot;
    private PdfDocument? _document;
    private string? _query;
    private bool _matchCase;
    private int _lastPage = -1;
    private int _lastMatch = -1;
    private bool _clearRequested;
    private readonly Dictionary<int, PageIndex> _pages = new();
    private readonly Dictionary<int, IReadOnlyList<IReadOnlyList<Rectangle>>> _matches = new();

    public Task<PdfSearchMatch?> FindAsync(byte[] pdfBytes, string query, int currentPageIndex, bool forward, bool matchCase) =>
        Task.Run(() =>
        {
            lock (_gate)
            {
                try { return Find(pdfBytes, query, currentPageIndex, forward, matchCase); }
                finally { if (Volatile.Read(ref _clearRequested)) Clear(); }
            }
        });

    // Tab switches must not block the UI on a page currently being parsed.
    public void Dispose()
    {
        Volatile.Write(ref _clearRequested, true);
        if (!Monitor.TryEnter(_gate)) return;
        try { Clear(); }
        finally { Monitor.Exit(_gate); }
    }

    private void Clear()
    {
        _document?.Close(); _document = null; _snapshot = null;
        _pages.Clear(); _matches.Clear(); _query = null;
        _lastPage = _lastMatch = -1;
        Volatile.Write(ref _clearRequested, false);
    }

    private PdfSearchMatch? Find(byte[] pdfBytes, string query, int currentPageIndex, bool forward, bool matchCase)
    {
        query = Normalize(query);
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (!ReferenceEquals(_snapshot, pdfBytes))
        {
            Clear();
            _document = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes, writable: false)));
            _snapshot = pdfBytes;
        }
        if (_query != query || _matchCase != matchCase)
        {
            _query = query; _matchCase = matchCase;
            _matches.Clear(); _lastPage = _lastMatch = -1;
        }
        var document = _document!;
        int totalPages = document.GetNumberOfPages();
        if (totalPages == 0) return null;
        int start = Math.Clamp(currentPageIndex, 0, totalPages - 1);
        int step = forward ? 1 : -1;
        for (int offset = 0; offset <= totalPages; offset++)
        {
            int pageIndex = (start + step * offset % totalPages + totalPages) % totalPages;
            if (!_matches.TryGetValue(pageIndex, out var matches))
            {
                if (!_pages.TryGetValue(pageIndex, out var index))
                {
                    var page = document.GetPage(pageIndex + 1);
                    var listener = new TextLocationStrategy();
                    new PdfCanvasProcessor(listener).ProcessPageContent(page);
                    _pages[pageIndex] = index = listener.Build(PdfPageCoordinates.ToDisplay(page));
                }
                _matches[pageIndex] = matches = index.Find(query, matchCase);
            }
            int selected = forward ? 0 : matches.Count - 1;
            if (offset == 0 && _lastPage == start) selected = _lastMatch + step;
            if (selected < 0 || selected >= matches.Count) continue;
            _lastPage = pageIndex; _lastMatch = selected;
            return new PdfSearchMatch(pageIndex + 1, matches, selected);
        }
        return null;
    }

    private static string Normalize(string value) => string.Join(" ",
        value.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record Glyph(string Text, PdfTextBox Bounds, double X, double Y,
        double EndX, double EndY, double Space, double Angle, int Line = -1);

    private sealed record PageIndex(string Text, IReadOnlyList<Glyph?> Characters)
    {
        internal IReadOnlyList<IReadOnlyList<Rectangle>> Find(string query, bool matchCase)
        {
            var result = new List<IReadOnlyList<Rectangle>>();
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int position = Text.IndexOf(query, comparison); position >= 0;
                position = Text.IndexOf(query, position + 1, comparison))
            {
                // One hit can cross lines. Keep its line boxes separate so the
                // highlight never paints the intervening page area.
                var rectangles = Characters.Skip(position).Take(query.Length).Where(g => g != null)
                    .Select(g => g!).GroupBy(g => g.Line).Select(group =>
                    {
                        var box = PdfTextBox.Union(group.Select(g => g.Bounds));
                        return new Rectangle((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height);
                    }).ToList();
                if (rectangles.Count > 0) result.Add(rectangles);
            }
            return result;
        }
    }

    private sealed class TextLocationStrategy : IEventListener
    {
        private readonly List<Glyph> _glyphs = new();
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            var info = (TextRenderInfo)data;
            var characters = info.GetCharacterRenderInfos();
            foreach (var character in characters.Count == 0 ? new[] { info } : characters.AsEnumerable())
            {
                var start = character.GetBaseline().GetStartPoint();
                var end = character.GetBaseline().GetEndPoint();
                var points = new[] { character.GetAscentLine().GetStartPoint(), character.GetAscentLine().GetEndPoint(),
                    character.GetDescentLine().GetStartPoint(), character.GetDescentLine().GetEndPoint() };
                double x = points.Min(p => p.Get(0)), y = points.Min(p => p.Get(1));
                _glyphs.Add(new(character.GetText()?.Normalize(NormalizationForm.FormC) ?? "",
                    new(x, y, points.Max(p => p.Get(0)) - x, points.Max(p => p.Get(1)) - y),
                    start.Get(0), start.Get(1), end.Get(0), end.Get(1), Math.Abs(character.GetSingleSpaceWidth()),
                    Math.Atan2(end.Get(1) - start.Get(1), end.Get(0) - start.Get(0))));
            }
        }

        internal PageIndex Build(PdfAffineTransform transform)
        {
            // Group by baseline before sorting along it: PDF drawing order is
            // often unrelated to reading order, even within a single sentence.
            var lines = new List<List<Glyph>>();
            foreach (var glyph in _glyphs)
            {
                var line = lines.LastOrDefault(l => Math.Abs(l[0].Angle - glyph.Angle) < .05 &&
                    Math.Abs((glyph.X - l[0].X) * -Math.Sin(glyph.Angle) +
                        (glyph.Y - l[0].Y) * Math.Cos(glyph.Angle)) < Math.Max(1, glyph.Bounds.Height * .2));
                if (line == null) lines.Add(line = new());
                line.Add(glyph);
            }
            var text = new StringBuilder();
            var map = new List<Glyph?>();
            void Separator(char separator)
            {
                if (text.Length == 0) return;
                if (text[^1] == ' ' && separator == ' ') return;
                text.Append(separator); map.Add(null);
            }
            int lineId = 0;
            List<Glyph>? previousLine = null;
            bool previousHadGutter = false;
            foreach (var line in lines.OrderBy(l => Math.Round(l[0].Angle, 1))
                .ThenByDescending(l => l[0].Y * Math.Cos(l[0].Angle) - l[0].X * Math.Sin(l[0].Angle)))
            {
                var ordered = line.OrderBy(g => g.X * Math.Cos(g.Angle) + g.Y * Math.Sin(g.Angle)).ToList();
                bool hasGutter = ordered.Zip(ordered.Skip(1)).Any(pair =>
                    Gap(pair.First, pair.Second) > Space(pair.First, pair.Second) * 4);
                bool separateBlock = previousLine != null && (previousHadGutter || hasGutter ||
                    Math.Abs(previousLine[0].Angle - line[0].Angle) > .05 ||
                    Math.Abs((line[0].X - previousLine[0].X) * -Math.Sin(line[0].Angle) +
                        (line[0].Y - previousLine[0].Y) * Math.Cos(line[0].Angle)) >
                        Math.Max(line[0].Bounds.Height, previousLine[0].Bounds.Height) * 2.5);
                Separator(separateBlock ? '\0' : ' ');
                Glyph? previous = null;
                foreach (var glyph in ordered)
                {
                    if (previous != null)
                    {
                        double gap = Gap(previous, glyph);
                        double space = Space(previous, glyph);
                        // A wide column/table gutter is a hard search boundary.
                        if (gap > space * 4) Separator('\0');
                        else if (gap > space * .45 && !char.IsWhiteSpace(previous.Text.LastOrDefault()) &&
                            !char.IsWhiteSpace(glyph.Text.FirstOrDefault())) Separator(' ');
                    }
                    var displayed = glyph with { Bounds = transform.Map(glyph.Bounds), Line = lineId };
                    foreach (char c in glyph.Text)
                    {
                        if (char.IsWhiteSpace(c)) Separator(' ');
                        else { text.Append(c); map.Add(displayed); }
                    }
                    previous = glyph;
                }
                lineId++;
                previousLine = line;
                previousHadGutter = hasGutter;
            }
            return new(text.ToString(), map);
        }

        private static double Gap(Glyph previous, Glyph next) =>
            (next.X - previous.EndX) * Math.Cos(next.Angle) + (next.Y - previous.EndY) * Math.Sin(next.Angle);
        private static double Space(Glyph previous, Glyph next) => Math.Max(1, (next.Space + previous.Space) / 2);
    }
}
