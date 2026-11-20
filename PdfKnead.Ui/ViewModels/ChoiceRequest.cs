namespace PdfKnead.ViewModels;

/// <summary>
/// A question with any number of answers, a way back and optionally a box to tick (shown by the view as <c>ChoiceWindow</c>). The first
/// answer is the default one (Enter); Esc and the window's close button mean <see cref="Cancel"/>.
/// </summary>
/// <param name="Heading">The question.</param>
/// <param name="Message">What the answers mean.</param>
/// <param name="Answers">The answers, the default one first (at least one).</param>
/// <param name="Cancel">The text of the way back („Abbrechen“, „Weiter messen“ …).</param>
/// <param name="Check">The text of a box to tick („Nicht mehr fragen“); null: none.</param>
/// <param name="Icon">The key of the icon resource beside the heading; null: that of a general question.</param>
public sealed record ChoiceRequest(string Heading, string Message, IReadOnlyList<string> Answers, string Cancel, string? Check = null, string? Icon = null);

/// <summary>The answer to a <see cref="ChoiceRequest"/>.</summary>
/// <param name="Answer">1 for the first answer, 2 for the second …; 0 for the way back.</param>
/// <param name="Checked">Whether the box was ticked (false without one).</param>
public readonly record struct ChoiceAnswer(int Answer, bool Checked)
{
    /// <summary>The way back, nothing ticked.</summary>
    public static ChoiceAnswer Cancelled => new(0, false);
}
