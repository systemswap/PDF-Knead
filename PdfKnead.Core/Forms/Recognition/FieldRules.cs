namespace PdfKnead.Core.Forms.Recognition;

/// <summary>What a label says about its field (KONZEPT.md, section 5.9a, step 5).</summary>
internal enum FieldMeaning
{
    Text,
    Signature,
    Date,
    DateEnglish,
    Amount,
    AmountEnglish,
    Number,
    PostCode,
    Email,
    Phone,
    Iban,
    Multiline,
}

/// <summary>
/// The rule table of the form recognition: which label means which kind of field, German and English. It is the file
/// <c>Forms/Recognition/Feldregeln.txt</c> in the program — extended there without changing code.
/// </summary>
internal static class FieldRules
{
    private sealed record Rule(string Kind, string Keyword);

    private static readonly Lazy<List<Rule>> Rules = new(Load);

    private static List<Rule> Load()
    {
        using var stream = typeof(FieldRules).Assembly.GetManifestResourceStream("Forms/Feldregeln.txt")
                           ?? throw new InvalidOperationException("Ressource fehlt: Feldregeln.txt");
        using var reader = new StreamReader(stream);
        var rules = new List<Rule>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            var colon = line.IndexOf(':');
            if (line.Length == 0 || line[0] == '#' || colon <= 0) continue;
            var kind = line[..colon].Trim();
            foreach (var keyword in line[(colon + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                rules.Add(new Rule(kind, keyword.ToLowerInvariant()));
        }
        return rules;
    }

    /// <summary>Does a keyword occur in the label? Short ones (up to three characters, „tel“, „nr.“) only as a word of their own.</summary>
    private static bool Has(string label, string keyword)
    {
        if (keyword.Length > 3 || !keyword.Any(char.IsLetterOrDigit)) return label.Contains(keyword, StringComparison.Ordinal);
        for (var at = label.IndexOf(keyword, StringComparison.Ordinal); at >= 0; at = label.IndexOf(keyword, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !char.IsLetterOrDigit(label[at - 1]);
            var end = at + keyword.Length;
            var after = end >= label.Length || !char.IsLetterOrDigit(label[end]) || !char.IsLetterOrDigit(keyword[^1]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>The meaning of a label: the first rule that matches; plain text if none does.</summary>
    public static FieldMeaning Meaning(string label)
    {
        var lower = label.ToLowerInvariant();
        foreach (var rule in Rules.Value)
        {
            if (rule.Kind is "pflicht" or "option" || !Has(lower, rule.Keyword)) continue;
            return rule.Kind switch
            {
                "text" => FieldMeaning.Text,
                "unterschrift" => FieldMeaning.Signature,
                "datum" => FieldMeaning.Date,
                "datum-en" => FieldMeaning.DateEnglish,
                "betrag" => FieldMeaning.Amount,
                "betrag-en" => FieldMeaning.AmountEnglish,
                "zahl" => FieldMeaning.Number,
                "plz" => FieldMeaning.PostCode,
                "email" => FieldMeaning.Email,
                "telefon" => FieldMeaning.Phone,
                "iban" => FieldMeaning.Iban,
                "mehrzeilig" => FieldMeaning.Multiline,
                _ => FieldMeaning.Text,
            };
        }
        return FieldMeaning.Text;
    }

    /// <summary>The label marks a field that must be filled in („*“, „Pflichtfeld“).</summary>
    public static bool Required(string label)
    {
        var lower = label.ToLowerInvariant();
        return Rules.Value.Any(r => r.Kind == "pflicht" && Has(lower, r.Keyword));
    }

    /// <summary>The label is a typical option of a choice („ja“, „nein“, „männlich“ …).</summary>
    public static bool IsOption(string label)
    {
        var lower = label.Trim().TrimEnd(':', '.').ToLowerInvariant();
        return Rules.Value.Any(r => r.Kind == "option" && lower == r.Keyword);
    }
}
