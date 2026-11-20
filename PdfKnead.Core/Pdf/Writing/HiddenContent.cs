using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Hidden things that nobody sees: annotations and form fields that are hidden, have no area or lie beside the page are
/// dropped by the sanitising, unless something can show them (a Hide action with /H false, a script that addresses the
/// field, an XFA form); those stay and are reported with the reason. If scripts are sanitised as well, scripts no longer
/// count. Text that is painted but not to be seen is measured by <see cref="TextVisibility"/>.
/// </summary>
internal static class HiddenContent
{
    /// <summary>Stays although invisible: name (field name or annotation type) and the localised reason.</summary>
    internal sealed record Kept(PdfDictionary Annotation, string Name, string Reason, bool ByScript);

    internal sealed record AnnotationScan(List<PdfDictionary> Unseen, List<Kept> Kept);

    private static readonly PdfName F = PdfName.Get("F"), Rect = PdfName.Get("Rect"), T = PdfName.Get("T"), Parent = PdfName.Get("Parent"),
        Ft = PdfName.Get("FT"), Annots = PdfName.Get("Annots"), S = PdfName.Get("S"), A = PdfName.Get("A"), AA = PdfName.Get("AA"),
        Js = PdfName.Get("JS"), H = PdfName.Get("H"), Next = PdfName.Get("Next"), Names = PdfName.Get("Names"), JavaScript = PdfName.Get("JavaScript"),
        OpenAction = PdfName.Get("OpenAction"), AcroForm = PdfName.Get("AcroForm"), Xfa = PdfName.Get("XFA"), Fields = PdfName.Get("Fields"),
        Outlines = PdfName.Get("Outlines"), First = PdfName.Get("First"), Kids = PdfName.Get("Kids");

    private const int Hidden = 2, Print = 4, NoZoom = 8, NoView = 32;

    /// <summary>
    /// Visible area for "lies beside the page": the crop box, unless it does not meet the media box at all (a broken value;
    /// some readers then show the media box), in which case nothing is risked and the whole media box is used.
    /// </summary>
    public static PdfRectangle VisibleBox(PdfPage page)
    {
        PdfRectangle crop = page.CropBox, media = page.MediaBox;
        var meets = Math.Min(crop.Right, media.Right) > Math.Max(crop.Left, media.Left) && Math.Min(crop.Top, media.Top) > Math.Max(crop.Bottom, media.Bottom);
        return meets ? crop : media;
    }

    // ------------------------------------------------------------ Annotations and fields

    /// <summary>Annotations of the page that nobody sees: those dropped and those that stay (with the reason). The program's own elements and pop-ups do not count.</summary>
    public static AnnotationScan Annotations(PdfDocument doc, PdfPage page, bool scriptsRemoved)
    {
        var scan = new AnnotationScan([], []);
        if (page.Dictionary.Get(Annots) is not PdfArray annots) return scan;
        var crop = VisibleBox(page);
        for (var i = 0; i < annots.Count && i < 100_000; i++)
        {
            if (annots.Get(i) is not PdfDictionary annot || annot.GetName(PdfName.Subtype)?.Value is "Popup" || !Unseen(annot, crop)) continue;
            if (ElementStore.IsOwn(doc, page, annot)) continue;
            if (Dependency(doc, annot, scriptsRemoved) is { } reason) scan.Kept.Add(new Kept(annot, NameOf(annot), reason.Text, reason.ByScript));
            else scan.Unseen.Add(annot);
        }
        return scan;
    }

    /// <summary>
    /// Hidden (Hidden; NoView without Print, because printed it would be visible), without area (except note icons, which
    /// appear at a fixed size) or entirely beside the visible page. Without an appearance of its own, an annotation counts where the
    /// display draws it — a line from its points even with an empty /Rect, as other readers draw it too (docs/FEHLERGEDAECHTNIS.md, No. 244).
    /// </summary>
    private static bool Unseen(PdfDictionary annot, PdfRectangle crop)
    {
        var flags = (int)(annot.GetInteger(F) ?? 0);
        if ((flags & Hidden) != 0 || (flags & NoView) != 0 && (flags & Print) == 0) return true;
        var icon = annot.GetName(PdfName.Subtype)?.Value == "Text" || (flags & NoZoom) != 0;
        if (annot.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.N) is null or PdfNull &&
            Content.AnnotationAppearance.DrawnBounds(annot, 0) is { Width: >= 0.01, Height: >= 0.01 } drawn)
            return drawn.Right <= crop.Left || drawn.Left >= crop.Right || drawn.Top <= crop.Bottom || drawn.Bottom >= crop.Top;
        if (PdfRectangle.From(annot.Get(Rect)) is not { } rect)
            return !icon || annot.Get(Rect) is not PdfArray;   // without area; an icon needs at least a position
        if (rect.Width < 0.01 || rect.Height < 0.01) return !icon;
        return rect.Right <= crop.Left || rect.Left >= crop.Right || rect.Top <= crop.Bottom || rect.Bottom >= crop.Top;
    }

    /// <summary>What can show the invisible annotation (localised reason), otherwise null.</summary>
    private static (string Text, bool ByScript)? Dependency(PdfDocument doc, PdfDictionary annot, bool scriptsRemoved)
    {
        var deps = DependenciesOf(doc);
        var widget = annot.GetName(PdfName.Subtype)?.Value == "Widget";
        // The field and its parents: an action or a script can refer to any of them
        var chain = new List<PdfDictionary>();
        for (PdfDictionary? d = annot; d is not null && chain.Count < 32; d = d.Get(Parent) as PdfDictionary) chain.Add(d);
        if (widget && chain.Any(d => d.GetName(Ft)?.Value == "Sig")) return (Tr.T("Unterschriftsfeld"), false);
        if (chain.Any(deps.ShownByAction.Contains)) return (Tr.T("eine Aktion kann es zeigen"), false);
        var names = widget ? FieldNames(chain) : [];
        if (names.Any(deps.ShownNames.Contains)) return (Tr.T("eine Aktion kann es zeigen"), false);
        if (scriptsRemoved) return null;
        if (deps.Xfa && widget) return (Tr.T("XFA-Formular"), true);
        if (widget && deps.DynamicFields) return (Tr.T("ein Skript spricht Felder allgemein an"), true);
        if (!widget && deps.AnnotationScripts) return (Tr.T("ein Skript spricht Anmerkungen an"), true);
        if (names.Count > 0 && deps.Scripts.Any(js => names.Any(n => Mentions(js, n)))) return (Tr.T("ein Skript kann es zeigen"), true);
        return null;
    }

    /// <summary>Does the field name occur in the script as a string literal ('Name' or "Name", also as the start of a longer name)?</summary>
    private static bool Mentions(string script, string name)
    {
        for (var at = script.IndexOf(name, StringComparison.Ordinal); at >= 0; at = script.IndexOf(name, at + 1, StringComparison.Ordinal))
            if (at > 0 && script[at - 1] is '\'' or '"') return true;
        return false;
    }

    /// <summary>Full name (parts via /Parent joined with a period) and the full names of the parents; an action on a parent refers to all its children.</summary>
    private static List<string> FieldNames(List<PdfDictionary> chain)
    {
        var parts = chain.Select(d => d.GetString(T)?.ToText()).Where(t => !string.IsNullOrEmpty(t)).Reverse().ToList();
        var names = new List<string>();
        for (var k = parts.Count; k > 0; k--) names.Add(string.Join(".", parts.Take(k)));
        return names;
    }

    private static string NameOf(PdfDictionary annot)
    {
        var chain = new List<PdfDictionary>();
        for (PdfDictionary? d = annot; d is not null && chain.Count < 32; d = d.Get(Parent) as PdfDictionary) chain.Add(d);
        return FieldNames(chain).FirstOrDefault() ?? annot.GetName(PdfName.Subtype)?.Value ?? "?";
    }

    // ------------------------------------------------------------ What can show something

    private sealed class Dependencies
    {
        public readonly List<string> Scripts = [];
        public readonly HashSet<PdfDictionary> ShownByAction = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<string> ShownNames = new(StringComparer.Ordinal);
        public bool DynamicFields, AnnotationScripts, Xfa;
        public long ScriptChars;
        public int Actions;
    }

    private static readonly ConditionalWeakTable<PdfDocument, Dependencies> Cache = new();

    // getField with a variable, loops over all fields: which field is meant cannot be determined
    private static readonly Regex DynamicField = new(@"getField\s*\(\s*[^'""\s)]|getNthFieldName|numFields", RegexOptions.Compiled);
    private static readonly Regex AnnotationAccess = new(@"getAnnots?\s*\(|syncAnnotScan", RegexOptions.Compiled);

    private static Dependencies DependenciesOf(PdfDocument doc) => Cache.GetValue(doc, Collect);

    /// <summary>Collects all actions and scripts of the file once (catalogue, name tree, pages, annotations, fields, bookmarks); cached per document.</summary>
    private static Dependencies Collect(PdfDocument doc)
    {
        var deps = new Dependencies();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var catalog = doc.Catalog;
        if (catalog is not null)
        {
            Action(catalog.Get(OpenAction));
            Triggers(catalog);
            if (catalog.GetDictionary(Names)?.GetDictionary(JavaScript) is { } tree) NameTree(tree, 0);
            if (catalog.GetDictionary(AcroForm) is { } form)
            {
                deps.Xfa = form.ContainsKey(Xfa);
                if (form.Get(Fields) is PdfArray fields) FieldTree(fields, 0);
            }
            if (catalog.GetDictionary(Outlines)?.Get(First) is PdfDictionary first) OutlineItems(first);
        }
        foreach (var page in doc.Pages)
        {
            Triggers(page.Dictionary);
            if (page.Dictionary.Get(Annots) is not PdfArray annots) continue;
            for (var i = 0; i < annots.Count && i < 100_000; i++)
                if (annots.Get(i) is PdfDictionary annot)
                {
                    Action(annot.Get(A));
                    Triggers(annot);
                }
        }
        foreach (var script in deps.Scripts)
        {
            deps.DynamicFields |= DynamicField.IsMatch(script);
            deps.AnnotationScripts |= AnnotationAccess.IsMatch(script);
        }
        return deps;

        void Triggers(PdfDictionary dict)
        {
            if (dict.Get(AA) is PdfDictionary triggers)
                foreach (var (key, _) in triggers.Entries) Action(triggers.Get(key));
        }

        void NameTree(PdfDictionary node, int depth)
        {
            if (depth > 32 || !seen.Add(node)) return;
            if (node.GetArray(Names) is { } pairs)
                for (var i = 1; i < pairs.Count; i += 2) Action(pairs.Get(i));
            if (node.GetArray(Kids) is { } kids)
                for (var i = 0; i < kids.Count; i++)
                    if (kids.Get(i) is PdfDictionary kid) NameTree(kid, depth + 1);
        }

        void FieldTree(PdfArray fields, int depth)
        {
            if (depth > 32) return;
            for (var i = 0; i < fields.Count && i < 100_000; i++)
                if (fields.Get(i) is PdfDictionary field && seen.Add(field))
                {
                    Action(field.Get(A));
                    Triggers(field);
                    if (field.Get(Kids) is PdfArray kids) FieldTree(kids, depth + 1);
                }
        }

        void OutlineItems(PdfDictionary first)
        {
            // Siblings in sequence, children from a list of their own instead of recursion: a crafted chain of thousands of nested
            // bookmarks would otherwise overflow the stack and end the process. Each bookmark once (seen); at most 100 000
            // bookmarks — the action limit (deps.Actions) counts actions only
            var pending = new Stack<PdfDictionary>();
            pending.Push(first);
            var visited = 0;
            while (pending.TryPop(out var start))
                for (PdfDictionary? item = start; item is not null && ++visited <= 100_000 && deps.Actions < 100_000 && seen.Add(item);
                     item = item.Get(Next) as PdfDictionary)
                {
                    Action(item.Get(A));
                    if (item.Get(First) is PdfDictionary child) pending.Push(child);
                }
        }

        void Action(PdfObject? value, int depth = 0)
        {
            if (depth > 8 || value?.AsDictionary() is not { } action || !seen.Add(action) || ++deps.Actions > 100_000) return;
            switch (action.GetName(S)?.Value)
            {
                case "JavaScript" when deps.ScriptChars < 16 << 20:
                    var text = action.Get(Js) switch
                    {
                        PdfString s => s.ToText(),
                        PdfStream stream => Read(stream),
                        _ => null,
                    };
                    if (text is { Length: > 0 })
                    {
                        deps.Scripts.Add(text);
                        deps.ScriptChars += text.Length;
                    }
                    break;
                case "Hide" when action.GetBoolean(H) == false:
                    Targets(action.Get(T), 0);
                    break;
            }
            switch (action.Get(Next))
            {
                case PdfArray list:
                    for (var i = 0; i < list.Count && i < 32; i++) Action(list.Get(i), depth + 1);
                    break;
                case PdfDictionary next:
                    Action(next, depth + 1);
                    break;
            }
        }

        void Targets(PdfObject? target, int depth)
        {
            switch (target)
            {
                case PdfString name:
                    deps.ShownNames.Add(name.ToText());
                    break;
                case PdfDictionary dict:
                    deps.ShownByAction.Add(dict);
                    break;
                case PdfArray list when depth == 0:
                    for (var i = 0; i < list.Count && i < 10_000; i++) Targets(list.Get(i), 1);
                    break;
            }
        }

        string? Read(PdfStream stream)
        {
            try
            {
                var data = doc.GetStreamData(stream);
                return data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2) : Encoding.Latin1.GetString(data);
            }
            catch (PdfException)
            {
                return null;
            }
        }
    }
}
