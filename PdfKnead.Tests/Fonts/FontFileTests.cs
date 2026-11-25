using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Fonts;

/// <summary>Tests for reading font files: TrueType, OpenType/CFF, bare CFF and Type 1.</summary>
public sealed class FontFileTests
{
    [Fact]
    public void TrueType_LiberationSans_HasMetricsNamesAndOutlines()
    {
        var font = EmbeddedFonts.StandardOpenType("LiberationSans-Regular.ttf");
        Assert.Equal("Liberation Sans", font.FamilyName);
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.Null(font.Cff);

        var a = font.GlyphForUnicode('A');
        Assert.True(a > 0);
        Assert.Equal(1366, font.AdvanceWidth(a));   // as in Arial/Helvetica: 0.667 em
        var outline = font.Outline(a);
        var bounds = outline.Bounds;
        Assert.InRange(bounds.Left, -10, 10);
        Assert.InRange(bounds.Right, 1350, 1380);
        Assert.InRange(bounds.Bottom, 1380, 1420);   // cap height
        Assert.Equal("A", font.GlyphName(a));
        Assert.Equal(a, font.GlyphByName("A"));
    }

    [Fact]
    public void TrueType_CompositeGlyph_CombinesParts()
    {
        var font = EmbeddedFonts.StandardOpenType("LiberationSans-Regular.ttf");
        var a = font.Outline(font.GlyphForUnicode('A'));
        var umlaut = font.Outline(font.GlyphForUnicode('Ä'));
        Assert.True(umlaut.Verbs.Count(v => v == PathVerb.Move) > a.Verbs.Count(v => v == PathVerb.Move));
        Assert.True(umlaut.Bounds.Bottom > a.Bounds.Bottom + 100);   // dots above the A
    }

    [Fact]
    public void Cff_FoxitSymbol_FindsGlyphsByName()
    {
        var font = EmbeddedFonts.StandardCff("FoxitSymbol.cff");
        Assert.False(font.IsCidKeyed);
        var alpha = font.GlyphByName("alpha");
        Assert.True(alpha > 0);
        var bounds = font.Outline(alpha).Bounds;
        Assert.False(bounds.IsEmpty);
        Assert.InRange(bounds.Width, 300, 800);
        Assert.True(font.AdvanceWidth(alpha) > 0);
        Assert.Equal(0.001, font.FontMatrix.A, 6);
    }

    [Fact]
    public void Cff_FoxitDingbats_HasA1()
    {
        var font = EmbeddedFonts.StandardCff("FoxitDingbats.cff");
        var a1 = font.GlyphByName("a1");
        Assert.True(a1 > 0);
        Assert.False(font.Outline(a1).IsEmpty);
        Assert.Equal("✁", GlyphNames.ToUnicode("a1", zapfDingbats: true));
    }

    [Fact]
    public void AllHandwritingFonts_ReadAndDrawLetters()
    {
        // 6 free fonts embedded in the program, the licensed ones from the Schriften-Unterschrift folder next to the tests
        // (Directory.Build.targets copies Schriften/Handschriften-lizenziert there — local files, not in the repository, so the folder may
        // be missing), the generated colour fonts from the user folder of the tests
        var folder = Path.Combine(AppContext.BaseDirectory, SignatureFonts.FolderName);
        var licensed = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Count(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf")
            : 0;
        Assert.Equal(6, EmbeddedFonts.Handwriting.Count(f => SignatureFonts.Origin(f) == SignatureFontOrigin.Embedded));
        Assert.Equal(6 + licensed + TestColorFonts.All.Count, EmbeddedFonts.Handwriting.Count);
        foreach (var file in EmbeddedFonts.Handwriting)
        {
            var font = OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting(file));
            foreach (var c in "Max")
            {
                var g = font.GlyphForUnicode(c);
                Assert.True(g > 0, $"{file}: keine Glyphe für {c}");
                Assert.False(font.Outline(g).IsEmpty, $"{file}: leerer Umriss für {c}");
                Assert.True(font.AdvanceWidth(g) > 0, $"{file}: Breite 0 für {c}");
            }
        }
    }

    [Fact]
    public void OpenTypeCff_UsesCffOutlines()
    {
        // An OpenType font with a 'CFF ' table instead of 'glyf'/'loca': the outlines come from the CFF charstrings
        var symbol = EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        var data = ColorFontBuilder.WithTables(EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf"),
            new Dictionary<string, byte[]?> { ["CFF "] = symbol, ["glyf"] = null, ["loca"] = null });
        var font = OpenTypeFont.Parse(data);
        var cff = CffFont.Parse(symbol);
        Assert.NotNull(font.Cff);
        Assert.False(font.HasTable("glyf"));
        var compared = 0;
        for (var g = 1; g < cff.GlyphCount && compared < 20; g++)
        {
            if (cff.Outline(g).IsEmpty) continue;
            Assert.Equal(cff.Outline(g).Bounds, font.Outline(g).Bounds);
            compared++;
        }
        Assert.Equal(20, compared);
    }

    [Fact]
    public void Type1_EexecEncryptedFont_IsDecryptedAndDrawn()
    {
        var font = Type1Font.Parse(Type1Builder.Build());
        Assert.Equal("KneadTest", font.Name);
        Assert.Equal("A", font.Encoding[65]);
        Assert.Equal("", font.Encoding[66]);
        Assert.Equal(600, font.AdvanceWidth("A"));
        var bounds = font.Outline("A").Bounds;
        Assert.Equal(50, bounds.Left, 3);
        Assert.Equal(550, bounds.Right, 3);
        Assert.Equal(0, bounds.Top, 3);
        Assert.Equal(700, bounds.Bottom, 3);
        // the crossbar comes from the subroutine
        Assert.Equal(2, font.Outline("A").Verbs.Count(v => v == PathVerb.Move));
    }

    /// <summary>
    /// Type 1 Font Format 6.6: div of 32-bit numbers. A chain of 70 divisions by 1/30 000 grew beyond any finite value and put
    /// infinities into the outline and its bounds (docs/FEHLERGEDAECHTNIS.md, No. 1139); the quotient stays within the 32-bit numbers.
    /// </summary>
    [Fact]
    public void Type1_DivisionChain_StaysFinite()
    {
        var font = Type1Font.Parse(Type1Builder.Build(w =>
        {
            w.N(0); w.N(600); w.Op(13);
            w.N(30000);
            for (var i = 0; i < 70; i++)
            {
                w.N(1); w.N(30000); w.Op(12); w.Op(12);   // 1/30 000 …
                w.Op(12); w.Op(12);                       // … and the number so far divided by it
            }
            w.N(0); w.Op(21);
            w.N(10); w.N(10); w.Op(5);
            w.Op(9);
            w.Op(14);
        }));
        var bounds = font.Outline("A").Bounds;
        Assert.True(double.IsFinite(bounds.Left) && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Top) && double.IsFinite(bounds.Bottom),
            $"{bounds.Left} {bounds.Right} {bounds.Top} {bounds.Bottom}");
    }

    [Fact]
    public void GlyphNames_FollowTheAdobeGlyphList()
    {
        Assert.Equal("ä", GlyphNames.ToUnicode("adieresis"));
        Assert.Equal("€", GlyphNames.ToUnicode("Euro"));
        Assert.Equal("ffi", GlyphNames.ToUnicode("f_f_i"));
        Assert.Equal("A", GlyphNames.ToUnicode("uni0041"));
        Assert.Equal("😀", GlyphNames.ToUnicode("u1F600"));
        Assert.Equal("a", GlyphNames.ToUnicode("a.sc"));
        Assert.Null(GlyphNames.ToUnicode(".notdef"));
        Assert.Equal("adieresis", GlyphNames.FromUnicode('ä'));
    }

    [Fact]
    public void BrokenFontData_ThrowsFontFormatException()
    {
        Assert.Throws<FontFormatException>(() => OpenTypeFont.Parse([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
        Assert.Throws<FontFormatException>(() => CffFont.Parse(new byte[] { 1, 0, 4, 1 }));
        Assert.Throws<FontFormatException>(() => Type1Font.Parse(Encoding.ASCII.GetBytes("%!PS kein eexec")));
    }
}

/// <summary>Builds a small but genuine Type 1 font (clear-text part plus eexec section with encrypted charstrings).</summary>
internal static class Type1Builder
{
    /// <param name="a">The charstring of „A“ in place of the triangle with its crossbar.</param>
    public static byte[] Build(Action<Writer>? a = null)
    {
        // Subr 0: the crossbar of the A as a separate subpath, then return
        var subr0 = Charstring(w => { w.N(-350); w.N(300); w.Op(21); w.N(300); w.N(0); w.Op(5); w.Op(9); w.Op(11); });
        // A: hsbw 50 600, triangle, crossbar from Subr 0, endchar
        var glyphA = Charstring(a ?? (w =>
        {
            w.N(50); w.N(600); w.Op(13);
            w.N(0); w.N(0); w.Op(21);
            w.N(250); w.N(700); w.Op(5);
            w.N(250); w.N(-700); w.Op(5);
            w.Op(9);
            w.N(0); w.Op(10);
            w.Op(14);
        }));
        var notdef = Charstring(w => { w.N(0); w.N(500); w.Op(13); w.Op(14); });

        var priv = new MemoryStream();
        void Text(string s) => priv.Write(Encoding.Latin1.GetBytes(s));
        void Bin(byte[] b) => priv.Write(b);
        Text("dup /Private 8 dict dup begin /RD{string currentfile exch readstring pop}executeonly def\n");
        Text("/lenIV 4 def\n/Subrs 1 array\n");
        Text($"dup 0 {subr0.Length} RD "); Bin(subr0); Text(" NP\nND\n");
        Text("2 index /CharStrings 2 dict dup begin\n");
        Text($"/.notdef {notdef.Length} RD "); Bin(notdef); Text(" ND\n");
        Text($"/A {glyphA.Length} RD "); Bin(glyphA); Text(" ND\n");
        Text("end end\nmark currentfile closefile\n");

        var clear = "%!PS-AdobeFont-1.0: KneadTest 001\n/FontName /KneadTest def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n"
                    + "/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for dup 65 /A put readonly def\ncurrentfile eexec\n";
        return [.. Encoding.Latin1.GetBytes(clear), .. Encrypt(priv.ToArray(), 55665)];
    }

    private static byte[] Charstring(Action<Writer> body)
    {
        var w = new Writer();
        body(w);
        return Encrypt(w.Bytes.ToArray(), 4330);
    }

    /// <summary>Encrypts as specified for Type 1 fonts, with four leading padding bytes.</summary>
    private static byte[] Encrypt(byte[] plain, ushort key)
    {
        var r = key;
        var result = new byte[plain.Length + 4];
        for (var i = 0; i < result.Length; i++)
        {
            var p = i < 4 ? (byte)(17 * i) : plain[i - 4];
            var c = (byte)(p ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            result[i] = c;
        }
        return result;
    }

    internal sealed class Writer
    {
        public readonly List<byte> Bytes = [];

        public void Op(int op) => Bytes.Add((byte)op);

        public void N(int v)
        {
            if (v is >= -107 and <= 107) Bytes.Add((byte)(v + 139));
            else if (v is >= 108 and <= 1131) { v -= 108; Bytes.Add((byte)(v / 256 + 247)); Bytes.Add((byte)(v % 256)); }
            else if (v is >= -1131 and <= -108) { v = -v - 108; Bytes.Add((byte)(v / 256 + 251)); Bytes.Add((byte)(v % 256)); }
            else { Bytes.Add(255); Bytes.Add((byte)(v >> 24)); Bytes.Add((byte)(v >> 16)); Bytes.Add((byte)(v >> 8)); Bytes.Add((byte)v); }
        }
    }
}
