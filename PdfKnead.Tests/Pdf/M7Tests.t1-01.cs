using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7 (text and fonts), package t1-01: Type 1 Font Format, chapters 1–3 (prefix T1). The helpers in <see cref="T1Paket"/>
/// serve the packages t1-01 … t1-03.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// Type 1 programs for the packages t1-01 … t1-03: clear text, eexec part with Private, Subrs and CharStrings, trailer; charstrings written
    /// in a small assembler ("0 600 hsbw 100 100 rmoveto …"; a number with the prefix L is written in the five-byte form, #n is the raw byte n).
    /// </summary>
    private static class T1Paket
    {
        /// <summary>Subrs 0–3 as the flex and hint replacement conventions have them, and subroutine 4 (the square of glyph A).</summary>
        public static readonly string[] DefaultSubrs =
        [
            "3 0 callothersubr pop pop setcurrentpoint return",
            "0 1 callothersubr return",
            "0 2 callothersubr return",
            "return",
            "300 0 rlineto 0 300 rlineto -300 0 rlineto return",
        ];

        public const string NotDef = "0 250 hsbw endchar";

        /// <summary>A triangle like the A of <see cref="Type1Builder"/>: sidebearing 50, width 600, apex at (300, 700).</summary>
        public const string Triangle = "50 600 hsbw 0 0 rmoveto 250 700 rlineto 250 -700 rlineto closepath endchar";

        /// <summary>The glyphs of <see cref="Type1Sample"/>: A through subroutine 4, B and C plain squares.</summary>
        public static readonly (string Name, string Code)[] DefaultGlyphs =
        [
            (".notdef", NotDef),
            ("A", "0 600 hsbw 100 100 rmoveto 4 callsubr closepath endchar"),
            ("B", "0 600 hsbw 50 50 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar"),
            ("C", "0 600 hsbw 20 20 rmoveto 200 0 rlineto 0 200 rlineto closepath endchar"),
        ];

        /// <summary>The 512 zeros and cleartomark after the eexec part.</summary>
        public static readonly string Zeros = string.Concat(Enumerable.Repeat(new string('0', 64) + "\n", 8)) + "cleartomark\n";

        public sealed record Spec
        {
            public string Header { get; init; } = "%!PS-AdobeFont-1.0: KneadTest 001";
            public string FontMatrix { get; init; } = "[0.001 0 0 0.001 0 0]";
            public string FontBBox { get; init; } = "{0 0 1000 1000}";
            public string ClearExtra { get; init; } = "";
            public string Encoding { get; init; } =
                "256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 65 /A put\ndup 66 /B put\ndup 67 /C put\nreadonly def";
            /// <summary>lenIV in the private part; null writes no entry (the charstrings then have the default of four leading bytes).</summary>
            public int? LenIV { get; init; } = 4;
            public string PrivateExtra { get; init; } = "";
            /// <summary>Null writes no /Subrs at all.</summary>
            public string[]? Subrs { get; init; } = DefaultSubrs;
            public (string Name, string Code)[] Glyphs { get; init; } = DefaultGlyphs;
            public bool NoCharStrings { get; init; }
            /// <summary>Text inside the CharStrings dictionary after the glyphs, before its "end".</summary>
            public string CharStringsExtra { get; init; } = "";
            /// <summary>Text after the "end" of the CharStrings dictionary.</summary>
            public string AfterCharStrings { get; init; } = "";
            public string RD { get; init; } = "RD";
            public string ND { get; init; } = "ND";
            public string NP { get; init; } = "NP";
            /// <summary>The eexec part in hexadecimal form: eight digits, then line breaks and spaces.</summary>
            public bool Hex { get; init; }
            public string Trailer { get; init; } = Zeros;
        }

        /// <summary>Builds the program; the lengths are those of its clear text and its eexec part (/Length1, /Length2).</summary>
        public static (byte[] Data, int Clear, int Encrypted) Build(Spec? spec = null)
        {
            spec ??= new Spec();
            var lenIV = spec.LenIV ?? 4;
            var priv = new MemoryStream();
            void Text(string t) => priv.Write(Encoding.Latin1.GetBytes(t));
            void Binary(string source)
            {
                var plain = Charstring(source);
                var bytes = lenIV < 0 ? plain : Type1Sample.Encrypt([.. new byte[lenIV], .. plain], 4330);
                Text($"{bytes.Length} {spec.RD} ");
                priv.Write(bytes);
            }
            Text("dup /Private 8 dict dup begin\n");
            if (!spec.RD.Contains(' ')) Text($"/{spec.RD}{{string currentfile exch readstring pop}}executeonly def\n");
            if (!spec.ND.Contains(' ')) Text($"/{spec.ND}{{noaccess def}}executeonly def\n");
            if (!spec.NP.Contains(' ')) Text($"/{spec.NP}{{noaccess put}}executeonly def\n");
            if (spec.LenIV is { } iv) Text($"/lenIV {iv} def\n");
            Text(spec.PrivateExtra);
            if (spec.Subrs is { } subrs)
            {
                Text($"/Subrs {subrs.Length} array\n");
                for (var i = 0; i < subrs.Length; i++)
                {
                    Text($"dup {i} ");
                    Binary(subrs[i]);
                    Text($" {spec.NP}\n");
                }
                Text($"{spec.ND}\n");
            }
            if (!spec.NoCharStrings)
            {
                Text($"2 index /CharStrings {spec.Glyphs.Length} dict dup begin\n");
                foreach (var (name, code) in spec.Glyphs)
                {
                    Text($"/{name} ");
                    Binary(code);
                    Text($" {spec.ND}\n");
                }
                Text(spec.CharStringsExtra);
                Text("end\n");
            }
            Text(spec.AfterCharStrings);
            Text("end\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");

            var clear = Encoding.Latin1.GetBytes(spec.Header + "\n12 dict begin\n/FontName /KneadTest def\n/FontType 1 def\n/FontMatrix " + spec.FontMatrix +
                " readonly def\n/FontBBox " + spec.FontBBox + " readonly def\n" + spec.ClearExtra + "/Encoding " + spec.Encoding +
                "\ncurrentdict end\ncurrentfile eexec\n");
            var encrypted = Type1Sample.Encrypt([0, 0, 0, 0, .. priv.ToArray()], 55665);
            if (spec.Hex)
            {
                var hex = Convert.ToHexString(encrypted);
                var text = new StringBuilder(hex[..8]);
                for (var i = 8; i < hex.Length; i += 2)
                {
                    text.Append(hex, i, 2);
                    var bytes = i / 2 + 1;
                    if (bytes % 32 == 0) text.Append("\r\n");
                    else if (bytes % 8 == 0) text.Append(' ');
                }
                text.Append('\n');
                encrypted = Encoding.ASCII.GetBytes(text.ToString());
            }
            var trailer = Encoding.Latin1.GetBytes(spec.Trailer);
            return ([.. clear, .. encrypted, .. trailer], clear.Length, encrypted.Length);
        }

        public static Type1Font Parse(Spec? spec = null) => Type1Font.Parse(Build(spec).Data);

        /// <summary>A program with .notdef and one glyph "A" of the given charstring (and the default subroutines).</summary>
        public static Type1Font WithGlyph(string code, string[]? subrs = null) =>
            Parse(new Spec { Glyphs = [(".notdef", NotDef), ("A", code)], Subrs = subrs ?? DefaultSubrs });

        private static readonly Dictionary<string, byte[]> Ops = new(StringComparer.Ordinal)
        {
            ["hstem"] = [1], ["vstem"] = [3], ["vmoveto"] = [4], ["rlineto"] = [5], ["hlineto"] = [6], ["vlineto"] = [7], ["rrcurveto"] = [8],
            ["closepath"] = [9], ["callsubr"] = [10], ["return"] = [11], ["hsbw"] = [13], ["endchar"] = [14], ["rmoveto"] = [21],
            ["hmoveto"] = [22], ["vhcurveto"] = [30], ["hvcurveto"] = [31],
            ["dotsection"] = [12, 0], ["vstem3"] = [12, 1], ["hstem3"] = [12, 2], ["seac"] = [12, 6], ["sbw"] = [12, 7], ["div"] = [12, 12],
            ["callothersubr"] = [12, 16], ["pop"] = [12, 17], ["setcurrentpoint"] = [12, 33],
        };

        /// <summary>The plain bytes of a charstring.</summary>
        public static byte[] Charstring(string source)
        {
            var bytes = new List<byte>();
            foreach (var token in source.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Ops.TryGetValue(token, out var op)) bytes.AddRange(op);
                else if (token[0] == '#') bytes.Add(byte.Parse(token[1..], System.Globalization.CultureInfo.InvariantCulture));
                else if (token[0] == 'L') Long(bytes, int.Parse(token[1..], System.Globalization.CultureInfo.InvariantCulture));
                else
                {
                    var v = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
                    if (v is >= -107 and <= 107) bytes.Add((byte)(v + 139));
                    else if (v is >= 108 and <= 1131) bytes.AddRange([(byte)((v - 108) / 256 + 247), (byte)((v - 108) % 256)]);
                    else if (v is >= -1131 and <= -108) bytes.AddRange([(byte)((-v - 108) / 256 + 251), (byte)((-v - 108) % 256)]);
                    else Long(bytes, v);
                }
            }
            return [.. bytes];
        }

        private static void Long(List<byte> bytes, int v) => bytes.AddRange([255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

        /// <summary>The decrypted private part (eexec key, four leading bytes) of a program with the given lengths.</summary>
        public static string Private(byte[] data, int clear, int encrypted) =>
            Encoding.Latin1.GetString(Type1Sample.Decrypt(data.AsSpan(clear, encrypted), 55665, 4));

        /// <summary>The points of an outline as pairs.</summary>
        public static (float X, float Y)[] Points(GlyphOutline outline) =>
            [.. Enumerable.Range(0, outline.Points.Count / 2).Select(i => (outline.Points[2 * i], outline.Points[2 * i + 1]))];

        /// <summary>The same verbs, points and width.</summary>
        public static void SameGlyph(Type1Font expected, Type1Font actual, string name, string? actualName = null)
        {
            actualName ??= name;
            Assert.False(expected.Outline(name).IsEmpty, name);
            Assert.Equal(expected.Outline(name).Verbs, actual.Outline(actualName).Verbs);
            Assert.Equal(Points(expected.Outline(name)), Points(actual.Outline(actualName)));
            Assert.Equal(expected.AdvanceWidth(name), actual.AdvanceWidth(actualName));
        }

        /// <summary>
        /// A page of 200 × 200 with the program embedded as FontFile of /F1 (object 5, nonsymbolic, no /Widths, no /Encoding unless
        /// <paramref name="fontExtra"/> gives one) and the given content.
        /// </summary>
        public static byte[] Pdf((byte[] Data, int Clear, int Encrypted) program, string content, string fontExtra = "")
        {
            var data = Encoding.Latin1.GetString(program.Data);
            return DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
                $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
                $"<< /Type /Font /Subtype /Type1 /BaseFont /KneadTest /FontDescriptor 6 0 R {fontExtra}>>",
                "<< /Type /FontDescriptor /FontName /KneadTest /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 " +
                "/CapHeight 700 /StemV 80 /FontFile 7 0 R >>",
                $"<< /Length {data.Length} /Length1 {program.Clear} /Length2 {program.Encrypted} " +
                $"/Length3 {program.Data.Length - program.Clear - program.Encrypted} >>\nstream\n{data}\nendstream");
        }

        public static PdfFont Font(PdfDocument doc) => PdfFont.Get((PdfDictionary)doc.GetObject(5), doc);

        /// <summary>The verbs of every filled entry of the display list.</summary>
        public static List<PathVerb[]> Fills(DisplayList list) =>
            [.. list.Items.ToArray().Where(i => i.Kind == DisplayItemKind.Fill).Select(i =>
            {
                var (verb, count, _) = list.Paths[i.Path];
                return list.Verbs.AsSpan(verb, count).ToArray();
            })];

        /// <summary>The page rendered at 72 dpi (y counted from the top).</summary>
        public static SKBitmap Render(byte[] file)
        {
            using var doc = PdfDocument.Open(file);
            var page = doc.Pages[0];
            return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        }

        public static bool Dark(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Red: < 100, Green: < 100, Blue: < 100 };
    }

    /// <summary>
    /// T1 1.1 sentences 7–9, 6 sentence 4, 6.4 sentences 73–108, 6.5 sentences 1–9 and 14–25, 8.1 sentences 1–3, 8.2 sentences 1–10: the hints of
    /// a charstring (hstem, vstem, hstem3, vstem3, dotsection — overlapping, as ghost stems, in pairs or alone, before and inside the outline) are
    /// read over: the glyph has the same points, verbs and width as without them (PDF-Knead draws without hinting).
    /// </summary>
    [Theory]
    [InlineData("0 50 hstem 0 50 vstem 0 50 300 50 600 50 hstem3 0 50 200 50 400 50 vstem3 dotsection", "dotsection 10 20 hstem", "")]
    [InlineData("0 50 hstem 0 50 vstem", "", "")]
    [InlineData("0 50 hstem 40 50 hstem", "", "")]
    [InlineData("0 50 300 50 600 50 hstem3", "", "")]
    [InlineData("0 50 vstem 40 50 vstem", "", "")]
    [InlineData("0 50 200 50 400 50 vstem3", "", "")]
    [InlineData("dotsection", "", "dotsection")]
    [InlineData("dotsection", "", "")]
    [InlineData("0 50 hstem 40 50 hstem 0 50 vstem", "", "")]
    [InlineData("700 -20 hstem 0 21 hstem", "", "")]
    [InlineData("0 700 hstem", "", "")]
    public void Type1_HintsInCharstrings_LeaveOutlineAndWidthUnchanged(string before, string inside, string after)
    {
        var plain = T1Paket.WithGlyph(T1Paket.Triangle);
        var hinted = T1Paket.WithGlyph($"50 600 hsbw {before} 0 0 rmoveto 250 700 rlineto {inside} 250 -700 rlineto closepath {after} endchar");
        T1Paket.SameGlyph(plain, hinted, "A");
    }

    /// <summary>
    /// T1 1.3 sentences 7–10: entries a reader does not know, in the clear text and in the private part, and a callothersubr with an unknown number
    /// (whose arguments pop returns) are passed over: name, encoding, outline and width as without them.
    /// </summary>
    [Fact]
    public void Type1_FutureEntriesAndUnknownOtherSubrs_AreSkipped()
    {
        var plain = T1Paket.Parse(new T1Paket.Spec { Glyphs = [(".notdef", T1Paket.NotDef), ("A", "50 600 hsbw 0 0 rmoveto 250 700 rlineto 250 -700 rlineto closepath endchar")] });
        var future = T1Paket.Parse(new T1Paket.Spec
        {
            ClearExtra = "/Zukunft [1 2 3] def\n",
            PrivateExtra = "/Zukunft [1 2 3] def\n",
            Glyphs = [(".notdef", T1Paket.NotDef), ("A", "50 600 hsbw 0 0 2 27 callothersubr pop pop rmoveto 250 700 rlineto 250 -700 rlineto closepath endchar")],
        });
        Assert.Equal(plain.Name, future.Name);
        Assert.Equal(plain.Encoding, future.Encoding);
        T1Paket.SameGlyph(plain, future, "A");
    }

    /// <summary>
    /// T1 2.1 sentence 9 (in PDF ISO 32000-2, 9.6.5.2): a font is re-encoded by its encoding alone — the embedded program under
    /// /Encoding &lt;&lt; /Differences [65 /B] &gt;&gt; draws the outline of B for code 65, and code 66 stays B.
    /// </summary>
    [Fact]
    public void Type1_DifferencesReencodeWithoutChangingTheProgram()
    {
        var sample = Type1Sample.Build();
        using var doc = PdfDocument.Open(T1Paket.Pdf(sample, "BT /F1 12 Tf 10 10 Td (AB) Tj ET", "/Encoding << /Differences [65 /B] >> "));
        var font = T1Paket.Font(doc);
        var b = Type1Font.Parse(sample.Data).Outline("B");
        Assert.Equal(b.Bounds, font.Glyph(65, 1).Outline.Bounds);
        Assert.Equal(b.Bounds, font.Glyph(66, 1).Outline.Bounds);
        Assert.NotEqual(Type1Font.Parse(sample.Data).Outline("A").Bounds, font.Glyph(65, 1).Outline.Bounds);
    }

    /// <summary>
    /// T1 2.1 sentence 12 and 6.4 sentences 13–15 with ISO 32000-2 9.9.1 sentences 69–70: for a program embedded in PDF, PaintType is passed
    /// over and the text rendering mode decides — /PaintType 2 and /StrokeWidth 40 in the clear text: text in mode 0 fills the glyph and nothing
    /// is stroked, as two of the three reference readers draw it (measured, probes p20a–p20d: docs/FEHLERGEDAECHTNIS.md, No. 1163; the third
    /// strokes with the program's stroke width).
    /// </summary>
    [Fact]
    public void Type1_PaintType2_PassedOverTheTextModeDecides()
    {
        var program = T1Paket.Build(new T1Paket.Spec { ClearExtra = "/PaintType 2 def\n/StrokeWidth 40 def\n" });
        using var doc = PdfDocument.Open(T1Paket.Pdf(program, "BT /F1 100 Tf 0 Tr 20 50 Td (A) Tj ET"));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Single(T1Paket.Fills(list));
        Assert.DoesNotContain(list.Items.ToArray(), i => i.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke);
    }

    /// <summary>
    /// T1 2.1 sentences 13–17: the reader does not trust the program — a subroutine that calls itself ends at the depth limit with an empty
    /// glyph (no stack overflow, quickly), and a charstring whose length reaches past the data makes the program unreadable
    /// (FontFormatException "Charstring außerhalb der Daten").
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Type1_SelfCallingSubroutineAndCharstringPastTheEnd_FailSafely()
    {
        var watch = Stopwatch.StartNew();
        var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 0 callsubr 100 0 rlineto closepath endchar", ["0 callsubr"]);
        Assert.True(font.Outline("A").IsEmpty);
        Assert.Equal(0, font.AdvanceWidth("A"));
        Assert.True(watch.ElapsedMilliseconds < 1000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");

        var broken = T1Paket.Build(new T1Paket.Spec { CharStringsExtra = "/Z 9999 RD abc ND\n" });
        var e = Assert.Throws<FontFormatException>(() => Type1Font.Parse(broken.Data));
        Assert.Equal("Charstring außerhalb der Daten", e.Message);
    }

    /// <summary>
    /// T1 2.2 sentences 1–3: CharStrings is required — a program without it in the eexec part is not read (FontFormatException), and the font
    /// counts as not embedded (FontProgram.FromBytes gives null).
    /// </summary>
    [Fact]
    public void Type1_WithoutCharStrings_IsNotAProgram()
    {
        var program = T1Paket.Build(new T1Paket.Spec { NoCharStrings = true });
        Assert.Throws<FontFormatException>(() => Type1Font.Parse(program.Data));
        Assert.Null(FontProgram.FromBytes(program.Data));
    }

    /// <summary>
    /// T1 2.3 sentences 11–13: the first line may also have the other header form ("%!FontType1-1.1: …"); the program reads the same and its name
    /// comes from /FontName.
    /// </summary>
    [Fact]
    public void Type1_OtherHeaderForm_ReadsTheSame()
    {
        var adobe = T1Paket.Parse();
        var other = T1Paket.Parse(new T1Paket.Spec { Header = "%!FontType1-1.1: Anders 001" });
        Assert.Equal("KneadTest", other.Name);
        Assert.Equal(adobe.Encoding, other.Encoding);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(adobe, other, name);
    }

    /// <summary>T1 2.3 sentences 14–22: a %%VMusage comment (for loading into a printer) changes nothing — name, encoding and glyphs as without it.</summary>
    [Fact]
    public void Type1_VmUsageComment_IsSkipped()
    {
        var plain = T1Paket.Parse();
        var commented = T1Paket.Parse(new T1Paket.Spec { Header = "%!PS-AdobeFont-1.0: KneadTest 001\n%%VMusage: 30000 40000" });
        Assert.Equal(plain.Name, commented.Name);
        Assert.Equal(plain.Encoding, commented.Encoding);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(plain, commented, name);
    }

    /// <summary>The glyphs of an accented character: A (sidebearing 50), acute (sidebearing 30) and Aacute built with seac.</summary>
    private static (string Name, string Code)[] T1PaketSeacGlyphs(string aacute = "50 600 hsbw 30 200 150 65 194 seac", string acuteHsbw = "30 300 hsbw") =>
    [
        (".notdef", T1Paket.NotDef),
        ("A", T1Paket.Triangle),
        ("acute", $"{acuteHsbw} 0 750 rmoveto 100 100 rlineto 20 -20 rlineto closepath endchar"),
        ("Aacute", aacute),
        ("B", "0 600 hsbw 50 50 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar"),
    ];

    /// <summary>
    /// T1 2.3 sentences 26–30 and 6.4 sentence 32: PDF-Knead neither caches nor clips by the FontBBox of the program — with /FontBBox {0 0 0 0}
    /// the seac character still has both parts, the base and the accent.
    /// </summary>
    [Fact]
    public void Type1_SeacWithZeroFontBBox_KeepsBothParts()
    {
        var font = T1Paket.Parse(new T1Paket.Spec { FontBBox = "{0 0 0 0}", Glyphs = T1PaketSeacGlyphs() });
        var aacute = font.Outline("Aacute");
        Assert.Equal(2, aacute.Verbs.Count(v => v == PathVerb.Move));
        var points = T1Paket.Points(aacute);
        Assert.Contains((300f, 700f), points);    // apex of the A
        Assert.Contains((300f, 1000f), points);   // tip of the accent
    }

    /// <summary>
    /// T1 2.3 sentences 37–39 and 7.2 sentences 3, 9–10: the eexec part may be hexadecimal, with line breaks and spaces after the first eight
    /// digits; it reads like the binary form (same glyphs and widths), and the cleaned program (binary again) reads the same.
    /// </summary>
    [Fact]
    public void Type1_HexadecimalEexec_ReadsAndCleansLikeBinary()
    {
        var binary = T1Paket.Parse();
        var hex = T1Paket.Build(new T1Paket.Spec { Hex = true });
        var text = Encoding.Latin1.GetString(hex.Data, hex.Clear, hex.Encrypted);
        Assert.Contains("\r\n", text);
        Assert.Contains(' ', text);
        Assert.DoesNotContain(' ', text[..8]);
        var read = Type1Font.Parse(hex.Data);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(binary, read, name);

        var (cleaned, _, _, _) = Type1Scrubber.Scrub(hex.Data, new HashSet<string> { "A", "B", "C" }, hex.Clear, hex.Encrypted);
        var again = Type1Font.Parse(cleaned);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(binary, again, name);
    }

    /// <summary>
    /// T1 2.3 sentences 40–43: after the encrypted part come 512 zeros and cleartomark. Cleaned without the lengths of the font file, the trailer
    /// is exactly the eight lines of zeros with cleartomark, unchanged at the end, and the third length is its length.
    /// </summary>
    [Fact]
    public void Type1_CleaningWithoutLengths_KeepsTheTrailerAsItIs()
    {
        var sample = Type1Sample.Build();
        var trailer = sample.Data.AsSpan(sample.Clear + sample.Encrypted).ToArray();
        Assert.Equal(T1Paket.Zeros, Encoding.ASCII.GetString(trailer));
        var (data, clear, encrypted, length3) = Type1Scrubber.Scrub(sample.Data, new HashSet<string> { "B" });
        Assert.Equal(trailer.Length, length3);
        Assert.Equal(trailer, data.AsSpan(clear + encrypted).ToArray());
    }

    /// <summary>
    /// T1 2.4 sentences 28–29, 10.3 sentences 6–7 and 11–13, Appendix 1 sentences 39–41 and 45–46: RD, ND and NP may have the short names made of
    /// hyphen and bar (-| |- |); the glyphs read as with RD, and cleaning removes the glyphs not kept as it does with RD — the result reads again.
    /// </summary>
    [Fact]
    public void Type1_ShortProcedureNames_ReadAndCleanLikeRD()
    {
        var plain = T1Paket.Parse();
        var program = T1Paket.Build(new T1Paket.Spec { RD = "-|", ND = "|-", NP = "|" });
        Assert.Matches(@"/A \d+ -\| ", T1Paket.Private(program.Data, program.Clear, program.Encrypted));
        var shortNames = Type1Font.Parse(program.Data);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(plain, shortNames, name);

        var (data, _, _, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "B" }, program.Clear, program.Encrypted);
        var cleaned = Type1Font.Parse(data);
        T1Paket.SameGlyph(plain, cleaned, "B");
        Assert.False(cleaned.HasGlyph("A"));
        Assert.False(cleaned.HasGlyph("C"));
    }

    /// <summary>
    /// T1 2.4 sentences 36–39: written out as "noaccess def" and "noaccess put" instead of ND and NP, an entry still leaves the program whole when
    /// cleaning — name, length, charstring and both words — and the result reads.
    /// </summary>
    [Fact]
    public void Type1_NoaccessSpelledOut_LeavesWithTheEntry()
    {
        var program = T1Paket.Build(new T1Paket.Spec { ND = "noaccess def", NP = "noaccess put" });
        var before = T1Paket.Private(program.Data, program.Clear, program.Encrypted);
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "B" }, program.Clear, program.Encrypted);
        var after = T1Paket.Private(data, clear, encrypted);
        static int Count(string text) => Regex.Count(text, "noaccess def");
        Assert.Equal(Count(before) - 2, Count(after));
        Assert.DoesNotContain("/A ", after);
        Assert.DoesNotContain("/C ", after);
        Assert.Contains("/B ", after);
        var cleaned = Type1Font.Parse(data);
        Assert.False(cleaned.Outline("B").IsEmpty);
        Assert.False(cleaned.HasGlyph("A"));
    }

    /// <summary>
    /// T1 2.4 sentences 45–46: every program has .notdef — cleaning keeps it, and it stays glyph number 0; a program without .notdef draws nothing
    /// for a code without a glyph, without an exception.
    /// </summary>
    [Fact]
    public void Type1_Notdef_StaysFirstAndAMissingOneDrawsNothing()
    {
        var sample = Type1Sample.Build();
        var (data, _, _, _) = Type1Scrubber.Scrub(sample.Data, new HashSet<string> { "B" }, sample.Clear, sample.Encrypted);
        var cleaned = Type1Font.Parse(data);
        Assert.True(cleaned.HasGlyph(".notdef"));
        Assert.Equal(".notdef", cleaned.GlyphNameAt(0));

        var program = T1Paket.Build(new T1Paket.Spec { Glyphs = [.. T1Paket.DefaultGlyphs.Skip(1)] });
        Assert.False(Type1Font.Parse(program.Data).HasGlyph(".notdef"));
        using var doc = PdfDocument.Open(T1Paket.Pdf(program, "BT /F1 100 Tf 20 50 Td (D) Tj ET"));
        Assert.True(T1Paket.Font(doc).Glyph('D', 1).Outline.IsEmpty);
        Assert.Empty(T1Paket.Fills(PageInterpreter.Build(doc, doc.Pages[0])));
    }

    /// <summary>
    /// T1 2.5 sentences 1–3 and 5–6: UniqueID serves caches across jobs, which PDF-Knead does not keep — differing values in the clear text and the
    /// private part change nothing in reading.
    /// </summary>
    [Fact]
    public void Type1_UniqueIdInBothDictionaries_DoesNotChangeReading()
    {
        var plain = T1Paket.Parse();
        var withIds = T1Paket.Parse(new T1Paket.Spec { ClearExtra = "/UniqueID 4123456 def\n", PrivateExtra = "/UniqueID 4999999 def\n" });
        Assert.Equal(plain.Name, withIds.Name);
        Assert.Equal(plain.Encoding, withIds.Encoding);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(plain, withIds, name);
    }

    /// <summary>
    /// T1 3.3 sentences 1–6: the character space maps to text space by the FontMatrix — with [0.002 0 0 0.002 0 0] the matrix is read so, and the
    /// glyph through the PDF font is twice as large and twice as wide as with 0.001.
    /// </summary>
    [Fact]
    public void Type1_FontMatrix_ScalesGlyphsAndWidths()
    {
        var normal = T1Paket.Build();
        var doubled = T1Paket.Build(new T1Paket.Spec { FontMatrix = "[0.002 0 0 0.002 0 0]" });
        Assert.Equal(0.002, Type1Font.Parse(doubled.Data).FontMatrix.A, 9);
        using var normalDoc = PdfDocument.Open(T1Paket.Pdf(normal, "BT /F1 12 Tf 10 10 Td (A) Tj ET"));
        using var doubledDoc = PdfDocument.Open(T1Paket.Pdf(doubled, "BT /F1 12 Tf 10 10 Td (A) Tj ET"));
        var a = T1Paket.Font(normalDoc).Glyph(65, 1);
        var b = T1Paket.Font(doubledDoc).Glyph(65, 1);
        var boundsA = a.ToText.TransformBounds(a.Outline.Bounds);
        var boundsB = b.ToText.TransformBounds(b.Outline.Bounds);
        Assert.Equal(2 * (boundsA.Right - boundsA.Left), boundsB.Right - boundsB.Left, 6);
        Assert.Equal(2 * (boundsA.Bottom - boundsA.Top), boundsB.Bottom - boundsB.Top, 6);
        Assert.Equal(0.6, a.Width, 9);
        Assert.Equal(1.2, b.Width, 9);
    }

    /// <summary>
    /// T1 3.3 sentence 7 and 6.4 sentence 109: fractions come from div — "145 10 div 0 rmoveto" after hsbw 0 starts at x = 14.5; a divisor of 0
    /// gives 0 without an exception.
    /// </summary>
    [Fact]
    public void Type1_Div_GivesFractionsAndZeroForADivisorOfZero()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 145 10 div 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((14.5f, 0f), T1Paket.Points(font.Outline("A"))[0]);
        var zero = T1Paket.WithGlyph("0 600 hsbw 145 0 div 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((0f, 0f), T1Paket.Points(zero.Outline("A"))[0]);
    }

    /// <summary>
    /// T1 3.3 sentences 14–17: a FontMatrix that slants ([0.001 0 0.0002 0.001 0 0]) changes coordinates and widths together — the apex of the
    /// triangle (300, 700) moves 0.14 em to the right in text space, the width stays 0.6.
    /// </summary>
    [Fact]
    public void Type1_ObliqueFontMatrix_ShearsTheOutlineOnly()
    {
        var glyphs = new[] { (".notdef", T1Paket.NotDef), ("A", T1Paket.Triangle) };
        using var upright = PdfDocument.Open(T1Paket.Pdf(T1Paket.Build(new T1Paket.Spec { Glyphs = glyphs }), "BT /F1 12 Tf 10 10 Td (A) Tj ET"));
        using var oblique = PdfDocument.Open(T1Paket.Pdf(T1Paket.Build(new T1Paket.Spec { Glyphs = glyphs, FontMatrix = "[0.001 0 0.0002 0.001 0 0]" }),
            "BT /F1 12 Tf 10 10 Td (A) Tj ET"));
        var a = T1Paket.Font(upright).Glyph(65, 1);
        var b = T1Paket.Font(oblique).Glyph(65, 1);
        Assert.Equal(0.3, a.ToText.Transform(300, 700).X, 9);
        Assert.Equal(0.44, b.ToText.Transform(300, 700).X, 9);
        Assert.Equal(0.7, b.ToText.Transform(300, 700).Y, 9);
        Assert.Equal(0.6, b.Width, 9);
        Assert.Equal(a.Width, b.Width, 9);
    }

    /// <summary>T1 3.3 sentences 18–20: the limit of ±2000 binds the program; the reader draws a line of 3000 units to x = sbx + 3000.</summary>
    [Fact]
    public void Type1_CoordinatesBeyond2000_AreDrawn()
    {
        var font = T1Paket.WithGlyph("40 600 hsbw 0 0 rmoveto 3000 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal(3040, font.Outline("A").Bounds.Right, 3);
    }

    /// <summary>
    /// T1 3.4 sentences 9–20: the limit of 1500 path elements binds printers; the reader draws a glyph of 3000 lines whole. Its own limit is the
    /// work per glyph: more than 200 000 steps give an empty outline (the exception is caught).
    /// </summary>
    [Fact]
    public void Type1_ManyPathElementsDrawnAndTheWorkLimitEmptiesTheGlyph()
    {
        var many = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto " + string.Concat(Enumerable.Repeat("1 1 rlineto ", 3000)) + "closepath endchar");
        Assert.Equal(3000, many.Outline("A").Verbs.Count(v => v == PathVerb.Line));
        var tooMuch = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto " + string.Concat(Enumerable.Repeat("1 1 rlineto ", 70_000)) + "closepath endchar");
        Assert.True(tooMuch.Outline("A").IsEmpty);
    }

    /// <summary>
    /// T1 3.5 sentences 1–8: an inner contour running against the outer one (outer counter-clockwise, inner clockwise) stays empty when the glyph
    /// is drawn (non-zero rule); running in the same direction it is filled.
    /// </summary>
    [Fact]
    public void Type1_InnerContourAgainstTheOuterDirection_StaysOpen()
    {
        const string outer = "0 600 hsbw 100 100 rmoveto 400 0 rlineto 0 400 rlineto -400 0 rlineto closepath ";
        var against = T1Paket.Build(new T1Paket.Spec { Glyphs = [(".notdef", T1Paket.NotDef), ("A", outer + "100 -300 rmoveto 0 200 rlineto 200 0 rlineto 0 -200 rlineto closepath endchar")] });
        var along = T1Paket.Build(new T1Paket.Spec { Glyphs = [(".notdef", T1Paket.NotDef), ("A", outer + "100 -300 rmoveto 200 0 rlineto 0 200 rlineto -200 0 rlineto closepath endchar")] });
        // size 200: the outer square spans 20–100 pt, the inner 40–80 pt
        using var open = T1Paket.Render(T1Paket.Pdf(against, "BT /F1 200 Tf 0 0 Td (A) Tj ET"));
        using var filled = T1Paket.Render(T1Paket.Pdf(along, "BT /F1 200 Tf 0 0 Td (A) Tj ET"));
        Assert.True(T1Paket.Dark(open, 30, 170));
        Assert.False(T1Paket.Dark(open, 60, 140));
        Assert.True(T1Paket.Dark(filled, 30, 170));
        Assert.True(T1Paket.Dark(filled, 60, 140));
    }

    /// <summary>T1 3.6 sentences 1–9: two overlapping subpaths of the same direction, in text mode 0, fill completely without a gap where they overlap.</summary>
    [Fact]
    public void Type1_OverlappingSubpaths_FillWithoutGap()
    {
        var program = T1Paket.Build(new T1Paket.Spec
        {
            Glyphs = [(".notdef", T1Paket.NotDef), ("A", "0 600 hsbw 100 100 rmoveto 300 0 rlineto 0 300 rlineto -300 0 rlineto closepath " +
                "150 -150 rmoveto 300 0 rlineto 0 300 rlineto -300 0 rlineto closepath endchar")],
        });
        // size 200: the squares span 20–80 pt and 50–110 pt, they overlap at 50–80 pt
        using var bitmap = T1Paket.Render(T1Paket.Pdf(program, "BT /F1 200 Tf 0 Tr 0 0 Td (A) Tj ET"));
        Assert.True(T1Paket.Dark(bitmap, 65, 135));
        Assert.True(T1Paket.Dark(bitmap, 30, 170));
        Assert.True(T1Paket.Dark(bitmap, 100, 100));
    }
}
