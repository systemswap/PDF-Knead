using System.Text.RegularExpressions;
using System.Xml;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        private static readonly PdfName Lang = PdfName.Get("Lang"), MarkInfo = PdfName.Get("MarkInfo"), Annots = PdfName.Get("Annots");

        /// <summary>Stage A: the rules about the document as a whole (docs/ABLAUFPLAN.md, 6.7 step 3 A).</summary>
        public void Document()
        {
            var xmp = Xmp();
            Identification(xmp);
            Version();
            Tagging();
            Metadata(xmp);
            Languages(xmp);
            Layers();
            EmbeddedFiles();
            Xfa();
            Permissions();
        }

        /// <summary>ISO 14289-1 and -2, clause 5: the identification in the XMP, its part, its prefixes; for part 2 the revision 2024.</summary>
        private void Identification(XmlDocument? xmp)
        {
            var properties = xmp is null ? [] : TopLevel(xmp).Where(n => n.NamespaceURI == AccessibilityClaims.PdfUaId).ToList();
            var partNode = properties.FirstOrDefault(n => n.LocalName == "part");
            if (partNode is null)
            {
                Add(AccessibilityRules.Identification, Tr.T("Die Metadaten nennen keine PDF/UA-Kennung (pdfuaid:part)."));
                return;
            }
            if (partNode.InnerText.Trim() != part.ToString(System.Globalization.CultureInfo.InvariantCulture))
                Add(AccessibilityRules.IdentificationPart, Tr.T("Die PDF/UA-Kennung nennt Teil {0}; geprüft wird PDF/UA-{1}.", partNode.InnerText.Trim(), part));
            foreach (var node in properties.Where(n => n.Prefix != "pdfuaid"))
            {
                var rule = node.LocalName switch
                {
                    "part" => AccessibilityRules.PartPrefix,
                    "amd" when part == 1 => AccessibilityRules.SecondPrefix,
                    "rev" when part == 2 => AccessibilityRules.SecondPrefix,
                    "corr" when part == 1 => AccessibilityRules.ThirdProperty,
                    _ => null,
                };
                if (rule is not null) Add(rule, Tr.T("Die Eigenschaft {0} der PDF/UA-Kennung steht nicht unter dem Präfix pdfuaid.", node.LocalName));
            }
            if (part == 2 && properties.FirstOrDefault(n => n.LocalName == "rev")?.InnerText.Trim() != "2024")
                Add(AccessibilityRules.ThirdProperty, Tr.T("Die PDF/UA-Kennung nennt nicht die Ausgabe 2024 (pdfuaid:rev)."));
        }

        /// <summary>The version: PDF/UA-1 a header 1.0 to 1.7 (ISO 14289-1, 6.1), PDF/UA-2 PDF 2.0 (ISO 14289-2, 6.1).</summary>
        private void Version()
        {
            if (part == 1)
            {
                if (!Regex.IsMatch(doc.HeaderVersion, "^1\\.[0-7]$")) Add(AccessibilityRules.Version, Tr.T("Die Datei nennt im Kopf die Version {0}; PDF/UA-1 erlaubt 1.0 bis 1.7.", doc.HeaderVersion));
            }
            else if (PdfWriter.CompareVersions(doc.Version, "2.0") < 0)
                Add(AccessibilityRules.Version, Tr.T("Die Datei ist PDF {0}; PDF/UA-2 verlangt PDF 2.0.", doc.Version));
        }

        /// <summary>The marks of a tagged file: /MarkInfo /Marked true, no /Suspects true (PDF/UA-1), a structure tree.</summary>
        private void Tagging()
        {
            var markInfo = Catalog?.GetDictionary(MarkInfo);
            if (markInfo?.Get(PdfName.Get("Marked")) is not PdfBoolean { Value: true })
                Add(AccessibilityRules.Marked, Tr.T("Der Katalog kennzeichnet die Datei nicht als getaggt (/MarkInfo /Marked true)."));
            if (markInfo?.Get(PdfName.Get("Suspects")) is PdfBoolean { Value: true })
                Add(AccessibilityRules.Suspects, Tr.T("Der Katalog meldet zweifelhafte Tags (/MarkInfo /Suspects true)."));
            if (Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is null)
                Add(AccessibilityRules.StructTreeRoot, Tr.T("Die Datei hat keine Struktur (/StructTreeRoot): Vorleseprogramme kennen weder Lesereihenfolge noch Überschriften."));
        }

        /// <summary>Metadata stream, title in it, the title shown instead of the file name.</summary>
        private void Metadata(XmlDocument? xmp)
        {
            if (Catalog?.Get(PdfName.Metadata) is not PdfStream)
                Add(AccessibilityRules.Metadata, Tr.T("Die Datei hat keine Metadaten (XMP) im Katalog."));
            var title = xmp?.GetElementsByTagName("title", "http://purl.org/dc/elements/1.1/").OfType<XmlElement>().FirstOrDefault();
            if (title is null || title.InnerText.Trim().Length == 0)
                Add(AccessibilityRules.Title, Tr.T("Die Metadaten nennen keinen Titel (dc:title)."));
            if (Catalog?.GetDictionary(PdfName.Get("ViewerPreferences"))?.Get(PdfName.Get("DisplayDocTitle")) is not PdfBoolean { Value: true })
                Add(AccessibilityRules.DisplayDocTitle, Tr.T("Das Anzeigeprogramm zeigt den Dateinamen statt des Titels (/DisplayDocTitle)."));
        }

        /// <summary>
        /// Languages: every /Lang valid (catalogue and structure elements); PDF/UA-2 a language in the catalogue; PDF/UA-1 the language of the
        /// outline entries, annotation and field texts, metadata and page text determinable — without a language in the catalogue only through
        /// the structure.
        /// </summary>
        private void Languages(XmlDocument? xmp)
        {
            var catalogLang = Text(Catalog?.Get(Lang));
            if (catalogLang is not null) Tag(catalogLang, Tr.T("/Lang im Katalog"));
            foreach (var element in Elements())
                if (Text(element.Get(Lang)) is { } lang) Tag(lang, Tr.T("/Lang an einem Strukturelement"));
            foreach (var page in doc.Pages)
            {
                cancel.ThrowIfCancellationRequested();
                foreach (var lang in MarkedLanguages(page)) Tag(lang, Tr.T("/Lang im Inhalt der Seite"), page.Index + 1);
            }
            if (catalogLang is { Length: > 0 }) return;
            var proposal = new Lazy<string?>(() => PdfATagging.ProposeLanguage([.. doc.Pages.Take(5).Select(p => (doc, p))], cancel) is { Length: > 0 } p ? p : null);
            if (part == 2)
            {
                Add(AccessibilityRules.CatalogLanguage, Tr.T("Der Katalog nennt keine Sprache des Dokuments (/Lang)."), suggestion: proposal.Value);
                return;
            }
            if (Catalog?.GetDictionary(PdfName.Get("Outlines"))?.Get(PdfName.Get("First")) is PdfDictionary)
                Add(AccessibilityRules.OutlineLanguage, Tr.T("Die Sprache der Lesezeichen ist nicht bestimmt: der Katalog nennt keine Sprache (/Lang)."), suggestion: proposal.Value);
            var anyStructureLanguage = Elements().Any(e => Text(e.Get(Lang)) is { Length: > 0 });
            foreach (var page in doc.Pages)
            {
                cancel.ThrowIfCancellationRequested();
                if (page.Dictionary.Get(Annots) is PdfArray annots)
                    for (var i = 0; i < annots.Count && i < 10_000; i++)
                    {
                        if (annots.Get(i) is not PdfDictionary annot) continue;
                        if (Text(annot.Get(PdfName.Contents)) is { Length: > 0 } && !LanguageInStructure(annot))
                            Add(AccessibilityRules.AnnotationLanguage, Tr.T("Die Sprache des Texts einer Anmerkung (/Contents) ist nicht bestimmt."), page.Index + 1, proposal.Value);
                        // the field's own Form element (or the catalogue) — as the reference checker reads it
                        if (annot.GetName(PdfName.Subtype)?.Value == "Widget" && FieldTooltip(annot) && !LanguageInStructure(annot, own: true))
                            Add(AccessibilityRules.FieldLanguage, Tr.T("Die Sprache der Beschreibung eines Formularfelds (/TU) ist nicht bestimmt."), page.Index + 1, proposal.Value);
                    }
                if (!anyStructureLanguage && !_found.Contains("7.2-34") && ShowsText(page))
                    Add(AccessibilityRules.PageLanguage, Tr.T("Die Sprache des Texts auf den Seiten ist nicht bestimmt: weder der Katalog noch die Struktur nennt eine Sprache."),
                        page.Index + 1, proposal.Value);
            }
            // the metadata: a title only as x-default says nothing about its language
            if (xmp?.GetElementsByTagName("title", "http://purl.org/dc/elements/1.1/").OfType<XmlElement>().FirstOrDefault() is { } title &&
                title.GetElementsByTagName("li", XmpSchemas.Rdf).OfType<XmlElement>().All(li => li.GetAttribute("lang", XmpSchemas.Xml) is "" or "x-default"))
                Add(AccessibilityRules.MetadataLanguage, Tr.T("Die Sprache der Metadaten ist nicht bestimmt: der Katalog nennt keine Sprache (/Lang)."), suggestion: proposal.Value);
        }

        /// <summary>
        /// A language tag found somewhere: of the wrong form or empty → rule (7.2-29, 8.4.4-2); of the right form but with a language subtag no
        /// language has → heuristic H11 with the tag meant.
        /// </summary>
        private void Tag(string lang, string where, int? page = null)
        {
            var shown = lang.Replace("\0", "\\0");
            if (lang.Length == 0)
                Add(AccessibilityRules.LanguageTag, Tr.T("Eine Sprachangabe ({0}) ist leer: die Sprache ist nicht bestimmt.", where), page);
            else if (!PdfACheck.LanguageTag(lang, 2))
                Add(AccessibilityRules.LanguageTag, Tr.T("Die Sprache „{0}“ ({1}) ist kein gültiges Sprachkürzel.", shown, where), page, LanguageTags.Proposal(lang));
            else if (!LanguageTags.Valid(lang))
                Add(AccessibilityRules.LanguageKnown, Tr.T("Die Sprache „{0}“ ({1}) ist kein bekanntes Sprachkürzel — vermutlich der Name einer Sprache.", shown, where),
                    page, LanguageTags.Proposal(lang));
        }

        /// <summary>Whether the field of a widget (the widget itself or a parent) has a tooltip (/TU).</summary>
        private static bool FieldTooltip(PdfDictionary widget)
        {
            var field = widget;
            for (var i = 0; i < 32 && field is not null; i++)
            {
                if (Text(field.Get(PdfName.Get("TU"))) is { Length: > 0 }) return true;
                field = field.Get(PdfName.Get("Parent")) as PdfDictionary;
            }
            return false;
        }

        /// <summary>
        /// Layer configurations (ISO 14289-1, 7.10; -2, 8.7): each with a name that is not empty — in PDF/UA-2 only where /Configs is there —
        /// and none with /AS.
        /// </summary>
        private void Layers()
        {
            if (Catalog?.GetDictionary(PdfName.Get("OCProperties")) is not { } properties) return;
            var configs = new List<PdfDictionary>();
            if (properties.GetDictionary(PdfName.Get("D")) is { } defaults) configs.Add(defaults);
            var listed = properties.Get(PdfName.Get("Configs")) as PdfArray;
            if (listed is not null)
                for (var i = 0; i < listed.Count && i < 1000; i++)
                    if (listed.Get(i) is PdfDictionary config) configs.Add(config);
            if (part == 1 || listed is not null)
                foreach (var config in configs)
                    if (Text(config.Get(PdfName.Get("Name"))) is not { Length: > 0 })
                    {
                        Add(AccessibilityRules.LayerName, Tr.T("Eine Konfiguration der Ebenen hat keinen Namen (/Name)."));
                        break;
                    }
            if (configs.Any(c => c.ContainsKey(PdfName.Get("AS"))))
                Add(AccessibilityRules.LayerAutoState, Tr.T("Eine Konfiguration der Ebenen schaltet Ebenen selbsttätig um (/AS)."));
        }

        /// <summary>
        /// Embedded files: PDF/UA-1 /F and /UF in each file specification of the EmbeddedFiles tree and of file attachment annotations (7.11,
        /// 7.18.7); PDF/UA-2 a description (/Desc) in each of the tree (8.14.1).
        /// </summary>
        private void EmbeddedFiles()
        {
            if (Catalog?.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("EmbeddedFiles")) is { } tree)
                foreach (var value in NameTreeValues(tree))
                {
                    if (value is not PdfDictionary spec) continue;
                    if (part == 1 && !Named(spec)) Add(AccessibilityRules.EmbeddedFileNames, Tr.T("Eine angehängte Datei hat nicht beide Dateinamen (/F und /UF)."));
                    if (part == 2 && Text(spec.Get(PdfName.Get("Desc"))) is not { Length: > 0 })
                        Add(AccessibilityRules.EmbeddedFileDescription, Tr.T("Eine angehängte Datei hat keine Beschreibung (/Desc)."));
                }
            if (part != 1) return;
            foreach (var page in doc.Pages)
                if (page.Dictionary.Get(Annots) is PdfArray annots)
                    for (var i = 0; i < annots.Count && i < 10_000; i++)
                        if (annots.Get(i) is PdfDictionary annot && annot.GetName(PdfName.Subtype)?.Value == "FileAttachment" && annot.Get(PdfName.Get("FS")) is PdfDictionary fs && !Named(fs))
                            Add(AccessibilityRules.AttachmentAnnotationNames, Tr.T("Die Datei einer Dateianhang-Anmerkung hat nicht beide Dateinamen (/F und /UF)."), page.Index + 1);

            static bool Named(PdfDictionary spec) => Text(spec.Get(PdfName.Get("F"))) is { Length: > 0 } && Text(spec.Get(PdfName.Get("UF"))) is { Length: > 0 };
        }

        /// <summary>XFA: PDF/UA-1 no dynamic form (dynamicRender required, ISO 14289-1, 7.15); PDF/UA-2 none at all (8.10.1).</summary>
        private void Xfa()
        {
            if (Catalog?.GetDictionary(PdfName.Get("AcroForm"))?.Get(PdfName.Get("XFA")) is not { } xfa || xfa is PdfNull) return;
            if (part == 2)
            {
                Add(AccessibilityRules.Xfa, Tr.T("Die Datei enthält ein XFA-Formular."));
                return;
            }
            var streams = xfa switch
            {
                PdfStream single => [single],
                PdfArray parts => Enumerable.Range(0, parts.Count).Select(parts.Get).OfType<PdfStream>().ToList(),
                _ => new List<PdfStream>(),
            };
            foreach (var stream in streams)
            {
                string text;
                try
                {
                    text = System.Text.Encoding.UTF8.GetString(doc.GetStreamData(stream));
                }
                catch (PdfException)
                {
                    continue;
                }
                if (Regex.IsMatch(text, @"<(?:\w+:)?dynamicRender\b[^>]*>\s*required\s*</(?:\w+:)?dynamicRender\s*>"))
                {
                    Add(AccessibilityRules.Xfa, Tr.T("Die Datei enthält ein dynamisches XFA-Formular (dynamicRender required)."));
                    return;
                }
            }
        }

        /// <summary>PDF/UA-1, 7.16: an encrypted file has /P with bit 10 set — assistive technology may read it.</summary>
        private void Permissions()
        {
            if (doc.Trailer.Get(PdfName.Get("Encrypt")) is not PdfDictionary encrypt) return;
            if (encrypt.Get(PdfName.Get("P")).AsNumber() is not { } p)
                Add(AccessibilityRules.Permissions, Tr.T("Die Datei ist geschützt, nennt aber keine Rechte (/P)."));
            else if (((long)p & 1 << 9) == 0)
                Add(AccessibilityRules.Permissions, Tr.T("Die Datei ist geschützt und verbietet Hilfsprogrammen das Auslesen (Rechte-Bit 10)."));
        }
    }
}
