using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Core.Forms.Recognition;

/// <summary>What a proposal was found on.</summary>
public enum ProposalSource
{
    /// <summary>A line to write on (drawn, or underscores and dots).</summary>
    Line,

    /// <summary>An empty rectangle.</summary>
    Box,

    /// <summary>An empty cell of a table.</summary>
    Cell,

    /// <summary>A small square or circle.</summary>
    CheckBox,

    /// <summary>A row of equal small boxes, one character each.</summary>
    Comb,

    /// <summary>Only the field detector saw it (nothing drawn on the page shows the field).</summary>
    Model,
}

/// <summary>
/// A proposed form field (KONZEPT.md, section 5.9a): where, of which kind with which properties, the label it was named after, how sure the
/// recognition is and what it was found on. A signature field has <see cref="FormFieldKind.Signature"/> as its kind.
/// </summary>
/// <param name="Page">Index of the page.</param>
/// <param name="Rect">The field in page space (unrotated).</param>
/// <param name="Spec">Kind, name, format and look (no border, no fill: the page draws them).</param>
/// <param name="Label">The text the field was named after; empty if none was found.</param>
/// <param name="Confidence">0 … 1.</param>
/// <param name="Source">What the field was found on.</param>
/// <param name="Part">For a comb line printed in groups with gaps (an IBAN in fours): the number of the group, from 1; 0 for a field on its own.</param>
/// <param name="Row">For a group: its line on the page (the groups of one line share their name with the group number appended).</param>
public sealed record FieldProposal(int Page, PdfRectangle Rect, FieldSpec Spec, string Label, double Confidence, ProposalSource Source, int Part = 0, int Row = 0);
