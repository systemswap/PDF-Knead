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
    /// <summary>The rules on the document as a whole: file structure, metadata, output intent, catalogue, signatures.</summary>
    private sealed partial class Run
    {
        // ------------------------------------------------------------ 6.1 File structure

        private void FileStructure()
        {
            var head = doc.FileBytes(0, Math.Min(doc.FileSize, 1024)).ToArray();
            if (Four && (head.Length < 9 || !head.AsSpan().StartsWith("%PDF-2."u8) || head[7] is < (byte)'0' or > (byte)'9'))
                Add(PdfARules.Header, Tr.T("Die Datei beginnt nicht mit der Kennung einer PDF-Version 2.x."));
            // PDF/A-1 (ISO 19005-1, 6.1.2) asks only for the form „%PDF-n.m“
            else if (_part.Part() == 1 && (head.Length < 9 || !head.AsSpan().StartsWith("%PDF-"u8) || !char.IsAsciiDigit((char)head[5]) || head[6] != '.' ||
                                           !char.IsAsciiDigit((char)head[7])))
                Add(PdfARules.Header, Tr.T("Die Datei beginnt nicht mit der Kennung einer PDF-Version."));
            else if (!Four && _part.Part() != 1 && (head.Length < 9 || !head.AsSpan().StartsWith("%PDF-1."u8) || head[7] is < (byte)'0' or > (byte)'7'))
                Add(PdfARules.Header, Tr.T("Die Datei beginnt nicht mit der Kennung einer PDF-Version 1.0 bis 1.7."));
            else if (head.Length < 9 || head[8] is not (10 or 13))
                Add(PdfARules.Header, Tr.T("Nach der Kennung der PDF-Version folgt kein Zeilenende."));
            else
            {
                // the comment of at least four bytes above 127 in the very next line: after exactly one line end (CR, LF or CR LF) — an
                // empty line between does not do (docs/FEHLERGEDAECHTNIS.md, No. 505)
                var eol = Array.FindIndex(head, b => b is 10 or 13);
                var at = eol < 0 ? -1 : eol + (head[eol] == 13 && eol + 1 < head.Length && head[eol + 1] == 10 ? 2 : 1);
                if (eol < 0 || at + 5 > head.Length || head[at] != '%' || head.Skip(at + 1).Take(4).Any(b => b <= 127))
                    Add(PdfARules.BinaryComment, Tr.T("Nach der Kennung fehlt die Kommentarzeile mit Binärzeichen."));
            }
            var tailLength = (int)Math.Min(doc.FileSize, 64);
            var tail = doc.FileBytes(doc.FileSize - tailLength, tailLength);
            var eof = tail.LastIndexOf("%%EOF"u8);
            if (eof < 0 || tail[(eof + 5)..].Length > 2 || tail[(eof + 5)..].ToArray().Any(b => b is not (10 or 13)))
                Add(PdfARules.NothingAfterEof, Tr.T("Nach dem Dateiende-Kennzeichen folgen noch Daten."));
            if (doc.Trailer.Get(PdfName.ID) is not PdfArray { Count: 2 } id || id.Get(0) is not PdfString first || id.Get(1) is not PdfString second)
                Add(PdfARules.FileId, Tr.T("Die Datei hat keine Kennung (/ID)."));
            // from part 2 both strings hold something: /ID [<> <>] is no identifier (docs/FEHLERGEDAECHTNIS.md, No. 498)
            else if (_part.Part() != 1 && (first.Bytes.Length == 0 || second.Bytes.Length == 0))
                Add(PdfARules.FileId, Tr.T("Die Kennung der Datei (/ID) ist leer."));
            if (doc.Trailer.ContainsKey(PdfName.Encrypt)) Add(PdfARules.NotEncrypted, Tr.T("Die Datei ist verschlüsselt."));
            PdfAFileSyntax.Check(doc, (rule, text) => Add(rule, text), cancel, _part);
            if (Four && doc.Trailer.Get(PdfName.Get("Info")) is PdfDictionary info)
            {
                // PDF/A-4 keeps the information dictionary only for /PieceInfo, and then with nothing but the date of the last change
                if (doc.Catalog?.ContainsKey(PdfName.Get("PieceInfo")) != true)
                    Add(PdfARules.NoInfo, Tr.T("Die Datei hat ein Dokumentinfo-Wörterbuch (/Info), das PDF/A-4 nicht erlaubt."));
                else if (info.Count != 1 || !info.ContainsKey(PdfName.Get("ModDate")))
                    Add(PdfARules.InfoOnlyModDate, Tr.T("Das Dokumentinfo-Wörterbuch (/Info) enthält mehr als das Änderungsdatum."));
            }
        }

        /// <summary>The document is checked against PDF/A-4.</summary>
        private bool Four => _part.Part() == 4;

        // ------------------------------------------------------------ 6.6 Metadata

        private void Metadata(PdfDictionary catalog)
        {
            if (catalog.Get(PdfName.Metadata) is not PdfStream stream)
            {
                Add(PdfARules.Metadata, Tr.T("Die Metadaten (XMP) fehlen."));
                return;
            }
            _documentMetadata = stream;
            // PDF/A-1 (6.7.2): the catalogue's packet stands unpacked in the file
            if (_part.Part() == 1 && stream.Dictionary.ContainsKey(PdfName.Get("Filter")))
                Add(PdfARules.MetadataUnfiltered, Tr.T("Die Metadaten (XMP) des Dokuments sind gepackt (/Filter)."));
            var data = StreamData(stream);
            foreach (var fault in PdfAXmp.Faults(data, null, out _declarations, _part)) Add(fault.Rule, fault.Text, test: fault.Test);
            if (PdfAXmp.Read(data) is not { } xmp) return;
            if (_part.Part() == 1) InfoMatchesXmp(xmp);
            var (part, level) = (Property(xmp, PdfAId, "part"), Property(xmp, PdfAId, "conformance"));
            if (part is null) Add(PdfARules.Identification, Tr.T("Die Metadaten nennen nicht Teil und Stufe von PDF/A (pdfaid)."));
            else if (part != _part.Part().ToString(System.Globalization.CultureInfo.InvariantCulture))
                Add(PdfARules.IdentificationPart, Tr.T("Die Metadaten nennen einen anderen Teil von PDF/A ({0}).", part));
            // part 4 names a level only for 4e and 4f, and always the revision of the standard; parts 2 and 3 name A, B or U, part 1 A or B
            if (part is not null && (Four ? level is not (null or "E" or "F") : _part.Part() == 1 ? level is not ("A" or "B") : level is not ("A" or "B" or "U")
                                     || _part.Accessible() && level != "A"))
                Add(PdfARules.IdentificationLevel, Tr.T("Die Metadaten nennen keine gültige Stufe von PDF/A ({0}).", level ?? "–"));
            if (part is not null && Property(xmp, PdfAId, "rev") != "2020")
                Add(PdfARules.IdentificationRevision, Tr.T("Die Metadaten nennen nicht die Ausgabe 2020 von PDF/A-4 (pdfaid:rev)."));
            // one test per property (parts 1–3: part, conformance, amd, corr; No. 503)
            foreach (var (label, name) in PdfAXmp.ForeignIdentificationPrefixes(xmp))
                Add(PdfARules.IdentificationPrefix, Tr.T("Die Kennung von PDF/A in den Metadaten steht nicht unter dem Präfix pdfaid ({0}).", label),
                    test: Four ? 0 : name switch { "part" => 1, "conformance" => 2, "amd" => 3, "corr" => 4, _ => 0 });
        }

        /// <summary>
        /// PDF/A-1 (ISO 19005-1, 6.7.3): each entry of the information dictionary equal to its XMP property — dates as the same instant,
        /// texts as they are; /Author the only entry of dc:creator.
        /// </summary>
        private void InfoMatchesXmp(XmlDocument xmp)
        {
            if (doc.Trailer.Get(PdfName.Get("Info")) is not PdfDictionary info) return;
            void Fault(string key) => Add(PdfARules.InfoMatchesXmp, Tr.T("Der Eintrag /{0} der Dokumentinfo stimmt nicht mit den Metadaten (XMP) überein.", key));
            foreach (var (key, property) in (ReadOnlySpan<(string, string)>)[("CreationDate", "CreateDate"), ("ModDate", "ModifyDate")])
                if (info.GetString(PdfName.Get(key)) is { } date && PdfDate.Parse(date.ToText()) is { } instant &&
                    !(Property(xmp, XmpNs, property) is { } text &&
                      DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var other) &&
                      other.UtcDateTime == instant.UtcDateTime))
                    Fault(key);
            foreach (var (key, ns, property) in (ReadOnlySpan<(string, string, string)>)[("Title", DcNs, "title"), ("Subject", DcNs, "description"),
                         ("Keywords", PdfNs, "Keywords"), ("Creator", XmpNs, "CreatorTool"), ("Producer", PdfNs, "Producer")])
                if (info.Get(PdfName.Get(key)) is not PdfNull and var value &&
                    (value is not PdfString text || (ns == DcNs ? DefaultLanguage(xmp, ns, property) : Property(xmp, ns, property)) != text.ToText()))
                    Fault(key);
            // an entry that is no text matches nothing
            if (info.Get(PdfName.Get("Author")) is not PdfNull and var author && Property(xmp, DcNs, "creator") is var creators &&
                (author is not PdfString authorText || creators is null || creators.Contains('\u0001') || creators != authorText.ToText()))
                Fault("Author");
        }

        /// <summary>The x-default entry of a language alternative (rdf:Alt) in any rdf:Description; null if there is none.</summary>
        private static string? DefaultLanguage(XmlDocument xmp, string ns, string name)
        {
            foreach (XmlElement description in xmp.GetElementsByTagName("Description", RdfNs))
                foreach (XmlNode child in description.ChildNodes)
                    if (child is XmlElement e && e.LocalName == name && e.NamespaceURI == ns)
                        foreach (XmlElement item in e.GetElementsByTagName("li", RdfNs))
                            if (item.GetAttribute("xml:lang") == "x-default") return item.InnerText;
            return null;
        }

        /// <summary>The document's XMP packet, and what its extension schemas declare (for the packets of objects, 6.6.2.3.1).</summary>
        private PdfStream? _documentMetadata;
        private PdfAXmp.Declarations? _declarations;

        /// <summary>The packet of an object (6.6.2): the same rules, with what the document's packet declares.</summary>
        private void ObjectMetadata(PdfStream stream)
        {
            if (ReferenceEquals(stream, _documentMetadata)) return;
            foreach (var fault in PdfAXmp.Faults(StreamData(stream), _declarations, out _, _part)) Add(fault.Rule, fault.Text, test: fault.Test);
        }

        // ------------------------------------------------------------ 6.2.3 Output intent

        private void OutputIntents(PdfDictionary catalog)
        {
            if (catalog.Get(PdfName.Get("OutputIntents")) is PdfArray intents) IntentList(intents, document: true);
        }

        /// <summary>
        /// A list of output intents — of the document, or (part 4, PDF 2.0) of a page (docs/FEHLERGEDAECHTNIS.md, No. 520): at most one PDF/A
        /// intent, none with /DestOutputProfileRef, all with a profile on the same object, the PDF/A profile one for a printer or a screen.
        /// Only the document's sets the output intent the colours are checked against (a page's: <see cref="IntentSpace"/>).
        /// </summary>
        private void IntentList(PdfArray intents, bool document)
        {
            PdfObject? profileRaw = null;
            var pdfaProfile = false;
            uint? space = null;
            {
                // PDF/A-4: one PDF/A output intent at most (6.2.3; No. 495)
                if (Enumerable.Range(0, Math.Min(intents.Count, 64)).Count(i => intents.Get(i) is PdfDictionary d && d.GetName(PdfName.Get("S"))?.Value == "GTS_PDFA1") > 1)
                    Add(PdfARules.OneOutputIntent, Tr.T("Die Datei hat mehr als eine PDF/A-Ausgabebedingung."));
                for (var i = 0; i < intents.Count && i < 64; i++)
                {
                    if (intents.Get(i) is PdfDictionary withRef && withRef.ContainsKey(PdfName.Get("DestOutputProfileRef")) &&
                        (Four || withRef.GetName(PdfName.Get("S"))?.Value == "GTS_PDFX"))
                        Add(PdfARules.IntentProfileRef, Tr.T("Eine Ausgabebedingung verweist auf ein Farbprofil außerhalb der Datei (/DestOutputProfileRef)."));
                    if (intents.Get(i) is not PdfDictionary intent || intent.GetRaw(PdfName.Get("DestOutputProfile")) is not { } raw) continue;
                    if (intent.GetName(PdfName.Get("S"))?.Value == "GTS_PDFA1")
                    {
                        pdfaProfile = true;
                        if (intent.Get(PdfName.Get("DestOutputProfile")) is PdfStream profile)
                        {
                            var data = doc.GetStreamData(profile);
                            space = UsableProfile(data, _part.Limits().IccMajor);
                            if (document)
                            {
                                _intentSpace = space;
                                _intentProfile = profile;
                            }
                            // PDF/A-1 (6.2.3.2): /N the number of colours of the profile, as for every ICC profile
                            if (_part.Part() == 1 && profile.Dictionary.GetInteger(PdfName.Get("N")) != IccComponents(data))
                                Add(PdfARules.IccProfileValid, Tr.T("Die Zahl der Farben (/N) eines ICC-Profils passt nicht zu seinem Farbraum."));
                            // description, copyright and media white point are required (ICC.1, 8.2; docs/FEHLERGEDAECHTNIS.md, No. 1133)
                            if (space is not null && Graphics.Icc.CalibratedProfile.MissingRequiredTags(data).Length > 0)
                                Add(PdfARules.IccProfileValid, Tr.T("Dem Profil der Ausgabebedingung fehlt eine Pflichtangabe (Beschreibung, Urheberrecht oder Medienweiß)."));
                        }
                    }
                    if (profileRaw is null) profileRaw = raw;
                    else if (!(raw is PdfReference a && profileRaw is PdfReference b && a.Id.Number == b.Id.Number))
                        Add(PdfARules.IntentsSameProfile, Tr.T("Mehrere Ausgabebedingungen haben verschiedene Farbprofile."));
                }
            }
            // without one, device colours need default colour spaces (checked where they are used, 6.2.4.3)
            if (pdfaProfile && space is null)
                Add(PdfARules.IntentProfile, Tr.T("Die Ausgabebedingung hat kein gültiges Drucker- oder Bildschirmprofil."));
        }

        // ------------------------------------------------------------ Catalogue: actions, form, layers, presentations, permissions

        private void Catalogue(PdfDictionary catalog)
        {
            if (TriggersNotAllowed(catalog, TriggerOwner.Catalogue)) Add(PdfARules.CatalogueTriggers, Tr.T("Das Dokument hat Aktionen beim Öffnen, Speichern oder Drucken (/AA)."));
            if (catalog.ContainsKey(PdfName.Get("Requirements"))) Add(PdfARules.NoRequirements, Tr.T("Das Dokument stellt Anforderungen an den Leser (/Requirements)."));
            if (Four && catalog.GetName(PdfName.Get("Version"))?.Value is { } version && !(version.Length == 3 && version.StartsWith("2.", StringComparison.Ordinal) && char.IsAsciiDigit(version[2])))
                Add(PdfARules.CatalogueVersion, Tr.T("Der Katalog nennt eine PDF-Version vor 2.0 (/Version)."));
            if (catalog.GetBoolean(PdfName.Get("NeedsRendering")) == true) Add(PdfARules.NoNeedsRendering, Tr.T("Das Formular muss vom Leser erst aufgebaut werden (XFA)."));
            if (catalog.GetDictionary(PdfName.Get("AcroForm")) is { } form)
            {
                if (form.GetBoolean(PdfName.Get("NeedAppearances")) == true)
                    Add(PdfARules.NeedAppearances, Tr.T("Formularfelder sollen vom Leser neu gezeichnet werden (/NeedAppearances)."));
                if (form.ContainsKey(PdfName.Get("XFA"))) Add(PdfARules.NoXfa, Tr.T("Das Formular enthält XFA-Daten."));
            }
            if (catalog.GetDictionary(PdfName.Get("Names")) is { } names)
            {
                if (names.ContainsKey(PdfName.Get("AlternatePresentations"))) Add(PdfARules.NoAlternatePresentations, Tr.T("Das Dokument enthält Diashows (/AlternatePresentations)."));
                // PDF/A-4 allows scripts (6.6.1)
                if (!Four && names.ContainsKey(PdfName.Get("JavaScript"))) Add(PdfARules.Actions, Tr.T("Das Dokument enthält JavaScript."));
            }
            if (catalog.GetDictionary(PdfName.Get("Names"))?.ContainsKey(PdfName.Get("EmbeddedFiles")) != true)
                Add(PdfARules.FourFHasAttachment, Tr.T("PDF/A-4f verlangt mindestens einen Anhang (/EmbeddedFiles)."));
            // (part 4: DocMDP only, ISO 19005-4, 6.1.11; No. 505)
            if (catalog.GetDictionary(PdfName.Get("Perms")) is { } perms && perms.Entries.Any(e => e.Key.Value is not "DocMDP" && (Four || e.Key.Value is not "UR3")))
                Add(PdfARules.Permissions, Four ? Tr.T("Das Dokument hat Berechtigungen außer DocMDP (/Perms).")
                    : Tr.T("Das Dokument hat Berechtigungen außer DocMDP und UR3 (/Perms)."));
            // PDF/A-1 (6.1.11, 6.1.13): neither embedded files nor optional content
            if (_part.Part() == 1 && catalog.GetDictionary(PdfName.Get("Names"))?.ContainsKey(PdfName.Get("EmbeddedFiles")) == true)
                Add(PdfARules.NoEmbeddedFiles, Tr.T("Das Dokument enthält Anhänge (/EmbeddedFiles), die PDF/A-1 nicht erlaubt."));
            if (_part.Part() == 1 && catalog.ContainsKey(PdfName.Get("OCProperties")))
                Add(PdfARules.NoOptionalContent, Tr.T("Das Dokument hat Ebenen (/OCProperties), die PDF/A-1 nicht erlaubt."));
            if (catalog.GetDictionary(PdfName.Get("OCProperties")) is { } layers)
            {
                var names2 = new HashSet<string>(StringComparer.Ordinal);
                var configs = new List<PdfDictionary>();
                if (layers.GetDictionary(PdfName.Get("D")) is { } standard) configs.Add(standard);
                if (layers.Get(PdfName.Get("Configs")) is PdfArray more)
                    for (var i = 0; i < more.Count && i < 1000; i++)
                        if (more.Get(i) is PdfDictionary config) configs.Add(config);
                var groups = LayerNames(layers.Get(PdfName.Get("OCGs")) as PdfArray);
                // /OCGs lists layers only: a membership dictionary there counts as a layer without a name that no order names (No. 510)
                if (layers.Get(PdfName.Get("OCGs")) is PdfArray listed)
                    for (var i = 0; i < listed.Count && i < 100_000; i++)
                        if (listed.Get(i) is PdfDictionary entry && entry.GetName(PdfName.Type)?.Value is { } type && type != "OCG")
                        {
                            Add(PdfARules.LayerOrder, Tr.T("Die Liste der Ebenen (/OCGs) enthält etwas, das keine Ebene ist."));
                            break;
                        }
                foreach (var config in configs)
                {
                    var name = config.GetString(PdfName.Get("Name"))?.ToText();
                    // PDF/A-4 (6.10) asks only for a name, which may be empty
                    if (name is null || name.Length == 0 && !Four) Add(PdfARules.LayerConfigName, Tr.T("Eine Ebenen-Konfiguration hat keinen Namen."));
                    else if (!names2.Add(name)) Add(PdfARules.LayerConfigUnique, Tr.T("Zwei Ebenen-Konfigurationen haben denselben Namen."));
                    if (config.ContainsKey(PdfName.Get("AS"))) Add(PdfARules.LayerAutoState, Tr.T("Eine Ebenen-Konfiguration schaltet Ebenen selbständig (/AS)."));
                    if (config.Get(PdfName.Get("Order")) is PdfArray order && !groups.IsSubsetOf(LayerNames(order)))
                        Add(PdfARules.LayerOrder, Tr.T("Eine Ebenen-Konfiguration nennt nicht alle Ebenen in ihrer Reihenfolge (/Order)."));
                }
            }
            if (catalog.Get(PdfName.Get("OpenAction")) is PdfDictionary open) Action(open);
        }


        // ------------------------------------------------------------ 6.7 Logical structure (level a)

        /// <summary>
        /// Level a (ISO 19005-2, 6.7; ISO 19005-1, 6.8): the document is marked (/MarkInfo /Marked true), has a structure tree, every structure
        /// type of its elements is standard or mapped to one in the role map — without a circle, and (parts 2 and 3) without mapping a standard
        /// type —, every /Lang is a language tag.
        /// </summary>
        private void Structure(PdfDictionary catalog)
        {
            if (catalog.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked")) != true)
                Add(PdfARules.Marked, Tr.T("Das Dokument ist nicht als getaggt gekennzeichnet (/MarkInfo /Marked)."));
            Language(catalog.Get(PdfName.Get("Lang")));
            if (catalog.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root)
            {
                Add(PdfARules.StructureRoot, Tr.T("Das Dokument hat keinen Strukturbaum (/StructTreeRoot)."));
                return;
            }
            var roles = root.GetDictionary(PdfName.Get("RoleMap"));
            var part = _part.Part();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var types = new HashSet<string>(StringComparer.Ordinal);
            var budget = 500_000;
            void Walk(PdfObject? k, int depth)
            {
                if (k is null || depth > 256 || --budget < 0) return;
                switch (k)
                {
                    case PdfArray array:
                        for (var i = 0; i < array.Count && budget > 0; i++) Walk(array.Get(i), depth + 1);
                        break;
                    case PdfDictionary element when seen.Add(element) && !element.IsType(PdfName.Get("MCR")) && !element.IsType(PdfName.Get("OBJR")):
                        if (element.GetName(PdfName.Get("S"))?.Value is { } type) types.Add(type);
                        Language(element.Get(PdfName.Get("Lang")));
                        Walk(element.Get(PdfName.Get("K")), depth + 1);
                        break;
                }
            }
            Walk(root.Get(PdfName.Get("K")), 0);
            foreach (var type in types)
            {
                if (StandardStructureType(type, part))
                {
                    if (part != 1 && roles?.Get(PdfName.Get(type)) is PdfName)
                        Add(PdfARules.StandardTypesKept, Tr.T("Die Strukturart „{0}“ ist eine Standardart und wird trotzdem umgedeutet (/RoleMap).", type));
                    continue;
                }
                var chain = new HashSet<string>(StringComparer.Ordinal) { type };
                var current = type;
                while (true)
                {
                    if (roles?.Get(PdfName.Get(current)) is not PdfName mapped)
                    {
                        Add(PdfARules.StructureTypesMapped, Tr.T("Die Strukturart „{0}“ ist keine Standardart und auf keine abgebildet (/RoleMap).", type));
                        break;
                    }
                    if (StandardStructureType(mapped.Value, part)) break;
                    if (!chain.Add(mapped.Value))
                    {
                        Add(PdfARules.RoleMapNotCircular, Tr.T("Die Abbildung der Strukturarten bildet einen Kreis (/RoleMap, „{0}“).", type));
                        break;
                    }
                    current = mapped.Value;
                }
            }
        }

        /// <summary>A /Lang value: a language tag or empty (level a, 6.7.4).</summary>
        private void Language(PdfObject? value)
        {
            if (value is PdfString text && !LanguageTag(text, _part.Part()))
                Add(PdfARules.LanguageTag, Tr.T("Die Sprachangabe „{0}“ (/Lang) ist kein gültiges Sprachkürzel.", RawText(text).Replace("\0", "\\0")));
        }

        /// <summary>The signatures cover the whole file but their own value (6.4.3).</summary>
        private void Signatures(PdfDictionary catalog)
        {
            if (catalog.GetDictionary(PdfName.Get("AcroForm"))?.Get(PdfName.Get("Fields")) is not PdfArray fields) return;
            var docMdp = catalog.GetDictionary(PdfName.Get("Perms"))?.ContainsKey(PdfName.Get("DocMDP")) == true;
            var stack = new Stack<(PdfDictionary Field, int Depth)>();
            for (var i = 0; i < fields.Count && i < 10_000; i++)
                if (fields.Get(i) is PdfDictionary field) stack.Push((field, 0));
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            while (stack.TryPop(out var entry))
            {
                if (!seen.Add(entry.Field) || seen.Count > 100_000) continue;
                // a field of the tree (not a widget only: those are checked with the annotations) without triggers (6.4.1)
                if (entry.Field.GetName(PdfName.Subtype)?.Value != "Widget" && TriggersNotAllowed(entry.Field, TriggerOwner.Field))
                    Add(PdfARules.FieldActions, Tr.T("Ein Formularfeld hat Aktionen bei Eingaben (/AA)."));
                if (entry.Field.Get(PdfName.Get("V")) is PdfDictionary signature)
                {
                    if (signature.Get(PdfName.Get("ByteRange")) is PdfArray { Count: 4 } range && !Covers(range))
                        Add(PdfARules.SignatureCoversFile, Tr.T("Eine digitale Signatur deckt nicht die ganze Datei ab (/ByteRange)."));
                    // the CMS block: exactly one signer, whose certificate it carries (6.4.3)
                    if (signature.GetString(PdfName.Get("Contents")) is { } block && PdfKnead.Core.Pdf.Signatures.CmsSignature.Signers(block.Bytes) is var (signers, certificate))
                    {
                        if (!certificate) Add(PdfARules.SignatureCertificate, Tr.T("Eine digitale Signatur enthält das Zertifikat des Unterzeichners nicht."));
                        if (signers != 1) Add(PdfARules.SignatureOneSigner, Tr.T("Eine digitale Signatur hat nicht genau einen Unterzeichner ({0}).", signers));
                    }
                    // with DocMDP, the signature references compute no digest of their own (6.1.12)
                    if (docMdp && signature.Get(PdfName.Get("Reference")) is PdfArray references)
                        for (var i = 0; i < references.Count && i < 100; i++)
                            if (references.Get(i) is PdfDictionary reference &&
                                reference.Keys.Any(k => k.Value is "DigestLocation" or "DigestMethod" or "DigestValue"))
                                Add(PdfARules.SignatureReferenceDigest, Tr.T("Ein Signaturverweis nennt eine eigene Prüfsumme (/DigestValue …), obwohl DocMDP gilt."));
                }
                if (entry.Depth < 32 && entry.Field.Get(PdfName.Kids) is PdfArray kids)
                    for (var i = 0; i < kids.Count && i < 10_000; i++)
                        if (kids.Get(i) is PdfDictionary kid) stack.Push((kid, entry.Depth + 1));
            }
        }

        private bool Covers(PdfArray range)
        {
            if (range.GetNumber(0) is not 0 || range.GetNumber(1) is not { } gapStart || range.GetNumber(2) is not { } gapEnd || range.GetNumber(3) is not { } tail)
                return false;
            var (start, end) = ((long)gapStart, (long)gapEnd);
            // the file as it was signed: to its end or to the end of a revision (%%EOF) — later revisions (validation data, document time
            // stamps, further signatures) are incremental updates PDF/A allows; the reference validator judges the same (docs/FEHLERGEDAECHTNIS.md,
            // No. 715)
            var covered = end + (long)tail;
            return start > 0 && end > start && end <= doc.FileSize && (covered == doc.FileSize || covered < doc.FileSize && EndsRevision(covered)) &&
                   doc.FileBytes(start, 1)[0] == '<' && doc.FileBytes(end - 1, 1)[0] == '>';
        }

        /// <summary>Whether the file has a revision ending at <paramref name="end"/>: %%EOF before it, whitespace at most between.</summary>
        private bool EndsRevision(long end)
        {
            if (end < 5) return false;
            var tail = doc.FileBytes(Math.Max(0, end - 32), (int)Math.Min(32, end));
            var length = tail.Length;
            while (length > 0 && tail[length - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t' or 0) length--;
            return tail[..length].EndsWith("%%EOF"u8);
        }
    }
}
