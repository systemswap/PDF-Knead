using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// Characters of real content that map to the private use areas of Unicode (U+E000–U+F8FF, planes 15 and 16) shown without a replacement
/// text around them (ISO 14289-2, 8.4.3: such content needs /ActualText or /Alt — of its marked content or its structure element; PDF/UA-1
/// allows it, ISO 14289-1, 7.2). Artifacts are no real content. Shared by the save after redacting (docs/ABLAUFPLAN.md, 6.3 step 5;
/// docs/FEHLERGEDAECHTNIS.md, No. 624) and the check „barrierefrei?“ (6.7 stage C).
/// </summary>
internal static class PrivateUseText
{
    /// <summary>Most operators looked at in one call (a crafted content stream must not hold the save up); beyond it the answer is yes.</summary>
    private const int MaxOperators = 5_000_000;

    /// <summary>Forms nested deeper than this are not looked into (the display stops there too).</summary>
    private const int MaxDepth = 16;

    /// <summary>
    /// Whether a content stream shows such characters without a replacement text: a marked content with /ActualText or /Alt around them (or a
    /// structure element with one, through <paramref name="markedWithText"/>) explains them, an artifact is none of it. Forms drawn are looked
    /// into, with the font and the marked content around them. Where the work would be too much, yes (the claim must not stay on a guess).
    /// </summary>
    /// <param name="doc">The document (fonts, forms).</param>
    /// <param name="content">The content stream (decoded).</param>
    /// <param name="resources">Its resources.</param>
    /// <param name="markedWithText">Of a marked-content identifier: whether its structure element (or one around it) has /ActualText or /Alt; null: none has.</param>
    public static bool ShownWithoutReplacement(PdfDocument doc, byte[] content, PdfDictionary? resources, Func<int, bool>? markedWithText = null) =>
        Found(doc, content, resources, markedWithText) != false;

    /// <summary>
    /// What <see cref="ShownWithoutReplacement"/> looks for, as found: true where such characters are shown, false where none are, null where
    /// the work was too much before any was found — a check claims only what it found (docs/ABLAUFPLAN.md, 6.7 step 1).
    /// </summary>
    public static bool? Found(PdfDocument doc, byte[] content, PdfDictionary? resources, Func<int, bool>? markedWithText = null)
    {
        var walk = new Walk(doc, markedWithText) { PageResources = resources };
        var shown = walk.Stream(content, resources, null, 0);
        return walk.Exhausted ? null : shown;
    }

    private sealed class Walk(PdfDocument doc, Func<int, bool>? markedWithText)
    {
        private int _operators;

        /// <summary>The forms being walked (a form drawing itself again ends here).</summary>
        private readonly HashSet<PdfStream> _open = new(ReferenceEqualityComparer.Instance);

        /// <summary>How many open marked contents give a replacement text, and how many are artifacts.</summary>
        private int _withText, _artifacts;

        public bool Exhausted => _operators >= MaxOperators;

        /// <summary>The resources of the page: a form without its own draws with them (7.8.3, sentence 18; docs/FEHLERGEDAECHTNIS.md, No. 791).</summary>
        public PdfDictionary? PageResources { get; init; }

        public bool Stream(byte[] data, PdfDictionary? resources, PdfDictionary? font, int depth)
        {
            var parser = new ContentParser(data);
            var fonts = new Stack<PdfDictionary?>();
            // marked content does not reach beyond its stream: what this one leaves open is closed at its end
            var marks = new Stack<(bool Text, bool Artifact)>();
            try
            {
                while (parser.Next() is { } op)
                {
                    if (++_operators >= MaxOperators) return true;
                    var operands = parser.Operands;
                    switch (op)
                    {
                        case "q":
                            fonts.Push(font);
                            break;
                        case "Q":
                            if (fonts.TryPop(out var saved)) font = saved;
                            break;
                        case "Tf" when operands.Count >= 1 && operands[0].Name is { } name:
                            font = Resource(resources, "Font", name) as PdfDictionary;
                            break;
                        case "BMC" or "BDC":
                        {
                            var tag = operands.Count > 0 ? operands[0].Name?.Value : null;
                            var properties = op == "BDC" && operands.Count > 1
                                ? operands[1].Object as PdfDictionary ?? (operands[1].Name is { } named ? Resource(resources, "Properties", named) as PdfDictionary : null)
                                : null;
                            var text = properties is not null && (properties.ContainsKey(PdfName.Get("ActualText")) || properties.ContainsKey(PdfName.Get("Alt")) ||
                                                                  properties.GetInteger(PdfName.Get("MCID")) is { } mcid && markedWithText?.Invoke((int)mcid) == true);
                            var artifact = tag == "Artifact";
                            marks.Push((text, artifact));
                            if (text) _withText++;
                            if (artifact) _artifacts++;
                            break;
                        }
                        case "EMC":
                            if (marks.TryPop(out var closed)) Close(closed);
                            break;
                        case "Tj" or "'" or "\"" when operands.Count > 0 && font is not null && operands[^1].Object is PdfString shown:
                            if (Shows(font, shown.Bytes)) return true;
                            break;
                        case "TJ" when operands.Count > 0 && font is not null && operands[^1].Object is PdfArray parts:
                            for (var i = 0; i < parts.Count; i++)
                                if (parts[i] is PdfString part && Shows(font, part.Bytes)) return true;
                            break;
                        case "Do" when operands.Count > 0 && operands[^1].Name is { } form && depth < MaxDepth &&
                                       Resource(resources, "XObject", form) is PdfStream xobject && xobject.Dictionary.GetName(PdfName.Subtype)?.Value == "Form":
                            if (!_open.Add(xobject)) break;
                            try
                            {
                                var inner = xobject.Dictionary.GetDictionary(PdfName.Resources) ?? PageResources ?? resources;
                                if (Stream(doc.GetStreamData(xobject), inner, font, depth + 1)) return true;
                            }
                            finally
                            {
                                _open.Remove(xobject);
                            }
                            break;
                    }
                }
                return false;
            }
            finally
            {
                foreach (var open in marks) Close(open);
            }
        }

        private void Close((bool Text, bool Artifact) mark)
        {
            if (mark.Text) _withText--;
            if (mark.Artifact) _artifacts--;
        }

        /// <summary>Whether shown text has a character of a private use area outside an artifact and outside a replacement text.</summary>
        private bool Shows(PdfDictionary fontDict, byte[] bytes)
        {
            if (_withText > 0 || _artifacts > 0 || bytes.Length == 0) return false;
            var font = PdfFont.Get(fontDict, doc);
            for (var pos = 0; pos < bytes.Length;)
            {
                var length = Math.Max(1, font.ReadCode(bytes, pos, out var code));
                pos += length;
                if (font.StrictText(code, length) is { } text && PdfACheck.HasPrivateUse(text)) return true;
            }
            return false;
        }

        private static PdfObject? Resource(PdfDictionary? resources, string kind, PdfName name) => resources?.GetDictionary(PdfName.Get(kind))?.Get(name);
    }
}
