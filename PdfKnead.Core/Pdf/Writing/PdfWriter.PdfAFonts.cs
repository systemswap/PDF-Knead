using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    /// <summary>
    /// The text of a code in a /ToUnicode of a PDF/A copy: what the display reads, without the values PDF/A forbids there (U+0000, U+FEFF,
    /// U+FFFE; ISO 19005-2, 6.2.11.7.2, ISO 19005-4, 6.2.10.7). Where nothing is left, level u — which asks for a value for every code — gets a
    /// value of Unicode's private use area (only that area claims no meaning; searching does not find such characters, copied they come out as
    /// placeholders), the other levels none.
    /// </summary>
    /// <param name="text">What the display reads for the code.</param>
    /// <param name="code">The code.</param>
    /// <param name="length">Its length in bytes: one byte from U+E000, longer codes from U+F0000 (plane 15, without its two non-characters).</param>
    /// <param name="unicode">Level u.</param>
    /// <param name="accessible">Level a: a character without text is a space where its glyph draws nothing, otherwise U+FFFD (the replacement
    /// character), as is one in a private use area of Unicode — such an area would need a replacement text around every such character
    /// (6.2.11.7.3).</param>
    /// <param name="blank">The glyph of the code draws nothing.</param>
    /// <returns>The text; null for none.</returns>
    internal static string? PdfAText(string? text, uint code, int length, bool unicode, bool accessible = false, bool blank = false)
    {
        var clean = (text ?? "").Replace("\0", "", StringComparison.Ordinal).Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace("\uFFFE", "", StringComparison.Ordinal);
        if (accessible)
        {
            if (clean.Length == 0) return blank ? " " : "\uFFFD";
            if (PdfACheck.HasPrivateUse(clean))
                clean = string.Concat(clean.EnumerateRunes().Select(r => r.Value is >= 0xE000 and <= 0xF8FF or >= 0xF0000 ? "\uFFFD" : r.ToString()));
        }
        return clean.Length > 0 ? clean
            : !unicode ? null
            : length == 1 ? ((char)(0xE000 + (code & 0xFF))).ToString()
            : char.ConvertFromUtf32(0xF0000 + (int)(code % 0xFFFE));
    }

    private sealed partial class Session
    {
        /// <summary>
        /// Fonts rebuilt for PDF/A, by the dictionary as read (a simple font, or the CIDFont of a composite one): what the copy writes instead.
        /// </summary>
        private readonly Dictionary<PdfDictionary, PdfDictionary> _rebuiltFonts = new(ReferenceEqualityComparer.Instance);

        /// <summary>Fonts embedded as the substitute the display draws them with (<see cref="Substitute"/>), by the dictionary as read.</summary>
        private readonly Dictionary<PdfDictionary, PdfDictionary> _substitutes = new(ReferenceEqualityComparer.Instance);

        /// <summary>The names of the fonts embedded as their substitute (<see cref="PdfSaveResult.SubstitutedFonts"/>).</summary>
        private readonly SortedSet<string> _substitutedNames = new(StringComparer.Ordinal);

        /// <summary>
        /// Every font the pages show is written anew from what the display draws (KONZEPT.md, section 5.21), so that the copy meets the font rules
        /// of PDF/A (6.2.11) by construction and looks the same: a simple TrueType font keeps its glyph data byte for byte and gets the widths of
        /// the file and a cmap of its own (<see cref="RebuildTrueType"/>); any other simple font with a program the display reads gets a CFF
        /// program from the outlines (<see cref="RebuildFromOutlines"/>); a composite font gets a new program, CIDs PDF/A allows and an encoding
        /// CMap of its own where it needs one (<see cref="RebuildComposite"/>); a Type 3 font gets procedures where its glyphs break the rules
        /// (<see cref="CompleteType3"/>). Every shown font gets a /ToUnicode with what the display reads (<see cref="WriteToUnicode"/>). What
        /// cannot be rebuilt stays as it is, and the check names it.
        /// </summary>
        private void RebuildFonts()
        {
            foreach (var (dict, (doc, codes)) in _pdfA!.Usage.Shown)
            {
                _cancel.ThrowIfCancellationRequested();
                var source = _sourceOrder.FirstOrDefault(s => ReferenceEquals(s.Doc, doc));
                if (source is null) continue;
                try
                {
                    // a font without a program the display reads: embedded as its substitute (named in the result), otherwise rebuilt — a
                    // standard font the bundled one matches is embedded as such when it is written
                    var standard = dict.GetName(PdfName.Subtype)?.Value is "Type1" or "MMType1" or "TrueType" && StandardEmbedding(dict, source, dict) is not null;
                    if (!standard && Substitute(dict, doc, source, codes)) _substitutedNames.Add(dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "?");
                    else Rebuild(dict, doc, source, codes);
                    WriteToUnicode(dict, doc, codes);
                }
                catch (Exception e) when (e is FontFormatException or PdfException or ArgumentException or IndexOutOfRangeException)
                {
                    // stays as it is; the check names it
                    if (PdfACheck.Debug) Console.Error.WriteLine($"PDF/A: {dict.GetName(PdfName.Get("BaseFont"))?.Value} nicht neu aufgebaut: {e.Message}");
                }
            }
        }

        /// <summary>One font rebuilt by its kind as the display reads it (see <see cref="RebuildFonts"/>).</summary>
        private void Rebuild(PdfDictionary dict, PdfDocument doc, Source source, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            switch (PdfFont.Get(dict, doc))
            {
                case SimpleFont simple when simple.HasEmbeddedProgram && simple.DrawnTrueType() is not null:
                    RebuildTrueType(dict, doc, source, codes);
                    break;
                case SimpleFont { HasEmbeddedProgram: true }:
                    RebuildFromOutlines(dict, doc, source, codes);
                    break;
                case CompositeFont { HasEmbeddedProgram: true }:
                    RebuildComposite(dict, doc, source, codes, substitute: false);
                    break;
                case Type3Font:
                    CompleteType3(dict, doc, codes);
                    break;
            }
        }

        /// <summary>
        /// A font without a program the display reads (not embedded, or not readable), embedded as the substitute the display draws it with —
        /// the outlines exactly as drawn (the substitute stretched to the file's widths where the display does so) as CFF, a simple font as
        /// Type 1 with /Differences, a composite one as CIDFontType0 (<see cref="RebuildComposite"/>). Standard fonts are left to
        /// <see cref="EmbeddedStandardFont"/> (it is tried first when writing). Type 3 fonts have no program.
        /// </summary>
        /// <returns>Whether a substitute was made.</returns>
        private bool Substitute(PdfDictionary dict, PdfDocument doc, Source source, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            var font = PdfFont.Get(dict, doc);
            var name = new string((dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "Ersatz").Select(c => c is > ' ' and < (char)127 and not ('/' or '[' or ']' or '(' or ')' or '<' or '>' or '{' or '}' or '%' or '#') ? c : '_').ToArray());
            switch (font)
            {
                case SimpleFont { HasEmbeddedProgram: false } simple when codes.Count > 0:
                {
                    var matrix = simple.ProgramMatrix;
                    if (matrix.A == 0 || !matrix.IsInvertible) return false;
                    var inverse = matrix.Invert();
                    var glyphs = new List<(string Name, GlyphOutline Outline, double Advance)>();
                    var differences = new PdfArray();
                    foreach (var code in codes.Select(c => (int)(c.Code & 0xFF)).Distinct().Order())
                    {
                        var glyph = simple.Glyph((uint)code, 1);
                        glyphs.Add(($"g{code}", Baked(glyph.Outline, glyph.ToText * inverse), glyph.Width / matrix.A));
                        differences.Add(PdfInteger.Of(code));
                        differences.Add(PdfName.Get($"g{code}"));
                    }
                    var program = CffBuilder.Build(name, matrix, glyphs);
                    var result = new PdfDictionary();
                    result.Set(PdfName.Type, PdfName.Get("Font"));
                    result.Set(PdfName.Subtype, PdfName.Get("Type1"));
                    result.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
                    var encoding = new PdfDictionary();
                    encoding.Set(PdfName.Type, PdfName.Get("Encoding"));
                    encoding.Set(PdfName.Get("Differences"), differences);
                    result.Set(PdfName.Get("Encoding"), encoding);
                    var widths = new PdfArray();
                    for (var c = 0; c < 256; c++) widths.Add(Exact(simple.Glyph((uint)c, 1).Width * 1000));
                    result.Set(PdfName.Get("FirstChar"), PdfInteger.Of(0));
                    result.Set(PdfName.Get("LastChar"), PdfInteger.Of(255));
                    result.Set(PdfName.Get("Widths"), widths);
                    result.Set(PdfName.Get("FontDescriptor"), SubstituteDescriptor(name, glyphs.Select(g => g.Outline), program, "Type1C",
                        dict.GetDictionary(PdfName.Get("FontDescriptor")), CharSet(glyphs.Select(g => g.Name))));
                    _substitutes[dict] = result;
                    return true;
                }
                case CompositeFont { HasEmbeddedProgram: false } when codes.Count > 0:
                    return RebuildComposite(dict, doc, source, codes, substitute: true);
                default:
                    return false;
            }
        }

        /// <summary>An outline through a matrix (the stretch of a substitute, as the display draws it); the outline itself where it is the identity.</summary>
        private static GlyphOutline Baked(GlyphOutline outline, Graphics.Matrix m)
        {
            if (Math.Abs(m.A - 1) + Math.Abs(m.B) + Math.Abs(m.C) + Math.Abs(m.D - 1) + Math.Abs(m.E) + Math.Abs(m.F) < 1e-12 || outline.IsEmpty) return outline;
            var result = new GlyphOutline();
            result.Append(outline, m);
            return result.Finish();
        }

        /// <summary>
        /// A font descriptor for a substitute: name, symbolic, the CFF program, the box of its outlines; the metrics of the file's descriptor where
        /// it has them (the display measures lines of text with its ascent and descent), otherwise from the outlines.
        /// </summary>
        private OutputReference SubstituteDescriptor(string name, IEnumerable<GlyphOutline> outlines, byte[] program, string fontFile3, PdfDictionary? original,
            PdfObject? glyphSet = null)
        {
            double left = 0, bottom = 0, right = 0, top = 0;
            foreach (var outline in outlines.Where(o => !o.IsEmpty))
            {
                var b = outline.Bounds;
                (left, bottom, right, top) = (Math.Min(left, b.Left), Math.Min(bottom, Math.Min(b.Top, b.Bottom)), Math.Max(right, b.Right), Math.Max(top, Math.Max(b.Top, b.Bottom)));
            }
            var d = new PdfDictionary();
            d.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
            d.Set(PdfName.Get("FontName"), PdfName.Get(name));
            d.Set(PdfName.Get("Flags"), PdfInteger.Of(4));
            d.Set(PdfName.Get("FontBBox"), new PdfArray([PdfInteger.Of((long)Math.Floor(left)), PdfInteger.Of((long)Math.Floor(bottom)),
                PdfInteger.Of((long)Math.Ceiling(right)), PdfInteger.Of((long)Math.Ceiling(top))]));
            d.Set(PdfName.Get("ItalicAngle"), PdfInteger.Of(0));
            d.Set(PdfName.Get("Ascent"), PdfInteger.Of((long)Math.Ceiling(top)));
            d.Set(PdfName.Get("Descent"), PdfInteger.Of((long)Math.Floor(bottom)));
            d.Set(PdfName.Get("CapHeight"), PdfInteger.Of((long)Math.Ceiling(top * 0.7)));
            d.Set(PdfName.Get("StemV"), PdfInteger.Of(80));
            foreach (var key in (string[])["Ascent", "Descent", "CapHeight", "XHeight", "ItalicAngle", "StemV", "StemH", "AvgWidth", "MaxWidth", "MissingWidth", "Leading", "FontWeight"])
                if (original?.Get(PdfName.Get(key)) is PdfInteger or PdfReal && original.Get(PdfName.Get(key)) is var value) d.Set(PdfName.Get(key), value);
            var file = new PdfDictionary();
            file.Set(PdfName.Subtype, PdfName.Get(fontFile3));
            d.Set(PdfName.Get("FontFile3"), Fresh(new PdfStream(file, program)));
            SetGlyphSet(d, glyphSet);
            var n = NewNumber();
            _memoryQueue.Enqueue((n, d));
            return new OutputReference(n);
        }

        /// <summary>
        /// The /ToUnicode of a shown font, anew for the codes shown, also in invisible text (<see cref="PdfACMaps.ToUnicode"/>): what the display
        /// reads for each, as <see cref="PdfAText"/> lets it stand — so level u maps every character (6.2.11.7.2) and no level has a value PDF/A
        /// forbids. A font without any such text gets none.
        /// </summary>
        private void WriteToUnicode(PdfDictionary dict, PdfDocument doc, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            var font = PdfFont.Get(dict, doc);
            var unicode = _pdfA!.Part.NeedsUnicode();
            var entries = new List<(uint Code, int Bytes, string Text)>();
            foreach (var (code, length) in codes.Select(c => (c.Code, c.Length)).Distinct())
            {
                var glyph = font.Glyph(code, length);
                if (PdfAText(glyph.Text, code, length, unicode, _pdfA.Part.Accessible(), glyph.Outline.IsEmpty && glyph.Procedure is null) is { } text)
                    entries.Add((code, length, text));
            }
            var target = _substitutes.TryGetValue(dict, out var substitute) ? substitute : Rebuilt(dict);
            if (entries.Count > 0) target.Set(PdfName.Get("ToUnicode"), Fresh(PdfACMaps.ToUnicode(entries)));
            else target.Remove(PdfName.Get("ToUnicode"));
        }

        /// <summary>The replacement of a font dictionary being rebuilt: the one made so far, or a copy (several steps may change one font).</summary>
        private PdfDictionary Rebuilt(PdfDictionary dict)
        {
            if (!_rebuiltFonts.TryGetValue(dict, out var result)) _rebuiltFonts[dict] = result = CloneWithResolver(dict);
            return result;
        }

        /// <summary>The uncompressed sfnt data of a font's embedded program (of a collection its first font, the one the display reads); null if it has none.</summary>
        private static (PdfDictionary Descriptor, byte[] Data)? SfntProgram(PdfDictionary font, PdfDocument doc)
        {
            if (font.GetDictionary(PdfName.Get("FontDescriptor")) is not { } descriptor) return null;
            var stream = descriptor.GetStream(PdfName.Get("FontFile2")) ?? descriptor.GetStream(PdfName.Get("FontFile3")) ?? descriptor.GetStream(PdfName.Get("FontFile"));
            if (stream is null) return null;
            var data = doc.DecodeStream(stream).Data;
            return (descriptor, data.AsSpan().StartsWith("ttcf"u8) ? TrueTypeSubsetter.FirstOfCollection(data) : data);
        }

        /// <summary>
        /// The glyphs of a rebuilt TrueType program: per code (or CID) the glyph the display draws, kept at its index with the width the file
        /// gives — a copy at the end where the glyph already has another width, where the display draws glyph 0 (.notdef must not be shown) or
        /// draws nothing (an empty glyph).
        /// </summary>
        private sealed class GlyphPlan(int unitsPerEm)
        {
            public Dictionary<int, int> Advances { get; } = [];
            public List<(int Glyph, int Advance)> Appended { get; } = [];
            private readonly Dictionary<(int Glyph, int Advance), int> _copies = [];
            private int _count;

            public void SetCount(int count) => _count = count;

            /// <summary>Glyph 0 at its index with the width given (1/1000 em).</summary>
            public int Notdef(double width)
            {
                Advances[0] = (int)Math.Round(width * unitsPerEm / 1000);
                return 0;
            }

            /// <summary>The glyph index in the new program for a glyph drawn (-1: none) with a width in 1/1000 em.</summary>
            public int Place(int drawn, double width)
            {
                var advance = (int)Math.Round(width * unitsPerEm / 1000);
                if (drawn > 0 && drawn < _count && (!Advances.TryGetValue(drawn, out var given) || given == advance))
                {
                    Advances[drawn] = advance;
                    return drawn;
                }
                var key = (drawn is > 0 && drawn < _count ? drawn : drawn == 0 ? 0 : -1, advance);
                if (_copies.TryGetValue(key, out var copy)) return copy;
                Appended.Add(key);
                return _copies[key] = _count + Appended.Count - 1;
            }
        }

        /// <summary>
        /// A simple TrueType font anew: symbolic, without /Encoding, a (3,0) cmap F0xx → the glyph drawn for each shown code, the widths of
        /// the file in hmtx (the text, which the encoding no longer says: <see cref="WriteToUnicode"/>).
        /// </summary>
        private void RebuildTrueType(PdfDictionary dict, PdfDocument doc, Source source, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            if (PdfFont.Get(dict, doc) is not SimpleFont font || font.DrawnTrueType() is not var (program, drawn) || SfntProgram(dict, doc) is not var (descriptor, data))
                return;
            var plan = new GlyphPlan(program.UnitsPerEm);
            plan.SetCount(program.GlyphCount);
            var cmap = new List<(int Code, int Glyph)>();
            foreach (var code in codes.Select(c => (int)(c.Code & 0xFF)).Distinct().Order())
                cmap.Add((0xF000 + code, plan.Place(drawn(code), font.Glyph((uint)code, 1).Width * 1000)));
            var rebuilt = TrueTypeSubsetter.Rebuild(data, plan.Advances, plan.Appended, cmap);
            var flags = (int)(descriptor.GetInteger(PdfName.Get("Flags")) ?? 0);
            var newDescriptor = NewDescriptor(descriptor, source, rebuilt, (flags | 4) & ~32);
            var result = CloneWithResolver(dict);
            result.Set(PdfName.Subtype, PdfName.Get("TrueType"));
            result.Remove(PdfName.Get("Encoding"));
            result.Set(PdfName.Get("FontDescriptor"), newDescriptor);
            CompleteWidths(result, font, codes);
            _rebuiltFonts[dict] = result;
        }

        /// <summary>
        /// A composite font anew from what the display draws (6.2.11): per shown code the CID the display read — CID 0 (.notdef, 6.2.11.8) and
        /// CIDs beyond 65 535 (6.1.13) get free ones —, its glyph in a new program (the TrueType glyph data byte for byte where the display reads
        /// a TrueType program under CIDFontType2, otherwise a CID-keyed CFF program from the outlines, also for a substitute), widths and vertical
        /// metrics as the display sets them. The encoding stays where it is one of ISO 32000-1, Table 118 and the CIDs stay; otherwise — an
        /// embedded CMap, an unknown name, changed CIDs — the font gets an encoding CMap of its own for the codes shown, in the writing mode the
        /// display uses (<see cref="PdfACMaps.Encoding"/>; 6.2.11.3.3). CMap and CIDFont name the same character collection (6.2.11.3.1).
        /// </summary>
        /// <returns>Whether the font was rebuilt.</returns>
        private bool RebuildComposite(PdfDictionary dict, PdfDocument doc, Source source, HashSet<(uint Code, int Length, bool Invisible)> codes, bool substitute)
        {
            if (PdfFont.Get(dict, doc) is not CompositeFont font || dict.Get(PdfName.Get("DescendantFonts")) is not PdfArray { Count: > 0 } descendants ||
                descendants.Get(0) is not PdfDictionary cidFont)
                return false;
            var shown = codes.Select(c => (c.Code, c.Length)).Distinct().OrderBy(c => c.Length).ThenBy(c => c.Code).ToList();
            if (shown.Count == 0) return false;

            // the CIDs of the copy, and per CID the code whose glyph, width and text it gets (a CID has one glyph)
            var read = shown.ToDictionary(c => c, c => font.Cid(c.Code, c.Length));
            var next = read.Values.Where(c => c is > 0 and <= 65535).DefaultIfEmpty(0).Max() + 1;
            var renumbered = new Dictionary<int, int>();
            var cids = new Dictionary<(uint Code, int Length), int>();
            foreach (var c in shown)
            {
                var cid = read[c];
                if (cid is > 0 and <= 65535) cids[c] = cid;
                else
                {
                    if (!renumbered.TryGetValue(cid, out var free)) renumbered[cid] = free = next++;
                    cids[c] = free;
                }
            }
            if (next > 65536) return false;
            var byCid = new SortedDictionary<int, (uint Code, int Length)>();
            foreach (var (c, cid) in cids) byCid.TryAdd(cid, c);

            // the encoding and the character collection
            var encoding = dict.Get(PdfName.Get("Encoding"));
            // PDF/A-1 (6.3.3.3): of the predefined CMaps only the identities stay unembedded
            var ownCMap = renumbered.Count > 0 || encoding is not PdfName { Value: var encodingName } || !PdfACheck.Table118.Contains(encodingName) ||
                          _pdfA!.Part.Part() == 1 && encodingName is not ("Identity-H" or "Identity-V");
            var info = cidFont.GetDictionary(PdfName.Get("CIDSystemInfo"));
            var registry = info?.GetString(PdfName.Get("Registry"))?.ToText() ?? "Adobe";
            var ordering = info?.GetString(PdfName.Get("Ordering"))?.ToText() ?? "Identity";
            var supplement = info?.GetInteger(PdfName.Get("Supplement")) ?? 0;
            if (registry.Length == 0 || ordering.Length == 0 || !registry.All(char.IsAsciiLetterOrDigit) || !ordering.All(char.IsAsciiLetterOrDigit) ||
                supplement is < 0 or > ushort.MaxValue)
                (registry, ordering, supplement) = ("Adobe", "Identity", 0);
            if (!ownCMap && encoding is PdfName predefined && PredefinedCMaps.Collection(predefined.Value) is { } collection)
            {
                var highest = PredefinedCMaps.Supplement(predefined.Value) ?? 99;
                if (registry != "Adobe" || ordering != collection || supplement > highest) (registry, ordering, supplement) = ("Adobe", collection, Math.Min(supplement, highest));
            }

            var name = new string((cidFont.GetName(PdfName.Get("BaseFont"))?.Value ?? dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "Ersatz")
                .Select(c => c is > ' ' and < (char)127 and not ('/' or '[' or ']' or '(' or ')' or '<' or '>' or '{' or '}' or '%' or '#') ? c : '_').ToArray());
            var newCid = new PdfDictionary { Resolver = cidFont.Resolver };
            newCid.Set(PdfName.Type, PdfName.Get("Font"));
            newCid.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
            var collectionDict = new PdfDictionary();
            collectionDict.Set(PdfName.Get("Registry"), new PdfString(System.Text.Encoding.ASCII.GetBytes(registry)));
            collectionDict.Set(PdfName.Get("Ordering"), new PdfString(System.Text.Encoding.ASCII.GetBytes(ordering)));
            collectionDict.Set(PdfName.Get("Supplement"), PdfInteger.Of(supplement));
            newCid.Set(PdfName.Get("CIDSystemInfo"), collectionDict);
            var widths = new List<(int Cid, double Width)>();
            var vertical = new List<(int Cid, double W1, double Vx, double Vy)>();
            foreach (var (cid, (code, length)) in byCid)
            {
                var glyph = font.Glyph(code, length);
                widths.Add((cid, glyph.Width * 1000));
                if (font.Vertical) vertical.Add((cid, glyph.VerticalAdvance * 1000, glyph.OriginX * 1000, glyph.OriginY * 1000));
            }
            newCid.Set(PdfName.Get("W"), WidthArray(widths.Select(w => (w.Cid, (PdfObject[])[Exact(w.Width)]))));
            if (font.Vertical) newCid.Set(PdfName.Get("W2"), WidthArray(vertical.Select(v => (v.Cid, (PdfObject[])[Exact(v.W1), Exact(v.Vx), Exact(v.Vy)]))));

            var descriptor = cidFont.GetDictionary(PdfName.Get("FontDescriptor"));
            if (!substitute && font.DrawnTrueType() is var (program, drawn) && SfntProgram(cidFont, doc) is var (_, data))
            {
                var plan = new GlyphPlan(program.UnitsPerEm);
                plan.SetCount(program.GlyphCount);
                var map = new Dictionary<int, int>();
                foreach (var (cid, (code, length)) in byCid)
                {
                    var gid = drawn(read[(code, length)]);
                    map[cid] = plan.Place(gid < program.GlyphCount ? gid : -1, font.Glyph(code, length).Width * 1000);
                }
                var rebuilt = TrueTypeSubsetter.Rebuild(data, plan.Advances, plan.Appended, null);
                var cidToGid = new byte[(map.Keys.Max() + 1) * 2];
                foreach (var (cid, gid) in map)
                {
                    cidToGid[2 * cid] = (byte)(gid >> 8);
                    cidToGid[2 * cid + 1] = (byte)gid;
                }
                newCid.Set(PdfName.Subtype, PdfName.Get("CIDFontType2"));
                newCid.Set(PdfName.Get("CIDToGIDMap"), Fresh(new PdfStream(new PdfDictionary(), cidToGid)));
                newCid.Set(PdfName.Get("FontDescriptor"), NewDescriptor(descriptor!, source, rebuilt, (int)(descriptor!.GetInteger(PdfName.Get("Flags")) ?? 4),
                    glyphSet: CidSet(map.Keys), fontName: name));
            }
            else
            {
                var matrix = font.ProgramMatrix;
                if (matrix.A == 0 || !matrix.IsInvertible) return false;
                var inverse = matrix.Invert();
                var glyphs = new List<(int Cid, GlyphOutline Outline, double Advance)>();
                foreach (var (cid, (code, length)) in byCid)
                {
                    var glyph = font.Glyph(code, length);
                    glyphs.Add((cid, Baked(glyph.Outline, glyph.ToText * inverse), glyph.Width / matrix.A));
                }
                var cff = CffBuilder.BuildCid(name, matrix, registry, ordering, (int)supplement, glyphs);
                newCid.Set(PdfName.Subtype, PdfName.Get("CIDFontType0"));
                newCid.Set(PdfName.Get("FontDescriptor"), substitute || descriptor is null
                    ? SubstituteDescriptor(name, glyphs.Select(g => g.Outline), cff, "CIDFontType0C", descriptor, CidSet(glyphs.Select(g => g.Cid)))
                    : NewDescriptor(descriptor, source, cff, (int)(descriptor.GetInteger(PdfName.Get("Flags")) ?? 4), "CIDFontType0C", CidSet(glyphs.Select(g => g.Cid)), name));
            }

            var result = Rebuilt(dict);
            result.Set(PdfName.Get("DescendantFonts"), new PdfArray([newCid]));
            if (substitute) result.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
            if (ownCMap)
            {
                var entries = cids.Select(e => (e.Key.Code, e.Key.Length, e.Value)).ToList();
                var cmapName = "PDFKnead-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.ASCII.GetBytes(string.Join(';', entries.Select(e => $"{e.Code}:{e.Length}:{e.Value}")))))[..12];
                result.Set(PdfName.Get("Encoding"), Fresh(PdfACMaps.Encoding(cmapName, registry, ordering, supplement, font.Vertical ? 1 : 0, entries)));
            }
            return true;
        }

        /// <summary>A /W or /W2 array: runs of consecutive CIDs as „first [values …]“.</summary>
        private static PdfArray WidthArray(IEnumerable<(int Cid, PdfObject[] Values)> entries)
        {
            var array = new PdfArray();
            PdfArray? run = null;
            var last = -2;
            foreach (var (cid, values) in entries.OrderBy(e => e.Cid))
            {
                // a new run also where one would grow beyond the 8 191 entries PDF/A-1 allows in a list
                if (run is null || cid != last + 1 || run.Count + values.Length > 8191)
                {
                    array.Add(PdfInteger.Of(cid));
                    run = new PdfArray();
                    array.Add(run);
                }
                foreach (var value in values) run.Add(value);
                last = cid;
            }
            return array;
        }

        /// <summary>
        /// A width or metric in 1/1000 em as the display sets it: back from text space without the noise of the division (9 decimals) — rounding to
        /// fewer moves glyphs where the file gives more (widths such as 277.83203 add up along a line).
        /// </summary>
        internal static PdfReal Exact(double value) => new(Math.Round(value, 9));

        /// <summary>
        /// Widths for every code of a rebuilt simple font where the file gives none, too few, or none for a shown code (6.2.11.2, 6.2.11.5) —
        /// those the display sets the codes with (an embedded font takes the program's width where the file gives none).
        /// </summary>
        private static void CompleteWidths(PdfDictionary result, PdfFont font, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            if (ValidWidths(result) && result.GetInteger(PdfName.Get("FirstChar")) is { } first && result.GetInteger(PdfName.Get("LastChar")) is { } last &&
                codes.All(c => (c.Code & 0xFF) >= first && (c.Code & 0xFF) <= last))
                return;
            var widths = new PdfArray();
            for (var c = 0; c < 256; c++) widths.Add(Exact(font.Glyph((uint)c, 1).Width * 1000));
            result.Set(PdfName.Get("FirstChar"), PdfInteger.Of(0));
            result.Set(PdfName.Get("LastChar"), PdfInteger.Of(255));
            result.Set(PdfName.Get("Widths"), widths);
        }

        /// <summary>
        /// A copy of a font descriptor with a new program — TrueType as FontFile2, otherwise FontFile3 of the given kind — and nothing that
        /// describes the old one (character and CID sets); those of the new program where given (<see cref="CharSet"/>, <see cref="CidSet"/>).
        /// </summary>
        private OutputReference NewDescriptor(PdfDictionary descriptor, Source source, byte[] program, int flags, string? fontFile3 = null,
            PdfObject? glyphSet = null, string? fontName = null)
        {
            // the old program left out before the references are taken over: otherwise it would be written as well, unused
            var without = CloneWithResolver(descriptor);
            foreach (var key in (string[])["FontFile", "FontFile2", "FontFile3", "CIDSet", "CharSet"]) without.Remove(PdfName.Get(key));
            var copy = (PdfDictionary)Rebase(without, source);
            copy.Set(PdfName.Get("Flags"), PdfInteger.Of(flags));
            // the name of the rebuilt font, where it got one of its own: /FontName equals /BaseFont (ISO 32000-2, Table 120; No. 525)
            if (fontName is not null) copy.Set(PdfName.Get("FontName"), PdfName.Get(fontName));
            var file = new PdfDictionary();
            if (fontFile3 is null) file.Set(PdfName.Get("Length1"), PdfInteger.Of(program.Length));
            else file.Set(PdfName.Subtype, PdfName.Get(fontFile3));
            copy.Set(PdfName.Get(fontFile3 is null ? "FontFile2" : "FontFile3"), Fresh(new PdfStream(file, program)));
            SetGlyphSet(copy, glyphSet);
            // queued as it is (registered in _memoryNumbers, Fresh would turn it into a reference to itself when it is written)
            var n = NewNumber();
            _memoryQueue.Enqueue((n, copy));
            return new OutputReference(n);
        }

        /// <summary>
        /// The character set of a rebuilt Type 1 font: every glyph name of its program (PDF/A-1 asks every Type 1 subset for one, ISO 19005-1,
        /// 6.3.5; the other parts allow none and get none). Null for the other parts.
        /// </summary>
        private PdfString? CharSet(IEnumerable<string> names) =>
            _pdfA!.Part.Part() == 1 ? new PdfString(System.Text.Encoding.ASCII.GetBytes(string.Concat(names.Where(n => n != ".notdef").Select(n => "/" + n)))) : null;

        /// <summary>
        /// The CID set of a rebuilt CIDFont: a bit per CID its program has, CID 0 first, the most significant bit of each byte first (ISO 32000-1,
        /// 9.8.3.3; PDF/A-1 asks every CIDFont subset for one, 6.3.5). Null for the other parts.
        /// </summary>
        private PdfStream? CidSet(IEnumerable<int> cids)
        {
            if (_pdfA!.Part.Part() != 1) return null;
            var all = cids.Append(0).ToList();
            var bits = new byte[all.Max() / 8 + 1];
            foreach (var cid in all) bits[cid >> 3] |= (byte)(0x80 >> (cid & 7));
            return new PdfStream(new PdfDictionary(), bits);
        }

        private void SetGlyphSet(PdfDictionary descriptor, PdfObject? glyphSet)
        {
            switch (glyphSet)
            {
                case PdfString charSet:
                    descriptor.Set(PdfName.Get("CharSet"), charSet);
                    break;
                case PdfStream cidSet:
                    descriptor.Set(PdfName.Get("CIDSet"), Fresh(cidSet));
                    break;
            }
        }

        /// <summary>
        /// A Type 1 or CFF font (or a TrueType font with CFF outlines) anew, from the outlines the display draws: a bare CFF program (FontFile3
        /// /Type1C) with one glyph per shown code, named after it, of the width the file gives the code; /Differences that name them (the text:
        /// <see cref="WriteToUnicode"/>). Only fonts whose embedded program the display reads — a font drawn with a substitute would look
        /// different elsewhere.
        /// </summary>
        private void RebuildFromOutlines(PdfDictionary dict, PdfDocument doc, Source source, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            if (PdfFont.Get(dict, doc) is not SimpleFont { HasEmbeddedProgram: true } font || dict.GetDictionary(PdfName.Get("FontDescriptor")) is not { } descriptor)
                return;
            // the matrix goes into the new program as it is (the widths are measured along its first column)
            var matrix = font.ProgramMatrix;
            if (matrix.A == 0 || !matrix.IsInvertible) return;
            var glyphs = new List<(string Name, GlyphOutline Outline, double Advance)>();
            var differences = new PdfArray();
            foreach (var code in codes.Select(c => (int)(c.Code & 0xFF)).Distinct().Order())
            {
                var glyph = font.Glyph((uint)code, 1);
                var name = $"g{code}";
                glyphs.Add((name, glyph.Outline, glyph.Width / matrix.A));
                differences.Add(PdfInteger.Of(code));
                differences.Add(PdfName.Get(name));
            }
            if (glyphs.Count == 0) return;
            var program = CffBuilder.Build(dict.GetName(PdfName.Get("BaseFont"))?.Value ?? "Font", matrix, glyphs);
            var encoding = new PdfDictionary();
            encoding.Set(PdfName.Type, PdfName.Get("Encoding"));
            encoding.Set(PdfName.Get("Differences"), differences);
            var flags = (int)(descriptor.GetInteger(PdfName.Get("Flags")) ?? 0);
            var result = CloneWithResolver(dict);
            result.Set(PdfName.Subtype, PdfName.Get("Type1"));
            result.Set(PdfName.Get("Encoding"), encoding);
            result.Set(PdfName.Get("FontDescriptor"), NewDescriptor(descriptor, source, program, (flags | 4) & ~32, "Type1C", CharSet(glyphs.Select(g => g.Name))));
            CompleteWidths(result, font, codes);
            _rebuiltFonts[dict] = result;
        }

        /// <summary>
        /// A Type 3 font whose shown codes lack a glyph procedure, are named .notdef or set another width in their procedure than /Widths
        /// gives (6.2.11.4.1, 6.2.11.8, 6.2.11.5): each such code gets a name of its own — with an empty procedure of its width where the display
        /// draws nothing (also for a procedure it cannot read), otherwise the procedure it showed with the width of /Widths in its d0 or d1 (the
        /// display places glyphs by /Widths).
        /// </summary>
        private void CompleteType3(PdfDictionary dict, PdfDocument doc, HashSet<(uint Code, int Length, bool Invisible)> codes)
        {
            if (PdfFont.Get(dict, doc) is not Type3Font font) return;
            // without procedures (a damaged dictionary) the display draws nothing: every shown code gets an empty one
            var procs = dict.GetDictionary(PdfName.Get("CharProcs")) ?? new PdfDictionary();
            var faulty = codes.Select(c => (int)(c.Code & 0xFF)).Distinct()
                .Where(c => font.Strict((uint)c, 1) is { } g && (!g.Present || g.Notdef || g.ProgramWidth is not { } w || Math.Abs(w - g.DictionaryWidth) > 1))
                .Order().ToList();
            if (faulty.Count == 0) return;
            var newProcs = CloneWithResolver(procs);
            var differences = new PdfArray();
            var encoding = dict.Get(PdfName.Get("Encoding")) as PdfDictionary;
            if (encoding?.Get(PdfName.Get("Differences")) is PdfArray old)
                for (var i = 0; i < old.Count; i++) differences.Add(old[i]);
            foreach (var code in faulty)
            {
                var glyph = font.Strict((uint)code, 1)!.Value;
                var width = PdfSerializer.FormatReal(glyph.DictionaryWidth);
                var name = PdfName.Get($"PDFKneadGlyph{code}");
                differences.Add(PdfInteger.Of(code));
                differences.Add(name);
                var shown = font.ProcOf((uint)code);
                // a procedure the display cannot read (a filter no reader knows) draws nothing either
                byte[]? original = null;
                if (shown is not null)
                    try
                    {
                        original = doc.GetStreamData(shown);
                    }
                    catch (PdfException)
                    {
                    }
                byte[] data;
                if (original is null) data = System.Text.Encoding.ASCII.GetBytes(width + " 0 d0\n");
                else
                {
                    // the procedure with the width of /Widths in its first operator
                    var parser = new Content.ContentParser(original);
                    var op = parser.Next();
                    var wellFormed = op is not null && Type3Font.WellFormed(op, parser.Operands);
                    var operands = parser.Operands.Select(o => o.Number).ToList();
                    var rest = original.AsSpan(parser.Position).ToArray();
                    // a d0 or d1 with other operands than the standard asks for: in its place one of the same kind (the display paints a d1 glyph
                    // in the colour of the text whatever its operands), the box of a d1 that of the font
                    var head = op switch
                    {
                        "d0" when wellFormed => $"{width} {PdfSerializer.FormatReal(operands[1])} d0",
                        "d1" when wellFormed => $"{width} {string.Join(' ', operands.Skip(1).Select(PdfSerializer.FormatReal))} d1",
                        "d0" => $"{width} 0 d0",
                        "d1" => $"{width} 0 {FontBox(dict)} d1",
                        _ => null,
                    };
                    data = head is null ? [.. System.Text.Encoding.ASCII.GetBytes(width + " 0 d0\n"), .. original] : [.. System.Text.Encoding.ASCII.GetBytes(head + "\n"), .. rest];
                }
                // PDF/A-4: an inline image over 4 096 bytes becomes an image XObject in the font's resources, which the glyph draws with (8.9.7;
                // docs/FEHLERGEDAECHTNIS.md, No. 1131)
                data = ContentConformance.Rewrite(data, RewriteOptions(shown?.Dictionary.GetDictionary(PdfName.Resources) ?? dict.GetDictionary(PdfName.Resources), doc, null, null,
                    placed: _pdfA!.Part.Part() == 4 ? FontImages(dict) : null));
                var procDict = new PdfDictionary();
                if (shown?.Dictionary.GetRaw(PdfName.Resources) is { } procResources) procDict.Set(PdfName.Resources, procResources);
                newProcs.Set(name, Fresh(new PdfStream(procDict, data)));
            }
            var newEncoding = encoding is null ? new PdfDictionary() : CloneWithResolver(encoding);
            newEncoding.Set(PdfName.Get("Differences"), differences);
            var result = CloneWithResolver(dict);
            result.Set(PdfName.Get("CharProcs"), newProcs);
            result.Set(PdfName.Get("Encoding"), newEncoding);
            _rebuiltFonts[dict] = result;
        }

        /// <summary>The /FontBBox of a Type 3 font as the operands of a d1; four zeros where it has none.</summary>
        private static string FontBox(PdfDictionary dict) =>
            dict.GetArray(PdfName.Get("FontBBox")) is { Count: 4 } box && Enumerable.Range(0, 4).All(i => box.GetNumber(i) is not null)
                ? string.Join(' ', Enumerable.Range(0, 4).Select(i => PdfSerializer.FormatReal(box.GetNumber(i)!.Value)))
                : "0 0 0 0";
    }
}
