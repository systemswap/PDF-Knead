using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.EInvoice;

/// <summary>
/// The text of a page as stage 3 searches it (KONZEPT.md, section 5.24, „Weg für Stufe 3“): every white space — also the protected and the
/// narrow spaces of typeset numbers — as one space, runs of it as one, soft hyphens left out, letters with separate accents composed, all
/// in capitals; a hyphen at the end of a line before a small letter joins the word, before any other it stays without the break.
/// <see cref="Compact"/> is the same without any space — texts of several words are searched there too, so that a line break or a space
/// around „/“ inside a name does not hide it. <see cref="Range"/> and <see cref="CompactRange"/> lead every hit back to the characters of
/// the page (place and visibility).
/// </summary>
internal sealed class InvoiceSearchText
{
    private InvoiceSearchText(string text, int[] map, string compact, int[] compactMap)
    {
        Text = text;
        _map = map;
        Compact = compact;
        _compactMap = compactMap;
    }

    private readonly int[] _map;          // per character of Text its place in the page text, and one more entry: the end
    private readonly int[] _compactMap;   // the same for Compact

    /// <summary>The prepared text.</summary>
    public string Text { get; }

    /// <summary>The prepared text without spaces.</summary>
    public string Compact { get; }

    /// <summary>Prepares the text of a page (<see cref="PageText.Text"/>).</summary>
    public static InvoiceSearchText Of(string page)
    {
        var sb = new StringBuilder(page.Length);
        var map = new List<int>(page.Length + 1);
        for (var i = 0; i < page.Length; i++)
        {
            var c = page[i];
            if (c == '\u00AD') continue;
            if (c == '-' && i + 1 < page.Length && page[i + 1] == '\n')
            {
                // „Muster-“ / „mann“: one word; „Beamer-“ / „Deckenhalterung“: the hyphen stays, the break goes
                if (!(i > 0 && char.IsLetter(page[i - 1]) && i + 2 < page.Length && char.IsLower(page[i + 2])))
                {
                    sb.Append('-');
                    map.Add(i);
                }
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                    map.Add(i);
                }
                continue;
            }
            if (i + 1 < page.Length && IsMark(page[i + 1]))
            {
                var j = i + 1;
                while (j < page.Length && IsMark(page[j])) j++;
                foreach (var composed in page.AsSpan(i, j - i).ToString().Normalize(NormalizationForm.FormC))
                {
                    sb.Append(char.ToUpperInvariant(composed));
                    map.Add(i);
                }
                i = j - 1;
                continue;
            }
            sb.Append(char.ToUpperInvariant(c));
            map.Add(i);
        }
        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
            map.RemoveAt(map.Count - 1);
        }
        map.Add(page.Length);
        var compact = new StringBuilder(sb.Length);
        var compactMap = new List<int>(sb.Length + 1);
        for (var k = 0; k < sb.Length; k++)
            if (sb[k] != ' ')
            {
                compact.Append(sb[k]);
                compactMap.Add(map[k]);
            }
        compactMap.Add(page.Length);
        return new InvoiceSearchText(sb.ToString(), [.. map], compact.ToString(), [.. compactMap]);
    }

    /// <summary>A search form prepared the same way (white space collapsed, composed, capitals).</summary>
    public static string Prepare(string form)
    {
        var sb = new StringBuilder(form.Length);
        foreach (var c in form.Normalize(NormalizationForm.FormC))
        {
            if (c == '\u00AD') continue;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>The characters of the page a hit covers.</summary>
    public TextRange Range(int start, int length) => new(_map[start], _map[start + length - 1] + 1 - _map[start]);

    /// <summary>The characters of the page a hit in <see cref="Compact"/> covers.</summary>
    public TextRange CompactRange(int start, int length) => new(_compactMap[start], _compactMap[start + length - 1] + 1 - _compactMap[start]);

    private static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}

/// <summary>
/// Many search forms found in one pass over a text (Aho-Corasick): every occurrence of every form, overlapping ones too, in time linear in
/// the text and the hits. The forms of all values of an invoice are searched at once instead of each form through every page.
/// </summary>
internal sealed class InvoicePatterns
{
    private readonly Dictionary<long, int> _next = [];   // (node, character) → node
    private readonly List<int> _fail = [0];
    private readonly List<int> _depth = [0];
    private readonly List<int> _pattern = [-1];          // the form that ends in the node, -1 if none
    private readonly List<int> _output = [0];            // the nearest node on the fail chain where a form ends (0: none)
    private readonly List<string> _forms = [];
    private bool _built;

    /// <summary>How many forms there are.</summary>
    public int Count => _forms.Count;

    /// <summary>All characters of all forms (bounds the memory of the automaton).</summary>
    public long Characters { get; private set; }

    /// <summary>The form of a number.</summary>
    public string this[int id] => _forms[id];

    /// <summary>Adds a form (prepared, not empty); the same form twice is one form. Returns its number.</summary>
    public int Add(string form)
    {
        if (_built) throw new InvalidOperationException("Muster schon gebaut");
        var node = 0;
        foreach (var c in form)
        {
            var key = (long)node << 16 | c;
            if (!_next.TryGetValue(key, out var child))
            {
                child = _fail.Count;
                _next[key] = child;
                _fail.Add(0);
                _depth.Add(_depth[node] + 1);
                _pattern.Add(-1);
                _output.Add(0);
            }
            node = child;
        }
        if (_pattern[node] < 0)
        {
            _pattern[node] = _forms.Count;
            _forms.Add(form);
            Characters += form.Length;
        }
        return _pattern[node];
    }

    /// <summary>Links the failure edges (breadth first, without recursion).</summary>
    public void Build()
    {
        _built = true;
        var children = new List<int>[_fail.Count];
        var chars = new List<char>[_fail.Count];
        foreach (var (key, child) in _next)
        {
            var parent = (int)(key >> 16);
            (children[parent] ??= []).Add(child);
            (chars[parent] ??= []).Add((char)(key & 0xFFFF));
        }
        var queue = new Queue<int>();
        if (children[0] is { } top)
            foreach (var c in top)
            {
                _fail[c] = 0;
                queue.Enqueue(c);
            }
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (children[node] is not { } kids) continue;
            for (var k = 0; k < kids.Count; k++)
            {
                var child = kids[k];
                var c = chars[node]![k];
                var f = _fail[node];
                int target;
                while (true)
                {
                    if (_next.TryGetValue((long)f << 16 | c, out target) && target != child) break;
                    if (f == 0)
                    {
                        target = 0;
                        break;
                    }
                    f = _fail[f];
                }
                _fail[child] = target;
                _output[child] = _pattern[target] >= 0 ? target : _output[target];
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>Every occurrence of every form in the text: (form, start).</summary>
    public void Search(string text, Action<int, int> found, CancellationToken cancel = default)
    {
        if (!_built) Build();
        var node = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
            var c = text[i];
            while (true)
            {
                if (_next.TryGetValue((long)node << 16 | c, out var child))
                {
                    node = child;
                    break;
                }
                if (node == 0) break;
                node = _fail[node];
            }
            for (var n = _pattern[node] >= 0 ? node : _output[node]; n != 0; n = _output[n])
                found(_pattern[n], i - _depth[n] + 1);
        }
    }
}
