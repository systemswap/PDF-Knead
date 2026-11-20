using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Font for page elements: a font file shipped with the program (or a system font), embedded as a composite font (Type0,
/// CIDFontType2 or, for CFF outlines, CIDFontType0; Identity-H — code = glyph number). This makes every character of the font
/// available, not just the 256 of a simple encoding. For display there is one font dictionary per font with the whole file
/// (shared by all elements, so the renderer builds the font once); on saving, the writer replaces it with a subset of the
/// glyphs used.
/// </summary>
/// <remarks>All static members are thread-safe.</remarks>
public sealed class ElementFace
{
    private static readonly Dictionary<ElementFont, ElementFace> Faces = [];
    private static readonly Dictionary<string, ElementFace> Named = new(StringComparer.Ordinal);
    private static readonly Lock FacesGate = new();

    private readonly byte[] _data;
    private PdfDictionary? _displayFont;
    private bool _fixedPitch, _serif, _italic;

    private ElementFace(string file) : this(EmbeddedFonts.StandardOpenType(file), Path.GetFileNameWithoutExtension(file)) { }

    private ElementFace(OpenTypeFont font, string name)
    {
        Font = font;
        _data = Font.FileData;   // the same file, not loaded a second time
        var units = (double)Font.UnitsPerEm;
        Ascent = Font.Ascent / units;
        Descent = Font.Descent / units;
        BaseName = Font.PostScriptName.Length > 0 ? Font.PostScriptName : name;
    }

    /// <summary>
    /// Font for formatted text (see KONZEPT.md, section 5.14), loaded once per key: a style of Liberation or a system font.
    /// <paramref name="load"/> supplies the font (a single file, not a collection); the traits are written into the descriptor.
    /// </summary>
    internal static ElementFace For(string key, Func<OpenTypeFont> load, bool fixedPitch, bool serif, bool italic)
    {
        lock (FacesGate)
        {
            if (Named.TryGetValue(key, out var face))
            {
                face._lastUse = ++_clock;
                return face;
            }
            face = new ElementFace(load(), key) { Key = key, _fixedPitch = fixedPitch, _serif = serif, _italic = italic };
            Named[key] = face;
            face._lastUse = ++_clock;
            _namedBytes += face._data.Length;
            // Budget: trying out many system fonts must not keep all their files in memory. Elements that use an evicted font keep
            // it alive themselves; new text loads it again
            while (_namedBytes > MaxNamedBytes && Named.Count > 1)
            {
                var oldest = Named.Values.Where(f => f != face).MinBy(f => f._lastUse)!;
                Named.Remove(oldest.Key!);
                _namedBytes -= oldest._data.Length;
            }
            return face;
        }
    }

    /// <summary>The font for <paramref name="key"/> if it is still loaded; otherwise null.</summary>
    internal static ElementFace? Cached(string key)
    {
        lock (FacesGate)
        {
            if (!Named.TryGetValue(key, out var face)) return null;
            face._lastUse = ++_clock;
            return face;
        }
    }

    private const long MaxNamedBytes = 96L << 20;
    private static long _namedBytes, _clock;
    private long _lastUse;

    /// <summary>Key of fonts for formatted text (<see cref="For(string, Func{OpenTypeFont}, bool, bool, bool)"/>); otherwise null.</summary>
    internal string? Key { get; private init; }

    /// <summary>Gets the font for one of the built-in element fonts (Liberation Sans, Serif or Mono), loaded once.</summary>
    /// <param name="font">The element font.</param>
    /// <returns>The shared font instance.</returns>
    public static ElementFace For(ElementFont font)
    {
        lock (FacesGate)
        {
            if (Faces.TryGetValue(font, out var face)) return face;
            face = new ElementFace(font switch
            {
                ElementFont.Serif => "LiberationSerif-Regular.ttf",
                ElementFont.Mono => "LiberationMono-Regular.ttf",
                _ => "LiberationSans-Regular.ttf",
            })
            { Kind = font, _fixedPitch = font == ElementFont.Mono, _serif = font == ElementFont.Serif };
            Faces[font] = face;
            return face;
        }
    }

    /// <summary>The built-in element font; for fonts for formatted text the default value.</summary>
    public ElementFont Kind { get; private init; }

    /// <summary>The parsed font program.</summary>
    public OpenTypeFont Font { get; }

    /// <summary>PostScript name of the font, or the file or key name if the font has none.</summary>
    public string BaseName { get; }

    /// <summary>Ascender in em units (1 = font size).</summary>
    public double Ascent { get; }

    /// <summary>Descender in em units (1 = font size); usually negative.</summary>
    public double Descent { get; }

    /// <summary>Determines whether the font has every character of a text (white space and control characters do not count).</summary>
    /// <param name="text">The text to check.</param>
    /// <returns><see langword="true"/> if no character maps to glyph 0.</returns>
    public bool Covers(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune) && Glyph(rune.Value) == 0) return false;
        return true;
    }

    /// <summary>
    /// Outlines in CFF format (OpenType "OTTO": system fonts, Noto CJK): embedded as CIDFontType0 with FontFile3/CIDFontType0C
    /// instead of TrueType, because a TrueType subset of such a font makes saving fail.
    /// </summary>
    private bool IsCff => Font.Cff is not null;

    /// <summary>
    /// The CFF table as a separate array for display, once per file and table: the language variants of Noto CJK share one
    /// table of 15 MB, and a copy per variant would cost an extra 31 MB once Japanese and Korean text has been typed.
    /// </summary>
    private byte[] SharedCff()
    {
        var tables = CffCopies.GetValue(_data, _ => []);
        var segment = CffData();
        lock (tables)
            return tables.TryGetValue(segment.Offset, out var copy) ? copy : tables[segment.Offset] = segment.ToArray();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Dictionary<int, byte[]>> CffCopies = new();

    /// <summary>The CFF table within the font file.</summary>
    private ArraySegment<byte> CffData() =>
        Font.Table("CFF ") is { } t ? new ArraySegment<byte>(_data, t.Offset, t.Length) : throw new FontFormatException("ohne CFF-Tabelle");

    /// <summary>Gets the glyph of a character; a tab maps to the glyph of a space.</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    /// <returns>The glyph number; 0 if the font lacks the character.</returns>
    public int Glyph(int codePoint) => codePoint == '\t' ? Font.GlyphForUnicode(' ') : Font.GlyphForUnicode(codePoint);

    /// <summary>Gets the advance width of a glyph.</summary>
    /// <param name="glyph">The glyph number.</param>
    /// <returns>The advance in em units.</returns>
    public double Advance(int glyph) => Font.AdvanceWidth(glyph) / Font.UnitsPerEm;

    /// <summary>Width in thousandths of an em (the unit of /W), to three decimals: whole numbers deviate from the advance by up
    /// to 0.5, which would put a correction in TJ between almost every pair of glyphs.</summary>
    internal double Width1000(int glyph) => Math.Round(Font.AdvanceWidth(glyph) * 1000.0 / Font.UnitsPerEm, 3);

    private PdfObject WidthObject(int glyph) => Width1000(glyph) is var w && w == Math.Floor(w) ? PdfInteger.Of((long)w) : new PdfReal(w);

    /// <summary>Display font (whole file, all widths), built on first use and shared by all elements with this font.</summary>
    public PdfDictionary DisplayFont
    {
        get
        {
            lock (FacesGate)
            {
                if (_displayFont is not null) return _displayFont;
                var widths = new PdfArray();
                for (var g = 0; g < Font.GlyphCount; g++) widths.Add(WidthObject(g));
                // CFF outlines (OpenType "OTTO", Noto CJK): the CFF data itself as CIDFontType0C, code = glyph
                return _displayFont = Type0(BaseName, IsCff ? SharedCff() : _data, new PdfArray([PdfInteger.Of(0), widths]), null);
            }
        }
    }

    /// <summary>
    /// Builds the font for saving: a subset with the given glyphs and widths for these glyphs only.
    /// </summary>
    /// <param name="glyphs">Glyph numbers mapped to their text, for ToUnicode (copying and searching in other programs).</param>
    /// <returns>A new Type0 font dictionary with the embedded subset.</returns>
    public PdfDictionary SavedFont(IReadOnlyDictionary<int, string> glyphs)
    {
        var used = glyphs.Keys.Order().ToList();
        var subset = IsCff ? CffSubsetter.Subset(CffData(), used) : TrueTypeSubsetter.Subset(_data, used);
        var widths = new PdfArray();
        for (var i = 0; i < used.Count;)
        {
            // Consecutive glyphs in one list: "5 [600 600 556]"
            var start = i;
            var list = new PdfArray();
            do list.Add(WidthObject(used[i++]));
            while (i < used.Count && used[i] == used[i - 1] + 1);
            widths.Add(PdfInteger.Of(used[start]));
            widths.Add(list);
        }
        return Type0(SubsetTag(used) + "+" + BaseName, subset, widths, ToUnicode(glyphs));
    }

    /// <summary>Six capital letters derived from the glyphs (9.9.2): the same subset gets the same name.</summary>
    private static string SubsetTag(List<int> glyphs)
    {
        uint hash = 2166136261;
        foreach (var g in glyphs) hash = (hash ^ (uint)g) * 16777619;
        var tag = new char[6];
        for (var i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + hash % 26);
            hash /= 26;
        }
        return new string(tag);
    }

    private PdfDictionary Type0(string name, byte[] fontFile, PdfArray widths, PdfStream? toUnicode)
    {
        var units = 1000.0 / Font.UnitsPerEm;
        var head = Font.TableData("head");
        PdfArray bbox = head.Length >= 54
            ? new PdfArray([
                PdfInteger.Of((long)Math.Round(BinaryPrimitives.ReadInt16BigEndian(head[36..]) * units)),
                PdfInteger.Of((long)Math.Round(BinaryPrimitives.ReadInt16BigEndian(head[38..]) * units)),
                PdfInteger.Of((long)Math.Round(BinaryPrimitives.ReadInt16BigEndian(head[40..]) * units)),
                PdfInteger.Of((long)Math.Round(BinaryPrimitives.ReadInt16BigEndian(head[42..]) * units)),
            ])
            : new PdfArray([PdfInteger.Of(-200), PdfInteger.Of(-300), PdfInteger.Of(1200), PdfInteger.Of(1000)]);
        var os2 = Font.TableData("OS/2");
        var capHeight = os2.Length >= 90 && BinaryPrimitives.ReadUInt16BigEndian(os2) >= 2
            ? BinaryPrimitives.ReadInt16BigEndian(os2[88..]) * units
            : Ascent * 700;

        var file = new PdfDictionary();
        if (IsCff) file.Set(PdfName.Subtype, PdfName.Get("CIDFontType0C"));
        else file.Set(PdfName.Get("Length1"), PdfInteger.Of(fontFile.Length));
        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Type, PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get(name));
        // 32 = non-symbolic; plus 1 = fixed pitch, 2 = serif, 64 = italic
        descriptor.Set(PdfName.Get("Flags"), PdfInteger.Of(32 | (_fixedPitch ? 1 : 0) | (_serif ? 2 : 0) | (_italic ? 64 : 0)));
        descriptor.Set(PdfName.Get("FontBBox"), bbox);
        descriptor.Set(PdfName.Get("ItalicAngle"), PdfInteger.Of(_italic ? -12 : 0));
        descriptor.Set(PdfName.Get("Ascent"), PdfInteger.Of((long)Math.Round(Ascent * 1000)));
        descriptor.Set(PdfName.Get("Descent"), PdfInteger.Of((long)Math.Round(Descent * 1000)));
        descriptor.Set(PdfName.Get("CapHeight"), PdfInteger.Of((long)Math.Round(capHeight)));
        descriptor.Set(PdfName.Get("StemV"), PdfInteger.Of(80));
        descriptor.Set(PdfName.Get(IsCff ? "FontFile3" : "FontFile2"), new PdfStream(file, fontFile));

        var systemInfo = new PdfDictionary();
        systemInfo.Set(PdfName.Get("Registry"), new PdfString("Adobe"u8.ToArray()));
        systemInfo.Set(PdfName.Get("Ordering"), new PdfString("Identity"u8.ToArray()));
        systemInfo.Set(PdfName.Get("Supplement"), PdfInteger.Of(0));

        var cid = new PdfDictionary();
        cid.Set(PdfName.Type, PdfName.Get("Font"));
        cid.Set(PdfName.Subtype, PdfName.Get(IsCff ? "CIDFontType0" : "CIDFontType2"));
        cid.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
        cid.Set(PdfName.Get("CIDSystemInfo"), systemInfo);
        cid.Set(PdfName.Get("FontDescriptor"), descriptor);
        cid.Set(PdfName.Get("DW"), PdfInteger.Of(1000));
        cid.Set(PdfName.W, widths);
        if (!IsCff) cid.Set(PdfName.Get("CIDToGIDMap"), PdfName.Identity);   // CFF: the subset maps CID → glyph itself

        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Get("Font"));
        font.Set(PdfName.Subtype, PdfName.Get("Type0"));
        font.Set(PdfName.Get("BaseFont"), PdfName.Get(name));
        font.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        font.Set(PdfName.Get("DescendantFonts"), new PdfArray([cid]));
        if (toUnicode is not null) font.Set(PdfName.Get("ToUnicode"), toUnicode);
        return font;
    }

    /// <summary>ToUnicode CMap (9.10.3): two-byte codes (= glyph) → UTF-16; with <paramref name="codeBytes"/> 1 for a simple font.</summary>
    internal static PdfStream ToUnicode(IReadOnlyDictionary<int, string> glyphs, int codeBytes = 2)
    {
        var digits = codeBytes == 1 ? "X2" : "X4";
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append(codeBytes == 1 ? "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" : "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        // (no text with U+0000, U+FEFF or U+FFFE: PDF/A forbids them in a /ToUnicode map)
        var entries = glyphs.Where(e => e.Value.Length > 0 && e.Value.IndexOfAny(['\0', '\uFEFF', '\uFFFE']) < 0).OrderBy(e => e.Key).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var block = entries.Skip(i).Take(100).ToList();
            sb.Append(CultureInfo.InvariantCulture, $"{block.Count} beginbfchar\n");
            foreach (var (glyph, text) in block)
            {
                sb.Append('<').Append(glyph.ToString(digits, CultureInfo.InvariantCulture)).Append("> <");
                foreach (var c in text) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>Appends glyph numbers as a hexadecimal string for Tj/TJ (two bytes per glyph).</summary>
    internal static void AppendHex(StringBuilder sb, IEnumerable<int> glyphs)
    {
        sb.Append('<');
        foreach (var g in glyphs) sb.Append(g.ToString("X4", CultureInfo.InvariantCulture));
        sb.Append('>');
    }

    internal static string Number(double value) => PdfSerializer.FormatReal(Math.Round(value, 3));
}
