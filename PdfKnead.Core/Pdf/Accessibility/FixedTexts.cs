using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// Texts PDF-Knead writes into the file itself for screen readers — in the language of the document, not of the program (a German letter gets
/// German words, any other document English; docs/ABLAUFPLAN.md, 6.3 decision H and 6.4 step 3): the neutral description of redacted
/// content, and what a new annotation is when it shows no text of its own. Never names of persons.
/// </summary>
internal static class FixedTexts
{
    /// <summary>Whether a language tag names German (de, de-DE, de-AT …).</summary>
    private static bool Is(string? tag, string code) =>
        tag is not null && tag.StartsWith(code, StringComparison.OrdinalIgnoreCase) && (tag.Length == code.Length || tag[code.Length] == '-');

    /// <summary>The neutral description of redacted content (docs/ABLAUFPLAN.md, 6.3, decision H).</summary>
    /// <param name="language">The language that applies (the element's own, else the document's); null: none.</param>
    /// <returns>The text, and the language to set on the element where the text is not in the language that applies (null: none needed).</returns>
    public static (string Text, string? Language) RedactedContent(string? language)
    {
        if (Is(language, "de")) return ("Geschwärzter Inhalt", null);
        return ("Redacted content", Is(language, "en") ? null : "en");
    }

    /// <summary>
    /// The neutral description of content whose text was edited (docs/FEHLERGEDAECHTNIS.md, No. 676): the old description could tell the old
    /// text, so it goes; a figure or formula needs one all the same (ISO 14289-1, 7.3 and 7.7).
    /// </summary>
    /// <param name="language">The language that applies (the element's own, else the document's); null: none.</param>
    /// <returns>The text, and the language to set on the element where the text is not in the language that applies (null: none needed).</returns>
    public static (string Text, string? Language) EditedContent(string? language)
    {
        if (Is(language, "de")) return ("Bearbeiteter Inhalt", null);
        return ("Edited content", Is(language, "en") ? null : "en");
    }

    /// <summary>
    /// A redacted page saved as its image („zusätzlich als Bild“; docs/ABLAUFPLAN.md, 6.6 g): „Seite 3 als Bild (geschwärzt)“ or „Page 3 as
    /// image (redacted)“.
    /// </summary>
    /// <param name="page">The page's number in the copy (from 1).</param>
    /// <param name="language">The document's language; null: none.</param>
    /// <returns>The text, and the language to set on the element where the text is not in the document's (null: none needed).</returns>
    public static (string Text, string? Language) PageAsImage(int page, string? language)
    {
        var number = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Is(language, "de")) return ($"Seite {number} als Bild (geschwärzt)", null);
        return ($"Page {number} as image (redacted)", Is(language, "en") ? null : "en");
    }

    /// <summary>What an image stamp shows: its alternative text, else „Bild“ or „Image“.</summary>
    public static string Image(ImageElement image, string? language) =>
        image.AltText.Trim() is { Length: > 0 } own ? own : Is(language, "de") ? "Bild" : "Image";

    /// <summary>A signature, fixed into the page: „Unterschrift“ or „Signature“ — never the name (ISO 14289-1, 7.13; J question 6).</summary>
    public static string Signature(string? language) => Is(language, "de") ? "Unterschrift" : "Signature";

    /// <summary>A visible digital signature (its widget's /TU and /Contents): „Digitale Signatur“ or „Digital signature“ — without the signer (J question 5).</summary>
    public static string DigitalSignature(string? language) => Is(language, "de") ? "Digitale Signatur" : "Digital signature";

    /// <summary>A freehand drawing: „Handschriftliche Zeichnung“ or „Hand drawing“.</summary>
    public static string Ink(string? language) => Is(language, "de") ? "Handschriftliche Zeichnung" : "Hand drawing";

    /// <summary>What a shape is: its kind (Rechteck, Pfeil … or Rectangle, Arrow …).</summary>
    public static string Shape(ShapeKind kind, string? language)
    {
        var german = Is(language, "de");
        return kind switch
        {
            ShapeKind.Rectangle => german ? "Rechteck" : "Rectangle",
            ShapeKind.Ellipse => german ? "Ellipse" : "Ellipse",
            ShapeKind.Line => german ? "Linie" : "Line",
            ShapeKind.Arrow => german ? "Pfeil" : "Arrow",
            ShapeKind.Triangle => german ? "Dreieck" : "Triangle",
            ShapeKind.Diamond => german ? "Raute" : "Diamond",
            ShapeKind.Star => german ? "Stern" : "Star",
            ShapeKind.Callout => german ? "Sprechblase" : "Speech bubble",
            _ => german ? "Form" : "Shape",
        };
    }

    /// <summary>A redaction mark: „Zum Schwärzen markiert“ or „Marked for redaction“, with the reason after a dash.</summary>
    /// <param name="reason">The reason as the mark carries it (code and text); null: none.</param>
    /// <param name="language">The document's language.</param>
    public static string RedactionMark(string? reason, string? language)
    {
        var what = Is(language, "de") ? "Zum Schwärzen markiert" : "Marked for redaction";
        return reason is { Length: > 0 } ? $"{what} — {reason}" : what;
    }
}
