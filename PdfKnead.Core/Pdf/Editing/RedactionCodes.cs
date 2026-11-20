using PdfKnead.Core.Localization;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Kinds of hits of „Suchen und schwärzen“ a code can be the default for.</summary>
[Flags]
public enum RedactionFinds
{
    None = 0,
    Email = 1,
    Phone = 2,
    Iban = 4,
    Card = 8,
    Date = 16,

    /// <summary>A person (KONZEPT.md, section 5.23).</summary>
    Person = 32,

    /// <summary>A company or organisation.</summary>
    Organisation = 64,

    /// <summary>A part of an address: street, house number, postal code, place, country, post office box.</summary>
    Address = 128,
}

/// <summary>
/// A redaction code (wish of 4.11.2026, docs/OFFENE-PUNKTE.md, „Schwärzungscodes“): the short code written into the bar („§ 5 IFG“), a short
/// text, what it means and its legal basis (both in the protocol), and the kinds of hits it is the default for.
/// </summary>
public sealed record RedactionReasonCode(string Code, string Short, string Meaning, string Basis, RedactionFinds Finds = RedactionFinds.None)
{
    /// <summary>How it is offered in a list: code and short text; the empty code is „no reason“.</summary>
    public string Title => Code.Length == 0 ? Tr.T("(kein Grund)") : Short.Length > 0 ? $"{Code} — {Short}" : Code;

    public override string ToString() => Title;

    /// <summary>The reason kept with a mark: short text and meaning (the code and the basis are kept beside it).</summary>
    public string Reason => Short.Length > 0 && Meaning.Length > 0 ? $"{Short}: {Meaning}" : Short.Length > 0 ? Short : Meaning.Length > 0 ? Meaning : Code;
}

/// <summary>
/// A reason chosen for marks: from a code (<paramref name="Code"/> and <paramref name="Basis"/> set) or an own one (both null), and whether the
/// code, else the reason, goes into the bar.
/// </summary>
public sealed record ReasonChoice(string Reason, string? Code = null, string? Basis = null, bool InBar = true)
{
    /// <summary>The choice of a code.</summary>
    public static ReasonChoice Of(RedactionReasonCode code, bool inBar) => new(code.Reason, code.Code, code.Basis.Length > 0 ? code.Basis : null, inBar);

    /// <summary>A mark with this reason.</summary>
    public RedactionElement On(RedactionElement mark) => mark with { Reason = Reason, ReasonCode = Code, ReasonBasis = Basis, ReasonInBar = InBar };
}

/// <summary>The codes of one country or legal area („Deutschland (Bund)“), as of <paramref name="Version"/>; <paramref name="Own"/>: from the user's folder.</summary>
public sealed record RedactionCodeSet(string Id, string Country, string Version, IReadOnlyList<RedactionReasonCode> Codes, bool Own = false)
{
    /// <summary>The code that is the default for a kind of hit; null: none (the code chosen for all applies).</summary>
    public RedactionReasonCode? For(RedactionFinds find) => find == RedactionFinds.None ? null : Codes.FirstOrDefault(c => (c.Finds & find) != 0);

    public override string ToString() => Country;
}

/// <summary>
/// The code sets: built in for Germany, the EU, Austria and Switzerland, each a text file with one code per line (tab-separated, also
/// editable in a spreadsheet); a file of the same name in <see cref="Folder"/> replaces the built-in one, further files there add sets.
/// </summary>
public static class RedactionCodes
{
    /// <summary>The built-in sets, in the order offered.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } = ["de", "eu", "at", "ch"];

    /// <summary>Folder of the user's own code files (<c>Schwaerzungscodes</c> beside the settings; tests set their own).</summary>
    public static string Folder { get; set; } = Path.Combine(AppLocations.UserDataFolder, "Schwaerzungscodes");

    /// <summary>A file holds at most this many codes (a crafted file must not fill the lists).</summary>
    public const int MaxCodes = 500;

    /// <summary>The text of a built-in set; null if there is none of that id.</summary>
    public static string? BuiltInText(string id)
    {
        using var stream = typeof(RedactionCodes).Assembly.GetManifestResourceStream($"Codes/{id}.txt");
        if (stream is null) return null;
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads a code file: lines „Land“ and „Stand“ with their value, then one code per line — code, short text, meaning, legal basis, kinds of
    /// hits (comma-separated: E-Mail, Telefon, IBAN, Karte, Datum, Person, Firma, Anschrift). Lines starting with # and empty lines are skipped; a line without a code
    /// too. Columns beyond those known are ignored, missing ones are empty.
    /// </summary>
    public static RedactionCodeSet Parse(string id, string text, bool own = false)
    {
        string country = id, version = "";
        var codes = new List<RedactionReasonCode>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cells = line.Split('\t').Select(c => c.Trim()).ToArray();
            string Cell(int i) => i < cells.Length ? RedactionReasons.Clean(cells[i]) ?? "" : "";
            switch (cells[0])
            {
                case "Land" when cells.Length >= 2 && codes.Count == 0:
                    country = Cell(1) is { Length: > 0 } c ? c : id;
                    continue;
                case "Stand" when cells.Length >= 2 && codes.Count == 0:
                    version = Cell(1);
                    continue;
            }
            if (Cell(0) is not { Length: > 0 } code || codes.Count >= MaxCodes) continue;
            codes.Add(new RedactionReasonCode(code, Cell(1), Cell(2), Cell(3), Finds(Cell(4))));
        }
        return new RedactionCodeSet(id, country, version, codes, own);
    }

    /// <summary>The kinds of hits named in a cell („E-Mail, Telefon, IBAN“); unknown names are skipped.</summary>
    public static RedactionFinds Finds(string cell)
    {
        var finds = RedactionFinds.None;
        foreach (var name in cell.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            finds |= name.ToUpperInvariant() switch
            {
                "E-MAIL" or "EMAIL" or "MAIL" => RedactionFinds.Email,
                "TELEFON" or "PHONE" => RedactionFinds.Phone,
                "IBAN" => RedactionFinds.Iban,
                "KARTE" or "KARTENNUMMER" or "CARD" => RedactionFinds.Card,
                "DATUM" or "DATE" => RedactionFinds.Date,
                "PERSON" or "PERSONEN" or "NAME" => RedactionFinds.Person,
                "FIRMA" or "FIRMEN" or "ORGANISATION" or "COMPANY" => RedactionFinds.Organisation,
                "ANSCHRIFT" or "ADRESSE" or "ADDRESS" => RedactionFinds.Address,
                _ => RedactionFinds.None,
            };
        return finds;
    }

    /// <summary>
    /// All sets: the built-in ones, each replaced by a file of the same name (<c>de.txt</c> …) in <paramref name="folder"/>, then the other
    /// files there in name order. A file that cannot be read is skipped.
    /// </summary>
    /// <param name="folder">The user's folder; null: <see cref="Folder"/>.</param>
    public static List<RedactionCodeSet> Load(string? folder = null)
    {
        folder ??= Folder;
        var own = new Dictionary<string, RedactionCodeSet>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*.txt").Order(StringComparer.OrdinalIgnoreCase).Take(50))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 1 << 20) continue;
                        var id = Path.GetFileNameWithoutExtension(file);
                        own[id] = Parse(id, File.ReadAllText(file), own: true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        var sets = new List<RedactionCodeSet>();
        foreach (var id in BuiltInIds)
            if (own.Remove(id, out var replaced)) sets.Add(replaced);
            else if (BuiltInText(id) is { } text) sets.Add(Parse(id, text));
        sets.AddRange(own.Values);
        return sets;
    }

    /// <summary>
    /// Copies a built-in set into the user's folder to be changed there (it then replaces the built-in one); an existing file is kept.
    /// </summary>
    /// <returns>The path of the file.</returns>
    public static string CopyForEditing(string id, string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".txt");
        if (!File.Exists(path) && BuiltInText(id) is { } text) File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>The set offered first for a language („de“, „de-AT“, „de-CH“, „en“ …): Austria, Switzerland, else Germany for German, else the EU.</summary>
    public static string DefaultFor(string? culture) => culture?.ToLowerInvariant() switch
    {
        { } c when c.EndsWith("-at", StringComparison.Ordinal) => "at",
        { } c when c.EndsWith("-ch", StringComparison.Ordinal) => "ch",
        { } c when c.StartsWith("de", StringComparison.Ordinal) => "de",
        _ => "eu",
    };
}
