using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, Adobe TN 5014 (CMap and CIDFont files), sections 1–3 (draft cid-01): CID 0, the character collection and the Font DICTs of
/// CID-keyed fonts as PDF-Knead reads and writes them (the PostScript form of a CIDFont is CFF in PDF).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ CFF building blocks (shared by the cid packages)

    /// <summary>A CFF INDEX with 4-byte offsets (Adobe TN 5176, section 5); empty: the count only.</summary>
    private static byte[] Cid01Index(params byte[][] items)
    {
        if (items.Length == 0) return [0, 0];
        var result = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 4 };
        var offset = 1;
        void Offset(int v) => result.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        Offset(offset);
        foreach (var item in items)
        {
            offset += item.Length;
            Offset(offset);
        }
        foreach (var item in items) result.AddRange(item);
        return [.. result];
    }

    /// <summary>A DICT number in its 5-byte form (operator 29), so that sizes are known before positions.</summary>
    private static byte[] Cid01Five(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>A Type 2 charstring drawing a box of <paramref name="w"/> × <paramref name="h"/> from the origin, without a width.</summary>
    private static byte[] Cid01Box(int w, int h)
    {
        var s = new List<byte>();
        void N(int v) => s.AddRange([28, (byte)(v >> 8), (byte)v]);
        N(0); N(0); s.Add(21);     // rmoveto
        N(w); N(0); s.Add(5);      // rlineto
        N(0); N(h); s.Add(5);
        N(-w); N(0); s.Add(5);
        s.Add(14);                 // endchar
        return [.. s];
    }

    /// <summary>
    /// A CID-keyed CFF (ROS Adobe-Identity-0): glyph 0 a visible box of 500 × 700, glyph i (i ≥ 1) a box of (100 + 10·i) × 100 with the CID
    /// <paramref name="cids"/>[i − 1]. Optional: CIDCount, UniqueID and XUID in the Top DICT, one Font DICT per default width (its Private DICT
    /// holds only defaultWidthX) and the FDSelect value of every glyph (glyph 0 included).
    /// </summary>
    private static byte[] Cid01CidCff(int[] cids, int? cidCount = null, bool uniqueIds = false, int[]? defaultWidths = null, byte[]? fdSelect = null)
    {
        defaultWidths ??= [0];
        var glyphCount = cids.Length + 1;
        fdSelect ??= new byte[glyphCount];
        var name = Cid01Index("ProbeCid"u8.ToArray());
        var strings = Cid01Index("Adobe"u8.ToArray(), "Identity"u8.ToArray());
        var global = Cid01Index();
        byte[] Top(int charsetAt, int charStringsAt, int fdArrayAt, int fdSelectAt)
        {
            var d = new List<byte>();
            d.AddRange([.. Cid01Five(391), .. Cid01Five(392), .. Cid01Five(0), 12, 30]);   // ROS
            if (cidCount is { } count) d.AddRange([.. Cid01Five(count), 12, 34]);
            if (uniqueIds)
            {
                d.AddRange([.. Cid01Five(4711), 13]);                              // UniqueID
                d.AddRange([.. Cid01Five(1000000), .. Cid01Five(4711), 14]);      // XUID
            }
            d.AddRange([.. Cid01Five(charsetAt), 15, .. Cid01Five(charStringsAt), 17, .. Cid01Five(fdArrayAt), 12, 36, .. Cid01Five(fdSelectAt), 12, 37]);
            return [.. d];
        }
        var charset = new List<byte> { 0 };
        foreach (var cid in cids) charset.AddRange([(byte)(cid >> 8), (byte)cid]);
        byte[] fdSelectBytes = [0, .. fdSelect];
        var glyphs = new List<byte[]> { Cid01Box(500, 700) };
        for (var i = 1; i < glyphCount; i++) glyphs.Add(Cid01Box(100 + 10 * i, 100));
        var charStrings = Cid01Index([.. glyphs]);
        var privates = defaultWidths.Select(w => (byte[])[.. Cid01Five(w), 20]).ToList();
        byte[][] FontDicts(int privateAt) =>
            [.. privates.Select((p, i) => (byte[])[.. Cid01Five(p.Length), .. Cid01Five(privateAt + privates.Take(i).Sum(q => q.Length)), 18])];

        var topSize = Cid01Index(Top(0, 0, 0, 0)).Length;
        var charsetAt = 4 + name.Length + topSize + strings.Length + global.Length;
        var fdSelectAt = charsetAt + charset.Count;
        var charStringsAt = fdSelectAt + fdSelectBytes.Length;
        var fdArrayAt = charStringsAt + charStrings.Length;
        var privateAt = fdArrayAt + Cid01Index(FontDicts(0)).Length;
        return [1, 0, 4, 4, .. name, .. Cid01Index(Top(charsetAt, charStringsAt, fdArrayAt, fdSelectAt)), .. strings, .. global, .. charset,
                .. fdSelectBytes, .. charStrings, .. Cid01Index(FontDicts(privateAt)), .. privates.SelectMany(p => p)];
    }

    /// <summary>The Top DICT of a CFF and its strings by SID.</summary>
    private static (CffFont.Dict Top, Func<int, string> Sid) Cid01Top(byte[] cff)
    {
        var data = new ArraySegment<byte>(cff);
        var r = new FontReader(data);
        r.U8();
        r.U8();
        r.Position = r.U8();
        CffFont.CffIndex.Read(data, ref r);
        var tops = CffFont.CffIndex.Read(data, ref r);
        var strings = CffFont.CffIndex.Read(data, ref r);
        var top = CffFont.Dict.Parse(tops.Item(data, 0));
        string Sid(int sid) => sid < FontTables.CffStandardStrings.Length
            ? FontTables.CffStandardStrings[sid]
            : Encoding.Latin1.GetString(strings.Item(data, sid - FontTables.CffStandardStrings.Length));
        return (top, Sid);
    }

    /// <summary>Registry, ordering and supplement of the ROS of a CID-keyed CFF.</summary>
    private static (string Registry, string Ordering, int Supplement) Cid01Ros(byte[] cff)
    {
        var (top, sid) = Cid01Top(cff);
        var ros = top.Get(1230)!;
        return (sid((int)ros[0]), sid((int)ros[1]), (int)ros[2]);
    }

    /// <summary>
    /// A file with one Type 0 font (object 3) over a CIDFontType0 (4) with the CFF program <paramref name="cff"/> (descriptor 5, FontFile3 6) and
    /// the given /W; the encoding is <paramref name="encoding"/>, or with <paramref name="cmapBody"/> an embedded CMap (7) with codespace
    /// <paramref name="codespace"/> and that body.
    /// </summary>
    private static (PdfDocument Doc, PdfFont Font) Cid01Type0(byte[] cff, string w, string encoding = "/Identity-H", string? cmapBody = null,
        string codespace = "<00> <FF>")
    {
        const string info = "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Type /Font /Subtype /Type0 /BaseFont /ProbeCid /Encoding {(cmapBody is null ? encoding : "7 0 R")} /DescendantFonts [4 0 R] >>",
            $"<< /Type /Font /Subtype /CIDFontType0 /BaseFont /ProbeCid {info} /FontDescriptor 5 0 R /DW 1000 /W {w} >>",
            "<< /Type /FontDescriptor /FontName /ProbeCid /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 6 0 R >>",
            $"<< /Subtype /CIDFontType0C /Length {cff.Length} >>\nstream\n{Encoding.Latin1.GetString(cff)}\nendstream",
        };
        if (cmapBody is not null)
        {
            var cmap = $"/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Probe def 1 begincodespacerange {codespace} endcodespacerange\n" +
                       $"{cmapBody}\nendcmap CMapName currentdict /CMap defineresource pop end end";
            objects.Add($"<< /Type /CMap /CMapName /Probe {info} /Length {cmap.Length} >>\nstream\n{cmap}\nendstream");
        }
        var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf([.. objects]));
        return (doc, PdfFont.Get((PdfDictionary)doc.GetObject(3), doc));
    }

    /// <summary>A PDF/A copy of <paramref name="original"/> and the written font /F1 of its first page (the copy must conform).</summary>
    private static (PdfDocument Doc, PdfDictionary Font) Cid01Copy(byte[] original, PdfAConformance part = PdfAConformance.A2b)
    {
        using var doc = PdfDocument.Open(original);
        var copy = PdfAExport.Write(doc, PageItem.From(doc), part);
        Assert.True(copy.Conforms, PdfATests.Describe(copy.Violations));
        var written = PdfDocument.Open(copy.Bytes);
        return (written, written.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!);
    }

    /// <summary>Registry, ordering and supplement of a /CIDSystemInfo dictionary.</summary>
    private static (string?, string?, long?) Cid01Collection(PdfDictionary? info) =>
        (info?.GetString(PdfName.Get("Registry"))?.ToText(), info?.GetString(PdfName.Get("Ordering"))?.ToText(), info?.GetInteger(PdfName.Get("Supplement")));

    // ------------------------------------------------------------ tests

    /// <summary>
    /// CID 2.3 sentence 6: CID 0 is the notdef character and applies to a code the CMap gives no CID. Embedded CMap with codespace 00–FF and only
    /// 41 → CID 5; code 42 takes CID 0 — its width from /W (250) and the outline of glyph 0 of the CID-keyed CFF (a visible box).
    /// </summary>
    [Fact]
    public void Cid01_CodeWithoutMapping_TakesCidZero_ItsWidthAndItsGlyph()
    {
        var cff = Cid01CidCff([1, 2, 3, 4, 5]);
        var (doc, font) = Cid01Type0(cff, "[0 [250] 5 [300]]", cmapBody: "1 begincidchar <41> 5 endcidchar");
        using (doc)
        {
            var unmapped = font.Glyph(0x42, 1);
            Assert.Equal(0.25, unmapped.Width, 6);
            Assert.False(unmapped.Outline.IsEmpty);
            Assert.Equal(CffFont.Parse(cff).Outline(0).Bounds, unmapped.Outline.Bounds);
            Assert.Equal(0.3, font.Glyph(0x41, 1).Width, 6);
            Assert.Equal(CffFont.Parse(cff).Outline(5).Bounds, font.Glyph(0x41, 1).Outline.Bounds);
        }
    }

    /// <summary>
    /// CID 2.4 sentences 8–10, 2.5 sentence 2, 3.2 sentences 99–102 and 197, 5.2 sentences 174–175, 7.4 sentences 69–72: a CID the CIDFont has no
    /// glyph for (another supplement, an empty interval) — the text asks for the glyph of CID 0. Open question (FRAGEN.md 19a): this test holds
    /// today's behaviour. CID-keyed CFF with a visible glyph 0 and the CIDs 1–3, Identity-H, code 0009 (CID 9 is not in the charset): nothing
    /// is drawn (CffFont.GlyphByCid gives −1, CompositeFont.Select an empty outline); the width stays the one of CID 9.
    /// </summary>
    [Fact]
    public void Cid01_CidWithoutGlyph_DrawsNothingToday()
    {
        var cff = Cid01CidCff([1, 2, 3]);
        Assert.Equal(-1, CffFont.Parse(cff).GlyphByCid(9));
        var (doc, font) = Cid01Type0(cff, "[9 [400]]");
        using (doc)
        {
            var missing = font.Glyph(0x0009, 2);
            Assert.True(missing.Outline.IsEmpty);   // the text: the box of glyph 0 (FRAGEN.md 19a)
            Assert.Equal(0.4, missing.Width, 6);
            Assert.False(font.Glyph(0x0003, 2).Outline.IsEmpty);
        }
    }

    /// <summary>
    /// CID 2.5 sentence 2 (writing), 3.2 sentences 164–168, 4.2 sentences 5–8, 7.4 sentences 57–59: every CIDFont PDF-Knead writes has glyph data
    /// for CID 0, and CIDCount n means the CIDs 0 to n − 1. CffSubsetter.Subset(cff, [5, 9]) read back: glyph 0 with CID 0, the CIDs 5 and 9
    /// (their outlines kept), CIDCount 10, ROS Adobe-Identity-0 (3.2 sentences 91–96: no new collection); CffBuilder.BuildCid with the CIDs 3 and
    /// 7: glyph 0 an empty .notdef with CID 0, CIDCount 8, the ROS it is given; CffScrubber.Scrub without glyph 0 in the list keeps it.
    /// </summary>
    [Fact]
    public void Cid01_WrittenCidFonts_KeepGlyphZeroAsCidZero_AndCountTheirCids()
    {
        var cff = Cid01CidCff([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        var source = CffFont.Parse(cff);

        var subset = CffSubsetter.Subset(cff, [5, 9]);
        var read = CffFont.Parse(subset);
        Assert.True(read.IsCidKeyed);
        Assert.Equal(3, read.GlyphCount);
        Assert.Equal(0, read.GlyphByCid(0));
        Assert.Equal(0, read.CidOf(0));
        Assert.Equal(source.Outline(0).Bounds, read.Outline(0).Bounds);
        Assert.Equal(source.Outline(5).Bounds, read.Outline(read.GlyphByCid(5)).Bounds);
        Assert.Equal(source.Outline(9).Bounds, read.Outline(read.GlyphByCid(9)).Bounds);
        Assert.Equal(10, Cid01Top(subset).Top.Int(1234));
        Assert.Equal(("Adobe", "Identity", 0), Cid01Ros(subset));

        var box = CffFont.Parse(cff).Outline(3);
        var built = CffBuilder.BuildCid("Probe", new Core.Pdf.Graphics.Matrix(0.001, 0, 0, 0.001, 0, 0), "Adobe", "Japan1", 2, [(3, box, 500), (7, box, 600)]);
        var builtRead = CffFont.Parse(built);
        Assert.Equal(0, builtRead.GlyphByCid(0));
        Assert.True(builtRead.Outline(0).IsEmpty);
        Assert.Equal(1, builtRead.GlyphByCid(3));
        Assert.Equal(2, builtRead.GlyphByCid(7));
        Assert.Equal(8, Cid01Top(built).Top.Int(1234));
        Assert.Equal(("Adobe", "Japan1", 2), Cid01Ros(built));

        var (cleaned, keep) = CffScrubber.Scrub(cff, [5]);
        Assert.Contains(0, keep);
        Assert.Equal(source.Outline(0).Bounds, CffFont.Parse(cleaned).Outline(0).Bounds);
    }

    /// <summary>
    /// CID 3.2 sentences 126–136: XUID (and UniqueID) belong to the one who issued them; the CID-keyed fonts PDF-Knead writes carry none.
    /// CffSubsetter.Subset of a CID-keyed CFF with UniqueID (Top DICT 13) and XUID (14): neither in the Top DICT of the subset; CffBuilder.BuildCid
    /// writes neither.
    /// </summary>
    [Fact]
    public void Cid01_WrittenCidFonts_CarryNoUniqueIdOrXuid()
    {
        var cff = Cid01CidCff([1, 2, 3], uniqueIds: true);
        Assert.True(Cid01Top(cff).Top.Has(13));
        Assert.True(Cid01Top(cff).Top.Has(14));
        var subset = Cid01Top(CffSubsetter.Subset(cff, [1, 2])).Top;
        Assert.False(subset.Has(13));
        Assert.False(subset.Has(14));
        var built = Cid01Top(CffBuilder.BuildCid("Probe", new Core.Pdf.Graphics.Matrix(0.001, 0, 0, 0.001, 0, 0), "Adobe", "Identity", 0,
            [(1, CffFont.Parse(cff).Outline(1), 500)])).Top;
        Assert.False(built.Has(13));
        Assert.False(built.Has(14));
    }

    /// <summary>
    /// CID 3.2 sentences 76–83, 4.2 sentences 29–35: registry and ordering of a collection consist of letters, digits and underscores. A PDF/A copy
    /// of a CIDFont not embedded whose /Registry is „Ado be“ or whose /Ordering is „Japan-1“ (code 0000 shown, so the copy writes a CMap of its
    /// own): the CIDFont, the CMap (dictionary and program) and the ROS of the CID-keyed CFF it is embedded as all name Adobe-Identity-0. A
    /// registry with an underscore („Muster_1“, 4.2 sentences 29–35) stays since FG 1147 (<see cref="Cid01_RegistryWithUnderscore_Stays"/>).
    /// </summary>
    [Theory]
    [InlineData("Ado be", "Identity")]
    [InlineData("Adobe", "Japan-1")]
    public void Cid01_CollectionNamesOutsideLettersAndDigits_BecomeAdobeIdentity(string registry, string ordering)
    {
        Cid01AssertCollectionOfCopy(registry, ordering, ("Adobe", "Identity", 0L));
    }

    /// <summary>
    /// CID 4.2 sentences 29–35: the registry may hold an underscore (a registered prefix) — the PDF/A copy keeps „Muster_1“ (since FG 1147;
    /// before it became Adobe, finding B22).
    /// </summary>
    [Fact]
    public void Cid01_RegistryWithUnderscore_Stays()
    {
        var original = PdfARuleCases.WithCidTrueType(
            cid: c => c.Replace("/Registry(Adobe)/Ordering(Identity)", "/Registry(Muster_1)/Ordering(Identity)", StringComparison.Ordinal),
            descriptor: d => d.Replace("/FontFile2 7 0 R", "", StringComparison.Ordinal),
            shown: $"<0000{PdfARuleCases.Gid('B'):X4}>")();
        var (doc, font) = Cid01Copy(original);
        using (doc)
        {
            var cidFont = (PdfDictionary)font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
            Assert.Equal(("Muster_1", "Identity", 0L), Cid01Collection(cidFont.GetDictionary(PdfName.Get("CIDSystemInfo"))));
        }
    }

    private static void Cid01AssertCollectionOfCopy(string registry, string ordering, (string, string, long) expected)
    {
        _ = expected;
        var original = PdfARuleCases.WithCidTrueType(
            cid: c => c.Replace("/Registry(Adobe)/Ordering(Identity)", $"/Registry({registry})/Ordering({ordering})", StringComparison.Ordinal),
            descriptor: d => d.Replace("/FontFile2 7 0 R", "", StringComparison.Ordinal),
            shown: $"<0000{PdfARuleCases.Gid('B'):X4}>")();
        var (doc, font) = Cid01Copy(original);
        using (doc)
        {
            var cidFont = (PdfDictionary)font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
            Assert.Equal(("Adobe", "Identity", 0L), Cid01Collection(cidFont.GetDictionary(PdfName.Get("CIDSystemInfo"))));
            var encoding = Assert.IsType<PdfStream>(font.Get(PdfName.Get("Encoding")));
            Assert.Equal(("Adobe", "Identity", 0L), Cid01Collection(encoding.Dictionary.GetDictionary(PdfName.Get("CIDSystemInfo"))));
            var program = Encoding.ASCII.GetString(doc.GetStreamData(encoding));
            Assert.Contains("/Registry (Adobe) def\n/Ordering (Identity) def\n/Supplement 0 def", program, StringComparison.Ordinal);
            var file = cidFont.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
            Assert.Equal(("Adobe", "Identity", 0), Cid01Ros(doc.GetStreamData(file)));
        }
    }

    /// <summary>
    /// CID 3.2 sentences 76–83, 4.2 sentences 29–35: the composite fonts of page elements name the collection Adobe-Identity-0 with exactly the
    /// three entries Registry, Ordering and Supplement (ElementFace.Type0, through SavedFont).
    /// </summary>
    [Fact]
    public void Cid01_ElementFonts_NameAdobeIdentityZero_WithExactlyThreeEntries()
    {
        var face = ElementFace.For(ElementFont.Sans);
        var saved = face.SavedFont(new Dictionary<int, string> { [face.Glyph('A')] = "A" });
        var info = ((PdfDictionary)saved.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)).GetDictionary(PdfName.Get("CIDSystemInfo"))!;
        Assert.Equal(3, info.Count);
        Assert.Equal(("Adobe", "Identity", 0L), Cid01Collection(info));
    }

    /// <summary>
    /// CID 3.2 sentence 90, 4.2 sentences 41–47: only registry and ordering together name a collection — another registry may use the ordering
    /// Japan1. CIDFont not embedded with /Registry (Muster) /Ordering (Japan1), Identity-H, without /ToUnicode: no text and no glyphs of
    /// Adobe-Japan1 are expected. PredefinedCMaps.IsCjk and Text look at the ordering alone (CompositeFont): the code gets the text of
    /// Adobe-Japan1 (finding B40).
    /// </summary>
    [Fact(Skip = "M7-Befund: fremde Registry mit Ordering Japan1 gilt als Adobe-Japan1 (B40)")]
    public void Cid01_ForeignRegistryWithOrderingJapan1_IsNotTakenForAdobeJapan1()
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Muster /Encoding /Identity-H /DescendantFonts [4 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Muster /CIDSystemInfo << /Registry (Muster) /Ordering (Japan1) /Supplement 0 >> " +
            "/FontDescriptor 5 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /Muster /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>");
        using var doc = PdfDocument.Open(file);
        var font = PdfFont.Get((PdfDictionary)doc.GetObject(3), doc);
        Assert.NotNull(PredefinedCMaps.Text("Japan1", 34));   // (what Adobe-Japan1 would give CID 34)
        var glyph = font.Glyph(34, 2);
        Assert.Null(glyph.Text);
        Assert.True(glyph.Outline.IsEmpty);
    }

    /// <summary>
    /// CID 3.2 sentences 91–96 and 99–102 (checker), 4.2 sentences 48–49: a supplement only adds CIDs; a CIDFont of a later supplement than its
    /// CMap may show CIDs the CMap does not know. CIDFont Adobe-Japan1-6 with 90ms-RKSJ-H (Adobe-Japan1-2): the PDF/A check reports the
    /// collection for PDF/A-2b, not for PDF/A-1b (which asks only for registry and ordering); the PDF/A-2b copy names Adobe-Japan1 with a supplement
    /// no higher than the CMap's (2) and its codes give the same CIDs (PdfACheck.CompositeRules, PdfWriter.RebuildComposite).
    /// </summary>
    [Fact]
    public void Cid01_PdfACopy_KeepsTheCollection_WithASupplementNoHigherThanTheCMaps()
    {
        var original = PdfARuleCases.WithCidTrueType(
            t => t.Replace("/Encoding/Identity-H", "/Encoding/90ms-RKSJ-H", StringComparison.Ordinal),
            cid: c => c.Replace("/Registry(Adobe)/Ordering(Identity)/Supplement 0", "/Registry(Adobe)/Ordering(Japan1)/Supplement 6", StringComparison.Ordinal),
            shown: "(AB)")();
        var id2 = PdfARules.CidSystemInfo.Id(PdfAConformance.A2b);
        var id1 = PdfARules.CidSystemInfo.Id(PdfAConformance.A1b);
        using (var doc = PdfDocument.Open(original))
        {
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == id2);
            Assert.DoesNotContain(PdfACheck.Check(doc, PdfAConformance.A1b), v => v.Id == id1);
        }
        using var source = PdfDocument.Open(original);
        var before = PdfFont.Get(source.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!, source);
        var (copy, font) = Cid01Copy(original);
        using (copy)
        {
            Assert.Equal("90ms-RKSJ-H", font.GetName(PdfName.Get("Encoding"))?.Value);
            var cidFont = (PdfDictionary)font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
            var (registry, ordering, supplement) = Cid01Collection(cidFont.GetDictionary(PdfName.Get("CIDSystemInfo")));
            Assert.Equal(("Adobe", "Japan1"), (registry, ordering));
            Assert.InRange(supplement!.Value, 0, PredefinedCMaps.Supplement("90ms-RKSJ-H")!.Value);
            var after = PdfFont.Get(font, copy);
            foreach (var code in "AB") Assert.Equal(before.Cid(code, 1), after.Cid(code, 1));
        }
    }

    /// <summary>
    /// CID 3.2 sentence 210, 4.2 sentences 50–54: every CIDFont has an FDArray with at least one Font DICT, and every charstring names one of
    /// them. CffSubsetter.Subset of a CFF without ROS read back: an FDArray of exactly one Font DICT and FDSelect 0 for all glyphs. CffFont.Parse
    /// of a CID-keyed CFF with two Font DICTs (defaultWidthX 700 and 300) and an FDSelect value 5 beyond them: that glyph takes the Private DICT
    /// of Font DICT 0 (width 700).
    /// </summary>
    [Fact]
    public void Cid01_FontDicts_ASubsetWithoutRosGetsOne_AndAnFdSelectBeyondTakesTheFirst()
    {
        var box = Cid01CidCffOutline();
        var nameKeyed = CffBuilder.Build("Probe", new Core.Pdf.Graphics.Matrix(0.001, 0, 0, 0.001, 0, 0), [("a", box, 500), ("b", box, 600), ("c", box, 700)]);
        Assert.False(Cid01Top(nameKeyed).Top.Has(1230));
        var subset = CffSubsetter.Subset(nameKeyed, [1, 3]);
        var (top, _) = Cid01Top(subset);
        var data = new ArraySegment<byte>(subset);
        var r = new FontReader(data) { Position = top.Int(1236)!.Value };
        Assert.Equal(1, CffFont.CffIndex.Read(data, ref r).Count);
        var glyphCount = CffFont.Parse(subset).GlyphCount;
        Assert.All(CffFont.ReadFdSelect(data, top.Int(1237)!.Value, glyphCount), fd => Assert.Equal(0, fd));

        var beyond = CffFont.Parse(Cid01CidCff([1, 2], defaultWidths: [700, 300], fdSelect: [0, 1, 5]));
        Assert.Equal(700, beyond.AdvanceWidth(0));
        Assert.Equal(300, beyond.AdvanceWidth(1));
        Assert.Equal(700, beyond.AdvanceWidth(2));
    }

    /// <summary>A box outline of 110 × 100 (glyph 1 of a CID-keyed probe font).</summary>
    private static GlyphOutline Cid01CidCffOutline() => CffFont.Parse(Cid01CidCff([1])).Outline(1);
}
