using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package cff-01 (Adobe TN 5176, sections 1–15): the missing evidence of the sentence-by-sentence draft as tests. The helpers named
/// Cff01… build small CFF fonts and Type 2 charstrings by hand; the packages cff-02 and t2-01 use them as well.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ Helpers (shared with cff-02 and t2-01)

    private static string Cff01Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>The bundled Noto Sans CJK, read once.</summary>
    private static readonly Lazy<OpenTypeFont> Cff01NotoFont =
        new(() => OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(Cff01Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc"))));

    /// <summary>The CFF table of Noto Sans CJK (CID-keyed, several Font DICTs, subroutines).</summary>
    private static readonly Lazy<ArraySegment<byte>> Cff01Noto = new(() =>
    {
        var table = Cff01NotoFont.Value.Table("CFF ")!.Value;
        return new ArraySegment<byte>(Cff01NotoFont.Value.FileData, table.Offset, table.Length);
    });

    /// <summary>A CFF INDEX with 4-byte offsets (TN 5176, 5).</summary>
    private static byte[] Cff01Index(params byte[][] items)
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

    /// <summary>A DICT integer in the 5-byte form (operator 29), so sizes are known before positions.</summary>
    private static byte[] Cff01Five(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>The operator bytes of a DICT operator (1200 + n for the escaped ones).</summary>
    private static byte[] Cff01Op(int op) => op >= 1200 ? [12, (byte)(op - 1200)] : [(byte)op];

    /// <summary>DICT entries: each operator with its integer operands in the 5-byte form.</summary>
    private static byte[] Cff01Dict(params (int Op, int[] Operands)[] entries)
    {
        var d = new List<byte>();
        foreach (var (op, operands) in entries)
        {
            foreach (var v in operands) d.AddRange(Cff01Five(v));
            d.AddRange(Cff01Op(op));
        }
        return [.. d];
    }

    /// <summary>What a hand-built name-keyed CFF contains; offsets are filled in by <see cref="Cff01Build"/>.</summary>
    private sealed class Cff01Spec
    {
        public byte[] Header = [1, 0, 4, 4];
        public byte[][] Names = ["Probe"u8.ToArray()];
        /// <summary>Further Top DICTs (each a copy of the first) — a FontSet of several fonts.</summary>
        public int TopDicts = 1;
        /// <summary>A different first Top DICT (e.g. a synthetic font) before the font's own one.</summary>
        public byte[]? FirstTop;
        /// <summary>Top DICT entries before the offsets.</summary>
        public byte[] TopExtra = [];
        public string[] Strings = [];
        public byte[][] GlobalSubrs = [];
        public byte[][] CharStrings = [[14]];
        /// <summary>Custom charset data, or null (then <see cref="CharsetId"/> or no operator 15).</summary>
        public byte[]? Charset;
        public int? CharsetId;
        public byte[]? Encoding;
        public int? EncodingId;
        /// <summary>Private DICT entries without Subrs; null: no Private operator in the Top DICT.</summary>
        public byte[]? Private = [];
        public byte[][] LocalSubrs = [];
        /// <summary>Zeros before the Private DICT so that it begins at this offset (0: right after the CharStrings).</summary>
        public int PrivateAt;
        /// <summary>Zeros between the Private DICT and its Subrs INDEX.</summary>
        public int SubrsGap;
    }

    /// <summary>Builds a name-keyed CFF: header, Name, Top DICT, String, Global Subrs INDEX, charset, encoding, CharStrings, Private DICT, Subrs.</summary>
    private static byte[] Cff01Build(Cff01Spec s)
    {
        var privateSize = s.Private is null ? 0 : s.Private.Length + (s.LocalSubrs.Length > 0 ? 6 : 0);
        byte[] Top(int charsetAt, int encodingAt, int charStringsAt, int privateAt)
        {
            var d = new List<byte>(s.TopExtra);
            if (s.Charset is not null) d.AddRange([.. Cff01Five(charsetAt), 15]);
            else if (s.CharsetId is { } c) d.AddRange([.. Cff01Five(c), 15]);
            if (s.Encoding is not null) d.AddRange([.. Cff01Five(encodingAt), 16]);
            else if (s.EncodingId is { } e) d.AddRange([.. Cff01Five(e), 16]);
            d.AddRange([.. Cff01Five(charStringsAt), 17]);
            if (s.Private is not null) d.AddRange([.. Cff01Five(privateSize), .. Cff01Five(privateAt), 18]);
            return [.. d];
        }
        byte[][] Tops(byte[] top) => s.FirstTop is { } first ? [first, top] : [.. Enumerable.Repeat(top, s.TopDicts)];
        var names = Cff01Index(s.Names);
        var strings = Cff01Index([.. s.Strings.Select(x => Encoding.Latin1.GetBytes(x))]);
        var global = Cff01Index(s.GlobalSubrs);
        var topSize = Cff01Index(Tops(Top(0, 0, 0, 0))).Length;
        var charsetAt = s.Header.Length + names.Length + topSize + strings.Length + global.Length;
        var charset = s.Charset ?? [];
        var encodingAt = charsetAt + charset.Length;
        var encoding = s.Encoding ?? [];
        var charStringsAt = encodingAt + encoding.Length;
        var charStrings = Cff01Index(s.CharStrings);
        var end = charStringsAt + charStrings.Length;
        var privateAt = Math.Max(end, s.PrivateAt);
        var privateDict = new List<byte>(s.Private ?? []);
        if (s.Private is not null && s.LocalSubrs.Length > 0) privateDict.AddRange([.. Cff01Five(privateSize + s.SubrsGap), 19]);
        var cff = new List<byte>(s.Header);
        cff.AddRange(names);
        cff.AddRange(Cff01Index(Tops(Top(charsetAt, encodingAt, charStringsAt, privateAt))));
        cff.AddRange(strings);
        cff.AddRange(global);
        cff.AddRange(charset);
        cff.AddRange(encoding);
        cff.AddRange(charStrings);
        if (s.Private is null) return [.. cff];
        cff.AddRange(new byte[privateAt - end]);
        cff.AddRange(privateDict);
        if (s.LocalSubrs.Length > 0)
        {
            cff.AddRange(new byte[s.SubrsGap]);
            cff.AddRange(Cff01Index(s.LocalSubrs));
        }
        return [.. cff];
    }

    /// <summary>One Font DICT of a hand-built CIDFont: its own entries, Private DICT entries (without Subrs) and local subroutines.</summary>
    private sealed record Cff01Fd(byte[] Private, byte[][] Subrs, byte[]? Font = null);

    /// <summary>
    /// Builds a CID-keyed CFF: ROS first (SIDs of <paramref name="strings"/>, supplement 0), then <paramref name="topExtra"/>, charset
    /// (omitted when null: offset 0), CharStrings, FDArray, FDSelect; each Private DICT followed by its Subrs INDEX.
    /// </summary>
    private static byte[] Cff01Cid(byte[][] charStrings, byte[] fdSelect, Cff01Fd[] fds, byte[][]? globalSubrs = null, byte[]? charset = null,
        string[]? strings = null, byte[]? topExtra = null, int[]? ros = null)
    {
        strings ??= ["Adobe", "Identity"];
        ros ??= [391, 392, 0];
        byte[] Top(int charsetAt, int charStringsAt, int fdArrayAt, int fdSelectAt)
        {
            var d = new List<byte>([.. Cff01Five(ros[0]), .. Cff01Five(ros[1]), .. Cff01Five(ros[2]), 12, 30]);
            d.AddRange(topExtra ?? []);
            if (charset is not null) d.AddRange([.. Cff01Five(charsetAt), 15]);
            d.AddRange([.. Cff01Five(charStringsAt), 17, .. Cff01Five(fdArrayAt), 12, 36, .. Cff01Five(fdSelectAt), 12, 37]);
            return [.. d];
        }
        var names = Cff01Index("CidProbe"u8.ToArray());
        var stringIndex = Cff01Index([.. strings.Select(x => Encoding.Latin1.GetBytes(x))]);
        var global = Cff01Index(globalSubrs ?? []);
        var topSize = Cff01Index(Top(0, 0, 0, 0)).Length;
        var charsetAt = 4 + names.Length + topSize + stringIndex.Length + global.Length;
        var fdSelectAt = charsetAt + (charset?.Length ?? 0);
        var charStringsAt = fdSelectAt + fdSelect.Length;
        var charStringIndex = Cff01Index(charStrings);
        var fdArrayAt = charStringsAt + charStringIndex.Length;
        int PrivateSize(Cff01Fd fd) => fd.Private.Length + (fd.Subrs.Length > 0 ? 6 : 0);
        byte[][] FontDicts(int start)
        {
            var result = new byte[fds.Length][];
            var at = start;
            for (var i = 0; i < fds.Length; i++)
            {
                result[i] = [.. fds[i].Font ?? [], .. Cff01Five(PrivateSize(fds[i])), .. Cff01Five(at), 18];
                at += PrivateSize(fds[i]) + (fds[i].Subrs.Length > 0 ? Cff01Index(fds[i].Subrs).Length : 0);
            }
            return result;
        }
        var privateStart = fdArrayAt + Cff01Index(FontDicts(0)).Length;
        var cff = new List<byte> { 1, 0, 4, 4 };
        cff.AddRange(names);
        cff.AddRange(Cff01Index(Top(charsetAt, charStringsAt, fdArrayAt, fdSelectAt)));
        cff.AddRange(stringIndex);
        cff.AddRange(global);
        cff.AddRange(charset ?? []);
        cff.AddRange(fdSelect);
        cff.AddRange(charStringIndex);
        cff.AddRange(Cff01Index(FontDicts(privateStart)));
        foreach (var fd in fds)
        {
            cff.AddRange(fd.Private);
            if (fd.Subrs.Length > 0)
            {
                cff.AddRange([.. Cff01Five(PrivateSize(fd)), 19]);
                cff.AddRange(Cff01Index(fd.Subrs));
            }
        }
        return [.. cff];
    }

    /// <summary>
    /// A Type 2 charstring from tokens: integers and doubles become numbers (shortest form; non-integers as 16.16 with 255), strings are
    /// operator names, byte arrays are copied (mask bytes, reserved operators).
    /// </summary>
    private static byte[] Cff01Cs(params object[] tokens)
    {
        var d = new List<byte>();
        foreach (var t in tokens)
            switch (t)
            {
                case byte[] raw: d.AddRange(raw); break;
                case string op: d.AddRange(Cff01CsOperators[op]); break;
                case int n: Cff01CsNumber(d, n); break;
                case double v: Cff01CsNumber(d, v); break;
                default: throw new ArgumentException(t?.ToString());
            }
        return [.. d];
    }

    private static void Cff01CsNumber(List<byte> d, double v)
    {
        if (v == Math.Round(v) && v is >= -32768 and <= 32767)
        {
            var n = (int)v;
            switch (n)
            {
                case >= -107 and <= 107: d.Add((byte)(n + 139)); return;
                case >= 108 and <= 1131: d.AddRange([(byte)((n - 108) / 256 + 247), (byte)((n - 108) % 256)]); return;
                case >= -1131 and <= -108: d.AddRange([(byte)((-n - 108) / 256 + 251), (byte)((-n - 108) % 256)]); return;
                default: d.AddRange([28, (byte)(n >> 8), (byte)n]); return;
            }
        }
        var f = (int)Math.Round(v * 65536);
        d.AddRange([255, (byte)(f >> 24), (byte)(f >> 16), (byte)(f >> 8), (byte)f]);
    }

    private static readonly Dictionary<string, byte[]> Cff01CsOperators = new()
    {
        ["hstem"] = [1], ["vstem"] = [3], ["vmoveto"] = [4], ["rlineto"] = [5], ["hlineto"] = [6], ["vlineto"] = [7], ["rrcurveto"] = [8],
        ["callsubr"] = [10], ["return"] = [11], ["endchar"] = [14], ["hstemhm"] = [18], ["hintmask"] = [19], ["cntrmask"] = [20],
        ["rmoveto"] = [21], ["hmoveto"] = [22], ["vstemhm"] = [23], ["rcurveline"] = [24], ["rlinecurve"] = [25], ["vvcurveto"] = [26],
        ["hhcurveto"] = [27], ["callgsubr"] = [29], ["vhcurveto"] = [30], ["hvcurveto"] = [31],
        ["dotsection"] = [12, 0], ["abs"] = [12, 9], ["add"] = [12, 10], ["sub"] = [12, 11], ["div"] = [12, 12], ["neg"] = [12, 14],
        ["drop"] = [12, 18], ["put"] = [12, 20], ["get"] = [12, 21], ["mul"] = [12, 24], ["sqrt"] = [12, 26], ["dup"] = [12, 27],
        ["exch"] = [12, 28], ["hflex"] = [12, 34], ["flex"] = [12, 35], ["hflex1"] = [12, 36], ["flex1"] = [12, 37],
    };

    /// <summary>A name-keyed CFF with .notdef and the given glyphs, optional subroutines and the widths of the Private DICT.</summary>
    private static byte[] Cff01Glyphs(byte[][] glyphs, byte[][]? subrs = null, byte[][]? globalSubrs = null, int? defaultWidth = null,
        int? nominalWidth = null)
    {
        var priv = new List<byte>();
        if (defaultWidth is { } dw) priv.AddRange(Cff01Dict((20, [dw])));
        if (nominalWidth is { } nw) priv.AddRange(Cff01Dict((21, [nw])));
        return Cff01Build(new Cff01Spec
        {
            CharStrings = [[14], .. glyphs], LocalSubrs = subrs ?? [], GlobalSubrs = globalSubrs ?? [], Private = [.. priv],
        });
    }

    /// <summary>The outline as text: "M x y", "L x y", "C x1 y1 x2 y2 x3 y3", "Z" (font units, invariant culture).</summary>
    private static string Cff01Path(GlyphOutline outline)
    {
        var parts = new List<string>();
        var p = 0;
        string N(int k) => outline.Points[k].ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var verb in outline.Verbs)
            switch (verb)
            {
                case PathVerb.Move: parts.Add($"M {N(p)} {N(p + 1)}"); p += 2; break;
                case PathVerb.Line: parts.Add($"L {N(p)} {N(p + 1)}"); p += 2; break;
                case PathVerb.Cubic: parts.Add($"C {N(p)} {N(p + 1)} {N(p + 2)} {N(p + 3)} {N(p + 4)} {N(p + 5)}"); p += 6; break;
                default: parts.Add("Z"); break;
            }
        return string.Join(" | ", parts);
    }

    /// <summary>The structures at the start of a CFF: Name, Top DICT and String INDEX items, the Global Subrs count and where it begins.</summary>
    private static (byte[][] Names, byte[][] Tops, byte[][] Strings, int GlobalCount, int GlobalAt) Cff01Parts(byte[] cff)
    {
        var data = new ArraySegment<byte>(cff);
        var r = new FontReader(data) { Position = cff[2] };
        byte[][] Items(CffFont.CffIndex index) => [.. Enumerable.Range(0, index.Count).Select(i => index.Item(data, i).ToArray())];
        var names = Items(CffFont.CffIndex.Read(data, ref r));
        var tops = Items(CffFont.CffIndex.Read(data, ref r));
        var strings = Items(CffFont.CffIndex.Read(data, ref r));
        var globalAt = r.Position;
        var global = CffFont.CffIndex.Read(data, ref r);
        return (names, tops, strings, global.Count, globalAt);
    }

    /// <summary>The entries of a DICT in their order: operator and operands.</summary>
    private static List<(int Op, double[] Operands)> Cff01Ops(ReadOnlySpan<byte> dict)
    {
        var result = new List<(int, double[])>();
        var start = 0;
        var i = 0;
        while (i < dict.Length)
        {
            int b0 = dict[i];
            if (b0 <= 21)
            {
                i += b0 == 12 ? 2 : 1;
                foreach (var (op, values) in CffFont.Dict.Parse(dict[start..Math.Min(i, dict.Length)]).Entries) result.Add((op, values));
                start = i;
            }
            else if (b0 == 28) i += 3;
            else if (b0 == 29) i += 5;
            else if (b0 == 30)
            {
                i++;
                while (i < dict.Length && (dict[i] & 0xF) != 0xF && (dict[i] >> 4) != 0xF) i++;
                i++;
            }
            else if (b0 is >= 247 and <= 254) i += 2;
            else i++;
        }
        return result;
    }

    /// <summary>The Top DICT of a CFF.</summary>
    private static CffFont.Dict Cff01Top(byte[] cff) => CffFont.Dict.Parse(Cff01Parts(cff).Tops[0]);

    /// <summary>A string of a CFF by its SID (standard strings below 391).</summary>
    private static string Cff01String(byte[] cff, int sid) =>
        sid < FontTables.CffStandardStrings.Length ? FontTables.CffStandardStrings[sid] : Encoding.Latin1.GetString(Cff01Parts(cff).Strings[sid - 391]);

    /// <summary>The charstrings of a CFF.</summary>
    private static byte[][] Cff01CharStrings(byte[] cff)
    {
        var data = new ArraySegment<byte>(cff);
        var r = new FontReader(data) { Position = Cff01Top(cff).Int(17)!.Value };
        var index = CffFont.CffIndex.Read(data, ref r);
        return [.. Enumerable.Range(0, index.Count).Select(i => index.Item(data, i).ToArray())];
    }

    /// <summary>The Font DICT and Private DICT entries (in their order) that apply to a glyph; for name-keyed fonts the Top DICT's Private.</summary>
    private static (List<(int Op, double[] Operands)> Font, List<(int Op, double[] Operands)> Private) Cff01FontDictOf(ArraySegment<byte> data, int glyph)
    {
        var bytes = data.ToArray();
        var top = Cff01Top(bytes);
        var owner = top;
        List<(int, double[])> font = [];
        if (top.Has(1230) && top.Int(1236) is { } fdArrayAt)
        {
            var r = new FontReader(data) { Position = fdArrayAt };
            var fdArray = CffFont.CffIndex.Read(data, ref r);
            var count = new FontReader(data) { Position = top.Int(17)!.Value };
            var glyphs = CffFont.CffIndex.Read(data, ref count).Count;
            var fd = top.Int(1237) is { } fdSelectAt ? CffFont.ReadFdSelect(data, fdSelectAt, glyphs)[glyph] : 0;
            font = Cff01Ops(fdArray.Item(data, fd));
            owner = CffFont.Dict.Parse(fdArray.Item(data, fd));
        }
        if (owner.Get(18) is not { Length: 2 } at) return (font, []);
        return (font, Cff01Ops(data.AsSpan((int)at[1], (int)at[0])));
    }

    /// <summary>The operators of a charstring without masks (escaped ones as 1200 + n), numbers skipped.</summary>
    private static List<int> Cff01CsOps(byte[] cs)
    {
        var ops = new List<int>();
        for (var i = 0; i < cs.Length;)
        {
            int b0 = cs[i];
            if (b0 >= 32) i += b0 <= 246 ? 1 : b0 <= 254 ? 2 : 5;
            else if (b0 == 28) i += 3;
            else if (b0 == 12) { ops.Add(1200 + cs[i + 1]); i += 2; }
            else { ops.Add(b0); i++; }
        }
        return ops;
    }

    /// <summary>A small outline: a square of the given size at the origin.</summary>
    private static GlyphOutline Cff01Square(float size)
    {
        var o = new GlyphOutline();
        o.MoveTo(0, 0);
        o.LineTo(size, 0);
        o.LineTo(size, size);
        o.CubicTo(size / 2, size, 0, size / 2, 0, 0);
        return o.Finish();
    }

    private static readonly Matrix Cff01Thousandth = new(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>The three writers of PDF-Knead on small inputs: subset, rebuild (name-keyed and CID-keyed) and scrub.</summary>
    private static IEnumerable<(string Writer, byte[] Cff)> Cff01Writers()
    {
        var source = Cff01Glyphs([Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")]);
        yield return ("Subset", CffSubsetter.Subset(source, [1]));
        yield return ("Build", CffBuilder.Build("Probe", Cff01Thousandth, [("g1", Cff01Square(500), 600)]));
        yield return ("BuildCid", CffBuilder.BuildCid("Probe", Cff01Thousandth, "Adobe", "Identity", 0, [(1, Cff01Square(500), 600)]));
        yield return ("Scrub", CffScrubber.Scrub(source, [1]).Data);
    }

    // ------------------------------------------------------------ Tests of cff-01

    /// <summary>
    /// TN 5176, 1 sentence 2 (cff-02: 17 sentences 8–9): a FontSet may hold several fonts. A CFF with two names and two Top DICTs is read
    /// as its first font; every writer of PDF-Knead (subset, rebuild, CID rebuild, scrub) writes exactly one font, without SyntheticBase.
    /// </summary>
    [Fact]
    public void Cff01_FontSetOfTwo_ReadsTheFirstAndWritesOne()
    {
        var cff = Cff01Build(new Cff01Spec
        {
            Names = ["Erste"u8.ToArray(), "Zweite"u8.ToArray()],
            TopDicts = 2,
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
        });
        var font = CffFont.Parse(cff);
        Assert.Equal("Erste", font.Name);
        Assert.Equal(2, font.GlyphCount);
        Assert.False(font.Outline(1).IsEmpty);
        Assert.Single(Cff01Parts(CffSubsetter.Subset(cff, [1])).Names);
        Assert.Single(Cff01Parts(CffScrubber.Scrub(cff, [1]).Data).Tops);
        foreach (var (writer, output) in Cff01Writers())
        {
            var parts = Cff01Parts(output);
            Assert.True(parts.Names.Length == 1 && parts.Tops.Length == 1, writer);
            Assert.False(CffFont.Dict.Parse(parts.Tops[0]).Has(1220), writer);
        }
    }

    /// <summary>TN 5176, 3 sentences 7–14: OffSize is 1 to 4; an INDEX with OffSize 0 or 5 is a format error.</summary>
    [Fact]
    public void Cff01_IndexWithOffSizeOutsideOneToFour_IsAFormatError()
    {
        foreach (var offSize in new byte[] { 0, 5 })
        {
            var cff = Cff01Glyphs([Cff01Cs("endchar")]);
            cff[cff[2] + 2] = offSize;   // OffSize of the Name INDEX
            Assert.Throws<FontFormatException>(() => CffFont.Parse(cff));
        }
    }

    /// <summary>
    /// TN 5176, 4 sentences 5–31 and Appendix H sentences 24–38: each integer form at the bounds of its range and the example values of the
    /// table read as their value, and the writer chooses the same bytes (the shortest form; above 16 bits the 5-byte form).
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 32 }, -107)]
    [InlineData(new byte[] { 246 }, 107)]
    [InlineData(new byte[] { 247, 0 }, 108)]
    [InlineData(new byte[] { 250, 255 }, 1131)]
    [InlineData(new byte[] { 251, 0 }, -108)]
    [InlineData(new byte[] { 254, 255 }, -1131)]
    [InlineData(new byte[] { 28, 0x80, 0x00 }, -32768)]
    [InlineData(new byte[] { 28, 0x7F, 0xFF }, 32767)]
    [InlineData(new byte[] { 29, 0x7F, 0xFF, 0xFF, 0xFF }, int.MaxValue)]
    [InlineData(new byte[] { 0x8B }, 0)]
    [InlineData(new byte[] { 0xEF }, 100)]
    [InlineData(new byte[] { 0x27 }, -100)]
    [InlineData(new byte[] { 0xFA, 0x7C }, 1000)]
    [InlineData(new byte[] { 0xFE, 0x7C }, -1000)]
    [InlineData(new byte[] { 0x1C, 0x27, 0x10 }, 10000)]
    [InlineData(new byte[] { 0x1C, 0xD8, 0xF0 }, -10000)]
    [InlineData(new byte[] { 0x1D, 0x00, 0x01, 0x86, 0xA0 }, 100000)]
    [InlineData(new byte[] { 0x1D, 0xFF, 0xFE, 0x79, 0x60 }, -100000)]
    [InlineData(new byte[] { 29, 0x00, 0x01, 0x00, 0x00 }, 65536)]
    [InlineData(new byte[] { 29, 0xFF, 0xFF, 0x7F, 0xFF }, -32769)]
    [InlineData(new byte[] { 248, 0 }, 364)]
    [InlineData(new byte[] { 28, 0x04, 0x6C }, 1132)]
    public void Cff01_DictIntegers_ReadAndWrittenInTheirForms(byte[] bytes, int value)
    {
        Assert.Equal(new double[] { value }, CffFont.Dict.Parse([.. bytes, 17]).Get(17));
        var written = new List<byte>();
        CffSubsetter.Operands(written, [value]);
        Assert.Equal(bytes, written.ToArray());
    }

    /// <summary>TN 5176, 4 sentences 32–44: real numbers in nibbles — the two examples read as their value, −2.25 is written as 1e e2 a2 5f.</summary>
    [Fact]
    public void Cff01_DictReals_ReadAndWritten()
    {
        Assert.Equal(-2.25, CffFont.Dict.Parse([30, 0xE2, 0xA2, 0x5F, 17]).Number(17));
        Assert.Equal(0.140541E-3, CffFont.Dict.Parse([30, 0x0A, 0x14, 0x05, 0x41, 0xC3, 0xFF, 17]).Number(17)!.Value, 12);
        var written = new List<byte>();
        CffSubsetter.Operands(written, [-2.25]);
        Assert.Equal(new byte[] { 30, 0xE2, 0xA2, 0x5F }, written.ToArray());
    }

    /// <summary>TN 5176, 4 sentences 45–46: a reserved byte (22, 31, 255) between the operands of an operator is passed over; the operands stay.</summary>
    [Fact]
    public void Cff01_DictReservedBytes_ArePassedOver()
    {
        foreach (var reserved in new byte[] { 22, 31, 255 })
            Assert.Equal(new double[] { 1, 2 }, CffFont.Dict.Parse([140, reserved, 141, 17]).Get(17));
    }

    /// <summary>TN 5176, 4 sentence 47: 49 operands before an operator (one more than the limit) are all read, without an error (lenient).</summary>
    [Fact]
    public void Cff01_DictWith49Operands_ReadsAll()
    {
        var bytes = Enumerable.Range(0, 49).Select(i => (byte)(139 + i % 100)).Append((byte)17).ToArray();
        Assert.Equal(49, CffFont.Dict.Parse(bytes).Get(17)!.Length);
    }

    /// <summary>
    /// TN 5176, 4 sentences 52–55 and 15 sentences 18–19: the delta arrays of the Private DICT (BlueValues, OtherBlues, StemSnapH/V) are taken
    /// over number by number and in the order of the source when Noto CJK is subset or scrubbed — never summed up.
    /// </summary>
    [Fact]
    public void Cff01_PrivateDeltaArrays_TakenOverUnchanged()
    {
        var noto = Cff01Noto.Value;
        var glyph = Cff01NotoFont.Value.GlyphForUnicode('A');   // its Font DICT has BlueValues and OtherBlues
        var source = Cff01FontDictOf(noto, glyph).Private.Where(e => e.Op != 19).ToList();
        Assert.Contains(source, e => e.Op == 6);
        Assert.Contains(source, e => e.Op == 7);
        var subset = Cff01FontDictOf(CffSubsetter.Subset(noto, [glyph]), 1).Private;
        var scrubbed = Cff01FontDictOf(CffScrubber.Scrub(noto, [glyph]).Data, glyph).Private;
        foreach (var written in new[] { subset, scrubbed })
        {
            Assert.Equal(source.Select(e => e.Op), written.Select(e => e.Op));
            for (var i = 0; i < source.Count; i++) Assert.Equal(source[i].Operands, written[i].Operands);
        }
    }

    /// <summary>TN 5176, 5 sentences 14–19: the INDEX writer — items [1, 2] and [3] become 00 02 01 01 03 04 01 02 03, no items 00 00.</summary>
    [Fact]
    public void Cff01_IndexWriter_CountOffSizeOffsetsData()
    {
        Assert.Equal(new byte[] { 0, 2, 1, 1, 3, 4, 1, 2, 3 }, CffSubsetter.Index([[1, 2], [3]]));
        Assert.Equal(new byte[] { 0, 0 }, CffSubsetter.Index([]));
    }

    /// <summary>TN 5176, 6 sentences 1–6 and 12: the output of the subset, the rebuild and the scrub begins with the header 01 00 04 04.</summary>
    [Fact]
    public void Cff01_WrittenHeader_Is01000404()
    {
        foreach (var (writer, cff) in Cff01Writers()) Assert.True(cff.AsSpan(0, 4).SequenceEqual(new byte[] { 1, 0, 4, 4 }), writer);
    }

    /// <summary>
    /// TN 5176, 6 sentence 7: a reader must not take a major version it does not know. An OpenType font whose "CFF " table has major
    /// version 2 gives no CFF outlines, and scrubbing the table is a format error (today both read it as version 1).
    /// </summary>
    [Fact]
    public void Cff01_MajorVersion2_IsNotReadAsCff()
    {
        var cff = Cff01Glyphs([Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")]);
        cff[0] = 2;
        var head = new byte[54];
        head[18] = 0x03;   // unitsPerEm 1000
        head[19] = 0xE8;
        var otf = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["CFF "] = cff, ["head"] = head });
        Assert.Null(OpenTypeFont.Parse(otf).Cff);
        Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(cff, [1]));
    }

    /// <summary>
    /// TN 5176, 6 sentences 8–9: a higher minor version stays readable. FontFile3 /Type1C data with the header 01 01 04 04 gives the CFF program
    /// (today none: the font counts as not embedded and is substituted).
    /// </summary>
    [Fact]
    public void Cff01_MinorVersion1_IsRead()
    {
        var cff = Cff01Glyphs([Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")]);
        cff[1] = 1;
        Assert.NotNull(FontProgram.FromBytes(cff)?.Cff);
    }

    /// <summary>TN 5176, 6 sentences 10–11: the Name INDEX begins at hdrSize; with hdrSize 5 (one extra byte) name and glyphs are found as with 4.</summary>
    [Fact]
    public void Cff01_HeaderSize5_NameAndGlyphsFound()
    {
        var font = CffFont.Parse(Cff01Build(new Cff01Spec
        {
            Header = [1, 0, 5, 4, 0xAA],
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
        }));
        Assert.Equal("Probe", font.Name);
        Assert.Equal("M 0 0 | L 10 0 | Z", Cff01Path(font.Outline(1)));
    }

    /// <summary>TN 5176, 7 sentence 1: the name of a font is its entry in the Name INDEX — for a rebuilt font and for FoxitSymbol.cff.</summary>
    [Fact]
    public void Cff01_FontName_IsTheNameIndexEntry()
    {
        Assert.Equal("ABCDEF+Test", CffFont.Parse(CffBuilder.Build("ABCDEF+Test", Cff01Thousandth, [("g1", Cff01Square(500), 600)])).Name);
        var symbol = EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        Assert.Equal(Encoding.ASCII.GetString(Cff01Parts(symbol).Names[0]), CffFont.Parse(symbol).Name);
        Assert.NotEmpty(CffFont.Parse(symbol).Name);
    }

    /// <summary>TN 5176, 7 sentences 6–7: a source with an empty Name INDEX gives a subset and a scrubbed font with exactly one name, "PDFKnead".</summary>
    [Fact]
    public void Cff01_EmptyNameIndex_WrittenWithOneName()
    {
        var cff = Cff01Build(new Cff01Spec { Names = [], CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")] });
        Assert.Equal("", CffFont.Parse(cff).Name);
        foreach (var output in new[] { CffSubsetter.Subset(cff, [1]), CffScrubber.Scrub(cff, [1]).Data })
            Assert.Equal(new[] { "PDFKnead" }, Cff01Parts(output).Names.Select(n => Encoding.ASCII.GetString(n)));
    }

    /// <summary>
    /// TN 5176, 7 sentences 8–9: the rebuild keeps only the characters 33–126 without the ten delimiters ("A B(C)/Ä" → "ABC"); for PDF/A every
    /// byte outside printable ASCII of the name becomes "_" of the same length.
    /// </summary>
    [Fact]
    public void Cff01_BuiltName_KeepsOnlyAllowedCharacters()
    {
        Assert.Equal("ABC", CffFont.Parse(CffBuilder.Build("A B(C)/Ä", Cff01Thousandth, [("g1", Cff01Square(500), 600)])).Name);
        var cff = Cff01Build(new Cff01Spec { Names = [[0x41, 0xC4, 0x42]], CharStrings = [[14]] });
        var ascii = CffBuilder.AsciiNames(cff)!;
        Assert.Equal(cff.Length, ascii.Length);
        Assert.Equal("A_B", Encoding.ASCII.GetString(Cff01Parts(ascii).Names[0]));
    }

    /// <summary>TN 5176, 7 sentences 8–9: a font name has at most 127 bytes; a rebuild from a 200-character name writes at most 127.</summary>
    [Fact]
    public void Cff01_BuiltName_AtMost127Bytes()
    {
        var cff = CffBuilder.Build(new string('N', 200), Cff01Thousandth, [("g1", Cff01Square(500), 600)]);
        Assert.InRange(Cff01Parts(cff).Names[0].Length, 1, 127);
    }

    /// <summary>TN 5176, 7 sentences 12–14: a name beginning with a zero byte (a deleted font) — PDF-Knead reads the glyphs anyway (lenient, today's behaviour).</summary>
    [Fact]
    public void Cff01_NameBeginningWithZero_GlyphsReadAnyway()
    {
        var font = CffFont.Parse(Cff01Build(new Cff01Spec
        {
            Names = [[0, 0x41]],
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
        }));
        Assert.Equal("M 0 0 | L 10 0 | Z", Cff01Path(font.Outline(1)));
    }

    /// <summary>
    /// TN 5176, 9 sentences 11–15 and 19–20: BaseFontName (12 22) and BaseFontBlend (12 23) stay when the font is scrubbed; the name still
    /// points to the same string although the custom strings are numbered anew.
    /// </summary>
    [Fact]
    public void Cff01_BaseFontNameAndBlend_KeptWhenScrubbed()
    {
        var cff = Cff01Build(new Cff01Spec
        {
            Strings = ["weg", "Basis-Probe"],
            TopExtra = Cff01Dict((1222, [392]), (1223, [0, 10, -5])),
            Charset = [0, 0x01, 0x87, 0x01, 0x87],   // format 0: glyphs 1 and 2 named by SID 391 ("weg")
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar"), Cff01Cs("endchar")],
        });
        var scrubbed = CffScrubber.Scrub(cff, []).Data;
        var top = Cff01Top(scrubbed);
        Assert.Equal("Basis-Probe", Cff01String(scrubbed, top.Int(1222)!.Value));
        Assert.Equal(new double[] { 0, 10, -5 }, top.Get(1223));
    }

    /// <summary>
    /// TN 5176, 9 sentence 16: the PostScript operator (12 21) carries code for printers. PDF-Knead runs none: the glyphs look as without it;
    /// scrubbing keeps the string in the program (today's behaviour).
    /// </summary>
    [Fact]
    public void Cff01_PostScriptOperator_NotRunAndKeptWhenScrubbed()
    {
        const string code = "/OrigFontType /Type1 def";
        byte[][] glyphs = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")];
        var with = Cff01Build(new Cff01Spec { Strings = [code], TopExtra = Cff01Dict((1221, [391])), CharStrings = glyphs });
        var without = Cff01Build(new Cff01Spec { CharStrings = glyphs });
        Assert.Equal(Cff01Path(CffFont.Parse(without).Outline(1)), Cff01Path(CffFont.Parse(with).Outline(1)));
        var scrubbed = CffScrubber.Scrub(with, [1]).Data;
        Assert.True(scrubbed.AsSpan().IndexOf(Encoding.ASCII.GetBytes(code)) >= 0);
    }

    /// <summary>
    /// TN 5176, 9 sentences 34–36 and 18 sentences 4–6: the Top DICT of a CIDFont begins with ROS — in the output of the subset, the CID
    /// rebuild and the scrub of Noto CJK the first entry is 12 30 with two SIDs and the supplement.
    /// </summary>
    [Fact]
    public void Cff01_CidTopDict_BeginsWithRos()
    {
        var noto = Cff01Noto.Value;
        var outputs = new[]
        {
            CffSubsetter.Subset(Cff01Glyphs([Cff01Cs("endchar")]), [1]),
            CffBuilder.BuildCid("Probe", Cff01Thousandth, "Adobe", "Japan1", 6, [(1, Cff01Square(500), 600)]),
            CffScrubber.Scrub(noto, [1]).Data,
        };
        foreach (var cff in outputs)
        {
            var (op, operands) = Cff01Ops(Cff01Parts(cff).Tops[0])[0];
            Assert.Equal(1230, op);
            Assert.Equal(3, operands.Length);
            Assert.True(operands[0] >= 391 && operands[1] >= 391);
        }
        var built = Cff01Ops(Cff01Parts(outputs[1]).Tops[0])[0].Operands;
        Assert.Equal(("Adobe", "Japan1", 6.0), (Cff01String(outputs[1], (int)built[0]), Cff01String(outputs[1], (int)built[1]), built[2]));
    }

    /// <summary>TN 5176, 10 sentences 10–12: a charset SID without a string (65 000) gives a glyph without a name, no error.</summary>
    [Fact]
    public void Cff01_SidWithoutString_GlyphWithoutName()
    {
        var font = CffFont.Parse(Cff01Build(new Cff01Spec
        {
            Charset = [0, 0xFD, 0xE8],
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
        }));
        Assert.Equal("", font.GlyphName(1));
        Assert.False(font.Outline(1).IsEmpty);
    }

    /// <summary>
    /// TN 5176, 10 sentence 13: a scrubbed font whose glyphs have standard names only, without Notice, has the empty String INDEX 00 00 — the
    /// custom string no glyph names leaves it. (A removed glyph would bring its neutral name as a custom string, question 9.)
    /// </summary>
    [Fact]
    public void Cff01_ScrubbedWithStandardNamesOnly_EmptyStringIndex()
    {
        var cff = Cff01Build(new Cff01Spec
        {
            Strings = ["eigener"],
            Charset = [0, 0, 34, 0, 35],   // glyph 1 "A" (SID 34), glyph 2 "B" (SID 35); the custom string names no glyph
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar"), Cff01Cs(0, 0, "rmoveto", 0, 10, "rlineto", "endchar")],
        });
        var scrubbed = CffScrubber.Scrub(cff, [1, 2]).Data;
        Assert.Empty(Cff01Parts(scrubbed).Strings);
        var at = Cff01Parts(scrubbed).GlobalAt;
        Assert.Equal(new byte[] { 0, 0 }, scrubbed[(at - 2)..at]);
        Assert.Equal("A", CffFont.Parse(scrubbed).GlyphName(1));
    }

    /// <summary>TN 5176, 12 sentences 17–29: encoding format 1 with the ranges (51, 4) and (65, 1) — codes 51–55 to glyphs 1–5, 65–66 to 6–7.</summary>
    [Fact]
    public void Cff01_EncodingFormat1_RangesToConsecutiveGlyphs()
    {
        var font = CffFont.Parse(Cff01Build(new Cff01Spec
        {
            Encoding = [1, 2, 51, 4, 65, 1],
            CharStrings = [.. Enumerable.Repeat(new byte[] { 14 }, 8)],
        }));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, font.Encoding[51..56]);
        Assert.Equal(new[] { 6, 7 }, font.Encoding[65..67]);
        Assert.Equal(0, font.Encoding[56]);
        Assert.Equal(0, font.Encoding[67]);
    }

    /// <summary>TN 5176, 12 sentence 44: in a rebuilt font glyph 0 is an empty .notdef of width 0, glyph 1 carries the smallest code.</summary>
    [Fact]
    public void Cff01_BuiltFont_NotdefFirstThenGlyphsByCode()
    {
        var font = CffFont.Parse(CffBuilder.Build("Probe", Cff01Thousandth, [("g65", Cff01Square(500), 600), ("g66", Cff01Square(300), 400)], [65, 66]));
        Assert.True(font.Outline(0).IsEmpty);
        Assert.Equal(0, font.AdvanceWidth(0));
        Assert.Equal(".notdef", font.GlyphName(0));
        Assert.Equal(1, font.Encoding[65]);
        Assert.Equal(2, font.Encoding[66]);
    }

    /// <summary>
    /// TN 5176, 12 sentences 45–55 and Appendix B sentences 18–31: the predefined encoding 1 is Expert Encoding — a glyph named "Asmall" gets
    /// code 97; spot checks of the table.
    /// </summary>
    [Fact]
    public void Cff01_PredefinedEncoding1_IsExpertEncoding()
    {
        var asmall = Array.IndexOf(FontTables.CffStandardStrings, "Asmall");
        Assert.True(asmall > 0);
        var font = CffFont.Parse(Cff01Build(new Cff01Spec
        {
            Charset = [0, (byte)(asmall >> 8), (byte)asmall],
            EncodingId = 1,
            CharStrings = [[14], [14]],
        }));
        Assert.Equal(1, font.Encoding[97]);
        Assert.Equal(256, FontTables.ExpertEncoding.Length);
        Assert.Equal("space", FontTables.ExpertEncoding[32]);
        Assert.Equal("Asmall", FontTables.ExpertEncoding[97]);
        Assert.Equal("", FontTables.ExpertEncoding[0]);
    }

    /// <summary>
    /// TN 5176, 13 sentences 39–48 and Appendix C sentences 2–27: the predefined charsets — without operator 15 ISOAdobe (glyph 1 "space",
    /// 228 "zcaron"), 1 Expert (2 "exclamsmall", 165 "Ydieresissmall"), 2 ExpertSubset (2 "dollaroldstyle", 86 "commainferior"); glyph 0 is
    /// .notdef. The tables have 166 and 87 entries.
    /// </summary>
    [Fact]
    public void Cff01_PredefinedCharsets_NameTheGlyphs()
    {
        CffFont Font(int? charset, int glyphs) =>
            CffFont.Parse(Cff01Build(new Cff01Spec { CharsetId = charset, CharStrings = [.. Enumerable.Repeat(new byte[] { 14 }, glyphs)] }));
        var iso = Font(null, 229);
        Assert.Equal((".notdef", "space", "zcaron"), (iso.GlyphName(0), iso.GlyphName(1), iso.GlyphName(228)));
        Assert.Equal("space", Font(0, 2).GlyphName(1));
        var expert = Font(1, 166);
        Assert.Equal(("space", "exclamsmall", "Ydieresissmall"), (expert.GlyphName(1), expert.GlyphName(2), expert.GlyphName(165)));
        var subset = Font(2, 87);
        Assert.Equal(("dollaroldstyle", "commainferior"), (subset.GlyphName(2), subset.GlyphName(86)));
        Assert.Equal(166, FontTables.ExpertCharset.Length);
        Assert.Equal(87, FontTables.ExpertSubsetCharset.Length);
        Assert.Equal(".notdef", FontTables.ExpertCharset[0]);
        Assert.Equal(".notdef", FontTables.ExpertSubsetCharset[0]);
    }

    /// <summary>TN 5176, 14 sentences 5–8: the scrubber refuses a CFF whose CharstringType is 1 (Type 1 charstrings) as a format error.</summary>
    [Fact]
    public void Cff01_CharstringType1_RefusedByTheScrubber()
    {
        var cff = Cff01Build(new Cff01Spec { TopExtra = Cff01Dict((1206, [1])), CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", "endchar")] });
        Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(cff, [1]));
    }

    /// <summary>
    /// TN 5176, 15 sentences 1–17: the width of a glyph is nominalWidthX plus the first number of its charstring, without one defaultWidthX.
    /// In FoxitSymbol.cff "alpha" begins with −52 at nominalWidthX 683 (631); a glyph without a width in its charstring has defaultWidthX 549.
    /// </summary>
    [Fact]
    public void Cff01_FoxitSymbolWidths_FromNominalOrDefault()
    {
        var data = EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        var font = CffFont.Parse(data);
        var priv = Cff01FontDictOf(data, 0).Private;
        Assert.Equal(683, priv.Single(e => e.Op == 21).Operands[0]);
        Assert.Equal(549, priv.Single(e => e.Op == 20).Operands[0]);
        Assert.Equal(631, font.AdvanceWidth(font.GlyphByName("alpha")));
        // a glyph whose first stack-clearing operator has no extra argument takes the default width
        var charStrings = Cff01CharStrings(data);
        var without = Enumerable.Range(1, charStrings.Length - 1).First(g => Cff01HasWidth(charStrings[g]) == false);
        Assert.Equal(549, font.AdvanceWidth(without));
    }

    /// <summary>Whether a charstring begins with a width (one argument more than its first operator takes); null when a subroutine call comes first.</summary>
    private static bool? Cff01HasWidth(byte[] cs)
    {
        var count = 0;
        for (var i = 0; i < cs.Length;)
        {
            int b0 = cs[i];
            if (b0 >= 32 || b0 == 28) { count++; i += b0 == 28 ? 3 : b0 <= 246 ? 1 : b0 <= 254 ? 2 : 5; continue; }
            return b0 switch
            {
                1 or 3 or 18 or 23 or 19 or 20 => count % 2 == 1,
                21 => count > 2,
                22 or 4 => count > 1,
                14 => count is 1 or 5,
                _ => null,   // a subroutine call or another operator first: not decided here
            };
        }
        return false;
    }

    /// <summary>TN 5176, 15 sentences 15–17: the rebuild writes each width — 500, 667 and 0 read back as written.</summary>
    [Fact]
    public void Cff01_BuiltWidths_ReadBack()
    {
        var font = CffFont.Parse(CffBuilder.Build("Probe", Cff01Thousandth,
            [("a", Cff01Square(500), 500), ("b", Cff01Square(500), 667), ("c", Cff01Square(500), 0)]));
        Assert.Equal((500.0, 667.0, 0.0), (font.AdvanceWidth(1), font.AdvanceWidth(2), font.AdvanceWidth(3)));
    }

    /// <summary>
    /// TN 5176, 15 sentence 20: a Private DICT of length 0 gives the defaults (every width 0 + the charstring's); a subset of a source without
    /// a Private operator still writes Private (0, offset) in its Font DICT.
    /// </summary>
    [Fact]
    public void Cff01_PrivateOfLengthZero_DefaultsAndAlwaysWritten()
    {
        var empty = CffFont.Parse(Cff01Build(new Cff01Spec { Private = [], CharStrings = [[14], Cff01Cs(250, 0, 0, "rmoveto", "endchar"), Cff01Cs("endchar")] }));
        Assert.Equal((250.0, 0.0), (empty.AdvanceWidth(1), empty.AdvanceWidth(2)));
        var none = Cff01Build(new Cff01Spec { Private = null, CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")] });
        Assert.False(Cff01Top(none).Has(18));
        var subset = CffSubsetter.Subset(none, [1]);
        var fontDict = Cff01FontDictOf(subset, 1).Font;
        var priv = fontDict.Single(e => e.Op == 18).Operands;
        Assert.Equal(0, priv[0]);
        Assert.True(priv[1] > 0);
    }

    /// <summary>
    /// TN 5176, 12 sentences 43 (FG 1156): a supplement adds a code to a glyph the main encoding table already encodes — a custom encoding
    /// (A: 65 and, as a supplement, 97; B: 66) scrubbed to keep A: the main table encodes A with 65, 97 stays a supplement, B's 66 is gone.
    /// </summary>
    [Fact]
    public void Cff01_ScrubbedCustomEncoding_MainTableWithSupplementsOnTop()
    {
        var cff = Cff01Build(new Cff01Spec
        {
            CharStrings = [[14], [14], [14]], Charset = [0, 0, 34, 0, 35], Encoding = [0x80, 2, 65, 66, 1, 97, 0, 34],
        });
        var (scrubbed, _) = CffScrubber.Scrub(cff, [1]);
        var font = CffFont.Parse(scrubbed);
        Assert.Equal((1, 1, 0), (font.Encoding[65], font.Encoding[97], font.Encoding[66]));
        var reader = new FontReader(scrubbed);
        reader.U8();
        reader.U8();
        reader.Position = reader.U8();
        _ = CffFont.CffIndex.Read(scrubbed, ref reader);
        var tops = CffFont.CffIndex.Read(scrubbed, ref reader);
        var at = CffFont.Dict.Parse(tops.Item(scrubbed, 0)).Int(16)!.Value;
        Assert.Equal(0x80, scrubbed[at] & 0x80);   // with supplements
        Assert.Equal(1, scrubbed[at + 1]);         // a main table of one glyph
        Assert.Equal(65, scrubbed[at + 2]);
    }

    /// <summary>
    /// TN 5176, 11 sentences 5–7: GID 0 is always .notdef (CID 0), the charset begins with GID 1 — the subset (CID-keyed, its charset holds the
    /// CIDs, which are the glyph indices of the source) of a font with A (GID 1) and B (GID 2) that keeps only B: two glyphs, GID 0 is CID 0,
    /// GID 1 is CID 2 (B), and no other glyph claims CID 0.
    /// </summary>
    [Fact]
    public void Cff01_Subset_NotdefAtGid0_CharsetFromGid1()
    {
        var cff = Cff01Build(new Cff01Spec { CharStrings = [[14], [14], [14]], Charset = [0, 0, 34, 0, 35] });
        var subset = CffFont.Parse(CffSubsetter.Subset(cff, [2]));
        Assert.True(subset.IsCidKeyed);
        Assert.Equal(2, subset.GlyphCount);
        Assert.Equal(0, subset.GlyphByCid(0));
        Assert.Equal(1, subset.GlyphByCid(2));
    }

    /// <summary>
    /// TN 5177, 4.5 (writer): a charstring with put/get cannot be flattened — the subset and the scrubber refuse it (FontFormatException);
    /// what happens to such a program when redacting is question 6 (the reader draws it: <c>T201_TransientArray_PerGlyph</c>).
    /// </summary>
    [Fact]
    public void Cff01_PutGet_SubsetAndScrubberRefuse()
    {
        var cff = Cff01Glyphs([Cff01Cs(25, 3, "put", 3, "get", 0, "rmoveto", 1, 0, "rlineto", "endchar")]);
        Assert.Throws<FontFormatException>(() => CffSubsetter.Subset(cff, [1]));
        Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(cff, [1]));
    }

    /// <summary>
    /// TN 5176, 3 sentences 7–14 (Table 2: OffSize is 1 to 4): an INDEX whose OffSize is 0 or 5 is refused (FontFormatException), one of 1 reads
    /// its single item.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Cff01_IndexOffSizeOutside1To4_IsRefused(int offSize)
    {
        byte[] data = [0, 1, (byte)offSize, 1, 2, 0x41];
        Assert.Throws<FontFormatException>(() =>
        {
            var reader = new FontReader(data);
            CffFont.CffIndex.Read(data, ref reader);
        });
        byte[] valid = [0, 1, 1, 1, 2, 0x41];
        var r = new FontReader(valid);
        var index = CffFont.CffIndex.Read(valid, ref r);
        Assert.Equal([0x41], index.Item(valid, 0).ToArray());
    }

    // ------------------------------------------------------------ CFF 11 sentence 3: unique names and CIDs after scrubbing (question 9)

    /// <summary>The Font DICT index FDSelect (format 0, as the scrubber writes it) gives a glyph of a CID-keyed CFF.</summary>
    private static int Cff01FdOf(byte[] cff, int glyph)
    {
        var at = Cff01Top(cff).Int(1237)!.Value;
        Assert.Equal(0, cff[at]);
        return cff[at + 1 + glyph];
    }

    /// <summary>
    /// TN 5176, 11 sentences 1–4 (redaction, question 9, docs/FEHLERGEDAECHTNIS.md, No. 1158): the names in the charset are unique — the scrubbed
    /// symbol font names every removed glyph „removed.“ and its glyph index (no more SID 0, the name of .notdef, for all of them): all names
    /// differ, none of a removed glyph maps to a character, and the names of the removed glyphs are gone.
    /// </summary>
    [Fact]
    public void Cff01_ScrubbedNameKeyed_RemovedGlyphsGetUniqueNeutralNames()
    {
        var data = EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        var font = CffFont.Parse(data);
        var kept = new[] { "alpha", "beta", "Sigma" }.Select(font.GlyphByName).ToList();
        var (bytes, keep) = CffScrubber.Scrub(data, kept);
        var scrubbed = CffFont.Parse(bytes);
        var names = Enumerable.Range(0, scrubbed.GlyphCount).Select(scrubbed.GlyphName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        for (var g = 1; g < font.GlyphCount; g++)
        {
            if (keep.Contains(g))
            {
                Assert.Equal(font.GlyphName(g), scrubbed.GlyphName(g));
                continue;
            }
            Assert.Equal("removed." + g.ToString(CultureInfo.InvariantCulture), scrubbed.GlyphName(g));
            Assert.Null(GlyphNames.ToUnicode(scrubbed.GlyphName(g)!));
            Assert.Equal(-1, scrubbed.GlyphByName(font.GlyphName(g)!));
        }
        Assert.Equal(-1, bytes.AsSpan().IndexOf("gamma"u8));
    }

    /// <summary>
    /// TN 5176, 11 sentences 1–4 (redaction, question 9): a removed glyph whose neutral name a kept glyph already has gets „.x“ appended — a kept
    /// glyph named „removed.2“ and the removed glyph 2: „removed.2.x“, the names stay unique.
    /// </summary>
    [Fact]
    public void Cff01_ScrubbedNameKeyed_NeutralNameOfAKeptGlyph_IsExtended()
    {
        var data = CffBuilder.Build("Probe", Cff01Thousandth, [("removed.2", Cff01Square(100), 500), ("b", Cff01Square(200), 500)]);
        var scrubbed = CffFont.Parse(CffScrubber.Scrub(data, [1]).Data);
        Assert.Equal("removed.2", scrubbed.GlyphName(1));
        Assert.Equal("removed.2.x", scrubbed.GlyphName(2));
        Assert.Equal(1, scrubbed.GlyphByName("removed.2"));
    }

    /// <summary>
    /// TN 5176, 11 sentences 1–4 (redaction, question 9): the CIDs in the charset of a CID-keyed font are unique — a subset of Noto Sans CJK (CID =
    /// glyph index of the full font) scrubbed down to 漢字A: each removed glyph (秘密Z) gets its own glyph index as CID, not CID 0 and not its
    /// original CID (which in a subset tells the character); the original CIDs reach no glyph any more, the kept ones theirs; a removed glyph
    /// takes the Font DICT of glyph 0 (its own would tell the kind of glyph); CIDCount covers the CIDs.
    /// </summary>
    [Fact]
    public void Cff01_ScrubbedCidKeyed_RemovedGlyphsGetUniqueNeutralCids()
    {
        var noto = Cff01NotoFont.Value;
        var all = "漢字A秘密Z".Select(c => noto.GlyphForUnicode(c)).ToList();
        var subset = CffSubsetter.Subset(Cff01Noto.Value, all);
        var font = CffFont.Parse(subset);
        var kept = "漢字A".Select(c => font.GlyphByCid(noto.GlyphForUnicode(c))).ToList();
        var removed = "秘密Z".Select(c => font.GlyphByCid(noto.GlyphForUnicode(c))).ToList();
        Assert.All(kept.Concat(removed), g => Assert.True(g > 0));
        Assert.Contains(removed, g => Cff01FdOf(subset, g) != Cff01FdOf(subset, 0));   // a removed glyph had a Font DICT of its own
        var bytes = CffScrubber.Scrub(subset, kept).Data;
        var scrubbed = CffFont.Parse(bytes);
        var cids = Enumerable.Range(0, scrubbed.GlyphCount).Select(scrubbed.CidOf).ToList();
        Assert.Equal(cids.Count, cids.Distinct().Count());
        foreach (var g in kept)
        {
            Assert.Equal(font.CidOf(g), scrubbed.CidOf(g));
            Assert.Equal(g, scrubbed.GlyphByCid(font.CidOf(g)));
            Assert.Equal(Cff01FdOf(subset, g), Cff01FdOf(bytes, g));
        }
        foreach (var g in removed)
        {
            Assert.Equal(g, scrubbed.CidOf(g));
            Assert.Equal(-1, scrubbed.GlyphByCid(font.CidOf(g)));
            Assert.True(scrubbed.Outline(g).IsEmpty);
            Assert.Equal(Cff01FdOf(bytes, 0), Cff01FdOf(bytes, g));
        }
        Assert.True((Cff01Top(bytes).Int(1234) ?? 8720) > cids.Max());
    }

    /// <summary>
    /// TN 5176, 11 sentences 1–4 (redaction of a page, question 9): the neutral CID of a removed glyph is not one the page still shows — a subset
    /// of Noto Sans CJK (CIDs 漢 and 秘 of the full font) under CIDFontType0 shows 漢, 秘 and the CID equal to the glyph index of 秘 (no glyph: it
    /// draws .notdef); redacting 秘 scrubs the program, and the removed glyph takes another free CID, so the shown CID keeps drawing .notdef — had
    /// it taken that CID, the shown code would have drawn the empty glyph, the check after scrubbing would have refused the program, and the
    /// glyph of the redacted character would have stayed in the file.
    /// </summary>
    [Fact]
    public void Cff01_ScrubbedOnAPage_NeutralCidAvoidsAShownCid()
    {
        var noto = Cff01NotoFont.Value;
        int han = noto.GlyphForUnicode('漢'), secret = noto.GlyphForUnicode('秘');
        var subset = CffSubsetter.Subset(Cff01Noto.Value, [han, secret]);
        var parsed = CffFont.Parse(subset);
        var secretGlyph = parsed.GlyphByCid(secret);
        Assert.Equal(-1, parsed.GlyphByCid(secretGlyph));
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(TestPdf.FlateStream(subset, TestPdf.Dict(("Subtype", TestPdf.N("CIDFontType0C")))));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Muster")), ("Flags", Core.Pdf.Objects.PdfInteger.Of(4)),
            ("FontBBox", TestPdf.Box(0, -200, 1000, 900)), ("ItalicAngle", Core.Pdf.Objects.PdfInteger.Of(0)), ("Ascent", Core.Pdf.Objects.PdfInteger.Of(900)),
            ("Descent", Core.Pdf.Objects.PdfInteger.Of(-200)), ("CapHeight", Core.Pdf.Objects.PdfInteger.Of(700)), ("StemV", Core.Pdf.Objects.PdfInteger.Of(80)),
            ("FontFile3", TestPdf.Ref(file))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Muster")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new Core.Pdf.Objects.PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")),
                ("Subtype", TestPdf.N("CIDFontType0")), ("BaseFont", TestPdf.N("ABCDEF+Muster")),
                ("CIDSystemInfo", TestPdf.Dict(("Registry", new Core.Pdf.Objects.PdfString("Adobe"u8.ToArray())),
                    ("Ordering", new Core.Pdf.Objects.PdfString("Identity"u8.ToArray())), ("Supplement", Core.Pdf.Objects.PdfInteger.Of(0)))),
                ("FontDescriptor", TestPdf.Ref(descriptor)))]))));
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(2)).GetDictionary(Core.Pdf.Objects.PdfName.Resources)!.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font"))!
            .Set(Core.Pdf.Objects.PdfName.Get("F1"), TestPdf.Ref(font));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 24 Tf 72 600 Td <{han:X4}> Tj 100 0 Td <{secret:X4}> Tj 100 0 Td <{secretGlyph:X4}> Tj ET"))));
        using var doc = Core.Pdf.Document.PdfDocument.Open(pdf.Build());
        using var after = Core.Pdf.Document.PdfDocument.Open(RedactionTests.WithBars(doc, (0, new Core.Pdf.Document.PdfRectangle(165, 590, 200, 630))));
        var stream = after.Pages[0].Resources!.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font"))!.GetDictionary(Core.Pdf.Objects.PdfName.Get("F1"))!
            .GetArray(Core.Pdf.Objects.PdfName.Get("DescendantFonts"))!.Get(0) is Core.Pdf.Objects.PdfDictionary cid
            ? cid.GetDictionary(Core.Pdf.Objects.PdfName.Get("FontDescriptor"))!.GetStream(Core.Pdf.Objects.PdfName.Get("FontFile3"))!
            : throw new InvalidOperationException("kein CIDFont");
        var scrubbed = CffFont.Parse(after.GetStreamData(stream));
        Assert.True(scrubbed.Outline(secretGlyph).IsEmpty);
        Assert.False(scrubbed.Outline(scrubbed.GlyphByCid(han)).IsEmpty);
        Assert.Equal(-1, scrubbed.GlyphByCid(secret));
        Assert.NotEqual(0, scrubbed.CidOf(secretGlyph));
        Assert.Equal(-1, scrubbed.GlyphByCid(secretGlyph));
    }
}
