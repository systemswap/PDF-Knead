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
    /// <summary>The rules on pages and their annotations.</summary>
    private sealed partial class Run
    {
        // ------------------------------------------------------------ Pages

        public void Page(PdfPage page)
        {
            var dict = page.Dictionary;
            _pageResources = page.Resources;   // what has no resources of its own draws with these (7.8.3, sentence 18; No. 791)
            if (TriggersNotAllowed(dict, TriggerOwner.Page)) Add(PdfARules.PageTriggers, Tr.T("Eine Seite hat Aktionen beim Öffnen oder Schließen (/AA)."));
            // PDF/A-4 (6.2.4.3): device colours may also rest on an output intent of the page or the colour space of its transparency group
            _pageIntent = Four && dict.GetArray(PdfName.Get("OutputIntents")) is { } pageIntents ? IntentSpace(pageIntents) : null;
            _pageIntentProfile = Four && dict.GetArray(PdfName.Get("OutputIntents")) is { } withProfile ? IntentProfileOf(withProfile) : null;
            // a page's output intents under the rules of the document's (6.2.3; docs/FEHLERGEDAECHTNIS.md, No. 520)
            if (Four && dict.GetArray(PdfName.Get("OutputIntents")) is { } ownIntents) IntentList(ownIntents, document: false);
            _pageBlend = Four && dict.GetDictionary(PdfName.Get("Group")) is { } pageGroup && pageGroup.GetName(PdfName.Get("S"))?.Value == "Transparency"
                ? BlendSpace(pageGroup.Get(PdfName.Get("CS")))
                : null;
            _blendProfile = BlendProfile(dict.GetDictionary(PdfName.Get("Group")));
            _blendGroup = _blendProfile is null ? null : dict.GetDictionary(PdfName.Get("Group"));
            _transparency = false;
            if (dict.ContainsKey(PdfName.Get("PresSteps"))) Add(PdfARules.NoPresSteps, Tr.T("Eine Seite hat Präsentationsschritte (/PresSteps)."));
            foreach (var key in (string[])["MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox"])
                // MediaBox and CropBox also as inherited from the page tree (docs/FEHLERGEDAECHTNIS.md, No. 497)
                if (BoxOf(dict, key) is { } given && PdfRectangle.From(given) is var box &&
                    !(box is { } b && Math.Abs(b.Width) is >= 3 and <= 14400 && Math.Abs(b.Height) is >= 3 and <= 14400 &&
                      // strict readers take only numbers there, not references to them
                      given is PdfArray numbers && !Enumerable.Range(0, numbers.Count).Any(i => numbers[i] is PdfReference)))
                    Add(PdfARules.PageBoxSize, Tr.T("Ein Seitenrahmen (/{0}) ist ungültig, kleiner als 3 oder größer als 14 400 Punkte.", key));
            var resources = page.Resources;
            if (dict.GetDictionary(PdfName.Get("Group")) is { } group) GroupSpace(group, resources, null);
            // the parts of the content: each written anew by the writer (one by one; a token across two parts is unusual)
            var parts = dict.Get(PdfName.Contents) switch
            {
                PdfStream single => [single],
                PdfArray array => Enumerable.Range(0, Math.Min(array.Count, 10_000)).Select(array.Get).OfType<PdfStream>().ToList(),
                _ => new List<PdfStream>(),
            };
            Rewrites?.UnionWith(parts);
            if (PageResources is not null && resources is not null)
                foreach (var part in parts) PageResources.TryAdd(part, resources);
            _streams = parts;
            _mcidsWithText = _part.Accessible() ? McidsWithText(dict) : null;
            if (PageContent(dict) is { } content && TopContent(content, resources) is var (faults, named))
            {
                if (faults != ContentFaults.None && Rewrites is null) Report(faults);
                // a page inherits its resources from the page tree: not explicitly its own (ISO 19005-2, 6.2.2; No. 497) — the writer puts
                // them on the page
                if (named && Rewrites is null && !dict.ContainsKey(PdfName.Resources) && resources is { Count: > 0 })
                    Add(PdfARules.OwnResources, Tr.T("Eine Seite erbt ihre Ressourcen vom Seitenbaum, statt eigene anzugeben."));
            }
            _streams = [];
            // the annotations count as well: their appearances (walked by Annotation) and an opacity or blend mode of their own
            // (docs/FEHLERGEDAECHTNIS.md, No. 496)
            if (dict.Get(PdfName.Get("Annots")) is PdfArray annots)
                for (var i = 0; i < annots.Count && i < 100_000; i++)
                    if (annots.Get(i) is PdfDictionary annot)
                    {
                        Annotation(annot);
                        if (TransparentAnnotation(annot)) _transparency = true;
                    }
            // without an output intent (of the document; part 4 also of the page) a page with transparency needs a group with a colour space
            if (_transparency && _intentSpace is null && _pageIntent is null &&
                dict.GetDictionary(PdfName.Get("Group"))?.HasValue(PdfName.Get("CS")) != true)
                Add(PdfARules.TransparencyNeedsSpace, Tr.T("Die Seite nutzt Transparenz, hat aber weder Ausgabebedingung noch einen Farbraum zum Mischen (/Group /CS)."));
        }

        /// <summary>Width and height of a rectangle from its four numbers (either may be 0); null if it is no rectangle of four numbers.</summary>
        private static (double Width, double Height)? Extent(PdfObject rect)
        {
            if (rect is not PdfArray { Count: >= 4 } a) return null;
            if (a.GetNumber(0) is not { } x1 || a.GetNumber(1) is not { } y1 || a.GetNumber(2) is not { } x2 || a.GetNumber(3) is not { } y2) return null;
            return double.IsFinite(x1 + y1 + x2 + y2) ? (Math.Abs(x2 - x1), Math.Abs(y2 - y1)) : null;
        }

        /// <summary>A page box: its own, MediaBox and CropBox also from the page tree (at most 32 levels up); null if there is none.</summary>
        private static PdfObject? BoxOf(PdfDictionary page, string key)
        {
            var name = PdfName.Get(key);
            var node = page;
            for (var depth = 0; node is not null && depth <= 32; depth++)
            {
                if (node.ContainsKey(name)) return node.Get(name);
                if (key is not ("MediaBox" or "CropBox")) return null;
                node = node.GetDictionary(PdfName.Parent);
            }
            return null;
        }

        /// <summary>An annotation drawn with an opacity below 1 (/CA, /ca) or a blend mode other than Normal or Compatible (/BM) of its own.</summary>
        private static bool TransparentAnnotation(PdfDictionary annot) =>
            annot.GetNumber(PdfName.Get("CA")) is < 1 || annot.GetNumber(PdfName.Get("ca")) is < 1 ||
            annot.Get(PdfName.Get("BM")) is PdfName { Value: not ("Normal" or "Compatible") } or PdfArray;

        /// <summary>The JPEG 2000 data of an image (its header is enough: the first megabyte).</summary>
        private byte[] JpxData(PdfStream stream)
        {
            try
            {
                return doc.DecodeStream(stream, 1 << 20).Data;
            }
            catch (PdfException)
            {
                return [];
            }
        }

        private byte[] StreamData(PdfStream stream)
        {
            try
            {
                return doc.GetStreamData(stream);
            }
            catch (PdfException)
            {
                return [];
            }
        }

        /// <summary>The faults of a content stream: reported, or the stream noted to be written anew.</summary>
        private void Faulty(ContentFaults faults, PdfStream stream)
        {
            // (the writer writes every content stream anew)
            if (faults != ContentFaults.None && Rewrites is null) Report(faults);
        }

        private void Report(ContentFaults faults)
        {
            if (faults.HasFlag(ContentFaults.UnknownOperator)) Add(PdfARules.KnownOperators, Tr.T("Der Seiteninhalt enthält unbekannte Operatoren."));
            if (faults.HasFlag(ContentFaults.OddHexString)) Add(PdfARules.HexEven, Tr.T("Eine hexadezimale Zeichenkette im Seiteninhalt hat eine ungerade Zahl von Ziffern."));
            if (faults.HasFlag(ContentFaults.NonHexDigit)) Add(PdfARules.HexDigits, Tr.T("Eine hexadezimale Zeichenkette enthält andere Zeichen als Ziffern."));
            if (faults.HasFlag(ContentFaults.IntegerOutOfRange)) Add(PdfARules.IntegerRange, Tr.T("Eine Zahl liegt außerhalb der Grenzen."));
            if (faults.HasFlag(ContentFaults.RealOutOfRange)) Add(PdfARules.RealRange, Tr.T("Eine Kommazahl liegt außerhalb der Grenzen."));
            if (faults.HasFlag(ContentFaults.RealNearZero)) Add(PdfARules.RealNearZero, Tr.T("Eine Kommazahl liegt näher an null als erlaubt."));
            if (faults.HasFlag(ContentFaults.ArrayTooLong)) Add(PdfARules.ArraySize, Tr.T("Eine Liste im Seiteninhalt hat mehr als 8 191 Einträge."));
            if (faults.HasFlag(ContentFaults.StringTooLong))
                Add(PdfARules.StringLength, Tr.T("Eine Zeichenkette ist länger als {0} Byte.", _part.Limits().String.ToString("N0", Tr.Culture)));
        }

        /// <summary>A form or Type 3 font without resources of its own uses some: noted to be given those it is drawn with, or reported.</summary>
        private void Borrowing(PdfDictionary owner, PdfDictionary? resources)
        {
            if (Borrowed is null) Add(PdfARules.OwnResources, Tr.T("Eine Form, ein Muster oder eine Schrift benutzt Ressourcen, ohne eigene anzugeben."));
            else if (resources is not null)
            {
                // drawn on several pages with other resources: what all of them give (the first one where names collide)
                if (!Borrowed.TryGetValue(owner, out var known)) Borrowed[owner] = resources;
                else if (!ReferenceEquals(known, resources)) Borrowed[owner] = Editing.ResourceUsage.Merged(known, resources);
            }
        }

        private byte[]? PageContent(PdfDictionary page)
        {
            try
            {
                switch (page.Get(PdfName.Contents))
                {
                    case PdfStream single:
                        return doc.GetStreamData(single);
                    case PdfArray parts:
                        // bounded together as one stream (No. 1002), counted while joined (No. 1003)
                        {
                            var all = new PdfKnead.Core.Pdf.Content.ContentJoiner(parts.Count);
                            for (var i = 0; i < parts.Count && i < 10_000; i++)
                                if (parts.Get(i) is PdfStream part) all.Add(doc.GetStreamData(part));
                            return all.ToArray();
                        }
                    default:
                        return null;
                }
            }
            catch (PdfException)
            {
                return null;
            }
        }

        private void Annotation(PdfDictionary annot)
        {
            var subtype = annot.GetName(PdfName.Subtype)?.Value;
            if (!AllowedAnnotation(subtype, _part))
            {
                Add(PdfARules.AnnotationTypes, string.Format(Tr.T("Das Dokument enthält eine unzulässige Anmerkungsart ({0})."), subtype ?? "?"));
                return;
            }
            if (subtype == "3D" && ThreeDData(annot) is { } threeD && !ThreeDKind(threeD))
                Add(PdfARules.ThreeDStream, Tr.T("Die 3D-Daten einer Anmerkung sind weder U3D noch PRC."));
            // the colours of 3D artwork in the space its stream names (ISO 32000-2, Table 311: DeviceRGB, CalRGB or ICC-based RGB; none named:
            // undefined): DeviceRGB there is a device colour, and without resources of its own only an RGB output intent covers it (ISO
            // 19005-4, 6.2.4.3; docs/FEHLERGEDAECHTNIS.md, No. 518). The background of a 3D view, DeviceRGB by Table 317, does not count — so the
            // test files for part 4e have it (CalRGB artwork without an output intent: valid)
            if (subtype == "3D" && Four && ThreeDData(annot) is { } artwork && artwork.Dictionary.Get(PdfName.Get("ColorSpace")) is { } artworkSpace and not PdfNull)
                ColorSpace(artworkSpace, null, 0);
            var flags = annot.GetInteger(PdfName.Get("F"));
            // the extent of /Rect from its four numbers: a rectangle of width 0 and height 550 has an area in the sense of 6.3.3 (the page
            // rectangle reader takes it for empty; docs/FEHLERGEDAECHTNIS.md, No. 499)
            var extent = Extent(annot.Get(PdfName.Get("Rect")));
            var point = extent is (0, 0);
            // PDF/A-1 (6.5.3) asks every annotation for its flags, pop-ups too, and knows no ToggleNoView (PDF 1.5)
            var one = _part.Part() == 1;
            if (flags is null && one) Add(PdfARules.AnnotationPrinted, Tr.T("Bei einer Anmerkung fehlen die Merkmale (/F)."));
            // (part 4: neither for an annotation whose rectangle is a point, 6.3.2)
            else if (flags is null && subtype != "Popup" && !(Four && point)) Add(PdfARules.AnnotationFlags, Tr.T("Bei einer Anmerkung fehlen die Merkmale (/F)."));
            if (flags is { } f && ((f & 4) == 0 || (f & (one ? 1 | 2 | 32 : 1 | 2 | 32 | 256)) != 0))
                Add(PdfARules.AnnotationPrinted, Tr.T("Eine Anmerkung wird nicht gedruckt oder ist verborgen."));
            if (annot.GetNumber(PdfName.Get("CA")) is { } opacity && opacity != 1)
                Add(PdfARules.AnnotationOpacity, Tr.T("Eine Anmerkung ist durchscheinend (/CA nicht 1)."));
            if (subtype == "Widget")
            {
                if (annot.ContainsKey(PdfName.Get("A"))) Add(PdfARules.WidgetActions, Tr.T("Ein Formularfeld hat eine Aktion (/A)."));
                if (TriggersNotAllowed(annot, TriggerOwner.Widget)) Add(PdfARules.WidgetActions, Tr.T("Ein Formularfeld hat Aktionen bei Eingaben (/AA)."));
            }
            else if (TriggersNotAllowed(annot, TriggerOwner.Annotation))
                Add(PdfARules.AnnotationTriggers, Tr.T("Eine Anmerkung hat Aktionen, die PDF/A-4 nicht zulässt (/AA)."));
            // a widget that is its field as well (6.4.1: the field has no /AA either)
            if (annot.ContainsKey(PdfName.Get("FT")) && TriggersNotAllowed(annot, TriggerOwner.Field))
                Add(PdfARules.FieldActions, Tr.T("Ein Formularfeld hat Aktionen bei Eingaben (/AA)."));
            if (_intentSpace is not IccProfile.RgbSpace && (annot.ContainsKey(PdfName.Get("C")) || annot.ContainsKey(PdfName.Get("IC"))))
                Add(PdfARules.AnnotationColours, Tr.T("Eine Anmerkung hat Farben (/C, /IC), die Ausgabebedingung ist aber nicht RGB."));
            // an appearance for every annotation but pop-ups, links (and projections) whose /Rect is more than a point (6.3.3)
            // PDF/A-1 (6.9) asks only form fields for an appearance
            // (PDF/A-1, 6.9: a widget needs one even without an area)
            var hasArea = one ? subtype == "Widget" : extent is not null && !point;
            if (annot.GetDictionary(PdfName.Get("AP")) is not { } ap)
            {
                if (subtype is not ("Popup" or "Link" or "Projection") && hasArea) Add(PdfARules.AnnotationAppearance, Tr.T("Eine Anmerkung hat kein Erscheinungsbild."));
                return;
            }
            // only the normal appearance, and that one (an empty /AP is none of the forms 6.3.3 allows)
            if (ap.Entries.Any(e => e.Key.Value != "N")) Add(PdfARules.AppearanceOnlyNormal, Tr.T("Das Erscheinungsbild einer Anmerkung hat mehr als das normale Aussehen."));
            else if (ap.Count == 0) Add(PdfARules.AppearanceOnlyNormal, Tr.T("Das Erscheinungsbild einer Anmerkung ist leer (/AP ohne /N)."));
            var isButton = subtype == "Widget" && FieldType(annot) == "Btn";
            switch (ap.Get(PdfName.Get("N")))
            {
                case PdfStream look:
                    if (isButton) Add(PdfARules.ButtonAppearanceStates, Tr.T("Das Erscheinungsbild eines Schaltfelds hat keine Zustände."));
                    TopForm(look);
                    break;
                case PdfDictionary states:
                    if (!isButton) Add(PdfARules.AppearanceStream, Tr.T("Das Erscheinungsbild einer Anmerkung ist kein einzelnes Bild."));
                    var looks = 0;
                    foreach (var (key, _) in states.Entries)
                        if (states.Get(key) is PdfStream stateLook)
                        {
                            looks++;
                            TopForm(stateLook);
                        }
                    if (looks == 0) Add(PdfARules.ButtonAppearanceStates, Tr.T("Das Erscheinungsbild eines Schaltfelds hat keinen Zustand mit einem Bild."));
                    break;
                // a normal appearance that refers to nothing (a broken reference) is none of the forms 6.3.3 allows — also for a link
                case not null when ap.ContainsKey(PdfName.Get("N")) && ap.Get(PdfName.Get("N")) is PdfNull:
                case null when ap.ContainsKey(PdfName.Get("N")):
                    Add(isButton ? PdfARules.ButtonAppearanceStates : PdfARules.AppearanceStream, Tr.T("Das Erscheinungsbild einer Anmerkung verweist auf nichts (/N)."));
                    break;
                default:
                    if (subtype is not ("Popup" or "Link" or "Projection") && hasArea) Add(PdfARules.AnnotationAppearance, Tr.T("Eine Anmerkung hat kein Erscheinungsbild."));
                    break;
            }
        }

        private static string? FieldType(PdfDictionary field)
        {
            for (var depth = 0; depth < 32 && field is not null; depth++)
            {
                if (field.GetName(PdfName.Get("FT"))?.Value is { } type) return type;
                field = field.GetDictionary(PdfName.Parent)!;
            }
            return null;
        }
    }
}
