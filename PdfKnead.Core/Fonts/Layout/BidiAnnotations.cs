namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Interlinear annotations in the bidirectional algorithm for text PDF-Knead sets itself (The Unicode Standard 23.8.3 sentences 44–48;
/// docs/FEHLERGEDAECHTNIS.md, No. 1222): U+FFF9 INTERLINEAR ANNOTATION ANCHOR, U+FFFA … SEPARATOR (one or more), U+FFFB … TERMINATOR. The main text
/// is ordered as if the annotated text stood in an isolate — the anchor taken as FSI, the terminator as PDI — and the annotating text after the
/// first separator were not there (each of its UTF-16 units taken as a character of class BN, which X9 removes); each annotating text between two
/// separators, or between a separator and the terminator, is ordered as a paragraph of its own (P2, P3 from its own first strong character, the
/// main paragraph's level without one). Its levels are then raised by an even number to the first level above the main text at its separator that
/// has the parity of its paragraph level, as the levels of an isolate are: in a line the annotation stays one unit (L2) beside the annotated text
/// and is reversed within as its own paragraph would be. The display stays otherwise as it was (no ruby setting): the anchor, separators and
/// terminator, which are not default ignorable (23.2.6), are drawn as characters of their own, the annotating text after the annotated text in
/// the line. Annotations nest up to <see cref="MaxDepth"/> deep (an annotation inside an annotating text is ordered within that one; deeper ones
/// count as plain characters); an anchor without terminator in its paragraph is a plain character (class ON) as before. Written from the Unicode
/// Standard; no code of another implementation used.
/// </summary>
internal static class BidiAnnotations
{
    internal const char Anchor = '\uFFF9', Separator = '\uFFFA', Terminator = '\uFFFB';

    /// <summary>How deep annotations within annotating texts are ordered on their own.</summary>
    private const int MaxDepth = 8;

    /// <summary>One annotation in the text: its anchor, its separators (none: an annotated text without annotation) and its terminator.</summary>
    private sealed record Annotation(int Anchor, List<int> Separators, int Terminator);

    /// <summary>
    /// The analysis of a text with interlinear annotations, or null if it has none (no anchor with a terminator in its paragraph): then
    /// <see cref="Bidi.Analyze"/> resolves the text as it stands.
    /// </summary>
    internal static Bidi.Analysis? Analyze(string text, byte? direction, byte defaultLevel) => Analyze(text, direction, defaultLevel, 0);

    private static Bidi.Analysis? Analyze(string text, byte? direction, byte defaultLevel, int depth)
    {
        if (text.AsSpan().IndexOf(Anchor) < 0 || text.AsSpan().IndexOf(Terminator) < 0) return null;
        var annotations = Find(text);
        if (annotations.Count == 0) return null;

        // the main text: anchor as FSI, terminator as PDI, the annotating text (from the first separator) as BN
        var main = text.ToCharArray();
        foreach (var a in annotations)
        {
            main[a.Anchor] = '\u2068';
            main[a.Terminator] = '\u2069';
            if (a.Separators.Count > 0) Array.Fill(main, '\u200B', a.Separators[0], a.Terminator - a.Separators[0]);
        }
        var resolved = Bidi.Analyze(new string(main), direction, defaultLevel);
        var levels = new byte[text.Length];
        var paragraphs = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            levels[i] = resolved.Level(i);
            paragraphs[i] = resolved.ParagraphLevel(i);
        }

        // each annotating text as a paragraph of its own, its levels raised above the main text at its separator
        foreach (var a in annotations)
            for (var k = 0; k < a.Separators.Count; k++)
            {
                var from = a.Separators[k] + 1;
                var to = k + 1 < a.Separators.Count ? a.Separators[k + 1] : a.Terminator;
                if (to <= from) continue;
                var piece = text[from..to];
                var own = resolved.ParagraphLevel(a.Anchor);
                var inner = depth + 1 < MaxDepth
                    ? Analyze(piece, null, own, depth + 1) ?? Bidi.Analyze(piece, null, own)
                    : Bidi.Analyze(piece.Replace(Anchor, '\uFFFC').Replace(Terminator, '\uFFFC'), null, own);
                var paragraphLevel = inner.ParagraphLevel(0);
                var target = levels[a.Separators[k]] + 1;
                if (target % 2 != paragraphLevel % 2) target++;
                var offset = target - paragraphLevel;
                var highest = 0;
                for (var i = 0; i < piece.Length; i++) highest = Math.Max(highest, inner.Level(i));
                if (highest + offset > byte.MaxValue) offset = 0;
                for (var i = 0; i < piece.Length; i++) levels[from + i] = (byte)(inner.Level(i) + offset);
            }

        // the classes of the characters as they are (for L1 of each line)
        var classes = new BidiClass[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                classes[i] = classes[i + 1] = Bidi.ClassOf(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else classes[i] = Bidi.ClassOf(text[i]);
        }
        return new Bidi.Analysis(text, classes, levels, paragraphs);
    }

    /// <summary>
    /// The annotations of the main text: each anchor with the separators after it and its terminator (a stack of the open anchors; a separator
    /// belongs to the innermost one; a paragraph separator, class B, closes all), except those within the annotating text of another, which that
    /// one's own ordering finds. Linear in the length of the text.
    /// </summary>
    private static List<Annotation> Find(string text)
    {
        var found = new List<Annotation>();
        var open = new List<(int Anchor, List<int> Separators, bool InAnnotation)>();
        var inAnnotation = 0;   // open anchors whose annotating text has begun
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == Anchor) open.Add((i, [], inAnnotation > 0));
            else if (c == Separator && open.Count > 0)
            {
                if (open[^1].Separators.Count == 0) inAnnotation++;
                open[^1].Separators.Add(i);
            }
            else if (c == Terminator && open.Count > 0)
            {
                var (anchor, separators, within) = open[^1];
                open.RemoveAt(open.Count - 1);
                if (separators.Count > 0) inAnnotation--;
                if (!within) found.Add(new Annotation(anchor, separators, i));
            }
            else if (c is not (Anchor or Separator or Terminator) && Bidi.ClassOf(c) == BidiClass.B)
            {
                open.Clear();
                inAnnotation = 0;
            }
        }
        return found;
    }
}
