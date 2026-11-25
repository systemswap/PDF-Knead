using System.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>What the PDF/A copy of a crafted file shows for its rule.</summary>
public enum RuleOutcome
{
    /// <summary>The copy meets the rule and conforms as a whole.</summary>
    Met,

    /// <summary>The copy is refused for this rule: the writer cannot meet it without losing what the page shows or holds.</summary>
    Refused,
}

/// <summary>A crafted file that breaks one rule, the part it is checked and written as, and what its copy shows.</summary>
/// <param name="File">Builds the file.</param>
/// <param name="Part">The part and level.</param>
/// <param name="Outcome">What the copy shows.</param>
/// <param name="Note">What the case is (shown when it fails).</param>
/// <param name="Look">Why the copy may look different from the original where the standard asks for it (null: it looks the same).</param>
public sealed record RuleCase(Func<byte[]> File, PdfAConformance Part = PdfAConformance.A2b, RuleOutcome Outcome = RuleOutcome.Met, string Note = "",
    string? Look = null)
{
    /// <summary>The test number the check reports where it is one of the rule's further ones (<see cref="PdfARule.Also"/>); null: its own.</summary>
    public string? Id { get; init; }
}

/// <summary>
/// One crafted file per rule of the PDF/A catalogue (docs/PDFA-REGELN.md, column „Testfall“): each breaks its rule and nothing else that
/// the copy could not mend. Built byte by byte where the rule is about the bytes (<see cref="Raw"/>).
/// </summary>
internal static class PdfARuleCases
{
    /// <summary>A page that draws a blue rectangle (no text: nothing needs a font).</summary>
    private const string Rectangle = "0 0 1 rg 72 600 200 100 re f";

    /// <summary>One object of a raw file: what stands before it, and its text from „n g obj“ on.</summary>
    internal sealed record RawObject(int Number, string Text, string Before = "\n");

    /// <summary>
    /// A file from objects given as text, byte by byte as written (Latin-1): header, objects at the positions the cross-references give,
    /// the cross-reference section, trailer and the end.
    /// </summary>
    internal static byte[] Raw(IEnumerable<RawObject> objects, string header = "%PDF-1.7\n%\xE2\xE3\xCF\xD3", string xrefKeyword = "xref\n",
        string trailerExtra = "/ID[<0102030405060708090A0B0C0D0E0F10><0102030405060708090A0B0C0D0E0F10>]", string end = "%%EOF\n", string subsectionSeparator = " ")
    {
        var text = new StringBuilder(header);
        var offsets = new SortedDictionary<int, int>();
        foreach (var o in objects)
        {
            text.Append(o.Before);
            offsets[o.Number] = Encoding.Latin1.GetByteCount(text.ToString());
            text.Append(o.Text);
        }
        text.Append('\n');
        var xref = Encoding.Latin1.GetByteCount(text.ToString());
        var max = offsets.Keys.Max();
        text.Append(xrefKeyword).Append($"0{subsectionSeparator}{max + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= max; i++) text.Append(offsets.TryGetValue(i, out var at) ? $"{at:D10} 00000 n \n" : "0000000000 65535 f \n");
        text.Append($"trailer\n<</Size {max + 1}/Root 1 0 R{trailerExtra}>>\nstartxref\n{xref}\n").Append(end);
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>An object as written: „n 0 obj“, line end, body, line end, „endobj“.</summary>
    internal static RawObject Obj(int number, string body) => new(number, $"{number} 0 obj\n{body}\nendobj");

    /// <summary>A stream object with the correct /Length.</summary>
    internal static RawObject Stream(int number, string dict, string data) =>
        new(number, $"{number} 0 obj\n<<{dict}/Length {Encoding.Latin1.GetByteCount(data)}>>\nstream\n{data}\nendstream\nendobj");

    /// <summary>
    /// The objects of a one-page file: catalogue (1, with <paramref name="catalogue"/>), pages (2), page (3, with <paramref name="page"/> and
    /// <paramref name="resources"/>), content (4); more objects from 5 on.
    /// </summary>
    internal static List<RawObject> Page(string content = Rectangle, string catalogue = "", string page = "", string resources = "", params RawObject[] more)
    {
        var list = new List<RawObject>
        {
            Obj(1, $"<</Type/Catalog/Pages 2 0 R{catalogue}>>"),
            Obj(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>"),
            Obj(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<{resources}>>{page}>>"),
            Stream(4, "", content),
        };
        list.AddRange(more);
        return list;
    }

    /// <summary>A file of <see cref="Page"/> with the objects changed by <paramref name="change"/>.</summary>
    private static Func<byte[]> File(Func<List<RawObject>, List<RawObject>>? change = null, string header = "%PDF-1.7\n%\xE2\xE3\xCF\xD3",
        string xrefKeyword = "xref\n", string trailerExtra = "/ID[<0102030405060708090A0B0C0D0E0F10><0102030405060708090A0B0C0D0E0F10>]",
        string end = "%%EOF\n") =>
        () => Raw(change is null ? Page() : change(Page()), header, xrefKeyword, trailerExtra, end);

    /// <summary>Replaces one object.</summary>
    private static List<RawObject> With(this List<RawObject> objects, RawObject replacement)
    {
        var copy = objects.Where(o => o.Number != replacement.Number).ToList();
        copy.Add(replacement);
        return copy.OrderBy(o => o.Number).ToList();
    }

    /// <summary>LZW with literal codes only (9 bits each between a clear code and the end code).</summary>
    internal static byte[] Lzw(byte[] data)
    {
        var bits = new List<bool>();
        void Code(int code)
        {
            for (var b = 8; b >= 0; b--) bits.Add(((code >> b) & 1) != 0);
        }
        Code(256);
        foreach (var d in data) Code(d);
        Code(257);
        var result = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) result[i / 8] |= (byte)(0x80 >> (i % 8));
        return result;
    }

    /// <summary>An ICC profile as a stream object (its bytes as Latin-1 text), the header changed by <paramref name="patch"/>.</summary>
    private static RawObject Profile(int number, byte[] profile, int components, Action<byte[]>? patch = null)
    {
        var bytes = (byte[])profile.Clone();
        patch?.Invoke(bytes);
        return Stream(number, $"/N {components}", Encoding.Latin1.GetString(bytes));
    }

    private static byte[] Srgb => Core.Pdf.Graphics.Icc.CalibratedProfile.Srgb();

    /// <summary>The names of nine colourants (one more than PDF/A-1 allows in DeviceN).</summary>
    private const string Nine = "/c0/c1/c2/c3/c4/c5/c6/c7/c8";

    /// <summary>
    /// A display profile of version 4.3 made of tone curves and a matrix, as many programs write it: description and copyright as
    /// multiLocalizedUnicodeType, the sRGB curve as a formula (parametricCurveType, function 3), the D65 → D50 adaptation (chad).
    /// </summary>
    internal static byte[] Version4Rgb()
    {
        static byte[] Xyz(double x, double y, double z) => [.. "XYZ "u8, 0, 0, 0, 0, .. Fixed(x), .. Fixed(y), .. Fixed(z)];
        static byte[] Fixed(double v)
        {
            var b = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, (int)Math.Round(v * 65536));
            return b;
        }
        static byte[] Mluc(string text)
        {
            var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
            byte[] head = [.. "mluc"u8, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 12, .. "enUS"u8, 0, 0, 0, 0, 0, 0, 0, 28];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(20), utf16.Length);
            return [.. head, .. utf16];
        }
        byte[] curve = [.. "para"u8, 0, 0, 0, 0, 0, 3, 0, 0, .. Fixed(2.4), .. Fixed(1 / 1.055), .. Fixed(0.055 / 1.055), .. Fixed(1 / 12.92), .. Fixed(0.04045)];
        var tags = new List<(string, byte[])>
        {
            ("desc", Mluc("Beispiel-RGB v4 – Grün")), ("cprt", Mluc("frei")), ("wtpt", Xyz(0.9642, 1, 0.8249)),
            ("chad", [.. "sf32"u8, 0, 0, 0, 0, .. Fixed(1.0479), .. Fixed(0.0229), .. Fixed(-0.0502), .. Fixed(0.0296), .. Fixed(0.9904), .. Fixed(-0.0171),
                .. Fixed(-0.0092), .. Fixed(0.0151), .. Fixed(0.7519)]),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)), ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)), ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        };
        var offset = 132 + tags.Count * 12;
        var table = new List<byte>();
        var data = new List<byte>();
        foreach (var (signature, bytes) in tags)
        {
            var entry = new byte[12];
            Encoding.ASCII.GetBytes(signature).CopyTo(entry, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(4), offset + data.Count);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(8), bytes.Length);
            table.AddRange(entry);
            data.AddRange(bytes);
            while (data.Count % 4 != 0) data.Add(0);
        }
        var header = new byte[128];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, offset + data.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 0x04300000);
        "mntrRGB XYZ "u8.CopyTo(header.AsSpan(12));
        "acsp"u8.CopyTo(header.AsSpan(36));
        Fixed(0.9642).CopyTo(header, 68);
        Fixed(1).CopyTo(header, 72);
        Fixed(0.8249).CopyTo(header, 76);
        byte[] count = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(count, tags.Count);
        return [.. header, .. count, .. table, .. data];
    }
    private static byte[] Cmyk => Core.Pdf.Graphics.Icc.CmykProfile.Bytes();

    /// <summary>
    /// Writes an ID into the header as a test file of the reference validator does (ISO 19005-4, 6.2.4.2, test 3, fail e): the ID ICC.1 7.2.18
    /// defines, the MD5 with flags, rendering intent and ID taken as zero — the same profile as its copy without ID (docs/FEHLERGEDAECHTNIS.md,
    /// No. 517). In the test file flags and intent are zero; the bundled CMYK profile has the intent 1, which the ID does not include (No. 1133;
    /// before, the MD5 here kept the intent, an ID no conforming writer gives).
    /// </summary>
    private static void WriteProfileId(byte[] profile)
    {
        var copy = (byte[])profile.Clone();
        copy.AsSpan(44, 4).Clear();
        copy.AsSpan(64, 4).Clear();
        copy.AsSpan(84, 16).Clear();
        System.Security.Cryptography.MD5.HashData(copy).CopyTo(profile.AsSpan(84));
    }

    /// <summary>A page whose content uses a graphics state /GS0 (given as text) before the rectangle.</summary>
    private static Func<byte[]> WithGraphicsState(string state, string content = "/GS0 gs " + Rectangle) =>
        File(o => o.With(Stream(4, "", content))
            .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ExtGState<</GS0 " + state + ">>>>>>")));

    /// <summary>A page that fills with a colour space /CS0 (given as text, objects from 5 on as <paramref name="more"/>).</summary>
    private static Func<byte[]> WithColourSpace(string space, string fill, params RawObject[] more) =>
        File(o =>
        {
            var list = o.With(Stream(4, "", "/CS0 cs " + fill + " sc 72 600 200 100 re f"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ColorSpace<</CS0 " + space + ">>>>>>"));
            foreach (var m in more) list = list.With(m);
            return list;
        });

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    /// <summary>The JPEG 2000 codestream of the image in a file of the public test collection (40 × 27 pixels, RGB).</summary>
    private static byte[] Codestream()
    {
        using var doc = Core.Pdf.Document.PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "jp2k-resetprob.pdf"));
        var xobjects = doc.Pages[0].Resources!.GetDictionary(Core.Pdf.Objects.PdfName.Get("XObject"))!;
        var image = xobjects.Keys.Select(k => xobjects.Get(k)).OfType<Core.Pdf.Objects.PdfStream>().First();
        var data = doc.DecodeStream(image).Data;
        // a JP2 file: the contents of its jp2c box
        for (var at = 0; at + 8 <= data.Length;)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            if (data.AsSpan(at + 4, 4).SequenceEqual("jp2c"u8)) return data[(at + 8)..(length == 0 ? data.Length : at + length)];
            if (length < 8) break;
            at += length;
        }
        return data;
    }

    /// <summary>A JP2 file of the codestream with its own header boxes: bit depth in ihdr (255: per channel in bpcc) and colour boxes.</summary>
    private static byte[] Jp2(byte[] codestream, byte depth = 7, byte[]? bpcc = null, params byte[][] colours)
    {
        static byte[] Box(string type, params byte[][] parts)
        {
            var content = parts.SelectMany(p => p).ToArray();
            var box = new byte[8 + content.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            content.CopyTo(box, 8);
            return box;
        }
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(8)) - System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(16));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(12)) - System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(20));
        var ihdr = new byte[14];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), width);
        ihdr[8] = codestream[40];
        ihdr[9] = codestream[41];
        ihdr[10] = depth;
        ihdr[11] = 7;
        var header = new List<byte[]> { Box("ihdr", ihdr) };
        if (bpcc is not null) header.Add(Box("bpcc", bpcc));
        header.AddRange(colours.Select(c => Box("colr", c)));
        return [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", "jp2 \0\0\0\0jp2 "u8.ToArray()), .. Box("jp2h", [.. header]), .. Box("jp2c", codestream)];
    }

    /// <summary>A colour box: enumerated (method 1) with the space, or another method.</summary>
    private static byte[] Colour(byte method, byte approx, uint space = 16)
    {
        var box = new byte[method == 1 ? 7 : 3];
        box[0] = method;
        box[2] = approx;
        if (method == 1) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(3), space);
        return box;
    }

    /// <summary>A page that draws the image /Im1 (object 5, given as dictionary text and data) over 100 × 100 points.</summary>
    private static Func<byte[]> WithImage(string dict, byte[] data, params RawObject[] more) =>
        File(o =>
        {
            var list = o.With(Stream(4, "", "q 100 0 0 100 72 600 cm /Im1 Do Q"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Im1 5 0 R>>>>>>"))
                .With(Stream(5, "/Type/XObject/Subtype/Image" + dict, Encoding.Latin1.GetString(data)));
            foreach (var m in more) list = list.With(m);
            return list;
        });

    /// <summary>A page that draws the form /Fm1 (object 5, given as dictionary text) with a rectangle inside.</summary>
    private static Func<byte[]> WithForm(string dict, params RawObject[] more) =>
        File(o =>
        {
            var list = o.With(Stream(4, "", "/Fm1 Do"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Fm1 5 0 R>>>>>>"))
                .With(Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 595 842]" + dict, Rectangle));
            foreach (var m in more) list = list.With(m);
            return list;
        });

    private const string Grey = "/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8";

    // ------------------------------------------------------------ fonts (from the bundled Liberation Sans)

    private static readonly Lazy<byte[]> SansFile = new(() => System.IO.File.ReadAllBytes(Path.Combine(Root(), "Schriften", "Standard", "LiberationSans-Regular.ttf")));
    private static readonly Lazy<Core.Fonts.OpenTypeFont> SansFont = new(() => Core.Fonts.OpenTypeFont.Parse(SansFile.Value));

    /// <summary>The glyph of a character in Liberation Sans.</summary>
    internal static int Gid(char c) => SansFont.Value.GlyphForUnicode(c);

    /// <summary>Liberation Sans cut to the glyphs of „AB“, with its cmap.</summary>
    private static byte[] Sans() => Core.Fonts.TrueTypeSubsetter.SubsetWithCmap(SansFile.Value, [0, Gid('A'), Gid('B')]);

    /// <summary>Liberation Sans with the glyphs of „AB“ and only a (3,0) cmap (F041, F042).</summary>
    private static byte[] SansSymbolCmap()
    {
        var advances = new Dictionary<int, int> { [0] = (int)SansFont.Value.AdvanceWidth(0), [Gid('A')] = (int)SansFont.Value.AdvanceWidth(Gid('A')), [Gid('B')] = (int)SansFont.Value.AdvanceWidth(Gid('B')) };
        return Core.Fonts.TrueTypeSubsetter.Rebuild(SansFile.Value, advances, [], [(0xF041, Gid('A')), (0xF042, Gid('B'))]);
    }

    /// <summary>A bare CFF program with the glyphs „A“ and „B“ of Liberation Sans (named so).</summary>
    private static byte[] SansCff() =>
        Core.Fonts.CffBuilder.Build("ABCDEF+Test", new Core.Pdf.Graphics.Matrix(1.0 / SansFont.Value.UnitsPerEm, 0, 0, 1.0 / SansFont.Value.UnitsPerEm, 0, 0),
            [("A", SansFont.Value.Outline(Gid('A')), SansFont.Value.AdvanceWidth(Gid('A'))), ("B", SansFont.Value.Outline(Gid('B')), SansFont.Value.AdvanceWidth(Gid('B')))]);

    /// <summary>A page that shows <paramref name="shown"/> with /F1 (object 5, given as text; more objects from 6 on).</summary>
    private static Func<byte[]> WithText(string font, string shown, params RawObject[] more) =>
        File(o =>
        {
            var list = o.With(Stream(4, "", $"BT /F1 24 Tf 72 700 Td {shown} Tj ET"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>"))
                .With(Obj(5, font));
            foreach (var m in more) list = list.With(m);
            return list;
        });

    private const string SansDescriptor =
        "<</Type/FontDescriptor/FontName/LiberationSans/Flags 32/FontBBox[-203 -303 1050 910]/ItalicAngle 0/Ascent 905/Descent -212/CapHeight 700/StemV 80/FontFile2 7 0 R>>";

    /// <summary>
    /// „AB“ in a simple TrueType font (object 5; descriptor 6, program 7) — valid but for what <paramref name="font"/> and
    /// <paramref name="descriptor"/> change in its dictionary and descriptor.
    /// </summary>
    internal static Func<byte[]> WithTrueType(Func<string, string>? font = null, Func<string, string>? descriptor = null, Func<byte[]>? program = null,
        params RawObject[] more) =>
        () =>
        {
            var data = (program ?? Sans)();
            const string dict = "<</Type/Font/Subtype/TrueType/BaseFont/LiberationSans/FirstChar 65/LastChar 66/Widths[667 667]/Encoding/WinAnsiEncoding/FontDescriptor 6 0 R>>";
            return WithText(font?.Invoke(dict) ?? dict, "(AB)",
                [Obj(6, descriptor?.Invoke(SansDescriptor) ?? SansDescriptor), Stream(7, $"/Length1 {data.Length}", Encoding.Latin1.GetString(data)), .. more])();
        };

    /// <summary>
    /// „AB“ in a composite font (object 5) over a TrueType CIDFont (8; descriptor 6, program 7), Identity-H, the glyph indices as codes — valid but
    /// for what the arguments change; <paramref name="shown"/> null shows „AB“.
    /// </summary>
    internal static Func<byte[]> WithCidTrueType(Func<string, string>? type0 = null, Func<string, string>? cid = null, Func<string, string>? descriptor = null,
        string? shown = null, params RawObject[] more) =>
        () =>
        {
            var data = Sans();
            const string font = "<</Type/Font/Subtype/Type0/BaseFont/LiberationSans/Encoding/Identity-H/DescendantFonts[8 0 R]>>";
            const string cidFont = "<</Type/Font/Subtype/CIDFontType2/BaseFont/LiberationSans/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor 6 0 R/CIDToGIDMap/Identity/DW 667>>";
            var desc = SansDescriptor.Replace("/Flags 32", "/Flags 4", StringComparison.Ordinal);
            return WithText(type0?.Invoke(font) ?? font, shown ?? $"<{Gid('A'):X4}{Gid('B'):X4}>",
                [Obj(6, descriptor?.Invoke(desc) ?? desc), Stream(7, $"/Length1 {data.Length}", Encoding.Latin1.GetString(data)), Obj(8, cid?.Invoke(cidFont) ?? cidFont), .. more])();
        };

    /// <summary>An embedded encoding CMap (object 9): two-byte codes, the given entries and further lines; its dictionary <paramref name="dict"/>.</summary>
    private static RawObject CMapStream(string entries, string dict = "/Type/CMap/CMapName/Test-H/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>",
        string program = "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def\n", string more = "") =>
        Stream(9, dict, "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" + program + "/CMapName /Test-H def\n/CMapType 1 def\n" + more +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" + entries + "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");

    /// <summary>The cidchar entries that map the glyph indices of „AB“ to themselves.</summary>
    private static string SelfCids() => $"2 begincidchar\n<{Gid('A'):X4}> {Gid('A')}\n<{Gid('B'):X4}> {Gid('B')}\nendcidchar\n";

    /// <summary>„AB“ in a Type 1 font with a bare CFF program (object 5; descriptor 6, program 7 as FontFile3 of <paramref name="kind"/>).</summary>
    private static Func<byte[]> WithCff(string kind = "Type1C", string descriptorExtra = "") =>
        () =>
        {
            var data = SansCff();
            return WithText("<</Type/Font/Subtype/Type1/BaseFont/ABCDEF+Test/FirstChar 65/LastChar 66/Widths[667 667]/Encoding<</Type/Encoding/Differences[65/A/B]>>/FontDescriptor 6 0 R>>",
                "(AB)", Obj(6, "<</Type/FontDescriptor/FontName/ABCDEF+Test/Flags 4/FontBBox[-203 -303 1050 910]/ItalicAngle 0/Ascent 905/Descent -212/CapHeight 700/StemV 80/FontFile3 7 0 R" +
                            descriptorExtra + ">>"),
                Stream(7, $"/Subtype/{kind}", Encoding.Latin1.GetString(data)))();
        };

    /// <summary>„AB“ in a Type 3 font with procedures /a and /b (objects 6, 7) and the given encoding, procedures and further entries.</summary>
    private static Func<byte[]> WithType3(string differences = "[65/a/b]", string procs = "/a 6 0 R/b 7 0 R", string extra = "") =>
        WithText($"<</Type/Font/Subtype/Type3/FontBBox[0 0 750 750]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<<{procs}>>/Encoding<</Type/Encoding/Differences{differences}>>/FirstChar 65/LastChar 66/Widths[750 750]/Resources<<>>{extra}>>",
            "(AB)", Stream(6, "", "750 0 0 0 750 750 d1 0 0 750 750 re f"), Stream(7, "", "750 0 0 0 750 750 d1 0 0 375 750 re f"));

    // ------------------------------------------------------------ annotations, forms, actions

    /// <summary>A red appearance (object 6, a form of 100 × 50).</summary>
    internal static RawObject RedLook(int number = 6) => Stream(number, "/Type/XObject/Subtype/Form/BBox[0 0 100 50]", "1 0 0 rg 0 0 100 50 re f");

    /// <summary>A page with one annotation (object 5, given as text; more objects from 6 on) and the rectangle as content.</summary>
    internal static Func<byte[]> WithAnnotation(string annot, string catalogue = "", string page = "", params RawObject[] more) =>
        File(o =>
        {
            var list = o.With(Obj(1, $"<</Type/Catalog/Pages 2 0 R{catalogue}>>"))
                .With(Obj(3, $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Annots[5 0 R]{page}>>"))
                .With(Obj(5, annot));
            foreach (var m in more) list = list.With(m);
            return list;
        });

    /// <summary>A square annotation over 100 × 50 with the red appearance and the given further entries.</summary>
    internal static Func<byte[]> WithSquare(string entries = "/F 4", string catalogue = "", string page = "", params RawObject[] more) =>
        WithAnnotation($"<</Type/Annot/Subtype/Square/Rect[100 600 200 650]{entries}/AP<</N 6 0 R>>>>", catalogue, page, [RedLook(), .. more]);

    /// <summary>A text field (object 5, a widget that is its field) in the form of the catalogue, with the red appearance and further entries.</summary>
    internal static Func<byte[]> WithField(string entries = "", string form = "", string type = "/FT/Tx", params RawObject[] more) =>
        WithAnnotation($"<</Type/Annot/Subtype/Widget{type}/T(Feld)/F 4/Rect[100 600 200 650]/P 3 0 R/AP<</N 6 0 R>>{entries}>>",
            $"/AcroForm<</Fields[5 0 R]{form}>>", "", [RedLook(), .. more]);

    /// <summary>A script action (object 8).</summary>
    internal static RawObject Script(int number = 8) => Obj(number, "<</Type/Action/S/JavaScript/JS(app.alert\\(1\\);)>>");

    /// <summary>A CMS block of a detached signature over empty content: with or without the signer's certificate, by one or two signers.</summary>
    private static string SignatureBlock(bool certificate = true, int signers = 1)
    {
        var cms = new System.Security.Cryptography.Pkcs.SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo([1]), detached: true);
        for (var i = 0; i < signers; i++)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN=Max Mustermann {i + 1}", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            cms.ComputeSignature(new System.Security.Cryptography.Pkcs.CmsSigner(cert)
            {
                IncludeOption = certificate ? System.Security.Cryptography.X509Certificates.X509IncludeOption.EndCertOnly : System.Security.Cryptography.X509Certificates.X509IncludeOption.None,
            });
        }
        return Convert.ToHexString(cms.Encode());
    }

    /// <summary>A signature field (object 5) whose value (object 7) is the given signature dictionary entries.</summary>
    private static Func<byte[]> WithSignature(string value, string catalogue = "") =>
        WithAnnotation("<</Type/Annot/Subtype/Widget/FT/Sig/T(Unterschrift)/F 4/Rect[100 600 200 650]/P 3 0 R/AP<</N 6 0 R>>/V 7 0 R>>",
            "/AcroForm<</Fields[5 0 R]/SigFlags 3>>" + catalogue, "", RedLook(),
            Obj(7, "<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached" + value + ">>"));

    // ------------------------------------------------------------ metadata

    private const string PacketHeader = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>";
    private const string Identified = "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"2\" pdfaid:conformance=\"B\"/>";

    /// <summary>An XMP packet with the given descriptions (by default after the identification of PDF/A-2b).</summary>
    private static string Packet(string descriptions, string header = PacketHeader, string identification = Identified) =>
        header + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" + identification + descriptions +
        "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>A page whose catalogue has the packet as /Metadata (object 9, its bytes as given).</summary>
    private static Func<byte[]> WithXmp(byte[] packet) =>
        File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Metadata 9 0 R>>")).With(Stream(9, "/Type/Metadata/Subtype/XML", Encoding.Latin1.GetString(packet))));

    private static Func<byte[]> WithXmp(string packet) => WithXmp(Encoding.UTF8.GetBytes(packet));

    /// <summary>An extension schema of the namespace http://beispiel.invalid/ns/ (prefix bsp) with one text property, changed by <paramref name="change"/>.</summary>
    private static string ExtensionSchema(Func<string, string>? change = null)
    {
        const string schema =
            "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" " +
            "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"><pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">" +
            "<pdfaSchema:schema>Beispiel</pdfaSchema:schema><pdfaSchema:namespaceURI>http://beispiel.invalid/ns/</pdfaSchema:namespaceURI>" +
            "<pdfaSchema:prefix>bsp</pdfaSchema:prefix><pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">" +
            "<pdfaProperty:name>Wert</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>internal</pdfaProperty:category>" +
            "<pdfaProperty:description>Ein Wert</pdfaProperty:description></rdf:li></rdf:Seq></pdfaSchema:property></rdf:li></rdf:Bag></pdfaExtension:schemas></rdf:Description>" +
            "<rdf:Description rdf:about=\"\" xmlns:bsp=\"http://beispiel.invalid/ns/\" bsp:Wert=\"1\"/>";
        return change?.Invoke(schema) ?? schema;
    }

    // ------------------------------------------------------------ attachments and layers

    /// <summary>A document with one attachment in /Names /EmbeddedFiles: specification 9 (given as text), file 10 (with the given entries).</summary>
    private static Func<byte[]> WithAttachment(string spec = "<</Type/Filespec/F(daten.txt)/UF(daten.txt)/AFRelationship/Data/EF<</F 10 0 R/UF 10 0 R>>>>",
        string file = "/Type/EmbeddedFile/Subtype/text#2Fplain", string catalogue = "/AF[9 0 R]") =>
        File(o => o.With(Obj(1, $"<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles<</Names[(daten.txt) 9 0 R]>>>>{catalogue}>>"))
            .With(Obj(9, spec)).With(Stream(10, file, "Rechnungsdaten")));

    /// <summary>A document with one layer (object 10) and the given optional-content properties (in which 10 0 R is the layer).</summary>
    private static Func<byte[]> WithLayers(string properties) =>
        File(o => o.With(Obj(1, $"<</Type/Catalog/Pages 2 0 R/OCProperties{properties}>>")).With(Obj(10, "<</Type/OCG/Name(Ebene)>>")));

    /// <summary>A /ToUnicode (object 9) that reads „A“ as U+FEFF U+0041 and „B“ as itself.</summary>
    /// <summary>
    /// A tagged page (level a): the rectangle as marked content 0 of one structure element of the given type; the catalogue marked, with a
    /// language and a role map as given (text to put in, empty for none).
    /// </summary>
    private static Func<byte[]> Tagged(string type = "P", string roleMap = "", string catalogue = "/MarkInfo<</Marked true>>/Lang(de)", string element = "",
        string content = "/P <</MCID 0>> BDC " + Rectangle + " EMC") =>
        () => Raw(Page(content: content, catalogue: "/StructTreeRoot 5 0 R" + catalogue, page: "/StructParents 0", more:
        [
            Obj(5, $"<</Type/StructTreeRoot/K 6 0 R/ParentTree 7 0 R/ParentTreeNextKey 1{roleMap}>>"),
            Obj(6, $"<</Type/StructElem/S/{type}/P 5 0 R/Pg 3 0 R/K 0{element}>>"),
            Obj(7, "<</Nums[0[6 0 R]]>>"),
        ]));

    /// <summary>A /ToUnicode that maps A into the private use area of Unicode (B to itself).</summary>
    internal static RawObject PrivateUseText() =>
        Stream(9, "", "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CMapName /T def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
                      "2 beginbfchar\n<41> <E000>\n<42> <0042>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");

    internal static RawObject ByteOrderMarkText() =>
        Stream(9, "", "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CMapName /T def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
                      "2 beginbfchar\n<41> <FEFF0041>\n<42> <0042>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");

    /// <summary>A /ToUnicode that maps „A“ to the value 0 (the text leaves it out; Fehlergedächtnis 501).</summary>
    internal static RawObject ZeroText() =>
        ByteOrderMarkText() with { Text = ByteOrderMarkText().Text.Replace("<FEFF0041>", "<0000>", StringComparison.Ordinal) };

    /// <summary>The cases by rule name (several for a rule that has several forms), those of PDF/A-1 added (<see cref="PartOne"/>).</summary>
    public static IReadOnlyDictionary<string, RuleCase[]> All { get; } = WithPartOne(new Dictionary<string, RuleCase[]>
    {
        // ------------------------------------------------------------ 6.2 Graphics
        ["IntentProfile"] = [new(() => Raw(Page(catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(x)/DestOutputProfile 5 0 R>>]",
            more: Profile(5, Srgb, 3, b => "scnr"u8.CopyTo(b.AsSpan(12))))), Note: "Profil eines Scanners als Ausgabebedingung"),
            new(() => Raw(Page(page: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(x)/DestOutputProfile 5 0 R>>]",
                more: Profile(5, Srgb, 3, b => "scnr"u8.CopyTo(b.AsSpan(12))))), PdfAConformance.A4,
                Note: "Profil eines Scanners als Ausgabebedingung einer Seite (PDF 2.0, Fehlergedächtnis 520)")],
        ["IntentsSameProfile"] = [new(() => Raw(Page(catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(a)/DestOutputProfile 5 0 R>>" +
            "<</Type/OutputIntent/S/GTS_PDFX/OutputConditionIdentifier(b)/DestOutputProfile 6 0 R>>]", more: [Profile(5, Srgb, 3), Profile(6, Srgb, 3)])),
            Note: "zwei Ausgabebedingungen mit verschiedenen Profil-Objekten")],
        ["OneOutputIntent"] = [new(() => Raw(Page(catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(a)/DestOutputProfile 5 0 R>>" +
            "<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(b)/DestOutputProfile 5 0 R>>]", more: Profile(5, Srgb, 3))), PdfAConformance.A4,
            Note: "zwei PDF/A-Ausgabebedingungen (Fehlergedächtnis 495)")],
        ["IntentProfileRef"] = [new(() => Raw(Page(catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(a)/DestOutputProfile 5 0 R>>" +
            "<</Type/OutputIntent/S/GTS_PDFX/OutputConditionIdentifier(a)/DestOutputProfile 5 0 R/DestOutputProfileRef<</URLs[(https://example.org/p.icc)]>>>>]",
            more: Profile(5, Srgb, 3))), Note: "PDF/X-Ausgabebedingung mit Verweis auf ein Profil außerhalb"),
            new(() => Raw(Page(page: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(a)/DestOutputProfile 5 0 R" +
                "/DestOutputProfileRef<</URLs[(https://example.org/p.icc)]>>>>]", more: Profile(5, Srgb, 3))), PdfAConformance.A4,
                Note: "Ausgabebedingung einer Seite mit Verweis auf ein Profil außerhalb (PDF 2.0, Fehlergedächtnis 520)")],
        ["IccProfileValid"] = [new(WithColourSpace("[/ICCBased 5 0 R]", "0 0 1", Stream(5, "/N 3", "kein Profil")), Note: "Profil, das niemand lesen kann: wie die Anzeige als DeviceRGB"),
                               // since docs/FEHLERGEDAECHTNIS.md, No. 1134 the display does not use a profile of such a class (as the reference readers):
                               // the copy keeps what the display shows, as for an unreadable profile (before, refused: colours would have changed)
                               new(WithColourSpace("[/ICCBased 5 0 R]", "0 0 1", Profile(5, Srgb, 3, b => "abst"u8.CopyTo(b.AsSpan(12)))),
                                   Note: "Profil einer Klasse, die PDF/A nicht zulässt (Abstract): wie die Anzeige als DeviceRGB")],
        ["OverprintMode"] = [new(() => Raw(Page(content: "/GS0 gs /CS0 cs 0 0 1 0 sc " + "72 600 200 100 re f",
            resources: "/ExtGState<</GS0<</OP true/op true/OPM 1>>>>/ColorSpace<</CS0[/ICCBased 5 0 R]>>", more: Profile(5, Cmyk, 4))), Note: "Überdrucken mit Modus 1 und ICC-CMYK")],
        ["IccNotIntent"] = [new(() => Raw(Page(content: "/CS0 cs 0 0 1 0 sc 72 600 200 100 re f", catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(c)/DestOutputProfile 5 0 R>>]",
            resources: "/ColorSpace<</CS0[/ICCBased 5 0 R]>>", more: Profile(5, Cmyk, 4))), PdfAConformance.A4, Note: "ICC-CMYK mit dem Profil der Ausgabebedingung"),
            new(() => Raw(Page(content: "/CS0 cs 0 0 1 0 sc 72 600 200 100 re f", catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(c)/DestOutputProfile 5 0 R>>]",
                resources: "/ColorSpace<</CS0[/ICCBased 6 0 R]>>",
                more: [Profile(5, Cmyk, 4, WriteProfileId), Profile(6, Cmyk, 4, b => b.AsSpan(84, 16).Clear())])),
                PdfAConformance.A4, Note: "dasselbe CMYK-Profil, einmal mit, einmal ohne Profil-ID (Fehlergedächtnis 517)"),
            new(() => Raw(Page(content: "/CS0 cs 0 0 1 0 sc 72 600 200 100 re f",
                page: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(c)/DestOutputProfile 5 0 R>>]",
                resources: "/ColorSpace<</CS0[/ICCBased 5 0 R]>>", more: Profile(5, Cmyk, 4))), PdfAConformance.A4,
                Note: "ICC-CMYK mit dem Profil der Ausgabebedingung der Seite (PDF 2.0, Fehlergedächtnis 520)"),
            new(() => Raw(Page(content: "/CS0 cs 0 0 1 0 sc 72 600 200 100 re f", catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(r)/DestOutputProfile 5 0 R>>]",
                page: "/Group<</S/Transparency/CS 6 0 R>>", resources: "/ColorSpace<</CS0 6 0 R>>", more: [Profile(5, Srgb, 3), Obj(6, "[/ICCBased 7 0 R]"), Profile(7, Cmyk, 4)])),
                PdfAConformance.A4, Note: "ICC-CMYK mit dem Profil des Mischfarbraums der Seite, RGB-Ausgabebedingung")],
        ["DeviceRgb"] = [new(File(), Note: "DeviceRGB ohne Ausgabebedingung"),
            // a non-isolated group inherits the blending space of the page; its own /CS does not count (ISO 32000-2, 11.6.6; Fehlergedächtnis 518)
            new(() => Raw(Page(content: "/Fm0 Do", catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(c)/DestOutputProfile 5 0 R>>]",
                page: "/Group<</S/Transparency/CS/DeviceCMYK>>", resources: "/XObject<</Fm0 6 0 R>>",
                more: [Profile(5, Cmyk, 4), Stream(6, "/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Group<</S/Transparency/CS[/CalRGB<</WhitePoint[0.9505 1 1.089]>>]>>",
                    "1 0 0 rg 72 600 200 100 re f")])), PdfAConformance.A4,
                Note: "DeviceRGB in einer nicht isolierten Gruppe mit CalRGB, CMYK-Ausgabebedingung (Fehlergedächtnis 518)"),
            // 3D artwork whose colours are DeviceRGB (ISO 32000-2, Table 311) under a CMYK output intent (Fehlergedächtnis 518)
            new(() => Raw(Page(content: "0 0 0 1 k 72 600 200 100 re f", catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(c)/DestOutputProfile 5 0 R>>]",
                page: "/Annots[<</Type/Annot/Subtype/3D/Rect[72 300 272 450]/F 4/3DD 6 0 R/AP<</N 7 0 R>>>>]",
                more: [Profile(5, Cmyk, 4), Stream(6, "/Type/3D/Subtype/U3D/ColorSpace/DeviceRGB", "Beispieldaten eines Modells"),
                    Stream(7, "/Type/XObject/Subtype/Form/BBox[0 0 200 150]", "0 0 0 0.5 k 0 0 200 150 re f")])), PdfAConformance.A4e,
                Note: "3D-Daten in DeviceRGB, CMYK-Ausgabebedingung (Fehlergedächtnis 518)"),
            new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ColorSpace<</DefaultRGB/DeviceRGB>>>>>>"))),
                Note: "DeviceRGB mit /DefaultRGB /DeviceRGB: kein geräteunabhängiger Standard-Farbraum (Fehlergedächtnis 509)")],
        ["DeviceCmyk"] = [new(File(o => o.With(Stream(4, "", "0 1 1 0 k 72 600 200 100 re f"))), Note: "DeviceCMYK ohne Ausgabebedingung")],
        ["DeviceGray"] = [new(File(o => o.With(Stream(4, "", "0.5 g 72 600 200 100 re f"))), Note: "DeviceGray ohne Ausgabebedingung")],
        ["Colorants"] = [new(WithColourSpace("[/DeviceN[/Rot/Blau]/DeviceRGB 5 0 R]", "1 0", Stream(5, "/FunctionType 4/Domain[0 1 0 1]/Range[0 1 0 1 0 1]",
            "{pop pop 1 0 0}")), Note: "DeviceN ohne /Colorants")],
        ["SeparationsAlike"] = [new(() => Raw(Page(content: "/A cs 1 sc 72 600 100 100 re f /B cs 1 sc 300 600 100 100 re f",
            resources: "/ColorSpace<</A[/Separation/Gold/DeviceRGB 5 0 R]/B[/Separation/Gold/DeviceRGB 6 0 R]>>",
            more: [Obj(5, "<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[1 0.8 0]/N 1>>"), Obj(6, "<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[0.8 0.6 0]/N 1>>")])),
            Note: "zwei Sonderfarben „Gold“ verschieden beschrieben"),
            // in the /Colorants of two DeviceN spaces (Fehlergedächtnis 504): renamed together with the Separation space there
            new(() => Raw(Page(content: "/CS0 cs 1 0 scn 72 600 100 100 re f /CS1 cs 1 0 scn 300 600 100 100 re f",
                resources: "/ColorSpace<</CS0[/DeviceN[/Rot/Gruen]/DeviceRGB 5 0 R 6 0 R]/CS1[/DeviceN[/Rot/Grau]/DeviceRGB 5 0 R 7 0 R]>>",
                more: [Stream(5, "/FunctionType 4/Domain[0 1 0 1]/Range[0 1 0 1 0 1]", "{pop 1 exch sub dup}"),
                       Obj(6, "<</Colorants<</Rot[/Separation/Rot/DeviceRGB 8 0 R]>>>>"), Obj(7, "<</Colorants<</Rot[/Separation/Rot/DeviceRGB 9 0 R]>>>>"),
                       Obj(8, "<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[1 0 0]/N 1>>"), Obj(9, "<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[0.8 0 0]/N 1>>")])),
                Note: "„Rot“ in /Colorants zweier DeviceN-Räume verschieden beschrieben"),
        ],
        ["NoTransfer"] = [new(WithGraphicsState("<</TR/Identity>>"))],
        ["NoTransfer2"] = [new(WithGraphicsState("<</TR2/Identity>>"))],
        ["NoHalftoneOrigin"] = [new(WithGraphicsState("<</HTP[0 0]>>")), new(WithGraphicsState("<</HTO[0 0]>>"), PdfAConformance.A4)],
        ["HalftoneType"] = [new(WithGraphicsState("<</HT<</Type/Halftone/HalftoneType 6/Width 2/Height 2/Length 4>>>>"))],
        ["HalftoneName"] = [new(WithGraphicsState("<</HT<</Type/Halftone/HalftoneType 1/HalftoneName(Raster)/Frequency 60/Angle 45/SpotFunction/Round>>>>"))],
        ["HalftoneTransfer"] =
        [
            new(WithGraphicsState("<</HT<</Type/Halftone/HalftoneType 1/Frequency 60/Angle 45/SpotFunction/Round/TransferFunction/Identity>>>>")),
            // a type 5 halftone with colourants of an RGB device, without transfer functions (Fehlergedächtnis 337, from a layout test file)
            new(WithGraphicsState("<</HT<</Type/Halftone/HalftoneType 5/Default<</HalftoneType 1/Frequency 150/Angle 75/SpotFunction/Round>>" +
                                  "/Red<</HalftoneType 1/Frequency 150/Angle 15/SpotFunction/Round>>>>>>")),
        ],
        ["RenderingIntent"] = [new(WithGraphicsState("<</RI/Erfunden>>"), Note: "im Grafikzustand"),
                               new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /Intent /Custom ID @À EI Q")),
                                   Note: "im Inline-Bild (Fehlergedächtnis 500)"),
                               new(File(o => o.With(Stream(4, "", "/Erfunden ri " + Rectangle))), Note: "im Inhalt"),
                               new(WithGraphicsState("<</RI/Erfunden>>"), PdfAConformance.A4, Note: "Teil 4 (6.2.4.6, Fehlergedächtnis 495)")],
        ["BlendModes"] = [new(WithGraphicsState("<</BM/Erfunden>>"))],
        ["TransparencyNeedsSpace"] =
        [
            new(WithGraphicsState("<</ca 0.5>>"), Note: "Deckkraft 0,5 ohne Ausgabebedingung und Gruppe"),
            // only an annotation is transparent: through its own opacity or blend mode, or its appearance (Fehlergedächtnis 496)
            new(WithSquare("/F 4/CA 0.5"), Note: "Anmerkung mit /CA 0,5"),
            new(WithSquare("/F 4/BM/Multiply"), PdfAConformance.A4, Note: "Anmerkung mit Mischmodus, Teil 4"),
            new(WithAnnotation("<</Type/Annot/Subtype/Square/Rect[100 600 200 650]/F 4/AP<</N 6 0 R>>>>", more:
                Stream(6, "/Type/XObject/Subtype/Form/BBox[0 0 100 50]/Resources<</ExtGState<</G0<</ca 0.5>>>>>>", "/G0 gs 1 0 0 rg 0 0 100 50 re f")),
                Note: "Deckkraft im Erscheinungsbild"),
        ],
        ["DeviceNColourants"] = [new(WithColourSpace("[/DeviceN[" + string.Concat(Enumerable.Range(1, 33).Select(i => $"/F{i}")) + "]/DeviceGray 5 0 R]",
            string.Join(" ", Enumerable.Repeat("0.3", 33)), Obj(5, "<</FunctionType 2/Domain[0 1]/C0[1]/C1[0]/N 1>>")),
            Note: "33 Farben: in der Ersatzfarbe, wie die Anzeige sie umrechnet"),
            new(() => Raw(Page(content: "/Sh0 sh", resources: "/Shading<</Sh0 5 0 R>>", more:
            [
                Obj(5, "<</ShadingType 4/ColorSpace 6 0 R/BitsPerCoordinate 16/BitsPerComponent 8/BitsPerFlag 8/Decode[0 595 0 842" +
                    string.Concat(Enumerable.Repeat(" 0 1", 33)) + "]>>"),
                Obj(6, "[/DeviceN[" + string.Concat(Enumerable.Range(1, 33).Select(i => $"/F{i}")) + "]/DeviceGray 7 0 R]"),
                Obj(7, "<</FunctionType 2/Domain[0 1]/C0[1]/C1[0]/N 1>>"),
            ])), Outcome: RuleOutcome.Refused, Note: "ein Gitterverlauf ohne Funktion: seine Farben stehen in seinen Daten")],
        ["ImageAlternates"] = [new(WithImage(Grey + "/Alternates[]", [0x40, 0xC0]))],
        ["ImageOpi"] = [new(WithImage(Grey + "/OPI<</Version 2.0>>", [0x40, 0xC0]))],
        ["ImageInterpolate"] = [new(WithImage(Grey + "/Interpolate true", [0x40, 0xC0]), Look: "die Anzeige glättet das Bild im Original, die Norm verbietet es")],
        ["ImageBits"] = [new(WithImage("/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 3", [0x3C]), Note: "3 Bit je Kanal")],
        ["MaskBits"] = [new(WithImage("/Width 2/Height 1/ImageMask true/BitsPerComponent 2", [0x30]), Note: "Bildmaske mit 2 Bit"),
            new(WithImage(Grey + "/Mask 6 0 R", [0x40, 0xC0], Stream(6, "/Type/XObject/Subtype/Image/Width 2/Height 1/BitsPerComponent 2", "0")),
                Note: "Maske (/Mask) mit 2 Bit")],
        ["JpxChannels"] = [new(() => WithImage("/Width 40/Height 27/Filter/JPXDecode", Codestream())(), Note: "Datenstrom ohne JP2-Rahmen")],
        ["JpxColourSpecs"] = [new(() => WithImage("/Width 40/Height 27/Filter/JPXDecode", Jp2(Codestream(), colours: [Colour(1, 0), Colour(1, 0)]))(),
            Note: "zwei Farbangaben, keine maßgeblich")],
        ["JpxMethod"] = [new(() => WithImage("/Width 40/Height 27/Filter/JPXDecode", Jp2(Codestream(), colours: [Colour(5, 0)]))(), Note: "Methode 5")],
        ["JpxJab"] = [new(() => WithImage("/Width 40/Height 27/Filter/JPXDecode", Jp2(Codestream(), colours: [Colour(1, 0, 19)]))(), Note: "CIEJab")],
        ["JpxBitDepth"] = [new(() => WithImage("/Width 40/Height 27/Filter/JPXDecode", Jp2(Codestream(), 255, [7, 7, 7], [Colour(1, 0)]))(),
            Note: "Bittiefe je Kanal (bpcc)")],
        ["FormOpiPostScript"] = [new(WithForm("/OPI<</Version 2.0>>"), Note: "OPI"), new(WithForm("/Subtype2/PS/PS 6 0 R", Stream(6, "", "showpage")), Note: "PostScript für Drucker")],
        ["NoReferenceXObject"] = [new(WithForm("/Ref<</F(andere.pdf)/Page 0>>"))],
        ["NoPostScript"] = [new(File(o => o.With(Stream(4, "", Rectangle + " /Ps1 Do"))
            .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Ps1 5 0 R>>>>>>"))
            .With(Stream(5, "/Type/XObject/Subtype/PS", "showpage"))), Note: "PostScript-XObject")],
        ["FontType"] = [new(WithTrueType(f => f.Replace("/Type/Font", "", StringComparison.Ordinal)), Note: "TrueType ohne /Type")],
        ["FontSubtype"] = [new(WithTrueType(f => f.Replace("/Subtype/TrueType", "/Subtype/TrueTypeX", StringComparison.Ordinal)), Note: "unbekannte Art")],
        ["FontBaseName"] = [new(WithTrueType(f => f.Replace("/BaseFont/LiberationSans", "", StringComparison.Ordinal)))],
        ["FontFirstChar"] = [new(WithTrueType(f => f.Replace("/FirstChar 65", "", StringComparison.Ordinal)))],
        ["FontLastChar"] = [new(WithTrueType(f => f.Replace("/LastChar 66", "", StringComparison.Ordinal)))],
        ["FontWidthsCount"] = [new(WithTrueType(f => f.Replace("/Widths[667 667]", "/Widths[667]", StringComparison.Ordinal)))],
        ["FontFileSubtype"] = [new(WithCff("Type1X"), Note: "FontFile3 /Type1X")],
        ["CidSystemInfo"] = [new(WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding 9 0 R", StringComparison.Ordinal),
            more: CMapStream(SelfCids(), "/Type/CMap/CMapName/Test-H/CIDSystemInfo<</Registry(Adobe)/Ordering(Japan1)/Supplement 2>>",
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> def\n")), Note: "CMap Japan1, CIDFont Identity")],
        ["CidToGidMap"] =
        [
            new(WithCidTrueType(cid: c => c.Replace("/CIDToGIDMap/Identity", "", StringComparison.Ordinal)), Note: "fehlt"),
            new(WithCidTrueType(cid: c => c.Replace("/CIDToGIDMap/Identity", "/CIDToGIDMap/KeineIdentity", StringComparison.Ordinal)), Note: "anderer Name"),
        ],
        ["CidRange"] = [new(() => WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding 9 0 R", StringComparison.Ordinal), shown: "<0041>",
            more: CMapStream("1 begincidchar\n<0041> 70000\nendcidchar\n"))(), Note: "CID 70 000")],
        ["CMapEmbedded"] =
        [
            new(WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding/Unbekannt-H", StringComparison.Ordinal)), Note: "unbekannter Name"),
            new(() => WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding 9 0 R", StringComparison.Ordinal),
                more: CMapStream($"2 beginbfchar\n<{Gid('A'):X4}> <{Gid('A'):X4}>\n<{Gid('B'):X4}> <{Gid('B'):X4}>\nendbfchar\n"))(), Note: "CMap in der Form einer /ToUnicode"),
        ],
        ["CMapWMode"] = [new(() => WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding 9 0 R", StringComparison.Ordinal),
            more: CMapStream(SelfCids(), "/Type/CMap/CMapName/Test-H/WMode 1/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>", more: "/WMode 0 def\n"))(),
            Note: "Wörterbuch 1, CMap 0")],
        ["CMapUses"] = [new(() => WithCidTrueType(t => t.Replace("/Encoding/Identity-H", "/Encoding 9 0 R", StringComparison.Ordinal),
            more: CMapStream(SelfCids(), "/Type/CMap/CMapName/Test-H/UseCMap/Andere-H/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>", more: "/Andere-H usecmap\n"))(),
            Note: "usecmap einer nicht vordefinierten CMap")],
        ["FontEmbedded"] = [new(WithText("<</Type/Font/Subtype/TrueType/BaseFont/Unbekannt/FirstChar 65/LastChar 66/Widths[600 600]/Encoding/WinAnsiEncoding/FontDescriptor 6 0 R>>",
            "(AB)", Obj(6, "<</Type/FontDescriptor/FontName/Unbekannt/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 900/Descent -200/CapHeight 700/StemV 80>>")),
            Note: "nicht eingebettet, keine Standardschrift: eingebettet als die Ersatzschrift der Anzeige")],
        ["GlyphPresent"] = [new(WithType3(procs: "/a 6 0 R"), Note: "Type 3 ohne Prozedur für „B“")],
        ["CharSet"] = [new(WithCff(descriptorExtra: "/CharSet(/A)"), Note: "/CharSet ohne „B“"),
            new(WithCff(descriptorExtra: "/CharSet(/A/B/Zeta)"), Note: "/CharSet mit einer Glyphe, die das Programm nicht hat (Fehlergedächtnis 616)")],
        ["CidSet"] = [new(WithCidTrueType(cid: c => c.Replace("/BaseFont/LiberationSans", "/BaseFont/ABCDEF+LiberationSans", StringComparison.Ordinal),
            descriptor: d => d.Replace("/FontFile2 7 0 R", "/FontFile2 7 0 R/CIDSet 10 0 R", StringComparison.Ordinal), more: Stream(10, "", "\0")), Note: "leere /CIDSet")],
        ["GlyphWidths"] = [new(WithTrueType(f => f.Replace("/Widths[667 667]", "/Widths[900 667]", StringComparison.Ordinal)), Note: "Breite 900 statt 667")],
        ["TrueTypeCmap"] = [new(WithTrueType(program: SansSymbolCmap), Note: "nicht symbolisch, nur cmap (3,0)")],
        ["TrueTypeEncoding"] = [new(WithTrueType(f => f.Replace("/Encoding/WinAnsiEncoding", "", StringComparison.Ordinal)), Note: "nicht symbolisch ohne /Encoding")],
        ["SymbolicNoEncoding"] = [new(WithTrueType(descriptor: d => d.Replace("/Flags 32", "/Flags 4", StringComparison.Ordinal)), Note: "symbolisch mit /Encoding")],
        ["SymbolicCmap"] = [new(WithTrueType(f => f.Replace("/Encoding/WinAnsiEncoding", "", StringComparison.Ordinal), d => d.Replace("/Flags 32", "/Flags 4", StringComparison.Ordinal)),
            Note: "symbolisch, mehrere cmaps ohne (3,0)")],
        ["UnicodeMapped"] = [new(WithType3("[65/x1/x2]", "/x1 6 0 R/x2 7 0 R"), PdfAConformance.A2u, Note: "Namen ohne Unicode")],
        // ------------------------------------------------------------ 6.7 Logical structure (level a)
        ["Marked"] = [new(Tagged(catalogue: "/Lang(de)"), PdfAConformance.A2a, Note: "getaggt, aber ohne /MarkInfo")],
        ["StructureRoot"] = [new(() => Raw(Page(content: "BT /F1 24 Tf 72 760 Td (Titel) Tj ET BT /F1 11 Tf 72 720 Td (Ein Absatz Text.) Tj ET",
                resources: "/Font<</F1 5 0 R>>", more: Obj(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))), PdfAConformance.A2a,
            Note: "ungetaggt: die Struktur entsteht aus den Textblöcken")],
        ["StructureTypesMapped"] = [new(Tagged(type: "Absatz"), PdfAConformance.A2a, Note: "eigene Strukturart ohne Abbildung")],
        ["RoleMapNotCircular"] = [new(Tagged(type: "Kasten", roleMap: "/RoleMap<</Kasten/Rahmen/Rahmen/Kasten>>"), PdfAConformance.A2a, Note: "Kreis in der Abbildung")],
        ["StandardTypesKept"] = [new(Tagged(roleMap: "/RoleMap<</P/Absatz>>"), PdfAConformance.A2a, Note: "Standardart P umgedeutet")],
        ["LanguageTag"] =
        [
            new(Tagged(catalogue: "/MarkInfo<</Marked true>>/Lang(de_DE)"), PdfAConformance.A2a, Note: "Unterstrich statt Bindestrich: wird de-DE"),
            new(Tagged(element: "/Lang(deutsch sprache)", content: "/P <</MCID 0/Lang(x y)>> BDC " + Rectangle + " EMC"), PdfAConformance.A2a,
                Note: "ungültig in Element und Inhalt: entfällt"),
        ],
        ["PuaActualText"] = [new(WithTrueType(f => f.Replace("/FontDescriptor 6 0 R", "/FontDescriptor 6 0 R/ToUnicode 9 0 R", StringComparison.Ordinal), more: PrivateUseText()),
            PdfAConformance.A2a, Note: "A im privaten Bereich: wird U+FFFD")],
        ["UnicodeValuesU"] = [new(WithTrueType(f => f.Replace("/FontDescriptor 6 0 R", "/FontDescriptor 6 0 R/ToUnicode 9 0 R", StringComparison.Ordinal), more: ByteOrderMarkText()),
            PdfAConformance.A2u, Note: "U+FEFF in /ToUnicode"),
            new(WithTrueType(f => f.Replace("/FontDescriptor 6 0 R", "/FontDescriptor 6 0 R/ToUnicode 9 0 R", StringComparison.Ordinal), more: ZeroText()),
                PdfAConformance.A2u, Note: "Wert 0 in /ToUnicode")],
        ["UnicodeValues"] = [new(WithTrueType(f => f.Replace("/FontDescriptor 6 0 R", "/FontDescriptor 6 0 R/ToUnicode 9 0 R", StringComparison.Ordinal), more: ByteOrderMarkText()),
            PdfAConformance.A4, Note: "U+FEFF in /ToUnicode"),
            new(WithTrueType(f => f.Replace("/FontDescriptor 6 0 R", "/FontDescriptor 6 0 R/ToUnicode 9 0 R", StringComparison.Ordinal), more: ZeroText()),
                PdfAConformance.A4, Note: "Wert 0 in /ToUnicode")],
        ["ActualTextNoPua"] =
        [
            new(File(o => o.With(Stream(4, "", "/Span <</ActualText <FEFFE000>>> BDC " + Rectangle + " EMC"))), PdfAConformance.A4, Note: "im Inhalt"),
            new(File(o => o.With(Stream(4, "", "/Span /P1 BDC " + Rectangle + " EMC"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Properties<</P1<</ActualText <FEFFE0000041>>>>>>>>>"))),
                PdfAConformance.A4, Note: "in einer Eigenschaftsliste"),
        ],
        ["NoNotdef"] =
        [
            new(WithTrueType(f => f.Replace("/Encoding/WinAnsiEncoding", "/Encoding<</Type/Encoding/BaseEncoding/WinAnsiEncoding/Differences[65/.notdef]>>", StringComparison.Ordinal)),
                Note: "einfache Schrift"),
            new(WithCidTrueType(shown: $"<0000{Gid('B'):X4}>"), Note: "CID 0"),
        ],
        ["AnnotationTypes"] =
        [
            new(WithAnnotation("<</Type/Annot/Subtype/Screen/Rect[100 600 200 650]/F 4/AP<</N 6 0 R>>>>", more: RedLook()), Note: "Screen mit Erscheinungsbild"),
            new(WithAnnotation("<</Type/Annot/Subtype/FileAttachment/Rect[100 600 200 650]/F 4/AP<</N 6 0 R>>/FS<</Type/Filespec/F(a.txt)/UF(a.txt)>>>>", more: RedLook()),
                PdfAConformance.A4, Note: "Anhang in PDF/A-4"),
        ],
        ["AnnotationFlags"] = [new(WithSquare(""), Note: "ohne /F")],
        ["AnnotationPrinted"] =
        [
            new(WithSquare("/F 0"), Note: "nicht gedruckt"),
            new(WithSquare("/F 6"), Note: "verborgen"),
        ],
        ["AnnotationAppearance"] =
        [
            new(WithAnnotation("<</Type/Annot/Subtype/FreeText/Rect[100 600 300 650]/F 4/Contents(Hallo)/DA(/Helv 12 Tf 0 0 1 rg)>>"), Note: "FreeText ohne Erscheinungsbild"),
            new(WithAnnotation("<</Type/Annot/Subtype/Square/Rect[100 600 200 650]/F 4>>"), Note: "Quadrat ohne Farben: die Anzeige zeichnet nichts"),
            new(WithAnnotation("<</Type/Annot/Subtype/Square/Rect[100 600 200 650]/F 4/C[1 0 0]/IC[0 0 1]>>"), Note: "Quadrat, das die Anzeige zeichnet"),
            new(WithAnnotation("<</Type/Annot/Subtype/Stamp/Rect[100 600 200 650]/F 4/AP<</D 6 0 R>>>>", more: RedLook()), Note: "nur /D"),
            // a rectangle of width 0 has an area in the sense of 6.3.3; PDF/A-1: a widget needs one even without an area (Fehlergedächtnis 499)
            new(WithAnnotation("<</Type/Annot/Subtype/FileAttachment/Rect[50 600 50 50]/F 4/FS<</Type/Filespec/F(a.txt)>>>>"), Note: "Breite 0, Höhe 550"),
            new(WithAnnotation("<</Type/Annot/Subtype/Widget/FT/Btn/Ff 65536/T(Knopf)/Rect[0 0 0 0]/F 4>>", catalogue: "/AcroForm<</Fields[5 0 R]>>"),
                PdfAConformance.A1b, Note: "Teil 1: Widget ohne Fläche"),
        ],
        ["AppearanceOnlyNormal"] = [new(WithAnnotation("<</Type/Annot/Subtype/Square/Rect[100 600 200 650]/F 4/AP<</N 6 0 R/D 7 0 R>>>>", more: [RedLook(), RedLook(7)]),
            Note: "/N und /D")],
        ["ButtonAppearanceStates"] = [new(WithField(type: "/FT/Btn"), Note: "Schaltfeld mit einem einzelnen Bild")],
        ["AppearanceStream"] = [new(WithAnnotation("<</Type/Annot/Subtype/Square/Rect[100 600 200 650]/F 4/AS/An/AP<</N<</An 6 0 R/Aus 7 0 R>>>>>>",
            more: [RedLook(), Stream(7, "/Type/XObject/Subtype/Form/BBox[0 0 100 50]", "0 0 1 rg 0 0 100 50 re f")]), Note: "Zustände an einem Quadrat")],
        ["WidgetActions"] =
        [
            new(WithField("/A<</S/URI/URI(http://beispiel.invalid)>>"), Note: "/A"),
            new(WithField("/AA<</K 8 0 R>>", more: Script()), Note: "/AA"),
        ],
        ["FieldActions"] = [new(WithAnnotation("<</Type/Annot/Subtype/Widget/F 4/Rect[100 600 200 650]/P 3 0 R/Parent 7 0 R/AP<</N 6 0 R>>>>",
            "/AcroForm<</Fields[7 0 R]>>", "", RedLook(), Obj(7, "<</FT/Tx/T(Feld)/Kids[5 0 R]/AA<</K 8 0 R>>>>"), Script()), Note: "Feld im Feldbaum")],
        ["NeedAppearances"] = [new(WithField(form: "/NeedAppearances true"))],
        ["NoXfa"] = [new(WithField(form: "/XFA 9 0 R", more: Stream(9, "", "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"/>")))],
        ["NoNeedsRendering"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/NeedsRendering true>>"))))],
        ["SignatureCoversFile"] = [new(WithSignature("/ByteRange[0 10 20 30]/Contents<3082>"), Note: "Bereiche decken die Datei nicht")],
        ["SignatureCertificate"] = [new(() => WithSignature($"/ByteRange[0 10 20 30]/Contents<{SignatureBlock(certificate: false)}>")(), Note: "ohne Zertifikat")],
        ["SignatureOneSigner"] = [new(() => WithSignature($"/ByteRange[0 10 20 30]/Contents<{SignatureBlock(signers: 2)}>")(), Note: "zwei Unterzeichner")],
        ["SignatureReferenceDigest"] = [new(WithSignature("/ByteRange[0 10 20 30]/Contents<3082>/Reference[<</Type/SigRef/TransformMethod/DocMDP/DigestMethod/MD5/DigestValue(12345678)>>]",
            "/Perms<</DocMDP 7 0 R>>"), Note: "DocMDP mit eigener Prüfsumme")],
        ["Actions"] =
        [
            new(WithSquare("/F 4/A<</S/Launch/F(programm)>>"), Note: "Launch"),
            new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/OpenAction 8 0 R/Names<</JavaScript<</Names[(a) 8 0 R]>>>>>>")).With(Script())),
                Note: "Skripte des Dokuments"),
            new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Names 9 0 R>>")).With(Script()).With(Obj(9, "<</JavaScript<</Names[(a) 8 0 R]>>>>"))),
                Note: "Skripte im eigenen Namensobjekt"),
            new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/OpenAction 3 0 R>>"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/S/JavaScript/JS(1)>>"))),
                Note: "Öffnungsaktion, die zugleich die Seite ist"),
        ],
        ["NamedActions"] = [new(WithAnnotation("<</Type/Annot/Subtype/Link/Rect[100 600 200 650]/F 4/A<</S/Named/N/Print>>>>"), Note: "Drucken")],
        ["CatalogueTriggers"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/AA<</WC 8 0 R>>>>")).With(Script())))],
        ["PageTriggers"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/AA<</O 8 0 R>>>>")).With(Script())))],
        ["AnnotationTriggers"] = [new(WithSquare("/F 4/AA<</PO 8 0 R/E 8 0 R>>", more: Obj(8, "<</S/URI/URI(http://beispiel.invalid)>>")), PdfAConformance.A4,
            Note: "PO verboten, E erlaubt")],
        ["Metadata"] = [new(File(), Note: "ohne XMP")],
        ["XmpNoBytes"] = [new(WithXmp(Packet("", PacketHeader.Replace(" id=", " bytes=\"1000\" id=", StringComparison.Ordinal))))],
        ["XmpNoEncoding"] = [new(WithXmp(Packet("", PacketHeader.Replace(" id=", " encoding=\"UTF-8\" id=", StringComparison.Ordinal))))],
        ["XmpWellFormed"] = [new(WithXmp(Packet("<rdf:Description rdf:about=\"\">")), Note: "nicht geschlossenes Element")],
        ["XmpUtf8"] = [new(WithXmp([.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(Packet(""))]), Note: "UTF-16")],
        ["XmpSchemas"] =
        [
            new(WithXmp(Packet("<rdf:Description rdf:about=\"\" xmlns:bsp=\"http://beispiel.invalid/ns/\" bsp:Wert=\"1\"/>")), Note: "unbekanntes Schema"),
            new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Metadata 9 0 R>>")).With(Stream(9, "/Type/Metadata/Subtype/XML", Packet("")))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Metadata 10 0 R>>"))
                .With(Stream(10, "/Type/Metadata/Subtype/XML", Packet("<rdf:Description rdf:about=\"\" xmlns:bsp=\"http://beispiel.invalid/ns/\" bsp:Wert=\"1\"/>", identification: "")))),
                Note: "Metadaten der Seite"),
        ],
        ["XmpValueTypes"] =
        [
            new(WithXmp(Packet("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Titel</dc:title></rdf:Description>")),
                Note: "dc:title als einfacher Text"),
            new(WithXmp(Packet("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:CreateDate=\"gestern\"/>")), Note: "Datum"),
        ],
        ["XmpExtensionSchemas"] = [new(WithXmp(Packet(ExtensionSchema(x => x.Replace("<pdfaSchema:prefix>bsp</pdfaSchema:prefix>",
            "<pdfaSchema:prefix>bsp</pdfaSchema:prefix><pdfaSchema:extra>1</pdfaSchema:extra>", StringComparison.Ordinal)))), Note: "Feld, das das Containerschema nicht kennt")],
        ["XmpExtensionContainer"] =
        [
            new(WithXmp(Packet(ExtensionSchema(x => x.Replace("<pdfaSchema:prefix>bsp</pdfaSchema:prefix>", "", StringComparison.Ordinal)))), Note: "ohne Präfix") { Id = "6.6.2.3.3-4" },
            new(WithXmp(Packet(ExtensionSchema(x => x.Replace("<pdfaProperty:category>internal</pdfaProperty:category>",
                "<pdfaProperty:category>sonstig</pdfaProperty:category>", StringComparison.Ordinal)))), Note: "Kategorie") { Id = "6.6.2.3.3-9" },
        ],
        ["Identification"] = [new(WithXmp(Packet("", identification: "")))],
        ["IdentificationPart"] = [new(WithXmp(Packet("", identification: Identified.Replace("pdfaid:part=\"2\"", "pdfaid:part=\"3\"", StringComparison.Ordinal))))],
        ["IdentificationLevel"] = [new(WithXmp(Packet("", identification: Identified.Replace("conformance=\"B\"", "conformance=\"X\"", StringComparison.Ordinal))))],
        ["IdentificationPrefix"] = [new(WithXmp(Packet("", identification: Identified.Replace("pdfaid", "id", StringComparison.Ordinal)
            .Replace("xmlns:id", "xmlns:id", StringComparison.Ordinal))), Note: "Präfix id")],
        ["IdentificationRevision"] = [new(WithXmp(Packet("", identification: "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"4\"/>")),
            PdfAConformance.A4, Note: "ohne pdfaid:rev")],
        ["AttachmentNames"] = [new(WithAttachment("<</Type/Filespec/F(daten.txt)/AFRelationship/Data/EF<</F 10 0 R>>>>"), PdfAConformance.A3b, Note: "ohne /UF")],
        ["AttachmentIsPdfA"] =
        [
            new(WithAttachment(catalogue: ""), Note: "Textdatei in PDF/A-2"),
            new(WithAttachment(catalogue: ""), PdfAConformance.A4, Note: "Textdatei in PDF/A-4"),
        ],
        ["AttachmentMime"] =
        [
            new(WithAttachment(file: "/Type/EmbeddedFile"), PdfAConformance.A3b, Note: "ohne /Subtype"),
            new(WithAttachment(file: "/Type/EmbeddedFile/Subtype/Text"), PdfAConformance.A3b, Note: "kein MIME-Typ"),
            new(WithAttachment(file: ""), PdfAConformance.A3b, Note: "Datenstrom ohne /Type /EmbeddedFile (sie ist freiwillig)"),
        ],
        ["AttachmentRelationship"] = [new(WithAttachment("<</Type/Filespec/F(daten.txt)/UF(daten.txt)/EF<</F 10 0 R>>>>"), PdfAConformance.A3b)],
        ["AttachmentAssociated"] = [new(WithAnnotation("<</Type/Annot/Subtype/Link/Rect[100 600 200 650]/F 4/A<</S/GoToR/D[0/Fit]/F<</Type/Filespec/F(andere.pdf)/UF(andere.pdf)/AFRelationship/Unspecified>>>>>>"),
            PdfAConformance.A3b, Note: "Dateiangabe einer Aktion, keiner /AF-Liste zugeordnet")],
        ["FourFHasAttachment"] = [new(File(), PdfAConformance.A4f, RuleOutcome.Refused, Note: "PDF/A-4f ohne Anhang")],
        ["LayerConfigName"] = [new(WithLayers("<</OCGs[10 0 R]/D<</Order[10 0 R]>>>>"))],
        ["LayerConfigUnique"] = [new(WithLayers("<</OCGs[10 0 R]/D<</Name(Ansicht)/Order[10 0 R]>>/Configs[<</Name(Ansicht)/Order[10 0 R]>>]>>"))],
        ["LayerOrder"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[10 0 R 11 0 R]/D<</Name(Ansicht)/Order[10 0 R]>>>>>>"))
            .With(Obj(10, "<</Type/OCG/Name(Ebene)>>")).With(Obj(11, "<</Type/OCG/Name(Zweite)>>"))), Note: "eine Ebene fehlt in /Order"),
            new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[10 0 R 11 0 R]/D<</Name(Ansicht)/Order[10 0 R 11 0 R]>>/Configs<</Name(Zweite Ansicht)/Order[10 0 R]>>>>>>"))
                .With(Obj(10, "<</Type/OCG/Name(Ebene)>>")).With(Obj(11, "<</Type/OCMD/OCGs[10 0 R]/P/AllOn>>"))),
                Note: "ein OCMD in /OCGs und /Order, /Configs als Wörterbuch (Fehlergedächtnis 510)")],
        ["LayerAutoState"] = [new(WithLayers("<</OCGs[10 0 R]/D<</Name(Ansicht)/Order[10 0 R]/AS[<</Event/Print/Category[/Print]/OCGs[10 0 R]>>]>>>>"))],
        ["NoAlternatePresentations"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Names<</AlternatePresentations<</Names[]>>>>>>"))))],
        ["NoPresSteps"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/PresSteps<</Type/NavNode>>>>"))))],
        ["NoRequirements"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Requirements[<</Type/Requirement/S/EnableJavaScripts>>]>>"))))],
        ["KnownOperators"] = [new(File(o => o.With(Stream(4, "", "BX /X 1 erfunden EX " + Rectangle))), Note: "unbekannter Operator zwischen BX und EX"),
                              new(File(o => o.With(Stream(4, "", "72 600 200 100 re% Kommentar\nf"))), Note: "Kommentar direkt am Operator")],
        ["ResourcesDefined"] =
        [
            new(File(o => o.With(Stream(4, "", Rectangle + " /Im9 Do /G9 gs /S9 sh"))), PdfAConformance.A4, Note: "Bild, Grafikzustand, Verlauf fehlen: nichts gezeichnet"),
            new(File(o => o.With(Stream(4, "", "/OC /oc1 BDC " + Rectangle + " EMC"))), PdfAConformance.A4, Note: "Eigenschaftsliste fehlt: sichtbar"),
            new(File(o => o.With(Stream(4, "", "/X1 cs 0.5 sc " + Rectangle))), PdfAConformance.A4, Note: "Farbraum fehlt: grau"),
            new(File(o => o.With(Stream(4, "", "BT /F9 12 Tf 72 700 Td (Text) Tj ET"))), PdfAConformance.A4, Note: "Schrift fehlt: Ersatzschrift der Anzeige"),
            new(File(o => o.With(Stream(4, "", Rectangle + " BT /F9 Tf ET"))), PdfAConformance.A4, Note: "Schrift ohne Größe, die fehlt: die Anzeige tut nichts damit"),
        ],
        ["InlineFilters"] = [new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /F /LZW ID " +
            Encoding.Latin1.GetString(Lzw([0, 255])) + " EI Q")), Note: "Inline-Bild mit LZW"),
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /F /LZW ID " +
                Encoding.Latin1.GetString(Lzw([0, 255])) + " EI Q")), PdfAConformance.A4, Note: "Teil 4 mit LZW"),
            // a filter not listed for inline images that is not LZW (Fehlergedächtnis 500): unpacked and packed with Flate
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /F /Crypt ID @À EI Q")), Note: "Crypt im Inline-Bild")],
        // PDF/A-4: /L as written — also after repacking (LZW → Flate) and as hexadecimal digits (data holding „EI “) (Fehlergedächtnis 495)
        ["InlineLength"] =
        [
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 ID @À EI Q")), PdfAConformance.A4, Note: "ohne /L"),
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /L 7 ID @À EI Q")), PdfAConformance.A4, Note: "falsches /L"),
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /F /LZW ID " +
                Encoding.Latin1.GetString(Lzw([0, 255])) + " EI Q")), PdfAConformance.A4, Note: "LZW, neu gepackt"),
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 3 /H 1 /CS /G /BPC 8 /L 9 ID EI EI Q")), PdfAConformance.A4,
                Note: "Daten mit „EI “, als Hexziffern geschrieben"),
        ],
        ["OwnResources"] =
        [
            new(File(o => o.With(Stream(4, "", "q 100 0 0 100 72 600 cm /Fm1 Do Q"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Fm1 5 0 R/Im1 6 0 R>>>>>>"))
                .With(Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 1 1]", "/Im1 Do"))
                .With(Stream(6, "/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", "\x80"))),
                Note: "Form ohne eigene Ressourcen"),
            new(File(o => o.With(Stream(4, "", "q 100 0 0 100 72 600 cm /Fm1 Do Q"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Fm1 5 0 R/Im1 6 0 R>>>>>>"))
                .With(Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 1 1]/Resources<</ProcSet[/PDF]>>", "/Im1 Do"))
                .With(Stream(6, "/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", "\x80"))),
                Note: "Form mit eigenen Ressourcen, denen das Bild der Seite fehlt"),
            // the page inherits its resources from the page tree (Fehlergedächtnis 497)
            new(File(o => o.With(Obj(2, "<</Type/Pages/Kids[3 0 R]/Count 1/Resources<</XObject<</Im1 6 0 R>>>>>>"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R>>"))
                .With(Stream(4, "", "q 100 0 0 100 72 600 cm /Im1 Do Q"))
                .With(Stream(6, "/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", ""))),
                Note: "Seite erbt die Ressourcen vom Seitenbaum"),
        ],

        // ------------------------------------------------------------ 6.1 File structure
        ["Header"] = [new(File(header: "%PDF-1.9\n%\xE2\xE3\xCF\xD3"), Note: "Version 1.9"), new(File(header: "%PDF-1.7 \n%\xE2\xE3\xCF\xD3"), Note: "Leerzeichen nach der Version")],
        ["BinaryComment"] = [new(File(header: "%PDF-1.7\n%abcd"), Note: "Kommentar ohne Binärzeichen"),
                             new(File(header: "%PDF-1.7\n\n%\xE2\xE3\xCF\xD3"), Note: "Leerzeile vor dem Kommentar (Fehlergedächtnis 505)")],
        ["FileId"] = [new(File(trailerExtra: ""), Note: "ohne /ID"),
                      new(File(trailerExtra: "/ID[<><>]"), Note: "leere Kennung (Fehlergedächtnis 498)"),
                      new(File(trailerExtra: "/ID[<><>]"), PdfAConformance.A4, Note: "leere Kennung, Teil 4")],
        ["NotEncrypted"] = [new(() => TestPdf.Simple(1, title: null).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "", owner: "rechte"), Note: "AES-256 mit leerem Benutzerpasswort")],
        ["NothingAfterEof"] = [new(File(end: "%%EOF\nRest\n"), Note: "Daten nach %%EOF")],
        ["NoInfo"] = [new(File(o => o.With(Obj(5, "<</Title(Titel)>>")), trailerExtra: "/Info 5 0 R/ID[<01><01>]"), PdfAConformance.A4)],
        ["InfoOnlyModDate"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/PieceInfo<</Programm<</LastModified(D:20260101000000Z)>>>>>>"))
            .With(Obj(5, "<</ModDate(D:20260101000000Z)/Title(Titel)>>")), trailerExtra: "/Info 5 0 R/ID[<01><01>]"), PdfAConformance.A4)],
        ["XrefLineEnd"] = [new(File(xrefKeyword: "xref \n"), Note: "Leerzeichen nach xref"), new(File(xrefKeyword: "xref\n\n"), Note: "zwei Zeilenenden")],
        ["HexEven"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen<ABC>>>"))), Note: "im Objekt"),
                       new(File(o => o.With(Stream(4, "", Rectangle + " BT <ABC> Tj ET"))), Note: "im Inhalt")],
        ["HexDigits"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen<41 4G>>>"))), Note: "im Objekt")],
        ["StreamLength"] = [new(File(o => o.With(new RawObject(4, $"4 0 obj\n<</Length {Rectangle.Length + 1}>>\nstream\n{Rectangle}\nendstream\nendobj"))),
            Note: "/Length zählt das Zeilenende mit")],
        ["StreamKeywords"] = [new(File(o => o.With(new RawObject(4, $"4 0 obj\n<</Length {Rectangle.Length}>>\nstream\r{Rectangle}\nendstream\nendobj"))),
            Note: "nur CR nach stream"), new(File(o => o.With(new RawObject(4, $"4 0 obj\n<</Length {Rectangle.Length}>>\nstream\n{Rectangle}endstream\nendobj"))),
            Note: "endstream ohne Zeilenende")],
        ["NoExternalStream"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Eigen 5 0 R>>")).With(Stream(5, "/F(daten.txt)", "")))
            , Note: "Datenstrom aus einer anderen Datei")],
        ["StandardFilters"] = [new(() => Raw(Page().With(new RawObject(4, "4 0 obj\n<</Filter/LZWDecode/Length " + Lzw(Encoding.ASCII.GetBytes(Rectangle)).Length +
            ">>\nstream\n" + Encoding.Latin1.GetString(Lzw(Encoding.ASCII.GetBytes(Rectangle))) + "\nendstream\nendobj"))), Note: "LZW"),
            new(File(o => o.With(Stream(4, "/Filter/Erfunden", Rectangle))), Note: "unbekanntes Verfahren: die Anzeige zeichnet nichts, die Kopie ist leer"),
            new(File(o => o.With(Stream(4, "/Filter/AHx", Convert.ToHexString(Encoding.ASCII.GetBytes(Rectangle)) + ">"))), Note: "Kurzform AHx (nur für Inline-Bilder)"),
            new(WithImage(Grey + "/Filter/Erfunden", [0x40, 0xC0]), Note: "Bild mit unbekanntem Verfahren: die Platzhalterfläche der Anzeige")],
        ["NamesUtf8"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F9 5 0 R>>>>>>"))
            .With(Obj(5, "<</Type/Font/Subtype/Type1/BaseFont/Gr#FCn>>"))), Note: "Schrift, die keine Seite zeigt, Name in Latin-1 (Fehlergedächtnis 519)"),
            new(File(o => o.With(Stream(4, "", "/Sonder cs 1 scn 72 600 200 100 re f"))
            .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ColorSpace<</Sonder[/Separation/Gr#FCn/DeviceRGB 5 0 R]>>>>>>"))
            .With(Obj(5, "<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[0 1 0]/N 1>>"))), Note: "Sonderfarbe „Grün“ in Latin-1"),
            // a DeviceN colourant in Latin-1, also in /Colorants (Fehlergedächtnis 504)
            new(File(o => o.With(Stream(4, "", "/Sonder cs 1 0 scn 72 600 200 100 re f"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ColorSpace<</Sonder[/DeviceN[/Gr#FCn/Blau]/DeviceRGB 5 0 R 6 0 R]>>>>>>"))
                .With(Stream(5, "/FunctionType 4/Domain[0 1 0 1]/Range[0 1 0 1 0 1]", "{pop 1 exch sub 1 exch}"))
                .With(Obj(6, "<</Colorants<</Gr#FCn[/Separation/Gr#FCn/DeviceRGB 7 0 R]>>>>"))
                .With(Obj(7, "<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[0 1 0]/N 1>>"))), Note: "DeviceN-Farbe „Grün“ in Latin-1, auch in /Colorants")],
        ["ObjectSpacing"] = [new(File(o => o.With(new RawObject(2, "2  0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj"))), Note: "zwei Leerzeichen"),
                             new(File(o => o.With(new RawObject(2, "2 0 obj <</Type/Pages/Kids[3 0 R]/Count 1>> endobj"))), Note: "ohne Zeilenenden"),
                             new(File(o => o.With(new RawObject(2, "2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj", Before: " "))), Note: "ohne Zeilenende davor")],
        ["Permissions"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Perms<</Eigen 5 0 R>>>>")).With(Obj(5, "<</Type/Sig>>")))),
                           new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Perms<</UR3 5 0 R>>>>")).With(Obj(5, "<</Type/Sig>>"))), PdfAConformance.A4,
                               Note: "UR3 in Teil 4 (Fehlergedächtnis 505)")],
        ["CatalogueVersion"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Version/1.7>>"))), PdfAConformance.A4)],
        ["IntegerRange"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen 9999999999>>"))), Note: "im Objekt"),
                            new(File(o => o.With(Stream(4, "", Rectangle + " 9999999999 0 m"))), Note: "im Inhalt")],
        ["RealRange"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen " + new string('9', 40) + ".0>>"))))],
        ["RealNearZero"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen 0." + new string('0', 40) + "1>>"))))],
        ["StringLength"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen(" + new string('x', 40_000) + ")>>"))),
            Outcome: RuleOutcome.Refused, Note: "40 000 Zeichen: kürzen hieße Inhalt verlieren"),
            new(TextPage("BT /F1 1 Tf 20 800 Td (" + new string('x', 40_000) + ") Tj 0 -10 Td [(" + new string('x', 33_000) + ") -200 (xx)] TJ ET"),
                Note: "Text im Inhalt: als mehrere Zeichenketten hintereinander gezeigt")],
        ["NameLength"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen/" + new string('n', 130) + ">>"))),
            Outcome: RuleOutcome.Refused, Note: "ein Name als Wert: umbenennen hieße den Wert ändern"),
            new(File(o => o.With(Stream(4, "", "/" + new string('G', 130) + " gs " + Rectangle))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ExtGState<</" + new string('G', 130) + "<</LW 2>>>>>>>>"))),
                Note: "Name einer Ressource: in Ressourcen und Inhalt gleich umbenannt"),
            new(() => Raw(Page(content: "q 10 0 0 10 72 600 cm BI /W 2 /H 1 /CS /" + new string('C', 130) + " /BPC 8 ID \x40\xC0 EI Q",
                resources: "/ColorSpace<</" + new string('C', 130) + " [/Indexed/DeviceRGB 255 <" + string.Concat(Enumerable.Range(0, 256).Select(i => $"{i:X2}{i:X2}{i:X2}")) + ">]>>")),
                Note: "Farbraum eines Inline-Bilds")],
        ["SaveNesting"] =
        [
            new(File(o => o.With(Stream(4, "", string.Concat(Enumerable.Repeat("q ", 30)) + Rectangle + string.Concat(Enumerable.Repeat(" Q", 30))))),
                Note: "30 Ebenen, die nichts zurücksetzen: die Paare entfallen"),
            new(File(o => o.With(Stream(4, "", string.Concat(Enumerable.Repeat("q 1 0 0 1 2 2 cm 0.02 g ", 40)) + Rectangle + string.Concat(Enumerable.Repeat(" Q", 40)) +
                " 1 0 0 rg 300 300 40 40 re f"))), Note: "40 Ebenen mit Änderungen: der tiefere Teil als Form"),
            new(File(o => o.With(Stream(4, "", string.Concat(Enumerable.Repeat("q 1 0 0 1 2 2 cm ", 40)) + Rectangle))), Note: "40 q ohne Q: die nie zurückgesetzten entfallen"),
            new(File(o => o.With(Stream(4, "", "q 2 0 0 2 100 100 cm /Fm0 Do Q"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Fm0 5 0 R>>>>>>"))
                .With(Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 200 200]", string.Concat(Enumerable.Repeat("q 1 0 0 1 1 1 cm 0.02 g ", 40)) +
                    "0 0 1 rg 0 0 50 50 re f" + string.Concat(Enumerable.Repeat(" Q", 40))))), Note: "eine Form, 40 Ebenen tief, ohne eigene Ressourcen"),
            new(File(o => o.With(Stream(4, "", string.Concat(Enumerable.Repeat("q 1 0 0 1 2 2 cm ", 30)) + "/P <</MCID 0>> BDC " + Rectangle + " EMC" +
                string.Concat(Enumerable.Repeat(" Q", 30))))), Outcome: RuleOutcome.Refused, Note: "markierter Inhalt der Struktur (MCID) in der Tiefe: bleibt im Seiteninhalt"),
        ],
        ["ThreeDStream"] = [new(() => PdfAPartTests.Engineering("VRML"), PdfAConformance.A4e,
            Note: "3D-Daten in VRML: die Anmerkung kommt wie bei PDF/A-4 mit ihrem Erscheinungsbild in den Inhalt")],
        ["PageBoxSize"] =
        [
            new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 20000 842]/Contents 4 0 R/Resources<<>>>>"))),
                Note: "Seite breiter als 14 400: größere Einheit (/UserUnit), die Seite gleich groß"),
            new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Outlines 6 0 R>>"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 20000 842]/CropBox[100 0 19900 842]/TrimBox[200 10 19800 832]/Contents 4 0 R" +
                    "/Resources<<>>/Annots[5 0 R]>>"))
                .With(Stream(4, "", Rectangle + " 1 0 0 rg 15000 300 2000 200 re f"))
                .With(Obj(5, "<</Type/Annot/Subtype/Square/Rect[15000 300 17000 500]/C[0 0.6 0]/F 4/BS<</W 4>>>>"))
                .With(Obj(6, "<</Type/Outlines/First 7 0 R/Last 7 0 R/Count 1>>"))
                .With(Obj(7, "<</Title(Rechts)/Parent 6 0 R/Dest[3 0 R/FitR 15000 300 17000 500]>>"))),
                Note: "mit Rahmen, Anmerkung und Sprungziel: alle im selben Maß"),
            // inherited from the page tree (Fehlergedächtnis 497): too small to write — refused, never an invalid copy
            new(File(o => o.With(Obj(2, "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 2 2]>>"))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>"))), Outcome: RuleOutcome.Refused,
                Note: "geerbter Rahmen 2 × 2 Einheiten"),
        ],
    });

    /// <summary>A page that shows text with Helvetica (the writer embeds the bundled equivalent).</summary>
    private static Func<byte[]> TextPage(string content) => File(o => o.With(Stream(4, "", content))
        .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>"))
        .With(Obj(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>")));

    private static Dictionary<string, RuleCase[]> WithPartOne(Dictionary<string, RuleCase[]> cases)
    {
        foreach (var (name, more) in PartOne()) cases[name] = cases.TryGetValue(name, out var known) ? [.. known, .. more] : more;
        return cases;
    }

    /// <summary>The file of <see cref="Page"/> as an ordinary writer writes it from PDF 1.5 on: objects and cross-references in streams.</summary>
    private static byte[] InStreams()
    {
        using var doc = Core.Pdf.Document.PdfDocument.Open(Raw(Page()));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Version = "1.7", ObjectStreams = true });
        return output.ToArray();
    }

    /// <summary>
    /// A linearized file (its first object the linearization dictionary, /L its length) whose first-page trailer has another /ID than the
    /// last.
    /// </summary>
    private static byte[] Linearized() => LinearizedAfter("");

    /// <summary>The linearized file of the case „FileIdLinearized“ with <paramref name="padding"/> (a comment) before its first object.</summary>
    internal static byte[] LinearizedAfter(string padding)
    {
        var objects = Page();
        objects[0] = objects[0] with { Before = padding + "\n5 0 obj\n<</Linearized 1/L 0000000000/N 1/T 900/O 3/E 800/H[0 0]>>\nendobj\nxref\n0 0\ntrailer\n" +
                                                  "<</Size 6/ID[<AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA><AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA>]>>\nstartxref\n0\n%%EOF\n" };
        var file = Raw(objects);
        Encoding.ASCII.GetBytes($"{file.Length:D10}").CopyTo(file, Encoding.Latin1.GetString(file).IndexOf("/L 0000000000", StringComparison.Ordinal) + 3);
        return file;
    }

    /// <summary>
    /// The cases of PDF/A-1 (ISO 19005-1): its own rules, and the rules of the other parts where PDF/A-1 asks something else (limits of PDF 1.4,
    /// no transparency, no layers and attachments, the character and CID sets, metadata the information dictionary repeats).
    /// </summary>
    /// <summary>Why a flattened copy of PDF/A-1 looks a little different: the translucent area is an image (<see cref="PdfAFlattening"/>).</summary>
    private const string Flattened = "das Durchscheinende als Bild mit 300 dpi über dem Inhalt (PDF/A-1 erlaubt keine Transparenz)";

    private static Dictionary<string, RuleCase[]> PartOne()
    {
        const PdfAConformance One = PdfAConformance.A1b;
        var softMask = Stream(6, "/Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", "\xFF\xFF");
        RawObject Mask(string alpha) => Stream(6, "/Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", alpha);
        var big = string.Join(" ", Enumerable.Repeat("0", 8192));
        var wide = string.Concat(Enumerable.Range(0, 4096).Select(i => $"/K{i} 0"));
        return new Dictionary<string, RuleCase[]>
        {
            ["FileIdLinearized"] = [new(Linearized, One, Note: "Kennung der ersten Seite anders als die letzte")],
            ["XrefSubsections"] = [new(() => Raw(Page(), subsectionSeparator: "  "), One, Note: "zwei Leerzeichen im Kopf des Abschnitts")],
            ["NoXrefStream"] = [new(InStreams, One, Note: "Querverweise und Objekte in Datenströmen (PDF 1.7)")],
            ["NoEmbeddedFiles"] = [new(WithAttachment(catalogue: ""), One, Note: "Textdatei als Anhang: geht, wie in PDF/A-2 jeder Anhang, der kein PDF/A ist")],
            ["ArraySize"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen[" + big + "]>>"))), One,
                RuleOutcome.Refused, Note: "8 192 Einträge: kürzen hieße Inhalt verlieren"),
                new(TextPage("BT /F1 1 Tf 20 700 Td [" + string.Concat(Enumerable.Repeat("(x) -10 ", 4_100)) + "] TJ ET"), One,
                    Note: "TJ mit 8 200 Einträgen: als mehrere TJ"),
                new(() => Raw(Page(catalogue: "/Names<</Dests 5 0 R>>/PageLabels<</Nums[" +
                        string.Concat(Enumerable.Range(0, 4_200).Select(i => $"{i}<</S/D/St {i + 1}>>")) + "]>>",
                    more: Obj(5, "<</Names[" + string.Concat(Enumerable.Range(0, 4_200).Select(i => $"(z{i:D5})[3 0 R/XYZ 0 {i % 800} 0]")) + "]>>"))), One,
                    Note: "Name- und Zahlenbaum mit 8 400 Einträgen: in Kinder mit Grenzen aufgeteilt"),
                new(() => Raw(Page(content: "/P <</MCID 0>> BDC " + Rectangle + " EMC", catalogue: "/StructTreeRoot 5 0 R/MarkInfo<</Marked true>>",
                    page: "/StructParents 0", more:
                    [
                        Obj(5, "<</Type/StructTreeRoot/K 6 0 R/ParentTree 7 0 R/ParentTreeNextKey 1>>"),
                        Obj(6, "<</Type/StructElem/S/Document/P 5 0 R/Pg 3 0 R/K[0 " + string.Join(" ", Enumerable.Range(0, 8_300).Select(i => $"{10 + i} 0 R")) + "]>>"),
                        Obj(7, "<</Nums[0[6 0 R]]>>"),
                        .. Enumerable.Range(0, 8_300).Select(i => Obj(10 + i, "<</Type/StructElem/S/P/P 6 0 R>>")),
                    ])), One, Note: "Strukturelement mit 8 301 Kindern: die Elemente in NonStruct gruppiert, die markierte Stelle bleibt")],
            ["DictionarySize"] = [new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Eigen<<" + wide + ">>>>"))), One,
                RuleOutcome.Refused, Note: "4 096 Einträge")],
            ["NoOptionalContent"] =
            [
                new(WithLayers("<</OCGs[10 0 R]/D<</Name(Ansicht)/Order[10 0 R]>>>>"), One, Note: "eine sichtbare Ebene"),
                new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[10 0 R]/D<</Name(Ansicht)/OFF[10 0 R]>>>>>>"))
                    .With(Stream(4, "", Rectangle + " /OC /E1 BDC 0 1 0 rg 300 600 100 100 re f EMC"))
                    .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Properties<</E1 10 0 R>>>>>>"))
                    .With(Obj(10, "<</Type/OCG/Name(Ebene)>>"))), One, Note: "eine verborgene Ebene: ihr Inhalt geht, wie die Anzeige ihn verbirgt"),
            ],
            ["NoSoftMask"] = [new(File(o => o.With(Stream(4, "", "/GS0 gs " + Rectangle))
                .With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</ExtGState<</GS0<</SMask<</S/Luminosity/G 5 0 R>>>>>>>>>>"))
                .With(Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Group<</S/Transparency/CS/DeviceGray>>", "0.5 g 0 0 595 842 re f"))), One,
                Note: "weiche Maske im Grafikzustand", Look: Flattened)],
            ["NoImageSoftMask"] =
            [
                new(WithImage(Grey + "/SMask 6 0 R", [0x40, 0xC0], softMask), One, Note: "überall deckende Maske: sie geht"),
                new(WithImage(Grey + "/SMask 6 0 R", [0x40, 0xC0], Mask("\xFF\x00")), One, Note: "nur ganz deckend oder durchsichtig: eine Maske aus einem Bit"),
                new(WithImage(Grey + "/SMask 6 0 R", [0x40, 0xC0], Mask("\xFF\x80")), One, Note: "halb durchscheinend", Look: Flattened),
            ],
            ["NoTransparencyGroup"] =
            [
                new(File(o => o.With(Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<<>>/Group<</S/Transparency/CS/DeviceRGB>>>>"))), One,
                    Note: "Seite als Gruppe, nichts durchscheinend darin"),
                new(WithForm("/Group<</S/Transparency/I true/K true>>"), One, Note: "Form als isolierte Knockout-Gruppe, deckender Inhalt"),
            ],
            ["ConstantAlpha"] = [new(WithGraphicsState("<</ca 0.5>>"), One, Note: "Deckkraft 0,5", Look: Flattened),
                                 new(WithGraphicsState("<</CA 0.5>>", "/GS0 gs 72 600 200 100 re S"), One, Note: "Linien mit Deckkraft 0,5", Look: Flattened)],
            ["AnnotationOpacity"] =
            [
                new(WithSquare("/F 4/CA 0.5"), One, Note: "/CA 0,5 mit eigenem Erscheinungsbild: die Anzeige zeichnet es ohne, die Kopie auch"),
                new(WithAnnotation("<</Type/Annot/Subtype/Square/Rect[100 600 200 650]/F 4/C[1 0 0]/IC[0 0 1]/CA 0.5>>"), One,
                    Note: "/CA 0,5 ohne Erscheinungsbild: die Anzeige zeichnet es halb durchscheinend, die Kopie als Bild", Look: Flattened),
            ],
            ["AnnotationColours"] = [new(WithSquare("/F 4/C[1 0 0]"), One, Note: "Farbe ohne RGB-Ausgabebedingung: die Kopie bekommt eine")],
            ["MetadataUnfiltered"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Metadata 9 0 R>>"))
                .With(Stream(9, "/Type/Metadata/Subtype/XML/Filter/ASCIIHexDecode", Convert.ToHexString(Encoding.UTF8.GetBytes(Packet(""))) + ">"))), One,
                Note: "gepackte Metadaten")],
            ["InfoMatchesXmp"] = [new(File(o => o.With(Obj(1, "<</Type/Catalog/Pages 2 0 R/Metadata 9 0 R>>")).With(Obj(5, "<</Title(Bericht)/Author(Max Mustermann)>>"))
                .With(Stream(9, "/Type/Metadata/Subtype/XML", Packet("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt>" +
                    "<rdf:li xml:lang=\"x-default\">Anderer Titel</rdf:li></rdf:Alt></dc:title></rdf:Description>"))),
                trailerExtra: "/Info 5 0 R/ID[<01><01>]"), One, Note: "Titel der Dokumentinfo anders als in den Metadaten")],
            ["BlendModes"] = [new(WithGraphicsState("<</BM/Multiply>>"), One, Note: "Multiplizieren: PDF/A-1 kennt nur Normal", Look: Flattened)],
            ["CharSet"] = [new(WithCff(), One, Note: "Teilschrift ohne /CharSet")],
            ["CidSet"] = [new(WithCidTrueType(cid: c => c.Replace("/BaseFont/LiberationSans", "/BaseFont/ABCDEF+LiberationSans", StringComparison.Ordinal)), One,
                Note: "Teilschrift ohne /CIDSet")],
            ["TrueTypeEncoding"] = [new(WithTrueType(f => f.Replace("/Encoding/WinAnsiEncoding", "/Encoding<</Type/Encoding/BaseEncoding/WinAnsiEncoding/Differences[65/A/B]>>",
                StringComparison.Ordinal)), One, Note: "nicht symbolisch mit /Differences")],
            ["ImageBits"] = [new(WithImage("/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 16", [0x40, 0x00, 0xC0, 0x00]), One, Note: "16 Bit")],
            ["StandardFilters"] = [new(() => WithImage("/Width 40/Height 27/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/JPXDecode", Jp2(Codestream(), colours: Colour(1, 0, 16)))(),
                One, Note: "JPEG 2000, das PDF 1.4 nicht kennt")],
            ["AnnotationTypes"] = [new(WithAnnotation("<</Type/Annot/Subtype/Polygon/Rect[100 600 200 650]/F 4/Vertices[100 600 200 600 150 650]/AP<</N 6 0 R>>>>",
                more: RedLook()), One, Note: "Vieleck, das PDF 1.4 nicht kennt: sein Bild geht in den Seiteninhalt")],
            ["AnnotationPrinted"] = [new(WithAnnotation("<</Type/Annot/Subtype/Popup/Rect[300 600 400 650]/Open false>>"), One, Note: "Pop-up ohne /F")],
            ["DeviceNColourants"] =
            [
                new(() => Raw(Page(content: "/Sh0 sh", resources: "/Shading<</Sh0 5 0 R>>", more:
                [
                    Obj(5, "<</ShadingType 2/ColorSpace 6 0 R/Coords[0 0 595 0]/Function 8 0 R/Extend[true true]>>"),
                    Obj(6, "[/DeviceN[" + Nine + "]/DeviceCMYK 7 0 R]"),
                    Stream(7, "/FunctionType 4/Domain[" + string.Concat(Enumerable.Repeat("0 1 ", 9)) + "]/Range[0 1 0 1 0 1 0 1]", "{ pop pop pop pop pop }"),
                    Stream(8, "/FunctionType 4/Domain[0 1]/Range[" + string.Concat(Enumerable.Repeat("0 1 ", 9)) + "]",
                        "{ dup 1 exch sub 0 0 0 0 0 0 0 9 -1 roll }"),
                ])), One, Note: "Verlauf mit 9 Farben: Funktion und Umrechnung als ein Rechenprogramm"),
                new(() => Raw(Page(content: "/CS0 cs 0.2 0.7 0.1 0 0 0 0 0 0 scn 72 600 200 100 re f /CS0 cs 72 450 100 100 re f /CS1 cs 1 sc 300 600 100 100 re f" +
                        " q 100 0 0 100 72 100 cm /Im0 Do Q /Sh0 sh",
                    resources: "/ColorSpace<</CS0 5 0 R/CS1[/Indexed 5 0 R 1 <000000000000000000FF00FF0000000000000000>]>>/XObject<</Im0 7 0 R>>/Shading<</Sh0 8 0 R>>",
                    more:
                    [
                        Obj(5, "[/DeviceN[" + Nine + "]/DeviceCMYK 6 0 R]"),
                        Stream(6, "/FunctionType 4/Domain[" + string.Concat(Enumerable.Repeat("0 1 ", 9)) + "]/Range[0 1 0 1 0 1 0 1]", "{ pop pop pop pop pop }"),
                        Stream(7, "/Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace 5 0 R/BitsPerComponent 8",
                            "\xFF\0\0\0\0\0\0\0\0\0\xFF\0\0\0\0\0\0\0"),
                        Obj(8, "<</ShadingType 2/ColorSpace 5 0 R/Coords[400 100 550 100]/Function[" +
                            string.Concat(Enumerable.Range(0, 9).Select(i => $"<</FunctionType 2/Domain[0 1]/C0[0]/C1[{i % 3 * 0.5}]/N 1>>")) + "]>>"),
                    ])), One, Note: "9 Farben in Inhalt (auch die Anfangsfarbe), Index-Tabelle, Bild und Verlauf aus Funktionen (abgetastet)"),
                new(() => Raw(Page(content: "q 100 0 0 100 300 300 cm BI /W 2 /H 1 /CS /CS0 /BPC 8 ID \x80\0\x40\0\0\0\0\0\0\0\x80\0\0\0\0\0\0\0 EI Q",
                    resources: "/ColorSpace<</CS0 5 0 R>>", more:
                    [
                        Obj(5, "[/DeviceN[" + Nine + "]/DeviceCMYK 6 0 R]"),
                        Stream(6, "/FunctionType 4/Domain[" + string.Concat(Enumerable.Repeat("0 1 ", 9)) + "]/Range[0 1 0 1 0 1 0 1]", "{ pop pop pop pop pop }"),
                    ])), One, Note: "Inline-Bild mit 9 Farben", Look: "in 8 Bit der Ersatzfarbe: einzelne Stufen anders"),
            ],
            ["UnicodeMapped"] = [new(WithType3("[65/x1/x2]", "/x1 6 0 R/x2 7 0 R"), PdfAConformance.A1a, Note: "PDF/A-1a: Namen ohne Unicode")],
            ["StructureRoot"] = [new(() => Raw(Page(content: "BT /F1 24 Tf 72 760 Td (Titel) Tj ET BT /F1 11 Tf 72 720 Td (Ein Absatz Text.) Tj ET",
                resources: "/Font<</F1 5 0 R>>", more: Obj(5, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))), PdfAConformance.A1a,
                Note: "PDF/A-1a, ungetaggt")],
            ["IccProfileValid"] = [new(() => Raw(Page(catalogue: "/OutputIntents[<</Type/OutputIntent/S/GTS_PDFA1/OutputConditionIdentifier(x)/DestOutputProfile 5 0 R>>]",
                more: Profile(5, Srgb, 4))), One, Note: "Ausgabeprofil RGB mit /N 4"),
                new(WithColourSpace("[/ICCBased 5 0 R]", "0.2 0.5 0.9", Profile(5, Version4Rgb(), 3)), One,
                    Note: "Profil der Version 4 aus Kurven und Matrix: dieselben Farben als Version 2.1")],
        };
    }

    /// <summary>Rules no crafted file can show in a test, with the reason; the check and the writer handle them all the same.</summary>
    public static IReadOnlyDictionary<string, string> Uncraftable { get; } = new Dictionary<string, string>
    {
        ["ObjectCount"] = "mehr als 8 388 607 Objekte — zu groß für einen Test",
    };
}
