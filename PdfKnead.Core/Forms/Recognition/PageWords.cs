using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Forms.Recognition;

/// <summary>A visible word or a phrase of words on one line, with its box in page space (y upwards).</summary>
internal sealed record Word(string Text, RectD Box)
{
    public double Height => Box.Bottom - Box.Top;
    public double CenterY => (Box.Top + Box.Bottom) / 2;
}

/// <summary>
/// The text of a page for the form recognition (KONZEPT.md, section 5.9a, step 2): visible upright words from the page's own text (invisible
/// text and text turned along a margin are no labels), joined into phrases where they stand close on one line — the candidates for labels. Runs of underscores or dots („____“,
/// „……“) are no words but lines to write on.
/// </summary>
internal static class PageWords
{
    public const int MaxWords = 20_000;

    /// <param name="glyphs">The text of the page (<see cref="DisplayList.Text"/>).</param>
    /// <param name="leaders">Receives the lines drawn with characters.</param>
    /// <returns>The phrases.</returns>
    public static List<Word> Phrases(TextGlyph[] glyphs, List<Segment> leaders) => Join(Words(glyphs, leaders));

    /// <summary>
    /// The text read in a scan as phrases (KONZEPT.md, section 5.9a, step 2): its words, joined like those of real text; underscores and dots
    /// in a word („Name:____“) are lines to write on, measured along the word as if its characters were equally wide.
    /// </summary>
    /// <param name="lines">The lines read, in page space.</param>
    /// <param name="leaders">Receives the lines drawn with characters.</param>
    /// <param name="more">Phrases of the page's real text to join with.</param>
    /// <returns>The phrases.</returns>
    public static List<Word> Phrases(IReadOnlyList<Ocr.OcrLine> lines, List<Segment> leaders, IEnumerable<Word> more)
    {
        var words = new List<Word>(more);
        foreach (var line in lines)
            foreach (var word in line.Words)
            {
                if (words.Count >= MaxWords) return Join(words);
                if (word.Corners.Length == 0 || word.Text.Length == 0) continue;
                double l = word.Corners.Min(c => c.X), r = word.Corners.Max(c => c.X), b = word.Corners.Min(c => c.Y), t = word.Corners.Max(c => c.Y);
                if (t - b > 200 || r - l <= 0) continue;
                var text = word.Text;
                var each = (r - l) / text.Length;
                var k = 0;
                while (k < text.Length)
                {
                    var leader = text[k] == '_' ? '_' : text[k] is '.' or '…' ? '.' : '\0';
                    var run = k;
                    if (leader != '\0')
                        while (run < text.Length && (leader == '_' ? text[run] == '_' : text[run] is '.' or '…')) run++;
                    if (leader != '\0' && run - k >= (leader == '_' ? 3 : 5))
                    {
                        leaders.Add(new Segment(b + 0.1 * (t - b), l + k * each, l + run * each));
                        k = run;
                        continue;
                    }
                    var end = k + 1;
                    while (end < text.Length && !(text[end] == '_' && end + 2 < text.Length && text[end + 1] == '_' && text[end + 2] == '_') &&
                           !(text[end] is '.' or '…' && end + 4 < text.Length && text.AsSpan(end, 5).IndexOfAnyExcept(".…") < 0))
                        end++;
                    words.Add(new Word(text[k..end], new RectD(l + k * each, b, l + end * each, t)));
                    k = end;
                }
            }
        return Join(words);
    }

    /// <summary>Words close together on one line become a phrase.</summary>
    private static List<Word> Join(List<Word> words)
    {
        words.Sort((a, b) => Math.Abs(a.CenterY - b.CenterY) > 0.5 * Math.Min(a.Height, b.Height) ? b.CenterY.CompareTo(a.CenterY) : a.Box.Left.CompareTo(b.Box.Left));
        var phrases = new List<Word>();
        Word? cur = null;
        foreach (var w in words)
        {
            if (cur is not null && Math.Abs(w.CenterY - cur.CenterY) <= 0.4 * Math.Max(w.Height, cur.Height) &&
                w.Box.Left - cur.Box.Right is > -1 and var gap && gap <= 0.9 * Math.Max(w.Height, cur.Height))
            {
                cur = new Word(cur.Text + " " + w.Text,
                    new RectD(cur.Box.Left, Math.Min(cur.Box.Top, w.Box.Top), Math.Max(cur.Box.Right, w.Box.Right), Math.Max(cur.Box.Bottom, w.Box.Bottom)));
                continue;
            }
            if (cur is not null) phrases.Add(cur);
            cur = w;
        }
        if (cur is not null) phrases.Add(cur);
        return phrases;
    }

    private static List<Word> Words(TextGlyph[] glyphs, List<Segment> leaders)
    {
        var words = new List<Word>();
        if (glyphs.Length == 0) return words;
        var text = PageText.Create(glyphs);
        var s = text.Text;
        var i = 0;
        while (i < s.Length && words.Count < MaxWords)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            // „Name:____“: the runs of underscores (three or more) or dots (five or more) are lines to write on, the rest are words
            var k = start;
            while (k < i)
            {
                var run = k;
                var leader = s[k] == '_' ? '_' : s[k] is '.' or '…' ? '.' : '\0';
                if (leader != '\0')
                    while (run < i && (leader == '_' ? s[run] == '_' : s[run] is '.' or '…')) run++;
                if (leader != '\0' && run - k >= (leader == '_' ? 3 : 5))
                {
                    if (Bounds(text, new TextRange(k, run - k)) is { } line) leaders.Add(new Segment(line.Top + 0.5, line.Left, line.Right));
                    k = run;
                    continue;
                }
                var piece = k;
                while (piece < i && !(s[piece] == '_' && piece + 2 < i && s[piece + 1] == '_' && s[piece + 2] == '_') &&
                       !(s[piece] is '.' or '…' && piece + 4 < i && s.AsSpan(piece, 5).IndexOfAnyExcept(".…") < 0))
                    piece++;
                if (piece == k) piece++;
                var range = new TextRange(k, piece - k);
                var visible = false;
                // upright text only: a note turned along the margin is no label
                foreach (var g in text.GlyphsOf(range))
                    if (g.Visible && !g.Vertical && g.X1 - g.X0 > 0 && Math.Abs(g.Y1 - g.Y0) <= 0.3 * (g.X1 - g.X0)) { visible = true; break; }
                if (visible && Bounds(text, range) is { } box && box.Bottom - box.Top <= 200) words.Add(new Word(s[k..piece], box));
                k = piece;
            }
        }
        return words;
    }

    private static RectD? Bounds(PageText text, TextRange range)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var q in text.Quads(range))
        {
            var box = q.Bounds;
            l = Math.Min(l, box.Left);
            b = Math.Min(b, box.Top);
            r = Math.Max(r, box.Right);
            t = Math.Max(t, box.Bottom);
        }
        return l > r ? null : new RectD(l, b, r, t);
    }
}
