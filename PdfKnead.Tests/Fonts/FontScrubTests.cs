using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Tests for scrubbing font programs after redaction (docs/FEHLERGEDAECHTNIS.md, No. 169): a character shown only in redacted text must not
/// be readable from the font any more — neither its outline nor its mapping, name or metrics.
/// </summary>
public sealed class FontScrubTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>
    /// A font with CFF outlines written anew keeps the version "OTTO" (ISO/IEC 14496-22, 5.2); with TrueType outlines it stays 1.0. Scrubbed
    /// after redacting, the CFF font of a public test file came out as 1.0, and a reference reader took it for TrueType
    /// (docs/FEHLERGEDAECHTNIS.md, No. 411).
    /// </summary>
    [Fact]
    public void Assembled_VersionTellsTheOutlines()
    {
        var head = new byte[54];
        var cff = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["CFF "] = [1, 0, 4, 2], ["head"] = head });
        var glyf = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["glyf"] = [0, 0], ["loca"] = [0, 0], ["head"] = head });
        Assert.Equal("OTTO"u8.ToArray(), cff[..4]);
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, glyf[..4]);
    }

    /// <summary>The CFF font of that public test file, scrubbed as after redacting: still "OTTO".</summary>
    [Fact]
    public void CffFontOfATestFile_ScrubbedStaysOpenTypeCff()
    {
        using var doc = Core.Pdf.Document.PdfDocument.Open(Path.Combine(Repo(), "TestPDFs", "pdfjs", "issue215.pdf"));
        var program = doc.Pages.SelectMany(p => p.Resources?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font"))?.Entries
                .Select(e => p.Resources!.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font"))!.GetDictionary(e.Key)) ?? [])
            .Select(f => f?.GetDictionary(Core.Pdf.Objects.PdfName.Get("FontDescriptor"))?.GetStream(Core.Pdf.Objects.PdfName.Get("FontFile2")))
            .Where(s => s is not null).Select(s => doc.GetStreamData(s!)).First(d => d.AsSpan(0, 4).SequenceEqual("OTTO"u8));
        var scrubbed = TrueTypeSubsetter.Scrub(program, [0, 1, 2, 3]);
        Assert.Equal("OTTO"u8.ToArray(), scrubbed[..4]);
    }

    [Fact]
    public void TrueType_GlyphsNotKept_LeaveNoTrace()
    {
        var data = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var font = OpenTypeFont.Parse(data);
        int Gid(char c) => font.GlyphForUnicode(c);
        var kept = "Text".Select(Gid).ToList();
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(data, kept));

        foreach (var c in "Text")
        {
            Assert.Equal(Gid(c), scrubbed.GlyphForUnicode(c));
            Assert.False(scrubbed.Outline(Gid(c)).IsEmpty);
            Assert.Equal(font.GlyphName(Gid(c)), scrubbed.GlyphName(Gid(c)));
        }
        foreach (var c in "GQßh")
        {
            Assert.Equal(0, scrubbed.GlyphForUnicode(c));
            Assert.True(scrubbed.Outline(Gid(c)).IsEmpty, $"Umriss von {c}");
            Assert.NotEqual(font.GlyphName(Gid(c)), scrubbed.GlyphName(Gid(c)));
            Assert.Equal(0, scrubbed.AdvanceWidth(Gid(c)));
        }
        // nothing that lists glyphs in pairs or classes
        Assert.False(scrubbed.HasTable("GSUB") || scrubbed.HasTable("GPOS") || scrubbed.HasTable("kern"));
    }

    /// <summary>
    /// The check after scrubbing (docs/FEHLERGEDAECHTNIS.md, No. 379): glyphs kept as they were read the same before and after; a glyph that
    /// is to stay but reads differently afterwards — in a damaged program the scrubber read one huge glyph as a small one, and its letter
    /// came under a bar — makes the program stay whole.
    /// </summary>
    [Fact]
    public void Scrubbing_KeptGlyphsMustReadTheSame()
    {
        var data = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var font = OpenTypeFont.Parse(data);
        var kept = "Text".Select(c => font.GlyphForUnicode(c)).ToHashSet();
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(data, kept));
        Assert.True(Core.Pdf.Writing.FontScrub.SameGlyphs(kept, font.GlyphCount, font.Outline, font.AdvanceWidth, scrubbed.Outline, scrubbed.AdvanceWidth, scrubbed.GlyphCount));
        // one more glyph that should have stayed, but the scrubbed program has it empty
        var also = new HashSet<int>(kept) { font.GlyphForUnicode('Q') };
        Assert.False(Core.Pdf.Writing.FontScrub.SameGlyphs(also, font.GlyphCount, font.Outline, font.AdvanceWidth, scrubbed.Outline, scrubbed.AdvanceWidth, scrubbed.GlyphCount));
    }

    /// <summary>
    /// A damaged CFF font whose CharStrings hold no glyph: refused as unreadable without reading past the end of a table — glyph 0, which is
    /// always kept, was looked up in the empty FDSelect (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 379).
    /// </summary>
    [Fact]
    public void Cff_WithoutGlyphs_RefusedWithoutReadingPastATable()
    {
        var data = File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "Standard", "FoxitSymbol.cff"));
        // the CharStrings INDEX made empty: its count (two bytes at the offset of the Top DICT's operator 17) set to 0
        var reader = new FontReader(data);
        reader.U8();
        reader.U8();
        reader.Position = reader.U8();
        _ = CffFont.CffIndex.Read(data, ref reader);
        var tops = CffFont.CffIndex.Read(data, ref reader);
        var offset = CffFont.Dict.Parse(tops.Item(data, 0)).Int(17)!.Value;
        var damaged = (byte[])data.Clone();
        damaged[offset] = damaged[offset + 1] = 0;
        var outOfRange = 0;
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is IndexOutOfRangeException) Interlocked.Increment(ref outOfRange);
        }
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(damaged, [1, 2]));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
        Assert.Equal(0, outOfRange);
    }

    /// <summary>
    /// A plain CFF font (names, built-in encoding): the glyphs not kept become empty and nameless at their numbers, their codes leave the
    /// encoding; the kept ones look as before without any subroutine (docs/FEHLERGEDAECHTNIS.md, No. 174).
    /// </summary>
    [Fact]
    public void Cff_GlyphsNotKept_LeaveNoTrace()
    {
        var data = File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "Standard", "FoxitSymbol.cff"));
        var font = CffFont.Parse(data);
        string[] kept = ["alpha", "beta", "Sigma"], removed = ["gamma", "Omega", "infinity"];
        var gids = kept.Select(font.GlyphByName).ToList();
        Assert.All(gids.Concat(removed.Select(font.GlyphByName)), g => Assert.True(g > 0));
        var (bytes, keep) = CffScrubber.Scrub(data, gids);
        var scrubbed = CffFont.Parse(bytes);
        Assert.Equal(font.GlyphCount, scrubbed.GlyphCount);
        foreach (var name in kept)
        {
            var g = font.GlyphByName(name);
            Assert.Equal(g, scrubbed.GlyphByName(name));
            Assert.Equal(font.Outline(g).Bounds, scrubbed.Outline(g).Bounds);
            Assert.Equal(font.AdvanceWidth(g), scrubbed.AdvanceWidth(g));
        }
        foreach (var name in removed)
        {
            var g = font.GlyphByName(name);
            Assert.DoesNotContain(g, keep);
            Assert.Equal(-1, scrubbed.GlyphByName(name));
            Assert.True(scrubbed.Outline(g).IsEmpty, name);
            Assert.DoesNotContain(g, scrubbed.Encoding);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(name)));   // its name is no custom string any more
        }
        foreach (var name in kept) Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(name)) >= 0);
        for (var c = 0; c < 256; c++)
            if (font.Encoding[c] is > 0 and var g && gids.Contains(g)) Assert.Equal(g, scrubbed.Encoding[c]);
    }

    /// <summary>
    /// A CID-keyed CFF font with many Font DICTs and subroutines (the CFF of Noto CJK): kept CIDs find their glyphs as before, the others
    /// are empty and have no CID any more (docs/FEHLERGEDAECHTNIS.md, No. 174).
    /// </summary>
    [Fact]
    public void CidCff_GlyphsNotKept_LeaveNoTrace()
    {
        var noto = OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));
        var table = noto.Table("CFF ")!.Value;
        var data = new ArraySegment<byte>(noto.FileData, table.Offset, table.Length);
        var font = CffFont.Parse(data);
        Assert.True(font.IsCidKeyed);
        int[] kept = [noto.GlyphForUnicode('漢'), noto.GlyphForUnicode('字'), noto.GlyphForUnicode('A')];
        int[] removed = [noto.GlyphForUnicode('秘'), noto.GlyphForUnicode('密'), noto.GlyphForUnicode('Z')];
        var scrubbed = CffFont.Parse(CffScrubber.Scrub(data, kept).Data);
        Assert.Equal(font.GlyphCount, scrubbed.GlyphCount);
        foreach (var g in kept)
        {
            Assert.Equal(font.Outline(g).Bounds, scrubbed.Outline(g).Bounds);
            Assert.Equal(font.AdvanceWidth(g), scrubbed.AdvanceWidth(g));
        }
        foreach (var g in removed)
            Assert.True(scrubbed.Outline(g).IsEmpty);
        // the CID of a kept glyph still leads to it; a removed one has no CID any more
        Assert.All(kept, g => Assert.Equal(g, scrubbed.GlyphByCid(font.CidOf(g))));
        Assert.All(removed, g => Assert.True(font.CidOf(g) > 0 && scrubbed.CidOf(g) == 0 && scrubbed.GlyphByCid(font.CidOf(g)) != g));
    }

    /// <summary>An OpenType font with CFF outlines: scrubbed like TrueType (cmap, metrics, layout tables), the outlines by the CFF scrubber.</summary>
    [Fact]
    public void OpenTypeCff_GlyphsNotKept_LeaveNoTrace()
    {
        var noto = OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "vhea", "vmtx", "GSUB", "GPOS"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        var otf = TrueTypeSubsetter.Finish(tables);
        var font = OpenTypeFont.Parse(otf);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(otf, "漢字A".Select(c => font.GlyphForUnicode(c))));
        foreach (var c in "漢字A")
        {
            Assert.Equal(font.GlyphForUnicode(c), scrubbed.GlyphForUnicode(c));
            Assert.Equal(font.Outline(font.GlyphForUnicode(c)).Bounds, scrubbed.Outline(font.GlyphForUnicode(c)).Bounds);
        }
        foreach (var c in "秘密Z")
        {
            Assert.Equal(0, scrubbed.GlyphForUnicode(c));
            Assert.True(scrubbed.Outline(font.GlyphForUnicode(c)).IsEmpty);
            Assert.Equal(0, scrubbed.AdvanceWidth(font.GlyphForUnicode(c)));
        }
        Assert.False(scrubbed.HasTable("GSUB") || scrubbed.HasTable("GPOS"));
    }

    /// <summary>
    /// A damaged OpenType font with CFF outlines but without head: scrubbing fails as for any unreadable font — a KeyNotFoundException
    /// broke off saving (found by fuzzing, docs/FEHLERGEDAECHTNIS.md, No. 183).
    /// </summary>
    [Fact]
    public void OpenTypeCff_WithoutHead_IsAFormatError()
    {
        var noto = OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        var otf = TrueTypeSubsetter.Finish(tables);
        // rename head in the table directory
        var count = otf[4] << 8 | otf[5];
        for (var i = 0; i < count; i++)
            if (otf.AsSpan(12 + 16 * i, 4).SequenceEqual("head"u8)) otf[12 + 16 * i + 3] = (byte)'X';
        Assert.Throws<FontFormatException>(() => TrueTypeSubsetter.Scrub(otf, [1, 2, 3]));
    }

    /// <summary>
    /// The check after scrubbing a Type 1 program (docs/FEHLERGEDAECHTNIS.md, No. 393): the display reads it as before — a glyph kept stays
    /// present and the same, a glyph that was missing stays missing, and a program the display could not read does not become readable (a
    /// letter drawn as nothing before appeared afterwards).
    /// </summary>
    [Fact]
    public void Type1_ScrubbedMustReadTheSame()
    {
        var font = Type1Builder.Build();
        Assert.True(Core.Pdf.Writing.FontScrub.SameType1(font, font, new HashSet<string> { "A" }));
        var scrubbed = Type1Scrubber.Scrub(font, new HashSet<string> { "A" }).Data;
        Assert.True(Core.Pdf.Writing.FontScrub.SameType1(font, scrubbed, new HashSet<string> { "A" }));
        // a glyph the display did not find before must not be found afterwards, and the other way round
        Assert.False(Core.Pdf.Writing.FontScrub.SameType1(font, Type1Scrubber.Scrub(font, new HashSet<string>()).Data, new HashSet<string> { "A" }));
        // unreadable before, readable afterwards
        Assert.False(Core.Pdf.Writing.FontScrub.SameType1(System.Text.Encoding.ASCII.GetBytes("%!PS kaputt eexec"), font, new HashSet<string> { "A" }));
    }

    /// <summary>
    /// A Type 1 font (FontFile): glyphs not kept leave the CharStrings with their names and the custom encoding with their codes; a
    /// subroutine only they called becomes an empty "return"; the kept glyph looks as before (docs/FEHLERGEDAECHTNIS.md, No. 180).
    /// </summary>
    [Fact]
    public void Type1_GlyphsNotKept_LeaveNoTrace()
    {
        var font = Type1Sample.Build();
        var original = Type1Font.Parse(font.Data);
        var (data, clear, encrypted, trailer) = Type1Scrubber.Scrub(font.Data, new HashSet<string> { "B" }, font.Clear, font.Encrypted);
        Assert.Equal(data.Length, clear + encrypted + trailer);
        var scrubbed = Type1Font.Parse(data);
        Assert.True(scrubbed.HasGlyph("B"));
        Assert.Equal(original.Outline("B").Bounds, scrubbed.Outline("B").Bounds);
        Assert.Equal(original.AdvanceWidth("B"), scrubbed.AdvanceWidth("B"));
        foreach (var name in (string[])["A", "C"])
        {
            Assert.False(scrubbed.HasGlyph(name), name);
            Assert.DoesNotContain(name, scrubbed.Encoding);
        }
        Assert.Equal("B", scrubbed.Encoding['B']);
        // subroutine 4 (part of A) is an empty "return" now: lenIV 4 random bytes and one operator
        var priv = System.Text.Encoding.Latin1.GetString(Type1Sample.Decrypt(data.AsSpan(clear, encrypted), 55665, 4));
        Assert.Contains("dup 4 5 RD ", priv);
        Assert.DoesNotContain("/A ", priv);
    }

    /// <summary>
    /// A Type 1 font embedded as PFB segments (0x80 0x01 clear text, 0x80 0x02 eexec part, 0x80 0x01 trailer, 0x80 0x03 — tolerated by readers)
    /// is scrubbed like any other; it was left whole (docs/FEHLERGEDAECHTNIS.md, No. 286). The result is the usual form with its three lengths.
    /// </summary>
    [Fact]
    public void Type1AsPfb_GlyphsNotKept_LeaveNoTrace()
    {
        var font = Type1Sample.Build();
        static byte[] Segment(byte type, ReadOnlySpan<byte> payload) =>
            [0x80, type, (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), (byte)(payload.Length >> 24), .. payload];
        var trailerStart = font.Clear + font.Encrypted;
        byte[] pfb = [.. Segment(1, font.Data.AsSpan(0, font.Clear)), .. Segment(2, font.Data.AsSpan(font.Clear, font.Encrypted)),
            .. Segment(1, font.Data.AsSpan(trailerStart)), 0x80, 3];
        var (data, clear, encrypted, trailer) = Type1Scrubber.Scrub(pfb, new HashSet<string> { "B" });
        Assert.NotEqual(0x80, data[0]);
        Assert.Equal(data.Length, clear + encrypted + trailer);
        var scrubbed = Type1Font.Parse(data);
        Assert.True(scrubbed.HasGlyph("B"));
        Assert.False(scrubbed.HasGlyph("A"));
        Assert.False(scrubbed.HasGlyph("C"));
    }

    /// <summary>A kept glyph keeps the subroutine it calls.</summary>
    [Fact]
    public void Type1_KeptGlyph_KeepsItsSubroutine()
    {
        var font = Type1Sample.Build();
        var original = Type1Font.Parse(font.Data);
        var (data, _, _, _) = Type1Scrubber.Scrub(font.Data, new HashSet<string> { "A" }, font.Clear, font.Encrypted);
        var scrubbed = Type1Font.Parse(data);
        Assert.False(scrubbed.Outline("A").IsEmpty);
        Assert.Equal(original.Outline("A").Bounds, scrubbed.Outline("A").Bounds);
        Assert.False(scrubbed.HasGlyph("B"));
    }

    /// <summary>
    /// OpenType cmap 9.3 and 9.4.1: the records of the cleaned cmap sorted by platform and encoding, a subtable of a format the reader does
    /// not read (here format 14 under (0,5)) left out instead of written as an empty format 4, and a Macintosh subtable with its language
    /// (here 18, Turkish) (docs/FEHLERGEDAECHTNIS.md, No. 1143).
    /// </summary>
    [Fact]
    public void ScrubbedCmap_SortedWithoutUnreadFormatsAndWithTheMacLanguage()
    {
        var ttf = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var a = OpenTypeFont.Parse(ttf).GlyphForUnicode('A');
        var cmap = new List<byte>();
        void U16(int v) => cmap.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(int v) { U16(v >> 16); U16(v); }
        const int first = 4 + 3 * 8;
        U16(0); U16(3);
        U16(3); U16(1); U32(first);                  // (3,1) format 4 at 28 (32 bytes)
        U16(1); U16(0); U32(first + 32);             // (1,0) format 6 at 60 (12 bytes)
        U16(0); U16(5); U32(first + 44);             // (0,5) format 14 at 72 (10 bytes)
        U16(4); U16(32); U16(0); U16(4); U16(4); U16(1); U16(0);              // format 4, two segments
        U16(0x41); U16(0xFFFF); U16(0); U16(0x41); U16(0xFFFF);               // ends, pad, starts
        U16((a - 0x41) & 0xFFFF); U16(1); U16(0); U16(0);                     // deltas, range offsets
        U16(6); U16(12); U16(18); U16(0x41); U16(1); U16(a);                  // format 6, language 18
        U16(14); U32(10); U32(0);                                             // format 14, no selectors
        var font = ColorFontBuilder.WithTables(ttf, new Dictionary<string, byte[]?> { ["cmap"] = [.. cmap] });
        var cleaned = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [0, a]));
        Assert.Equal(new (int, int)[] { (1, 0), (3, 1) }, cleaned.CmapSubtables);
        Assert.Equal(a, cleaned.Lookup(1, 0, 0x41));
        Assert.Equal(a, cleaned.Lookup(3, 1, 'A'));
        var data = cleaned.FileData;
        var at = cleaned.CmapOffset(1, 0);
        Assert.Equal(18, data[at + 4] << 8 | data[at + 5]);
    }

    /// <summary>
    /// Type 1 Font Format 2.5 (and CFF, TN 5176 Table 9, after it): a changed font must not carry the UniqueID and XUID of the unchanged
    /// one. Cleaning kept them in the clear text, the private part and the Top DICT (docs/FEHLERGEDAECHTNIS.md, No. 1143).
    /// </summary>
    [Fact]
    public void CleanedPrograms_DropUniqueIdAndXuid()
    {
        var type1 = Type1Builder.Build(clearExtra: "/UniqueID 4711815 def\n/XUID [6 4711815] readonly def\n", privateExtra: "/UniqueID 4711815 def\n");
        var (cleaned, clear, encrypted, _) = Type1Scrubber.Scrub(type1, new HashSet<string> { "A" });
        var text = System.Text.Encoding.Latin1.GetString(cleaned, 0, clear);
        Assert.DoesNotContain("/UniqueID", text);
        Assert.DoesNotContain("/XUID", text);
        Assert.Equal(600, Type1Font.Parse(cleaned).AdvanceWidth("A"));
        // the private part, decrypted (eexec, key 55665)
        var priv = new byte[encrypted];
        ushort r = 55665;
        for (var i = 0; i < encrypted; i++)
        {
            var c = cleaned[clear + i];
            priv[i] = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
        }
        Assert.DoesNotContain("4711815", System.Text.Encoding.Latin1.GetString(priv));

        // CFF: UniqueID (13), XUID (14) in the Top DICT
        byte[] Index(params byte[][] items)
        {
            var result = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 4 };
            var offset = 1;
            void Offset(int v) => result.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            Offset(offset);
            foreach (var item in items) Offset(offset += item.Length);
            foreach (var item in items) result.AddRange(item);
            return [.. result];
        }
        byte[] Top(int charStrings) => [29, 0, 0x47, 0xE5, 0x87, 13, 140, 141, 142, 14, 29, (byte)(charStrings >> 24), (byte)(charStrings >> 16),
            (byte)(charStrings >> 8), (byte)charStrings, 17];
        var name = Index("Probe"u8.ToArray());
        var at = 4 + name.Length + Index(Top(0)).Length + 2 + 2;
        byte[] cff = [1, 0, 4, 4, .. name, .. Index(Top(at)), 0, 0, 0, 0, .. Index([14], [139, 139, 21, 14])];
        var (cleanCff, _) = CffScrubber.Scrub(cff, [1]);
        Assert.False(cleanCff.AsSpan().IndexOf((ReadOnlySpan<byte>)[29, 0, 0x47, 0xE5, 0x87, 13]) >= 0, "UniqueID");
        Assert.False(cleanCff.AsSpan().IndexOf((ReadOnlySpan<byte>)[140, 141, 142, 14]) >= 0, "XUID");
    }

    /// <summary>
    /// OpenType name 30.2, OS/2 31.2.12/21/22 and recommendations 3.27: a font rebuilt as symbolic (its only cmap (3,0), the PDF/A copy of a
    /// symbolic TrueType font) has its Windows names in encoding 0, PANOSE family kind 5 and usFirst/LastCharIndex of its codes. Before, the
    /// names (3,1), PANOSE and range of Liberation Sans stayed (docs/FEHLERGEDAECHTNIS.md, No. 1146).
    /// </summary>
    [Fact]
    public void RebuiltAsSymbolic_NamesPanoseAndRangeFollow()
    {
        var ttf = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var a = OpenTypeFont.Parse(ttf).GlyphForUnicode('A');
        var rebuilt = TrueTypeSubsetter.Rebuild(ttf, new Dictionary<int, int> { [a] = 1000 }, [], [(0xF041, a), (0xF05A, a)]);
        var font = OpenTypeFont.Parse(rebuilt);
        Assert.Equal(a, font.Lookup(3, 0, 0xF041));
        var os2 = font.Table("OS/2")!.Value;
        var data = font.FileData;
        Assert.Equal(5, data[os2.Offset + 32]);
        Assert.Equal(0xF041, data[os2.Offset + 64] << 8 | data[os2.Offset + 65]);
        Assert.Equal(0xF05A, data[os2.Offset + 66] << 8 | data[os2.Offset + 67]);
        var name = font.Table("name")!.Value;
        int count = data[name.Offset + 2] << 8 | data[name.Offset + 3];
        var windows = Enumerable.Range(0, count).Select(i => name.Offset + 6 + i * 12)
            .Where(r => (data[r] << 8 | data[r + 1]) == 3).Select(r => data[r + 2] << 8 | data[r + 3]).ToList();
        Assert.NotEmpty(windows);
        Assert.All(windows, e => Assert.Equal(0, e));
        Assert.Equal("Liberation Sans", font.FamilyName);
    }

}

/// <summary>A small Type 1 font for tests: glyphs A (through subroutine 4), B and C, encoding A B C at their ASCII codes.</summary>
internal static class Type1Sample
{
    public static (byte[] Data, int Clear, int Encrypted) Build()
    {
        static byte[] Num(int v) => v is >= -107 and <= 107 ? [(byte)(v + 139)]
            : v is >= 108 and <= 1131 ? [(byte)((v - 108) / 256 + 247), (byte)((v - 108) % 256)]
            : [(byte)((-v - 108) / 256 + 251), (byte)((-v - 108) % 256)];
        static byte[] Code(params object[] parts) => [.. parts.SelectMany(p => p is int i ? Num(i) : (byte[])p)];
        byte[] hsbw = [13], rmoveto = [21], rlineto = [5], closepath = [9], endchar = [14], callsubr = [10], ret = [11];
        var subrs = new List<byte[]> { Code(ret), Code(ret), Code(ret), Code(ret), Code(300, 0, rlineto, 0, 300, rlineto, -300, 0, rlineto, ret) };
        var glyphs = new List<(string Name, byte[] Code)>
        {
            (".notdef", Code(0, 250, hsbw, endchar)),
            ("A", Code(0, 600, hsbw, 100, 100, rmoveto, 4, callsubr, closepath, endchar)),
            ("B", Code(0, 600, hsbw, 50, 50, rmoveto, 100, 0, rlineto, 0, 100, rlineto, closepath, endchar)),
            ("C", Code(0, 600, hsbw, 20, 20, rmoveto, 200, 0, rlineto, 0, 200, rlineto, closepath, endchar)),
        };
        var priv = new MemoryStream();
        void Text(string t) => priv.Write(System.Text.Encoding.ASCII.GetBytes(t));
        void Binary(byte[] plain)
        {
            var encrypted = Encrypt([0, 0, 0, 0, .. plain], 4330);
            Text($"{encrypted.Length} RD ");
            priv.Write(encrypted);
        }
        Text("dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n/ND{noaccess def}executeonly def\n" +
             "/NP{noaccess put}executeonly def\n/lenIV 4 def\n/Subrs 5 array\n");
        for (var i = 0; i < subrs.Count; i++)
        {
            Text($"dup {i} ");
            Binary(subrs[i]);
            Text(" NP\n");
        }
        Text("ND\n2 index /CharStrings 4 dict dup begin\n");
        foreach (var (name, code) in glyphs)
        {
            Text($"/{name} ");
            Binary(code);
            Text(" ND\n");
        }
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        var clear = System.Text.Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0: Probe 001\n12 dict begin\n/FontName /Probe def\n/FontType 1 def\n" +
            "/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/FontBBox {0 0 1000 1000} readonly def\n/Encoding 256 array\n" +
            "0 1 255 {1 index exch /.notdef put} for\ndup 65 /A put\ndup 66 /B put\ndup 67 /C put\nreadonly def\ncurrentdict end\ncurrentfile eexec\n");
        var encryptedPart = Encrypt([0, 0, 0, 0, .. priv.ToArray()], 55665);
        var trailer = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('0', 64) + "\n", 8)) + "cleartomark\n");
        return ([.. clear, .. encryptedPart, .. trailer], clear.Length, encryptedPart.Length);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ushort key)
    {
        var r = key;
        var result = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            var c = (byte)(plain[i] ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            result[i] = c;
        }
        return result;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, ushort key, int skip)
    {
        var r = key;
        var result = new byte[Math.Max(0, data.Length - skip)];
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            var plain = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            if (i >= skip) result[i - skip] = plain;
        }
        return result;
    }
}
