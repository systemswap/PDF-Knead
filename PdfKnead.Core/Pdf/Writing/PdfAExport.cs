using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>A PDF/A copy as written and checked.</summary>
/// <param name="Bytes">The written file.</param>
/// <param name="Violations">The rules it does not meet; empty if it is a PDF/A file.</param>
public sealed record PdfAResult(byte[] Bytes, IReadOnlyList<PdfAViolation> Violations)
{
    /// <summary>The fonts embedded as the substitute PDF-Knead draws them with (<see cref="PdfSaveResult.SubstitutedFonts"/>).</summary>
    public IReadOnlyList<string> SubstitutedFonts { get; init; } = [];

    /// <summary>The embedded fonts left as they were because their licence forbids changing them (<see cref="PdfSaveResult.LicenceKeptFonts"/>).</summary>
    public IReadOnlyList<string> LicenceKeptFonts { get; init; } = [];

    /// <summary>PDF/A-1: the pages (index) whose translucent areas are images (<see cref="PdfAFlattening"/>).</summary>
    public IReadOnlyList<int> FlattenedPages { get; init; } = [];

    /// <summary>The PDF/UA identification or accessibility declarations of the source were left out: something broke the accessibility.</summary>
    public bool AccessibilityClaimRemoved { get; init; }

    /// <summary>What broke it (<see cref="PdfSaveResult.AccessibilityBreaks"/>).</summary>
    public IReadOnlyList<string> AccessibilityBreaks { get; init; } = [];

    /// <summary>Declarations of the source a PDF/A-2/3 copy leaves out (<see cref="PdfSaveResult.DeclarationsLeftOut"/>).</summary>
    public IReadOnlyList<string> DeclarationsLeftOut { get; init; } = [];

    /// <summary>The file meets every rule checked (<see cref="PdfACheck"/>); only then is it saved as PDF/A.</summary>
    public bool Conforms => Violations.Count == 0;
}

/// <summary>
/// Saving as PDF/A (KONZEPT.md, section 5.21): the pages are written for PDF/A (<see cref="PdfSaveOptions.PdfA"/>) and the result is read back
/// and checked like by a validator. A file that does not conform is not to be saved as PDF/A; the violations say why.
/// </summary>
public static class PdfAExport
{
    /// <summary>Writes and checks a PDF/A copy in memory.</summary>
    /// <param name="main">The main document.</param>
    /// <param name="pages">The pages, as for <see cref="PdfWriter.Write(PdfDocument, IReadOnlyList{PageItem}, Stream, PdfSaveOptions?, CancellationToken)"/>.</param>
    /// <param name="part">The part and level.</param>
    /// <param name="options">Further options (compression, flattening …); encryption and version are set for PDF/A.</param>
    /// <param name="cancel">Cancels writing and checking.</param>
    /// <param name="accessibility">Level a: the language and the answers for the figures (<see cref="PdfATagging"/>).</param>
    /// <returns>The file and what it does not meet.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="part"/> is <see cref="PdfAConformance.None"/>.</exception>
    public static PdfAResult Write(PdfDocument main, IReadOnlyList<PageItem> pages, PdfAConformance part, PdfSaveOptions? options = null,
        CancellationToken cancel = default, PdfAAccessibility? accessibility = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(part, PdfAConformance.None);
        using var buffer = new MemoryStream();
        var saved = PdfWriter.Write(main, pages, buffer, (options ?? new PdfSaveOptions()) with { PdfA = part, PdfAAccessibility = accessibility }, cancel);
        var bytes = buffer.ToArray();
        using var written = PdfDocument.Open(bytes);
        IReadOnlyList<PdfAViolation> violations = [.. Unanswered(pages, part, accessibility, cancel), .. PdfACheck.Check(written, cancel)];
        var result = new PdfAResult(bytes, violations)
        {
            SubstitutedFonts = saved.SubstitutedFonts, AccessibilityClaimRemoved = saved.AccessibilityClaimRemoved, AccessibilityBreaks = saved.AccessibilityBreaks,
            DeclarationsLeftOut = saved.DeclarationsLeftOut, LicenceKeptFonts = saved.LicenceKeptFonts,
        };
        // PDF/A-1 allows no transparency: the copy, which looks as the original is shown, is written once more with what was translucent
        // as images over its pages (PdfAFlattening)
        if (part.Part() != 1 || PdfAFlattening.Rasterizer is not { } rasterizer || !violations.Any(v => Transparency.Contains(v.Id))) return result;
        // (nothing translucent, only groups: written again without them, which draws the same)
        var plan = PdfAFlattening.For(written, rasterizer, cancel) ?? PdfAFlattening.Nothing;
        using var flatBuffer = new MemoryStream();
        var flatSaved = PdfWriter.Write(written, PageItem.From(written), flatBuffer, new PdfSaveOptions
        {
            PdfA = part,
            ImageCodec = options?.ImageCodec,
            Overlays = plan.Overlays,
            PdfAFlattened = true,
            PdfABlankAnnotations = plan.Annotations,
        }, cancel);
        var flat = flatBuffer.ToArray();
        using var flatDoc = PdfDocument.Open(flat);
        return new PdfAResult(flat, [.. Unanswered(pages, part, accessibility, cancel), .. PdfACheck.Check(flatDoc, cancel)])
        {
            SubstitutedFonts = saved.SubstitutedFonts,
            LicenceKeptFonts = [.. saved.LicenceKeptFonts.Union(flatSaved.LicenceKeptFonts).Order(StringComparer.Ordinal)],
            FlattenedPages = [.. plan.Overlays.Keys.Order()],
            // the second write reads the first copy: what it removes adds to what the first left out
            AccessibilityClaimRemoved = saved.AccessibilityClaimRemoved || flatSaved.AccessibilityClaimRemoved,
            AccessibilityBreaks = [.. saved.AccessibilityBreaks.Union(flatSaved.AccessibilityBreaks)],
            DeclarationsLeftOut = saved.DeclarationsLeftOut,
        };
    }

    /// <summary>
    /// The figures a copy of level a needs and has no answer for (<see cref="PdfAAccessibility"/>): an alternative text or „only decoration“ —
    /// the logical structure has to say what a figure shows (ISO 19005-2, 6.7; ISO 19005-1, 6.8).
    /// </summary>
    /// <param name="pages">The pages as they are written.</param>
    /// <param name="part">The part and level.</param>
    /// <param name="accessibility">The answers given.</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>One finding per figure without an answer.</returns>
    private static IEnumerable<PdfAViolation> Unanswered(IReadOnlyList<PageItem> pages, PdfAConformance part, PdfAAccessibility? accessibility,
        CancellationToken cancel)
    {
        if (!part.Accessible()) return [];
        accessibility ??= new PdfAAccessibility();
        return PdfATagging.Figures([.. pages.Select(p => (p.Source, p.Page))], cancel)
            .Where(f => !accessibility.Answered(f.Key, out _))
            .Select(f => new PdfAViolation(part.Part() == 1 ? "6.8" : "6.7", Tr.T("Ein Bild braucht einen Alternativtext oder die Angabe „nur Schmuck“."),
                f.Page >= 0 ? f.Page + 1 : null))
            .ToList();
    }

    /// <summary>The rules of PDF/A-1 on transparency (6.4, 6.5.3): a copy that breaks one of them is flattened.</summary>
    private static readonly HashSet<string> Transparency = new(new[]
    {
        PdfARules.NoSoftMask, PdfARules.NoImageSoftMask, PdfARules.NoTransparencyGroup, PdfARules.ConstantAlpha, PdfARules.BlendModes, PdfARules.AnnotationOpacity,
    }.Select(r => r.One).OfType<string>(), StringComparer.Ordinal);
}
