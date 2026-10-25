using iText.Kernel.Geom;
using PDF_simple_edit.Models;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PDF_simple_edit.Services;

public sealed record PdfSearchMatch(int PageNumber, IReadOnlyList<Rectangle> Rectangles, int SelectedIndex);

public sealed class PdfSearchService
{
    private readonly object _gate = new();
    private byte[]? _snapshot;
    private string? _query;
    private bool _matchCase;
    private int _lastPage = -1;
    private int _lastMatch = -1;
    private readonly Dictionary<int, IReadOnlyList<Rectangle>> _matches = new();

    public Task<PdfSearchMatch?> FindAsync(byte[] pdfBytes, string query, int currentPageIndex, bool forward, bool matchCase) =>
        Task.Run(() =>
        {
            lock (_gate) return Find(pdfBytes, query, currentPageIndex, forward, matchCase);
        });

    private PdfSearchMatch? Find(byte[] pdfBytes, string query, int currentPageIndex, bool forward, bool matchCase)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (!ReferenceEquals(_snapshot, pdfBytes) || _query != query || _matchCase != matchCase)
        {
            _snapshot = pdfBytes; _query = query; _matchCase = matchCase;
            _matches.Clear(); _lastPage = -1; _lastMatch = -1;
        }
        using var document = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes, writable: false)));
        int totalPages = document.GetNumberOfPages();
        int start = Math.Clamp(currentPageIndex, 0, totalPages - 1);
        int step = forward ? 1 : -1;
        // The extra iteration wraps to earlier/later matches on the starting page.
        for (int offset = 0; offset <= totalPages; offset++)
        {
            int pageIndex = (start + step * offset % totalPages + totalPages) % totalPages;
            if (!_matches.TryGetValue(pageIndex, out var matches))
            {
                var page = document.GetPage(pageIndex + 1);
                var strategy = new TextLocationStrategy(query, matchCase);
                new PdfCanvasProcessor(strategy).ProcessPageContent(page);
                var transform = PdfPageCoordinates.ToDisplay(page);
                var displayed = new List<Rectangle>();
                foreach (var rect in strategy.FindMatches())
                {
                    var box = transform.Map(new PdfTextBox(rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight()));
                    displayed.Add(new Rectangle((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height));
                }
                _matches[pageIndex] = matches = displayed;
            }
            int selected = forward ? 0 : matches.Count - 1;
            if (offset == 0 && _lastPage == start) selected = _lastMatch + step;
            if (selected < 0 || selected >= matches.Count) continue;
            _lastPage = pageIndex; _lastMatch = selected;
            return new PdfSearchMatch(pageIndex + 1, matches, selected);
        }
        return null;
    }

    private sealed class TextLocationStrategy : ITextExtractionStrategy
    {
        private readonly string _query;
        private readonly StringComparison _comparison;
        private readonly List<TextRenderInfo> _characterRenderInfos = new();

        public TextLocationStrategy(string query, bool matchCase)
        {
            _query = query.Normalize(NormalizationForm.FormC);
            _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT)
                return;

            var textInfo = (TextRenderInfo)data;
            textInfo.PreserveGraphicsState();

            // A single PDF text operation can contain an entire line or sentence.
            // Keep glyph-level render information so a search highlight only covers
            // the matched characters instead of the whole text operation.
            var characterInfos = textInfo.GetCharacterRenderInfos();
            if (characterInfos.Count == 0)
            {
                _characterRenderInfos.Add(textInfo);
                return;
            }

            foreach (TextRenderInfo characterInfo in characterInfos)
                _characterRenderInfos.Add(characterInfo);
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public string GetResultantText() => string.Empty;

        public IReadOnlyList<Rectangle> FindMatches()
        {
            var rectangles = new List<Rectangle>();
            if (string.IsNullOrEmpty(_query))
                return rectangles;

            var fullText = new StringBuilder();
            var characterToRenderInfo = new List<int>();
            for (int i = 0; i < _characterRenderInfos.Count; i++)
            {
                string text = _characterRenderInfos[i].GetText()?.Normalize(NormalizationForm.FormC) ?? string.Empty;
                foreach (char character in text)
                {
                    fullText.Append(character);
                    characterToRenderInfo.Add(i);
                }
            }

            string textToSearch = fullText.ToString();
            int matchIndex = textToSearch.IndexOf(_query, _comparison);
            while (matchIndex >= 0)
            {
                int lastCharacterIndex = matchIndex + _query.Length - 1;
                if (lastCharacterIndex < characterToRenderInfo.Count)
                {
                    int startInfo = characterToRenderInfo[matchIndex];
                    int endInfo = characterToRenderInfo[lastCharacterIndex];
                    Rectangle? bounds = GetBounds(startInfo, endInfo);
                    if (bounds != null)
                        rectangles.Add(bounds);
                }
                matchIndex = textToSearch.IndexOf(_query, matchIndex + 1, _comparison);
            }

            return rectangles;
        }

        private Rectangle? GetBounds(int startInfo, int endInfo)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            for (int i = startInfo; i <= endInfo; i++)
            {
                TextRenderInfo info = _characterRenderInfos[i];
                var baseline = info.GetBaseline();
                var ascent = info.GetAscentLine();
                var descent = info.GetDescentLine();
                minX = Math.Min(minX, Math.Min(baseline.GetStartPoint().Get(0), ascent.GetStartPoint().Get(0)));
                minY = Math.Min(minY, descent.GetStartPoint().Get(1));
                maxX = Math.Max(maxX, Math.Max(ascent.GetEndPoint().Get(0), baseline.GetEndPoint().Get(0)));
                maxY = Math.Max(maxY, ascent.GetEndPoint().Get(1));
            }

            return minX == float.MaxValue
                ? null
                : new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
