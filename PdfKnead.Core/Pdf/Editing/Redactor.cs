using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Result of redacting a page.</summary>
/// <param name="Page">New page dictionary: new content and new resources in memory, otherwise the old entries.</param>
/// <param name="Resources">The new resources (also in <paramref name="Page"/>).</param>
/// <param name="DroppedAnnotations">Annotations and fields under the areas, which are dropped.</param>
/// <param name="RemovedText">Removed text (to check whether it still occurs elsewhere).</param>
/// <param name="RemovedGlyphs">Number of characters removed.</param>
/// <param name="RemovedPaths">Number of paths removed.</param>
/// <param name="BlankedImages">Number of images whose pixels under the areas were blacked out.</param>
public sealed record RedactionResult(PdfDictionary Page, PdfDictionary Resources, IReadOnlySet<PdfDictionary> DroppedAnnotations, string RemovedText,
    int RemovedGlyphs, int RemovedPaths, int BlankedImages)
{
    /// <summary>Replaced or dropped originals (object numbers): kept only if something still draws them.</summary>
    public IReadOnlySet<int> ReplacedObjects { get; init; } = new HashSet<int>();

    /// <summary>What the check of the file as written needs for this page (<see cref="Redactor.CheckWritten"/>).</summary>
    internal Redactor.WrittenCheck? Check { get; init; }

    /// <summary>Fonts (object numbers) that lost characters: the writer scrubs what nothing shows any more out of them (<see cref="Writing.FontScrub"/>).</summary>
    public IReadOnlySet<int> TouchedFonts { get; init; } = new HashSet<int>();

    /// <summary>
    /// Characters that touch an area but stay (too little of them under it) and may be seen nowhere else — clipped beside the areas, or
    /// with something painted over them later; 0 if every such character shows beside the areas. If one of them showed only where the
    /// bar now lies, nobody sees it any more: the writer then removes such characters with <see cref="Redactor.RemoveHiddenIn"/> (grazing).
    /// </summary>
    public int GrazedGlyphs { get; init; }

    /// <summary>
    /// Where the bars grew to hide the length of the redacted words (page space; also painted black, and nothing that stays lies under
    /// them): further passes over the page must count them as bars.
    /// </summary>
    public IReadOnlyList<PdfRectangle> BarExtensions { get; init; } = [];

    /// <summary>Gaps whose length could only partly be hidden (not enough room in their line).</summary>
    public int LengthsPartlyShown { get; init; }
}

/// <summary>
/// Applies redactions: whatever lies under the areas is removed from the page content, not merely covered — characters (from
/// the text operators; the remaining ones keep their position, and the gap is coarsened to a grid of
/// <see cref="PositionGrid"/> points so that its width does not reveal the word), image pixels (blacked out in the image
/// itself, which is rebuilt for this page only), paths (subpaths entirely under an area removed, the others painted only outside), shadings
/// (clipped out), contents of form XObjects (a separate copy per invocation), alternate texts (/ActualText) and annotations.
/// Then black bars are drawn and a check runs: no character may remain under an area.
/// </summary>
public static partial class Redactor
{
    /// <summary>Grid for the gap of removed characters in points (the remaining characters move by at most half of it).</summary>
    public const double PositionGrid = 0.5;

    /// <summary>
    /// Subpaths whose bounds lie under the areas by at least this fraction are removed (a glyph converted to curves); the others stay and
    /// are painted only outside the areas — a background box a bar covered half of went entirely, also where it showed beside the bar
    /// (docs/FEHLERGEDAECHTNIS.md, No. 150).
    /// </summary>
    private const double RemoveCoverage = 0.9;

    /// <summary>
    /// What of a subpath removed whole may lie beside the areas (pt²): the rest of a glyph converted to curves, a dot of dirt at the edge of a
    /// bar — not a visible part of a frame or a box.
    /// </summary>
    private const double DroppedRest = 25;

    /// <summary>
    /// How far what of a subpath removed whole may reach beside the areas (pt, its longest side): a line has almost no area, and 10 pt of a
    /// thin rule beside a bar counted as 1 pt² — the visible piece went with the rest (docs/FEHLERGEDAECHTNIS.md, No. 290).
    /// </summary>
    private const double DroppedRestExtent = 3;

    /// <summary>
    /// Whether a subpath under the areas goes whole instead of being cut at them: with at least <see cref="RemoveCoverage"/> of its bounds
    /// under them, at most <see cref="DroppedRest"/> beside them and that rest no longer than <see cref="DroppedRestExtent"/> (a glyph converted
    /// to curves, a short stroke under a bar). What shows beside a bar decides, not the kind: a frame 96 % of whose box lay under a bar lost its
    /// side beside it (370 pt²), and of a large shape the tenth beside a bar is plainly to be seen (docs/FEHLERGEDAECHTNIS.md, No. 261); cut at
    /// the bar instead, a small stroke kept points under it.
    /// </summary>
    private static bool DroppedWhole(List<RectD> rects, RectD bounds, double covered) =>
        covered >= RemoveCoverage && (1 - covered) * bounds.Width * bounds.Height <= DroppedRest && RestExtent(rects, bounds) <= DroppedRestExtent;

    /// <summary>
    /// The longest side of the box around the part of <paramref name="bounds"/> beside the areas, sampled on a grid of at most 32 × 32 cells
    /// (a thin side as one cell); 0 if all of it lies under them.
    /// </summary>
    private static double RestExtent(List<RectD> rects, RectD bounds)
    {
        int nx = bounds.Width < 0.5 ? 1 : 32, ny = bounds.Height < 0.5 ? 1 : 32;
        double cw = bounds.Width / nx, ch = bounds.Height / ny;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                double x = bounds.Left + (i + 0.5) * cw, y = bounds.Top + (j + 0.5) * ch;
                if (rects.Any(r => x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)) continue;
                left = Math.Min(left, x - cw / 2);
                right = Math.Max(right, x + cw / 2);
                top = Math.Min(top, y - ch / 2);
                bottom = Math.Max(bottom, y + ch / 2);
            }
        return left > right ? 0 : Math.Max(right - left, bottom - top);
    }

    /// <summary>Redacts the areas of a page.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="areas">The areas in page space; empty ones are ignored.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="moved">Annotations of other programs moved as elements, with their new position.</param>
    /// <param name="paper">The areas become white instead of black (a code replaced by a new one, KONZEPT.md, section 5.19): everything under
    /// them is removed all the same — image pixels are blacked out in the image and covered by the white.</param>
    /// <param name="hideLengths">Hide the length of redacted words (docs/RECHERCHE-VERSTECKEN.md, B-13): their gap and bar grow to the next
    /// step, the line makes room (Redactor.Widen.cs).</param>
    /// <returns>The redacted page.</returns>
    /// <exception cref="PdfException">The check found a character still under an area or under a grown bar; nothing must be saved then.</exception>
    public static RedactionResult Apply(PdfDocument doc, PdfPage page, IReadOnlyList<PdfRectangle> areas, CancellationToken cancel = default,
        IReadOnlyDictionary<PdfDictionary, PdfRectangle>? moved = null, bool paper = false, bool hideLengths = false)
    {
        var rects = areas.Where(a => !a.IsEmpty).Select(a => RectD.FromLtrb(a.Left, a.Bottom, a.Right, a.Top)).ToList();
        hideLengths &= !paper;
        // Hiding the lengths moves characters that stay: afterwards the page is checked against the page before (Redactor.MoveCheck.cs) —
        // a line on which a character went missing, moved out of its order or its clip or under a bar is frozen, and the page is written
        // again with that line as it was (a few rounds at most, then without hiding any length). Nothing that stays ever lies under a bar.
        // the characters before, always: what stays is checked after the last round, also without hiding lengths (No. 450)
        // the last check needs them on every page, beyond the limit for hiding lengths too (Analyse F3b-2, No. 460): a page with more than
        // that was saved unchecked; one beyond the larger limit is not redacted (damaged or made to tire the program). One snapshot for both
        var checkBefore = Snapshot(doc, page, MaxCheckSnapshot) ?? throw new PdfException(Tr.T("Seite {0} hat zu viele Zeichen, um die Schwärzung sicher zu prüfen — es wurde nichts gespeichert. Als Bild flachlegen und dann schwärzen hilft hier.",
            page.Index + 1));
        var before = checkBefore.Count <= MaxSnapshot ? checkBefore : null;
        hideLengths &= before is not null;   // too many characters to compare: the lengths stay as the rounded gaps leave them
        var frozen = new List<double>();
        for (var round = 0; ; round++)
        {
            var (result, pieces) = Rewrite(doc, page, rects, cancel, moved, paper, hideLengths && round < 4, frozen);
            if (before is not null && pieces.Count > 0 && MovesChecked(doc, page, before, result, rects, pieces) is { Count: > 0 } failed)
            {
                frozen.AddRange(failed);
                continue;
            }
            var pieceRects = pieces.Select(p => p.Rect).ToList();
            Verify(doc, page, result.Page, result.Resources, [.. rects, .. pieceRects], rects, [.. rects, .. pieceRects, .. Joined(rects, pieceRects)]);
            if (!StaysChecked(doc, page, checkBefore, result.Page, result.Resources, rects, hideLengths && pieces.Count > 0, cancel))
                throw new PdfException(Tr.T("Die Schwärzung ließ sich auf Seite {0} nicht vollständig anwenden — es wurde nichts gespeichert.", page.Index + 1));
            return result with { Check = new WrittenCheck(checkBefore, rects, pieceRects, hideLengths && pieces.Count > 0) };
        }
    }

    /// <summary>What the check of the written file needs for one redacted page: the characters before, the bars and the pieces they grew by.</summary>
    internal sealed class WrittenCheck(List<Seen> before, List<RectD> rects, List<RectD> pieces, bool moved)
    {
        internal List<Seen> Before { get; } = before;
        internal List<RectD> Rects { get; } = rects;
        internal List<RectD> Pieces { get; } = pieces;
        internal bool Moved { get; } = moved;
    }

    /// <summary>
    /// The second check, on the file as written (Analyse F3b-3; decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 461): the
    /// pages were checked before writing, but writing does more to them — fonts that lost characters are scrubbed, streams packed, objects
    /// renumbered. On every redacted page of the written file nothing may lie under a bar (<see cref="Verify"/>), and, where nothing else
    /// changes the content on saving, every character outside the bars is there as before (<see cref="StaysChecked(PdfDocument, PdfPage,
    /// List{Seen}, PdfDictionary, PdfDictionary, List{RectD}, bool)"/>).
    /// </summary>
    /// <param name="written">The written file.</param>
    /// <param name="checks">The redacted pages (index in the written file) and what to check there.</param>
    /// <param name="contentAsRedacted">Saving changes the content of the pages no further than redacting (no cleanup, no PDF/A).</param>
    /// <param name="cancel">Abort.</param>
    /// <exception cref="PdfException">A check failed: the file must not be handed out.</exception>
    internal static void CheckWritten(PdfDocument written, IReadOnlyList<(int Page, WrittenCheck Check)> checks, bool contentAsRedacted, CancellationToken cancel = default)
    {
        foreach (var (index, check) in checks)
        {
            if (index >= written.Pages.Count) throw new PdfException(Tr.T("Die Schwärzung ließ sich auf Seite {0} nicht vollständig anwenden — es wurde nichts gespeichert.", index + 1));
            var page = written.Pages[index];
            var resources = page.Resources ?? new PdfDictionary();
            Verify(written, page, page.Dictionary, resources, [.. check.Rects, .. check.Pieces], check.Rects,
                [.. check.Rects, .. check.Pieces, .. Joined(check.Rects, check.Pieces)]);
            if (contentAsRedacted && !StaysChecked(written, page, check.Before, page.Dictionary, resources, check.Rects, check.Moved, cancel))
                throw new PdfException(Tr.T("Die Schwärzung ließ sich auf Seite {0} nicht vollständig anwenden — es wurde nichts gespeichert.", index + 1));
        }
    }

    /// <summary>
    /// The bars as drawn: a piece a bar grew by (hiding the length of a word) joins its bar to one rectangle — drawn as two, the seam between
    /// them would stand exactly where the removed word ended and tell its width (docs/FEHLERGEDAECHTNIS.md, No. 354). A bar of a justified
    /// line set anew covers its old bar on both sides and takes its place (No. 356). Only a piece as high as its bar joins it: the piece of
    /// one line beside an area over several lines stays a piece of its own — joined, it widened the whole area, over what lies beside it on
    /// the other lines (No. 357); its edge there is the area's, not the end of a word.
    /// </summary>
    private static List<RectD> Joined(List<RectD> bars, List<RectD> pieces)
    {
        var result = new List<RectD>(bars);
        foreach (var piece in pieces)
        {
            var at = result.FindIndex(b => Math.Abs(piece.Top - b.Top) < 0.01 && Math.Abs(piece.Bottom - b.Bottom) < 0.01 &&
                                           (Math.Abs(b.Right - piece.Left) < 0.01 || piece.Left <= b.Left + 0.01 && piece.Right >= b.Right - 0.01));
            if (at >= 0) result[at] = RectD.FromLtrb(Math.Min(result[at].Left, piece.Left), result[at].Top, Math.Max(result[at].Right, piece.Right), result[at].Bottom);
            else result.Add(piece);
        }
        return result;
    }

    /// <summary>One pass of <see cref="Apply"/>: the rewritten page and the pieces its bars grew by (each with the line it belongs to).</summary>
    private static (RedactionResult Result, List<(RectD Rect, double Line)> Extensions) Rewrite(PdfDocument doc, PdfPage page, List<RectD> rects,
        CancellationToken cancel, IReadOnlyDictionary<PdfDictionary, PdfRectangle>? moved, bool paper, bool hideLengths, List<double> frozen)
    {
        var root = new Scope();
        var observer = new Observer(rects, root, RectD.FromLtrb(page.CropBox.Left, page.CropBox.Bottom, page.CropBox.Right, page.CropBox.Top), doc);
        PageInterpreter.Observe(doc, page, observer, cancel);

        var rewriter = new Rewriter(doc, rects, page.MediaBox, hideLengths: hideLengths, frozen: frozen);
        var resources = CopyResources(page.Resources);
        byte[] body;
        // everything the page no longer draws leaves its resources, not only what was replaced: an unused copy of a redacted image (another
        // name for the same picture, never invoked) would otherwise keep the unredacted original in the file — also when „Was niemand sieht“
        // is not chosen (docs/FEHLERGEDAECHTNIS.md, No. 350)
        using (var content = PageContent(doc, page.Dictionary)) body = rewriter.Rewrite(content.Span, root, resources, pruneUnused: true);
        var pieces = rewriter.BarExtensions.Where(e => e.Rect.Width > 0 && e.Rect.Height > 0).ToList();
        var extensions = pieces.Select(e => e.Rect).ToList();

        // Old content inside q … Q (it may leave the graphics state altered), then the bars; a Q that closes more than it opened goes first
        // — inside the q it would restore the initial state where readers pass over it (docs/FEHLERGEDAECHTNIS.md, No. 383)
        body = StateBalance.WithoutStrayRestores([body])[0] ?? body;
        var (open, close) = StateBalance.Of([body]);
        var output = new MemoryStream(body.Length + 256);
        for (var i = 0; i < open; i++) output.Write("q\n"u8);
        output.Write(body);
        output.WriteByte((byte)'\n');
        // In a tagged file the bars are an artifact (ISO 32000-2, 14.8.2.2; ISO 14289-2, 8.2.2 and 8.9.2.4.17: subtype Redaction; ISO 14289-1,
        // 7.1): marked content the old content leaves open is closed first — the artifact would otherwise stand inside tagged content. The
        // same form whatever version is written (in 1.7 an unknown subtype, harmless). Untagged files: as before, byte for byte
        // (docs/ABLAUFPLAN.md, 6.3 step 1; docs/FEHLERGEDAECHTNIS.md, No. 617).
        var tagged = StructureRoles.IsTagged(doc.Catalog);
        if (tagged)
            for (var i = StructureRoles.OpenMarks(body); i > 0; i--) output.Write("EMC\n"u8);
        for (var i = 0; i < close; i++) output.Write("Q\n"u8);
        if (tagged) output.Write(BarArtifact);
        output.Write(paper ? "q 1 g\n"u8 : "q 0 g\n"u8);
        foreach (var r in Joined(rects, extensions))
            output.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"{Num(r.Left)} {Num(r.Top)} {Num(r.Width)} {Num(r.Height)} re\n")));
        output.Write("f\nQ\n"u8);
        if (tagged) output.Write("EMC\n"u8);

        // every description of the redacted page goes (No. 134, 624), also in what no bar reaches and no observer looks into: the cells of
        // tiling patterns (painted over several tiles they are never looked into), patterns in forms that describe nothing themselves, the
        // glyphs of Type 3 fonts, and what lies in them (docs/FEHLERGEDAECHTNIS.md, No. 696 and 697; how: DescriptionPass)
        resources = new DescriptionPass(doc, rewriter.ReplacedObjects, rewriter.Type3Without).Resources(resources, 0,
            DescriptionPass.Used.Of(doc, body, resources)) ?? resources;

        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), output.ToArray()));
        dict.Set(PdfName.Resources, resources);

        var dropped = DroppedAnnotations(page.Dictionary, page.Rotate, rects, moved);
        if (page.Dictionary.Get(AnnotsName) is PdfArray annots && dropped.Count > 0)
        {
            var kept = new PdfArray { Resolver = annots.Resolver };   // references must still resolve (elements of other programs)
            for (var i = 0; i < annots.Count; i++)
                if (!(annots.Get(i) is PdfDictionary annot && dropped.Contains(annot))) kept.Add(annots[i]);
            if (kept.Count > 0) dict.Set(AnnotsName, kept);
            else dict.Remove(AnnotsName);
        }

        var result = new RedactionResult(dict, resources, dropped, observer.Removed.ToString(), observer.Glyphs, observer.Paths, rewriter.BlankedImages)
        {
            ReplacedObjects = rewriter.ReplacedObjects,
            TouchedFonts = rewriter.TouchedFonts,
            GrazedGlyphs = observer.GrazedMayHide ? observer.Grazed : 0,
            BarExtensions = [.. extensions.Select(e => new PdfRectangle(e.Left, e.Top, e.Right, e.Bottom))],
            LengthsPartlyShown = rewriter.LengthsPartlyShown,
        };
        return (result, pieces);
    }

    private static readonly PdfName AnnotsName = PdfName.Get("Annots");

    /// <summary>The marked content around the bars of a tagged file: an artifact of pagination, subtype Redaction (ISO 14289-2, 8.9.2.4.17).</summary>
    internal static ReadOnlySpan<byte> BarArtifact => "/Artifact <</Type /Pagination /Subtype /Redaction>> BDC\n"u8;

    /// <summary>
    /// Annotations that touch an area where they draw (<see cref="Content.AnnotationAppearance.DrawnBounds"/>, at their new place if they
    /// were moved), plus their popups and replies (/IRT), which would otherwise still show the content.
    /// </summary>
    private static HashSet<PdfDictionary> DroppedAnnotations(PdfDictionary page, int rotate, List<RectD> rects, IReadOnlyDictionary<PdfDictionary, PdfRectangle>? moved)
    {
        var dropped = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (page.Get(AnnotsName) is not PdfArray annots) return dropped;
        for (var i = 0; i < annots.Count; i++)
            if (annots.Get(i) is PdfDictionary annot && Drawn(annot) is { } r && Hits(rects, RectD.FromLtrb(r.Left, r.Bottom, r.Right, r.Top)))
            {
                dropped.Add(annot);
                if (annot.Get(PdfName.Get("Popup")) is PdfDictionary popup) dropped.Add(popup);
            }
        if (dropped.Count == 0) return dropped;
        var irt = PdfName.Get("IRT");

        PdfRectangle? Drawn(PdfDictionary annot)
        {
            var drawn = Content.AnnotationAppearance.DrawnBounds(annot, rotate);
            if (moved is null || !moved.TryGetValue(annot, out var now) || drawn is not { } d || Content.AnnotationAppearance.RectOf(annot) is not { } was) return drawn;
            double dx = now.Left - was.Left, dy = now.Bottom - was.Bottom;
            return new PdfRectangle(d.Left + dx, d.Bottom + dy, d.Right + dx, d.Top + dy);
        }
        bool changed;
        var rounds = 0;
        do
        {
            changed = false;
            for (var i = 0; i < annots.Count; i++)
                if (annots.Get(i) is PdfDictionary a && !dropped.Contains(a) &&
                    (a.Get(PdfName.Parent) is PdfDictionary p && dropped.Contains(p) || a.Get(irt) is PdfDictionary reply && dropped.Contains(reply)))
                    changed |= dropped.Add(a);
        } while (changed && ++rounds < 16);
        return dropped;
    }

    /// <summary>
    /// Cross-check: process the new page once more — no character may lie mostly under an area (60 %, measured on its shape as
    /// when removing). The remaining characters of a line may have moved by up to half the grid (<see cref="PositionGrid"/>): a character
    /// counts as left under an area only if it lies there wherever within that it started — measured shifted by half the grid either way.
    /// Measuring it narrowed by half the grid on both sides instead made a small character that moved towards the bar look covered: of a comma
    /// beside a redacted word only the part next to the bar was left, and the page was refused (docs/FEHLERGEDAECHTNIS.md, No. 266).
    /// </summary>

    /// <summary>
    /// A character that carries nothing: its text is a space and it draws nothing (no outline, or invisible). Decided by the drawn shape as
    /// well as by the text — a glyph read as a space that draws a letter is checked like any other character, and a character without text
    /// (no ToUnicode) is checked too (docs/FEHLERGEDAECHTNIS.md, No. 438). Shared by the checks after redacting, the fuzz step and the CLI.
    /// </summary>
    /// <param name="text">The text of the character as the display reads it; null if it has none.</param>
    /// <param name="draws">It draws something (an outline or a Type 3 procedure; <see cref="IContentObserver.GlyphDraws"/>).</param>
    /// <param name="invisible">It is drawn invisibly (text rendering mode 3, the text layer of a scan).</param>
    /// <returns>True if nothing about it needs checking.</returns>
    internal static bool CarriesNothing(string? text, bool draws, bool invisible) => text is not null && string.IsNullOrWhiteSpace(text) && (!draws || invisible);

    /// <summary>Levels within which two samples of an image count as alike (a JPEG packed again blurs a little; docs/FEHLERGEDAECHTNIS.md, No. 431).</summary>
    internal const int ImageSameLevels = 48;

    /// <summary>
    /// Whether an image blacked out in its own samples reads alike under each area at its full size: every pixel whose middle lies in the
    /// area, its colour and its opacity within <see cref="ImageSameLevels"/> — the image and its mask (/SMask or /Mask stream) each at their
    /// own size, their rows converted as the display converts them. Reduced to a million pixels, a rest of two pixels under a bar of a large
    /// scan was averaged away, and a bar edge inside a block made a correctly blacked-out scan look unlike and sent it the lossy way of the
    /// display (docs/FEHLERGEDAECHTNIS.md, No. 439).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="blanked">The blacked-out image.</param>
    /// <param name="resources">Resources for a named colour space.</param>
    /// <param name="stencil">The image is a stencil mask.</param>
    /// <param name="imageToPage">Unit square of the image → page.</param>
    /// <param name="rects">The areas (page space).</param>
    /// <returns>False if a pixel under an area differs from the others there, or if the image cannot be read row by row.</returns>
    internal static bool UniformUnderAreas(PdfDocument doc, PdfStream blanked, PdfDictionary? resources, bool stencil, Matrix imageToPage,
        IReadOnlyList<RectD> rects) =>
        Uniformity(doc, blanked, resources, stencil, imageToPage, rects) == true;

    /// <summary>
    /// As <see cref="UniformUnderAreas"/>, telling apart an image that cannot be read row by row (null): the check of the command line falls
    /// back to the display's decoding there instead of reporting it (docs/FEHLERGEDAECHTNIS.md, No. 448).
    /// </summary>
    internal static bool? Uniformity(PdfDocument doc, PdfStream blanked, PdfDictionary? resources, bool stencil, Matrix imageToPage,
        IReadOnlyList<RectD> rects)
    {
        if (rects.Count == 0) return true;
        if (!imageToPage.IsInvertible) return null;
        var unreadable = false;
        var toImage = imageToPage.Invert();
        var upright = imageToPage.B == 0 && imageToPage.C == 0 || imageToPage.A == 0 && imageToPage.D == 0;
        var areas = rects.Select(r => toImage.TransformBounds(r)).ToArray();
        var alike = Uniform(blanked, stencil);
        // the mask under the areas too: opacity is part of what the bar must leave alike
        var dict = blanked.Dictionary;
        if (alike && !stencil && dict.Get(PdfName.Get("SMask")) is PdfStream soft) alike = Uniform(soft, stencilMask: false);
        else if (alike && !stencil && dict.Get(PdfName.Get("Mask")) is PdfStream hard) alike = Uniform(hard, stencilMask: true);
        return unreadable ? null : alike;

        bool Uniform(PdfStream image, bool stencilMask)
        {
            var w = (int)(image.Dictionary.GetNumber(PdfName.Get("Width")) ?? 0);
            var h = (int)(image.Dictionary.GetNumber(PdfName.Get("Height")) ?? 0);
            if (w <= 0 || h <= 0)
            {
                unreadable = true;
                return false;
            }
            // the rows the areas reach (their bounds in the unit square: v = 1 at the top row)
            int first = h, last = -1;
            foreach (var a in areas)
            {
                if (a.Right <= 0 || a.Left >= 1 || a.Bottom <= 0 || a.Top >= 1) continue;
                first = Math.Min(first, Math.Max(0, (int)Math.Floor((1 - a.Bottom) * h - 0.5)));
                last = Math.Max(last, Math.Min(h - 1, (int)Math.Ceiling((1 - a.Top) * h - 0.5)));
            }
            if (last < first) return true;
            var bpp = stencilMask ? 1 : 4;
            var low = new int[rects.Count * 4];
            var high = new int[rects.Count * 4];
            Array.Fill(low, 255);
            var size = PdfImageDecoder.ReadRows(image, resources, doc, stencilMask, first, last, (y, pixels) =>
            {
                var v = 1 - (y + 0.5) / h;
                for (var k = 0; k < rects.Count; k++)
                {
                    var a = areas[k];
                    if (v <= a.Top || v >= a.Bottom) continue;
                    var (from, to) = (Math.Max(0, (int)Math.Floor(a.Left * w - 0.5)), Math.Min(w - 1, (int)Math.Ceiling(a.Right * w - 0.5)));
                    for (var x = from; x <= to; x++)
                    {
                        var u = (x + 0.5) / w;
                        if (upright ? u <= a.Left || u >= a.Right
                            : imageToPage.Transform(u, v) is var (qx, qy) && (qx <= rects[k].Left || qx >= rects[k].Right || qy <= rects[k].Top || qy >= rects[k].Bottom))
                            continue;
                        for (var c = 0; c < bpp; c++)
                        {
                            var value = pixels[x * bpp + c];
                            if (value < low[k * 4 + c]) low[k * 4 + c] = value;
                            if (value > high[k * 4 + c]) high[k * 4 + c] = value;
                        }
                    }
                }
            });
            if (size is null)
            {
                unreadable = true;
                return false;
            }
            for (var i = 0; i < low.Length; i++)
                if (high[i] - low[i] > ImageSameLevels) return false;
            return true;
        }
    }

    /// <inheritdoc cref="Verify(PdfDocument, PdfPage, RedactionResult, List{RectD})"/>
    /// <param name="pathAreas">The areas no point of a path may lie under (the planned bars, not the pieces they grew by: under a piece lies
    /// visible content that is not redacted, such as an underline); null: paths are not checked.</param>
    /// <param name="edges">The edges a point of a path may lie on (bars, pieces and the bars as drawn); null: those of
    /// <paramref name="pathAreas"/>.</param>
    internal static void Verify(PdfDocument doc, PdfPage page, PdfDictionary newPage, PdfDictionary resources, List<RectD> rects,
        List<RectD>? pathAreas = null, List<RectD>? edges = null)
    {
        var check = new PdfPage(page.Index, newPage, null, resources, page.MediaBox, page.CropBox, page.Rotate);
        var observer = new VerifyObserver(rects, pathAreas, edges ?? pathAreas);
        PageInterpreter.Observe(doc, check, observer);
        if (observer.Failed)
            throw new PdfException(Tr.T("Die Schwärzung ließ sich auf Seite {0} nicht vollständig anwenden — es wurde nichts gespeichert.", page.Index + 1));
    }

    private sealed class VerifyObserver(List<RectD> rects, List<RectD>? pathAreas, List<RectD>? edges) : IContentObserver
    {
        public bool Failed { get; private set; }

        private readonly RectD _near = Around(rects);

        private readonly RectD? _nearPaths = pathAreas is { Count: > 0 } ? Around(pathAreas) : null;

        // no coordinate of a path under a bar (docs/FEHLERGEDAECHTNIS.md, No. 449): only paths near the bars
        public bool WantsPathGeometry(RectD bounds) => !Failed && _nearPaths is { } near && bounds.Inflate(0.05).Intersects(near);

        // a clip may also have its corners on the bars less the seam: the clip that cuts out the bars (No. 455), whose corner lies inside
        // another bar where two overlap
        private readonly List<RectD>? _clipEdges = edges is null ? null
            : [.. edges, .. edges.Where(e => e.Width > 2 * Rewriter.ClipSeam && e.Height > 2 * Rewriter.ClipSeam)
                .Select(e => RectD.FromLtrb(e.Left + Rewriter.ClipSeam, e.Top + Rewriter.ClipSeam, e.Right - Rewriter.ClipSeam, e.Bottom - Rewriter.ClipSeam))];

        public void PathGeometry(int op, ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, Matrix ctm, bool stroke, double lineWidth, bool clip)
        {
            if (PathPointUnder(verbs, points, ctm, stroke ? lineWidth : 0, pathAreas!, clip ? _clipEdges! : edges!) is not null) Failed = true;
        }

        // a shading ("sh") paints wherever its clip lets it: redacting clips the bars out of it, so that it reaches at most the seam under
        // them (No. 455)
        public bool WantsShadingClip(RectD bounds) => !Failed && pathAreas is { Count: > 0 } && Hits(pathAreas, bounds);

        public void ShadingClip(int op, RectD bounds, Func<double, double, bool> inside)
        {
            foreach (var a in pathAreas!)
            {
                var inner = RectD.FromLtrb(Math.Max(a.Left, bounds.Left) + Rewriter.ClipSeam + 0.05, Math.Max(a.Top, bounds.Top) + Rewriter.ClipSeam + 0.05,
                    Math.Min(a.Right, bounds.Right) - Rewriter.ClipSeam - 0.05, Math.Min(a.Bottom, bounds.Bottom) - Rewriter.ClipSeam - 0.05);
                if (inner.Width < 0 || inner.Height < 0) continue;
                // every quarter point, coarser on a large bar (at most about 250,000 points per bar): what is sought is a clip that misses
                // the cut-out, not a sliver of it
                var step = Math.Max(0.25, Math.Sqrt(inner.Width * inner.Height / 250_000));
                for (var y = inner.Top; y <= inner.Bottom; y += step)
                    for (var x = inner.Left; x <= inner.Right; x += step)
                        if (inside(x, y))
                        {
                            Failed = true;
                            return;
                        }
            }
        }

        private bool _draws = true;

        public void GlyphDraws(bool draws) => _draws = draws;

        // the copies of pattern cells are checked like forms
        public bool WantsPatternCell(RectD painted) => Hits(rects, painted);

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (Failed) return;
            // a space that draws nothing carries nothing, as for the pieces the bars grow by (Covering): one of the text layer of a scan
            // under such a piece refused the page (docs/FEHLERGEDAECHTNIS.md, No. 257); one read as a space that draws a letter is checked (No. 438)
            if (CarriesNothing(text, _draws, invisible)) return;
            // beside all areas, even moved by the possible shift: nothing to measure (No. 384)
            if (cellToPage.IsInvertible && !cellToPage.TransformBounds(cell.Union(ink)).Inflate(PositionGrid).Intersects(_near)) return;
            if (!cellToPage.IsInvertible)
            {
                Failed = AtOrigin(rects, cellToPage, cell);   // character without area under a bar
                return;
            }
            var along = vertical ? cellToPage.TransformVector(0, 1) : cellToPage.TransformVector(1, 0);
            var pagePerUnit = Math.Sqrt(along.X * along.X + along.Y * along.Y);
            if (pagePerUnit < 1e-9) return;
            // what stays may have moved by half the grid of the gap along its line: a character counts as left under a bar only if it lies
            // there wherever within that it started — also one smaller than that shift, which was passed over and a letter of 0.4 pt stayed
            // whole under a bar (docs/FEHLERGEDAECHTNIS.md, No. 383)
            var shrink = PositionGrid / 2 / pagePerUnit;
            // a character without a shape is judged by its cell, as when removing
            var shapeless = invisible || ink.IsEmpty;
            RectD Shift(RectD r, double by) => vertical
                ? RectD.FromLtrb(r.Left, r.Top + by, r.Right, r.Bottom + by)
                : RectD.FromLtrb(r.Left + by, r.Top, r.Right + by, r.Bottom);
            Failed = GlyphRemoved(rects, Shift(cell, -shrink), Shift(ink, -shrink), cellToPage, verify: true, shapeless) &&
                     GlyphRemoved(rects, Shift(cell, shrink), Shift(ink, shrink), cellToPage, verify: true, shapeless);
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden) { }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage) { }

        public void EndForm() { }
    }

    private static string Num(double v) => PdfSerializer.FormatReal(Math.Round(v, 3));

    /// <summary>
    /// A length or the phase of a dash pattern as read from the file — the shortest form that reads back as the same single-precision
    /// number the display draws with (<see cref="DashPattern"/>), not rounded: every period repeats the error of a rounded length
    /// (docs/FEHLERGEDAECHTNIS.md, No. 871).
    /// </summary>
    private static string DashNumber(float v)
    {
        if (!float.IsFinite(v)) return "0";
        var text = v.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E')) text = v.ToString("0.##########", CultureInfo.InvariantCulture);
        return text is "-0" ? "0" : text;
    }

    /// <summary>
    /// A point a path passes through (end points of m and l — also a lone m —, and 16 steps along each curve; not the control points, which
    /// may lie anywhere in its hull) that lies under an area beyond a seam at its edge — 0.5 pt, for a stroke its width more (cutting leaves
    /// a stroke reach + 0.5 pt short of the area, and a thick stroke over a narrow area is not cut at all) — and not on the edge of an area
    /// or a piece (a cut fill ends on the edge; where areas overlap that point lies inside the other). The invariant of cutting paths: no
    /// coordinate of what was redacted stays in the content. The same rule the check of the command line applies (docs/FEHLERGEDAECHTNIS.md,
    /// No. 449).
    /// </summary>
    /// <param name="ctm">User space → page.</param>
    /// <param name="lineWidth">The line width in user space; 0 for a fill or a path that paints nothing.</param>
    /// <param name="areas">The areas (page space).</param>
    /// <param name="edges">The edges a point may lie on.</param>
    /// <returns>The point and its area; null if there is none.</returns>
    /// <summary>
    /// Whether the line of a path comes within <paramref name="reach"/> (page space) of an area: its straight pieces exactly (against the
    /// area grown by the reach), a curve by the box of its control points; a subpath of a single point as that point (a cap paints a dot).
    /// A path whose line is nowhere near paints nothing under the areas when only stroked (<see cref="Observer.StrokeLine"/>).
    /// </summary>
    internal static bool StrokeNear(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, Matrix ctm, double reach, IReadOnlyList<RectD> areas)
    {
        var at = 0;
        double cx = 0, cy = 0, sx = 0, sy = 0;   // current point and start of the subpath, page space
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move when at + 1 < points.Length:
                    (cx, cy) = ctm.Transform(points[at], points[at + 1]);
                    (sx, sy) = (cx, cy);
                    at += 2;
                    if (SegmentNear(cx, cy, cx, cy)) return true;
                    break;
                case PathVerb.Line when at + 1 < points.Length:
                    var (lx, ly) = ctm.Transform(points[at], points[at + 1]);
                    at += 2;
                    if (SegmentNear(cx, cy, lx, ly)) return true;
                    (cx, cy) = (lx, ly);
                    break;
                case PathVerb.Cubic when at + 5 < points.Length:
                    var (x1, y1) = ctm.Transform(points[at], points[at + 1]);
                    var (x2, y2) = ctm.Transform(points[at + 2], points[at + 3]);
                    var (x3, y3) = ctm.Transform(points[at + 4], points[at + 5]);
                    at += 6;
                    // the curve lies within its control points
                    var hull = RectD.FromLtrb(Math.Min(Math.Min(cx, x1), Math.Min(x2, x3)), Math.Min(Math.Min(cy, y1), Math.Min(y2, y3)),
                        Math.Max(Math.Max(cx, x1), Math.Max(x2, x3)), Math.Max(Math.Max(cy, y1), Math.Max(y2, y3)));
                    if (!double.IsFinite(hull.Left + hull.Top + hull.Right + hull.Bottom)) return true;
                    var grown = hull.Inflate(reach);
                    foreach (var area in areas)
                        if (grown.Left <= area.Right && grown.Right >= area.Left && grown.Top <= area.Bottom && grown.Bottom >= area.Top) return true;
                    (cx, cy) = (x3, y3);
                    break;
                case PathVerb.Close:
                    if (SegmentNear(cx, cy, sx, sy)) return true;
                    (cx, cy) = (sx, sy);
                    break;
                default:
                    return true;   // points missing: not judged
            }
        }
        return false;

        bool SegmentNear(double ax, double ay, double bx, double by)
        {
            if (!double.IsFinite(ax + ay + bx + by)) return true;
            foreach (var area in areas)
            {
                // Liang–Barsky: the part of the segment within the grown area
                double left = area.Left - reach, top = area.Top - reach, right = area.Right + reach, bottom = area.Bottom + reach;
                double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
                if (Clip(-dx, ax - left) && Clip(dx, right - ax) && Clip(-dy, ay - top) && Clip(dy, bottom - ay)) return true;

                bool Clip(double p, double q)
                {
                    if (p == 0) return q >= 0;
                    var r = q / p;
                    if (p < 0)
                    {
                        if (r > t1) return false;
                        if (r > t0) t0 = r;
                    }
                    else
                    {
                        if (r < t0) return false;
                        if (r < t1) t1 = r;
                    }
                    return true;
                }
            }
            return false;
        }
    }

    internal static (double X, double Y, RectD Area)? PathPointUnder(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, Matrix ctm, double lineWidth,
        IReadOnlyList<RectD> areas, IReadOnlyList<RectD> edges)
    {
        var scale = ctm.MeanScale;
        var seam = 0.5 + (lineWidth > 0 && double.IsFinite(scale) ? lineWidth * scale : 0);
        var at = 0;
        double cx = 0, cy = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move or PathVerb.Line when at + 1 < points.Length:
                    (cx, cy) = (points[at], points[at + 1]);
                    at += 2;
                    if (Under(cx, cy) is { } point) return point;
                    break;
                case PathVerb.Cubic when at + 5 < points.Length:
                    double x1 = points[at], y1 = points[at + 1], x2 = points[at + 2], y2 = points[at + 3], x3 = points[at + 4], y3 = points[at + 5];
                    at += 6;
                    for (var k = 1; k <= 16; k++)
                    {
                        var t = k / 16.0;
                        var u = 1 - t;
                        if (Under(u * u * u * cx + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
                                u * u * u * cy + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3) is { } onCurve)
                            return onCurve;
                    }
                    (cx, cy) = (x3, y3);
                    break;
            }
        }
        return null;

        (double, double, RectD)? Under(double px, double py)
        {
            var (x, y) = ctm.Transform(px, py);
            if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
            // coordinates are written in single precision (No. 428): under a huge matrix a point on an edge lands a little beside it
            var tol = 0.01 + (double.IsFinite(scale) ? scale : 0) * (Math.Abs(px) + Math.Abs(py)) * Math.Pow(2, -22);
            foreach (var e in edges)
                if ((Math.Abs(x - e.Left) < tol || Math.Abs(x - e.Right) < tol) && y >= e.Top - tol && y <= e.Bottom + tol ||
                    (Math.Abs(y - e.Top) < tol || Math.Abs(y - e.Bottom) < tol) && x >= e.Left - tol && x <= e.Right + tol)
                    return null;
            foreach (var a in areas)
                if (x > a.Left + seam && x < a.Right - seam && y > a.Top + seam && y < a.Bottom - seam) return (x, y, a);
            return null;
        }
    }

    /// <summary>
    /// A character without area (0 Tf, 0 Tz, a matrix that flattens it): under a bar if its origin is, if the middle of the line its cell is
    /// flattened to is — as a character with area counts by its middle —, or if at least half of that line lies under the bars. An origin just
    /// beside a bar kept a character whose line lay under it (docs/FEHLERGEDAECHTNIS.md, No. 381); a line with its middle under a small bar but
    /// a little less than half of it there kept its character as well, and the text stayed in the file at the bar (No. 419).
    /// </summary>
    private static bool AtOrigin(List<RectD> rects, Matrix cellToPage, RectD cell)
    {
        static bool Inside(List<RectD> rects, double x, double y) =>
            double.IsFinite(x) && double.IsFinite(y) && rects.Any(r => x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom);
        var (ox, oy) = cellToPage.Transform(0, 0);
        var corners = Corners(cell, cellToPage);
        return Inside(rects, ox, oy) || Longest(corners) is var (a, b) && Inside(rects, (a.X + b.X) / 2, (a.Y + b.Y) / 2) || LineShare(rects, corners) >= 0.5;
    }

    /// <summary>
    /// The two corners farthest apart (the line a flattened cell lies on). The distances without squaring: a matrix stretching a character to
    /// 10^296 points made every square infinite, the first pair counted as farthest and the middle of the line lay elsewhere
    /// (docs/FEHLERGEDAECHTNIS.md, No. 419).
    /// </summary>
    private static ((double X, double Y) A, (double X, double Y) B) Longest((double X, double Y)[] corners)
    {
        (double X, double Y) a = corners[0], b = corners[0];
        var longest = 0.0;
        for (var i = 0; i < corners.Length; i++)
            for (var j = i + 1; j < corners.Length; j++)
            {
                var d = Distance(corners[i], corners[j]);
                if (d > longest) (longest, a, b) = (d, corners[i], corners[j]);
            }
        return (a, b);
    }

    /// <summary>The distance of two points, without the overflow of squaring large coordinates.</summary>
    private static double Distance((double X, double Y) p, (double X, double Y) q)
    {
        double dx = Math.Abs(p.X - q.X), dy = Math.Abs(p.Y - q.Y);
        var large = Math.Max(dx, dy);
        if (!(large > 0)) return 0;
        var small = Math.Min(dx, dy) / large;
        return large * Math.Sqrt(1 + small * small);
    }

    /// <summary>
    /// A cell flattened to a line (or nearly): the share of the line between its two corners farthest apart that lies under the areas (17 points
    /// along it); 0 if it is a point.
    /// </summary>
    private static double LineShare(List<RectD> rects, (double X, double Y)[] corners)
    {
        static bool Inside(List<RectD> rects, double x, double y) =>
            double.IsFinite(x) && double.IsFinite(y) && rects.Any(r => x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom);
        var (a, b) = Longest(corners);
        var longest = Distance(a, b);
        if (!(longest > 1e-6) || !double.IsFinite(longest)) return 0;
        const int samples = 17;
        var inside = 0;
        for (var k = 0; k < samples; k++)
        {
            var t = k / (double)(samples - 1);
            if (Inside(rects, a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)) inside++;
        }
        return inside / (double)samples;
    }

    /// <summary><see cref="Hits(List{RectD}, RectD)"/> for many areas: only those near the box are tested.</summary>
    internal static bool Hits(AreaIndex rects, RectD bounds) =>
        rects.Any(bounds, 0.02, r =>
        {
            var i = r.Intersect(bounds);
            return i.Right - i.Left > 0.01 && i.Bottom - i.Top > 0.01;
        });

    /// <summary>The box around all areas, a little larger (what touches an area within <see cref="Hits(List{RectD}, RectD)"/>'s tolerance lies in it).</summary>
    private static RectD Around(List<RectD> rects)
    {
        if (rects.Count == 0) return RectD.Empty;
        var around = rects[0];
        foreach (var r in rects) around = around.Union(r);
        return around.Inflate(0.05);
    }

    internal static bool Hits(List<RectD> rects, RectD bounds)
    {
        foreach (var r in rects)
        {
            var i = r.Intersect(bounds);
            if (i.Right - i.Left > 0.01 && i.Bottom - i.Top > 0.01) return true;
        }
        return false;
    }

    /// <summary>A character counts as redacted if at least this fraction of its width and height lies under an area.</summary>
    private const double GlyphCoverage = 0.3;

    /// <summary>
    /// Determines whether a character lies under the areas so that redaction can remove it — by <see cref="GlyphCoverage"/> in
    /// width and height. Used for copying and for the search for further occurrences; counting only the centre would put a
    /// character covered by 40 % on the clipboard even though redaction removes it.
    /// </summary>
    /// <param name="rects">The redaction areas in page space.</param>
    /// <param name="bounds">Bounds of the character in page space.</param>
    /// <returns><see langword="true"/> if the character counts as redacted.</returns>
    public static bool CoversCharacter(List<RectD> rects, RectD bounds) => Covers(rects, bounds, GlyphCoverage);

    /// <summary>
    /// Whether <paramref name="bounds"/> lies under the areas by at least the fraction <paramref name="fraction"/> in width and height: a
    /// rectangle of that size inside it is covered by their union. Areas count together — several bars side by side over a letter, each
    /// too narrow by itself, left its text under them (docs/FEHLERGEDAECHTNIS.md, No. 165).
    /// </summary>
    internal static bool Covers(List<RectD> rects, RectD bounds, double fraction)
    {
        // measured against its own size, however small: with a floor of a millionth of a point, a character narrower than that (a matrix
        // squeezing it to nothing) could never be covered and stayed whole under a bar (docs/FEHLERGEDAECHTNIS.md, No. 385); one without
        // width or height has no part under an area and is judged elsewhere (AtOrigin)
        double w = bounds.Width, h = bounds.Height;
        var parts = new List<RectD>();
        foreach (var r in rects)
        {
            var i = r.Intersect(bounds);
            if (i.Right - i.Left <= 0 || i.Bottom - i.Top <= 0) continue;
            if (i.Right - i.Left >= fraction * w && i.Bottom - i.Top >= fraction * h) return true;
            parts.Add(i);
        }
        if (parts.Count < 2) return false;
        // the corners of the parts divide the bounds into cells; a covered rectangle is a block of covered cells
        var (xs, ys, covered) = Cells(parts, bounds);
        var nx = xs.Length - 1;
        var ny = ys.Length - 1;
        // per row, covered cells counted from the left
        var prefix = new int[ny, nx + 1];
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++) prefix[j, i + 1] = prefix[j, i] + (covered[i, j] ? 1 : 0);
        for (var a = 0; a < nx; a++)
            for (var b = a + 1; b <= nx; b++)
            {
                if (xs[b] - xs[a] < fraction * w) continue;
                var run = 0.0;
                for (var j = 0; j < ny; j++)
                {
                    run = prefix[j, b] - prefix[j, a] == b - a ? run + (ys[j + 1] - ys[j]) : 0;
                    if (run >= fraction * h) return true;
                }
            }
        return false;
    }

    /// <summary>
    /// The cells into which the corners of the parts divide <paramref name="bounds"/> (sorted edges) and which of them a part covers. Up
    /// to 64 parts are taken exactly; beyond that the bounds are sampled in a grid of 64 × 64 cells.
    /// </summary>
    private static (double[] Xs, double[] Ys, bool[,] Covered) Cells(List<RectD> parts, RectD bounds)
    {
        double[] xs, ys;
        if (parts.Count <= 64)
        {
            xs = [.. parts.SelectMany(p => new[] { p.Left, p.Right }).Append(bounds.Left).Append(bounds.Right).Distinct().Order()];
            ys = [.. parts.SelectMany(p => new[] { p.Top, p.Bottom }).Append(bounds.Top).Append(bounds.Bottom).Distinct().Order()];
        }
        else
        {
            xs = [.. Enumerable.Range(0, 65).Select(i => bounds.Left + bounds.Width * i / 64)];
            ys = [.. Enumerable.Range(0, 65).Select(i => bounds.Top + bounds.Height * i / 64)];
        }
        var covered = new bool[xs.Length - 1, ys.Length - 1];
        for (var i = 0; i + 1 < xs.Length; i++)
            for (var j = 0; j + 1 < ys.Length; j++)
            {
                double cx = (xs[i] + xs[i + 1]) / 2, cy = (ys[j] + ys[j + 1]) / 2;
                foreach (var p in parts)
                    if (cx >= p.Left && cx <= p.Right && cy >= p.Top && cy <= p.Bottom)
                    {
                        covered[i, j] = true;
                        break;
                    }
            }
        return (xs, ys, covered);
    }

    /// <summary>The union of the areas within <paramref name="bounds"/> as rectangles that do not overlap (the covered cells, see <see cref="Cells"/>).</summary>
    private static List<RectD> Disjoint(List<RectD> rects, RectD bounds)
    {
        var parts = new List<RectD>();
        foreach (var r in rects)
        {
            var i = r.Intersect(bounds);
            if (i.Right - i.Left > 0 && i.Bottom - i.Top > 0) parts.Add(i);
        }
        if (parts.Count <= 1) return parts;
        var (xs, ys, covered) = Cells(parts, bounds);
        var cells = new List<RectD>();
        for (var i = 0; i + 1 < xs.Length; i++)
            for (var j = 0; j + 1 < ys.Length; j++)
                if (covered[i, j]) cells.Add(RectD.FromLtrb(xs[i], ys[j], xs[i + 1], ys[j + 1]));
        return cells;
    }

    /// <summary>
    /// Whether to remove a character: its shape (outlines) must be covered by <see cref="GlyphCoverage"/> and its cell as well —
    /// or the shape almost entirely (<see cref="InkAloneCoverage"/>). This way a bar placed tightly around a small character
    /// (dot, comma, dash) at high zoom takes effect even though it barely covers the cell, while a neighbour whose shape only
    /// reaches into the cell of the redacted character (the hook of a "j", italics) stays. An invisible character (text rendering
    /// mode 3, an OCR layer) goes by its cell alone: nobody sees its shape, selecting shows its cell — a dot whose cell lay mostly
    /// under a bar stayed because its shape lay below it (docs/FEHLERGEDAECHTNIS.md, No. 196). <paramref name="verify"/>: the
    /// thresholds of the cross-check.
    /// </summary>
    /// <summary>
    /// Whether a character goes under the bars: by its shape (<paramref name="ink"/>) together with its cell — an invisible one, and one whose
    /// shape is empty (an outline without area, as a damaged font program gives it), by its cell alone: an empty shape counted as the point of
    /// its origin, which lay beside the bar, and the character's text stayed in the file under it (docs/FEHLERGEDAECHTNIS.md, No. 377).
    /// </summary>
    internal static bool GlyphRemoved(List<RectD> rects, RectD cell, RectD ink, Matrix cellToPage, bool verify = false, bool invisible = false)
    {
        var (fraction, rotated, inkAlone) = verify ? (VerifyCoverage, VerifyCoverage, 0.9) : (GlyphCoverage, RotatedGlyphCoverage, InkAloneCoverage);
        if (invisible || ink.IsEmpty) return GlyphCovered(rects, cell, cellToPage, fraction, rotated);
        if (!GlyphCovered(rects, ink, cellToPage, fraction, rotated)) return false;
        return GlyphCovered(rects, cell, cellToPage, fraction, rotated) || GlyphCovered(rects, ink, cellToPage, inkAlone, inkAlone);
    }

    private const double InkAloneCoverage = 0.7;
    private const double VerifyCoverage = 0.6;

    /// <summary>
    /// Whether a character is redacted. Upright (or rotated by 90°): covered by <see cref="GlyphCoverage"/> in width and height.
    /// Slanted (such as a diagonal stamp): the bounds would be much larger than the character and would include neighbours, so
    /// the covered fraction of the rotated cell itself counts, from <see cref="RotatedGlyphCoverage"/>.
    /// </summary>
    private static bool GlyphCovered(List<RectD> rects, RectD cell, Matrix cellToPage, double upright = GlyphCoverage, double rotated = RotatedGlyphCoverage)
    {
        if (AxisAligned(cellToPage)) return Covers(rects, cellToPage.TransformBounds(cell), upright);
        return QuadCoverage(rects, Corners(cell, cellToPage)) >= rotated;
    }

    private const double RotatedGlyphCoverage = 0.25;

    private static bool AxisAligned(Matrix m)
    {
        var scale = Math.Max(Math.Max(Math.Abs(m.A), Math.Abs(m.B)), Math.Max(Math.Abs(m.C), Math.Abs(m.D)));
        var tolerance = scale * 1e-3;
        return Math.Abs(m.B) <= tolerance && Math.Abs(m.C) <= tolerance || Math.Abs(m.A) <= tolerance && Math.Abs(m.D) <= tolerance;
    }

    private static (double X, double Y)[] Corners(RectD r, Matrix m) =>
        [m.Transform(r.Left, r.Top), m.Transform(r.Right, r.Top), m.Transform(r.Right, r.Bottom), m.Transform(r.Left, r.Bottom)];

    /// <summary>Fraction of a (convex) quadrilateral that lies under the areas (overlapping areas count once).</summary>
    internal static double QuadCoverage(List<RectD> rects, (double X, double Y)[] quad)
    {
        var total = Math.Abs(Area(quad));
        // all but flat (a matrix that squeezes it to a sliver): by the line it lies on, as a character without area — counted as uncovered,
        // a sliver of a letter stayed under a bar over the whole page (docs/FEHLERGEDAECHTNIS.md, No. 391). Flat also relative to its sides
        // (the sine of their angle below a millionth): far from the origin a sliver still has a large area, made only of rounding (No. 395)
        double Side(int i) => Math.Sqrt((quad[i].X - quad[0].X) * (quad[i].X - quad[0].X) + (quad[i].Y - quad[0].Y) * (quad[i].Y - quad[0].Y));
        if (total < 1e-9 || quad.Length == 4 && !(total >= 1e-6 * Side(1) * Side(3))) return LineShare(rects, quad);
        var bounds = RectD.FromLtrb(quad.Min(p => p.X), quad.Min(p => p.Y), quad.Max(p => p.X), quad.Max(p => p.Y));
        double covered = 0;
        foreach (var r in Disjoint(rects, bounds)) covered += Math.Abs(Area(Clip(quad, r)));
        return Math.Min(1, covered / total);
    }

    /// <summary>Clips a convex polygon to an axis-aligned rectangle (Sutherland–Hodgman).</summary>
    private static (double X, double Y)[] Clip((double X, double Y)[] polygon, RectD r)
    {
        var points = polygon.ToList();
        foreach (var (inside, cut) in new (Func<(double X, double Y), bool>, Func<(double X, double Y), (double X, double Y), (double X, double Y)>)[]
                 {
                     (p => p.X >= r.Left, (a, b) => At(a, b, (r.Left - a.X) / (b.X - a.X))),
                     (p => p.X <= r.Right, (a, b) => At(a, b, (r.Right - a.X) / (b.X - a.X))),
                     (p => p.Y >= r.Top, (a, b) => At(a, b, (r.Top - a.Y) / (b.Y - a.Y))),
                     (p => p.Y <= r.Bottom, (a, b) => At(a, b, (r.Bottom - a.Y) / (b.Y - a.Y))),
                 })
        {
            if (points.Count == 0) break;
            var next = new List<(double X, double Y)>(points.Count + 4);
            for (var i = 0; i < points.Count; i++)
            {
                var current = points[i];
                var previous = points[(i + points.Count - 1) % points.Count];
                if (inside(current))
                {
                    if (!inside(previous)) next.Add(cut(previous, current));
                    next.Add(current);
                }
                else if (inside(previous))
                {
                    next.Add(cut(previous, current));
                }
            }
            points = next;
        }
        return [.. points];

        static (double X, double Y) At((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static double Area((double X, double Y)[] p)
    {
        if (p.Length == 0) return 0;
        // relative to its first corner: far from the origin (a page set at 10^11 points) the products of the coordinates are so large that
        // their difference — the area — was lost in rounding, and a character wholly under a bar counted as hardly covered
        // (docs/FEHLERGEDAECHTNIS.md, No. 387)
        var (ox, oy) = p[0];
        double sum = 0;
        for (var i = 0; i < p.Length; i++)
        {
            var (x0, y0) = (p[i].X - ox, p[i].Y - oy);
            var (x1, y1) = (p[(i + 1) % p.Length].X - ox, p[(i + 1) % p.Length].Y - oy);
            sum += x0 * y1 - x1 * y0;
        }
        return sum / 2;
    }

    /// <summary>Fraction of the area of <paramref name="bounds"/> under the redactions (lines: as a band of at least 0.5 pt).</summary>
    private static double Coverage(List<RectD> rects, RectD bounds)
    {
        if (bounds.Width < 0.5 || bounds.Height < 0.5) bounds = bounds.Inflate(0.25);
        var total = Math.Max(bounds.Width * bounds.Height, 1e-9);
        double covered = 0;
        // overlapping areas count once: three bars over the same word counted three times and removed the background under it
        // (docs/FEHLERGEDAECHTNIS.md, No. 165)
        foreach (var r in Disjoint(rects, bounds)) covered += r.Width * r.Height;
        return Math.Min(1, covered / total);
    }

    /// <summary>Extends the bounds of the last subpath by a point.</summary>
    private static void Extend(List<(int Start, RectD Bounds, bool Empty)> subpaths, double x, double y)
    {
        var (start, b, empty) = subpaths[^1];
        subpaths[^1] = (start, empty ? RectD.FromLtrb(x, y, x, y) : RectD.FromLtrb(Math.Min(b.Left, x), Math.Min(b.Top, y), Math.Max(b.Right, x), Math.Max(b.Bottom, y)), false);
    }

    /// <summary>
    /// Removes from a rewritten page what describes its content in another form: the thumbnail, private data of other programs, object
    /// metadata (XMP) and associated files (ISO 32000-2, 14.13 — the data of a chart or the original of a picture beside a redacted one;
    /// docs/FEHLERGEDAECHTNIS.md, No. 434).
    /// </summary>
    internal static void WithoutOldForms(PdfDictionary page)
    {
        page.Remove(PdfName.Get("Thumb"));
        foreach (var key in DescribesOldContent) page.Remove(PdfName.Get(key));
    }

    /// <summary>
    /// Entries that describe content in another form and so outlive its redaction: data of other programs (/PieceInfo, /LastModified),
    /// object metadata (XMP, ISO 32000-2 14.3.2), associated files (14.13: the data of a chart, the source of a formula), alternate images
    /// and references to the original file (/OPI, /Ref). Dropped wherever redacted content is rewritten — page, form, pattern, image,
    /// property list of marked content, structure element (docs/FEHLERGEDAECHTNIS.md, No. 434 and 444). /MCAF: the files of marked content
    /// in the form of the corrigenda of ISO 32000-2, 14.13.5, a dictionary with /MCAF as the property list (before, only the older form, an
    /// array, left the copy — No. 1254).
    /// </summary>
    internal static readonly string[] DescribesOldContent = ["PieceInfo", "LastModified", "Metadata", "AF", "MCAF", "Alternates", "OPI", "Ref"];

    /// <summary>
    /// What a redacted page draws, without descriptions of marked content where no observer looked (docs/FEHLERGEDAECHTNIS.md, No. 696 and
    /// 697): the tiling patterns and forms the page uses (<see cref="Used"/>), the glyphs of the Type 3 fonts it shows, and below them what
    /// their content uses of their own resources (patterns, forms, Type 3 fonts, named property lists; 8 levels). Each stream with /ActualText,
    /// /Alt, /E or what describes the content in another form (<see cref="DescribesOldContent"/>), there or below, becomes a copy without
    /// them; the original joins the replaced objects (the writer leaves it out unless something else still draws it — shared resources name it
    /// on other pages as well). Only what is used is looked at, so every copy is of something drawn as it is: resources are often shared, and
    /// a copy of a form merely named there would carry its unredacted content past the writer, which leaves the original out. What the
    /// rewriter noted as replaced is no reason to leave a stream out: it notes as well what it only took out of resources that did not use it. A Type 3 font becomes a copy with only the glyphs shown here (<paramref name="reduce"/>, as the rewriter makes it), so
    /// that it keeps no glyph the writer would take out of the original font (No. 169); if that cannot be told, it stays as it is.
    /// </summary>
    private sealed class DescriptionPass(PdfDocument doc, HashSet<int> replaced, Func<PdfDictionary, HashSet<byte>, PdfDictionary?>? reduce)
    {
        /// <summary>A form (an appearance stream) without descriptions in its content and below; null if it has none.</summary>
        public PdfStream? Form(PdfStream form) => Stream(form, 0);

        private static readonly PdfName FontName = PdfName.Get("Font"), CharProcsName = PdfName.Get("CharProcs");
        private readonly Dictionary<PdfStream, PdfStream> _done = new(ReferenceEqualityComparer.Instance);

        /// <summary>What a content uses of its resources: patterns painted, XObjects invoked, the codes each font shows (null: not known).</summary>
        internal readonly record struct Used(IReadOnlySet<PdfName> Patterns, IReadOnlySet<PdfName> XObjects, Dictionary<PdfDictionary, HashSet<byte>>? Codes)
        {
            public static Used Of(PdfDocument doc, byte[] content, PdfDictionary resources)
            {
                var (xobjects, patterns, _) = ResourceUsage.Names(doc, content, resources);
                return new Used(patterns, xobjects, ResourceUsage.FontCodes(doc, content, resources));
            }
        }

        /// <summary>The resources without descriptions in what <paramref name="used"/> names; null if nothing changes.</summary>
        public PdfDictionary? Resources(PdfDictionary? resources, int depth, Used used)
        {
            if (resources is null || depth > 8) return null;
            PdfDictionary? copy = null;
            PdfDictionary Copy() => copy ??= CopyOf(resources);
            // named property lists (inside a pattern or form: those its content names; on the page the rewriter has done them)
            if (depth > 0 && resources.GetDictionary(PropertiesName) is { } properties)
            {
                PdfDictionary? lists = null;
                foreach (var (name, _) in properties.Entries.Take(10_000))
                    if (properties.Get(name) is PdfDictionary list && PlainList(list) is { } plain) (lists ??= CopyOf(properties)).Set(name, plain);
                if (lists is not null) Copy().Set(PropertiesName, lists);
            }
            // patterns and forms
            foreach (var (kind, names) in (ReadOnlySpan<(PdfName, IReadOnlySet<PdfName>)>)[(PatternName, used.Patterns), (XObjectName, used.XObjects)])
            {
                if (resources.GetDictionary(kind) is not { } entries) continue;
                PdfDictionary? changed = null;
                foreach (var (name, _) in entries.Entries.Take(10_000))
                {
                    if (!names.Contains(name)) continue;
                    var raw = entries.GetRaw(name);
                    if (entries.Get(name) is not PdfStream stream) continue;
                    var isCell = stream.Dictionary.GetInteger(PdfName.Get("PatternType")) == 1;
                    var isForm = stream.Dictionary.GetName(PdfName.Subtype)?.Value == "Form";
                    if (!isCell && !isForm) continue;
                    if (Stream(stream, depth) is not { } plain) continue;
                    (changed ??= CopyOf(entries)).Set(name, plain);
                    // the original leaves the file unless something still draws it (shared resources name it on other pages too)
                    if (raw is PdfReference original) replaced.Add(original.Id.Number);
                }
                if (changed is not null) Copy().Set(kind, changed);
            }
            // Type 3 fonts: the glyphs shown
            if (used.Codes is { } codes && resources.GetDictionary(FontName) is { } fonts)
            {
                PdfDictionary? changed = null;
                foreach (var (name, _) in fonts.Entries.Take(10_000))
                {
                    if (fonts.Get(name) is PdfDictionary font && codes.TryGetValue(font, out var shown) && Type3(font, shown, depth) is { } plain)
                        (changed ??= CopyOf(fonts)).Set(name, plain);
                }
                if (changed is not null) Copy().Set(FontName, changed);
            }
            return copy;
        }

        /// <summary>
        /// A Type 3 font with only the glyphs of <paramref name="shown"/>, those without descriptions; null if none of them has one, or if it
        /// cannot be told which glyphs the codes name (no /Differences).
        /// </summary>
        private PdfDictionary? Type3(PdfDictionary font, HashSet<byte> shown, int depth)
        {
            if (reduce is null || shown.Count == 0 || font.GetName(PdfName.Subtype)?.Value != "Type3" || font.GetDictionary(CharProcsName) is not { } procs) return null;
            if (ResourceUsage.Type3Glyphs(font, shown) is not { } names) return null;
            Dictionary<PdfName, PdfStream>? plainGlyphs = null;
            foreach (var (glyph, _) in procs.Entries.Take(10_000))
                if (names.Contains(glyph) && procs.Get(glyph) is PdfStream proc && Stream(proc, depth) is { } plain) (plainGlyphs ??= []).Add(glyph, plain);
            if (plainGlyphs is null) return null;
            var reduced = reduce(font, shown) ?? CopyOf(font);
            var kept = CopyOf(reduced.GetDictionary(CharProcsName) ?? procs);
            foreach (var (glyph, plain) in plainGlyphs)
            {
                kept.Set(glyph, plain);
                // the original glyph leaves the file unless a page still draws it: the font is often shared, and pages that do not show the
                // glyph would otherwise keep it with its description (as the rewriter does with glyphs it removes)
                if (procs.GetRaw(glyph) is PdfReference original) replaced.Add(original.Id.Number);
            }
            var copy = ReferenceEquals(reduced, font) ? CopyOf(font) : reduced;
            copy.Set(CharProcsName, kept);
            // the font's own resources: their named property lists (glyphs name them there)
            if (Resources(font.GetDictionary(PdfName.Resources), depth + 1, new Used(new HashSet<PdfName>(), new HashSet<PdfName>(), null)) is { } fontResources)
                copy.Set(PdfName.Resources, fontResources);
            return copy;
        }

        /// <summary>
        /// A stream (pattern cell, form, glyph) without descriptions in its content and in what it uses of its resources; null if it has none.
        /// Each stream is done once (a pattern named from several places stays one copy).
        /// </summary>
        private PdfStream? Stream(PdfStream stream, int depth)
        {
            if (_done.TryGetValue(stream, out var known)) return ReferenceEquals(known, stream) ? null : known;
            _done[stream] = stream;   // a stream that names itself: as it is
            byte[] data;
            try
            {
                data = doc.GetStreamData(stream);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
            {
                return null;
            }
            var written = data.AsSpan().IndexOf("BDC"u8) >= 0 ? Accessibility.MarkedContentIds.WithPropertyLists(data, PlainList) : data;
            PdfDictionary? ownResources = null;
            if (stream.Dictionary.GetDictionary(PdfName.Resources) is { } resources)
                ownResources = Resources(resources, depth + 1, Used.Of(doc, data, resources));
            if (ReferenceEquals(written, data) && ownResources is null) return null;
            var dict = CopyOf(stream.Dictionary);
            foreach (var key in (ReadOnlySpan<string>)["Filter", "DecodeParms", "Length", "DL"]) dict.Remove(PdfName.Get(key));
            if (ownResources is not null) dict.Set(PdfName.Resources, ownResources);
            var plain = new PdfStream(dict, written);
            _done[stream] = plain;
            return plain;
        }
    }

    /// <summary>
    /// The appearances of an annotation that stays on a redacted page, without descriptions of marked content (docs/FEHLERGEDAECHTNIS.md,
    /// No. 697): every description of the page goes (No. 134, 624), also in the normal, rollover and down appearances and their states. A new
    /// dictionary with copies of the streams that had some; streams shared with other annotations stay as they are for those. Glyphs of Type 3
    /// fonts are not followed here (a copy of such a font would escape the writer, which keeps in it only what the pages still show). Null if
    /// nothing changes.
    /// </summary>
    internal static PdfDictionary? AppearancesWithoutDescriptions(PdfDocument doc, PdfDictionary appearances)
    {
        var pass = new DescriptionPass(doc, [], null);
        PdfDictionary? copy = null;
        foreach (var (key, _) in appearances.Entries.Take(16))
        {
            switch (appearances.Get(key))
            {
                case PdfStream stream when pass.Form(stream) is { } plain:
                    (copy ??= CopyOf(appearances)).Set(key, plain);
                    break;
                case PdfDictionary states:
                    PdfDictionary? changed = null;
                    foreach (var (state, _) in states.Entries.Take(10_000))
                        if (states.Get(state) is PdfStream s && pass.Form(s) is { } plainState) (changed ??= CopyOf(states)).Set(state, plainState);
                    if (changed is not null) (copy ??= CopyOf(appearances)).Set(key, changed);
                    break;
            }
        }
        return copy;
    }

    /// <summary>A property list of marked content without /ActualText, /Alt, /E and <see cref="DescribesOldContent"/>; null if it has none of them.</summary>
    private static PdfDictionary? PlainList(PdfDictionary list)
    {
        if (!list.Entries.Any(e => e.Key.Value is "ActualText" or "Alt" or "E" || DescribesOld(e.Key))) return null;
        var copy = CopyOf(list);
        foreach (var (key, _) in list.Entries.ToList())
            if (key.Value is "ActualText" or "Alt" or "E" || DescribesOld(key)) copy.Remove(key);
        return copy;
    }

    /// <summary>A shallow copy of a dictionary that resolves references as the original does.</summary>
    private static PdfDictionary CopyOf(PdfDictionary dict)
    {
        var copy = new PdfDictionary(dict.Count) { Resolver = dict.Resolver };
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    private static readonly PdfName PropertiesName = PdfName.Get("Properties"), PatternName = PdfName.Get("Pattern"), XObjectName = PdfName.Get("XObject");

    /// <summary>Whether a key is one of <see cref="DescribesOldContent"/>.</summary>
    internal static bool DescribesOld(PdfName key) => Array.IndexOf(DescribesOldContent, key.Value) >= 0;

    /// <summary>
    /// Whether the content of a form (not the forms inside it) describes marked content: a BDC whose properties — given there or named in the
    /// form's resources — have /ActualText, /Alt, /E or one of <see cref="DescribesOldContent"/>, or associate files („/AF /MC0 BDC“). Named
    /// properties of a form without resources of its own lie in the page or form around it: then yes (its copy settles it). An unreadable form:
    /// no (it draws nothing). See docs/FEHLERGEDAECHTNIS.md, No. 624.
    /// </summary>
    internal static bool DescribesMarkedContent(PdfDocument doc, PdfStream form)
    {
        byte[] data;
        try
        {
            data = doc.GetStreamData(form);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
        {
            return false;
        }
        if (data.AsSpan().IndexOf("BDC"u8) < 0) return false;
        static bool Describing(PdfDictionary d) => d.Entries.Any(e => e.Key.Value is "ActualText" or "Alt" or "E" || DescribesOld(e.Key));
        var resources = form.Dictionary.GetDictionary(PdfName.Resources);
        var parser = new ContentParser(data);
        while (parser.Next() is { } op)
        {
            if (op != "BDC" || parser.Operands.Count < 2) continue;
            if (parser.Operands[1].Object is PdfDictionary inline)
            {
                if (Describing(inline)) return true;
                continue;
            }
            if (parser.Operands[1].Name is not { } name) continue;
            if (resources is null) return true;
            switch (resources.GetDictionary(PdfName.Get("Properties"))?.Get(name))
            {
                case PdfArray when parser.Operands[0].Name?.Value == "AF":
                    return true;
                case PdfDictionary named when !named.IsType(PdfName.Get("OCG")) && !named.IsType(PdfName.Get("OCMD")) && Describing(named):
                    return true;
            }
        }
        return false;
    }

    internal static PdfDictionary CopyResources(PdfDictionary? resources)
    {
        var copy = new PdfDictionary { Resolver = resources?.Resolver };
        if (resources is not null)
            foreach (var (key, value) in resources.Entries) copy.Set(key, value);
        return copy;
    }

    /// <summary>Page content concatenated (as the interpreter reads it).</summary>
    /// <summary>
    /// The content of a page as the display reads it (<see cref="PageInterpreter.PageContent(PdfDocument, PdfDictionary)"/>): the rewriter numbers
    /// the operators as the display reported them. It read them with a function of its own — a part that could not be decoded broke the
    /// redaction off where the display showed it empty, and forms went another way of decoding (docs/FEHLERGEDAECHTNIS.md, No. 452).
    /// </summary>
    internal static PooledBytes PageContent(PdfDocument doc, PdfDictionary page) => PageInterpreter.PageContent(doc, page);

    /// <summary>The content of a form or pattern cell as the display executes it (see <see cref="PageContent"/>).</summary>
    private static byte[] StreamContent(PdfDocument doc, PdfStream stream)
    {
        using var content = PageInterpreter.ContentBytes(doc, stream);
        return content.Span.ToArray();
    }

    // ------------------------------------------------------------ What to do where

    /// <summary>
    /// An observer that collects into <see cref="Scope"/>s what the rewriter changes: what every such observer passes on from the display
    /// in the same way — which text operators add to a clip (the text rendering mode is saved by q and inherited by forms, so the rewriter
    /// does not follow it on its own; docs/FEHLERGEDAECHTNIS.md, No. 426) and where a path begun without m starts (No. 428).
    /// </summary>
    private interface IScopeObserver : IContentObserver
    {
        /// <summary>The scope of the content being interpreted.</summary>
        Scope Current { get; }

        bool IContentObserver.WantsTextClip => true;

        void IContentObserver.TextClipGlyph(int op, int element, int start)
        {
            if (!Current.ClipGlyphs.TryGetValue(op, out var list)) Current.ClipGlyphs[op] = list = [];
            list.Add((element, start));
        }

        bool IContentObserver.WantsPathStart => true;

        void IContentObserver.PathStart(int op, double x, double y) => Current.PathStarts[op] = (x, y);
    }

    /// <summary>Decisions for a content stream (page or one invocation of a form), by operator number.</summary>
    private sealed class Scope
    {
        // its elements belong to a knockout group (a form with /Group /K true, or a form without a group inside one; ISO 32000-2, 11.4.6)
        public bool Knockout;
        public readonly Dictionary<int, List<GlyphCut>> Glyphs = [];
        public readonly Dictionary<int, Matrix?> Paths = [];   // null = remove, otherwise clip out with this CTM
        public readonly Dictionary<int, (Matrix ImageToPage, PdfStream? Stream, InlineImage? Inline, PdfDictionary? Resources)> Images = [];
        public readonly Dictionary<int, Matrix> Shadings = [];
        // the default space of this content (a form's, a mask group's, a pattern cell's) → page: where its patterns are placed (pattern
        // space is relative to it, ISO 32000-2, 8.7.2); the page's own is the page's
        public Matrix ToPage = Matrix.Identity;
        public readonly Dictionary<int, Scope> Forms = [];
        public readonly Dictionary<int, (PdfStream Group, Scope Scope)> Masks = [];   // gs with a soft mask: its content
        public readonly HashSet<int> Dropped = [];   // Do, BI, sh: removed entirely (hidden layer)
        public readonly HashSet<int> PaintedOutside = [];   // paths whose painted part (inside the clip) lies beside all areas
        public readonly Dictionary<int, (double Reach, DashPattern? Dash)> Strokes = [];   // painted paths: reach of their stroke's ends, dashes
        public readonly Dictionary<int, (double Fill, double Stroke, byte Blend, StrokeStyle Style)> TransparentPairs = [];   // B … with opacity or blend mode (no pattern)
        public readonly Dictionary<int, Matrix> ClipPaths = [];   // paths that set a clip reaching under an area: their CTM
        public readonly Dictionary<(int Op, bool Stroke), (PdfStream Pattern, Scope Scope)> PatternCells = [];   // tiling pattern a path paints with: its cell
        // moved images and drawings (Redactor.ContentEdits.cs): at a "q" the matrix follows it, around a painting operator, Do, BI or sh it
        // wraps the operator (with its path) in q … Q; new images drawn before an operator
        public readonly Dictionary<int, Matrix> Transforms = [];
        public readonly Dictionary<int, Matrix> LineMatrices = [];   // text line matrix after each operator that starts a line, as the display has it
        public readonly Dictionary<int, (double X, double Y)> PathStarts = [];   // path begun without m: where the display starts it (user space)
        public readonly Dictionary<int, List<(int Element, int Start)>> ClipGlyphs = [];   // characters that add an outline to the text clip, as the display has them
        public readonly Dictionary<int, Matrix> PatternMoves = [];   // the same in the stream's default space, for the patterns painted there
        public readonly Dictionary<int, List<(double X, double Y)[]>> ClipsBefore = [];   // clips right before a painting operator, in q … Q (its space)
        public readonly Dictionary<int, List<(PdfStream Image, Matrix Place, (double X, double Y)[]? Clip)>> Inserts = [];
        public RectD? GrowBox;   // a form's copy: its box must hold this area as well (form space), where moved or new content lies
        public HashSet<int>? Keep;   // copying chosen objects: only these painting operators, Do, BI, sh (and forms with a scope) stay, no text
        public List<GlyphLayout>? Layout;   // every character of this content, for evening out the lines that lose some (Redactor.Lines.cs)
        public ShowLayout? LastShow;        // what the last character recorded shares with the next ones
        public bool LayoutTruncated;

        /// <summary>
        /// A form on a redacted page whose own content describes marked content (/ActualText, /Alt, /E or what describes it in another form,
        /// <see cref="DescribesOldContent"/>): it is rewritten without them even where no bar reaches it (docs/ABLAUFPLAN.md, 6.3 step 5;
        /// docs/FEHLERGEDAECHTNIS.md, No. 624: every description of a redacted page goes, No. 134).
        /// </summary>
        public bool Describes;

        /// <summary>Work of its own on what is drawn (not the forms, masks and pattern cells in it).</summary>
        private bool OwnWork => Glyphs.Count > 0 || Paths.Count > 0 || Images.Count > 0 || Shadings.Count > 0 || Dropped.Count > 0 || ClipPaths.Count > 0 ||
                                Transforms.Count > 0 || Inserts.Count > 0 || ClipsBefore.Count > 0 || Keep is not null;

        public bool HasWork => Describes || OwnWork || Forms.Values.Any(f => f.HasWork) || Masks.Values.Any(m => m.Scope.HasWork) ||
                               PatternCells.Values.Any(c => c.Scope.HasWork);

        /// <summary>
        /// Whether all there is to do is to drop descriptions (here and in the forms inside): the copy is the same for every invocation, so it is
        /// made once (a form drawn thousands of times must not be copied thousands of times, No. 164).
        /// </summary>
        public bool OnlyDescriptions => HasWork && !OwnWork && Forms.Values.All(f => !f.HasWork || f.OnlyDescriptions) &&
                                        !Masks.Values.Any(m => m.Scope.HasWork) && !PatternCells.Values.Any(c => c.Scope.HasWork);

        public void Cut(int op, int element, int start, int length, double advance, double fontSize, double horizontalScale, bool vertical, Matrix textToPage)
        {
            if (!Glyphs.TryGetValue(op, out var list)) Glyphs[op] = list = [];
            var unit = vertical ? textToPage.TransformVector(0, 1) : textToPage.TransformVector(1, 0);
            var cut = new GlyphCut(element, start, length, advance, fontSize, horizontalScale, vertical, Math.Sqrt(unit.X * unit.X + unit.Y * unit.Y));
            // a run of removed characters in one string is one cut (the gap sums their advances anyway): one entry per character took
            // gigabytes for a string of 20 million characters (docs/FEHLERGEDAECHTNIS.md, No. 384)
            if (list.Count > 0 && list[^1] is var last && last.Element == element && last.Start + last.Length == start && last.FontSize == fontSize &&
                last.Scale == horizontalScale && last.Vertical == vertical && last.PagePerText == cut.PagePerText)
                list[^1] = last with { Length = last.Length + length, Advance = last.Advance + advance };
            else
                list.Add(cut);
        }
    }

    /// <summary>A removed character: where in the text operator, how far it advanced the text matrix, and how large that is in page space.</summary>
    private readonly record struct GlyphCut(int Element, int Start, int Length, double Advance, double FontSize, double Scale, bool Vertical, double PagePerText);

    /// <param name="page">What the page shows (its crop box, page space): a character reaching beyond it counts by what shows of it.</param>
    /// <param name="doc">The document (to look into forms for descriptions of marked content, <see cref="Scope.Describes"/>); null: not looked.</param>
    private sealed class Observer(List<RectD> rects, Scope root, RectD? page = null, PdfDocument? doc = null) : IScopeObserver
    {
        /// <summary>Forms already looked into for descriptions of marked content (a form drawn often is read once).</summary>
        private readonly Dictionary<PdfStream, bool> _describing = new(ReferenceEqualityComparer.Instance);

        private readonly Stack<Scope> _scopes = new([root]);
        public readonly StringBuilder Removed = new();
        public int Glyphs, Paths, Grazed;

        /// <summary>
        /// A character that grazes an area may be seen nowhere else: what shows of it beside the areas is clipped away, or something
        /// painted later lies over it (or a soft mask is in play). Only then must the redacted page be measured for characters that the
        /// bar made invisible (<see cref="RemoveHiddenIn"/> with grazing) — a character on white beside a bar stays visible, and measuring
        /// every redacted page cost the save a third more time (docs/FEHLERGEDAECHTNIS.md, No. 188).
        /// </summary>
        public bool GrazedMayHide => _mayHide || Grazed > 0 && _softMask;

        private bool _mayHide, _softMask;

        // grazing characters whose part beside the areas nothing has covered so far (bounds in page space, within the clip)
        private readonly List<RectD> _grazing = [];

        /// <summary>
        /// Something painted over <paramref name="bounds"/> after the grazing characters so far: does it lie over a quarter of one of them
        /// or more? (A grazing character lies mostly beside the areas; the neighbours in its word touch or overlap it a little, which hides
        /// nothing.)
        /// </summary>
        private void Later(RectD bounds)
        {
            if (_mayHide || _grazing.Count == 0) return;
            foreach (var g in _grazing)
                if (bounds.Intersect(g) is var i && i.Width > 0 && i.Height > 0 && i.Width * i.Height >= 0.25 * g.Width * g.Height)
                {
                    _mayHide = true;
                    _grazing.Clear();
                    return;
                }
        }

        public Scope Current => _scopes.Peek();

        private GlyphStyle _style;

        public bool WantsStyle => true;

        public void Style(in GlyphStyle style) => _style = style;

        /// <summary>
        /// A character reaching beyond what can show (beside the page, outside its clip) counts by the part that shows: under a bar along the
        /// page's edge only a sliver of it may lie, while all that anybody sees of it is under the bar — it stayed because most of it lay
        /// beside the page (docs/FEHLERGEDAECHTNIS.md, No. 258). Upright characters (the clip is a box in page space).
        /// </summary>
        private bool ShownPartCovered(RectD cell, RectD ink, Matrix cellToPage, bool invisible)
        {
            if (!AxisAligned(cellToPage)) return false;
            var whole = cellToPage.TransformBounds(invisible ? cell : ink);
            var shown = Shown(whole);
            // nothing of its shape shows: by its cell, as an invisible character (selecting shows the cell)
            if (!invisible && (shown.Width <= 0.01 || shown.Height <= 0.01)) shown = Shown(whole = cellToPage.TransformBounds(cell));
            if (shown.Width <= 0.01 || shown.Height <= 0.01 || shown.Width >= whole.Width - 0.01 && shown.Height >= whole.Height - 0.01) return false;
            return Covers(rects, shown, GlyphCoverage);

            RectD Shown(RectD r) => page is { } p ? r.Intersect(_clip).Intersect(p) : r.Intersect(_clip);
        }

        // around all areas: a character beside it is neither removed nor grazing, and is only recorded — tested first, so that a page of tens of
        // millions of characters is not measured against the areas character by character (docs/FEHLERGEDAECHTNIS.md, No. 384)
        private readonly RectD _near = Around(rects);

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (cellToPage.IsInvertible && cellToPage.TransformBounds(cell.Union(ink)) is var reach && !reach.Intersects(_near))
            {
                RecordLayout(Current, op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage, _style, false, space: text == " ",
                    ink: cellToPage.TransformBounds(ink));
                if (!hidden && !invisible) Later(reach);
                return;
            }
            // Without area (0 Tf, 0 Tz, degenerate matrix) no coverage applies; then the position of the character counts
            var removed = cellToPage.IsInvertible ? GlyphRemoved(rects, cell, ink, cellToPage, invisible: invisible) || ShownPartCovered(cell, ink, cellToPage, invisible)
                : AtOrigin(rects, cellToPage, cell);
            RecordLayout(Current, op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage, _style, removed, space: text == " ",
                ink: cellToPage.IsInvertible ? cellToPage.TransformBounds(ink) : null);
            if (!removed)
            {
                // only grazed: stays
                var bounds = cellToPage.IsInvertible ? cellToPage.TransformBounds(cell.Union(ink)) : RectD.Empty;
                if (!hidden && !invisible) Later(bounds);
                if (cellToPage.IsInvertible && Hits(rects, bounds))
                {
                    Grazed++;
                    if (hidden || invisible || _mayHide) return;
                    // what can show of it: within the clip, and not entirely under the areas
                    var shown = bounds.Intersect(_clip);
                    if (shown.Width <= 0.01 || shown.Height <= 0.01 || Covers(rects, shown, 0.999)) _mayHide = true;
                    else if (_grazing.Count < 2000) _grazing.Add(shown);
                    else _mayHide = true;
                }
                return;
            }
            Current.Cut(op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage);
            if (text is not null && Removed.Length < 100_000) Removed.Append(text);
            Glyphs++;
        }

        private RectD _clip = RectD.Infinite;

        public bool WantsClip => true;

        public void Clip(RectD bounds) => _clip = bounds;

        private (double Reach, DashPattern? Dash) _stroke;

        public bool WantsStroke => true;

        public bool WantsLineMatrix => true;

        public void LineMatrix(int op, Matrix lineMatrix) => Current.LineMatrices[op] = lineMatrix;

        public void Stroke(double reach, DashPattern? dash)
        {
            _stroke = (reach, dash);
            _pair = null;
        }

        private (double Fill, double Stroke, byte Blend, StrokeStyle Style)? _pair;

        public void FillStroke(double fillAlpha, double strokeAlpha, byte blendMode, bool pattern, StrokeStyle style) =>
            _pair = (fillAlpha < 1 || strokeAlpha < 1 || blendMode != 0) && !pattern ? (fillAlpha, strokeAlpha, blendMode, style) : null;

        public void ClipPath(int op, RectD bounds, Matrix ctm)
        {
            if (Hits(rects, bounds.Inflate(0.05))) Current.ClipPaths[op] = ctm;
        }

        // "n" without a clip under an area: it paints nothing, its coordinates go with it (docs/FEHLERGEDAECHTNIS.md, No. 449)
        public void EmptyPath(int op, RectD bounds, Matrix ctm)
        {
            if (Hits(rects, bounds.Inflate(0.05))) Current.Paths[op] = null;
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden)
        {
            if (!hidden) Later(bounds.Intersect(_clip));
            // a path without height or width (a line filled, which paints nothing) still has its points under the area: a little wider, as
            // when cutting (docs/FEHLERGEDAECHTNIS.md, No. 178)
            if (!Hits(rects, bounds.Inflate(0.05))) return;
            Current.Paths[op] = ctm;   // decided per subpath when rewriting
            Current.Strokes[op] = _stroke;
            if (_pair is { } pair) Current.TransparentPairs[op] = pair;
            // What the path paints lies inside the clip: if that part is beside all areas, nothing under them is seen — a large
            // rectangle filled with a pattern and clipped to a thin shadow was painted only outside the bars, and its pattern was
            // emptied for the page, so that the shadow vanished (docs/FEHLERGEDAECHTNIS.md, No. 162)
            var painted = bounds.Intersect(_clip);
            if (painted.Width < 0 || painted.Height < 0 || !Hits(rects, painted)) Current.PaintedOutside.Add(op);
            Paths++;
        }

        public bool WantsStrokeLine(RectD bounds) => Hits(rects, bounds.Inflate(0.05));

        // A line only stroked paints along itself, not what it encloses: the frame of a table cell with a bar inside it is beside the bar.
        // Clipped all the same, it was drawn on its own — a reader that draws like lines together then shades the pixel where two frames
        // meet differently (public test files example_006, example_048; docs/FEHLERGEDAECHTNIS.md, No. 464)
        public void StrokeLine(int op, ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, Matrix ctm, double reach)
        {
            if (!Current.Paths.ContainsKey(op) || !double.IsFinite(reach)) return;
            if (!StrokeNear(verbs, points, ctm, reach + 0.05 + Rewriter.ClipSeam, rects)) Current.PaintedOutside.Add(op);
        }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden)
        {
            var bounds = imageToPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1));
            if (!hidden) Later(bounds.Intersect(_clip));
            if (Hits(rects, bounds)) Current.Images[op] = (imageToPage, stream, inline, resources);
        }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden)
        {
            if (!hidden) Later(bounds.Intersect(_clip));
            if (Hits(rects, bounds)) Current.Shadings[op] = ctm;
        }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            var group = form.Dictionary.GetDictionary(PdfName.Get("Group"));
            var scope = new Scope
            {
                Knockout = group?.GetName(PdfName.Get("S"))?.Value == "Transparency" ? group.GetBoolean(PdfName.Get("K")) == true : Current.Knockout,
                Describes = doc is not null && (_describing.TryGetValue(form, out var known) ? known : _describing[form] = DescribesMarkedContent(doc, form)),
                ToPage = formToPage,
            };
            Current.Forms[op] = scope;
            _scopes.Push(scope);
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
            _softMask = true;
            // a description in the group's own content goes like one in a form (docs/FEHLERGEDAECHTNIS.md, No. 697)
            var scope = new Scope
            {
                Describes = doc is not null && (_describing.TryGetValue(group, out var known) ? known : _describing[group] = DescribesMarkedContent(doc, group)),
                ToPage = groupToPage,
            };
            Current.Masks[op] = (group, scope);
            _scopes.Push(scope);
        }

        // A tiling pattern a bar reaches into is redacted in its cell like a form (in a copy for this path); otherwise it would be emptied
        // for the page, also where it shows beside the bar (docs/FEHLERGEDAECHTNIS.md, No. 163)
        public bool WantsPatternCell(RectD painted) => Hits(rects, painted);

        public void BeginPatternCell(int op, bool stroke, PdfStream pattern, Matrix cellToPage)
        {
            var scope = new Scope { ToPage = cellToPage };
            Current.PatternCells[(op, stroke)] = (pattern, scope);
            _scopes.Push(scope);
        }

        public void EndForm()
        {
            if (_scopes.Count > 1) _scopes.Pop();
        }
    }

    /// <summary>Observer for sanitising: removes everything on hidden layers and/or invisible text (text rendering mode 3).</summary>
    /// <param name="outside">Also what nobody can see: entirely outside the page (<paramref name="visible"/>, the CropBox),
    /// entirely clipped away, or smaller than 0.01 pt (font size 0, Tz 0).</param>
    /// <param name="suspicious">The test of „Verborgenes schwärzen“ for text that is painted but not to be seen (<see cref="HiddenTextKind.Suspicious"/>).</param>
    /// <param name="keep">Places that stay (<see cref="Writing.CleanupKeep"/>): hidden text of their item whose cell centre lies in them is kept.</param>
    private sealed class HiddenObserver(bool layers, bool invisibleText, bool outside, RectD visible, Scope root, HiddenText.Test? suspicious = null,
        IReadOnlyList<Writing.CleanupKeep>? keep = null, bool screenLayers = false) : IScopeObserver
    {
        // A form removed entirely: its operators end up in a scope that nobody rewrites
        private readonly Stack<Scope> _scopes = new([root]);
        private RectD _clip = RectD.Infinite;
        public int Glyphs, Items;

        public Scope Current => _scopes.Peek();

        // A layer shown only on paper is not hidden: it is printed (as the display shows the page, it is)
        public bool HiddenOnlyIfUnprinted => !screenLayers;

        public bool WantsClip => outside || suspicious is not null;

        public void Clip(RectD bounds)
        {
            _clip = bounds;
            suspicious?.Clip(bounds);
        }

        public void GlyphSerial(int serial) => suspicious?.Serial(serial);

        /// <summary>Whether a place of the item that stays holds the area.</summary>
        private bool Kept(Writing.CleanupItems item, RectD area) => keep is { Count: > 0 } && keep.Any(k => k.Item == item && k.Holds(area));

        /// <summary>Lies entirely beside the page or entirely outside the clipping.</summary>
        private bool Unseen(RectD bounds) => outside && (!bounds.Intersects(visible) || !bounds.Intersects(_clip));

        /// <summary>Painted but not to be seen, as „Verborgenes schwärzen“ finds it.</summary>
        private bool Suspicious(RectD cell, RectD ink, Matrix cellToPage, bool hidden, bool invisible) =>
            suspicious?.Classify(cell, ink, cellToPage, hidden, invisible, out _) == HiddenTextKind.Suspicious;

        /// <summary>Cell smaller than 0.01 pt in one direction (or of almost no area) — about a hundredth of a screen pixel at 100 %.</summary>
        private static bool Tiny(Matrix cellToPage, RectD cell)
        {
            var (ax, ay) = cellToPage.TransformVector(cell.Width, 0);
            var (bx, by) = cellToPage.TransformVector(0, cell.Height);
            return Math.Abs(ax * by - ay * bx) < 1e-4 || Math.Sqrt(ax * ax + ay * ay) < 0.01 || Math.Sqrt(bx * bx + by * by) < 0.01;
        }

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            var at = cellToPage.TransformBounds(cell);
            if (!(layers && hidden || invisibleText && invisible && !Kept(Writing.CleanupItems.InvisibleText, at)
                  || outside && (Unseen(cellToPage.TransformBounds(cell.Union(ink))) || Tiny(cellToPage, cell)) && !Kept(Writing.CleanupItems.HiddenContent, at)
                  || Suspicious(cell, ink, cellToPage, hidden, invisible) && !Kept(Writing.CleanupItems.SuspiciousText, at))) return;
            Current.Cut(op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage);
            Glyphs++;
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) => Drop(layers && hidden || Unseen(bounds), () => Current.Paths[op] = null);

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) =>
            Drop(layers && hidden || Unseen(imageToPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1))), () => Current.Dropped.Add(op));

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) =>
            Drop(layers && hidden || outside && (bounds.IsEmpty || !bounds.Intersects(visible)), () => Current.Dropped.Add(op));

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            var scope = new Scope();
            if (layers && hidden)
            {
                Current.Dropped.Add(op);
                Items++;
            }
            else
            {
                Current.Forms[op] = scope;
            }
            _scopes.Push(scope);
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
            var scope = new Scope();
            Current.Masks[op] = (group, scope);
            _scopes.Push(scope);
        }

        public void EndForm()
        {
            if (_scopes.Count > 1) _scopes.Pop();
        }

        private void Drop(bool remove, Action drop)
        {
            if (!remove) return;
            drop();
            Items++;
        }
    }

    /// <summary>Result of <see cref="RemoveHidden"/>, <see cref="RemoveGlyphs"/> and <see cref="RemoveForms"/>: new page, removed characters and other content.</summary>
    /// <param name="Page">New page dictionary with new content and resources in memory.</param>
    /// <param name="Resources">The new resources (also in <paramref name="Page"/>).</param>
    /// <param name="RemovedGlyphs">Number of characters removed.</param>
    /// <param name="RemovedItems">Number of other items removed (paths, images, forms …).</param>
    public sealed record HiddenContentResult(PdfDictionary Page, PdfDictionary Resources, int RemovedGlyphs, int RemovedItems)
    {
        /// <summary>As <see cref="RedactionResult.ReplacedObjects"/>.</summary>
        public IReadOnlySet<int> ReplacedObjects { get; init; } = new HashSet<int>();

        /// <summary>Fonts (object numbers) that lost characters.</summary>
        public IReadOnlySet<int> TouchedFonts { get; init; } = new HashSet<int>();

        /// <summary>Text of the removed characters (editing text: to check that the right ones were removed).</summary>
        public string RemovedText { get; init; } = "";

        /// <summary>
        /// Editing text: marked content (MCID in the page content) with removed characters — its alternate texts in the structure
        /// tree belong to the old text (the writer removes them only there).
        /// </summary>
        public IReadOnlySet<int> AlternateTextMcids { get; init; } = new HashSet<int>();
    }

    /// <summary>
    /// Editing text (see KONZEPT.md, section 5.14): removes the characters <paramref name="glyphs"/> and lines
    /// <paramref name="paths"/> from the page content — as when redacting (the remaining characters keep their position, forms
    /// get a separate copy per invocation), only without bars.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="glyphs">Character numbers in content order, as <see cref="Text.TextBlocks"/> counts them.</param>
    /// <param name="paths">Numbers of painted paths in content order, counted the same way.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The page without them; <see langword="null"/> if none was found, in which case the page stays as it is.</returns>
    public static HiddenContentResult? RemoveGlyphs(PdfDocument doc, PdfPage page, IReadOnlySet<int> glyphs, IReadOnlySet<int> paths,
        CancellationToken cancel = default)
    {
        var root = new Scope();
        var observer = new OrdinalObserver(glyphs, paths, root);
        PageInterpreter.Observe(doc, page, observer, cancel);
        if (!root.HasWork) return null;
        var rewriter = new Rewriter(doc, [], page.MediaBox, AlternateTexts.Affected);
        var resources = CopyResources(page.Resources);
        byte[] body;
        using (var content = PageContent(doc, page.Dictionary)) body = rewriter.Rewrite(content.Span, root, resources);
        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), body));
        dict.Set(PdfName.Resources, resources);
        // The page's thumbnail, private data, metadata and associated files would still show the old text
        WithoutOldForms(dict);
        return new HiddenContentResult(dict, resources, observer.Glyphs, observer.Paths)
        {
            ReplacedObjects = rewriter.ReplacedObjects,
            RemovedText = observer.Removed.ToString(),
            AlternateTextMcids = rewriter.AffectedMcids,
        };
    }

    /// <summary>
    /// Takes top-level forms out of the page content (their "Do"; resources no longer invoked are dropped), so that stored
    /// edited text (<see cref="ContentElements"/>) becomes a block again on opening. A form is taken only if it draws exactly
    /// the expected text, is visible (not on a hidden layer) and no clipping cuts it; otherwise PDF Knead would show stored data
    /// instead of the content every other reader shows. Right-to-left text is compared in the order the appearance draws it
    /// (<see cref="ShownText"/>); a glyph within /ActualText stands for that text, and a glyph without a text counts only if it advances
    /// nothing (the dots and marks the appearance draws apart from their letter; docs/FEHLERGEDAECHTNIS.md, No. 1177).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="select">Gives, for a form and its mapping onto the page, the text it must show (<see cref="ShownText"/>), or null.</param>
    /// <param name="max">At most this many forms.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The page without the forms and the forms taken; <see langword="null"/> if none was found.</returns>
    public static (HiddenContentResult Result, List<(PdfStream Form, Matrix FormToPage)> Removed)? RemoveForms(PdfDocument doc, PdfPage page,
        Func<PdfStream, Matrix, ShownText?> select, int max = int.MaxValue, CancellationToken cancel = default)
    {
        var root = new Scope();
        var observer = new FormObserver(select, root, max);
        PageInterpreter.Observe(doc, page, observer, cancel);
        if (observer.Removed.Count == 0) return null;
        var rewriter = new Rewriter(doc, [], page.MediaBox, AlternateTexts.None);
        var resources = CopyResources(page.Resources);
        byte[] body;
        using (var content = PageContent(doc, page.Dictionary)) body = rewriter.Rewrite(content.Span, root, resources, pruneUnused: true);
        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources && key != ContentElements.PageKey && key != ContentElements.LegacyPageKey) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), body));
        dict.Set(PdfName.Resources, resources);
        return (new HiddenContentResult(dict, resources, 0, observer.Removed.Count) { ReplacedObjects = rewriter.ReplacedObjects }, observer.Removed);
    }

    private sealed class FormObserver(Func<PdfStream, Matrix, ShownText?> select, Scope root, int max) : IContentObserver
    {
        private int _depth;
        public readonly List<(PdfStream Form, Matrix FormToPage)> Removed = [];

        // Top-level form currently running that is a candidate: what it must show, what it draws, whether visible and unclipped
        private Candidate? _candidate;

        private sealed class Candidate(int op, PdfStream form, Matrix formToPage, ShownText expected, RectD bounds)
        {
            public readonly int Op = op;
            public readonly PdfStream Form = form;
            public readonly Matrix FormToPage = formToPage;
            public readonly ShownText Expected = expected;
            public readonly RectD Bounds = bounds;
            public readonly StringBuilder Drawn = new();
            // where each drawn glyph's text starts in Drawn (a ligature's letters may stand in either order), and where a glyph stands on
            // another baseline than the one before it (a right-to-left paragraph may break into lines only there; ShownText)
            public readonly List<int> Units = [], Lines = [];
            public double LastX = double.NaN, LastY = double.NaN, UpX, UpY, Em;
            // the marked content open within the form: an /ActualText span, or null for any other
            public readonly Stack<ActualSpan?> Actual = new();
            public bool Ok = true;
        }

        public bool WantsClip => _candidate is not null;

        public void Clip(RectD bounds)
        {
            if (_candidate is not { } c) return;
            const double tolerance = 1;
            if (bounds.Left > c.Bounds.Left + tolerance || bounds.Right < c.Bounds.Right - tolerance ||
                Math.Min(bounds.Top, bounds.Bottom) > Math.Min(c.Bounds.Top, c.Bounds.Bottom) + tolerance ||
                Math.Max(bounds.Top, bounds.Bottom) < Math.Max(c.Bounds.Top, c.Bounds.Bottom) - tolerance) c.Ok = false;
        }

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (_candidate is not { Ok: true } c) return;
            if (hidden || invisible || c.Drawn.Length > c.Expected.Length)
            {
                c.Ok = false;
                return;
            }
            // within /ActualText (a glyph the appearance draws for another letter than its text in ToUnicode — the body beh, teh and yeh
            // share): the first glyph that advances stands for the actual text — before it, a glyph that advances nothing counts as one without
            // a text of its own (a cluster of reordered Arabic marks drawn right to left starts with its marks; docs/FEHLERGEDAECHTNIS.md,
            // No. 1180) —, any further one counts as a glyph without a text of its own
            if (c.Actual.Count > 0 && c.Actual.Peek() is { } actual)
            {
                var still = Math.Abs(advance) <= 1e-3 * Math.Abs(fontSize);
                if (!actual.Used && !(still && !string.IsNullOrWhiteSpace(actual.Text)))
                {
                    actual.Used = true;
                    // an empty actual text shows nothing: its glyph is judged as one without a text
                    text = string.IsNullOrWhiteSpace(actual.Text) ? null : actual.Text;
                }
                else
                {
                    // a span whose glyphs all advance nothing takes its text at its end, placed at its first glyph
                    if (!actual.Used) actual.Pending ??= (cellToPage, textToPage, fontSize);
                    text = null;
                }
            }
            // a glyph without a text: only the dots and marks the appearance draws apart from their letter, which advance nothing (a glyph that
            // moves the text on shows a character, and one the text does not name would be seen by every reader but not come back with the
            // block; docs/FEHLERGEDAECHTNIS.md, No. 1177)
            if (text is null)
            {
                if (!(Math.Abs(advance) <= 1e-3 * Math.Abs(fontSize))) c.Ok = false;
                return;
            }
            var at = c.Drawn.Length;
            foreach (var ch in text) if (ShownText.Shown(ch)) c.Drawn.Append(ch);
            if (c.Drawn.Length > at) Placed(c, at, cellToPage, textToPage, fontSize);
        }

        public void MarkedContent(int op, PdfName tag, PdfDictionary? properties)
        {
            if (_candidate is not { } c) return;
            // nested deeper than any appearance writes: not taken (the stack stays small)
            if (c.Actual.Count >= 64) c.Ok = false;
            var text = properties?.GetString(PdfName.Get("ActualText"))?.ToText();
            c.Actual.Push(text is null ? null : new ActualSpan(text));
        }

        public void EndMarkedContent(int op)
        {
            if (_candidate is not { } c || c.Actual.Count == 0) return;
            if (c.Actual.Pop() is { Used: false, Pending: { } first } span && c.Ok)
            {
                var at = c.Drawn.Length;
                foreach (var ch in span.Text) if (ShownText.Shown(ch)) c.Drawn.Append(ch);
                if (c.Drawn.Length > at) Placed(c, at, first.CellToPage, first.TextToPage, first.FontSize);
            }
        }

        /// <summary>An /ActualText span within a candidate, whether a glyph has taken its text, and where its first glyph that advances nothing
        /// stands (it takes the text at the end of a span without a glyph that advances).</summary>
        private sealed class ActualSpan(string text)
        {
            public readonly string Text = text;
            public bool Used;
            public (Matrix CellToPage, Matrix TextToPage, double FontSize)? Pending;
        }

        /// <summary>
        /// Notes a drawn glyph's text starting at <paramref name="at"/>, and whether its origin lies off the baseline of the glyph before it by
        /// more than a tenth of the font size (measured across the text direction of that glyph; docs/FEHLERGEDAECHTNIS.md, No. 1177).
        /// </summary>
        private static void Placed(Candidate c, int at, Matrix cellToPage, Matrix textToPage, double fontSize)
        {
            var (x, y) = cellToPage.Transform(0, 0);
            if (c.Units.Count > 0)
            {
                var across = (x - c.LastX) * c.UpX + (y - c.LastY) * c.UpY;
                if (!(Math.Abs(across) <= 0.1 * c.Em)) c.Lines.Add(at);
            }
            c.Units.Add(at);
            // across the baseline: perpendicular to the text direction (a slanted text matrix of fake italic leans its y axis); the font size
            // as the height of an em in page space
            var (bx, by) = textToPage.TransformVector(1, 0);
            var length = Math.Sqrt(bx * bx + by * by);
            (c.LastX, c.LastY) = (x, y);
            (c.UpX, c.UpY) = length > 1e-12 ? (-by / length, bx / length) : (0, 1);
            c.Em = length > 1e-12 ? Math.Abs(fontSize * textToPage.Determinant) / length : 0;
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            if (_depth++ != 0 || hidden || Removed.Count >= max || select(form, formToPage) is not { } expected) return;
            var box = PdfRectangle.From(form.Dictionary.Get(PdfName.Get("BBox")));
            var bounds = box is { } b ? formToPage.TransformBounds(RectD.FromLtrb(b.Left, b.Bottom, b.Right, b.Top)) : default;
            _candidate = new Candidate(op, form, formToPage, expected, bounds);
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage) => _depth++;

        public void EndForm()
        {
            _depth = Math.Max(0, _depth - 1);
            if (_depth != 0 || _candidate is not { } c) return;
            _candidate = null;
            if (!c.Ok || !c.Expected.Matches(c.Drawn.ToString(), c.Units, c.Lines)) return;
            root.Dropped.Add(c.Op);
            Removed.Add((c.Form, c.FormToPage));
        }
    }

    /// <summary>Observer for <see cref="RemoveGlyphs"/>: counts characters and painted paths like the block recognition and cuts out the requested ones.</summary>
    private sealed class OrdinalObserver(IReadOnlySet<int> glyphs, IReadOnlySet<int> paths, Scope root) : IScopeObserver
    {
        private readonly Stack<Scope> _scopes = new([root]);
        private int _glyph, _path;
        public readonly StringBuilder Removed = new();
        public int Glyphs, Paths;

        public Scope Current => _scopes.Peek();

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (!glyphs.Contains(_glyph++)) return;
            Current.Cut(op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage);
            if (text is not null && Removed.Length < 100_000) Removed.Append(text);
            Glyphs++;
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden)
        {
            if (!paths.Contains(_path++)) return;
            Current.Paths[op] = null;   // removed entirely (an underline exists only for the text)
            Paths++;
        }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            var group = form.Dictionary.GetDictionary(PdfName.Get("Group"));
            var scope = new Scope
            {
                Knockout = group?.GetName(PdfName.Get("S"))?.Value == "Transparency" ? group.GetBoolean(PdfName.Get("K")) == true : Current.Knockout,
            };
            Current.Forms[op] = scope;
            _scopes.Push(scope);
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
            var scope = new Scope();
            Current.Masks[op] = (group, scope);
            _scopes.Push(scope);
        }

        public void EndForm()
        {
            if (_scopes.Count > 1) _scopes.Pop();
        }
    }

    /// <summary>
    /// Sanitises a page: removes the content of hidden layers and invisible text (such as the OCR text of a scan) from the page
    /// content — otherwise it stays readable in the file although nobody sees it.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="layers">Remove content on hidden layers.</param>
    /// <param name="invisibleText">Remove invisible text (text rendering mode 3).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="outside">Also remove what can never be seen: entirely outside the page, entirely clipped away, smaller than
    /// 0.01 pt, and images, forms and patterns in the resources that the content never invokes.</param>
    /// <param name="suspiciousText">Also remove text that presumably nobody sees (<see cref="Writing.HiddenContent.SuspiciousText"/>).</param>
    /// <returns>The sanitised page; <see langword="null"/> if nothing was found, in which case the page stays as it is.</returns>
    /// <param name="keep">Places of the page that stay (the user's „x“ in „Dokument bereinigen“); null: none.</param>
    /// <param name="screenLayers">Layers count as hidden as the display shows them (also those only printed), not only those never shown —
    /// for a copy without layers that looks as the display shows the page.</param>
    public static HiddenContentResult? RemoveHidden(PdfDocument doc, PdfPage page, bool layers, bool invisibleText, CancellationToken cancel = default,
        bool outside = false, bool suspiciousText = false, IReadOnlyList<Writing.CleanupKeep>? keep = null, bool screenLayers = false)
    {
        var root = new Scope();
        var crop = Writing.HiddenContent.VisibleBox(page);
        var suspicious = suspiciousText ? HiddenText.Test.For(doc, page, cancel) : null;
        var observer = new HiddenObserver(layers, invisibleText, outside, RectD.FromLtrb(crop.Left, crop.Bottom, crop.Right, crop.Top), root, suspicious, keep,
            screenLayers);
        PageInterpreter.Observe(doc, page, observer, cancel);
        if (!root.HasWork && !(outside && HasUnusedResources(doc, page))) return null;
        var rewriter = new Rewriter(doc, [], page.MediaBox);
        var resources = CopyResources(page.Resources);
        byte[] body;
        using (var content = PageContent(doc, page.Dictionary)) body = rewriter.Rewrite(content.Span, root, resources, pruneUnused: outside);
        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), body));
        dict.Set(PdfName.Resources, resources);
        // the replacement texts in the structure tree go with those in the content (all of the page, as when redacting)
        return new HiddenContentResult(dict, resources, observer.Glyphs, observer.Items) { ReplacedObjects = rewriter.ReplacedObjects, AlternateTextMcids = rewriter.AffectedMcids, TouchedFonts = rewriter.TouchedFonts };
    }

    /// <summary>
    /// „Verborgenes schwärzen“ (KONZEPT.md, section 5.18): removes the characters that nobody sees (<see cref="HiddenText"/>) whose cell
    /// centre lies in one of the areas — only those: visible content in the same areas stays, and there is no bar. Then the new page is
    /// tested once more: whatever is hidden in the areas now (visibility is measured by drawing, and a character that counted as
    /// visible because of another one lying over it may be hidden once that one is gone) is removed in a further pass, up to
    /// <see cref="MaxHiddenPasses"/> passes; if hidden text is still left after that, nothing must be saved.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="areas">The marked areas in page space.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="probe">Measures by drawing; null = <see cref="TextVisibility.DefaultProbe"/>.</param>
    /// <param name="grazing">
    /// After bars (the areas are the bars, the page is the redacted one): instead of every hidden character whose centre lies in an area,
    /// the characters that touch a bar and are painted but no longer seen — a character covered by other content that showed only a
    /// sliver where the bar now lies stayed in the file, and nobody could see it any more (docs/FEHLERGEDAECHTNIS.md, No. 172).
    /// </param>
    /// <returns>The page without them and the removed text; <see langword="null"/> if none was found, in which case the page stays as it is.</returns>
    /// <exception cref="PdfException">Hidden characters may still be in an area after the last pass, or a further pass could not measure
    /// completely; nothing must be saved then.</exception>
    public static HiddenContentResult? RemoveHiddenIn(PdfDocument doc, PdfPage page, IReadOnlyList<PdfRectangle> areas, CancellationToken cancel = default,
        IVisibilityProbe? probe = null, bool grazing = false)
    {
        var rects = areas.Select(a => RectD.FromLtrb(a.Left, a.Bottom, a.Right, a.Top)).ToList();
        var index = new AreaIndex(rects);
        var current = page;
        HiddenContentResult? result = null;
        var replaced = new HashSet<int>();
        var touched = new HashSet<int>();
        var mcids = new HashSet<int>();
        var removed = new StringBuilder();
        var glyphs = 0;
        for (var pass = 0; ; pass++)
        {
            // A fresh test of the current page (its display list, its clips)
            var root = new Scope();
            // grazing: only the characters at the bars matter
            var test = HiddenText.Test.For(doc, current, cancel, probe, grazing ? rects : null);
            var observer = new HiddenInObserver(test, index, root, grazing);
            PageInterpreter.Observe(doc, current, observer, cancel);
            if (!root.HasWork) break;
            // a further pass that ran out of time: what became hidden through the passes before cannot be told from what is seen, and
            // hidden text would stay in the marked areas without anyone knowing (docs/FEHLERGEDAECHTNIS.md, No. 166) — as when the
            // passes run out; an incomplete first pass the search has already reported
            if (pass == MaxHiddenPasses || pass > 0 && !test.Complete)
                throw new PdfException(Tr.T("Der verborgene Inhalt ließ sich auf Seite {0} nicht vollständig entfernen — es wurde nichts gespeichert.", page.Index + 1));
            // The gap is coarsened as under a bar: the hidden text was marked as secret, and its exact width would reveal the word. Not when
            // grazing: that cleans up after a redaction that was checked as it stands — what stays keeps its exact place, a gap evened out
            // there moved characters of a slanted line into a bar, and nothing checked it any more (docs/FEHLERGEDAECHTNIS.md, No. 377).
            // Only the alternate texts of marked content that loses characters go; those of visible content stay
            var rewriter = new Rewriter(doc, [], page.MediaBox, AlternateTexts.Affected, coarseGaps: !grazing);
            var resources = CopyResources(current.Resources);
            byte[] body;
            using (var content = PageContent(doc, current.Dictionary)) body = rewriter.Rewrite(content.Span, root, resources);
            var dict = new PdfDictionary(current.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
            foreach (var (key, value) in current.Dictionary.Entries)
                if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
            dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), body));
            dict.Set(PdfName.Resources, resources);
            WithoutOldForms(dict);
            replaced.UnionWith(rewriter.ReplacedObjects);
            touched.UnionWith(rewriter.TouchedFonts);
            mcids.UnionWith(rewriter.AffectedMcids);
            glyphs += observer.Glyphs;
            if (removed.Length < 100_000) removed.Append(observer.Removed);
            current = new PdfPage(page.Index, dict, null, resources, page.MediaBox, page.CropBox, page.Rotate);
            result = new HiddenContentResult(dict, resources, glyphs, 0) { ReplacedObjects = replaced, RemovedText = removed.ToString(), AlternateTextMcids = mcids, TouchedFonts = touched };
            // the first measurement hit its limit: what it could not decide counts as visible, a further pass would only spend the time
            // again (the search reported the page as not checked completely)
            if (pass == 0 && !test.Complete) break;
        }
        // after bars: the page is checked again as after redacting — no character may lie mostly under a bar (Verify)
        if (grazing && result is not null) Verify(doc, page, result.Page, result.Resources, rects);
        return result;
    }

    /// <summary>Passes of <see cref="RemoveHiddenIn"/>: the first removes what was found, the others what only became hidden by it.</summary>
    private const int MaxHiddenPasses = 3;

    /// <summary>Observer for <see cref="RemoveHiddenIn"/>: cuts hidden characters in the areas; paths, images and forms stay.</summary>
    private sealed class HiddenInObserver(HiddenText.Test test, AreaIndex areas, Scope root, bool grazing) : IScopeObserver
    {
        private readonly Stack<Scope> _scopes = new([root]);
        public readonly StringBuilder Removed = new();
        public int Glyphs;
        private GlyphStyle _style;

        public Scope Current => _scopes.Peek();

        public bool WantsClip => true;

        public bool HiddenOnlyIfUnprinted => true;

        public bool WantsStyle => true;

        public void Style(in GlyphStyle style) => _style = style;

        public void Clip(RectD bounds) => test.Clip(bounds);

        public void GlyphSerial(int serial) => test.Serial(serial);

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            var kind = test.Classify(cell, ink, cellToPage, hidden, invisible, out var bounds, op, start);
            // grazing a bar: only what is painted but no longer seen (text of other kinds stays as the bar left it)
            var removed = kind is not null && !(grazing ? kind != HiddenTextKind.Suspicious || !Hits(areas, cellToPage.TransformBounds(cell.Union(ink))) : !HiddenText.Within(areas, bounds));
            // the line of a removed character is evened out like under a bar (Redactor.Lines.cs); invisible text (OCR) moves nobody
            RecordLayout(Current, op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage, _style, removed,
                evens: kind != HiddenTextKind.Invisible && !grazing, space: text == " ", ink: cellToPage.IsInvertible ? cellToPage.TransformBounds(ink) : null);
            if (!removed) return;
            Current.Cut(op, element, start, length, advance, fontSize, horizontalScale, vertical, textToPage);
            if (Removed.Length < 100_000) Removed.Append(text);
            Glyphs++;
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            var group = form.Dictionary.GetDictionary(PdfName.Get("Group"));
            var scope = new Scope
            {
                Knockout = group?.GetName(PdfName.Get("S"))?.Value == "Transparency" ? group.GetBoolean(PdfName.Get("K")) == true : Current.Knockout,
            };
            Current.Forms[op] = scope;
            _scopes.Push(scope);
            test.BeginNested();
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
            var scope = new Scope();
            Current.Masks[op] = (group, scope);
            _scopes.Push(scope);
            test.BeginNested();
        }

        // Hidden text in the cell of a tiling pattern is removed in a copy of the pattern for this path, as under a bar
        // (docs/FEHLERGEDAECHTNIS.md, No. 163 and 233)
        public bool WantsPatternCell(RectD painted) => Hits(areas, painted.Inflate(HiddenText.MinMarkSize));

        public void BeginPatternCell(int op, bool stroke, PdfStream pattern, Matrix cellToPage)
        {
            var scope = new Scope();
            Current.PatternCells[(op, stroke)] = (pattern, scope);
            _scopes.Push(scope);
            test.BeginCell(pattern);
        }

        public void EndForm()
        {
            if (_scopes.Count > 1) _scopes.Pop();
            test.End();
        }
    }

    /// <summary>
    /// Only counts what <see cref="RemoveHidden"/> with <c>outside</c> would remove (check in the "sanitise document" window):
    /// characters, other content and whether unused resources exist.
    /// </summary>
    internal static (int Glyphs, int Items, bool UnusedResources) CountUnseen(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        var crop = Writing.HiddenContent.VisibleBox(page);
        var observer = new HiddenObserver(false, false, true, RectD.FromLtrb(crop.Left, crop.Bottom, crop.Right, crop.Top), new Scope());
        PageInterpreter.Observe(doc, page, observer, cancel);
        return (observer.Glyphs, observer.Items, HasUnusedResources(doc, page));
    }

    /// <summary>Whether the page's resources contain images, forms or patterns that its content never invokes (readable in the file, never visible).</summary>
    private static bool HasUnusedResources(PdfDocument doc, PdfPage page)
    {
        if (page.Resources is not { } resources) return false;
        var xobjects = resources.GetDictionary(PdfName.Get("XObject"));
        var patterns = resources.GetDictionary(PdfName.Get("Pattern"));
        if (xobjects is not { Count: > 0 } && patterns is not { Count: > 0 }) return false;
        using var content = PageContent(doc, page.Dictionary);
        var (usedXObjects, usedPatterns, _) = ResourceUsage.Names(doc, content.Span.ToArray(), resources);
        return xobjects?.Entries.Any(e => !usedXObjects.Contains(e.Key)) == true || patterns?.Entries.Any(e => !usedPatterns.Contains(e.Key)) == true;
    }

    // ------------------------------------------------------------ Rewriting

    /// <summary>Which alternate texts (/ActualText, /Alt, /E) of marked content the rewriting removes.</summary>
    private enum AlternateTexts
    {
        /// <summary>All on the page (redacting, sanitising: the whole page counts as affected).</summary>
        All,

        /// <summary>Only those of marked content from which characters are removed (editing text, hidden text).</summary>
        Affected,

        /// <summary>None (taking stored edited text out on opening).</summary>
        None,
    }

    /// <param name="coarseGaps">Coarsen the gaps of removed characters even without bars (hidden text marked for redaction); with
    /// bars they always are.</param>
    /// <param name="copyUnchanged">Editing images, drawings and shadings: the operators the plan does not name are copied as they are, and
    /// the fonts stay (no characters are removed).</param>
    private sealed partial class Rewriter(PdfDocument doc, List<RectD> rects, PdfRectangle mediaBox, AlternateTexts alternates = AlternateTexts.All,
        bool coarseGaps = false, bool hideLengths = false, IReadOnlyList<double>? frozen = null, bool copyUnchanged = false)
    {
        /// <summary>The pieces the bars grow by where the lengths of redacted words are hidden (page space; Redactor.Widen.cs), each with the
        /// baseline of its line.</summary>
        public List<(RectD Rect, double Line)> BarExtensions { get; } = [];

        /// <summary>Gaps whose length could only partly be hidden (not enough room in their line).</summary>
        public int LengthsPartlyShown { get; private set; }

        private int _depth;

        /// <summary>MCIDs of the marked content in the page content (not in forms) whose alternate texts were removed.</summary>
        public HashSet<int> AffectedMcids { get; } = [];
        private static readonly HashSet<string> PathConstruction = ["m", "l", "c", "v", "y", "h", "re"];
        private static readonly HashSet<string> PathPainting = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n"];

        /// <summary>
        /// Operators that leave a path under construction as it is, for the display (they are written before the construction, which is
        /// written with its painting): text, colour, line style, graphics state parameters, marked content. Also "q": the current path is not
        /// part of the graphics state — drawing programs write „path … 2 j q 2 w S Q“ (the painting inside its own block; taken as a broken
        /// path, its painting went lost, docs/FEHLERGEDAECHTNIS.md, No. 262). And "cm" and "Q": the display keeps the construction in user
        /// space and maps it with the matrix in effect when it is painted, so written after them it lands where the display draws it; taken
        /// as broken, the construction stayed in the file unchanged under the bar and its painting was dropped (No. 414).
        /// </summary>
        private static readonly HashSet<string> PathNeutral =
        [
            "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts", "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "\"",
            "g", "G", "rg", "RG", "k", "K", "cs", "CS", "sc", "SC", "scn", "SCN", "w", "J", "j", "M", "d", "ri", "i", "gs", "q",
            "cm", "Q", "BDC", "BMC", "EMC", "MP", "DP", "BX", "EX",
        ];
        private static readonly PdfName XObjectName = PdfName.Get("XObject");

        /// <summary>
        /// Painting that leaves a path under construction as it is, for the display: a shading, an image (as XObject or inline). Before
        /// them the construction is not broken off — it was written out before them, the clip redacting writes around a shading or an image
        /// took it in (thousands of subpaths in one clip made the check take minutes), and the painting after them lost its path; the path
        /// had to go uncut, the redaction failed (docs/FEHLERGEDAECHTNIS.md, No. 913). A form is not one of them: its content goes on with
        /// the path.
        /// </summary>
        private static bool KeepsPath(string op, IReadOnlyList<Operand> operands, PdfDictionary resources) =>
            op is "sh" or "BI" || op == "Do" && operands.Count >= 1 && operands[0].Name is { } name
                && resources.GetDictionary(XObjectName)?.Get(name) is PdfStream xobject && xobject.Dictionary.GetName(PdfName.Subtype)?.Value == "Image";
        private static readonly PdfName ExtGStateName = PdfName.Get("ExtGState"), SMaskName = PdfName.Get("SMask"), ShadingName = PdfName.Get("Shading");
        private int _names;

        public int BlankedImages { get; private set; }

        /// <summary>
        /// Rewrites a content stream; new images and forms go into <paramref name="resources"/> (a copy). <paramref name="pruneUnused"/>:
        /// all images, forms and patterns that the new content does not invoke are dropped from the resources (copies for forms, sanitising).
        /// </summary>
        // the resources of the page rewritten (the copy new images go into): a form, pattern or glyph without its own draws with them, wherever it
        // is invoked (ISO 32000-2, 7.8.3, sentence 18; docs/FEHLERGEDAECHTNIS.md, No. 791) — as the display reads it
        private PdfDictionary? _pageResources;

        public byte[] Rewrite(ReadOnlySpan<byte> content, Scope scope, PdfDictionary resources, bool pruneUnused = false)
        {
            var top = _depth++ == 0;
            if (top) _pageResources = resources;
            try
            {
                var body = RewriteCore(content, scope, resources, pruneUnused, top);
                if (scope.Paths.Count > 0) CutMeshPatterns(content, scope, resources);
                return body;
            }
            finally
            {
                _depth--;
            }
        }

        private static readonly PdfName McidName = PdfName.Get("MCID");

        /// <summary>
        /// The shading patterns with a mesh (types 4–7) that a path reaching under the bars paints with, cut at the bars in
        /// <paramref name="resources"/> (docs/FEHLERGEDAECHTNIS.md, No. 701): the path is cut there already, but the pattern kept all its data.
        /// Replaced under the same name — every other path of this content painting with it lies beside the bars and needs nothing of what
        /// went; nothing left beside them: an empty pattern. Pattern space: the pattern's /Matrix in the default space of this content.
        /// </summary>
        private void CutMeshPatterns(ReadOnlySpan<byte> content, Scope scope, PdfDictionary resources)
        {
            if (resources.GetDictionary(PatternName) is not { } patterns) return;
            var names = new HashSet<PdfName>();
            var paint = new ResourceUsage.PatternState();
            var parser = new ContentParser(content.ToArray());
            var index = 0;
            while (parser.Next() is { } op)
            {
                var number = index++;
                paint.Apply(op, parser.Operands);
                if (!scope.Paths.ContainsKey(number)) continue;
                if (paint.FillPattern(op) is { } fill) names.Add(fill);
                if (paint.StrokePattern(op) is { } stroke) names.Add(stroke);
            }
            PdfDictionary? changed = null;
            foreach (var name in names)
            {
                if (patterns.Get(name) is not PdfDictionary pattern || pattern.GetInteger(PdfName.Get("PatternType")) != 2) continue;
                var toPage = (Matrix.FromArray(pattern.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity) * scope.ToPage;
                if (MeshCut.Cut(doc, pattern.Get(PdfName.Get("Shading")), resources, toPage, rects) is not { } cut) continue;
                if (patterns.GetRaw(name) is PdfReference original) ReplacedObjects.Add(original.Id.Number);
                if (pattern.GetRaw(PdfName.Get("Shading")) is PdfReference shading) ReplacedObjects.Add(shading.Id.Number);
                PdfObject replacement;
                if (cut.Shading is { } cutShading)
                {
                    var copy = new PdfDictionary { Resolver = pattern.Resolver };
                    foreach (var (key, value) in pattern.Entries)
                        if (!DescribesOld(key)) copy.Set(key, value);
                    copy.Set(PdfName.Get("Shading"), cutShading);
                    replacement = copy;
                }
                else replacement = EmptyPattern(pattern);
                changed ??= CopyOf(patterns);
                changed.Set(name, replacement);
            }
            if (changed is not null) resources.Set(PatternName, changed);
        }

        /// <summary>
        /// Marked content sections (number of their BDC) in which something is removed — a character, or a form in which one is
        /// removed; nested sections include the outer ones.
        /// </summary>
        private static HashSet<int> AffectedSections(ReadOnlySpan<byte> content, Scope scope)
        {
            var affected = new HashSet<int>();
            var open = new List<int>();   // BDC numbers, -1 for BMC (without properties)
            var parser = new ContentParser(content.ToArray());
            var index = 0;
            while (parser.Next() is { } op)
            {
                var number = index++;
                switch (op)
                {
                    case "BDC":
                        open.Add(number);
                        break;
                    case "BMC":
                        open.Add(-1);
                        break;
                    case "EMC" when open.Count > 0:
                        open.RemoveAt(open.Count - 1);
                        break;
                    default:
                        if (open.Count > 0 && (scope.Glyphs.ContainsKey(number) || scope.Forms.TryGetValue(number, out var form) && form.HasWork))
                            foreach (var section in open)
                                if (section >= 0) affected.Add(section);
                        break;
                }
            }
            return affected;
        }

        private byte[] RewriteCore(ReadOnlySpan<byte> content, Scope scope, PdfDictionary resources, bool pruneUnused, bool top)
        {
            var affected = alternates == AlternateTexts.Affected ? AffectedSections(content, scope) : null;
            var lines = coarseGaps || rects.Count > 0 ? LineWriter.For(content.ToArray(), scope, hideLengths && rects.Count > 0 ? rects : null, frozen) : null;
            if (lines is not null)
            {
                BarExtensions.AddRange(lines.Widening.Extensions);
                LengthsPartlyShown += lines.Widening.PartlyShown;
            }
            var main = new MemoryStream(content.Length + 64);
            var output = main;
            // Operators between a path's construction and its painting that change nothing about the path (text, colours, line
            // style, marked content): kept back and written before the path, so that the painting stays with its path — a text object
            // inside a rectangle's construction made the rectangle's stroke go with the next one, in the next colour
            // (docs/FEHLERGEDAECHTNIS.md, No. 153)
            var deferred = new MemoryStream();
            var bytes = content.ToArray();
            var parser = new ContentParser(bytes);
            var path = new MemoryStream();   // path construction up to the painting operator (for clipping out)
            // Subpaths in it (from m or re): start within the path construction and bounds in user space (without a list per subpath)
            var subpaths = new List<(int Start, RectD Bounds, bool Empty)>();
            var clipPending = false;
            var (clipStart, clipEnd) = (-1, -1);   // where the "W"/"W*" of a path stands in its construction
            var index = 0;
            var replaced = new HashSet<PdfName>();   // images and forms that were replaced or dropped here
            var replacedStates = new HashSet<PdfName>();   // graphics states whose soft mask was replaced here by a redacted copy
            var paint = new ResourceUsage.PatternState();
            // a moved block: the move of its patterns (default space of this stream) while it lasts, with q/Q
            Matrix? heldPatterns = null;
            var patternStack = new Stack<Matrix?>();
            var tainted = new HashSet<PdfName>();    // patterns with text or images under an area
            // Text as a clip (modes 4–7, as the display has them: scope.ClipGlyphs): if every character of a text object that adds to the
            // clip goes, the clip must become empty — readers differ on a text clip without characters (some clip nothing), and what was
            // shown through the letters would appear around them (docs/FEHLERGEDAECHTNIS.md, No. 152 and 426)
            // Page space: how far the text after the gaps written so far in this line lands from where it was — carried across text
            // operators until the line is positioned anew, so that one character per operator does not add up the rounding
            // (docs/FEHLERGEDAECHTNIS.md, No. 154)
            var lineCarry = 0.0;
            bool clipEmptied = false, clipKept = false;
            // Editing (moving and deleting images, drawings, shadings): every operator the plan does not name stays as it was — its bytes
            // are copied instead of being written anew. A plan has millions of operators; writing them all anew took more than a second per
            // step (docs/FEHLERGEDAECHTNIS.md, No. 255). A path the plan names is read again from its construction (the rewriter needs it
            // whole), a moved block (q … Q) goes through the rewriter as a whole (its patterns move along)
            var edited = copyUnchanged && lines is null && affected is null ? Named(scope) : null;
            var copyFrom = -1;   // the original bytes from here on are still to be copied (-1: none)
            var end = 0;         // where the operator read last ends
            var (pathIndex, pathByte) = (-1, -1);   // where the construction of the path read so far began (copied as it is)
            var pathColours = paint.Colours;
            var rereadUntil = -1;   // operators up to this number go through the rewriter (a path read again)
            var blockDepth = 0;     // within a moved block: its depth of q
            while (parser.Next() is { } op)
            {
                var number = index++;
                var operands = parser.Operands;
                var begin = end;
                end = parser.Position;
                // an operator the display ignores for want of operands is not written again: it shows nothing, and a reference reader
                // stopped drawing the page at it — with the bars written after it (docs/FEHLERGEDAECHTNIS.md, No. 416)
                if (PageInterpreter.LacksOperands(op, operands))
                {
                    if (copyFrom >= 0)
                    {
                        CopyOriginal(main, bytes, ref copyFrom, begin);
                        copyFrom = end;
                    }
                    continue;
                }
                if (edited is not null && blockDepth == 0 && number > rereadUntil && path.Length == 0)
                {
                    // within a form being rewritten, every "Do" is checked (it may invoke the form again: the original would come back)
                    if (!edited.Contains(number) && !(op == "Do" && _rewriting.Count > 0))
                    {
                        if (PathConstruction.Contains(op) || op is "W" or "W*")
                        {
                            if (pathIndex < 0) (pathIndex, pathByte, pathColours) = (number, begin, paint.Colours);
                        }
                        else if (pathIndex >= 0 && !PathNeutral.Contains(op) && !KeepsPath(op, operands, resources))
                        {
                            pathIndex = -1;   // painted (or broken off): it stays as it was
                        }
                        paint.Apply(op, operands);
                        if (copyFrom < 0) copyFrom = begin;
                        continue;
                    }
                    if (pathIndex >= 0 && (PathPainting.Contains(op) || KeepsPath(op, operands, resources)))
                    {
                        // the painting of a named path: its construction is read again, now through the rewriter (also a shading or image
                        // the plan names within a construction: the rewriter keeps the construction past it, No. 913)
                        CopyOriginal(main, bytes, ref copyFrom, pathByte);
                        paint.Colours = pathColours;
                        rereadUntil = number;
                        (index, end) = (pathIndex, pathByte);
                        parser.Rewind(pathByte);
                        pathIndex = -1;
                        continue;
                    }
                    CopyOriginal(main, bytes, ref copyFrom, begin);
                    pathIndex = -1;
                    if (op == "q" && scope.Transforms.ContainsKey(number)) blockDepth = 1;
                }
                else if (blockDepth > 0)
                {
                    if (op == "q") blockDepth++;
                    else if (op == "Q") blockDepth--;
                }
                paint.Apply(op, operands);
                if (scope.Inserts.TryGetValue(number, out var inserts)) WriteInserts(main, resources, inserts);

                if (PathConstruction.Contains(op) || op is "W" or "W*")
                {
                    if (op is "W" or "W*")
                    {
                        clipPending = true;
                        clipStart = (int)path.Length;
                    }
                    if (op is "m" or "re" || subpaths.Count == 0) subpaths.Add(((int)path.Length, default, true));
                    // a path begun without m: written with the m the display assumes, so that a cut starts its pieces where the path does —
                    // they started at the origin of the page, with a line from there (docs/FEHLERGEDAECHTNIS.md, No. 428)
                    if (op is not ("m" or "re") && scope.PathStarts.TryGetValue(number, out var implicitStart))
                    {
                        Extend(subpaths, implicitStart.X, implicitStart.Y);
                        WriteNumber(path, implicitStart.X);
                        path.WriteByte((byte)' ');
                        WriteNumber(path, implicitStart.Y);
                        path.Write(" m\n"u8);
                    }
                    if (op == "re" && operands.Count >= 4)
                    {
                        double rx = operands[0].Number, ry = operands[1].Number, rw = operands[2].Number, rh = operands[3].Number;
                        Extend(subpaths, rx, ry);
                        Extend(subpaths, rx + rw, ry + rh);
                    }
                    else
                    {
                        for (var k = 0; k + 1 < operands.Count; k += 2) Extend(subpaths, operands[k].Number, operands[k + 1].Number);
                    }
                    WriteOperator(path, operands, op);
                    if (op is "W" or "W*") clipEnd = (int)path.Length;
                    continue;
                }
                if (PathPainting.Contains(op))
                {
                    main.Write(deferred.GetBuffer(), 0, (int)deferred.Length);
                    deferred.SetLength(0);
                    output = main;
                    if (scope.Keep is { } keptPaths && !keptPaths.Contains(number))
                    {
                        // copying chosen objects: a path not chosen goes; a clip it sets stays (without painting)
                        if (clipPending)
                        {
                            output.Write(path.GetBuffer(), 0, (int)path.Length);
                            output.Write("n\n"u8);
                        }
                        path.SetLength(0);
                        subpaths.Clear();
                        clipPending = false;
                        (clipStart, clipEnd) = (-1, -1);
                        continue;
                    }
                    if (scope.Paths.TryGetValue(number, out var keep))
                    {
                        if (keep is { } ctm && !clipPending)
                        {
                            // Per subpath: mostly covered ones are dropped (such as a glyph converted to curves), the rest stay
                            // together in one painting operator (holes as in an "O" stay holes). If one touches the areas, paint
                            // only outside them. A pattern used as colour would stay entirely in the file, so a pattern with
                            // text or images in it becomes empty for this page
                            var kept = new List<int>();
                            var touched = false;
                            for (var s = 0; s < subpaths.Count; s++)
                            {
                                if (subpaths[s].Empty)
                                {
                                    kept.Add(s);
                                    continue;
                                }
                                // a line along an axis has no height (a point no size at all): a little wider, so that it is seen to lie under an area
                                var bounds = ctm.TransformBounds(subpaths[s].Bounds).Inflate(0.05);
                                if (!Hits(rects, bounds))
                                {
                                    kept.Add(s);
                                }
                                else if (!DroppedWhole(rects, bounds, Coverage(rects, bounds)))
                                {
                                    kept.Add(s);
                                    touched |= !scope.PaintedOutside.Contains(number);
                                }
                            }
                            if (kept.Count > 0)
                            {
                                // a tiling pattern whose cell was redacted paints from a copy; one that could not be (several tiles,
                                // uncoloured) becomes empty for the page
                                var fillCell = CellCopy(scope, number, stroke: false);
                                var strokeCell = CellCopy(scope, number, stroke: true);
                                if (touched)
                                {
                                    if (fillCell is null && paint.FillPattern(op) is { } fill) tainted.Add(fill);
                                    if (strokeCell is null && paint.StrokePattern(op) is { } stroke) tainted.Add(stroke);
                                }
                                var wrap = touched || fillCell is not null || strokeCell is not null;
                                if (wrap) output.Write("q\n"u8);
                                // Not in a knockout group: there a reference reader blends a clipped element with the group's content beneath
                                // instead of knocking it out (ISO 32000-2, 11.4.6 knows no such difference; docs/FEHLERGEDAECHTNIS.md, No. 417).
                                // The path is cut at the areas anyway (WriteCut), the clip only adds to it
                                if (touched && !scope.Knockout) WriteComplementClip(output, ctm);
                                if (fillCell is not null)
                                {
                                    output.Write("/Pattern cs "u8);
                                    WriteResource(output, resources, PatternName, fillCell, " scn\n"u8);
                                }
                                if (strokeCell is not null)
                                {
                                    output.Write("/Pattern CS "u8);
                                    WriteResource(output, resources, PatternName, strokeCell, " SCN\n"u8);
                                }
                                WriteCut(output, resources, path, subpaths, kept, ctm, op, operands, scope.Strokes.GetValueOrDefault(number),
                                    scope.TransparentPairs.TryGetValue(number, out var pair) ? pair : null);
                                if (wrap) output.Write("Q\n"u8);
                            }
                        }
                        else
                        {
                            // Remove: entirely — as "path n" its coordinates would still be in the file (such as text converted
                            // to curves). Only a clipping it sets must stay: then without painting
                            if (clipPending)
                            {
                                output.Write(path.GetBuffer(), 0, (int)path.Length);
                                output.Write("n\n"u8);
                            }
                        }
                    }
                    else if (clipPending && op == "n" && scope.ClipPaths.TryGetValue(number, out var clipCtm))
                    {
                        // a clip that paints nothing: its shape under the areas goes as for a fill — the clip changes only there, where
                        // everything is redacted anyway (docs/FEHLERGEDAECHTNIS.md, No. 168)
                        WriteClipCut(output, path, subpaths, clipCtm);
                        WriteOperator(output, operands, op);
                    }
                    else
                    {
                        // The path stays, but the cell of its pattern lost hidden text („Verborgenes schwärzen“, docs/FEHLERGEDAECHTNIS.md,
                        // No. 233): it paints from the copy, in q … Q. With a clip it sets, that would end at Q — then the original
                        // stays, and the check after the pass refuses to save. A moved drawing is wrapped the same way, with its matrix
                        if (clipPending && clipStart >= 0 && scope.Transforms.TryGetValue(number, out var shapeMove))
                        {
                            // A moved path that also clips: the painted shape moves, the clip stays where it was (it holds for what
                            // follows in the content) — first the shape without the "W" in q … Q with the move (a clip takes effect only
                            // after the painting, so it never cut its own shape), then the construction with "W n"
                            output.Write("q\n"u8);
                            WriteMatrix(output, shapeMove);
                            WritePatternColour(output, resources, paint, op, stroke: false, null, Along(scope.PatternMoves.GetValueOrDefault(number), heldPatterns), null);
                            WritePatternColour(output, resources, paint, op, stroke: true, null, Along(scope.PatternMoves.GetValueOrDefault(number), heldPatterns), null);
                            output.Write(path.GetBuffer(), 0, clipStart);
                            output.Write(path.GetBuffer(), clipEnd, (int)path.Length - clipEnd);
                            WriteOperator(output, operands, op);
                            output.Write("Q\n"u8);
                            output.Write(path.GetBuffer(), 0, (int)path.Length);
                            output.Write("n\n"u8);
                            path.SetLength(0);
                            subpaths.Clear();
                            clipPending = false;
                            (clipStart, clipEnd) = (-1, -1);
                            continue;
                        }
                        var fillCell = clipPending ? null : CellCopy(scope, number, stroke: false);
                        var strokeCell = clipPending ? null : CellCopy(scope, number, stroke: true);
                        var moved = clipPending ? null : scope.Transforms.TryGetValue(number, out var m) ? m : (Matrix?)null;
                        var clipsBefore = clipPending ? null : ClipsBefore(scope, number);
                        // its patterns move along: with its own move, or as part of a moved block (a path that also clips there sets
                        // the moved pattern and the original again after it, as q … Q would end its clip)
                        var patterns = Along(moved is not null ? scope.PatternMoves.GetValueOrDefault(number) : null, heldPatterns);
                        var wrap = fillCell is not null || strokeCell is not null || moved is not null || clipsBefore is not null;
                        var restore = wrap ? null : new MemoryStream();
                        if (wrap) output.Write("q\n"u8);
                        if (moved is { } matrix) WriteMatrix(output, matrix);
                        foreach (var clip in clipsBefore ?? []) WriteClip(output, clip);
                        WritePatternColour(output, resources, paint, op, stroke: false, fillCell, patterns, restore);
                        WritePatternColour(output, resources, paint, op, stroke: true, strokeCell, patterns, restore);
                        output.Write(path.GetBuffer(), 0, (int)path.Length);
                        WriteOperator(output, operands, op);
                        if (wrap) output.Write("Q\n"u8);
                        else output.Write(restore!.GetBuffer(), 0, (int)restore.Length);
                    }
                    path.SetLength(0);
                    subpaths.Clear();
                    clipPending = false;
                    (clipStart, clipEnd) = (-1, -1);
                    continue;
                }
                // an operator the display does not know it ignores — also between the construction of a path and its painting, which then
                // still paints: written out here, the path lost its painting (a page-filling shading went white; docs/FEHLERGEDAECHTNIS.md, No. 454)
                if (path.Length > 0 && (PathNeutral.Contains(op) || KeepsPath(op, operands, resources) || !PageInterpreter.Operators.Contains(op)))
                {
                    output = deferred;
                }
                else if (path.Length > 0)
                {
                    // Path construction without a painting operator (broken): leave it as it was, with what came in between before it — that
                    // may hold a shading or image with a clip of its own, which must not take the construction in (No. 913)
                    main.Write(deferred.GetBuffer(), 0, (int)deferred.Length);
                    main.Write(path.GetBuffer(), 0, (int)path.Length);
                    deferred.SetLength(0);
                    output = main;
                    path.SetLength(0);
                    subpaths.Clear();
                    clipPending = false;
                    (clipStart, clipEnd) = (-1, -1);
                }

                if (PageInterpreter.StartsLine(op, operands)) lineCarry = 0;
                switch (op)
                {
                    case "q":
                        if (patternStack.Count < 4096) patternStack.Push(heldPatterns);
                        if (scope.Transforms.TryGetValue(number, out var blockMove))
                        {
                            // a moved block (an image or drawing with its clip): the matrix right after its "q"
                            heldPatterns = Along(scope.PatternMoves.GetValueOrDefault(number), heldPatterns);
                            WriteOperator(output, operands, op);
                            WriteMatrix(output, blockMove);
                            continue;
                        }
                        break;
                    case "Q":
                        if (patternStack.Count > 0) heldPatterns = patternStack.Pop();
                        break;
                    case "BT":
                        clipEmptied = clipKept = false;
                        break;
                    case "Tj" or "TJ" or "'" or "\"" when scope.ClipGlyphs.TryGetValue(number, out var clipGlyphs):
                        // by the characters with an outline, as the display builds the clip: a space left over adds nothing, a Type 3
                        // glyph never did (docs/FEHLERGEDAECHTNIS.md, No. 441)
                        // the removed characters are runs of bytes (an element, its first byte and length); both lists in the order of the
                        // string, walked once — a string of millions of characters in clip mode stays linear
                        var runs = scope.Glyphs.TryGetValue(number, out var removed)
                            ? removed.OrderBy(c => c.Element).ThenBy(c => c.Start).ToList() : [];
                        var at = 0;
                        foreach (var (element, start) in clipGlyphs)
                        {
                            while (at < runs.Count && (runs[at].Element < element || runs[at].Element == element && runs[at].Start + runs[at].Length <= start)) at++;
                            if (at < runs.Count && runs[at].Element == element && start >= runs[at].Start) clipEmptied = true;
                            else clipKept = true;
                        }
                        break;
                    case "ET" when clipEmptied && !clipKept:
                        WriteOperator(output, operands, op);
                        output.Write("0 0 0 0 re W n\n"u8);
                        continue;
                }
                // copying chosen objects (B7): text goes, and every image, form and shading not chosen (a form holding chosen ones has a scope)
                if (scope.Keep is { } keptHere)
                {
                    if (op is "Tj" or "TJ" or "'" or "\"") continue;
                    if (op is "Do" or "BI" or "sh" && !keptHere.Contains(number) && !scope.Forms.ContainsKey(number)) continue;
                }
                // lines that lose characters: written anew, so that their spacing tells nothing of them (Redactor.Lines.cs)
                if (lines is not null && lines.Handle(output, number, op, operands)) continue;
                if (scope.Glyphs.TryGetValue(number, out var cuts) && op is "Tj" or "TJ" or "'" or "\"")
                {
                    WriteShowText(output, op, operands, cuts, coarse: coarseGaps || rects.Count > 0, ref lineCarry);
                    continue;
                }
                if (op is "Do" or "sh" or "BI" && !scope.Dropped.Contains(number) && !scope.Images.ContainsKey(number) && !scope.Forms.ContainsKey(number) &&
                    !scope.Shadings.ContainsKey(number))
                {
                    // a moved image (or shading) without a block of its own: wrapped with its matrix; a stencil mask painted with a pattern
                    // takes the pattern along, also in a moved block
                    var opMove = scope.Transforms.TryGetValue(number, out var own) ? own : (Matrix?)null;
                    var patterns = op != "sh" && paint.FillPattern(op) is not null && Stencil(op, operands, parser.InlineImage, resources)
                        ? Along(opMove is not null ? scope.PatternMoves.GetValueOrDefault(number) : null, heldPatterns)
                        : null;
                    var clipBefore = ClipsBefore(scope, number);
                    if (opMove is not null || patterns is not null || clipBefore is not null)
                    {
                        output.Write("q\n"u8);
                        if (opMove is { } matrix) WriteMatrix(output, matrix);
                        foreach (var clip in clipBefore ?? []) WriteClip(output, clip);
                        WritePatternColour(output, resources, paint, op, stroke: false, null, patterns, null);
                        if (op == "BI" && parser.InlineImage is { } moving) WriteInlineImage(output, moving);
                        else WriteOperator(output, operands, op);
                        output.Write("Q\n"u8);
                        continue;
                    }
                }
                if (op == "Do" && operands.Count >= 1 && operands[0].Name is { } name)
                {
                    if (scope.Dropped.Contains(number))
                    {
                        replaced.Add(name);
                        continue;
                    }
                    if (scope.Images.TryGetValue(number, out var image))
                    {
                        replaced.Add(name);
                        // the same image at the same place is blacked out and stored once: a file drawing it 14,000 times took
                        // seconds and would have carried 14,000 copies (docs/FEHLERGEDAECHTNIS.md, No. 164)
                        var key = (image.Stream!, image.ImageToPage, resources);
                        if (_blankedImages.TryGetValue(key, out var known))
                        {
                            PdfSerializer.Write(known, output);
                            output.Write(" Do\n"u8);
                        }
                        else if (BlankImage(image.ImageToPage, image.Stream, null, image.Resources) is { } blanked)
                        {
                            _blankedImages[key] = WriteDo(output, resources, blanked);
                        }
                        continue;   // unreadable: the image is dropped entirely, to be on the safe side
                    }
                    var reference = (resources.GetDictionary(XObjectName)?.GetRaw(name) as PdfReference)?.Id;
                    if (scope.Forms.TryGetValue(number, out var form) && form.HasWork &&
                        resources.GetDictionary(XObjectName)?.Get(name) is PdfStream formStream)
                    {
                        replaced.Add(name);
                        // only its descriptions to drop: the same copy for every invocation (No. 624)
                        if (form.OnlyDescriptions && _strippedForms.TryGetValue((formStream, resources), out var stripped))
                        {
                            PdfSerializer.Write(stripped, output);
                            output.Write(" Do\n"u8);
                            continue;
                        }
                        if (reference is { } entered) _rewriting.Add(entered);
                        try
                        {
                            var written = WriteDo(output, resources, RewriteForm(formStream, form, resources));
                            if (form.OnlyDescriptions) _strippedForms[(formStream, resources)] = written;
                        }
                        finally
                        {
                            if (reference is { } left) _rewriting.Remove(left);
                        }
                        continue;
                    }
                    // Cycle: a form that is being rewritten invokes itself again by a detour. The interpreter does not execute this (it
                    // reports the form but stops); written out, the invocation would keep the original with the old text in the file
                    if (reference is { } cyclic && _rewriting.Contains(cyclic))
                    {
                        replaced.Add(name);
                        continue;
                    }
                }
                if (op == "gs" && operands.Count >= 1 && operands[0].Name is { } gsName && scope.Masks.TryGetValue(number, out var mask) && mask.Scope.HasWork &&
                    resources.GetDictionary(ExtGStateName)?.GetDictionary(gsName) is { } state && state.GetDictionary(SMaskName) is { } softMask)
                {
                    // Soft mask with content under an area: this graphics state gets a redacted copy of the mask
                    var maskCopy = new PdfDictionary { Resolver = softMask.Resolver };
                    foreach (var (key, value) in softMask.Entries) maskCopy.Set(key, value);
                    maskCopy.Set(PdfName.Get("G"), RewriteForm(mask.Group, mask.Scope, resources));
                    var stateCopy = new PdfDictionary { Resolver = state.Resolver };
                    foreach (var (key, value) in state.Entries) stateCopy.Set(key, value);
                    stateCopy.Set(SMaskName, maskCopy);
                    replacedStates.Add(gsName);
                    WriteResource(output, resources, ExtGStateName, stateCopy, " gs\n"u8);
                    continue;
                }
                if (op == "BI" && parser.InlineImage is { } inline)
                {
                    if (scope.Dropped.Contains(number)) continue;
                    if (scope.Images.TryGetValue(number, out var image))
                    {
                        if (BlankImage(image.ImageToPage, null, inline, image.Resources) is { } blanked) WriteDo(output, resources, blanked);
                        continue;
                    }
                    WriteInlineImage(output, inline);
                    continue;
                }
                if (op == "sh" && scope.Dropped.Contains(number)) continue;
                if (op == "sh" && scope.Shadings.TryGetValue(number, out var shadingCtm))
                {
                    // a mesh shading loses its data under the bars, not only what shows of them (docs/FEHLERGEDAECHTNIS.md, No. 701): cut, in a
                    // copy under a name of its own; with nothing left beside the bars it is not drawn at all. The clip stays around it as before
                    var shadingName = operands.Count >= 1 ? operands[0].Name : null;
                    var shadings = resources.GetDictionary(ShadingName);
                    var cut = shadingName is null ? null : MeshCut.Cut(doc, shadings?.Get(shadingName), resources, shadingCtm, rects);
                    if (cut is not null && shadings?.GetRaw(shadingName!) is PdfReference original) ReplacedObjects.Add(original.Id.Number);
                    if (cut is { Empty: true }) continue;
                    output.Write("q\n"u8);
                    WriteComplementClip(output, shadingCtm);
                    if (cut is { Shading: { } cutShading }) WriteResource(output, resources, ShadingName, cutShading, " sh\n"u8);
                    else WriteOperator(output, operands, op);
                    output.Write("Q\n"u8);
                    continue;
                }
                // Alternate texts may contain the removed text: when redacting all of the page, when editing only those of the affected
                // marked content (removing all of them would harm accessibility), when opening own blocks none
                var stripHere = alternates == AlternateTexts.All || alternates == AlternateTexts.Affected && affected!.Contains(number);
                // redacting: also what describes the content in another form (associated files, XMP); editing keeps it (no privacy case)
                var describing = alternates == AlternateTexts.All;
                if (op == "BDC" && operands.Count >= 2 && operands[1].Object is PdfDictionary properties && stripHere)
                {
                    if (top && properties.GetInteger(McidName) is { } mcid) AffectedMcids.Add((int)mcid);
                    var stripped = new PdfDictionary();
                    foreach (var (key, value) in properties.Entries)
                        if (!(key.Value is "ActualText" or "Alt" or "E" || describing && DescribesOld(key))) stripped.Set(key, value);
                    WriteOperator(output, [operands[0], new Operand(stripped)], op);
                    continue;
                }
                if (op == "BDC" && operands.Count >= 2 && operands[1].Name is { } propertyName && stripHere)
                {
                    if (top && resources.GetDictionary(PropertiesName)?.Get(propertyName) is PdfDictionary named && named.GetInteger(McidName) is { } mcid)
                        AffectedMcids.Add((int)mcid);
                    StripNamedProperties(resources, propertyName, operands[0].Name, describing);   // "/Span /MC0 BDC", "/AF /MC0 BDC"
                }
                WriteOperator(output, operands, op);
            }
            CopyOriginal(main, bytes, ref copyFrom, end);
            main.Write(deferred.GetBuffer(), 0, (int)deferred.Length);
            if (path.Length > 0) main.Write(path.GetBuffer(), 0, (int)path.Length);
            var body = main.ToArray();
            foreach (var name in replaced)
                if (resources.GetDictionary(XObjectName)?.GetRaw(name) is PdfReference original) ReplacedObjects.Add(original.Id.Number);
            if (tainted.Count > 0) BlankPatterns(resources, tainted);
            if (replacedStates.Count > 0) PruneStates(resources, replacedStates, body);
            Prune(resources, replaced, body, allUnused: pruneUnused);
            // editing removes no characters: the fonts stay as they are (reading them took a third of a step in a plan)
            if (!copyUnchanged) PruneFonts(content, body, resources, pruneUnused);
            return body;
        }

        private static readonly PdfName FontName = PdfName.Get("Font"), CharProcsName = PdfName.Get("CharProcs");

        /// <summary>
        /// Fonts whose characters were all removed are replaced by a bare font of the same writing direction (the "Tf" stays valid, and
        /// what is left of a text operator — only shifts — moves as before), so that their font program no longer travels in the file
        /// with this page. A Type 3 font that lost characters keeps only the glyph contents of those still shown — its glyphs are content
        /// streams and may contain text or images themselves. Codes are compared per font object, through forms (also the copies made
        /// here), glyphs and mask groups. In the copy of a form's resources (<paramref name="allUnused"/>), fonts the form does not show
        /// at all become bare as well: they came along from the page and would otherwise keep what the page has just lost. Fonts that
        /// showed nothing before stay as they are; if it cannot be told which font shows what, nothing is changed.
        /// </summary>
        private void PruneFonts(ReadOnlySpan<byte> before, byte[] after, PdfDictionary resources, bool allUnused)
        {
            var states = resources.GetDictionary(ExtGStateName);
            if (resources.GetDictionary(FontName) is not { Count: > 0 } && states is null) return;
            if (ResourceUsage.FontCodes(doc, before.ToArray(), resources, _pageResources) is not { } old) return;
            if (ResourceUsage.FontCodes(doc, after, resources, _pageResources) is not { } now) return;
            // fonts set by a graphics state: they lose characters as well — noted as touched, the writer keeps of them only what the pages
            // still show (docs/FEHLERGEDAECHTNIS.md, No. 700; the state itself stays, it may set more than the font)
            if (states is not null)
                foreach (var (key, _) in states.Entries.Take(10_000))
                    if (states.GetDictionary(key)?.GetArray(FontName) is { Count: >= 2 } gsFont && gsFont[0] is PdfReference lostRef &&
                        gsFont.Get(0) is PdfDictionary gsDict && old.TryGetValue(gsDict, out var shownByState) && shownByState.Count > 0 &&
                        (!now.TryGetValue(gsDict, out var stillByState) || shownByState.Any(c => !stillByState.Contains(c))))
                        TouchedFonts.Add(lostRef.Id.Number);
            if (resources.GetDictionary(FontName) is not { Count: > 0 } fonts) return;
            PdfDictionary? copy = null;
            foreach (var (key, _) in fonts.Entries)
            {
                if (fonts.Get(key) is not PdfDictionary font) continue;
                old.TryGetValue(font, out var shown);
                now.TryGetValue(font, out var still);
                if (shown is { Count: > 0 } && (still is null || shown.Any(c => !still.Contains(c))) && fonts.GetRaw(key) is PdfReference lost)
                    TouchedFonts.Add(lost.Id.Number);
                if (still is not { Count: > 0 })
                {
                    if (shown is { Count: > 0 } || allUnused)
                    {
                        (copy ??= Copy(fonts)).Set(key, Placeholder(font));
                        // its glyphs leave the file unless a page still shows them (the font may be shared)
                        if (font.GetDictionary(CharProcsName) is { } procs)
                            foreach (var (_, proc) in procs.Entries)
                                if (proc is PdfReference glyph) ReplacedObjects.Add(glyph.Id.Number);
                    }
                }
                else if (shown is not null && shown.Any(c => !still.Contains(c)) && Type3Without(font, still) is { } reduced)
                {
                    (copy ??= Copy(fonts)).Set(key, reduced);
                }
            }
            if (copy is not null) resources.Set(FontName, copy);

            static PdfDictionary Copy(PdfDictionary d)
            {
                var c = new PdfDictionary { Resolver = d.Resolver };
                foreach (var (k, v) in d.Entries) c.Set(k, v);
                return c;
            }
        }

        /// <summary>
        /// A font that shows nothing: a Type 3 font without glyphs, or for vertical writing a Type 0 font with Identity-V (shifts in a TJ then
        /// still move downwards; with a horizontal font the text after them jumped sideways). The Type 3 font needs no font program: a standard
        /// Helvetica in its place was a font not embedded, which PDF/A and PDF/UA forbid as long as a „Tf“ still names it — text edited in a
        /// PDF/UA file kept its identification over that break (docs/FEHLERGEDAECHTNIS.md, No. 674). The shifts left in a TJ are thousandths of
        /// the font size, whatever the font.
        /// </summary>
        private PdfDictionary Placeholder(PdfDictionary font)
        {
            var f = new PdfDictionary();
            f.Set(PdfName.Type, PdfName.Get("Font"));
            bool vertical;
            try
            {
                vertical = Fonts.PdfFont.Get(font, doc).Vertical;
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                vertical = false;
            }
            if (!vertical)
            {
                f.Set(PdfName.Subtype, PdfName.Get("Type3"));
                f.Set(PdfName.Get("FontBBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)]));
                f.Set(PdfName.Get("FontMatrix"), new PdfArray([new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0)]));
                f.Set(CharProcsName, new PdfDictionary());
                var encoding = new PdfDictionary();
                encoding.Set(PdfName.Type, PdfName.Get("Encoding"));
                encoding.Set(DifferencesName, new PdfArray());
                f.Set(EncodingName, encoding);
                f.Set(FirstCharName, PdfInteger.Of(0));
                f.Set(PdfName.Get("LastChar"), PdfInteger.Of(0));
                f.Set(WidthsName, new PdfArray([PdfInteger.Of(0)]));
                f.Set(PdfName.Get("Resources"), new PdfDictionary());
                // in a tagged file a Type 3 font needs a font descriptor (ISO 32000-2, 9.6.4, Table 110; docs/FEHLERGEDAECHTNIS.md, No. 687 —
                // found by the run of all ways to write: without it the reference model of the file format refused it); of its entries a Type 3
                // font needs only these (Table 120: no box, heights or stems). Tagged as the norm means it: marked (/MarkInfo /Marked true,
                // 14.7.1), with or without a tree
                if (doc.Catalog?.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked")) == true)
                {
                    var type3Descriptor = new PdfDictionary();
                    type3Descriptor.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
                    type3Descriptor.Set(PdfName.Get("FontName"), PdfName.Get("Empty"));
                    type3Descriptor.Set(PdfName.Get("Flags"), PdfInteger.Of(4));   // symbolic: its codes name no standard characters
                    type3Descriptor.Set(PdfName.Get("ItalicAngle"), PdfInteger.Of(0));
                    f.Set(PdfName.Get("FontDescriptor"), type3Descriptor);
                }
                return f;
            }
            // vertical: a Type 3 font knows no vertical writing, so a Type 0 font with Identity-V — with a font program of its own that has
            // nothing but the missing glyph (a subset of Liberation Sans), embedded: before it named a Helvetica without a program, which PDF/A
            // and PDF/UA forbid while a „Tf“ names it (docs/FEHLERGEDAECHTNIS.md, No. 691; the horizontal case No. 674)
            const string name = "PKLEER+LiberationSans";
            var program = new PdfStream(new PdfDictionary(), EmptyFontProgram.Value);
            program.Dictionary.Set(PdfName.Get("Length1"), PdfInteger.Of(EmptyFontProgram.Value.Length));
            var descriptor = new PdfDictionary();
            descriptor.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
            descriptor.Set(PdfName.Get("FontName"), PdfName.Get(name));
            descriptor.Set(PdfName.Get("Flags"), PdfInteger.Of(4));   // symbolic: its codes name no standard characters
            descriptor.Set(PdfName.Get("FontBBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)]));
            foreach (var key in (ReadOnlySpan<string>)["ItalicAngle", "Ascent", "CapHeight", "StemV"]) descriptor.Set(PdfName.Get(key), PdfInteger.Of(0));
            // /Descent is negative (ISO 32000-2, 9.8.1; before 0, docs/FEHLERGEDAECHTNIS.md, No. 1156) — as the font of the text recognition (No. 773)
            descriptor.Set(PdfName.Get("Descent"), PdfInteger.Of(-1));
            descriptor.Set(PdfName.Get("FontFile2"), program);
            var info = new PdfDictionary();
            info.Set(PdfName.Get("Registry"), new PdfString("Adobe"u8.ToArray()));
            info.Set(PdfName.Get("Ordering"), new PdfString("Identity"u8.ToArray()));
            info.Set(PdfName.Get("Supplement"), PdfInteger.Of(0));
            var cid = new PdfDictionary();
            cid.Set(PdfName.Type, PdfName.Get("Font"));
            cid.Set(PdfName.Subtype, PdfName.Get("CIDFontType2"));
            cid.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
            cid.Set(PdfName.Get("CIDSystemInfo"), info);
            cid.Set(PdfName.Get("FontDescriptor"), descriptor);
            cid.Set(PdfName.Get("CIDToGIDMap"), PdfName.Get("Identity"));
            cid.Set(PdfName.Get("DW"), PdfInteger.Of(0));
            f.Set(PdfName.Subtype, PdfName.Get("Type0"));
            f.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
            f.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-V"));
            f.Set(PdfName.Get("DescendantFonts"), new PdfArray([cid]));
            return f;
        }

        /// <summary>The font program of the vertical stand-in: Liberation Sans with nothing but its glyph 0 (made once).</summary>
        private static readonly Lazy<byte[]> EmptyFontProgram =
            new(() => Core.Fonts.TrueTypeSubsetter.Subset(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"), [0]));

        /// <summary>A Type 3 font with only the glyph contents of <paramref name="codes"/> (by /Differences); null if nothing would go.</summary>
        private static readonly PdfName EncodingName = PdfName.Get("Encoding"), DifferencesName = PdfName.Get("Differences"), WidthsName = PdfName.Get("Widths"),
            FirstCharName = PdfName.Get("FirstChar"), ToUnicodeName = PdfName.Get("ToUnicode");

        internal PdfDictionary? Type3Without(PdfDictionary font, HashSet<byte> codes)
        {
            if (font.GetName(PdfName.Subtype)?.Value != "Type3" || font.GetDictionary(CharProcsName) is not { } procs) return null;
            // without /Differences the glyph names come from a base encoding: keep everything
            if (ResourceUsage.Type3Glyphs(font, codes) is not { } names) return null;
            var kept = new PdfDictionary { Resolver = procs.Resolver };
            // A glyph that draws nothing but carries the box around all the glyphs' boxes, first: the height of the font comes from them,
            // and with fewer glyphs the boxes of the text left would change (docs/FEHLERGEDAECHTNIS.md, No. 161). It tells nothing about
            // the glyphs removed
            var notdef = PdfName.Get(".notdef");
            if (Fonts.Type3Font.ProcBounds(procs, doc) is var (x0, y0, x1, y1) && !names.Contains(notdef))
                kept.Set(notdef, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes($"0 0 {Num(x0)} {Num(y0)} {Num(x1)} {Num(y1)} d1")));
            var dropped = false;
            foreach (var (key, value) in procs.Entries)
            {
                if (names.Contains(key)) kept.Set(key, value);
                else
                {
                    dropped = true;
                    if (value is PdfReference glyph) ReplacedObjects.Add(glyph.Id.Number);
                }
            }
            if (!dropped) return null;
            var reduced = new PdfDictionary { Resolver = font.Resolver };
            foreach (var (key, value) in font.Entries) reduced.Set(key, key == CharProcsName ? kept : value);
            // The copy shows only these codes: its encoding, widths and ToUnicode name no others. Shared with the original (written
            // into the font dictionary itself), they kept the removed glyph's name, width and text (docs/FEHLERGEDAECHTNIS.md, No. 181)
            var used = new bool[256];
            foreach (var c in codes) used[c] = true;
            if (font.Get(EncodingName) is PdfDictionary encoding && encoding.GetArray(DifferencesName) is { } differences)
                reduced.Set(EncodingName, Writing.FontScrub.EncodingWith(encoding, differences, used));
            if (font.GetArray(WidthsName) is { } widths && font.GetInteger(FirstCharName) is { } first)
                reduced.Set(WidthsName, Writing.FontScrub.WidthsWith(widths, (int)first, used));
            if (font.Get(ToUnicodeName) is PdfStream unicode && Fonts.PdfFont.ReadToUnicode(font, doc) is { } cmap)
                reduced.Set(ToUnicodeName, Writing.FontScrub.ToUnicodeOf(doc, unicode, cmap, codes.Select(c => ((uint)c, 1)).ToHashSet()));
            return reduced;
        }

        /// <summary>
        /// Graphics states whose mask was replaced by a redacted copy are dropped from the resources if the content no longer
        /// invokes them — otherwise they would carry the unredacted mask into the file. Their objects count as replaced (dropped
        /// when no page uses them any more).
        /// </summary>
        private void PruneStates(PdfDictionary resources, HashSet<PdfName> replacedStates, byte[] body)
        {
            if (resources.GetDictionary(ExtGStateName) is not { } states) return;
            var used = new HashSet<PdfName>();
            var parser = new ContentParser(body);
            while (parser.Next() is { } op)
                if (op == "gs" && parser.Operands.Count >= 1 && parser.Operands[0].Name is { } name) used.Add(name);
            var kept = new PdfDictionary { Resolver = states.Resolver };
            foreach (var (key, value) in states.Entries)
            {
                if (!replacedStates.Contains(key) || used.Contains(key))
                {
                    kept.Set(key, value);
                    continue;
                }
                if (value is PdfReference state) ReplacedObjects.Add(state.Id.Number);
                if (states.GetDictionary(key)?.GetDictionary(SMaskName)?.GetRaw(PdfName.Get("G")) is PdfReference group) ReplacedObjects.Add(group.Id.Number);
            }
            resources.Set(ExtGStateName, kept);
        }

        private static readonly PdfName PropertiesName = PdfName.Get("Properties");

        /// <summary>
        /// Named properties of a BDC without alternate texts (/ActualText, /Alt, /E) — when redacting also without what describes the content
        /// in another form (<see cref="DescribesOldContent"/>, ISO 32000-2 14.3.2), and the files of „/AF /MC0 BDC“ (an array of file
        /// specifications, 14.13.5) as an empty array, which associates none and keeps the pair valid. A copy in the resources of this page;
        /// layers (/OCG, /OCMD) apply to the whole document and stay (docs/FEHLERGEDAECHTNIS.md, No. 444).
        /// </summary>
        private void StripNamedProperties(PdfDictionary resources, PdfName name, PdfName? tag, bool describing)
        {
            bool Strip(PdfName key) => key.Value is "ActualText" or "Alt" or "E" || describing && DescribesOld(key);
            if (resources.GetDictionary(PropertiesName) is not { } properties) return;
            PdfObject replacement;
            switch (properties.Get(name))
            {
                case PdfArray when describing && tag?.Value == "AF":
                    replacement = new PdfArray();
                    break;
                case PdfDictionary named when !named.IsType(PdfName.Get("OCG")) && !named.IsType(PdfName.Get("OCMD")) && named.Entries.Any(e => Strip(e.Key)):
                    var stripped = new PdfDictionary { Resolver = named.Resolver };
                    foreach (var (key, value) in named.Entries)
                        if (!Strip(key)) stripped.Set(key, value);
                    replacement = stripped;
                    break;
                default:
                    return;
            }
            var copy = new PdfDictionary { Resolver = properties.Resolver };
            foreach (var (key, value) in properties.Entries) copy.Set(key, key == name ? replacement : value);
            resources.Set(PropertiesName, copy);
            // Do not write the original (possibly in resources that pages share)
            if (properties.GetRaw(name) is PdfReference original) ReplacedObjects.Add(original.Id.Number);
        }

        /// <summary>
        /// Tiling patterns with text or images that are painted under an area: an empty pattern for this page (the redaction does
        /// not process the content of patterns, so nothing can be cut out of it).
        /// </summary>
        private void BlankPatterns(PdfDictionary resources, HashSet<PdfName> tainted)
        {
            if (resources.GetDictionary(PatternName) is not { } patterns) return;
            var copy = new PdfDictionary { Resolver = patterns.Resolver };
            foreach (var (key, value) in patterns.Entries)
            {
                if (!tainted.Contains(key) || patterns.Get(key) is not PdfStream pattern || !DrawsTextOrImages(pattern))
                {
                    copy.Set(key, value);
                    continue;
                }
                // an empty cell: without its resources (they would keep the fonts and images of the old cell in the file) and without
                // what describes the old content (docs/FEHLERGEDAECHTNIS.md, No. 444)
                var dict = new PdfDictionary { Resolver = pattern.Dictionary.Resolver };
                foreach (var (k, v) in pattern.Dictionary.Entries)
                    if (k.Value is not ("Length" or "Filter" or "DecodeParms" or "Resources") && !DescribesOld(k)) dict.Set(k, v);
                dict.Set(PdfName.Resources, new PdfDictionary());
                copy.Set(key, new PdfStream(dict, []));
                if (value is PdfReference original) ReplacedObjects.Add(original.Id.Number);
            }
            resources.Set(PatternName, copy);
        }

        private bool DrawsTextOrImages(PdfStream pattern)
        {
            byte[] data;
            try
            {
                data = doc.GetStreamData(pattern);
            }
            catch (PdfException)
            {
                return true;   // unreadable: to be on the safe side
            }
            var parser = new ContentParser(data);
            while (parser.Next() is { } op)
                if (op is "Tj" or "TJ" or "'" or "\"" or "Do" or "BI" or "sh") return true;
            return false;
        }

        /// <summary>
        /// Originals (object numbers of the source) that were replaced or dropped here: images, forms, patterns no longer used.
        /// The writer omits them when no page draws them any more (<see cref="RedactionResult.ReplacedObjects"/>).
        /// </summary>
        public HashSet<int> ReplacedObjects { get; } = [];

        /// <summary>Fonts (object numbers) that lost characters here.</summary>
        public HashSet<int> TouchedFonts { get; } = [];

        private static readonly PdfName PatternName = PdfName.Get("Pattern"), MatrixName = PdfName.Get("Matrix"), ImageMaskName = PdfName.Get("ImageMask"),
            InlineMaskName = PdfName.Get("IM");

        /// <summary>
        /// Removes replaced images and forms from the resources if the new content no longer invokes them — otherwise the
        /// unredacted original would remain unused in the file. If still used elsewhere on the page, it stays. In copies for a
        /// form (<paramref name="allUnused"/>) everything unused is dropped: otherwise a form without resources of its own would
        /// get those of the page, including the originals just replaced there. Patterns nobody paints any more (such as the fill
        /// of a removed path) are always dropped.
        /// </summary>
        private void Prune(PdfDictionary resources, HashSet<PdfName> replaced, byte[] body, bool allUnused)
        {
            var existing = resources.GetDictionary(XObjectName);
            var patterns = resources.GetDictionary(PatternName);
            if (existing is null && patterns is null) return;
            var (usedXObjects, usedPatterns, selectedPatterns) = ResourceUsage.Names(doc, body, resources, _pageResources);
            if (existing is not null)
            {
                var kept = new PdfDictionary { Resolver = existing.Resolver };
                foreach (var (key, value) in existing.Entries)
                    if (usedXObjects.Contains(key) || !allUnused && !replaced.Contains(key)) kept.Set(key, value);
                resources.Set(XObjectName, kept);
            }
            if (patterns is not null)
            {
                var kept = new PdfDictionary { Resolver = patterns.Resolver };
                foreach (var (key, value) in patterns.Entries)
                {
                    if (usedPatterns.Contains(key))
                    {
                        kept.Set(key, value);
                        continue;
                    }
                    if (value is PdfReference original) ReplacedObjects.Add(original.Id.Number);
                    // chosen as a colour but nothing painted with it any more: an empty pattern under the same name, so that "/P0 scn"
                    // still names a resource (readers that check names refused the page, docs/FEHLERGEDAECHTNIS.md, No. 406)
                    if (selectedPatterns.Contains(key)) kept.Set(key, EmptyPattern(patterns.Get(key)));
                }
                resources.Set(PatternName, kept);
            }
        }

        /// <summary>
        /// A tiling pattern that paints nothing, of the paint type of <paramref name="original"/> (an uncoloured pattern has to stay
        /// uncoloured: its colour space expects the colour values before the name).
        /// </summary>
        private static PdfStream EmptyPattern(PdfObject? original)
        {
            var paintType = (original as PdfStream)?.Dictionary is { } d && d.GetInteger(PdfName.Get("PatternType")) == 1 && d.GetInteger(PdfName.Get("PaintType")) == 2 ? 2 : 1;
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("Pattern"));
            dict.Set(PdfName.Get("PatternType"), PdfInteger.Of(1));
            dict.Set(PdfName.Get("PaintType"), PdfInteger.Of(paintType));
            dict.Set(PdfName.Get("TilingType"), PdfInteger.Of(1));
            dict.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(1), PdfInteger.Of(1)]));
            dict.Set(PdfName.Get("XStep"), PdfInteger.Of(1));
            dict.Set(PdfName.Get("YStep"), PdfInteger.Of(1));
            dict.Set(PdfName.Resources, new PdfDictionary());
            return new PdfStream(dict, []);
        }

        // -------------------------------------------------- Text

        /// <summary>
        /// Text operator without the removed characters: as TJ, with the gap as a shift — coarsened when redacting, also hidden text
        /// (<paramref name="coarse"/>), exact when sanitising and editing text: there the width reveals nothing, and rounding would
        /// move visible characters after the gap. <paramref name="carry"/>: the rounding carried over from the earlier operators of the line.
        /// </summary>
        private static void WriteShowText(MemoryStream output, string op, IReadOnlyList<Operand> operands, List<GlyphCut> cuts, bool coarse, ref double carry)
        {
            var array = new PdfArray();
            var gap = new Gap(array, coarse) { Carry = carry };
            // the cuts of each element in order, sorted once: picked out of all cuts per element of a TJ, the work grew with the square of
            // its length (a TJ of 200,000 removed characters took 20 s, docs/FEHLERGEDAECHTNIS.md, No. 283)
            var byElement = new Dictionary<int, List<GlyphCut>>();
            foreach (var cut in cuts)
            {
                if (!byElement.TryGetValue(cut.Element, out var list)) byElement[cut.Element] = list = [];
                list.Add(cut);
            }
            foreach (var list in byElement.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
            List<GlyphCut>? CutsOf(int element) => byElement.GetValueOrDefault(element);
            switch (op)
            {
                case "'":
                    output.Write("T*\n"u8);
                    AppendString(gap, operands[0].Object as PdfString, CutsOf(0));
                    break;
                case "\"" when operands.Count >= 3:
                    WriteNumber(output, operands[0].Number);
                    output.Write(" Tw "u8);
                    WriteNumber(output, operands[1].Number);
                    output.Write(" Tc T*\n"u8);
                    AppendString(gap, operands[2].Object as PdfString, CutsOf(2));
                    break;
                case "Tj":
                    AppendString(gap, operands[0].Object as PdfString, CutsOf(0));
                    break;
                case "TJ" when operands[0].Object is PdfArray original:
                    for (var i = 0; i < original.Count; i++)
                    {
                        if (original[i] is PdfString s) AppendString(gap, s, CutsOf(i));
                        else if (gap.Open && original.GetNumber(i) is { } n) gap.Shift(n);   // spacing between removed characters
                        else array.Add(original[i]);
                    }
                    break;
            }
            gap.Flush();
            carry = gap.Carry;
            PdfSerializer.Write(array, output);
            output.Write(" TJ\n"u8);
        }

        /// <summary>
        /// Gap of removed characters: consecutively removed characters give a single shift — also across elements of a TJ,
        /// including the spacing in between — rounded to the grid once. If each element got its own gap and the kerning values
        /// stayed, the number of characters, their widths and pairs could be read off and the name recovered with a dictionary
        /// ("Edact-Ray").
        /// </summary>
        private sealed class Gap(PdfArray array, bool coarse)
        {
            private double _gap;     // in text space
            /// <summary>Page space: how far the text written so far lands from where it was (at most half the grid).</summary>
            public double Carry { get; set; }
            private GlyphCut _last;

            /// <summary>Something was removed since the last kept character.</summary>
            public bool Open { get; private set; }

            public PdfArray Array => array;

            public void Remove(GlyphCut cut)
            {
                _gap += cut.Advance;
                _last = cut;
                Open = true;
            }

            /// <summary>Number in TJ (thousandths, positive to the left) between removed characters.</summary>
            public void Shift(double thousandths)
            {
                var size = _last.Vertical ? _last.FontSize : _last.FontSize * _last.Scale;
                if (double.IsFinite(size)) _gap -= thousandths / 1000 * size;
            }

            /// <summary>Before the next kept character and at the end: the gap as one rounded shift.</summary>
            public void Flush()
            {
                if (!Open) return;
                Open = false;
                var gap = _gap;
                _gap = 0;
                if (gap == 0 || _last.FontSize == 0) return;
                // Redacting: round in page space to the grid, so that the exact width does not reveal the word — rounded is where the
                // following text lands, not each gap by itself: otherwise the errors of several gaps in one operator add up (several
                // tiny removed characters moved the rest of a line by 2 pt, docs/FEHLERGEDAECHTNIS.md, No. 138; across the operators of a
                // line, No. 154)
                var perText = _last.PagePerText;
                if (coarse && perText > 0 && double.IsFinite(perText))
                {
                    var exact = gap * perText;
                    var written = Math.Round((exact - Carry) / PositionGrid) * PositionGrid;
                    Carry += written - exact;
                    gap = written / perText;
                    if (gap == 0) return;
                }
                var adjust = _last.Vertical ? -gap * 1000 / _last.FontSize : -gap * 1000 / (_last.FontSize * _last.Scale);
                if (double.IsFinite(adjust)) array.Add(new PdfReal(Math.Round(adjust, coarse ? 2 : 4)));
            }
        }

        /// <summary>Bytes of the strings a text operator shows.</summary>
        private static int ShownBytes(string op, IReadOnlyList<Operand> operands)
        {
            switch (op)
            {
                case "TJ" when operands.Count >= 1 && operands[0].Object is PdfArray parts:
                    var n = 0;
                    for (var i = 0; i < parts.Count; i++)
                        if (parts[i] is PdfString s) n += s.Bytes.Length;
                    return n;
                case "\"" when operands.Count >= 3:
                    return (operands[2].Object as PdfString)?.Bytes.Length ?? 0;
                default:
                    return operands.Count >= 1 ? (operands[0].Object as PdfString)?.Bytes.Length ?? 0 : 0;   // the string the display shows
            }
        }

        /// <summary>A string of a text operator without its removed characters; <paramref name="cuts"/>: those of this element, in order.</summary>
        private static void AppendString(Gap gap, PdfString? text, List<GlyphCut>? cuts)
        {
            if (text is null) return;
            var bytes = text.Bytes;
            var position = 0;
            foreach (var cut in cuts ?? [])
            {
                if (cut.Start > position)
                {
                    gap.Flush();
                    gap.Array.Add(new PdfString(bytes[position..cut.Start], text.IsHex));
                }
                gap.Remove(cut);
                position = Math.Max(position, cut.Start + cut.Length);
            }
            if (position < bytes.Length)
            {
                gap.Flush();
                gap.Array.Add(new PdfString(bytes[position..], text.IsHex));
            }
        }

        // -------------------------------------------------- Clipping out

        /// <summary>
        /// How far what is painted only outside the areas still reaches under them, in points. Ending exactly at the edge of the bar, the
        /// edge of the clip and the edge of the bar fell on the same pixel, each smoothed by half: what lay beneath (a word covered by a
        /// white box) showed through as a fine line along the bar — visible after redaction, and so not taken for hidden
        /// (docs/FEHLERGEDAECHTNIS.md, No. 172). The bar covers the overlap.
        /// </summary>
        internal const double ClipSeam = 0.25;

        /// <summary>
        /// How far inside an area a point of a clip may lie and the clip still stay as written (<see cref="WriteClipCut"/>): the seam the
        /// check after redacting allows a path that paints nothing (<see cref="PathPointUnder"/>, 0.5 pt).
        /// </summary>
        private const double ClipPointSeam = 0.5;

        /// <summary>
        /// Clipping "everything except the areas" in the user space of the CTM: a large quadrilateral plus the areas less
        /// <see cref="ClipSeam"/>, even-odd.
        /// </summary>
        private void WriteComplementClip(MemoryStream output, Matrix ctm)
        {
            Matrix inverse;
            try
            {
                inverse = ctm.Invert();
            }
            catch (ArgumentException)
            {
                return;
            }
            var outer = RectD.FromLtrb(mediaBox.Left - 10_000, mediaBox.Bottom - 10_000, mediaBox.Right + 10_000, mediaBox.Top + 10_000);
            // Even-odd: where two areas overlap, the point lies inside the clip again — a shading painted there, under both bars
            // (docs/FEHLERGEDAECHTNIS.md, No. 455). Overlapping areas go into clips of their own, one after the other.
            var layers = new List<List<RectD>> { new() };
            foreach (var r in rects)
            {
                if (r.Width <= 2 * ClipSeam || r.Height <= 2 * ClipSeam) continue;
                var hole = RectD.FromLtrb(r.Left + ClipSeam, r.Top + ClipSeam, r.Right - ClipSeam, r.Bottom - ClipSeam);
                var layer = layers.Find(l => !l.Exists(h => Math.Min(h.Right, hole.Right) > Math.Max(h.Left, hole.Left) &&
                                                             Math.Min(h.Bottom, hole.Bottom) > Math.Max(h.Top, hole.Top)));
                if (layer is null) layers.Add(layer = []);
                layer.Add(hole);
            }
            foreach (var layer in layers)
            {
                WritePolygon(output, inverse, outer);
                foreach (var hole in layer) WritePolygon(output, inverse, hole);
                output.Write("W* n\n"u8);
            }
        }

        private static void WritePolygon(MemoryStream output, Matrix inverse, RectD r)
        {
            var corners = new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom) };
            for (var i = 0; i < 4; i++)
            {
                var (x, y) = inverse.Transform(corners[i].Item1, corners[i].Item2);
                WriteNumber(output, x);
                output.WriteByte((byte)' ');
                WriteNumber(output, y);
                output.Write(i == 0 ? " m\n"u8 : " l\n"u8);
            }
            output.Write("h\n"u8);
        }

        // -------------------------------------------------- Images

        // Decoded once per page (blacking out works on a copy per drawing): a slide drew the same image with a large mask twenty
        // times, decoded anew each time — 4.3 s for redacting (issue16263). With a budget, so that not all large scans are kept
        private readonly Dictionary<(PdfStream, PdfDictionary?), DecodedImage?> _decoded = new(DecodedKey.Instance);
        private long _decodedBytes;
        private const long DecodedBudget = 96L << 20;

        private DecodedImage? DecodeOnce(PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool stencil)
        {
            if (stream is null) return PdfImageDecoder.Decode(new ImageSource(null, inline, resources, stencil, 0xFF000000), doc);
            if (_decoded.TryGetValue((stream, resources), out var known)) return known;
            var decoded = PdfImageDecoder.Decode(new ImageSource(stream, null, resources, stencil, 0xFF000000), doc);
            var size = decoded?.Pixels.Length ?? 0;
            if (_decodedBytes + size <= DecodedBudget)
            {
                _decoded[(stream, resources)] = decoded;
                _decodedBytes += size;
            }
            return decoded;
        }

        /// <summary>Key by object (not by content): the same stream with the same resources.</summary>
        private sealed class DecodedKey : IEqualityComparer<(PdfStream Stream, PdfDictionary? Resources)>
        {
            public static readonly DecodedKey Instance = new();

            public bool Equals((PdfStream Stream, PdfDictionary? Resources) a, (PdfStream Stream, PdfDictionary? Resources) b) =>
                ReferenceEquals(a.Stream, b.Stream) && ReferenceEquals(a.Resources, b.Resources);

            public int GetHashCode((PdfStream Stream, PdfDictionary? Resources) key) =>
                HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Stream),
                    key.Resources is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Resources));
        }

        /// <summary>
        /// Image with blacked-out pixels under the areas, as a new image XObject (for this page only); null if it cannot be read
        /// (it is then dropped entirely). Image XObjects and inline images are blacked out in their own samples (<see cref="ImageBlanker"/>;
        /// an inline image as a stream of its own, written back as an XObject) — JPEG and JPEG 2000 through the own decoders in their own
        /// channels; only what they cannot read goes the way of the display, refined the same way.
        /// </summary>
        /// <summary>The work left for the images of this page (<see cref="ImageBlanker.PageBudget"/>).</summary>
        private readonly ImageBlanker.PageBudget _budget = new();

        /// <summary>
        /// The image blacked out under the areas (<see cref="BlankOwnImage"/>). Printed, an image without /OC shows the first of its
        /// /Alternates marked /DefaultForPrinting (ISO 32000-2, 8.9.5.4 d): that one is blacked out at the same place and kept as the only
        /// alternate of the copy. Before, the alternates were left out (they hold the whole image, No. 434), and the copy printed the image
        /// for the screen instead — changed outside the bar (docs/FEHLERGEDAECHTNIS.md, No. 925). An unredacted version never goes along: an
        /// alternate that cannot be blacked out is left out as before.
        /// </summary>
        private PdfStream? BlankImage(Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources)
        {
            var blanked = BlankOwnImage(imageToPage, stream, inline, resources);
            if (blanked is null || stream is null || ReferenceEquals(blanked, stream) || stream.Dictionary.GetRaw(OptionalContentKey) is not (null or PdfNull))
                return blanked;
            if (stream.Dictionary.GetArray(AlternatesKey) is not { } alternates) return blanked;
            for (var i = 0; i < alternates.Count && i < 64; i++)
            {
                if (alternates.Get(i) is not PdfDictionary entry || entry.GetBoolean(DefaultForPrintingKey) != true) continue;
                if (entry.Get(AlternateImageKey) is PdfStream printed && !ReferenceEquals(printed, stream) &&
                    BlankOwnImage(imageToPage, printed, null, resources) is { } printedBlanked && !ReferenceEquals(printedBlanked, printed))
                {
                    var kept = new PdfDictionary();
                    foreach (var (key, value) in entry.Entries)
                        if (key != AlternateImageKey) kept.Set(key, value);
                    kept.Set(AlternateImageKey, printedBlanked);
                    var only = new PdfArray();
                    only.Add(kept);
                    blanked.Dictionary.Set(AlternatesKey, only);
                }
                break;   // the first one marked counts (case d), blacked out or left out
            }
            return blanked;
        }

        private static readonly PdfName AlternatesKey = PdfName.Get("Alternates"), DefaultForPrintingKey = PdfName.Get("DefaultForPrinting"),
            AlternateImageKey = PdfName.Get("Image"), OptionalContentKey = PdfName.Get("OC");

        private PdfStream? BlankOwnImage(Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources)
        {
            // an inline image in its own samples too: its dictionary as that of an XObject (full keys and values only — kept abbreviations
            // garbled the result, No. 289) and its data as a stream — before, it went the way of the display and came back 8 bits per sample
            // in RGB (docs/FEHLERGEDAECHTNIS.md, No. 279)
            var own = stream ?? (inline is not null ? new PdfStream(XObjectDictionary(inline, resources), inline.Data) : null);
            if (own is not null)
            {
                var blanked = ImageBlanker.TryBlank(doc, own, resources, imageToPage, rects, out var handled, _budget);
                if (handled && ReadsAsBlanked(blanked, stream, inline, resources, imageToPage))
                {
                    if (blanked is not null) BlankedImages++;
                    return blanked;
                }
            }
            var dict = stream?.Dictionary ?? inline!.Dictionary;
            var stencil = (dict.GetBoolean(PdfName.Get("ImageMask")) ?? dict.GetBoolean(PdfName.Get("IM"))) == true;
            var interpolate = (dict.GetBoolean(PdfName.Get("Interpolate")) ?? dict.GetBoolean(PdfName.Get("I"))) == true;
            var decoded = DecodeOnce(stream, inline, resources, stencil);
            if (decoded is null || !_budget.TakeSamples((long)decoded.Width * decoded.Height)) return null;   // dropped
            var bpp = decoded.BytesPerPixel;
            var (kx, ky) = ImageBlanker.Factors(imageToPage, decoded.Width, decoded.Height, _budget);
            var (w, h) = (decoded.Width * kx, decoded.Height * ky);
            var pixels = ImageBlanker.Refine(decoded.Pixels, decoded.Width, decoded.Height, bpp, kx, ky,
                smooth: interpolate && decoded.Format != ImagePixels.Alpha);
            if (ImageBlanker.Quads(imageToPage, w, h, rects) is not { } quads) return null;
            for (var y = 0; y < h; y++)
            {
                var row = y;
                ImageBlanker.Spans(quads, y, w, (from, to) =>
                {
                    var span = pixels.AsSpan((row * w + from) * bpp, (to - from) * bpp);
                    if (decoded.Format != ImagePixels.Rgba)
                    {
                        span.Clear();   // grey: black; stencil: paint nothing here any more (the bar covers it)
                        return;
                    }
                    for (var at = 0; at < span.Length; at += 4)
                    {
                        span[at] = span[at + 1] = span[at + 2] = 0;
                        span[at + 3] = 255;
                    }
                });
            }
            BlankedImages++;
            return EncodeImage(w, h, pixels, decoded.Format, interpolate);
        }

        /// <summary>
        /// Whether the image blacked out in its own samples reads, through the display's decoder, as the original with the areas blacked out:
        /// under each area all its pixels alike at full size (colour and opacity within <see cref="SameLevels"/>, <see cref="UniformUnderAreas"/>),
        /// and beside the areas and their seam
        /// (the pixels they touch) at most <see cref="DifferingShare"/> of the pixels more than that apart from the original — a JPEG packed
        /// again changes a little. The blanker reads the samples a way of its own (rows, bit depth, /Decode, masks); a small Flate image the
        /// display reads despite a damaged header came back black, and nothing compared the result (docs/FEHLERGEDAECHTNIS.md, No. 431).
        /// If the display cannot read the original, it draws a placeholder and there is nothing to compare: the image stays as blacked out
        /// (or dropped). One the blanker could not read (<paramref name="blanked"/> null) but the display can goes the display's way.
        /// </summary>
        private bool ReadsAsBlanked(PdfStream? blanked, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, Matrix imageToPage)
        {
            var dict = stream?.Dictionary ?? inline!.Dictionary;
            var stencil = (dict.GetBoolean(PdfName.Get("ImageMask")) ?? dict.GetBoolean(PdfName.Get("IM"))) == true;
            // both readings reduced to a million pixels: enough to see what a bar covers and what lies beside it, and a large scan is not
            // decoded twice at full size for the check
            if (DecodeReducedOnce(stream, inline, resources, stencil) is not { } before) return true;
            if (blanked is null) return false;
            if (!_budget.TakeSamples((long)before.Width * before.Height)) return false;
            // reading the copy costs what it holds, not what it is reduced to: a tiny image with a mask of 151 megapixels, drawn some forty
            // times under a bar, took a minute and a half for this check alone (a damaged file of the fuzzer, docs/FEHLERGEDAECHTNIS.md,
            // No. 463). Beyond the budget the copy goes the display's way — read once per page, blacked out as the display shows it
            var cost = ImageBlanker.StoredSamples(blanked);
            if (cost > _checkSamples) return false;
            _checkSamples -= cost;
            if (PdfImageDecoder.DecodeReduced(new ImageSource(blanked, null, resources, stencil, 0xFF000000), doc, CheckPixels) is not { } after
                || after.Format != before.Format) return false;
            var bpp = after.BytesPerPixel;
            // a refined copy of an interpolated image is smoothed like a reader enlarging it (ImageBlanker.Refine): beside the nearest pixel of
            // the original it may match the original sampled that way — compared with the nearest pixel only, such a coarse image went the
            // display's way and lost its colour space (docs/FEHLERGEDAECHTNIS.md, No. 809)
            var smooth = (dict.GetBoolean(PdfName.Get("Interpolate")) ?? dict.GetBoolean(PdfName.Get("I"))) == true
                         && (blanked.Dictionary.GetInteger(PdfName.Get("Width")) > (dict.GetInteger(PdfName.Get("Width")) ?? dict.GetInteger(PdfName.Get("W")))
                             || blanked.Dictionary.GetInteger(PdfName.Get("Height")) > (dict.GetInteger(PdfName.Get("Height")) ?? dict.GetInteger(PdfName.Get("H"))));
            var (px, py) = ImageBlanker.PixelSize(imageToPage, after.Width, after.Height);
            var (bx, by) = ImageBlanker.PixelSize(imageToPage, before.Width, before.Height);
            var seam = 2 * Math.Max(Math.Max(px, py), Math.Max(bx, by)) + 0.01;
            // the areas and their seams in the unit square of the image: with an image placed upright or turned by a right angle (the
            // usual case) exactly, so that no pixel needs a matrix of its own — a matrix per pixel cost a third of the time of redacting
            var toImage = imageToPage.Invert();
            var upright = imageToPage.B == 0 && imageToPage.C == 0 || imageToPage.A == 0 && imageToPage.D == 0;
            var areas = rects.Select(r => toImage.TransformBounds(r)).ToArray();
            var near = rects.Select(r => toImage.TransformBounds(r.Inflate(seam))).ToArray();
            long outside = 0, differing = 0;
            for (var y = 0; y < after.Height; y++)
            {
                var v = 1 - (y + 0.5) / after.Height;
                var row = Math.Min(before.Height - 1, (int)((y + 0.5) * before.Height / after.Height));
                // rows beside all areas and seams: compared as a whole
                var touched = false;
                for (var k = 0; k < near.Length && !touched; k++) touched = v > near[k].Top && v < near[k].Bottom;
                if (upright && !touched)
                {
                    outside += after.Width;
                    differing += DifferingPixels(after, y, before, row, bpp, smooth);
                    continue;
                }
                for (var x = 0; x < after.Width; x++)
                {
                    var u = (x + 0.5) / after.Width;
                    var at = (y * after.Width + x) * bpp;
                    var (qx, qy) = upright ? (u, v) : imageToPage.Transform(u, v);
                    var inside = false;
                    for (var k = 0; k < rects.Count && !inside; k++)
                    {
                        var r = upright ? areas[k] : rects[k];
                        inside = qx > r.Left && qx < r.Right && qy > r.Top && qy < r.Bottom;
                    }
                    for (var k = 0; k < near.Length && !inside; k++)
                    {
                        var r = upright ? near[k] : rects[k].Inflate(seam);
                        inside = qx > r.Left && qx < r.Right && qy > r.Top && qy < r.Bottom;
                    }
                    if (inside) continue;
                    outside++;
                    if (Differs(after, x, y, before, bpp, smooth)) differing++;
                }
            }
            // beside the areas reduced (enough to see that the rest of the image stayed), under them at full size (No. 439)
            var beside = differing <= outside * DifferingShare;
            var uniform = beside && UniformUnderAreas(doc, blanked, resources, stencil, imageToPage, rects);
            if (DebugOn && !uniform)
                Console.Error.WriteLine($"[Schwärzen, Bild] eigene Samples verworfen: {(beside ? "unter den Bereichen nicht einheitlich" : $"daneben {differing} von {outside} Pixeln anders")}");
            return uniform;
        }

        /// <summary>
        /// Whether the pixel (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="after"/> differs from the original by more than
        /// <see cref="SameLevels"/>: from its nearest pixel, and with <paramref name="smooth"/> also from the original sampled bilinearly the way
        /// <see cref="ImageBlanker.Refine"/> smooths a refined copy (pixel centres, clamped at the edges; alpha by its nearest pixel — masks are
        /// refined by repeating).
        /// </summary>
        private static bool Differs(DecodedImage after, int x, int y, DecodedImage before, int bpp, bool smooth)
        {
            var at = (y * after.Width + x) * bpp;
            var column = Math.Min(before.Width - 1, (int)((x + 0.5) * before.Width / after.Width));
            var row = Math.Min(before.Height - 1, (int)((y + 0.5) * before.Height / after.Height));
            var from = (row * before.Width + column) * bpp;
            var near = false;
            for (var c = 0; c < bpp && !near; c++) near = Math.Abs(after.Pixels[at + c] - before.Pixels[from + c]) > SameLevels;
            if (!near || !smooth) return near;
            var (x0, x1, fx) = Bilinear((x + 0.5) * before.Width / after.Width - 0.5, before.Width);
            var (y0, y1, fy) = Bilinear((y + 0.5) * before.Height / after.Height - 0.5, before.Height);
            var alpha = after.Format == ImagePixels.Rgba ? 3 : -1;
            for (var c = 0; c < bpp; c++)
            {
                double expected = before.Pixels[from + c];
                if (c != alpha)
                {
                    int a0 = before.Pixels[(y0 * before.Width + x0) * bpp + c], a1 = before.Pixels[(y0 * before.Width + x1) * bpp + c];
                    int b0 = before.Pixels[(y1 * before.Width + x0) * bpp + c], b1 = before.Pixels[(y1 * before.Width + x1) * bpp + c];
                    expected = (1 - fy) * (a0 + (a1 - a0) * fx) + fy * (b0 + (b1 - b0) * fx);
                }
                if (Math.Abs(after.Pixels[at + c] - expected) > SameLevels) return true;
            }
            return false;
        }

        /// <summary>The two pixels around position <paramref name="s"/> (in pixel centres) and the weight of the second, clamped at the edges.</summary>
        private static (int A, int B, double F) Bilinear(double s, int size)
        {
            if (s <= 0) return (0, 0, 0);
            if (s >= size - 1) return (size - 1, size - 1, 0);
            var a = (int)Math.Floor(s);
            return (a, a + 1, s - a);
        }

        /// <summary>Pixels of a row of <paramref name="after"/> that differ from the row of <paramref name="before"/> under them by more than <see cref="SameLevels"/>.</summary>
        private static long DifferingPixels(DecodedImage after, int y, DecodedImage before, int row, int bpp, bool smooth)
        {
            if (smooth)
            {
                long count = 0;
                for (var x = 0; x < after.Width; x++)
                    if (Differs(after, x, y, before, bpp, true)) count++;
                return count;
            }
            long differing = 0;
            var same = after.Width == before.Width;
            var to = after.Pixels.AsSpan(y * after.Width * bpp, after.Width * bpp);
            var from = before.Pixels.AsSpan(row * before.Width * bpp, before.Width * bpp);
            for (var x = 0; x < after.Width; x++)
            {
                var column = same ? x : Math.Min(before.Width - 1, (int)((x + 0.5) * before.Width / after.Width));
                for (var c = 0; c < bpp; c++)
                    if (Math.Abs(to[x * bpp + c] - from[column * bpp + c]) > SameLevels)
                    {
                        differing++;
                        break;
                    }
            }
            return differing;
        }

        /// <summary>Pixels an image is reduced to for the check of its blacked-out copy.</summary>
        private const long CheckPixels = 1_000_000;

        /// <summary>
        /// Samples the checks of blacked-out copies (<see cref="ReadsAsBlanked"/>) may read on one page, with their masks: half a gigasample,
        /// about five seconds — ten scans of fifty megapixels (No. 463).
        /// </summary>
        internal const long CheckSamplesPerPage = 500_000_000;

        private long _checkSamples = CheckSamplesPerPage;

        // The reduced reading of an original once per page: a slide drew the same tiny image with a huge mask eleven times, and reading the
        // mask anew for every drawing cost more than the redaction itself
        private readonly Dictionary<(PdfStream, PdfDictionary?), DecodedImage?> _reduced = new(DecodedKey.Instance);

        private DecodedImage? DecodeReducedOnce(PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool stencil)
        {
            if (stream is null) return PdfImageDecoder.DecodeReduced(new ImageSource(null, inline, resources, stencil, 0xFF000000), doc, CheckPixels);
            if (_reduced.TryGetValue((stream, resources), out var known)) return known;
            return _reduced[(stream, resources)] = PdfImageDecoder.DecodeReduced(new ImageSource(stream, null, resources, stencil, 0xFF000000), doc, CheckPixels);
        }

        /// <summary>Levels within which two samples count as alike (a JPEG packed again blurs a little).</summary>
        private const int SameLevels = ImageSameLevels;

        /// <summary>Share of the pixels beside the areas that may differ by more than <see cref="SameLevels"/> (edges of a JPEG packed again).</summary>
        private const double DifferingShare = 0.02;

        /// <summary>
        /// The dictionary of an inline image as that of an image XObject (<see cref="InlineImage.AsXObjectDictionary"/>), with a colour space
        /// named in the page's resources (/CS /CS0, allowed inline) replaced by the space itself: an XObject reads no resource names there.
        /// </summary>
        private static PdfDictionary XObjectDictionary(InlineImage inline, PdfDictionary? resources)
        {
            var dict = inline.AsXObjectDictionary();
            if (dict.Get(PdfName.Get("ColorSpace")) is PdfName { Value: not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Indexed") } name &&
                resources?.GetDictionary(PdfName.Get("ColorSpace"))?.Get(name) is { } named and not PdfNull)
                dict.Set(PdfName.Get("ColorSpace"), named);
            return dict;
        }

        private static PdfStream EncodeImage(int w, int h, byte[] pixels, ImagePixels format, bool interpolate)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, XObjectName);
            dict.Set(PdfName.Subtype, PdfName.Get("Image"));
            dict.Set(PdfName.Get("Width"), PdfInteger.Of(w));
            dict.Set(PdfName.Get("Height"), PdfInteger.Of(h));
            if (interpolate) dict.Set(PdfName.Get("Interpolate"), PdfBoolean.True);
            switch (format)
            {
                case ImagePixels.Alpha:
                {
                    // 1-bit stencil: 0 = paint (where the coverage is at least one half)
                    var stride = (w + 7) / 8;
                    var bits = new byte[stride * h];
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++)
                            if (pixels[y * w + x] < 128) bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
                    dict.Set(PdfName.Get("ImageMask"), PdfBoolean.True);
                    dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(1));
                    return Packed(dict, bits);
                }
                case ImagePixels.Gray:
                    dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
                    dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
                    return Packed(dict, pixels);
                default:
                {
                    // Alpha only if there is transparency; compact the colours within the pixel array itself (RGBA → RGB) —
                    // otherwise a 20 MP photo would need another 80 MB
                    var count = w * h;
                    var transparent = false;
                    for (var i = 3; i < pixels.Length && !transparent; i += 4) transparent = pixels[i] < 255;
                    byte[]? alpha = null;
                    if (transparent)
                    {
                        alpha = new byte[count];
                        for (var i = 0; i < count; i++) alpha[i] = pixels[i * 4 + 3];
                    }
                    for (var i = 0; i < count; i++)
                    {
                        pixels[i * 3] = pixels[i * 4];
                        pixels[i * 3 + 1] = pixels[i * 4 + 1];
                        pixels[i * 3 + 2] = pixels[i * 4 + 2];
                    }
                    var rgb = pixels.AsSpan(0, count * 3);
                    dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
                    dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
                    if (alpha is not null)
                    {
                        var mask = new PdfDictionary();
                        mask.Set(PdfName.Type, XObjectName);
                        mask.Set(PdfName.Subtype, PdfName.Get("Image"));
                        mask.Set(PdfName.Get("Width"), PdfInteger.Of(w));
                        mask.Set(PdfName.Get("Height"), PdfInteger.Of(h));
                        mask.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
                        mask.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
                        dict.Set(PdfName.Get("SMask"), Packed(mask, alpha));
                    }
                    return Packed(dict, rgb);
                }
            }
        }

        private static PdfStream Packed(PdfDictionary dict, ReadOnlySpan<byte> data)
        {
            using var packed = new MemoryStream(Math.Max(256, data.Length / 4));
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            return new PdfStream(dict, packed.ToArray());
        }

        // -------------------------------------------------- Forms

        // Forms (object numbers) currently being rewritten: the chain of invocations up to here
        private readonly HashSet<PdfObjectId> _rewriting = [];

        /// <summary>Blacked-out copies per image, place and resources they were entered in: their name there.</summary>
        private readonly Dictionary<(PdfStream Image, Matrix ToPage, PdfDictionary Resources), PdfName> _blankedImages = new(BlankedKey.Instance);

        /// <summary>Forms copied only without their descriptions (<see cref="Scope.OnlyDescriptions"/>): one copy per form and resources (No. 624).</summary>
        private readonly Dictionary<(PdfStream Stream, PdfDictionary? Resources), PdfName> _strippedForms = new(DecodedKey.Instance);

        private sealed class BlankedKey : IEqualityComparer<(PdfStream Image, Matrix ToPage, PdfDictionary Resources)>
        {
            public static readonly BlankedKey Instance = new();

            public bool Equals((PdfStream Image, Matrix ToPage, PdfDictionary Resources) a, (PdfStream Image, Matrix ToPage, PdfDictionary Resources) b) =>
                ReferenceEquals(a.Image, b.Image) && a.ToPage == b.ToPage && ReferenceEquals(a.Resources, b.Resources);

            public int GetHashCode((PdfStream Image, Matrix ToPage, PdfDictionary Resources) key) =>
                HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Image), key.ToPage,
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Resources));
        }

        /// <summary>Rebuilds a form for this one invocation: its own content, its own resources.</summary>
        private PdfStream RewriteForm(PdfStream form, Scope scope, PdfDictionary parentResources)
        {
            var formResources = CopyResources(form.Dictionary.GetDictionary(PdfName.Resources) ?? _pageResources ?? parentResources);
            var content = StreamContent(doc, form);
            var body = Rewrite(content, scope, formResources, pruneUnused: true);
            var dict = new PdfDictionary { Resolver = form.Dictionary.Resolver };
            foreach (var (key, value) in form.Dictionary.Entries)
                // Without data of other programs, metadata and associated files: they describe the unredacted form (such as /PieceInfo
                // with the whole graphic of a drawing program, or the data behind a chart) and would otherwise bypass sanitising; /OPI and
                // /Ref point to the original file (docs/FEHLERGEDAECHTNIS.md, No. 434)
                if (key.Value is not ("Length" or "Filter" or "DecodeParms" or "Resources") && !DescribesOld(key)) dict.Set(key, value);
            dict.Set(PdfName.Resources, formResources);
            // moved or new content inside: the box (which clips the form) grows to hold it
            if (scope.GrowBox is { } grow && PdfRectangle.From(form.Dictionary.Get(PdfName.Get("BBox"))) is { } box)
            {
                var held = RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top).Union(grow);
                if (double.IsFinite(held.Left) && double.IsFinite(held.Top) && double.IsFinite(held.Right) && double.IsFinite(held.Bottom))
                    dict.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(held.Left), new PdfReal(held.Top), new PdfReal(held.Right), new PdfReal(held.Bottom)]));
            }
            // the form it stands for (one of the file, or the one a copy stood for): the writer points the structure at the first copy (6.6 a);
            // whether characters left it — then the replacement texts of its elements go (fewer bytes shown than before; the rewriter adds no text)
            return new PdfStream(dict, body) { CopyOf = form.Id?.Number ?? form.CopyOf, LostText = form.LostText || ShownBytes(body) < ShownBytes(content) };
        }

        /// <summary>The bytes of the strings the text operators of a content show (Tj, ', ", TJ) — fewer after rewriting: characters were removed.</summary>
        private static long ShownBytes(ReadOnlySpan<byte> content)
        {
            var parser = new ContentParser(content.ToArray());
            long total = 0;
            string? op;
            while ((op = parser.Next()) is not null)
                switch (op)
                {
                    case "Tj" or "'" or "\"" when parser.Operands.Count > 0 && parser.Operands[^1].Object is PdfString shown:
                        total += shown.Bytes.Length;
                        break;
                    case "TJ" when parser.Operands.Count > 0 && parser.Operands[0].Object is PdfArray parts:
                        foreach (var part in parts)
                            if (part is PdfString piece) total += piece.Bytes.Length;
                        break;
                }
            return total;
        }

        /// <summary>
        /// Writes the kept subpaths with the painting operator, each one that reaches under an area with that part cut out of its
        /// geometry (<see cref="PathCutter"/>; docs/FEHLERGEDAECHTNIS.md, No. 168) — the clip around the areas alone left its coordinates
        /// under them in the file. Filling and stroking need different cuts: an operator that does both is written as a fill followed by
        /// a stroke. The pieces of a dashed stroke are stroked one by one, each starting into the pattern where it lay.
        /// </summary>
        private void WriteCut(MemoryStream output, PdfDictionary resources, MemoryStream path, List<(int Start, RectD Bounds, bool Empty)> subpaths, List<int> kept,
            Matrix ctm, string op, IReadOnlyList<Operand> operands, (double Reach, DashPattern? Dash) stroke, (double Fill, double Stroke, byte Blend, StrokeStyle Style)? pair)
        {
            var buffer = path.GetBuffer();
            ReadOnlySpan<byte> Bytes(int s) => buffer.AsSpan(subpaths[s].Start, (s + 1 < subpaths.Count ? subpaths[s + 1].Start : (int)path.Length) - subpaths[s].Start);
            // s, b and b* close the current subpath (the last) before painting
            bool Closes(int s) => op is "s" or "b" or "b*" && s == subpaths.Count - 1;
            var fills = op is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*";
            var strokes = op is "S" or "s" or "B" or "B*" or "b" or "b*";
            string? Cut(int s, bool forStroke)
            {
                if (subpaths[s].Empty || !Hits(rects, ctm.TransformBounds(subpaths[s].Bounds).Inflate(0.05))) return null;
                byte[] bytes = forStroke && Closes(s) ? [.. Bytes(s), .. "h\n"u8] : Bytes(s).ToArray();
                if (forStroke && stroke.Dash is { } dash)
                {
                    // dashed: each piece with its own start into the pattern, the length along the path to where it begins, so that its
                    // dashes lie where they were (the pattern starts anew with every subpath). The lengths as they were read: rounded,
                    // their error adds up period by period — a dotted leader 1.99945 apart written as 1.999 moved its dots right of a bar
                    // by 0.1 pt (docs/FEHLERGEDAECHTNIS.md, No. 871)
                    if (PathCutter.CutDashed(bytes, ctm, rects, stroke.Reach) is not { } pieces) return null;
                    var sb = new StringBuilder();
                    var lengths = "[" + string.Join(" ", dash.Lengths.Select(DashNumber)) + "] ";
                    foreach (var (ops, offset) in pieces)
                        // the phase is reckoned along the path, not read: to a millionth (its error does not repeat)
                        sb.Append("q ").Append(lengths).Append(PdfSerializer.FormatReal(Math.Round(dash.Phase + offset, 6))).Append(" d\n").Append(ops).Append("S\nQ\n");
                    return sb.ToString();
                }
                return PathCutter.Cut(bytes, ctm, rects, forStroke, stroke.Reach);
            }
            var fillCuts = fills ? kept.ToDictionary(s => s, s => Cut(s, false)) : [];
            var strokeCuts = strokes ? kept.ToDictionary(s => s, s => Cut(s, true)) : [];
            if (fillCuts.Values.All(c => c is null) && strokeCuts.Values.All(c => c is null))
            {
                foreach (var s in kept) output.Write(Bytes(s));
                WriteOperator(output, operands, op);
                return;
            }
            void Pass(Dictionary<int, string?> cuts, bool forStroke, ReadOnlySpan<byte> painting)
            {
                // the pieces of a dashed stroke paint themselves (each its own pattern start), after the rest
                var selfPainting = forStroke && stroke.Dash is not null;
                foreach (var s in kept)
                {
                    if (cuts[s] is { } cut && !selfPainting) output.Write(Encoding.ASCII.GetBytes(cut));
                    else if (cuts[s] is null)
                    {
                        output.Write(Bytes(s));
                        if (forStroke && Closes(s)) output.Write("h\n"u8);
                    }
                }
                if (!selfPainting || kept.Any(s => cuts[s] is null)) output.Write(painting);
                if (selfPainting)
                    foreach (var s in kept)
                        if (cuts[s] is { } cut) output.Write(Encoding.ASCII.GetBytes(cut));
            }
            if (fills && strokes && pair is { } transparent)
            {
                // With opacity or a blend mode, B paints as a knockout group: the stroke replaces its own fill instead of blending with
                // it (ISO 32000-2, 11.7.4.4). Written as two passes, the stroke blended with the fill beneath it and the edge changed
                // colour beside the bar (docs/FEHLERGEDAECHTNIS.md, No. 176). So both passes go into a knockout group, drawn opaquely;
                // inside it, the opacities and the blend mode of the path apply to each pass
                var outer = output;
                output = new MemoryStream();
                var inner = new PdfDictionary();
                inner.Set(PdfName.Get("ca"), new PdfReal(transparent.Fill));
                inner.Set(PdfName.Get("CA"), new PdfReal(transparent.Stroke));
                inner.Set(PdfName.Get("BM"), PdfName.Get(BlendModes.Name(transparent.Blend)));
                output.Write("/GS0 gs\n"u8);
                // the stroke parameters stated in the group as the display uses them: a group inherits them (ISO 32000-2, 8.10.1), but a
                // reference reader drew the stroke of such a copy as a hairline (docs/FEHLERGEDAECHTNIS.md, No. 410)
                var style = transparent.Style;
                static string Exact(double v) => PdfSerializer.FormatReal(Math.Round(v, 6));
                output.Write(Encoding.ASCII.GetBytes($"{Exact(style.Width)} w {style.Cap} J {style.Join} j {Exact(style.MiterLimit)} M " +
                    (style.Dash is { } dashed ? "[" + string.Join(" ", dashed.Lengths.Select(DashNumber)) + "] " + DashNumber(dashed.Phase) : "[] 0") + " d\n"));
                Pass(fillCuts, false, op is "B*" or "b*" ? "f*\n"u8 : "f\n"u8);
                Pass(strokeCuts, true, "S\n"u8);
                var group = new PdfDictionary();
                group.Set(PdfName.Type, XObjectName);
                group.Set(PdfName.Subtype, PdfName.Get("Form"));
                group.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(-100_000), PdfInteger.Of(-100_000), PdfInteger.Of(100_000), PdfInteger.Of(100_000)]));
                group.Set(PdfName.Get("Group"), Dictionary(("Type", PdfName.Get("Group")), ("S", PdfName.Get("Transparency")), ("K", PdfBoolean.True)));
                group.Set(PdfName.Resources, Dictionary(("ExtGState", Dictionary(("GS0", inner)))));
                var opaque = Dictionary(("ca", PdfInteger.Of(1)), ("CA", PdfInteger.Of(1)), ("BM", PdfName.Get("Normal")));
                outer.Write("q\n"u8);
                WriteResource(outer, resources, ExtGStateName, opaque, " gs\n"u8);
                WriteResource(outer, resources, XObjectName, new PdfStream(group, output.ToArray()), " Do\n"u8);
                outer.Write("Q\n"u8);
            }
            else if (fills && strokes)
            {
                Pass(fillCuts, false, op is "B*" or "b*" ? "f*\n"u8 : "f\n"u8);
                Pass(strokeCuts, true, "S\n"u8);
            }
            else if (fills) Pass(fillCuts, false, Encoding.ASCII.GetBytes(op + "\n"));
            else Pass(strokeCuts, true, "S\n"u8);   // "s" closed its subpath above; the open pieces must not be closed
        }

        /// <summary>Writes the subpaths of a clip, cut as for filling where they reach under an area, with its W or W*.</summary>
        private void WriteClipCut(MemoryStream output, MemoryStream path, List<(int Start, RectD Bounds, bool Empty)> subpaths, Matrix ctm)
        {
            var buffer = path.GetBuffer();
            var parser = new ContentParser(buffer.AsSpan(0, (int)path.Length).ToArray());
            var clip = "W";
            while (parser.Next() is { } o)
                if (o is "W" or "W*") clip = o;
            var start = output.Length;
            for (var s = 0; s < subpaths.Count; s++)
            {
                var bytes = buffer.AsSpan(subpaths[s].Start, (s + 1 < subpaths.Count ? subpaths[s + 1].Start : (int)path.Length) - subpaths[s].Start);
                // a subpath that is only a point (a lone „m“ after a closed shape) adds nothing to the clip, but its coordinate is the start
                // of the shape: under an area it goes (docs/FEHLERGEDAECHTNIS.md, No. 455)
                if (!subpaths[s].Empty && subpaths[s].Bounds is { Width: 0, Height: 0 } point &&
                    Hits(rects, ctm.TransformBounds(point).Inflate(0.05)))
                    continue;
                // only a subpath with a point under an area is cut (the invariant, No. 449): one whose outline merely passes an area kept
                // its shape anyway, written anew with two more corners on its edge — a rectangle (re) became a path, and the renderer
                // smoothed the edge of the image it clips otherwise (DocLayNet 19ee9956; docs/FEHLERGEDAECHTNIS.md, No. 873)
                var cut = subpaths[s].Empty || !Hits(rects, ctm.TransformBounds(subpaths[s].Bounds).Inflate(0.05))
                    || !PathCutter.PointUnder(bytes, ctm, rects, ClipPointSeam) ? null : PathCutter.Cut(bytes, ctm, rects, false, 0);
                if (cut is null) WriteWithout(output, bytes);
                else output.Write(Encoding.ASCII.GetBytes(cut));
            }
            // A clip entirely under the areas is cut to nothing: then it clips to nothing (what it let through lay under the bar). Without
            // any path, "W n" clipped nothing at all, and a shading behind it covered the whole page (docs/FEHLERGEDAECHTNIS.md, No. 175)
            if (output.Length == start) output.Write("0 0 0 0 re\n"u8);
            output.Write(Encoding.ASCII.GetBytes(clip + "\n"));

            // the subpath as written, without the W or W* in it (written once after all of them)
            static void WriteWithout(MemoryStream output, ReadOnlySpan<byte> bytes)
            {
                var p = new ContentParser(bytes.ToArray());
                while (p.Next() is { } o)
                    if (o is not ("W" or "W*")) WriteOperator(output, p.Operands, o);
            }
        }

        /// <summary>
        /// The redacted copy of the tiling pattern the path operator paints with (its cell, <see cref="Scope.PatternCells"/>), with what
        /// is under the areas removed as in a form; null if nothing in it is affected. The original is written only if something else
        /// still paints with it.
        /// </summary>
        /// <summary>Two moves of patterns one after the other: <paramref name="inner"/> (of the object), then <paramref name="outer"/> (of its block).</summary>
        private static Matrix? Along(Matrix? inner, Matrix? outer) =>
            inner is { } i ? outer is { } o ? i * o : i : outer;

        /// <summary>Whether a Do or BI paints a stencil mask (an image mask: painted in the fill colour, which may be a pattern).</summary>
        private static bool Stencil(string op, IReadOnlyList<Operand> operands, InlineImage? inline, PdfDictionary resources)
        {
            var dict = op == "BI"
                ? inline?.Dictionary
                : operands.Count > 0 && operands[0].Name is { } name ? (resources.GetDictionary(XObjectName)?.Get(name) as PdfStream)?.Dictionary : null;
            return dict is not null && (dict.GetBoolean(ImageMaskName) ?? dict.GetBoolean(InlineMaskName)) == true;
        }

        /// <summary>
        /// Sets the pattern colour <paramref name="op"/> paints with again for fill or stroke, from a copy: <paramref name="cell"/> (a
        /// redacted cell) and/or the pattern with <paramref name="move"/> appended to its matrix — with the colour space and colour values it
        /// was set with. With <paramref name="restore"/>, what sets the original colour again afterwards is written there. Nothing if the
        /// operator paints no pattern or there is nothing to change.
        /// </summary>
        private void WritePatternColour(MemoryStream output, PdfDictionary resources, ResourceUsage.PatternState paint, string op, bool stroke,
            PdfStream? cell, Matrix? move, MemoryStream? restore)
        {
            if ((stroke ? paint.StrokePattern(op) : paint.FillPattern(op)) is not { } name || cell is null && move is null) return;
            var (space, values, _) = paint.Colour(stroke);
            PdfObject? copy = cell;
            if (move is { } m)
            {
                var source = cell ?? resources.GetDictionary(PatternName)?.Get(name) switch
                {
                    PdfStream tiling => CopyPattern(tiling),
                    PdfDictionary shading => CopyPattern(shading),
                    _ => null,
                };
                var dict = (source as PdfStream)?.Dictionary ?? source as PdfDictionary;
                if (dict is null || ((Matrix.FromArray(dict.GetArray(MatrixName)) ?? Matrix.Identity) * m) is not { IsFinite: true } matrix) return;
                dict.Set(MatrixName, new PdfArray([new PdfReal(matrix.A), new PdfReal(matrix.B), new PdfReal(matrix.C), new PdfReal(matrix.D),
                    new PdfReal(matrix.E), new PdfReal(matrix.F)]));
                copy = source;
            }
            if (copy is null) return;
            void Colour(MemoryStream target)
            {
                // a colour space set in this content again (cs resets the colour); one set before it (a form's caller) is still in effect
                if (space is not null)
                {
                    PdfSerializer.Write(space, target);
                    target.Write(stroke ? " CS "u8 : " cs "u8);
                }
                foreach (var value in values)
                {
                    WriteNumber(target, value.Number);
                    target.WriteByte((byte)' ');
                }
            }
            Colour(output);
            WriteResource(output, resources, PatternName, copy, stroke ? " SCN\n"u8 : " scn\n"u8);
            if (restore is null) return;
            Colour(restore);
            PdfSerializer.Write(name, restore);
            restore.Write(stroke ? " SCN\n"u8 : " scn\n"u8);
        }

        /// <summary>A copy of a pattern to change (its stream decoded, the same resources).</summary>
        private PdfObject CopyPattern(PdfObject pattern)
        {
            var original = (pattern as PdfStream)?.Dictionary ?? (PdfDictionary)pattern;
            var dict = new PdfDictionary { Resolver = original.Resolver };
            foreach (var (key, value) in original.Entries)
                if (pattern is not PdfStream || key.Value is not ("Length" or "Filter" or "DecodeParms")) dict.Set(key, value);
            return pattern is PdfStream stream ? new PdfStream(dict, StreamContent(doc, stream)) : dict;
        }

        private PdfStream? CellCopy(Scope scope, int op, bool stroke)
        {
            if (!scope.PatternCells.TryGetValue((op, stroke), out var cell) || !cell.Scope.HasWork) return null;
            var pattern = cell.Pattern;
            // a pattern without resources of its own draws with those of the page (No. 791; before, with none — while the display lent none either)
            var cellResources = CopyResources(pattern.Dictionary.GetDictionary(PdfName.Resources) ?? _pageResources ?? new PdfDictionary());
            var body = Rewrite(StreamContent(doc, pattern), cell.Scope, cellResources, pruneUnused: true);
            var dict = new PdfDictionary { Resolver = pattern.Dictionary.Resolver };
            foreach (var (key, value) in pattern.Dictionary.Entries)
                // the redacted cell: without what describes the old cell (XMP, associated files; docs/FEHLERGEDAECHTNIS.md, No. 444)
                if (key.Value is not ("Length" or "Filter" or "DecodeParms" or "Resources") && !DescribesOld(key)) dict.Set(key, value);
            dict.Set(PdfName.Resources, cellResources);
            if (pattern.Id is { } id) ReplacedObjects.Add(id.Number);
            return new PdfStream(dict, body);
        }

        // -------------------------------------------------- Output

        /// <summary>Adds a new XObject under a free name to the resources and "/Name Do" to the content; returns the name.</summary>
        private PdfName WriteDo(MemoryStream output, PdfDictionary resources, PdfStream xobject) => WriteResource(output, resources, XObjectName, xobject, " Do\n"u8);

        /// <summary>"a b c d e f cm" (only finite matrices are ever written; a non-finite one writes nothing).</summary>
        private static void WriteMatrix(MemoryStream output, Matrix m)
        {
            if (!m.IsFinite) return;
            WriteOperator(output, [new Operand(m.A), new Operand(m.B), new Operand(m.C), new Operand(m.D), new Operand(m.E), new Operand(m.F)], "cm");
        }

        /// <summary>New images before an operator: each in q … Q, cut to its clip (four corners in the space there) if it has one.</summary>
        /// <summary>The clips to set before an operator (<see cref="ContentEditPlan.ClipsBefore"/>), those with finite corners; null if none.</summary>
        private static List<(double X, double Y)[]>? ClipsBefore(Scope scope, int op) =>
            scope.ClipsBefore.TryGetValue(op, out var clips) && clips.Where(c => c.Length >= 3 && c.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y))).ToList() is
                { Count: > 0 } usable ? usable : null;

        /// <summary>A clip to the polygon (corners in the space there): "m … l … h W n".</summary>
        private static void WriteClip(MemoryStream output, (double X, double Y)[] corners)
        {
            WriteOperator(output, [new Operand(corners[0].X), new Operand(corners[0].Y)], "m");
            for (var i = 1; i < corners.Length; i++) WriteOperator(output, [new Operand(corners[i].X), new Operand(corners[i].Y)], "l");
            output.Write("h W n\n"u8);
        }

        private void WriteInserts(MemoryStream output, PdfDictionary resources, List<(PdfStream Image, Matrix Place, (double X, double Y)[]? Clip)> inserts)
        {
            foreach (var (image, place, clip) in inserts)
            {
                if (!place.IsFinite || clip is not null && clip.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) continue;
                output.Write("q\n"u8);
                if (clip is { Length: >= 3 }) WriteClip(output, clip);
                WriteMatrix(output, place);
                WriteDo(output, resources, image);
                output.Write("Q\n"u8);
            }
        }

        /// <summary>Adds a new resource (XObject, graphics state) under a free name to the resources, and "/Name" with the operator to the content; returns the name.</summary>
        private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] entries)
        {
            var dict = new PdfDictionary();
            foreach (var (key, value) in entries) dict.Set(PdfName.Get(key), value);
            return dict;
        }

        private PdfName WriteResource(MemoryStream output, PdfDictionary resources, PdfName category, PdfObject value, ReadOnlySpan<byte> op)
        {
            var existing = resources.GetDictionary(category);
            var entries = new PdfDictionary { Resolver = resources.Resolver };
            if (existing is not null)
                foreach (var (key, item) in existing.Entries) entries.Set(key, item);
            PdfName name;
            do name = PdfName.Get($"PKr{++_names}");
            while (entries.ContainsKey(name));
            entries.Set(name, value);
            resources.Set(category, entries);
            PdfSerializer.Write(name, output);
            output.Write(op);
            return name;
        }

        private static void WriteInlineImage(MemoryStream output, InlineImage inline)
        {
            output.Write("BI\n"u8);
            foreach (var (key, value) in inline.Dictionary.Entries)
            {
                if (InlineImage.ExpandedKey(inline.Dictionary, key)) continue;
                PdfSerializer.Write(key, output);
                output.WriteByte((byte)' ');
                PdfSerializer.Write(value, output);
                output.WriteByte((byte)'\n');
            }
            output.Write("ID "u8);
            output.Write(inline.Data);
            output.Write("\nEI\n"u8);
        }

        /// <summary>Editing: the operators the plan names (where the rewriter changes something), of this content only.</summary>
        private static HashSet<int> Named(Scope scope)
        {
            var named = new HashSet<int>(scope.Transforms.Keys);
            named.UnionWith(scope.PatternMoves.Keys);
            named.UnionWith(scope.Paths.Keys);
            named.UnionWith(scope.Dropped);
            named.UnionWith(scope.Inserts.Keys);
            named.UnionWith(scope.ClipsBefore.Keys);
            named.UnionWith(scope.Forms.Keys);
            named.UnionWith(scope.Glyphs.Keys);
            named.UnionWith(scope.Images.Keys);
            named.UnionWith(scope.Shadings.Keys);
            named.UnionWith(scope.Masks.Keys);
            named.UnionWith(scope.ClipPaths.Keys);
            named.UnionWith(scope.PatternCells.Keys.Select(k => k.Op));
            return named;
        }

        /// <summary>
        /// Editing: writes the original bytes from <paramref name="from"/> up to <paramref name="to"/>, with a line break, so that the next
        /// operator stays apart; nothing if none are pending (-1). Afterwards none are.
        /// </summary>
        private static void CopyOriginal(MemoryStream output, byte[] content, ref int from, int to)
        {
            if (from < 0) return;
            if (to > from)
            {
                output.Write(content, from, to - from);
                output.WriteByte((byte)'\n');
            }
            from = -1;
        }

        private static void WriteOperator(MemoryStream output, IReadOnlyList<Operand> operands, string op)
        {
            foreach (var operand in operands)
            {
                if (operand.Object is { } obj and not (PdfInteger or PdfReal)) PdfSerializer.Write(obj, output);
                else WriteNumber(output, operand.Number);
                output.WriteByte((byte)' ');
            }
            Span<byte> name = stackalloc byte[16];
            if (op.Length <= name.Length && Encoding.ASCII.TryGetBytes(op, name, out var length)) output.Write(name[..length]);
            else output.Write(Encoding.ASCII.GetBytes(op));
            output.WriteByte((byte)'\n');
        }

        /// <summary>
        /// A number exactly (the shortest form that reads back as the same value): rounding an operand would move or scale everything after it
        /// — a matrix with a tiny scale drew its text a fifth smaller (docs/FEHLERGEDAECHTNIS.md, No. 132). Written as
        /// <see cref="PdfSerializer.FormatReal"/> writes it (whole numbers without a point), formatted on the stack: every number of the
        /// content passes here, a plan has millions.
        /// </summary>
        private static void WriteNumber(MemoryStream output, double value)
        {
            Span<byte> buffer = stackalloc byte[32];
            if (value == Math.Floor(value) && Math.Abs(value) < 1e9 && ((long)value).TryFormat(buffer, out var length, default, CultureInfo.InvariantCulture))
            {
                output.Write(buffer[..length]);
                return;
            }
            // "R" as FormatReal; with an exponent, or not finite, its own way
            if (double.IsFinite(value) && value.TryFormat(buffer, out length, "R", CultureInfo.InvariantCulture) && buffer[..length].IndexOf((byte)'E') < 0)
            {
                output.Write(length == 2 && buffer[0] == '-' && buffer[1] == '0' ? "0"u8 : buffer[..length]);
                return;
            }
            output.Write(Encoding.ASCII.GetBytes(PdfSerializer.FormatReal(value)));
        }
    }
}
