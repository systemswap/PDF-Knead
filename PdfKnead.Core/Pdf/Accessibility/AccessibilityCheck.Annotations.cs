using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        private static readonly PdfName ContentsKey = PdfName.Get("Contents");

        /// <summary>
        /// Stage D (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 661): what annotations say of themselves, without their place in
        /// the structure — the order of the tab key (ISO 14289-1, 7.18.3; -2, 8.9.3.3), types not allowed (7.18.2; 8.9.2.4.11, 8.9.2.4.15), the
        /// descriptions links, stamps, ink, screen, 3D and rich media annotations need (7.18.1, 7.18.5; 8.9.2.4.7/8/12/19), media clips with a
        /// content type and a description (7.18.6.2), rich text equal to the plain one (8.9.2.3), the file of an attachment with its
        /// relationship (8.9.2.4.10), pop-ups outside the tree (8.9.2.4.9), widgets with actions described (8.10.2.3) and a rich value of a text
        /// field equal to its value (8.10.3.3). Part 1 passes over annotations that are hidden or lie outside the crop box, as the rules allow.
        /// </summary>
        public void Annotations()
        {
            var visited = 0;
            foreach (var page in doc.Pages)
            {
                cancel.ThrowIfCancellationRequested();
                if (page.Dictionary.Get(Annots) is not PdfArray annots || annots.Count == 0) continue;
                var number = page.Index + 1;
                var tabs = page.Dictionary.GetName(PdfName.Get("Tabs"))?.Value;
                if (part == 1 && tabs != "S")
                    Add(AccessibilityRules.TabOrder, Tr.T("Eine Seite mit Anmerkungen nennt nicht die Reihenfolge der Struktur (/Tabs /S)."), number);
                if (part == 2 && tabs is not ("A" or "W" or "S"))
                    Add(AccessibilityRules.TabOrder, Tr.T("Eine Seite mit Anmerkungen nennt keine Reihenfolge (/Tabs /A, /W oder /S)."), number);
                for (var i = 0; i < annots.Count && i < 10_000 && ++visited < MaxElements; i++)
                    if (annots.Get(i) is PdfDictionary annot) Annotation(annot, page, number);
            }
            if (part == 2) TextFieldValues();
        }

        /// <summary>The rules of stage D for one annotation.</summary>
        private void Annotation(PdfDictionary annot, PdfPage page, int number)
        {
            var subtype = annot.GetName(PdfName.Subtype)?.Value;
            var contents = annot.Get(ContentsKey) as PdfString;
            var described = contents is not null && PdfACheck.RawText(contents).Length > 0;
            if (part == 1)
            {
                // hidden or outside the crop box: none of these rules applies (as the rules of part 1 say)
                if (((annot.GetInteger(PdfName.F) ?? 0) & 2) != 0 || OutsideCropBox(annot, page)) return;
                if (subtype == "TrapNet") Add(AccessibilityRules.TrapNet, Tr.T("Die Datei enthält eine Anmerkung der Art TrapNet."), number);
                if (subtype == "Link" && !described) Add(AccessibilityRules.LinkDescription, Tr.T("Ein Link beschreibt nicht, wohin er führt (/Contents)."), number);
                if (subtype != "Widget" && !described && !AltInStructure(annot))
                    Add(AccessibilityRules.AnnotationDescription, Tr.T("Eine Anmerkung hat weder /Contents noch eine Beschreibung (/Alt) in der Struktur."), number);
                MediaClips(annot, number);
                return;
            }
            switch (subtype)
            {
                case "TrapNet":
                    Add(AccessibilityRules.TrapNet, Tr.T("Die Datei enthält eine Anmerkung der Art TrapNet."), number);
                    break;
                case "Sound":
                    Add(AccessibilityRules.SoundAnnotation, Tr.T("Die Datei enthält eine Ton-Anmerkung (Sound); PDF/UA-2 erlaubt sie nicht."), number);
                    break;
                case "Movie":
                    Add(AccessibilityRules.MovieAnnotation, Tr.T("Die Datei enthält eine Film-Anmerkung (Movie); PDF/UA-2 erlaubt sie nicht."), number);
                    break;
                case "Stamp" when contents is null && !annot.ContainsKey(PdfName.Get("Name")):
                    Add(AccessibilityRules.StampDescription, Tr.T("Ein Stempel sagt weder mit /Name noch mit /Contents, was er bedeutet."), number);
                    break;
                case "Ink" when contents is null:
                    Add(AccessibilityRules.InkDescription, Tr.T("Eine Freihand-Anmerkung beschreibt nicht, was sie meint (/Contents)."), number);
                    break;
                case "Screen" when contents is null:
                    Add(AccessibilityRules.ScreenDescription, Tr.T("Eine Bildschirm-Anmerkung (Screen) hat keine Beschreibung (/Contents)."), number);
                    break;
                case "3D" when contents is null:
                    Add(AccessibilityRules.ThreeDDescription, Tr.T("Eine 3D-Anmerkung hat keine Beschreibung (/Contents)."), number);
                    break;
                case "RichMedia" when contents is null:
                    Add(AccessibilityRules.RichMediaDescription, Tr.T("Eine Multimedia-Anmerkung (RichMedia) hat keine Beschreibung (/Contents)."), number);
                    break;
                case "Popup" when annot.ContainsKey(PdfName.Get("StructParent")):
                    Add(AccessibilityRules.PopupInTree, Tr.T("Ein Pop-up einer Anmerkung steht im Strukturbaum."), number);
                    break;
                case "FileAttachment" when annot.Get(PdfName.Get("FS")) is PdfDictionary fs && !fs.ContainsKey(PdfName.Get("AFRelationship")):
                    Add(AccessibilityRules.AttachmentRelationship, Tr.T("Die Datei einer Dateianhang-Anmerkung nennt nicht ihre Beziehung zum Dokument (/AFRelationship)."), number);
                    break;
                case "Widget" when annot.ContainsKey(PdfName.Get("AA")) && contents is null && IsFieldWidget(annot) && InStructure(annot):
                    Add(AccessibilityRules.WidgetActionDescription, Tr.T("Ein Formularfeld mit eigenen Aktionen (/AA) hat keine Beschreibung (/Contents)."), number);
                    break;
            }
            // a markup annotation with rich text: its plain text the same (8.9.2.3)
            if (contents is not null && RichText(annot.Get(PdfName.Get("RC"))) is { } rich && Normalised(rich) != Normalised(PdfACheck.RawText(contents)))
                Add(AccessibilityRules.RichTextContents, Tr.T("Der formatierte Text einer Anmerkung (/RC) sagt etwas anderes als ihr /Contents."), number);
        }

        /// <summary>The rectangle of an annotation lies wholly outside the page's crop box (touching its edge from outside counts as outside).</summary>
        private static bool OutsideCropBox(PdfDictionary annot, PdfPage page)
        {
            // the four numbers as written (a rectangle without area is no rectangle to PdfRectangle.From, but it has a place)
            if (annot.Get(PdfName.Get("Rect")) is not PdfArray { Count: 4 } numbers ||
                Enumerable.Range(0, 4).Select(i => numbers.Get(i) switch { PdfInteger n => (double?)n.Value, PdfReal r => r.Value, _ => null }).ToList() is not
                    [{ } x1, { } y1, { } x2, { } y2])
                return false;
            var crop = page.CropBox;
            return Math.Max(x1, x2) <= crop.Left || Math.Min(x1, x2) >= crop.Right || Math.Max(y1, y2) <= crop.Bottom || Math.Min(y1, y2) >= crop.Top;
        }

        /// <summary>The structure element an annotation is attached to through /StructParent; null if none.</summary>
        private PdfDictionary? StructureOf(PdfDictionary annot)
        {
            if (NumberTree.KeyOf(annot.Get(PdfName.Get("StructParent"))) is not { } key ||
                Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.GetDictionary(PdfName.Get("ParentTree")) is not { } parents)
                return null;
            var value = NumberTree.Value(parents, key);
            return (value is PdfReference r ? doc.Resolve(r.Id) : value) is PdfDictionary element && element.ContainsKey(PdfName.Get("S")) ? element : null;
        }

        /// <summary>The structure element of an annotation has a description (/Alt, not empty).</summary>
        private bool AltInStructure(PdfDictionary annot) => StructureOf(annot) is { } element && Text(element.Get(PdfName.Get("Alt"))) is { Length: > 0 };

        /// <summary>An annotation is real content in the structure: attached to an element that does not stand for an artifact.</summary>
        private bool InStructure(PdfDictionary annot) =>
            StructureOf(annot) is { } element && StandardStructure.Resolve(element, Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")), o => o is PdfReference r ? doc.Resolve(r.Id) : o)?.Name != "Artifact";

        /// <summary>A widget of a form field (the field type on itself or up its /Parent chain).</summary>
        private static bool IsFieldWidget(PdfDictionary widget)
        {
            var node = widget;
            for (var i = 0; i < 32 && node is not null; i++)
            {
                if (node.ContainsKey(PdfName.Get("FT"))) return true;
                node = node.Get(PdfName.Get("Parent")) as PdfDictionary;
            }
            return false;
        }

        /// <summary>
        /// Media clips reached from an annotation's actions (/A, /AA, renditions and their clips, a few levels deep): each needs a content type
        /// (/CT) and a description (/Alt: pairs of language and text) — ISO 14289-1, 7.18.6.2.
        /// </summary>
        private void MediaClips(PdfDictionary annot, int number)
        {
            var stack = new Stack<(PdfObject? Node, int Depth)>([(annot.Get(PdfName.Get("A")), 0), (annot.Get(PdfName.Get("AA")), 0)]);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            while (stack.Count > 0 && seen.Count < 1000)
            {
                var (node, depth) = stack.Pop();
                if (depth > 8) continue;
                switch (node)
                {
                    case PdfArray list:
                        for (var i = 0; i < list.Count && i < 100; i++) stack.Push((list.Get(i), depth + 1));
                        break;
                    case PdfDictionary dict when seen.Add(dict):
                        if (dict.GetName(PdfName.Get("S"))?.Value == "MCD")
                        {
                            if (!dict.ContainsKey(PdfName.Get("CT")))
                                Add(AccessibilityRules.MediaClipType, Tr.T("Ein Medienclip nennt nicht die Art seines Inhalts (/CT)."), number);
                            if (!DescribedClip(dict))
                                Add(AccessibilityRules.MediaClipDescription, Tr.T("Ein Medienclip hat keine Beschreibung (/Alt)."), number);
                        }
                        foreach (var key in new[] { "R", "C", "D", "Next", "RT" })
                            if (dict.Get(PdfName.Get(key)) is { } inner) stack.Push((inner, depth + 1));
                        if (ReferenceEquals(node, annot.Get(PdfName.Get("AA"))))
                            foreach (var (key, _) in dict.Entries.Take(32)) stack.Push((dict.Get(key), depth + 1));
                        break;
                }
            }
        }

        /// <summary>A media clip's /Alt: an array of pairs of language (may be empty) and text (not empty), at least one.</summary>
        private static bool DescribedClip(PdfDictionary clip) =>
            clip.Get(PdfName.Get("Alt")) is PdfArray { Count: > 0 } alt && alt.Count % 2 == 0 &&
            Enumerable.Range(0, alt.Count / 2).All(i => alt.Get(2 * i) is PdfString && alt.Get(2 * i + 1) is PdfString text && PdfACheck.RawText(text).Length > 0);

        /// <summary>The plain text of rich text (/RC as a string or stream of XHTML): its text nodes with their entities read; null if none.</summary>
        private string? RichText(PdfObject? rc)
        {
            string? xml = rc switch
            {
                PdfString s => PdfACheck.RawText(s),
                PdfStream { DataLength: <= 1 << 20 } stream => System.Text.Encoding.UTF8.GetString(doc.GetStreamData(stream)),
                _ => null,
            };
            if (xml is null) return null;
            var text = Regex.Replace(xml, "<[^>]*>", " ");
            return System.Net.WebUtility.HtmlDecode(text);
        }

        /// <summary>Text with all white space as single spaces, trimmed.</summary>
        private static string Normalised(string text) => Regex.Replace(text, @"\s+", " ").Trim();

        /// <summary>PDF/UA-2, 8.10.3.3: a text field with a rich value (/RV) has a value (/V) with the same text.</summary>
        private void TextFieldValues()
        {
            if (Catalog?.GetDictionary(PdfName.Get("AcroForm"))?.Get(PdfName.Get("Fields")) is not PdfArray fields) return;
            var stack = new Stack<(PdfObject? Node, string? Type, int Depth)>();
            for (var i = fields.Count - 1; i >= 0 && i >= fields.Count - 10_000; i--) stack.Push((fields.Get(i), null, 0));
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            while (stack.Count > 0 && seen.Count < MaxElements)
            {
                var (node, inherited, depth) = stack.Pop();
                if (node is not PdfDictionary field || depth > 32 || !seen.Add(field)) continue;
                var type = field.GetName(PdfName.Get("FT"))?.Value ?? inherited;
                // only a field with a rich value (a missing /RV reads as the null object: before, every text field with a plain value was
                // reported; docs/FEHLERGEDAECHTNIS.md, No. 1004)
                if (type == "Tx" && field.Get(PdfName.Get("RV")) is { } rv and not PdfNull &&
                    (field.Get(PdfName.Get("V")) is not PdfString value || RichText(rv) is not { } rich || Normalised(rich) != Normalised(PdfACheck.RawText(value))))
                    Add(AccessibilityRules.RichValue, Tr.T("Ein Textfeld hat einen formatierten Wert (/RV), aber keinen gleichen einfachen Wert (/V)."));
                if (field.Get(PdfName.Kids) is PdfArray kids)
                    for (var k = 0; k < kids.Count && k < 10_000; k++) stack.Push((kids.Get(k), type, depth + 1));
            }
        }
    }
}
