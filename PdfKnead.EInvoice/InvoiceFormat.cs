using System.Globalization;
using System.Numerics;
using System.Text;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.EInvoice;

/// <summary>
/// The values of a readable invoice for display (KONZEPT.md, section 5.24, stage 2): dates in the language's form, amounts with thousands
/// separators, at least two decimals (more if the invoice gives them — unit prices) and the currency, percentages, quantities with the name
/// of their unit, codes with their name, an IBAN in groups of four, the cash discount of XRechnung (BT-20) spelled out. What does not parse
/// stays as written: the display never changes a value it cannot read. Everything is plain text (Bauvorgaben Sicherheit 8): control, bidi and
/// zero-width characters are made visible as ⟨U+202E⟩ and reported by <see cref="Check"/>, long values are cut with their length given.
/// </summary>
public static class InvoiceFormat
{
    /// <summary>The longest value shown in full (characters); a longer one is cut, with its length given.</summary>
    public const int MaxShown = 2000;

    /// <summary>How many findings of one kind <see cref="Check"/> lists before it only counts the rest.</summary>
    internal const int MaxFindingsPerKind = 20;

    /// <summary>The terms that hold a payment account: an IBAN is checked and shown in groups.</summary>
    internal static readonly HashSet<string> Accounts = new(StringComparer.Ordinal) { "BT-84", "BT-91" };

    /// <summary>
    /// The terms where a letter of another script that looks the same would deceive: invoice number, names and identifiers of the parties,
    /// electronic addresses, payment details. Mixed scripts in item names or notes are common and harmless, so they are not checked there.
    /// </summary>
    private static readonly HashSet<string> Deceivable = new(StringComparer.Ordinal)
    {
        "BT-1", "BT-27", "BT-28", "BT-29", "BT-30", "BT-31", "BT-32", "BT-34", "BT-43", "BT-44", "BT-45", "BT-46", "BT-47", "BT-48", "BT-49",
        "BT-58", "BT-59", "BT-60", "BT-61", "BT-62", "BT-63", "BT-83", "BT-84", "BT-85", "BT-86", "BT-89", "BT-90", "BT-91",
    };
    internal static readonly HashSet<string> Dates = new(StringComparer.Ordinal)
        { "BT-2", "BT-7", "BT-9", "BT-26", "BT-72", "BT-73", "BT-74", "BT-134", "BT-135" };

    internal static readonly HashSet<string> Amounts = new(StringComparer.Ordinal)
    {
        "BT-92", "BT-93", "BT-99", "BT-100", "BT-106", "BT-107", "BT-108", "BT-109", "BT-110", "BT-111", "BT-112", "BT-113", "BT-114",
        "BT-115", "BT-116", "BT-117", "BT-131", "BT-136", "BT-137", "BT-141", "BT-142", "BT-146", "BT-147", "BT-148",
    };

    internal static readonly HashSet<string> Percentages = new(StringComparer.Ordinal)
        { "BT-94", "BT-96", "BT-101", "BT-103", "BT-119", "BT-138", "BT-143", "BT-152" };

    internal static readonly HashSet<string> Quantities = new(StringComparer.Ordinal) { "BT-129", "BT-149" };

    /// <summary>The value of a term for display in the current language.</summary>
    /// <param name="field">The term.</param>
    /// <param name="invoice">The invoice (its currency BT-5 where an amount names none).</param>
    public static string Display(InvoiceField field, InvoiceData? invoice = null)
    {
        var text = field.Text?.Trim() ?? "";
        if (field.Node is { IsAttribute: false, Element.Binary: { } binary })
            return InvoiceAttachment.Describe(binary, field.Attribute("mimeCode"), field.Attribute("filename"));
        if (text.Length == 0) return "";
        var culture = Tr.Culture;
        if (Dates.Contains(field.Id) && Date(text) is { } date) return date.ToString("d", culture);
        if (Amounts.Contains(field.Id) && Number(text) is { } amount) return Amount(amount, text, field.Attribute("currencyID") ?? invoice?.Text("BT-5"));
        if (Percentages.Contains(field.Id) && Number(text) is { } percent) return Decimals(percent, text, culture, minimum: 0) + " %";
        if (Quantities.Contains(field.Id) && Number(text) is { } quantity)
        {
            var unit = field.Attribute("unitCode")?.Trim();
            var unitName = unit is null ? null : InvoiceCodes.Name("Unit", unit);
            return Decimals(quantity, text, culture, minimum: 0) + (unit is null ? "" : " " + (unitName ?? Plain(unit, 32)));
        }
        if (field.Id == "BT-20") return PaymentTerms(text, invoice?.Text("BT-5"));
        if (Accounts.Contains(field.Id) && IsAscii(text) && NumberChecks.IbanValid(text)) return Grouped(text);
        return InvoiceCodes.NameOf(field) is { } name ? $"{Plain(text, 64)} ({name})" : Plain(text);
    }

    /// <summary>An amount with the decimals the invoice gives (at least two) and its currency (a code, made plain).</summary>
    private static string Amount(decimal amount, string text, string? currency) =>
        Decimals(amount, text, Tr.Culture) + (string.IsNullOrWhiteSpace(currency) ? "" : " " + Plain(currency.Trim(), 16));

    /// <summary>
    /// The payment terms (BT-20) line by line; a cash discount in the form of XRechnung („#SKONTO#TAGE=14#PROZENT=2.25#“, optionally
    /// „BASISBETRAG=357.93#“) spelled out, every other line as written. Whether the form is kept exactly is a rule of stage 4 (BR-DE-18).
    /// </summary>
    private static string PaymentTerms(string text, string? currency)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            if (sb.Length >= MaxShown) break;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Discount(line, currency) ?? Plain(line));
        }
        return sb.Length <= MaxShown ? sb.ToString() : Plain(sb.ToString());
    }

    /// <summary>A cash discount line of XRechnung in words; null if the line is not one.</summary>
    internal static string? Discount(string line, string? currency)
    {
        if (!line.StartsWith("#SKONTO#", StringComparison.Ordinal) || !line.EndsWith('#') || line.Length > 200) return null;
        var parts = line[1..^1].Split('#');
        if (parts.Length is not (3 or 4) || parts[0] != "SKONTO") return null;
        if (!parts[1].StartsWith("TAGE=", StringComparison.Ordinal)
            || !int.TryParse(parts[1].AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var days)) return null;
        if (!parts[2].StartsWith("PROZENT=", StringComparison.Ordinal) || Number(parts[2][8..]) is not { } percent || percent < 0) return null;
        var culture = Tr.Culture;
        var rate = Decimals(percent, parts[2][8..], culture);
        var within = days == 1 ? Tr.T("1 Tag") : Tr.T("{0} Tagen", days.ToString("N0", culture));
        if (parts.Length == 3) return Tr.T("Skonto {0} % bei Zahlung innerhalb von {1}", rate, within);
        if (!parts[3].StartsWith("BASISBETRAG=", StringComparison.Ordinal) || Number(parts[3][12..]) is not { } basis) return null;
        return Tr.T("Skonto {0} % auf {2} bei Zahlung innerhalb von {1}", rate, within, Amount(basis, parts[3][12..], currency));
    }

    /// <summary>An IBAN in groups of four, as it is printed (ISO 13616-1, paper format).</summary>
    private static string Grouped(string iban)
    {
        var compact = iban.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return string.Join(' ', compact.Chunk(4).Select(c => new string(c)));
    }

    private static bool IsAscii(string text) => System.Text.Ascii.IsValid(text);

    /// <summary>
    /// A text for display as plain text (Bauvorgaben Sicherheit 8): control characters (also C1, which some terminals take as commands), format
    /// characters (bidi controls like U+202E, zero width), line and paragraph separators and the few letters and marks that are invisible
    /// (Hangul fillers, variation selectors, braille blank) are shown as ⟨U+XXXX⟩; tab and line feed stay. A text longer than
    /// <paramref name="max"/> characters is cut, with its full length given.
    /// </summary>
    public static string Plain(string? text, int max = MaxShown)
    {
        if (string.IsNullOrEmpty(text)) return "";
        max = Math.Max(max, 1);
        if (text.Length <= max && FirstHidden(text) < 0) return text;
        var sb = new StringBuilder(Math.Min(text.Length, max) + 16);
        var i = 0;
        while (i < text.Length && sb.Length < max)
        {
            if (HiddenAt(text, i, out var value, out var used)) sb.Append(CultureInfo.InvariantCulture, $"⟨U+{value:X4}⟩");
            else sb.Append(text, i, used);
            i += used;
        }
        if (i < text.Length) sb.Append(" … ").Append(Tr.T("(gekürzt, {0} Zeichen)", text.Length.ToString("N0", Tr.Culture)));
        return sb.ToString();
    }

    /// <summary>A value between bidi isolates (U+2068 … U+2069): a right-to-left value cannot reorder the text around it (Bauvorgaben Sicherheit 8).</summary>
    public static string Isolated(string text) => "\u2068" + text + "\u2069";

    /// <summary>Whether a character is not to be shown as it is (see <see cref="Plain"/>).</summary>
    internal static bool Hidden(Rune r)
    {
        if (r.Value is '\n' or '\t') return false;
        if (Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate) return true;
        // letters and marks that show nothing: combining grapheme joiner, Hangul fillers, Khmer inherent vowels, Mongolian selectors,
        // braille blank, variation selectors
        return r.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or 0x2800 or 0x3164 or 0xFFA0
            or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);
    }

    /// <summary>The place of the first hidden character; -1 if there is none.</summary>
    private static int FirstHidden(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (HiddenAt(text, i, out _, out var used)) return i;
            i += used;
        }
        return -1;
    }

    /// <summary>
    /// Whether the character at <paramref name="i"/> is hidden; <paramref name="value"/> is its code point (of a lone surrogate, which the reader
    /// lets none through, the surrogate), <paramref name="used"/> its length. A carriage return right before a line feed is the end of a line
    /// as Windows writes it (in invoices often as „&amp;#x0D;“) and is not hidden; a carriage return alone is — on a terminal it would let a
    /// later text overwrite an earlier one.
    /// </summary>
    private static bool HiddenAt(string text, int i, out int value, out int used)
    {
        if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out used) != System.Buffers.OperationStatus.Done)
        {
            value = text[i];
            used = 1;
            return true;
        }
        value = rune.Value;
        if (value == '\r' && i + 1 < text.Length && text[i + 1] == '\n') return false;
        return Hidden(rune);
    }

    /// <summary>The hidden characters of a text, each once, as ⟨U+XXXX⟩ (at most four named); null if there are none.</summary>
    private static string? HiddenIn(string? text)
    {
        if (text is null) return null;
        var first = FirstHidden(text);
        if (first < 0) return null;
        var found = new SortedSet<int>();
        for (var i = first; i < text.Length && found.Count < 5;)
        {
            if (HiddenAt(text, i, out var value, out var used)) found.Add(value);
            i += used;
        }
        var named = string.Join(", ", found.Take(4).Select(v => string.Create(CultureInfo.InvariantCulture, $"⟨U+{v:X4}⟩")));
        return found.Count > 4 ? named + ", …" : named;
    }

    /// <summary>
    /// The scripts of the first word that mixes Latin, Greek and Cyrillic letters (a letter that only looks like a Latin one — „Ѕ“, „о“ —
    /// in a name or an IBAN); null if no word does.
    /// </summary>
    private static string? MixedWord(string? text)
    {
        if (text is null) return null;
        var scripts = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(r))
            {
                if (BitOperations.PopCount((uint)scripts) > 1) return ScriptNames(scripts);
                scripts = 0;
                continue;
            }
            scripts |= ScriptOf(r);
        }
        return BitOperations.PopCount((uint)scripts) > 1 ? ScriptNames(scripts) : null;
    }

    /// <summary>1 Latin, 2 Greek, 4 Cyrillic, 0 any other script.</summary>
    private static int ScriptOf(Rune r) => r.Value switch
    {
        < 0x80 => 1,
        0xAA or 0xB5 or 0xBA or (>= 0xC0 and <= 0x24F) or (>= 0x1E00 and <= 0x1EFF) or (>= 0x2C60 and <= 0x2C7F) or (>= 0xA720 and <= 0xA7FF)
            or (>= 0xAB30 and <= 0xAB6F) or (>= 0xFF21 and <= 0xFF3A) or (>= 0xFF41 and <= 0xFF5A) => 1,
        (>= 0x370 and <= 0x3FF) or (>= 0x1F00 and <= 0x1FFF) => 2,
        (>= 0x400 and <= 0x52F) or (>= 0x1C80 and <= 0x1C8F) or (>= 0x2DE0 and <= 0x2DFF) or (>= 0xA640 and <= 0xA69F) => 4,
        _ => 0,
    };

    private static string ScriptNames(int scripts) => string.Join(", ", new[]
    {
        (scripts & 1) != 0 ? Tr.T("lateinisch") : null,
        (scripts & 2) != 0 ? Tr.T("griechisch") : null,
        (scripts & 4) != 0 ? Tr.T("kyrillisch") : null,
    }.OfType<string>());

    /// <summary>
    /// What the display noticed about the values (Bauvorgaben Sicherheit 8): hidden characters (also in the attributes and in the values
    /// without a term), an account that is not plain A–Z/0–9 or an IBAN whose check digits do not fit, a word that mixes scripts in a name, an
    /// identifier or a payment detail, and more payment groups than the direct debit is repeated in. At most <see cref="MaxFindingsPerKind"/>
    /// of a kind, then the number of the rest. The texts name the term, never the value.
    /// </summary>
    public static IReadOnlyList<InvoiceFinding> Check(InvoiceData invoice)
    {
        var findings = new List<InvoiceFinding>();
        var counts = new Dictionary<InvoiceFindingKind, (int Count, InvoiceFindingWeight Weight)>();
        void Add(InvoiceFindingKind kind, InvoiceFindingWeight weight, string text)
        {
            var n = counts.GetValueOrDefault(kind).Count + 1;
            counts[kind] = (n, weight);
            if (n <= MaxFindingsPerKind) findings.Add(new InvoiceFinding(kind, weight, text));
        }
        foreach (var field in invoice.Root.AllFields())
        {
            var label = $"{field.Label} ({field.Id})";
            var hidden = HiddenIn(field.Text);
            if (!field.Node.IsAttribute)
                foreach (var a in field.Node.Element.Attributes)
                    hidden ??= HiddenIn(a.Value);
            if (hidden is not null)
                Add(InvoiceFindingKind.HiddenCharacters, InvoiceFindingWeight.Finding,
                    Tr.T("{0}: enthält unsichtbare Steuer- oder Formatzeichen ({1}); in der Darstellung sind sie sichtbar gemacht.", label, hidden));
            if (Accounts.Contains(field.Id) && field.Text is { } account)
            {
                var compact = account.Trim().Replace(" ", "", StringComparison.Ordinal);
                if (!IsAscii(account))
                    Add(InvoiceFindingKind.AccountCharacters, InvoiceFindingWeight.Finding,
                        Tr.T("{0}: enthält Zeichen außerhalb von A–Z und 0–9, die in einer IBAN nicht vorkommen.", label));
                else if (compact.Length >= 5 && char.IsAsciiLetter(compact[0]) && char.IsAsciiLetter(compact[1]) && char.IsAsciiDigit(compact[2])
                         && char.IsAsciiDigit(compact[3]) && !NumberChecks.IbanValid(compact))
                    Add(InvoiceFindingKind.IbanCheckDigits, InvoiceFindingWeight.Finding,
                        Tr.T("{0}: Die Prüfziffer der IBAN stimmt nicht (ISO 13616) – bitte die Kontoverbindung prüfen.", label));
            }
            if (Deceivable.Contains(field.Id) && MixedWord(field.Text) is { } scripts)
                Add(InvoiceFindingKind.MixedScripts, InvoiceFindingWeight.Note,
                    Tr.T("{0}: mischt in einem Wort Buchstaben verschiedener Schriften ({1}) – sie können gleich aussehen.", label, scripts));
        }
        foreach (var further in invoice.Further)
            if ((HiddenIn(further.Text) ?? HiddenIn(further.Node.Element.Local)) is { } hidden)
                Add(InvoiceFindingKind.HiddenCharacters, InvoiceFindingWeight.Finding,
                    Tr.T("{0}: enthält unsichtbare Steuer- oder Formatzeichen ({1}); in der Darstellung sind sie sichtbar gemacht.",
                        Tr.T("Weitere Angabe {0}", Plain(further.Path, 200)), hidden));
        foreach (var (kind, (count, weight)) in counts)
            if (count > MaxFindingsPerKind)
                findings.Add(new InvoiceFinding(kind, weight, Tr.T("… und {0} weitere dieser Art.", (count - MaxFindingsPerKind).ToString("N0", Tr.Culture))));
        var paymentGroups = invoice.Groups("BG-16").ToList();
        if (paymentGroups.Count > InvoiceReader.MaxRepeatedPaymentGroups && paymentGroups.Any(g => g.GroupsOf("BG-19").Any()))
            findings.Add(new InvoiceFinding(InvoiceFindingKind.ManyPaymentGroups, InvoiceFindingWeight.Note,
                Tr.T("{0} Zahlungsgruppen: Die Angaben zur Lastschrift (BG-19) stehen nur bei den ersten {1}.", paymentGroups.Count,
                    InvoiceReader.MaxRepeatedPaymentGroups)));
        return findings;
    }

    /// <summary>A date of CII (format 102, „20261113“) or UBL (ISO, „2026-11-13“); null if it is neither or no real day.</summary>
    public static DateOnly? Date(string text)
    {
        // exactly these two forms: a dash elsewhere, a sign or a missing digit is no date (FG 831 — "-20210101" was read as a day)
        var t = text.Trim();
        var format = t.Length switch
        {
            8 => "yyyyMMdd",
            10 when t[4] == '-' && t[7] == '-' => "yyyy-MM-dd",
            _ => null,
        };
        return format is not null && t.All(ch => char.IsAsciiDigit(ch) || ch == '-')
            && DateOnly.TryParseExact(t, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>A number as XML writes it (dot as decimal separator, optional sign); null otherwise — a comma is not read as a decimal.</summary>
    public static decimal? Number(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>
    /// The number with the decimals the invoice gives, at least <paramref name="minimum"/> and at most 28 (all a decimal holds — a value with a
    /// million zeros after the point must not become a text of a million characters).
    /// </summary>
    private static string Decimals(decimal value, string text, CultureInfo culture, int minimum = 2)
    {
        var dot = text.IndexOf('.');
        var given = dot < 0 ? 0 : text.Length - dot - 1;
        return value.ToString("N" + Math.Clamp(given, minimum, 28).ToString(CultureInfo.InvariantCulture), culture);
    }
}

/// <summary>
/// What an attachment of the invoice (BT-125) is, by its first bytes — not by its name or declared type (KONZEPT.md, section 5.24,
/// „Bauvorgaben Sicherheit“ 9): the type is compared with the declared mimeCode, a program is named as such. Nothing is opened.
/// </summary>
public static class InvoiceAttachment
{
    /// <summary>The kind of the data by its first bytes.</summary>
    public enum Kind
    {
        Unknown,
        Pdf,
        Png,
        Jpeg,
        Gif,
        Tiff,
        Zip,
        Xml,
        Text,
        Program,
    }

    /// <summary>What the first bytes say.</summary>
    public static Kind Of(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("%PDF-"u8)) return Kind.Pdf;
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])) return Kind.Png;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return Kind.Jpeg;
        if (data.StartsWith("GIF8"u8)) return Kind.Gif;
        if (data.StartsWith("II*\0"u8) || data.StartsWith("MM\0*"u8)) return Kind.Tiff;
        if (data.StartsWith("PK\u0003\u0004"u8)) return Kind.Zip;
        // programs and scripts: Windows (MZ), Linux (ELF), Mach-O, a script with interpreter line
        if (data.StartsWith("MZ"u8) || data.StartsWith((ReadOnlySpan<byte>)[0x7F, (byte)'E', (byte)'L', (byte)'F'])
            || data.StartsWith((ReadOnlySpan<byte>)[0xCF, 0xFA, 0xED, 0xFE]) || data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xED, 0xFA, 0xCF])
            || data.StartsWith("#!"u8)) return Kind.Program;
        if (InvoiceXml.LooksLikeXml(data)) return Kind.Xml;
        var probe = data[..Math.Min(data.Length, 512)];
        if (probe.Length > 0 && System.Text.Unicode.Utf8.IsValid(probe) && !probe.ContainsAnyInRange((byte)0, (byte)8)) return Kind.Text;
        return Kind.Unknown;
    }

    /// <summary>The MIME types the code list allows for attachments and the kind each stands for.</summary>
    private static Kind KindOfMime(string? mime) => mime?.Trim().ToLowerInvariant() switch
    {
        "application/pdf" => Kind.Pdf,
        "image/png" => Kind.Png,
        "image/jpeg" => Kind.Jpeg,
        "text/csv" => Kind.Text,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/vnd.oasis.opendocument.spreadsheet" => Kind.Zip,
        _ => Kind.Unknown,
    };

    /// <summary>Whether the first bytes contradict the declared type (only for the types the code list allows).</summary>
    public static bool Contradicts(ReadOnlySpan<byte> data, string? mime)
    {
        var declared = KindOfMime(mime);
        return declared != Kind.Unknown && Of(data) != declared;
    }

    /// <summary>A line for display: name, size, declared type, and a warning if the bytes say otherwise or it is a program.</summary>
    public static string Describe(byte[] data, string? mime, string? name)
    {
        var size = data.Length < 1024 ? Tr.T("{0} Bytes", data.Length) : Tr.T("{0} KB", (data.Length + 512) / 1024);
        // name and type come from the invoice: plain text, cut (Bauvorgaben Sicherheit 8)
        var line = $"{(name is null ? Tr.T("ohne Namen") : InvoiceFormat.Plain(name, 120))} ({size}{(mime is null ? "" : ", " + InvoiceFormat.Plain(mime, 80))})";
        if (Of(data) == Kind.Program) return line + " — " + Tr.T("ein Programm! Nicht öffnen.");
        if (Contradicts(data, mime)) return line + " — " + Tr.T("der Inhalt passt nicht zum angegebenen Typ");
        return line;
    }
}
