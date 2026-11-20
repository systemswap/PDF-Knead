using System.Xml;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfACheck
{
    /// <summary>
    /// The CIDs an embedded CIDFont program has (what its /CIDSet marks, 6.2.11.4.2): of a CID-keyed CFF program the CIDs of its charset, of
    /// another CFF program its glyph indices, of a TrueType program the CIDs its /CIDToGIDMap leads to a glyph with data (with /Identity or
    /// none: the glyph indices with data) — slots a subset left empty are no glyphs of the font. Null for another program.
    /// </summary>
    /// <param name="program">The embedded program.</param>
    /// <param name="map">The data of a /CIDToGIDMap stream; null for /Identity or none.</param>
    internal static IEnumerable<int>? PresentCids(FontProgram program, byte[]? map)
    {
        if (program.IsTrueType && program.OpenType is { } ot)
            return map is null
                ? Enumerable.Range(0, ot.GlyphCount).Where(ot.HasGlyphData)
                : Enumerable.Range(0, map.Length / 2).Where(c => (map[2 * c] << 8 | map[2 * c + 1]) is var g && (c == 0 || g > 0) && ot.HasGlyphData(g));
        return program.Cff is { } cff ? Enumerable.Range(0, cff.GlyphCount).Select(g => cff.IsCidKeyed ? cff.CidOf(g) : g) : null;
    }

    /// <summary>
    /// The CIDs an embedded CIDFont program has a glyph for, empty ones as well (what a /CIDSet may mark): of a TrueType program every CID its
    /// /CIDToGIDMap leads to a glyph index of the program (with /Identity or none: every glyph index), of a CFF program those of
    /// <see cref="PresentCids"/>. Null for another program.
    /// </summary>
    /// <param name="program">The embedded program.</param>
    /// <param name="map">The data of a /CIDToGIDMap stream; null for /Identity or none.</param>
    internal static IEnumerable<int>? ExistingCids(FontProgram program, byte[]? map)
    {
        if (program.IsTrueType && program.OpenType is { } ot)
            return map is null
                ? Enumerable.Range(0, ot.GlyphCount)
                : Enumerable.Range(0, map.Length / 2).Where(c => (map[2 * c] << 8 | map[2 * c + 1]) is var g && (c == 0 || g > 0) && g < ot.GlyphCount);
        return PresentCids(program, map);
    }

    /// <summary>The rules on fonts and the text shown with them.</summary>
    private sealed partial class Run
    {
        // ------------------------------------------------------------ 6.2.11 Fonts

        private static string FontName(PdfDictionary font) =>
            font.GetName(PdfName.Get("BaseFont"))?.Value ?? font.GetName(PdfName.Get("Name"))?.Value ?? "?";

        /// <summary>The rules on a font's dictionaries and the form of its program (6.2.11.2, 6.2.11.3, 6.2.11.4.1, 6.2.11.6).</summary>
        private void FontRules(PdfDictionary font)
        {
            var subtype = font.GetName(PdfName.Subtype)?.Value;
            var name = FontName(font);
            if (font.GetName(PdfName.Type)?.Value != "Font") Add(PdfARules.FontType, Tr.T("Die Schrift „{0}“ ist nicht als Schrift gekennzeichnet (/Type).", name));
            if (subtype is not ("Type0" or "Type1" or "MMType1" or "Type3" or "TrueType"))
                Add(PdfARules.FontSubtype, Tr.T("Die Schrift „{0}“ hat eine Art, die ISO 32000 nicht kennt (/Subtype {1}).", name, subtype ?? "–"));
            if (subtype == "Type3")
            {
                // a simple font without a program (ISO 32000-1, Table 112): its widths as for every other simple font (6.2.11.2)
                WidthsRules(font, name);
                return;
            }
            if (font.GetName(PdfName.Get("BaseFont")) is null) Add(PdfARules.FontBaseName, Tr.T("Bei einer Schrift fehlt der Name (/BaseFont)."));
            else if (!Utf8(font.GetName(PdfName.Get("BaseFont"))!.Value)) Add(PdfARules.NamesUtf8, Tr.T("Der Name der Schrift „{0}“ ist kein gültiges UTF-8.", name));
            if (subtype == "Type0")
            {
                CompositeRules(font, name);
                return;
            }
            var descriptor = font.GetDictionary(PdfName.Get("FontDescriptor"));
            var program = descriptor is null ? null : ProgramStream(descriptor);
            if (program is not null || !StandardFonts.IsStandard14(font.GetName(PdfName.Get("BaseFont"))?.Value ?? "")) WidthsRules(font, name);
            if (program is not { } p) return;
            if (!ProgramForm(p.Key, p.Stream, subtype)) ProgramFormFault(p.Key, p.Stream, name);
            else if (subtype == "TrueType") TrueTypeRules(font, descriptor!, name);
            else if (subtype is "Type1" or "MMType1") CharSetRule(font, descriptor!, name);
        }

        /// <summary>/FirstChar, /LastChar and as many /Widths as codes between them (6.2.11.2).</summary>
        private void WidthsRules(PdfDictionary font, string name)
        {
            var first = font.GetInteger(PdfName.Get("FirstChar"));
            var last = font.GetInteger(PdfName.Get("LastChar"));
            if (first is null) Add(PdfARules.FontFirstChar, Tr.T("Bei der Schrift „{0}“ fehlt /FirstChar.", name));
            if (last is null) Add(PdfARules.FontLastChar, Tr.T("Bei der Schrift „{0}“ fehlt /LastChar.", name));
            if (font.Get(PdfName.Get("Widths")) is not PdfArray widths || first is not null && last is not null && widths.Count != last - first + 1)
                Add(PdfARules.FontWidthsCount, Tr.T("Die Breitenangaben der Schrift „{0}“ fehlen oder sind unvollständig (/Widths).", name));
        }

        /// <summary>The Type 1 subsets with a /CharSet (PDF/A-1) and the glyph names the text shows with each font.</summary>
        private readonly List<(PdfDictionary Font, HashSet<string> Listed, string Name)> _charSets = [];
        private readonly Dictionary<PdfDictionary, HashSet<string>> _usedGlyphs = new(ReferenceEqualityComparer.Instance);

        /// <summary>The CIDFont subsets with a /CIDSet (PDF/A-1): their composite font, the CIDs their program has, what the set marks.</summary>
        private readonly List<(PdfDictionary Font, HashSet<int> Existing, Func<int, bool> Marked, string Name)> _cidSets = [];

        /// <summary>Composite fonts whose CIDFontType2 has no /CIDToGIDMap (PDF/A-1).</summary>
        private readonly List<(PdfDictionary Font, string Name)> _withoutGlyphMap = [];

        /// <summary>
        /// PDF/A-1 (ISO 19005-1, 6.3.5): the /CharSet of a Type 1 subset names every glyph of its program the text shows; (6.3.3.2) a CIDFontType2
        /// that draws text has a /CIDToGIDMap.
        /// </summary>
        private void UsedGlyphsListed()
        {
            foreach (var (font, name) in _withoutGlyphMap)
                if (_glyphs.TryGetValue(font, out var shown) && shown.Any(g => !g.Item3))
                    Add(PdfARules.CidToGidMap, Tr.T("Bei der Schrift „{0}“ fehlt die Zuordnung der Zeichen zu Glyphen (/CIDToGIDMap).", name));
            foreach (var (font, existing, marked, name) in _cidSets)
                if (_glyphs.TryGetValue(font, out var shown) && PdfFont.Get(font, doc) is var shownFont &&
                    shown.Any(g => shownFont.Cid(g.Item1, g.Item2) is var c && existing.Contains(c) && !marked(c)))
                    Add(PdfARules.CidSet, Tr.T("Die CID-Liste (/CIDSet) der Schrift „{0}“ nennt nicht alle CIDs, die der Text zeigt.", name));
            foreach (var (font, listed, name) in _charSets)
                if (_usedGlyphs.TryGetValue(font, out var used) && used.Any(g => !listed.Contains(g)))
                    Add(PdfARules.CharSet, Tr.T("Die Zeichenliste (/CharSet) der Schrift „{0}“ nennt nicht alle Glyphen, die der Text zeigt.", name));
        }

        /// <summary>A font name with the tag of a subset (ISO 32000-1, 9.6.4: six capital letters and „+“) — the fonts 6.2.11.4.2 is about.</summary>
        private static bool SubsetName(string name) =>
            name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).IndexOfAnyExceptInRange('A', 'Z') < 0;

        /// <summary>
        /// 6.2.11.4.2: the /CharSet of an embedded Type 1 subset, where it has one, names every glyph of its program (besides .notdef). PDF/A-1
        /// (6.3.5) asks every Type 1 subset for one, naming the glyphs the text shows (<see cref="UsedGlyphsListed"/>, once all pages are read).
        /// </summary>
        private void CharSetRule(PdfDictionary font, PdfDictionary descriptor, string name)
        {
            if (!SubsetName(name)) return;
            if (descriptor.Get(PdfName.Get("CharSet")) is not PdfString charSet)
            {
                if (_part.Part() == 1) Add(PdfARules.CharSet, Tr.T("Bei der Teilschrift „{0}“ fehlt die Zeichenliste (/CharSet).", name));
                return;
            }
            var listed = charSet.ToText().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            if (_part.Part() == 1)
            {
                _charSets.Add((font, listed, name));
                return;
            }
            if (FontProgram.Embedded(descriptor, doc) is not { IsEmbedded: true } program) return;
            var glyphs = (program.Type1 is { } type1 ? type1.GlyphNames
                : program.Cff is { IsCidKeyed: false } cff ? Enumerable.Range(0, cff.GlyphCount).Select(cff.GlyphName).OfType<string>()
                : []).ToHashSet(StringComparer.Ordinal);
            if (glyphs.Any(g => g != ".notdef" && !listed.Contains(g)))
                Add(PdfARules.CharSet, Tr.T("Die Zeichenliste (/CharSet) der Schrift „{0}“ nennt nicht alle Glyphen ihres Schriftprogramms.", name));
            // and nothing the program does not have (the list names the glyphs present — no more; Matterhorn 31-013 for PDF/UA-1, 7.21.4.2;
            // docs/FEHLERGEDAECHTNIS.md, No. 616)
            else if (glyphs.Count > 0 && listed.Any(g => g != ".notdef" && !glyphs.Contains(g)))
                Add(PdfARules.CharSet, Tr.T("Die Zeichenliste (/CharSet) der Schrift „{0}“ nennt Glyphen, die ihr Schriftprogramm nicht hat.", name));
        }

        /// <summary>
        /// 6.2.11.4.2: the /CIDSet of an embedded CIDFont subset, where it has one, marks every CID its program has (<see cref="PresentCids"/>).
        /// PDF/A-1 (6.3.5) asks every CIDFont subset for one that marks the CIDs of its program the text shows (judged once all pages are read,
        /// <see cref="UsedGlyphsListed"/>).
        /// </summary>
        private void CidSetRule(PdfDictionary font, PdfDictionary cid, PdfDictionary descriptor, string name)
        {
            if (!SubsetName(cid.GetName(PdfName.Get("BaseFont"))?.Value ?? "")) return;
            if (descriptor.GetStream(PdfName.Get("CIDSet")) is not { } cidSet)
            {
                if (_part.Part() == 1) Add(PdfARules.CidSet, Tr.T("Bei der Teilschrift „{0}“ fehlt die CID-Liste (/CIDSet).", name));
                return;
            }
            if (FontProgram.Embedded(descriptor, doc) is not { IsEmbedded: true } program) return;
            byte[] bits;
            try
            {
                bits = doc.DecodeStream(cidSet).Data;
            }
            catch (PdfException)
            {
                bits = [];
            }
            bool Marked(int c) => c >> 3 < bits.Length && (bits[c >> 3] & 0x80 >> (c & 7)) != 0;
            var map = cid.GetStream(PdfName.Get("CIDToGIDMap")) is { } mapStream ? StreamData(mapStream) : null;
            if (_part.Part() == 1)
            {
                if (ExistingCids(program, map) is { } existing) _cidSets.Add((font, existing.ToHashSet(), Marked, name));
                return;
            }
            if (PresentCids(program, map) is not { } present) return;
            if (present.Any(c => !Marked(c)))
                Add(PdfARules.CidSet, Tr.T("Die CID-Liste (/CIDSet) der Schrift „{0}“ nennt nicht alle CIDs ihres Schriftprogramms.", name));
        }

        private void CompositeRules(PdfDictionary font, string name)
        {
            if (font.Get(PdfName.Get("DescendantFonts")) is not PdfArray { Count: > 0 } descendants || descendants.Get(0) is not PdfDictionary cid) return;
            var kind = cid.GetName(PdfName.Subtype)?.Value;
            var program = cid.GetDictionary(PdfName.Get("FontDescriptor")) is { } descriptor ? ProgramStream(descriptor) : null;
            if (cid.GetName(PdfName.Type)?.Value != "Font") Add(PdfARules.FontType, Tr.T("Die Schrift „{0}“ ist nicht als Schrift gekennzeichnet (/Type).", name));
            if (kind is not ("CIDFontType0" or "CIDFontType2"))
                Add(PdfARules.FontSubtype, Tr.T("Die Schrift „{0}“ hat eine Art, die ISO 32000 nicht kennt (/Subtype {1}).", name, kind ?? "–"));
            if (program is not null && cid.GetDictionary(PdfName.Get("FontDescriptor")) is { } cidDescriptor) CidSetRule(font, cid, cidDescriptor, name);
            if (program is { } p && !ProgramForm(p.Key, p.Stream, kind)) ProgramFormFault(p.Key, p.Stream, name);
            // a stream or the name Identity (ISO 32000-1, 9.7.4.2, Table 117); PDF/A-1 (6.3.3.2) asks it of every such font that draws text
            // (judged once all pages are read), the other parts of every one with an embedded program
            if (kind == "CIDFontType2" && cid.Get(PdfName.Get("CIDToGIDMap")) is not (PdfStream or PdfName { Value: "Identity" }))
            {
                if (_part.Part() == 1) _withoutGlyphMap.Add((font, name));
                else if (program is not null) Add(PdfARules.CidToGidMap, Tr.T("Bei der Schrift „{0}“ fehlt die Zuordnung der Zeichen zu Glyphen (/CIDToGIDMap).", name));
            }
            switch (font.Get(PdfName.Get("Encoding")))
            {
                // PDF/A-1 (6.3.3.3): of the predefined CMaps only the identities need not be embedded
                case PdfName predefined when !Table118.Contains(predefined.Value) ||
                                             _part.Part() == 1 && predefined.Value is not ("Identity-H" or "Identity-V"):
                    Add(PdfARules.CMapEmbedded, Tr.T("Die Kodierung der Schrift „{0}“ nennt eine CMap, die weder vordefiniert noch eingebettet ist ({1}).", name, predefined.Value));
                    break;
                case PdfStream cmap:
                {
                    var cmapData = StreamData(cmap);
                    if (!CidCMap(cmapData))
                        Add(PdfARules.CMapEmbedded, Tr.T("Die Kodierung der Schrift „{0}“ ist keine gültige CMap: sie ordnet Codes nicht CIDs zu.", name));
                    if (MaxCid(cmapData) > 65535) Add(PdfARules.CidRange, Tr.T("Die Kodierung der Schrift „{0}“ nennt CIDs über 65 535.", name));
                    if ((cmap.Dictionary.GetInteger(PdfName.Get("WMode")) ?? 0) != CMap.Parse(cmapData).WMode)
                        Add(PdfARules.CMapWMode, Tr.T("Die Schreibrichtung der Kodierung der Schrift „{0}“ steht im Wörterbuch anders als in der CMap (/WMode).", name));
                    if (OtherCMap(cmap, cmapData) is { } used)
                        Add(PdfARules.CMapUses, Tr.T("Die Kodierung der Schrift „{0}“ verweist auf eine andere als eine vordefinierte CMap ({1}).", name, used));
                    break;
                }
            }
            // the character collection of the CIDFont and of the CMap agree, except with an identity CMap (6.2.11.3.1)
            // (the supplement of the CIDFont no higher than the CMap's — strict readers hold it so; PDF/A-1, 6.3.3.1, asks only for registry
            // and ordering)
            if (CMapCollection(font.Get(PdfName.Get("Encoding"))) is var (registry, ordering, supplement) &&
                cid.GetDictionary(PdfName.Get("CIDSystemInfo")) is var info &&
                (info?.GetString(PdfName.Get("Registry"))?.ToText() != registry || info?.GetString(PdfName.Get("Ordering"))?.ToText() != ordering ||
                 _part.Part() != 1 && (info?.GetInteger(PdfName.Get("Supplement")) ?? 0) > supplement))
                Add(PdfARules.CidSystemInfo, Tr.T("Die Zeichensammlung der Schrift „{0}“ passt nicht zu ihrer Kodierung (/CIDSystemInfo).", name));
        }

        /// <summary>
        /// A CMap an embedded CMap uses that is not one of ISO 32000-1, Table 118 (6.2.11.3.3): by its /UseCMap entry or the usecmap operator of its
        /// program; null if it uses none or only predefined ones.
        /// </summary>
        private static string? OtherCMap(PdfStream cmap, byte[] data)
        {
            switch (cmap.Dictionary.Get(PdfName.Get("UseCMap")))
            {
                case PdfStream:
                    return Tr.T("eingebettete CMap");
                case PdfName used when !Table118.Contains(used.Value):
                    return used.Value;
            }
            var text = System.Text.Encoding.Latin1.GetString(data);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"/([^\s/\[\]()<>{}%]+)\s+usecmap\b"))
                if (!Table118.Contains(m.Groups[1].Value)) return m.Groups[1].Value;
            return null;
        }

        /// <summary>The writer's note of the codes a font shows (for rebuilding fonts that break 6.2.11).</summary>
        private void NoteShown(PdfDictionary dict, PdfObject? shown, bool invisible)
        {
            if (!Shown!.TryGetValue(dict, out var entry)) Shown[dict] = entry = (doc, []);
            if (entry.Codes.Count > 70_000) return;
            var font = PdfFont.Get(dict, doc);
            void One(byte[] bytes)
            {
                for (var pos = 0; pos < bytes.Length;)
                {
                    var length = Math.Max(1, font.ReadCode(bytes, pos, out var code));
                    pos += length;
                    // a code outside the code space the writer leaves out of the content (WithoutStrayCodes): it needs no glyph
                    if (font is CompositeFont composite && !composite.InCodespace(code, length)) continue;
                    entry.Codes.Add((code, length, invisible));
                }
            }
            switch (shown)
            {
                case PdfString text:
                    One(text.Bytes);
                    break;
                case PdfArray parts:
                    for (var i = 0; i < parts.Count; i++)
                        if (parts[i] is PdfString part) One(part.Bytes);
                    break;
            }
        }

        /// <summary>The writer's part of 6.2.11.3.1: a CIDFont whose collection differs from its CMap's is noted with the CMap's.</summary>
        private void NoteCollection(PdfDictionary font)
        {
            if (font.Get(PdfName.Get("DescendantFonts")) is not PdfArray { Count: > 0 } descendants || descendants.Get(0) is not PdfDictionary cid ||
                CMapCollection(font.Get(PdfName.Get("Encoding"))) is not var (registry, ordering, supplement))
                return;
            if (CollectionFor(cid, registry, ordering, supplement) is { } collection) Collections!.TryAdd(cid, collection);
        }

        /// <summary>
        /// The character collection a CIDFont is given so that it agrees with its CMap (6.2.11.3.1): the CMap's registry and ordering, a
        /// supplement no higher than the CMap's; null if it agrees already.
        /// </summary>
        internal static PdfDictionary? CollectionFor(PdfDictionary cid, string registry, string ordering, long supplement)
        {
            var info = cid.GetDictionary(PdfName.Get("CIDSystemInfo"));
            var own = info?.GetInteger(PdfName.Get("Supplement")) ?? 0;
            if (info?.GetString(PdfName.Get("Registry"))?.ToText() == registry && info.GetString(PdfName.Get("Ordering"))?.ToText() == ordering && own <= supplement)
                return null;
            var collection = new PdfDictionary();
            collection.Set(PdfName.Get("Registry"), new PdfString(System.Text.Encoding.ASCII.GetBytes(registry)));
            collection.Set(PdfName.Get("Ordering"), new PdfString(System.Text.Encoding.ASCII.GetBytes(ordering)));
            collection.Set(PdfName.Get("Supplement"), PdfInteger.Of(Math.Min(own, supplement)));
            return collection;
        }

        /// <summary>Registry, ordering and supplement of a CMap other than Identity-H and -V; null for those and for what cannot be read.</summary>
        private static (string Registry, string Ordering, long Supplement)? CMapCollection(PdfObject encoding)
        {
            switch (encoding)
            {
                case PdfName { Value: "Identity-H" or "Identity-V" }:
                    return null;
                case PdfName predefined:
                    return PredefinedCMaps.Collection(predefined.Value) is { } ordering ? ("Adobe", ordering, PredefinedCMaps.Supplement(predefined.Value) ?? 99) : null;
                case PdfStream stream when stream.Dictionary.GetDictionary(PdfName.Get("CIDSystemInfo")) is { } info:
                    return info.GetString(PdfName.Get("Registry"))?.ToText() is { } registry && info.GetString(PdfName.Get("Ordering"))?.ToText() is { } order
                        ? (registry, order, info.GetInteger(PdfName.Get("Supplement")) ?? 0)
                        : null;
                default:
                    return null;
            }
        }

        /// <summary>Whether a font (or the CIDFont of a composite one) has an embedded program.</summary>
        private static bool HasProgram(PdfDictionary font)
        {
            var described = font.Get(PdfName.Get("DescendantFonts")) is PdfArray { Count: > 0 } descendants && descendants.Get(0) is PdfDictionary cid ? cid : font;
            return described.GetDictionary(PdfName.Get("FontDescriptor")) is { } descriptor && ProgramStream(descriptor) is not null;
        }

        /// <summary>The embedded font program of a descriptor and its key; null if there is none.</summary>
        private static (string Key, PdfStream Stream)? ProgramStream(PdfDictionary descriptor)
        {
            foreach (var key in (string[])["FontFile", "FontFile2", "FontFile3"])
                if (descriptor.GetStream(PdfName.Get(key)) is { } stream) return (key, stream);
            return null;
        }

        /// <summary>
        /// Whether a font program is embedded in a form strict readers take (ISO 32000-1, 9.9, Table 126) — they choose how to read it by the
        /// kind of font: TrueType and CIDFontType2 as an sfnt (also with CFF outlines, not as a collection), CIDFontType0 only as CFF of
        /// FontFile3 /CIDFontType0C or as OpenType of FontFile3 /OpenType, Type 1 as text with both lengths (not as PFB), as CFF of FontFile3
        /// /Type1C or as OpenType of FontFile3 /OpenType.
        /// </summary>
        /// <summary>
        /// A program not in the form its font kind prescribes: a /Subtype of FontFile3 that ISO 32000 does not know (6.2.11.2), otherwise a
        /// program strict readers cannot take as the font's (a Type 1 program as PFB, a CFF under FontFile2 …) — for them it is not embedded
        /// (6.2.11.4.1).
        /// </summary>
        private void ProgramFormFault(string key, PdfStream stream, string name)
        {
            if (key == "FontFile3" && stream.Dictionary.GetName(PdfName.Subtype)?.Value is not ("Type1C" or "CIDFontType0C" or "OpenType") ||
                key == "FontFile3" && _part.Part() == 1 && stream.Dictionary.GetName(PdfName.Subtype)?.Value == "OpenType")
                Add(PdfARules.FontFileSubtype, Tr.T("Das Schriftprogramm der Schrift „{0}“ hat eine unbekannte Art (/Subtype).", name));
            else Add(PdfARules.FontEmbedded, Tr.T("Die Schrift „{0}“ ist nicht in der vorgeschriebenen Form eingebettet.", name));
        }

        private bool ProgramForm(string key, PdfStream stream, string? fontKind)
        {
            byte[] head;
            try
            {
                head = doc.DecodeStream(stream, 64).Data;
            }
            catch (PdfException)
            {
                return false;
            }
            if (head.Length < 4) return false;
            var start = head.AsSpan(0, 4);
            var sfnt = start.SequenceEqual(new byte[] { 0, 1, 0, 0 }) || start.SequenceEqual("true"u8) || start.SequenceEqual("OTTO"u8);
            var cff = head[0] == 1 && head[1] == 0;
            var subtype = key == "FontFile3" ? stream.Dictionary.GetName(PdfName.Subtype)?.Value : null;
            // PDF 1.4 knows no OpenType programs (ISO 19005-1, 6.3.2)
            if (subtype == "OpenType" && _part.Part() == 1) return false;
            return fontKind switch
            {
                "TrueType" or "CIDFontType2" => sfnt,
                "CIDFontType0" => subtype == "CIDFontType0C" && cff || subtype == "OpenType" && start.SequenceEqual("OTTO"u8),
                "Type1" or "MMType1" => key switch
                {
                    "FontFile" => head.AsSpan().TrimStart(" \t\r\n\0"u8).StartsWith("%!"u8) &&
                                  stream.Dictionary.GetInteger(PdfName.Get("Length1")) > 0 && stream.Dictionary.GetInteger(PdfName.Get("Length2")) > 0,
                    "FontFile3" => subtype == "Type1C" && cff || subtype == "OpenType" && start.SequenceEqual("OTTO"u8),
                    _ => false,
                },
                _ => true,
            };
        }

        /// <summary>The rules on TrueType fonts (6.2.11.6): how codes are meant to reach the glyphs.</summary>
        private void TrueTypeRules(PdfDictionary font, PdfDictionary descriptor, string name)
        {
            if (FontProgram.Embedded(descriptor, doc)?.OpenType is not { } program) return;
            var symbolic = ((descriptor.GetInteger(PdfName.Get("Flags")) ?? 0) & 4) != 0;
            var encoding = font.Get(PdfName.Get("Encoding"));
            // the cmap subtables the parts ask for (6.2.11.6): part 2 and 3 a single one or (3,0) in a symbolic font, one besides (3,0) in a
            // non-symbolic one; part 4 (3,0) or (1,0), and (3,1) or (1,0)
            // part 1 (ISO 19005-1, 6.3.7): exactly one in a symbolic font, nothing on the cmap of a non-symbolic one
            var four = _part.Part() == 4;
            var one = _part.Part() == 1;
            if (symbolic)
            {
                if (encoding is not PdfNull) Add(PdfARules.SymbolicNoEncoding, Tr.T("Die symbolische TrueType-Schrift „{0}“ hat eine Kodierung (/Encoding).", name));
                if (four ? !program.HasCmap(3, 0) && !program.HasCmap(1, 0)
                    : one ? program.CmapSubtables.Count != 1
                    : program.CmapSubtables.Count != 1 && !program.HasCmap(3, 0))
                    Add(PdfARules.SymbolicCmap, Tr.T("Die symbolische TrueType-Schrift „{0}“ hat keine passende Zeichentabelle (cmap).", name));
                return;
            }
            if (four ? !program.HasCmap(3, 1) && !program.HasCmap(1, 0) : program.CmapSubtables.Count <= (program.HasCmap(3, 0) ? 1 : 0))
                Add(PdfARules.TrueTypeCmap, Tr.T("Die TrueType-Schrift „{0}“ hat keine passende Zeichentabelle (cmap).", name));
            var baseEncoding = encoding switch
            {
                PdfName n => n.Value,
                PdfDictionary d => d.GetName(PdfName.Get("BaseEncoding"))?.Value,
                _ => null,
            };
            if (baseEncoding is not ("WinAnsiEncoding" or "MacRomanEncoding"))
                Add(PdfARules.TrueTypeEncoding, Tr.T("Die TrueType-Schrift „{0}“ hat nicht die Kodierung WinAnsi oder MacRoman.", name));
            // part 1 (6.3.7): the encoding as it is, without differences
            else if (one && encoding is PdfDictionary withDifferences && withDifferences.ContainsKey(PdfName.Get("Differences")))
                Add(PdfARules.TrueTypeEncoding, Tr.T("Die TrueType-Schrift „{0}“ ändert ihre Kodierung (/Differences).", name));
            else if (encoding is PdfDictionary e && e.Get(PdfName.Get("Differences")) is PdfArray differences &&
                     differences.Any(item => item is PdfName glyph && !GlyphNames.InList(glyph.Value) && GlyphNames.CodePoint(glyph.Value) <= 0 &&
                                             program.GlyphByName(glyph.Value) <= 0 && SimpleFont.GlyphIndexName(glyph.Value, program.GlyphCount) <= 0))
                Add(PdfARules.TrueTypeEncoding, Tr.T("Die TrueType-Schrift „{0}“ ändert ihre Kodierung mit Namen, die die Adobe Glyph List nicht kennt.", name));
        }

        /// <summary>
        /// The text of a code shown, also in invisible text: level u (6.2.11.7.2) asks for one; no part allows the values 0, U+FEFF, U+FFFE in it
        /// (level u, and part 4 at every level: ISO 19005-4, 6.2.10.7). The rules apply by part and level (<see cref="Add(PdfARule, string, int?)"/>).
        /// </summary>
        private void Unicode(string? text, string name, bool forbidden = false)
        {
            // the values as written in /ToUnicode: the text leaves a 0 out (docs/FEHLERGEDAECHTNIS.md, No. 501)
            if (string.IsNullOrEmpty(text) && !forbidden) Add(PdfARules.UnicodeMapped, Tr.T("Die Schrift „{0}“ zeigt Zeichen ohne Unicode-Zuordnung.", name));
            else if (forbidden || text!.AsSpan().IndexOfAny('\0', '\uFEFF', '\uFFFE') >= 0)
            {
                var message = Tr.T("Die Unicode-Zuordnung der Schrift „{0}“ enthält unzulässige Werte (0, U+FEFF, U+FFFE).", name);
                Add(PdfARules.UnicodeValuesU, message);
                Add(PdfARules.UnicodeValues, message);
            }
        }

        /// <summary>The codes of shown text against the font program (6.2.11.4.1, 6.2.11.5, 6.2.11.8).</summary>
        private void Glyphs(PdfDictionary dict, PdfObject? shown, bool invisible)
        {
            var font = PdfFont.Get(dict, doc);
            if (!_glyphs.TryGetValue(dict, out var seen)) _glyphs[dict] = seen = [];
            var name = FontName(dict);
            void One(byte[] bytes)
            {
                for (var pos = 0; pos < bytes.Length;)
                {
                    var length = Math.Max(1, font.ReadCode(bytes, pos, out var code));
                    pos += length;
                    // a code outside the code space of a composite font (a string ending in the middle of one): readers take it differently
                    // (docs/FEHLERGEDAECHTNIS.md, No. 340)
                    if (font is CompositeFont composite && !composite.InCodespace(code, length))
                    {
                        Add(PdfARules.GlyphPresent, Tr.T("Die Schrift „{0}“ zeigt einen Code, den ihre Kodierung nicht kennt (/Encoding).", name));
                        continue;
                    }
                    // level a (6.2.11.7.3): a character in a private use area needs a replacement text around it
                    if (_actualText == 0 && PdfARules.PuaActualText.Id(_part) is not null && font.StrictText(code, length) is { } shownText && HasPrivateUse(shownText))
                        Add(PdfARules.PuaActualText, Tr.T("Die Schrift „{0}“ zeigt Zeichen aus dem privaten Bereich von Unicode ohne Ersatztext (/ActualText).", name));
                    if (seen.Count > 100_000 || !seen.Add((code, length, invisible))) continue;
                    Unicode(font.StrictText(code, length), name, font.ForbiddenUnicode(code, length));
                    if (font.Strict(code, length) is not { } glyph) return;
                    if (!glyph.Embedded)
                    {
                        // an embedded program the display cannot read: drawn with a substitute; strict readers take it as not embedded
                        if (!invisible)
                            Add(PdfARules.FontEmbedded, HasProgram(dict)
                                ? Tr.T("Die eingebettete Schrift „{0}“ lässt sich nicht lesen.", name)
                                : Tr.T("Die Schrift „{0}“ ist nicht eingebettet.", name));
                        return;
                    }
                    if (Debug && (glyph.Notdef || !invisible && !glyph.Present || !invisible && glyph.ProgramWidth is { } w0 && Math.Abs(w0 - glyph.DictionaryWidth) > 1))
                        Console.Error.WriteLine($"PDF/A {name} Code {code}/{length}{(invisible ? " unsichtbar" : "")}: {glyph}");
                    if (glyph is { Present: true, Name: { } glyphName } && _part.Part() == 1)
                        (_usedGlyphs.TryGetValue(dict, out var used) ? used : _usedGlyphs[dict] = new HashSet<string>(StringComparer.Ordinal)).Add(glyphName);
                    if (glyph.Notdef) Add(PdfARules.NoNotdef, Tr.T("Die Schrift „{0}“ zeigt das Ersatzzeichen .notdef.", name));
                    if (invisible) continue;
                    if (!glyph.Present) Add(PdfARules.GlyphPresent, Tr.T("Die Schrift „{0}“ zeigt Zeichen, die ihr Schriftprogramm nicht hat.", name));
                    else if (glyph.ProgramWidth is { } width && Math.Abs(width - glyph.DictionaryWidth) > 1)
                        Add(PdfARules.GlyphWidths, Tr.T("Die Breiten der Schrift „{0}“ passen nicht zu ihrem Schriftprogramm.", name));
                }
            }
            var any = false;
            switch (shown)
            {
                case PdfString text:
                    One(text.Bytes);
                    any = text.Bytes.Length > 0;
                    break;
                case PdfArray parts:
                    for (var i = 0; i < parts.Count; i++)
                        if (parts[i] is PdfString part)
                        {
                            One(part.Bytes);
                            any |= part.Bytes.Length > 0;
                        }
                    break;
            }
            // a font shown with empty text is used all the same (strict readers judge the operator)
            if (!any && !invisible && dict.GetName(PdfName.Subtype)?.Value != "Type3" && !HasProgram(dict))
                Add(PdfARules.FontEmbedded, Tr.T("Die Schrift „{0}“ ist nicht eingebettet.", name));
        }

        /// <summary>Notes the codes of a string (or the strings of a TJ array) shown with a simple font.</summary>
        private void Codes(PdfDictionary font, PdfObject? shown)
        {
            if (!FontCodes!.TryGetValue(font, out var codes)) FontCodes[font] = codes = new bool[256];
            switch (shown)
            {
                case PdfString text:
                    foreach (var b in text.Bytes) codes[b] = true;
                    break;
                case PdfArray parts:
                    for (var i = 0; i < parts.Count; i++)
                        if (parts[i] is PdfString part)
                            foreach (var b in part.Bytes) codes[b] = true;
                    break;
            }
        }
    }
}
