using System.Text;

namespace PdfKnead.Core.Pdf.Text;

public static partial class TextBlocks
{
    // plus the bullets that office programs set in Symbol and Wingdings — without ToUnicode in the private use area (F0B7 dot, F0A7 square, F0D8 arrow,
    // F0FC check mark, F076 diamond, F06E square, F0A8 circle); written as escapes, because as invisible characters they would be lost when saving
    private static readonly string[] Bullets =
        ["•", "·", "▪", "■", "●", "◦", "○", "–", "-", "—", "*", "►", "▶", "✓", "✔", "\uF0B7", "\uF0A7", "\uF0D8", "\uF0FC", "\uF076", "\uF06E", "\uF0A8"];

    /// <summary>
    /// List items: the segment starts with a marker — characters such as "•", numbering such as "1.", "1.2.3", "a)", "(iv)", "[12]", "[0012]" —
    /// followed by a gap or a space; <see cref="Segment.BulletIndent"/> is then the start of the text.
    /// </summary>
    private sealed class MarkerStep : ILayoutStep
    {
        public string Name => "Aufzählungen";

        public bool Optional => true;

        public void Run(LayoutFrame frame)
        {
            foreach (var segment in frame.Segments) segment.BulletIndent = BulletIndent(segment);
        }

        private static double? BulletIndent(Segment segment)
        {
            var glyphs = segment.Glyphs;
            if (Superscript(glyphs) is { } raised) return raised;
            var marker = new StringBuilder();
            for (var i = 0; i < Math.Min(glyphs.Count - 1, 8); i++)
            {
                var text = glyphs[i].Glyph.Text ?? "";
                if (string.IsNullOrWhiteSpace(text))
                {
                    // space after the marker: the text starts at the first character after it
                    var next = i + 1;
                    while (next < glyphs.Count && string.IsNullOrWhiteSpace(glyphs[next].Glyph.Text)) next++;
                    return next < glyphs.Count && Accept(marker.ToString(), glyphs[next].Start - glyphs[i - 1 < 0 ? 0 : i - 1].End, segment.Size)
                        ? glyphs[next].Start : null;
                }
                marker.Append(text);
                if (glyphs[i + 1].Start - glyphs[i].End >= 0.25 * segment.Size)
                    return !string.IsNullOrWhiteSpace(glyphs[i + 1].Glyph.Text) && Accept(marker.ToString(), glyphs[i + 1].Start - glyphs[i].End, segment.Size)
                        ? glyphs[i + 1].Start : null;
            }
            return null;
        }

        /// <summary>
        /// Marker with the gap to the text: a capital letter with a full stop at the start of a line is usually an initial ("D. Surname"); it
        /// counts as a marker only with clearly more space than a word space ("A.   Introduction").
        /// </summary>
        private static bool Accept(string marker, double gap, double size) =>
            IsMarker(marker) && !(marker.Length == 2 && char.IsUpper(marker[0]) && marker[1] == '.' && gap < 0.6 * size);

        /// <summary>Superscript number before the text (bibliography "¹ A. B. Surname …"): the start of the text after it.</summary>
        private static double? Superscript(List<Placed> glyphs)
        {
            var digits = 0;
            while (digits < glyphs.Count && digits < 3 && glyphs[digits].Glyph.Text is [var c] && char.IsDigit(c)) digits++;
            if (digits == 0 || digits >= glyphs.Count) return null;
            var next = digits;
            while (next < glyphs.Count && string.IsNullOrWhiteSpace(glyphs[next].Glyph.Text)) next++;
            if (next >= glyphs.Count || glyphs[next].Glyph.Text is not [var t] || !char.IsLetter(t)) return null;
            var text = glyphs[next];
            var number = glyphs[digits - 1];
            var raised = number.Baseline - text.Baseline > 0.2 * text.Size || number.Size < 0.8 * text.Size && number.Top > text.Top - 0.1 * text.Size;
            return raised ? text.Start : null;
        }
    }

    /// <summary>Whether the start is a list marker.</summary>
    internal static bool IsMarker(string marker)
    {
        if (marker.Length == 0) return false;
        if (Bullets.Contains(marker)) return true;
        if (marker.Length > 8) return false;
        // (a) (iv) (12) [3] [0012]
        if (marker.Length >= 3 && (marker[0], marker[^1]) is ('(', ')') or ('[', ']')) return Counter(marker[1..^1]);
        // 1. 1.2.3 a) IV.
        return marker.Length >= 2 && marker[^1] is '.' or ')' && Counter(marker[..^1]);
    }

    /// <summary>Numbering: a number (also structured as "1.2.3"), a single letter or a Roman numeral.</summary>
    private static bool Counter(string s)
    {
        if (s.Length == 0) return false;
        if (s.Length == 1) return char.IsLetterOrDigit(s[0]);
        if (char.IsDigit(s[0]) && char.IsDigit(s[^1]) && s.All(c => char.IsDigit(c) || c == '.') && !s.Contains("..")) return true;
        return s.Length <= 6 && (s.All(c => "ivxlc".Contains(c)) || s.All(c => "IVXLC".Contains(c)));
    }
}
