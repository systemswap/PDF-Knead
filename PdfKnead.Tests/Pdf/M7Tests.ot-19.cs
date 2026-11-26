using System.Buffers.Binary;
using System.Globalization;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-19 (OpenType: gasp — grid-fitting and scan-conversion procedure, GDEF — glyph definition): the tests
/// the sentence-by-sentence drafts named as missing. gasp is not read (PDF-Knead renders without hinting, always anti-aliased); GDEF gives
/// glyph classes, mark attachment classes and mark glyph sets to the shaper, while AttachList, LigCaretList and the ItemVariationStore are not
/// read (carets inside ligatures are proportional, user question 15 is open: these tests hold today's behaviour). The variation tables come
/// from the helpers of package ot-18.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-19)

    /// <summary>Big-endian bytes with labels; 16- and 32-bit offsets are filled in once every label is placed.</summary>
    internal sealed class Ot19Bytes
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int At, string Target, int From, bool Wide)> _offsets = [];
        private int _next;

        public int Here => _bytes.Count;

        public string NewLabel() => "#" + _next++;

        public Ot19Bytes U16(params int[] values)
        {
            foreach (var v in values)
            {
                _bytes.Add((byte)(v >> 8));
                _bytes.Add((byte)v);
            }
            return this;
        }

        public Ot19Bytes U32(long v) => U16((int)(v >> 16) & 0xFFFF, (int)v & 0xFFFF);

        public Ot19Bytes Tag(string tag)
        {
            foreach (var c in tag) _bytes.Add((byte)c);
            return this;
        }

        public Ot19Bytes Bytes(byte[] data)
        {
            _bytes.AddRange(data);
            return this;
        }

        public Ot19Bytes PadTo(int position)
        {
            Assert.True(Here <= position);
            while (Here < position) _bytes.Add(0);
            return this;
        }

        public Ot19Bytes Label(string name)
        {
            _labels[name] = Here;
            return this;
        }

        public Ot19Bytes Off16(string target, int from)
        {
            _offsets.Add((Here, target, from, false));
            return U16(0);
        }

        public Ot19Bytes Off32(string target, int from)
        {
            _offsets.Add((Here, target, from, true));
            return U32(0);
        }

        public byte[] ToArray()
        {
            var bytes = _bytes.ToArray();
            foreach (var (at, target, from, wide) in _offsets)
            {
                var value = _labels[target] - from;
                Assert.InRange(value, 1, wide ? int.MaxValue : 0xFFFF);
                if (wide) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)value);
                else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), (ushort)value);
            }
            return bytes;
        }
    }

    private static int Ot19G(char c) => Ot18SansFont.GlyphForUnicode(c);

    private static void Ot19Coverage(Ot19Bytes b, IEnumerable<int> glyphs)
    {
        var sorted = glyphs.Distinct().Order().ToArray();
        b.U16(1, sorted.Length);
        foreach (var g in sorted) b.U16(g);
    }

    /// <summary>ClassDef format 1: classes of the glyphs from <paramref name="start"/> on.</summary>
    private static Action<Ot19Bytes> Ot19ClassDef1(int start, params int[] classes) => b =>
    {
        b.U16(1, start, classes.Length);
        foreach (var c in classes) b.U16(c);
    };

    /// <summary>ClassDef format 2: the ranges in the order given.</summary>
    private static Action<Ot19Bytes> Ot19ClassDef2(params (int First, int Last, int Class)[] ranges) => b =>
    {
        b.U16(2, ranges.Length);
        foreach (var r in ranges) b.U16(r.First, r.Last, r.Class);
    };

    /// <summary>ClassDef format 2 with one range per glyph (sorted).</summary>
    private static Action<Ot19Bytes> Ot19Classes(params (int Glyph, int Class)[] classes) =>
        Ot19ClassDef2([.. classes.OrderBy(c => c.Glyph).Select(c => (c.Glyph, c.Glyph, c.Class))]);

    /// <summary>MarkGlyphSets: the format given, one Coverage per set (32-bit offsets).</summary>
    private static Action<Ot19Bytes> Ot19MarkSets(int format, params int[][] sets) => b =>
    {
        var at = b.Here;
        var labels = sets.Select(_ => b.NewLabel()).ToArray();
        b.U16(format, sets.Length);
        foreach (var l in labels) b.Off32(l, at);
        for (var i = 0; i < sets.Length; i++)
        {
            b.Label(labels[i]);
            Ot19Coverage(b, sets[i]);
        }
    };

    private static Action<Ot19Bytes> Ot19Raw(params int[] words) => b => b.U16(words);

    /// <summary>
    /// A GDEF: header of the version given (markGlyphSetsDefOffset from minor version 2, itemVarStoreOffset from 3), then the parts given,
    /// from <paramref name="bodyAt"/> on when it is set; <paramref name="rawStore"/> writes that value as itemVarStoreOffset.
    /// </summary>
    private static byte[] Ot19Gdef(int major = 1, int minor = 0, Action<Ot19Bytes>? glyphClasses = null, Action<Ot19Bytes>? attachList = null,
        Action<Ot19Bytes>? ligCarets = null, Action<Ot19Bytes>? markClasses = null, Action<Ot19Bytes>? markSets = null,
        Action<Ot19Bytes>? varStore = null, int bodyAt = 0, long rawStore = -1)
    {
        var b = new Ot19Bytes();
        var parts = new (Action<Ot19Bytes>? Part, string Label, bool Wide)[]
        {
            (glyphClasses, b.NewLabel(), false), (attachList, b.NewLabel(), false), (ligCarets, b.NewLabel(), false), (markClasses, b.NewLabel(), false),
            (minor >= 2 ? markSets : null, b.NewLabel(), false), (minor >= 3 ? varStore : null, b.NewLabel(), true),
        };
        b.U16(major, minor);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i == 4 && minor < 2 || i == 5 && minor < 3) continue;
            if (i == 5 && rawStore >= 0) b.U32(rawStore);
            else if (parts[i].Part is null) _ = parts[i].Wide ? b.U32(0) : b.U16(0);
            else _ = parts[i].Wide ? b.Off32(parts[i].Label, 0) : b.Off16(parts[i].Label, 0);
        }
        if (bodyAt > 0) b.PadTo(bodyAt);
        foreach (var (part, label, _) in parts)
            if (part is not null)
            {
                b.Label(label);
                part(b);
            }
        return b.ToArray();
    }

    /// <summary>A GSUB or GPOS: script „latn“ whose default language system names one feature with every lookup given.</summary>
    private static byte[] Ot19Layout(string feature, params Action<Ot19Bytes>[] lookups)
    {
        var b = new Ot19Bytes();
        string scripts = b.NewLabel(), features = b.NewLabel(), list = b.NewLabel();
        b.U16(1, 0).Off16(scripts, 0).Off16(features, 0).Off16(list, 0);
        b.Label(scripts).U16(1).Tag("latn").U16(8).U16(4, 0).U16(0, 0xFFFF, 1, 0);
        b.Label(features).U16(1).Tag(feature).U16(8).U16(0, lookups.Length);
        for (var i = 0; i < lookups.Length; i++) b.U16(i);
        b.Label(list);
        var at = b.Here;
        var labels = lookups.Select(_ => b.NewLabel()).ToArray();
        b.U16(lookups.Length);
        foreach (var l in labels) b.Off16(l, at);
        for (var i = 0; i < lookups.Length; i++)
        {
            b.Label(labels[i]);
            lookups[i](b);
        }
        return b.ToArray();
    }

    /// <summary>A lookup with its subtables right after it; markFilteringSet only when not negative.</summary>
    private static Action<Ot19Bytes> Ot19Lookup(int type, int flag, int markSet, params Action<Ot19Bytes>[] subtables) => b =>
    {
        var start = b.Here;
        var labels = subtables.Select(_ => b.NewLabel()).ToArray();
        b.U16(type, flag, subtables.Length);
        foreach (var l in labels) b.Off16(l, start);
        if (markSet >= 0) b.U16(markSet);
        for (var i = 0; i < subtables.Length; i++)
        {
            b.Label(labels[i]);
            subtables[i](b);
        }
    };

    /// <summary>Ligature substitution format 1 with one ligature.</summary>
    private static Action<Ot19Bytes> Ot19Ligature(int[] components, int ligature) => b =>
    {
        var start = b.Here;
        string coverage = b.NewLabel(), set = b.NewLabel(), lig = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(1).Off16(set, start);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1).Off16(lig, setAt);
        b.Label(lig).U16(ligature, components.Length);
        foreach (var c in components.Skip(1)) b.U16(c);
        b.Label(coverage);
        Ot19Coverage(b, [components[0]]);
    };

    /// <summary>Mark-to-base attachment format 1, one mark class; the base anchor in format 1, or format 2 with contour point 18.</summary>
    private static Action<Ot19Bytes> Ot19MarkToBase(int[] bases, int[] marks, (int X, int Y) baseAnchor, (int X, int Y) markAnchor, int baseAnchorFormat = 1) => b =>
    {
        var start = b.Here;
        string markCoverage = b.NewLabel(), baseCoverage = b.NewLabel(), markArray = b.NewLabel(), baseArray = b.NewLabel();
        b.U16(1).Off16(markCoverage, start).Off16(baseCoverage, start).U16(1).Off16(markArray, start).Off16(baseArray, start);
        b.Label(markArray);
        var at = b.Here;
        var anchor = b.NewLabel();
        b.U16(marks.Length);
        foreach (var _ in marks) b.U16(0).Off16(anchor, at);
        b.Label(anchor).U16(1, markAnchor.X, markAnchor.Y);
        b.Label(baseArray);
        at = b.Here;
        var baseAnchorLabel = b.NewLabel();
        b.U16(bases.Length);
        foreach (var _ in bases) b.Off16(baseAnchorLabel, at);
        b.Label(baseAnchorLabel).U16(baseAnchorFormat, baseAnchor.X, baseAnchor.Y);
        if (baseAnchorFormat == 2) b.U16(18);
        b.Label(markCoverage);
        Ot19Coverage(b, marks);
        b.Label(baseCoverage);
        Ot19Coverage(b, bases);
    };

    /// <summary>Pair positioning format 1, one pair: value format 1 with XAdvance (0x0004), and with 0x0080 an XAdvDevice offset to <paramref name="device"/>.</summary>
    private static Action<Ot19Bytes> Ot19Pair(int first, int second, int valueFormat, int advance, Action<Ot19Bytes>? device = null) => b =>
    {
        var start = b.Here;
        string coverage = b.NewLabel(), set = b.NewLabel(), deviceLabel = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(valueFormat, 0, 1).Off16(set, start);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1, second);
        if ((valueFormat & 0x4) != 0) b.U16(advance);
        if ((valueFormat & 0x80) != 0) b.Off16(deviceLabel, setAt);
        if (device is not null)
        {
            b.Label(deviceLabel);
            device(b);
        }
        b.Label(coverage);
        Ot19Coverage(b, [first]);
    };

    private static readonly Action<Ot19Bytes> Ot19VariationIndex = b => b.U16(0, 0, 0x8000);

    /// <summary>A Device table for 12–17 ppem, delta format 2 (the values of the caret example).</summary>
    private static readonly Action<Ot19Bytes> Ot19Device = b => b.U16(12, 17, 2, 0x1111, 0x2200);

    /// <summary>A shaper for Liberation Sans with the GSUB, GPOS and GDEF given (and further tables), without the kern table.</summary>
    private static TextShaper Ot19Shaper(byte[]? gsub, byte[]? gpos, byte[]? gdef, params (string Tag, byte[]? Data)[] extra)
    {
        var tables = new Dictionary<string, byte[]?> { ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = gdef, ["kern"] = null };
        foreach (var (tag, data) in extra) tables[tag] = data;
        return TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot18Sans, tables)));
    }

    private static (int Glyph, double XAdvance, double XOffset, double YOffset, int AttachBack)[] Ot19Result(ShapedGlyph[] glyphs) =>
        [.. glyphs.Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset, g.AttachBack))];

    // ---- rendering

    /// <summary>A simple TrueType font (WinAnsiEncoding) with the font file given.</summary>
    private static PdfDictionary Ot19TrueType(TestPdf pdf, byte[] file)
    {
        var font = OpenTypeFont.Parse(file);
        var widths = new PdfArray(Enumerable.Range(32, 95).Select(c => (PdfObject)PdfInteger.Of((int)Math.Round(font.AdvanceWidth(font.GlyphForUnicode(c)) * 1000 / font.UnitsPerEm))));
        var stream = pdf.Add(TestPdf.FlateStream(file));
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Probe")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)),
            ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(stream)));
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+Probe")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(126)), ("Widths", widths), ("Encoding", TestPdf.N("WinAnsiEncoding")),
            ("FontDescriptor", descriptor));
    }

    /// <summary>„Hamburg 0123“ in the font given at <paramref name="size"/> pt, rendered at <paramref name="scale"/> pixels per point.</summary>
    private static byte[] Ot19Render(byte[] file, double size, float scale)
    {
        var y = size > 100 ? 30 : 100;
        var content = string.Create(CultureInfo.InvariantCulture, $"BT /F1 {size} Tf 4 {y} Td (Hamburg 0123) Tj ET");
        var (doc, list) = InterpreterTests.Page(content, pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F1", Ot19TrueType(pdf, file))))));
        using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], scale, new ImageCache());
        var bytes = bitmap.Bytes;
        Assert.True(bytes.AsSpan().IndexOfAnyExcept(bytes[0]) >= 0, "nichts gezeichnet");
        return bytes;
    }

    /// <summary>Liberation Sans with the gasp table given as hex (empty: removed, "orig": its own).</summary>
    private static byte[] Ot19WithGasp(string hex) => hex == "orig" ? Ot18Sans : Ot18With(Ot18Sans, ("gasp", hex.Length == 0 ? null : Convert.FromHexString(hex)));

    /// <summary>The gasp table of the example (OT 17.3).</summary>
    private const string Ot19ExampleGasp = "00010004" + "0008000A" + "00100005" + "00130007" + "FFFF000F";

    // ------------------------------------------------------------ OT 17: gasp

    /// <summary>
    /// OT 17 sentences 1–6, OT 17.1 sentences 8–11, 12–23 and 24–32, OT 17.3 sentences 1–15: gasp is not evaluated (no hinting, always
    /// grey anti-aliasing) — the same pixels with Liberation Sans' own gasp (version 0: up to 8 ppem grey only, up to 17 grid fitting only, beyond
    /// both) and without it at 6 and 72 ppem; with the ranges 8/0x0002, 17/0x0001, 0xFFFF/0x0003 and with 0xFFFF/0x0000 at 8, 12 and 40 ppem;
    /// with 0xFFFF/0xFFFF (all bits, reserved ones too) and 0xFFFF/0x0000; with 0xFFFF/0x0002 and 0xFFFF/0x0003 at 6, 12 and 2 100 ppem; with the
    /// table of the example and the original at 8, 16, 19 and 20 ppem.
    /// </summary>
    [Theory]
    [InlineData("orig", "", 6, 1)]
    [InlineData("orig", "", 72, 1)]
    [InlineData("000100030008000200110001FFFF0003", "00010001FFFF0000", 8, 1)]
    [InlineData("000100030008000200110001FFFF0003", "00010001FFFF0000", 12, 1)]
    [InlineData("000100030008000200110001FFFF0003", "00010001FFFF0000", 40, 1)]
    [InlineData("00010001FFFFFFFF", "00010001FFFF0000", 12, 1)]
    [InlineData("00010001FFFF0002", "00010001FFFF0003", 6, 1)]
    [InlineData("00010001FFFF0002", "00010001FFFF0003", 12, 1)]
    [InlineData("00010001FFFF0002", "00010001FFFF0003", 150, 14)]
    [InlineData(Ot19ExampleGasp, "orig", 8, 1)]
    [InlineData(Ot19ExampleGasp, "orig", 16, 1)]
    [InlineData(Ot19ExampleGasp, "orig", 19, 1)]
    [InlineData(Ot19ExampleGasp, "orig", 20, 1)]
    public void Ot19_Gasp_DoesNotChangeTheRendering(string gasp, string other, double size, float scale)
    {
        Assert.Equal(Ot19Render(Ot19WithGasp(other), size, scale), Ot19Render(Ot19WithGasp(gasp), size, scale));
    }

    /// <summary>
    /// OT 17 sentences 7–8 (writer): PDF-Knead never creates a gasp table — a subset of Liberation Sans has none; a subset keeping all tables and a
    /// scrubbed font carry the original byte for byte (version 0 stays 0).
    /// </summary>
    [Fact]
    public void Ot19_Gasp_SubsetDropsIt_KeepAllAndScrubKeepItUnchanged()
    {
        var a = Ot19G('A');
        var gasp = Ot18SansFont.TableData("gasp").ToArray();
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(gasp));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot18Sans, [0, a])).HasTable("gasp"));
        Assert.Equal(gasp, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot18Sans, [0, a], keepAllTables: true)).TableData("gasp").ToArray());
        Assert.Equal(gasp, OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot18Sans, [0, a])).TableData("gasp").ToArray());
    }

    /// <summary>
    /// OT 17.1 sentences 1–7, 8–11 and 33–35, OT 17.3 sentences 1–15 (scrub part): gasp is neither read nor checked — numRanges 0xFFFF beyond the
    /// end of the table, the ranges 8/2, 17/1, 0xFFFF/3, ranges in falling order without the 0xFFFF end mark, the table of the example: each
    /// font parses without error with the outlines and advances of Liberation Sans, and the scrubbed font carries exactly these bytes.
    /// </summary>
    [Theory]
    [InlineData("0001FFFF00080002")]
    [InlineData("000100030008000200110001FFFF0003")]
    [InlineData("0001000200110001" + "00080002")]
    [InlineData(Ot19ExampleGasp)]
    public void Ot19_GaspIsNotRead_ParsedAsWithoutIt_ScrubKeepsTheBytes(string hex)
    {
        var gasp = Convert.FromHexString(hex);
        var file = Ot18With(Ot18Sans, ("gasp", gasp));
        var font = OpenTypeFont.Parse(file);
        Assert.False(font.Broken);
        Ot18SameGlyphsAsLiberation(font);
        Assert.Equal(gasp, OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [0, Ot19G('A')])).TableData("gasp").ToArray());
    }

    /// <summary>
    /// OT 17.2 sentences 1–5: a variable TrueType font with fvar, an MVAR that varies the gasp range limit (tag „gsp0“) and gasp — drawn in its
    /// default instance (the outlines of glyf); the scrubbed font carries MVAR and gasp byte for byte (today's behaviour, user question 2).
    /// </summary>
    [Fact]
    public void Ot19_VariableFontWithMvarAndGasp_DefaultInstance_ScrubKeepsBoth()
    {
        var store = Ot18Ivs(1, 1, _ => 4);
        var mvar = new Ot18Writer().U16(1, 0, 0, 8, 1, 12 + 8).Tag("gsp0").U16(0, 0).Bytes(store).ToArray();
        var gasp = Convert.FromHexString("000100020008000AFFFF000F");
        var file = Ot18With(Ot18Sans, ("fvar", Ot18WeightFvar()), ("MVAR", mvar), ("gasp", gasp), ("gvar", Ot18Gvar(Ot18SansFont, 1, Ot19G('A'), 80)));
        var font = OpenTypeFont.Parse(file);
        Ot18SameGlyphsAsLiberation(font);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [0, Ot19G('A')]));
        Assert.Equal(mvar, scrubbed.TableData("MVAR").ToArray());
        Assert.Equal(gasp, scrubbed.TableData("gasp").ToArray());
    }

    // ------------------------------------------------------------ OT 18.1: GDEF overview

    /// <summary>
    /// OT 18.1 sentences 1–3 and 6–7: GDEF 1.2 with GlyphClassDef (glyphs 5 and 6 class 3), MarkAttachClassDef (5 → 1, 6 → 2) and MarkGlyphSets
    /// with one set {6} — glyph 5 is of class 3, glyph 6 of mark attachment class 2, glyph 6 is in set 0 and glyph 5 is not.
    /// </summary>
    [Fact]
    public void Ot19_Gdef12_GlyphClassesMarkAttachClassesAndMarkSets()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(minor: 2, glyphClasses: Ot19Classes((5, 3), (6, 3)), markClasses: Ot19Classes((5, 1), (6, 2)),
            markSets: Ot19MarkSets(1, [6])));
        Assert.Equal(3, gdef.GlyphClass(5));
        Assert.Equal(3, gdef.GlyphClass(6));
        Assert.Equal(1, gdef.MarkAttachClass(5));
        Assert.Equal(2, gdef.MarkAttachClass(6));
        Assert.True(gdef.InMarkSet(0, 6));
        Assert.False(gdef.InMarkSet(0, 5));
    }

    /// <summary>The GPOS of <see cref="Ot19_GdefPartsNotRead_ShapingUnchanged"/>: pair A–V −80 (XAdvance with a VariationIndex), acute on „a“.</summary>
    private static byte[] Ot19KernAndMarkGpos() => Ot19Layout("kern",
        Ot19Lookup(2, 0, -1, Ot19Pair(Ot19G('A'), Ot19G('V'), 0x0084, -80, Ot19VariationIndex)),
        Ot19Lookup(4, 0, -1, Ot19MarkToBase([Ot19G('a')], [Ot19G('́')], (520, 1100), (-60, 1000))));

    private static Action<Ot19Bytes> Ot19BaseClasses() =>
        Ot19Classes((Ot19G('A'), 1), (Ot19G('V'), 1), (Ot19G('a'), 1), (Ot19G('́'), 3));

    /// <summary>
    /// OT 18.1 sentences 4–5, 8, 11–12, 23–26, 48–58; OT 18.2.1 sentence 33; OT 18.2.3 sentences 1–8; OT 18.2.4 sentences 18–22; OT 18.2.7
    /// sentences 1–8; OT 18.3.3 sentences 1–20; OT 18.3.4 sentences 5–22 and 23–31; OT 18.3.5 sentences 1–6; OT 18.3.6 sentences 1–17: AttachList, LigCaretList and the ItemVariationStore of GDEF are not read — GDEF 1.3 with all three, an AttachList with point
    /// 18 for „a“, a variable font (fvar) whose GDEF 1.3 has a store and whose GPOS pair adjustment a VariationIndex, an itemVarStoreOffset
    /// beyond the end of the table, the AttachList of the example at offset 6, an AttachList whose glyphCount 0xFFFF runs past the end, the
    /// LigCaretList of example 4 at offset 8, a LigCaretList with caret values of formats 1, 2 and 3 (with a Device table): „AVá“ is
    /// shaped exactly as with a GDEF that has only the glyph classes (pair −80 in the default instance, the acute attached to „a“).
    /// </summary>
    [Theory]
    [InlineData("version13")]
    [InlineData("attachPoint18")]
    [InlineData("variableStore")]
    [InlineData("storeBeyondEnd")]
    [InlineData("example3AttachList")]
    [InlineData("attachCountBeyondEnd")]
    [InlineData("example4LigCaretList")]
    [InlineData("caretFormats123")]
    public void Ot19_GdefPartsNotRead_ShapingUnchanged(string variant)
    {
        var a = Ot19G('a');
        var attach18 = (Action<Ot19Bytes>)(b =>
        {
            var at = b.Here;
            string coverage = b.NewLabel(), point = b.NewLabel();
            b.Off16(coverage, at).U16(1).Off16(point, at);
            b.Label(point).U16(1, 18);
            b.Label(coverage);
            Ot19Coverage(b, [a]);
        });
        var carets = (Action<Ot19Bytes>)(b =>
        {
            var at = b.Here;
            string coverage = b.NewLabel(), glyph = b.NewLabel();
            b.Off16(coverage, at).U16(1).Off16(glyph, at);
            b.Label(glyph).U16(1, 4).U16(1, 603);
            b.Label(coverage);
            Ot19Coverage(b, [Ot19G('ﬁ')]);
        });
        var store = Ot18Ivs(1, 1, _ => 25);
        var gdef = variant switch
        {
            "version13" => Ot19Gdef(minor: 3, glyphClasses: Ot19BaseClasses(), attachList: attach18, ligCarets: carets, varStore: b => b.Bytes(store)),
            "attachPoint18" => Ot19Gdef(glyphClasses: Ot19BaseClasses(), attachList: attach18),
            "variableStore" => Ot19Gdef(minor: 3, glyphClasses: Ot19BaseClasses(), varStore: b => b.Bytes(store)),
            "storeBeyondEnd" => Ot19Gdef(minor: 3, glyphClasses: Ot19BaseClasses(), rawStore: 0x7FFFFFF0),
            "example3AttachList" => Ot19Gdef(glyphClasses: Ot19BaseClasses(), attachList: Ot19Raw(0x0012, 0x0002, 0x0008, 0x000C, 0x0001, 0x0012,
                0x0002, 0x000E, 0x0017, 0x0001, 0x0002, 0x001C, 0x0020)),
            "example4LigCaretList" => Ot19Gdef(glyphClasses: Ot19BaseClasses(), ligCarets: Ot19Raw(0x0008, 0x0002, 0x0010, 0x0014, 0x0001, 0x0002,
                0x009F, 0x00A5, 0x0001, 0x000E, 0x0002, 0x0006, 0x000E, 0x0001, 0x025B, 0x0001, 0x025B, 0x0001, 0x04B6)),
            // one ligature with three carets: format 1 (603), format 2 (contour point 13), format 3 (1206 with a Device table)
            "caretFormats123" => Ot19Gdef(glyphClasses: Ot19BaseClasses(), ligCarets: Ot19Raw(0x0026, 0x0001, 0x0006, 0x0003, 0x0008, 0x000C, 0x0010,
                0x0001, 0x025B, 0x0002, 0x000D, 0x0003, 0x04B6, 0x0006, 0x000C, 0x0011, 0x0002, 0x1111, 0x2200, 0x0001, 0x0001, Ot19G('ﬃ'))),
            _ => Ot19Gdef(glyphClasses: Ot19BaseClasses(), attachList: Ot19Raw(0x0004, 0xFFFF, 0x0001, 0x0001, a)),
        };
        if (variant == "example3AttachList") Assert.Equal(0x0012, BinaryPrimitives.ReadUInt16BigEndian(gdef.AsSpan(BinaryPrimitives.ReadUInt16BigEndian(gdef.AsSpan(6)))));
        if (variant is "example4LigCaretList" or "caretFormats123")
        {
            // the LigCaretList is well formed: per covered ligature its carets, each of format 1, 2 or 3
            var t = new TableData(gdef);
            var list = t.U16(8);
            var formats = new List<int>();
            for (var k = 0; k < t.U16(list + 2); k++)
            {
                var ligGlyph = list + t.U16(list + 4 + 2 * k);
                for (var c = 0; c < t.U16(ligGlyph); c++) formats.Add(t.U16(ligGlyph + t.U16(ligGlyph + 2 + 2 * c)));
            }
            Assert.Equal(variant == "caretFormats123" ? [1, 2, 3] : new[] { 1, 1, 1 }, formats);
            Assert.Equal(variant == "caretFormats123" ? 0 : 1, t.Coverage(list + t.U16(list), variant == "caretFormats123" ? Ot19G('ﬃ') : 0xA5));
        }
        var fvar = variant is "variableStore" or "version13" ? Ot18WeightFvar() : null;
        var expected = Ot19Result(Ot19Shaper(null, Ot19KernAndMarkGpos(), Ot19Gdef(glyphClasses: Ot19BaseClasses())).Shape("AVá"));
        var actual = Ot19Result(Ot19Shaper(null, Ot19KernAndMarkGpos(), gdef, ("fvar", fvar)).Shape("AVá"));
        Assert.Equal(Ot18SansFont.AdvanceWidth(Ot19G('A')) - 80, expected[0].XAdvance);
        Assert.Equal(1, expected[3].AttachBack);
        Assert.Equal(0, expected[3].XAdvance);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// OT 18.1 sentences 9–10 and 13: a ligature f + i in a lookup with LookupFlag 0x0008 (ignore marks) and GDEF class 3 for the acute: „f́i“
    /// becomes the ligature (the mark skipped and kept); without GDEF, or with LookupFlag 0, „f́i“ stays three glyphs.
    /// </summary>
    [Fact]
    public void Ot19_IgnoreMarksFlag_UsesTheGdefClasses()
    {
        int f = Ot19G('f'), i = Ot19G('i'), fi = Ot19G('ﬁ'), acute = Ot19G('́');
        byte[] Gsub(int flag) => Ot19Layout("liga", Ot19Lookup(4, flag, -1, Ot19Ligature([f, i], fi)));
        var gdef = Ot19Gdef(glyphClasses: Ot19Classes((acute, 3)));
        int[] Shape(byte[] gsub, byte[]? classes) => [.. Ot19Shaper(gsub, null, classes).Shape("f́i").Select(g => g.Glyph)];
        Assert.Equal([fi, acute], Shape(Gsub(0x0008), gdef));
        Assert.Equal([f, acute, i], Shape(Gsub(0x0008), null));
        Assert.Equal([f, acute, i], Shape(Gsub(0), gdef));
    }

    /// <summary>
    /// OT 18.1 sentences 16–21: GDEF with the base „b“ (class 1) and the spacing grave and circumflex as marks (class 3), GPOS mark-to-base on „b“
    /// — in „b`^“ both marks attach to „b“ (one and two glyphs back) and advance 0, although their hmtx advance is not 0; a lookup with
    /// LookupFlag 0x0004 skips a ligature of class 2.
    /// </summary>
    [Fact]
    public void Ot19_MarksAttachToTheBaseBeforeThem_WithoutAdvance()
    {
        int b = Ot19G('b'), m1 = Ot19G('`'), m2 = Ot19G('^'), fi = Ot19G('ﬁ');
        Assert.True(Ot18SansFont.AdvanceWidth(m1) > 0);
        Assert.True(Ot18SansFont.AdvanceWidth(m2) > 0);
        var gdef = Ot19Gdef(glyphClasses: Ot19Classes((b, 1), (m1, 3), (m2, 3), (fi, 2)));
        var gpos = Ot19Layout("mark", Ot19Lookup(4, 0, -1, Ot19MarkToBase([b], [m1, m2], (500, 1200), (200, 1000))));
        var glyphs = Ot19Shaper(null, gpos, gdef).Shape("b`^");
        Assert.Equal([b, m1, m2], glyphs.Select(g => g.Glyph));
        Assert.Equal([0, 1, 2], glyphs.Select(g => g.AttachBack));
        Assert.Equal(0, glyphs[1].XAdvance);
        Assert.Equal(0, glyphs[2].XAdvance);
        Assert.Equal(300 - Ot18SansFont.AdvanceWidth(b), glyphs[1].XOffset);
        Assert.Equal(glyphs[1].XOffset, glyphs[2].XOffset);
        var classes = new GlyphDefinitions(gdef);
        Assert.True(classes.Skips(new Lookup(1, 0x0004, [], -1), fi));
        Assert.False(classes.Skips(new Lookup(1, 0x0004, [], -1), b));
    }

    /// <summary>
    /// OT 18.1 sentence 22 (today's behaviour, user question 15): without GDEF no classes are derived — with a GPOS mark-to-base for „a“ and the
    /// marks U+0301 and U+0323, in „ạ́“ only U+0301 attaches (U+0323 would need the base before U+0301), both marks keep their hmtx advance, and
    /// a ligature lookup with LookupFlag 0x0008 skips no mark.
    /// </summary>
    [Fact]
    public void Ot19_WithoutGdef_NoClassesAreDerived()
    {
        int a = Ot19G('a'), acute = Ot19G('́'), dot = Ot19G('̣');
        Assert.NotEqual(0, dot);
        var gpos = Ot19Layout("mark", Ot19Lookup(4, 0, -1, Ot19MarkToBase([a], [acute, dot], (500, 1100), (0, 1000))));
        var glyphs = Ot19Shaper(null, gpos, null).Shape("ạ́");
        Assert.Equal([0, 1, 0], glyphs.Select(g => g.AttachBack));
        Assert.Equal(Ot18SansFont.AdvanceWidth(acute), glyphs[1].XAdvance);
        Assert.Equal(Ot18SansFont.AdvanceWidth(dot), glyphs[2].XAdvance);
        var gsub = Ot19Layout("liga", Ot19Lookup(4, 0x0008, -1, Ot19Ligature([Ot19G('f'), Ot19G('i')], Ot19G('ﬁ'))));
        Assert.Equal(3, Ot19Shaper(gsub, null, null).Shape("f́i").Length);
    }

    /// <summary>
    /// OT 18.1 sentences 41–44: marks m1 (class 3, mark attachment class 1) and m2 (class 3, class 2) — a lookup with LookupFlag 0x0100 (only
    /// marks of class 1) skips m2, not m1; with LookupFlag 0 neither.
    /// </summary>
    [Fact]
    public void Ot19_MarkAttachmentTypeFlag_SkipsMarksOfOtherClasses()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19Classes((40, 3), (41, 3)), markClasses: Ot19Classes((40, 1), (41, 2))));
        Assert.False(gdef.Skips(new Lookup(1, 0x0100, [], -1), 40));
        Assert.True(gdef.Skips(new Lookup(1, 0x0100, [], -1), 41));
        Assert.False(gdef.Skips(new Lookup(1, 0, [], -1), 40));
        Assert.False(gdef.Skips(new Lookup(1, 0, [], -1), 41));
    }

    /// <summary>
    /// OT 18.1 sentences 45–47: GDEF 1.2 with mark glyph set 0 = {m1}; a lookup with LookupFlag 0x0010 and mark filtering set 0 (read from the
    /// lookup table) skips m2, not m1.
    /// </summary>
    [Fact]
    public void Ot19_MarkFilteringSet_SkipsMarksOutsideTheSet()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(minor: 2, glyphClasses: Ot19Classes((40, 3), (41, 3)), markSets: Ot19MarkSets(1, [40])));
        var gsub = Ot19Layout("liga", Ot19Lookup(4, 0x0010, 0, Ot19Ligature([1, 2], 3)));
        var lookup = new LayoutTable(gsub, 7).Lookup(0);
        Assert.Equal(0, lookup.MarkFilteringSet);
        Assert.False(gdef.Skips(lookup, 40));
        Assert.True(gdef.Skips(lookup, 41));
    }

    /// <summary>
    /// OT 18.1 sentences 59–62: Device and VariationIndex tables of a ValueRecord are passed over — pair positioning with value format 0x0084
    /// (XAdvance −70 with an XAdvDevice offset) changes the advance of „A“ by −70 only, whether the offset names a VariationIndex (format 0x8000) or
    /// a Device table for 12–17 ppem (format 2).
    /// </summary>
    [Fact]
    public void Ot19_DeviceAndVariationIndexInValueRecords_PassedOver()
    {
        int a = Ot19G('A'), v = Ot19G('V');
        foreach (var device in (Action<Ot19Bytes>[])[Ot19VariationIndex, Ot19Device])
        {
            var gpos = Ot19Layout("kern", Ot19Lookup(2, 0, -1, Ot19Pair(a, v, 0x0084, -70, device)));
            var glyphs = Ot19Shaper(null, gpos, null).Shape("AV");
            Assert.Equal(Ot18SansFont.AdvanceWidth(a) - 70, glyphs[0].XAdvance);
            Assert.Equal(Ot18SansFont.AdvanceWidth(v), glyphs[1].XAdvance);
        }
    }

    // ------------------------------------------------------------ carets in ligatures (OT 18.1, 18.2.4, 18.3.4–18.3.6)

    /// <summary>A line with one ligature (<paramref name="characters"/> characters, advance 9 pt at x 10) and „x“ after it.</summary>
    private static LaidLine Ot19LigatureLine(int characters) => new()
    {
        Start = 0,
        End = characters + 1,
        EndsParagraph = true,
        Ascent = 9,
        Descent = -2,
        Size = 10,
        X = 10,
        Width = 14,
        Glyphs =
        [
            new LaidGlyph(Ot19G(characters == 2 ? 'ﬁ' : 'ﬃ'), 10, 9, null, false, false, 0, 0, characters, characters == 2 ? "fi" : "ffi", 0, 0),
            new LaidGlyph(Ot19G('x'), 19, 5, null, false, false, 0, characters, 1, "x", 0, 0),
        ],
    };

    /// <summary>
    /// OT 18.1 sentences 33–35 and 36–40, OT 18.2.4 sentences 10–17, 18–22, 23–29, 30–38 and 39–52, OT 18.3.4 sentences 1–4, 5–22 and 23–31,
    /// OT 18.3.5 sentences 1–6, OT 18.3.6 sentences 1–17 (today's behaviour, user question 15): the LigCaretList is not read — a caret inside a
    /// ligature lies at the share of its characters: in „fi“ (advance 9 pt from x 10) at the middle (14.5), in „ffi“ at one and two thirds
    /// (13, 16), whatever caret values (formats 1, 2, 3) the font holds; the edges at the start of the ligature and of the next glyph.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Ot19_CaretInsideALigature_IsProportional(int characters)
    {
        var line = Ot19LigatureLine(characters);
        Assert.Equal(10, RichLayout.XAt(line, 0), 6);
        for (var k = 1; k < characters; k++) Assert.Equal(10 + 9.0 * k / characters, RichLayout.XAt(line, k), 6);
        Assert.Equal(19, RichLayout.XAt(line, characters), 6);
        Assert.Equal(24, RichLayout.XAt(line, characters + 1), 6);
    }

    /// <summary>
    /// OT 18.1 sentences 27–32, OT 18.2.4 sentences 1–9 (today's behaviour, user question 15): Noto Sans Arabic has a LigCaretList with carets for
    /// the ligature glyph of U+FDF2 (three carets in format 1) — formatted text with that ligature has no insertion point inside it: the
    /// ligature stands for one character, the carets are not read.
    /// </summary>
    [Fact]
    public void Ot19_LigatureWithCaretsInTheFont_OffersNoCaretInside()
    {
        var arabic = OpenTypeFont.Parse(EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf").FileData);
        var gdef = new TableData(arabic.TableData("GDEF").ToArray());
        var ligCarets = gdef.U16(8);
        Assert.NotEqual(0, ligCarets);
        var ligature = arabic.GlyphForUnicode(0xFDF2);
        var index = gdef.Coverage(ligCarets + gdef.U16(ligCarets), ligature);
        Assert.True(index >= 0);
        Assert.Equal(3, gdef.U16(ligCarets + gdef.U16(ligCarets + 4 + 2 * index)));

        RichFonts.SystemFontsOff = true;
        try
        {
            var layout = RichLayout.Layout(new RichText("ﷲ", new RichStyle { Family = "Liberation Sans", Size = 10 }), 200, BlockAlignment.Left, 1.2, 10, 0);
            var glyph = Assert.Single(layout.Lines[0].Glyphs);
            Assert.Equal("uniFDF2", glyph.Face!.Font.GlyphName(glyph.Glyph));
            Assert.Equal(1, glyph.CharLength);
            var edges = new[] { glyph.X, glyph.X + glyph.Advance };
            Assert.Contains(layout.Caret(0).X, edges);
            Assert.Contains(layout.Caret(1).X, edges);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    // ------------------------------------------------------------ OT 18.2.1: header

    /// <summary>
    /// OT 18.2.1 sentences 1–6 (version gate): a GDEF 1.0 (12-byte header) whose bytes after the header look like a markGlyphSetsDefOffset (0x0010
    /// to a mark glyph set {7}) — glyph 7 is in no set; the same bytes as version 1.2 — it is.
    /// </summary>
    [Fact]
    public void Ot19_Gdef10_NoMarkGlyphSetsBehindTheHeader()
    {
        byte[] Table(int minor) => [.. new Ot19Bytes().U16(1, minor, 32, 0, 0, 0).U16(0x0010, 0).U16(1, 1).U32(8).U16(1, 1, 7)
            .PadTo(32).U16(1, 7, 1, 3).ToArray()];
        Assert.False(new GlyphDefinitions(Table(0)).InMarkSet(0, 7));
        Assert.Equal(3, new GlyphDefinitions(Table(0)).GlyphClass(7));
        Assert.True(new GlyphDefinitions(Table(2)).InMarkSet(0, 7));
    }

    /// <summary>
    /// OT 18.2.1 sentences 1–6 (major version): a GDEF of major version 2 is a version PDF-Knead does not know — it is passed over: the spacing
    /// circumflex it declares a mark keeps its advance in a font with GPOS.
    /// </summary>
    [Fact]
    public void Ot19_GdefMajorVersion2_IsPassedOver()
    {
        var circumflex = Ot19G('^');
        var gpos = Ot19Layout("kern", Ot19Lookup(2, 0, -1, Ot19Pair(Ot19G('A'), Ot19G('V'), 0x0004, -70)));
        var version1 = Ot19Shaper(null, gpos, Ot19Gdef(glyphClasses: Ot19Classes((circumflex, 3)))).Shape("^");
        Assert.Equal(0, version1[0].XAdvance);
        var version2 = Ot19Shaper(null, gpos, Ot19Gdef(major: 2, glyphClasses: Ot19Classes((circumflex, 3)))).Shape("^");
        Assert.Equal(Ot18SansFont.AdvanceWidth(circumflex), version2[0].XAdvance);
    }

    /// <summary>
    /// OT 18.2.1 sentences 7–14: GDEF 1.0 with glyphClassDefOffset 0 and markAttachClassDefOffset 0 — class 0 and mark attachment class 0 for every
    /// glyph (the start of the table is not read as a ClassDef); with offset 12 to a ClassDef its classes.
    /// </summary>
    [Fact]
    public void Ot19_NullClassDefOffsets_ClassZero()
    {
        var none = new GlyphDefinitions(Ot19Gdef());
        for (var g = 0; g < 20; g++)
        {
            Assert.Equal(0, none.GlyphClass(g));
            Assert.Equal(0, none.MarkAttachClass(g));
        }
        var table = Ot19Gdef(glyphClasses: Ot19ClassDef1(3, 1, 2), markClasses: Ot19ClassDef1(4, 2));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4)));
        var some = new GlyphDefinitions(table);
        Assert.Equal([0, 1, 2, 0], Enumerable.Range(2, 4).Select(some.GlyphClass));
        Assert.Equal([0, 2, 0], Enumerable.Range(3, 3).Select(some.MarkAttachClass));
    }

    /// <summary>
    /// OT 18.2.1 sentences 15–23: GDEF 1.2 with markGlyphSetsDefOffset 0 — no glyph is in any set; with an offset to MarkGlyphSets as their Coverage
    /// tables say.
    /// </summary>
    [Fact]
    public void Ot19_Gdef12_MarkGlyphSetsOffsetZero_NoSet()
    {
        var none = new GlyphDefinitions(Ot19Gdef(minor: 2, glyphClasses: Ot19Classes((6, 3))));
        Assert.False(none.InMarkSet(0, 6));
        Assert.False(none.InMarkSet(1, 6));
        var some = new GlyphDefinitions(Ot19Gdef(minor: 2, glyphClasses: Ot19Classes((6, 3)), markSets: Ot19MarkSets(1, [6], [7])));
        Assert.True(some.InMarkSet(0, 6));
        Assert.False(some.InMarkSet(0, 7));
        Assert.True(some.InMarkSet(1, 7));
    }

    /// <summary>
    /// OT 18.2.1 sentences 24–32: a GDEF 1.3 with the same offsets as a GDEF 1.2 (parts at the same places) gives the same glyph classes, mark
    /// attachment classes and mark sets.
    /// </summary>
    [Fact]
    public void Ot19_Gdef13_SameAsGdef12()
    {
        GlyphDefinitions Table(int minor) => new(Ot19Gdef(minor: minor, glyphClasses: Ot19ClassDef1(5, 1, 3, 3, 2),
            markClasses: Ot19ClassDef1(6, 1, 2), markSets: Ot19MarkSets(1, [6], [7]), bodyAt: 24));
        GlyphDefinitions v12 = Table(2), v13 = Table(3);
        for (var g = 0; g < 12; g++)
        {
            Assert.Equal(v12.GlyphClass(g), v13.GlyphClass(g));
            Assert.Equal(v12.MarkAttachClass(g), v13.MarkAttachClass(g));
            Assert.Equal(v12.InMarkSet(0, g), v13.InMarkSet(0, g));
            Assert.Equal(v12.InMarkSet(1, g), v13.InMarkSet(1, g));
        }
        Assert.Equal(3, v13.GlyphClass(6));
        Assert.True(v13.InMarkSet(1, 7));
    }

    // ------------------------------------------------------------ OT 18.2.2: glyph class definition

    /// <summary>
    /// OT 18.2.2 sentences 1–8: GlyphClassDef format 1 (start 10, classes 1, 2, 3, 4) — classes 1 to 4; lookups with LookupFlag 0x2, 0x4 and 0x8
    /// skip exactly glyph 10, 11 and 12; glyph 12 (a mark) has advance 0 when the font has a GPOS, glyph 10 keeps its advance.
    /// </summary>
    [Fact]
    public void Ot19_GlyphClassDefFormat1_ClassesSkipsAndMarkAdvance()
    {
        var table = Ot19Gdef(glyphClasses: Ot19ClassDef1(10, 1, 2, 3, 4));
        var gdef = new GlyphDefinitions(table);
        Assert.Equal([1, 2, 3, 4], Enumerable.Range(10, 4).Select(gdef.GlyphClass));
        foreach (var (flag, skipped) in new[] { (0x2, 10), (0x4, 11), (0x8, 12) })
            for (var g = 10; g <= 13; g++)
                Assert.Equal(g == skipped, gdef.Skips(new Lookup(1, flag, [], -1), g));

        int c10 = Enumerable.Range(32, 0x2D0).First(c => Ot18SansFont.GlyphForUnicode(c) == 10), c12 = Enumerable.Range(32, 0x2D0).First(c => Ot18SansFont.GlyphForUnicode(c) == 12);
        Assert.True(Ot18SansFont.AdvanceWidth(12) > 0);
        var gpos = Ot19Layout("kern", Ot19Lookup(2, 0, -1, Ot19Pair(Ot19G('A'), Ot19G('V'), 0x0004, -70)));
        var glyphs = Ot19Shaper(null, gpos, table).Shape(string.Concat((char)c10, (char)c12));
        Assert.Equal(Ot18SansFont.AdvanceWidth(10), glyphs[0].XAdvance);
        Assert.Equal(0, glyphs[1].XAdvance);
    }

    /// <summary>
    /// OT 18.2.2 sentences 9–11: GlyphClassDef format 2 with one range 100–120 of class 1 — class 0 for glyphs 99 and 121, 1 for 110; an unknown
    /// format 3 — class 0 for every glyph.
    /// </summary>
    [Fact]
    public void Ot19_GlyphClassDefFormat2And3_ClassZeroOutside()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19ClassDef2((100, 120, 1))));
        Assert.Equal(0, gdef.GlyphClass(99));
        Assert.Equal(1, gdef.GlyphClass(110));
        Assert.Equal(0, gdef.GlyphClass(121));
        var format3 = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19Raw(3, 1, 100, 120, 1)));
        foreach (var g in (int[])[99, 100, 110, 120, 121]) Assert.Equal(0, format3.GlyphClass(g));
    }

    /// <summary>OT 18.2.2 sentences 12–14: a component glyph (class 4) is skipped by no LookupFlag — not by 0x0002, 0x0004, 0x0008 nor 0xFF00.</summary>
    [Fact]
    public void Ot19_ComponentGlyph_NeverSkipped()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19Classes((20, 4))));
        Assert.Equal(4, gdef.GlyphClass(20));
        foreach (var flag in (int[])[0x0002, 0x0004, 0x0008, 0xFF00, 0xFF0E]) Assert.False(gdef.Skips(new Lookup(1, flag, [], -1), 20));
    }

    // ------------------------------------------------------------ OT 18.2.3: attachment points

    /// <summary>
    /// OT 18.2.3 sentences 9–14: GPOS mark-to-base whose base anchor is format 2 (x, y and contour point 18), with an AttachList naming point 18 for
    /// „a“ — the acute is placed from the design coordinates of the anchor, exactly as with anchor format 1.
    /// </summary>
    [Fact]
    public void Ot19_AnchorFormat2WithAttachPoint_UsesTheDesignCoordinates()
    {
        int a = Ot19G('a'), acute = Ot19G('́');
        var gdef = Ot19Gdef(glyphClasses: Ot19BaseClasses(), attachList: b =>
        {
            var at = b.Here;
            string coverage = b.NewLabel(), point = b.NewLabel();
            b.Off16(coverage, at).U16(1).Off16(point, at);
            b.Label(point).U16(1, 18);
            b.Label(coverage);
            Ot19Coverage(b, [a]);
        });
        ShapedGlyph[] Shape(int format) => Ot19Shaper(null, Ot19Layout("mark", Ot19Lookup(4, 0, -1, Ot19MarkToBase([a], [acute], (520, 1100), (-60, 1000), format))), gdef)
            .Shape("á");
        var one = Ot19Result(Shape(1));
        Assert.Equal(Ot19Result(Shape(2)), one);
        Assert.Equal(520 + 60 - Ot18SansFont.AdvanceWidth(a), one[1].XOffset);
        Assert.Equal(100, one[1].YOffset);
    }

    // ------------------------------------------------------------ OT 18.2.5, 18.2.6: mark attachment classes, mark glyph sets

    /// <summary>OT 18.2.5 sentence 1: MarkAttachClassDef format 1 (start 20, classes 1, 2) — mark attachment class 1 for glyph 20, 2 for 21, 0 for 22.</summary>
    [Fact]
    public void Ot19_MarkAttachClassDefFormat1()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(markClasses: Ot19ClassDef1(20, 1, 2)));
        Assert.Equal([0, 1, 2, 0], Enumerable.Range(19, 4).Select(gdef.MarkAttachClass));
    }

    /// <summary>
    /// OT 18.2.6 sentences 1–7: MarkGlyphSets 0001 0002 with the Coverage tables {5} and {6} — glyph 6 is in set 1, glyph 5 is not, and there is no set
    /// 2; the same table with format 2 is read like format 1 today (the format is not checked).
    /// </summary>
    [Fact]
    public void Ot19_MarkGlyphSets_TwoSets_FormatNotChecked()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(minor: 2, markSets: Ot19MarkSets(1, [5], [6])));
        Assert.True(gdef.InMarkSet(1, 6));
        Assert.False(gdef.InMarkSet(1, 5));
        Assert.True(gdef.InMarkSet(0, 5));
        Assert.False(gdef.InMarkSet(2, 6));
        Assert.True(new GlyphDefinitions(Ot19Gdef(minor: 2, markSets: Ot19MarkSets(2, [5], [6]))).InMarkSet(1, 6));
    }

    /// <summary>
    /// OT 18.2.6 sentences 8–11: the sets {5, 6} and {6, 7} overlap — glyph 6 is in both; a Coverage table beyond 64 KB (Offset32 0x00010010 in a
    /// large GDEF) is found.
    /// </summary>
    [Fact]
    public void Ot19_MarkGlyphSets_OverlapAndOffset32()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(minor: 2, markSets: Ot19MarkSets(1, [5, 6], [6, 7])));
        Assert.True(gdef.InMarkSet(0, 6));
        Assert.True(gdef.InMarkSet(1, 6));
        Assert.False(gdef.InMarkSet(0, 7));

        const int sets = 14;
        var b = new Ot19Bytes().U16(1, 2, 0, 0, 0, 0, sets).U16(1, 1).U32(0x00010010);
        b.PadTo(sets + 0x00010010);
        Ot19Coverage(b, [6]);
        var large = new GlyphDefinitions(b.ToArray());
        Assert.True(large.InMarkSet(0, 6));
        Assert.False(large.InMarkSet(0, 5));
    }

    // ------------------------------------------------------------ OT 18.3: examples

    private static readonly Action<Ot19Bytes> Ot19Example2ClassDef = Ot19ClassDef2((0x24, 0x24, 1), (0x9F, 0x9F, 2), (0x58, 0x58, 3), (0x18F, 0x18F, 4));

    private static readonly Action<Ot19Bytes> Ot19Example7ClassDef = Ot19ClassDef2((0x268, 0x26A, 1), (0x270, 0x272, 1), (0x28C, 0x28F, 2), (0x295, 0x295, 2));

    /// <summary>
    /// OT 18.3.1 sentences 1–8: the GDEF header of example 1 (00010000 000C 0026 0040 005A) with the ClassDef of example 2 at 0x0C and that of example 7
    /// at 0x5A — glyph class 1 for 0x24 and 2 for 0x9F, mark attachment class 1 for 0x269; the AttachList and LigCaretList offsets have no effect.
    /// </summary>
    [Fact]
    public void Ot19_Example1Header()
    {
        var b = new Ot19Bytes().U16(1, 0, 0x000C, 0x0026, 0x0040, 0x005A);
        Ot19Example2ClassDef(b);
        b.PadTo(0x5A);
        Ot19Example7ClassDef(b);
        var gdef = new GlyphDefinitions(b.ToArray());
        Assert.Equal(1, gdef.GlyphClass(0x24));
        Assert.Equal(2, gdef.GlyphClass(0x9F));
        Assert.Equal(1, gdef.MarkAttachClass(0x269));
        Assert.Equal(2, gdef.MarkAttachClass(0x295));
        Assert.Equal(0, gdef.MarkAttachClass(0x24));
    }

    /// <summary>
    /// OT 18.3.2 sentences 1–5 and 6–21: the GlyphClassDef of example 2 (format 2, four ranges) — class 1 for 0x24, 2 for 0x9F, 4 for 0x18F; its
    /// ranges are not in ascending order (0x9F before 0x58), and the binary search misses 0x58 (class 0); the same ranges sorted give 3.
    /// </summary>
    [Fact]
    public void Ot19_Example2GlyphClassDef()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19Example2ClassDef));
        Assert.Equal(1, gdef.GlyphClass(0x24));
        Assert.Equal(2, gdef.GlyphClass(0x9F));
        Assert.Equal(4, gdef.GlyphClass(0x18F));
        Assert.Equal(0, gdef.GlyphClass(0x58));
        var sorted = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19ClassDef2((0x24, 0x24, 1), (0x58, 0x58, 3), (0x9F, 0x9F, 2), (0x18F, 0x18F, 4))));
        Assert.Equal(3, sorted.GlyphClass(0x58));
        Assert.Equal(2, sorted.GlyphClass(0x9F));
    }

    /// <summary>
    /// OT 18.3.7 sentences 1–5 and 6–21: the MarkAttachClassDef of example 7 — class 1 for 0x269 and 0x271, 2 for 0x28D and 0x295, 0 for 0x26B; with
    /// these glyphs marks (class 3) a lookup with LookupFlag 0x0100 skips 0x28D and not 0x269.
    /// </summary>
    [Fact]
    public void Ot19_Example7MarkAttachClassDef()
    {
        var gdef = new GlyphDefinitions(Ot19Gdef(glyphClasses: Ot19ClassDef2((0x268, 0x295, 3)), markClasses: Ot19Example7ClassDef));
        Assert.Equal(1, gdef.MarkAttachClass(0x269));
        Assert.Equal(1, gdef.MarkAttachClass(0x271));
        Assert.Equal(2, gdef.MarkAttachClass(0x28D));
        Assert.Equal(2, gdef.MarkAttachClass(0x295));
        Assert.Equal(0, gdef.MarkAttachClass(0x26B));
        var lookup = new Lookup(1, 0x0100, [], -1);
        Assert.True(gdef.Skips(lookup, 0x28D));
        Assert.False(gdef.Skips(lookup, 0x269));
    }
}
