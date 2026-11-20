using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Barcodes;

/// <summary>Something the content offers to do — only ever after the user confirmed it, never by itself.</summary>
/// <param name="Label">Text of the button, translated.</param>
/// <param name="Target">The address exactly as it would be opened (host in its ASCII form).</param>
public sealed record BarcodeAction(string Label, string Target);

/// <summary>What the content of a code means: its kind, the fields, a possible action and warnings (all texts translated and safe to show).</summary>
/// <param name="Kind">Kind of content ("Überweisung (GiroCode)", "Web-Adresse" …).</param>
/// <param name="Rows">The fields, label and value.</param>
/// <param name="Action">What could be opened (web or mail address); null if nothing.</param>
/// <param name="Warnings">What deserves caution (look-alike addresses, hidden characters, wrong check digits …).</param>
public sealed record BarcodeMeaning(string Kind, IReadOnlyList<(string Label, string Value)> Rows, BarcodeAction? Action, IReadOnlyList<string> Warnings);

/// <summary>
/// Interprets the content of a code: payment forms (EPC "GiroCode", Swiss QR-bill), addresses, contacts, Wi-Fi, events, GS1 data,
/// article numbers, ISBN. Nothing is fetched or executed; every value goes through <see cref="Safe"/> before it is shown.
/// </summary>
public static class BarcodeInterpreter
{
    /// <summary>Longest value shown in full.</summary>
    private const int MaxValue = 2000;

    /// <summary>Interprets a content.</summary>
    /// <param name="format">Kind of the code.</param>
    /// <param name="text">The decoded content.</param>
    /// <param name="gs1">The code marks its content as GS1 data (FNC1 first).</param>
    /// <returns>The meaning.</returns>
    public static BarcodeMeaning Interpret(BarcodeFormat format, string text, bool gs1 = false)
    {
        var warnings = new List<string>();
        if (HasHiddenCharacters(text))
            warnings.Add(Tr.T("Der Inhalt enthält unsichtbare oder die Schreibrichtung umkehrende Zeichen; sie sind unten als ⟨U+…⟩ zu sehen. Damit lassen sich Adressen und Dateinamen tarnen."));
        var meaning = format switch
        {
            BarcodeFormat.Ean13 or BarcodeFormat.Ean8 or BarcodeFormat.UpcA or BarcodeFormat.UpcE => Article(format, text),
            _ when gs1 || format == BarcodeFormat.Gs1_128 => Gs1(text),
            _ => Text(text),
        };
        return meaning with { Warnings = [.. warnings, .. meaning.Warnings] };
    }

    // ------------------------------------------------------------ display safety

    /// <summary>
    /// The text safe to show: control characters as their pictures (␀ … ␟, ␡), other invisible characters and those that change the
    /// writing direction as ⟨U+XXXX⟩; line breaks stay; very long values are cut.
    /// </summary>
    public static string Safe(string text)
    {
        var b = new StringBuilder(Math.Min(text.Length, MaxValue) + 16);
        foreach (var rune in text.EnumerateRunes())
        {
            if (b.Length >= MaxValue)
            {
                b.Append(" …");
                break;
            }
            var v = rune.Value;
            if (v is '\n') b.Append('\n');
            else if (v is '\r' or '\t') b.Append(' ');
            else if (v < 0x20) b.Append((char)(0x2400 + v));
            else if (v == 0x7F) b.Append('␡');
            else if (Hidden(v)) b.Append($"⟨U+{v:X4}⟩");
            else b.Append(rune.ToString());
        }
        return b.ToString();
    }

    private static bool Hidden(int v) =>
        v is >= 0x80 and <= 0x9F                     // C1 controls
        || v is 0x061C or 0x200E or 0x200F           // direction marks
        || v is >= 0x202A and <= 0x202E              // embeddings and overrides
        || v is >= 0x2066 and <= 0x2069              // isolates
        || v is >= 0x200B and <= 0x200D or 0x2060 or 0xFEFF or 0x00AD   // zero width, soft hyphen
        || v is >= 0xE0000 and <= 0xE007F            // tags
        || v is >= 0xFFF9 and <= 0xFFFB;             // interlinear annotation

    private static bool HasHiddenCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is < 0x20 and not ('\n' or '\r' or '\t' or '\u001D' or '\u001E' or '\u0004') || rune.Value == 0x7F || Hidden(rune.Value))
                return true;
        return false;
    }

    private static BarcodeMeaning Make(string kind, List<(string, string)> rows, BarcodeAction? action = null, List<string>? warnings = null) =>
        new(kind, rows.Where(r => r.Item2.Length > 0).Select(r => (r.Item1, Safe(r.Item2))).ToList(), action, warnings ?? []);

    // ------------------------------------------------------------ text contents of 2D codes and Code 128/39

    private static BarcodeMeaning Text(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("BCD\n", StringComparison.Ordinal) || trimmed.StartsWith("BCD\r\n", StringComparison.Ordinal)) return Epc(text);
        if (trimmed.StartsWith("SPC\n", StringComparison.Ordinal) || trimmed.StartsWith("SPC\r\n", StringComparison.Ordinal)) return SwissQr(text);
        if (trimmed.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return Wifi(trimmed);
        if (trimmed.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) return VCard(trimmed);
        if (trimmed.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase)) return MeCard(trimmed);
        if (trimmed.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            return Event(trimmed);
        if (trimmed.StartsWith("MATMSG:", StringComparison.OrdinalIgnoreCase)) return MatMsg(trimmed);
        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return Mail(trimmed);
        if (trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return Make(Tr.T("Telefonnummer"), [(Tr.T("Nummer"), Uri.UnescapeDataString(trimmed[4..]))]);
        if (trimmed.StartsWith("SMSTO:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("sms:", StringComparison.OrdinalIgnoreCase)) return Sms(trimmed);
        if (trimmed.StartsWith("geo:", StringComparison.OrdinalIgnoreCase)) return Geo(trimmed);
        if (trimmed.StartsWith("URLTO:", StringComparison.OrdinalIgnoreCase)) return Address(trimmed[6..]);
        if (LooksLikeAddress(trimmed)) return Address(trimmed);
        return Make(Tr.T("Text"), [(Tr.T("Inhalt"), text)]);
    }

    private static bool LooksLikeAddress(string text) =>
        !text.Contains(' ') && !text.Contains('\n') && (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------ addresses

    /// <summary>A web address with everything that could make it look like another one (the check shared with links, <see cref="Security.AddressCheck"/>).</summary>
    private static BarcodeMeaning Address(string text)
    {
        var info = Security.AddressCheck.Check(text);
        if (info.Scheme is null) return Make(Tr.T("Adresse (ungültig)"), [(Tr.T("Inhalt"), text)], warnings: [.. info.Warnings]);
        var rows = new List<(string, string)> { (Tr.T("Adresse"), text) };
        // a code opens only web addresses here (mail addresses have their own reading)
        if (info.Scheme is not ("http" or "https"))
            return Make(Tr.T("Adresse"), rows, null, info.Scheme == "mailto" ? [Tr.T("Die Adresse verwendet „{0}:“ — so etwas öffnet PDF-Knead nicht.", info.Scheme)] : [.. info.Warnings]);
        rows.Add((Tr.T("Server"), info.Host!));
        return Make(Tr.T("Web-Adresse"), rows, new BarcodeAction(Tr.T("Im Browser öffnen …"), info.Target!), [.. info.Warnings]);
    }

    /// <summary>
    /// A mail address (RFC 6068). Handed to the mail program is only what is shown — recipients, copy, subject, text — written anew; a
    /// blind copy (bcc), attachments (attach, which some mail programs accept) and anything else are dropped with a warning, as are line
    /// breaks in recipients and subject (they could add header lines).
    /// </summary>
    private static BarcodeMeaning Mail(string text)
    {
        var rest = text[7..];
        var q = rest.IndexOf('?');
        var to = new List<string>();
        var cc = new List<string>();
        string? subject = null, body = null;
        var warnings = new List<string>();
        var dropped = new List<string>();
        void Add(List<string> list, string value)
        {
            foreach (var address in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) list.Add(OneLine(address));
        }
        Add(to, Uri.UnescapeDataString(q < 0 ? rest : rest[..q]));
        if (q >= 0)
            foreach (var part in rest[(q + 1)..].Split('&'))
            {
                var eq = part.IndexOf('=');
                var key = (eq < 0 ? part : part[..eq]).ToLowerInvariant();
                var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                switch (key)
                {
                    case "to": Add(to, value); break;
                    case "cc": Add(cc, value); break;
                    case "subject": subject = OneLine(value); break;
                    case "body": body = value; break;
                    case "bcc":
                        warnings.Add(Tr.T("Der Code verlangt eine versteckte Blindkopie an „{0}“ — sie wird nicht übernommen.", Safe(OneLine(value))));
                        break;
                    case "attach" or "attachment":
                        warnings.Add(Tr.T("Der Code verlangt einen Dateianhang („{0}“) — er wird nicht übernommen.", Safe(OneLine(value))));
                        break;
                    case "":
                        break;
                    default:
                        dropped.Add(Safe(OneLine(key)));
                        break;
                }
            }
        if (dropped.Count > 0) warnings.Add(Tr.T("Weitere Angaben im Code werden nicht übernommen: {0}", string.Join(", ", dropped.Distinct().Take(10))));
        var rows = new List<(string, string)> { (Tr.T("An"), string.Join(", ", to)) };
        if (cc.Count > 0) rows.Add((Tr.T("Kopie an"), string.Join(", ", cc)));
        if (subject is not null) rows.Add((Tr.T("Betreff"), subject));
        if (body is not null) rows.Add((Tr.T("Text"), body));
        // written anew from what is shown: addresses keep @ and the comma between them readable, everything else is escaped
        static string Escape(string value) => Uri.EscapeDataString(value).Replace("%40", "@", StringComparison.Ordinal);
        var target = new StringBuilder("mailto:").AppendJoin(',', to.Select(Escape));
        var separator = '?';
        void Parameter(string name, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            target.Append(separator).Append(name).Append('=').Append(value);
            separator = '&';
        }
        Parameter("cc", string.Join(',', cc.Select(Escape)));
        Parameter("subject", subject is null ? null : Uri.EscapeDataString(subject));
        Parameter("body", body is null ? null : Uri.EscapeDataString(body));
        var action = to.Count > 0 || cc.Count > 0 ? new BarcodeAction(Tr.T("E-Mail schreiben …"), target.ToString()) : null;
        return Make(Tr.T("E-Mail"), rows, action, warnings);
    }

    /// <summary>Line breaks (and other control characters) removed: recipients and subject are single header lines.</summary>
    private static string OneLine(string value)
    {
        if (!value.Any(char.IsControl)) return value;
        var b = new StringBuilder(value.Length);
        foreach (var c in value) b.Append(char.IsControl(c) ? ' ' : c);
        return b.ToString();
    }

    /// <summary>Fields of the form KEY:value;KEY:value;; with backslash escapes (MeCard, MATMSG, WIFI).</summary>
    private static List<(string Key, string Value)> Fields(string body)
    {
        var fields = new List<(string, string)>();
        var key = new StringBuilder();
        var value = new StringBuilder();
        var inValue = false;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                (inValue ? value : key).Append(body[++i]);
                continue;
            }
            if (!inValue && c == ':')
            {
                inValue = true;
                continue;
            }
            if (c == ';')
            {
                if (key.Length > 0) fields.Add((key.ToString().ToUpperInvariant(), value.ToString()));
                key.Clear();
                value.Clear();
                inValue = false;
                continue;
            }
            (inValue ? value : key).Append(c);
        }
        if (key.Length > 0) fields.Add((key.ToString().ToUpperInvariant(), value.ToString()));
        return fields;
    }

    private static string Field(List<(string Key, string Value)> fields, string key) =>
        string.Join(", ", fields.Where(f => f.Key == key).Select(f => f.Value));

    private static BarcodeMeaning MatMsg(string text)
    {
        var f = Fields(text[7..]);
        var to = Field(f, "TO");
        return Make(Tr.T("E-Mail"), [(Tr.T("An"), to), (Tr.T("Betreff"), Field(f, "SUB")), (Tr.T("Text"), Field(f, "BODY"))],
            to.Length > 0 ? new BarcodeAction(Tr.T("E-Mail schreiben …"), "mailto:" + Uri.EscapeDataString(to)) : null);
    }

    private static BarcodeMeaning Sms(string text)
    {
        string number, body = "";
        if (text.StartsWith("SMSTO:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[6..];
            var colon = rest.IndexOf(':');
            number = colon < 0 ? rest : rest[..colon];
            if (colon >= 0) body = rest[(colon + 1)..];
        }
        else
        {
            var rest = text[4..];
            var q = rest.IndexOf('?');
            number = Uri.UnescapeDataString(q < 0 ? rest : rest[..q]);
            if (q >= 0 && rest[(q + 1)..].StartsWith("body=", StringComparison.OrdinalIgnoreCase)) body = Uri.UnescapeDataString(rest[(q + 6)..]);
        }
        return Make(Tr.T("SMS"), [(Tr.T("An"), number), (Tr.T("Text"), body)]);
    }

    private static BarcodeMeaning Geo(string text)
    {
        var parts = text[4..].Split('?')[0].Split(',');
        var rows = new List<(string, string)>();
        if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
        {
            rows.Add((Tr.T("Breite"), lat.ToString("0.######", Tr.Culture) + "°"));
            rows.Add((Tr.T("Länge"), lon.ToString("0.######", Tr.Culture) + "°"));
        }
        else rows.Add((Tr.T("Inhalt"), text));
        return Make(Tr.T("Ort"), rows);
    }

    private static BarcodeMeaning Wifi(string text)
    {
        var f = Fields(text[5..]);
        var security = Field(f, "T");
        return Make(Tr.T("WLAN-Zugang"),
        [
            (Tr.T("Netzname"), Field(f, "S")),
            (Tr.T("Verschlüsselung"), security.Length == 0 || security.Equals("nopass", StringComparison.OrdinalIgnoreCase) ? Tr.T("keine") : security),
            (Tr.T("Passwort"), Field(f, "P")),
            (Tr.T("Verborgenes Netz"), Field(f, "H").Equals("true", StringComparison.OrdinalIgnoreCase) ? Tr.T("ja") : ""),
        ]);
    }

    private static BarcodeMeaning MeCard(string text)
    {
        var f = Fields(text[7..]);
        var name = Field(f, "N");
        var comma = name.IndexOf(',');
        if (comma > 0) name = name[(comma + 1)..].Trim() + " " + name[..comma].Trim();
        return Make(Tr.T("Kontakt"),
        [
            (Tr.T("Name"), name), (Tr.T("Firma"), Field(f, "ORG")), (Tr.T("Telefon"), Field(f, "TEL")), (Tr.T("E-Mail"), Field(f, "EMAIL")),
            (Tr.T("Anschrift"), Field(f, "ADR")), (Tr.T("Web-Adresse"), Field(f, "URL")), (Tr.T("Notiz"), Field(f, "NOTE")),
        ]);
    }

    /// <summary>Lines of a vCard/iCalendar with folded lines joined (RFC 6350, 3.2; RFC 5545, 3.1); name without parameters → value.</summary>
    private static List<(string Name, string Value)> Lines(string text)
    {
        var unfolded = text.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
        var lines = new List<(string, string)>();
        foreach (var line in unfolded.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon];
            var semicolon = name.IndexOf(';');
            if (semicolon >= 0) name = name[..semicolon];
            var dot = name.IndexOf('.');   // grouped properties: item1.TEL
            if (dot >= 0) name = name[(dot + 1)..];
            var value = line[(colon + 1)..].Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
            lines.Add((name.ToUpperInvariant(), value));
        }
        return lines;
    }

    private static string Values(List<(string Name, string Value)> lines, string name) =>
        string.Join("\n", lines.Where(l => l.Name == name).Select(l => l.Value.Trim()).Where(v => v.Length > 0));

    private static BarcodeMeaning VCard(string text)
    {
        var l = Lines(text);
        var name = Values(l, "FN");
        if (name.Length == 0)
        {
            var n = Values(l, "N").Split(';');
            name = string.Join(" ", n.Skip(1).Take(2).Append(n[0]).Where(p => p.Length > 0));
        }
        var address = string.Join("\n", l.Where(x => x.Name == "ADR").Select(x => string.Join(", ", x.Value.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))));
        return Make(Tr.T("Kontakt (vCard)"),
        [
            (Tr.T("Name"), name), (Tr.T("Firma"), Values(l, "ORG").Replace(';', ' ')), (Tr.T("Funktion"), Values(l, "TITLE")),
            (Tr.T("Telefon"), Values(l, "TEL")), (Tr.T("E-Mail"), Values(l, "EMAIL")), (Tr.T("Anschrift"), address),
            (Tr.T("Web-Adresse"), Values(l, "URL")), (Tr.T("Notiz"), Values(l, "NOTE")),
        ]);
    }

    private static BarcodeMeaning Event(string text)
    {
        var l = Lines(text);
        return Make(Tr.T("Termin"),
        [
            (Tr.T("Titel"), Values(l, "SUMMARY")), (Tr.T("Beginn"), IcsTime(Values(l, "DTSTART"))), (Tr.T("Ende"), IcsTime(Values(l, "DTEND"))),
            (Tr.T("Ort"), Values(l, "LOCATION")), (Tr.T("Beschreibung"), Values(l, "DESCRIPTION")),
        ]);
    }

    private static string IcsTime(string value)
    {
        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return value.Length == 8 ? t.ToString("d", Tr.Culture) : t.ToString("g", Tr.Culture) + (value.EndsWith('Z') ? " UTC" : "");
        return value;
    }

    // ------------------------------------------------------------ payments

    /// <summary>EPC QR code for SEPA credit transfers (EPC069-12, version 001/002).</summary>
    private static BarcodeMeaning Epc(string text)
    {
        var l = text.Replace("\r\n", "\n").Split('\n');
        string Line(int i) => i < l.Length ? l[i].Trim() : "";
        var warnings = new List<string>();
        if (Line(3) != "SCT") warnings.Add(Tr.T("Der GiroCode ist nicht wie vorgeschrieben aufgebaut."));
        var iban = Line(6).Replace(" ", "");
        if (iban.Length > 0 && !IbanValid(iban)) warnings.Add(Tr.T("Die Prüfziffer der IBAN stimmt nicht — die IBAN ist falsch."));
        var amount = Line(7);
        if (amount.StartsWith("EUR", StringComparison.Ordinal) && decimal.TryParse(amount[3..], NumberStyles.Number, CultureInfo.InvariantCulture, out var euro))
            amount = euro.ToString("N2", Tr.Culture) + " €";
        return Make(Tr.T("Überweisung (GiroCode)"),
        [
            (Tr.T("Empfänger"), Line(5)), (Tr.T("IBAN"), FormatIban(iban)), (Tr.T("BIC"), Line(4)), (Tr.T("Betrag"), amount),
            (Tr.T("Verwendungszweck"), Line(10)), (Tr.T("Referenz"), Line(9)), (Tr.T("Zweck-Schlüssel"), Line(8)), (Tr.T("Hinweis"), Line(11)),
        ], null, warnings);
    }

    /// <summary>Swiss QR-bill (SIX "Swiss Payment Standards", Implementation Guidelines QR-bill 2.x).</summary>
    private static BarcodeMeaning SwissQr(string text)
    {
        var l = text.Replace("\r\n", "\n").Split('\n');
        string Line(int i) => i < l.Length ? l[i].Trim() : "";
        string Address(int at)
        {
            // address type S: street, number, postcode, town; K: two address lines (postcode and town empty)
            var parts = Line(at) == "S" ? new[] { Line(at + 1), $"{Line(at + 2)} {Line(at + 3)}".Trim(), $"{Line(at + 4)} {Line(at + 5)}".Trim(), Line(at + 6) }
                : [Line(at + 1), Line(at + 2), Line(at + 3), $"{Line(at + 4)} {Line(at + 5)}".Trim(), Line(at + 6)];
            return string.Join("\n", parts.Where(p => p.Length > 0));
        }
        var warnings = new List<string>();
        var iban = Line(3).Replace(" ", "");
        if (iban.Length > 0 && !IbanValid(iban)) warnings.Add(Tr.T("Die Prüfziffer der IBAN stimmt nicht — die IBAN ist falsch."));
        var amount = Line(18);
        if (decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) amount = value.ToString("N2", Tr.Culture) + " " + Line(19);
        var referenceKind = Line(27) switch { "QRR" => Tr.T("QR-Referenz"), "SCOR" => Tr.T("Creditor Reference (ISO 11649)"), _ => "" };
        return Make(Tr.T("Schweizer QR-Rechnung"),
        [
            (Tr.T("Empfänger"), Address(4)), (Tr.T("IBAN"), FormatIban(iban)), (Tr.T("Betrag"), amount),
            (Tr.T("Zahlungspflichtig"), Address(20)), (referenceKind.Length > 0 ? referenceKind : Tr.T("Referenz"), Line(28)),
            (Tr.T("Mitteilung"), Line(29)), (Tr.T("Rechnungsinformationen"), Line(31)),
        ], null, warnings);
    }

    /// <summary>IBAN check (ISO 13616): country and check digits moved to the end, letters as 10 … 35, the number mod 97 must be 1.</summary>
    public static bool IbanValid(string iban)
    {
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit)) return false;
        var moved = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in moved.ToUpperInvariant())
        {
            var v = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = v >= 10 ? (remainder * 100 + v) % 97 : (remainder * 10 + v) % 97;
        }
        return remainder == 1;
    }

    private static string FormatIban(string iban) =>
        string.Join(" ", Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));

    // ------------------------------------------------------------ articles and GS1

    private static BarcodeMeaning Article(BarcodeFormat format, string digits)
    {
        var gtin = format switch
        {
            BarcodeFormat.UpcA => "0" + digits,
            BarcodeFormat.UpcE => "0" + Linear.LinearCodes.ExpandUpcE(digits[..7]) + digits[7],
            _ => digits,
        };
        var rows = new List<(string, string)> { (Tr.T("Artikelnummer (GTIN)"), gtin) };
        if (format == BarcodeFormat.UpcE) rows.Add((Tr.T("Ausgeschrieben (UPC-A)"), gtin[1..]));
        var kind = Tr.T("Artikelnummer");
        if (gtin.Length == 13)
        {
            var prefix = int.Parse(gtin[..3], CultureInfo.InvariantCulture);
            if (prefix is 978 or 979)
            {
                kind = gtin.StartsWith("9790", StringComparison.Ordinal) ? Tr.T("Musikalie (ISMN)") : Tr.T("Buch (ISBN)");
                rows.Add((Tr.T("ISBN-13"), gtin));
                if (prefix == 978) rows.Add((Tr.T("ISBN-10"), Isbn10(gtin)));
            }
            else if (prefix == 977)
            {
                kind = Tr.T("Zeitschrift (ISSN)");
                rows.Add((Tr.T("ISSN"), Issn(gtin)));
            }
            else rows.Add((Tr.T("Nummernkreis"), Gs1Prefixes.Name(prefix, gtin.Length) ?? Tr.T("unbekannt")));
        }
        else if (gtin.Length == 8 && int.TryParse(gtin[..3], CultureInfo.InvariantCulture, out var p8))
            rows.Add((Tr.T("Nummernkreis"), Gs1Prefixes.Name(p8, 8) ?? Tr.T("unbekannt")));
        rows.Add((Tr.T("Prüfziffer"), Tr.T("stimmt")));
        return Make(kind, rows);
    }

    private static string Isbn10(string isbn13)
    {
        var core = isbn13[3..12];
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (core[i] - '0') * (10 - i);
        var check = (11 - sum % 11) % 11;
        return core + (check == 10 ? "X" : check.ToString(CultureInfo.InvariantCulture));
    }

    private static string Issn(string ean)
    {
        var core = ean[3..10];
        var sum = 0;
        for (var i = 0; i < 7; i++) sum += (core[i] - '0') * (8 - i);
        var check = (11 - sum % 11) % 11;
        return $"{core[..4]}-{core[4..]}{(check == 10 ? "X" : check.ToString(CultureInfo.InvariantCulture))}";
    }

    /// <summary>GS1 element strings (GS1 General Specifications, section 3): application identifiers with their data.</summary>
    private static BarcodeMeaning Gs1(string text)
    {
        var rows = new List<(string, string)>();
        var warnings = new List<string>();
        var i = 0;
        // GS1 Digital Link: https://…/01/<GTIN>/10/<batch>…
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Text(text);
        while (i < text.Length)
        {
            if (text[i] == '\u001D')
            {
                i++;
                continue;
            }
            var ai = Gs1Elements.Identifier(text, i);
            if (ai is null)
            {
                rows.Add((Tr.T("Rest"), text[i..]));
                warnings.Add(Tr.T("Die GS1-Daten enthalten einen unbekannten Datenbezeichner."));
                break;
            }
            i += ai.Code.Length;
            int end;
            if (ai.Length > 0) end = Math.Min(text.Length, i + ai.Length);
            else
            {
                end = text.IndexOf('\u001D', i);
                if (end < 0) end = text.Length;
                end = Math.Min(end, i + ai.Max);
            }
            var data = text[i..end];
            i = end;
            rows.Add(($"({ai.Code}) {ai.Name}", Gs1Elements.Format(ai, data, warnings)));
        }
        return Make(Tr.T("GS1-Daten"), rows, null, warnings);
    }
}
