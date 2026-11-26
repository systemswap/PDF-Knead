using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, Adobe TN 5014 (CMap and CIDFont files), appendices A–C (draft cid-04): the Japanese CMaps by name, the names of composite fonts, codes of
/// four bytes, def and begin/end in CMap programs, ranges over several bytes.
/// </summary>
public sealed partial class M7NormTests
{
    private static string Cid04Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>
    /// CID A.2 sentences 27–30, 31–41, 42–53: the Japanese CMap files of Table A.1. PredefinedCMaps.Get finds each of the 21 names; WMode is 1
    /// exactly for Add-RKSJ-V, Add-V, Ext-RKSJ-V, Ext-V, EUC-V, RKSJ-V, V and NWP-V; each maps at least one code to a CID other than 0.
    /// </summary>
    [Fact]
    public void Cid04_JapaneseCMapsOfTableA1_AreAllAvailable()
    {
        string[] names = ["83pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V", "Add-H", "Add-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "Ext-H", "Ext-V", "EUC-H", "EUC-V",
                          "RKSJ-H", "RKSJ-V", "H", "V", "NWP-H", "NWP-V", "WP-Symbol", "Roman", "Katakana", "Hankaku"];
        string[] vertical = ["Add-RKSJ-V", "Add-V", "Ext-RKSJ-V", "Ext-V", "EUC-V", "RKSJ-V", "V", "NWP-V"];
        Assert.Equal(21, names.Length);
        foreach (var name in names)
        {
            var cmap = PredefinedCMaps.Get(name);
            Assert.True(cmap is not null, name);
            Assert.True(cmap.WMode == (vertical.Contains(name) ? 1 : 0), $"{name}: WMode {cmap.WMode}");
            var (code, bytes, cid) = cmap.CidMappings().First(m => m.Cid != 0);
            Assert.True(cmap.ToCid(code, bytes) == cid && cid != 0, name);
        }
    }

    /// <summary>
    /// CID A.4 sentences 8–10: the name of a composite font in the PostScript convention joins CIDFont and CMap with „--“. In PDF the rule of
    /// ISO 32000-2 9.7.6.1 applies (one hyphen): text set with Noto CJK (CFF outlines) and saved — the /BaseFont of the Type 0 font is the
    /// /BaseFont of its CIDFontType0, a hyphen and „Identity-H“, without „--“ (ElementFace.Type0, since FG 1147).
    /// </summary>
    [Fact]
    public void Cid04_ElementFontWithCffOutlines_Type0NamedLikeItsCidFont()
    {
        var file = Path.Combine(Cid04Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc");
        var face = ElementFace.For("probe-cid04-noto", () => OpenTypeFont.Parse(File.ReadAllBytes(file), 0), false, false, false);
        var saved = face.SavedFont(new Dictionary<int, string> { [face.Glyph('あ')] = "あ" });
        var cidFont = (PdfDictionary)saved.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
        Assert.Equal("CIDFontType0", cidFont.GetName(PdfName.Subtype)?.Value);
        var name = saved.GetName(PdfName.Get("BaseFont"))!.Value;
        Assert.Equal(cidFont.GetName(PdfName.Get("BaseFont"))!.Value + "-Identity-H", name);
        Assert.DoesNotContain("--", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// CID A.4 sentences 13–14: the name of a CMap tells its character collection. Type 0 font with /Encoding /90ms-RKSJ-H over a CIDFont
    /// Adobe-GB1, PDF/A-2b: the check reports that the collections do not agree (Japan1 from the name of the CMap; PredefinedCMaps.Collection,
    /// PdfACheck.CMapCollection) and the copy names Adobe-Japan1 (PdfWriter.RebuildComposite).
    /// </summary>
    [Fact]
    public void Cid04_CMapName_GivesTheCollection_AgainstAGb1CidFont()
    {
        var original = PdfARuleCases.WithCidTrueType(
            t => t.Replace("/Encoding/Identity-H", "/Encoding/90ms-RKSJ-H", StringComparison.Ordinal),
            cid: c => c.Replace("/Registry(Adobe)/Ordering(Identity)/Supplement 0", "/Registry(Adobe)/Ordering(GB1)/Supplement 2", StringComparison.Ordinal),
            shown: "(AB)")();
        Assert.Equal("Japan1", PredefinedCMaps.Collection("90ms-RKSJ-H"));
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == PdfARules.CidSystemInfo.Id(PdfAConformance.A2b));
        var (copy, font) = Cid01Copy(original);
        using (copy)
        {
            var cidFont = (PdfDictionary)font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
            var (registry, ordering, _) = Cid01Collection(cidFont.GetDictionary(PdfName.Get("CIDSystemInfo")));
            Assert.Equal(("Adobe", "Japan1"), (registry, ordering));
        }
    }

    /// <summary>CID A.6 sentence 1: codes of up to four bytes. Codespace 00000000–FFFFFFFF with cidchar 00010203 → 7: ReadCode takes four bytes, ToCid gives 7.</summary>
    [Fact]
    public void Cid04_FourByteCodes_AreRead()
    {
        var cmap = Cid03Parse("1 begincodespacerange <00000000> <FFFFFFFF> endcodespacerange 1 begincidchar <00010203> 7 endcidchar");
        Assert.Equal(4, cmap.ReadCode([0x00, 0x01, 0x02, 0x03, 0x41], 0, out var code));
        Assert.Equal(0x00010203u, code);
        Assert.Equal(7, cmap.ToCid(code, 4));
    }

    /// <summary>
    /// CID B.3 sentence 8 (reader, special rule A199): what stands in a procedure { … } would not be executed when the CMap is read — but two of
    /// three reference readers take the mappings inside it (probe K23b, 26.11.2026), and so does PDF-Knead: CMap.Parse treats braces as
    /// delimiters only, so „{ 1 begincidchar &lt;41&gt; 9 endcidchar } pop“ beside a codespace 00–FF maps 41 to CID 9 (the text: CID 0).
    /// </summary>
    [Fact]
    public void Cid04_ProcedureBodies_TheirMappingsCount_AsTwoOfThreeReaders()
    {
        var cmap = Cid03Parse("1 begincodespacerange <00> <FF> endcodespacerange { 1 begincidchar <41> 9 endcidchar } pop");
        Assert.Equal(9, cmap.ToCid(0x41, 1));   // the text: 0 (FRAGEN.md 30)
    }

    /// <summary>
    /// CID B.3 sentences 11 and 15–20: def binds the value right before it to the name before that; values computed by other operators are not
    /// evaluated by the reader. „/WMode 1 def“ gives WMode 1; „/WMode 2 1 sub def“ and „1 /WMode exch def“ give 0 (CMap.Parse). PdfACMaps.Encoding
    /// with writing mode 1 writes „/WMode 1 def“.
    /// </summary>
    [Fact]
    public void Cid04_WModeDef_OnlyTheLiteralValueCounts()
    {
        Assert.Equal(1, Cid03Parse("/WMode 1 def").WMode);
        Assert.Equal(0, Cid03Parse("/WMode 2 1 sub def").WMode);
        Assert.Equal(0, Cid03Parse("1 /WMode exch def").WMode);
        var text = Encoding.ASCII.GetString(PdfACMaps.Encoding("Probe-V", "Adobe", "Identity", 0, 1, [(0x41, 1, 5)]).RawData!);
        Assert.Contains("/WMode 1 def\n", text, StringComparison.Ordinal);
        Assert.Equal(1, CMap.Parse(Encoding.ASCII.GetBytes(text)).WMode);
    }

    /// <summary>
    /// CID B.3 sentences 24–25: every begin has its end. In the programs of all writers (PdfACMaps.Encoding, PdfACMaps.ToUnicode,
    /// ElementFace.ToUnicode, FontScrub.ToUnicodeOf, the encoding CMap of a PDF/A copy) the tokens begin and end are as many, and no end comes
    /// before its begin.
    /// </summary>
    [Fact]
    public void Cid04_WrittenCMaps_PairEveryBeginWithAnEnd()
    {
        foreach (var (writer, text) in Cid02Writers())
        {
            var depth = 0;
            foreach (var token in text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "begin") depth++;
                else if (token == "end") depth--;
                Assert.True(depth >= 0, $"{writer}: end vor seinem begin");
            }
            Assert.True(depth == 0, $"{writer}: {depth} begin ohne end");
        }
    }

    /// <summary>
    /// CID B.4 sentence 6: a range over several bytes. Codespace 8140–84FC with cidrange 8140–82FC → 100: today CMap.ToCid counts the code as a
    /// number from the start of the range — 8240 gives 356 (100 + 0x100); the counting in several dimensions belongs to section 5 (package cid-02).
    /// </summary>
    [Fact]
    public void Cid04_MultiByteRange_CountsTheCodeAsANumber()
    {
        var cmap = Cid03Parse("1 begincodespacerange <8140> <84FC> endcodespacerange 1 begincidrange <8140> <82FC> 100 endcidrange");
        Assert.Equal(356, cmap.ToCid(0x8240, 2));
    }
}
