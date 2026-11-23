using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        private static readonly PdfName SKey = PdfName.Get("S"), NsKey = PdfName.Get("NS"), PKey = PdfName.Get("P"), KKey = PdfName.Get("K"),
            AltKey = PdfName.Get("Alt"), ActualTextKey = PdfName.Get("ActualText"), EKey = PdfName.Get("E"), IdKey = PdfName.Get("ID"),
            RefKey = PdfName.Get("Ref"), AKey = PdfName.Get("A"), OKey = PdfName.Get("O");

        /// <summary>What the role map does with a type (<see cref="Role"/>); several at once (a circle maps to no standard type either).</summary>
        [Flags]
        private enum RoleFinding
        {
            None = 0,
            Unmapped = 1,
            Circular = 2,
            RemappedStandard = 4,
            SameNamespace = 8,
        }

        /// <summary>
        /// Stage E (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 663): the structure tree — the role map (every type standard or
        /// mapped to one, no circle, no standard type mapped again; PDF/UA-2 also no map within one namespace: ISO 14289-1, 7.1; -2, 8.2.4),
        /// figures and formulas with a description (7.3, 7.7; 8.2.5.28.2), the headings (7.4.2, 7.4.4; 8.2.5.12), notes (7.9; 8.2.5.14), the root
        /// with one Document of the namespace for PDF 2.0 (8.2.5.2), TOCI with /Ref (8.2.5.8), lists with labels numbered (8.2.5.25), MathML
        /// only in formulas (8.2.5.29) and the language of /Alt, /ActualText and /E (7.2).
        /// </summary>
        public void Structure()
        {
            if (Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root) return;
            var elements = Elements();
            var catalogLang = Text(Catalog.Get(Lang)) is not null;
            var noteIds = new HashSet<string>(StringComparer.Ordinal);
            var usesH = false;
            var usesHn = false;
            var lastLevel = 0;
            var headingsInOrder = true;
            foreach (var element in elements)
            {
                cancel.ThrowIfCancellationRequested();
                RoleMap(element, root);
                var type = StandardStructure.Resolve(element, root);
                var name = type?.Name;
                switch (name)
                {
                    case "Figure":
                        if (part == 1 && Text(element.Get(AltKey)) is not { Length: > 0 } && element.Get(ActualTextKey) is not PdfString)
                            Add(AccessibilityRules.FigureDescription, Tr.T("Eine Abbildung (Figure) hat weder Ersatztext (/Alt) noch /ActualText."));
                        if (part == 2 && element.Get(AltKey) is not PdfString && element.Get(ActualTextKey) is not PdfString)
                            Add(AccessibilityRules.FigureDescription, Tr.T("Eine Abbildung (Figure) hat weder Ersatztext (/Alt) noch /ActualText."));
                        break;
                    case "Formula" when part == 1 && Text(element.Get(AltKey)) is not { Length: > 0 } && element.Get(ActualTextKey) is not PdfString:
                        Add(AccessibilityRules.FormulaDescription, Tr.T("Eine Formel (Formula) hat weder Ersatztext (/Alt) noch /ActualText."));
                        break;
                    case "H":
                        usesH = true;
                        if (part == 2) Add(AccessibilityRules.NoPlainHeading, Tr.T("Die Struktur verwendet die Überschrift H ohne Stufe; PDF/UA-2 erlaubt nur H1, H2 …"));
                        break;
                    case "Note" when type!.Value.Space == StructureSpace.Pdf17:
                        if (part == 1)
                        {
                            if (element.Get(IdKey) is not PdfString { Bytes.Length: > 0 })
                                Add(AccessibilityRules.NoteId, Tr.T("Eine Fußnote (Note) hat keine Kennung (/ID)."));
                            else if (element.Get(IdKey) is PdfString id && !noteIds.Add(Convert.ToHexString(id.Bytes)))
                                Add(AccessibilityRules.NoteIdUnique, Tr.T("Zwei Fußnoten (Note) haben dieselbe Kennung (/ID)."));
                        }
                        else Add(AccessibilityRules.NoNote, Tr.T("Die Struktur verwendet Note; PDF/UA-2 verlangt FENote."));
                        break;
                    case "FENote" when part == 2 && Attribute(element, null, "NoteType") is PdfName { Value: not ("Footnote" or "Endnote" or "None") }:
                        Add(AccessibilityRules.NoteType, Tr.T("Eine Fuß- oder Endnote (FENote) nennt keine gültige Art (NoteType)."));
                        break;
                    case "TOCI" when part == 2 && !HasRef(element, 0):
                        Add(AccessibilityRules.TociRef, Tr.T("Ein Eintrag des Inhaltsverzeichnisses (TOCI) nennt nicht sein Ziel (/Ref)."));
                        break;
                    case "L" when part == 2 && HasLabels(element, root) && Attribute(element, "List", "ListNumbering") is not PdfName { Value: not "None" }:
                        Add(AccessibilityRules.ListNumbering, Tr.T("Eine Liste mit Aufzählungszeichen (Lbl) nennt nicht ihre Nummerierung (ListNumbering)."));
                        break;
                }
                if (name is { Length: >= 2 } && name[0] == 'H' && int.TryParse(name.AsSpan(1), out var level))
                {
                    usesHn = true;
                    if (part == 1 && level > lastLevel + 1) headingsInOrder = false;
                    lastLevel = level;
                }
                if (part == 1 && name is not null && Kids(element).Count(k => StandardStructure.Resolve(k, root)?.Name == "H") > 1)
                    Add(AccessibilityRules.OneHeadingPerNode, Tr.T("Ein Strukturelement hat mehr als eine Überschrift H als Kind."));
                if (part == 2 && type?.Space == StructureSpace.MathML && element.Get(PKey) is PdfDictionary parent &&
                    StandardStructure.Resolve(parent, root) is not ({ Name: "Formula" } or { Space: StructureSpace.MathML }))
                    Add(AccessibilityRules.MathInFormula, Tr.T("Ein MathML-Element steht nicht in einer Formel (Formula)."));
                if (part == 1 && !catalogLang && !LanguageAbove(element))
                {
                    if (element.Get(ActualTextKey) is PdfString)
                        Add(AccessibilityRules.ElementActualTextLanguage, Tr.T("Die Sprache des /ActualText eines Strukturelements ist nirgends angegeben (/Lang)."));
                    if (element.Get(AltKey) is PdfString)
                        Add(AccessibilityRules.ElementAltLanguage, Tr.T("Die Sprache des Ersatztexts (/Alt) eines Strukturelements ist nirgends angegeben (/Lang)."));
                    if (element.Get(EKey) is PdfString)
                        Add(AccessibilityRules.ElementExpansionLanguage, Tr.T("Die Sprache der Auflösung einer Abkürzung (/E) ist nirgends angegeben (/Lang)."));
                }
            }
            if (part == 1)
            {
                if (!headingsInOrder) Add(AccessibilityRules.HeadingNesting, Tr.T("Die nummerierten Überschriften beginnen nicht mit H1 oder überspringen eine Stufe."));
                if (usesH && usesHn)
                {
                    Add(AccessibilityRules.PlainHeadingsOnly, Tr.T("Die Struktur verwendet H und H1, H2 … zugleich."));
                    Add(AccessibilityRules.NumberedHeadingsOnly, Tr.T("Die Struktur verwendet H1, H2 … und H zugleich."));
                }
            }
            if (part == 2) RootDocument(root);
        }

        /// <summary>The structure elements among an element's kids (/K), resolved.</summary>
        private static IEnumerable<PdfDictionary> Kids(PdfDictionary element) => element.Get(KKey) switch
        {
            PdfArray kids => Enumerable.Range(0, Math.Min(kids.Count, 10_000)).Select(kids.Get).OfType<PdfDictionary>().Where(d => d.ContainsKey(SKey)),
            PdfDictionary one when one.ContainsKey(SKey) => [one],
            _ => [],
        };

        /// <summary>
        /// The role map of an element's type (ISO 14289-1, 7.1 tests 5–7; -2, 8.2.4): a standard type mapped again, a type that is neither
        /// standard nor mapped to one, a circle, and (PDF/UA-2) a map within one explicit namespace.
        /// </summary>
        private void RoleMap(PdfDictionary element, PdfDictionary root)
        {
            var found = Role(element, root);
            if (found.HasFlag(RoleFinding.RemappedStandard))
                Add(AccessibilityRules.StandardNotRemapped, Tr.T("Eine Standardart der Struktur wird umgedeutet (/RoleMap)."));
            if (found.HasFlag(RoleFinding.Unmapped))
                Add(AccessibilityRules.TypeMapped, Tr.T("Eine Strukturart ist keine Standardart und auf keine abgebildet (/RoleMap)."));
            if (found.HasFlag(RoleFinding.Circular))
                Add(AccessibilityRules.RoleMapCircle, Tr.T("Die Abbildung der Strukturarten bildet einen Kreis (/RoleMap)."));
            if (found.HasFlag(RoleFinding.SameNamespace))
                Add(AccessibilityRules.RoleMapSameNamespace, Tr.T("Eine Strukturart wird auf eine Art desselben Namensraums abgebildet (/RoleMapNS)."));
        }

        /// <summary>
        /// Follows an element's type through the role maps (PDF/UA-1: /RoleMap only; PDF/UA-2 with its namespaces): a standard type with an entry is
        /// mapped again (and may run back to itself: a circle); a type of no standard leads to a standard type, to nothing, or in a circle (then to
        /// no standard type either); in PDF/UA-2 a chain that comes back into the explicit namespace it began in maps within that namespace.
        /// </summary>
        private RoleFinding Role(PdfDictionary element, PdfDictionary root)
        {
            if (element.Get(SKey) is not PdfName { Value: var type }) return RoleFinding.None;
            var ns = part == 2 ? element.Get(NsKey) as PdfDictionary : null;
            var start = ns;
            var found = RoleFinding.None;
            var seen = new HashSet<(string, PdfDictionary?)>();
            for (var step = 0; step < 64; step++)
            {
                var space = StandardStructure.SpaceOf(ns);
                var map = ns is null ? root.GetDictionary(PdfName.Get("RoleMap")) : ns.GetDictionary(PdfName.Get("RoleMapNS"));
                // (a key the map does not have reads as null, not as „no entry“)
                PdfObject? entry = space == StructureSpace.MathML ? null : map?.Get(PdfName.Get(type));
                if (entry is not (PdfName or PdfArray)) entry = null;
                var standard = StandardStructure.IsStandard(type, space);
                if (standard && (step > 0 || entry is null)) return found;
                if (standard) found |= RoleFinding.RemappedStandard;
                if (!seen.Add((type, ns))) return found | RoleFinding.Circular | (standard || found.HasFlag(RoleFinding.RemappedStandard) ? 0 : RoleFinding.Unmapped);
                PdfDictionary? next;
                switch (entry)
                {
                    case PdfName target:
                        type = target.Value;
                        next = null;
                        break;
                    case PdfArray { Count: >= 1 } target when target.Get(0) is PdfName name:
                        type = name.Value;
                        next = target.Count >= 2 ? target.Get(1) as PdfDictionary : null;
                        break;
                    default:
                        return found | RoleFinding.Unmapped;
                }
                // a chain back to where it was: a circle (before the namespace: a type mapped to itself is a circle first)
                if (seen.Contains((type, next))) return found | RoleFinding.Circular | (found.HasFlag(RoleFinding.RemappedStandard) ? 0 : RoleFinding.Unmapped);
                if (part == 2 && start is not null && !found.HasFlag(RoleFinding.RemappedStandard) && SameSpace(next, start)) return found | RoleFinding.SameNamespace;
                ns = next;
            }
            return found | RoleFinding.Circular;
        }

        /// <summary>Two namespaces are the same: the same dictionary or the same name; no dictionary is the default namespace of PDF 1.7.</summary>
        private static bool SameSpace(PdfDictionary? a, PdfDictionary b) =>
            ReferenceEquals(a, b) || (a?.GetString(NsKey)?.ToText() ?? StandardStructure.Pdf17Namespace) == b.GetString(NsKey)?.ToText();

        /// <summary>An element or one of its ancestors names a language (/Lang).</summary>
        private static bool LanguageAbove(PdfDictionary element)
        {
            PdfDictionary? current = element;
            for (var i = 0; i < MaxDepth && current?.ContainsKey(SKey) == true; i++)
            {
                if (current.Get(Lang) is PdfString) return true;
                current = current.Get(PKey) as PdfDictionary;
            }
            return false;
        }

        /// <summary>A TOCI or one of its descendants names its target (/Ref).</summary>
        private static bool HasRef(PdfDictionary element, int depth)
        {
            if (element.Get(RefKey) is PdfArray { Count: > 0 } or PdfDictionary) return true;
            return depth < 16 && Kids(element).Take(1000).Any(k => HasRef(k, depth + 1));
        }

        /// <summary>A list whose items hold labels (Lbl among the kids of its LI).</summary>
        private static bool HasLabels(PdfDictionary list, PdfDictionary root) =>
            Kids(list).Where(k => StandardStructure.Resolve(k, root)?.Name == "LI")
                .Any(item => Kids(item).Any(k => StandardStructure.Resolve(k, root)?.Name == "Lbl"));

        /// <summary>The value of a standard attribute of an element (in /A; with an owner, only in attribute objects of that owner).</summary>
        private static PdfObject? Attribute(PdfDictionary element, string? owner, string key)
        {
            IEnumerable<PdfDictionary> objects = element.Get(AKey) switch
            {
                PdfDictionary one => [one],
                PdfArray list => Enumerable.Range(0, Math.Min(list.Count, 256)).Select(list.Get).OfType<PdfDictionary>(),
                _ => [],
            };
            // an attribute object without the key reads it as the null object: the search goes on to the next (before, it ended at the first;
            // docs/FEHLERGEDAECHTNIS.md, No. 1004)
            foreach (var attributes in objects)
                if ((owner is null || attributes.GetName(OKey)?.Value == owner) && attributes.Get(PdfName.Get(key)) is { } value and not PdfNull)
                    return value;
            return null;
        }

        /// <summary>PDF/UA-2, 8.2.5.2: the root holds one Document as its only kid, of the namespace for PDF 2.0.</summary>
        private void RootDocument(PdfDictionary root)
        {
            var kids = Kids(root).ToList();
            var others = root.Get(KKey) is PdfArray all ? all.Count - kids.Count : 0;
            if (kids.Count != 1 || others != 0 || StandardStructure.Resolve(kids[0], root)?.Name != "Document")
            {
                Add(AccessibilityRules.RootDocument, Tr.T("Die Wurzel der Struktur hat nicht genau ein Document als einziges Kind."));
                return;
            }
            if (StandardStructure.Resolve(kids[0], root)?.Space != StructureSpace.Pdf20)
                Add(AccessibilityRules.RootDocumentNamespace, Tr.T("Das Document an der Wurzel gehört nicht zum Namensraum von PDF 2.0."));
        }
    }
}
