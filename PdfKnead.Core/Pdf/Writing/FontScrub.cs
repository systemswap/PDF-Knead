using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// After redaction, the fonts that lost characters keep only what the new file still shows — over all its pages, forms and
/// annotations (docs/FEHLERGEDAECHTNIS.md, No. 169). A character that occurred only in the redacted text would otherwise still tell of
/// itself: its glyph in the embedded program, its mapping (cmap, ToUnicode), its name (/Differences, post, /CharSet), its width
/// (/Widths, /W), its CID (/CIDSet, CIDToGIDMap). Glyph indices and codes stay as they are; only what nothing shows any more is cleared.
/// A font program shared by several fonts keeps what any of them shows. Fonts of the form (/DR, used to write new values) and fonts set
/// by a graphics state stay whole, as does everything if a content stream cannot be read.
/// </summary>
internal static class FontScrub
{
    private static readonly PdfName ToUnicode = PdfName.Get("ToUnicode"), Encoding = PdfName.Get("Encoding"), Differences = PdfName.Get("Differences"),
        Widths = PdfName.Get("Widths"), FirstChar = PdfName.Get("FirstChar"), DescendantFonts = PdfName.Get("DescendantFonts"), W = PdfName.Get("W"),
        CidToGidMap = PdfName.Get("CIDToGIDMap"), FontDescriptor = PdfName.Get("FontDescriptor"), CharSet = PdfName.Get("CharSet"),
        CidSet = PdfName.Get("CIDSet"), FontFile = PdfName.Get("FontFile"), FontFile2 = PdfName.Get("FontFile2"), FontFile3 = PdfName.Get("FontFile3"), Subtype = PdfName.Get("Subtype");

    /// <summary>Replacements (object number → new object) for the fonts <paramref name="touched"/> and their programs.</summary>
    /// <param name="doc">The source document.</param>
    /// <param name="pages">Its pages in the new file (redacted ones with their new content).</param>
    /// <param name="touched">Object numbers of the font dictionaries that lost characters.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="replaced">Objects written in place of the originals (an appearance stream without its invisible text); null = the originals.</param>
    public static Dictionary<int, PdfObject> Overrides(PdfDocument doc, IEnumerable<PdfPage> pages, IReadOnlySet<int> touched, CancellationToken cancel,
        IReadOnlyDictionary<int, PdfObject>? replaced = null)
    {
        var result = new Dictionary<int, PdfObject>();
        if (touched.Count == 0) return result;
        var usage = new FontUsage(doc, replaced).Collect(pages, cancel);
        if (usage.Failed) return result;
        // Who refers to which part: a part shared by several fonts keeps what any of them shows (also copies a redaction made of a font
        // for one page, which keep their widths and maps), and stays whole if one of them must. Shared means in the same role: a damaged
        // file pointing one font's descendant at the object that is another font's descriptor gave the first font the CIDs and widths of
        // the second (docs/FEHLERGEDAECHTNIS.md, No. 424)
        var users = new Dictionary<(int Number, PdfName Role), List<PdfDictionary>>();
        void Note(PdfDictionary font)
        {
            void Add(PdfName role, PdfObject? raw)
            {
                if (raw is not PdfReference r) return;
                if (!users.TryGetValue((r.Id.Number, role), out var list)) users[(r.Id.Number, role)] = list = [];
                if (!list.Contains(font, ReferenceEqualityComparer.Instance)) list.Add(font);
            }
            foreach (var key in (ReadOnlySpan<PdfName>)[ToUnicode, Widths, Encoding, FontDescriptor])
                Add(key, font.GetRaw(key));
            if (font.GetArray(DescendantFonts) is { Count: >= 1 } kids)
            {
                Add(DescendantFonts, kids[0]);
                if (kids.Get(0) is PdfDictionary kid)
                    foreach (var key in (ReadOnlySpan<PdfName>)[W, CidToGidMap, FontDescriptor])
                        Add(key, kid.GetRaw(key));
            }
        }
        foreach (var font in usage.Codes.Keys) Note(font);
        foreach (var font in usage.WholeFonts) Note(font);
        foreach (var number in touched)
            if (doc.GetObject(number) is PdfDictionary font) Note(font);
        HashSet<(uint Code, int Length)>? CodesOf(PdfName role, PdfObject? raw, PdfDictionary own)
        {
            if (raw is not PdfReference r || !users.TryGetValue((r.Id.Number, role), out var list))
                return usage.WholeFonts.Contains(own) ? null : usage.Codes.GetValueOrDefault(own) ?? [];
            if (list.Any(usage.WholeFonts.Contains)) return null;
            var union = new HashSet<(uint, int)>();
            foreach (var font in list)
                if (usage.Codes.TryGetValue(font, out var codes)) union.UnionWith(codes);
            return union;
        }

        // The CIDs a shared descendant must keep: each font sharing it reads its own codes through its own CMap — read through the one being
        // scrubbed, the codes of another page came out as other CIDs, and its widths went (docs/FEHLERGEDAECHTNIS.md, No. 392). With each its
        // width as the display reads it: /W may name a CID twice, and a reading of its own took the other one (No. 401)
        Dictionary<int, double>? CidsOf(PdfObject? raw, PdfDictionary own)
        {
            IReadOnlyList<PdfDictionary> fonts = raw is PdfReference r && users.TryGetValue((r.Id.Number, DescendantFonts), out var list) ? list : [own];
            if (fonts.Any(usage.WholeFonts.Contains)) return null;
            var cids = new Dictionary<int, double>();
            foreach (var font in fonts)
                if (usage.Codes.TryGetValue(font, out var codes))
                {
                    var reader = PdfFont.Get(font, doc);
                    foreach (var (code, length) in codes)
                        if (reader.Cid(code, length) is >= 0 and var cid) cids.TryAdd(cid, reader.CidWidth(cid));
                }
            return cids;
        }

        var programs = new HashSet<int>();
        foreach (var number in touched)
        {
            cancel.ThrowIfCancellationRequested();
            if (doc.GetObject(number) is not PdfDictionary dict || usage.WholeFonts.Contains(dict)) continue;
            try
            {
                ScrubDictionary(doc, number, dict, CodesOf, CidsOf, usage.Codes.GetValueOrDefault(dict) ?? [], result);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                continue;   // unreadable: stays as it is
            }
            if (ProgramOf(dict) is { } program) programs.Add(program.Id.Number);
        }
        foreach (var program in programs) ScrubProgram(doc, program, usage, result);
        return result;
    }

    // -------------------------------------------------- The font dictionary

    /// <summary>
    /// Clears from the font dictionary (ToUnicode, /Widths, /Differences, /CharSet; of a composite font /W, CIDToGIDMap, /CIDSet) what the
    /// codes no longer shown would tell. The new dictionary is then read as the display reads it, and every code still shown must give the
    /// same glyph — outline, width, text, placement — as before; otherwise the dictionary stays as it is. Its parts were rewritten by a
    /// reading of their own (/Differences counted on beyond the codes the display names), and a shown code could get another glyph or none
    /// (docs/FEHLERGEDAECHTNIS.md, No. 429).
    /// </summary>
    /// <param name="codesOf">Codes whose traces a part of the font (its entry in a dictionary, the dictionary itself) must keep; null if it stays whole.</param>
    /// <param name="cidsOf">CIDs a descendant font (its entry in the dictionary) must keep, as the fonts sharing it read their codes, each with
    /// its width as the display reads it; null if it stays whole.</param>
    /// <param name="shown">The codes this font shows.</param>
    private static void ScrubDictionary(PdfDocument doc, int number, PdfDictionary dict,
        Func<PdfName, PdfObject?, PdfDictionary, HashSet<(uint Code, int Length)>?> codesOf, Func<PdfObject?, PdfDictionary, Dictionary<int, double>?> cidsOf,
        HashSet<(uint Code, int Length)> shown, Dictionary<int, PdfObject> overrides)
    {
        var result = new Dictionary<int, PdfObject>();   // collected here first, taken only if the display reads the new dictionary as the old
        // the new font with every part in place, as the display would read it from the new file
        var view = Clone(dict);
        PdfDictionary? copy = null;
        PdfDictionary Copy() => copy ??= Clone(dict);
        void Replace(PdfDictionary owner, PdfName key, PdfObject value, Func<PdfDictionary> ownerCopy)
        {
            if (owner.GetRaw(key) is PdfReference r) result[r.Id.Number] = value;
            else ownerCopy().Set(key, value);
            view.Set(key, value);
        }

        if (dict.Get(ToUnicode) is PdfStream unicode && PdfFont.ReadToUnicode(dict, doc) is { } cmap && codesOf(ToUnicode, dict.GetRaw(ToUnicode), dict) is { } mapped)
            Replace(dict, ToUnicode, ToUnicodeOf(doc, unicode, cmap, mapped), Copy);

        bool[] Used(HashSet<(uint Code, int Length)> codes)
        {
            var used = new bool[256];
            foreach (var (code, _) in codes) used[code & 0xFF] = true;
            return used;
        }
        var simple = dict.GetArray(DescendantFonts) is null;
        if (simple)
        {
            if (dict.GetArray(Widths) is { } widths && dict.GetInteger(FirstChar) is { } first && !SystemMetrics(dict, widths) &&
                codesOf(Widths, dict.GetRaw(Widths), dict) is { } widthCodes)
                Replace(dict, Widths, WidthsWith(widths, (int)first, Used(widthCodes)), Copy);
            if (dict.Get(Encoding) is PdfDictionary encoding && encoding.GetArray(Differences) is { } differences && codesOf(Encoding, dict.GetRaw(Encoding), dict) is { } nameCodes)
                Replace(dict, Encoding, EncodingWith(encoding, differences, Used(nameCodes)), Copy);
            if (dict.GetDictionary(FontDescriptor) is { } descriptor && descriptor.ContainsKey(CharSet) && codesOf(FontDescriptor, dict.GetRaw(FontDescriptor), dict) is not null)
            {
                var newDescriptor = Clone(descriptor);
                newDescriptor.Remove(CharSet);
                Replace(dict, FontDescriptor, newDescriptor, Copy);
            }
        }
        else if (dict.GetArray(DescendantFonts) is { Count: >= 1 } kids && kids.Get(0) is PdfDictionary cidFont && cidsOf(kids[0], dict) is { } kept)
        {
            // the parts of the descendant are its own: the CIDs of the fonts sharing it, each with its width as the display reads it
            var cids = new SortedDictionary<int, double?>();
            foreach (var (cid, width) in kept) cids[cid] = double.IsFinite(width) ? width : null;
            var newCidFont = Clone(cidFont);
            var cidView = Clone(cidFont);
            var changed = false;
            if (cidFont.ContainsKey(W))
            {
                var w = new PdfArray();
                foreach (var (cid, width) in cids)
                    if (width is { } value)
                    {
                        w.Add(PdfInteger.Of(cid));
                        w.Add(new PdfArray([new PdfReal(value)]));
                    }
                cidView.Set(W, w);
                if (cidFont.GetRaw(W) is PdfReference wr) result[wr.Id.Number] = w;
                else
                {
                    newCidFont.Set(W, w);
                    changed = true;
                }
            }
            if (cidFont.Get(CidToGidMap) is PdfStream map)
            {
                var bytes = doc.GetStreamData(map);
                var cleared = new byte[bytes.Length];
                foreach (var cid in cids.Keys)
                    if (CompositeFont.InMap(bytes.Length, cid)) (cleared[2 * cid], cleared[2 * cid + 1]) = (bytes[2 * cid], bytes[2 * cid + 1]);
                var stream = new PdfStream(Plain(map.Dictionary), cleared);
                cidView.Set(CidToGidMap, stream);
                if (cidFont.GetRaw(CidToGidMap) is PdfReference mr) result[mr.Id.Number] = stream;
                else
                {
                    newCidFont.Set(CidToGidMap, stream);
                    changed = true;
                }
            }
            if (cidFont.GetDictionary(FontDescriptor) is { } descriptor && (descriptor.ContainsKey(CidSet) || descriptor.ContainsKey(CharSet)))
            {
                var newDescriptor = Clone(descriptor);
                newDescriptor.Remove(CidSet);
                newDescriptor.Remove(CharSet);
                cidView.Set(FontDescriptor, newDescriptor);
                if (cidFont.GetRaw(FontDescriptor) is PdfReference dr) result[dr.Id.Number] = newDescriptor;
                else
                {
                    newCidFont.Set(FontDescriptor, newDescriptor);
                    changed = true;
                }
            }
            if (changed)
            {
                if (kids[0] is PdfReference kr) result[kr.Id.Number] = newCidFont;
                else Copy().Set(DescendantFonts, new PdfArray([newCidFont]));
            }
            view.Set(DescendantFonts, new PdfArray([cidView]));
        }
        if (copy is not null) result[number] = copy;
        if (result.Count == 0 || !SameShown(PdfFont.Get(dict, doc), PdfFont.Uncached(view, doc), shown)) return;
        foreach (var (key, value) in result) overrides[key] = value;
    }

    /// <summary>Whether every code in <paramref name="shown"/> gives the same glyph in both fonts: outline, width, text, placement and procedure.</summary>
    internal static bool SameShown(PdfFont before, PdfFont after, IEnumerable<(uint Code, int Length)> shown)
    {
        foreach (var (code, length) in shown)
        {
            PdfGlyph a = before.Glyph(code, length), b = after.Glyph(code, length);
            if (Math.Abs(a.Width - b.Width) > 1e-9 || a.Text != b.Text || a.ToText != b.ToText || !ReferenceEquals(a.Procedure, b.Procedure)
                || Math.Abs(a.VerticalAdvance - b.VerticalAdvance) > 1e-9 || Math.Abs(a.OriginX - b.OriginX) > 1e-9 || Math.Abs(a.OriginY - b.OriginY) > 1e-9
                || a.Outline.IsEmpty != b.Outline.IsEmpty || !a.Outline.Verbs.SequenceEqual(b.Outline.Verbs) || !a.Outline.Points.SequenceEqual(b.Outline.Points))
                return false;
            if (before.Cid(code, length) != after.Cid(code, length) && before.Cid(code, length) >= 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether /Widths of a simple font are the metrics of a font that is not embedded, given for (nearly) every code: they come from the
    /// font on the producer's system and tell nothing of the characters used. They stay, as readers choose the substitute font by them —
    /// with the widths of the unused codes cleared, the rest can all be equal and the reader takes a fixed-width font
    /// (docs/FEHLERGEDAECHTNIS.md, No. 243). Up to one code in ten may have none (codes the encoding leaves empty). A table with widths
    /// for the used codes only (0 for the others) does tell, as do the widths of an embedded font or a Type 3 font, whose glyphs are in
    /// the file.
    /// </summary>
    private static bool SystemMetrics(PdfDictionary dict, PdfArray widths)
    {
        if (widths.Count == 0 || dict.GetName(Subtype)?.Value == "Type3") return false;
        if (dict.GetDictionary(FontDescriptor) is { } descriptor &&
            (descriptor.ContainsKey(FontFile) || descriptor.ContainsKey(FontFile2) || descriptor.ContainsKey(FontFile3)))
            return false;
        var empty = 0;
        for (var i = 0; i < widths.Count; i++)
            if (widths.GetNumber(i) is not > 0) empty++;
        return empty * 10 <= widths.Count;
    }

    /// <summary>/Widths of a simple font with 0 for every code not in <paramref name="used"/>.</summary>
    internal static PdfArray WidthsWith(PdfArray widths, int first, bool[] used)
    {
        var cleared = new PdfArray();
        for (var i = 0; i < widths.Count; i++)
            cleared.Add(first + i is >= 0 and < 256 && used[first + i] ? widths.Get(i) : PdfInteger.Of(0));
        return cleared;
    }

    /// <summary>
    /// A copy of the encoding whose /Differences name only the codes in <paramref name="used"/> (at their codes), read as the display reads
    /// them (PdfFont): entries as they stand, a reference neither a code nor a name, and a name outside 0…255 names no code and does not
    /// move the count on (docs/FEHLERGEDAECHTNIS.md, No. 429).
    /// </summary>
    internal static PdfDictionary EncodingWith(PdfDictionary encoding, PdfArray differences, bool[] used)
    {
        var kept = new PdfArray();
        var code = 0;
        var open = false;
        for (var i = 0; i < differences.Count; i++)
        {
            var item = differences[i];
            if (item.AsNumber() is { } n)
            {
                code = (int)n;
                open = false;
                continue;
            }
            if (item is not PdfName name) continue;
            if (code is not (>= 0 and < 256))
            {
                open = false;
                continue;
            }
            if (used[code])
            {
                if (!open) kept.Add(PdfInteger.Of(code));
                kept.Add(name);
                open = true;
            }
            else open = false;
            code++;
        }
        var newEncoding = Clone(encoding);
        newEncoding.Set(Differences, kept);
        return newEncoding;
    }

    /// <summary>A ToUnicode CMap with the mappings of the codes still shown only, in the code space of the original.</summary>
    internal static PdfStream ToUnicodeOf(PdfDocument doc, PdfStream original, CMap cmap, HashSet<(uint Code, int Length)> codes)
    {
        var text = System.Text.Encoding.Latin1.GetString(doc.GetStreamData(original));
        var spaces = Regex.Matches(text, @"\d+\s+begincodespacerange.*?endcodespacerange", RegexOptions.Singleline).Select(m => m.Value).ToList();
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        if (spaces.Count > 0) foreach (var space in spaces) sb.Append(space).Append('\n');
        else
            foreach (var length in codes.Select(c => c.Length).Distinct().Order())
                sb.Append("1 begincodespacerange\n<").Append(new string('0', 2 * length)).Append("> <").Append(new string('F', 2 * length)).Append(">\nendcodespacerange\n");
        var entries = codes.Order().Select(c => (c.Code, c.Length, Text: cmap.ToUnicode(c.Code, c.Length))).Where(e => e.Text is { Length: > 0 }).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var block = entries.Skip(i).Take(100).ToList();
            sb.Append(block.Count).Append(" beginbfchar\n");
            foreach (var (code, length, value) in block)
            {
                sb.Append('<').Append(code.ToString("X" + 2 * length, CultureInfo.InvariantCulture)).Append("> <");
                foreach (var b in System.Text.Encoding.BigEndianUnicode.GetBytes(value!)) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(sb.ToString()));
    }

    // -------------------------------------------------- The font program

    /// <summary>The embedded program of a font (of its descendant for a composite font); null if it has none.</summary>
    private static PdfReference? ProgramOf(PdfDictionary font)
    {
        var holder = font.GetArray(DescendantFonts) is { Count: >= 1 } kids && kids.Get(0) is PdfDictionary cid ? cid : font;
        if (holder.GetDictionary(FontDescriptor) is not { } descriptor) return null;
        foreach (var key in (ReadOnlySpan<PdfName>)[FontFile2, FontFile3, FontFile])
            if (descriptor.GetRaw(key) is PdfReference r) return r;
        return null;
    }

    /// <summary>
    /// The program keeps the glyphs that any font using it still shows; if one of them must stay whole, or the program cannot be read, it
    /// stays as it is.
    /// </summary>
    /// <summary>
    /// Whether every glyph that stays reads the same after scrubbing as before — its outline and its advance, read the way the display reads
    /// them. In a damaged program the scrubber can read a glyph's data differently from the display: a glyph drawn huge beside the page
    /// came back small, under a bar, and its text with it (docs/FEHLERGEDAECHTNIS.md, No. 379). Then the program stays as it is: what the page
    /// shows comes before clearing the traces of a broken font.
    /// </summary>
    /// <param name="afterCount">The number of glyphs read after scrubbing: it must stay — the display looks glyphs up only below it, and a
    /// scrubbed font that counted none drew nothing (docs/FEHLERGEDAECHTNIS.md, No. 389).</param>
    internal static bool SameGlyphs(HashSet<int> kept, int count, Func<int, Core.Fonts.GlyphOutline> before, Func<int, double> beforeAdvance,
        Func<int, Core.Fonts.GlyphOutline>? after, Func<int, double> afterAdvance, int afterCount)
    {
        if (after is null || afterCount != count) return false;
        foreach (var gid in kept)
        {
            if (gid < 0 || gid >= count) continue;
            Core.Fonts.GlyphOutline a, b;
            double wa, wb;
            try
            {
                (a, wa) = (before(gid), beforeAdvance(gid));
            }
            catch (Exception e) when (e is Core.Fonts.FontFormatException or IndexOutOfRangeException or ArgumentException)
            {
                continue;   // the display cannot read it either
            }
            try
            {
                (b, wb) = (after(gid), afterAdvance(gid));
            }
            catch (Exception e) when (e is Core.Fonts.FontFormatException or IndexOutOfRangeException or ArgumentException)
            {
                return false;
            }
            if (Math.Abs(wa - wb) > 1e-6 || a.IsEmpty != b.IsEmpty || !a.Verbs.SequenceEqual(b.Verbs) || !a.Points.SequenceEqual(b.Points)) return false;
        }
        return true;
    }

    /// <summary>
    /// For a Type 1 program: whether the display reads the scrubbed one as the one before — both unreadable, or every glyph kept the same
    /// (present or not, outline, advance) and the built-in encoding the same for them. A damaged program the display could not read in part
    /// (a glyph missing, drawn as nothing) came out of the scrubber readable, and a letter appeared that was not to be seen before
    /// (docs/FEHLERGEDAECHTNIS.md, No. 393).
    /// </summary>
    internal static bool SameType1(byte[] before, byte[] after, IReadOnlySet<string> kept)
    {
        var a = Core.Pdf.Fonts.FontProgram.FromBytes(before)?.Type1;
        var b = Core.Pdf.Fonts.FontProgram.FromBytes(after)?.Type1;
        if (a is null || b is null) return a is null && b is null;
        foreach (var name in kept)
        {
            if (a.HasGlyph(name) != b.HasGlyph(name)) return false;
            if (!a.HasGlyph(name)) continue;
            Core.Fonts.GlyphOutline oa, ob;
            try
            {
                (oa, ob) = (a.Outline(name), b.Outline(name));
            }
            catch (Exception e) when (e is Core.Fonts.FontFormatException or IndexOutOfRangeException or ArgumentException)
            {
                return false;
            }
            if (Math.Abs(a.AdvanceWidth(name) - b.AdvanceWidth(name)) > 1e-6 || !oa.Verbs.SequenceEqual(ob.Verbs) || !oa.Points.SequenceEqual(ob.Points)) return false;
        }
        for (var code = 0; code < 256; code++)
            if (kept.Contains(a.Encoding[code] ?? "") && a.Encoding[code] != b.Encoding[code]) return false;
        return true;
    }

    private static void ScrubProgram(PdfDocument doc, int program, FontUsage usage, Dictionary<int, PdfObject> result)
    {
        var gids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dict, codes) in usage.Codes)
        {
            if (ProgramOf(dict)?.Id.Number != program) continue;
            if (usage.WholeFonts.Contains(dict)) return;
            var font = PdfFont.Get(dict, doc);
            foreach (var (code, length) in codes)
            {
                var (g, n) = font.ProgramGlyphs(code, length);
                gids.UnionWith(g);
                names.UnionWith(n);
            }
        }
        if (doc.GetObject(program) is not PdfStream stream) return;
        try
        {
            var data = doc.GetStreamData(stream);
            byte[]? scrubbed = null;
            if (Core.Fonts.OpenTypeFont.LooksLikeSfnt(data))
            {
                if (Core.Fonts.OpenTypeFont.Parse(data) is { Broken: false } ot && (ot.HasTable("glyf") || ot.HasTable("CFF ")))
                {
                    scrubbed = Core.Fonts.TrueTypeSubsetter.Scrub(data, gids);
                    var after = Core.Fonts.OpenTypeFont.Parse(scrubbed);
                    if (!SameGlyphs(gids, ot.GlyphCount, ot.Outline, ot.AdvanceWidth, after.Outline, after.AdvanceWidth, after.GlyphCount)) return;
                    // every shown code draws the same glyph as before, as the display chooses it: a code whose cmap entry pointed beyond the
                    // glyphs drew nothing; with the entry cleared it fell to glyph 0, a box under a bar (docs/FEHLERGEDAECHTNIS.md, No. 402)
                    foreach (var (user, shown) in usage.Codes)
                    {
                        if (ProgramOf(user)?.Id.Number != program) continue;
                        var reader = PdfFont.Get(user, doc);
                        foreach (var (code, length) in shown)
                            if (reader.DrawnGlyphWith(ot, code, length) is var drawn && drawn != int.MinValue && reader.DrawnGlyphWith(after, code, length) != drawn)
                                return;
                    }
                }
            }
            else if (stream.Dictionary.GetName(PdfName.Subtype)?.Value is "Type1C" or "CIDFontType0C" && data is [1, ..])
            {
                // bare CFF (FontFile3)
                scrubbed = Core.Fonts.CffScrubber.Scrub(data, gids).Data;
                var before = Core.Fonts.CffFont.Parse(data);
                var after = Core.Fonts.CffFont.Parse(scrubbed);
                if (!SameGlyphs(gids, before.GlyphCount, before.Outline, before.AdvanceWidth, after.Outline, after.AdvanceWidth, after.GlyphCount)) return;
                // every shown code draws the same glyph as before, read through the scrubbed program as the display reads it: removing the
                // names, CIDs and codes of glyphs no longer used can move what a shown code reaches (the charset, the built-in encoding),
                // and only each glyph by its number was compared (docs/FEHLERGEDAECHTNIS.md, No. 430)
                var replacement = new PdfStream(Plain(stream.Dictionary), scrubbed);
                foreach (var (user, shown) in usage.Codes)
                    if (ProgramOf(user)?.Id.Number == program && !SameShown(PdfFont.Get(user, doc), PdfFont.Uncached(WithProgram(user, replacement), doc), shown))
                        return;
            }
            else if (data is [(byte)'%', (byte)'!', ..] && stream.Dictionary.ContainsKey(PdfName.Get("Length2")) || data is [0x80, 1, ..])
            {
                // Type 1 (FontFile): clear text, eexec part, trailer, each with its length — or the segments of a PFB file, written in that
                // form afterwards
                var length1 = (int?)stream.Dictionary.GetInteger(PdfName.Get("Length1"));
                var length2 = (int?)stream.Dictionary.GetInteger(PdfName.Get("Length2"));
                var type1 = Core.Fonts.Type1Scrubber.Scrub(data, names, length1, length2);
                if (!SameType1(data, type1.Data, names)) return;
                var plain = Plain(stream.Dictionary);
                plain.Set(PdfName.Get("Length1"), PdfInteger.Of(type1.Clear));
                plain.Set(PdfName.Get("Length2"), PdfInteger.Of(type1.Encrypted));
                plain.Set(PdfName.Get("Length3"), PdfInteger.Of(type1.Trailer));
                result[program] = new PdfStream(plain, type1.Data);
                return;
            }
            if (scrubbed is null) return;
            var dict = Plain(stream.Dictionary);
            if (dict.ContainsKey(PdfName.Get("Length1"))) dict.Set(PdfName.Get("Length1"), PdfInteger.Of(scrubbed.Length));
            result[program] = new PdfStream(dict, scrubbed);
        }
        catch (Exception e) when (e is Core.Fonts.FontFormatException or PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // unreadable: stays as it is
        }
    }

    // -------------------------------------------------- Helpers

    /// <summary>A copy of the font whose descriptor (of its descendant for a composite font) holds <paramref name="program"/> as FontFile3.</summary>
    private static PdfDictionary WithProgram(PdfDictionary font, PdfStream program)
    {
        var view = Clone(font);
        var cid = font.GetArray(DescendantFonts) is { Count: >= 1 } kids && kids.Get(0) is PdfDictionary kid ? Clone(kid) : null;
        var holder = cid ?? view;
        if (holder.GetDictionary(FontDescriptor) is { } descriptor)
        {
            var copy = Clone(descriptor);
            copy.Set(FontFile3, program);
            holder.Set(FontDescriptor, copy);
        }
        if (cid is not null) view.Set(DescendantFonts, new PdfArray([cid]));
        return view;
    }

    private static PdfDictionary Clone(PdfDictionary dict)
    {
        var copy = new PdfDictionary { Resolver = dict.Resolver };
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    /// <summary>A stream dictionary for new, unfiltered data.</summary>
    private static PdfDictionary Plain(PdfDictionary dict)
    {
        var copy = Clone(dict);
        foreach (var key in (ReadOnlySpan<string>)["Length", "Filter", "DecodeParms", "DL"]) copy.Remove(PdfName.Get(key));
        return copy;
    }
}
