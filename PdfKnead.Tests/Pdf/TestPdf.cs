using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>The /AuthCode a test file gets (ISO/TS 32004).</summary>
public enum TestMac
{
    /// <summary>A stand-in where the MAC is required (macRequired), none otherwise.</summary>
    Default,
    /// <summary>No /AuthCode, also where one is required.</summary>
    None,
    /// <summary>A real standalone MAC over the whole file (classic table only), made with <see cref="Core.Pdf.Security.PdfMac.Token"/> —
    /// which the known-answer test holds against an independent computation.</summary>
    Real,
}

public enum XRefStyle
{
    /// <summary>Classic table "xref … trailer".</summary>
    Table,
    /// <summary>Cross-reference stream, all non-stream objects in one object stream (PDF 1.5).</summary>
    Stream,
}

/// <summary>
/// Creates test PDFs in all variants the reader must handle, so the tests need
/// no foreign files. Objects are created via <see cref="Add"/> (number = position + 1).
/// </summary>
internal sealed class TestPdf
{
    private readonly List<PdfObject> _objects = [];

    public int Add(PdfObject obj)
    {
        _objects.Add(obj);
        return _objects.Count;
    }

    public void Replace(int number, PdfObject obj) => _objects[number - 1] = obj;

    public PdfObject Get(int number) => _objects[number - 1];

    public int RootNumber { get; set; }

    public int? InfoNumber { get; set; }

    public static PdfReference Ref(int number) => new(new PdfObjectId(number, 0));

    public static PdfName N(string name) => PdfName.Get(name);

    public static PdfArray Box(params double[] v) => new(v.Select(x => (PdfObject)new PdfReal(x)));

    public static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (k, v) in entries) d.Set(N(k), v);
        return d;
    }

    /// <summary>Creates a stream with Flate compression.</summary>
    public static PdfStream FlateStream(byte[] data, PdfDictionary? dict = null)
    {
        dict ??= new PdfDictionary();
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        return new PdfStream(dict, ms.ToArray());
    }

    /// <summary>Content of a page (text "Seite n"), so that streams with real content occur in the tests; something to look at: colour areas, a line, a circle and text.</summary>
    public static byte[] PageContent(int page) => Encoding.ASCII.GetBytes(
        $"0.31 0.27 0.9 rg 72 620 {150 + page * 30} 120 re f " +
        "0.95 0.6 0.2 rg 300 500 180 180 re f " +
        "0.1 0.1 0.1 RG 2 w 72 460 m 520 300 l S " +
        "0 0.6 0.4 RG 6 w 200 200 m 200 255 245 300 300 300 c 355 300 400 255 400 200 c 400 145 355 100 300 100 c 245 100 200 145 200 200 c S " +
        $"BT /F1 24 Tf 72 760 Td (Seite {page}) Tj ET");

    /// <summary>
    /// Document with <paramref name="pages"/> pages. MediaBox (A4) and Resources are only on the
    /// /Pages node, and the pages inherit them. Page 2 is rotated and has its own size (Letter landscape).
    /// </summary>
    public static TestPdf Simple(int pages = 3, string? title = "Testdokument")
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(Dict(("Type", N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica"))));
        var kids = new PdfArray();
        for (var i = 1; i <= pages; i++)
        {
            var content = pdf.Add(FlateStream(PageContent(i)));
            var page = Dict(("Type", N("Page")), ("Parent", Ref(pagesNode)), ("Contents", Ref(content)));
            if (i == 2)
            {
                page.Set(PdfName.MediaBox, Box(0, 0, 792, 612));
                page.Set(PdfName.Rotate, PdfInteger.Of(90));
            }
            kids.Add(Ref(pdf.Add(page)));
        }
        pdf.Replace(pagesNode, Dict(("Type", N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(pages)),
            ("MediaBox", Box(0, 0, 595.28, 841.89)),
            ("Resources", Dict(("Font", Dict(("F1", Ref(font))))))));
        ((PdfDictionary)pdf._objects[catalog - 1]).Set(PdfName.Pages, Ref(pagesNode));
        pdf.RootNumber = catalog;
        if (title is not null)
            pdf.InfoNumber = pdf.Add(Dict(("Title", new PdfString(PdfTextEncoding.Encode(title))), ("Producer", new PdfString("PDF-Knead-Test"u8.ToArray()))));
        return pdf;
    }

    /// <summary>
    /// Fills the /AuthCode placeholders of a written file: /ByteRange from 0 to the end around the /MAC gap (padded with spaces, same width),
    /// then the token over those bytes as hexadecimal digits exactly in the gap (ISO/TS 32004, 5.2.3, Table 6).
    /// </summary>
    private static byte[] WithMac(byte[] file, StandardSecurityHandler security, PdfObject encrypt)
    {
        var text = Encoding.Latin1.GetString(file);
        var gapStart = text.IndexOf('<', text.LastIndexOf("/MAC", StringComparison.Ordinal));
        var gapEnd = text.IndexOf('>', gapStart) + 1;
        var rangeAt = text.IndexOf('[', text.LastIndexOf("/ByteRange", StringComparison.Ordinal));
        var rangeEnd = text.IndexOf(']', rangeAt);
        var range = $"[0 {gapStart} {gapEnd} {file.Length - gapEnd}";
        Encoding.ASCII.GetBytes(range.PadRight(rangeEnd - rangeAt)).CopyTo(file, rangeAt);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(file, 0, gapStart);
        hash.AppendData(file, gapEnd, file.Length - gapEnd);
        var salt = ((PdfDictionary)encrypt).GetString(PdfName.Get("KDFSalt"))!.Bytes;
        var token = Core.Pdf.Security.PdfMac.Token(security.MacKek(salt), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), hash.GetHashAndReset());
        Encoding.ASCII.GetBytes(Convert.ToHexStringLower(token)).CopyTo(file, gapStart + 1);
        return file;
    }

    /// <summary>Writes the file.</summary>
    /// <param name="cipher">Encryption; <see cref="PdfCipher.None"/> = none.</param>
    /// <param name="prefix">Foreign bytes before the header (this occurs in practice).</param>
    /// <param name="cryptFiltersApart">The crypt filters of /Encrypt as an object of their own (<c>/CF 7 0 R</c>), as some programs write them.</param>
    /// <param name="withoutId">Encrypted without /ID in the trailer: the key is derived with an empty identifier (as some programs write it).</param>
    /// <param name="patchEncrypt">Changes the /Encrypt dictionary before it is written (a damaged or forged encryption; the file is encrypted with
    /// the handler as created).</param>
    /// <param name="encryptMetadata">False: /EncryptMetadata false — the document's metadata stream (/Metadata of the catalogue) stays plain.</param>
    /// <param name="allMetadataPlain">With <paramref name="encryptMetadata"/> false: every metadata stream plain, as some writers do (against
    /// the standard).</param>
    /// <param name="patchTrailer">Changes the trailer before it is written (keys of other programs, a direct /Info).</param>
    /// <param name="attachmentsOnly">Only embedded files encrypted (AES-128, /EFF with AuthEvent EFOpen; streams and strings Identity): the
    /// document opens without a password, the attachments need <paramref name="user"/>.</param>
    public byte[] Build(XRefStyle style = XRefStyle.Table, PdfCipher cipher = PdfCipher.None, string user = "", string owner = "owner",
        PdfPermissions permissions = PdfPermissions.Print, string version = "1.7", byte[]? prefix = null, bool cryptFiltersApart = false,
        bool withoutId = false, bool attachmentsOnly = false, Action<PdfDictionary>? patchEncrypt = null, bool macRequired = false,
        Action<PdfDictionary>? patchTrailer = null, bool encryptMetadata = true, bool allMetadataPlain = false, TestMac mac = TestMac.Default)
    {
        // a file of ISO/TS 32004 (AES-256 only): MAC required (bit 13 cleared, /KDFSalt), declared in the catalog, a stand-in /AuthCode
        if (macRequired)
            ((PdfDictionary)Get(RootNumber)).Set(PdfName.Get("Extensions"), Dict(("ISO_", new PdfArray([Dict(("Type", N("DeveloperExtensions")),
                ("BaseVersion", N("2.0")), ("ExtensionLevel", PdfInteger.Of(32004)), ("ExtensionRevision", new PdfString(":2024"u8.ToArray())),
                ("URL", new PdfString("https://www.iso.org/standard/45877.html"u8.ToArray())))]))));
        var output = new MemoryStream();
        if (prefix is not null) output.Write(prefix);
        var headerStart = output.Position;
        Write(output, $"%PDF-{version}\n%âãÏÓ\n");

        var firstId = withoutId ? [] : Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
        StandardSecurityHandler? security = null;
        var encryptNumber = 0;
        var all = new List<PdfObject>(_objects);
        if (cipher != PdfCipher.None)
        {
            var (encrypt, handler) = StandardSecurityHandler.Create(cipher, user, owner, permissions, firstId, encryptMetadata, requireMac: macRequired);
            security = handler;
            if (mac == TestMac.Real && !encrypt.ContainsKey(PdfName.Get("KDFSalt")))
                encrypt.Set(PdfName.Get("KDFSalt"), new PdfString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            patchEncrypt?.Invoke(encrypt);
            if (attachmentsOnly)
            {
                encrypt.Set(PdfName.Get("CF"), Dict(("StdCF", Dict(("AuthEvent", N("EFOpen")), ("CFM", N("AESV2")), ("Length", PdfInteger.Of(16))))));
                encrypt.Set(PdfName.Get("StmF"), N("Identity"));
                encrypt.Set(PdfName.Get("StrF"), N("Identity"));
                encrypt.Set(PdfName.Get("EFF"), N("StdCF"));
            }
            if (cryptFiltersApart && encrypt.GetRaw(PdfName.Get("CF")) is PdfDictionary filters)
            {
                all.Add(filters);
                encrypt.Set(PdfName.Get("CF"), Ref(all.Count));
            }
            all.Add(encrypt);
            encryptNumber = all.Count;
        }

        var offsets = new Dictionary<int, long>();
        var compressed = new Dictionary<int, int>();   // number → index in the object stream
        var inObjectStream = style == XRefStyle.Stream
            ? Enumerable.Range(1, all.Count).Where(n => all[n - 1] is not PdfStream && n != encryptNumber).ToList()
            : [];

        var documentXmp = ((PdfDictionary)all[RootNumber - 1]).GetRaw(PdfName.Metadata) is PdfReference xmpRef ? xmpRef.Id.Number : -1;
        bool Plain(int number, PdfStream s) =>
            !encryptMetadata && s.Dictionary.IsType(PdfName.Metadata) && (number == documentXmp || allMetadataPlain);

        void WriteObject(int number, PdfObject obj)
        {
            offsets[number] = output.Position - headerStart;
            var id = new PdfObjectId(number, 0);
            Func<byte[], byte[]>? enc = security is not null && number != encryptNumber && !attachmentsOnly ? b => security.EncryptString(id, b) : null;
            Write(output, $"{number} 0 obj\n");
            if (obj is PdfStream s)
            {
                var data = s.RawData!;
                if (security is not null && !attachmentsOnly && !Plain(number, s)) data = security.EncryptStream(id, data);
                else if (security is not null && s.Dictionary.IsType(PdfName.Get("EmbeddedFile"))) data = security.EncryptStream(id, data, PdfCipher.Aes128);
                s.Dictionary.Set(PdfName.Length, PdfInteger.Of(data.Length));
                PdfSerializer.Write(s.Dictionary, output, enc);
                Write(output, "\nstream\n");
                output.Write(data);
                Write(output, "\nendstream\nendobj\n");
            }
            else
            {
                PdfSerializer.Write(obj, output, enc);
                Write(output, "\nendobj\n");
            }
        }

        for (var n = 1; n <= all.Count; n++)
            if (!inObjectStream.Contains(n)) WriteObject(n, all[n - 1]);

        var size = all.Count + 1;
        var trailer = Dict(("Root", Ref(RootNumber)));
        if (InfoNumber is { } info) trailer.Set(PdfName.Info, Ref(info));
        if (security is not null)
        {
            trailer.Set(PdfName.Encrypt, Ref(encryptNumber));
            if (mac == TestMac.Real)
                trailer.Set(PdfName.Get("AuthCode"), Dict(("MACLocation", N("Standalone")),
                    ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(1_000_000_000), PdfInteger.Of(1_000_000_000), PdfInteger.Of(1_000_000_000)])),
                    ("MAC", new PdfString(new byte[Core.Pdf.Security.PdfMac.TokenLength], true))));
            else if (macRequired && mac == TestMac.Default)
                trailer.Set(PdfName.Get("AuthCode"), Dict(("MACLocation", N("Standalone")),
                    ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)])), ("MAC", new PdfString(new byte[32], true))));
            if (!withoutId) trailer.Set(PdfName.ID, new PdfArray([new PdfString(firstId, true), new PdfString(firstId, true)]));
        }
        patchTrailer?.Invoke(trailer);

        if (style == XRefStyle.Table)
        {
            var xref = output.Position - headerStart;
            Write(output, $"xref\n0 {size}\n0000000000 65535 f \n");
            for (var n = 1; n < size; n++) Write(output, $"{offsets[n]:D10} 00000 n \n");
            trailer.Set(PdfName.Size, PdfInteger.Of(size));
            Write(output, "trailer\n");
            PdfSerializer.Write(trailer, output);
            Write(output, $"\nstartxref\n{xref}\n%%EOF\n");
            return mac == TestMac.Real ? WithMac(output.ToArray(), security!, encrypt: all[encryptNumber - 1]) : output.ToArray();
        }

        // Object stream: header "number offset …", then the objects (strings inside are not encrypted individually)
        var header = new StringBuilder();
        var body = new MemoryStream();
        for (var i = 0; i < inObjectStream.Count; i++)
        {
            var n = inObjectStream[i];
            compressed[n] = i;
            header.Append(n).Append(' ').Append(body.Length).Append(' ');
            PdfSerializer.Write(all[n - 1], body);
            body.WriteByte((byte)'\n');
        }
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        var objStm = FlateStream([.. headerBytes, .. body.ToArray()], Dict(("Type", N("ObjStm")),
            ("N", PdfInteger.Of(inObjectStream.Count)), ("First", PdfInteger.Of(headerBytes.Length))));
        var objStmNumber = size;
        WriteObject(objStmNumber, objStm);

        // Cross-reference stream: W [1 4 2]
        var xrefNumber = objStmNumber + 1;
        var xrefOffset = output.Position - headerStart;
        offsets[xrefNumber] = xrefOffset;
        var entries = new MemoryStream();
        void Entry(int type, long f2, int f3)
        {
            entries.WriteByte((byte)type);
            for (var k = 3; k >= 0; k--) entries.WriteByte((byte)(f2 >> (8 * k)));
            entries.WriteByte((byte)(f3 >> 8));
            entries.WriteByte((byte)f3);
        }
        Entry(0, 0, 65535);
        for (var n = 1; n <= xrefNumber; n++)
        {
            if (compressed.TryGetValue(n, out var index)) Entry(2, objStmNumber, index);
            else Entry(1, offsets[n], 0);
        }
        var xrefDict = Dict(("Type", N("XRef")), ("Size", PdfInteger.Of(xrefNumber + 1)), ("W", new PdfArray([PdfInteger.Of(1), PdfInteger.Of(4), PdfInteger.Of(2)])));
        foreach (var (k, v) in trailer.Entries) xrefDict.Set(k, v);
        var xrefStream = FlateStream(entries.ToArray(), xrefDict);
        // The cross-reference stream is never encrypted
        var saved = security;
        security = null;
        WriteObject(xrefNumber, xrefStream);
        security = saved;
        Write(output, $"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// Appends an incremental update: changed or new objects, with its own table whose /Prev points to the old one.
    /// </summary>
    public static byte[] AppendUpdate(byte[] original, int size, int root, int? info, params (int Number, PdfObject Value)[] changed)
    {
        var text = Encoding.Latin1.GetString(original);
        var startxref = long.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0]);

        var output = new MemoryStream();
        output.Write(original);
        var offsets = new SortedDictionary<int, long>();
        foreach (var (number, value) in changed)
        {
            offsets[number] = output.Position;
            Write(output, $"{number} 0 obj\n");
            PdfSerializer.Write(value, output);
            Write(output, "\nendobj\n");
        }
        var newSize = Math.Max(size, offsets.Keys.Max() + 1);
        var xref = output.Position;
        Write(output, "xref\n");
        foreach (var (number, offset) in offsets)
            Write(output, $"{number} 1\n{offset:D10} 00000 n \n");
        var trailer = Dict(("Size", PdfInteger.Of(newSize)), ("Root", Ref(root)), ("Prev", PdfInteger.Of(startxref)));
        if (info is { } i) trailer.Set(PdfName.Info, Ref(i));
        Write(output, "trailer\n");
        PdfSerializer.Write(trailer, output);
        Write(output, $"\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    public int Count => _objects.Count;

    private static void Write(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));
}
