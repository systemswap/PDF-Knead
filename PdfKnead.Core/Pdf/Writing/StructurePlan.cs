using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The changes the writer makes to the structure tree of the main source while it writes the copy (docs/ABLAUFPLAN.md, 6.2 step 4): new
/// elements at a place the containment rules allow (<see cref="Containment.PlaceFor"/>), marked content and object references for them, an
/// object reference replaced by marked content (flattening) — and at the end one parent tree built anew from what the copy keeps and what was
/// added (step 5; ISO 32000-2, 14.7.5.4). One plan per save, made only when a step needs it and only for a copy that keeps the tree (after
/// the tree was pruned to the pages written). The plan never edits; later steps such as making a document ready for screen readers write
/// through the same calls (step 10).
/// </summary>
/// <remarks>
/// Elements are named by handles: the reference of an element of the source, or the <see cref="OutputReference"/> of a new one. Changed
/// elements of the source become copies in the writer's replacements (<see cref="IStructureHost.Writable"/>); new ones are written as new
/// objects by <see cref="Finish"/>. Rules for every new element (step 6): only standard types; in a tree with the namespace for PDF 2.0
/// written as 2.0 the types of that namespace with /NS, else only those of PDF 1.7 without /NS.
/// </remarks>
internal sealed class StructurePlan
{
    private static readonly PdfName K = PdfName.Get("K"), P = PdfName.Get("P"), S = PdfName.Get("S"), Pg = PdfName.Get("Pg"), Ns = PdfName.Get("NS"),
        Mcid = PdfName.Get("MCID"), Obj = PdfName.Get("Obj"), Alt = PdfName.Get("Alt"), Lang = PdfName.Get("Lang"), ParentTree = PdfName.Get("ParentTree"),
        A = PdfName.Get("A"), ActualText = PdfName.Get("ActualText"), RoleMap = PdfName.Get("RoleMap"), ClassMap = PdfName.Get("ClassMap"),
        IdTree = PdfName.Get("IDTree"),
        ParentTreeNextKey = PdfName.Get("ParentTreeNextKey"), StructParent = PdfName.Get("StructParent"), Namespaces = PdfName.Get("Namespaces");

    private readonly IStructureHost _host;
    private readonly StructureView _view;
    private readonly Dictionary<int, PdfDictionary> _newElements = [];
    private readonly Dictionary<object, List<PdfObject>> _kids = [];
    private readonly PdfObject? _pdf20Namespace;

    // the parent tree: the pairs the copy keeps (read once), the arrays of the pages changed, new keys of object references, keys that go
    private SortedDictionary<long, PdfObject>? _pairs;
    private long _nextKey;
    private readonly Dictionary<long, List<PdfObject>> _pageArrays = [];
    private readonly Dictionary<long, PdfObject> _objectKeys = [];
    private readonly Dictionary<long, PdfObject> _rebasedPairs = [];
    private readonly Dictionary<int, int> _firstPages = [];
    private readonly HashSet<long> _removedKeys = [];
    private readonly Dictionary<int, long> _pageKeys = [];
    private readonly Dictionary<int, long> _newPageKeys = [];
    private readonly Dictionary<object, int> _nextMcid = [];

    // the IDs of the copy (hexadecimal, read once) and those added for merged files
    private HashSet<string>? _ids;
    private readonly List<(byte[] Id, PdfObject Element)> _addedIds = [];
    private bool _changed;

    private StructurePlan(IStructureHost host)
    {
        _host = host;
        // a new root is written like a new element (Finish)
        if (host.RootReference is OutputReference newRoot) _newElements[newRoot.Number] = host.SourceRoot;
        _view = new StructureView(host.SourceRoot, Resolve);
        _pdf20Namespace = host.Pdf20 ? Pdf20Namespace() : null;
    }

    /// <summary>
    /// A plan for the tree of the main source, or for a new one (<see cref="IStructureHost.RootReference"/>); null if the copy has no tree the
    /// plan can change (no root as an object of its own).
    /// </summary>
    public static StructurePlan? For(IStructureHost host) =>
        host.RootReference is OutputReference || host.Resolve(host.RootReference) is PdfDictionary ? new StructurePlan(host) : null;

    /// <summary>Whether the plan has changed anything so far (a new tree is written only then).</summary>
    public bool Changed => _changed;

    /// <summary>Whether new elements get the types of the namespace for PDF 2.0 (with /NS): written as 2.0 into a tree that has that namespace.</summary>
    public bool Pdf20Types => _pdf20Namespace is not null;

    /// <summary>The tree as it stands with the changes so far.</summary>
    public StructureView View => _view;

    /// <summary>Whether an element can be changed: a new one, or one of the source that is an object of its own.</summary>
    public bool CanEdit(PdfObject? element) => element is OutputReference o ? _newElements.ContainsKey(o.Number) : element is PdfReference && _view.Element(element) is not null;

    // ------------------------------------------------------------ Elements

    /// <summary>
    /// A new structure element for content that belongs to <paramref name="anchor"/>: at the place the containment rules allow
    /// (<see cref="Containment.PlaceFor"/>), else at the end of the top Document (made if the tree has none); with /P, /Pg, /Alt, /Lang.
    /// </summary>
    /// <param name="type">A standard type (of PDF 1.7, or of 2.0 where <see cref="Pdf20Types"/>).</param>
    /// <param name="anchor">The element the content belongs to; null: none (the end of the top Document).</param>
    /// <param name="page">The page of the copy (index) the content lies on.</param>
    /// <param name="alt">An alternative description; null: none.</param>
    /// <param name="lang">The language where it differs from the document's; null: none.</param>
    /// <param name="besideAnchor">Only beside the anchor, not into it (content that belongs to no element; <see cref="Containment.PlaceFor"/>).</param>
    /// <returns>The handle of the new element; null if the tree has no place that can be changed.</returns>
    /// <exception cref="ArgumentException">The type is no standard type the tree may get.</exception>
    public PdfObject? NewElement(string type, PdfObject? anchor, int page, string? alt = null, string? lang = null, bool besideAnchor = false)
    {
        var place = anchor is not null && Containment.PlaceFor(_view, anchor, type, besideAnchor) is { } found && CanEdit(found.Parent) ? found : TopDocument(type);
        return Place(type, place, page, alt, lang);
    }

    /// <summary>
    /// A new structure element at a given place: at <paramref name="index"/> in the /K of <paramref name="parent"/> where the containment rules
    /// let the parent take it (<see cref="Containment.PlaceFor"/> keeps it there), else beside the parent, else at the end of the top Document —
    /// for content that takes the place of elements that went (a page replaced by its image, docs/ABLAUFPLAN.md, 6.6 g).
    /// </summary>
    /// <returns>The handle of the new element; null if the tree has no place that can be changed.</returns>
    public PdfObject? NewElementAt(string type, PdfObject parent, int index, int page, string? alt = null, string? lang = null)
    {
        Placement? place = null;
        if (CanEdit(parent) && Containment.PlaceFor(_view, parent, type) is { } into && StructureView.Same(into.Parent, parent))
            place = new Placement(parent, Math.Clamp(index, 0, KidsOf(parent).Count));
        else if (Containment.PlaceFor(_view, parent, type, beside: true) is { } beside && CanEdit(beside.Parent))
            place = beside;
        return Place(type, place ?? TopDocument(type), page, alt, lang);
    }

    /// <summary>Makes the element at <paramref name="place"/> (with /P, /Pg, /Alt, /Lang); null without a place.</summary>
    private PdfObject? Place(string type, Placement? place, int page, string? alt, string? lang)
    {
        var ns = NamespaceFor(type);
        if (place is not { } at) return null;
        var element = Create(type, ns, at.Parent);
        element.Dictionary.Set(Pg, new OutputReference(_host.PageNumber(page)));
        if (alt is not null) element.Dictionary.Set(Alt, new PdfString(PdfTextEncoding.Encode(alt)));
        if (lang is not null) element.Dictionary.Set(Lang, new PdfString(PdfTextEncoding.Encode(lang)));
        var kids = KidsOf(at.Parent);
        kids.Insert(Math.Clamp(at.Index, 0, kids.Count), element.Handle);
        SetKids(at.Parent, kids);
        _changed = true;
        return element.Handle;
    }

    /// <summary>
    /// Marked content of a page given to an element: the MCID in /K where the element's /Pg is that page, else a marked-content reference with
    /// /Pg (ISO 32000-2, 14.7.5.2); in the parent tree the page's array names the element at that MCID.
    /// </summary>
    public void AddMarkedContent(PdfObject element, int page, int mcid)
    {
        Editable(element);
        var kids = KidsOf(element);
        kids.Add(ContentItem(element, page, mcid));
        SetKids(element, kids);
        var array = PageArray(page);
        while (array.Count <= mcid) array.Add(PdfNull.Instance);
        array[mcid] = element;
        _changed = true;
    }

    /// <summary>
    /// Marked content of a page given to an element at the place of other marked content <paramref name="at"/> of the same page in its /K
    /// (edited text, docs/ABLAUFPLAN.md, 6.6 i): instead of it where <paramref name="replace"/> (its entry in the page's array becomes null —
    /// its bracket in the content must no longer name the element), else right after it. The reading order of the element stays.
    /// </summary>
    /// <returns>Whether the element had <paramref name="at"/> in its /K.</returns>
    public bool PutMarkedContent(PdfObject element, int page, int mcid, int at, bool replace)
    {
        Editable(element);
        var kids = KidsOf(element);
        var index = kids.FindIndex(k => IsMarkedContent(element, k, page, at));
        if (index < 0) return false;
        var item = ContentItem(element, page, mcid);
        if (replace) kids[index] = item;
        else kids.Insert(index + 1, item);
        SetKids(element, kids);
        var array = PageArray(page);
        while (array.Count <= Math.Max(mcid, at)) array.Add(PdfNull.Instance);
        array[mcid] = element;
        if (replace) array[at] = PdfNull.Instance;
        _changed = true;
        return true;
    }

    /// <summary>Marked content of a page taken out of an element (its bracket no longer names it); its entry in the page's array becomes null.</summary>
    /// <returns>Whether the element had it in its /K.</returns>
    public bool RemoveMarkedContent(PdfObject element, int page, int mcid)
    {
        Editable(element);
        var kids = KidsOf(element);
        var index = kids.FindIndex(k => IsMarkedContent(element, k, page, mcid));
        if (index < 0) return false;
        kids.RemoveAt(index);
        SetKids(element, kids);
        var array = PageArray(page);
        if (mcid < array.Count) array[mcid] = PdfNull.Instance;
        _changed = true;
        return true;
    }

    /// <summary>The place of marked content of the page in an element's /K; -1 if the element does not have it.</summary>
    public int IndexOfMarkedContent(PdfObject element, int page, int mcid) => KidsOf(element).FindIndex(k => IsMarkedContent(element, k, page, mcid));

    /// <summary>Whether an entry of an element's /K is the marked content <paramref name="mcid"/> of the page (an MCID or an MCR without /Stm).</summary>
    private bool IsMarkedContent(PdfObject element, PdfObject kid, int page, int mcid)
    {
        var own = _host.PageIndexOf((Resolve(element) as PdfDictionary)?.GetRaw(Pg));
        return Resolve(kid) switch
        {
            PdfInteger number => number.Value == mcid && own == page,
            PdfDictionary mcr when mcr.GetName(PdfName.Type)?.Value == "MCR" && !mcr.ContainsKey(PdfName.Get("Stm")) && mcr.Get(Mcid) is PdfInteger id =>
                id.Value == mcid && (mcr.GetRaw(Pg) is { } pg ? _host.PageIndexOf(pg) : own) == page,
            _ => false,
        };
    }

    /// <summary>
    /// An object (an annotation) given to an element: an object reference with /Pg in /K and a new key of the parent tree for it
    /// (ISO 32000-2, 14.7.5.3 and 14.7.5.4).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="page">The page of the copy (index) the object is on.</param>
    /// <param name="obj">The object as the copy refers to it.</param>
    /// <returns>The key the object gets as its /StructParent.</returns>
    public int AddObjectReference(PdfObject element, int page, PdfObject obj)
    {
        Editable(element);
        var reference = new PdfDictionary();
        reference.Set(PdfName.Type, PdfName.Get("OBJR"));
        reference.Set(Obj, obj);
        reference.Set(Pg, new OutputReference(_host.PageNumber(page)));
        var kids = KidsOf(element);
        kids.Add(reference);
        SetKids(element, kids);
        var key = NewKey();
        _objectKeys[key] = element;
        _changed = true;
        return (int)key;
    }

    /// <summary>
    /// An object reference replaced by marked content (an annotation flattened into the page): in /K the reference to <paramref name="obj"/>
    /// becomes the content item; the object's key goes from the parent tree, the page's array names the element at the MCID.
    /// </summary>
    /// <returns>Whether the element referred to the object.</returns>
    public bool ReplaceObjectReference(PdfObject element, PdfObject obj, int page, int mcid)
    {
        Editable(element);
        var kids = KidsOf(element);
        var at = kids.FindIndex(k => Resolve(k) is PdfDictionary d && d.GetName(PdfName.Type)?.Value == "OBJR" && StructureView.Same(d.GetRaw(Obj), obj));
        if (at < 0) return false;
        kids[at] = ContentItem(element, page, mcid);
        SetKids(element, kids);
        if (Resolve(obj) is PdfDictionary target && NumberTree.KeyOf(target.Get(StructParent)) is { } key) _removedKeys.Add(key);
        var array = PageArray(page);
        while (array.Count <= mcid) array.Add(PdfNull.Instance);
        array[mcid] = element;
        _changed = true;
        return true;
    }

    /// <summary>
    /// An object reference taken out of an element (its object is not written: a hidden annotation left out when flattening); the object's
    /// key goes from the parent tree.
    /// </summary>
    /// <returns>Whether the element referred to the object.</returns>
    public bool RemoveObjectReference(PdfObject element, PdfObject obj)
    {
        Editable(element);
        var kids = KidsOf(element);
        var at = kids.FindIndex(k => Resolve(k) is PdfDictionary d && d.GetName(PdfName.Type)?.Value == "OBJR" && StructureView.Same(d.GetRaw(Obj), obj));
        if (at < 0) return false;
        kids.RemoveAt(at);
        SetKids(element, kids);
        if (Resolve(obj) is PdfDictionary target && NumberTree.KeyOf(target.Get(StructParent)) is { } key) _removedKeys.Add(key);
        _changed = true;
        return true;
    }

    /// <summary>
    /// An element of the source left without kids taken out of the tree: out of its parent's /K, and not written (an element of an
    /// annotation PDF-Knead writes anew; it gets a new element). An element with kids, the root or a new element stays.
    /// </summary>
    /// <returns>Whether it was taken out.</returns>
    public bool RemoveIfEmpty(PdfObject element)
    {
        if (element is not PdfReference reference || !CanEdit(element) || KidsOf(element).Count > 0 || _view.Parent(element) is not { } parent ||
            !(CanEdit(parent) || _view.IsRoot(parent))) return false;
        var siblings = KidsOf(parent);
        var at = siblings.FindIndex(k => StructureView.Same(k, element));
        if (at < 0) return false;
        siblings.RemoveAt(at);
        SetKids(parent, siblings);
        _host.Drop(reference);
        _changed = true;
        return true;
    }

    /// <summary>
    /// Elements of one parent put in the order given, in the places they hold among its kids (the other kids stay where they are): the order
    /// of the object references follows the chosen order of the annotations (/Tabs /S; ISO 14289-1, 7.18.3).
    /// </summary>
    /// <returns>Whether all of them are kids of the parent (and so now in that order).</returns>
    public bool OrderKids(PdfObject parent, IReadOnlyList<PdfObject> wanted)
    {
        if (!(CanEdit(parent) || _view.IsRoot(parent))) return false;
        var kids = KidsOf(parent);
        var places = new List<int>();
        for (var i = 0; i < kids.Count; i++)
            if (wanted.Any(w => StructureView.Same(w, kids[i]))) places.Add(i);
        if (places.Count != wanted.Count) return false;
        var changed = false;
        for (var j = 0; j < places.Count; j++)
        {
            if (StructureView.Same(kids[places[j]], wanted[j])) continue;
            kids[places[j]] = wanted[j];
            changed = true;
        }
        if (changed)
        {
            SetKids(parent, kids);
            _changed = true;
        }
        return true;
    }

    /// <summary>
    /// Attributes added to an element (/A, ISO 32000-2, 14.8.5): an attribute object of its own beside those it has (/A becomes an array if
    /// it held one already).
    /// </summary>
    public void AddAttributes(PdfObject element, PdfDictionary attributes)
    {
        Editable(element);
        var dict = WritableOf(element);
        dict.Set(A, dict.GetRaw(A) switch
        {
            null or PdfNull => attributes,
            PdfArray list => new PdfArray([.. list, attributes]),
            var one => new PdfArray([one, attributes]),
        });
        _changed = true;
    }

    /// <summary>An alternative description for an element that has none (no /Alt and no /ActualText; ISO 32000-2, 14.9.3).</summary>
    public void DescribeIfUndescribed(PdfObject element, string alt)
    {
        Editable(element);
        if (Resolve(element) is PdfDictionary current && (current.ContainsKey(Alt) || current.ContainsKey(ActualText))) return;
        WritableOf(element).Set(Alt, new PdfString(PdfTextEncoding.Encode(alt)));
        _changed = true;
    }

    /// <summary>The element the parent tree gives a key (an annotation's /StructParent); null if none or not one that can be changed.</summary>
    public PdfObject? ElementForKey(long key)
    {
        LoadPairs();
        return !_removedKeys.Contains(key) && _pairs!.TryGetValue(key, out var value) && CanEdit(value) ? value : null;
    }

    /// <summary>A key taken out of the parent tree (its object is no longer a content item of the element it named).</summary>
    public void RemoveKey(long key)
    {
        _removedKeys.Add(key);
        _changed = true;
    }

    /// <summary>The last element the page's array of the parent tree names (for an anchor where nothing nearer is known); null if none.</summary>
    public PdfObject? LastElementOnPage(int page)
    {
        if (CurrentArray(page) is not { } array) return null;
        for (var i = array.Count - 1; i >= 0; i--)
            if (CanEdit(array[i])) return array[i];
        return null;
    }

    /// <summary>The element the page's array of the parent tree gives an MCID; null if none or not one that can be changed.</summary>
    public PdfObject? ElementOfMcid(int page, int mcid) =>
        CurrentArray(page) is { } array && mcid >= 0 && mcid < array.Count && CanEdit(array[mcid]) ? array[mcid] : null;

    /// <summary>The page's array of the parent tree as it stands with the changes so far; null if it has none.</summary>
    private IReadOnlyList<PdfObject>? CurrentArray(int page) =>
        _pageKeys.TryGetValue(page, out var key) && _pageArrays.TryGetValue(key, out var changed) ? changed : ExistingArray(page);

    /// <summary>
    /// A new element at the top of the tree for a part merged in from another file (docs/ABLAUFPLAN.md, 6.5 step 5): under the top Document
    /// (made if the tree has none), before the first of its kids whose content begins on a later page than <paramref name="firstPage"/> — the
    /// reading order follows the pages; never right under the root.
    /// </summary>
    /// <returns>The handle of the new element; null if the root cannot be changed.</returns>
    public PdfObject? NewTopElement(string type, int firstPage, string? lang = null)
    {
        var ns = NamespaceFor(type);
        if (TopDocument(type) is not { } top) return null;
        var kids = KidsOf(top.Parent);
        var at = kids.Count;
        for (var i = 0; i < kids.Count; i++)
            if (FirstPageOf(kids[i]) > firstPage)
            {
                at = i;
                break;
            }
        var element = Create(type, ns, top.Parent);
        if (lang is not null) element.Dictionary.Set(Lang, new PdfString(PdfTextEncoding.Encode(lang)));
        _firstPages[((OutputReference)element.Handle).Number] = firstPage;   // its kids are of another file: the plan does not look into them
        kids.Insert(at, element.Handle);
        SetKids(top.Parent, kids);
        _changed = true;
        return element.Handle;
    }

    /// <summary>
    /// An element of a merged file made by PDF-Knead itself (the redaction protocol, 6.6 i) as a kid of the top Document directly, at the place
    /// the pages give (before the first kid that begins on a later page); its /P is the caller's (the handle returned).
    /// </summary>
    /// <param name="kid">The element as the copy refers to it (a reference of the copy).</param>
    /// <param name="firstPage">The page of the copy (index) it begins on.</param>
    /// <returns>The top Document it now hangs in; null if the tree has none that can be changed.</returns>
    public PdfObject? InsertTopKid(OutputReference kid, int firstPage)
    {
        if (TopDocument("Sect") is not { } top) return null;
        var kids = KidsOf(top.Parent);
        var at = kids.Count;
        for (var i = 0; i < kids.Count; i++)
            if (FirstPageOf(kids[i]) > firstPage)
            {
                at = i;
                break;
            }
        _firstPages[kid.Number] = firstPage;   // of another file: the plan does not look into it
        kids.Insert(at, kid);
        SetKids(top.Parent, kids);
        _changed = true;
        return top.Parent;
    }

    /// <summary>
    /// A new element made by the caller (the copy of an element for a further copy of a page, 6.5 d): its entries already of the copy, /P
    /// and /K set by the caller; written with the other new elements (<see cref="Finish"/>).
    /// </summary>
    public OutputReference AddElement(PdfDictionary element)
    {
        var number = _host.NewNumber();
        _newElements[number] = element;
        _changed = true;
        return new OutputReference(number);
    }

    /// <summary>
    /// The array of a further copy of a page (6.5 d) under a new key of the parent tree: read like the arrays the copy keeps (steps that add
    /// content to the page later find its elements), written by <see cref="Finish"/>.
    /// </summary>
    /// <returns>The key the page gets as its /StructParents.</returns>
    public long AddPageArray(PdfArray array)
    {
        LoadPairs();
        var key = NewKey();
        _pairs![key] = array;
        _changed = true;
        return key;
    }

    /// <summary>A kid added at the end of a new element (an element of a merged file, its reference already of the copy).</summary>
    public void AppendKid(PdfObject newElement, PdfObject kid)
    {
        if (newElement is not OutputReference o || !_newElements.ContainsKey(o.Number))
            throw new ArgumentException("Nur neue Elemente nehmen fremde Kinder auf.", nameof(newElement));
        var kids = KidsOf(newElement);
        kids.Add(kid);
        SetKids(newElement, kids);
        _changed = true;
    }

    /// <summary>A role map entry added to the root (a merged file's own type and the type it stands for).</summary>
    public void AddRole(PdfName type, PdfName standard) => AddRootEntry(RoleMap, type, standard);

    /// <summary>Whether the role map of the copy has an entry for the type (the merged ones too).</summary>
    public bool HasRole(PdfName type) => RootEntry(RoleMap, type) is not null;

    /// <summary>What the role map of the copy maps the type to; null if it has no entry for it or it names no type.</summary>
    public PdfName? RoleOf(PdfName type) => Resolve(RootEntry(RoleMap, type)) as PdfName;

    /// <summary>A class of the class map of the copy as stored (an attribute object, an array of them, or a reference); null if none.</summary>
    public PdfObject? ClassOf(PdfName name) => RootEntry(ClassMap, name);

    /// <summary>A class added to the class map of the copy (a merged file's class, its value already of the copy; ISO 32000-2, 14.7.6.2).</summary>
    public void AddClass(PdfName name, PdfObject value) => AddRootEntry(ClassMap, name, value);

    /// <summary>Whether an element of the copy has the ID already (the /IDTree as the copy keeps it, and the IDs added).</summary>
    public bool HasId(byte[] id)
    {
        if (_ids is null)
        {
            _ids = [];
            if (Resolve(_host.RootReference) is PdfDictionary root && Resolve(root.GetRaw(IdTree)) is PdfDictionary tree)
                foreach (var (key, _) in NameTree.Pairs(tree, Resolve)) _ids.Add(Convert.ToHexString(key));
        }
        return _ids.Contains(Convert.ToHexString(id));
    }

    /// <summary>
    /// An ID of an element of a merged file into the /IDTree of the copy (ISO 32000-2, 14.7.2): a key no element of the copy has
    /// (<see cref="HasId"/>), the element already of the copy. <see cref="Finish"/> builds the /IDTree anew.
    /// </summary>
    public void AddId(byte[] id, PdfObject element)
    {
        if (HasId(id)) throw new ArgumentException("Die ID hat schon ein Element der Kopie.", nameof(id));
        _ids!.Add(Convert.ToHexString(id));
        _addedIds.Add((id, element));
        _changed = true;
    }

    /// <summary>The entries of /Namespaces of the copy as stored (references of the main file, or of the copy for those added).</summary>
    public IReadOnlyList<PdfObject> NamespaceEntries() =>
        Resolve(_host.RootReference) is PdfDictionary root && Resolve(root.GetRaw(Namespaces)) is PdfArray list ? [.. list.Take(1000)] : [];

    /// <summary>A namespace added to /Namespaces of the copy (a merged file's, its reference already of the copy; 6.5 g, ISO 32000-2, 14.7.4).</summary>
    public void AddNamespace(PdfObject handle)
    {
        var root = WritableOf(_host.RootReference);
        var list = new PdfArray([.. NamespaceEntries(), handle]);
        root.Set(Namespaces, list);
        _changed = true;
    }

    /// <summary>A handle of the main file as the copy writes it (its reference renumbered).</summary>
    public PdfObject Written(PdfObject handle) => _host.Rebase(handle);

    /// <summary>An entry of a dictionary of the root (/RoleMap, /ClassMap) as stored; null if none.</summary>
    private PdfObject? RootEntry(PdfName map, PdfName key) =>
        Resolve(_host.RootReference) is PdfDictionary root && Resolve(root.GetRaw(map)) is PdfDictionary entries ? entries.GetRaw(key) : null;

    /// <summary>An entry added to a dictionary of the root (/RoleMap, /ClassMap): the dictionary written directly into the root.</summary>
    private void AddRootEntry(PdfName map, PdfName key, PdfObject value)
    {
        var root = WritableOf(_host.RootReference);
        var entries = new PdfDictionary();
        if (Resolve(root.GetRaw(map)) is PdfDictionary existing)
            foreach (var (k, v) in existing.Entries) entries.Set(k, v);
        entries.Set(key, value);
        root.Set(map, entries);
        _changed = true;
    }

    /// <summary>
    /// A range of keys of the parent tree for a merged file: its keys shifted by the first one returned keep their order and clash with none
    /// of the copy (docs/ABLAUFPLAN.md, 6.5 step 3).
    /// </summary>
    public long ReserveKeys(long count)
    {
        LoadPairs();
        var first = _nextKey;
        _nextKey += Math.Max(0, count);
        return first;
    }

    /// <summary>A pair of a merged file's parent tree, its key shifted and its value already of the copy.</summary>
    public void AddRebasedPair(long key, PdfObject value)
    {
        _rebasedPairs[key] = value;
        _changed = true;
    }

    /// <summary>The page (index) the content of an element begins on, following its /Pg and its kids; <see cref="int.MaxValue"/> if none.</summary>
    private int FirstPageOf(PdfObject handle)
    {
        if (handle is OutputReference o && _firstPages.TryGetValue(o.Number, out var known)) return known;
        var stack = new Stack<PdfObject>([handle]);
        var steps = 0;
        while (stack.TryPop(out var item) && ++steps < 5000)
        {
            if (Resolve(item) is not PdfDictionary d) continue;
            if (_host.PageIndexOf(d.GetRaw(Pg)) is { } page) return page;
            if (_view.Element(item) is null) continue;
            var items = _view.Items(item);
            for (var i = items.Count - 1; i >= 0; i--) stack.Push(items[i]);
        }
        return int.MaxValue;
    }

    // ------------------------------------------------------------ Keys and MCIDs

    /// <summary>A new key of the parent tree: above every key the tree has and above /ParentTreeNextKey (files have it wrong or not at all).</summary>
    public long NewKey()
    {
        LoadPairs();
        return _nextKey++;
    }

    /// <summary>
    /// The next free MCID of a page: above the largest its content marks (all streams of /Contents, named property lists too) and above the
    /// entries of its array in the parent tree; pages that share their content count together.
    /// </summary>
    public int NextMcid(int page)
    {
        var key = _host.ContentKey(page);
        if (!_nextMcid.TryGetValue(key, out var next))
        {
            var (content, properties) = _host.PageContent(page);
            next = Math.Max(MarkedContentIds.Largest(content, properties) + 1, ExistingArray(page)?.Count ?? 0);
        }
        _nextMcid[key] = next + 1;
        return next;
    }

    /// <summary>The key a page of the copy (by its object number) gets as /StructParents because it had none; null if it keeps its own.</summary>
    public int? NewPageKey(int pageNumber) => _newPageKeys.TryGetValue(pageNumber, out var key) ? (int)key : null;

    // ------------------------------------------------------------ The end

    /// <summary>
    /// Writes what the plan changed: the new elements as objects of the copy and one parent tree built anew — the pairs the copy keeps, the
    /// arrays of the pages changed, the keys of new object references; sorted, with /Kids and /Limits beyond <see cref="NumberTree.PairsPerNode"/> pairs,
    /// /ParentTreeNextKey the next free key. Nothing if nothing was changed.
    /// </summary>
    public void Finish()
    {
        if (!_changed) return;
        LoadPairs();
        var entries = new SortedDictionary<long, PdfObject>(_pairs!);
        foreach (var key in _removedKeys) entries.Remove(key);
        foreach (var (key, array) in _pageArrays) entries[key] = new PdfArray(array);
        foreach (var (key, element) in _objectKeys) entries[key] = element;
        foreach (var (key, value) in _rebasedPairs) entries[key] = value;
        // the values as 7.9.6 recommends (for number trees alike): an array an object of its own (No. 779)
        var written = entries.Select(e => (e.Key, StoreArray(_host.Rebase(e.Value)))).ToList();
        var tree = NumberTree.Build(written, Store);
        var root = WritableOf(_host.RootReference);
        root.Set(ParentTree, Store(tree));
        root.Set(ParentTreeNextKey, PdfInteger.Of(Math.Max(_nextKey, written.Count > 0 ? written[^1].Key + 1 : 0)));
        if (_addedIds.Count > 0)
        {
            // the /IDTree: the pairs the copy keeps and those of the merged files, built anew (7.9.6)
            var ids = new List<(byte[] Key, PdfObject Value)>();
            if (Resolve(root.GetRaw(IdTree)) is PdfDictionary idTree)
                foreach (var (key, value) in NameTree.Pairs(idTree, Resolve)) ids.Add((key, _host.Rebase(value)));
            ids.AddRange(_addedIds.Select(a => (a.Id, _host.Rebase(a.Element))));
            root.Set(IdTree, Store(NameTree.Build(ids, Store)));
        }
        foreach (var (number, element) in _newElements) _host.Enqueue(number, _host.Rebase(element));
        _changed = false;
    }

    // ------------------------------------------------------------ Inside

    /// <summary>A handle as the plan sees it: a new element, else what the copy has.</summary>
    private PdfObject? Resolve(PdfObject? handle) =>
        handle is OutputReference o && _newElements.TryGetValue(o.Number, out var element) ? element : _host.Resolve(handle);

    /// <summary>A value of the parent tree as written: an array an object of its own, anything else (a reference) as it is.</summary>
    private PdfObject StoreArray(PdfObject value)
    {
        if (value is not PdfArray array) return value;
        var number = _host.NewNumber();
        _host.Enqueue(number, array);
        return new OutputReference(number);
    }

    /// <summary>Writes a node of the copy as an object of its own.</summary>
    private OutputReference Store(PdfDictionary node)
    {
        var number = _host.NewNumber();
        _host.Enqueue(number, node);
        return new OutputReference(number);
    }

    /// <summary>The key of a handle for the lists of kids (handles of one element are equal by number).</summary>
    private static object KeyOf(PdfObject handle) => handle switch
    {
        PdfReference r => (0, r.Id.Number),
        OutputReference o => (1, o.Number),
        _ => handle,
    };

    private void Editable(PdfObject element)
    {
        if (!CanEdit(element)) throw new ArgumentException("Das Strukturelement lässt sich nicht ändern (kein eigenes Objekt).", nameof(element));
    }

    /// <summary>An element's /K as the plan changes it (MCIDs, content items, elements); the root's too.</summary>
    private List<PdfObject> KidsOf(PdfObject handle)
    {
        var key = KeyOf(handle);
        if (!_kids.TryGetValue(key, out var kids)) _kids[key] = kids = [.. _view.Items(handle)];
        return kids;
    }

    /// <summary>Writes the kids into the element (or the root) the copy writes.</summary>
    private void SetKids(PdfObject handle, List<PdfObject> kids) => WritableOf(handle).Set(K, new PdfArray(kids));

    /// <summary>The dictionary of an element (or the root) as the copy writes it: a new element itself, one of the source as its copy.</summary>
    private PdfDictionary WritableOf(PdfObject handle) => handle is OutputReference o ? _newElements[o.Number] : _host.Writable((PdfReference)handle);

    /// <summary>A new element (not yet in any /K): type, namespace, parent.</summary>
    private (PdfObject Handle, PdfDictionary Dictionary) Create(string type, PdfObject? ns, PdfObject parent)
    {
        var number = _host.NewNumber();
        var element = new PdfDictionary();
        element.Set(PdfName.Type, PdfName.Get("StructElem"));
        element.Set(S, PdfName.Get(type));
        if (ns is not null) element.Set(Ns, ns);
        element.Set(P, parent);
        _newElements[number] = element;
        return (new OutputReference(number), element);
    }

    /// <summary>
    /// Where an element goes when the tree has no place by the rules: at the end of the top Document; with none, a Document made under the root
    /// that takes all its kids (the root holds one Document, ISO 32000-2, Annex L). Null if the root cannot be changed.
    /// </summary>
    private Placement? TopDocument(string type)
    {
        var root = (PdfObject)_host.RootReference;
        var rootKids = KidsOf(root);
        // the Document by its standard type — or by its name, where a role map (against the rules, ISO 14289-1, 7.1) maps Document to a type of
        // its own: else every save would put another Document around the kids (docs/FEHLERGEDAECHTNIS.md, No. 633)
        if (rootKids.FirstOrDefault(k => CanEdit(k) && (_view.TypeOf(k) == "Document" || _view.Element(k)?.GetName(S)?.Value == "Document")) is { } document)
            return new Placement(document, KidsOf(document).Count);
        var made = Create("Document", NamespaceFor("Document"), root);
        var moved = new List<PdfObject>(rootKids.Count);
        foreach (var kid in rootKids)
        {
            if (kid is PdfReference reference && _view.Element(kid) is not null) _host.Writable(reference).Set(P, made.Handle);
            else if (Resolve(kid) is PdfDictionary direct && _view.Element(kid) is not null)
            {
                // a direct element: its copy names the new parent
                var copy = new PdfDictionary(direct.Count);
                foreach (var (key, value) in direct.Entries) copy.Set(key, value);
                copy.Set(P, made.Handle);
                moved.Add(copy);
                continue;
            }
            moved.Add(kid);
        }
        _kids[KeyOf(made.Handle)] = moved;
        SetKids(made.Handle, moved);
        rootKids.Clear();
        rootKids.Add(made.Handle);
        SetKids(root, rootKids);
        _changed = true;
        return new Placement(made.Handle, moved.Count);
    }

    /// <summary>
    /// The /NS a new element of the type gets (null: none), after the rules for new elements (step 6): in a tree with the namespace for PDF 2.0
    /// its types with /NS, the types only PDF 1.7 has in the default namespace — but no Note there (FENote instead); else only types of 1.7.
    /// </summary>
    private PdfObject? NamespaceFor(string type)
    {
        if (_pdf20Namespace is not null && StandardStructure.IsStandard(type, StructureSpace.Pdf20)) return _pdf20Namespace;
        if (_pdf20Namespace is not null && type == "Note") throw new ArgumentException("In einem Baum mit dem Namensraum für PDF 2.0 FENote statt Note.", nameof(type));
        if (StandardStructure.IsStandard(type, StructureSpace.Pdf17)) return null;
        throw new ArgumentException($"„{type}“ ist keine Standardart, die dieser Baum bekommen darf.", nameof(type));
    }

    /// <summary>The namespace for PDF 2.0 among /Namespaces of the root, as a reference (an element names it with /NS); null if it has none.</summary>
    private PdfObject? Pdf20Namespace()
    {
        if (Resolve(_host.RootReference) is not PdfDictionary root || Resolve(root.GetRaw(Namespaces)) is not PdfArray namespaces) return null;
        foreach (var entry in namespaces.Take(1000))
            if (entry is PdfReference && Resolve(entry) is PdfDictionary ns && StandardStructure.SpaceOf(ns) == StructureSpace.Pdf20)
                return entry;
        return null;
    }

    /// <summary>The content item for marked content: the MCID where the element's /Pg is the page, else a reference with /Pg.</summary>
    private PdfObject ContentItem(PdfObject element, int page, int mcid)
    {
        var pageNumber = _host.PageNumber(page);
        var pg = (Resolve(element) as PdfDictionary)?.GetRaw(Pg);
        var samePage = pg switch
        {
            OutputReference o => o.Number == pageNumber,
            PdfReference => _host.Rebase(pg) is OutputReference mapped && mapped.Number == pageNumber,
            _ => false,
        };
        if (samePage) return PdfInteger.Of(mcid);
        var reference = new PdfDictionary();
        reference.Set(PdfName.Type, PdfName.Get("MCR"));
        reference.Set(Pg, new OutputReference(pageNumber));
        reference.Set(Mcid, PdfInteger.Of(mcid));
        return reference;
    }

    /// <summary>A page's key into the parent tree: its own (/StructParents), else a new one the page gets when it is written.</summary>
    private long PageKey(int page)
    {
        if (_pageKeys.TryGetValue(page, out var key)) return key;
        if (_host.StructParents(page) is { } own) key = own;
        else _newPageKeys[_host.PageNumber(page)] = key = NewKey();
        return _pageKeys[page] = key;
    }

    /// <summary>The page's array of the parent tree as the plan changes it (a copy of the one the copy keeps).</summary>
    private List<PdfObject> PageArray(int page)
    {
        var key = PageKey(page);
        if (!_pageArrays.TryGetValue(key, out var array)) _pageArrays[key] = array = [.. ExistingArray(page) ?? []];
        return array;
    }

    /// <summary>The page's array of the parent tree as the copy keeps it; null if it has none.</summary>
    private IReadOnlyList<PdfObject>? ExistingArray(int page)
    {
        if (_host.StructParents(page) is not { } key) return null;
        LoadPairs();
        return _pairs!.TryGetValue(key, out var value) && Resolve(value) is PdfArray array ? [.. array.Take(1 << 20)] : null;
    }

    /// <summary>The pairs of the parent tree the copy keeps (after pruning; through /Kids), and the next free key.</summary>
    private void LoadPairs()
    {
        if (_pairs is not null) return;
        _pairs = [];
        var root = Resolve(_host.RootReference) as PdfDictionary;
        if (root is not null && Resolve(root.GetRaw(ParentTree)) is PdfDictionary tree)
            foreach (var (key, value) in NumberTree.Pairs(tree, Resolve))
                _pairs[key] = value;
        var next = root is not null && Resolve(root.GetRaw(ParentTreeNextKey)) is PdfInteger given ? given.Value : 0;
        _nextKey = Math.Max(next, _pairs.Count > 0 ? _pairs.Keys.Max() + 1 : 0);
    }
}
