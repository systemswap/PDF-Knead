using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Where chosen objects of the page content go in the drawing order (KONZEPT.md, section 5.22 „Ebenen“).</summary>
public enum ContentLayerMove
{
    /// <summary>Drawn last: in front of everything else of the page content.</summary>
    ToFront,

    /// <summary>In front of the next object above that overlaps them.</summary>
    Forward,

    /// <summary>Behind the next object below that overlaps them.</summary>
    Backward,

    /// <summary>Drawn first: behind everything else of the page content.</summary>
    ToBack,
}

public static partial class ContentObjectEditor
{
    /// <summary>
    /// Moves objects of the page content in the drawing order (KONZEPT.md, section 5.22 „Ebenen“): they are copied as one form (as when copying,
    /// B7, with everything they need), taken out of the content, and the form is drawn at the new place — between two blocks at the outermost
    /// level of the content, where the coordinate system is known exactly: at the start, at the end, or right after (before) the outermost
    /// block holding the next object that overlaps them. Elements inserted in PDF Knead stay in front of the page content.
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <param name="keys">The objects (from <see cref="ObjectsOf"/>).</param>
    /// <param name="move">Where they go.</param>
    /// <param name="found">The objects of <paramref name="item"/> if already found.</param>
    /// <returns>The new entry and the keys of the objects at their new place; null if nothing changes (already in front or behind, nothing
    /// overlapping, or the content gives no place to put them).</returns>
    public static (PageItem Item, IReadOnlyList<ContentKey> Keys)? Restack(PageItem item, IReadOnlyList<ContentKey> keys, ContentLayerMove move,
        PageObjects? found = null)
    {
        found ??= ObjectsOf(item);
        var chosen = keys.Select(found.IndexOf).OfType<int>().Distinct().Order().ToList();
        if (chosen.Count == 0) return null;
        // the current content as the starting point: its objects in the same order as those found (the edits keep the order)
        var based = item.Rebased();
        var now = ObjectsOf(based);
        if (now.Count != found.Count || chosen.Any(i => now[i].Kind != found[i].Kind || !Near(now.VisibleOf(i), found.VisibleOf(i)))) return null;
        var box = chosen.Select(now.VisibleOf).Aggregate((a, b) => a.Union(b));
        if (box.IsEmpty) return null;
        byte[] content;
        using (var bytes = Redactor.PageContent(based.Source, based.Page.Dictionary)) content = bytes.Span.ToArray();
        // a tagged page: objects of the structure stay where they are (6.6 d, No. 651) — moved into a form they would leave their marked
        // content (an empty bracket behind, MCIDs in a form without keys); the reading order hangs on their place in the content
        if (NumberTree.KeyOf(based.Page.Dictionary.Get(PdfName.Get("StructParents"))) is not null && InStructure(content, based.Page.Resources, chosen.Select(now.KeyOf)))
            throw new ArgumentException(StructureStays);
        var places = Places(content);
        if (places.Count == 0) return null;
        var set = chosen.ToHashSet();
        bool Overlaps(int i) => !set.Contains(i) && now.VisibleOf(i).Intersect(box) is { IsEmpty: false };
        static int Outermost(ContentObject o) => o.Key.Forms.Any() ? o.Key.Forms.First() : o.Key.Op;
        Place? at = null;
        switch (move)
        {
            case ContentLayerMove.ToFront:
                if (Enumerable.Range(chosen[^1] + 1, now.Count - chosen[^1] - 1).All(i => !Overlaps(i))) return null;   // nothing in front of them
                at = places[^1];
                break;
            case ContentLayerMove.ToBack:
                if (Enumerable.Range(0, chosen[0]).All(i => !Overlaps(i))) return null;
                at = places[0];
                break;
            case ContentLayerMove.Forward:
                if (Enumerable.Range(chosen[^1] + 1, now.Count - chosen[^1] - 1).FirstOrDefault(Overlaps, -1) is var next and >= 0)
                    at = places.FirstOrDefault(p => p.Op > Outermost(now[next]));
                break;
            case ContentLayerMove.Backward:
                if (Enumerable.Range(0, chosen[0]).LastOrDefault(Overlaps, -1) is var previous and >= 0)
                    at = places.LastOrDefault(p => p.Op <= Outermost(now[previous]));
                break;
        }
        if (at is not { } place || !place.Ctm.IsInvertible) return null;
        if (Copy(based, [.. chosen.Select(now.KeyOf)], now) is not { } copied) return null;

        // the form at the place: "q <inverse of the CTM there> cm /Name Do Q" — four operators before the operator that stood there
        var page = based.Page;
        var resources = Redactor.CopyResources(page.Resources);
        var xobjects = new PdfDictionary { Resolver = resources.Resolver };
        if (resources.GetDictionary(PdfName.Get("XObject")) is { } existing)
            foreach (var (key, value) in existing.Entries) xobjects.Set(key, value);
        var name = PdfName.Get("KnObj1");
        for (var i = 2; xobjects.GetRaw(name) is not null; i++) name = PdfName.Get("KnObj" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        xobjects.Set(name, copied.Form);
        resources.Set(PdfName.Get("XObject"), xobjects);
        var output = new MemoryStream(content.Length + 96);
        output.Write(content, 0, place.Offset);
        output.Write("\nq "u8);
        var back = place.Ctm.Invert();
        foreach (var v in new[] { back.A, back.B, back.C, back.D, back.E, back.F })
        {
            Writing.PdfSerializer.Write(new PdfReal(v), output);
            output.WriteByte((byte)' ');
        }
        output.Write("cm "u8);
        Writing.PdfSerializer.Write(name, output);
        output.Write(" Do Q\n"u8);
        output.Write(content, place.Offset, content.Length - place.Offset);
        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), output.ToArray()));
        dict.Set(PdfName.Resources, resources);
        var inserted = based.WithContent(dict, resources);

        // the originals leave the content (their operators moved by the four new ones before them)
        var shifted = chosen.Select(i => Shifted(now.KeyOf(i), place.Op)).ToList();
        var result = Delete(inserted, shifted);
        var scope = (place.Op + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var after = ObjectsOf(result);
        var keysNow = Enumerable.Range(0, after.Count).Select(after.KeyOf)
            .Where(k => k.Scope == scope || k.Scope.StartsWith(scope + "/", StringComparison.Ordinal)).ToList();
        return keysNow.Count == 0 ? null : (result, keysNow);
    }

    /// <summary>
    /// Whether one of the objects belongs to the structure: in marked content with an MCID in the page content, or drawn by a form with keys
    /// of its own (/StructParents) — or within marked content with an MCID around it.
    /// </summary>
    private static bool InStructure(byte[] content, PdfDictionary? resources, IEnumerable<ContentKey> keys)
    {
        var properties = resources?.GetDictionary(PdfName.Get("Properties"));
        var xobjects = resources?.GetDictionary(PdfName.Get("XObject"));
        var marked = new HashSet<int>();     // operators within marked content with an MCID
        var keyed = new HashSet<int>();      // "Do" of a form with /StructParents
        var open = new List<bool>();
        var parser = new Content.ContentParser(content);
        var op = 0;
        while (parser.Next() is { } name)
        {
            switch (name)
            {
                case "BDC":
                    var list = parser.Operands.Count < 2 ? null : parser.Operands[1].Object switch
                    {
                        PdfDictionary inline => inline,
                        PdfName named => properties?.Get(named) as PdfDictionary,
                        _ => null,
                    };
                    open.Add(list?.ContainsKey(PdfName.Get("MCID")) == true);
                    break;
                case "BMC":
                    open.Add(false);
                    break;
                case "EMC":
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    break;
                case "Do" when parser.Operands.Count > 0 && parser.Operands[0].Object is PdfName form &&
                               xobjects?.Get(form) is PdfStream stream && stream.Dictionary.ContainsKey(PdfName.Get("StructParents")):
                    keyed.Add(op);
                    break;
            }
            if (open.Contains(true)) marked.Add(op);
            op++;
            if (open.Count > 1000) open.RemoveAt(0);
        }
        foreach (var key in keys)
        {
            var at = key.Forms.Any() ? key.Forms.First() : key.Op;
            if (marked.Contains(at) || keyed.Contains(at)) return true;
        }
        return false;
    }

    /// <summary>Why an object of the structure stays where it is (shown instead of moving it, 6.6 d).</summary>
    private static string StructureStays =>
        Localization.Tr.T("Das gehört zur Struktur für Bildschirmleser und bleibt in getaggten Seiten an seiner Stelle im Seiteninhalt — sonst stimmt die Lesereihenfolge nicht mehr.");

    /// <summary>A place between two blocks at the outermost level of the content: the operator that follows, the byte offset, the CTM there.</summary>
    private readonly record struct Place(int Op, int Offset, Matrix Ctm);

    /// <summary>
    /// The places at the outermost level of the content where something can be put in between: the start, before every outermost "q",
    /// after every "Q" that closes an outermost block, and the end — never inside a text object or while a path is being built. The CTM is
    /// followed through "cm", "q" and "Q".
    /// </summary>
    private static List<Place> Places(byte[] content)
    {
        var places = new List<Place>();
        var stack = new Stack<Matrix>();
        var ctm = Matrix.Identity;
        var text = false;
        var path = false;
        var parser = new Content.ContentParser(content);
        var op = 0;
        var before = 0;
        places.Add(new Place(0, 0, ctm));
        while (parser.Next() is { } name)
        {
            var free = stack.Count == 0 && !text && !path;
            switch (name)
            {
                case "q":
                    if (free) places.Add(new Place(op, before, ctm));
                    stack.Push(ctm);
                    break;
                case "Q":
                    if (stack.Count > 0) ctm = stack.Pop();
                    break;
                case "cm" when parser.Operands.Count >= 6:
                    var o = parser.Operands;
                    var m = new Matrix(o[0].Number, o[1].Number, o[2].Number, o[3].Number, o[4].Number, o[5].Number);
                    if (m.IsFinite && m * ctm is { IsFinite: true } next) ctm = next;
                    break;
                case "BT":
                    text = true;
                    break;
                case "ET":
                    text = false;
                    break;
                case "m" or "l" or "c" or "v" or "y" or "h" or "re":
                    path = true;
                    break;
                case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s" or "n" or "W" or "W*":
                    if (name is not ("W" or "W*")) path = false;
                    break;
            }
            op++;
            before = parser.Position;
            if (name == "Q" && stack.Count == 0 && !text && !path) places.Add(new Place(op, before, ctm));
        }
        if (places[^1].Offset != content.Length && !text && !path && stack.Count == 0) places.Add(new Place(op, content.Length, ctm));
        else if (places[^1].Offset != content.Length)
        {
            // unbalanced at the end: the end is still a place, behind everything (the open blocks end with the stream)
            places.Add(new Place(op, content.Length, Matrix.Identity));
        }
        return places;
    }

    /// <summary>The key of an operator after four operators were put in before operator <paramref name="at"/> of the page content.</summary>
    private static ContentKey Shifted(ContentKey key, int at)
    {
        if (key.Scope.Length == 0) return key.Op >= at ? key with { Op = key.Op + 4 } : key;
        var parts = key.Scope.Split('/');
        var first = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        if (first < at) return key;
        parts[0] = (first + 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return key with { Scope = string.Join('/', parts) };
    }

    private static bool Near(RectD a, RectD b) =>
        a.IsEmpty && b.IsEmpty || Math.Abs(a.Left - b.Left) < 0.5 && Math.Abs(a.Top - b.Top) < 0.5 && Math.Abs(a.Right - b.Right) < 0.5 && Math.Abs(a.Bottom - b.Bottom) < 0.5;
}
