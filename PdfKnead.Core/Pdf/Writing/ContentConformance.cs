using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>What in the syntax of a content stream breaks ISO 19005-2 (<see cref="ContentConformance.Scan"/>).</summary>
[Flags]
internal enum ContentFaults
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>An operator ISO 32000-1 does not define, also between BX and EX (6.2.2) — or a comment right after an operator, which strict
    /// readers take as part of it ("re%…").</summary>
    UnknownOperator = 1,

    /// <summary>A hexadecimal string with an odd number of digits (6.1.6).</summary>
    OddHexString = 2,

    /// <summary>An integer beyond ±2³¹ (6.1.13) — or a sign without digits ("-" alone: the display reads 0, strict readers an integer out of
    /// range), or another integer the parser reads though 7.3.3 does not allow it ("--5"; <see cref="ContentConformance.Canonical"/>).</summary>
    IntegerOutOfRange = 4,

    /// <summary>An inline image to be smoothed when enlarged (/I true, 6.2.8).</summary>
    InlineInterpolate = 8,

    /// <summary>A real number beyond ±3.403·10³⁸ (6.1.13) — or a point without digits (strict readers: a real out of range), or another real
    /// the parser reads though 7.3.3 does not allow it ("1.2.3", "1.5e3").</summary>
    RealOutOfRange = 16,

    /// <summary>A real number other than 0 closer to zero than ±1.175·10⁻³⁸ (6.1.13).</summary>
    RealNearZero = 32,

    /// <summary>A hexadecimal string with characters other than digits and white-space (6.1.6).</summary>
    NonHexDigit = 128,

    /// <summary>A string longer than the part allows (6.1.13; ISO 19005-1, 6.1.12).</summary>
    StringTooLong = 256,

    /// <summary>An array longer than the part allows (ISO 19005-1, 6.1.12: 8 191 entries, also as an operand).</summary>
    ArrayTooLong = 512,
}

/// <summary>
/// The syntax of content streams for PDF/A (KONZEPT.md, section 5.21): finding what ISO 19005-2 forbids there, and writing such a stream anew
/// from what the display reads of it — the same parser, so the page looks the same: unknown operators (which the display skips) go with their
/// operands, hexadecimal strings are written whole, numbers out of range are limited (to values beyond any page), inline images lose the
/// request to be smoothed (as image objects do).
/// </summary>
internal static class ContentConformance
{
    /// <summary>The largest value written: integers may not exceed 2³¹ − 1, and a larger real would be written as such an integer.</summary>
    private const double Limit = 2147483647;

    /// <summary>Finds the faults of a content stream.</summary>
    /// <param name="data">The decoded content.</param>
    /// <returns>The faults; <see cref="ContentFaults.None"/> if there are none.</returns>
    public static ContentFaults Scan(byte[] data)
    {
        var faults = ContentFaults.None;
        var parser = new ContentParser(data);
        var before = 0;
        string? op;
        while ((op = parser.Next()) is not null)
        {
            faults |= Faults(op, parser.Operands, data, before, parser.Position, parser.InlineImage);
            before = parser.Position;
        }
        return faults;
    }

    /// <summary>The faults of one operator with its operands, read from <paramref name="before"/> to <paramref name="after"/>.</summary>
    /// <param name="op">The operator.</param>
    /// <param name="operands">Its operands.</param>
    /// <param name="data">The content.</param>
    /// <param name="before">Where the reading of the operator began (after the previous one).</param>
    /// <param name="after">Where it ended (<see cref="ContentParser.Position"/>).</param>
    /// <param name="inline">For "BI": the inline image.</param>
    /// <param name="realLimit">The largest real the part allows (<see cref="PdfALimits.Real"/>).</param>
    /// <param name="stringLimit">The longest string the part allows, in bytes (<see cref="PdfALimits.String"/>).</param>
    /// <param name="arrayLimit">The longest array the part allows (PDF/A-1: 8 191 entries).</param>
    /// <returns>The faults.</returns>
    public static ContentFaults Faults(string op, IReadOnlyList<Operand> operands, byte[] data, int before, int after, InlineImage? inline = null,
        double realLimit = 3.403e38, int stringLimit = int.MaxValue, int arrayLimit = int.MaxValue)
    {
        var faults = ContentFaults.None;
        foreach (var operand in operands)
        {
            if (operand.Object is PdfString { Bytes.Length: var length } && length > stringLimit ||
                operand.Object is PdfArray parts && parts.Any(p => p is PdfString { Bytes.Length: var partLength } && partLength > stringLimit))
                faults |= ContentFaults.StringTooLong;
            if (operand.Object is PdfArray { Count: var count } && count > arrayLimit) faults |= ContentFaults.ArrayTooLong;
        }
        if (inline is not null && Smoothed(inline.Dictionary)) faults |= ContentFaults.InlineInterpolate;
        // a strict reader ends the image at the first "EI" in its data and reads the rest as operators
        if (inline is not null && EndsEarly(inline.Data)) faults |= ContentFaults.UnknownOperator;
        if (!ContentParser.Operators.Contains(op) || Stray(op) || after < data.Length && data[after] == '%') faults |= ContentFaults.UnknownOperator;
        // (the data of an inline image is binary: not scanned for strings and numbers)
        if (op != "BI") faults |= Raw(data, before, after, realLimit);
        return faults;
    }

    /// <summary>
    /// What only the bytes between <paramref name="from"/> and <paramref name="to"/> show: numbers as written (an integer beyond 32 bits, a real
    /// beyond the limits, a sign or point without digits — "-" alone reads as 0 here, strict readers fail on it), hexadecimal strings with an
    /// odd number of digits or other characters.
    /// </summary>
    /// <param name="canonical">Where given: each number not written as 7.3.3 allows, with the form it is written in instead
    /// (<see cref="Canonical"/>).</param>
    private static ContentFaults Raw(byte[] data, int from, int to, double realLimit, List<(int Start, int End, byte[] Text)>? canonical = null)
    {
        var faults = ContentFaults.None;
        for (var i = from; i < to; i++)
        {
            switch (data[i])
            {
                case (byte)'/':
                    // a name: its characters are no numbers
                    while (i + 1 < to && PdfKnead.Core.Pdf.Parsing.PdfLexer.IsRegular(data[i + 1])) i++;
                    break;
                case (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9' when i == 0 || !PdfKnead.Core.Pdf.Parsing.PdfLexer.IsRegular(data[i - 1]):
                    var start = i;
                    while (i + 1 < to && PdfKnead.Core.Pdf.Parsing.PdfLexer.IsRegular(data[i + 1])) i++;
                    var token = data.AsSpan(start, i - start + 1);
                    faults |= Number(token, realLimit);
                    if (canonical is not null && Canonical(token) is { } text) canonical.Add((start, i + 1, text));
                    break;
                case (byte)'%':
                    while (i < to && data[i] is not (10 or 13)) i++;
                    break;
                case (byte)'(':
                    // a literal string: balanced parentheses, a backslash escapes the next byte
                    var depth = 1;
                    for (i++; i < to && depth > 0; i++)
                        if (data[i] == '\\') i++;
                        else if (data[i] == '(') depth++;
                        else if (data[i] == ')') depth--;
                    i--;
                    break;
                case (byte)'<' when i + 1 < to && data[i + 1] == '<':
                    i++;
                    break;
                case (byte)'<':
                    var digits = 0;
                    for (i++; i < to && data[i] != '>'; i++)
                        if (Uri.IsHexDigit((char)data[i])) digits++;
                        else if (data[i] is not (0 or 9 or 10 or 12 or 13 or 32)) faults |= ContentFaults.NonHexDigit;
                    if (digits % 2 != 0) faults |= ContentFaults.OddHexString;
                    break;
            }
        }
        return faults;
    }

    /// <summary>
    /// A token that starts like a number (sign, point or digit): its faults as PDF/A sees them. A token with other characters than a number has
    /// is an operator or unknown word — not a number (the operator check sees it).
    /// </summary>
    internal static ContentFaults Number(ReadOnlySpan<byte> token, double realLimit = 3.403e38)
    {
        // read as a number, but not written as 7.3.3 allows ("--5", "1.2.3", "1.5e3"): strict readers fail on it, as on "-" alone (S-A A01)
        if (!WellFormed(token) && Canonical(token) is not null)
            return token.IndexOf((byte)'.') >= 0 ? ContentFaults.RealOutOfRange : ContentFaults.IntegerOutOfRange;
        var digits = 0;
        var point = false;
        for (var k = 0; k < token.Length; k++)
        {
            var b = token[k];
            if (b is >= (byte)'0' and <= (byte)'9') digits++;
            else if (b == '.') point = true;
            else if (b is (byte)'+' or (byte)'-' && k == 0) { }
            else return ContentFaults.None;
        }
        if (digits == 0 || !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return point ? ContentFaults.RealOutOfRange : ContentFaults.IntegerOutOfRange;
        if (!point) return value is > int.MaxValue or < int.MinValue ? ContentFaults.IntegerOutOfRange : ContentFaults.None;
        if (Math.Abs(value) > realLimit) return ContentFaults.RealOutOfRange;
        return value != 0 && Math.Abs(value) < 1.175e-38 ? ContentFaults.RealNearZero : ContentFaults.None;
    }

    /// <summary>A number as ISO 32000-2, 7.3.3 writes it: at most one sign at the start, then digits with at most one point, at least one digit.</summary>
    internal static bool WellFormed(ReadOnlySpan<byte> token)
    {
        var k = token.Length > 0 && token[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        var digits = 0;
        var point = false;
        for (; k < token.Length; k++)
        {
            if (token[k] is >= (byte)'0' and <= (byte)'9') digits++;
            else if (token[k] == '.' && !point) point = true;
            else return false;
        }
        return digits > 0;
    }

    /// <summary>
    /// A token the parser reads as a number though 7.3.3 does not allow its form — "--5", "1.2.3", "-" alone, "1.5e3" (S-A A01;
    /// <see cref="PdfLexer"/>): the number as the display reads it, in the form of 7.3.3. Null for a well-formed number and for a token that
    /// is no number („12abc“ is an unknown operator).
    /// </summary>
    /// <param name="token">The bytes of the token (white space and delimiters around it).</param>
    internal static byte[]? Canonical(ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty || WellFormed(token) || token[0] is not ((byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9')) return null;
        using var source = PdfSource.FromBytes(token.ToArray());
        var lexer = new PdfLexer(source);
        var read = lexer.Next();
        if (lexer.Position != token.Length) return null;
        return read.Kind switch
        {
            PdfTokenKind.Integer => Encoding.ASCII.GetBytes(read.Integer.ToString(CultureInfo.InvariantCulture)),
            PdfTokenKind.Real => Encoding.ASCII.GetBytes(PdfSerializer.FormatReal(read.Real)),
            _ => null,
        };
    }

    /// <summary>
    /// A content stream with what the display reads in a form ISO 32000-2 allows, each part as the display reads it: a number not written as
    /// 7.3.3 allows in that form, with the value the display reads (S-A A01, docs/FEHLERGEDAECHTNIS.md, No. 930); an array or dictionary that
    /// an operator ends before its closing bracket left out, as the display passes over it (S-A A02, No. 931). The page looks the same in
    /// PDF-Knead, and other readers no longer read such content each their own way (one reads „--24“ as −24, others drop the operator). All
    /// else stays byte for byte; inline image data is not looked into.
    /// </summary>
    /// <param name="data">The decoded content.</param>
    /// <returns>The new content; null where there is nothing to repair.</returns>
    public static byte[]? CanonicalSyntax(byte[] data) => CanonicalSyntax(data, data.Length);

    /// <summary>As <see cref="CanonicalSyntax(byte[])"/>, over the first <paramref name="length"/> bytes of a rented buffer.</summary>
    /// <param name="data">The buffer holding the decoded content.</param>
    /// <param name="length">Number of valid bytes.</param>
    /// <returns>The new content; null where there is nothing to repair.</returns>
    public static byte[]? CanonicalSyntax(byte[] data, int length)
    {
        length = Math.Clamp(length, 0, data.Length);
        if (!Suspect(data, length)) return null;
        var found = new List<(int Start, int End, byte[] Text)>();
        var numbers = new List<(int Start, int End, byte[] Text)>();
        var parser = new ContentParser(data, length);
        var before = 0;
        while (parser.Next() is { } op)
        {
            var discarded = parser.Discarded;
            if (discarded is not null && Without(op, parser.Operands) is { } instead)
            {
                // the operator lost what it needs with the broken operand: written as the display reads it — strict readers stop at an
                // operator with too few operands and draw nothing of the rest of the page
                found.Add((before, parser.Position, instead));
                before = parser.Position;
                continue;
            }
            if (discarded is not null)
                foreach (var (start, end) in discarded) found.Add((start, end, " "u8.ToArray()));
            if (op != "BI")
            {
                numbers.Clear();
                Raw(data, before, parser.Position, double.MaxValue, numbers);
                // a number inside a part that goes, goes with it
                foreach (var number in numbers)
                    if (discarded is null || !discarded.Any(d => number.Start < d.End && number.End > d.Start)) found.Add(number);
            }
            before = parser.Position;
        }
        if (found.Count == 0) return null;
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        var output = new MemoryStream(length + 16 * found.Count);
        var from = 0;
        foreach (var (start, end, text) in found)
        {
            output.Write(data, from, start - from);
            output.Write(text);
            from = end;
        }
        output.Write(data, from, length - from);
        return output.ToArray();
    }

    /// <summary>
    /// An operator whose broken operand went (<see cref="ContentParser.Discarded"/>) as the display reads it, when that is not the operator
    /// with what is left: marked content without its property list as marked content without one (BDC → BMC, DP → MP, the tag kept, so
    /// that EMC still closes it); an operator with fewer operands than it needs (a TJ without its array) goes, as the display passes over it.
    /// Null where the operator stays as it is.
    /// </summary>
    private static byte[]? Without(string op, IReadOnlyList<Operand> operands)
    {
        if (op is "BDC" or "DP" && operands.Count == 1 && operands[0].Name is { } tag)
            return [(byte)' ', .. PdfSerializer.ToBytes(tag), .. (op == "BDC" ? " BMC"u8 : " MP"u8)];
        return PageInterpreter.LacksOperands(op, operands) ? " "u8.ToArray() : null;
    }

    /// <summary>
    /// Whether the content may hold what <see cref="CanonicalSyntax(byte[], int)"/> repairs — without the parser, more than it finds, never
    /// less, so that most content is passed by without parsing it (docs/FEHLERGEDAECHTNIS.md, No. 936):
    /// <list type="bullet">
    /// <item>a run of regular characters at a token boundary that looks like a number but is not well-formed; literal and hexadecimal strings,
    /// comments and names are passed over as the lexer reads them (a CID text „&lt;00A1&gt;“ or a date „(18.11.2026)“ holds no number);</item>
    /// <item>opening and closing brackets of arrays and dictionaries not as many — counted over everything, as before, and outside the
    /// strings: unequal in either count sends the content through the parser. An array an operator ends whose bracket closes later („[(a) TJ
    /// (b) ] Tj“) keeps the counts equal and is not found here: the display reads it alike, only the copy keeps the bytes;</item>
    /// <item>an inline image (its data can hold anything; the parser reads it as a whole).</item>
    /// </list>
    /// </summary>
    internal static bool Suspect(byte[] data, int length)
    {
        int arrays = 0, dictionaries = 0;
        for (var i = 0; i < length; i++)
            switch (data[i])
            {
                case (byte)'[': arrays++; break;
                case (byte)']': arrays--; break;
                case (byte)'<' when i + 1 < length && data[i + 1] == '<': dictionaries++; i++; break;
                case (byte)'>' when i + 1 < length && data[i + 1] == '>': dictionaries--; i++; break;
            }
        if (arrays != 0 || dictionaries != 0) return true;
        var pos = 0;
        while (pos < length)
        {
            var c = data[pos];
            switch (c)
            {
                case 0 or 9 or 10 or 12 or 13 or 32:
                    pos++;
                    continue;
                case (byte)'%':
                    while (pos < length && data[pos] is not (10 or 13)) pos++;
                    continue;
                case (byte)'(':
                    // as PdfLexer.ReadLiteralString: nested brackets, a backslash takes the next byte; not closed: up to the end
                    pos++;
                    for (var depth = 1; pos < length && depth > 0; pos++)
                        if (data[pos] == '\\') pos++;
                        else if (data[pos] == '(') depth++;
                        else if (data[pos] == ')') depth--;
                    continue;
                case (byte)'<' when pos + 1 < length && data[pos + 1] == '<':
                    dictionaries++;
                    pos += 2;
                    continue;
                case (byte)'<':
                    // as PdfLexer.ReadHexString: up to „>“ or the end
                    pos++;
                    while (pos < length && data[pos] != '>') pos++;
                    pos++;
                    continue;
                case (byte)'>' when pos + 1 < length && data[pos + 1] == '>':
                    dictionaries--;
                    pos += 2;
                    continue;
                case (byte)'[':
                    arrays++;
                    pos++;
                    continue;
                case (byte)']':
                    arrays--;
                    pos++;
                    continue;
                case (byte)'/':
                    // a name: the regular characters after the solidus
                    pos++;
                    while (pos < length && PdfLexer.IsRegular(data[pos])) pos++;
                    continue;
            }
            if (!PdfLexer.IsRegular(c))
            {
                pos++;
                continue;
            }
            var start = pos;
            while (pos < length && PdfLexer.IsRegular(data[pos])) pos++;
            var token = data.AsSpan(start, pos - start);
            if (token.SequenceEqual("BI"u8)) return true;
            if (c is (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9' && !WellFormed(token)) return true;
        }
        return arrays != 0 || dictionaries != 0;
    }

    /// <summary>
    /// A resource name within the 127 bytes PDF/A allows (6.1.13): a longer one becomes „R“ and 32 hexadecimal digits of its SHA-256 sum — the
    /// same in the resources and in every content that names it, without a table between them, and unlike any name a program writes.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name, or its short form.</returns>
    internal static PdfName ShortName(PdfName name) =>
        Encoding.Latin1.GetByteCount(name.Value) <= 127 ? name
        : PdfName.Get("R" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(name.Value)))[..32]);

    /// <summary>The resource category an operator names, and the name; null if it names none.</summary>
    /// <summary>
    /// „ID“ or „EI“ on their own: they belong to an inline image (BI … ID … EI), which the parser reads as a whole — standing alone they are no
    /// operator ISO 32000 defines, and the display does nothing with them (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    internal static bool Stray(string op) => op is "ID" or "EI";

    /// <summary>A text-showing operator without the text the display shows with it (a string; for TJ an array): it draws nothing.</summary>
    internal static bool WithoutText(string op, IReadOnlyList<Operand> operands) => op switch
    {
        "Tj" or "'" => operands.Count < 1 || operands[0].Object is not PdfString,
        "\"" => operands.Count < 3 || operands[2].Object is not PdfString,
        "TJ" => operands.Count < 1 || operands[0].Object is not PdfArray,
        _ => false,
    };

    internal static (string Category, PdfName Name)? Named(string op, IReadOnlyList<Operand> operands) => op switch
    {
        "Do" when operands.Count > 0 && operands[^1].Name is { } n => ("XObject", n),
        "gs" when operands.Count > 0 && operands[^1].Name is { } n => ("ExtGState", n),
        "sh" when operands.Count > 0 && operands[^1].Name is { } n => ("Shading", n),
        "Tf" when operands.Count > 1 && operands[0].Name is { } n => ("Font", n),
        "cs" or "CS" when operands.Count > 0 && operands[^1].Name is { Value: not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK") } n
            => ("ColorSpace", n),
        "scn" or "SCN" when operands.Count > 0 && operands[^1].Name is { } n => ("Pattern", n),
        "BDC" or "DP" when operands.Count > 1 && operands[1].Name is { } n => ("Properties", n),
        _ => null,
    };

    /// <summary>
    /// Writes a content stream anew without its faults (see the class), and — given <see cref="ContentRewrite.Missing"/> — without names its
    /// resources lack, as the display draws them: an image, form, graphics state or shading that is not there draws nothing (the operator goes),
    /// a missing colour space draws in grey (DeviceGray), a missing property list of marked content leaves the content visible (BMC, MP), a
    /// missing pattern paints the light grey of a pattern without its pattern. A missing font stays: the writer gives the name the font the
    /// display draws with instead. Inline images with a filter PDF/A does not allow in them are unpacked and packed with Flate. Text strings
    /// longer than the part allows are shown as several strings one after the other, at the boundaries of the font's codes (TJ: a string ends
    /// with its own codes, the next goes on where it ended), a TJ array longer than the part allows as several TJ; graphics states nested deeper
    /// than 28 levels are reduced (<see cref="ContentNesting"/>).
    /// </summary>
    /// <param name="data">The decoded content.</param>
    /// <param name="options">What else to observe; null: the limits of ISO 19005-2, no names missing.</param>
    /// <returns>The new content.</returns>
    public static byte[] Rewrite(byte[] data, ContentRewrite? options = null)
    {
        options ??= new ContentRewrite();
        var (missing, noPrivateUse, realLimit) = (options.Missing, options.NoPrivateUse, options.RealLimit);
        var output = new MemoryStream(data.Length + data.Length / 8);
        var parser = new ContentParser(data);
        // the font chosen and the colour spaces whose values change (part of the graphics state, restored by Q)
        PdfName? font = null;
        ColourChange? fill = null, stroke = null;
        var saved = new Stack<(PdfName?, ColourChange?, ColourChange?)>();
        var marks = options.Marks;
        var index = -1;
        string? op;
        while ((op = parser.Next()) is not null)
        {
            index++;
            if (marks is not null)
            {
                // the marked content of a structure made for the copy, by the number of the operator as read
                if (marks.After.TryGetValue(index - 1, out var ends))
                    for (var e = 0; e < ends; e++) output.Write("EMC\n"u8);
                if (marks.Before.TryGetValue(index, out var begin)) output.Write(Encoding.ASCII.GetBytes(begin));
            }
            if (!ContentParser.Operators.Contains(op) || Stray(op)) continue;
            // marked content of an Artifact element of PDF 2.0: artifact content in a copy of PDF 1.x (its EMC stays)
            if (op == "BDC" && options.ArtifactMcids is { Count: > 0 } artifacts && parser.Operands.Count == 2 &&
                (parser.Operands[1].Object is PdfDictionary markedList ? markedList.Get(PdfName.Get("MCID")) as PdfInteger
                    : parser.Operands[1].Name is { } listName ? options.NamedMcid?.Invoke(listName) : null) is { Value: >= 0 and <= int.MaxValue } artifactMcid &&
                artifacts.Contains((int)artifactMcid.Value))
            {
                output.Write("/Artifact BMC\n"u8);
                continue;
            }
            if (op is "BDC" or "DP" && parser.Operands.Count == 2 && parser.Operands[1].Object is PdfDictionary listed && Properties(listed, options) is { } changedList)
            {
                PdfSerializer.Write(parser.Operands[0].Object ?? PdfName.Get("Span"), output);
                output.WriteByte((byte)' ');
                PdfSerializer.Write(changedList, output);
                output.Write(Encoding.ASCII.GetBytes(" " + op + "\n"));
                continue;
            }
            // a font chosen without its size: the display does nothing with it (and strict readers still look for the name)
            if (op == "Tf" && parser.Operands.Count < 2) continue;
            // a text operator without its text: the display does nothing with it (no line moved either) — strict readers count its font as used
            // for rendering all the same, which then must be embedded (docs/FEHLERGEDAECHTNIS.md, No. 340)
            if (WithoutText(op, parser.Operands)) continue;
            if (op == "q") saved.Push((font, fill, stroke));
            else if (op == "Q" && saved.TryPop(out var restored)) (font, fill, stroke) = restored;
            else if (op == "Tf") font = parser.Operands[0].Name;
            else if (op is "g" or "rg" or "k") fill = null;
            else if (op is "G" or "RG" or "K") stroke = null;
            if (options.Colours is { } colours && Colour(op, parser.Operands, colours, ref fill, ref stroke, options.RealLimit) is { } recoloured)
            {
                output.Write(recoloured);
                continue;
            }
            if (op == "BI" && options.Colours is { } inlineColours && parser.InlineImage is { } image && Recoloured(image, inlineColours) is { } converted)
            {
                WriteInlineImage(output, converted, options.InlineLength);
                continue;
            }
            if (op is "Tj" or "'" or "\"" or "TJ" && Shown(op, parser.Operands, font, options, Recoded(op, parser.Operands, font, options.Codes)) is { } shown)
            {
                output.Write(shown);
                continue;
            }
            if (op == "BI")
            {
                if (parser.InlineImage is { } inline) WriteInlineImage(output, inline, options.InlineLength);
                continue;
            }
            var operands = parser.Operands;
            if (missing is not null && Named(op, operands) is var (category, name) && category != "Font" && missing(category, name))
            {
                switch (op)
                {
                    case "cs" or "CS":
                        output.Write(Encoding.ASCII.GetBytes(op == "cs" ? "/DeviceGray cs\n" : "/DeviceGray CS\n"));
                        break;
                    case "BDC":
                        PdfSerializer.Write(operands[0].Object ?? PdfName.Get("Span"), output);
                        output.Write(" BMC\n"u8);
                        break;
                    case "DP":
                        PdfSerializer.Write(operands[0].Object ?? PdfName.Get("Span"), output);
                        output.Write(" MP\n"u8);
                        break;
                    case "scn" or "SCN":
                        output.Write(Encoding.ASCII.GetBytes(op == "scn" ? "0.784314 0.784314 0.784314 rg\n" : "0.784314 0.784314 0.784314 RG\n"));
                        break;
                }
                continue;
            }
            // a rendering intent ISO 32000 does not know: the display draws as with the default (6.2.6)
            if (op == "ri" && operands.Count == 1 && operands[0].Name is { } intent && !PdfACheck.RenderingIntent(intent.Value))
            {
                output.Write("/RelativeColorimetric ri\n"u8);
                continue;
            }
            if (noPrivateUse && op is "BDC" or "DP" && operands.Count == 2 && operands[1].Object is PdfDictionary properties &&
                properties.Get(PdfName.Get("ActualText")) is PdfString actual && PdfACheck.HasPrivateUse(actual.ToText()))
            {
                var kept = new PdfDictionary();
                foreach (var (key, value) in properties.Entries) kept.Set(key, value);
                if (PdfACheck.WithoutPrivateUse(actual) is { } text) kept.Set(PdfName.Get("ActualText"), text);
                else kept.Remove(PdfName.Get("ActualText"));
                PdfSerializer.Write(operands[0].Object ?? PdfName.Get("Span"), output);
                output.WriteByte((byte)' ');
                PdfSerializer.Write(kept, output);
                output.Write(Encoding.ASCII.GetBytes(" " + op + "\n"));
                continue;
            }
            // the abbreviations of the device spaces belong in inline images; the display reads them here too: written in full
            if (op is "cs" or "CS" && operands.Count == 1 && operands[0].Name?.Value is "G" or "RGB" or "CMYK")
            {
                output.Write(Encoding.ASCII.GetBytes(operands[0].Name!.Value switch { "G" => "/DeviceGray ", "RGB" => "/DeviceRGB ", _ => "/DeviceCMYK " } + op + "\n"));
                continue;
            }
            // a resource name too long for PDF/A as its short form (the resources are renamed alike, ShortName)
            var named = Named(op, operands) is var (_, resourceName) && !ReferenceEquals(ShortName(resourceName), resourceName) ? resourceName : null;
            foreach (var operand in operands)
            {
                if (named is not null && ReferenceEquals(operand.Name, named))
                {
                    PdfSerializer.Write(ShortName(named), output);
                    output.WriteByte((byte)' ');
                    continue;
                }
                WriteOperand(output, operand, realLimit);
                output.WriteByte((byte)' ');
            }
            output.Write(Encoding.ASCII.GetBytes(op));
            output.WriteByte((byte)'\n');
        }
        if (marks is not null && marks.After.TryGetValue(index, out var last))
            for (var e = 0; e < last; e++) output.Write("EMC\n"u8);
        var written = output.ToArray();
        return ContentNesting.Depth(written) > ContentNesting.MaxDepth ? ContentNesting.Reduce(written, options.Nest) : written;
    }

    /// <summary>
    /// A property list written in the content, changed where the copy asks: without its MCID (<see cref="ContentRewrite.StripMcids"/>), with
    /// its /Lang a language tag or without it (<see cref="ContentRewrite.Language"/>); null where it stays.
    /// </summary>
    private static PdfDictionary? Properties(PdfDictionary properties, ContentRewrite options)
    {
        var strip = options.StripMcids && properties.ContainsKey(PdfName.Get("MCID"));
        var language = properties.Get(PdfName.Get("Lang")) is PdfString lang && options.Language is { } fix
            ? (Given: PdfACheck.RawText(lang), Fixed: fix(PdfACheck.RawText(lang))) : default;
        if (!strip && (language.Given is null || language.Fixed == language.Given)) return null;
        var changed = new PdfDictionary();
        foreach (var (key, value) in properties.Entries) changed.Set(key, value);
        if (strip) changed.Remove(PdfName.Get("MCID"));
        if (language.Given is not null)
        {
            if (language.Fixed is { } tag) changed.Set(PdfName.Get("Lang"), new PdfString(PdfTextEncoding.Encode(tag)));
            else changed.Remove(PdfName.Get("Lang"));
        }
        return changed;
    }

    /// <summary>
    /// A colour operator in a colour space whose values change (<see cref="ContentRewrite.Colours"/>): cs/CS choose it — followed by its initial
    /// colour changed, as the space it becomes starts with another —, sc/scn/SC/SCN set changed values (a pattern keeps its name). Null where
    /// the operator stays as it is; <paramref name="fill"/> and <paramref name="stroke"/> follow the spaces chosen.
    /// </summary>
    private static byte[]? Colour(string op, IReadOnlyList<Operand> operands, Func<PdfName, ColourChange?> colours, ref ColourChange? fill,
        ref ColourChange? stroke, double realLimit)
    {
        switch (op)
        {
            case "cs" or "CS" when operands.Count == 1 && operands[0].Name is { } name:
            {
                var change = name.Value is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK" ? null : colours(name);
                if (op == "cs") fill = change;
                else stroke = change;
                if (change is null || change.Pattern) return null;
                var output = new MemoryStream();
                PdfSerializer.Write(ShortName(name), output);
                output.Write(Encoding.ASCII.GetBytes(" " + op + "\n"));
                WriteValues(output, change.Convert([.. Enumerable.Repeat(1.0, change.Inputs)]), realLimit);
                output.Write(Encoding.ASCII.GetBytes(op == "cs" ? "scn\n" : "SCN\n"));
                return output.ToArray();
            }
            case "sc" or "scn" or "SC" or "SCN" when (op is "sc" or "scn" ? fill : stroke) is { } current:
            {
                var pattern = operands.Count > 0 && operands[^1].Name is not null ? operands[^1].Name : null;
                var count = operands.Count - (pattern is null ? 0 : 1);
                if (count != current.Inputs || Enumerable.Range(0, count).Any(i => !operands[i].IsNumber)) return null;
                var output = new MemoryStream();
                WriteValues(output, current.Convert([.. Enumerable.Range(0, count).Select(i => operands[i].Number)]), realLimit);
                if (pattern is not null)
                {
                    PdfSerializer.Write(ShortName(pattern), output);
                    output.WriteByte((byte)' ');
                }
                output.Write(Encoding.ASCII.GetBytes((op is "sc" or "scn" ? "scn" : "SCN") + "\n"));
                return output.ToArray();
            }
            default:
                return null;
        }
    }

    private static void WriteValues(MemoryStream output, double[] values, double realLimit)
    {
        foreach (var value in values)
        {
            WriteNumber(output, Math.Round(value, 6), realLimit);
            output.WriteByte((byte)' ');
        }
    }

    /// <summary>
    /// An inline image in a colour space whose values change, with its samples changed as the display draws them: decoded with /Decode,
    /// changed, written with 8 bits per colour, packed with Flate. Null where nothing changes or the samples cannot be read.
    /// </summary>
    private static InlineImage? Recoloured(InlineImage image, Func<PdfName, ColourChange?> colours)
    {
        var dict = image.Dictionary;
        if ((dict.Get(PdfName.Get("CS")) is PdfNull ? dict.Get(PdfName.Get("ColorSpace")) : dict.Get(PdfName.Get("CS"))) is not PdfName name ||
            colours(name) is not { Pattern: false } change)
            return null;
        var width = (int)(dict.GetInteger(PdfName.Get("W")) ?? dict.GetInteger(PdfName.Get("Width")) ?? 0);
        var height = (int)(dict.GetInteger(PdfName.Get("H")) ?? dict.GetInteger(PdfName.Get("Height")) ?? 0);
        var bits = (int)(dict.GetInteger(PdfName.Get("BPC")) ?? dict.GetInteger(PdfName.Get("BitsPerComponent")) ?? 8);
        if (width <= 0 || height <= 0 || (long)width * height > 1 << 24 || bits is not (1 or 2 or 4 or 8 or 16)) return null;
        byte[] samples;
        try
        {
            var decoded = StreamFilters.Decode(image.Data, dict);
            if (decoded.ImageFilter is not null) return null;
            samples = decoded.Data;
        }
        catch (PdfException)
        {
            return null;
        }
        var n = change.Inputs;
        // in long: 2^24 samples of 32 components at 16 bits overflowed int, the row became 0 (docs/FEHLERGEDAECHTNIS.md, No. 1122)
        var row = ((long)width * n * bits + 7) / 8;
        if (row > int.MaxValue) return null;
        if (samples.Length < (long)row * height) return null;
        var decode = Numbers(dict.Get(PdfName.Get("D")) is PdfArray d ? d : dict.Get(PdfName.Get("Decode")) as PdfArray);
        var max = (1 << bits) - 1;
        var input = new double[n];
        double[]? output = null;
        var result = new List<byte>(width * height * 4);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < n; c++)
                {
                    var bit = (long)(x * n + c) * bits;
                    var at = (int)(y * row + bit / 8);
                    var raw = bits switch
                    {
                        16 => samples[at] << 8 | samples[at + 1],
                        8 => samples[at],
                        _ => samples[at] >> (8 - bits - (int)(bit % 8)) & max,
                    };
                    var (low, high) = decode is { } dd && dd.Length >= 2 * n ? (dd[2 * c], dd[2 * c + 1]) : (0.0, 1.0);
                    input[c] = low + raw * (high - low) / max;
                }
                output = change.Convert(input);
                foreach (var v in output) result.Add((byte)Math.Clamp((int)Math.Round(v * 255), 0, 255));
            }
        var recoloured = new PdfDictionary();
        foreach (var (key, value) in dict.Entries)
            if (key.Value is not ("F" or "Filter" or "DP" or "DecodeParms" or "D" or "Decode" or "BPC" or "BitsPerComponent")) recoloured.Set(key, value);
        recoloured.Set(PdfName.Get("BPC"), PdfInteger.Of(8));
        recoloured.Set(PdfName.Get("F"), PdfName.Get("Fl"));
        return new InlineImage(recoloured, Flate.Encode([.. result]));
    }

    private static double[]? Numbers(PdfArray? array) =>
        array is null ? null : [.. Enumerable.Range(0, array.Count).Select(i => array.Get(i).AsNumber() ?? 0)];

    /// <summary>
    /// A text-showing operator whose strings or array are longer than the part allows, as several strings in TJ (and the TJ as several, each
    /// within the limit of arrays); null if it is within the limits. ' moves to the next line first (T*), " sets the word and character spacing
    /// first (Tw, Tc) — as they do themselves.
    /// </summary>
    private static byte[]? Shown(string op, IReadOnlyList<Operand> operands, PdfName? font, ContentRewrite options, List<PdfObject>? recoded = null)
    {
        var items = recoded ?? Items(op, operands);
        if (items is null || recoded is null && items.Count <= options.ArrayLimit && !items.Any(i => i is PdfString s && s.Bytes.Length > options.StringLimit))
            return null;
        var parts = new List<PdfObject>();
        foreach (var item in items)
        {
            if (item is not PdfString { Bytes: var bytes } s || bytes.Length <= options.StringLimit)
            {
                parts.Add(item);
                continue;
            }
            for (var pos = 0; pos < bytes.Length;)
            {
                var room = Math.Min(options.StringLimit, bytes.Length - pos);
                // at a boundary of the font's codes (without the font: of two bytes, which holds for codes of one and of two bytes)
                var end = room == bytes.Length - pos ? bytes.Length : options.CodeEnd?.Invoke(font, bytes, pos, room) ?? pos + (room & ~1);
                if (end <= pos || end > pos + room) end = pos + Math.Max(1, room & ~1);
                parts.Add(new PdfString(bytes[pos..end], s.IsHex));
                pos = end;
            }
        }
        var output = new MemoryStream();
        if (op == "'") output.Write("T*\n"u8);
        else if (op == "\"")
        {
            if (operands.Count < 3) return null;
            WriteNumber(output, operands[0].Number, options.RealLimit);
            output.Write(" Tw "u8);
            WriteNumber(output, operands[1].Number, options.RealLimit);
            output.Write(" Tc\nT*\n"u8);
        }
        for (var start = 0; start < parts.Count; start += options.ArrayLimit)
        {
            var piece = new PdfArray([.. parts.Skip(start).Take(options.ArrayLimit)]);
            WriteOperand(output, new Operand(piece), options.RealLimit);
            output.Write(" TJ\n"u8);
        }
        return output.ToArray();
    }

    /// <summary>The strings and numbers a text operator shows (a TJ array, or its one string); null if it has none.</summary>
    private static List<PdfObject>? Items(string op, IReadOnlyList<Operand> operands) =>
        op == "TJ" ? operands.Count > 0 && operands[^1].Object is PdfArray array ? [.. array] : null
            : operands.Count > 0 && operands[^1].Object is PdfString text ? [text] : null;

    /// <summary>The items of a text operator with <see cref="ContentRewrite.Codes"/> applied to its strings; null where none changes.</summary>
    private static List<PdfObject>? Recoded(string op, IReadOnlyList<Operand> operands, PdfName? font, Func<PdfName?, PdfString, PdfObject[]?>? codes)
    {
        if (codes is null || Items(op, operands) is not { } items) return null;
        List<PdfObject>? result = null;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is PdfString text && codes(font, text) is { } parts)
            {
                result ??= items.Take(i).ToList();
                result.AddRange(parts);
            }
            else result?.Add(items[i]);
        }
        return result;
    }

    private static void WriteOperand(MemoryStream output, Operand operand, double realLimit)
    {
        switch (operand.Object)
        {
            case null or PdfInteger or PdfReal:
                WriteNumber(output, operand.Number, realLimit);
                break;
            case PdfArray array when array.Any(item => item is PdfInteger or PdfReal):
                // a TJ array: its numbers limited as well
                output.WriteByte((byte)'[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) output.WriteByte((byte)' ');
                    if (array[i] is PdfInteger or PdfReal) WriteNumber(output, array[i].AsNumber() ?? 0, realLimit);
                    else PdfSerializer.Write(array[i], output);
                }
                output.WriteByte((byte)']');
                break;
            default:
                PdfSerializer.Write(operand.Object, output);
                break;
        }
    }

    /// <summary>
    /// A number exactly (the shortest form that reads back as the same value), limited to ±2³¹ − 1; a real beyond <paramref name="realLimit"/>
    /// as the nearest integer.
    /// </summary>
    private static void WriteNumber(MemoryStream output, double value, double realLimit = 3.403e38)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < 1.175e-38) value = 0;
        value = Math.Clamp(value, -Limit, Limit);
        if (Math.Abs(value) > realLimit) value = Math.Round(value);
        var text = value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : PdfSerializer.FormatReal(value);
        output.Write(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>Whether an inline image asks to be smoothed (/I or /Interpolate true).</summary>
    private static bool Smoothed(PdfDictionary image) =>
        image.Get(PdfName.Get("I")) is PdfBoolean { Value: true } || image.Get(PdfName.Get("Interpolate")) is PdfBoolean { Value: true };

    /// <summary>The filters ISO 19005-2, 6.1.10 allows in inline images (full and abbreviated names).</summary>
    private static readonly HashSet<string> InlineFilters = new(["ASCIIHexDecode", "ASCII85Decode", "FlateDecode", "RunLengthDecode", "CCITTFaxDecode",
        "DCTDecode", "AHx", "A85", "Fl", "RL", "CCF", "DCT"], StringComparer.Ordinal);

    /// <summary>An inline image's filters break 6.1.10 (LZW, Crypt, filters not listed for inline images).</summary>
    internal static bool InlineFilterFault(PdfDictionary dict) =>
        (dict.Get(PdfName.Get("F")) is PdfNull ? dict.Get(PdfName.Filter) : dict.Get(PdfName.Get("F"))) switch
        {
            PdfName name => !InlineFilters.Contains(name.Value),
            PdfArray names => Enumerable.Range(0, names.Count).Any(i => names.Get(i) is not PdfName n || !InlineFilters.Contains(n.Value)),
            _ => false,
        };

    private static void WriteInlineImage(MemoryStream output, InlineImage inline, bool withLength = false)
    {
        var dict = inline.Dictionary;
        var data = inline.Data;
        if (InlineFilterFault(dict))
        {
            // unpacked up to an image filter (a JPEG stays as it is) and packed with Flate; what cannot be unpacked the display cannot draw either
            try
            {
                var decoded = StreamFilters.Decode(data, dict);
                if (decoded.ImageFilter is { Value: not ("DCTDecode" or "CCITTFaxDecode") }) return;
                var flate = Flate.Encode(decoded.Data);
                var repacked = new PdfDictionary();
                foreach (var (key, value) in dict.Entries)
                    if (key.Value is not ("F" or "Filter" or "DP" or "DecodeParms")) repacked.Set(key, value);
                if (decoded.ImageFilter is { } imageFilter)
                {
                    repacked.Set(PdfName.Get("F"), new PdfArray([PdfName.Get("Fl"), imageFilter]));
                    if (decoded.ImageParms is { } parms) repacked.Set(PdfName.Get("DP"), new PdfArray([PdfNull.Instance, parms]));
                }
                else repacked.Set(PdfName.Get("F"), PdfName.Get("Fl"));
                (dict, data) = (repacked, flate);
            }
            catch (PdfException)
            {
                return;
            }
        }
        if (EndsEarly(data)) (dict, data) = AsHex(dict, data);
        output.Write("BI\n"u8);
        foreach (var (key, value) in dict.Entries)
        {
            // a colour space named by its resource: its short form, as in the resources (ShortName)
            if (key.Value is "CS" or "ColorSpace" && value is PdfName space && !ReferenceEquals(ShortName(space), space))
            {
                PdfSerializer.Write(key, output);
                output.WriteByte((byte)' ');
                PdfSerializer.Write(ShortName(space), output);
                output.WriteByte((byte)'\n');
                continue;
            }
            // smoothing is not allowed (6.2.8): left out, as for image objects; the full names the parser adds are not written twice
            if (key.Value is "I" or "Interpolate" || InlineImage.ExpandedKey(dict, key)) continue;
            // the length is written below, as the data now are (repacked, as hexadecimal digits)
            if (withLength && key.Value is "L" or "Length") continue;
            // a rendering intent none of the four (6.2.6): left out — the display does not use it (No. 500)
            if (key.Value == "Intent" && value is PdfName intent && !PdfACheck.RenderingIntent(intent.Value)) continue;
            PdfSerializer.Write(key, output);
            output.WriteByte((byte)' ');
            PdfSerializer.Write(value, output);
            output.WriteByte((byte)'\n');
        }
        if (withLength) output.Write(Encoding.ASCII.GetBytes(FormattableString.Invariant($"/L {data.Length}\n")));
        output.Write("ID "u8);
        output.Write(data);
        output.Write("\nEI\n"u8);
    }

    /// <summary>
    /// The data of an inline image hold "EI" followed by white space, a delimiter or their end: strict readers end the image there — whatever
    /// precedes it — and read the rest as operators (ISO 32000-2, 8.9.7 leaves finding the end to the reader); the display reads the image to its
    /// actual end (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    internal static bool EndsEarly(byte[] data)
    {
        for (var i = data.AsSpan().IndexOf("EI"u8); i >= 0 && i + 1 < data.Length;)
        {
            if (i + 2 == data.Length || !PdfKnead.Core.Pdf.Parsing.PdfLexer.IsRegular(data[i + 2]))
                return true;
            var next = data.AsSpan(i + 1).IndexOf("EI"u8);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return false;
    }

    /// <summary>
    /// An inline image whose data a strict reader would end early (<see cref="EndsEarly"/>), with ASCIIHex in front of its filters: hexadecimal
    /// digits hold no "EI", and the image stays as it is.
    /// </summary>
    private static (PdfDictionary Dict, byte[] Data) AsHex(PdfDictionary dict, byte[] data)
    {
        var filters = new PdfArray([PdfName.Get("AHx")]);
        var parms = new PdfArray([PdfNull.Instance]);
        // the abbreviation or the full name (a missing entry reads as the null object: before, a full /Filter was lost here, No. 1004)
        var filter = dict.Get(PdfName.Get("F")) is { } shortFilter and not PdfNull ? shortFilter : dict.Get(PdfName.Filter);
        var given = dict.Get(PdfName.Get("DP")) is { } shortParms and not PdfNull ? shortParms : dict.Get(PdfName.DecodeParms);
        if (filter is PdfName single)
        {
            filters.Add(single);
            parms.Add(given is PdfDictionary p ? p : PdfNull.Instance);
        }
        else if (filter is PdfArray chain)
            for (var i = 0; i < chain.Count; i++)
            {
                filters.Add(chain[i]);
                parms.Add(given is PdfArray list && i < list.Count ? list[i] : PdfNull.Instance);
            }
        var result = new PdfDictionary();
        foreach (var (key, value) in dict.Entries)
            if (key.Value is not ("F" or "Filter" or "DP" or "DecodeParms")) result.Set(key, value);
        result.Set(PdfName.Get("F"), filters);
        if (parms.Any(p => p is not PdfNull)) result.Set(PdfName.Get("DP"), parms);
        return (result, Encoding.ASCII.GetBytes(Convert.ToHexString(data) + ">"));
    }

    /// <summary>A full key whose abbreviation the inline image has as well (the parser stores both): only the abbreviation is written.</summary>
}

/// <summary>What <see cref="ContentConformance.Rewrite"/> observes besides the syntax.</summary>
internal sealed class ContentRewrite
{
    /// <summary>Whether a name of a category is missing from the content's resources; null: none is.</summary>
    public Func<string, PdfName, bool>? Missing { get; init; }

    /// <summary>PDF/A-4 (6.2.10.8): a replacement text (/ActualText) of a property list written in the content loses the characters of the
    /// private use areas, as in property lists that are objects.</summary>
    public bool NoPrivateUse { get; init; }

    /// <summary>The largest real the part allows (<see cref="PdfALimits.Real"/>): a real beyond it is written as the nearest integer (PDF/A-1:
    /// ±32 767, a coordinate of more than eleven metres — half a unit there is nothing a page shows).</summary>
    public double RealLimit { get; init; } = 3.403e38;

    /// <summary>The longest string the part allows, in bytes (<see cref="PdfALimits.String"/>).</summary>
    public int StringLimit { get; init; } = int.MaxValue;

    /// <summary>PDF/A-4 (6.1.9): every inline image states the length of its data as written (/L; docs/FEHLERGEDAECHTNIS.md, No. 495).</summary>
    public bool InlineLength { get; init; }

    /// <summary>The longest array the part allows (PDF/A-1: 8 191 entries; later parts have no limit).</summary>
    public int ArrayLimit { get; init; } = int.MaxValue;

    /// <summary>
    /// Where a string shown with a font may end: given the font's resource name (null if none is chosen), the string, where the piece starts and
    /// how many bytes it may have, the end of the last whole code within them. Null: at a boundary of two bytes.
    /// </summary>
    public Func<PdfName?, byte[], int, int, int>? CodeEnd { get; init; }

    /// <summary>
    /// A string shown with a font as it is to be written, given the font's resource name: the string without codes outside the font's code
    /// space and the displacement they took (a string and a number of a TJ array). Null: the string stays.
    /// </summary>
    public Func<PdfName?, PdfString, PdfObject[]?>? Codes { get; init; }

    /// <summary>Makes a form of content and returns its name (<see cref="ContentNesting.Reduce"/>); null: nothing moves into a form.</summary>
    public Func<byte[], PdfName>? Nest { get; init; }

    /// <summary>Marked content to write in (level a, a structure made for the copy): before and after operators by their number in the content.</summary>
    public PdfATagging.PageMarks? Marks { get; init; }

    /// <summary>Marked content loses its numbers (MCID): a structure made for the copy numbers anew (old numbers have no structure).</summary>
    public bool StripMcids { get; init; }

    /// <summary>
    /// Marked content whose MCID is among these becomes artifact content (<c>/Artifact BMC</c>): content of an Artifact element of PDF 2.0
    /// in a copy of PDF 1.x (docs/ABLAUFPLAN.md, 6.5 f); null: none.
    /// </summary>
    public IReadOnlySet<int>? ArtifactMcids { get; init; }

    /// <summary>The MCID of a property list named in the content (/Properties of the resources); null if it names none.</summary>
    public Func<PdfName, PdfInteger?>? NamedMcid { get; init; }

    /// <summary>Level a: /Lang of a property list made a language tag (null: removed); null where nothing is checked.</summary>
    public Func<string, string?>? Language { get; init; }

    /// <summary>
    /// The colour spaces (by resource name) whose values the copy writes in another space — a DeviceN with more colourants than the part
    /// allows becomes its alternate space —, with the change of their values; null for a space that stays.
    /// </summary>
    public Func<PdfName, ColourChange?>? Colours { get; init; }
}

/// <summary>How the values of a colour space change in the copy (<see cref="ContentRewrite.Colours"/>).</summary>
/// <param name="Inputs">The number of values the space has.</param>
/// <param name="Convert">The values it gets instead (those of the space it becomes).</param>
/// <param name="Pattern">A pattern space whose underlying space changes: its values change, the pattern name stays.</param>
internal sealed record ColourChange(int Inputs, Func<double[], double[]> Convert, bool Pattern = false);
