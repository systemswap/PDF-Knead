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
    /// <summary>
    /// The options for a PDF/A file: PDF 1.7 (PDF 2.0 for PDF/A-4; PDF 1.4 for PDF/A-1, which also means a cross-reference table, not streams:
    /// ISO 19005-1, 6.1.4) without encryption (ISO 19005-2, 6.1.3), without thumbnails. Actions,
    /// triggers and embedded files follow the rules of the part as the PDF/A pass writes them (<see cref="PdfATriggers"/>, <see cref="PdfADictionary"/>).
    /// </summary>
    private static PdfSaveOptions ForPdfA(PdfSaveOptions options) => options with
    {
        Version = options.PdfA.Part() switch { 4 => "2.0", 1 => "1.4", _ => "1.7" },
        KeepEncryption = false,
        Protection = null,
        GrantAllPermissions = false,
        Cleanup = options.Cleanup | CleanupItems.Thumbnails,
    };

    private sealed partial class Session
    {
        /// <summary>What the PDF/A pass needs while writing; null for an ordinary file.</summary>
        private PdfAState? _pdfA;

        private sealed class PdfAState(PdfAConformance part)
        {
            public PdfAConformance Part { get; } = part;

            /// <summary>The output intent's profile in the new file and the name of its condition.</summary>
            public PdfObject Profile = PdfNull.Instance;
            public string Condition = CalibratedProfile.SrgbName;
            public string? ConditionInfo;
            public uint ProfileSpace;

            /// <summary>[/ICCBased n] for DeviceCMYK and DeviceRGB, where the output intent does not cover them (null where it does).</summary>
            public PdfArray? Cmyk, Rgb;

            /// <summary>New numbers of objects taken from a source, with where they come from (to look into referenced objects).</summary>
            public Dictionary<int, (Source Source, int Old)> Origin { get; } = [];

            /// <summary>New numbers of appearance streams: they draw content and need resources of their own.</summary>
            public HashSet<int> Appearances { get; } = [];

            /// <summary>
            /// Parts with attachments: whether file specifications written are collected; the number of the array of the catalogue's /AF (0:
            /// none — given only when the catalogue is written and there is something to name, <see cref="AssociatedCatalog"/>); the file
            /// specifications written.
            /// </summary>
            public bool CollectsAssociated;
            public int AssociatedNumber;
            public HashSet<int> Associated { get; } = [];

            /// <summary>New numbers of colour space dictionaries of resources (/ColorSpace as an object of its own).</summary>
            public HashSet<int> ColorSpaces { get; } = [];

            /// <summary>New numbers of resource dictionaries of objects that draw (a page, a form …) kept as objects of their own: they get the
            /// default colour spaces whatever else they hold (keys of no resource category included).</summary>
            public HashSet<int> ResourceDictionaries { get; } = [];

            /// <summary>What the pages use (<see cref="PdfACheck.Used"/>): the codes shown per simple font, content streams to write anew, borrowed
            /// resources, character collections to put right.</summary>
            public PdfACheck.Usage Usage = PdfACheck.Usage.Nothing;

            /// <summary>Per simple font as read: the codes shown with it.</summary>
            public IReadOnlyDictionary<PdfDictionary, bool[]> FontCodes => Usage.FontCodes;

            /// <summary>The output intent's profile as read (source, number, data) where the document's own is kept; null for the own sRGB.</summary>
            public (Source? Source, int Number, byte[] Data)? IntentProfile;

            /// <summary>The information dictionary as written; the XMP metadata repeats it.</summary>
            public PdfDictionary Info = new();

            /// <summary>The font the display draws text with whose font is missing, embedded once (null if no content needs it).</summary>
            public OutputReference? MissingFont;

            /// <summary>
            /// Dictionaries copied from a source into objects of the new file (<see cref="Rebase"/>): the dictionary as read, its source, and the
            /// values of the copy as made (a copy changed since is no longer the dictionary as read).
            /// </summary>
            public Dictionary<PdfDictionary, (PdfDictionary Original, Source Source, PdfObject[] Values)> RebasedFrom { get; } = new(ReferenceEqualityComparer.Instance);

            /// <summary>Level a: the language of the document (a language tag; empty if none).</summary>
            public string Language = "";

            /// <summary>Level a, a structure made for an untagged document: its tree; null where the document has one or level a is not asked.</summary>
            public PdfATagging.Tagged? Tags;

            /// <summary>The number of the new structure tree root (with <see cref="Tags"/>).</summary>
            public int StructureRoot;

            /// <summary>The marks of the pages' content (with <see cref="Tags"/>): by content stream, and by page for content in parts.</summary>
            public Dictionary<PdfStream, PdfATagging.PageMarks> MarksByStream { get; } = new(ReferenceEqualityComparer.Instance);
            public Dictionary<PdfDictionary, PdfATagging.PageMarks> MarksByPage { get; } = new(ReferenceEqualityComparer.Instance);

            /// <summary>The key of each tagged page in the parent tree (its /StructParents), by the page as read.</summary>
            public Dictionary<PdfDictionary, int> PageKeys { get; } = new(ReferenceEqualityComparer.Instance);

            /// <summary>Level a with the document's own structure: the structure types its elements use (for the role map).</summary>
            public HashSet<string>? StructureTypes;

            /// <summary>
            /// Parts 1 to 3, a tree with namespaces (6.5 f): the 1.7 type each name of an element in a namespace leads to (into the role map, as
            /// /NS and /Namespaces go); null where the tree has none.
            /// </summary>
            public Dictionary<string, string>? NamespaceRoles;

            /// <summary>Parts 1 to 3: the MCIDs that become artifact content (Artifact elements of PDF 2.0), by page and by its single content stream.</summary>
            public Dictionary<PdfDictionary, HashSet<int>> ArtifactMcidsByPage { get; } = new(ReferenceEqualityComparer.Instance);

            /// <inheritdoc cref="ArtifactMcidsByPage"/>
            public Dictionary<PdfStream, HashSet<int>> ArtifactMcidsByStream { get; } = new(ReferenceEqualityComparer.Instance);

            /// <summary>
            /// Parts 2 and 3: standard types the source's role map gives another meaning, by their new own name and the source's target
            /// („Table“ meant as a box → „Table_eigen“ mapped to Div): the elements are renamed instead of read literally (docs/FEHLERGEDAECHTNIS.md,
            /// No. 502).
            /// </summary>
            public Dictionary<string, (string Renamed, string Target)>? RenamedTypes;

            /// <summary>Level a with the document's own structure: its elements with their surroundings, under the names they are written with (No. 507).</summary>
            public List<StructureOccurrence>? StructureOccurrences;

            /// <summary>
            /// The children each structure element of the main document loses, by object number: those reached before — an ancestor (a circle)
            /// or an element with a parent already (No. 512). Computed when the first structure element is written.
            /// </summary>
            public Dictionary<int, HashSet<int>>? CutKids;

            /// <summary>
            /// The field tree of the main document as a tree (No. 514): the children each field loses (reached before), and for each field
            /// reached the reference to the field it was reached from (null at the top). Computed when the first field is written.
            /// </summary>
            public (Dictionary<int, HashSet<int>> Cuts, Dictionary<int, PdfReference?> Parents)? FieldTree;
        }

        private static readonly PdfName DefaultCmykKey = PdfName.Get("DefaultCMYK");
        private static readonly PdfName DefaultRgbKey = PdfName.Get("DefaultRGB");
        private static readonly PdfName IccBased = PdfName.Get("ICCBased");
        private static readonly PdfName Rect = PdfName.Get("Rect");
        private static readonly PdfName F = PdfName.Get("F");
        private static readonly PdfName Ap = PdfName.Get("AP");
        private static readonly PdfName Aa = PdfName.Get("AA");
        private static readonly PdfName N = PdfName.Get("N");
        private static readonly PdfName FT = PdfName.Get("FT");

        /// <summary>Resource categories (ISO 32000-2, 7.8.3, Table 34): a dictionary with only these keys is a resource dictionary.</summary>
        private static readonly HashSet<string> ResourceKeys = new(["ExtGState", "ColorSpace", "Pattern", "Shading", "XObject", "Font", "ProcSet", "Properties"],
            StringComparer.Ordinal);

        /// <summary>
        /// Chooses the output intent: a usable one of the document (its profile a display or printer profile of one, three or four
        /// components) is kept, so that DeviceCMYK keeps the colours it had. Otherwise the bundled CMYK profile where the pages draw with
        /// DeviceCMYK — the profile the display draws DeviceCMYK with —, else the own sRGB profile. A CMYK intent lets DeviceCMYK stand, also as
        /// the colour space of a transparency group: an ICC-based CMYK space equal to the blending space is not allowed (PDF/A-4, 6.2.4.2), and
        /// there is none then. Then the default colour spaces for the device spaces the intent does not cover.
        /// </summary>
        private void PreparePdfA()
        {
            var state = _pdfA!;
            state.CollectsAssociated = state.Part.KeepsAttachments();
            var main = _sources[_main];
            if (_main.Catalog?.GetArray(PdfName.Get("OutputIntents")) is { } intents)
                for (var i = 0; i < intents.Count && i < 16; i++)
                {
                    if (intents.Get(i) is not PdfDictionary intent || intent.GetRaw(PdfName.Get("DestOutputProfile")) is not PdfReference profileRef ||
                        _main.GetObject(profileRef.Id.Number) is not PdfStream profile || PdfACheck.UsableProfile(_main.GetStreamData(profile), state.Part.Limits().IccMajor) is not { } space)
                        continue;
                    var profileData = _main.GetStreamData(profile);
                    // its /N as the profile says (PDF/A-1, 6.2.3.2, asks so of every profile; a reader takes the colours from the profile)
                    state.Profile = profile.Dictionary.GetInteger(PdfName.Get("N")) == PdfACheck.IccComponents(profileData)
                        ? new OutputReference(Map(main, profileRef.Id.Number))
                        : Fresh(IccStream(profileData, PdfACheck.IccComponents(profileData) ?? 3));
                    state.IntentProfile = (main, profileRef.Id.Number, profileData);
                    state.ProfileSpace = space;
                    state.Condition = intent.GetString(PdfName.Get("OutputConditionIdentifier"))?.ToText() is { Length: > 0 } condition ? condition : "Custom";
                    state.ConditionInfo = intent.GetString(PdfName.Get("Info"))?.ToText();
                    break;
                }
            // the CMYK profile is large: only where DeviceCMYK is drawn with (default spaces only matter where a device space is used)
            var usage = PdfACheck.Used(_pages.Select(p => (p.Source, p.Page)), _cancel);
            var (rgb, cmyk) = (usage.Rgb, usage.Cmyk);
            if (state.Profile is PdfNull && cmyk)
            {
                var profile = CmykProfile.Bytes();
                state.Profile = Fresh(IccStream(profile, 4));
                state.ProfileSpace = IccProfile.CmykSpace;
                state.Condition = "Custom";
                state.ConditionInfo = CmykProfile.Name;
                state.IntentProfile = (null, -1, profile);
            }
            if (state.Profile is PdfNull)
            {
                state.Profile = Fresh(IccStream(CalibratedProfile.Srgb(), 3));
                state.ProfileSpace = IccProfile.RgbSpace;
            }
            state.Usage = usage;
            NoteLargePages();
            PrepareTagging();
            RebuildFonts();
            // the display's font for text whose font is missing: embedded once, every content that needs it refers to it
            if (usage.Missing.Values.Any(names => names.Any(n => n.Category == "Font")) && PdfAFix(PdfACheck.MissingFont, main, 0) is PdfDictionary missingFont)
            {
                var n = NewNumber();
                _memoryQueue.Enqueue((n, missingFont));
                state.MissingFont = new OutputReference(n);
            }
            if (cmyk && state.ProfileSpace != IccProfile.CmykSpace) state.Cmyk = new PdfArray([IccBased, Fresh(IccStream(CmykProfile.Bytes(), 4))]);
            if (rgb && state.ProfileSpace != IccProfile.RgbSpace) state.Rgb = new PdfArray([IccBased, Fresh(IccStream(CalibratedProfile.Srgb(), 3))]);
        }

        private static PdfStream IccStream(byte[] profile, int components)
        {
            var dict = new PdfDictionary();
            dict.Set(N, PdfInteger.Of(components));
            return new PdfStream(dict, profile);
        }

        /// <summary>
        /// The information dictionary for PDF/A: only the entries that XMP repeats (6.6.2.3.1, Table 3), texts without control characters (XML
        /// cannot hold them), dates in the standard form; the origin records of signatures stay.
        /// </summary>
        private PdfDictionary PdfAInfo(PdfDictionary info)
        {
            var result = new PdfDictionary();
            foreach (var key in (string[])["Title", "Author", "Subject", "Keywords", "Creator", "Producer"])
                if (info.GetString(PdfName.Get(key))?.ToText() is { } text && PdfAMetadata.Clean(text) is { Length: > 0 } clean)
                    result.Set(PdfName.Get(key), new PdfString(PdfTextEncoding.Encode(clean)));
            foreach (var key in (string[])["CreationDate", "ModDate"])
                if (Document.PdfDate.Parse(info.GetString(PdfName.Get(key))?.ToText()) is { } date)
                    result.Set(PdfName.Get(key), new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(date))));
            if (info.Get(SignaturesKey) is PdfString seals) result.Set(SignaturesKey, seals);
            return _pdfA!.Info = result;
        }

        /// <summary>
        /// The links of the main document's structure that make it no tree, by the object number of the parent: walked from the root, a child
        /// element reached before — an ancestor or an element under another parent — is cut there (No. 512). At most 500 000 elements.
        /// </summary>
        private Dictionary<int, HashSet<int>> StructureCuts()
        {
            var cuts = new Dictionary<int, HashSet<int>>();
            if (_main.Catalog?.GetDictionary(StructTreeRoot) is not { } root) return cuts;
            var reached = new HashSet<int>();
            var stack = new Stack<(PdfObject? K, int Parent)>();
            stack.Push((root.GetRaw(PdfName.Get("K")), 0));
            var budget = 500_000;
            while (stack.TryPop(out var item) && --budget > 0)
            {
                var kids = item.K switch
                {
                    PdfArray array => Enumerable.Range(0, Math.Min(array.Count, 100_000)).Select(i => array[i]),
                    null => [],
                    var one => [one],
                };
                foreach (var kid in kids)
                    switch (kid)
                    {
                        case PdfReference r when _main.GetObject(r.Id.Number) is PdfDictionary element && element.ContainsKey(PdfName.Get("S")):
                            if (!reached.Add(r.Id.Number))
                            {
                                if (item.Parent > 0)
                                {
                                    if (!cuts.TryGetValue(item.Parent, out var cut)) cuts[item.Parent] = cut = [];
                                    cut.Add(r.Id.Number);
                                }
                                break;
                            }
                            stack.Push((element.GetRaw(PdfName.Get("K")), r.Id.Number));
                            break;
                        case PdfDictionary direct when direct.ContainsKey(PdfName.Get("S")):
                            // (an element written in its parent has no number of its own: its children are checked, not cut)
                            stack.Push((direct.GetRaw(PdfName.Get("K")), 0));
                            break;
                    }
            }
            return cuts;
        }

        /// <summary>
        /// The field tree of the main document walked from /Fields through /Kids (No. 514): per field the children reached before (cut there),
        /// and per field reached the reference of the field it was reached from — null for the fields at the top. At most 500 000 fields.
        /// </summary>
        private (Dictionary<int, HashSet<int>> Cuts, Dictionary<int, PdfReference?> Parents) FieldTree()
        {
            var cuts = new Dictionary<int, HashSet<int>>();
            var parents = new Dictionary<int, PdfReference?>();
            if (_main.Catalog?.GetDictionary(PdfName.Get("AcroForm"))?.GetRaw(PdfName.Get("Fields")) is not { } fieldsRaw) return (cuts, parents);
            var stack = new Stack<(PdfObject? Kids, PdfReference? Parent)>();
            stack.Push((fieldsRaw is PdfReference fr ? _main.GetObject(fr.Id.Number) : fieldsRaw, null));
            var budget = 500_000;
            while (stack.TryPop(out var item) && --budget > 0)
            {
                if (item.Kids is not PdfArray kids) continue;
                for (var i = 0; i < kids.Count && i < 100_000; i++)
                {
                    if (kids[i] is not PdfReference r || _main.GetObject(r.Id.Number) is not PdfDictionary kid) continue;
                    if (parents.ContainsKey(r.Id.Number))
                    {
                        if (item.Parent is not null)
                        {
                            if (!cuts.TryGetValue(item.Parent.Id.Number, out var cut)) cuts[item.Parent.Id.Number] = cut = [];
                            cut.Add(r.Id.Number);
                        }
                        continue;
                    }
                    parents[r.Id.Number] = item.Parent;
                    var grandKids = kid.GetRaw(PdfName.Get("Kids"));
                    stack.Push((grandKids is PdfReference gr ? _main.GetObject(gr.Id.Number) : grandKids, r));
                }
            }
            return (cuts, parents);
        }

        /// <summary>Declarations of the source a PDF/A-2/3 copy leaves out (<see cref="PdfSaveResult.DeclarationsLeftOut"/>).</summary>
        private readonly List<string> _declarationsLeftOut = [];

        /// <summary>
        /// What the PDF/A packet takes over from the source's XMP (not with „Metadaten bereinigen“; docs/ABLAUFPLAN.md, 6.1 step 7;
        /// docs/FEHLERGEDAECHTNIS.md, No. 614): the properties of the predefined schemas of the part with the value type of their schema — those
        /// the information dictionary repeats only where it has no entry (a title only in the XMP stays); not the dates, the format, the
        /// preview, the instance and the PDF version, which describe the source. The PDF/UA identification, if nothing in this save broke the
        /// accessibility (the version included: PDF/UA-1 not in PDF/A-4, PDF/UA-2 only there) — for PDF/A-1 to -3 with its extension schema.
        /// The declarations in PDF/A-4 (those claiming accessibility only without a break); PDF/A-2/3 leave them out, and that is said.
        /// The properties of schemas the source declares itself in a correct extension schema, with that declaration (No. 863: the invoice data
        /// of a hybrid invoice were lost) — only where the copy keeps the attachments, which such data often describe.
        /// </summary>
        private (List<string> Carried, bool PdfUaExtension, string? Declarations) PdfACarried()
        {
            var state = _pdfA!;
            if (_options.Cleanup.HasFlag(CleanupItems.Metadata) || _main.Catalog?.GetRaw(PdfName.Metadata) is not PdfReference reference ||
                _main.GetObject(reference.Id.Number) is not PdfStream stream || stream.DataLength > 16 << 20) return ([], false, null);
            byte[] packet;
            try
            {
                packet = _main.GetStreamData(stream);
            }
            catch (PdfException)
            {
                return ([], false, null);
            }
            if (PdfAXmp.Read(packet) is not { } xml) return ([], false, null);
            const string dc = "http://purl.org/dc/elements/1.1/", xmpNs = "http://ns.adobe.com/xap/1.0/", pdfNs = "http://ns.adobe.com/pdf/1.3/";
            bool Has(string key) => state.Info.GetString(PdfName.Get(key)) is not null;
            bool Skip(string ns, string name) => (ns, name) switch
            {
                (dc, "title") => Has("Title"),
                (dc, "creator") => Has("Author"),
                (dc, "description") => Has("Subject"),
                (pdfNs, "Keywords") => Has("Keywords"),
                (pdfNs, "Producer") => Has("Producer"),
                (xmpNs, "CreatorTool") => Has("Creator"),
                (xmpNs, "CreateDate") => Has("CreationDate"),
                (dc, "format") or (xmpNs, "ModifyDate") or (xmpNs, "MetadataDate") or (xmpNs, "Thumbnails") or (pdfNs, "PDFVersion") => true,
                ("http://ns.adobe.com/xap/1.0/mm/", "InstanceID") => true,
                _ => false,
            };
            var carried = PdfAXmp.Carryable(xml, state.Part, Skip);
            // declared properties of other software often describe the attached files (the invoice data of a hybrid invoice): only where the
            // copy keeps its attachments — PDF/A-1, -2 and -4 drop them, and the data would name a file no longer there (No. 863)
            var (declared, declarations) = state.Part.KeepsAttachments() && !_options.Cleanup.HasFlag(CleanupItems.Attachments)
                                           && !(_options.PartOfDocument && !_options.KeepDocumentAttachments)
                ? PdfAXmp.CarryableDeclared(xml, state.Part)
                : ([], null);
            carried.AddRange(declared);
            var four = state.Part.Part() == 4;
            var broken = _accessibilityBreaks.Count > 0;
            var claims = AccessibilityClaims.Read(packet);
            var pdfUa = false;
            if (claims?.PdfUaPart is not null)
            {
                if (broken) _accessibilityClaimRemoved = true;
                else
                {
                    carried.AddRange(PdfAXmp.PropertiesOf(xml, AccessibilityClaims.PdfUaId));
                    pdfUa = !four;
                }
            }
            if (claims?.ConformsTo is { Count: > 0 } uris)
            {
                if (broken && uris.Any(AccessibilityClaims.IsAccessibility)) _accessibilityClaimRemoved = true;
                var kept = broken ? uris.Where(u => !AccessibilityClaims.IsAccessibility(u)).ToList() : [.. uris];
                if (!four) _declarationsLeftOut.AddRange(kept);
                else if (kept.Count > 0 && (broken ? AccessibilityClaims.Remove(packet, out _) : packet) is { } left && PdfAXmp.Read(left) is { } leftXml)
                    carried.AddRange(PdfAXmp.PropertiesOf(leftXml, AccessibilityClaims.Declarations));
            }
            _pdfAMetadataLanguages = PdfAXmpLanguages(xml);
            return (carried, pdfUa, declarations);
        }

        /// <summary>The source's further language versions of dc:title and dc:description, for <see cref="PdfAMetadata.Build"/> (No. 677).</summary>
        private Dictionary<string, IReadOnlyList<(string Language, string Text)>>? _pdfAMetadataLanguages;

        /// <summary>The rdf:li of dc:title and dc:description with a language other than x-default (a few each).</summary>
        private static Dictionary<string, IReadOnlyList<(string Language, string Text)>> PdfAXmpLanguages(System.Xml.XmlDocument xml)
        {
            const string dc = "http://purl.org/dc/elements/1.1/", rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            var result = new Dictionary<string, IReadOnlyList<(string Language, string Text)>>(StringComparer.Ordinal);
            foreach (var property in (ReadOnlySpan<string>)["title", "description"])
            {
                var list = new List<(string, string)>();
                foreach (System.Xml.XmlElement element in xml.GetElementsByTagName(property, dc))
                    foreach (System.Xml.XmlElement li in element.GetElementsByTagName("li", rdf))
                    {
                        var lang = li.GetAttribute("xml:lang");
                        if (lang.Length == 0 || lang.Equals("x-default", StringComparison.OrdinalIgnoreCase) || !Accessibility.LanguageTags.Valid(lang) ||
                            list.Any(l => l.Item1.Equals(lang, StringComparison.OrdinalIgnoreCase)) || list.Count >= 16)
                            continue;
                        list.Add((lang, li.InnerText));
                    }
                if (list.Count > 0) result[property] = list;
            }
            return result;
        }

        /// <summary>The catalogue for PDF/A: output intent, XMP metadata, no document-level actions or permissions; layers named.</summary>
        private PdfDictionary PdfACatalog(PdfDictionary catalog)
        {
            var state = _pdfA!;
            catalog = (PdfDictionary)PdfAFix(catalog, null, 0);
            // the opening action as read (here it refers to the new file already, where a page can stand in for it): only one the part allows
            // stays (6.5.1; a destination stays as well)
            if (catalog.ContainsKey(OpenAction) && _main.Catalog?.Get(OpenAction) is PdfDictionary opening && !KeptAction(opening, _sources[_main]))
                catalog.Remove(OpenAction);
            // The permissions of a signature (DocMDP, UR3) belong to bytes this copy no longer has
            catalog.Remove(PdfName.Get("Perms"));
            catalog.Remove(NeedsRendering);
            // no requirements of a reader (6.11; PDF/A-4: 6.12)
            catalog.Remove(PdfName.Get("Requirements"));
            if (state.Part.Part() == 4)
            {
                // PDF/A-4 (6.1.12): a version in the catalogue is one of PDF 2. The file has no information dictionary: the origin records of
                // signatures move to the catalogue (SealRecord.Read looks there too)
                if (catalog.GetName(PdfName.Get("Version"))?.Value is { } version && !version.StartsWith("2.", StringComparison.Ordinal)) catalog.Remove(PdfName.Get("Version"));
                if (state.Info.Get(SignaturesKey) is PdfString seals) catalog.Set(SignaturesKey, seals);
            }
            var intent = new PdfDictionary();
            intent.Set(PdfName.Type, PdfName.Get("OutputIntent"));
            intent.Set(S, PdfName.Get("GTS_PDFA1"));
            intent.Set(PdfName.Get("OutputConditionIdentifier"), new PdfString(PdfTextEncoding.Encode(state.Condition)));
            if (state.ConditionInfo is { Length: > 0 } info) intent.Set(PdfName.Get("Info"), new PdfString(PdfTextEncoding.Encode(info)));
            intent.Set(PdfName.Get("DestOutputProfile"), state.Profile);
            catalog.Set(PdfName.Get("OutputIntents"), new PdfArray([intent]));
            var xmp = new PdfDictionary();
            xmp.Set(PdfName.Type, PdfName.Metadata);
            xmp.Set(PdfName.Subtype, PdfName.Get("XML"));
            var (carried, pdfUaExtension, declarations) = PdfACarried();
            catalog.Set(PdfName.Metadata, Fresh(new PdfStream(xmp, PdfAMetadata.Build(state.Part, state.Info, carried, pdfUaExtension, _pdfAMetadataLanguages,
                declarations))));
            if (catalog.Get(AcroForm) is PdfDictionary form && (form.ContainsKey(NeedAppearances) || form.ContainsKey(Xfa)))
            {
                form = Clone(form);
                form.Remove(NeedAppearances);
                form.Remove(Xfa);
                catalog.Set(AcroForm, form);
            }
            if (catalog.Get(OCProperties) is PdfDictionary layers) catalog.Set(OCProperties, NamedLayerConfigurations(layers));
            // the scripts of the document in a name dictionary written into the catalogue (one of its own object: PdfADictionary), and in PDF/A-1
            // the list of attachments (6.1.11: it allows none)
            if (catalog.GetRaw(PdfName.Get("Names")) is PdfDictionary names && NamesNotAllowed(names, state.Part) is { Count: > 0 } notAllowed)
            {
                names = Clone(names);
                foreach (var key in notAllowed) names.Remove(key);
                catalog.Set(PdfName.Get("Names"), names);
            }
            // parts with attachments: every file specification written is associated with the document (PDF/A-3, 6.8), listed in an array
            // written last (WriteAssociated); whether there is one is known when the catalogue is written (AssociatedCatalog)
            catalog.Remove(PdfName.Get("AF"));
            PdfATaggedCatalog(catalog);
            return catalog;
        }

        /// <summary>Every layer configuration with a name of its own and without /AS (ISO 19005-2, 6.9).</summary>
        private PdfDictionary NamedLayerConfigurations(PdfDictionary layers)
        {
            layers = Clone(layers);
            var names = new HashSet<string>(StringComparer.Ordinal);
            // The lists of layers hold layers only (ISO 32000-2, 8.11.4.2 and 8.11.4.3): a membership dictionary (/Type /OCMD) some producers put
            // into /OCGs and /Order counted for a reference validator as a layer without a name missing in the order — and the completion of
            // the order appended it once more (ISO 19005-2, 6.9; docs/FEHLERGEDAECHTNIS.md, No. 510). /Order keeps its labels and sub-lists.
            PdfArray? OnlyLayers(PdfObject? raw, bool order, int depth)
            {
                if (raw is null || depth > 32 || Look(raw, null) is not (PdfArray list, var from)) return null;
                var kept = new PdfArray();
                for (var i = 0; i < list.Count && i < 100_000; i++)
                    switch (Look(list[i], from))
                    {
                        case (PdfDictionary other, _) when other.GetName(PdfName.Type)?.Value is { } type && type != "OCG":
                            break;
                        case (PdfArray, _) when order:
                            if (OnlyLayers(list[i], true, depth + 1) is { } inner) kept.Add(inner);
                            break;
                        default:
                            kept.Add(Portable(list[i], from, null));
                            break;
                    }
                return kept;
            }
            if (OnlyLayers(layers.GetRaw(PdfName.Get("OCGs")), false, 0) is { } groups) layers.Set(PdfName.Get("OCGs"), groups);
            PdfDictionary Named(PdfDictionary config, string fallback)
            {
                config = Clone(config);
                config.Remove(PdfName.Get("AS"));
                foreach (var key in (ReadOnlySpan<string>)["Order", "ON", "OFF", "Locked"])
                    if (OnlyLayers(config.GetRaw(PdfName.Get(key)), key == "Order", 0) is { } layersOnly) config.Set(PdfName.Get(key), layersOnly);
                var name = config.GetString(PdfName.Get("Name"))?.ToText();
                if (string.IsNullOrEmpty(name) || !names.Add(name))
                {
                    var n = 1;
                    name = fallback;
                    while (!names.Add(name)) name = $"{fallback} {++n}";
                    config.Set(PdfName.Get("Name"), new PdfString(PdfTextEncoding.Encode(name)));
                }
                return config;
            }
            if (layers.GetRaw(D) is { } standardRaw && PdfAResolve(standardRaw, null) is PdfDictionary standard)
                layers.Set(D, Named(Direct(standard, standardRaw, null, null), Tr.T("Standard")));
            if (layers.GetRaw(PdfName.Get("Configs")) is { } configsRaw && Look(configsRaw, null) is (PdfArray configs, var from))
            {
                var named = new PdfArray();
                for (var i = 0; i < configs.Count && i < 1000; i++)
                    if (PdfAResolve(configs[i], from) is PdfDictionary config) named.Add(Named(Direct(config, configs[i], from, null), Tr.T("Ansicht")));
                layers.Set(PdfName.Get("Configs"), named);
            }
            // one configuration given as a dictionary instead of an array of them (No. 510)
            else if (layers.GetRaw(PdfName.Get("Configs")) is { } singleRaw && Look(singleRaw, null) is (PdfDictionary single, var singleFrom))
                layers.Set(PdfName.Get("Configs"), new PdfArray([Named(Direct(single, singleRaw, singleFrom, null), Tr.T("Ansicht"))]));
            return layers;
        }

        /// <summary>Whether an action stays in the copy: one the part allows (<see cref="PdfACheck.AllowedAction"/>) that still leads somewhere.</summary>
        private bool KeptAction(PdfDictionary action, Source? source) => PdfACheck.AllowedAction(action, _pdfA!.Part) && !GoesToDroppedAttachment(action, source);

        /// <summary>
        /// Whether an action goes to an attachment of its document that the copy does not keep — a GoToE whose target is a child named in
        /// /Names /EmbeddedFiles (ISO 32000-2, 12.6.4.4): with the file gone it would name nothing (docs/FEHLERGEDAECHTNIS.md, No. 369).
        /// </summary>
        private bool GoesToDroppedAttachment(PdfDictionary action, Source? source)
        {
            if (_pdfA!.Part.KeepsAttachments() || action.GetName(S)?.Value != "GoToE" || source?.Doc.Catalog is not { } catalog) return false;
            if (action.Get(PdfName.Get("T")) is not PdfDictionary target || target.GetName(PdfName.Get("R"))?.Value != "C" ||
                target.Get(PdfName.Get("N")) is not PdfString name)
                return false;
            if (catalog.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("EmbeddedFiles")) is not { } tree) return false;
            return Document.PdfOutline.LookupNameTree(tree, System.Text.Encoding.Latin1.GetString(name.Bytes)) is PdfDictionary spec &&
                   PdfACheck.IsFileSpec(spec) && !AllowedAttachments(spec, source);
        }

        /// <summary>The cache of <see cref="AllowedAttachments"/>, by the embedded file as read.</summary>
        private readonly Dictionary<PdfStream, bool> _allowedAttachments = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Whether every embedded file of a specification (/EF) is an attachment the part allows (<see cref="PdfACheck.AllowedAttachment"/>,
        /// the same decision as the check's).
        /// </summary>
        private bool AllowedAttachments(PdfDictionary spec, Source? source)
        {
            // PDF/A-1 allows none (6.1.11)
            if (_pdfA!.Part.Part() == 1) return false;
            if (spec.GetRaw(PdfName.Get("EF")) is not { } raw || Look(raw, source) is not (PdfDictionary files, var from) || from is null) return false;
            foreach (var (_, value) in files.Entries)
            {
                if (Look(value, from) is not (PdfStream file, var fileFrom) || fileFrom is null) return false;
                if (!_allowedAttachments.TryGetValue(file, out var allowed))
                    _allowedAttachments[file] = allowed = PdfACheck.AllowedAttachment(EmbeddedData(file, fileFrom), _pdfA!.Part);
                if (!allowed) return false;
            }
            return true;
        }

        private readonly Dictionary<Source, HashSet<PdfStream>> _embeddedFiles = [];
        private readonly Dictionary<Source, HashSet<PdfStream>> _relatedFiles = [];

        /// <summary>
        /// The streams of the related-files arrays (/RF) of the file specifications of a source: embedded file streams too, encrypted with
        /// the method of the embedded files (ISO 32000-2, 7.6.6: /RF uses the filter of /EF) — found by the same walk as
        /// <see cref="EmbeddedFiles"/> (docs/FEHLERGEDAECHTNIS.md, No. 1100).
        /// </summary>
        private HashSet<PdfStream> RelatedFiles(Source source)
        {
            if (!_relatedFiles.ContainsKey(source)) EmbeddedFiles(source);
            return _relatedFiles[source];
        }

        /// <summary>
        /// The embedded files of a source: every stream an /EF of a file specification names (/Type /EmbeddedFile is optional, so the
        /// specifications decide) — found once per source, following every reference from its catalogue.
        /// </summary>
        private HashSet<PdfStream> EmbeddedFiles(Source source)
        {
            if (_embeddedFiles.TryGetValue(source, out var known)) return known;
            var found = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
            var related = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<int>();
            var seen = new HashSet<int>();
            void Walk(PdfObject obj, int depth)
            {
                if (depth > 64) return;
                switch (obj)
                {
                    case PdfReference r:
                        if (seen.Count < 8_388_608 && seen.Add(r.Id.Number)) queue.Enqueue(r.Id.Number);
                        return;
                    case PdfStream stream:
                        Walk(stream.Dictionary, depth + 1);
                        return;
                    case PdfArray array:
                        for (var i = 0; i < array.Count; i++) Walk(array[i], depth + 1);
                        return;
                    case PdfDictionary dict:
                        if (PdfACheck.IsFileSpec(dict) && dict.GetDictionary(PdfName.Get("EF")) is { } files)
                            foreach (var (key, _) in files.Entries)
                                if (files.Get(key) is PdfStream file) found.Add(file);
                        if (PdfACheck.IsFileSpec(dict) && dict.GetDictionary(PdfName.Get("RF")) is { } relatedFiles)
                            foreach (var (key, _) in relatedFiles.Entries)
                                if (relatedFiles.Get(key) is PdfArray list)
                                    for (var i = 0; i < list.Count; i++)
                                        if (list.Get(i) is PdfStream file) related.Add(file);
                        foreach (var (_, value) in dict.Entries) Walk(value, depth + 1);
                        return;
                }
            }
            if (source.Doc.Trailer.GetRaw(PdfName.Root) is { } root) Walk(root, 0);
            while (queue.TryDequeue(out var number))
            {
                _cancel.ThrowIfCancellationRequested();
                try
                {
                    Walk(source.Doc.GetObject(number), 0);
                }
                catch (PdfException)
                {
                    // an object that cannot be read holds no file specification
                }
            }
            _relatedFiles[source] = related;
            return _embeddedFiles[source] = found;
        }

        /// <summary>The decoded data of an embedded file as read; empty if it cannot be read.</summary>
        private static byte[] EmbeddedData(PdfStream file, Source source)
        {
            try
            {
                return source.Doc.DecodeStream(file).Data;
            }
            catch (PdfException)
            {
                return [];
            }
        }

        /// <summary>
        /// The catalogue of a part with attachments as it is written last: with /AF naming the array of the file specifications only when one
        /// was written or can still be reached from the catalogue (the attachments of its name tree are written after it) — an empty /AF
        /// (ISO 32000-2, Table 29: one or more) was written into every copy without attachments (docs/FEHLERGEDAECHTNIS.md, No. 688).
        /// </summary>
        private void AssociatedCatalog(PdfDictionary catalog)
        {
            if (_pdfA is not { CollectsAssociated: true } state) return;
            if (state.Associated.Count == 0 && !ReachesFileSpec(catalog)) return;
            state.AssociatedNumber = NewNumber();
            catalog.Set(PdfName.Get("AF"), new OutputReference(state.AssociatedNumber));
        }

        /// <summary>
        /// Whether a file specification can be reached from the catalogue of the main file, not through the pages (written before, their file
        /// specifications collected): its name tree, outline, form, structure … At most 200 000 objects.
        /// </summary>
        private bool ReachesFileSpec(PdfDictionary catalog)
        {
            var seen = new HashSet<int>();
            var stack = new Stack<PdfObject>([catalog]);
            var budget = 200_000;
            while (stack.TryPop(out var item) && --budget > 0)
            {
                if (item is PdfReference r)
                {
                    if (!seen.Add(r.Id.Number)) continue;
                    item = _main.Resolve(r.Id) ?? PdfNull.Instance;
                }
                switch (item)
                {
                    case PdfDictionary d when d.IsType(PdfName.Get("Page")):
                        break;   // a page named by a destination: written before
                    case PdfDictionary d:
                        if (PdfACheck.IsFileSpec(d)) return true;
                        // not back to the pages (the page tree, a page named by an element, an annotation or a destination's parent)
                        foreach (var (key, value) in d.Entries)
                            if (key.Value is not ("Pages" or "Parent" or "P" or "Pg")) stack.Push(value);
                        break;
                    case PdfArray a:
                        for (var i = 0; i < a.Count; i++) stack.Push(a[i]);
                        break;
                }
            }
            return budget <= 0;   // not looked through: as if there were one
        }

        /// <summary>PDF/A-3 and PDF/A-4f: the array of the catalogue's /AF, with every file specification written (<see cref="AssociatedCatalog"/>).</summary>
        private void WriteAssociated()
        {
            if (_pdfA is not { AssociatedNumber: > 0 } state) return;
            WriteObject(state.AssociatedNumber, new PdfArray(state.Associated.Order().Select(n => (PdfObject)new OutputReference(n))), null);
        }

        /// <summary>
        /// A direct copy of a dictionary that was reached through <paramref name="raw"/> (in an object of <paramref name="rawSource"/>), for an
        /// object written with <paramref name="target"/>.
        /// </summary>
        private PdfDictionary Direct(PdfDictionary dict, PdfObject raw, Source? rawSource, Source? target)
        {
            var from = Look(raw, rawSource).From;
            return from is null || ReferenceEquals(from, target) ? CloneWithResolver(dict) : (PdfDictionary)Rebase(dict, from);
        }

        /// <summary>
        /// Looks through a reference: of a source (in an object written with <paramref name="source"/>), or of the new file for objects taken
        /// from a source. Returns the object and the source whose numbering the references in it use; the object is null for objects that exist
        /// only in memory.
        /// </summary>
        private (PdfObject? Obj, Source? From) Look(PdfObject raw, Source? source) => raw switch
        {
            PdfReference r when source is not null =>
                (source.Overrides.TryGetValue(r.Id.Number, out var over) ? over : source.Doc.GetObject(r.Id.Number), source),
            OutputReference o when _pdfA!.Origin.TryGetValue(o.Number, out var at) =>
                (at.Source.Overrides.TryGetValue(at.Old, out var replaced) ? replaced : at.Source.Doc.GetObject(at.Old), at.Source),
            PdfReference or OutputReference => (null, null),
            _ => (raw, source),
        };

        private PdfObject? PdfAResolve(PdfObject raw, Source? source) => Look(raw, source).Obj;

        /// <summary>A value read inside an object of <paramref name="from"/>, for an object written with <paramref name="target"/>.</summary>
        private PdfObject Portable(PdfObject value, Source? from, Source? target) =>
            ReferenceEquals(from, target) || from is null ? value : Rebase(value, from);

        /// <summary>The number in the new file of a reference in an object of <paramref name="source"/>; 0 if there is none.</summary>
        private int NumberOf(PdfObject value, Source? source) => value switch
        {
            OutputReference o => o.Number,
            PdfReference r when source is not null => Map(source, r.Id.Number),
            _ => 0,
        };

        /// <summary>A copy (<see cref="Rebase"/>) that still has the values it was made with, in the same order.</summary>
        private static bool AsCopied(PdfDictionary copy, PdfObject[] values)
        {
            if (copy.Count != values.Length) return false;
            var i = 0;
            foreach (var (_, value) in copy.Entries)
                if (!ReferenceEquals(value, values[i++])) return false;
            return true;
        }

        /// <summary>The most entries a list of a tree gets when it is split (an even number: pairs stay together).</summary>
        private const int ListChunk = 8000;

        /// <summary>
        /// PDF/A-1 allows lists of at most 8 191 entries (ISO 19005-1, 6.1.12). Lists that are a part of a tree split without changing what they
        /// say: the pairs of a number or name tree node (/Nums, /Names) go into children of their own with their limits (ISO 32000-1, 7.9.6,
        /// 7.9.7), the structure elements among the children of a structure element (/K) into elements of type NonStruct, which group without
        /// meaning (14.8.4.2) — marked content and object references stay where they are, so the parent tree still names their element. Other
        /// lists stay as they are (the check names them).
        /// </summary>
        /// <param name="dict">The dictionary as it is written (values of <paramref name="source"/>, or of the new file).</param>
        /// <param name="source">Its source; null if its values are those of the new file.</param>
        /// <param name="number">Its number in the new file; 0 if it is written into another object.</param>
        /// <returns>The dictionary, or a copy with lists split.</returns>
        private PdfDictionary WithinListLimit(PdfDictionary dict, Source? source, int number)
        {
            foreach (var key in (ReadOnlySpan<string>)["Nums", "Names"])
            {
                var name = PdfName.Get(key);
                if (dict.GetRaw(name) is not PdfArray { Count: > 8191 } pairs || dict.ContainsKey(PdfName.Kids)) continue;
                var kids = new PdfArray();
                for (var start = 0; start + 1 < pairs.Count; start += ListChunk)
                {
                    var chunk = new PdfArray { Resolver = pairs.Resolver };
                    for (var i = start; i < Math.Min(pairs.Count & ~1, start + ListChunk); i++) chunk.Add(pairs[i]);
                    var kid = new PdfDictionary();
                    kid.Set(name, chunk);
                    kid.Set(PdfName.Get("Limits"), new PdfArray([chunk[0], chunk[^2]]));
                    kids.Add(Queued(kid, source));
                }
                var split = CloneWithResolver(dict);
                split.Remove(name);
                split.Set(PdfName.Kids, kids);
                dict = split;
            }
            if (number > 0 && source is not null && dict.GetRaw(PdfName.Get("K")) is PdfArray { Count: > 8191 } children &&
                (dict.IsType(PdfName.Get("StructElem")) || dict.IsType(PdfName.Get("StructTreeRoot"))))
            {
                var grouped = new PdfArray();
                var run = new List<PdfReference>();
                void Flush()
                {
                    for (var start = 0; start < run.Count; start += ListChunk)
                    {
                        var group = NewNumber();
                        var element = new PdfDictionary();
                        element.Set(PdfName.Type, PdfName.Get("StructElem"));
                        element.Set(PdfName.Get("S"), PdfName.Get("NonStruct"));
                        element.Set(PdfName.Get("P"), new OutputReference(number));
                        if (dict.GetRaw(PdfName.Get("Pg")) is { } page) element.Set(PdfName.Get("Pg"), Rebase(page, source));
                        var members = new PdfArray();
                        foreach (var member in run.Skip(start).Take(ListChunk))
                        {
                            members.Add(Rebase(member, source));
                            // the member's parent is now the group (written when it is dequeued)
                            if ((source.Overrides.GetValueOrDefault(member.Id.Number) ?? source.Doc.GetObject(member.Id.Number)) is PdfDictionary read)
                            {
                                var moved = CloneWithResolver(read);
                                moved.Set(PdfName.Get("P"), new OutputReference(group));
                                source.Overrides[member.Id.Number] = moved;
                            }
                        }
                        element.Set(PdfName.Get("K"), members);
                        _memoryQueue.Enqueue((group, element));
                        grouped.Add(new OutputReference(group));
                    }
                    run.Clear();
                }
                for (var i = 0; i < children.Count; i++)
                {
                    if (children[i] is PdfReference reference && source.Doc.GetObject(reference.Id.Number) is PdfDictionary child &&
                        (child.IsType(PdfName.Get("StructElem")) || child.ContainsKey(PdfName.Get("S"))))
                    {
                        run.Add(reference);
                        continue;
                    }
                    Flush();
                    grouped.Add(children[i]);
                }
                Flush();
                var regrouped = CloneWithResolver(dict);
                regrouped.Set(PdfName.Get("K"), grouped);
                dict = regrouped;
            }
            return dict;
        }

        /// <summary>A dictionary as an object of its own in the new file (its values those of <paramref name="source"/>, or of the new file).</summary>
        private OutputReference Queued(PdfDictionary dict, Source? source)
        {
            var own = NewNumber();
            _memoryQueue.Enqueue((own, source is null ? dict : (PdfDictionary)Rebase(dict, source)));
            return new OutputReference(own);
        }

        /// <summary>
        /// Brings an object in line with PDF/A where that keeps the appearance (KONZEPT.md, section 5.21), together with the dictionaries and
        /// arrays directly in it. Objects of a source are never changed: a changed dictionary is a copy.
        /// </summary>
        /// <param name="obj">The object as it is about to be written.</param>
        /// <param name="source">The source whose numbering its references use; null for references of the new file.</param>
        /// <param name="number">Its number in the new file; 0 for objects inside another.</param>
        private PdfObject PdfAFix(PdfObject obj, Source? source, int number, int depth = 0)
        {
            if (depth > 32) return obj;
            switch (obj)
            {
                case PdfArray array:
                {
                    // a DeviceN space with more colourants than the part allows: its alternate space (the content changes its values)
                    if (WithoutWideSpace(array, source) is { } narrowed) return narrowed;
                    // a destination to a page whose unit grew: its coordinates shrink alike
                    if (ShrunkDestination(array, source) is { } destination) array = destination;
                    PdfArray? copy = null;
                    for (var i = 0; i < array.Count; i++)
                    {
                        var fixedItem = array[i] is PdfDictionary or PdfArray ? PdfAFix(array[i], source, 0, depth + 1) : PdfANumber(array[i]);
                        if (copy is null && ReferenceEquals(fixedItem, array[i])) continue;
                        if (copy is null)
                        {
                            copy = new PdfArray { Resolver = array.Resolver };
                            for (var k = 0; k < i; k++) copy.Add(array[k]);
                        }
                        copy.Add(fixedItem);
                    }
                    // colour spaces: profiles as the display reads them, names in UTF-8, same names alike, colourants complete, device bases covered
                    var icc = IccSpace(copy ?? array, source);
                    if (icc is not PdfArray space) return icc;
                    return DeviceBase(CompleteColorants(AlikeColorants(Utf8Colorants(space, source), source), source));
                }
                // a dictionary of a source copied into an object of the new file (the dictionaries of a page, its resources): brought in line
                // as it is read — the pages were measured by those (fonts shown, resources borrowed …) —, then copied as before
                case PdfDictionary copied when source is null && _pdfA!.RebasedFrom.TryGetValue(copied, out var origin) && AsCopied(copied, origin.Values):
                {
                    var inLine = PdfAFix(origin.Original, origin.Source, number, depth);
                    return ReferenceEquals(inLine, origin.Original) ? copied : Rebase(inLine, origin.Source);
                }
                case PdfDictionary read:
                {
                    // a font rebuilt for its shown text (RebuildFonts) is written instead, and brought in line like any other
                    var dict = _rebuiltFonts.TryGetValue(read, out var rebuilt) ? rebuilt : read;
                    PdfDictionary? copy = null;
                    PdfDictionary Writable() => copy ??= CloneWithResolver(dict);
                    foreach (var (key, value) in dict.Entries)
                        if (value is PdfDictionary or PdfArray && PdfAFix(value, source, 0, depth + 1) is var child && !ReferenceEquals(child, value))
                            Writable().Set(key, child);
                        else if (value is PdfInteger or PdfReal && PdfANumber(value) is var limited && !ReferenceEquals(limited, value))
                            Writable().Set(key, limited);
                    // the structure is a tree (ISO 32000-2, 14.7.2): a child reached before is left out of /K — a circle in a source made a
                    // reference validator overflow its stack, and every reader that follows /K runs in a circle (docs/FEHLERGEDAECHTNIS.md, No. 512)
                    if (number > 0 && dict.ContainsKey(PdfName.Get("S")) && dict.ContainsKey(PdfName.Get("K")) && _pdfA!.Origin.TryGetValue(number, out var element) &&
                        ReferenceEquals(element.Source.Doc, _main) && (_pdfA.CutKids ??= StructureCuts()).TryGetValue(element.Old, out var cut))
                        switch ((copy ?? dict).GetRaw(PdfName.Get("K")))
                        {
                            case PdfArray kids:
                                var kept = new PdfArray { Resolver = kids.Resolver };
                                for (var i = 0; i < kids.Count; i++)
                                    if (kids[i] is not PdfReference r || !cut.Contains(r.Id.Number)) kept.Add(kids[i]);
                                Writable().Set(PdfName.Get("K"), kept);
                                break;
                            case PdfReference single when cut.Contains(single.Id.Number):
                                Writable().Remove(PdfName.Get("K"));
                                break;
                        }
                    // a Type 3 font: its own resources without the font itself (no glyph can draw with the font it belongs to; a reference
                    // validator followed the circle until its stack overflowed), its bounding box numbers only (ISO 32000-2, 9.6.4 and Table 110;
                    // docs/FEHLERGEDAECHTNIS.md, No. 515)
                    if (number > 0 && dict.GetName(PdfName.Subtype)?.Value == "Type3" && _pdfA!.Origin.TryGetValue(number, out var type3))
                    {
                        if ((copy ?? dict).Get(PdfName.Resources) is PdfDictionary own && own.Get(PdfName.Get("Font")) is PdfDictionary ownFonts &&
                            ownFonts.Entries.Where(e => ownFonts.GetRaw(e.Key) is PdfReference r && r.Id.Number == type3.Old).Select(e => e.Key).ToList() is { Count: > 0 } selves)
                        {
                            var fonts = CloneWithResolver(ownFonts);
                            foreach (var key in selves) fonts.Remove(key);
                            var resources = CloneWithResolver(own);
                            resources.Set(PdfName.Get("Font"), fonts);
                            Writable().Set(PdfName.Resources, resources);
                        }
                        if ((copy ?? dict).Get(PdfName.Get("FontBBox")) is PdfArray box && Enumerable.Range(0, box.Count).Any(i => box.Get(i) is not (PdfInteger or PdfReal)))
                            Writable().Set(PdfName.Get("FontBBox"), new PdfArray([.. Enumerable.Range(0, 4).Select(i =>
                                i < box.Count && box.Get(i) is PdfInteger or PdfReal ? box.Get(i)! : PdfInteger.Of(0))]));
                    }
                    // 3D artwork in DeviceRGB (ISO 32000-2, Table 311) where the copy's output intent is no RGB one: the copy's sRGB profile,
                    // as for DeviceRGB in content (/DefaultRGB) — the artwork has no resources (docs/FEHLERGEDAECHTNIS.md, No. 518)
                    if (dict.GetName(PdfName.Type)?.Value == "3D" && dict.Get(PdfName.Get("ColorSpace")) is PdfName { Value: "DeviceRGB" } &&
                        _pdfA!.Part.Part() == 4 && _pdfA.ProfileSpace != IccProfile.RgbSpace)
                        Writable().Set(PdfName.Get("ColorSpace"), _pdfA.Rgb ??= new PdfArray([IccBased, Fresh(IccStream(CalibratedProfile.Srgb(), 3))]));
                    // the field hierarchy is a tree as well (ISO 32000-2, 12.7.4): no child reached before in /Kids, /Parent the field it is
                    // reached from — a field its own child or parent made a reference validator give up (docs/FEHLERGEDAECHTNIS.md, No. 514)
                    if (number > 0 && (dict.ContainsKey(PdfName.Get("Kids")) || dict.ContainsKey(PdfName.Get("Parent"))) &&
                        _pdfA!.Origin.TryGetValue(number, out var field) && ReferenceEquals(field.Source.Doc, _main))
                    {
                        var (fieldCuts, fieldParents) = _pdfA.FieldTree ??= FieldTree();
                        if (fieldCuts.TryGetValue(field.Old, out var cutFields) && (copy ?? dict).GetRaw(PdfName.Get("Kids")) is PdfArray fieldKids)
                        {
                            var keptFields = new PdfArray { Resolver = fieldKids.Resolver };
                            for (var i = 0; i < fieldKids.Count; i++)
                                if (fieldKids[i] is not PdfReference r || !cutFields.Contains(r.Id.Number)) keptFields.Add(fieldKids[i]);
                            Writable().Set(PdfName.Get("Kids"), keptFields);
                        }
                        if (fieldParents.TryGetValue(field.Old, out var parentRef))
                        {
                            // (only a /Parent of the source: one the writer set itself — same-named fields linked under a new field, No. 337 — stays)
                            var written = (copy ?? dict).GetRaw(PdfName.Get("Parent"));
                            if (parentRef is null && written is PdfReference) Writable().Remove(PdfName.Get("Parent"));
                            else if (parentRef is not null && written is PdfReference or null && (written as PdfReference)?.Id.Number != parentRef.Id.Number)
                                Writable().Set(PdfName.Get("Parent"), parentRef);
                        }
                    }
                    var current = copy ?? dict;
                    var result = PdfADictionary(current, source, number, depth, read) ?? current;
                    if (_pdfA!.Part.Part() == 1) result = WithinListLimit(result, source, number);
                    if (WithoutWideShading(result, read, source) is { } shading) result = shading;
                    if (AnnotationUnit(number) is > 0 and var unit) result = ShrunkAnnotation(result, unit);
                    if (_pdfA.Part.Accessible())
                    {
                        // level a: language tags, answers for figures, the role map of the document's own structure
                        var levelA = result;
                        PdfDictionary? changed = null;
                        PdfDictionary LevelAWritable() => changed ??= CloneWithResolver(levelA);
                        PdfALevelA(levelA, LevelAWritable, number);
                        if (levelA.IsType(StructTreeRoot)) FixedStructureRoot(changed ?? levelA, LevelAWritable);
                        if (changed is not null) result = changed;
                    }
                    // parts with attachments: every file specification associated with the document (PDF/A-3, 6.8) — one written into another
                    // object becomes an object of its own, so that the catalogue's /AF can name it (WriteAssociated)
                    if (_pdfA!.CollectsAssociated && PdfACheck.IsFileSpec(result))
                    {
                        if (number > 0) _pdfA.Associated.Add(number);
                        else if (depth > 0)
                        {
                            // queued as it is (its references already those of the new file)
                            var own = NewNumber();
                            _memoryQueue.Enqueue((own, source is null ? result : (PdfDictionary)Rebase(result, source)));
                            _pdfA.Associated.Add(own);
                            return new OutputReference(own);
                        }
                    }
                    return result;
                }
                case PdfInteger or PdfReal:
                    return PdfANumber(obj);
                default:
                    return obj;
            }
        }

        /// <summary>
        /// A number within the limits of the part (ISO 19005-2, 6.1.13): integers to ±2³¹ − 1, reals to ±3.403·10³⁸, reals closer to zero than
        /// ±1.175·10⁻³⁸ to 0 — values beyond anything a page shows; PDF/A-1 (6.1.12) reals beyond ±32 767 as the nearest integer. Part 4 has no
        /// such limits; the number stays.
        /// </summary>
        private PdfObject PdfANumber(PdfObject value)
        {
            if (PdfARules.IntegerRange.Id(_pdfA!.Part) is null) return value;
            return value switch
            {
                PdfInteger i when i.Value > int.MaxValue => PdfInteger.Of(int.MaxValue),
                PdfInteger i when i.Value < int.MinValue => PdfInteger.Of(int.MinValue),
                PdfReal r when !double.IsFinite(r.Value) || r.Value != 0 && Math.Abs(r.Value) < 1.175e-38 => PdfInteger.Of(0),
                // written as a real, a value beyond 2³¹ would read as an integer out of range: the largest integer, still beyond any page
                PdfReal r when Math.Abs(r.Value) > int.MaxValue => PdfInteger.Of(r.Value > 0 ? int.MaxValue : int.MinValue),
                PdfReal r when Math.Abs(r.Value) > _pdfA.Part.Limits().Real => PdfInteger.Of((long)Math.Round(r.Value)),
                _ => value,
            };
        }

        private static PdfDictionary CloneWithResolver(PdfDictionary dict)
        {
            var copy = new PdfDictionary(dict.Count) { Resolver = dict.Resolver };
            foreach (var (key, value) in dict.Entries) copy.Set(key, value);
            return copy;
        }

        /// <summary>
        /// The additional actions of a dictionary (/AA) as the part allows them on their owner (<see cref="PdfACheck.AllowedTrigger"/>), each with an
        /// action the part allows; without any left, the entry goes.
        /// </summary>
        private void PdfATriggers(PdfDictionary dict, Func<PdfDictionary> writable, Source? source)
        {
            var owner = PdfACheck.OwnerOf(dict);
            if (dict.GetRaw(Aa) is not { } raw || Look(raw, source) is not (PdfDictionary triggers, var from))
            {
                writable().Remove(Aa);
                return;
            }
            var kept = new PdfDictionary();
            foreach (var (key, value) in triggers.Entries)
                if (PdfACheck.AllowedTrigger(key.Value, owner, _pdfA!.Part) && PdfAResolve(value, from) is PdfDictionary action && KeptAction(action, from))
                    kept.Set(key, Portable(value, from, source));
            if (kept.Count == 0) writable().Remove(Aa);
            else if (kept.Count != triggers.Count) writable().Set(Aa, kept);
        }

        /// <summary>Embedded standard fonts by what they hold: descriptor (with the font program) shared by equal ones.</summary>
        private readonly Dictionary<string, OutputReference> _embeddedFonts = new(StringComparer.Ordinal);

        /// <summary>The rules for one dictionary, by what it is; null if nothing changes.</summary>
        /// <param name="dict">The dictionary as it is about to be written.</param>
        /// <param name="source">The source whose numbering its references use.</param>
        /// <param name="number">Its number in the new file; 0 inside another object.</param>
        /// <param name="depth">Nesting.</param>
        /// <param name="original">The dictionary as read (what <see cref="PdfACheck.Used"/> noted things by).</param>
        private PdfDictionary? PdfADictionary(PdfDictionary dict, Source? source, int number, int depth, PdfDictionary original)
        {
            var state = _pdfA!;
            if (dict.GetName(PdfName.Subtype)?.Value is "Type1" or "MMType1" or "TrueType" && EmbeddedStandardFont(dict, source, original) is { } font) return font;
            // a font embedded as its substitute (complete as built)
            if (_substitutes.TryGetValue(original, out var substitute)) return substitute;
            PdfDictionary? copy = null;
            PdfDictionary Writable() => copy ??= CloneWithResolver(dict);
            PdfDictionary Current() => copy ?? dict;

            // names of colourants (keys of /Colorants) and structure types (/S of a structure element, the role map) in UTF-8 (6.1.8)
            foreach (var key in (string[])["Colorants", "RoleMap"])
                if (dict.Get(PdfName.Get(key)) is PdfDictionary names && names.Entries.Any(e => !PdfACheck.Utf8(e.Key.Value) || e.Value is PdfName v && !PdfACheck.Utf8(v.Value)))
                {
                    var renamed = new PdfDictionary { Resolver = names.Resolver };
                    foreach (var (name, value) in names.Entries) renamed.Set(Utf8Transcoded(name), value is PdfName v ? Utf8Transcoded(v) : names.GetRaw(name)!);
                    Writable().Set(PdfName.Get(key), renamed);
                }
            if (dict.GetName(PdfName.Type)?.Value == "StructElem" && dict.GetName(PdfName.Get("S")) is { } structureType && !PdfACheck.Utf8(structureType.Value))
                Writable().Set(PdfName.Get("S"), Utf8Transcoded(structureType));

            // a font by its /Type, or because the pages show text with it whatever its /Type says (6.2.11.2: the copy names it a font)
            if (dict.GetName(PdfName.Subtype)?.Value is { } fontKind && FontKinds.Contains(fontKind) &&
                (dict.GetName(PdfName.Type)?.Value is null or "Font" || state.Usage.Shown.ContainsKey(original)) &&
                (dict.ContainsKey(BaseFont) || dict.ContainsKey(PdfName.Get("FontDescriptor")) || dict.ContainsKey(PdfName.Get("DescendantFonts")) ||
                 dict.ContainsKey(PdfName.Get("CharProcs")) || fontKind == "Type1"))
                PdfAFont(Current(), Writable, source, original, fontKind);
            // overprint mode 1 where CMYK becomes ICC-based (6.2.4.2): mode 0 — the same unless overprint is simulated
            if (dict.GetInteger(PdfName.Get("OPM")) == 1 && (state.Cmyk is not null || state.Usage.IccCmyk)) Writable().Set(PdfName.Get("OPM"), PdfInteger.Of(0));
            // a signature value belongs to bytes this copy no longer has (6.4.3): the field stays, unsigned
            if (dict.GetName(FT)?.Value == "Sig" && dict.ContainsKey(V)) Writable().Remove(V);
            // the colour space of a shading and the process space of an NChannel space: no default colour space applies there
            if ((dict.ContainsKey(PdfName.Get("ShadingType")) || dict.ContainsKey(PdfName.Get("Components"))) &&
                dict.Get(ColorSpaceKey) is PdfName { Value: "DeviceCMYK" or "DeviceRGB" } shadingSpace &&
                (shadingSpace.Value == "DeviceCMYK" ? state.Cmyk : state.Rgb) is { } iccSpace)
                Writable().Set(ColorSpaceKey, iccSpace);
            // the layer order names every layer (6.9)
            if (dict.ContainsKey(PdfName.Get("OCGs")) && dict.ContainsKey(D)) CompleteLayerOrder(Current(), Writable, source);

            // Metadata of single objects: PDF/A asks every XMP packet to use only declared schemas (6.6.2.3); the document's own is written anew
            if (dict.ContainsKey(PdfName.Metadata)) Writable().Remove(PdfName.Metadata);
            // actions and triggers: what the part forbids goes, the rest stays (6.4.1, 6.5; ISO 19005-4, 6.6)
            if (dict.ContainsKey(Aa)) PdfATriggers(Current(), Writable, source);
            // the scripts of the document (/Names /JavaScript of the catalogue): part 4 allows them (6.6.1), parts 2 and 3 not (6.5.1); PDF/A-1
            // neither scripts nor attachments; a name dictionary written into the catalogue is taken care of there (PdfACatalog)
            if (NamesNotAllowed(dict, state.Part) is { Count: > 0 } namesNotAllowed &&
                _sourceOrder.Any(s => ReferenceEquals(original, s.Doc.Catalog?.GetDictionary(PdfName.Get("Names")))))
                foreach (var key in namesNotAllowed) Writable().Remove(key);
            // (an action without /S where its place makes it one — of an annotation, an outline item, the catalogue — goes too: it does nothing)
            foreach (var key in (PdfName[])[A, OpenAction])
                if (Current().GetRaw(key) is { } raw && PdfAResolve(raw, source) is PdfDictionary action &&
                    (action.ContainsKey(S) || key == OpenAction || PdfACheck.HoldsAction(dict)) && !KeptAction(action, source))
                    Writable().Remove(key);
            if (dict.ContainsKey(S) && Current().GetRaw(Next) is { } next)
                switch (Look(next, source))
                {
                    case (PdfDictionary nextAction, var nextFrom) when !KeptAction(nextAction, nextFrom):
                        Writable().Remove(Next);
                        break;
                    case (PdfArray chain, var from):
                        var kept = new PdfArray();
                        for (var i = 0; i < chain.Count; i++)
                            if (PdfAResolve(chain[i], from) is PdfDictionary item && KeptAction(item, from)) kept.Add(Portable(chain[i], from, source));
                        if (kept.Count != chain.Count) Writable().Set(Next, kept);
                        break;
                }
            // alternate presentations and presentation steps (6.10)
            if (dict.ContainsKey(PdfName.Get("AlternatePresentations"))) Writable().Remove(PdfName.Get("AlternatePresentations"));
            if (dict.ContainsKey(PdfName.Get("PresSteps"))) Writable().Remove(PdfName.Get("PresSteps"));
            // forms: the fields are drawn by their appearances, not anew by the reader (6.4.1), no XFA (6.4.2), no actions on fields
            if (dict.ContainsKey(Fields))
            {
                Writable().Remove(NeedAppearances);
                Writable().Remove(Xfa);
            }
            if (dict.ContainsKey(FT) || dict.GetName(PdfName.Subtype)?.Value == "Widget") Writable().Remove(A);

            // graphics state (6.2.5): no transfer functions or halftone phase, standard rendering intents and blend modes
            // (the /TR of a soft mask is another thing and allowed)
            if (dict.ContainsKey(PdfName.Get("TR")) && !PdfACheck.IsSoftMask(dict)) Writable().Remove(PdfName.Get("TR"));
            if (dict.Get(PdfName.Get("TR2")) is not (PdfNull or PdfName { Value: "Default" })) Writable().Set(PdfName.Get("TR2"), PdfName.Get("Default"));
            if (dict.ContainsKey(PdfName.Get("HTP"))) Writable().Remove(PdfName.Get("HTP"));
            if (dict.ContainsKey(PdfName.Get("HTO"))) Writable().Remove(PdfName.Get("HTO"));   // its PDF 2.0 name (PDF/A-4, 6.2.5)
            // PDF/A-4 (6.2.10.8): a replacement text without characters of the private use area (they say nothing without the font)
            if (state.Part.Part() == 4 && dict.Get(PdfName.Get("ActualText")) is PdfString actual && PdfACheck.HasPrivateUse(actual.ToText()))
            {
                if (PdfACheck.WithoutPrivateUse(actual) is { } kept) Writable().Set(PdfName.Get("ActualText"), kept);
                else Writable().Remove(PdfName.Get("ActualText"));
            }
            // a halftone the part does not allow goes (the display draws without halftones; without /HT the device's own one applies)
            if (dict.Get(PdfName.Get("HT")) is PdfDictionary or PdfStream && !AllowedHalftone(dict.Get(PdfName.Get("HT")), null, 0)) Writable().Remove(PdfName.Get("HT"));
            foreach (var key in (string[])["RI", "Intent"])
                if (dict.Get(PdfName.Get(key)) is PdfName intent && !PdfACheck.RenderingIntent(intent.Value))
                    Writable().Set(PdfName.Get(key), PdfName.Get("RelativeColorimetric"));
            // an unknown blend mode draws as Normal (ISO 32000-1, 11.3.5); a known one PDF/A-1 does not allow changes the look and stays, refused
            if (dict.Get(PdfName.Get("BM")) is not PdfNull and var blend && !PdfACheck.StandardBlend(blend, PdfAConformance.A2b))
                Writable().Set(PdfName.Get("BM"), PdfName.Get("Normal"));
            // a soft mask that refers to no object is none (ISO 32000-2, 7.3.10): written without the key — the reference, left dangling, counted
            // for a reference validator as a mask, in PDF/A-1 a violation of 6.4 (docs/FEHLERGEDAECHTNIS.md, No. 511)
            if (dict.GetRaw(PdfName.Get("SMask")) is PdfReference && dict.Get(PdfName.Get("SMask")) is PdfNull) Writable().Remove(PdfName.Get("SMask"));
            // PDF/A-1 flattened: a graphics state draws opaque — what it drew translucent is in the images over the page (PdfAFlattening)
            if (state.Part.Part() == 1 && _options.PdfAFlattened && dict.GetName(PdfName.Subtype) is null &&
                (dict.GetName(PdfName.Type)?.Value == "ExtGState" || dict.ContainsKey(PdfName.Get("CA")) || dict.ContainsKey(PdfName.Get("ca")) ||
                 dict.ContainsKey(PdfName.Get("SMask")) || dict.ContainsKey(PdfName.Get("BM"))))
            {
                foreach (var key in (string[])["CA", "ca"])
                    if (dict.ContainsKey(PdfName.Get(key))) Writable().Remove(PdfName.Get(key));
                if (dict.Get(PdfName.Get("SMask")) is not (PdfNull or PdfName { Value: "None" })) Writable().Set(PdfName.Get("SMask"), PdfName.Get("None"));
                if (dict.Get(PdfName.Get("BM")) is not (PdfNull or PdfName { Value: "Normal" or "Compatible" })) Writable().Set(PdfName.Get("BM"), PdfName.Get("Normal"));
            }

            // images and forms (6.2.8, 6.2.9)
            var subtype = dict.GetName(PdfName.Subtype)?.Value;
            if (subtype == "Image")
            {
                if (dict.ContainsKey(PdfName.Get("Interpolate"))) Writable().Remove(PdfName.Get("Interpolate"));
                if (dict.ContainsKey(PdfName.Get("Alternates"))) Writable().Remove(PdfName.Get("Alternates"));
                // a soft mask that is none (/SMask /None, a name where a stream belongs): the display draws without; PDF/A-1 (6.4) allows no /SMask key
                if (dict.GetRaw(PdfName.Get("SMask")) is PdfName) Writable().Remove(PdfName.Get("SMask"));
            }
            if (subtype is "Image" or "Form" && dict.ContainsKey(PdfName.Get("OPI"))) Writable().Remove(PdfName.Get("OPI"));
            if (subtype == "Form" && dict.ContainsKey(PdfName.Get("Ref"))) Writable().Remove(PdfName.Get("Ref"));   // a reference XObject draws its own content then
            // a form with PostScript for printers: the display draws its content as an ordinary form, which it stays (6.2.9)
            if (subtype == "Form")
                foreach (var key in (string[])["PS", "Subtype2"])
                    if (dict.ContainsKey(PdfName.Get(key))) Writable().Remove(PdfName.Get(key));
            // PostScript XObjects draw nothing in the display: left out of the resources (their "Do" goes with the content)
            if (dict.GetRaw(PdfName.Get("XObject")) is { } xobjectsRaw && PdfAResolve(xobjectsRaw, source) is PdfDictionary xobjects &&
                xobjects.Entries.Any(e => PdfAResolve(e.Value, source) is PdfStream { } x && x.Dictionary.GetName(PdfName.Subtype)?.Value == "PS"))
            {
                var kept = new PdfDictionary { Resolver = xobjects.Resolver };
                foreach (var (key, value) in xobjects.Entries)
                    if (PdfAResolve(value, source) is not PdfStream { } x || x.Dictionary.GetName(PdfName.Subtype)?.Value != "PS")
                        kept.Set(key, Portable(value, Look(xobjectsRaw, source).From, source));
                Writable().Set(PdfName.Get("XObject"), kept);
            }
            // font programs: a character set that may be incomplete is left out (6.2.11.4.2 allows that; PDF/A-1 asks subsets for one, 6.3.5:
            // it stays, and rebuilt fonts get the one of their new program); the font name in UTF-8 (6.1.8)
            if (dict.GetName(PdfName.Type)?.Value == "FontDescriptor" || dict.ContainsKey(PdfName.Get("FontFile2")) || dict.ContainsKey(PdfName.Get("FontFile3")) ||
                dict.ContainsKey(PdfName.Get("FontFile")))
            {
                if (dict.Get(PdfName.Get("FontName")) is PdfName fontName && !PdfACheck.Utf8(fontName.Value)) Writable().Set(PdfName.Get("FontName"), Utf8Name(fontName));
                if (state.Part.Part() != 1 && dict.ContainsKey(PdfName.Get("CIDSet"))) Writable().Remove(PdfName.Get("CIDSet"));
                if (state.Part.Part() != 1 && dict.ContainsKey(PdfName.Get("CharSet"))) Writable().Remove(PdfName.Get("CharSet"));
            }
            // PDF/A-1 (6.4) has no transparency groups: a page or form that is one is written as plain content when the transparency is
            // flattened — what was translucent inside is in the images over the page, the rest is drawn the same (PdfAFlattening)
            if (state.Part.Part() == 1 && _options.PdfAFlattened && (dict.GetName(PdfName.Type)?.Value == "Page" || subtype == "Form") &&
                dict.GetRaw(PdfName.Get("Group")) is { } groupRaw && PdfAResolve(groupRaw, source) is PdfDictionary transparencyGroup &&
                transparencyGroup.GetName(S)?.Value == "Transparency")
                Writable().Remove(PdfName.Get("Group"));
            // PDF/A-4: a group whose ICC-based CMYK space a colour inside uses as well loses its space (/CS is optional): an ICC-based space equal
            // to the blending space is not allowed (6.2.4.2), DeviceCMYK would need a device-independent blending space (6.2.4.3); the display
            // uses the space of a group only for the backdrop of a soft mask, and soft-mask groups are not among these
            if (state.Part.Part() == 4 && dict.GetName(S)?.Value == "Transparency" && state.Usage.DeviceGroups.Contains(original))
                Writable().Remove(PdfName.Get("CS"));
            // transparency groups in a device space the output intent does not cover (PDF/A-4: the device space is the group's blending space,
            // which allows device colours inside, 6.2.4.3 — an ICC space there would equal a default space inside)
            else if (state.Part.Part() != 4 && dict.GetName(S)?.Value == "Transparency" && dict.Get(PdfName.Get("CS")) is PdfName groupSpace)
            {
                if (groupSpace.Value == "DeviceCMYK" && state.Cmyk is { } cmyk) Writable().Set(PdfName.Get("CS"), cmyk);
                else if (groupSpace.Value == "DeviceRGB" && state.Rgb is { } rgb) Writable().Set(PdfName.Get("CS"), rgb);
            }

            // file specifications and embedded files (6.8; ISO 19005-4, 6.9)
            if (PdfACheck.IsFileSpec(dict))
            {
                // an embedded file the part does not allow goes (PDF/A-2 and PDF/A-4 allow only files confirmed as PDF/A), the specification stays
                if (dict.Get(PdfName.Get("EF")) is PdfDictionary && !state.Part.KeepsAttachments() && !AllowedAttachments(dict, source))
                {
                    Writable().Remove(PdfName.Get("EF"));
                    Writable().Remove(PdfName.Get("RF"));
                }
                var embedded = Current().Get(PdfName.Get("EF")) is PdfDictionary;
                if (embedded)
                {
                    // both names of the file (6.8)
                    var uf = dict.Get(PdfName.Get("UF"));
                    var f = dict.Get(F);
                    if (uf is PdfNull && f is PdfString name) Writable().Set(PdfName.Get("UF"), new PdfString(PdfTextEncoding.Encode(name.ToText())));
                    if (f is PdfNull && uf is PdfString unicode) Writable().Set(F, new PdfString(PdfTextEncoding.Encode(unicode.ToText())));
                }
                // its relationship to the document: part 3 for every specification, part 4 for those with a file
                if (PdfARules.AttachmentRelationship.Id(state.Part) is not null && (embedded || state.Part.Part() == 3) && !dict.ContainsKey(PdfName.Get("AFRelationship")))
                    Writable().Set(PdfName.Get("AFRelationship"), PdfName.Get("Unspecified"));
            }
            // an embedded file (its kind as a MIME type: PdfAStream, which reads the data)
            if (dict.GetName(PdfName.Type)?.Value == "EmbeddedFile")
            {
                // PDF/A-3 and PDF/A-4f: the date of the file
                if (state.Part.KeepsAttachments())
                {
                    var parms = PdfAResolve(dict.GetRaw(PdfName.Get("Params")) ?? PdfNull.Instance, source) as PdfDictionary;
                    if (parms?.ContainsKey(PdfName.Get("ModDate")) != true)
                    {
                        var withDate = parms is null ? new PdfDictionary() : Direct(parms, dict.GetRaw(PdfName.Get("Params"))!, source, source);
                        withDate.Set(PdfName.Get("ModDate"), new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(Modified))));
                        Writable().Set(PdfName.Get("Params"), withDate);
                    }
                }
            }
            // associated files (/AF) are a means of PDF/A-3 and PDF 2.0; a PDF/A-2 file has none
            if (state.Part.Part() == 2 && dict.ContainsKey(PdfName.Get("AF"))) Writable().Remove(PdfName.Get("AF"));
            // a specification whose file goes (above) leaves the associated files: there it must carry its file (ISO 32000-2, 14.13.2;
            // docs/FEHLERGEDAECHTNIS.md, No. 368)
            else if (!state.Part.KeepsAttachments() && dict.GetRaw(PdfName.Get("AF")) is { } associatedRaw && Look(associatedRaw, source) is (PdfArray associated, var associatedFrom))
            {
                var kept = new PdfArray();
                var dropped = false;
                for (var i = 0; i < associated.Count && i < 10_000; i++)
                {
                    if (Look(associated[i], associatedFrom) is (PdfDictionary spec, var specFrom) && PdfACheck.IsFileSpec(spec) && spec.Get(PdfName.Get("EF")) is PdfDictionary &&
                        !AllowedAttachments(spec, specFrom))
                    {
                        dropped = true;
                        continue;
                    }
                    kept.Add(Portable(associated[i], associatedFrom, source));
                }
                if (dropped && kept.Count > 0) Writable().Set(PdfName.Get("AF"), kept);
                else if (dropped) Writable().Remove(PdfName.Get("AF"));
            }
            // the list of attachments (a node of /Names /EmbeddedFiles): an entry whose file the part does not allow leaves it
            if (!state.Part.KeepsAttachments() && dict.GetRaw(PdfName.Get("Names")) is { } namesRaw && Look(namesRaw, source) is (PdfArray pairs, var pairsFrom))
            {
                var kept = new PdfArray();
                var dropped = false;
                for (var i = 0; i + 1 < pairs.Count && i < 200_000; i += 2)
                {
                    // the specification in the numbering of its source (the list here may already refer to the new file)
                    if (Look(pairs[i + 1], pairsFrom) is (PdfDictionary spec, var specFrom) && PdfACheck.IsFileSpec(spec) && spec.Get(PdfName.Get("EF")) is PdfDictionary &&
                        !AllowedAttachments(spec, specFrom))
                    {
                        dropped = true;
                        continue;
                    }
                    kept.Add(Portable(pairs[i], pairsFrom, source));
                    kept.Add(Portable(pairs[i + 1], pairsFrom, source));
                }
                if (dropped) Writable().Set(PdfName.Get("Names"), kept);
            }

            // an annotation: by its /Rect, or marked as one (a pop-up without /Rect is one as well)
            if (subtype is not null && (dict.ContainsKey(Rect) || dict.GetName(PdfName.Type)?.Value == "Annot") && dict.GetName(PdfName.Type)?.Value is null or "Annot")
                PdfAAnnotation(Current(), Writable, source, original);
            if (dict.GetName(PdfName.Type)?.Value == "Page") PdfAPage(Current(), Writable, source);

            // what draws content gets the default colour spaces (6.2.4.3): pages, forms, tiling patterns, Type 3 fonts and appearances — one
            // without resources of its own gets some (strict readers take default spaces only from the own resources; what it borrows by name
            // is given to it already, ContentConformance)
            var draws = subtype is "Form" or "Type3" || dict.GetInteger(PdfName.Get("PatternType")) == 1 || number > 0 && state.Appearances.Contains(number);
            if (draws) WithDefaultSpaces(Current(), Writable, source);
            // a resource dictionary of its own (several pages share one), and a colour space dictionary of one
            if (number > 0 && depth == 0 && (dict.Count > 0 && dict.Entries.All(e => ResourceKeys.Contains(e.Key.Value)) && !dict.ContainsKey(PdfName.Type)
                                             || state.ResourceDictionaries.Contains(number)))
                AddDefaultSpaces(Current(), Writable, source);
            // resource names too long for PDF/A (6.1.13): their short form, as the content writes it (ContentConformance.ShortName)
            if (dict.Count > 0 && dict.Entries.All(e => ResourceKeys.Contains(e.Key.Value)) && !dict.ContainsKey(PdfName.Type))
                foreach (var (category, _) in Current().Entries.ToList())
                    if (Current().GetRaw(category) is { } raw && Look(raw, source) is (PdfDictionary names, var from) &&
                        names.Keys.Any(k => !ReferenceEquals(ContentConformance.ShortName(k), k)))
                    {
                        var renamed = new PdfDictionary(names.Count);
                        foreach (var (key, value) in names.Entries) renamed.Set(ContentConformance.ShortName(key), Portable(value, from, source));
                        Writable().Set(category, renamed);
                    }
            if (number > 0 && state.ColorSpaces.Contains(number)) AddDefaults(Current(), Writable);
            return copy;
        }

        /// <summary>
        /// The entries of the catalogue's name dictionary a part does not allow: the scripts in parts 1 to 3 (ISO 19005-2, 6.5.1; ISO 19005-1,
        /// 6.6.1), the attachments in part 1 (6.1.11).
        /// </summary>
        private static List<PdfName> NamesNotAllowed(PdfDictionary names, PdfAConformance part)
        {
            var result = new List<PdfName>();
            if (part.Part() != 4 && names.ContainsKey(PdfName.Get("JavaScript"))) result.Add(PdfName.Get("JavaScript"));
            if (part.Part() == 1 && names.ContainsKey(PdfName.Get("EmbeddedFiles"))) result.Add(PdfName.Get("EmbeddedFiles"));
            return result;
        }

        private static readonly PdfName BaseFont = PdfName.Get("BaseFont");

        /// <summary>Layers missing in the order of a configuration are put at its end (6.9; readers compare them by name).</summary>
        private void CompleteLayerOrder(PdfDictionary properties, Func<PdfDictionary> writable, Source? source)
        {
            if (properties.GetRaw(PdfName.Get("OCGs")) is not { } groupsRaw || Look(groupsRaw, source) is not (PdfArray groups, var groupsFrom)) return;
            // the names in an order, its references read as those of the source or of the new file (the catalogue is assembled already)
            HashSet<string> NamesIn(PdfArray order, Source? from)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                void Walk(PdfArray a, Source? f, int depth)
                {
                    for (var i = 0; i < a.Count && i < 100_000; i++)
                        switch (Look(a[i], f))
                        {
                            case (PdfDictionary layer, _) when layer.GetName(PdfName.Type)?.Value == "OCG":
                                names.Add(layer.GetString(PdfName.Get("Name"))?.ToText() ?? "");
                                break;
                            case (PdfArray inner, var innerFrom) when depth < 32:
                                Walk(inner, innerFrom, depth + 1);
                                break;
                        }
                }
                Walk(order, from, 0);
                return names;
            }
            PdfDictionary? Fixed(PdfDictionary config)
            {
                if (config.GetRaw(PdfName.Get("Order")) is not { } orderRaw || Look(orderRaw, source) is not (PdfArray order, var orderFrom)) return null;
                var named = NamesIn(order, orderFrom);
                var added = new PdfArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < groups.Count && i < 100_000; i++)
                    if (PdfAResolve(groups[i], groupsFrom) is PdfDictionary layer && layer.GetString(PdfName.Get("Name"))?.ToText() is var name &&
                        !named.Contains(name ?? "") && names.Add(name ?? ""))
                        added.Add(Portable(groups[i], groupsFrom, source));
                if (added.Count == 0) return null;
                var copy = CloneWithResolver(config);
                var complete = new PdfArray();
                for (var i = 0; i < order.Count; i++) complete.Add(Portable(order[i], orderFrom, source));
                foreach (var item in added) complete.Add(item);
                copy.Set(PdfName.Get("Order"), complete);
                return copy;
            }
            if (properties.GetRaw(D) is { } standardRaw && PdfAResolve(standardRaw, source) is PdfDictionary standard &&
                Fixed(Direct(standard, standardRaw, source, source)) is { } fixedStandard)
                writable().Set(D, fixedStandard);
        }

        /// <summary>
        /// A halftone as the part allows it (ISO 19005-2, 6.2.5; the check, <see cref="PdfACheck"/>): of type 1 or 5, without a name, a
        /// /TransferFunction only for colourants other than the primaries in a type 5 halftone and there always — each colourant of a type 5
        /// halftone as well (docs/FEHLERGEDAECHTNIS.md, No. 337: Red, Green, Blue and Gray without one).
        /// </summary>
        private static bool AllowedHalftone(PdfObject? raw, string? colorant, int depth)
        {
            var halftone = raw switch { PdfStream stream => stream.Dictionary, PdfDictionary dict => dict, _ => null };
            if (halftone is null || depth > 4) return false;
            var type = halftone.GetInteger(PdfName.Get("HalftoneType"));
            if (type is not (1 or 5) || halftone.ContainsKey(PdfName.Get("HalftoneName"))) return false;
            if (colorant != "Default" && (colorant is null or "Cyan" or "Magenta" or "Yellow" or "Black") == halftone.ContainsKey(PdfName.Get("TransferFunction")))
                return false;
            if (type == 5)
                foreach (var (key, _) in halftone.Entries.Take(64))
                    if (key.Value is not ("Type" or "HalftoneType" or "HalftoneName") && halftone.Get(key) is PdfDictionary or PdfStream &&
                        !AllowedHalftone(halftone.Get(key), key.Value, depth + 1))
                        return false;
            return true;
        }

    }
}
