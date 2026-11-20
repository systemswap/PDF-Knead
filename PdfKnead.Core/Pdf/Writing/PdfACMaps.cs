using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The CMaps a PDF/A copy writes for its fonts: an encoding CMap from codes to CIDs (ISO 32000-1, 9.7.5) and a /ToUnicode map (9.10.3), for
/// exactly the codes the pages show. The codespace is made from those codes — per code length, the runs of their first bytes, each followed by
/// any bytes — so that every reader splits the strings of the pages into the codes the display read.
/// </summary>
internal static class PdfACMaps
{
    /// <summary>At most this many entries per begincidchar or beginbfchar block (ISO 32000-1, 9.7.5.4).</summary>
    private const int Block = 100;

    /// <summary>The codespace ranges of a set of codes: per length, one range per run of consecutive first bytes.</summary>
    /// <param name="codes">The codes with their lengths in bytes (1 to 4).</param>
    /// <returns>The ranges, shorter codes first.</returns>
    internal static List<(int Bytes, uint Low, uint High)> Codespace(IEnumerable<(uint Code, int Bytes)> codes)
    {
        var ranges = new List<(int Bytes, uint Low, uint High)>();
        foreach (var group in codes.GroupBy(c => Math.Clamp(c.Bytes, 1, 4)).OrderBy(g => g.Key))
        {
            var shift = 8 * (group.Key - 1);
            var rest = shift == 0 ? 0u : (1u << shift) - 1;
            var firsts = group.Select(c => (c.Code >> shift) & 0xFF).Distinct().Order().ToList();
            for (var i = 0; i < firsts.Count;)
            {
                var end = i;
                while (end + 1 < firsts.Count && firsts[end + 1] == firsts[end] + 1) end++;
                ranges.Add((group.Key, firsts[i] << shift, firsts[end] << shift | rest));
                i = end + 1;
            }
        }
        return ranges;
    }

    /// <summary>An encoding CMap (CMapType 1) for the codes shown, with the same writing mode in its dictionary and its program (6.2.11.3.3).</summary>
    /// <param name="name">The CMap name.</param>
    /// <param name="registry">Registry of the character collection (letters and digits).</param>
    /// <param name="ordering">Ordering of the character collection (letters and digits).</param>
    /// <param name="supplement">Supplement of the character collection.</param>
    /// <param name="wMode">Writing mode: 0 horizontal, 1 vertical.</param>
    /// <param name="entries">Code, its length in bytes and its CID (0 to 65 535).</param>
    /// <returns>The CMap stream, to be packed by the writer.</returns>
    internal static PdfStream Encoding(string name, string registry, string ordering, long supplement, int wMode,
        IReadOnlyCollection<(uint Code, int Bytes, int Cid)> entries)
    {
        var text = new StringBuilder();
        text.Append("%!PS-Adobe-3.0 Resource-CMap\n/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        text.Append(CultureInfo.InvariantCulture,
            $"/CIDSystemInfo 3 dict dup begin\n/Registry ({registry}) def\n/Ordering ({ordering}) def\n/Supplement {supplement} def\nend def\n");
        text.Append(CultureInfo.InvariantCulture, $"/CMapName /{name} def\n/CMapType 1 def\n/WMode {wMode} def\n");
        AppendCodespace(text, Codespace(entries.Select(e => (e.Code, e.Bytes))));
        var sorted = entries.OrderBy(e => e.Bytes).ThenBy(e => e.Code).ToList();
        for (var i = 0; i < sorted.Count; i += Block)
        {
            var block = sorted.Skip(i).Take(Block).ToList();
            text.Append(CultureInfo.InvariantCulture, $"{block.Count} begincidchar\n");
            foreach (var (code, bytes, cid) in block) text.Append(CultureInfo.InvariantCulture, $"<{Hex(code, bytes)}> {cid}\n");
            text.Append("endcidchar\n");
        }
        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("CMap"));
        dict.Set(PdfName.Get("CMapName"), PdfName.Get(name));
        var info = new PdfDictionary();
        info.Set(PdfName.Get("Registry"), new PdfString(System.Text.Encoding.ASCII.GetBytes(registry)));
        info.Set(PdfName.Get("Ordering"), new PdfString(System.Text.Encoding.ASCII.GetBytes(ordering)));
        info.Set(PdfName.Get("Supplement"), PdfInteger.Of(supplement));
        dict.Set(PdfName.Get("CIDSystemInfo"), info);
        dict.Set(PdfName.Get("WMode"), PdfInteger.Of(wMode));
        return new PdfStream(dict, System.Text.Encoding.ASCII.GetBytes(text.ToString()));
    }

    /// <summary>A /ToUnicode map (CMapType 2) for the codes shown.</summary>
    /// <param name="entries">Code, its length in bytes and its text (not empty).</param>
    /// <returns>The CMap stream, to be packed by the writer.</returns>
    internal static PdfStream ToUnicode(IReadOnlyCollection<(uint Code, int Bytes, string Text)> entries)
    {
        var text = new StringBuilder();
        text.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        text.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        AppendCodespace(text, Codespace(entries.Select(e => (e.Code, e.Bytes))));
        var sorted = entries.OrderBy(e => e.Bytes).ThenBy(e => e.Code).ToList();
        for (var i = 0; i < sorted.Count; i += Block)
        {
            var block = sorted.Skip(i).Take(Block).ToList();
            text.Append(CultureInfo.InvariantCulture, $"{block.Count} beginbfchar\n");
            foreach (var (code, bytes, value) in block)
            {
                text.Append('<').Append(Hex(code, bytes)).Append("> <");
                foreach (var c in value) text.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                text.Append(">\n");
            }
            text.Append("endbfchar\n");
        }
        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(text.ToString()));
    }

    private static void AppendCodespace(StringBuilder text, List<(int Bytes, uint Low, uint High)> ranges)
    {
        if (ranges.Count == 0) ranges.Add((1, 0, 0xFF));
        for (var i = 0; i < ranges.Count; i += Block)
        {
            var block = ranges.Skip(i).Take(Block).ToList();
            text.Append(CultureInfo.InvariantCulture, $"{block.Count} begincodespacerange\n");
            foreach (var (bytes, low, high) in block) text.Append(CultureInfo.InvariantCulture, $"<{Hex(low, bytes)}> <{Hex(high, bytes)}>\n");
            text.Append("endcodespacerange\n");
        }
    }

    private static string Hex(uint value, int bytes) => value.ToString("X" + 2 * bytes, CultureInfo.InvariantCulture);
}
