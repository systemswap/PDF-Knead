using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>Per redacted page of the main file (object number): the page as it was before (for the marked content under its bars).</summary>
        private readonly Dictionary<int, PdfPage> _barOriginals = [];

        /// <summary>The content streams (in memory) of redacted pages whose bars lie within text: per bar its rectangle as drawn and its MCID.</summary>
        private readonly Dictionary<PdfStream, List<(string Rect, int Mcid)>> _inlineBars = new(ReferenceEqualityComparer.Instance);

        /// <summary>The structure types a bar can lie within as an inline artifact: text, its inline parts, cells and list bodies.</summary>
        private static readonly HashSet<string> TextHolders = new(["P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "Span", "Em", "Strong", "Quote", "Code", "Sub",
            "Lbl", "LBody", "TD", "TH", "Caption", "Title", "Note", "FENote", "BlockQuote", "Reference", "BibEntry"], StringComparer.Ordinal);

        /// <summary>
        /// A bar within text of a tagged copy written as PDF 2.0 with the namespace for PDF 2.0 (docs/ABLAUFPLAN.md, 6.3 step 6; docs/FEHLERGEDAECHTNIS.md,
        /// No. 659): an artifact only meaningful in the context of that text, so it is enclosed in an Artifact structure element (ISO 14289-2,
        /// 8.3.2) with /A &lt;&lt;/O /Artifact /Type /Inline /Subtype /Redaction /BBox …&gt;&gt; (ISO 32000-2, 14.8.5.8, Table 385) — a kid of the
        /// element whose marked content lay under the bar's middle, right after that marked content, where that element is text and still has
        /// content; its bar is drawn in its own marked content with the MCID (<see cref="_inlineBars"/>). Other bars stay artifacts of
        /// pagination. Never in a tree of PDF 1.7 (it would become a mixed tree).
        /// </summary>
        private void InlineBarArtifacts()
        {
            if (_barOriginals.Count == 0 || !Pdf20 || !TaggedCopy() || StructureEdits() is not { Pdf20Types: true } plan) return;
            var main = _sources[_main];
            for (var i = 0; i < _pages.Count; i++)
            {
                var item = _pages[i];
                if (!ReferenceEquals(item.Source, _main) || item.Page.Id is not { } id || !_barOriginals.TryGetValue(id.Number, out var original) ||
                    item.Page.Dictionary.Get(PdfName.Contents) is not PdfStream { Id: null, RawData: { } data } content ||
                    Bars(data) is not { Count: > 0 } bars)
                    continue;
                var geometry = MarkedContentGeometry.Of(item.Source, original, _cancel);
                var inline = new List<(string, int)>();
                foreach (var (text, rect) in bars)
                {
                    if (MarkedContentGeometry.At(geometry, (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2) is not { } under ||
                        plan.ElementOfMcid(i, under) is not { } element || element is PdfReference gone && main.Removed.Contains(gone.Id.Number) ||
                        plan.View.TypeOf(element) is not { } type || !TextHolders.Contains(type) ||
                        plan.IndexOfMarkedContent(element, i, under) is var index && index < 0 ||
                        plan.NewElementAt("Artifact", element, index + 1, i) is not { } made)
                        continue;
                    var attributes = new PdfDictionary();
                    attributes.Set(PdfName.Get("O"), PdfName.Get("Artifact"));
                    attributes.Set(PdfName.Get("Type"), PdfName.Get("Inline"));
                    attributes.Set(PdfName.Get("Subtype"), PdfName.Get("Redaction"));
                    attributes.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(Math.Round(rect.Left, 3)), new PdfReal(Math.Round(Math.Min(rect.Top, rect.Bottom), 3)),
                        new PdfReal(Math.Round(rect.Right, 3)), new PdfReal(Math.Round(Math.Max(rect.Top, rect.Bottom), 3))]));
                    plan.AddAttributes(made, attributes);
                    var mcid = plan.NextMcid(i);
                    plan.AddMarkedContent(made, i, mcid);
                    inline.Add((text, mcid));
                }
                if (inline.Count > 0) _inlineBars[content] = inline;
            }
        }

        /// <summary>
        /// The bars of a redacted page as the redaction draws them in a tagged file (<see cref="Redactor.BarArtifact"/>, then „q 0 g“ or
        /// „q 1 g“, one „x y w h re“ per bar, „f Q EMC“): each line and its rectangle; null if the content does not end that way.
        /// </summary>
        private static List<(string Line, RectD Rect)>? Bars(byte[] data)
        {
            var text = Encoding.Latin1.GetString(data);
            var start = text.LastIndexOf(Encoding.Latin1.GetString(Redactor.BarArtifact), StringComparison.Ordinal);
            if (start < 0) return null;
            var lines = text[start..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var bars = new List<(string, RectD)>();
            foreach (var line in lines.Skip(2))
            {
                var parts = line.Split(' ');
                if (parts.Length != 5 || parts[4] != "re") break;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                    return null;
                bars.Add((line, RectD.FromLtrb(Math.Min(x, x + w), Math.Min(y, y + h), Math.Max(x, x + w), Math.Max(y, y + h))));
            }
            return bars;
        }

        /// <summary>
        /// The bars of a page with inline artifacts drawn anew: those within text each in marked content with its MCID (same fill), the others
        /// in the artifact of pagination as before; the content before them byte for byte.
        /// </summary>
        private static byte[] WithInlineBars(byte[] data, List<(string Line, int Mcid)> inline)
        {
            var text = Encoding.Latin1.GetString(data);
            var marker = Encoding.Latin1.GetString(Redactor.BarArtifact);
            var start = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return data;
            var lines = text[start..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return data;
            var fill = lines[1];   // „q 0 g“ or „q 1 g“
            var rest = lines.Skip(2).Where(l => l.EndsWith(" re", StringComparison.Ordinal)).ToList();
            var output = new StringBuilder(text, 0, start, text.Length + 64 * inline.Count);
            var plain = rest.Where(l => !inline.Any(b => b.Line == l)).ToList();
            if (plain.Count > 0)
            {
                output.Append(marker).Append(fill).Append('\n');
                foreach (var line in plain) output.Append(line).Append('\n');
                output.Append("f\nQ\nEMC\n");
            }
            foreach (var (line, mcid) in inline)
                output.Append("/Artifact <</MCID ").Append(mcid.ToString(CultureInfo.InvariantCulture)).Append(">> BDC\n").Append(fill).Append('\n')
                    .Append(line).Append("\nf\nQ\nEMC\n");
            return Encoding.Latin1.GetBytes(output.ToString());
        }
    }
}
