using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>Copies of an owner (form, tiling pattern, soft-mask group, Type 3 font) by output number, with the resources each is written with.</summary>
        private readonly Dictionary<int, PdfDictionary> _variantLent = [];

        /// <summary>
        /// Dictionaries of the sources that name an owner with copies (a /XObject, /Pattern or /Font dictionary, a soft mask's /G): per name, the
        /// copy that name stands for there — applied wherever the dictionary is written (<see cref="Rebase"/>, <see cref="WriteObject"/>).
        /// </summary>
        private readonly Dictionary<PdfDictionary, Dictionary<PdfName, int>> _variantNames = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The resources an owner without resources of its own is written with (ISO 32000-2, 7.8.3; ISO 32000-1, 7.8.3, for writers of every
        /// version: they should not leave it inheriting): of what it is drawn with, only the names its content uses — also through nested content
        /// without resources of its own (colour spaces whole: an inline image names one in its own dictionary); and where the sets it is drawn
        /// with give a used name different things, or one has it and another not, it is written once per set that fits — the first on the owner
        /// itself, each further one on a copy of it that the names in those resources then stand for. Before, a copy below 2.0 left an owner
        /// drawn with colliding names inheriting, one of 2.0 gave it the first set where names collided (a second page showed its text in the
        /// first page's font), sets were merged even where one lacked a used name (test file pr4922: a glyph of a Type 3 font found a font on the
        /// second page that page does not have), and every resource of the page was lent (test file xfermodes2: a Type 3 font that uses none got
        /// the page's graphics states, and a reader decided otherwise about transparency; docs/FEHLERGEDAECHTNIS.md, No. 782, 895).
        /// </summary>
        /// <param name="owner">The owner (its dictionary as read).</param>
        /// <param name="givens">The resources it is drawn with, in the order met.</param>
        /// <param name="source">The source it belongs to.</param>
        private void PlanResourceVariants(PdfDictionary owner, List<PdfDictionary> givens, Source source)
        {
            var named = givens.SelectMany(g => Holders(g, owner)).FirstOrDefault();
            var content = named.Raw is null ? null : named.Raw is PdfReference reference ? source.Doc.Resolve(reference.Id) : named.Raw;
            var all = givens.Count == 1 ? givens[0] : givens.Skip(1).Aggregate(givens[0], ResourceUsage.Merged);
            // the content not found (the owner named nowhere in what it is drawn with): lent as before, merged
            var used = content is null ? null : UsedNames(source.Doc, content, all);
            var groups = new List<(PdfDictionary Lent, List<PdfDictionary> Givens)>();
            foreach (var given in givens)
            {
                var only = used is null ? given : OnlyUsed(given, used);
                var index = groups.FindIndex(g => !ResourceUsage.Collide(g.Lent, only) && SameNames(g.Lent, only));
                if (index < 0)
                {
                    groups.Add((only, [given]));
                    continue;
                }
                var (lent, members) = groups[index];
                members.Add(given);
                groups[index] = (ResourceUsage.Merged(lent, only), members);
            }
            _borrowed[owner] = groups[0].Lent;
            foreach (var group in groups) _lentSource[group.Lent] = source;
            for (var i = 1; i < groups.Count; i++)
            {
                var number = 0;
                foreach (var given in groups[i].Givens)
                    foreach (var (holder, name, raw) in Holders(given, owner))
                    {
                        if (number == 0) number = QueueVariant(owner, raw, source, groups[i].Lent);
                        if (!_variantNames.TryGetValue(holder, out var names)) _variantNames[holder] = names = [];
                        names[name] = number;
                    }
            }
        }

        private static readonly string[] ResourceCategories = ["ExtGState", "ColorSpace", "Pattern", "Shading", "XObject", "Font", "Properties"];

        /// <summary>The names operands of the owner's content use — a form's or pattern's stream, the glyph procedures of a Type 3 font — with
        /// those of content without resources of its own that it draws by a name of <paramref name="resources"/>.</summary>
        private static HashSet<string> UsedNames(PdfDocument doc, PdfObject owner, PdfDictionary resources)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<(PdfObject Owner, int Depth)>([(owner, 0)]);
            var operations = 0;
            while (pending.TryPop(out var next))
            {
                if (next.Depth > 8 || !seen.Add(next.Owner)) continue;
                var streams = next.Owner switch
                {
                    PdfStream stream => [stream],
                    PdfDictionary font when font.GetDictionary(PdfName.Get("CharProcs")) is { } procs =>
                        procs.Entries.Take(10_000).Select(e => procs.Get(e.Key)).OfType<PdfStream>().ToList(),
                    _ => new List<PdfStream>(),
                };
                var found = new List<string>();
                foreach (var stream in streams)
                {
                    byte[] data;
                    try
                    {
                        data = doc.GetStreamData(stream);
                    }
                    catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
                    {
                        continue;
                    }
                    var parser = new Content.ContentParser(data);
                    for (; operations < 1_000_000 && parser.Next() is not null; operations++)
                        foreach (var operand in parser.Operands)
                            if (operand.Name is { } name && used.Add(name.Value)) found.Add(name.Value);
                }
                // forms and patterns it draws that inherit as well look their names up in the same resources
                foreach (var name in found)
                    foreach (var category in new[] { "XObject", "Pattern" })
                        if (resources.GetDictionary(PdfName.Get(category))?.Get(PdfName.Get(name)) is PdfStream nested &&
                            nested.Dictionary.GetDictionary(PdfName.Resources) is null)
                            pending.Push((nested, next.Depth + 1));
            }
            return used;
        }

        /// <summary>The resources with, per category, only the names used (colour spaces whole; other entries such as /ProcSet as they are).</summary>
        private static PdfDictionary OnlyUsed(PdfDictionary resources, HashSet<string> used)
        {
            var only = new PdfDictionary { Resolver = resources.Resolver };
            foreach (var (key, raw) in resources.Entries)
            {
                if (key.Value == "ColorSpace" || !ResourceCategories.Contains(key.Value) || resources.Get(key) is not PdfDictionary entries)
                {
                    only.Set(key, raw);
                    continue;
                }
                var kept = new PdfDictionary { Resolver = entries.Resolver };
                foreach (var (name, value) in entries.Entries)
                    if (used.Contains(name.Value)) kept.Set(name, value);
                if (kept.Count > 0) only.Set(key, kept);
            }
            return only;
        }

        /// <summary>Do both name the same names in each category (what they name is compared by <see cref="ResourceUsage.Collide"/>)?</summary>
        private static bool SameNames(PdfDictionary first, PdfDictionary second)
        {
            foreach (var category in ResourceCategories)
            {
                var a = first.GetDictionary(PdfName.Get(category));
                var b = second.GetDictionary(PdfName.Get(category));
                if ((a?.Count ?? 0) != (b?.Count ?? 0)) return false;
                if (a is not null && b is not null && a.Entries.Any(e => !b.ContainsKey(e.Key))) return false;
            }
            return true;
        }

        /// <summary>
        /// Where resources name the owner: entries of their /XObject, /Pattern and /Font dictionaries, and the /G of a soft mask in their
        /// graphics states — with the dictionary holding the entry and the entry as written (a reference, or a font given directly).
        /// </summary>
        private static IEnumerable<(PdfDictionary Holder, PdfName Name, PdfObject Raw)> Holders(PdfDictionary resources, PdfDictionary owner)
        {
            static bool Is(PdfObject? value, PdfDictionary owner) =>
                value is PdfStream stream ? ReferenceEquals(stream.Dictionary, owner) : ReferenceEquals(value, owner);

            foreach (var category in new[] { "XObject", "Pattern", "Font" })
            {
                if (resources.GetDictionary(PdfName.Get(category)) is not { } entries) continue;
                foreach (var (name, raw) in entries.Entries.Take(10_000).ToList())
                    if (Is(entries.Get(name), owner)) yield return (entries, name, raw);
            }
            if (resources.GetDictionary(PdfName.Get("ExtGState")) is { } states)
                foreach (var (name, _) in states.Entries.Take(10_000).ToList())
                    if (states.GetDictionary(name)?.GetDictionary(PdfName.Get("SMask")) is { } mask && mask.GetRaw(PdfName.Get("G")) is { } group &&
                        Is(mask.Get(PdfName.Get("G")), owner))
                        yield return (mask, PdfName.Get("G"), group);
        }

        /// <summary>A further copy of the owner with <paramref name="lent"/> as its resources, under a number of its own.</summary>
        private int QueueVariant(PdfDictionary owner, PdfObject raw, Source source, PdfDictionary lent)
        {
            var number = NewNumber();
            if (raw is PdfReference reference)
            {
                // the object of the source written once more: WriteObject and WriteStream give it these resources
                _variantLent[number] = lent;
                _queue.Enqueue((source, reference.Id.Number, number));
            }
            else
            {
                // a Type 3 font given directly in a /Font dictionary
                var copy = (PdfDictionary)Rebase(owner, source);
                copy.Set(PdfName.Resources, LentObject(lent, source));
                _memoryQueue.Enqueue((number, copy));
            }
            return number;
        }

        /// <summary>A dictionary with the names that stand for copies there (<see cref="_variantNames"/>) pointing at those; itself if none.</summary>
        private PdfObject WithVariantNames(PdfObject obj)
        {
            if (_variantNames.Count == 0 || obj is not PdfDictionary dict || obj is PdfStream || !_variantNames.TryGetValue(dict, out var names)) return obj;
            var copy = CloneWithResolver(dict);
            foreach (var (name, number) in names) copy.Set(name, new OutputReference(number));
            return copy;
        }
    }
}
