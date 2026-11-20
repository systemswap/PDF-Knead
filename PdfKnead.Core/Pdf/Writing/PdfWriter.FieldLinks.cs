using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// A group of linked fields (KONZEPT.md, section 5.4a) as it is written: one field — a field of the document that keeps its widgets, or
        /// a new one — with all widgets of the name as its kids.
        /// </summary>
        private sealed class LinkGroup
        {
            /// <summary>The full name.</summary>
            public required string Name { get; init; }

            /// <summary>A field of the document (as read) that stays the group's field; null for a new field or a widget on its own.</summary>
            public PdfDictionary? Owner { get; set; }

            /// <summary>The number of the group's field in the new file (0: a widget on its own, it is its field).</summary>
            public int Number { get; set; }

            /// <summary>The new field (null for an owner of the document or a widget on its own).</summary>
            public PdfDictionary? NewField { get; set; }

            /// <summary>The kids the field gets (references in the new file): of a new field its /Kids, of an owner all its kids once its
            /// copy is made (<see cref="ApplyFieldLinks"/>); widgets are added as they are written.</summary>
            public PdfArray Kids { get; set; } = new();

            /// <summary>A widget of the document on its own: separated from its field, it becomes a field itself.</summary>
            public bool Alone { get; set; }
        }

        /// <summary>New fields that are linked, by element.</summary>
        private readonly Dictionary<FieldElement, LinkGroup> _ownLinks = new(ReferenceEqualityComparer.Instance);

        /// <summary>Widgets of the document (as read) that leave their field: to another field, or on their own.</summary>
        private readonly Dictionary<PdfDictionary, LinkGroup> _movedWidgets = new(ReferenceEqualityComparer.Instance);

        /// <summary>Fields of the document (as read, with their number) that keep the widgets of their group and get others.</summary>
        private readonly Dictionary<PdfDictionary, (int Number, LinkGroup Group)> _ownerFields = new(ReferenceEqualityComparer.Instance);

        /// <summary>Fields of the document (as read, with their number) that lose widgets — moved or on their own — and the widgets they lose.</summary>
        private readonly Dictionary<PdfDictionary, (int Number, HashSet<PdfDictionary> Lost)> _losingFields = new(ReferenceEqualityComparer.Instance);

        /// <summary>Numbers (in the main document) of fields that are no longer fields: a widget that was its own field and is now a kid.</summary>
        private readonly HashSet<int> _noLongerFields = [];

        /// <summary>
        /// Copies of widgets that became kids of another field (<see cref="Relink"/>), with the field type they had: the copy no longer carries
        /// it, and its /Parent points into the new file, where the PDF/A step cannot follow it (a check box must keep its states, ISO 19005-2,
        /// 6.3.3; docs/FEHLERGEDAECHTNIS.md, No. 337).
        /// </summary>
        private readonly Dictionary<PdfDictionary, string> _relinkedFieldTypes = new(ReferenceEqualityComparer.Instance);

        /// <summary>New fields of linked groups, written once their kids are known (<see cref="FinishFieldLinks"/>).</summary>
        private readonly List<LinkGroup> _newLinkFields = [];

        private static readonly PdfName[] FieldKeys = [T, PdfName.Get("TU"), PdfName.Get("TM"), PdfName.Get("FT"), PdfName.Get("Ff"), V, PdfName.Get("DV"),
            PdfName.Get("Opt"), PdfName.Get("MaxLen"), FieldSpec.CodeKey, FieldSpec.LegacyCodeKey, PdfName.Get("I"), PdfName.Get("RV"), PdfName.Get("Lock"), PdfName.Get("SV")];

        /// <summary>Inheritable entries that decide how a widget shows its value: the flags (comb, multiline, password), the length, the text style.</summary>
        private static readonly PdfName[] LookKeys = [PdfName.Get("Ff"), PdfName.Get("MaxLen"), PdfName.Get("DA"), PdfName.Get("Q")];

        /// <summary>The value an entry of <see cref="LookKeys"/> has when it is missing; null where none can be written.</summary>
        private static PdfObject? LookDefault(PdfName key) => key.Value is "Ff" or "Q" ? PdfInteger.Of(0) : null;

        /// <summary>The scripts of a field (12.7.5.3, Table 197): the rest of /AA belongs to the widget.</summary>
        private static readonly HashSet<string> FieldTriggers = new(["K", "F", "V", "C"], StringComparer.Ordinal);

        /// <summary>
        /// Linked fields of the main document (KONZEPT.md, section 5.4a): the fields on the pages written are grouped by their full name as it
        /// now applies (renamed, separated, new). A group whose widgets come from one field of the document and no other stays as it is. Else
        /// the group's field is a field of the document all of whose widgets are in the group (one with kids first), a field with kids whose name
        /// it is (its other widgets leave it), or a new one; every other
        /// widget of the group becomes its kid. A widget separated from its field on its own becomes a field itself. Fields left without widgets
        /// go; a widget that was its own field and is now a kid is no longer in /Fields. Names with points (of fields within other fields) and
        /// the other documents are not linked.
        /// </summary>
        private void PlanFieldLinks()
        {
            if (_options.Flatten) return;
            var main = _sources[_main];
            var own = new Dictionary<string, List<FieldElement>>(StringComparer.Ordinal);
            var foreign = new Dictionary<string, List<(ForeignAnnotation Element, FormField Field)>>(StringComparer.Ordinal);
            var present = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var groupOf = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages)
            {
                if (!ReferenceEquals(item.Source, _main)) continue;
                foreach (var element in item.Elements)
                    switch (element)
                    {
                        case FieldElement { Spec: var spec } field when FieldLinks.Linkable(spec.Kind) && spec.Name.Trim().Length > 0:
                            (own.TryGetValue(spec.Name.Trim(), out var list) ? list : own[spec.Name.Trim()] = []).Add(field);
                            break;
                        case ForeignAnnotation annot when FormField.Of(annot) is { } formField && FieldLinks.Linkable(formField.Kind):
                            var name = FieldLinks.CurrentName(annot);
                            if (name.Length == 0) break;
                            (foreign.TryGetValue(name, out var widgets) ? widgets : foreign[name] = []).Add((annot, formField));
                            present.Add(annot.Original);
                            groupOf[annot.Original] = name;
                            break;
                    }
            }
            foreach (var name in own.Keys.Concat(foreign.Keys).Distinct(StringComparer.Ordinal).ToList())
            {
                var owns = own.GetValueOrDefault(name) ?? [];
                var widgets = foreign.GetValueOrDefault(name) ?? [];
                var terminals = widgets.GroupBy(w => w.Field.Field, ReferenceEqualityComparer.Instance).Select(g => g.First().Field).ToList();
                // a field of the document is whole in the group when none of its widgets on the pages belongs to another name
                var whole = terminals.Where(f => f.Widgets().Where(present.Contains).All(w => groupOf[w] == name)).Select(f => f.Field).ToList();
                if (owns.Count == 0 && terminals.Count == 1 && whole.Count == 1) continue;   // an ordinary field (perhaps renamed)
                if (owns.Count <= 1 && widgets.Count == 0) continue;                       // one new field on its own
                if (name.Contains('.') && !(owns.Count == 0 && widgets.Count == 1)) continue;   // within another field: not linked
                var kinds = owns.Select(o => o.Spec.Kind).Concat(widgets.Select(w => w.Element.Spec?.Kind ?? w.Field.Kind)).Distinct().ToList();
                if (kinds.Count > 1) continue;   // different kinds are not linked (the window does not let it happen)
                var group = new LinkGroup { Name = name };
                // else a field with kids keeps the widgets that keep its name (the others leave it)
                var owner = whole.FirstOrDefault(f => !IsWidget(f)) ?? whole.FirstOrDefault() ??
                            terminals.FirstOrDefault(f => !IsWidget(f.Field) && f.Name == name)?.Field;
                if (owner is not null && IsWidget(owner) && owns.Count + widgets.Count > 1)
                    owner = null;   // a widget that is its own field: the group gets a new field (the widget becomes its kid)
                if (owner is not null && NumberOf(main, owner) is { } ownerNumber)
                {
                    group.Owner = owner;
                    group.Number = Map(main, ownerNumber);
                    _ownerFields[owner] = (ownerNumber, group);
                }
                else if (owns.Count == 0 && widgets.Count == 1)
                    group.Alone = true;   // separated from its field: a field itself
                else
                {
                    group.Number = NewNumber();
                    group.NewField = NewLinkField(name, owns, widgets);
                    group.Kids = (PdfArray)group.NewField.Get(KidsKey);
                    _newLinkFields.Add(group);
                    _newFields.Add(group.Number);
                }
                foreach (var field in owns) _ownLinks[field] = group;
                foreach (var (element, formField) in widgets)
                {
                    if (group.Owner is not null && ReferenceEquals(formField.Field, group.Owner)) continue;
                    _movedWidgets[element.Original] = group;
                    if (ReferenceEquals(formField.Field, formField.Widget))
                    {
                        // a widget that was its own field: no longer one; where it was a kid of another field, that one loses it
                        if (NumberOf(main, formField.Widget) is { } widgetNumber) _noLongerFields.Add(widgetNumber);
                        if (formField.Widget.Get(PdfName.Parent) is PdfDictionary parent && NumberOf(main, parent) is { } parentNumber)
                            Losing(parent, parentNumber).Lost.Add(formField.Widget);
                    }
                    else if (NumberOf(main, formField.Field) is { } fieldNumber) Losing(formField.Field, fieldNumber).Lost.Add(element.Original);
                    if (group.Alone && NumberOf(main, element.Original) is { } aloneNumber) _newFields.Add(Map(main, aloneNumber));
                }
            }
            if (_movedWidgets.Count > 0 || _ownLinks.Count > 0) _specChanged = true;

            (int Number, HashSet<PdfDictionary> Lost) Losing(PdfDictionary field, int number)
            {
                if (!_losingFields.TryGetValue(field, out var entry)) _losingFields[field] = entry = (number, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
                return entry;
            }
        }

        /// <summary>Whether a field of the document is also its widget (merged).</summary>
        private static bool IsWidget(PdfDictionary field) => field.GetName(PdfName.Subtype)?.Value == "Widget";

        /// <summary>The object number of a dictionary of the main document (found once per document).</summary>
        private int? NumberOf(Source source, PdfDictionary dict)
        {
            if (_objectNumbers is null)
            {
                _objectNumbers = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
                foreach (var item in _pages)
                    if (ReferenceEquals(item.Source, _main))
                        foreach (var element in item.Elements)
                            if (element is ForeignAnnotation annot && FormField.Of(annot) is { } formField)
                                // at most 32 parents, as for inherited entries: in a damaged file the chain can be a circle
                                for (var (node, depth) = (formField.Widget, 0); depth < 32 && _objectNumbers.Count < 1_000_000; depth++)
                                {
                                    if (node.GetRaw(PdfName.Parent) is not PdfReference up || source.Doc.GetObject(up.Id.Number) is not PdfDictionary parent) break;
                                    _objectNumbers.TryAdd(parent, up.Id.Number);
                                    node = parent;
                                }
                // the widgets by the references of the pages
                foreach (var item in _pages)
                    if (ReferenceEquals(item.Source, _main) && item.Page.Dictionary.Get(Annots) is PdfArray annots)
                        for (var i = 0; i < annots.Count; i++)
                            if (annots[i] is PdfReference r && annots.Get(i) is PdfDictionary d) _objectNumbers.TryAdd(d, r.Id.Number);
                // the fields of /Fields
                if (_main.Catalog?.GetDictionary(AcroForm)?.Get(Fields) is PdfArray fields)
                    for (var i = 0; i < fields.Count; i++)
                        if (fields[i] is PdfReference r && fields.Get(i) is PdfDictionary d) _objectNumbers.TryAdd(d, r.Id.Number);
            }
            return _objectNumbers.TryGetValue(dict, out var number) ? number : null;
        }

        private Dictionary<PdfDictionary, int>? _objectNumbers;

        /// <summary>
        /// The new field of a group: the entries of a new field from its properties, or those of the first field of the document in the group
        /// (its scripts and options stay), the name and the value.
        /// </summary>
        private PdfDictionary NewLinkField(string name, List<FieldElement> owns, List<(ForeignAnnotation Element, FormField Field)> widgets)
        {
            var main = _sources[_main];
            var field = new PdfDictionary();
            PdfObject? value = null;
            if (widgets.Count > 0)
            {
                var (element, formField) = widgets[0];
                foreach (var key in FieldKeys)
                    if (FormField.Inherited(formField.Widget, key) is { } inherited and not PdfNull) field.Set(key, Rebase(inherited, main));
                // the text style and alignment of the first widget: a text field needs them as the field (ISO 32000-2, 12.7.4.3) — a widget
                // that differs keeps its own (Relink; docs/FEHLERGEDAECHTNIS.md, No. 365)
                foreach (var key in (ReadOnlySpan<PdfName>)[PdfName.Get("DA"), PdfName.Get("Q")])
                    if (FormField.Inherited(formField.Widget, key) is { } look and not PdfNull) field.Set(key, Rebase(look, main));
                if (FormField.Inherited(formField.Widget, PdfName.Get("AA")) is PdfDictionary scripts)
                {
                    var aa = new PdfDictionary();
                    foreach (var (key, script) in scripts.Entries)
                        if (FieldTriggers.Contains(key.Value)) aa.Set(key, Rebase(script, main));
                    if (aa.Count > 0) field.Set(PdfName.Get("AA"), aa);
                }
                if (element.Spec is { } spec) spec.WriteField(field, FieldSpec.Of(element));
                value = element.Value is { } filled ? FieldValue(element.Spec?.Kind ?? formField.Kind, filled, element.Spec?.MultiSelect ?? formField.MultiSelect) : null;
            }
            else
            {
                owns[0].Spec.WriteField(field);
                value = FieldValue(owns[0].Spec.Kind, owns[0].Value, owns[0].Spec.MultiSelect);
            }
            field.Set(T, new PdfString(PdfTextEncoding.Encode(name)));
            if (value is not null) SetValue(field, value);
            else if (widgets.Count == 0) field.Remove(V);
            field.Set(KidsKey, new PdfArray());
            return field;
        }

        /// <summary>A value as a field carries it (/V): a name for check boxes, text otherwise, several selections of a list as an array.</summary>
        private static PdfObject? FieldValue(FormFieldKind kind, string value, bool multiSelect) => kind switch
        {
            FormFieldKind.CheckBox => PdfName.Get(value.Length > 0 ? value : "Off"),
            FormFieldKind.ListBox when multiSelect && value.Contains('\n') =>
                new PdfArray([.. value.Split('\n').Take(10_000).Select(v => (PdfObject)new PdfString(PdfTextEncoding.Encode(v)))]),
            _ when value.Length > 0 => new PdfString(PdfTextEncoding.Encode(value)),
            _ => null,
        };

        /// <summary>
        /// A widget of the document that leaves its field (<see cref="PlanFieldLinks"/>), in its copy: to another field — without the entries of
        /// a field and its scripts, with /Parent —, or on its own — with the entries it inherited, its name, no /Parent.
        /// </summary>
        /// <param name="copy">The copy of the widget that is written.</param>
        /// <param name="element">The widget.</param>
        /// <param name="field">Its field as read.</param>
        /// <param name="number">The widget's number in the main document.</param>
        private void Relink(PdfDictionary copy, ForeignAnnotation element, FormField field, int? number)
        {
            if (!_movedWidgets.TryGetValue(element.Original, out var group)) return;
            var main = _sources[_main];
            if (group.Alone)
            {
                foreach (var key in FieldKeys)
                    if (!copy.ContainsKey(key) && FormField.Inherited(field.Widget, key) is { } inherited and not PdfNull) copy.Set(key, Rebase(inherited, main));
                if (FormField.Inherited(field.Field, PdfName.Get("DA")) is { } da && !copy.ContainsKey(PdfName.Get("DA"))) copy.Set(PdfName.Get("DA"), Rebase(da, main));
                if (FormField.Inherited(field.Field, PdfName.Get("Q")) is { } q && !copy.ContainsKey(PdfName.Get("Q"))) copy.Set(PdfName.Get("Q"), Rebase(q, main));
                if (FormField.Inherited(field.Field, PdfName.Get("AA")) is PdfDictionary scripts)
                {
                    var aa = copy.GetDictionary(PdfName.Get("AA")) is { } existing ? Clone(existing) : new PdfDictionary();
                    foreach (var (key, script) in scripts.Entries)
                        if (FieldTriggers.Contains(key.Value) && !aa.ContainsKey(key)) aa.Set(key, Rebase(script, main));
                    if (aa.Count > 0) copy.Set(PdfName.Get("AA"), aa);
                }
                var spec = element.Spec ?? FieldSpec.Of(element);
                spec?.WriteField(copy, FieldSpec.Of(element));
                copy.Set(T, new PdfString(PdfTextEncoding.Encode(group.Name)));
                if (element.Value is { } value && FieldValue(spec?.Kind ?? field.Kind, value, spec?.MultiSelect ?? field.MultiSelect) is { } v) SetValue(copy, v);
                copy.Remove(PdfName.Parent);
                return;
            }
            if (FormField.Inherited(field.Widget, PdfName.Get("FT")) is PdfName fieldType) _relinkedFieldTypes[copy] = fieldType.Value;
            foreach (var key in FieldKeys) copy.Remove(key);
            // what it showed differently from the group's field stays on it: fields of one name may differ in how they look (ISO 32000-1,
            // 12.7.3.2) — a box of one character among combs of twenty keeps its one character (docs/FEHLERGEDAECHTNIS.md, No. 343)
            foreach (var key in LookKeys)
            {
                // an entry missing means its default (flags and alignment 0); a length missing cannot be written as such and stays inherited
                var own = FormField.Inherited(field.Widget, key) is { } o and not PdfNull ? o : LookDefault(key);
                var shared = (group.NewField is { } fresh ? fresh.Get(key) : group.Owner is { } owner ? FormField.Inherited(owner, key) : null) ?? LookDefault(key);
                if (own is not null && own.ToString() != shared?.ToString()) copy.Set(key, Rebase(own, main));
            }
            if (copy.GetDictionary(PdfName.Get("AA")) is { } triggers)
            {
                var widgetScripts = new PdfDictionary();
                foreach (var (key, script) in triggers.Entries)
                    if (!FieldTriggers.Contains(key.Value)) widgetScripts.Set(key, script);
                if (widgetScripts.Count > 0) copy.Set(PdfName.Get("AA"), widgetScripts);
                else copy.Remove(PdfName.Get("AA"));
            }
            copy.Set(PdfName.Parent, new OutputReference(group.Number));
            if (number is { } n) group.Kids.Add(new OutputReference(Map(main, n)));
        }

        /// <summary>
        /// The fields of the document after <see cref="ForeignChanges"/>: an owner gets the widgets added to its group as kids, a field that
        /// loses widgets keeps the others — or goes, with nothing left, also from the field above it.
        /// </summary>
        private void ApplyFieldLinks()
        {
            var main = _sources[_main];
            foreach (var (field, (number, lost)) in _losingFields)
            {
                var current = main.Overrides.TryGetValue(number, out var o) && o is PdfDictionary od ? od : field;
                if (current.Get(KidsKey) is not PdfArray kids) continue;
                var kept = new PdfArray();
                for (var k = 0; k < kids.Count; k++)
                    if (!(kids.Get(k) is PdfDictionary kid && lost.Contains(kid))) kept.Add(kids[k]);
                if (kept.Count == 0 && !_ownerFields.ContainsKey(field))
                {
                    main.Removed.Add(number);
                    if (field.Get(PdfName.Parent) is PdfDictionary up && NumberOf(main, up) is { } upNumber &&
                        (main.Overrides.TryGetValue(upNumber, out var uo) && uo is PdfDictionary ud ? ud : up).Get(KidsKey) is PdfArray upKids)
                    {
                        var upKept = new PdfArray();
                        for (var k = 0; k < upKids.Count; k++)
                            if (!(upKids.Get(k) is PdfDictionary kid && ReferenceEquals(kid, field))) upKept.Add(upKids[k]);
                        var upCopy = Clone(up);
                        upCopy.Set(KidsKey, upKept);
                        main.Overrides[upNumber] = upCopy;
                    }
                    continue;
                }
                var copy = Clone(current);
                copy.Set(KidsKey, kept);
                main.Overrides[number] = copy;
            }
            foreach (var (field, (number, group)) in _ownerFields)
            {
                var current = main.Overrides.TryGetValue(number, out var o) && o is PdfDictionary od ? od : field;
                var copy = Clone(current);
                var kids = new PdfArray();
                if (current.Get(KidsKey) is PdfArray existing)
                    for (var k = 0; k < existing.Count; k++) kids.Add(existing[k]);
                // the widgets added so far; new ones are added to this array while their page is written, before the field is written
                for (var k = 0; k < group.Kids.Count; k++) kids.Add(group.Kids[k]);
                group.Kids = kids;
                copy.Set(KidsKey, kids);
                main.Overrides[number] = copy;
            }
        }

        /// <summary>The new fields of linked groups, with their kids (after the pages are written).</summary>
        private void FinishFieldLinks()
        {
            foreach (var group in _newLinkFields)
                if (group.NewField is { } field) _memoryQueue.Enqueue((group.Number, field));
        }
    }
}
