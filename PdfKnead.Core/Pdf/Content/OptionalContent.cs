using System.Globalization;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>What the content is drawn for: the events of the usage application dictionaries (ISO 32000-2, 8.11.4.4, Table 101).</summary>
public enum OptionalContentUse
{
    /// <summary>Viewing (event /View).</summary>
    View,

    /// <summary>Printing (event /Print).</summary>
    Print,

    /// <summary>Exporting to a format without optional content, such as page images (event /Export).</summary>
    Export,
}

/// <summary>
/// Optional content — "layers" (ISO 32000-2, 8.11): determines which layers are off according to the default configuration
/// (/OCProperties /D): /BaseState, /ON, /OFF; the usage application dictionaries of /AS for the use at hand (viewing, printing,
/// exporting); /Intent (a layer whose intent the configuration does not name has no effect on visibility). Optional content groups (OCG)
/// and membership dictionaries are evaluated: a visibility expression (/VE with /And, /Or, /Not over layers) first, otherwise /OCGs with /P
/// AnyOn, AllOn, AnyOff, AllOff (ISO 32000-2, 8.11.2.2). Only the layers listed in /OCProperties /OCGs that are layers (/Type /OCG) count
/// (8.11.3.2): content marked with any other dictionary is not optional and is visible (docs/FEHLERGEDAECHTNIS.md, No. 1132).
/// </summary>
public sealed class OptionalContent
{
    private readonly HashSet<PdfDictionary> _on = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _off = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The layers that count: the entries of /OCProperties /OCGs with /Type /OCG (8.11.2.1, 8.11.3.2). Before, every dictionary that was not
    /// a membership dictionary counted as a layer, and /BaseState /OFF or an /OFF list hid content that every 2.0 reader shows — removing
    /// hidden content took it along (No. 1132).
    /// </summary>
    private readonly HashSet<PdfDictionary> _listed = new(ReferenceEqualityComparer.Instance);

    /// <summary>Most entries of /OCGs read as layers that count.</summary>
    internal const int MaxListed = 100_000;
    private readonly bool _baseOff;

    /// <summary>The intents of the configuration (8.11.2.3): null for all (/All); empty: no layer counts, everything is visible.</summary>
    private readonly HashSet<string>? _intents = ["View"];

    /// <summary>For <see cref="NeverShown"/>: the print state; hidden counts only what is hidden in it as well.</summary>
    private OptionalContent? _alsoPrint;

    /// <summary>States set by the usage application dictionaries of /AS for the use at hand (ISO 32000-2, 8.11.4.4); they take precedence over ON/OFF.</summary>
    private readonly Dictionary<PdfDictionary, bool> _forced = new(ReferenceEqualityComparer.Instance);

    private OptionalContent(PdfDictionary? properties, OptionalContentUse use, string language)
    {
        if (properties?.GetDictionary(PdfName.Get("D")) is not { } config) return;
        // at most the first 100 000 entries (built for every page, mask and pattern cell): a layer beyond counts as unlisted, its content shows
        if (properties.GetArray(PdfName.Get("OCGs")) is { } groups)
            for (var i = 0; i < groups.Count && i < MaxListed; i++)
                if (groups.Get(i) is PdfDictionary group && group.GetName(PdfName.Type)?.Value == "OCG") _listed.Add(group);
        _baseOff = config.GetName(PdfName.Get("BaseState"))?.Value == "OFF";
        Collect(config.GetArray(PdfName.Get("ON")), _on);
        Collect(config.GetArray(PdfName.Get("OFF")), _off);
        _intents = config.Get(PdfName.Get("Intent")) switch
        {
            PdfName { Value: "All" } => null,
            PdfName name => [name.Value],
            PdfArray array when Names(array) is var names => names.Contains("All") ? null : names,
            _ => ["View"],
        };
        if (config.GetArray(PdfName.Get("AS")) is not { } usages) return;
        // the current states are those shown — PDF-Knead changes no layer by hand or by action, so they are /D with the /View dictionaries;
        // printing and exporting apply their own dictionaries over them (8.11.4.5 sentences 21 and 23). Before, they started from /D alone,
        // and a layer the screen turned off was printed (No. 1132)
        ApplyUsages(usages, OptionalContentUse.View, language, _forced);
        if (use == OptionalContentUse.View) return;
        var over = new Dictionary<PdfDictionary, bool>(ReferenceEqualityComparer.Instance);
        ApplyUsages(usages, use, language, over);
        foreach (var (group, state) in over) _forced[group] = state;
    }

    /// <summary>
    /// The usage application dictionaries of /AS whose /Event is the use at hand (8.11.4.4, 8.11.4.5): for each of their layers, the entries
    /// of its usage dictionary named by /Category give a recommendation; ON only if every one is ON, also across several dictionaries.
    /// /View, /Print, /Export give their state (a missing one leaves the layer as it is); /Language compares with the language of the program.
    /// /Zoom (the drawn page is the same at every magnification) and /User (the reader is not known) give none — the layer stays as the
    /// configuration has it. Only listed layers are set; the recommendations go into <paramref name="forced"/>.
    /// </summary>
    private void ApplyUsages(PdfArray usages, OptionalContentUse use, string language, Dictionary<PdfDictionary, bool> forced)
    {
        var eventName = use switch { OptionalContentUse.Print => "Print", OptionalContentUse.Export => "Export", _ => "View" };
        // every layer and category of every dictionary counted together: 1 000 dictionaries × 10 000 layers × 100 categories were 10^9 steps,
        // at every page, mask and pattern cell (docs/FEHLERGEDAECHTNIS.md, No. 1122)
        var work = 0L;
        for (var i = 0; i < usages.Count && i < 1000 && work < MaxUsageWork; i++)
        {
            if (usages.Get(i) is not PdfDictionary usage || usage.GetName(PdfName.Get("Event"))?.Value != eventName) continue;
            HashSet<string> categories = usage.Get(PdfName.Get("Category")) switch
            {
                PdfName single => [single.Value],
                PdfArray list => Names(list),
                _ => [],
            };
            if (categories.Count == 0 || usage.Get(PdfName.Get("OCGs")) is not { } listed) continue;
            var groups = new List<PdfDictionary>();
            if (listed is PdfDictionary one && _listed.Contains(one)) groups.Add(one);
            else if (listed is PdfArray members)
                for (var g = 0; g < members.Count && g < 10_000; g++)
                    if (members.Get(g) is PdfDictionary group && _listed.Contains(group)) groups.Add(group);
            var byLanguage = categories.Contains("Language") ? LanguageRecommendations(groups, language) : null;
            foreach (var group in groups)
            {
                if ((work += categories.Count) > MaxUsageWork) break;
                bool? state = null;
                foreach (var category in categories)
                {
                    var recommended = category switch
                    {
                        "View" => UsageState(group, "View", "ViewState"),
                        "Print" => UsageState(group, "Print", "PrintState"),
                        "Export" => UsageState(group, "Export", "ExportState"),
                        "Language" => byLanguage?.GetValueOrDefault(group),
                        _ => null,
                    };
                    if (recommended is { } r) state = (state ?? true) && r;
                }
                if (state is { } s) forced[group] = forced.TryGetValue(group, out var before) ? before && s : s;
            }
        }
    }

    /// <summary>Most steps (a layer in one category of one usage dictionary) of /AS; what lies beyond is not applied.</summary>
    private const long MaxUsageWork = 1_000_000;

    /// <summary>The state an entry of the layer's /Usage names (ON or OFF); null if it names none.</summary>
    private static bool? UsageState(PdfDictionary group, string entry, string key) =>
        group.GetDictionary(PdfName.Get("Usage"))?.GetDictionary(PdfName.Get(entry))?.GetName(PdfName.Get(key))?.Value switch
        {
            "ON" => true,
            "OFF" => false,
            _ => null,
        };

    /// <summary>
    /// /Language (8.11.4.4): the layers whose language matches that of the program exactly are ON; without an exact match, those whose
    /// language matches without the region and whose /Preferred is ON; all others OFF. Null without a language of the program.
    /// </summary>
    private static Dictionary<PdfDictionary, bool>? LanguageRecommendations(List<PdfDictionary> groups, string language)
    {
        if (string.IsNullOrEmpty(language)) return null;
        var primary = language.Split('-')[0];
        var langs = groups.Select(g => g.GetDictionary(PdfName.Get("Usage"))?.GetDictionary(PdfName.Get("Language"))).ToList();
        string? Lang(int i) => langs[i]?.GetString(PdfName.Get("Lang"))?.ToText();
        var exact = Enumerable.Range(0, groups.Count).Where(i => string.Equals(Lang(i), language, StringComparison.OrdinalIgnoreCase)).ToHashSet();
        var result = new Dictionary<PdfDictionary, bool>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < groups.Count; i++)
            result[groups[i]] = exact.Count > 0
                ? exact.Contains(i)
                : Lang(i) is { } lang && string.Equals(lang.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase)
                  && langs[i]!.GetName(PdfName.Get("Preferred"))?.Value == "ON";
        return result;
    }

    private static HashSet<string> Names(PdfArray array)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count && i < 100; i++)
            if (array.Get(i) is PdfName name) names.Add(name.Value);
        return names;
    }

    /// <summary>Reads the default layer configuration of <paramref name="doc"/>.</summary>
    /// <param name="doc">The document whose /OCProperties are evaluated.</param>
    /// <param name="print">For printing: also applies the states of layers that are to be visible or invisible only when printed (for example watermarks).</param>
    /// <returns>The visibility rules; without /OCProperties all content is visible.</returns>
    public static OptionalContent For(PdfDocument doc, bool print = false) => For(doc, print ? OptionalContentUse.Print : OptionalContentUse.View);

    /// <summary>Reads the default layer configuration of <paramref name="doc"/> for a use (viewing, printing, exporting).</summary>
    /// <param name="doc">The document whose /OCProperties are evaluated.</param>
    /// <param name="use">The use: decides which usage application dictionaries of /AS apply.</param>
    /// <param name="language">The language for /Language (BCP 47, for example de-DE); null: that of the program (<see cref="ProgramLanguage()"/>).</param>
    /// <returns>The visibility rules; without /OCProperties all content is visible.</returns>
    public static OptionalContent For(PdfDocument doc, OptionalContentUse use, string? language = null) =>
        new(doc.Catalog?.GetDictionary(PdfName.Get("OCProperties")), use, language ?? ProgramLanguage());

    /// <summary>
    /// The language of the program for /Language (8.11.4.4): the one chosen in PDF-Knead (<see cref="Tr.Current"/>), not the system's —
    /// before, the system's language decided, and a German system showed the German layers to an English user (No. 1132).
    /// </summary>
    /// <returns>A language with region, see <see cref="ProgramLanguage(string, CultureInfo)"/>.</returns>
    public static string ProgramLanguage() => ProgramLanguage(Tr.Current.Code, CultureInfo.CurrentUICulture);

    /// <summary>
    /// A language of the program with a region, as /Lang names one (BCP 47): the languages of PDF-Knead have none. Where the system's language
    /// is the same, its region is taken (de on de-AT: de-AT); otherwise the usual region of the language (en: en-US, de: de-DE), as .NET knows it.
    /// </summary>
    /// <param name="program">The code of the program's language (for example "de").</param>
    /// <param name="system">The culture of the system.</param>
    /// <returns>The language with region; the code alone if no region is known; the system's language without a code.</returns>
    internal static string ProgramLanguage(string program, CultureInfo system)
    {
        if (string.IsNullOrWhiteSpace(program)) return system.Name;
        if (string.Equals(system.TwoLetterISOLanguageName, program, StringComparison.OrdinalIgnoreCase) && !system.IsNeutralCulture && system.Name.Length > 0)
            return system.Name;
        try
        {
            return CultureInfo.CreateSpecificCulture(program).Name is { Length: > 0 } specific ? specific : program;
        }
        catch (CultureNotFoundException)
        {
            return program;
        }
    }

    /// <summary>
    /// Layers that are shown neither on screen nor when printed: what „Dokument bereinigen“ may remove as „Inhalte ausgeblendeter Ebenen“.
    /// A layer that is off on screen but printed (a watermark, a note for the paper) shows on paper and is not hidden here.
    /// </summary>
    /// <param name="doc">The document whose /OCProperties are evaluated.</param>
    /// <returns>The visibility rules; without /OCProperties all content is visible.</returns>
    public static OptionalContent NeverShown(PdfDocument doc)
    {
        var screen = For(doc);
        screen._alsoPrint = For(doc, print: true);
        return screen;
    }

    /// <summary>
    /// Builds /OCProperties for the print PDF (CUPS, print to PDF): all layers, with those off that are off when printing, so that every
    /// reader and every printer shows them as printed.
    /// </summary>
    /// <param name="doc">The source document; the references in the result point into it.</param>
    /// <returns>The new /OCProperties dictionary; null if the document has no layers (no /OCProperties with /OCGs).</returns>
    /// <remarks>At most the first 10,000 entries of /OCGs are taken over.</remarks>
    public static PdfDictionary? ForPrintConfig(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(PdfName.Get("OCProperties")) is not { } properties || properties.GetArray(PdfName.Get("OCGs")) is not { } groups)
            return null;
        var print = For(doc, print: true);
        var off = new PdfArray();
        var all = new PdfArray();
        for (var i = 0; i < groups.Count && i < 10_000; i++)
        {
            all.Add(groups[i]);
            if (groups.Get(i) is PdfDictionary group && print.GroupState(group) == false) off.Add(groups[i]);
        }
        var config = new PdfDictionary();
        config.Set(PdfName.Get("OFF"), off);
        var result = new PdfDictionary();
        result.Set(PdfName.Get("OCGs"), all);
        result.Set(PdfName.Get("D"), config);
        return result;
    }

    private static void Collect(PdfArray? array, HashSet<PdfDictionary> set)
    {
        if (array is null) return;
        for (var i = 0; i < array.Count; i++)
            if (array.Get(i) is PdfDictionary d) set.Add(d);
    }

    /// <summary>
    /// Determines whether content marked with <paramref name="obj"/> is invisible.
    /// </summary>
    /// <param name="obj">An OCG or OCMD dictionary, or a name looked up in /Resources /Properties.</param>
    /// <param name="resources">The resources used to resolve a name; may be null.</param>
    /// <returns>True if the content is hidden; false for anything that is not an OCMD or a layer listed in /OCGs.</returns>
    public bool IsHidden(PdfObject? obj, PdfDictionary? resources)
    {
        if (obj is PdfName name) obj = resources?.GetDictionary(PdfName.Get("Properties"))?.Get(name);
        if (obj is not PdfDictionary dict) return false;
        var visible = dict.IsType(PdfName.Get("OCMD")) ? MembershipVisible(dict) : GroupState(dict);
        return visible == false && (_alsoPrint is null || _alsoPrint.IsHidden(dict, resources));
    }

    /// <summary>Whether a layer counts for visibility: one of its intents (default /View) is among those of the configuration (8.11.2.3).</summary>
    private bool Counts(PdfDictionary ocg)
    {
        if (_intents is null) return true;
        if (_intents.Count == 0) return false;
        return ocg.Get(PdfName.Get("Intent")) switch
        {
            PdfName name => _intents.Contains(name.Value),
            PdfArray array => Names(array).Overlaps(_intents),
            _ => _intents.Contains("View"),
        };
    }

    /// <summary>The state of a layer (ON = true) as the configuration and /AS set it.</summary>
    private bool GroupOn(PdfDictionary ocg) =>
        _forced.TryGetValue(ocg, out var forced) ? forced : _baseOff ? _on.Contains(ocg) : !_off.Contains(ocg);

    /// <summary>Whether a layer is on as the configuration and /AS set it (for merging: an inserted layer keeps its state).</summary>
    internal bool IsOn(PdfDictionary ocg) => GroupOn(ocg);

    /// <summary>Whether a dictionary is a layer that counts: listed in /OCProperties /OCGs, with /Type /OCG (8.11.3.2).</summary>
    internal bool IsListed(PdfDictionary ocg) => _listed.Contains(ocg);

    /// <summary>
    /// The state of a layer for visibility; null if it has no effect: it is not a listed layer (8.11.3.2), or its intent is not one of the
    /// configuration's (8.11.2.3).
    /// </summary>
    private bool? GroupState(PdfDictionary ocg) => _listed.Contains(ocg) && Counts(ocg) ? GroupOn(ocg) : null;

    /// <summary>Whether content of a membership dictionary is visible; null if it has no effect (then it is visible).</summary>
    private bool? MembershipVisible(PdfDictionary ocmd)
    {
        // the same for every marked section and object naming it: once per dictionary — before, its lists were run through again at every
        // BDC, Do and annotation, with up to 16 million operators per page (docs/FEHLERGEDAECHTNIS.md, No. 1122)
        if (_memberships.TryGetValue(ocmd, out var known)) return known;
        var visible = MembershipVisibleCore(ocmd);
        if (_memberships.Count < 100_000) _memberships[ocmd] = visible;
        return visible;
    }

    private readonly Dictionary<PdfDictionary, bool?> _memberships = new(ReferenceEqualityComparer.Instance);

    private bool? MembershipVisibleCore(PdfDictionary ocmd)
    {
        // a visibility expression takes precedence over /OCGs and /P (Table 97); an invalid, cyclic or too deep one falls back to them
        // (docs/ABLAUFPLAN.md, 7.1; docs/FEHLERGEDAECHTNIS.md, No. 561)
        if (ocmd.Get(PdfName.Get("VE")) is PdfArray expression)
        {
            var budget = MaxExpressionNodes;
            if (Evaluate(expression, 0, ref budget, new HashSet<PdfArray>(ReferenceEqualityComparer.Instance)) is { Valid: true } result) return result.Value;
        }
        var groups = new List<PdfDictionary>();
        switch (ocmd.Get(PdfName.Get("OCGs")))
        {
            case PdfDictionary single:
                groups.Add(single);
                break;
            case PdfArray array:
                for (var i = 0; i < array.Count && i < 10_000; i++)
                    if (array.Get(i) is PdfDictionary d) groups.Add(d);
                break;
        }
        // layers without effect (8.11.2.3) count as if they were not listed; none left: no effect
        var states = groups.Select(GroupState).OfType<bool>().ToList();
        if (states.Count == 0) return null;
        return ocmd.GetName(PdfName.Get("P"))?.Value switch
        {
            "AllOn" => states.All(v => v),
            "AnyOff" => states.Any(v => !v),
            "AllOff" => states.All(v => !v),
            _ => states.Any(v => v),
        };
    }

    private const int MaxExpressionDepth = 32, MaxExpressionNodes = 1000;

    /// <summary>Result of a visibility expression: invalid (then /OCGs and /P apply), without effect (Value null), or visible / hidden.</summary>
    private readonly record struct Expression(bool Valid, bool? Value);

    /// <summary>
    /// A visibility expression: [/And op …], [/Or op …] (at least one operand), [/Not op] (exactly one); an operand is a layer or a nested
    /// expression. Invalid if it is malformed, cyclic, deeper than 32 or larger than 1 000 nodes. A membership dictionary as an operand is not
    /// a layer (8.11.2.2): the expression is invalid — before, it counted as a layer that is on, and a public test file hid a frame that every
    /// reader shows (docs/FEHLERGEDAECHTNIS.md, No. 600). Operands without effect (a layer whose intent does not count, 8.11.2.3) are left
    /// out; an expression with none left has no effect itself.
    /// </summary>
    private Expression Evaluate(PdfArray expression, int depth, ref int budget, HashSet<PdfArray> open)
    {
        var invalid = new Expression(false, null);
        if (depth > MaxExpressionDepth || --budget < 0 || expression.Count < 2 || !open.Add(expression)) return invalid;
        try
        {
            if (expression.Get(0) is not PdfName { Value: "And" or "Or" or "Not" } op || op.Value == "Not" && expression.Count != 2) return invalid;
            var values = new List<bool>(expression.Count - 1);
            for (var i = 1; i < expression.Count; i++)
            {
                if (--budget < 0) return invalid;   // every operand counts, not only the nested expressions (No. 1122)
                var operand = expression.Get(i) switch
                {
                    PdfDictionary { } membership when membership.GetName(PdfName.Type)?.Value == "OCMD" => invalid,
                    PdfDictionary layer => new Expression(true, GroupState(layer)),
                    PdfArray inner => Evaluate(inner, depth + 1, ref budget, open),
                    _ => invalid,
                };
                if (!operand.Valid) return invalid;
                if (operand.Value is { } known) values.Add(known);
            }
            if (values.Count == 0) return new Expression(true, null);
            return new Expression(true, op.Value switch
            {
                "Not" => !values[0],
                "And" => values.All(v => v),
                _ => values.Any(v => v),
            });
        }
        finally
        {
            open.Remove(expression);
        }
    }
}
