using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        /// <summary>The markup annotations (ISO 32000-2, 12.5.6.2, Table 171 as ISO 14289-2, 8.9.2.3 lists them).</summary>
        private static readonly HashSet<string> Markup = new(["Text", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline",
            "Squiggly", "StrikeOut", "Caret", "Stamp", "Ink", "FileAttachment", "Redact"], StringComparer.Ordinal);

        /// <summary>
        /// Stage F (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 665): annotations and their place in the structure tree — the
        /// element their /StructParent names, by its standard type. PDF/UA-1: annotations in Annot, widgets in Form, links in Link, printer
        /// marks outside the tree, fields with /TU or a described element (ISO 14289-1, 7.18.1, 7.18.4, 7.18.5, 7.18.8). PDF/UA-2: invisible
        /// annotations are artifacts, markup and watermarks in Annot, widgets in Form (one each), with a label or /Contents, links in Link or
        /// Reference (one target each), /Contents the same as the /Alt of the element (8.9.2.2, 8.9.2.3, 8.9.2.4.13/14/16, 8.9.4.2, 8.10.1,
        /// 8.10.2.3, 8.2.5.20) — and, as a sign to check, an annotation outside the tree that no rule lets be an artifact (8.9.2.1).
        /// </summary>
        public void AnnotationsInStructure()
        {
            // without a tree the annotations of PDF/UA-1 stand in no element either (the rules apply all the same); PDF/UA-2 asks only of those in it
            var root = Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"));
            if (root is null && part == 2) return;
            var visited = 0;
            foreach (var page in doc.Pages)
            {
                cancel.ThrowIfCancellationRequested();
                if (page.Dictionary.Get(Annots) is not PdfArray annots) continue;
                for (var i = 0; i < annots.Count && i < 10_000 && ++visited < MaxElements; i++)
                    if (annots.Get(i) is PdfDictionary annot) AnnotationInStructure(annot, page, page.Index + 1, root);
            }
            foreach (var element in root is null ? [] : Elements())
            {
                var type = StandardStructure.Resolve(element, root)?.Name;
                if (type == "Form") FormElement(element);
                if (part == 2 && type == "Link") LinkTargets(element);
            }
        }

        private void AnnotationInStructure(PdfDictionary annot, PdfPage page, int number, PdfDictionary? root)
        {
            var subtype = annot.GetName(PdfName.Subtype)?.Value;
            var flags = annot.GetInteger(PdfName.F) ?? 0;
            var element = root is null ? null : StructureOf(annot);
            var type = element is null ? null : StandardStructure.Resolve(element, root)?.Name;
            if (part == 1)
            {
                if ((flags & 2) != 0 || OutsideCropBox(annot, page)) return;
                switch (subtype)
                {
                    case "Widget":
                        if (type != "Form") Add(AccessibilityRules.WidgetInForm, Tr.T("Ein Formularfeld steht nicht in einem Form-Element der Struktur."), number);
                        if (IsFieldWidget(annot) && !OwnFieldTooltip(annot) && Text(element?.Get(AltKey)) is not { Length: > 0 })
                            Add(AccessibilityRules.FieldDescription, Tr.T("Ein Formularfeld hat weder eine Kurzinfo (/TU) noch einen Ersatztext (/Alt) an seinem Element."), number);
                        break;
                    case "Link":
                        if (type != "Link") Add(AccessibilityRules.LinkInLink, Tr.T("Ein Link steht nicht in einem Link-Element der Struktur."), number);
                        break;
                    case "PrinterMark":
                        if (element is not null) Add(AccessibilityRules.PrinterMarkOutside, Tr.T("Eine Druckmarke (PrinterMark) steht im Strukturbaum."), number);
                        break;
                    default:
                        if (type != "Annot") Add(AccessibilityRules.AnnotationInAnnot, Tr.T("Eine Anmerkung steht nicht in einem Annot-Element der Struktur."), number);
                        break;
                }
                return;
            }
            var artifact = type == "Artifact";
            var real = element is not null && !artifact;
            if (real && (flags & 1) != 0)
                Add(AccessibilityRules.InvisibleArtifact, Tr.T("Eine unsichtbare Anmerkung (Invisible) steht als Inhalt im Strukturbaum statt als Artefakt."), number);
            if (real && (flags & 32) != 0 && (flags & 256) == 0)
                Add(AccessibilityRules.NoViewArtifact, Tr.T("Eine nicht angezeigte Anmerkung (NoView) steht als Inhalt im Strukturbaum statt als Artefakt."), number);
            if (real && subtype is not null && Markup.Contains(subtype) && type != "Annot")
                Add(AccessibilityRules.MarkupInAnnot, Tr.T("Eine Kommentar-Anmerkung steht nicht in einem Annot-Element der Struktur."), number);
            if (real && subtype == "Watermark" && type != "Annot")
                Add(AccessibilityRules.WatermarkInAnnot, Tr.T("Ein Wasserzeichen als Inhalt steht nicht in einem Annot-Element der Struktur."), number);
            if (real && subtype == "PrinterMark")
                Add(AccessibilityRules.PrinterMarkArtifact, Tr.T("Eine Druckmarke (PrinterMark) steht als Inhalt im Strukturbaum statt als Artefakt."), number);
            if (real && subtype == "Widget" && ZeroSize(annot))
                Add(AccessibilityRules.ZeroWidgetArtifact, Tr.T("Ein Formularfeld ohne Fläche steht als Inhalt im Strukturbaum statt als Artefakt."), number);
            if (real && subtype == "Widget" && IsFieldWidget(annot) && type != "Form")
                Add(AccessibilityRules.WidgetInForm, Tr.T("Ein Formularfeld steht nicht in einem Form-Element der Struktur."), number);
            if (real && subtype == "Widget" && IsFieldWidget(annot) && annot.Get(ContentsKey) is not PdfString && !HasLabel(element!, root))
                Add(AccessibilityRules.WidgetLabel, Tr.T("Ein Formularfeld hat weder eine Beschriftung (Lbl) noch /Contents."), number);
            // a visible signature whose appearance shows a picture: something describes it (ISO 14289-2, 8.10.3.5) — a sign only: how the rule
            // collections tell the graphic of a signature is not certain, and /Contents must equal /Alt (8.9.4.2), so either counts
            if (real && subtype == "Widget" && element is not null && IsSignature(annot) && !ZeroSize(annot) && ShowsImage(annot) &&
                element.Get(AltKey) is not PdfString && annot.Get(ContentsKey) is not PdfString)
                Add(AccessibilityRules.SignatureGraphicAlt, Tr.T("Das Erscheinungsbild einer Signatur zeigt ein Bild, aber nichts beschreibt es (/Alt oder /Contents)."), number);
            if (real && subtype == "Link" && type is not ("Link" or "Reference"))
                Add(AccessibilityRules.LinkInLink, Tr.T("Ein Link steht nicht in einem Link- oder Reference-Element der Struktur."), number);
            if (element is not null && annot.Get(ContentsKey) is PdfString contents && element.Get(AltKey) is PdfString alt &&
                PdfACheck.RawText(contents) != PdfACheck.RawText(alt))
                Add(AccessibilityRules.ContentsEqualsAlt, Tr.T("Das /Contents einer Anmerkung sagt etwas anderes als das /Alt ihres Elements."), number);
            // outside the tree: allowed for artifacts — only a sign where no rule makes it one (8.9.2.1)
            if (element is null && subtype is not ("Popup" or "PrinterMark") && (flags & 1) == 0 && !((flags & 32) != 0 && (flags & 256) == 0) &&
                !(subtype == "Widget" && ZeroSize(annot)) && (flags & 2) == 0)
                Add(AccessibilityRules.AnnotationInTree, Tr.T("Eine Anmerkung steht nicht im Strukturbaum — ist sie wirklich nur Schmuck (Artefakt)?"), number);
        }

        /// <summary>A rectangle of no width and no height.</summary>
        /// <summary>A widget of a signature field (/FT /Sig on it or a parent).</summary>
        private static bool IsSignature(PdfDictionary widget)
        {
            PdfDictionary? field = widget;
            for (var i = 0; i < 32 && field is not null; i++)
            {
                if (field.GetName(PdfName.Get("FT")) is { } type) return type.Value == "Sig";
                field = field.Get(PdfName.Get("Parent")) as PdfDictionary;
            }
            return false;
        }

        /// <summary>Whether the normal appearance of an annotation paints an image XObject (also in forms it draws, within a depth).</summary>
        private static bool ShowsImage(PdfDictionary annot)
        {
            static bool Paints(PdfObject? xobject, int depth)
            {
                if (xobject is not PdfStream stream || depth > 4) return false;
                if (stream.Dictionary.GetName(PdfName.Subtype)?.Value == "Image") return true;
                if (stream.Dictionary.GetDictionary(PdfName.Get("Resources"))?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) return false;
                foreach (var (key, _) in xobjects.Entries.Take(256))
                    if (Paints(xobjects.Get(key), depth + 1)) return true;
                return false;
            }
            var normal = annot.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N"));
            if (normal is PdfStream) return Paints(normal, 0);
            if (normal is PdfDictionary states)
                foreach (var (key, _) in states.Entries.Take(32))
                    if (Paints(states.Get(key), 0)) return true;
            return false;
        }

        private static bool ZeroSize(PdfDictionary annot) =>
            annot.Get(PdfName.Get("Rect")) is PdfArray { Count: 4 } r && Number(r.Get(0)) == Number(r.Get(2)) && Number(r.Get(1)) == Number(r.Get(3));

        private static double? Number(PdfObject? value) => value switch
        {
            PdfInteger i => i.Value,
            PdfReal d => d.Value,
            _ => null,
        };

        /// <summary>
        /// The field of a widget has a tooltip (/TU, not empty): the widget itself where it is the field (with /T), else its parent field — a
        /// /TU on a widget of a radio group is no tooltip of the group (/TU is not inherited, ISO 32000-2, Table 226); found with the test file
        /// 7.18.1-t03-fail-d (docs/FEHLERGEDAECHTNIS.md, No. 665).
        /// </summary>
        private static bool OwnFieldTooltip(PdfDictionary widget)
        {
            var field = widget.ContainsKey(PdfName.Get("T")) ? widget : widget.Get(PdfName.Get("Parent")) as PdfDictionary;
            return field is not null && Text(field.Get(PdfName.Get("TU"))) is { Length: > 0 };
        }

        /// <summary>A Form element (or its parent) holds a label (Lbl) for the field.</summary>
        private static bool HasLabel(PdfDictionary element, PdfDictionary? root)
        {
            bool Labels(PdfDictionary e) => Kids(e).Any(k => StandardStructure.Resolve(k, root)?.Name == "Lbl");
            return Labels(element) || element.Get(PKey) is PdfDictionary parent && parent.ContainsKey(SKey) && Labels(parent);
        }

        /// <summary>
        /// A Form element: PDF/UA-1 without a Role attribute holds exactly one object reference to a widget and nothing else (7.18.4); PDF/UA-2
        /// holds at most one widget (8.10.1).
        /// </summary>
        private void FormElement(PdfDictionary element)
        {
            var items = element.Get(KKey) switch
            {
                PdfArray list => Enumerable.Range(0, Math.Min(list.Count, 10_000)).Select(list.Get).ToList(),
                null or PdfNull => [],
                var one => [one],
            };
            var widgets = items.Count(i => i is PdfDictionary r && r.GetName(PdfName.Type)?.Value == "OBJR" &&
                                           r.Get(PdfName.Get("Obj")) is PdfDictionary o && o.GetName(PdfName.Subtype)?.Value == "Widget");
            if (part == 1 && Attribute(element, "PrintField", "Role") is null && (items.Count != 1 || widgets != 1))
                Add(AccessibilityRules.FormOneWidget, Tr.T("Ein Form-Element ohne Rolle (Role) enthält nicht genau ein Formularfeld."));
            if (part == 2 && widgets > 1)
                Add(AccessibilityRules.FormOneWidget, Tr.T("Ein Form-Element enthält mehr als ein Formularfeld."));
        }

        /// <summary>PDF/UA-2, 8.2.5.20: the link annotations of one Link element lead to one target.</summary>
        private void LinkTargets(PdfDictionary element)
        {
            if (element.Get(KKey) is not PdfArray list) return;
            var targets = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count && i < 1000; i++)
                if (list.Get(i) is PdfDictionary r && r.GetName(PdfName.Type)?.Value == "OBJR" && r.Get(PdfName.Get("Obj")) is PdfDictionary link &&
                    link.GetName(PdfName.Subtype)?.Value == "Link")
                    targets.Add(Target(link));
            if (targets.Count > 1) Add(AccessibilityRules.LinkOneTarget, Tr.T("Ein Link-Element enthält Links zu verschiedenen Zielen."));
        }

        /// <summary>What a link leads to, as text for comparing: its /Dest, or its action's /URI, /D, /SD (a structure destination) or /F.</summary>
        private static string Target(PdfDictionary link)
        {
            static string Of(PdfObject? value) => value switch
            {
                null or PdfNull => "",
                PdfString s => "s:" + Convert.ToHexString(s.Bytes),
                PdfName n => "n:" + n.Value,
                PdfArray a => "[" + string.Join(",", Enumerable.Range(0, Math.Min(a.Count, 16)).Select(i => a[i] is PdfReference r ? "r" + r.Id.Number : Of(a.Get(i)))) + "]",
                _ => value.ToString() ?? "",
            };
            if (link.ContainsKey(PdfName.Get("Dest"))) return "dest " + Of(link.Get(PdfName.Get("Dest")));
            if (link.Get(PdfName.Get("A")) is PdfDictionary action)
                return "action " + Of(action.Get(PdfName.Get("S"))) + Of(action.Get(PdfName.Get("URI"))) + Of(action.Get(PdfName.Get("D"))) + Of(action.Get(PdfName.Get("SD"))) +
                       Of(action.Get(PdfName.Get("F")));
            return "";
        }
    }
}
