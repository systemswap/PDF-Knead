using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, Adobe TN 5014 (CMap and CIDFont files), sections 4–5 (draft cid-02): CIDCount, the CMap files PDF-Knead writes (encoding CMaps of the
/// PDF/A copy, ToUnicode maps) and reads (codespace, cidchar/cidrange, notdef mappings).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>The encoding CMap a PDF/A copy writes for a composite font that shows CID 0 (it gets a free CID and a CMap of its own).</summary>
    private static (PdfDictionary Dict, string Text, byte[] Data) Cid02CopiedEncodingCMap()
    {
        var (doc, font) = Cid01Copy(PdfARuleCases.WithCidTrueType(shown: $"<0000{PdfARuleCases.Gid('B'):X4}>")());
        using (doc)
        {
            var encoding = Assert.IsType<PdfStream>(font.Get(PdfName.Get("Encoding")));
            var data = doc.GetStreamData(encoding);
            return (encoding.Dictionary, Encoding.ASCII.GetString(data), data);
        }
    }

    /// <summary>
    /// The CMap programs of every writer: the encoding CMap of the PDF/A copy, PdfACMaps.Encoding and PdfACMaps.ToUnicode called directly,
    /// ElementFace.ToUnicode and FontScrub.ToUnicodeOf (the ToUnicode map of a cleaned font).
    /// </summary>
    private static List<(string Writer, string Text)> Cid02Writers()
    {
        var result = new List<(string, string)> { ("PDF/A-Kopie", Cid02CopiedEncodingCMap().Text) };
        result.Add(("PdfACMaps.Encoding", Encoding.ASCII.GetString(PdfACMaps.Encoding("Probe-H", "Adobe", "Identity", 0, 0, [(0x41, 1, 5), (0x8140, 2, 700)]).RawData!)));
        result.Add(("PdfACMaps.ToUnicode", Encoding.ASCII.GetString(PdfACMaps.ToUnicode([(0x41, 1, "A"), (0x0102, 2, "B")]).RawData!)));
        result.Add(("ElementFace.ToUnicode", Encoding.ASCII.GetString(ElementFace.ToUnicode(new Dictionary<int, string> { [3] = "A", [4] = "B" }).RawData!)));
        const string original = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                                "2 beginbfchar <41> <0041> <42> <0042> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {original.Length} >>\nstream\n{original}\nendstream"));
        var stream = (PdfStream)doc.GetObject(3);
        var scrubbed = FontScrub.ToUnicodeOf(doc, stream, CMap.Parse(doc.GetStreamData(stream)), [(0x41, 1)]);
        result.Add(("FontScrub.ToUnicodeOf", Encoding.ASCII.GetString(scrubbed.RawData!)));
        return result;
    }

    private static readonly Regex Cid02RangeOperator = new(@"^(begin|end)(codespacerange|cidchar|cidrange|bfchar|bfrange|notdefchar|notdefrange)$");

    /// <summary>
    /// CID 4.2 sentences 5–8: CIDCount gives the number of valid CIDs. The reader does not check it (tolerant reading, FRAGEN.md 24): a CID-keyed
    /// CFF with CIDCount 10 and a glyph with CID 12 in its charset finds that glyph for CID 12 — today's behaviour.
    /// </summary>
    [Fact]
    public void Cid02_CidCount_TheReaderTakesCidsBeyondIt()
    {
        var cff = Cid01CidCff([1, 12], cidCount: 10);
        Assert.Equal(10, Cid01Top(cff).Top.Int(1234));
        Assert.Equal(2, CffFont.Parse(cff).GlyphByCid(12));
    }

    /// <summary>
    /// CID 5.2 sentences 27–29 and 32, 7.3 sentences 1–5: a CMap file begins with „%!“ and a first line naming it a CMap resource. The encoding CMap
    /// of a PDF/A copy (and PdfACMaps.Encoding) begins with „%!PS-Adobe-3.0 Resource-CMap“; it carries no %% comments for spoolers (today's state,
    /// question FRAGEN.md 28) and CMap.Parse reads every entry of it. The ToUnicode writers begin without such a line (today's state).
    /// </summary>
    [Fact]
    public void Cid02_WrittenEncodingCMap_BeginsWithTheResourceComment_WithoutDscComments()
    {
        var (dict, text, data) = Cid02CopiedEncodingCMap();
        var writers = Cid02Writers();
        foreach (var (writer, program) in writers.Where(w => w.Writer is "PDF/A-Kopie" or "PdfACMaps.Encoding"))
        {
            Assert.True(program.Split('\n')[0] == "%!PS-Adobe-3.0 Resource-CMap", writer);
            Assert.DoesNotContain("%%", program, StringComparison.Ordinal);
        }
        foreach (var (writer, program) in writers.Where(w => w.Writer.Contains("ToUnicode", StringComparison.Ordinal)))
            Assert.False(program.StartsWith('%'), writer);

        var cmap = CMap.Parse(data);
        Assert.Equal(dict.GetName(PdfName.Get("CMapName"))!.Value, cmap.Name);
        var entries = Regex.Matches(text, @"<([0-9A-F]+)> (\d+)\n").Select(m => (Code: Convert.ToUInt32(m.Groups[1].Value, 16), Bytes: m.Groups[1].Value.Length / 2,
            Cid: int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, cmap.CidMappings().Count());
        foreach (var (code, bytes, cid) in entries) Assert.Equal(cid, cmap.ToCid(code, bytes));
        Assert.NotEqual(0, cmap.ToCid(0, 2));
    }

    /// <summary>
    /// CID 5.2 sentences 42–48, 7.3 sentence 6: right after the header the file finds and begins the procset CIDInit, then „12 dict begin“ starts
    /// the resource dictionary and defineresource registers the CMap. The encoding CMaps (copy and PdfACMaps.Encoding) have them as lines 2 and 3,
    /// the three ToUnicode writers as lines 1 and 2; all register the CMap with „CMapName currentdict /CMap defineresource pop“.
    /// </summary>
    [Fact]
    public void Cid02_WrittenCMaps_OpenTheProcSetAndTheResourceDictionary()
    {
        foreach (var (writer, text) in Cid02Writers())
        {
            var lines = text.Split('\n');
            var first = writer is "PDF/A-Kopie" or "PdfACMaps.Encoding" ? 1 : 0;
            Assert.True(lines[first] == "/CIDInit /ProcSet findresource begin", writer);
            Assert.True(lines[first + 1] == "12 dict begin", writer);
            Assert.Contains("CMapName currentdict /CMap defineresource pop", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// CID 5.2 sentences 52–54, 7.3 sentences 7–12, 7.4 sentences 41–42: the CMap proper stands between begincmap and endcmap, the codespace first
    /// of all range operators. In the programs of every writer begincmap comes before the first begincodespacerange, every mapping block and the
    /// codespace lie between begincmap and endcmap, and the first range operator is begincodespacerange.
    /// </summary>
    [Fact]
    public void Cid02_WrittenCMaps_KeepCodespaceAndMappingsBetweenBegincmapAndEndcmap()
    {
        foreach (var (writer, text) in Cid02Writers())
        {
            var tokens = text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var begin = Array.IndexOf(tokens, "begincmap");
            var end = Array.IndexOf(tokens, "endcmap");
            Assert.True(begin >= 0 && end > begin, writer);
            var operators = tokens.Select((t, i) => (t, i)).Where(x => Cid02RangeOperator.IsMatch(x.t)).ToList();
            Assert.NotEmpty(operators);
            Assert.True(operators[0].t == "begincodespacerange", writer);
            Assert.All(operators, o => Assert.InRange(o.i, begin + 1, end - 1));
        }
    }

    /// <summary>
    /// CID 5.2 sentences 65–71: CMapName names the resource, CMapType is an integer, CMapVersion is optional. The encoding CMap of the copy: the
    /// name CMap.Parse reads is the /CMapName of its dictionary, „/CMapType 1 def“ (the type ISO 32000-2 9.7.5.4 and the predefined CMaps use), no
    /// CMapVersion; the ToUnicode maps of ElementFace and PdfACMaps: „/CMapName /Adobe-Identity-UCS def“ and „/CMapType 2 def“.
    /// </summary>
    [Fact]
    public void Cid02_WrittenEncodingCMap_NamesItselfAndItsType()
    {
        var (dict, text, data) = Cid02CopiedEncodingCMap();
        Assert.Equal(dict.GetName(PdfName.Get("CMapName"))!.Value, CMap.Parse(data).Name);
        Assert.Contains($"/CMapName /{dict.GetName(PdfName.Get("CMapName"))!.Value} def\n", text, StringComparison.Ordinal);
        Assert.Contains("/CMapType 1 def\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CMapVersion", text, StringComparison.Ordinal);
        foreach (var (writer, program) in Cid02Writers().Where(w => w.Writer is "ElementFace.ToUnicode" or "PdfACMaps.ToUnicode"))
        {
            Assert.True(program.Contains("/CMapName /Adobe-Identity-UCS def\n", StringComparison.Ordinal), writer);
            Assert.True(program.Contains("/CMapType 2 def\n", StringComparison.Ordinal), writer);
        }
    }

    /// <summary>
    /// CID 5.2 sentences 72–90, 5.4 sentence 25: UIDOffset and XUID serve the font cache of printers; usecmap, where used, comes before every range
    /// operator. The encoding CMaps PDF-Knead writes (copy and PdfACMaps.Encoding) carry no UIDOffset, no XUID and no usecmap, and the dictionary
    /// of the copy's CMap no /UseCMap.
    /// </summary>
    [Fact]
    public void Cid02_WrittenEncodingCMap_HasNoCacheNumbersAndNoUsecmap()
    {
        var (dict, _, _) = Cid02CopiedEncodingCMap();
        Assert.False(dict.ContainsKey(PdfName.Get("UseCMap")));
        foreach (var (writer, text) in Cid02Writers().Where(w => w.Writer is "PDF/A-Kopie" or "PdfACMaps.Encoding"))
            foreach (var word in (string[])["UIDOffset", "XUID", "usecmap"])
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{writer}: {word}");
    }

    /// <summary>
    /// CID 5.3 sentences 1–7, 7.3 sentences 13–14: the last lines close the CMap — endcmap, defineresource with the name and the category CMap, and
    /// two end (the resource dictionary, the procset); no CMap operator after endcmap. Every writer ends its program exactly so.
    /// </summary>
    [Fact]
    public void Cid02_WrittenCMaps_EndWithEndcmapDefineresourceAndTwoEnds()
    {
        foreach (var (writer, text) in Cid02Writers())
            Assert.True(text.EndsWith("\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n", StringComparison.Ordinal), writer);
    }

    /// <summary>
    /// CID 5.2 sentences 95–98: the codespace comes before all mappings, its ranges do not overlap and decide for every code how many bytes it has.
    /// PdfACMaps.Encoding with the codes 41 (one byte) and 8140 (two bytes): begincodespacerange stands before begincidchar, the ranges are 41–41
    /// and 8100–81FF, and reading back takes 41 as one byte and 81 40 as two.
    /// </summary>
    [Fact]
    public void Cid02_WrittenCodespace_ComesFirst_WithoutOverlap()
    {
        var text = Encoding.ASCII.GetString(PdfACMaps.Encoding("Probe-H", "Adobe", "Identity", 0, 0, [(0x41, 1, 5), (0x8140, 2, 700)]).RawData!);
        Assert.True(text.IndexOf("begincodespacerange", StringComparison.Ordinal) < text.IndexOf("begincidchar", StringComparison.Ordinal));
        var space = Regex.Match(text, @"(\d+) begincodespacerange\n(.*?)endcodespacerange", RegexOptions.Singleline);
        Assert.Equal("2", space.Groups[1].Value);
        Assert.Equal("<41> <41>\n<8100> <81FF>\n", space.Groups[2].Value);
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes(text));
        Assert.Equal(1, cmap.ReadCode([0x41, 0x81, 0x40], 0, out var one));
        Assert.Equal(5, cmap.ToCid(one, 1));
        Assert.Equal(2, cmap.ReadCode([0x41, 0x81, 0x40], 1, out var two));
        Assert.Equal(700, cmap.ToCid(two, 2));
    }

    /// <summary>
    /// CID 5.2 sentences 99–103, 7.4 sentences 43–52: at most 100 codespace ranges per set, as many sets as needed — any number of ranges. A CMap
    /// with two sets of 35 two-byte ranges (00xx, 02xx … 8Axx): the codes of the 65th to 70th range (80xx … 8Axx) lie in the codespace and are read
    /// as two-byte codes. Since FG 1154 the reader keeps up to 256 ranges (CMap.MaxReadCodespaceRanges); the writer stays at 64 (FG 1141).
    /// </summary>
    [Fact]
    public void Cid02_SeventyCodespaceRanges_AreAllRead()
    {
        var sets = new StringBuilder();
        for (var set = 0; set < 2; set++)
        {
            sets.Append("35 begincodespacerange\n");
            for (var i = 35 * set; i < 35 * (set + 1); i++) sets.Append($"<{2 * i:X2}00> <{2 * i:X2}FF>\n");
            sets.Append("endcodespacerange\n");
        }
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" + sets +
                                                      "1 begincidrange <8800> <88FF> 1000 endcidrange\nendcmap end end"));
        for (var i = 64; i < 70; i++)
        {
            var code = (uint)(2 * i) << 8 | 0x10;
            Assert.Equal(2, cmap.ReadCode([(byte)(code >> 8), (byte)code], 0, out _));
            Assert.True(cmap.InCodespace(code, 2), $"{code:X4}");
        }
        Assert.Equal(1000 + 0x10, cmap.ToCid(0x8810, 2));
    }

    /// <summary>
    /// CID 5.2 sentences 99–103 and 159–164, 169, 7.4 sentences 23–26: at most 100 entries per begincidchar set (and 100 ranges per codespace set),
    /// the first line giving their number. PdfACMaps.Encoding with 250 two-byte codes of 125 scattered first bytes: three cidchar sets of 100, 100
    /// and 50 entries, every set header naming its count, every codespace set at most 100 ranges with its count, and every code read back gives its
    /// CID. (The draft's writer case of 150 separate first bytes cannot arise: there are only 128 of them, and since FG 1141 the writer joins its
    /// ranges to at most 64, see CMapNormTests.WrittenCodespace_StaysWithinWhatTheReaderReads.)
    /// </summary>
    [Fact]
    public void Cid02_EncodingCMap_WritesItsSetsWithAtMostHundredEntries()
    {
        var entries = Enumerable.Range(0, 250).Select(i => ((uint)((i % 125) * 2) << 8 | (uint)(i / 125), 2, i + 1)).ToList();
        var text = Encoding.ASCII.GetString(PdfACMaps.Encoding("Probe-H", "Adobe", "Identity", 0, 0, entries).RawData!);
        var sets = Regex.Matches(text, @"(\d+) begincidchar\n(.*?)endcidchar", RegexOptions.Singleline).ToList();
        Assert.Equal(["100", "100", "50"], sets.Select(m => m.Groups[1].Value));
        Assert.All(sets, m => Assert.Equal(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value.Count(c => c == '\n')));
        var spaces = Regex.Matches(text, @"(\d+) begincodespacerange\n(.*?)endcodespacerange", RegexOptions.Singleline).ToList();
        Assert.NotEmpty(spaces);
        foreach (var m in spaces)
        {
            var count = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(count, 1, 100);
            Assert.Equal(count, m.Groups[2].Value.Count(c => c == '\n'));
        }
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes(text));
        foreach (var (code, bytes, cid) in entries)
        {
            Assert.Equal(2, cmap.ReadCode([(byte)(code >> 8), (byte)code], 0, out var read));
            Assert.Equal(cid, cmap.ToCid(read, bytes));
        }
    }

    /// <summary>
    /// CID 5.2 sentences 178–188, 7.4 sentences 53–56 and 60–62: a notdef mapping gives a code without a valid mapping its notdef CID, whose
    /// width and glyph are then used. Embedded CMap with cidrange 20–7E → 1 and notdefrange 00–1F → 7, CID-keyed CFF with the CIDs 6 and 7 only,
    /// /W [7 [300]]: code 05 gets CID 7 — width 0.3 and the glyph of CID 7; code 21 (CID 2, no glyph) draws nothing today (FRAGEN.md 19a; the text:
    /// the glyph of CID 0). The same with notdefchar 05 → 7.
    /// </summary>
    [Fact]
    public void Cid02_NotdefMappings_GiveTheFontTheirCidWidthAndGlyph()
    {
        var cff = Cid01CidCff([6, 7]);
        var cid7 = CffFont.Parse(cff).Outline(2).Bounds;
        var (doc, font) = Cid01Type0(cff, "[7 [300]]", cmapBody: "1 begincidrange <20> <7E> 1 endcidrange\n1 beginnotdefrange <00> <1F> 7 endnotdefrange");
        using (doc)
        {
            Assert.Equal(7, font.Cid(0x05, 1));
            var notdef = font.Glyph(0x05, 1);
            Assert.Equal(0.3, notdef.Width, 6);
            Assert.Equal(cid7, notdef.Outline.Bounds);
            Assert.Equal(2, font.Cid(0x21, 1));
            Assert.True(font.Glyph(0x21, 1).Outline.IsEmpty);   // today: nothing (FRAGEN.md 19a)
        }
        var (charDoc, charFont) = Cid01Type0(cff, "[7 [300]]", cmapBody: "1 beginnotdefchar <05> 7 endnotdefchar");
        using (charDoc)
        {
            Assert.Equal(7, charFont.Cid(0x05, 1));
            Assert.Equal(0.3, charFont.Glyph(0x05, 1).Width, 6);
            Assert.Equal(cid7, charFont.Glyph(0x05, 1).Outline.Bounds);
        }
    }

    /// <summary>
    /// CID 5.2 sentences 174–175: a code outside the codespace that no mapping covers takes the standard notdef. Embedded CMap with codespace 00–7F
    /// and cidrange 20–7E → 1: code 90 gets CID 0 and the glyph 0 of the CID-keyed CFF (CMap.ToCid, CompositeFont.Select). (A code outside the
    /// codespace that a range covers keeps the range's CID — measured, see Cid03_CodeOutsideTheCodespace_KeepsTheCidOfARange.)
    /// </summary>
    [Fact]
    public void Cid02_CodeOutsideTheCodespace_WithoutMapping_DrawsGlyphZero()
    {
        var cff = Cid01CidCff([1, 2, 3]);
        var (doc, font) = Cid01Type0(cff, "[]", cmapBody: "1 begincidrange <20> <7E> 1 endcidrange", codespace: "<00> <7F>");
        using (doc)
        {
            Assert.False(((CompositeFont)font).InCodespace(0x90, 1));
            Assert.Equal(0, font.Cid(0x90, 1));
            Assert.Equal(CffFont.Parse(cff).Outline(0).Bounds, font.Glyph(0x90, 1).Outline.Bounds);
        }
    }
}
