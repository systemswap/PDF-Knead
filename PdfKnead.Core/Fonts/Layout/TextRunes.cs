using System.Buffers;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Reading characters from text that may come from a file — a /ToUnicode or /ActualText of a prepared PDF can hold unpaired surrogates: an
/// unpaired surrogate reads as U+FFFD, as .NET's decoders read it, instead of throwing (Rune.GetRuneAt and char.ConvertToUtf32 throw on it).
/// The text itself stays as it is; only what layout, shaping and drawing read of it is safe (docs/FEHLERGEDAECHTNIS.md, No. 1192: the fuzz
/// fuzz-m7b found exceptions in the text editing of such files).
/// </summary>
internal static class TextRunes
{
    /// <summary>The character at UTF-16 index <paramref name="index"/>; U+FFFD for an unpaired surrogate or an index outside the text.</summary>
    public static Rune RuneAt(string text, int index) =>
        index >= 0 && index < text.Length && Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out _) == OperationStatus.Done
            ? rune : Rune.ReplacementChar;

    /// <summary>The first character of the text; U+FFFD for an empty text or an unpaired surrogate.</summary>
    public static Rune First(string text) => RuneAt(text, 0);

    /// <summary>The last character of the text; U+FFFD for an empty text or an unpaired surrogate.</summary>
    public static Rune Last(string text) =>
        text.Length > 0 && Rune.DecodeLastFromUtf16(text, out var rune, out _) == OperationStatus.Done ? rune : Rune.ReplacementChar;
}
