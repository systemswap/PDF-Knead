namespace PdfKnead.Core.Pdf.Writing;

/// <summary>Which files a rule applies to beyond its part: every level, or only some.</summary>
internal enum PdfARuleLevel
{
    /// <summary>Every level of the parts that have the rule.</summary>
    All,

    /// <summary>Only level u (and a): every character maps to Unicode.</summary>
    Unicode,

    /// <summary>Only PDF/A-4f.</summary>
    FourF,

    /// <summary>Every level whose embedded files must themselves be PDF/A (all but PDF/A-4f and PDF/A-4e).</summary>
    AttachmentsPdfA,

    /// <summary>Only PDF/A-4e.</summary>
    FourE,

    /// <summary>Only level a: the document is tagged.</summary>
    Accessible,
}

/// <summary>
/// One requirement of PDF/A (KONZEPT.md, section 5.21, „Umbau vom Regelwerk her“; table with what checks and ensures it: docs/PDFA-REGELN.md):
/// implemented once, with the clause and test number it has in each part — ISO 19005-2 for parts 2 and 3 (part 3 differs only for embedded
/// files), ISO 19005-4 for part 4, ISO 19005-1 for part 1. The numbering follows the machine-readable rule list of a reference validator, so
/// that a finding there names the same rule; null where a part does not have the requirement.
/// </summary>
/// <param name="Two">Clause and test number in part 2, such as "6.2.11.4.1-2"; null if part 2 does not have it.</param>
/// <param name="Four">In part 4; null if part 4 does not have it.</param>
/// <param name="One">In part 1; null if part 1 does not have it.</param>
/// <param name="Level">The levels it applies to.</param>
/// <param name="Three">In part 3 where it differs from <paramref name="Two"/>; "-" if part 3 does not have it.</param>
internal sealed record PdfARule(string? Two, string? Four, string? One = null, PdfARuleLevel Level = PdfARuleLevel.All, string? Three = null)
{
    /// <summary>Clause and test number in the given part and level; null if the rule does not apply there.</summary>
    /// <param name="part">The part and level checked.</param>
    /// <returns>Such as "6.1.2-1", or null.</returns>
    public string? Id(PdfAConformance part)
    {
        var applies = Level switch
        {
            PdfARuleLevel.Unicode => part.NeedsUnicode(),
            PdfARuleLevel.FourF => part == PdfAConformance.A4f,
            PdfARuleLevel.AttachmentsPdfA => part is not (PdfAConformance.A4f or PdfAConformance.A4e),
            PdfARuleLevel.FourE => part == PdfAConformance.A4e,
            PdfARuleLevel.Accessible => part.Accessible(),
            _ => true,
        };
        if (!applies) return null;
        return part.Part() switch
        {
            1 => One,
            2 => Two,
            3 => Three == "-" ? null : Three ?? Two,
            4 => Four,
            _ => null,
        };
    }

    /// <summary>Further tests of the rule list the same check covers (in any part), such as the fields of an extension schema.</summary>
    public string[] Also { get; init; } = [];

    /// <summary>The clause alone (without the test number), such as "6.1.2".</summary>
    /// <param name="part">The part and level checked.</param>
    /// <returns>The clause, or null if the rule does not apply.</returns>
    public string? Clause(PdfAConformance part) => Id(part) is { } id ? id[..id.LastIndexOf('-')] : null;
}

/// <summary>
/// All requirements of PDF/A the check knows (<see cref="PdfACheck"/>), grouped as the standard is; the test <c>PdfARuleTests</c> holds a
/// crafted file for each, the check must find it and the PDF/A copy must meet it.
/// </summary>
internal static class PdfARules
{
    // ------------------------------------------------------------ 6.1 File structure

    /// <summary>The header names a version the part allows (%PDF-1.0 … 1.7; part 4: %PDF-2.n) and starts at byte 0.</summary>
    public static readonly PdfARule Header = new("6.1.2-1", "6.1.2-1", "6.1.2-1");

    /// <summary>The header is followed by a comment of at least four bytes above 127.</summary>
    public static readonly PdfARule BinaryComment = new("6.1.2-2", "6.1.2-2", "6.1.2-2");

    /// <summary>The trailer has a file identifier (/ID).</summary>
    public static readonly PdfARule FileId = new("6.1.3-1", "6.1.3-1", "6.1.3-1");

    /// <summary>Part 1: a linearized file has the same identifier in its first-page trailer as in its last.</summary>
    public static readonly PdfARule FileIdLinearized = new(null, null, "6.1.3-4");

    /// <summary>The file is not encrypted.</summary>
    public static readonly PdfARule NotEncrypted = new("6.1.3-2", "6.1.3-2", "6.1.3-2");

    /// <summary>Nothing follows the last %%EOF but one end-of-line marker.</summary>
    public static readonly PdfARule NothingAfterEof = new("6.1.3-3", "6.1.3-3", "6.1.3-3");

    /// <summary>Part 4: no information dictionary unless the catalogue has /PieceInfo.</summary>
    public static readonly PdfARule NoInfo = new(null, "6.1.3-4");

    /// <summary>Part 4: an information dictionary holds nothing but /ModDate.</summary>
    public static readonly PdfARule InfoOnlyModDate = new(null, "6.1.3-5");

    /// <summary>The xref keyword and the subsection header are separated by one end-of-line marker.</summary>
    public static readonly PdfARule XrefLineEnd = new("6.1.4-2", "6.1.4-1", "6.1.4-2");

    /// <summary>Part 1: the header of a cross-reference subsection is two numbers separated by one space.</summary>
    public static readonly PdfARule XrefSubsections = new(null, null, "6.1.4-1");

    /// <summary>Part 1: no cross-reference streams (and so no object streams; PDF 1.4).</summary>
    public static readonly PdfARule NoXrefStream = new(null, null, "6.1.4-3");

    /// <summary>A hexadecimal string has an even number of digits.</summary>
    public static readonly PdfARule HexEven = new("6.1.6-1", "6.1.5-1", "6.1.6-1");

    /// <summary>A hexadecimal string holds only hexadecimal digits.</summary>
    public static readonly PdfARule HexDigits = new("6.1.6-2", "6.1.5-2", "6.1.6-2");

    /// <summary>/Length of a stream is the number of its bytes.</summary>
    public static readonly PdfARule StreamLength = new("6.1.7.1-1", "6.1.6.1-1", "6.1.7-1");

    /// <summary>stream is followed by CR LF or LF, endstream preceded by an end-of-line marker.</summary>
    public static readonly PdfARule StreamKeywords = new("6.1.7.1-2", null, "6.1.7-2");

    /// <summary>No stream refers to an external file (/F, /FFilter, /FDecodeParms).</summary>
    public static readonly PdfARule NoExternalStream = new("6.1.7.1-3", "6.1.6.1-2", "6.1.7-3");

    /// <summary>PDF/A-4e: the data of a 3D annotation are U3D or PRC (a 3D stream's /Subtype).</summary>
    public static readonly PdfARule ThreeDStream = new(null, "6.1.6.1-3", Level: PdfARuleLevel.FourE);

    /// <summary>Streams use only standard filters, not LZW, Crypt only as Identity.</summary>
    public static readonly PdfARule StandardFilters = new("6.1.7.2-1", "6.1.6.2-1", "6.1.10-1");

    /// <summary>Names of fonts, colourants and structure types are valid UTF-8.</summary>
    public static readonly PdfARule NamesUtf8 = new("6.1.8-1", "6.1.7-1");

    /// <summary>Object number, generation, obj and endobj separated as the standard says.</summary>
    public static readonly PdfARule ObjectSpacing = new("6.1.9-1", "6.1.8-1", "6.1.8-1");

    /// <summary>Inline images use only standard filters, not LZW or Crypt.</summary>
    public static readonly PdfARule InlineFilters = new("6.1.10-1", "6.1.9-1", "6.1.10-2");

    /// <summary>Part 4: an inline image states the length of its data as written (/L; docs/FEHLERGEDAECHTNIS.md, No. 495).</summary>
    public static readonly PdfARule InlineLength = new(null, "6.1.9-2");

    /// <summary>The permissions dictionary holds only DocMDP and UR3.</summary>
    public static readonly PdfARule Permissions = new("6.1.12-1", "6.1.11-1");

    /// <summary>Part 1: no embedded files — no file specification with /EF, no /EmbeddedFiles.</summary>
    public static readonly PdfARule NoEmbeddedFiles = new(null, null, "6.1.11-1") { Also = ["6.1.11-2"] };

    /// <summary>With DocMDP, signature references have no DigestLocation, DigestMethod, DigestValue.</summary>
    public static readonly PdfARule SignatureReferenceDigest = new("6.1.12-2", null);

    /// <summary>Part 4: a /Version in the catalogue is 2.n.</summary>
    public static readonly PdfARule CatalogueVersion = new(null, "6.1.12-1");

    /// <summary>Integers lie within the range of 32 bits.</summary>
    public static readonly PdfARule IntegerRange = new("6.1.13-1", null, "6.1.12-1");

    /// <summary>Real numbers lie within ±3.403 × 10^38.</summary>
    public static readonly PdfARule RealRange = new("6.1.13-2", null, "6.1.12-2");

    /// <summary>Strings are at most 32 767 bytes long.</summary>
    public static readonly PdfARule StringLength = new("6.1.13-3", null, "6.1.12-3");

    /// <summary>Names are at most 127 bytes long.</summary>
    public static readonly PdfARule NameLength = new("6.1.13-4", null, "6.1.12-4");

    /// <summary>Real numbers are 0 or at least ±1.175 × 10^-38.</summary>
    public static readonly PdfARule RealNearZero = new("6.1.13-5", null);

    /// <summary>At most 8 388 607 indirect objects.</summary>
    public static readonly PdfARule ObjectCount = new("6.1.13-7", null, "6.1.12-7");

    /// <summary>q/Q nested at most 28 levels deep.</summary>
    public static readonly PdfARule SaveNesting = new("6.1.13-8", null, "6.1.12-8");

    /// <summary>DeviceN with at most 32 colourants.</summary>
    public static readonly PdfARule DeviceNColourants = new("6.1.13-9", null, "6.1.12-9");

    /// <summary>CIDs at most 65 535.</summary>
    public static readonly PdfARule CidRange = new("6.1.13-10", null, "6.1.12-10");

    /// <summary>Page boundaries between 3 and 14 400 units in either direction.</summary>
    public static readonly PdfARule PageBoxSize = new("6.1.13-11", null);

    /// <summary>Part 1: arrays of at most 8 191 entries.</summary>
    public static readonly PdfARule ArraySize = new(null, null, "6.1.12-5");

    /// <summary>Part 1: dictionaries of at most 4 095 entries.</summary>
    public static readonly PdfARule DictionarySize = new(null, null, "6.1.12-6");

    /// <summary>Part 1: no optional content (layers; PDF 1.5).</summary>
    public static readonly PdfARule NoOptionalContent = new(null, null, "6.1.13-1");

    // ------------------------------------------------------------ 6.2 Graphics

    /// <summary>Content streams use only operators of ISO 32000.</summary>
    public static readonly PdfARule KnownOperators = new("6.2.2-1", "6.2.2-1", "6.2.10-1");

    /// <summary>A content stream that uses resources has its own resource dictionary (no inherited ones).</summary>
    public static readonly PdfARule OwnResources = new("6.2.2-2", "6.2.2-2");

    /// <summary>Part 4: every named resource a content stream uses is in its resource dictionary.</summary>
    public static readonly PdfARule ResourcesDefined = new(null, "6.2.2-3");

    /// <summary>The output intent's profile is a printer or display profile in Gray, RGB or CMYK, version below 5.</summary>
    public static readonly PdfARule IntentProfile = new("6.2.3-1", "6.2.3-1", "6.2.2-1");

    /// <summary>Several output intents with a profile share the same profile object.</summary>
    public static readonly PdfARule IntentsSameProfile = new("6.2.3-2", "6.2.3-2", "6.2.2-2");

    /// <summary>No /DestOutputProfileRef (part 2: in a PDF/X intent; part 4: in any).</summary>
    public static readonly PdfARule IntentProfileRef = new("6.2.3-3", "6.2.3-3");

    /// <summary>Part 4: at most one PDF/A output intent (/S /GTS_PDFA1) in an /OutputIntents array (No. 495).</summary>
    public static readonly PdfARule OneOutputIntent = new(null, "6.2.3-4");

    /// <summary>The profile of an ICCBased space conforms to ICC (classes, colour spaces, version).</summary>
    public static readonly PdfARule IccProfileValid = new("6.2.4.2-1", "6.2.4.2-1", "6.2.3.2-1") { Also = ["6.2.3.2-2"] };

    /// <summary>No overprint mode 1 with overprinting of ICCBased CMYK.</summary>
    public static readonly PdfARule OverprintMode = new("6.2.4.2-2", "6.2.4.2-2");

    /// <summary>Part 4: no ICCBased CMYK whose profile is the output intent's or the blending space's.</summary>
    public static readonly PdfARule IccNotIntent = new(null, "6.2.4.2-3");

    /// <summary>DeviceRGB only with an RGB output intent, a DefaultRGB (part 4 also: an RGB blending space).</summary>
    public static readonly PdfARule DeviceRgb = new("6.2.4.3-2", "6.2.4.3-2", "6.2.3.3-1");

    /// <summary>DeviceCMYK only with a CMYK output intent, a DefaultCMYK (part 4 also: a CMYK blending space).</summary>
    public static readonly PdfARule DeviceCmyk = new("6.2.4.3-3", "6.2.4.3-3", "6.2.3.3-2");

    /// <summary>DeviceGray only with an output intent or a DefaultGray.</summary>
    public static readonly PdfARule DeviceGray = new("6.2.4.3-4", "6.2.4.3-4", "6.2.3.3-3");

    /// <summary>Every spot colour of DeviceN (NChannel) is in its /Colorants.</summary>
    public static readonly PdfARule Colorants = new("6.2.4.4-1", "6.2.4.4-1");

    /// <summary>Separations of the same name have the same alternate space and tint transform.</summary>
    public static readonly PdfARule SeparationsAlike = new("6.2.4.4-2", "6.2.4.4-2");

    /// <summary>Graphics states have no /TR.</summary>
    public static readonly PdfARule NoTransfer = new("6.2.5-1", "6.2.5-1", "6.2.8-1");

    /// <summary>Graphics states have no /TR2 other than /Default.</summary>
    public static readonly PdfARule NoTransfer2 = new("6.2.5-2", "6.2.5-2", "6.2.8-2");

    /// <summary>Graphics states have no /HTP (part 4: /HTO).</summary>
    public static readonly PdfARule NoHalftoneOrigin = new("6.2.5-3", "6.2.5-3");

    /// <summary>Halftones are of type 1 or 5.</summary>
    public static readonly PdfARule HalftoneType = new("6.2.5-4", "6.2.5-4");

    /// <summary>Halftones have no /HalftoneName.</summary>
    public static readonly PdfARule HalftoneName = new("6.2.5-5", "6.2.5-5");

    /// <summary>/TransferFunction in a halftone only where ISO 32000 requires it.</summary>
    /// (Part 4 has no such test: its clause on halftones ends with test 5 — No. 495.)
    public static readonly PdfARule HalftoneTransfer = new("6.2.5-6", null);

    /// <summary>Rendering intents are one of the four of ISO 32000.</summary>
    public static readonly PdfARule RenderingIntent = new("6.2.6-1", "6.2.4.6-1", "6.2.9-1");

    /// <summary>Images have no /Alternates.</summary>
    public static readonly PdfARule ImageAlternates = new("6.2.8-1", "6.2.7.1-1", "6.2.4-1");

    /// <summary>Images have no /OPI.</summary>
    public static readonly PdfARule ImageOpi = new("6.2.8-2", "6.2.7.1-2", "6.2.4-2");

    /// <summary>Images (and inline images) are not smoothed (/Interpolate, /I).</summary>
    public static readonly PdfARule ImageInterpolate = new("6.2.8-3", "6.2.7.1-3", "6.2.4-3");

    /// <summary>Images have 1, 2, 4, 8 or 16 bits per component.</summary>
    public static readonly PdfARule ImageBits = new("6.2.8-4", "6.2.7.1-4", "6.2.4-4");

    /// <summary>Image masks have 1 bit per component.</summary>
    public static readonly PdfARule MaskBits = new("6.2.8-5", "6.2.7.1-5", "6.2.4-5");

    /// <summary>JPEG 2000: 1, 3 or 4 colour channels.</summary>
    public static readonly PdfARule JpxChannels = new("6.2.8.3-1", "6.2.7.3-1");

    /// <summary>JPEG 2000: with several colour specifications exactly one marked as the one to use.</summary>
    public static readonly PdfARule JpxColourSpecs = new("6.2.8.3-2", "6.2.7.3-2");

    /// <summary>JPEG 2000: colour specification method 1, 2 or 3.</summary>
    public static readonly PdfARule JpxMethod = new("6.2.8.3-3", "6.2.7.3-3");

    /// <summary>JPEG 2000: not the enumerated colour space 19 (CIEJab).</summary>
    public static readonly PdfARule JpxJab = new("6.2.8.3-4", "6.2.7.3-4");

    /// <summary>JPEG 2000: bit depth 1 to 38, the same in all channels.</summary>
    public static readonly PdfARule JpxBitDepth = new("6.2.8.3-5", "6.2.7.3-5");

    /// <summary>Forms have no /OPI (part 2 also: no /PS, no /Subtype2 /PS).</summary>
    public static readonly PdfARule FormOpiPostScript = new("6.2.9-1", "6.2.8.1-1", "6.2.5-1");

    /// <summary>No reference XObjects.</summary>
    public static readonly PdfARule NoReferenceXObject = new("6.2.9-2", "6.2.8.2-1", "6.2.6-1");

    /// <summary>No PostScript XObjects.</summary>
    public static readonly PdfARule NoPostScript = new("6.2.9-3", null, "6.2.7-1");

    /// <summary>Only blend modes of ISO 32000.</summary>
    public static readonly PdfARule BlendModes = new("6.2.10-1", "6.2.9-1", "6.4-4");

    /// <summary>Without an output intent, pages with transparency have a group with a colour space.</summary>
    public static readonly PdfARule TransparencyNeedsSpace = new("6.2.10-2", "6.2.9-2");

    /// <summary>Part 1: no soft mask in a graphics state (/SMask only /None).</summary>
    public static readonly PdfARule NoSoftMask = new(null, null, "6.4-1");

    /// <summary>Part 1: no image or form with a soft mask (/SMask).</summary>
    public static readonly PdfARule NoImageSoftMask = new(null, null, "6.4-2");

    /// <summary>Part 1: no transparency group (/Group /S /Transparency).</summary>
    public static readonly PdfARule NoTransparencyGroup = new(null, null, "6.4-3");

    /// <summary>Part 1: graphics states without constant opacity below 1 (/CA, /ca).</summary>
    public static readonly PdfARule ConstantAlpha = new(null, null, "6.4-5") { Also = ["6.4-6"] };

    // ------------------------------------------------------------ 6.2.11 Fonts

    /// <summary>A font dictionary has /Type /Font.</summary>
    public static readonly PdfARule FontType = new("6.2.11.2-1", "6.2.10.2-1", "6.3.2-1");

    /// <summary>A font dictionary has a known /Subtype.</summary>
    public static readonly PdfARule FontSubtype = new("6.2.11.2-2", "6.2.10.2-2", "6.3.2-2");

    /// <summary>A font other than Type 3 has /BaseFont.</summary>
    public static readonly PdfARule FontBaseName = new("6.2.11.2-3", "6.2.10.2-3", "6.3.2-3");

    /// <summary>A simple font other than the standard 14 has /FirstChar.</summary>
    public static readonly PdfARule FontFirstChar = new("6.2.11.2-4", "6.2.10.2-4", "6.3.2-4");

    /// <summary>A simple font other than the standard 14 has /LastChar.</summary>
    public static readonly PdfARule FontLastChar = new("6.2.11.2-5", "6.2.10.2-5", "6.3.2-5");

    /// <summary>A simple font other than the standard 14 has /Widths of LastChar − FirstChar + 1 entries.</summary>
    public static readonly PdfARule FontWidthsCount = new("6.2.11.2-6", "6.2.10.2-6", "6.3.2-6");

    /// <summary>The /Subtype of a FontFile3 is Type1C, CIDFontType0C or OpenType.</summary>
    public static readonly PdfARule FontFileSubtype = new("6.2.11.2-7", "6.2.10.2-7", "6.3.2-7");

    /// <summary>The character collection of a CIDFont matches its CMap (unless Identity).</summary>
    public static readonly PdfARule CidSystemInfo = new("6.2.11.3.1-1", "6.2.10.3.1-1", "6.3.3.1-1");

    /// <summary>An embedded CIDFontType2 has /CIDToGIDMap.</summary>
    public static readonly PdfARule CidToGidMap = new("6.2.11.3.2-1", "6.2.10.3.2-1", "6.3.3.2-1");

    /// <summary>CMaps other than the predefined ones are embedded.</summary>
    public static readonly PdfARule CMapEmbedded = new("6.2.11.3.3-1", "6.2.10.3.3-1", "6.3.3.3-1");

    /// <summary>The /WMode of an embedded CMap matches its stream.</summary>
    public static readonly PdfARule CMapWMode = new("6.2.11.3.3-2", "6.2.10.3.3-2", "6.3.3.3-2");

    /// <summary>An embedded CMap uses only predefined CMaps (/UseCMap).</summary>
    public static readonly PdfARule CMapUses = new("6.2.11.3.3-3", "6.2.10.3.3-3");

    /// <summary>Fonts used for rendering are embedded.</summary>
    public static readonly PdfARule FontEmbedded = new("6.2.11.4.1-1", "6.2.10.4.1-1", "6.3.4-1");

    /// <summary>Embedded fonts have every glyph shown.</summary>
    public static readonly PdfARule GlyphPresent = new("6.2.11.4.1-2", "6.2.10.4.1-2", "6.3.5-1");

    /// <summary>A /CharSet of an embedded Type 1 subset lists all its glyphs.</summary>
    public static readonly PdfARule CharSet = new("6.2.11.4.2-1", null, "6.3.5-2");

    /// <summary>A /CIDSet of an embedded CID subset lists all its CIDs.</summary>
    public static readonly PdfARule CidSet = new("6.2.11.4.2-2", null, "6.3.5-3");

    /// <summary>Widths of the font dictionary and of the program agree (within 1/1000).</summary>
    public static readonly PdfARule GlyphWidths = new("6.2.11.5-1", "6.2.10.5-1", "6.3.6-1");

    /// <summary>A non-symbolic TrueType program has the cmap the lookup needs.</summary>
    public static readonly PdfARule TrueTypeCmap = new("6.2.11.6-1", "6.2.10.6-1");

    /// <summary>A non-symbolic TrueType font has WinAnsi or MacRoman encoding, differences only with known glyph names.</summary>
    public static readonly PdfARule TrueTypeEncoding = new("6.2.11.6-2", "6.2.10.6-2", "6.3.7-1");

    /// <summary>A symbolic TrueType font has no /Encoding.</summary>
    public static readonly PdfARule SymbolicNoEncoding = new("6.2.11.6-3", "6.2.10.6-3", "6.3.7-2");

    /// <summary>A symbolic TrueType program has one cmap or a (3,0) one (part 4: (3,0) or (1,0)).</summary>
    public static readonly PdfARule SymbolicCmap = new("6.2.11.6-4", "6.2.10.6-4", "6.3.7-3");

    /// <summary>Level u (and a): every character shown maps to Unicode (PDF/A-1a, 6.3.8: a /ToUnicode where the encoding does not say it).</summary>
    public static readonly PdfARule UnicodeMapped = new("6.2.11.7.2-1", null, "6.3.8-1", Level: PdfARuleLevel.Unicode);

    /// <summary>Level a: a character mapped into a private use area of Unicode stands in a replacement text (/ActualText).</summary>
    public static readonly PdfARule PuaActualText = new("6.2.11.7.3-1", null, Level: PdfARuleLevel.Accessible);

    /// <summary>Level u: the Unicode values are neither 0 nor U+FEFF, U+FFFE.</summary>
    public static readonly PdfARule UnicodeValuesU = new("6.2.11.7.2-2", null, Level: PdfARuleLevel.Unicode);

    /// <summary>Part 4: values of a /ToUnicode are neither 0 nor U+FEFF, U+FFFE.</summary>
    public static readonly PdfARule UnicodeValues = new(null, "6.2.10.7-1");

    /// <summary>Part 4: /ActualText without characters of the private use areas.</summary>
    public static readonly PdfARule ActualTextNoPua = new(null, "6.2.10.8-1");

    /// <summary>No text shows the .notdef glyph.</summary>
    public static readonly PdfARule NoNotdef = new("6.2.11.8-1", "6.2.10.9-1");

    // ------------------------------------------------------------ 6.3 Annotations

    /// <summary>Only annotation types the part allows.</summary>
    public static readonly PdfARule AnnotationTypes = new("6.3.1-1", "6.3.1-1", "6.5.2-1");

    /// <summary>Every annotation but Popup has /F.</summary>
    public static readonly PdfARule AnnotationFlags = new("6.3.2-1", "6.3.2-1");

    /// <summary>Annotations are printed and not hidden.</summary>
    public static readonly PdfARule AnnotationPrinted = new("6.3.2-2", "6.3.2-2", "6.5.3-2");

    /// <summary>Part 1: annotations have colours (/C, /IC) only with an RGB output intent.</summary>
    public static readonly PdfARule AnnotationColours = new(null, null, "6.5.3-3");

    /// <summary>Part 1: annotations without an opacity below 1 (/CA).</summary>
    public static readonly PdfARule AnnotationOpacity = new(null, null, "6.5.3-1");

    /// <summary>Annotations with an area (but Popup and Link) have an appearance.</summary>
    public static readonly PdfARule AnnotationAppearance = new("6.3.3-1", "6.3.3-1", "6.9-2");

    /// <summary>An appearance dictionary holds only /N.</summary>
    public static readonly PdfARule AppearanceOnlyNormal = new("6.3.3-2", "6.3.3-2", "6.5.3-4");

    /// <summary>The /N of a button widget is a dictionary of states.</summary>
    public static readonly PdfARule ButtonAppearanceStates = new("6.3.3-3", "6.3.3-3", "6.5.3-5");

    /// <summary>The /N of any other annotation is a stream.</summary>
    public static readonly PdfARule AppearanceStream = new("6.3.3-4", "6.3.3-4", "6.5.3-6");

    // ------------------------------------------------------------ 6.4 Forms, signatures

    /// <summary>Widgets have no /A (part 2 also: no /AA).</summary>
    public static readonly PdfARule WidgetActions = new("6.4.1-1", "6.4.1-1", "6.6.1-3") { Also = ["6.6.2-1"] };

    /// <summary>Form fields have no /AA.</summary>
    public static readonly PdfARule FieldActions = new("6.4.1-2", null, "6.6.2-2");

    /// <summary>The form has no /NeedAppearances true.</summary>
    public static readonly PdfARule NeedAppearances = new("6.4.1-3", "6.4.1-2", "6.9-1");

    /// <summary>The form has no XFA.</summary>
    public static readonly PdfARule NoXfa = new("6.4.2-1", "6.4.2-1");

    /// <summary>The catalogue has no /NeedsRendering.</summary>
    public static readonly PdfARule NoNeedsRendering = new("6.4.2-2", "6.4.2-2");

    /// <summary>A signature covers the whole file.</summary>
    public static readonly PdfARule SignatureCoversFile = new("6.4.3-1", null);

    /// <summary>A signature includes the signer's certificate.</summary>
    public static readonly PdfARule SignatureCertificate = new("6.4.3-2", null);

    /// <summary>A signature has exactly one signer.</summary>
    public static readonly PdfARule SignatureOneSigner = new("6.4.3-3", null);

    // ------------------------------------------------------------ 6.5 Actions

    /// <summary>Only the actions the part allows (part 4 also scripts and rich media).</summary>
    public static readonly PdfARule Actions = new("6.5.1-1", "6.6.1-1", "6.6.1-1");

    /// <summary>Named actions only NextPage, PrevPage, FirstPage, LastPage.</summary>
    public static readonly PdfARule NamedActions = new("6.5.1-2", "6.6.1-2", "6.6.1-2");

    /// <summary>The catalogue has no /AA (part 4: only the triggers E, X, D, U, Fo, Bl).</summary>
    public static readonly PdfARule CatalogueTriggers = new("6.5.2-1", "6.6.3-1", "6.6.2-3");

    /// <summary>Pages have no /AA (part 4: only the triggers E, X, D, U, Fo, Bl).</summary>
    public static readonly PdfARule PageTriggers = new("6.5.2-2", "6.6.3-1");

    /// <summary>Part 4: annotations other than widgets only with the triggers E, X, D, U, Fo, Bl in /AA.</summary>
    public static readonly PdfARule AnnotationTriggers = new(null, "6.6.3-1");

    // ------------------------------------------------------------ 6.6 Metadata

    /// <summary>The catalogue has /Metadata, a stream /Type /Metadata /Subtype /XML.</summary>
    public static readonly PdfARule Metadata = new("6.6.2.1-1", "6.7.2.1-1", "6.7.2-1");

    /// <summary>Part 1: the document's XMP stream without a filter.</summary>
    public static readonly PdfARule MetadataUnfiltered = new(null, null, "6.7.2-2");

    /// <summary>Part 1: the information dictionary and the XMP metadata say the same (dates, title, author, subject, keywords, creator, producer).</summary>
    public static readonly PdfARule InfoMatchesXmp = new(null, null, "6.7.3-1") { Also = [.. Enumerable.Range(2, 7).Select(n => $"6.7.3-{n}")] };

    /// <summary>The XMP packet header has no bytes attribute.</summary>
    public static readonly PdfARule XmpNoBytes = new("6.6.2.1-2", "6.7.2.1-2", "6.7.5-1");

    /// <summary>The XMP packet header has no encoding attribute.</summary>
    public static readonly PdfARule XmpNoEncoding = new("6.6.2.1-3", "6.7.2.1-3", "6.7.5-2");

    /// <summary>XMP packets are well-formed XML and RDF.</summary>
    public static readonly PdfARule XmpWellFormed = new("6.6.2.1-4", "6.7.2.1-4", "6.7.9-1");

    /// <summary>XMP packets are encoded as UTF-8.</summary>
    public static readonly PdfARule XmpUtf8 = new("6.6.2.1-5", "6.7.2.1-5");

    /// <summary>XMP properties use predefined schemas or declared extension schemas.</summary>
    public static readonly PdfARule XmpSchemas = new("6.6.2.3.1-1", null, "6.7.9-2");

    /// <summary>XMP properties have the value type of their schema.</summary>
    public static readonly PdfARule XmpValueTypes = new("6.6.2.3.1-2", null, "6.7.9-3");

    /// <summary>The structures of an extension schema have only the fields of their container schema (6.6.2.3.2).</summary>
    public static readonly PdfARule XmpExtensionSchemas = new("6.6.2.3.2-1", null, "6.7.8-1");

    /// <summary>Extension schemas are declared completely with the container schemas and their prefixes (6.6.2.3.3, tests 1–18).</summary>
    public static readonly PdfARule XmpExtensionContainer = new("6.6.2.3.3-1", null, "6.7.8-2")
    {
        Also = [.. Enumerable.Range(2, 17).Select(n => $"6.6.2.3.3-{n}"), .. Enumerable.Range(3, 17).Select(n => $"6.7.8-{n}")],
    };

    /// <summary>The XMP names part and level (part 4: part and revision) with the identification schema.</summary>
    public static readonly PdfARule Identification = new("6.6.4-1", "6.7.3-1", "6.7.11-1");

    /// <summary>pdfaid:part is the part the file conforms to.</summary>
    public static readonly PdfARule IdentificationPart = new("6.6.4-2", "6.7.3-2", "6.7.11-2");

    /// <summary>pdfaid:conformance is A, B or U (part 4: none but E or F).</summary>
    public static readonly PdfARule IdentificationLevel = new("6.6.4-3", "6.7.3-3", "6.7.11-3");

    /// <summary>The identification properties have the prefix pdfaid (part, conformance, amd, corr; part 4: part, rev).</summary>
    public static readonly PdfARule IdentificationPrefix = new("6.6.4-4", "6.7.3-4", "6.7.11-4")
    {
        Also = ["6.6.4-5", "6.6.4-6", "6.6.4-7", "6.7.3-6", "6.7.11-5", "6.7.11-6"],
    };

    /// <summary>Part 4: pdfaid:rev is 2020.</summary>
    public static readonly PdfARule IdentificationRevision = new(null, "6.7.3-5");

    // ------------------------------------------------------------ 6.8 Embedded files

    /// <summary>The file specification of an embedded file has /F and /UF.</summary>
    public static readonly PdfARule AttachmentNames = new("6.8-2", "6.9-2");

    /// <summary>Parts 2 and 4 (without f and e): embedded files are PDF/A themselves.</summary>
    public static readonly PdfARule AttachmentIsPdfA = new("6.8-5", "6.9-3", Three: "-", Level: PdfARuleLevel.AttachmentsPdfA);

    /// <summary>Parts 3 and 4f: an embedded file names its MIME type (/Subtype).</summary>
    public static readonly PdfARule AttachmentMime = new(null, "6.9-1", Three: "6.8-1");

    /// <summary>Parts 3 and 4: a file specification has /AFRelationship.</summary>
    public static readonly PdfARule AttachmentRelationship = new(null, "6.9-4", Three: "6.8-3");

    /// <summary>Part 3: a file specification is associated (in an /AF array).</summary>
    public static readonly PdfARule AttachmentAssociated = new(null, null, Three: "6.8-4");

    /// <summary>PDF/A-4f: the name dictionary has /EmbeddedFiles.</summary>
    public static readonly PdfARule FourFHasAttachment = new(null, "6.9-5", Level: PdfARuleLevel.FourF);

    // ------------------------------------------------------------ 6.9 – 6.11 Layers, presentations, requirements

    /// <summary>Every layer configuration has a /Name (part 2: not empty).</summary>
    public static readonly PdfARule LayerConfigName = new("6.9-1", "6.10-1");

    /// <summary>Names of layer configurations are unique.</summary>
    public static readonly PdfARule LayerConfigUnique = new("6.9-2", "6.10-2");

    /// <summary>An /Order of a layer configuration lists all layers.</summary>
    public static readonly PdfARule LayerOrder = new("6.9-3", "6.10-3");

    /// <summary>Layer configurations have no /AS.</summary>
    public static readonly PdfARule LayerAutoState = new("6.9-4", null);

    /// <summary>No /AlternatePresentations.</summary>
    public static readonly PdfARule NoAlternatePresentations = new("6.10-1", "6.11-1");

    /// <summary>Pages have no /PresSteps.</summary>
    public static readonly PdfARule NoPresSteps = new("6.10-2", "6.11-2");

    /// <summary>The catalogue has no /Requirements.</summary>
    public static readonly PdfARule NoRequirements = new("6.11-1", "6.12-1");

    // ------------------------------------------------------------ 6.7 Logical structure (level a; ISO 19005-1: 6.8)

    /// <summary>Level a: the catalogue has /MarkInfo with /Marked true.</summary>
    public static readonly PdfARule Marked = new("6.7.2.2-1", null, "6.8.2.2-1", Level: PdfARuleLevel.Accessible);

    /// <summary>Level a: the catalogue has a structure tree (/StructTreeRoot).</summary>
    public static readonly PdfARule StructureRoot = new("6.7.3.3-1", null, "6.8.3.3-1", Level: PdfARuleLevel.Accessible);

    /// <summary>Level a: every structure type that is not a standard one is mapped to one in the role map.</summary>
    public static readonly PdfARule StructureTypesMapped = new("6.7.3.4-1", null, "6.8.3.4-1", Level: PdfARuleLevel.Accessible);

    /// <summary>Level a: the role map has no circle.</summary>
    public static readonly PdfARule RoleMapNotCircular = new("6.7.3.4-2", null, "6.8.3.4-2", Level: PdfARuleLevel.Accessible);

    /// <summary>Level a (parts 2 and 3): a standard structure type is not mapped to another type.</summary>
    public static readonly PdfARule StandardTypesKept = new("6.7.3.4-3", null, Level: PdfARuleLevel.Accessible);

    /// <summary>Level a: /Lang (catalogue, structure elements, property lists) is a language tag (RFC 3066 / 1766) or empty.</summary>
    public static readonly PdfARule LanguageTag = new("6.7.4-1", null, "6.8.4-1", Level: PdfARuleLevel.Accessible);
}
