using System.Globalization;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>Document information from the trailer's /Info dictionary (ISO 32000-2, 14.3.3).</summary>
/// <remarks>Only /Info is read; XMP metadata is not included. Empty or missing text entries are null,
/// all texts are trimmed.</remarks>
/// <param name="Title">The /Title entry.</param>
/// <param name="Author">The /Author entry.</param>
/// <param name="Subject">The /Subject entry.</param>
/// <param name="Keywords">The /Keywords entry, as one unsplit text.</param>
/// <param name="Creator">The /Creator entry (the application that created the original document).</param>
/// <param name="Producer">The /Producer entry (the application that produced the PDF).</param>
/// <param name="Created">The /CreationDate entry, parsed with <see cref="PdfDate.Parse"/>.</param>
/// <param name="Modified">The /ModDate entry, parsed with <see cref="PdfDate.Parse"/>.</param>
public sealed record PdfInfo(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified)
{
    /// <summary>Information with every entry null; used when the document has no /Info dictionary.</summary>
    public static readonly PdfInfo Empty = new(null, null, null, null, null, null, null, null);

    internal static PdfInfo From(PdfDictionary? info)
    {
        if (info is null) return Empty;
        string? Text(string key) => info.GetString(PdfName.Get(key))?.ToText() is { Length: > 0 } t ? t.Trim() : null;
        return new PdfInfo(Text("Title"), Text("Author"), Text("Subject"), Text("Keywords"), Text("Creator"), Text("Producer"),
            PdfDate.Parse(Text("CreationDate")), PdfDate.Parse(Text("ModDate")));
    }
}

/// <summary>Parser for PDF date strings of the form "D:YYYYMMDDHHmmSSOHH'mm'" (ISO 32000-2, 7.9.4).</summary>
public static class PdfDate
{
    /// <summary>
    /// Writes a date as ISO 32000-2, 7.9.4 asks: D:YYYYMMDDHHmmSS+HH'mm — up to PDF 1.7 with an apostrophe at the end (the form of
    /// ISO 32000-1), from PDF 2.0 without (readers take both). The one place dates are formatted (docs/FEHLERGEDAECHTNIS.md, No. 487).
    /// </summary>
    /// <param name="time">The date.</param>
    /// <param name="pdf20">The file is PDF 2.0 or later.</param>
    /// <returns>The date string.</returns>
    public static string Format(DateTimeOffset time, bool pdf20 = false)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{time:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}{(pdf20 ? "" : "'")}");
    }

    /// <summary>Parses a PDF date string leniently.</summary>
    /// <param name="text">The date text; the "D:" prefix is optional.</param>
    /// <returns>The date, or null if the text is empty, has fewer than four leading digits (the year) or
    /// cannot be represented.</returns>
    /// <remarks>Missing fields fall back to January, day 1, 00:00:00; out-of-range fields are clamped
    /// (month 1–12, day to the length of the month, hour 0–23, minute and second 0–59). Apostrophes in
    /// the offset are ignored; without a "+" or "-" offset (for example "Z") the offset is zero.</remarks>
    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];

        var digits = 0;
        while (digits < s.Length && digits < 14 && char.IsAsciiDigit(s[digits])) digits++;
        if (digits < 4) return null;

        int Part(int start, int length, int fallback) =>
            start + length <= digits ? int.Parse(s.AsSpan(start, length), CultureInfo.InvariantCulture) : fallback;

        var year = Part(0, 4, 1);
        // year 0000 („D:00000000000000“, a placeholder some producers write) is no date: clamped, it became the first of January of year 1,
        // which the PDF/A copy then wrote into the information dictionary and XMP (docs/FEHLERGEDAECHTNIS.md, No. 508)
        if (year == 0) return null;
        var month = Math.Clamp(Part(4, 2, 1), 1, 12);
        var day = Math.Clamp(Part(6, 2, 1), 1, DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month));
        var hour = Math.Clamp(Part(8, 2, 0), 0, 23);
        var minute = Math.Clamp(Part(10, 2, 0), 0, 59);
        var second = Math.Clamp(Part(12, 2, 0), 0, 59);

        var offset = TimeSpan.Zero;
        var rest = s[digits..].Replace("'", "");
        if (rest.Length >= 3 && rest[0] is '+' or '-' && int.TryParse(rest.AsSpan(1, 2), CultureInfo.InvariantCulture, out var oh))
        {
            var om = rest.Length >= 5 && int.TryParse(rest.AsSpan(3, 2), CultureInfo.InvariantCulture, out var m) ? m : 0;
            // Capped at 14 hours: DateTimeOffset throws for larger offsets such as "+14'59'"
            offset = TimeSpan.FromMinutes(Math.Min(Math.Clamp(oh, 0, 14) * 60 + Math.Clamp(om, 0, 59), 14 * 60));
            if (rest[0] == '-') offset = -offset;
        }

        // the moment in UTC must lie within years 1 to 9999 as well: the first of January of year 1 with a positive offset does not (checked
        // here — it threw, and every damaged date cost an exception; docs/FEHLERGEDAECHTNIS.md, No. 394)
        var local = new DateTime(Math.Clamp(year, 1, 9999), month, day, hour, minute, second);
        var utc = local.Ticks - offset.Ticks;
        if (utc < DateTime.MinValue.Ticks || utc > DateTime.MaxValue.Ticks) return null;
        return new DateTimeOffset(local, offset);
    }
}
