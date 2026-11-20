using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// A standard font that is not embedded, embedded as the Liberation font the display draws it with (<see cref="StandardFontEmbedding"/>);
        /// null if it is none or the page would not look the same.
        /// </summary>
        private PdfDictionary? EmbeddedStandardFont(PdfDictionary font, Source? source, PdfDictionary original)
        {
            // a font by its /Type, or because the pages show text with it (6.2.11.2: the copy names it a font)
            if (font.GetName(PdfName.Type)?.Value != "Font" && !_pdfA!.Usage.Shown.ContainsKey(original)) return null;
            if (StandardEmbedding(font, source, original) is not { } embedded) return null;
            // embedded or not by the font as read: a font rebuilt for its program already refers to the new one
            if (!_embeddedFonts.TryGetValue(embedded.Key, out var descriptorRef))
            {
                embedded.Descriptor.Set(PdfName.Get(embedded.ProgramKey), Fresh(embedded.Program));
                // queued as it is (registered in _memoryNumbers, Fresh would turn it into a reference to itself when it is written)
                var n = NewNumber();
                _memoryQueue.Enqueue((n, embedded.Descriptor));
                _embeddedFonts[embedded.Key] = descriptorRef = new OutputReference(n);
            }
            var result = embedded.Font;
            result.Set(PdfName.Get("FontDescriptor"), descriptorRef);
            // the text of the codes shown, written for every shown font (WriteToUnicode); a font not shown keeps what it had
            if (font.GetRaw(PdfName.Get("ToUnicode")) is { } toUnicode) result.Set(PdfName.Get("ToUnicode"), toUnicode);
            else if (embedded.ToUnicode is { } reads) result.Set(PdfName.Get("ToUnicode"), Fresh(reads));
            return result;
        }

        /// <summary>Standard fonts as embedded (<see cref="StandardEmbedding"/>), by the dictionary as read; null where it cannot be.</summary>
        private readonly Dictionary<PdfDictionary, StandardFontEmbedding.Result?> _standardEmbeddings = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// How a standard font that is not embedded is embedded as the Liberation font the display draws it with (<see cref="StandardFontEmbedding"/>):
        /// decided once per font, before the fonts are rebuilt (a font this embeds needs no substitute) and when it is written; null if it is
        /// no such font or the page would not look the same.
        /// </summary>
        private StandardFontEmbedding.Result? StandardEmbedding(PdfDictionary font, Source? source, PdfDictionary original)
        {
            if (_standardEmbeddings.TryGetValue(original, out var known)) return known;
            // embedded or not by the font as read: a font rebuilt for its program already refers to the new one
            var descriptor = original.GetRaw(PdfName.Get("FontDescriptor")) is { } d ? PdfAResolve(d, source) as PdfDictionary : null;
            StandardFontEmbedding.Result? result = null;
            if (descriptor is null || !(descriptor.ContainsKey(PdfName.Get("FontFile")) || descriptor.ContainsKey(PdfName.Get("FontFile2")) ||
                                        descriptor.ContainsKey(PdfName.Get("FontFile3"))))
            {
                var encoding = font.GetRaw(PdfName.Get("Encoding")) is { } e ? PdfAResolve(e, source) : null;
                var widths = font.GetRaw(PdfName.Get("Widths")) is { } w ? PdfAResolve(w, source) as PdfArray : null;
                // what the display draws per code with the substitute (for a font that must be written symbolic)
                Func<int, (int, double, string?)>? drawn = null;
                if (source is not null && PdfFont.Get(original, source.Doc) is SimpleFont shownFont && shownFont.DrawnTrueType(substitute: true) is var (_, glyphOf))
                    drawn = code => (glyphOf(code), shownFont.Glyph((uint)code, 1).Width * 1000, shownFont.Glyph((uint)code, 1).Text);
                result = StandardFontEmbedding.For(font, encoding, widths, descriptor, _pdfA!.FontCodes.GetValueOrDefault(original), drawn,
                    noDifferences: _pdfA.Part.Part() == 1);
            }
            return _standardEmbeddings[original] = result;
        }

        /// <summary>
        /// A font dictionary for PDF/A (6.2.11.2, 6.2.11.3): marked as a font, named, widths for every code of an embedded, non-standard or Type 3
        /// simple font (those the display uses — where the file gives none it uses the program's), /CIDToGIDMap /Identity for an embedded TrueType
        /// CIDFont without a valid one (what the display takes then), the character collection of the CMap, resources where a Type 3 font borrowed them.
        /// </summary>
        private void PdfAFont(PdfDictionary font, Func<PdfDictionary> writable, Source? source, PdfDictionary original, string kind)
        {
            var state = _pdfA!;
            if (font.GetName(PdfName.Type)?.Value != "Font") writable().Set(PdfName.Type, PdfName.Get("Font"));
            var descriptor = font.GetRaw(PdfName.Get("FontDescriptor")) is { } raw ? PdfAResolve(raw, source) as PdfDictionary : null;
            if (kind != "Type3" && font.Get(BaseFont) is not PdfName)
                writable().Set(BaseFont, descriptor?.GetName(PdfName.Get("FontName")) ?? PdfName.Get("Unbenannt"));
            if (font.Get(BaseFont) is PdfName baseFont && !PdfACheck.Utf8(baseFont.Value)) writable().Set(BaseFont, Utf8Name(baseFont));
            var embedded = descriptor is not null && (descriptor.ContainsKey(PdfName.Get("FontFile")) || descriptor.ContainsKey(PdfName.Get("FontFile2")) ||
                                                      descriptor.ContainsKey(PdfName.Get("FontFile3")));
            // a stream or Identity (Table 117); where the entry is neither, the display takes Identity (a map the writer made refers to the new file)
            if (kind == "CIDFontType2" && embedded && font.GetRaw(PdfName.Get("CIDToGIDMap")) is var map && map is not OutputReference &&
                (map is null ? null : PdfAResolve(map, source)) is not (PdfStream or PdfName { Value: "Identity" }))
                writable().Set(PdfName.Get("CIDToGIDMap"), PdfName.Get("Identity"));
            if (kind is "CIDFontType0" or "CIDFontType2" && state.Usage.Collections.TryGetValue(original, out var collection))
                writable().Set(PdfName.Get("CIDSystemInfo"), collection);
            // PDF/A-1 (6.3.5): a CIDFont subset names the CIDs of its program (/CIDSet) — also one the writer made itself
            if (kind is "CIDFontType0" or "CIDFontType2" && state.Part.Part() == 1 && embedded && descriptor is not null && !descriptor.ContainsKey(PdfName.Get("CIDSet")) &&
                font.Get(BaseFont) is PdfName { Value: var subsetName } && subsetName.Length > 7 && subsetName[6] == '+' &&
                subsetName.AsSpan(0, 6).IndexOfAnyExceptInRange('A', 'Z') < 0 && WithCidSet(font, descriptor, source) is { } withSet)
                writable().Set(PdfName.Get("FontDescriptor"), withSet);
            // the resources its glyphs are drawn with: none of its own, or entries its own lack (6.2.2)
            PdfDictionary? borrowed = null;
            if (kind == "Type3" && state.Usage.Borrowed.TryGetValue(original, out borrowed))
            {
                // the resources of the page it is drawn on name the font itself: left out — no glyph draws with its own font, and a reference
                // validator followed the circle until its stack overflowed (docs/FEHLERGEDAECHTNIS.md, No. 515)
                if (borrowed.Get(PdfName.Get("Font")) is PdfDictionary pageFonts &&
                    pageFonts.Entries.Where(e => ReferenceEquals(pageFonts.Get(e.Key), original)).Select(e => e.Key).ToList() is { Count: > 0 } selves)
                {
                    var fonts = CloneWithResolver(pageFonts);
                    foreach (var key in selves) fonts.Remove(key);
                    borrowed = CloneWithResolver(borrowed);
                    borrowed.Set(PdfName.Get("Font"), fonts);
                }
                writable().Set(PdfName.Resources, borrowed);
            }
            if (kind == "Type3" && (borrowed ?? font.GetRaw(PdfName.Resources)) is { } type3Resources)
                WithoutUnchosenFonts(original, type3Resources, writable, source);
            if (kind is "Type1" or "MMType1" or "TrueType" && (embedded || !StandardFonts.IsStandard14(font.GetName(BaseFont)?.Value ?? "")) &&
                source is not null && !ValidWidths(font))
            {
                var shown = PdfFont.Get(original, source.Doc);
                var widths = new PdfArray();
                for (var c = 0; c < 256; c++) widths.Add(Exact(shown.Glyph((uint)c, 1).Width * 1000));
                writable().Set(PdfName.Get("FirstChar"), PdfInteger.Of(0));
                writable().Set(PdfName.Get("LastChar"), PdfInteger.Of(255));
                writable().Set(PdfName.Get("Widths"), widths);
            }
            // a glyph procedure that is no stream (an object that cannot be read, or null): the same as none (ISO 32000-1, 7.3.7) — left out, strict
            // readers fail on such an entry
            if (kind == "Type3" && font.GetRaw(PdfName.Get("CharProcs")) is { } rawProcs && Look(rawProcs, source) is (PdfDictionary procs, var procsFrom) &&
                procs.Entries.Any(e => e.Value is not OutputReference && PdfAResolve(e.Value, procsFrom) is not PdfStream))
            {
                var kept = new PdfDictionary();
                foreach (var (key, value) in procs.Entries)
                    if (value is OutputReference || PdfAResolve(value, procsFrom) is PdfStream) kept.Set(key, Portable(value, procsFrom, source));
                writable().Set(PdfName.Get("CharProcs"), kept);
            }
            // a Type 3 font is a simple font as well (6.2.11.2): its widths in glyph space, as the display takes them from /FirstChar and /Widths
            if (kind == "Type3" && source is not null && !ValidWidths(font) && PdfFont.Get(original, source.Doc) is Type3Font type3)
            {
                var widths = new PdfArray();
                for (var c = 0; c < 256; c++) widths.Add(Exact(type3.Strict((uint)c, 1)!.Value.DictionaryWidth));
                writable().Set(PdfName.Get("FirstChar"), PdfInteger.Of(0));
                writable().Set(PdfName.Get("LastChar"), PdfInteger.Of(255));
                writable().Set(PdfName.Get("Widths"), widths);
            }
        }

        /// <summary>
        /// A font the writer made itself for elements and fields (<see cref="Editing.ElementFace.SavedFont"/>), for PDF/A-1: its CIDFont subset
        /// gets the /CIDSet of its program (6.3.5) — read from the program as built, which the descriptor holds directly.
        /// </summary>
        private PdfDictionary WithOwnCidSet(PdfDictionary font)
        {
            if (_pdfA?.Part.Part() != 1 || font.Get(PdfName.Get("DescendantFonts")) is not PdfArray { Count: > 0 } descendants ||
                descendants.Get(0) is not PdfDictionary cid || cid.Get(PdfName.Get("FontDescriptor")) is not PdfDictionary descriptor ||
                (descriptor.Get(PdfName.Get("FontFile2")) as PdfStream ?? descriptor.Get(PdfName.Get("FontFile3")) as PdfStream) is not { RawData: { } data } ||
                FontProgram.FromBytes(data) is not { } program || PdfACheck.ExistingCids(program, null) is not { } present || CidSet(present) is not { } set)
                return font;
            descriptor.Set(PdfName.Get("CIDSet"), Fresh(set));
            return font;
        }

        /// <summary>
        /// A copy of the descriptor of a CIDFont with the /CIDSet of its program (<see cref="PdfACheck.PresentCids"/>); null where the program
        /// cannot be read.
        /// </summary>
        private PdfDictionary? WithCidSet(PdfDictionary cidFont, PdfDictionary descriptor, Source? source)
        {
            var doc = source?.Doc ?? _main;
            if (FontProgram.Embedded(descriptor, doc) is not { IsEmbedded: true } program) return null;
            byte[]? map = null;
            if (cidFont.Get(PdfName.Get("CIDToGIDMap")) is PdfStream mapStream)
                try
                {
                    map = doc.GetStreamData(mapStream);
                }
                catch (PdfException)
                {
                    return null;
                }
            if (PdfACheck.ExistingCids(program, map) is not { } present || CidSet(present) is not { } set) return null;
            var copy = CloneWithResolver(descriptor);
            copy.Set(PdfName.Get("CIDSet"), Fresh(set));
            return copy;
        }

        /// <summary>
        /// A font name that is no UTF-8 (6.1.8; often a Chinese name in GBK) in ASCII: every other byte becomes "_" (as in the program,
        /// <see cref="CffBuilder.AsciiNames"/>). A re-encoding as UTF-8 is read back differently by strict readers. Only the name changes — the
        /// font is embedded, no reader looks for it by name.
        /// </summary>
        private static PdfName Utf8Name(PdfName name) => PdfName.Get(new string(name.Value.Select(c => c is > ' ' and < (char)127 ? c : '_').ToArray()));

        /// <summary>
        /// The fonts of a Type 3 font's resources that none of its procedures chooses, left out: files often give it the fonts of the page, itself
        /// among them, and strict readers follow such a circle without end (docs/FEHLERGEDAECHTNIS.md, No. 340). Unused resources draw nothing;
        /// where a procedure draws a form (which may take the font's resources) or cannot be read, all stay.
        /// </summary>
        private void WithoutUnchosenFonts(PdfDictionary original, PdfObject rawResources, Func<PdfDictionary> writable, Source? source)
        {
            if (source is null || Look(rawResources, source) is not (PdfDictionary resources, var resourcesFrom) ||
                resources.GetRaw(PdfName.Get("Font")) is not { } rawFonts || Look(rawFonts, resourcesFrom) is not (PdfDictionary fonts, var fontsFrom) ||
                fonts.Count == 0)
                return;
            // the procedures as read (the writer changes only their first operator, or adds empty ones)
            var chosen = new HashSet<PdfName>();
            // (a Type 3 font without /CharProcs, found by fuzzing: no procedure chooses a font — No. 522)
            if (original.GetDictionary(PdfName.Get("CharProcs")) is { } procs)
            foreach (var (key, _) in procs.Entries)
            {
                if (procs.Get(key) is not PdfStream proc) continue;
                // a procedure with resources of its own takes its fonts from there
                if (proc.Dictionary.ContainsKey(PdfName.Resources)) continue;
                try
                {
                    var parser = new ContentParser(source.Doc.GetStreamData(proc));
                    while (parser.Next() is { } op)
                    {
                        if (op == "Do") return;
                        if (op == "Tf" && parser.Operands.Count > 0 && parser.Operands[0].Name is { } name) chosen.Add(name);
                    }
                }
                catch (PdfException)
                {
                    return;
                }
            }
            if (fonts.Entries.All(e => chosen.Contains(e.Key))) return;
            var kept = new PdfDictionary();
            foreach (var (key, value) in fonts.Entries)
                if (chosen.Contains(key)) kept.Set(key, Portable(value, fontsFrom, source));
            var newResources = new PdfDictionary();
            foreach (var (key, value) in resources.Entries) newResources.Set(key, key.Value == "Font" ? kept : Portable(value, resourcesFrom, source));
            writable().Set(PdfName.Resources, newResources);
        }

        private static bool ValidWidths(PdfDictionary font) =>
            font.GetInteger(PdfName.Get("FirstChar")) is { } first && font.GetInteger(PdfName.Get("LastChar")) is { } last &&
            font.Get(PdfName.Get("Widths")) is PdfArray widths && widths.Count == last - first + 1;
    }
}
