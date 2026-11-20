using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Ocr;

/// <summary>
/// The recognised text as an invisible layer over the page (KONZEPT.md, section 5.9): a form XObject in the page's default space whose text
/// is drawn in rendering mode 3 (neither filled nor stroked), each word stretched over its box — searching, selecting and copying then work
/// in every reader, the page looks the same. The font has no outlines (every glyph empty) and maps its codes to the text with /ToUnicode;
/// the form carries the marker <see cref="Marker"/>, so the program recognises its own layer again.
/// </summary>
public static class OcrTextLayer
{
    /// <summary>The key in the form dictionary that marks it as a text layer of this program.</summary>
    public static readonly PdfName Marker = PdfName.Get("PDFK_Ocr");   // a second-class name (ISO 32000-2, Annex E.2; No. 599)

    /// <summary>Name of the font (in the file, never shown).</summary>
    private const string FontName = "PDFKneadOcrText";

    /// <summary>Advance of every glyph in 1/1000 em: a word of n characters is n / 2 units long before it is stretched over its box.</summary>
    private const int Advance = 500;

    /// <summary>Builds the layer of a page.</summary>
    /// <param name="lines">The lines with the word boxes in the page's default space (y upwards; see <see cref="ToPage"/>).</param>
    /// <param name="box">The area of the form (the page's media box).</param>
    /// <returns>The form, or null if there is no word.</returns>
    public static PdfStream? Form(IReadOnlyList<OcrLine> lines, RectD box)
    {
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var content = new StringBuilder("BT\n3 Tr\n/F0 1 Tf\n");
        var any = false;
        foreach (var line in lines)
            for (var w = 0; w < line.Words.Count; w++)
            {
                var word = line.Words[w];
                var elements = Elements(word.Text);
                if (elements.Count == 0 || word.Corners is not { Length: 4 } c) continue;
                // the space after a word belongs to it: copying gives "word word", and the selection runs on without gaps
                var next = w + 1 < line.Words.Count ? line.Words[w + 1].Corners : null;
                if (next is { Length: 4 }) elements.Add(" ");
                var (left, right) = next is { Length: 4 } ? (c[3], next[3]) : (c[3], c[2]);
                var top = c[0];
                double ux = right.X - left.X, uy = right.Y - left.Y, vx = top.X - left.X, vy = top.Y - left.Y;
                var length = elements.Count * Advance / 1000.0;
                var m = new[] { ux / length, uy / length, vx, vy, left.X, left.Y };
                if (m.Any(v => !double.IsFinite(v)) || Math.Abs(ux * vy - uy * vx) < 1e-9) continue;
                foreach (var v in m) content.Append(Number(v)).Append(' ');
                content.Append("Tm <");
                foreach (var e in elements)
                {
                    if (!codes.TryGetValue(e, out var code))
                    {
                        if (codes.Count >= 0xFFFE) break;
                        codes[e] = code = codes.Count + 1;
                    }
                    content.Append(code.ToString("X4", CultureInfo.InvariantCulture));
                }
                content.Append("> Tj\n");
                any = true;
            }
        if (!any) return null;
        content.Append("ET\n");

        var resources = new PdfDictionary();
        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F0"), Font(codes));
        resources.Set(PdfName.Get("Font"), fonts);
        var form = new PdfDictionary();
        form.Set(PdfName.Type, PdfName.Get("XObject"));
        form.Set(PdfName.Subtype, PdfName.Get("Form"));
        form.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(box.Left), new PdfReal(box.Top), new PdfReal(box.Right), new PdfReal(box.Bottom)]));
        form.Set(PdfName.Resources, resources);
        form.Set(Marker, PdfBoolean.True);
        return new PdfStream(form, Encoding.ASCII.GetBytes(content.ToString()));
    }

    /// <summary>
    /// Turns lines from the pixels of a page image into the page's default space.
    /// </summary>
    /// <param name="lines">The lines in image pixels.</param>
    /// <param name="imageToPage">The matrix from image pixels to the page's default space (the inverse of the one the page was rendered with).</param>
    /// <returns>The lines in page space.</returns>
    public static List<OcrLine> ToPage(IEnumerable<OcrLine> lines, Matrix imageToPage) =>
    [
        .. lines.Select(l => l with
        {
            Words = [.. l.Words.Select(w => w with { Corners = [.. w.Corners.Select(p => imageToPage.Transform(p.X, p.Y)).Select(p => new OcrPoint(p.X, p.Y))] })],
        }),
    ];

    /// <summary>Share of the page an image must cover before the page is worth reading: smaller ones are logos and symbols.</summary>
    private const double ImageShare = 0.02;

    /// <summary>Share of a word's box covered by real visible text from which the word counts as text the page has already.</summary>
    private const double CoveredShare = 0.4;

    /// <summary>Whether a page shows an image of some size — without one, there is nothing to read that is not text already.</summary>
    /// <param name="list">The display list of the page.</param>
    /// <returns>True if an image covers at least 2 % of the page.</returns>
    public static bool ShowsImages(DisplayList list)
    {
        var page = list.PageBox;
        var area = Math.Abs(page.Width * page.Height);
        foreach (ref readonly var item in list.Items.Span)
            if (item.Kind == DisplayItemKind.Image)
            {
                double left = Math.Max(item.Left, page.Left), right = Math.Min(item.Right, page.Right);
                double top = Math.Max(item.Top, page.Top), bottom = Math.Min(item.Bottom, page.Bottom);
                if (right > left && bottom > top && (right - left) * (bottom - top) >= ImageShare * area) return true;
            }
        return false;
    }

    /// <summary>
    /// The lines without the words that lie on real visible text of the page: that is searchable already, and a second, recognised copy
    /// would make every search find it twice (a scan with a page number or stamp added as text).
    /// </summary>
    /// <param name="lines">The recognised lines in page space.</param>
    /// <param name="glyphs">The text of the page (<see cref="DisplayList.Text"/>); invisible text does not count.</param>
    /// <returns>The lines that still have words.</returns>
    public static List<OcrLine> WithoutText(IReadOnlyList<OcrLine> lines, IReadOnlyList<TextGlyph> glyphs)
    {
        var boxes = new List<RectD>();
        foreach (var g in glyphs)
            if (g.Visible)
                boxes.Add(RectD.FromLtrb(Math.Min(Math.Min(g.X0, g.X1), Math.Min(g.X2, g.X3)), Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3)),
                    Math.Max(Math.Max(g.X0, g.X1), Math.Max(g.X2, g.X3)), Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3))));
        if (boxes.Count == 0) return [.. lines];
        var index = new AreaIndex(boxes);
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var words = line.Words.Where(w => !Covered(w, index)).ToList();
            if (words.Count == line.Words.Count) result.Add(line);
            else if (words.Count > 0) result.Add(line with { Words = words });
        }
        return result;
    }

    private static bool Covered(OcrWord word, AreaIndex index)
    {
        var c = word.Corners;
        var box = RectD.FromLtrb(c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
        var area = box.Width * box.Height;
        if (!(area > 0)) return false;
        double covered = 0;
        index.Any(box, 0, g =>
        {
            double w = Math.Min(g.Right, box.Right) - Math.Max(g.Left, box.Left), h = Math.Min(g.Bottom, box.Bottom) - Math.Max(g.Top, box.Top);
            if (w > 0 && h > 0) covered += w * h;
            return covered >= CoveredShare * area;
        });
        return covered >= CoveredShare * area;
    }

    /// <summary>The characters of a word as the reader sees them (a letter with its accents is one).</summary>
    private static List<string> Elements(string text)
    {
        var result = new List<string>(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
            if (e.GetTextElement() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s) && !s.Any(char.IsControl)) result.Add(s);
        return result;
    }

    private static string Number(double value) => PdfKnead.Core.Pdf.Writing.PdfSerializer.FormatReal(Math.Round(value, 4));

    /// <summary>The font: Type 0 over a TrueType without outlines, every code to glyph 1, the text from /ToUnicode.</summary>
    private static PdfDictionary Font(Dictionary<string, int> codes)
    {
        var file = new PdfDictionary();
        file.Set(PdfName.Get("Length1"), PdfInteger.Of(Glyphless.Length));
        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get(FontName));
        descriptor.Set(PdfName.Get("Flags"), PdfInteger.Of(4));   // symbolic: its glyphs are not those of a standard character set
        descriptor.Set(PdfName.Get("FontBBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(Advance), PdfInteger.Of(1000)]));
        descriptor.Set(PdfName.Get("ItalicAngle"), PdfInteger.Of(0));
        descriptor.Set(PdfName.Get("Ascent"), PdfInteger.Of(1000));
        // ISO 32000-2, 9.8.1 (Table 120): a negative number — the smallest one; the glyphs reach no lower (docs/FEHLERGEDAECHTNIS.md, No. 773)
        descriptor.Set(PdfName.Get("Descent"), PdfInteger.Of(-1));
        descriptor.Set(PdfName.Get("CapHeight"), PdfInteger.Of(1000));
        descriptor.Set(PdfName.Get("StemV"), PdfInteger.Of(80));
        descriptor.Set(PdfName.Get("FontFile2"), new PdfStream(file, Glyphless));

        var systemInfo = new PdfDictionary();
        systemInfo.Set(PdfName.Get("Registry"), new PdfString("Adobe"u8.ToArray()));
        systemInfo.Set(PdfName.Get("Ordering"), new PdfString("Identity"u8.ToArray()));
        systemInfo.Set(PdfName.Get("Supplement"), PdfInteger.Of(0));

        // every code shows glyph 1 (empty, the advance of all): two bytes per code
        var map = new byte[(codes.Count + 1) * 2];
        for (var i = 1; i <= codes.Count; i++) map[i * 2 + 1] = 1;
        var cid = new PdfDictionary();
        cid.Set(PdfName.Type, PdfName.Get("Font"));
        cid.Set(PdfName.Subtype, PdfName.Get("CIDFontType2"));
        cid.Set(PdfName.Get("BaseFont"), PdfName.Get(FontName));
        cid.Set(PdfName.Get("CIDSystemInfo"), systemInfo);
        cid.Set(PdfName.Get("FontDescriptor"), descriptor);
        cid.Set(PdfName.Get("DW"), PdfInteger.Of(Advance));
        cid.Set(PdfName.Get("CIDToGIDMap"), new PdfStream(new PdfDictionary(), map));

        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Get("Font"));
        font.Set(PdfName.Subtype, PdfName.Get("Type0"));
        font.Set(PdfName.Get("BaseFont"), PdfName.Get(FontName));
        font.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        font.Set(PdfName.Get("DescendantFonts"), new PdfArray([cid]));
        font.Set(PdfName.Get("ToUnicode"), ElementFace.ToUnicode(codes.ToDictionary(e => e.Value, e => e.Key)));
        return font;
    }

    /// <summary>The TrueType program: two empty glyphs (.notdef and the one every code shows), each 500 units wide, 1000 units per em.</summary>
    internal static byte[] Glyphless { get; } = BuildGlyphless();

    private static byte[] BuildGlyphless()
    {
        static byte[] Table(int length, Action<Span<byte>> fill)
        {
            var bytes = new byte[length];
            fill(bytes);
            return bytes;
        }
        static void U16(Span<byte> b, int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(b[at..], (ushort)v);
        static void U32(Span<byte> b, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b[at..], v);
        var head = Table(54, b =>
        {
            U32(b, 0, 0x00010000);
            U32(b, 4, 0x00010000);
            U32(b, 12, 0x5F0F3CF5);
            U16(b, 16, 0x000B);                 // baseline and left side bearing at 0, integer scaling
            U16(b, 18, 1000);                   // units per em
            U16(b, 40, Advance);                // xMax
            U16(b, 42, 1000);                   // yMax
            U16(b, 46, 3);                      // smallest readable size
            U16(b, 48, 2);                      // direction hint
        });
        var hhea = Table(36, b =>
        {
            U32(b, 0, 0x00010000);
            U16(b, 4, 1000);                    // ascender
            U16(b, 10, Advance);                // widest advance
            U16(b, 18, 1);                      // caret upright
            U16(b, 34, 2);                      // number of metrics
        });
        var maxp = Table(32, b =>
        {
            U32(b, 0, 0x00010000);
            U16(b, 4, 2);                       // glyphs
            U16(b, 14, 2);                      // zones
        });
        var hmtx = Table(8, b =>
        {
            U16(b, 0, Advance);
            U16(b, 4, Advance);
        });
        var cmap = Table(4 + 8 + 24, b =>
        {
            U16(b, 2, 1);                       // one subtable: Windows, Unicode BMP, format 4 with only the closing segment
            U16(b, 4, 3);
            U16(b, 6, 1);
            U32(b, 8, 12);
            U16(b, 12, 4);
            U16(b, 14, 24);
            U16(b, 18, 2);                      // 2 × segments
            U16(b, 20, 2);                      // search range
            U16(b, 24, 0);                      // range shift
            U16(b, 26, 0xFFFF);                 // end code
            U16(b, 30, 0xFFFF);                 // start code
            U16(b, 32, 1);                      // delta: 0xFFFF → glyph 0
        });
        var nameText = Encoding.BigEndianUnicode.GetBytes(FontName);
        var name = Table(6 + 2 * 12 + 2 * nameText.Length, b =>
        {
            U16(b, 2, 2);
            U16(b, 4, 6 + 2 * 12);
            for (var i = 0; i < 2; i++)
            {
                var r = 6 + i * 12;
                U16(b, r, 3);
                U16(b, r + 2, 1);
                U16(b, r + 4, 0x409);
                U16(b, r + 6, i == 0 ? 1 : 6);  // family, PostScript name
                U16(b, r + 8, nameText.Length);
                U16(b, r + 10, i * nameText.Length);
            }
            nameText.CopyTo(b[(6 + 2 * 12)..]);
            nameText.CopyTo(b[(6 + 2 * 12 + nameText.Length)..]);
        });
        var os2 = Table(96, b =>
        {
            U16(b, 0, 4);
            U16(b, 2, Advance);                 // average width
            U16(b, 4, 400);                     // regular
            U16(b, 6, 5);                       // normal width
            U16(b, 62, 0x40);                   // regular
            U16(b, 64, 0x20);                   // first and last character
            U16(b, 66, 0xFFFF);
            U16(b, 68, 1000);                   // typographic ascender
            U16(b, 74, 1000);                   // Windows ascent
            U16(b, 86, Advance);                // x height
            U16(b, 88, 1000);                   // cap height
            U16(b, 92, 0x20);                   // break character
        });
        var post = Table(32, b =>
        {
            U32(b, 0, 0x00030000);              // no glyph names
            U16(b, 8, unchecked((ushort)-100)); // underline
            U16(b, 10, 50);
            U32(b, 12, 1);                      // fixed pitch
        });
        return TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]>
        {
            ["head"] = head, ["hhea"] = hhea, ["maxp"] = maxp, ["hmtx"] = hmtx, ["cmap"] = cmap, ["name"] = name, ["OS/2"] = os2, ["post"] = post,
            ["loca"] = new byte[6], ["glyf"] = new byte[4],
        });
    }
}
