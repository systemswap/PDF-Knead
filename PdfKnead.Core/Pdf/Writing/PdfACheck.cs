using System.Xml;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Checks a file against the rules of PDF/A-2b and PDF/A-3b (ISO 19005-2 and -3, level b) that concern what the program writes and can
/// read (KONZEPT.md, section 5.21): file structure, metadata, output intent and device colours, fonts in use, graphics state, images and
/// forms, annotations, actions, form fields, layers, embedded files and the implementation limits — glyphs and widths of the shown text
/// against the font programs (6.2.11.4, 6.2.11.5, 6.2.11.8) and the syntax of content streams included. Not checked: the values of
/// foreign XMP properties.
/// </summary>
public static partial class PdfACheck
{
    private const int MaxObjects = 1 << 21;
    private const int MaxNesting = 28;

    /// <summary>Checks a document.</summary>
    /// <param name="doc">The document (opened from the bytes to check).</param>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>The rules not met, each once (with the first page where it was found); empty if none. The part and level checked are the
    /// ones the document declares (2b if it declares none or another).</returns>
    public static IReadOnlyList<PdfAViolation> Check(PdfDocument doc, CancellationToken cancel = default) => new Run(doc, cancel).Check();

    /// <summary>Checks a document against a given part and level, whatever its metadata declare.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="part">The part and level.</param>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>The rules not met, each once; empty if none.</returns>
    public static IReadOnlyList<PdfAViolation> Check(PdfDocument doc, PdfAConformance part, CancellationToken cancel = default) =>
        new Run(doc, cancel) { Part = part }.Check();

    /// <summary>
    /// Whether an embedded file is an attachment a part allows (ISO 19005-2, 6.8: a PDF/A-1 or PDF/A-2 file; ISO 19005-4, 6.9: also PDF/A-4):
    /// a PDF that names such a part in its metadata and meets its rules — checked here, with its own attachments to a limited depth.
    /// </summary>
    /// <param name="data">The embedded file.</param>
    /// <param name="part">The part of the document it is attached to.</param>
    /// <param name="depth">How deep it lies in attachments of attachments.</param>
    /// <returns>Whether it is confirmed as an allowed attachment.</returns>
    internal static bool AllowedAttachment(byte[] data, PdfAConformance part, int depth = 0)
    {
        if (depth > 2 || !data.AsSpan().StartsWith("%PDF-"u8)) return false;
        try
        {
            using var doc = PdfDocument.Open(data);
            if (Declared(doc) is not { } declared || !(declared.Part() is 1 or 2 || declared.Part() == 4 && part.Part() == 4)) return false;
            return new Run(doc, CancellationToken.None) { Part = declared, Depth = depth + 1 }.Check().Count == 0;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or OverflowException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The part and level the document's XMP metadata declares (pdfaid:part 1, 2, 3 or 4); null if none.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The declared part and level, or null.</returns>
    public static PdfAConformance? Declared(PdfDocument doc) => Identification(doc) switch
    {
        (1, "A") => PdfAConformance.A1a,
        (1, _) => PdfAConformance.A1b,
        (2, "U") => PdfAConformance.A2u,
        (2, "A") => PdfAConformance.A2a,
        (2, _) => PdfAConformance.A2b,
        (3, "U") => PdfAConformance.A3u,
        (3, "A") => PdfAConformance.A3a,
        (3, _) => PdfAConformance.A3b,
        (4, "F") => PdfAConformance.A4f,
        (4, "E") => PdfAConformance.A4e,
        (4, _) => PdfAConformance.A4,
        _ => null,
    };

    private static (int? Part, string? Level) Identification(PdfDocument doc)
    {
        if (ReadXmp(doc) is not { } xmp) return (null, null);
        var part = Property(xmp, PdfAId, "part");
        return (int.TryParse(part, out var n) ? n : null, Property(xmp, PdfAId, "conformance"));
    }

    private const string PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private const string XmpNs = "http://ns.adobe.com/xap/1.0/";
    private const string PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>The catalogue's XMP packet as XML; null if there is none or it cannot be read.</summary>
    private static XmlDocument? ReadXmp(PdfDocument doc)
    {
        if (doc.Catalog?.Get(PdfName.Metadata) is not PdfStream stream) return null;
        try
        {
            return PdfAXmp.Read(doc.GetStreamData(stream));
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>A simple property in any rdf:Description, as an element or an attribute; for lists (Seq, Bag, Alt) the entries joined.</summary>
    private static string? Property(XmlDocument xmp, string ns, string name)
    {
        foreach (XmlElement description in xmp.GetElementsByTagName("Description", RdfNs))
        {
            if (description.GetAttributeNode(name, ns) is { } attribute) return attribute.Value;
            foreach (XmlNode child in description.ChildNodes)
                if (child is XmlElement e && e.LocalName == name && e.NamespaceURI == ns)
                {
                    var items = e.GetElementsByTagName("li", RdfNs);
                    return items.Count == 0 ? e.InnerText : string.Join("\u0001", items.Cast<XmlNode>().Select(i => i.InnerText));
                }
        }
        return null;
    }

    /// <summary>
    /// The colour space of a profile that can be the output intent (6.2.3; ISO 19005-1, 6.2.2: a printer or display profile of one, three or
    /// four components, of a major version the part allows); null otherwise.
    /// </summary>
    /// <param name="data">The profile.</param>
    /// <param name="maxMajor">The highest major version (<see cref="PdfALimits.IccMajor"/>).</param>
    internal static uint? UsableProfile(byte[] data, int maxMajor)
    {
        if (data.Length < 132 || IccProfile.Parse(data) is null || data[8] > maxMajor) return null;
        var deviceClass = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
        var space = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        if (deviceClass is not (0x70727472 or 0x6D6E7472)) return null;   // prtr, mntr
        return space is IccProfile.RgbSpace or IccProfile.CmykSpace or IccProfile.GraySpace ? space : null;
    }

    /// <summary>
    /// The number of colours an ICC profile describes, by the colour space in its header (Gray 1, RGB and Lab 3, CMYK 4); null for another
    /// space or a profile too short.
    /// </summary>
    internal static int? IccComponents(byte[] data) =>
        data.Length < 20 ? null : System.Text.Encoding.ASCII.GetString(data, 16, 4) switch
        {
            "GRAY" => 1,
            "RGB " or "Lab " => 3,
            "CMYK" => 4,
            _ => null,
        };

    /// <summary>
    /// The header of a profile of an ICCBased space as PDF/A asks (ISO 19005-2, 6.2.4.2; ISO 19005-1, 6.2.3.2): device class input, display,
    /// output or colour space (scnr, mntr, prtr, spac), colour space Gray, RGB, CMYK or Lab, a major version the part allows.
    /// </summary>
    /// <param name="data">The profile.</param>
    /// <param name="maxMajor">The highest major version (<see cref="PdfALimits.IccMajor"/>).</param>
    internal static bool IccProfileHeaderValid(byte[] data, int maxMajor)
    {
        if (data.Length < 132 || data[8] > maxMajor) return false;
        var deviceClass = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
        var space = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        return deviceClass is 0x70727472 or 0x6D6E7472 or 0x73636E72 or 0x73706163 &&   // prtr, mntr, scnr, spac
               space is IccProfile.GraySpace or IccProfile.RgbSpace or IccProfile.CmykSpace or IccProfile.LabSpace;
    }

        private static readonly HashSet<string> ActionKinds = new(["GoTo", "GoToR", "GoToE", "Launch", "Thread", "URI", "Sound", "Movie", "Hide", "Named",
        "SubmitForm", "ResetForm", "ImportData", "JavaScript", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "RichMediaExecute",
        // the actions PDF 1.2 knew and later versions dropped (ISO 19005-1, 6.6.1)
        "SetState", "NOP"], StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedNamed = new(["NextPage", "PrevPage", "FirstPage", "LastPage"], StringComparer.Ordinal);

    /// <summary>One of the four rendering intents of ISO 32000-1, 8.6.5.8.</summary>
    internal static bool RenderingIntent(string name) => name is "RelativeColorimetric" or "AbsoluteColorimetric" or "Perceptual" or "Saturation";

    private static readonly HashSet<string> Blends = new(["Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge",
        "ColorBurn", "HardLight", "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity"], StringComparer.Ordinal);

    /// <summary>
    /// A blend mode a part allows (an array counts by its first entry): one of ISO 32000-1, 11.3.5; in PDF/A-1 (6.4) only Normal and
    /// Compatible.
    /// </summary>
    internal static bool StandardBlend(PdfObject blend, PdfAConformance part) => blend switch
    {
        PdfName name => AllowedBlend(name.Value, part),
        PdfArray modes => modes.Count > 0 && modes.Get(0) is PdfName first && AllowedBlend(first.Value, part),
        _ => false,
    };

    private static bool AllowedBlend(string blend, PdfAConformance part) =>
        Blends.Contains(blend) && (blend is "Normal" or "Compatible" || part.Part() != 1);

        /// <summary>Annotation types of ISO 32000-1 that PDF/A-2 allows (6.3.1: not 3D, Sound, Screen, Movie).</summary>
    internal static readonly HashSet<string> Annotations = new(["Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine",
        "Highlight", "Underline", "Squiggly", "StrikeOut", "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Widget", "PrinterMark", "TrapNet",
        "Watermark", "Redact"], StringComparer.Ordinal);

    /// <summary>
    /// A file specification (ISO 32000-1, 7.11.3): marked so, or with embedded files (/EF as a dictionary — elsewhere /EF means other things,
    /// such as the rights to attachments in the transform parameters of a signature).
    /// </summary>
    internal static bool IsFileSpec(PdfDictionary dict) => dict.GetName(PdfName.Type)?.Value == "Filespec" || dict.Get(PdfName.Get("EF")) is PdfDictionary;

    /// <summary>A MIME type as type/subtype (ISO 19005-3, 6.8; unknown files: application/octet-stream).</summary>
    internal static bool MimeType(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"^[-\w+.]+/[-\w+.]+$");

    /// <summary>
    /// The standard structure types of PDF/A, those of the namespace for PDF 1.7 (ISO 32000-1, 14.8.4; <see cref="Accessibility.StandardStructure"/>);
    /// PDF/A-1: PDF Reference 1.4, 9.7.4, without THead, TBody, TFoot and Annot.
    /// </summary>
    internal static bool StandardStructureType(string type, int part) =>
        Accessibility.StandardStructure.IsStandard(type, Accessibility.StructureSpace.Pdf17) && !(part == 1 && type is "THead" or "TBody" or "TFoot" or "Annot");

    /// <summary>
    /// A language identifier as level a asks (ISO 19005-2, 6.7.4: ISO 32000-1, 14.9.2; ISO 19005-1, 6.8.4: RFC 1766): empty, or a primary tag
    /// of one to eight letters with subtags of one to eight letters (and, from part 2 on, digits) after hyphens.
    /// </summary>
    internal static bool LanguageTag(PdfString text, int part) => LanguageTag(RawText(text), part);

    /// <summary>A text string as it stands, without the cleaning <see cref="PdfString.ToText"/> does (a zero at its end stays: strict readers see it).</summary>
    internal static string RawText(PdfString text) =>
        text.Bytes is [0xFE, 0xFF, ..] ? System.Text.Encoding.BigEndianUnicode.GetString(text.Bytes, 2, (text.Bytes.Length - 2) / 2 * 2)
        : System.Text.Encoding.Latin1.GetString(text.Bytes);

    internal static bool LanguageTag(string text, int part) =>
        text.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(text, part == 1 ? "^[a-zA-Z]{1,8}(-[a-zA-Z]{1,8})*$" : "^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$");

    /// <summary>The text has characters of the private use areas (U+E000–U+F8FF, planes 15 and 16).</summary>
    internal static bool HasPrivateUse(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is >= 0xE000 and <= 0xF8FF or >= 0xF0000) return true;
        return false;
    }

    /// <summary>
    /// A replacement text (/ActualText) without the characters of Unicode's private use areas (ISO 19005-4, 6.2.10.8: they say nothing without
    /// the font); null where nothing is left.
    /// </summary>
    internal static PdfString? WithoutPrivateUse(PdfString text)
    {
        var kept = string.Concat(text.ToText().EnumerateRunes().Where(r => r.Value is not (>= 0xE000 and <= 0xF8FF or >= 0xF0000)));
        return kept.Length == 0 ? null : new PdfString(PdfTextEncoding.Encode(kept));
    }

    /// <summary>A soft-mask dictionary (ISO 32000-1, 11.6.5.2): its /TR is a transfer function of the mask, which PDF/A allows.</summary>
    internal static bool IsSoftMask(PdfDictionary dict) =>
        dict.GetName(PdfName.Type)?.Value == "Mask" || dict.GetName(PdfName.Get("S"))?.Value is "Luminosity" or "Alpha" && dict.ContainsKey(PdfName.Get("G"));

    /// <summary>
    /// Whether a part allows an action (ISO 19005-2, 6.5.1; ISO 19005-4, 6.6.1): GoTo, GoToR, GoToE, Thread, URI, SubmitForm and the four named
    /// page actions; part 4 also scripts, the actions of rich media and of document parts, PDF/A-4e also those of layers and 3D views; part 1
    /// (ISO 19005-1, 6.6.1) not GoToE, which PDF 1.4 does not know.
    /// </summary>
    /// <summary>
    /// Whether the /A of a dictionary is an action by its place: an annotation's (it has a /Rect and a /Subtype) or an outline item's (a /Title)
    /// — also without /S, which then makes it an action of no allowed type. Elsewhere /A is something else (the attributes of a structure
    /// element).
    /// </summary>
    internal static bool HoldsAction(PdfDictionary owner) =>
        owner.ContainsKey(PdfName.Get("Rect")) && owner.ContainsKey(PdfName.Subtype) || owner.ContainsKey(PdfName.Get("Title")) && !owner.ContainsKey(PdfName.Get("S"));

    internal static bool AllowedAction(PdfDictionary action, PdfAConformance part) =>
        action.GetName(PdfName.Get("S"))?.Value is "GoTo" or "GoToR" or "Thread" or "URI" or "SubmitForm" ||
        action.GetName(PdfName.Get("S"))?.Value == "GoToE" && part.Part() != 1 ||
        action.GetName(PdfName.Get("S"))?.Value == "Named" && action.GetName(PdfName.Get("N"))?.Value is { } name && AllowedNamed.Contains(name) ||
        part.Part() == 4 && action.GetName(PdfName.Get("S"))?.Value is "JavaScript" or "GoToDp" or "RichMediaExecute" ||
        part == PdfAConformance.A4e && action.GetName(PdfName.Get("S"))?.Value is "SetOCGState" or "GoTo3DView";

    /// <summary>
    /// Whether a part allows an annotation type (6.3.1): part 4 also projections, file attachments only in PDF/A-4f, 3D and rich media only in
    /// PDF/A-4e; part 1 (ISO 19005-1, 6.5.2) only the types of PDF 1.4 without file attachments, sounds and movies.
    /// </summary>
    internal static bool AllowedAnnotation(string? subtype, PdfAConformance part) =>
        subtype is not null && (part.Part() switch
        {
            4 => Annotations.Contains(subtype) && (subtype != "FileAttachment" || part == PdfAConformance.A4f) || subtype == "Projection" ||
                 part == PdfAConformance.A4e && subtype is "3D" or "RichMedia",
            1 => Annotations1.Contains(subtype),
            _ => Annotations.Contains(subtype),
        });

    /// <summary>
    /// Whether a part allows an annotation (<see cref="AllowedAnnotation(string?, PdfAConformance)"/>), and a 3D annotation's data are of a kind
    /// PDF/A-4e allows (ISO 19005-4, 6.1.6.1: a 3D stream of U3D or PRC — directly or through a 3D reference).
    /// </summary>
    internal static bool AllowedAnnotation(PdfDictionary annot, PdfAConformance part) =>
        AllowedAnnotation(annot.GetName(PdfName.Subtype)?.Value, part) && (annot.GetName(PdfName.Subtype)?.Value != "3D" || ThreeDData(annot) is not { } data ||
                                                                          ThreeDKind(data));

    /// <summary>The 3D stream of a 3D annotation (/3DD, or the /3DD of a 3D reference dictionary there); null if it has none.</summary>
    internal static PdfStream? ThreeDData(PdfDictionary annot) => annot.Get(PdfName.Get("3DD")) switch
    {
        PdfStream stream => stream,
        PdfDictionary reference => reference.Get(PdfName.Get("3DD")) as PdfStream,
        _ => null,
    };

    /// <summary>A 3D stream of a kind PDF/A-4e allows (ISO 19005-4, 6.1.6.1; ISO 32000-2, 13.6.3): /Subtype U3D or PRC.</summary>
    internal static bool ThreeDKind(PdfStream data) => data.Dictionary.GetName(PdfName.Subtype)?.Value is "U3D" or "PRC";

    private static readonly HashSet<string> Annotations1 = new(["Text", "Link", "FreeText", "Line", "Square", "Circle", "Highlight", "Underline",
        "Squiggly", "StrikeOut", "Stamp", "Ink", "Popup", "Widget", "PrinterMark", "TrapNet"], StringComparer.Ordinal);

    /// <summary>What carries additional actions (/AA): the catalogue, a page, a widget, a form field, another annotation.</summary>
    internal enum TriggerOwner
    {
        Catalogue,
        Page,
        Widget,
        Field,
        Annotation,
    }

    /// <summary>The owner of an /AA entry by its dictionary: a widget, a form field (/FT, or a node of the field tree), another annotation, a page, the catalogue.</summary>
    internal static TriggerOwner OwnerOf(PdfDictionary dict) =>
        dict.GetName(PdfName.Subtype)?.Value == "Widget" ? TriggerOwner.Widget
        : dict.ContainsKey(PdfName.Get("FT")) || dict.ContainsKey(PdfName.Get("T")) && (dict.ContainsKey(PdfName.Kids) || dict.ContainsKey(PdfName.Parent)) ? TriggerOwner.Field
        : dict.ContainsKey(PdfName.Get("Rect")) && dict.ContainsKey(PdfName.Subtype) ? TriggerOwner.Annotation
        : dict.GetName(PdfName.Type)?.Value == "Page" ? TriggerOwner.Page
        : TriggerOwner.Catalogue;

    /// <summary>
    /// Whether a part allows a trigger of /AA on its owner: parts 2 and 3 none on the catalogue, pages, widgets and form fields (6.4.1, 6.5.2),
    /// those of other annotations; part 1 (ISO 19005-1, 6.6.2) none on the catalogue, widgets and form fields, those of pages and other
    /// annotations; part 4 (6.6.3) every one on widgets and form fields, elsewhere only those of the mouse and the focus (E, X, D, U, Fo, Bl).
    /// </summary>
    internal static bool AllowedTrigger(string key, TriggerOwner owner, PdfAConformance part) => part.Part() switch
    {
        4 => owner is TriggerOwner.Widget or TriggerOwner.Field || key is "E" or "X" or "D" or "U" or "Fo" or "Bl",
        1 => owner is TriggerOwner.Annotation or TriggerOwner.Page,
        _ => owner == TriggerOwner.Annotation,
    };

    /// <summary>What the pages use (in their content, forms, patterns, images and appearances), measured before a PDF/A copy is written.</summary>
    /// <param name="Rgb">DeviceRGB is drawn with.</param>
    /// <param name="Cmyk">DeviceCMYK is drawn with.</param>
    /// <param name="FontCodes">Per simple font (the dictionary as read): the codes text is shown with, visible or not.</param>
    /// <param name="Rewrites">Every content stream the pages draw (as read): each is written anew (<see cref="ContentConformance"/>).</param>
    /// <param name="Borrowed">Forms and Type 3 fonts (their dictionaries as read) without resources of their own that use some: the resources
    /// they are drawn with, which the copy gives them (6.2.2).</param>
    /// <param name="IccCmyk">An ICC-based CMYK space is drawn with.</param>
    /// <param name="Collections">CIDFonts (their dictionaries as read) whose character collection does not match the CMap of their composite font:
    /// the collection of the CMap, which the copy gives them (6.2.11.3.1; it only names the CIDs, the glyphs stay).</param>
    /// <param name="Shown">Per font that shows text (its dictionary as read): its document and the codes shown (code, length, invisible).</param>
    /// <param name="Missing">Per content stream (as read): the names its resources lack (category, name) — the writer draws them as the display
    /// does (<see cref="ContentConformance.Rewrite"/>); a missing font gets <see cref="MissingFont"/> under its name.</param>
    /// <param name="DeviceGroups">Transparency groups (as read) whose ICC-based CMYK blending space a colour inside uses as well: written
    /// without their colour space (PDF/A-4).</param>
    /// <param name="Deep">Content streams (as read) whose graphics states are nested deeper than 28 levels: the writer reduces the nesting
    /// (<see cref="ContentNesting"/>), a page's content as one stream.</param>
    /// <param name="PageResources">The content streams of pages (as read) with the resources of their page — a stream of a page has none of its
    /// own, and the writer needs them to write it anew (fonts of long strings, colour spaces whose values change).</param>
    internal sealed record Usage(bool Rgb, bool Cmyk, IReadOnlyDictionary<PdfDictionary, bool[]> FontCodes, IReadOnlySet<PdfStream> Rewrites,
        IReadOnlyDictionary<PdfDictionary, PdfDictionary> Borrowed, bool IccCmyk, IReadOnlyDictionary<PdfDictionary, PdfDictionary> Collections,
        IReadOnlyDictionary<PdfDictionary, (PdfDocument Doc, HashSet<(uint Code, int Length, bool Invisible)> Codes)> Shown,
        IReadOnlyDictionary<PdfStream, HashSet<(string Category, PdfName Name)>> Missing, IReadOnlySet<PdfDictionary> DeviceGroups,
        IReadOnlySet<PdfStream> Deep, IReadOnlyDictionary<PdfStream, PdfDictionary> PageResources)
    {
        /// <summary>Nothing used (before the pages are measured).</summary>
        public static Usage Nothing { get; } = new(false, false, new Dictionary<PdfDictionary, bool[]>(), new HashSet<PdfStream>(),
            new Dictionary<PdfDictionary, PdfDictionary>(), false, new Dictionary<PdfDictionary, PdfDictionary>(),
            new Dictionary<PdfDictionary, (PdfDocument, HashSet<(uint, int, bool)>)>(), new Dictionary<PdfStream, HashSet<(string, PdfName)>>(),
            new HashSet<PdfDictionary>(), new HashSet<PdfStream>(), new Dictionary<PdfStream, PdfDictionary>());
    }

    /// <summary>
    /// The font the display draws text with whose font its resources lack (a Helvetica of the standard 14, standard encoding): the writer
    /// gives the content this font under the missing name, embedded like every standard font.
    /// </summary>
    internal static PdfDictionary MissingFont { get; } = MakeMissingFont();

    private static PdfDictionary MakeMissingFont()
    {
        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Get("Font"));
        font.Set(PdfName.Subtype, PdfName.Get("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));
        return font;
    }

    /// <summary>
    /// What the pages use: device colour spaces, so that the PDF/A writer adds a default colour space — and with it a colour profile of
    /// 120 kilobytes — only where one is drawn with; and the codes of each simple font, so that an embedded font has to match the file only
    /// in the characters that are shown (ISO 19005-2, 6.2.11.5, applies to them).
    /// </summary>
    internal static Usage Used(IEnumerable<(PdfDocument Doc, PdfPage Page)> pages, CancellationToken cancel = default)
    {
        var runs = new Dictionary<PdfDocument, Run>(ReferenceEqualityComparer.Instance);
        var codes = new Dictionary<PdfDictionary, bool[]>(ReferenceEqualityComparer.Instance);
        var rewrites = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        var borrowed = new Dictionary<PdfDictionary, PdfDictionary>(ReferenceEqualityComparer.Instance);
        var collections = new Dictionary<PdfDictionary, PdfDictionary>(ReferenceEqualityComparer.Instance);
        var shown = new Dictionary<PdfDictionary, (PdfDocument, HashSet<(uint, int, bool)>)>(ReferenceEqualityComparer.Instance);
        var missing = new Dictionary<PdfStream, HashSet<(string, PdfName)>>(ReferenceEqualityComparer.Instance);
        var deviceGroups = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var deep = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        var pageResources = new Dictionary<PdfStream, PdfDictionary>(ReferenceEqualityComparer.Instance);
        bool rgb = false, cmyk = false, iccCmyk = false;
        foreach (var (doc, page) in pages)
        {
            if (!runs.TryGetValue(doc, out var run))
                runs[doc] = run = new Run(doc, cancel) { FontCodes = codes, Rewrites = rewrites, Borrowed = borrowed, Collections = collections, Shown = shown, MissingNames = missing, DeviceGroups = deviceGroups, Deep = deep, PageResources = pageResources };
            try
            {
                run.Page(page);
            }
            catch (PdfException)
            {
                // a page that cannot be read draws nothing
            }
            rgb |= run.UsesRgb;
            cmyk |= run.UsesCmyk;
            iccCmyk |= run.UsesIccCmyk;
        }
        return new Usage(rgb, cmyk, codes, rewrites, borrowed, iccCmyk, collections, shown, missing, deviceGroups, deep, pageResources);
    }

    /// <summary>The filters ISO 19005-2, 6.1.7.2 allows in streams (not LZW, not Crypt, no abbreviations).</summary>
    private static readonly HashSet<string> StandardFilters = new(["ASCIIHexDecode", "ASCII85Decode", "FlateDecode", "RunLengthDecode", "CCITTFaxDecode",
        "JBIG2Decode", "DCTDecode", "JPXDecode"], StringComparer.Ordinal);

    /// <summary>The predefined CMaps of ISO 32000-1, 9.7.5.2, Table 118: the only ones a PDF/A file names without embedding them (6.2.11.3.3).</summary>
    internal static readonly HashSet<string> Table118 = new([
        "Identity-H", "Identity-V", "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V",
        "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V", "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V",
        "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H",
        "UniCNS-UTF16-V", "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V",
        "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V", "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
        "UniJIS-UTF16-H", "UniJIS-UTF16-V", "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V", "KSCpc-EUC-H",
        "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V"], StringComparer.Ordinal);

    /// <summary>
    /// Whether an embedded CMap maps codes to CIDs (cidchar, cidrange, notdef ranges or another CMap it uses) — not only to Unicode text
    /// (bfchar, bfrange: the form of a /ToUnicode map, which strict readers do not take as an encoding; ISO 32000-1, 9.7.5.4).
    /// </summary>
    internal static bool CidCMap(byte[] data)
    {
        var text = System.Text.Encoding.Latin1.GetString(data);
        return !(text.Contains("beginbfchar", StringComparison.Ordinal) || text.Contains("beginbfrange", StringComparison.Ordinal)) ||
               text.Contains("begincidchar", StringComparison.Ordinal) || text.Contains("begincidrange", StringComparison.Ordinal) ||
               text.Contains("usecmap", StringComparison.Ordinal);
    }

    /// <summary>The largest CID an embedded CMap names (cidchar, cidrange); 0 if none.</summary>
    internal static long MaxCid(byte[] data)
    {
        var text = System.Text.Encoding.Latin1.GetString(data);
        long max = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"begincidchar(.*?)endcidchar", System.Text.RegularExpressions.RegexOptions.Singleline))
            foreach (System.Text.RegularExpressions.Match e in System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"<[0-9A-Fa-f\s]*>\s*(\d+)"))
                if (long.TryParse(e.Groups[1].Value, out var cid)) max = Math.Max(max, cid);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"begincidrange(.*?)endcidrange", System.Text.RegularExpressions.RegexOptions.Singleline))
            foreach (System.Text.RegularExpressions.Match e in System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"<([0-9A-Fa-f\s]*)>\s*<([0-9A-Fa-f\s]*)>\s*(\d+)"))
                if (long.TryParse(e.Groups[3].Value, out var cid) && Hex(e.Groups[1].Value) is { } low && Hex(e.Groups[2].Value) is { } high)
                    max = Math.Max(max, cid + Math.Max(0, high - low));
        return max;
    }

    private static long? Hex(string digits)
    {
        var clean = string.Concat(digits.Where(Uri.IsHexDigit));
        return clean.Length is > 0 and <= 15 ? Convert.ToInt64(clean, 16) : null;
    }

    /// <summary>
    /// Whether a part allows a filter in streams (ISO 19005-2, 6.1.7.2: the filters of ISO 32000-1, 7.4 but LZW and Crypt; PDF/A-1, 6.1.10,
    /// not JPEG 2000 either, which PDF 1.4 does not know).
    /// </summary>
    internal static bool IsStandardFilter(string name, PdfAConformance part) => StandardFilters.Contains(name) && !(name == "JPXDecode" && part.Part() == 1);

    /// <summary>
    /// The filter names of a stream as they stand in the file (/Filter: a name or an array of names) — the abbreviations of inline images
    /// (AHx, A85, Fl …) are no names of stream filters (ISO 32000-1, 7.4, Table 6; 6.1.7.2), so they are not read as the full ones here.
    /// </summary>
    internal static IEnumerable<string> FilterNames(PdfDictionary dict) => dict.Get(PdfName.Filter) switch
    {
        PdfName name => [name.Value],
        PdfArray names => Enumerable.Range(0, names.Count).Select(names.Get).OfType<PdfName>().Select(n => n.Value),
        _ => [],
    };

    /// <summary>Whether a name (one character per byte) is valid UTF-8 (6.1.8).</summary>
    // checked without an exception for every name that is not (the fuzz run counted each as an error caught inside, No. 913)
    internal static bool Utf8(string name) => System.Text.Unicode.Utf8.IsValid(System.Text.Encoding.Latin1.GetBytes(name));

    /// <summary>The names of the layers in an array, nested arrays included (labels and other entries skipped) — strict readers compare layers
    /// by name (6.9).</summary>
    private static HashSet<string> LayerNames(PdfArray? array)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(PdfArray a, int depth)
        {
            for (var i = 0; i < a.Count && i < 100_000; i++)
                switch (a.Get(i))
                {
                    case PdfDictionary layer when layer.GetName(PdfName.Type)?.Value == "OCG":
                        names.Add(layer.GetString(PdfName.Get("Name"))?.ToText() ?? "");
                        break;
                    case PdfArray inner when depth < 32:
                        Walk(inner, depth + 1);
                        break;
                }
        }
        if (array is not null) Walk(array, 0);
        return names;
    }

    /// <summary>The object numbers of the layers in an array, nested arrays included (labels and other entries skipped).</summary>
    internal static HashSet<int> LayerNumbers(PdfArray? array)
    {
        var numbers = new HashSet<int>();
        void Walk(PdfArray a, int depth)
        {
            for (var i = 0; i < a.Count && i < 100_000; i++)
            {
                if (a[i] is PdfReference r) numbers.Add(r.Id.Number);
                if (depth < 32 && a.Get(i) is PdfArray inner) Walk(inner, depth + 1);
            }
        }
        if (array is not null) Walk(array, 0);
        return numbers;
    }

    /// <summary>PDFKNEAD_PDFA_DEBUG=1: the codes that break a font rule, and fonts the writer could not rebuild, are written to the error output
    /// (for comparing with a reference validator).</summary>
    internal static readonly bool Debug = Environment.GetEnvironmentVariable("PDFKNEAD_PDFA_DEBUG") == "1";

    private sealed partial class Run(PdfDocument doc, CancellationToken cancel)
    {
        public bool UsesRgb { get; private set; }
        public bool UsesCmyk { get; private set; }
        public bool UsesIccCmyk { get; private set; }

        /// <summary>Where to note the codes shown per simple font; null when only checking.</summary>
        public Dictionary<PdfDictionary, bool[]>? FontCodes { get; init; }

        /// <summary>Where to note content streams to write anew; null when only checking (then their faults are reported).</summary>
        public HashSet<PdfStream>? Rewrites { get; init; }

        /// <summary>Where to note content streams nested too deep (the writer reduces them); null when only checking (then it is reported).</summary>
        public HashSet<PdfStream>? Deep { get; init; }

        /// <summary>Where to note the resources of the page each content stream of a page is drawn with; null when only checking.</summary>
        public Dictionary<PdfStream, PdfDictionary>? PageResources { get; init; }

        /// <summary>Where to note the resources borrowed by forms and Type 3 fonts; null when only checking.</summary>
        public Dictionary<PdfDictionary, PdfDictionary>? Borrowed { get; init; }

        /// <summary>Where to note CIDFonts whose collection differs from their CMap's; null when only checking.</summary>
        public Dictionary<PdfDictionary, PdfDictionary>? Collections { get; init; }

        /// <summary>Where to note the codes each font shows; null when only checking.</summary>
        public Dictionary<PdfDictionary, (PdfDocument Doc, HashSet<(uint Code, int Length, bool Invisible)> Codes)>? Shown { get; init; }

        /// <summary>Where to note the names each content stream's resources lack; null when only checking.</summary>
        public Dictionary<PdfStream, HashSet<(string, PdfName)>>? MissingNames { get; init; }

        /// <summary>The content streams being walked (the parts of a page's content, or one form, pattern, glyph procedure).</summary>
        private IReadOnlyList<PdfStream> _streams = [];

        /// <summary>The codes already checked per font (code, length, invisible).</summary>
        private readonly Dictionary<PdfDictionary, HashSet<(uint, int, bool)>> _glyphs = new(ReferenceEqualityComparer.Instance);

        /// <summary>Separation spaces by colourant name: the first one's alternate space and tint transform (6.2.4.4).</summary>
        private readonly Dictionary<string, (PdfObject Alternate, PdfObject Tint)> _separations = new(StringComparer.Ordinal);

        private readonly List<PdfAViolation> _found = [];
        private readonly HashSet<(string, string)> _seen = [];
        private PdfAConformance _part = PdfAConformance.A2b;

        /// <summary>The part to check against; null: the one the document declares.</summary>
        public PdfAConformance? Part { get; init; }

        /// <summary>How deep the document lies in attachments of the document checked (0: that document).</summary>
        public int Depth { get; init; }
        private uint? _intentSpace;

        /// <summary>While a form, pattern or Type 3 font with resources of its own is walked: its dictionary, and the resources around it.</summary>
        private PdfDictionary? _owner, _outer;

        /// <summary>A form or glyph procedure without resources of its own is being walked: inherited default colour spaces do not count.</summary>
        private bool _withoutOwnResources;

        /// <summary>File specifications met, and those an /AF array names (part 3: every one must be associated, 6.8).</summary>
        private readonly HashSet<PdfDictionary> _fileSpecs = new(ReferenceEqualityComparer.Instance), _associated = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfStream> _embedded = new(ReferenceEqualityComparer.Instance);

        /// <summary>PDF/A-4: the space of the page's own output intent and the current blending space of transparency (null where there is none).</summary>
        private uint? _pageIntent, _pageBlend;

        /// <summary>The profile of the output intent, and that of the current blending space where it is ICC-based (PDF/A-4, 6.2.4.2).</summary>
        private PdfStream? _intentProfile, _blendProfile, _pageIntentProfile;

        /// <summary>The transparency group whose ICC-based colour space is the current blending space, and whether it belongs to a soft mask.</summary>
        private PdfDictionary? _blendGroup;
        private bool _inMask;

        /// <summary>
        /// Where to note transparency groups (as read) whose ICC-based CMYK space a colour inside uses as well (PDF/A-4, 6.2.4.2): the writer
        /// leaves out their colour space (it is optional); null when only checking.
        /// </summary>
        public HashSet<PdfDictionary>? DeviceGroups { get; init; }

        /// <summary>The page draws with transparency (6.2.10: without an output intent it needs a group with a colour space).</summary>
        private bool _transparency;
        private readonly HashSet<(object, object?)> _walked = [];
        private readonly HashSet<PdfDictionary> _fontsChecked = new(ReferenceEqualityComparer.Instance);
        private int _page;

        /// <summary>Notes a rule not met — if the part and level checked have it (the catalogue says which ones do).</summary>
        private void Add(PdfARule rule, string text, int? page = null, int test = 0)
        {
            if (rule.Id(_part) is not { } id) return;
            // a test of the clause counted from the rule's number (one of those in Also: docs/FEHLERGEDAECHTNIS.md, No. 503)
            if (test > 1 && int.TryParse(id.AsSpan(id.LastIndexOf('-') + 1), out var first) &&
                $"{id[..id.LastIndexOf('-')]}-{first + test - 1}" is var sub && rule.Also.Contains(sub))
                id = sub;
            if (_seen.Add((id, text))) _found.Add(new PdfAViolation(id[..id.LastIndexOf('-')], text, page ?? (_page > 0 ? _page : null)) { Id = id });
        }

        public IReadOnlyList<PdfAViolation> Check()
        {
            if (Part is { } given) _part = given;
            else if (Declared(doc) is { } part) _part = part;
            FileStructure();
            var catalog = doc.Catalog ?? new PdfDictionary();
            Metadata(catalog);
            OutputIntents(catalog);
            Catalogue(catalog);
            if (_part.Accessible()) Structure(catalog);
            Objects();
            for (var i = 0; i < doc.PageCount; i++)
            {
                cancel.ThrowIfCancellationRequested();
                _page = i + 1;
                Page(doc.Pages[i]);
            }
            _page = 0;
            UsedGlyphsListed();
            Signatures(catalog);
            if (_fileSpecs.Any(spec => !_associated.Contains(spec)))
                Add(PdfARules.AttachmentAssociated, Tr.T("Eine Dateiangabe ist keinem Teil des Dokuments zugeordnet (/AF)."));
            return _found;
        }
    }
}
