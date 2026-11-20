using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// The containment rules of the standard structure types (ISO/TS 32005, 7.2, Table 5, for the namespaces of PDF 1.7 and 2.0; ISO 32000-2,
/// Annex L), as facts in <c>Containment.tsv</c>: which child type may not occur in which parent type, how often at most, which only where the
/// parent groups, which types hold no content directly. Types the facts do not know (own types not mapped yet, NonStruct, which takes the
/// rules of its parent) pass. Phase 6.7 completes it into the check of PDF/UA (docs/FEHLERGEDAECHTNIS.md, No. 507).
/// </summary>
public static partial class Containment
{
    private sealed record Facts(HashSet<(string Parent, string Child)> Forbidden, Dictionary<(string Parent, string Child), int> Most,
        HashSet<(string Parent, string Child)> OnlyGrouping, Dictionary<(string Parent, string Child), int> MostGrouping, HashSet<string> NoContent,
        HashSet<string> Known);

    private static readonly Lazy<Facts> Data = new(Load);

    private static Facts Load()
    {
        var facts = new Facts([], [], [], [], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        using var stream = typeof(Containment).Assembly.GetManifestResourceStream("Accessibility/Containment.tsv")
                           ?? throw new InvalidOperationException("Accessibility/Containment.tsv fehlt.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            switch (f[0])
            {
                case "nein" when f.Length >= 3:
                    facts.Forbidden.Add((f[1], f[2]));
                    facts.Known.Add(f[1]);
                    facts.Known.Add(f[2]);
                    break;
                case "max" when f.Length >= 4:
                    facts.Most[(f[1], f[2])] = int.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "gruppe" when f.Length >= 3:
                    facts.OnlyGrouping.Add((f[1], f[2]));
                    break;
                case "gruppe-max" when f.Length >= 4:
                    facts.MostGrouping[(f[1], f[2])] = int.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "ohne-inhalt" when f.Length >= 2:
                    facts.NoContent.Add(f[1]);
                    break;
            }
        }
        return facts;
    }

    /// <summary>The type as the facts name it: H1, H2 … are „Hn“.</summary>
    private static string Key(string type) =>
        type.Length >= 2 && type[0] == 'H' && type.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 && type[1] != '0' ? "Hn" : type;

    /// <summary>Whether the facts know the type (a standard type of the namespaces of PDF 1.7 and 2.0).</summary>
    public static bool Known(string type) => Data.Value.Known.Contains(Key(type));

    /// <summary>Whether <paramref name="child"/> may occur in <paramref name="parent"/> (a parent used as a grouping element where it says so).</summary>
    public static bool Allowed(string parent, string child, bool parentGroups = true)
    {
        var facts = Data.Value;
        var key = (Key(parent), Key(child));
        if (facts.Forbidden.Contains(key)) return false;
        return parentGroups || !facts.OnlyGrouping.Contains(key);
    }

    /// <summary>How often <paramref name="child"/> may occur in <paramref name="parent"/> at most; null for no limit.</summary>
    public static int? Most(string parent, string child, bool parentGroups = false)
    {
        var facts = Data.Value;
        var key = (Key(parent), Key(child));
        if (facts.Most.TryGetValue(key, out var most)) return most;
        return parentGroups && facts.MostGrouping.TryGetValue(key, out var grouping) ? grouping : null;
    }

    /// <summary>Whether elements of the type may hold content items directly (marked content, objects).</summary>
    public static bool MayHoldContent(string type) => !Data.Value.NoContent.Contains(Key(type));

    /// <summary>
    /// What a structure tree breaks of the rules, each as „parent &gt; child“, „parent &gt; n child“ or „type [content]“ (at most
    /// <paramref name="most"/>, 200 000 elements looked at): types resolved through the role map, NonStruct taking the rules of its parent, an
    /// element „grouping“ where it holds no content directly (an approximation, see the writer's role repair).
    /// </summary>
    public static List<string> Violations(PdfDictionary structTreeRoot, int most = 1000)
    {
        // the standard type (role maps and namespaces, StandardStructure); a type that stands for none keeps its name and passes (not Known)
        string Standard(PdfDictionary element) => StandardStructure.Resolve(element, structTreeRoot)?.Name ?? element.GetName(PdfName.Get("S"))!.Value;
        var found = new List<string>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject? K, string Parent)>();
        stack.Push((structTreeRoot.Get(PdfName.Get("K")), "StructTreeRoot"));
        var budget = 200_000;
        while (stack.TryPop(out var item) && --budget > 0 && found.Count < most)
        {
            var elements = new List<PdfDictionary>();
            var content = false;
            void One(PdfObject? kid)
            {
                if (kid is PdfInteger || kid is PdfDictionary d && d.GetName(PdfName.Type)?.Value is "MCR" or "OBJR") content = true;
                else if (kid is PdfDictionary e && e.GetName(PdfName.Get("S")) is not null && seen.Add(e)) elements.Add(e);
            }
            if (item.K is PdfArray array)
                for (var i = 0; i < array.Count && i < 100_000; i++) One(array.Get(i));
            else One(item.K);
            if (content && !MayHoldContent(item.Parent)) found.Add(item.Parent + " [content]");
            var types = elements.Select(Standard).ToList();
            if (Known(item.Parent))
                foreach (var group in types.Where(t => t != "NonStruct" && Known(t)).GroupBy(t => t))
                {
                    if (!Allowed(item.Parent, group.Key, !content)) found.Add(item.Parent + " > " + group.Key);
                    else if (Most(item.Parent, group.Key, !content) is { } limit && group.Count() > limit) found.Add($"{item.Parent} > {group.Count()} {group.Key}");
                }
            for (var i = 0; i < elements.Count; i++)
                stack.Push((elements[i].Get(PdfName.Get("K")), types[i] == "NonStruct" ? item.Parent : types[i]));
        }
        return found;
    }
}
