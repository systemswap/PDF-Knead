using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>A character to measure: its number (<see cref="DisplayList.GlyphTags"/>) and the bounds of its entries in page space.</summary>
/// <param name="Serial">Number of the character in content order.</param>
/// <param name="Bounds">Bounds of all entries it draws, in page space.</param>
public readonly record struct VisibilityCandidate(int Serial, RectD Bounds);

/// <summary>Result of <see cref="IVisibilityProbe.Measure"/>.</summary>
/// <param name="Hidden">Per candidate (same order): nobody sees it.</param>
/// <param name="Complete">False if a work limit left candidates unmeasured; they count as visible.</param>
public sealed record VisibilityMeasure(bool[] Hidden, bool Complete);

/// <summary>
/// Measures by drawing whether characters of a page can be seen (KONZEPT.md, section 5.18): a character is hidden if leaving it out
/// changes no pixel noticeably — and leaving out all characters found hidden together changes nothing either, so that removing them
/// never alters what the page shows. Implemented by the renderer; the core library only chooses what to measure
/// (<see cref="Editing.TextVisibility"/>).
/// </summary>
public interface IVisibilityProbe
{
    /// <summary>Measures the candidates of a page.</summary>
    /// <param name="list">Display list of the page, built with tagging (<see cref="DisplayList.GlyphTags"/>).</param>
    /// <param name="doc">The document the list belongs to.</param>
    /// <param name="page">The page.</param>
    /// <param name="candidates">The characters to measure.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>Which of them are hidden.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    VisibilityMeasure Measure(DisplayList list, PdfDocument doc, PdfPage page, IReadOnlyList<VisibilityCandidate> candidates, CancellationToken cancel);

    /// <summary>
    /// Where two builds of the same page differ — for characters in the cells of tiling patterns, which have no number of their own:
    /// the page is built again without them and compared, area by area.
    /// </summary>
    /// <param name="before">The page as it is.</param>
    /// <param name="after">The page built without some characters.</param>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="areas">Where to compare, in page space.</param>
    /// <param name="joint">As strict as the joint check of <see cref="Measure"/> (removing them must not even soften an edge); otherwise
    /// as the check of single characters.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>Per area: something differs there; null if it could not be measured (counts as visible, incomplete).</returns>
    bool[]? Differences(DisplayList before, DisplayList after, PdfDocument doc, PdfPage page, IReadOnlyList<RectD> areas, bool joint,
        CancellationToken cancel) => null;
}
