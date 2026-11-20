using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Metadata inside objects (docs/RECHERCHE-VERSTECKEN.md, B-3; docs/FEHLERGEDAECHTNIS.md, No. 237): „Metadaten“ removes it from the data of
/// images and colour profiles and from annotations, losslessly; „Copyright- und Lizenztexte“ overwrites the free texts of fonts and
/// profiles with spaces of the same length.
/// </summary>
public sealed class ObjectMetadataTests
{
    /// <summary>A small real JPEG (from Skia).</summary>
    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(new SKColor(200, 40, 40));
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] Segment(byte marker, byte[] body) => [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

    /// <summary>The JPEG with EXIF (camera, serial number), a comment and Photoshop/IPTC data after its start.</summary>
    private static byte[] WithMetadata(byte[] jpeg) =>
    [
        .. jpeg[..2], .. Segment(0xE1, [.. "Exif\0\0"u8, .. "KAMERAGEHEIM Seriennummer 4711"u8]), .. Segment(0xFE, "KOMMENTARGEHEIM"u8.ToArray()),
        .. Segment(0xED, [.. "Photoshop 3.0\0"u8, .. "IPTCGEHEIM"u8]), .. jpeg[2..],
    ];

    /// <summary>
    /// „Bereinigen: Metadaten“ keeps JFIF (it decides the colours) but not the preview a JFIF segment may carry itself (ITU-T T.871, 6.3:
    /// Xthumbnail × Ythumbnail RGB pixels after its fields) — a cropped photo can show whole in it. Before, the segment stayed as it was
    /// (docs/FEHLERGEDAECHTNIS.md, No. 960; Verzeichnis L, L45). The fields stay, the preview is 0 × 0, the image the same.
    /// </summary>
    [Fact]
    public void JfifPreview_Removed_TheFieldsStay()
    {
        var jpeg = Jpeg();
        // the JFIF fields (version 1.02, no units, density 1:1), then a preview of 5 × 1 pixels whose 15 bytes spell a word
        byte[] fields = [.. "JFIF\0"u8, 1, 2, 0, 0, 1, 0, 1, 5, 1];
        var withPreview = (byte[])[.. jpeg[..2], .. Segment(0xE0, [.. fields, .. "VORSCHAUGEHEIM!"u8]), .. jpeg[2..]];
        var cleaned = Assert.IsType<byte[]>(PdfKnead.Core.Pdf.Writing.ObjectMetadata.Jpeg(withPreview));
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("VORSCHAUGEHEIM"u8));
        byte[] head = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. fields[..12], 0, 0];
        Assert.Equal(head, cleaned[..20]);
        using var before = SKBitmap.Decode(withPreview);
        using var after = SKBitmap.Decode(cleaned);
        Assert.Equal(before.Pixels, after.Pixels);
    }

    /// <summary>A minimal ICC profile whose tags are of type 'text'.</summary>
    private static byte[] Profile(params (string Tag, string Text)[] tags)
    {
        var data = new List<byte[]>();
        foreach (var (_, text) in tags) data.Add([.. "text"u8, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(text), 0]);
        var offset = 132 + tags.Length * 12;
        var result = new List<byte>(new byte[offset]);
        var table = new List<(string, int, int)>();
        for (var i = 0; i < tags.Length; i++)
        {
            while (result.Count % 4 != 0) result.Add(0);
            table.Add((tags[i].Tag, result.Count, data[i].Length));
            result.AddRange(data[i]);
        }
        while (result.Count % 4 != 0) result.Add(0);
        var bytes = result.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0x02100000);
        "mntrRGB XYZ "u8.CopyTo(bytes.AsSpan(12));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), (uint)tags.Length);
        for (var i = 0; i < table.Count; i++)
        {
            Encoding.ASCII.GetBytes(table[i].Item1, bytes.AsSpan(132 + i * 12, 4));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(136 + i * 12), (uint)table[i].Item2);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(140 + i * 12), (uint)table[i].Item3);
        }
        return bytes;
    }

    private static bool Has(byte[] data, string text) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    /// <summary>
    /// A page with a JPEG carrying EXIF, a comment and IPTC (and /Alternates, /OPI), a colour profile with the device maker, a comment by
    /// „Max Mustermann“ with its dates, and /LastModified. Before, „Metadaten“ removed only the XMP, all of this stayed. Now the window names
    /// it, and it goes: the JPEG is exactly the one without the segments (nothing re-encoded), the profile keeps description and copyright,
    /// the comment keeps its text.
    /// </summary>
    [Fact]
    public void Metadata_InImagesProfilesAndComments_Removed()
    {
        var jpeg = Jpeg();
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(16)),
            ("Height", PdfInteger.Of(16)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode")),
            ("Alternates", new PdfArray([TestPdf.Dict(("DefaultForPrinting", PdfBoolean.True))])), ("OPI", TestPdf.Dict(("1.3", TestPdf.Dict(("F", new PdfString("original.tif"u8.ToArray()))))))),
            WithMetadata(jpeg)));
        var profile = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))),
            Profile(("desc", "Profil sRGB"), ("dmnd", "HERSTELLERGEHEIM"), ("cprt", "Copyright Beispiel"))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(300, 700, 320, 720)),
            ("Contents", new PdfString("Notiz bleibt"u8.ToArray())), ("T", new PdfString("Max Mustermann"u8.ToArray())),
            ("M", new PdfString("D:20261028120000"u8.ToArray())), ("CreationDate", new PdfString("D:20261027090000"u8.ToArray()))));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note)]));
        page.Set(PdfName.Get("LastModified"), new PdfString("D:20261028120000"u8.ToArray()));
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Im1", TestPdf.Ref(image)))),
            ("ColorSpace", TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(profile)]))))));
        // the profile is used (a resource nothing uses is not written, docs/FEHLERGEDAECHTNIS.md, No. 788)
        pdf.Replace(4, TestPdf.FlateStream("q 100 0 0 100 72 500 cm /Im1 Do Q BT /F1 12 Tf 72 700 Td (Text) Tj ET /CS0 cs 0 0 1 sc 10 10 5 5 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());

        var metadata = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.Metadata);
        Assert.Contains("1 JPEG", metadata.Detail);
        Assert.Contains("1 Farbprofilen", metadata.Detail);
        Assert.Contains("1 Kommentaren", metadata.Detail);

        PdfDocument Save(CleanupItems items)
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = items, CompressStreams = false });
            return PdfDocument.Open(output.ToArray());
        }
        PdfStream Image(PdfDocument d) => (PdfStream)d.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im1"))!;
        byte[] ProfileOf(PdfDocument d) =>
            d.GetStreamData((PdfStream)((PdfArray)d.Pages[0].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"))!).Get(1)!);

        using (var kept = Save(CleanupItems.None))
            Assert.True(Has(kept.GetRawStreamData(Image(kept)), "KAMERAGEHEIM"));   // without the item everything stays

        using var saved = Save(CleanupItems.Metadata);
        var raw = saved.GetRawStreamData(Image(saved));
        Assert.Equal(jpeg, raw);   // exactly the JPEG without the segments: nothing re-encoded
        Assert.False(Image(saved).Dictionary.ContainsKey(PdfName.Get("Alternates")));
        Assert.False(Image(saved).Dictionary.ContainsKey(PdfName.Get("OPI")));
        var cleanProfile = ProfileOf(saved);
        Assert.False(Has(cleanProfile, "HERSTELLERGEHEIM"));
        Assert.True(Has(cleanProfile, "Profil sRGB"));
        Assert.True(Has(cleanProfile, "Copyright Beispiel"));   // licence texts only with „Copyright- und Lizenztexte“
        var annot = (PdfDictionary)((PdfArray)saved.Pages[0].Dictionary.Get(PdfName.Get("Annots"))!).Get(0)!;
        foreach (var key in new[] { "T", "M", "CreationDate" }) Assert.False(annot.ContainsKey(PdfName.Get(key)), key);
        Assert.Equal("Notiz bleibt", annot.GetString(PdfName.Get("Contents"))!.ToText());
        Assert.False(saved.Pages[0].Dictionary.ContainsKey(PdfName.Get("LastModified")));
    }

    /// <summary>Text of a name record (platform 3 or 0: UTF-16, otherwise single-byte), or null.</summary>
    private static string? NameText(byte[] font, int id)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < tables; i++)
        {
            var entry = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, entry, 4) != "name") continue;
            var table = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(entry + 8));
            int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(table + 2));
            int strings = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(table + 4));
            for (var r = 0; r < count; r++)
            {
                var record = table + 6 + r * 12;
                if (BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 6)) != id) continue;
                int platform = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record));
                int length = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 8));
                int offset = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 10));
                var bytes = font.AsSpan(table + strings + offset, length);
                return platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            }
        }
        return null;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            sum += word;
        }
        return sum;
    }

    /// <summary>
    /// „Copyright- und Lizenztexte“ (Wunsch vom 28.10.2026: free texts can carry anything and cannot be checked without reading them):
    /// TrueType keeps its family name, loses copyright, licence and the like, and stays a valid font (same length, checksums right);
    /// CFF loses Notice and Copyright, Type 1 /Notice and /Copyright, a profile description and copyright — all as spaces of the same
    /// length, so that the structure stays valid.
    /// </summary>
    [Fact]
    public void Notices_InFontsAndProfiles_BecomeSpaces()
    {
        var ttf = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        Assert.False(string.IsNullOrWhiteSpace(NameText(ttf, 0)));
        var clean = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(ttf));
        Assert.Equal(ttf.Length, clean.Length);
        Assert.True(string.IsNullOrWhiteSpace(NameText(clean, 0)), NameText(clean, 0));
        Assert.Equal(NameText(ttf, 1), NameText(clean, 1));
        Assert.Equal(OpenTypeFont.Parse(ttf).FamilyName, OpenTypeFont.Parse(clean).FamilyName);
        Assert.Equal(0xB1B0AFBAu, Checksum(clean));
        Assert.Null(ObjectMetadata.FontNotices(clean));   // nothing left

        // CFF: Notice (SID 391) and Copyright (SID 392) in the top DICT; 391 = 248 27, 392 = 248 28
        byte[] Index(params byte[][] items)
        {
            var result = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 1 };
            var at = 1;
            result.Add((byte)at);
            foreach (var item in items) result.Add((byte)(at += item.Length));
            foreach (var item in items) result.AddRange(item);
            return [.. result];
        }
        byte[] cff = [1, 0, 4, 1, .. Index("T"u8.ToArray()), .. Index([248, 27, 1, 248, 28, 12, 0]),
            .. Index("Hinweis geheim"u8.ToArray(), "Copyright geheim"u8.ToArray()), 0, 0];
        var cleanCff = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(cff));
        Assert.Equal(cff.Length, cleanCff.Length);
        Assert.False(Has(cleanCff, "geheim"));

        var type1 = Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0: Test 001\n/FontInfo 2 dict dup begin\n/Notice (Geheimer \\(Hinweis\\) Text) readonly def\n" +
                                            "/Copyright (Copyright Muster) readonly def\nend readonly def\ncurrentfile eexec\n0000");
        var cleanType1 = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(type1));
        var text = Encoding.ASCII.GetString(cleanType1);
        Assert.Equal(type1.Length, cleanType1.Length);
        Assert.DoesNotContain("Geheimer", text);
        Assert.DoesNotContain("Muster", text);
        Assert.Contains("/Notice (", text);
        Assert.Contains(") readonly def\n/Copyright (", text);

        var profile = Profile(("desc", "Profilbeschreibung"), ("cprt", "Copyright Beispiel"));
        var cleanProfile = Assert.IsType<byte[]>(ObjectMetadata.IccNotices(profile));
        Assert.Equal(profile.Length, cleanProfile.Length);
        Assert.False(Has(cleanProfile, "Profilbeschreibung"));
        Assert.False(Has(cleanProfile, "Copyright"));
    }

    /// <summary>The option in a file: the window names the font, and saved with it the font program has no licence text left.</summary>
    [Fact]
    public void Notices_InAFile_FoundAndRemoved()
    {
        var ttf = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(TestPdf.FlateStream(ttf, TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length)))));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("PatrickHand")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(0, -300, 1000, 900)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-300)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("PatrickHand")),
            ("FontDescriptor", TestPdf.Ref(descriptor))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)), ("F2", TestPdf.Ref(font))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F2 20 Tf 72 700 Td (Hallo) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var finding = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.Notices);
        Assert.Contains("1 Schriften", finding.Detail);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.Notices });
        using var saved = PdfDocument.Open(output.ToArray());
        var descriptorOut = ((PdfDictionary)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F2"))!).GetDictionary(PdfName.Get("FontDescriptor"))!;
        var program = saved.GetStreamData((PdfStream)descriptorOut.Get(PdfName.Get("FontFile2"))!);
        Assert.True(string.IsNullOrWhiteSpace(NameText(program, 0)));
        Assert.Equal(ttf.Length, program.Length);
        Assert.DoesNotContain(DocumentCleanup.Inspect(saved, PageItem.From(saved)), f => f.Item == CleanupItems.Notices);
    }
}
