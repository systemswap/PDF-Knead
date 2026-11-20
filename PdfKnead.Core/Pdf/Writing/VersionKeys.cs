using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Keys that do not belong to the version written (docs/FEHLERGEDAECHTNIS.md, No. 702; found by the runs of all ways to write against the
/// file-format model): each object is written without them, in its direct parts as well. Decided per key — leave out what the version does
/// not know and what carries nothing a reader of it would miss; raise the version (<see cref="PdfWriter"/>'s content version, No. 553) where
/// content would change; keep what carries meaning a reader of the lower version simply passes over:
/// <list type="bullet">
/// <item>below 1.5: /Tabs of a page (tab order only) and /FontDescriptor of a Type 3 font go — not /Tabs /S of a tagged copy: PDF/UA asks
/// for it on every page with annotations (ISO 14289-1, 7.18.3), a reader of 1.4 passes over it (docs/FEHLERGEDAECHTNIS.md, No. 921); field flags of PDF 1.5 (do not spell-check,
/// do not scroll, comb, rich text, radios in unison, commit on selection change) raise the version to 1.5 — with PDF/A (fixed version)
/// they are cleared;</item>
/// <item>below 2.0: /UseBlackPtComp and /HTO of a graphics state go; /AF, /NS and /Namespaces stay (PDF/A-3 needs associated files, the
/// namespaces carry what the structure means);</item>
/// <item>2.0: the deprecated /ProcSet, /Name of fonts (not of Type 3 fonts, No. 812) and XObjects, /CharSet and /CIDSet of font descriptors, /Suspects, /NeedsRendering
/// and the XFA of a form that has fields of its own go; the document information dictionary stays (deprecated, not forbidden, and what it
/// says may stand nowhere else).</item>
/// </list>
/// </summary>
internal static class VersionKeys
{
    private static readonly PdfName Type = PdfName.Get("Type"), Subtype = PdfName.Get("Subtype"), Tabs = PdfName.Get("Tabs"),
        FontDescriptor = PdfName.Get("FontDescriptor"), Ff = PdfName.Get("Ff"), UseBlackPtComp = PdfName.Get("UseBlackPtComp"), Hto = PdfName.Get("HTO"),
        ProcSet = PdfName.Get("ProcSet"), Name = PdfName.Get("Name"), CharSet = PdfName.Get("CharSet"), CidSet = PdfName.Get("CIDSet"),
        Suspects = PdfName.Get("Suspects"), NeedsRendering = PdfName.Get("NeedsRendering"), Xfa = PdfName.Get("XFA"), Fields = PdfName.Get("Fields"),
        FT = PdfName.Get("FT");

    /// <summary>Field flags (bits 23–27, 1-based) introduced with PDF 1.5 (ISO 32000-2, Tables 228, 230, 232).</summary>
    private const long Flags15 = (1L << 22) | (1L << 23) | (1L << 24) | (1L << 25) | (1L << 26);

    /// <summary>
    /// The object as the version has it; the object itself if nothing changes.
    /// </summary>
    /// <param name="obj">The object to write.</param>
    /// <param name="version">The version written ("1.4" … "2.0").</param>
    /// <param name="pdfA">PDF/A: the version is fixed, nothing raises it.</param>
    /// <param name="needs">Notes that the content needs a higher version (version, reason).</param>
    /// <param name="structureOrder">The copy is tagged: a page's /Tabs /S (tab order of the structure) stays below 1.5.</param>
    public static PdfObject Filter(PdfObject obj, string version, bool pdfA, Action<string, string> needs, bool structureOrder = false)
    {
        var below15 = string.CompareOrdinal(version, "1.5") < 0;
        var below20 = string.CompareOrdinal(version, "2.0") < 0;
        return Walk(obj, 0);

        PdfObject Walk(PdfObject value, int depth)
        {
            if (depth > 32) return value;
            switch (value)
            {
                case PdfStream stream:
                    return Dict(stream.Dictionary, depth) is { } changedDict ? WithDictionary(stream, changedDict) : stream;
                case PdfDictionary dict:
                    return Dict(dict, depth) ?? dict;
                case PdfArray array:
                    List<PdfObject>? items = null;
                    for (var i = 0; i < array.Count && i < 100_000; i++)
                    {
                        var item = array[i];
                        if (item is not (PdfDictionary or PdfArray)) continue;
                        var walked = Walk(item, depth + 1);
                        if (ReferenceEquals(walked, item)) continue;
                        if (items is null)
                        {
                            items = new List<PdfObject>(array.Count);
                            for (var k = 0; k < array.Count; k++) items.Add(array[k]);
                        }
                        items[i] = walked;
                    }
                    return items is null ? array : new PdfArray(items);
                default:
                    return value;
            }
        }

        // a dictionary without what the version does not have; null if nothing changes (direct parts walked as well, references not followed)
        PdfDictionary? Dict(PdfDictionary dict, int depth)
        {
            List<PdfName>? drop = null;
            (PdfName Key, PdfObject Value)? set = null;
            var type = dict.GetRaw(Type) is PdfName t ? t.Value : null;
            var subtype = dict.GetRaw(Subtype) is PdfName s ? s.Value : null;
            void Drop(PdfName key)
            {
                if (dict.ContainsKey(key)) (drop ??= []).Add(key);
            }
            if (below15)
            {
                if (type == "Page" && !(structureOrder && dict.GetRaw(Tabs) is PdfName { Value: "S" })) Drop(Tabs);
                if (subtype == "Type3") Drop(FontDescriptor);
                if (dict.GetRaw(Ff) is PdfInteger flags && (flags.Value & Flags15) != 0 && (dict.ContainsKey(FT) || dict.ContainsKey(PdfName.Get("T")) ||
                                                                                          dict.ContainsKey(PdfName.Kids)))
                {
                    if (pdfA) set = (Ff, PdfInteger.Of(flags.Value & ~Flags15));
                    else needs("1.5", Localization.Tr.T("Formularfeld-Eigenschaften"));
                }
            }
            if (below20)
            {
                // keys of a graphics state only
                Drop(UseBlackPtComp);
                Drop(Hto);
            }
            else
            {
                Drop(ProcSet);
                // not that of a Type 3 font: ISO 32000-2 deprecates /Name in Table 109 (simple fonts) but not in Table 110, and the model asks
                // its descriptor's /FontName to equal it — it is the font's only name (docs/FEHLERGEDAECHTNIS.md, No. 812)
                if ((type == "Font" && subtype != "Type3") || type == "XObject" || subtype is "Image" or "Form" or "PS") Drop(Name);
                if (type == "FontDescriptor" || dict.ContainsKey(PdfName.Get("FontFile")) || dict.ContainsKey(PdfName.Get("FontFile2")) ||
                    dict.ContainsKey(PdfName.Get("FontFile3")) || dict.ContainsKey(PdfName.Get("Ascent")))
                {
                    Drop(CharSet);
                    Drop(CidSet);
                }
                Drop(Suspects);
                Drop(NeedsRendering);
                if (dict.ContainsKey(Xfa) && dict.Get(Fields) is PdfArray { Count: > 0 }) Drop(Xfa);
            }
            PdfDictionary? copy = null;
            PdfDictionary Copy()
            {
                if (copy is not null) return copy;
                copy = new PdfDictionary(dict.Count) { Resolver = dict.Resolver };
                foreach (var (key, item) in dict.Entries) copy.Set(key, item);
                return copy;
            }
            if (drop is not null)
                foreach (var key in drop) Copy().Remove(key);
            if (set is { } change) Copy().Set(change.Key, change.Value);
            // direct parts: resources, graphics states, the mark information, an inline form dictionary …
            foreach (var (key, item) in (copy ?? dict).Entries.ToList())
            {
                if (item is not (PdfDictionary or PdfArray) || item is PdfStream) continue;
                var walked = Walk(item, depth + 1);
                if (!ReferenceEquals(walked, item)) Copy().Set(key, walked);
            }
            return copy;
        }
    }

    private static PdfStream WithDictionary(PdfStream stream, PdfDictionary dict) =>
        stream.RawData is { } data ? new PdfStream(dict, data) : new PdfStream(dict, stream.DataOffset, stream.DataLength, stream.Id);
}
