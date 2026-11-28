using System.Text;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Builds a bare, name-keyed CFF font (Adobe Technical Note #5176, Type 2 charstrings: #5177) from glyph outlines — for PDF/A, where a Type 1
/// or CFF font of a file is rebuilt from what the display draws (KONZEPT.md, section 5.21). No hints: the outlines are written exactly
/// (coordinates as 16.16 fixed point where they are not whole), the advance of each glyph as given, glyph 0 an empty .notdef.
/// </summary>
internal static class CffBuilder
{
    /// <summary>The first string ID of the font's own strings (after the 391 standard strings).</summary>
    private const int FirstOwnString = 391;

    /// <summary>Builds the font.</summary>
    /// <param name="fontName">The font name (Name INDEX); only printable ASCII is kept.</param>
    /// <param name="fontMatrix">The font matrix (glyph space → text space).</param>
    /// <param name="glyphs">The glyphs after .notdef: name, outline in glyph space, advance in glyph space.</param>
    /// <param name="codes">A custom encoding (format 0): the code of each glyph after .notdef, in their order; null: the standard encoding.</param>
    /// <returns>The CFF data.</returns>
    public static byte[] Build(string fontName, Matrix fontMatrix, IReadOnlyList<(string Name, GlyphOutline Outline, double Advance)> glyphs,
        IReadOnlyList<byte>? codes = null)
    {
        var charStrings = new List<byte[]> { CharString(GlyphOutline.Empty, 0) };
        foreach (var glyph in glyphs) charStrings.Add(CharString(glyph.Outline, glyph.Advance));
        var name = new string(fontName.Where(c => c is > ' ' and < (char)127 and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%')).Take(127).ToArray());   // at most 127 bytes (TN 5176, 7; No. 1148)
        var names = CffSubsetter.Index([Encoding.ASCII.GetBytes(name.Length > 0 ? name : "Font")]);
        var strings = CffSubsetter.Index([.. glyphs.Select(g => Encoding.ASCII.GetBytes(g.Name))]);
        var globalSubrs = CffSubsetter.Index([]);
        var charset = new List<byte> { 0 };
        for (var i = 0; i < glyphs.Count; i++) CffSubsetter.AddU16(charset, FirstOwnString + i);
        byte[] encoding = codes is null ? [] : [0, (byte)codes.Count, .. codes];
        var glyphIndex = CffSubsetter.Index(charStrings);
        var privateDict = new List<byte>();
        CffSubsetter.Operands(privateDict, [0]);
        CffSubsetter.Op(privateDict, 20);   // defaultWidthX 0 (nominalWidthX is 0 as well): every width is written

        var (left, bottom, right, top) = Box(glyphs.Select(g => g.Outline));

        // Top DICT: its offsets as 5-byte numbers, so its size does not depend on them
        List<byte> TopDict(int charsetAt, int encodingAt, int charStringsAt, int privateAt)
        {
            var d = new List<byte>();
            if (fontMatrix != new Matrix(0.001, 0, 0, 0.001, 0, 0))
            {
                CffSubsetter.Operands(d, [fontMatrix.A, fontMatrix.B, fontMatrix.C, fontMatrix.D, fontMatrix.E, fontMatrix.F]);
                CffSubsetter.Op(d, 1207);
            }
            CffSubsetter.Operands(d, [Math.Floor(left), Math.Floor(bottom), Math.Ceiling(right), Math.Ceiling(top)]);
            CffSubsetter.Op(d, 5);
            CffSubsetter.Fixed(d, charsetAt);
            CffSubsetter.Op(d, 15);
            if (encoding.Length > 0)
            {
                CffSubsetter.Fixed(d, encodingAt);
                CffSubsetter.Op(d, 16);
            }
            CffSubsetter.Fixed(d, charStringsAt);
            CffSubsetter.Op(d, 17);
            CffSubsetter.Fixed(d, privateDict.Count);
            CffSubsetter.Fixed(d, privateAt);
            CffSubsetter.Op(d, 18);
            return d;
        }
        var topSize = CffSubsetter.Index([[.. TopDict(0, 0, 0, 0)]]).Length;
        var charsetAt = 4 + names.Length + topSize + strings.Length + globalSubrs.Length;
        var encodingAt = charsetAt + charset.Count;
        var charStringsAt = encodingAt + encoding.Length;
        var privateAt = charStringsAt + glyphIndex.Length;
        var topIndex = CffSubsetter.Index([[.. TopDict(charsetAt, encodingAt, charStringsAt, privateAt)]]);

        var output = new MemoryStream(privateAt + privateDict.Count);
        output.Write([1, 0, 4, 4]);
        output.Write(names);
        output.Write(topIndex);
        output.Write(strings);
        output.Write(globalSubrs);
        output.Write(charset.ToArray());
        output.Write(encoding);
        output.Write(glyphIndex);
        output.Write(privateDict.ToArray());
        return output.ToArray();
    }

    /// <summary>
    /// Builds a CID-keyed CFF font (FontFile3 /CIDFontType0C) from glyph outlines: one Font DICT carrying the font matrix (the Top DICT has the
    /// identity, the usual arrangement), glyph 0 an empty .notdef, then one glyph per CID in ascending order.
    /// </summary>
    /// <param name="fontName">The font name; only printable ASCII is kept.</param>
    /// <param name="fontMatrix">The font matrix (glyph space → text space).</param>
    /// <param name="registry">Registry of the character collection (ROS).</param>
    /// <param name="ordering">Ordering of the character collection.</param>
    /// <param name="supplement">Supplement of the character collection.</param>
    /// <param name="glyphs">The glyphs: CID (ascending, above 0), outline in glyph space, advance in glyph space. An entry of CID 0 gives the
    /// outline of .notdef instead of an empty one (redaction rebuilds a font whose shown CID 0 draws something; docs/FEHLERGEDAECHTNIS.md, No. 1164).</param>
    /// <returns>The CFF data.</returns>
    public static byte[] BuildCid(string fontName, Matrix fontMatrix, string registry, string ordering, int supplement,
        IReadOnlyList<(int Cid, GlyphOutline Outline, double Advance)> glyphs)
    {
        var notdef = glyphs.Where(g => g.Cid == 0).Select(g => ((GlyphOutline, double)?)(g.Outline, g.Advance)).FirstOrDefault();
        var all = glyphs;
        glyphs = [.. glyphs.Where(g => g.Cid != 0)];
        var charStrings = new List<byte[]> { notdef is var (notdefOutline, notdefAdvance) ? CharString(notdefOutline, notdefAdvance) : CharString(GlyphOutline.Empty, 0) };
        foreach (var glyph in glyphs) charStrings.Add(CharString(glyph.Outline, glyph.Advance));
        var name = new string(fontName.Where(c => c is > ' ' and < (char)127 and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%')).Take(127).ToArray());   // at most 127 bytes (TN 5176, 7; No. 1148)
        var names = CffSubsetter.Index([Encoding.ASCII.GetBytes(name.Length > 0 ? name : "Font")]);
        var strings = CffSubsetter.Index([Encoding.ASCII.GetBytes(registry), Encoding.ASCII.GetBytes(ordering)]);
        var globalSubrs = CffSubsetter.Index([]);
        var charset = new List<byte> { 0 };
        foreach (var glyph in glyphs) CffSubsetter.AddU16(charset, glyph.Cid);
        var fdSelect = new List<byte> { 3 };
        CffSubsetter.AddU16(fdSelect, 1);
        CffSubsetter.AddU16(fdSelect, 0);
        fdSelect.Add(0);
        CffSubsetter.AddU16(fdSelect, charStrings.Count);
        var glyphIndex = CffSubsetter.Index(charStrings);
        var privateDict = new List<byte>();
        CffSubsetter.Operands(privateDict, [0]);
        CffSubsetter.Op(privateDict, 20);
        var (left, bottom, right, top) = Box(all.Select(g => g.Outline));

        List<byte> FontDict(int privateAt)
        {
            var d = new List<byte>();
            CffSubsetter.Operands(d, [fontMatrix.A, fontMatrix.B, fontMatrix.C, fontMatrix.D, fontMatrix.E, fontMatrix.F]);
            CffSubsetter.Op(d, 1207);
            CffSubsetter.Fixed(d, privateDict.Count);
            CffSubsetter.Fixed(d, privateAt);
            CffSubsetter.Op(d, 18);
            return d;
        }
        List<byte> TopDict(int charsetAt, int fdSelectAt, int charStringsAt, int fdArrayAt)
        {
            var d = new List<byte>();
            CffSubsetter.Operands(d, [FirstOwnString, FirstOwnString + 1, supplement]);
            CffSubsetter.Op(d, 1230);   // ROS: first in the Top DICT of a CID-keyed font
            CffSubsetter.Operands(d, [glyphs.Count > 0 ? glyphs[^1].Cid + 1 : 1]);
            CffSubsetter.Op(d, 1234);   // CIDCount
            CffSubsetter.Operands(d, [1, 0, 0, 1, 0, 0]);
            CffSubsetter.Op(d, 1207);
            CffSubsetter.Operands(d, [Math.Floor(left), Math.Floor(bottom), Math.Ceiling(right), Math.Ceiling(top)]);
            CffSubsetter.Op(d, 5);
            CffSubsetter.Fixed(d, charsetAt);
            CffSubsetter.Op(d, 15);
            CffSubsetter.Fixed(d, charStringsAt);
            CffSubsetter.Op(d, 17);
            CffSubsetter.Fixed(d, fdArrayAt);
            CffSubsetter.Op(d, 1236);
            CffSubsetter.Fixed(d, fdSelectAt);
            CffSubsetter.Op(d, 1237);
            return d;
        }
        var topSize = CffSubsetter.Index([[.. TopDict(0, 0, 0, 0)]]).Length;
        var charsetAt = 4 + names.Length + topSize + strings.Length + globalSubrs.Length;
        var fdSelectAt = charsetAt + charset.Count;
        var charStringsAt = fdSelectAt + fdSelect.Count;
        var fdArrayAt = charStringsAt + glyphIndex.Length;
        var fdArraySize = CffSubsetter.Index([[.. FontDict(0)]]).Length;
        var privateAt = fdArrayAt + fdArraySize;
        var output = new MemoryStream(privateAt + privateDict.Count);
        output.Write([1, 0, 4, 4]);
        output.Write(names);
        output.Write(CffSubsetter.Index([[.. TopDict(charsetAt, fdSelectAt, charStringsAt, fdArrayAt)]]));
        output.Write(strings);
        output.Write(globalSubrs);
        output.Write(charset.ToArray());
        output.Write(fdSelect.ToArray());
        output.Write(glyphIndex);
        output.Write(CffSubsetter.Index([[.. FontDict(privateAt)]]));
        output.Write(privateDict.ToArray());
        return output.ToArray();
    }

    /// <summary>The bounding box of outlines (0 0 0 0 without any).</summary>
    private static (double Left, double Bottom, double Right, double Top) Box(IEnumerable<GlyphOutline> outlines)
    {
        double left = 0, bottom = 0, right = 0, top = 0;
        var any = false;
        foreach (var outline in outlines)
        {
            if (outline.IsEmpty) continue;
            var b = outline.Bounds;
            (left, bottom, right, top) = any
                ? (Math.Min(left, b.Left), Math.Min(bottom, Math.Min(b.Top, b.Bottom)), Math.Max(right, b.Right), Math.Max(top, Math.Max(b.Top, b.Bottom)))
                : (b.Left, Math.Min(b.Top, b.Bottom), b.Right, Math.Max(b.Top, b.Bottom));
            any = true;
        }
        return (left, bottom, right, top);
    }

    /// <summary>
    /// PDF/A (6.1.8: font names in UTF-8, strict readers take the name from the program as well): the font name of a CFF program — in its Name
    /// INDEX and where its strings repeat it — with every byte outside printable ASCII replaced by "_", of the same length, so no offset in the
    /// program changes. Null if there is nothing to replace.
    /// </summary>
    /// <param name="cff">The CFF data.</param>
    /// <returns>The data with the names replaced, or null.</returns>
    internal static byte[]? AsciiNames(byte[] cff)
    {
        if (cff.Length < 8 || cff[0] != 1) return null;
        // an INDEX: where its data begins, the offsets of its items, where it ends
        (int Data, int[] Offsets, int End)? Index(int at)
        {
            if (at + 2 > cff.Length) return null;
            var count = cff[at] << 8 | cff[at + 1];
            if (count == 0) return (at + 2, [1], at + 2);
            if (at + 3 > cff.Length) return null;
            var offSize = cff[at + 2];
            if (offSize is < 1 or > 4 || at + 3 + (count + 1) * offSize > cff.Length) return null;
            var offsets = new int[count + 1];
            for (var i = 0; i <= count; i++)
                for (var k = 0; k < offSize; k++) offsets[i] = offsets[i] << 8 | cff[at + 3 + i * offSize + k];
            var data = at + 3 + (count + 1) * offSize - 1;
            return data + offsets[^1] > cff.Length ? null : (data, offsets, data + offsets[^1]);
        }
        if (Index(cff[2]) is not var (nameData, names, namesEnd) || names.Length < 2) return null;
        var name = cff.AsSpan(nameData + names[0], names[1] - names[0]).ToArray();
        if (name.All(b => b is >= 0x21 and <= 0x7E)) return null;
        var result = (byte[])cff.Clone();
        void Replace(int from, int to)
        {
            for (var i = from; i < to; i++)
                if (result[i] is < 0x21 or > 0x7E) result[i] = (byte)'_';
        }
        Replace(nameData + names[0], nameData + names[1]);
        // the same name among the strings (the FontName of a CID-keyed font's dictionaries): replaced as well, glyph names stay
        if (Index(namesEnd) is var (_, _, topEnd) && Index(topEnd) is var (stringData, strings, _))
            for (var i = 0; i + 1 < strings.Length; i++)
                if (cff.AsSpan(stringData + strings[i], strings[i + 1] - strings[i]).SequenceEqual(name)) Replace(stringData + strings[i], stringData + strings[i + 1]);
        return result;
    }

    /// <summary>A Type 2 charstring: the advance, the outline as relative moves, lines and curves, endchar.</summary>
    private static byte[] CharString(GlyphOutline outline, double advance)
    {
        var d = new List<byte>();
        if (advance != 0) Number(d, advance);
        AppendPath(d, outline);
        d.Add(14);   // endchar
        // a charstring holds at most 65 535 bytes (Adobe TN 5177, Appendix B): a longer outline is not built (No. 1148)
        if (d.Count > 65535) throw new FontFormatException("CFF: Glyphe zu lang");
        return [.. d];
    }

    /// <summary>
    /// A CFF2 charstring of an outline (OpenType 8.10): the relative moves, lines and curves alone — no width, no endchar (No. 1158).
    /// </summary>
    /// <param name="outline">The outline in glyph space.</param>
    /// <returns>The charstring; empty for an empty outline.</returns>
    /// <exception cref="FontFormatException">The charstring would exceed 65 535 bytes.</exception>
    internal static byte[] Path(GlyphOutline outline)
    {
        var d = new List<byte>();
        AppendPath(d, outline);
        if (d.Count > 65535) throw new FontFormatException("CFF2: Glyphe zu lang");
        return [.. d];
    }

    /// <summary>The outline as relative moves, lines and curves (rmoveto, rlineto, rrcurveto).</summary>
    private static void AppendPath(List<byte> d, GlyphOutline outline)
    {
        double x = 0, y = 0;
        var points = outline.Points;
        var p = 0;
        // each coordinate as the step from the point reached so far, as it will be read back (no drift)
        double Step(double target, ref double current)
        {
            var delta = target - current;
            var encoded = Math.Abs(delta - Math.Round(delta)) < 1e-9 ? Math.Round(delta) : Math.Round(delta * 65536) / 65536;
            current += encoded;
            return encoded;
        }
        foreach (var verb in outline.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    Number(d, Step(points[p], ref x));
                    Number(d, Step(points[p + 1], ref y));
                    d.Add(21);   // rmoveto
                    p += 2;
                    break;
                case PathVerb.Line:
                    Number(d, Step(points[p], ref x));
                    Number(d, Step(points[p + 1], ref y));
                    d.Add(5);   // rlineto
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    for (var k = 0; k < 3; k++)
                    {
                        Number(d, Step(points[p + 2 * k], ref x));
                        Number(d, Step(points[p + 2 * k + 1], ref y));
                    }
                    d.Add(8);   // rrcurveto
                    p += 6;
                    break;
            }
        }
    }

    /// <summary>A charstring number: an integer in its short forms, otherwise 16.16 fixed point.</summary>
    internal static void Number(List<byte> d, double v)
    {
        if (v == Math.Round(v) && v is >= short.MinValue and <= short.MaxValue)
        {
            var n = (int)v;
            switch (n)
            {
                case >= -107 and <= 107:
                    d.Add((byte)(n + 139));
                    return;
                case >= 108 and <= 1131:
                    d.Add((byte)((n - 108) / 256 + 247));
                    d.Add((byte)((n - 108) % 256));
                    return;
                case >= -1131 and <= -108:
                    d.Add((byte)((-n - 108) / 256 + 251));
                    d.Add((byte)((-n - 108) % 256));
                    return;
                default:
                    d.Add(28);
                    d.Add((byte)(n >> 8));
                    d.Add((byte)n);
                    return;
            }
        }
        var fixedPoint = (int)Math.Clamp(Math.Round(v * 65536), int.MinValue, int.MaxValue);
        d.Add(255);
        d.Add((byte)(fixedPoint >> 24));
        d.Add((byte)(fixedPoint >> 16));
        d.Add((byte)(fixedPoint >> 8));
        d.Add((byte)fixedPoint);
    }
}
