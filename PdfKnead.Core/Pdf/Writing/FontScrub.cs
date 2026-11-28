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
        CidSet = PdfName.Get("CIDSet"), FontFile = PdfName.Get("FontFile"), FontFile2 = PdfName.Get("FontFile2"), FontFile3 = PdfName.Get("FontFile3"), Subtype = PdfName.Get("Subtype"),
        BaseFont = PdfName.Get("BaseFont"), LastChar = PdfName.Get("LastChar"), Flags = PdfName.Get("Flags");

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
            usage.Numbers.TryAdd(dict, number);
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
    /// Whether every glyph that stays reads the same after scrubbing as before — its outline and its advance, read the way the display reads
    /// them. In a damaged program the scrubber can read a glyph's data differently from the display: a glyph drawn huge beside the page
    /// came back small, under a bar, and its text with it (docs/FEHLERGEDAECHTNIS.md, No. 379). Then the scrubbed program is not taken: what the
    /// page shows comes first — the font gets a program from the outlines the display draws instead (<see cref="Replace"/>, No. 1164).
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

    /// <summary>
    /// The program keeps the glyphs that any font using it still shows; if one of them must stay whole, it stays as it is. A program that
    /// cannot be scrubbed does not stay (M6: nothing of the redacted text may remain): the fonts showing it get programs of their own from what
    /// the display draws (<see cref="Replace"/>; docs/FEHLERGEDAECHTNIS.md, No. 1164 — before such a program stayed whole, with the glyphs of
    /// the redacted text).
    /// </summary>
    private static void ScrubProgram(PdfDocument doc, int program, FontUsage usage, Dictionary<int, PdfObject> result)
    {
        var gids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        // the CIDs still shown: a removed glyph of a CID-keyed CFF must not take one of them as its new CID (No. 1158)
        var cids = new HashSet<int>();
        var unread = false;
        foreach (var (dict, codes) in usage.Codes)
        {
            if (ProgramOf(dict)?.Id.Number != program) continue;
            if (usage.WholeFonts.Contains(dict)) return;
            var font = PdfFont.Get(dict, doc);
            unread |= !Reads(font);
            foreach (var (code, length) in codes)
            {
                var (g, n) = font.ProgramGlyphs(code, length);
                gids.UnionWith(g);
                names.UnionWith(n);
                if (font.Cid(code, length) is >= 0 and var cid) cids.Add(cid);
            }
        }
        if (doc.GetObject(program) is not PdfStream stream) return;
        // a program the display does not read (it draws the substitute) is not scrubbed but taken out
        if (!unread && Scrubbed(doc, program, stream, gids, names, cids, usage, result)) return;
        Replace(doc, program, usage, result);
    }

    /// <summary>Whether the display reads the embedded program of the font (otherwise it draws a substitute).</summary>
    private static bool Reads(PdfFont font) => font is SimpleFont { DrawsEmbeddedProgram: true } or CompositeFont { DrawsEmbeddedProgram: true };

    /// <summary>
    /// The program scrubbed down to the glyphs still shown (TrueType and OpenType, bare CFF, Type 1), taken only where the display reads every
    /// shown code of every font using it as before.
    /// </summary>
    /// <returns>Whether the scrubbed program was taken (written to <paramref name="result"/>).</returns>
    private static bool Scrubbed(PdfDocument doc, int program, PdfStream stream, HashSet<int> gids, HashSet<string> names, HashSet<int> cids, FontUsage usage,
        Dictionary<int, PdfObject> result)
    {
        try
        {
            var data = doc.GetStreamData(stream);
            byte[]? scrubbed = null;
            // a collection as FontFile2: the display reads its first font, which is scrubbed as a font file of its own (PDF allows no
            // collections; before the scrubber looked for tables at byte 0, failed, and the collection stayed whole — No. 1164)
            if (data.AsSpan().StartsWith("ttcf"u8)) data = Core.Fonts.TrueTypeSubsetter.FirstOfCollection(data);
            if (Core.Fonts.OpenTypeFont.LooksLikeSfnt(data))
            {
                // (CFF2 as well: before it stayed whole, with the glyphs of the redacted text; docs/FEHLERGEDAECHTNIS.md, No. 1158)
                // and a program whose glyphs exist only as images (EBLC/EBDT, CBLC/CBDT, sbix): before it stayed whole, with the images of the
                // redacted characters (docs/FEHLERGEDAECHTNIS.md, No. 1169)
                if (Core.Fonts.OpenTypeFont.Parse(data) is not { Broken: false } ot
                    || !(ot.HasTable("glyf") || ot.HasTable("CFF ") || ot.Cff2 is not null || Core.Fonts.BitmapScrubber.ImagesOnly(ot)))
                    return false;
                scrubbed = Core.Fonts.TrueTypeSubsetter.Scrub(data, gids, cids);
                var after = Core.Fonts.OpenTypeFont.Parse(scrubbed);
                if (!SameGlyphs(gids, ot.GlyphCount, ot.Outline, ot.AdvanceWidth, after.Outline, after.AdvanceWidth, after.GlyphCount)) return false;
                // the images of the kept glyphs as before, in every strike (and their components)
                if (Core.Fonts.BitmapScrubber.ImagesOnly(ot) && !Core.Fonts.BitmapScrubber.SameImages(ot, after, Core.Fonts.BitmapScrubber.Kept(ot, gids))) return false;
                // every shown code draws the same glyph as before, as the display chooses it: a code whose cmap entry pointed beyond the
                // glyphs drew nothing; with the entry cleared it fell to glyph 0, a box under a bar (docs/FEHLERGEDAECHTNIS.md, No. 402)
                foreach (var (user, shown) in usage.Codes)
                {
                    if (ProgramOf(user)?.Id.Number != program) continue;
                    var reader = PdfFont.Get(user, doc);
                    foreach (var (code, length) in shown)
                        if (reader.DrawnGlyphWith(ot, code, length) is var drawn && drawn != int.MinValue && reader.DrawnGlyphWith(after, code, length) != drawn)
                            return false;
                }
            }
            else if (stream.Dictionary.GetName(PdfName.Subtype)?.Value is "Type1C" or "CIDFontType0C" && data is [1, ..])
            {
                // bare CFF (FontFile3)
                scrubbed = Core.Fonts.CffScrubber.Scrub(data, gids, cids).Data;
                var before = Core.Fonts.CffFont.Parse(data);
                var after = Core.Fonts.CffFont.Parse(scrubbed);
                if (!SameGlyphs(gids, before.GlyphCount, before.Outline, before.AdvanceWidth, after.Outline, after.AdvanceWidth, after.GlyphCount)) return false;
                // every shown code draws the same glyph as before, read through the scrubbed program as the display reads it: removing the
                // names, CIDs and codes of glyphs no longer used can move what a shown code reaches (the charset, the built-in encoding),
                // and only each glyph by its number was compared (docs/FEHLERGEDAECHTNIS.md, No. 430)
                var replacement = new PdfStream(Plain(stream.Dictionary), scrubbed);
                foreach (var (user, shown) in usage.Codes)
                    if (ProgramOf(user)?.Id.Number == program && !SameShown(PdfFont.Get(user, doc), PdfFont.Uncached(WithProgram(user, replacement), doc), shown))
                        return false;
            }
            else if (data is [(byte)'%', (byte)'!', ..] && stream.Dictionary.ContainsKey(PdfName.Get("Length2")) || data is [0x80, 1, ..])
            {
                // Type 1 (FontFile): clear text, eexec part, trailer, each with its length — or the segments of a PFB file, written in that
                // form afterwards
                var length1 = (int?)stream.Dictionary.GetInteger(PdfName.Get("Length1"));
                var length2 = (int?)stream.Dictionary.GetInteger(PdfName.Get("Length2"));
                var type1 = Core.Fonts.Type1Scrubber.Scrub(data, names, length1, length2);
                if (!SameType1(data, type1.Data, names)) return false;
                var plain = Plain(stream.Dictionary);
                plain.Set(PdfName.Get("Length1"), PdfInteger.Of(type1.Clear));
                plain.Set(PdfName.Get("Length2"), PdfInteger.Of(type1.Encrypted));
                plain.Set(PdfName.Get("Length3"), PdfInteger.Of(type1.Trailer));
                result[program] = new PdfStream(plain, type1.Data);
                return true;
            }
            if (scrubbed is null) return false;
            var dict = Plain(stream.Dictionary);
            if (dict.ContainsKey(PdfName.Get("Length1"))) dict.Set(PdfName.Get("Length1"), PdfInteger.Of(scrubbed.Length));
            result[program] = new PdfStream(dict, scrubbed);
            return true;
        }
        catch (Exception e) when (e is Core.Fonts.FontFormatException or PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return false;   // cannot be scrubbed: replaced
        }
    }

    // -------------------------------------------------- A program that cannot be scrubbed

    /// <summary>
    /// A program that cannot be scrubbed — refused by the scrubbers (arithmetic or put/get in Type 2 charstrings, a synthetic Type 1 font), not
    /// read by the display, or read differently once scrubbed — leaves the file. Each font showing it: where the display draws its embedded
    /// program, it gets a new one from the outlines the display draws for its shown codes (<see cref="Rebuilt"/>, as the PDF/A copy rebuilds a
    /// font); where the display draws the substitute, it loses the program and is drawn as before (<see cref="WithoutProgram"/>). The object
    /// of the old program becomes null, so a font nothing shows does not carry it either (docs/FEHLERGEDAECHTNIS.md, No. 1164).
    /// </summary>
    private static void Replace(PdfDocument doc, int program, FontUsage usage, Dictionary<int, PdfObject> result)
    {
        foreach (var (user, shown) in usage.Codes)
        {
            if (ProgramOf(user)?.Id.Number != program) continue;
            int? number = usage.Numbers.TryGetValue(user, out var n) ? n : null;
            var current = number is { } k && result.TryGetValue(k, out var replaced) && replaced is PdfDictionary d ? d : user;
            var font = PdfFont.Get(user, doc);
            if (!Reads(font) && WithoutProgram(doc, current, number, font, shown, result)) continue;
            if (number is { } m && Rebuilt(doc, current, font, shown, result) is { } rebuilt)
            {
                result[m] = rebuilt;
                continue;
            }
            // neither way gives the same page: the program goes all the same (the font is then drawn with the substitute)
            WithoutProgram(doc, current, number, font, null, result);
        }
        result[program] = PdfNull.Instance;
    }

    /// <summary>The object an entry stands for in the new file: its replacement so far, or the object of the source.</summary>
    private static PdfObject? Current(PdfDocument doc, PdfObject? raw, Dictionary<int, PdfObject> result) =>
        raw is PdfReference r ? result.TryGetValue(r.Id.Number, out var replaced) ? replaced : doc.GetObject(r.Id.Number) : raw;

    /// <summary>
    /// The font without its embedded program (its descriptor — of its descendant for a composite font — without FontFile, FontFile2, FontFile3
    /// and the glyph sets that describe them), where the display draws it then as before (checked for <paramref name="shown"/>; null: not
    /// checked).
    /// </summary>
    /// <returns>Whether the font went without its program.</returns>
    private static bool WithoutProgram(PdfDocument doc, PdfDictionary current, int? number, PdfFont font, HashSet<(uint Code, int Length)>? shown,
        Dictionary<int, PdfObject> result)
    {
        var kids = current.GetArray(DescendantFonts);
        var kidRaw = kids is { Count: >= 1 } ? kids[0] : null;
        var kid = kidRaw is null ? null : Current(doc, kidRaw, result) as PdfDictionary;
        if (kids is { Count: >= 1 } && kid is null) return false;
        var holder = kid ?? current;
        if (Current(doc, holder.GetRaw(FontDescriptor), result) is not PdfDictionary descriptor) return false;
        var bare = Clone(descriptor);
        foreach (var key in (ReadOnlySpan<PdfName>)[FontFile, FontFile2, FontFile3, CidSet, CharSet]) bare.Remove(key);
        var holderView = Clone(holder);
        holderView.Set(FontDescriptor, bare);
        var view = kid is null ? holderView : Clone(current);
        if (kid is not null) view.Set(DescendantFonts, new PdfArray([holderView]));
        if (shown is not null && !SameShown(font, PdfFont.Uncached(view, doc), shown)) return false;
        if (holder.GetRaw(FontDescriptor) is PdfReference dr) result[dr.Id.Number] = bare;
        else if (kid is not null && kidRaw is PdfReference kr) result[kr.Id.Number] = holderView;
        else if (number is { } m) result[m] = view;
        else return false;
        return true;
    }

    /// <summary>
    /// A font anew from what the display draws for the codes it shows, as the PDF/A copy rebuilds one (KONZEPT.md, section 5.21): a simple font
    /// becomes a Type 1 font with a bare CFF program (FontFile3 /Type1C) of one glyph per shown code, named in /Differences, with the widths
    /// the display sets; a composite font keeps its encoding and CIDs, its descendant becomes a CIDFontType0 with a CID-keyed CFF program
    /// (/CIDFontType0C) of the shown CIDs (without CIDToGIDMap). The text the display reads goes into a new /ToUnicode, as the glyph names
    /// no longer tell it. Taken only where the display draws every shown code of the new font as before (<see cref="SameDrawn"/>).
    /// </summary>
    /// <returns>The new font dictionary; null if it cannot be built or would be drawn differently.</returns>
    private static PdfDictionary? Rebuilt(PdfDocument doc, PdfDictionary current, PdfFont font, HashSet<(uint Code, int Length)> shown, Dictionary<int, PdfObject> result)
    {
        try
        {
            var rebuilt = font switch
            {
                SimpleFont simple => RebuiltSimple(current, simple, shown),
                CompositeFont composite => RebuiltComposite(doc, current, composite, shown, result),
                _ => null,
            };
            return rebuilt is not null && SameDrawn(font, PdfFont.Uncached(rebuilt, doc), shown) ? rebuilt : null;
        }
        catch (Exception e) when (e is Core.Fonts.FontFormatException or PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static PdfDictionary? RebuiltSimple(PdfDictionary current, SimpleFont font, HashSet<(uint Code, int Length)> shown)
    {
        var matrix = font.ProgramMatrix;
        if (matrix.A == 0 || !matrix.IsInvertible || current.GetDictionary(FontDescriptor) is not { } descriptor) return null;
        var inverse = matrix.Invert();
        var glyphs = new List<(string Name, Core.Fonts.GlyphOutline Outline, double Advance)>();
        var differences = new PdfArray();
        var texts = new List<(uint Code, int Bytes, string Text)>();
        var widths = new SortedDictionary<int, double>();
        foreach (var code in shown.Select(c => (int)(c.Code & 0xFF)).Distinct().Order())
        {
            var glyph = font.Glyph((uint)code, 1);
            var name = $"g{code}";
            glyphs.Add((name, Baked(glyph.Outline, glyph.ToText * inverse), glyph.Width / matrix.A));
            differences.Add(PdfInteger.Of(code));
            differences.Add(PdfName.Get(name));
            widths[code] = glyph.Width * 1000;
            if (glyph.Text is { Length: > 0 } text) texts.Add(((uint)code, 1, text));
        }
        if (glyphs.Count == 0) return null;
        var program = Core.Fonts.CffBuilder.Build(current.GetName(BaseFont)?.Value ?? "Font", matrix, glyphs);
        var rebuilt = Clone(current);
        rebuilt.Set(Subtype, PdfName.Get("Type1"));
        var encoding = new PdfDictionary();
        encoding.Set(PdfName.Type, Encoding);
        encoding.Set(Differences, differences);
        rebuilt.Set(Encoding, encoding);
        // the widths the display sets for the shown codes, 0 for the others
        var (first, last) = (widths.Keys.First(), widths.Keys.Last());
        var array = new PdfArray();
        for (var code = first; code <= last; code++) array.Add(widths.TryGetValue(code, out var w) ? new PdfReal(Math.Round(w, 9)) : PdfInteger.Of(0));
        rebuilt.Set(FirstChar, PdfInteger.Of(first));
        rebuilt.Set(LastChar, PdfInteger.Of(last));
        rebuilt.Set(Widths, array);
        var flags = (int)(descriptor.GetInteger(Flags) ?? 0);
        rebuilt.Set(FontDescriptor, NewDescriptor(descriptor, program, "Type1C", (flags | 4) & ~32));
        SetToUnicode(rebuilt, texts);
        return rebuilt;
    }

    private static PdfDictionary? RebuiltComposite(PdfDocument doc, PdfDictionary current, CompositeFont font, HashSet<(uint Code, int Length)> shown,
        Dictionary<int, PdfObject> result)
    {
        var matrix = font.ProgramMatrix;
        if (matrix.A == 0 || !matrix.IsInvertible || current.GetArray(DescendantFonts) is not { Count: >= 1 } kids
            || Current(doc, kids[0], result) is not PdfDictionary kid || kid.GetDictionary(FontDescriptor) is not { } descriptor)
            return null;
        var inverse = matrix.Invert();
        var byCid = new SortedDictionary<int, (uint Code, int Length)>();
        var texts = new List<(uint Code, int Bytes, string Text)>();
        foreach (var (code, length) in shown.OrderBy(c => c.Length).ThenBy(c => c.Code))
        {
            if (font.Cid(code, length) is not (>= 0 and var cid)) continue;
            if (cid > ushort.MaxValue) return null;   // a CFF names CIDs up to 65 535
            byCid.TryAdd(cid, (code, length));
            if (font.Glyph(code, length).Text is { Length: > 0 } text) texts.Add((code, length, text));
        }
        if (byCid.Count == 0) return null;
        var glyphs = byCid.Select(e =>
        {
            var glyph = font.Glyph(e.Value.Code, e.Value.Length);
            return (e.Key, Baked(glyph.Outline, glyph.ToText * inverse), glyph.Width / matrix.A);
        }).ToList();
        var info = kid.GetDictionary(PdfName.Get("CIDSystemInfo"));
        var program = Core.Fonts.CffBuilder.BuildCid(kid.GetName(BaseFont)?.Value ?? current.GetName(BaseFont)?.Value ?? "Font", matrix,
            info?.GetString(PdfName.Get("Registry"))?.ToText() ?? "Adobe", info?.GetString(PdfName.Get("Ordering"))?.ToText() ?? "Identity",
            (int)Math.Clamp(info?.GetInteger(PdfName.Get("Supplement")) ?? 0, 0, ushort.MaxValue), glyphs);
        var newKid = Clone(kid);
        newKid.Set(Subtype, PdfName.Get("CIDFontType0"));
        newKid.Remove(CidToGidMap);
        newKid.Set(FontDescriptor, NewDescriptor(descriptor, program, "CIDFontType0C", (int)(descriptor.GetInteger(Flags) ?? 4)));
        var rebuilt = Clone(current);
        rebuilt.Set(DescendantFonts, new PdfArray([newKid]));
        SetToUnicode(rebuilt, texts);
        return rebuilt;
    }

    /// <summary>A copy of the descriptor with the new program as FontFile3 of the given kind and nothing that described the old one.</summary>
    private static PdfDictionary NewDescriptor(PdfDictionary descriptor, byte[] program, string kind, int flags)
    {
        var copy = Clone(descriptor);
        foreach (var key in (ReadOnlySpan<PdfName>)[FontFile, FontFile2, FontFile3, CidSet, CharSet]) copy.Remove(key);
        copy.Set(Flags, PdfInteger.Of(flags));
        var file = new PdfDictionary();
        file.Set(Subtype, PdfName.Get(kind));
        copy.Set(FontFile3, new PdfStream(file, program));
        return copy;
    }

    /// <summary>A /ToUnicode with the text the display reads for each shown code; none where there is no text.</summary>
    private static void SetToUnicode(PdfDictionary font, List<(uint Code, int Bytes, string Text)> texts)
    {
        if (texts.Count > 0) font.Set(ToUnicode, PdfACMaps.ToUnicode(texts));
        else font.Remove(ToUnicode);
    }

    /// <summary>An outline through a matrix (into the glyph space of the new program); the outline itself where it is the identity.</summary>
    private static Core.Fonts.GlyphOutline Baked(Core.Fonts.GlyphOutline outline, Graphics.Matrix m)
    {
        if (Math.Abs(m.A - 1) + Math.Abs(m.B) + Math.Abs(m.C) + Math.Abs(m.D - 1) + Math.Abs(m.E) + Math.Abs(m.F) < 1e-12 || outline.IsEmpty) return outline;
        var result = new Core.Fonts.GlyphOutline();
        result.Append(outline, m);
        return result.Finish();
    }

    /// <summary>
    /// Whether every code in <paramref name="shown"/> is drawn the same by both fonts: width, vertical metrics, text and CID equal, the outline
    /// the same in text space — within 10⁻⁵ em, as a rebuilt program holds coordinates in 16.16 fixed point, and without regard to where a
    /// subpath is closed (filling closes it either way).
    /// </summary>
    internal static bool SameDrawn(PdfFont before, PdfFont after, IEnumerable<(uint Code, int Length)> shown)
    {
        const double Tolerance = 1e-5;
        foreach (var (code, length) in shown)
        {
            PdfGlyph a = before.Glyph(code, length), b = after.Glyph(code, length);
            if (Math.Abs(a.Width - b.Width) > 1e-6 || a.Text != b.Text || a.Procedure is not null || b.Procedure is not null
                || Math.Abs(a.VerticalAdvance - b.VerticalAdvance) > 1e-6 || Math.Abs(a.OriginX - b.OriginX) > 1e-6 || Math.Abs(a.OriginY - b.OriginY) > 1e-6
                || before.Cid(code, length) != after.Cid(code, length))
                return false;
            List<(Graphics.PathVerb Verb, double X, double Y)> sa = Segments(a.Outline, a.ToText), sb = Segments(b.Outline, b.ToText);
            if (sa.Count != sb.Count) return false;
            for (var i = 0; i < sa.Count; i++)
                if (sa[i].Verb != sb[i].Verb || Math.Abs(sa[i].X - sb[i].X) > Tolerance || Math.Abs(sa[i].Y - sb[i].Y) > Tolerance) return false;
        }
        return true;
    }

    /// <summary>The points of an outline in text space, each with the verb it belongs to (closing verbs left out).</summary>
    private static List<(Graphics.PathVerb Verb, double X, double Y)> Segments(Core.Fonts.GlyphOutline outline, Graphics.Matrix m)
    {
        var result = new List<(Graphics.PathVerb, double, double)>();
        var points = outline.Points;
        var p = 0;
        foreach (var verb in outline.Verbs)
        {
            var count = verb switch { Graphics.PathVerb.Move or Graphics.PathVerb.Line => 1, Graphics.PathVerb.Cubic => 3, _ => 0 };
            for (var k = 0; k < count; k++, p += 2)
            {
                var (x, y) = (points[p], points[p + 1]);
                result.Add((verb, m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F));
            }
        }
        return result;
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
