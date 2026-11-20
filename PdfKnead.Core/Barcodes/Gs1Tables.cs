using System.Text;
using System.Globalization;

namespace PdfKnead.Core.Barcodes;

/// <summary>
/// GS1 prefixes (the first three digits of a GTIN-13): which GS1 member organisation issued the number — not the country the product
/// comes from. Facts from the GS1 prefix list (https://www.gs1.org/standards/id-keys/company-prefix), compiled here.
/// </summary>
internal static class Gs1Prefixes
{
    // from, to, German, English
    private static readonly (int From, int To, string De, string En)[] Ranges =
    [
        (0, 19, "GS1 USA/Kanada", "GS1 US/Canada"), (20, 29, "interne Nummer (Händler)", "restricted circulation (retailer)"),
        (30, 39, "GS1 USA", "GS1 US"), (40, 49, "interne Nummer (Händler)", "restricted circulation (retailer)"),
        (50, 59, "Gutschein", "coupon"), (60, 139, "GS1 USA/Kanada", "GS1 US/Canada"),
        (200, 299, "interne Nummer (Händler, oft Gewicht oder Preis)", "restricted circulation (retailer, often weight or price)"),
        (300, 379, "GS1 Frankreich", "GS1 France"), (380, 380, "GS1 Bulgarien", "GS1 Bulgaria"), (383, 383, "GS1 Slowenien", "GS1 Slovenia"),
        (385, 385, "GS1 Kroatien", "GS1 Croatia"), (387, 387, "GS1 Bosnien und Herzegowina", "GS1 Bosnia and Herzegovina"),
        (389, 389, "GS1 Montenegro", "GS1 Montenegro"), (390, 390, "GS1 Kosovo", "GS1 Kosovo"),
        (400, 440, "GS1 Deutschland", "GS1 Germany"), (450, 459, "GS1 Japan", "GS1 Japan"), (460, 469, "GS1 Russland", "GS1 Russia"),
        (470, 470, "GS1 Kirgisistan", "GS1 Kyrgyzstan"), (471, 471, "GS1 Taiwan", "GS1 Taiwan"), (474, 474, "GS1 Estland", "GS1 Estonia"),
        (475, 475, "GS1 Lettland", "GS1 Latvia"), (476, 476, "GS1 Aserbaidschan", "GS1 Azerbaijan"), (477, 477, "GS1 Litauen", "GS1 Lithuania"),
        (478, 478, "GS1 Usbekistan", "GS1 Uzbekistan"), (479, 479, "GS1 Sri Lanka", "GS1 Sri Lanka"), (480, 480, "GS1 Philippinen", "GS1 Philippines"),
        (481, 481, "GS1 Belarus", "GS1 Belarus"), (482, 482, "GS1 Ukraine", "GS1 Ukraine"), (483, 483, "GS1 Turkmenistan", "GS1 Turkmenistan"),
        (484, 484, "GS1 Moldau", "GS1 Moldova"), (485, 485, "GS1 Armenien", "GS1 Armenia"), (486, 486, "GS1 Georgien", "GS1 Georgia"),
        (487, 487, "GS1 Kasachstan", "GS1 Kazakhstan"), (488, 488, "GS1 Tadschikistan", "GS1 Tajikistan"), (489, 489, "GS1 Hongkong", "GS1 Hong Kong"),
        (490, 499, "GS1 Japan", "GS1 Japan"), (500, 509, "GS1 Vereinigtes Königreich", "GS1 UK"), (520, 521, "GS1 Griechenland", "GS1 Greece"),
        (528, 528, "GS1 Libanon", "GS1 Lebanon"), (529, 529, "GS1 Zypern", "GS1 Cyprus"), (530, 530, "GS1 Albanien", "GS1 Albania"),
        (531, 531, "GS1 Nordmazedonien", "GS1 North Macedonia"), (535, 535, "GS1 Malta", "GS1 Malta"), (539, 539, "GS1 Irland", "GS1 Ireland"),
        (540, 549, "GS1 Belgien und Luxemburg", "GS1 Belgium and Luxembourg"), (560, 560, "GS1 Portugal", "GS1 Portugal"),
        (569, 569, "GS1 Island", "GS1 Iceland"), (570, 579, "GS1 Dänemark", "GS1 Denmark"), (590, 590, "GS1 Polen", "GS1 Poland"),
        (594, 594, "GS1 Rumänien", "GS1 Romania"), (599, 599, "GS1 Ungarn", "GS1 Hungary"), (600, 601, "GS1 Südafrika", "GS1 South Africa"),
        (603, 603, "GS1 Ghana", "GS1 Ghana"), (604, 604, "GS1 Senegal", "GS1 Senegal"), (608, 608, "GS1 Bahrain", "GS1 Bahrain"),
        (609, 609, "GS1 Mauritius", "GS1 Mauritius"), (611, 611, "GS1 Marokko", "GS1 Morocco"), (613, 613, "GS1 Algerien", "GS1 Algeria"),
        (615, 615, "GS1 Nigeria", "GS1 Nigeria"), (616, 616, "GS1 Kenia", "GS1 Kenya"), (618, 618, "GS1 Elfenbeinküste", "GS1 Côte d’Ivoire"),
        (619, 619, "GS1 Tunesien", "GS1 Tunisia"), (620, 620, "GS1 Tansania", "GS1 Tanzania"), (621, 621, "GS1 Syrien", "GS1 Syria"),
        (622, 622, "GS1 Ägypten", "GS1 Egypt"), (623, 623, "GS1 Brunei", "GS1 Brunei"), (624, 624, "GS1 Libyen", "GS1 Libya"),
        (625, 625, "GS1 Jordanien", "GS1 Jordan"), (626, 626, "GS1 Iran", "GS1 Iran"), (627, 627, "GS1 Kuwait", "GS1 Kuwait"),
        (628, 628, "GS1 Saudi-Arabien", "GS1 Saudi Arabia"), (629, 629, "GS1 Vereinigte Arabische Emirate", "GS1 United Arab Emirates"),
        (640, 649, "GS1 Finnland", "GS1 Finland"), (690, 699, "GS1 China", "GS1 China"), (700, 709, "GS1 Norwegen", "GS1 Norway"),
        (729, 729, "GS1 Israel", "GS1 Israel"), (730, 739, "GS1 Schweden", "GS1 Sweden"), (740, 740, "GS1 Guatemala", "GS1 Guatemala"),
        (741, 741, "GS1 El Salvador", "GS1 El Salvador"), (742, 742, "GS1 Honduras", "GS1 Honduras"), (743, 743, "GS1 Nicaragua", "GS1 Nicaragua"),
        (744, 744, "GS1 Costa Rica", "GS1 Costa Rica"), (745, 745, "GS1 Panama", "GS1 Panama"),
        (746, 746, "GS1 Dominikanische Republik", "GS1 Dominican Republic"), (750, 750, "GS1 Mexiko", "GS1 Mexico"),
        (754, 755, "GS1 Kanada", "GS1 Canada"), (759, 759, "GS1 Venezuela", "GS1 Venezuela"),
        (760, 769, "GS1 Schweiz und Liechtenstein", "GS1 Switzerland and Liechtenstein"), (770, 771, "GS1 Kolumbien", "GS1 Colombia"),
        (773, 773, "GS1 Uruguay", "GS1 Uruguay"), (775, 775, "GS1 Peru", "GS1 Peru"), (777, 777, "GS1 Bolivien", "GS1 Bolivia"),
        (778, 779, "GS1 Argentinien", "GS1 Argentina"), (780, 780, "GS1 Chile", "GS1 Chile"), (784, 784, "GS1 Paraguay", "GS1 Paraguay"),
        (786, 786, "GS1 Ecuador", "GS1 Ecuador"), (789, 790, "GS1 Brasilien", "GS1 Brazil"), (800, 839, "GS1 Italien", "GS1 Italy"),
        (840, 849, "GS1 Spanien", "GS1 Spain"), (850, 850, "GS1 Kuba", "GS1 Cuba"), (858, 858, "GS1 Slowakei", "GS1 Slovakia"),
        (859, 859, "GS1 Tschechien", "GS1 Czechia"), (860, 860, "GS1 Serbien", "GS1 Serbia"), (865, 865, "GS1 Mongolei", "GS1 Mongolia"),
        (867, 867, "GS1 Nordkorea", "GS1 North Korea"), (868, 869, "GS1 Türkei", "GS1 Türkiye"), (870, 879, "GS1 Niederlande", "GS1 Netherlands"),
        (880, 881, "GS1 Südkorea", "GS1 South Korea"), (884, 884, "GS1 Kambodscha", "GS1 Cambodia"), (885, 885, "GS1 Thailand", "GS1 Thailand"),
        (888, 888, "GS1 Singapur", "GS1 Singapore"), (890, 890, "GS1 Indien", "GS1 India"), (893, 893, "GS1 Vietnam", "GS1 Vietnam"),
        (896, 896, "GS1 Pakistan", "GS1 Pakistan"), (899, 899, "GS1 Indonesien", "GS1 Indonesia"), (900, 919, "GS1 Österreich", "GS1 Austria"),
        (930, 939, "GS1 Australien", "GS1 Australia"), (940, 949, "GS1 Neuseeland", "GS1 New Zealand"), (950, 950, "GS1 Global Office", "GS1 Global Office"),
        (951, 951, "GS1 Global Office (EPC)", "GS1 Global Office (EPC)"), (955, 955, "GS1 Malaysia", "GS1 Malaysia"), (958, 958, "GS1 Macau", "GS1 Macao"),
        (960, 969, "GS1 Global Office (GTIN-8)", "GS1 Global Office (GTIN-8)"), (977, 977, "Zeitschrift (ISSN)", "serial publication (ISSN)"),
        (978, 979, "Buch (ISBN)", "book (ISBN)"), (980, 980, "Rücknahmebeleg", "refund receipt"), (981, 984, "Gutschein (Währung)", "coupon (currency)"),
        (990, 999, "Gutschein", "coupon"),
    ];

    /// <summary>The issuing organisation of a prefix; null if none is assigned.</summary>
    public static string? Name(int prefix, int length)
    {
        // GTIN-8: the prefixes 0 … 1 and 2 are internal numbers or velocity codes
        if (length == 8 && prefix < 100) return Tr.Current.IsGerman ? "interne Nummer (Händler)" : "restricted circulation (retailer)";
        foreach (var r in Ranges)
            if (prefix >= r.From && prefix <= r.To)
                return Tr.Current.IsGerman ? r.De : r.En;
        return null;
    }
}

/// <summary>A GS1 application identifier.</summary>
/// <param name="Code">The digits of the identifier (2 … 4).</param>
/// <param name="Length">Fixed length of the data; 0 if variable.</param>
/// <param name="Max">Maximal length of variable data.</param>
/// <param name="Kind">How to show the data: "n" plain, "d" date YYMMDD, "g" GTIN/GLN/SSCC with check digit, "w" decimal (last digit of the
/// identifier = decimals), "c" ISO country code.</param>
/// <param name="De">German name.</param>
/// <param name="En">English name.</param>
internal sealed record Gs1Ai(string Code, int Length, int Max, char Kind, string De, string En)
{
    public string Name => Tr.Current.IsGerman ? De : En;
}

/// <summary>
/// The common GS1 application identifiers (GS1 General Specifications, section 3.2) and the reading of element strings. Facts from the
/// specification (https://www.gs1.org/standards/barcodes-epcrfid-id-keys/gs1-general-specifications), compiled here.
/// </summary>
internal static class Gs1Elements
{
    private static readonly Gs1Ai[] Table =
    [
        new("00", 18, 18, 'g', "Versandeinheit (SSCC)", "shipping container (SSCC)"),
        new("01", 14, 14, 'g', "Artikelnummer (GTIN)", "trade item (GTIN)"),
        new("02", 14, 14, 'g', "enthaltene Artikel (GTIN)", "contained trade items (GTIN)"),
        new("10", 0, 20, 'n', "Charge", "batch or lot"),
        new("11", 6, 6, 'd', "Herstellungsdatum", "production date"),
        new("12", 6, 6, 'd', "Fälligkeitsdatum", "due date"),
        new("13", 6, 6, 'd', "Verpackungsdatum", "packaging date"),
        new("15", 6, 6, 'd', "Mindesthaltbarkeit", "best before"),
        new("16", 6, 6, 'd', "Verkaufsende", "sell by"),
        new("17", 6, 6, 'd', "Verfallsdatum", "expiration date"),
        new("20", 2, 2, 'n', "Variante", "variant"),
        new("21", 0, 20, 'n', "Seriennummer", "serial number"),
        new("22", 0, 20, 'n', "Verbrauchsartikel-Variante", "consumer product variant"),
        new("235", 0, 28, 'n', "Seriennummer (TPX)", "third-party serial (TPX)"),
        new("240", 0, 30, 'n', "zusätzliche Produktangabe", "additional product identification"),
        new("241", 0, 30, 'n', "Kunden-Artikelnummer", "customer part number"),
        new("250", 0, 30, 'n', "sekundäre Seriennummer", "secondary serial number"),
        new("251", 0, 30, 'n', "Ursprungsangabe", "reference to source entity"),
        new("253", 0, 30, 'n', "Dokumentnummer (GDTI)", "document type (GDTI)"),
        new("254", 0, 20, 'n', "Ortskennung (GLN-Zusatz)", "GLN extension"),
        new("30", 0, 8, 'n', "Menge", "variable count"),
        new("37", 0, 8, 'n', "Anzahl enthaltener Einheiten", "count of contained items"),
        new("400", 0, 30, 'n', "Bestellnummer des Kunden", "customer purchase order"),
        new("401", 0, 30, 'n', "Sendungsnummer (GINC)", "consignment (GINC)"),
        new("402", 17, 17, 'n', "Sendungskennung (GSIN)", "shipment (GSIN)"),
        new("403", 0, 30, 'n', "Leitweg", "routing code"),
        new("410", 13, 13, 'g', "Lieferung an (GLN)", "ship to (GLN)"),
        new("411", 13, 13, 'g', "Rechnung an (GLN)", "bill to (GLN)"),
        new("412", 13, 13, 'g', "gekauft bei (GLN)", "purchased from (GLN)"),
        new("413", 13, 13, 'g', "Endempfänger (GLN)", "ship for (GLN)"),
        new("414", 13, 13, 'g', "Ort (GLN)", "physical location (GLN)"),
        new("415", 13, 13, 'g', "Rechnungssteller (GLN)", "invoicing party (GLN)"),
        new("416", 13, 13, 'g', "Herstellungsort (GLN)", "production location (GLN)"),
        new("420", 0, 20, 'n', "Postleitzahl Empfänger", "ship-to postal code"),
        new("421", 0, 12, 'n', "Postleitzahl Empfänger mit Land", "ship-to postal code with country"),
        new("422", 3, 3, 'c', "Ursprungsland", "country of origin"),
        new("8004", 0, 30, 'n', "Anlagennummer (GIAI)", "individual asset (GIAI)"),
        new("8005", 6, 6, 'n', "Preis je Einheit", "price per unit"),
        new("8008", 0, 12, 'n', "Herstellungszeit", "production date and time"),
        new("8020", 0, 25, 'n', "Zahlungsreferenz", "payment slip reference"),
        new("90", 0, 30, 'n', "vereinbarte Angabe", "mutually agreed information"),
        new("91", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("92", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("93", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("94", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("95", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("96", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("97", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("98", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
        new("99", 0, 90, 'n', "firmeninterne Angabe", "company internal information"),
    ];

    // measures: 31nn … 36nn (fixed six digits, last digit of the identifier = decimals) and amounts 390n … 393n
    private static readonly (string Prefix, string De, string En, int Length)[] Measures =
    [
        ("310", "Nettogewicht (kg)", "net weight (kg)", 6), ("311", "Länge (m)", "length (m)", 6), ("312", "Breite (m)", "width (m)", 6),
        ("313", "Höhe (m)", "height (m)", 6), ("314", "Fläche (m²)", "area (m²)", 6), ("315", "Nettovolumen (l)", "net volume (l)", 6),
        ("316", "Nettovolumen (m³)", "net volume (m³)", 6), ("330", "Bruttogewicht (kg)", "gross weight (kg)", 6),
        ("390", "Betrag (Landeswährung)", "amount (local currency)", 0), ("391", "Betrag mit Währung", "amount with currency", 0),
        ("392", "Preis (Landeswährung)", "price (local currency)", 0), ("393", "Preis mit Währung", "price with currency", 0),
    ];

    // identifiers whose data has a predefined length by their first two digits: no group separator after them (GS1 General
    // Specifications, 7.8.5, figure 7.8.5-2)
    private static readonly string[] PredefinedLength =
        ["00", "01", "02", "03", "04", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "31", "32", "33", "34", "35", "36", "41"];

    /// <summary>
    /// The element string of GS1 data written with identifiers in brackets, „(01)04012345678901(17)261231(10)A1“: the identifiers and
    /// their data one after the other, a group separator (U+001D) after data of variable length unless it ends the string. Known
    /// identifiers are checked for length and digits, GTIN/GLN/SSCC for their check digit (added when it is missing).
    /// </summary>
    /// <param name="text">The bracketed form.</param>
    /// <returns>The element string.</returns>
    /// <exception cref="ArgumentException">The text is not of that form, or an identifier's data does not fit it (message for the user).</exception>
    public static string FromBracketed(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || s[0] != '(') throw new ArgumentException(Tr.T("GS1-Daten beginnen mit einem Datenbezeichner in Klammern, z. B. (01)."));
        var result = new StringBuilder();
        var at = 0;
        while (at < s.Length)
        {
            var close = s.IndexOf(')', at);
            if (s[at] != '(' || close < 0) throw new ArgumentException(Tr.T("Klammer erwartet an Stelle {0}.", at + 1));
            var code = s[(at + 1)..close];
            if (code.Length is < 2 or > 4 || !code.All(char.IsAsciiDigit)) throw new ArgumentException(Tr.T("„({0})“ ist kein Datenbezeichner (2 bis 4 Ziffern).", code));
            var next = s.IndexOf('(', close + 1);
            var data = (next < 0 ? s[(close + 1)..] : s[(close + 1)..next]).Trim();
            if (data.Length == 0) throw new ArgumentException(Tr.T("Nach ({0}) fehlen die Daten.", code));
            if (data.Any(c => c < 0x20 || c > 0x7E)) throw new ArgumentException(Tr.T("Die Daten von ({0}) enthalten Zeichen, die GS1 nicht erlaubt.", code));
            if (Identifier(code, 0) is { } ai && ai.Code == code)
            {
                if (ai.Kind == 'g' && data.All(char.IsAsciiDigit) && data.Length == ai.Length - 1)
                    data += Linear.LinearCodes.Gs1CheckDigit(data);
                if (ai.Length > 0 && data.Length != ai.Length) throw new ArgumentException(Tr.T("({0}) verlangt genau {1} Zeichen.", code, ai.Length));
                if (ai.Length == 0 && data.Length > ai.Max) throw new ArgumentException(Tr.T("({0}) erlaubt höchstens {1} Zeichen.", code, ai.Max));
                if (ai.Kind is 'g' or 'd' or 'w' && !data.All(char.IsAsciiDigit)) throw new ArgumentException(Tr.T("({0}) erlaubt nur Ziffern.", code));
                if (ai.Kind == 'g' && Linear.LinearCodes.Gs1CheckDigit(data.AsSpan(0, data.Length - 1)) != data[^1] - '0')
                    throw new ArgumentException(Tr.T("Die Prüfziffer von ({0}) stimmt nicht.", code));
            }
            result.Append(code).Append(data);
            at = next < 0 ? s.Length : next;
            if (at < s.Length && !PredefinedLength.Contains(code[..2])) result.Append('\u001D');
        }
        return result.ToString();
    }

    /// <summary>The bracketed form of an element string (for display and editing); the string itself if it cannot be split.</summary>
    /// <param name="elements">The element string, group separators as U+001D.</param>
    /// <returns>„(01)…(10)…“.</returns>
    public static string ToBracketed(string elements)
    {
        var result = new StringBuilder();
        var at = 0;
        while (at < elements.Length)
        {
            if (Identifier(elements, at) is not { } ai) return elements.Replace('\u001D', ' ');
            at += ai.Code.Length;
            var end = ai.Length > 0 ? Math.Min(elements.Length, at + ai.Length) : elements.IndexOf('\u001D', at) is var gs && gs >= 0 ? gs : elements.Length;
            result.Append('(').Append(ai.Code).Append(')').Append(elements, at, end - at);
            at = end < elements.Length && elements[end] == '\u001D' ? end + 1 : end;
        }
        return result.ToString();
    }

    /// <summary>The identifier at position <paramref name="at"/>; null if none is known there.</summary>
    public static Gs1Ai? Identifier(string text, int at)
    {
        foreach (var length in new[] { 4, 3, 2 })
        {
            if (at + length > text.Length) continue;
            var code = text.Substring(at, length);
            if (!code.All(char.IsAsciiDigit)) continue;
            if (length == 4)
                foreach (var m in Measures)
                    if (code.StartsWith(m.Prefix, StringComparison.Ordinal))
                        return new Gs1Ai(code, m.Length, m.Length == 0 ? 15 : m.Length, m.Length == 0 ? 'w' : 'w', m.De, m.En);
            if (Table.FirstOrDefault(a => a.Code == code) is { } ai) return ai;
        }
        return null;
    }

    /// <summary>The data for display: dates, decimals, check digits.</summary>
    public static string Format(Gs1Ai ai, string data, List<string> warnings)
    {
        switch (ai.Kind)
        {
            case 'd' when data.Length == 6 && data.All(char.IsAsciiDigit):
            {
                int year = 2000 + int.Parse(data[..2], CultureInfo.InvariantCulture), month = int.Parse(data[2..4], CultureInfo.InvariantCulture);
                var day = int.Parse(data[4..], CultureInfo.InvariantCulture);
                // the century: years more than 50 ahead belong to the last one (GS1 General Specifications, 7.12)
                if (year - DateTime.Today.Year > 50) year -= 100;
                if (month is < 1 or > 12) return data;
                if (day == 0) return new DateTime(year, month, 1).ToString("MMMM yyyy", Tr.Culture) + " " + Tr.T("(Monatsende)");
                return day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day).ToString("d", Tr.Culture) : data;
            }
            case 'g' when data.All(char.IsAsciiDigit) && data.Length > 1:
                if (Linear.LinearCodes.Gs1CheckDigit(data.AsSpan(0, data.Length - 1)) != data[^1] - '0')
                    warnings.Add(Tr.T("Die Prüfziffer von ({0}) stimmt nicht.", ai.Code));
                return data;
            case 'w' when data.All(char.IsAsciiDigit) && data.Length > 0:
            {
                var decimals = ai.Code[^1] - '0';
                var digits = data;
                var currency = "";
                if (ai.Code.StartsWith("391", StringComparison.Ordinal) || ai.Code.StartsWith("393", StringComparison.Ordinal))
                {
                    if (digits.Length <= 3) return data;
                    currency = " (ISO 4217: " + digits[..3] + ")";
                    digits = digits[3..];
                }
                var value = decimal.Parse(digits, CultureInfo.InvariantCulture) / (decimal)Math.Pow(10, Math.Min(decimals, 9));
                return value.ToString("0.#########", Tr.Culture) + currency;
            }
            default:
                return data;
        }
    }
}
