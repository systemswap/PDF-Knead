namespace PdfKnead.Core.Pdf.Writing;

/// <summary>The PDF/A part and level a file is written for (KONZEPT.md, section 5.21).</summary>
public enum PdfAConformance
{
    /// <summary>An ordinary PDF.</summary>
    None,

    /// <summary>PDF/A-2b (ISO 19005-2, level b): the appearance is preserved; no embedded files.</summary>
    A2b,

    /// <summary>PDF/A-3b (ISO 19005-3, level b): as PDF/A-2b, with embedded files of any kind (each with its relationship to the document).</summary>
    A3b,

    /// <summary>PDF/A-2u (ISO 19005-2, level u): as PDF/A-2b, and every character shown maps to Unicode (the text can be searched and copied).</summary>
    A2u,

    /// <summary>PDF/A-3u (ISO 19005-3, level u): as PDF/A-3b, and every character shown maps to Unicode.</summary>
    A3u,

    /// <summary>PDF/A-4 (ISO 19005-4:2020): PDF 2.0 as its base, no information dictionary, embedded files only if they are PDF/A themselves.</summary>
    A4,

    /// <summary>PDF/A-4f (ISO 19005-4:2020, Annex A): as PDF/A-4, with embedded files of any kind — at least one.</summary>
    A4f,

    /// <summary>
    /// PDF/A-1b (ISO 19005-1:2005, level b): PDF 1.4 as its base — no transparency, no layers, no embedded files, no object streams, ICC profiles
    /// of version 2; the appearance is preserved.
    /// </summary>
    A1b,

    /// <summary>
    /// PDF/A-4e (ISO 19005-4:2020, Annex B): as PDF/A-4 for engineering documents — 3D annotations (data as U3D or PRC) and rich media
    /// annotations, the actions of layers and 3D views, embedded files of any kind (none required).
    /// </summary>
    A4e,

    /// <summary>PDF/A-2a (ISO 19005-2, level a): as PDF/A-2u, and the document is tagged — a structure tree in reading order, the language,
    /// alternative texts for figures.</summary>
    A2a,

    /// <summary>PDF/A-3a (ISO 19005-3, level a): as PDF/A-3u, and tagged as PDF/A-2a.</summary>
    A3a,

    /// <summary>PDF/A-1a (ISO 19005-1:2005, level a): as PDF/A-1b, every character maps to Unicode, and tagged as PDF/A-2a.</summary>
    A1a,
}

/// <summary>
/// The limits of a part (ISO 19005-1, 6.1.12, 6.2.2, 6.2.3.2; ISO 19005-2, 6.1.13, 6.2.3, 6.2.4.2): the largest real, the longest string in
/// bytes, the most colourants of a DeviceN space, the highest major version of an ICC profile.
/// </summary>
internal readonly record struct PdfALimits(double Real, int String, int Colourants, int IccMajor);

/// <summary>What a PDF/A part and level asks for.</summary>
public static class PdfAParts
{
    /// <summary>The limits of the part: PDF/A-1 those of PDF 1.4 (reals ±32 767, strings 65 535 bytes, 8 colourants, ICC version 2), the
    /// later parts those of PDF 1.7.</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>The limits.</returns>
    internal static PdfALimits Limits(this PdfAConformance conformance) =>
        conformance.Part() == 1 ? new PdfALimits(32767, 65535, 8, 2) : new PdfALimits(3.403e38, 32767, 32, 4);

    /// <summary>The part of ISO 19005 (1, 2, 3 or 4); 0 for <see cref="PdfAConformance.None"/>.</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>The part.</returns>
    public static int Part(this PdfAConformance conformance) => conformance switch
    {
        PdfAConformance.A1b or PdfAConformance.A1a => 1,
        PdfAConformance.A2b or PdfAConformance.A2u or PdfAConformance.A2a => 2,
        PdfAConformance.A3b or PdfAConformance.A3u or PdfAConformance.A3a => 3,
        PdfAConformance.A4 or PdfAConformance.A4f or PdfAConformance.A4e => 4,
        _ => 0,
    };

    /// <summary>The level as pdfaid:conformance names it ("A", "B", "U", "F", "E"); null for PDF/A-4, which names none.</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>The level, or null.</returns>
    public static string? Level(this PdfAConformance conformance) => conformance switch
    {
        PdfAConformance.A1b or PdfAConformance.A2b or PdfAConformance.A3b => "B",
        PdfAConformance.A2u or PdfAConformance.A3u => "U",
        PdfAConformance.A4f => "F",
        PdfAConformance.A4e => "E",
        PdfAConformance.A1a or PdfAConformance.A2a or PdfAConformance.A3a => "A",
        _ => null,
    };

    /// <summary>Embedded files of any kind are kept (PDF/A-3, PDF/A-4f, PDF/A-4e); the other parts allow only PDF/A files, which cannot be told
    /// apart reliably.</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>Whether attachments stay.</returns>
    public static bool KeepsAttachments(this PdfAConformance conformance) =>
        conformance is PdfAConformance.A3b or PdfAConformance.A3u or PdfAConformance.A3a or PdfAConformance.A4f or PdfAConformance.A4e;

    /// <summary>Every character shown must map to Unicode (levels u and a).</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>Whether level u (or a) is asked for.</returns>
    public static bool NeedsUnicode(this PdfAConformance conformance) =>
        conformance is PdfAConformance.A2u or PdfAConformance.A3u || conformance.Accessible();

    /// <summary>The document must be tagged (level a): structure tree, language, alternative texts.</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>Whether level a is asked for.</returns>
    public static bool Accessible(this PdfAConformance conformance) => conformance is PdfAConformance.A1a or PdfAConformance.A2a or PdfAConformance.A3a;

    /// <summary>The part and level from its short name: "1a", "1b", "2a", "2b", "3a", "3b", "2u", "3u", "4", "4f" or "4e" (also with "PDF/A-"
    /// before it, any case).</summary>
    /// <param name="name">The short name.</param>
    /// <returns>The part and level; null for an unknown name.</returns>
    public static PdfAConformance? FromName(string? name) =>
        (name?.Trim().ToLowerInvariant() is { } n && n.StartsWith("pdf/a-", StringComparison.Ordinal) ? n[6..] : name?.Trim().ToLowerInvariant()) switch
        {
            "1b" => PdfAConformance.A1b,
            "1a" => PdfAConformance.A1a,
            "2a" => PdfAConformance.A2a,
            "3a" => PdfAConformance.A3a,
            "2b" => PdfAConformance.A2b,
            "3b" => PdfAConformance.A3b,
            "2u" => PdfAConformance.A2u,
            "3u" => PdfAConformance.A3u,
            "4" => PdfAConformance.A4,
            "4f" => PdfAConformance.A4f,
            "4e" => PdfAConformance.A4e,
            _ => null,
        };

    /// <summary>The name as the user sees it, such as „PDF/A-2b“ or „PDF/A-4f“.</summary>
    /// <param name="conformance">The part and level.</param>
    /// <returns>The name.</returns>
    public static string Name(this PdfAConformance conformance) =>
        $"PDF/A-{conformance.Part()}{conformance.Level()?.ToLowerInvariant()}";
}

/// <summary>A rule of PDF/A that a file does not meet.</summary>
/// <param name="Rule">The clause of the part checked (ISO 19005-1 for part 1, ISO 19005-2 for parts 2 and 3, ISO 19005-4 for part 4), such as "6.2.11.4.1".</param>
/// <param name="Text">What is wrong, in the language of the user interface.</param>
/// <param name="Page">The page (from 1) where it was found; null for the document as a whole.</param>
public sealed record PdfAViolation(string Rule, string Text, int? Page = null)
{
    /// <summary>Clause and test number in the rule list (such as "6.2.11.4.1-2"); the same as <see cref="Rule"/> where none is known.</summary>
    public string Id { get; init; } = Rule;
}
