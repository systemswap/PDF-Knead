using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        // ------------------------------------------------------------ Name and number trees as written (ISO 32000-2, 7.9.6, 7.9.7; No. 779)

        /// <summary>The name trees of the name dictionary (ISO 32000-2, 7.7.4, Table 32).</summary>
        private static readonly HashSet<string> NameTreeKeys =
            ["Dests", "AP", "JavaScript", "Pages", "Templates", "IDS", "URLS", "EmbeddedFiles", "AlternatePresentations", "Renditions"];

        /// <summary>Nodes and pairs looked at in one tree; a larger tree is written as it stands (it is not judged, so not built anew).</summary>
        private const int TreeBudget = 1_000_000;

        /// <summary>Depth of a tree as the readers of this program follow it (<see cref="NameTree"/>, <see cref="NumberTree"/>).</summary>
        private const int MaxTreeDepth = 32;

        /// <summary>A handle of the main source as the copy sees it: an object replaced for the copy (a pruned tree node), else the one read.</summary>
        private PdfObject? CurrentOfMain(PdfObject? handle) =>
            handle is PdfReference reference
                ? _sources[_main].Overrides.TryGetValue(reference.Id.Number, out var replaced) ? replaced : _main.Resolve(reference.Id)
                : handle;

        /// <summary>
        /// Whether a name or number tree of the main source can be written as it stands (ISO 32000-2, 7.9.6 and 7.9.7, Tables 36 and 37): the
        /// root with /Kids or the pairs, not both, and without /Limits; every other node an intermediate node (/Kids) or a leaf (the pairs) with
        /// /Limits naming its least and greatest key; kids as indirect references; the keys direct strings (names) or integers (numbers), in
        /// order through the whole tree — byte order, a key given twice only within one leaf (the leaves' ranges do not overlap); ascending
        /// numbers, each once —; the values as 7.9.6 recommends (<see cref="ValueAsWritten"/>); no node reached twice, at most 32 deep. A tree
        /// larger than <see cref="TreeBudget"/> counts as written as it stands. <paramref name="keep"/>: where given, a value it refuses makes the
        /// tree one to build anew without that pair.
        /// </summary>
        private bool TreeAsWritten(PdfDictionary root, bool numbers, Func<PdfObject, bool>? keep = null)
        {
            var entriesKey = numbers ? NumsKey : NamesKey;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var budget = TreeBudget;
            PdfObject? previous = null;
            var leafStart = false;
            var ok = Node(root, 0, out _, out _);
            return ok || budget < 0;

            bool Node(PdfDictionary node, int depth, out PdfObject? first, out PdfObject? last)
            {
                first = last = null;
                if (depth > MaxTreeDepth || --budget < 0 || !seen.Add(node)) return false;
                // an entry naming the void counts as absent (7.3.10, S-A A67; No. 798): such a node is no valid node, the tree is rebuilt
                bool Has(PdfName key) => node.GetRaw(key) is { } raw && CurrentOfMain(raw) is not (null or PdfNull);
                var hasKids = Has(KidsKey);
                if (hasKids == Has(entriesKey)) return false;
                if (depth == 0 ? Has(LimitsKey) : CurrentOfMain(node.GetRaw(LimitsKey)) is not PdfArray { Count: 2 }) return false;
                if (hasKids)
                {
                    if (CurrentOfMain(node.GetRaw(KidsKey)) is not PdfArray kids) return false;
                    for (var i = 0; i < kids.Count; i++)
                    {
                        if (kids[i] is not PdfReference || CurrentOfMain(kids[i]) is not PdfDictionary kid ||
                            !Node(kid, depth + 1, out var kidFirst, out var kidLast))
                            return false;
                        first ??= kidFirst;
                        last = kidLast ?? last;
                    }
                }
                else
                {
                    if (CurrentOfMain(node.GetRaw(entriesKey)) is not PdfArray pairs || pairs.Count % 2 != 0) return false;
                    leafStart = true;
                    for (var i = 0; i < pairs.Count; i += 2)
                    {
                        if (--budget < 0) return false;
                        var key = pairs[i];
                        if (numbers ? key is not PdfInteger : key is not PdfString) return false;
                        if (previous is not null)
                        {
                            var order = CompareTreeKeys(previous, key);
                            // numbers: each key once; names: a key given twice stays within one leaf
                            if (order > 0 || order == 0 && (numbers || leafStart)) return false;
                        }
                        if (!ValueAsWritten(pairs[i + 1]) || keep is not null && !keep(pairs[i + 1])) return false;
                        leafStart = false;
                        previous = key;
                        first ??= key;
                        last = key;
                    }
                }
                if (depth == 0) return true;
                // a node below the root names its least and greatest key — an empty one cannot
                var bounds = (PdfArray)CurrentOfMain(node.GetRaw(LimitsKey))!;
                return first is not null && last is not null && SameTreeKey(bounds[0], first) && SameTreeKey(bounds[1], last);
            }
        }

        private static int CompareTreeKeys(PdfObject a, PdfObject b) => a is PdfInteger x && b is PdfInteger y
            ? x.Value.CompareTo(y.Value)
            : NameTree.Compare(((PdfString)a).Bytes, ((PdfString)b).Bytes);

        private static bool SameTreeKey(PdfObject bound, PdfObject key) =>
            bound.GetType() == key.GetType() && CompareTreeKeys(bound, key) == 0;

        /// <summary>
        /// A value as 7.9.6 recommends (sentence 5, for number trees alike, 7.9.7): dictionaries, arrays and strings as indirect objects, null,
        /// numbers, booleans and names direct (streams are always indirect).
        /// </summary>
        private bool ValueAsWritten(PdfObject stored) => stored is PdfReference reference
            ? CurrentOfMain(reference) is not (PdfNull or PdfInteger or PdfReal or PdfBoolean or PdfName)
            : stored is not (PdfDictionary or PdfArray or PdfString);

        /// <summary>
        /// A value of a tree the writer builds, as <see cref="ValueAsWritten"/> asks: a direct dictionary, array or string an object of its own
        /// (cleaned as every object of the copy), a reference to a simple object that object itself; anything else as it stands (a reference is
        /// mapped when the node is written).
        /// </summary>
        private PdfObject TreeValue(PdfObject stored)
        {
            if (stored is PdfReference reference)
                return CurrentOfMain(reference) is var target && target is PdfNull or PdfInteger or PdfReal or PdfBoolean or PdfName ? target : stored;
            if (stored is not (PdfDictionary or PdfArray or PdfString)) return stored;
            var cleaned = Cleanup != CleanupItems.None ? DocumentCleanup.Filter(stored, Cleanup, _main, kept: _options.KeptAttachments) : stored;
            var number = NewNumber();
            _memoryQueue.Enqueue((number, Rebase(cleaned, _sources[_main])));
            return new OutputReference(number);
        }

        /// <summary>Writes a node of a tree built here as an object of its own (the values of the main source in it mapped).</summary>
        private OutputReference StoreTreeNode(PdfDictionary node)
        {
            var number = NewNumber();
            _memoryQueue.Enqueue((number, Rebase(node, _sources[_main])));
            return new OutputReference(number);
        }

        /// <summary>
        /// Every pair of a tree of the main source in the order of the tree, each node once, at most 32 deep — the key as read, the value as
        /// stored; null if the tree holds more than <see cref="TreeBudget"/> pairs and kids (it stays as it is).
        /// </summary>
        private List<(PdfObject Key, PdfObject Value)>? TreePairs(PdfDictionary root, PdfName entriesKey)
        {
            var pairs = new List<(PdfObject, PdfObject)>();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            stack.Push((root, 0));
            var budget = TreeBudget;
            while (stack.TryPop(out var item))
            {
                if (item.Depth > MaxTreeDepth || !seen.Add(item.Node)) continue;
                if (CurrentOfMain(item.Node.GetRaw(entriesKey)) is PdfArray entries)
                    for (var i = 0; i + 1 < entries.Count; i += 2)
                    {
                        if (--budget < 0) return null;
                        if (CurrentOfMain(entries[i]) is { } key) pairs.Add((key, entries[i + 1]));
                    }
                if (CurrentOfMain(item.Node.GetRaw(KidsKey)) is PdfArray kids)
                    for (var i = kids.Count - 1; i >= 0; i--)
                    {
                        if (--budget < 0) return null;
                        if (CurrentOfMain(kids[i]) is PdfDictionary kid) stack.Push((kid, item.Depth + 1));
                    }
            }
            return pairs;
        }

        /// <summary>
        /// A name or number tree of the main source built anew as the writer builds its own: the pairs as this program reads them — in a name
        /// tree every pair with a string key, also a key given twice (an attachment must not get lost); in a number tree the first pair of each
        /// integer key, the one the readers of this program take —, sorted, with /Kids and /Limits beyond <see cref="NameTree.PairsPerNode"/>
        /// pairs, the values as <see cref="TreeValue"/> writes them; without the pairs <paramref name="keep"/> refuses. Returns the reference to
        /// the new root; null if the tree is too large to read whole (it stays as it is). <paramref name="kept"/>: the pairs written.
        /// </summary>
        private OutputReference? RebuiltTree(PdfDictionary root, bool numbers, out int kept, Func<PdfObject, bool>? keep = null,
            IReadOnlyDictionary<string, byte[]>? renames = null)
        {
            kept = 0;
            if (TreePairs(root, numbers ? NumsKey : NamesKey) is not { } pairs) return null;
            if (keep is not null) pairs.RemoveAll(p => !keep(p.Value));
            kept = pairs.Count;
            PdfDictionary tree;
            if (numbers)
            {
                var keys = new HashSet<long>();
                var entries = new List<(long Key, PdfObject Value)>();
                // keys as the readers read them: an integral real is that integer, written as one (No. 896)
                foreach (var (key, value) in pairs)
                    if (NumberTree.KeyOf(key) is { } number && keys.Add(number)) entries.Add((number, TreeValue(value)));
                tree = NumberTree.Build(entries, StoreTreeNode);
            }
            else
                // a name as key stands for its bytes and is written as the string the tree needs (No. 896)
                // a renamed attachment under its file's name (7.7.4, sentence 47; No. 909)
                tree = NameTree.Build([.. pairs.Where(p => NameTree.KeyOf(p.Key) is not null)
                    .Select(p => (Renamed(NameTree.KeyOf(p.Key)!, renames), TreeValue(p.Value)))], StoreTreeNode);
            return StoreTreeNode(tree);
        }

        /// <summary>
        /// A name tree of the name dictionary as written: as stored if it can be written so (<see cref="TreeAsWritten"/>), else built anew; null
        /// when nothing is left in it (the entry goes from the name dictionary).
        /// </summary>
        private PdfObject? NameTreeWritten(PdfName key, PdfObject stored)
        {
            var renames = RenamesFor(key);
            if (!NameTreeKeys.Contains(key.Value) || CurrentOfMain(stored) is not PdfDictionary root ||
                renames is null && TreeAsWritten(root, numbers: false, KeptValue(key)) ||
                RebuiltTree(root, numbers: false, out var kept, KeptValue(key), renames) is not { } rebuilt)
                return Rebase(stored, _sources[_main]);
            return kept > 0 ? rebuilt : null;
        }

        /// <summary>
        /// Attachments renamed after their file (ISO 32000-2, 7.7.4, sentence 47, should: the key in /EmbeddedFiles matches /UF or /F of its
        /// file specification; decided on 17.11.2026, variant C): old key (its bytes as Latin-1) → new key. Planned once before
        /// writing (<see cref="PlanAttachmentRenames"/>); the tree is built with the new keys and every /GoToE action of the main file that names
        /// an old one is changed with it (<see cref="GoToERenamed"/>). Before, foreign keys stayed as they came (docs/FEHLERGEDAECHTNIS.md,
        /// No. 909).
        /// </summary>
        private readonly Dictionary<string, byte[]> _attachmentRenames = new(StringComparer.Ordinal);

        private IReadOnlyDictionary<string, byte[]>? RenamesFor(PdfName key) => key.Value == "EmbeddedFiles" && _attachmentRenames.Count > 0 ? _attachmentRenames : null;

        private static byte[] Renamed(byte[] key, IReadOnlyDictionary<string, byte[]>? renames) =>
            renames is not null && renames.TryGetValue(System.Text.Encoding.Latin1.GetString(key), out var renamed) ? renamed : key;

        /// <summary>
        /// Plans <see cref="_attachmentRenames"/>: an entry of the main file's /EmbeddedFiles whose key differs (as text) from /UF — else /F — of
        /// its file specification gets those bytes as key, unless
        /// <list type="bullet">
        /// <item>a script of the file names the old key (it would no longer find the attachment; also when not every script can be read);</item>
        /// <item>another entry has that name already, or several would get it (keys stay unique);</item>
        /// <item>the attachments of the file are not written (a part of the document without them).</item>
        /// </list>
        /// </summary>
        private void PlanAttachmentRenames()
        {
            if (_options.PartOfDocument && !_options.KeepDocumentAttachments) return;
            if (CurrentOfMain(_main.Catalog?.GetRaw(NamesKeyOfCatalog)) is not PdfDictionary names ||
                CurrentOfMain(names.GetRaw(PdfName.Get("EmbeddedFiles"))) is not PdfDictionary root || TreePairs(root, NamesKey) is not { } pairs)
                return;
            var wanted = new List<(string Old, string OldText, byte[] New, string NewText)>();
            var texts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in pairs)
            {
                if (NameTree.KeyOf(key) is not { } bytes) continue;
                var text = new PdfString(bytes).ToText();
                texts.Add(text);
                if (CurrentOfMain(value) is not PdfDictionary spec ||
                    (CurrentOfMain(spec.GetRaw(UfKey)) as PdfString ?? CurrentOfMain(spec.GetRaw(F)) as PdfString) is not { } name) continue;
                var nameText = name.ToText();
                if (nameText.Length == 0 || nameText == text) continue;
                wanted.Add((System.Text.Encoding.Latin1.GetString(bytes), text, name.Bytes, nameText));
            }
            if (wanted.Count == 0) return;
            var claimed = wanted.GroupBy(w => w.NewText, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var w in wanted)
            {
                if (texts.Contains(w.NewText) || claimed.Contains(w.NewText)) continue;
                if (DocumentScripts.Any(_main, script => script is null || script.Contains(w.OldText, StringComparison.Ordinal), incomplete: true)) continue;
                _attachmentRenames[w.Old] = w.New;
            }
        }

        private static readonly PdfName NamesKeyOfCatalog = PdfName.Get("Names");

        /// <summary>
        /// A /GoToE action of the main file whose target lies in this file (no /F) and names a renamed attachment (/T with /R /C and /N): a copy
        /// with the new name; null otherwise. Only the first level — a /T inside the target names an attachment of the embedded file (12.6.4.4,
        /// Table 205, 206).
        /// </summary>
        private PdfDictionary? GoToERenamed(PdfDictionary action, Source? source)
        {
            if (_attachmentRenames.Count == 0 || source is not { IsMain: true } || action.GetName(PdfName.Get("S"))?.Value != "GoToE" ||
                action.Get(F) is not PdfNull || action.Get(PdfName.Get("T")) is not PdfDictionary target ||
                target.GetName(PdfName.Get("R"))?.Value != "C" || target.Get(PdfName.Get("N")) is not PdfString name ||
                !_attachmentRenames.TryGetValue(System.Text.Encoding.Latin1.GetString(name.Bytes), out var renamed))
                return null;
            var newTarget = CloneWithResolver(target);
            newTarget.Set(PdfName.Get("N"), new PdfString(renamed));
            var copy = CloneWithResolver(action);
            copy.Set(PdfName.Get("T"), newTarget);
            return copy;
        }

        /// <summary>
        /// The values a name tree of the name dictionary may hold beyond its form: in /EmbeddedFiles only file specifications with an embedded
        /// file — /EF holding /F or /UF as a stream (ISO 32000-2, 7.7.4, Table 32) — and, in a PDF/A part without attachments of its own, only
        /// those the part allows (the PDF/A step takes the file out of the others; their entry would be left naming none). Null: any value.
        /// </summary>
        private Func<PdfObject, bool>? KeptValue(PdfName key) => key.Value != "EmbeddedFiles" ? null : stored =>
            CurrentOfMain(stored) is PdfDictionary spec && CurrentOfMain(spec.GetRaw(EfKey)) is PdfDictionary files &&
            (CurrentOfMain(files.GetRaw(F)) is PdfStream || CurrentOfMain(files.GetRaw(UfKey)) is PdfStream) &&
            (_pdfA is null || _pdfA.Part.KeepsAttachments() || AllowedAttachments(spec, _sources[_main]));


        /// <summary>
        /// The name dictionary with its trees as written (<see cref="NameTreeWritten"/>); null when every tree can be written as it stands — the
        /// dictionary is then written as before.
        /// </summary>
        private PdfDictionary? NamesWritten(PdfDictionary names)
        {
            var rebuilt = new Dictionary<PdfName, PdfObject?>();
            foreach (var (key, stored) in names.Entries)
                if (NameTreeKeys.Contains(key.Value) && CurrentOfMain(stored) is PdfDictionary root &&
                    (RenamesFor(key) is not null || !TreeAsWritten(root, numbers: false, KeptValue(key))) &&
                    RebuiltTree(root, numbers: false, out var kept, KeptValue(key), RenamesFor(key)) is { } tree)
                    rebuilt[key] = kept > 0 ? tree : null;
            if (rebuilt.Count == 0) return null;
            var copy = new PdfDictionary(names.Count);
            foreach (var (key, stored) in names.Entries)
                if (!rebuilt.TryGetValue(key, out var tree)) copy.Set(key, Rebase(stored, _sources[_main]));
                else if (tree is not null) copy.Set(key, tree);
            return copy;
        }

        /// <summary>
        /// The page labels of the main source built anew (their tree could not be written as it stands): the ranges as this program reads
        /// them (<see cref="Document.PageLabels.Ranges"/> — so every page keeps the label it shows), the values as <see cref="TreeValue"/>
        /// writes them; null if the tree is too large to read whole.
        /// </summary>
        private PdfDictionary? RebuiltPageLabels(PdfDictionary root)
        {
            if (Document.PageLabels.Ranges(root) is not { } ranges) return null;
            // the keys are page indices, and index 0 has a value (ISO 32000-2, 7.7.2, Table 29): a range beyond the last page labels none and
            // goes; without a range at 0 the pages before the first are read with their numbers (S-A A91) — written as a range of their own
            // (docs/FEHLERGEDAECHTNIS.md, No. 786)
            var kept = ranges.Where(r => r.Key < _main.PageCount).Select(r => (r.Key, TreeValue(r.Value))).ToList();
            if (kept.Count == 0 || kept[0].Key != 0)
            {
                var numbers = new PdfDictionary();
                numbers.Set(PdfName.Get("S"), PdfName.Get("D"));
                kept.Insert(0, (0, TreeValue(numbers)));
            }
            return NumberTree.Build(kept, StoreTreeNode);
        }

        /// <summary>
        /// Whether the page labels of the main source can be written as they stand: the tree as <see cref="TreeAsWritten"/> asks, its first key
        /// 0 and none beyond the last page (Table 29: keys are page indices, index 0 has a value).
        /// </summary>
        private bool PageLabelsAsWritten(PdfDictionary root) =>
            TreeAsWritten(root, numbers: true) && Document.PageLabels.Ranges(root) is { Count: > 0 } ranges && ranges[0].Key == 0 &&
            ranges[^1].Key < _main.PageCount;

        /// <summary>The page labels this copy writes for its pages (<see cref="Document.PageLabels.Tree"/>), the ranges as objects of their own (7.9.6).</summary>
        private PdfDictionary OwnPageLabels(IReadOnlyList<Document.PageLabel> labels)
        {
            var tree = Document.PageLabels.Tree(labels);
            var nums = tree.GetArray(NumsKey)!;
            var written = new PdfArray();
            for (var i = 0; i + 1 < nums.Count; i += 2)
            {
                written.Add(nums[i]);
                written.Add(TreeValue(nums[i + 1]));
            }
            tree.Set(NumsKey, written);
            return tree;
        }

        /// <summary>
        /// The structure tree root of the main source with its parent tree and ID tree as written: a tree the copy cannot write as it stands
        /// built anew; one the structure plan built (an <see cref="OutputReference"/>) is its own and stays.
        /// </summary>
        private PdfDictionary WithStructureTreesAsWritten(PdfDictionary root)
        {
            PdfDictionary? copy = null;
            foreach (var (key, numbers) in StructureTrees)
                if (root.GetRaw(key) is PdfReference or PdfDictionary && CurrentOfMain(root.GetRaw(key)) is PdfDictionary tree &&
                    !TreeAsWritten(tree, numbers) && RebuiltTree(tree, numbers, out _) is { } rebuilt)
                    (copy ??= Clone(root)).Set(key, rebuilt);
            return copy ?? root;
        }

        private static readonly (PdfName Key, bool Numbers)[] StructureTrees = [(ParentTreeKey, true), (IdTreeKey, false)];

        /// <summary>The object number of the main source's structure tree root; -1 if it has none as an object of its own.</summary>
        private int StructureRootNumber => _structureRootNumber ??=
            _main.Catalog?.GetRaw(StructTreeRoot) is PdfReference reference ? reference.Id.Number : -1;

        private int? _structureRootNumber;
    }
}
