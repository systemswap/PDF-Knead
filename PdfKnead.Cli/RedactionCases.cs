using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Cli;

/// <summary>What must become of a word after redacting.</summary>
internal enum Fate
{
    /// <summary>Nowhere in the file any more (the leak search finds nothing).</summary>
    Gone,

    /// <summary>Still in the file (shown somewhere, or stays by design such as text on a layer that is printed).</summary>
    Stays,
}

/// <summary>Expectation for a word of a test case: <see cref="Fate.Stays"/> means the leak search still finds it in the file.</summary>
internal sealed record Expectation(int Page, string Word, Fate Fate);

/// <summary>
/// A test case for redaction: the file, the bars per page (areas in page space) and whether all hidden text is to be marked as well,
/// and what must become of its words.
/// </summary>
internal sealed record RedactionCase(string Name, byte[] Pdf, Dictionary<int, List<PdfRectangle>> Bars, bool MarkHidden, List<Expectation> Expectations,
    string Description);

/// <summary>
/// Generates redaction test cases from building blocks, reproducible by a seed (see docs/PRUEFUNGEN.md, section 6j): words in every place
/// content can live (page, form with or without resources of its own, a form shown twice and on two pages, nested forms, the glyph of a
/// Type 3 font, a tiling pattern, a soft mask, an annotation), with every font kind (standard Type 1, embedded TrueType, Type 3, vertical
/// Type 0), hidden in every way known (white, transparent, the colour of the background, covered by an area, an image or later characters,
/// clipped, on a layer off on the screen or only in print, tiny, beside the page, invisible mode, as a clip), under bars or beside them,
/// on rotated and shifted pages. Every word is unique, so its fate can be checked in the file; glyphs, forms, fonts and images are
/// deliberately shared between places that are redacted and places that are not.
/// </summary>
internal static class RedactionCases
{
    private const double Size = 12;

    /// <summary>Builds the case of a seed.</summary>
    /// <param name="structure">Tagged, with a structure tree whose elements carry descriptions (<c>--struktur</c>, <see cref="Builder.Structure"/>);
    /// drawn from a random stream of its own, so the case is otherwise the same as without.</param>
    public static RedactionCase Make(int seed, bool structure = false)
    {
        var rng = new Random(seed);
        var b = new Builder(rng, seed);
        var pages = 1 + rng.Next(3);
        for (var p = 0; p < pages; p++) b.Page(p);
        if (structure) b.Structure(new Random(unchecked(seed * 7919 + 17)));
        return b.Finish($"fall-{seed}");
    }

    private sealed class Builder(Random rng, int seed)
    {
        private readonly List<byte[]> _objects = [];
        private readonly List<(StringBuilder Content, List<string> Annots, string Extra)> _pages = [];
        private readonly Dictionary<int, List<PdfRectangle>> _bars = [];
        private readonly List<Expectation> _expect = [];
        private readonly StringBuilder _description = new();
        private readonly Dictionary<string, string> _xobjects = [];   // name → reference
        private readonly Dictionary<string, string> _patterns = [];
        private readonly Dictionary<string, string> _states = new() { ["Null"] = "<< /ca 0 >>", ["Half"] = "<< /ca 0.5 >>" };
        private readonly List<string> _ocgs = [];
        private readonly Dictionary<string, string> _properties = [];
        private readonly List<string> _printOff = [];
        private int _words;
        private string? _sharedForm;      // a form shown on several pages and twice on one
        private string? _sharedWord;
        private bool _markHidden;
        private int _fontTrueType, _fontType3, _image, _rotate;
        private readonly List<string> _type3Procs = [];
        private readonly List<string> _type3Names = [];

        private string Word(string kind)
        {
            // letters only, unique per case and kind, so that a leak search finds exactly this occurrence
            var n = ++_words;
            var letters = new StringBuilder();
            for (var x = seed * 131 + n * 7; letters.Length < 4; x /= 26) letters.Append((char)('a' + Math.Abs(x % 26)));
            return $"{kind}{letters}{(char)('a' + n % 26)}{(char)('a' + n / 26 % 26)}";
        }

        private int Add(string body)
        {
            _objects.Add(Encoding.Latin1.GetBytes(body));
            return _objects.Count;
        }

        private int AddStream(string dict, string content)
        {
            var data = Encoding.Latin1.GetBytes(content);
            return AddStream(dict, data);
        }

        private int AddStream(string dict, byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            var packed = ms.ToArray();
            var head = Encoding.Latin1.GetBytes($"<< {dict} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n");
            _objects.Add([.. head, .. packed, .. "\nendstream"u8.ToArray()]);
            return _objects.Count;
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Approximate bounds of a word set at (x, y) in a font of this size (Helvetica-like widths).</summary>
        private static PdfRectangle Bounds(double x, double y, string word, double size, string font = "/F1") =>
            new(x - 1, y - size * 0.3, x + word.Length * size * (font == "/F3" ? 0.72 : 0.62) + 1, y + size * 0.85);

        private static PdfRectangle Inflate(PdfRectangle r, double d) => new(r.Left - d, r.Bottom - d, r.Right + d, r.Top + d);

        private void Bar(int page, PdfRectangle area)
        {
            if (!_bars.TryGetValue(page, out var list)) _bars[page] = list = [];
            list.Add(area);
        }

        private string Font()
        {
            switch (rng.Next(4))
            {
                case 0:
                    _fontTrueType = 1;
                    return "/FT";
                case 1:
                    _fontType3 = 1;
                    return "/F3";
                default:
                    return "/F1";
            }
        }

        public void Page(int index)
        {
            var content = new StringBuilder();
            _pages.Add((content, [], ""));
            if (index == 0 && rng.Next(5) == 0) _rotate = 90 * (1 + rng.Next(3));
            // a visible anchor and a background of some kind
            var anchor = Word("sicht");
            content.Append($"BT /F1 14 Tf 40 800 Td ({anchor}) Tj ET ");
            _expect.Add(new Expectation(index, anchor, Fate.Stays));
            var items = 3 + rng.Next(5);
            for (var i = 0; i < items; i++)
            {
                var y = 740 - i * 90;
                // each item in its own graphics state: text state such as Tr and Tc lasts beyond ET
                content.Append("q ");
                switch (rng.Next(11))
                {
                    case <= 3: Visible(index, content, y); break;
                    case <= 6: Hidden(index, content, y); break;
                    case 7: SharedForm(index, content, y); break;
                    case 8: Type3Text(index, content, y); break;
                    case 9: Justified(index, content, y); break;
                    default: Annotation(index, y); break;
                }
                content.Append("Q ");
            }
        }

        /// <summary>A visible word in some place, under a bar or not (sometimes only half of it).</summary>
        private void Visible(int page, StringBuilder content, double y)
        {
            var word = Word("wort");
            var font = Font();
            var x = 40 + rng.Next(200);
            var size = rng.Next(6) == 0 ? 4 + rng.Next(4) : Size;
            var text = $"BT {font} {F(size)} Tf {F(x)} {F(y)} Td ({word}) Tj ET";
            var place = rng.Next(7);
            string drawn;
            switch (place)
            {
                case 0:
                    drawn = Form(text, ownResources: true);
                    break;
                case 1:
                    drawn = Form(text, ownResources: false);
                    break;
                case 2:
                    drawn = Form(Form(text, ownResources: rng.Next(2) == 0), ownResources: rng.Next(2) == 0);
                    break;
                case 3:
                    // inside a tiling pattern cell: painted with a rectangle over the word's place
                    var name = $"P{_patterns.Count}";
                    var cell = AddStream($"/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 600 840] /XStep 600 /YStep 840 /Resources {Resources()}", text);
                    _patterns[name] = $"{cell} 0 R";
                    var r = Bounds(x, y, word, size, font);
                    drawn = $"q /Pattern cs /{name} scn {F(r.Left)} {F(r.Bottom)} {F(r.Width)} {F(r.Height)} re f Q";
                    break;
                case 4:
                {
                    // a stack under one bar: the text at the bottom, a coloured area over it, an image on top — the bar takes all of it
                    _image = 1;
                    var s = Bounds(x, y, word, size, font);
                    drawn = $"{text} 0.2 0.6 0.3 rg {F(s.Left - 2)} {F(s.Bottom - 2)} {F(s.Width + 4)} {F(s.Height + 4)} re f 0 g " +
                            $"q {F(s.Width + 2)} 0 0 {F(s.Height + 2)} {F(s.Left - 1)} {F(s.Bottom - 1)} cm /Im Do Q";
                    break;
                }
                default:
                    drawn = text;
                    break;
            }
            content.Append(drawn).Append(' ');
            var bounds = Bounds(x, y, word, size, font);
            var covered = place == 4 || rng.Next(3) != 0;
            var half = covered && place != 4 && rng.Next(4) == 0;
            var fully = covered && !half;
            var signature = rng.Next(3) == 0;
            if (signature)
            {
                // a signature drawn as a path across the word, stroked or filled: under a bar its points leave the file
                var r = bounds;
                double F2(double v) => Math.Round(v, 2);
                var curve = $"{F(F2(r.Left - 10))} {F(F2(r.Bottom))} m {F(F2(r.Left + r.Width * 0.3))} {F(F2(r.Top + 8))} {F(F2(r.Left + r.Width * 0.6))} {F(F2(r.Bottom - 8))} " +
                            $"{F(F2(r.Right + 10))} {F(F2(r.Top))} c";
                // filled only over a word that goes entirely: over one that stays, the shape would hide it
                content.Append(!fully || rng.Next(2) == 0 ? $"q 1.5 w 1 J 0.1 0.1 0.6 RG {curve} S Q " : $"q 0.6 0.1 0.1 rg {curve} {F(F2(r.Left + r.Width / 2))} {F(F2(r.Bottom - 6))} l h f Q ");
            }
            if (half)
            {
                // half of it: the rest stays (checked by position)
                Bar(page, new PdfRectangle(bounds.Left - 1, bounds.Bottom - 1, (bounds.Left + bounds.Right) / 2, bounds.Top + 1));
                _description.Append($"S{page + 1}: halb {place} ");
            }
            else if (covered)
            {
                Bar(page, Inflate(bounds, 2));
                _expect.Add(new Expectation(page, word, Fate.Gone));
                _description.Append($"S{page + 1}: Balken {place} ");
            }
            else
            {
                _expect.Add(new Expectation(page, word, Fate.Stays));
            }
        }

        /// <summary>
        /// A justified paragraph in Courier (every character 0.6 em, so the lines can be set exactly to the margin): three or four lines to
        /// the margin and a short last one, written with word spacing (Tw), with the numbers of TJ or with each word placed by its own Td; one
        /// or two whole words of the justified lines under bars. With hidden lengths their lines are set anew (docs/FEHLERGEDAECHTNIS.md,
        /// No. 356).
        /// </summary>
        private void Justified(int page, StringBuilder content, double y)
        {
            const double size = 10, leading = 12, per = 0.6 * size;
            double x = 40 + rng.Next(60);
            static double Natural(List<string> words) => (string.Join(" ", words).Length) * per;
            // filled as a typesetting program fills them: words of 7 to 11 letters (unique) while they fit, the rest of the line spread over
            // the gaps — at most 0.8 em each (a gap of more than an em reads as a gap between columns)
            var width = 230 + rng.Next(120);
            var count = 4 + rng.Next(2);
            var lines = new List<List<string>>();
            for (var l = 0; l < count; l++)
            {
                var line = new List<string>();
                for (var tries = 0; tries < 200; tries++)
                {
                    line.Clear();
                    while (true)
                    {
                        var next = "block"[..(1 + rng.Next(5))].Length + 6;
                        if (Natural(line) + (line.Count > 0 ? per : 0) + next * per > width) break;
                        line.Add(new string('x', next));
                    }
                    if (l == count - 1 || line.Count >= 3 && (width - Natural(line)) / (line.Count - 1) <= 0.8 * size) break;
                }
                if (l == count - 1) line = line.Take(2).ToList();
                lines.Add([.. line.Select(w => Word("block"[..(w.Length - 6)]))]);
            }
            var margin = x + width;
            var style = rng.Next(3);
            var redacted = new HashSet<(int Line, int Word)>();
            for (var n = 1 + rng.Next(2); redacted.Count < n;) redacted.Add((rng.Next(count - 1), rng.Next(3)));
            content.Append($"BT /FC {F(size)} Tf ");
            for (var l = 0; l < count; l++)
            {
                var words = lines[l];
                var extra = l < count - 1 ? (margin - x - Natural(words)) / (words.Count - 1) : 0;
                var baseline = y - l * leading;
                content.Append($"1 0 0 1 {F(x)} {F(baseline)} Tm ");
                switch (style)
                {
                    case 0:
                        content.Append($"{F(extra)} Tw ({string.Join(" ", words)}) Tj 0 Tw ");
                        break;
                    case 1:
                        content.Append('[').Append(string.Join($" {F(-extra * 1000 / size)} ", words.Select((w, i) => $"({w}{(i < words.Count - 1 ? " " : "")})"))).Append("] TJ ");
                        break;
                    default:
                        for (var i = 0; i < words.Count; i++)
                            content.Append(i == 0 ? $"({words[i]}) Tj " : $"{F((words[i - 1].Length + 1) * per + extra)} 0 Td ({words[i]}) Tj ");
                        break;
                }
                var at = x;
                for (var i = 0; i < words.Count; i++)
                {
                    var word = words[i];
                    if (redacted.Contains((l, i)))
                    {
                        var margin2 = 0.5 + rng.Next(3) * 0.5;
                        Bar(page, new PdfRectangle(at - margin2, baseline - size * 0.3, at + word.Length * per + margin2, baseline + size * 0.85));
                        _expect.Add(new Expectation(page, word, Fate.Gone));
                    }
                    else
                    {
                        _expect.Add(new Expectation(page, word, Fate.Stays));
                    }
                    at += (word.Length + 1) * per + extra;
                }
            }
            content.Append("ET ");
            _description.Append($"S{page + 1}: Blocksatz {style} ");
        }

        /// <summary>A hidden word, hidden in one of the known ways; all hidden text of the case is marked.</summary>
        private void Hidden(int page, StringBuilder content, double y)
        {
            var word = Word("geheim");
            var font = Font();
            var x = 40 + rng.Next(200);
            var t = $"BT {font} {F(Size)} Tf {F(x)} {F(y)} Td ({word}) Tj ET";
            var r = Bounds(x, y, word, Size, font);
            var box = $"{F(r.Left - 3)} {F(r.Bottom - 3)} {F(r.Width + 6)} {F(r.Height + 6)} re f";
            _markHidden = true;
            var fate = Fate.Gone;
            string how;
            switch (rng.Next(13))
            {
                case 0: content.Append($"1 1 1 rg {t} 0 g "); how = "weiß"; break;
                case 1: content.Append($"q /Null gs {t} Q "); how = "Deckkraft 0"; break;
                case 2: content.Append($"0 0 1 rg {box} {t} 0 g "); how = "gleiche Farbe"; break;
                case 3: content.Append($"{t} 0.2 0.6 0.3 rg {box} 0 g "); how = "unter Fläche"; break;
                case 4:
                    _image = 1;
                    content.Append($"{t} q {F(r.Width + 6)} 0 0 {F(r.Height + 6)} {F(r.Left - 3)} {F(r.Bottom - 3)} cm /Im Do Q ");
                    how = "unter Bild";
                    break;
                case 5: content.Append($"q 0 0 m 30 0 l 0 30 l h W n {t} Q "); how = "Clip Dreieck"; break;
                case 6: content.Append($"/OC /{Layer(printed: false, shown: false)} BDC {t} EMC "); how = "Ebene aus"; break;
                case 7:
                    // shown on paper only: not hidden
                    content.Append($"/OC /{Layer(printed: true, shown: false)} BDC {t} EMC ");
                    fate = Fate.Stays;
                    how = "nur Druck";
                    break;
                case 8: content.Append($"BT {font} 0.001 Tf {F(x)} {F(y)} Td ({word}) Tj ET "); how = "winzig"; break;
                case 9: content.Append($"BT {font} {F(Size)} Tf {F(-500 - x)} {F(y)} Td ({word}) Tj ET "); how = "neben der Seite"; break;
                case 10: content.Append($"BT 3 Tr {font} {F(Size)} Tf {F(x)} {F(y)} Td ({word}) Tj ET "); how = "Modus 3"; break;
                case 11:
                    // as a clip: visible through what is painted inside — not hidden
                    content.Append($"q BT 7 Tr {font} {F(Size)} Tf {F(x)} {F(y)} Td ({word}) Tj ET 0.8 0.1 0.1 rg {box} Q ");
                    fate = Fate.Stays;
                    how = "Clip-Text";
                    break;
                default:
                    // covered by two later rows of filled squares (characters of a symbol font)
                    var squares = new string('n', (int)(r.Width / 13) + 2);
                    content.Append($"{t} BT /FZ 18 Tf -1 Tc {F(r.Left - 3)} {F(y - 3.5)} Td ({squares}) Tj 0 6.5 Td ({squares}) Tj ET ");
                    how = "überdeckt";
                    break;
            }
            // visible text in a form or on another line keeps sharing the glyphs of the font
            _expect.Add(new Expectation(page, word, fate));
            _description.Append($"S{page + 1}: {how} ");
        }

        /// <summary>A form shown on several pages and twice on this one: once under a bar, once not — its word stays.</summary>
        private void SharedForm(int page, StringBuilder content, double y)
        {
            if (_sharedForm is null)
            {
                _sharedWord = Word("geteilt");
                _sharedForm = Form($"BT /F1 {F(Size)} Tf 0 0 Td ({_sharedWord}) Tj ET", ownResources: rng.Next(2) == 0);
            }
            var x = 40 + rng.Next(150);
            content.Append($"q 1 0 0 1 {F(x)} {F(y)} cm {_sharedForm} Q q 1 0 0 1 {F(x + 250)} {F(y)} cm {_sharedForm} Q ");
            if (rng.Next(2) == 0) Bar(page, Inflate(Bounds(x, y, _sharedWord!, Size), 2));
            _expect.Add(new Expectation(page, _sharedWord!, Fate.Stays));
            _description.Append($"S{page + 1}: geteilte Form ");
        }

        /// <summary>Text inside the glyph of a Type 3 font, the glyph shown under a bar or not; the same font is also shown elsewhere.</summary>
        private void Type3Text(int page, StringBuilder content, double y)
        {
            _fontType3 = 1;
            var word = Word("glyphe");
            var name = $"g{_type3Procs.Count}";
            var proc = AddStream("", $"1000 0 d0 BT /F1 60 Tf 0 100 Td ({word}) Tj ET 0 0 200 700 re f");
            _type3Procs.Add($"/{name} {proc} 0 R");
            _type3Names.Add(name);
            var code = (char)('0' + _type3Procs.Count - 1);
            var x = 40 + rng.Next(200);
            content.Append($"BT /F3 {F(Size)} Tf {F(x)} {F(y)} Td ({code}) Tj ET ");
            if (rng.Next(2) == 0)
            {
                Bar(page, Inflate(new PdfRectangle(x, y - 2, x + Size, y + Size), 2));
                // its code is shown only here; the font itself also shows other glyphs, here and on other pages
                _expect.Add(new Expectation(page, word, Fate.Gone));
                _description.Append($"S{page + 1}: Type-3-Glyphe ");
            }
            else
            {
                _expect.Add(new Expectation(page, word, Fate.Stays));
            }
        }

        /// <summary>A free text annotation with a word, under a bar or not.</summary>
        private void Annotation(int page, double y)
        {
            var word = Word("anmerkung");
            var x = 40 + rng.Next(200);
            var r = Bounds(x, y, word, Size);
            var ap = AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 {F(r.Width)} {F(r.Height)}] /Resources {Resources()}",
                $"BT /F1 {F(Size)} Tf 1 {F(Size * 0.3)} Td ({word}) Tj ET");
            var annot = Add($"<< /Type /Annot /Subtype /FreeText /Rect [{F(r.Left)} {F(r.Bottom)} {F(r.Right)} {F(r.Top)}] /Contents ({word}) /DA (/F1 12 Tf 0 g) /AP << /N {ap} 0 R >> >>");
            _pages[page].Annots.Add($"{annot} 0 R");
            if (rng.Next(2) == 0)
            {
                Bar(page, Inflate(r, 2));
                _expect.Add(new Expectation(page, word, Fate.Gone));
                _description.Append($"S{page + 1}: Anmerkung ");
            }
        }

        /// <summary>Structure tree (<see cref="Structure"/>): the root's kids, the parent tree per page (MCID → element), the catalog entries.</summary>
        private readonly List<string> _structureKids = [];
        private readonly List<List<string>> _parentTree = [];
        private int _structureRoot;

        /// <summary>
        /// Tags the case (docs/FEHLERGEDAECHTNIS.md, No. 617 and 618): on every page one to three small visible words at the top, each in
        /// marked content (sometimes with an /ActualText of its own), and structure elements over them whose descriptions (/ActualText, /Alt)
        /// hold unique words — on the element of the marked content (with a title, /T); on a parent without a page of its own; on a figure (with a
        /// title and a /Summary attribute, No. 694); and on more than one
        /// page also on a parent naming the other page and on an element reaching the page only through a content reference; sometimes a form
        /// beside the bars with a replacement text in its own content (No. 624). A bar on the page (or on the page an element names) takes every
        /// description of it (No. 134): those words must go, the others stay. Marked hidden content touches none of them (the marked words are
        /// visible).
        /// </summary>
        /// <summary>The title word of a description word (unique as it is; derived, so the other words of the case stay the same).</summary>
        private static string Title(string described) => "titel" + described["beschreibung".Length..];

        /// <summary>The summary word of a description word (as <see cref="Title"/>).</summary>
        private static string Summary(string described) => "zusammenfassung" + described["beschreibung".Length..];

        public void Structure(Random srng)
        {
            _structureRoot = Add("");
            var root = $"{_structureRoot} 0 R";
            bool Redacted(int page) => _bars.TryGetValue(page, out var list) && list.Count > 0;
            for (var p = 0; p < _pages.Count; p++)
            {
                var tree = new List<string>();
                _parentTree.Add(tree);
                var count = 1 + srng.Next(3);
                for (var m = 0; m < count; m++)
                {
                    var shown = Word("struktur");
                    _expect.Add(new Expectation(p, shown, Fate.Stays));
                    var own = "";
                    if (srng.Next(3) == 0)
                    {
                        var replacement = Word("ersatz");
                        own = $" /ActualText ({replacement})";
                        _expect.Add(new Expectation(p, replacement, Redacted(p) ? Fate.Gone : Fate.Stays));
                    }
                    _pages[p].Content.Append($"/Span << /MCID {m}{own} >> BDC BT /F1 8 Tf {F(300 + 100 * m)} 805 Td ({shown}) Tj ET EMC ");
                    var described = Word("beschreibung");
                    var other = _pages.Count > 1 ? (p + 1) % _pages.Count : -1;
                    var page = $"{PageNumber(p)} 0 R";
                    var shape = srng.Next(other >= 0 ? 5 : 3);
                    var element = Add("");
                    switch (shape)
                    {
                        case 0:
                            // with a title as well (No. 694): it goes with the bar like the descriptions
                            Set(element, $"<< /Type /StructElem /S /Span /P {root} /Pg {page} /K {m} /ActualText ({described}) /T ({Title(described)}) >>");
                            _expect.Add(new Expectation(p, Title(described), Redacted(p) ? Fate.Gone : Fate.Stays));
                            break;
                        case 1:
                        {
                            // a parent without a page of its own (nor its parents): it describes its span
                            var span = Add("");
                            Set(span, $"<< /Type /StructElem /S /Span /P {element} 0 R /Pg {page} /K {m} >>");
                            Set(element, $"<< /Type /StructElem /S /P /P {root} /K [{span} 0 R] /ActualText ({described}) >>");
                            tree.Add($"{span} 0 R");
                            break;
                        }
                        case 2:
                            // a title and a summary in its attributes (No. 694)
                            Set(element, $"<< /Type /StructElem /S /Figure /P {root} /Pg {page} /K {m} /Alt ({described}) /T ({Title(described)}) " +
                                         $"/A << /O /Table /Summary ({Summary(described)}) >> >>");
                            _expect.Add(new Expectation(p, Title(described), Redacted(p) ? Fate.Gone : Fate.Stays));
                            _expect.Add(new Expectation(p, Summary(described), Redacted(p) ? Fate.Gone : Fate.Stays));
                            break;
                        case 3:
                        {
                            // a parent naming the other page, its span on this one
                            var span = Add("");
                            Set(span, $"<< /Type /StructElem /S /Span /P {element} 0 R /Pg {page} /K {m} >>");
                            Set(element, $"<< /Type /StructElem /S /P /P {root} /Pg {PageNumber(other)} 0 R /K [{span} 0 R] /ActualText ({described}) >>");
                            tree.Add($"{span} 0 R");
                            break;
                        }
                        default:
                            // an element of the other page reaching this one through a content reference
                            Set(element, $"<< /Type /StructElem /S /Span /P {root} /Pg {PageNumber(other)} 0 R " +
                                         $"/K [<< /Type /MCR /Pg {page} /MCID {m} >>] /Alt ({described}) >>");
                            break;
                    }
                    if (shape is 0 or 2 or 4) tree.Add($"{element} 0 R");
                    _structureKids.Add($"{element} 0 R");
                    var gone = Redacted(p) || shape is 3 or 4 && Redacted(other);
                    _expect.Add(new Expectation(p, described, gone ? Fate.Gone : Fate.Stays));
                }
                // a form beside every bar whose own content gives a replacement text (docs/FEHLERGEDAECHTNIS.md, No. 624): on a page with a bar
                // it goes as every description of it, its visible word stays
                if (srng.Next(2) == 0)
                {
                    var word = Word("formwort");
                    var replacement = Word("formersatz");
                    var draw = Form($"/Span << /ActualText ({replacement}) >> BDC BT /F1 8 Tf 0 0 Td ({word}) Tj ET EMC", ownResources: srng.Next(2) == 0);
                    _pages[p].Content.Append($"q 1 0 0 1 450 790 cm {draw} Q ");
                    _expect.Add(new Expectation(p, word, Fate.Stays));
                    _expect.Add(new Expectation(p, replacement, Redacted(p) ? Fate.Gone : Fate.Stays));
                }
                // a tiling pattern beside the bars, painted over many tiles, whose cell gives a replacement text (No. 696): on a page with a
                // bar it goes as every description of it — no observer looks into a cell painted over several tiles
                if (srng.Next(2) == 0)
                {
                    var replacement = Word("musterersatz");
                    var name = $"P{_patterns.Count}";
                    var cell = AddStream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>",
                        $"/Span << /ActualText ({replacement}) >> BDC 0.5 g 0 0 2 2 re f EMC");
                    _patterns[name] = $"{cell} 0 R";
                    _pages[p].Content.Append($"q /Pattern cs /{name} scn 20 790 40 40 re f Q ");
                    _expect.Add(new Expectation(p, replacement, Redacted(p) ? Fate.Gone : Fate.Stays));
                }
                // beside the bars a description in each other place no bar reaches (No. 697): the group of a soft mask, a pattern painted by a
                // form (which itself describes nothing), the glyph of a Type 3 font, the appearance of an annotation — all go on a page with a bar;
                // at the foot of the page, where nothing else is (over the anchor they would hide part of it)
                var besideFate = Redacted(p) ? Fate.Gone : Fate.Stays;
                if (srng.Next(2) == 0)
                {
                    var replacement = Word("maskeersatz");
                    var group = AddStream("/Type /XObject /Subtype /Form /BBox [0 0 600 840] /Group << /S /Transparency /CS /DeviceRGB >> /Resources << >>",
                        $"/Span << /ActualText ({replacement}) >> BDC 1 g 0 0 600 840 re f EMC");
                    var state = $"SM{_states.Count}";
                    _states[state] = $"<< /Type /ExtGState /SMask << /S /Luminosity /G {group} 0 R >> >>";
                    _pages[p].Content.Append($"q /{state} gs 0.3 g 300 30 30 30 re f Q ");
                    _expect.Add(new Expectation(p, replacement, besideFate));
                }
                if (srng.Next(2) == 0)
                {
                    var replacement = Word("formmusterersatz");
                    var name = $"P{_patterns.Count}";
                    var cell = AddStream("/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>",
                        $"/Span << /ActualText ({replacement}) >> BDC 0.2 g 0 0 2 2 re f EMC");
                    _patterns[name] = $"{cell} 0 R";
                    var draw = Form($"/Pattern cs /{name} scn 0 0 30 30 re f", ownResources: srng.Next(2) == 0);
                    _pages[p].Content.Append($"q 1 0 0 1 350 30 cm {draw} Q ");
                    _expect.Add(new Expectation(p, replacement, besideFate));
                }
                if (srng.Next(2) == 0)
                {
                    _fontType3 = 1;
                    var replacement = Word("glyphersatz");
                    var name = $"g{_type3Procs.Count}";
                    var proc = AddStream("", $"1000 0 d0 /Span << /ActualText ({replacement}) >> BDC 0 0 200 700 re f EMC");
                    _type3Procs.Add($"/{name} {proc} 0 R");
                    _type3Names.Add(name);
                    var code = (char)('0' + _type3Procs.Count - 1);
                    _pages[p].Content.Append($"BT /F3 12 Tf 400 35 Td ({code}) Tj ET ");
                    _expect.Add(new Expectation(p, replacement, besideFate));
                }
                if (srng.Next(2) == 0)
                {
                    var replacement = Word("aussehenersatz");
                    var ap = AddStream("/Type /XObject /Subtype /Form /BBox [0 0 30 30] /Resources << >>",
                        $"/Span << /ActualText ({replacement}) >> BDC 0 0 1 RG 1 1 28 28 re S EMC");
                    var annot = Add($"<< /Type /Annot /Subtype /Square /Rect [450 30 480 60] /F 4 /AP << /N {ap} 0 R >> >>");
                    _pages[p].Annots.Add($"{annot} 0 R");
                    _expect.Add(new Expectation(p, replacement, besideFate));
                }
            }
            _description.Append("Struktur ");
        }

        private void Set(int number, string body) => _objects[number - 1] = Encoding.Latin1.GetBytes(body);

        /// <summary>The object number page <paramref name="index"/> will get in <see cref="Finish"/> (reserved here when tagging).</summary>
        private int PageNumber(int index)
        {
            while (_pageNumbers.Count < _pages.Count) _pageNumbers.Add(Add(""));
            return _pageNumbers[index];
        }

        private readonly List<int> _pageNumbers = [];

        private string Layer(bool printed, bool shown)
        {
            var name = $"L{_ocgs.Count}";
            var usage = printed ? " /Usage << /Print << /PrintState /ON >> >>" : "";
            var ocg = Add($"<< /Type /OCG /Name ({name}){usage} >>");
            _ocgs.Add($"{ocg} 0 R");
            if (!shown) _printOff.Add($"{ocg} 0 R");
            _properties[name] = $"{ocg} 0 R";
            return name;
        }

        /// <summary>A form XObject with the content; returns the operator that shows it.</summary>
        private string Form(string content, bool ownResources)
        {
            var name = $"X{_xobjects.Count}";
            var resources = ownResources ? $"/Resources {Resources()}" : "";
            var form = AddStream($"/Type /XObject /Subtype /Form /BBox [-1000 -1000 2000 2000] {resources}", content);
            _xobjects[name] = $"{form} 0 R";
            return $"/{name} Do";
        }

        // resources are written once at the end (all names known); forms with their own refer to the same dictionary object
        private int _resources;

        private string Resources()
        {
            if (_resources == 0) _resources = Add("");   // filled in Finish
            return $"{_resources} 0 R";
        }

        public RedactionCase Finish(string name)
        {
            var fonts = new StringBuilder("/F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> /FZ << /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >> " +
                                          "/FC << /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
            if (_fontTrueType != 0)
            {
                var file = AddStream("", EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
                var descriptor = Add($"<< /Type /FontDescriptor /FontName /LiberationSans /Flags 32 /FontBBox [-200 -300 1200 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {file} 0 R >>");
                var widths = string.Join(" ", Enumerable.Repeat("556", 95));
                fonts.Append($" /FT << /Type /Font /Subtype /TrueType /BaseFont /LiberationSans /FirstChar 32 /LastChar 126 /Widths [{widths}] /Encoding /WinAnsiEncoding /FontDescriptor {descriptor} 0 R >>");
            }
            if (_fontType3 != 0)
            {
                // letters as boxes of different widths; codes '0'… are the glyphs with text inside (Type3Text)
                var procs = new StringBuilder();
                var differences = new StringBuilder("[48 ");
                foreach (var n in _type3Names) differences.Append($"/{n} ");
                differences.Append("97 ");
                for (var c = 'a'; c <= 'z'; c++)
                {
                    var proc = AddStream("", $"600 0 d0 50 0 {(c - 'a') * 10 + 300} 700 re f");
                    procs.Append($"/l{c} {proc} 0 R ");
                    differences.Append($"/l{c} ");
                }
                foreach (var p in _type3Procs) procs.Append(p).Append(' ');
                differences.Append(']');
                fonts.Append($" /F3 << /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << {procs}>> " +
                             $"/Encoding << /Type /Encoding /Differences {differences} >> /FirstChar 48 /LastChar 122 /Widths [{string.Join(" ", Enumerable.Repeat("700", 75))}] " +
                             "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            }
            var xobjects = string.Join(" ", _xobjects.Select(x => $"/{x.Key} {x.Value}"));
            if (_image != 0)
            {
                // a gradient: pixels left under a bar would show as different values
                var pixels = new byte[16 * 16 * 3];
                for (var i = 0; i < 256; i++) (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = ((byte)(i % 16 * 16), (byte)(i / 16 * 16), 120);
                var image = AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceRGB", pixels);
                xobjects += $" /Im {image} 0 R";
            }
            var resources = $"<< /Font << {fonts} >> /XObject << {xobjects} >> /Pattern << {string.Join(" ", _patterns.Select(p => $"/{p.Key} {p.Value}"))} >> " +
                            $"/ExtGState << {string.Join(" ", _states.Select(s => $"/{s.Key} {s.Value}"))} >> /Properties << {string.Join(" ", _properties.Select(p => $"/{p.Key} {p.Value}"))} >> >>";
            if (_resources == 0) _resources = Add("");
            _objects[_resources - 1] = Encoding.Latin1.GetBytes(resources);

            var pagesNumber = Add("");
            var kids = new List<string>();
            for (var p = 0; p < _pages.Count; p++)
            {
                var contents = AddStream("", _pages[p].Content.ToString());
                var extra = p == 0 && _rotate != 0 ? $" /Rotate {_rotate}" : "";
                if (p == 1) extra += " /CropBox [10 20 600 830]";
                var annots = _pages[p].Annots.Count > 0 ? $" /Annots [{string.Join(" ", _pages[p].Annots)}]" : "";
                if (_structureRoot != 0) extra += $" /StructParents {p}";
                var page = $"<< /Type /Page /Parent {pagesNumber} 0 R /MediaBox [0 0 612 842] /Resources {_resources} 0 R /Contents {contents} 0 R{extra}{annots} >>";
                if (p < _pageNumbers.Count) Set(_pageNumbers[p], page);
                kids.Add($"{(p < _pageNumbers.Count ? _pageNumbers[p] : Add(page))} 0 R");
            }
            _objects[pagesNumber - 1] = Encoding.Latin1.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {_pages.Count} >>");
            var oc = _ocgs.Count == 0 ? "" :
                $" /OCProperties << /OCGs [{string.Join(" ", _ocgs)}] /D << /OFF [{string.Join(" ", _printOff)}] /AS [<< /Event /Print /Category [/Print] /OCGs [{string.Join(" ", _ocgs)}] >>] >> >>";
            var tagged = "";
            if (_structureRoot != 0)
            {
                var nums = string.Join(" ", _parentTree.Select((tree, p) => $"{p} [{string.Join(" ", tree)}]"));
                var parentTree = Add($"<< /Nums [{nums}] >>");
                Set(_structureRoot, $"<< /Type /StructTreeRoot /K [{string.Join(" ", _structureKids)}] /ParentTree {parentTree} 0 R /ParentTreeNextKey {_pages.Count} >>");
                tagged = $" /StructTreeRoot {_structureRoot} 0 R /MarkInfo << /Marked true >> /Lang (de-DE)";
            }
            var catalog = Add($"<< /Type /Catalog /Pages {pagesNumber} 0 R{oc}{tagged} >>");

            using var output = new MemoryStream();
            output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
            var offsets = new long[_objects.Count];
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets[i] = output.Position;
                output.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
                output.Write(_objects[i]);
                output.Write("\nendobj\n"u8);
            }
            var xref = output.Position;
            var table = new StringBuilder($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) table.Append($"{o:D10} 00000 n \n");
            table.Append($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            output.Write(Encoding.ASCII.GetBytes(table.ToString()));
            return new RedactionCase(name, output.ToArray(), _bars, _markHidden, _expect, _description.ToString().Trim());
        }
    }
}
