using System.Buffers.Binary;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Tests.Imaging;

/// <summary>
/// The TIFF writer against TIFF 6.0 (M2 rows of TIFF6): the structure of every file it writes, read field by field as the specification lays
/// it out. PDF-Knead writes TIFF (pages of scans and documents, CMYK images) and reads none.
/// </summary>
public class Tiff6Tests
{
    /// <summary>A field as stored: tag, type, count, where its value lies and how many bytes it takes.</summary>
    private sealed record Field(int Tag, int Type, uint Count, int ValueAt, int Size);

    /// <summary>The bytes of one value of each field type (section 2: types 1–12).</summary>
    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => throw new InvalidDataException($"Feldtyp {type}"),
    };

    /// <summary>
    /// Walks the file as section 2 describes it and checks what the section fixes: the header „MM“ (big-endian, the byte order of every value),
    /// 42, the offset of the first directory; each directory a count of entries, the entries of 12 bytes (tag, type, count, value or offset)
    /// in ascending order of their tags, then the offset of the next directory or 0 after the last; the count is the number of values, not of
    /// bytes; a value of at most four bytes stands left-justified in the entry, a longer one at its offset, on a word boundary and inside the
    /// file; directories begin on a word boundary; the file stays below 2^32 bytes. Returns the fields of each directory.
    /// </summary>
    private static List<List<Field>> Walk(byte[] file, List<(int Start, int End, string What)> used)
    {
        Assert.True(file.LongLength < 1L << 32);
        Assert.Equal((byte[])[(byte)'M', (byte)'M'], file[..2]);
        Assert.Equal(42, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(2)));
        used.Add((0, 8, "Kopf"));
        var directories = new List<List<Field>>();
        for (var at = (long)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)); at != 0;)
        {
            Assert.True(at % 2 == 0 && at + 2 <= file.Length, $"Verzeichnis bei {at}");
            var count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan((int)at));
            Assert.True(count > 0);
            used.Add(((int)at, (int)at + 2 + 12 * count + 4, "Verzeichnis"));
            var fields = new List<Field>();
            for (var i = 0; i < count; i++)
            {
                var e = (int)at + 2 + 12 * i;
                int tag = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(e)), type = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(e + 2));
                var n = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(e + 4));
                Assert.True(n > 0, $"Tag {tag}: Anzahl 0");
                var size = checked((int)(TypeSize(type) * n));
                int valueAt;
                if (size <= 4)
                {
                    valueAt = e + 8;
                    // left-justified: the bytes after the value stay 0
                    Assert.All(file.AsSpan(e + 8 + size, 4 - size).ToArray(), b => Assert.Equal(0, b));
                }
                else
                {
                    var offset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(e + 8));
                    Assert.True(offset % 2 == 0 && offset + (long)size <= file.Length, $"Tag {tag}: Wert bei {offset}");
                    valueAt = (int)offset;
                    used.Add((valueAt, valueAt + size, $"Wert von Tag {tag}"));
                }
                Assert.True(fields.Count == 0 || fields[^1].Tag < tag, $"Tag {tag} nicht aufsteigend");
                fields.Add(new Field(tag, type, n, valueAt, size));
            }
            directories.Add(fields);
            at = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)at + 2 + 12 * count));
        }
        return directories;
    }

    private static uint[] Values(byte[] file, Field f) =>
        [.. Enumerable.Range(0, (int)f.Count).Select(k => f.Type switch
        {
            3 => BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(f.ValueAt + 2 * k)),
            4 => BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(f.ValueAt + 4 * k)),
            _ => (uint)file[f.ValueAt + k],
        })];

    /// <summary>The strips of a directory as used ranges.</summary>
    private static void Strips(byte[] file, List<Field> fields, List<(int Start, int End, string What)> used)
    {
        var offsets = Values(file, fields.Single(f => f.Tag == 273));
        var counts = Values(file, fields.Single(f => f.Tag == 279));
        Assert.Equal(offsets.Length, counts.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            Assert.True(offsets[i] + (long)counts[i] <= file.Length);
            used.Add(((int)offsets[i], (int)(offsets[i] + counts[i]), $"Streifen {i}"));
        }
    }

    /// <summary>No byte is pointed to from two places (section 2, 7: each value and each strip once).</summary>
    private static void NoOverlap(List<(int Start, int End, string What)> used)
    {
        var sorted = used.Where(u => u.End > u.Start).OrderBy(u => u.Start).ToList();
        for (var i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i - 1].End <= sorted[i].Start, $"{sorted[i - 1].What} überlappt {sorted[i].What}");
    }

    private static TiffPage Page(TiffPageKind kind, int width, int height, int seed)
    {
        var random = new Random(seed);
        var rowBytes = kind switch { TiffPageKind.BlackWhite => (width + 7) / 8, TiffPageKind.Gray => width, _ => width * 3 };
        var data = new byte[rowBytes * height];
        random.NextBytes(data);
        return new TiffPage(width, height, kind, 300, (y, row) => data.AsSpan(y * rowBytes, rowBytes).CopyTo(row));
    }

    /// <summary>
    /// The required fields of sections 3, 4, 6 (bilevel, greyscale, RGB) and their values as the writer sets them: ImageWidth, ImageLength,
    /// Compression, PhotometricInterpretation, StripOffsets, RowsPerStrip, StripByteCounts, XResolution, YResolution, ResolutionUnit, and for
    /// greyscale and RGB also BitsPerSample (and SamplesPerPixel 3 for RGB). Black and white: one bit, WhiteIsZero (0 white, the maximum
    /// black), CCITT Group 4 (the extension of section 11 instead of 1, 2 or 32773); greyscale BlackIsZero with 8 bits, RGB 2 with 8, 8, 8 —
    /// both with Deflate (compression 8, an extension); the resolution in pixels per inch (ResolutionUnit 2) as RATIONAL; the strips cover
    /// every row, the last one not padded (section 3).
    /// </summary>
    [Theory]
    [InlineData(TiffPageKind.BlackWhite, 0, 4, 1)]
    [InlineData(TiffPageKind.Gray, 1, 8, 1)]
    [InlineData(TiffPageKind.Rgb, 2, 8, 3)]
    public void RequiredFields_OfSections3To6(TiffPageKind kind, int photometric, int compression, int samples)
    {
        const int width = 210, height = 333;
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => Page(kind, width, height, 5) with { Dpi = 240 }, output);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        uint One(int tag) => Assert.Single(Values(file, fields.Single(f => f.Tag == tag)));
        int[] required = [256, 257, 259, 262, 273, 278, 279, 282, 283, 296, .. kind == TiffPageKind.BlackWhite ? Array.Empty<int>() : [258]];
        Assert.All(required, tag => Assert.Contains(fields, f => f.Tag == tag));
        Assert.Equal(((uint)width, (uint)height), (One(256), One(257)));
        Assert.Equal((uint)compression, One(259));
        Assert.Equal((uint)photometric, One(262));
        Assert.Equal(2u, One(296));
        foreach (var tag in new[] { 282, 283 })
        {
            var f = fields.Single(x => x.Tag == tag);
            Assert.Equal((5, 1u), (f.Type, f.Count));   // RATIONAL
            uint numerator = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(f.ValueAt)), denominator = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(f.ValueAt + 4));
            Assert.Equal(240.0, numerator / (double)denominator, 6);
        }
        Assert.Equal(kind == TiffPageKind.BlackWhite ? [1u] : Enumerable.Repeat(8u, samples).ToArray(), Values(file, fields.Single(f => f.Tag == 258)));
        Assert.Equal(1u, One(284));   // PlanarConfiguration 1, chunky (section 8)
        Assert.Equal((uint)samples, fields.Any(f => f.Tag == 277) ? One(277) : 1u);
        Assert.DoesNotContain(fields, f => f.Tag == 339);   // no SampleFormat: its default 1, unsigned integers (section 19)
        var rowsPerStrip = One(278);
        Assert.Equal((height + rowsPerStrip - 1) / rowsPerStrip, (uint)Values(file, fields.Single(f => f.Tag == 273)).Length);
    }

    /// <summary>
    /// TIFF 6.0, section 2: pages of each kind in one file (one directory per page, the next directory named in the one before, 0 after the
    /// last) and a CMYK image with alpha and profile — the structure as the section lays it out, nothing used twice.
    /// </summary>
    [Fact]
    public void Structure_AsSection2()
    {
        TiffPage[] pages = [Page(TiffPageKind.BlackWhite, 203, 117, 1), Page(TiffPageKind.Gray, 150, 90, 2), Page(TiffPageKind.Rgb, 120, 80, 3)];
        using var paged = new MemoryStream();
        TiffEncoder.EncodePages(pages.Length, i => pages[i], paged);
        var file = paged.ToArray();
        var used = new List<(int, int, string)>();
        var directories = Walk(file, used);
        Assert.Equal(3, directories.Count);
        foreach (var fields in directories) Strips(file, fields, used);
        NoOverlap(used);

        var cmyk = new byte[40 * 30 * 5];
        new Random(4).NextBytes(cmyk);
        using var separated = new MemoryStream();
        TiffEncoder.EncodeCmyk(40, 30, 8, true, (y, row) => cmyk.AsSpan(y * 200, 200).CopyTo(row), [1, 2, 3, 4, 5, 6, 7], separated);
        file = separated.ToArray();
        used = [];
        var single = Assert.Single(Walk(file, used));
        Strips(file, single, used);
        NoOverlap(used);
        Assert.Equal((7, 7), (single.Single(f => f.Tag == 34675).Type, single.Single(f => f.Tag == 34675).Size));   // the profile: UNDEFINED, count = its bytes
    }

    /// <summary>
    /// TIFF 6.0, section 8, NewSubfileType: 32 flag bits, unused ones 0; bit 1 is set when the image is a single page of a multi-page image
    /// (with PageNumber). Three pages in one file: each 2 and PageNumber (n, 3); one page alone: 0 (FG 1017: before 0 on every page).
    /// </summary>
    [Fact]
    public void NewSubfileType_PagesOfAMultiPageImage()
    {
        foreach (var count in new[] { 3, 1 })
        {
            using var output = new MemoryStream();
            TiffEncoder.EncodePages(count, i => Page(TiffPageKind.Gray, 20, 10, i), output);
            var file = output.ToArray();
            var directories = Walk(file, []);
            Assert.Equal(count, directories.Count);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(4, directories[i].Single(f => f.Tag == 254).Type);   // LONG
                Assert.Equal(count > 1 ? 2u : 0u, Assert.Single(Values(file, directories[i].Single(f => f.Tag == 254))));
                Assert.Equal([(uint)i, (uint)count], Values(file, directories[i].Single(f => f.Tag == 297)));
            }
        }
    }

    /// <summary>
    /// TIFF 6.0, section 11, T.6 encoding: each strip encoded on its own as an image of its own, beginning on a fresh byte, and the writer
    /// arranges a multiple of eight lines per strip (Satz 111; FG 1018: before as many rows as fitted into 8 KB, e.g. 26 for a page 2 481
    /// pixels wide). Black and white pages of 2 481 and 203 pixels: RowsPerStrip a multiple of 8, each strip decodes on its own to its rows.
    /// </summary>
    [Theory]
    [InlineData(2481, 700)]
    [InlineData(203, 1300)]
    public void Group4_StripsOfAMultipleOfEightRows_EachItsOwnEncoding(int width, int height)
    {
        using var output = new MemoryStream();
        var page = Page(TiffPageKind.BlackWhite, width, height, 6);
        TiffEncoder.EncodePages(1, _ => page, output);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        var rowsPerStrip = (int)Assert.Single(Values(file, fields.Single(f => f.Tag == 278)));
        Assert.Equal(0, rowsPerStrip % 8);
        var rowBytes = (width + 7) / 8;
        var expected = new byte[rowBytes];
        var top = 0;
        foreach (var (offset, length) in Values(file, fields.Single(f => f.Tag == 273)).Zip(Values(file, fields.Single(f => f.Tag == 279))))
        {
            var rows = Math.Min(rowsPerStrip, height - top);
            var decoded = PdfKnead.Core.Pdf.Filters.CcittFax.DecodeMmr(file, (int)offset, (int)(offset + length), width, rows, out _);
            for (var y = 0; y < rows; y++)
            {
                page.Row(top + y, expected);
                if (width % 8 != 0) expected[^1] &= (byte)(0xFF << (8 - width % 8));   // the bits beyond the width belong to no pixel
                // the writer stores 1 = black; the decoder gives 1 = black as well (blackIs1)
                Assert.Equal(expected, decoded.AsSpan(y * rowBytes, rowBytes).ToArray());
            }
            top += rows;
        }
        Assert.Equal(height, top);
    }

    /// <summary>
    /// TIFF 6.0, section 16: a separated image — PhotometricInterpretation 5, InkSet 1 (CMYK, the components in the order cyan, magenta,
    /// yellow, black), SamplesPerPixel 4, with an alpha channel 5 (NumberOfInks stays at its default 4, equal to the inks without the extra
    /// sample), BitsPerSample one value per component, 8 or 16; no DotRange, so 0 is 0 % ink and the maximum 100 % (its default); the values
    /// are written as given (0 = no ink).
    /// </summary>
    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public void Separated_FieldsOfSection16(int depth, bool alpha)
    {
        var channels = alpha ? 5 : 4;
        var rowBytes = 6 * channels * depth / 8;
        var data = new byte[rowBytes * 3];
        new Random(16).NextBytes(data);
        using var output = new MemoryStream();
        TiffEncoder.EncodeCmyk(6, 3, depth, alpha, (y, row) => data.AsSpan(y * rowBytes, rowBytes).CopyTo(row), null, output);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        uint One(int tag) => Assert.Single(Values(file, fields.Single(f => f.Tag == tag)));
        Assert.Equal((5u, 1u, (uint)channels), (One(262), One(332), One(277)));
        Assert.Equal(Enumerable.Repeat((uint)depth, channels), Values(file, fields.Single(f => f.Tag == 258)));
        Assert.DoesNotContain(fields, f => f.Tag is 334 or 336 or 333);   // NumberOfInks, DotRange and InkNames at their defaults / not needed
        Assert.Equal(alpha, fields.Any(f => f.Tag == 338));
    }

    /// <summary>
    /// TIFF 6.0, section 12, PageNumber with section 8, NewSubfileType: a page saved as a file of its own still names its place in the document
    /// — page 5 of 10 is (4, 10), and bit 1 is set, since it is a page of a multi-page document (FG 1019: before (0, 1) in every such file).
    /// </summary>
    [Fact]
    public void PageNumber_APageOfSeveralInAFileOfItsOwn()
    {
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => Page(TiffPageKind.Gray, 20, 10, 9), output, firstPage: 4, documentPages: 10);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        Assert.Equal([4u, 10u], Values(file, fields.Single(f => f.Tag == 297)));
        Assert.Equal(2u, Assert.Single(Values(file, fields.Single(f => f.Tag == 254))));
    }

    /// <summary>
    /// TIFF 6.0, sections 7 and 8 (decided by the user on 22.11.2026): strips of about 8 KB of raw data, and a single row per strip when a row
    /// is larger than 8 KB (section 7, a should). Grey 3 000 pixels wide: two rows (6 000 bytes) per strip; RGB 3 000 wide (9 000 bytes a
    /// row): one; grey 500 wide: 16 rows (8 000 bytes). Before up to 32 KB (FG 1019).
    /// </summary>
    [Theory]
    [InlineData(TiffPageKind.Gray, 3000, 2)]
    [InlineData(TiffPageKind.Rgb, 3000, 1)]
    [InlineData(TiffPageKind.Gray, 500, 16)]
    public void Strips_AboutEightKilobytes_OneRowWhenLarger(TiffPageKind kind, int width, int rows)
    {
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => Page(kind, width, 40, 10), output);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        Assert.Equal((uint)rows, Assert.Single(Values(file, fields.Single(f => f.Tag == 278))));
    }

    /// <summary>
    /// TIFF 6.0, section 17 (Satz 40, a must) with section 20 (decided by the user on 22.11.2026): the values of greyscale and RGB pages follow the
    /// sRGB curve, not linear light, so the pages carry a TransferFunction — SHORT, 2^8 values, one table for all channels: for each value its
    /// linear intensity 0 … 65535 after IEC 61966-2-1 (0 → 0, 255 → 65535, 128 → 14 146, rising); black and white pages none. Before only the
    /// ICC profile said so, which a reader of TIFF 6.0 does not know (FG 1019).
    /// </summary>
    [Theory]
    [InlineData(TiffPageKind.Gray, true)]
    [InlineData(TiffPageKind.Rgb, true)]
    [InlineData(TiffPageKind.BlackWhite, false)]
    public void TransferFunction_TheSrgbCurve(TiffPageKind kind, bool present)
    {
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => Page(kind, 16, 4, 11), output);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        Assert.Equal(present, fields.Any(f => f.Tag == 301));
        if (!present) return;
        var f = fields.Single(x => x.Tag == 301);
        Assert.Equal((3, 256u), (f.Type, f.Count));
        var table = Values(file, f);
        for (var v = 0; v < 256; v++)
        {
            var c = v / 255.0;
            var linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            Assert.InRange((double)table[v], linear * 65535 - 0.51, linear * 65535 + 0.51);
        }
        Assert.Equal((0u, 14146u, 65535u), (table[0], table[128], table[255]));
        Assert.True(table.Zip(table.Skip(1)).All(p => p.First < p.Second));
    }

    /// <summary>
    /// TIFF 6.0, section 8, ResolutionUnit (decided by the user on 22.11.2026): a CMYK image has no resolution of its own — it gets the one of its
    /// size as drawn on its page (150 × 300 dpi given), and without a known size the longest side measures four inches (40 × 30 pixels: 10 dpi,
    /// as section 8 advises for an arbitrary resolution). Before always 72 (FG 1019).
    /// </summary>
    [Theory]
    [InlineData(150.0, 300.0, 150.0, 300.0)]
    [InlineData(0.0, 0.0, 10.0, 10.0)]
    public void Separated_ResolutionFromTheDrawnSizeOrFourInches(double givenX, double givenY, double expectedX, double expectedY)
    {
        var data = new byte[40 * 30 * 4];
        using var output = new MemoryStream();
        TiffEncoder.EncodeCmyk(40, 30, 8, false, (y, row) => data.AsSpan(y * 160, 160).CopyTo(row), null, output,
            givenX > 0 ? (givenX, givenY) : null);
        var file = output.ToArray();
        var fields = Assert.Single(Walk(file, []));
        double Rational(int tag)
        {
            var f = fields.Single(x => x.Tag == tag);
            return BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(f.ValueAt)) / (double)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(f.ValueAt + 4));
        }
        Assert.Equal((expectedX, expectedY), (Rational(282), Rational(283)));
        Assert.Equal(2u, Assert.Single(Values(file, fields.Single(f => f.Tag == 296))));
    }

    /// <summary>
    /// TIFF 6.0, section 8, SubfileType (sentences 267, 270): replaced by NewSubfileType, which is to be used instead. Pages of every kind in one
    /// file (each with its own directory), a single page and a CMYK image with alpha: no directory has SubfileType (255), every directory of
    /// the page files has NewSubfileType (254).
    /// </summary>
    [Fact]
    public void SubfileType_NeverWritten_NewSubfileTypeInstead()
    {
        var files = new List<byte[]>();
        foreach (var count in new[] { 3, 1 })
        {
            using var pages = new MemoryStream();
            TiffEncoder.EncodePages(count, i => Page((TiffPageKind)(i % 3), 20, 10, i), pages);
            files.Add(pages.ToArray());
        }
        using var cmyk = new MemoryStream();
        TiffEncoder.EncodeCmyk(6, 3, 8, true, (_, row) => row.Fill(0x40), null, cmyk);
        files.Add(cmyk.ToArray());
        foreach (var file in files)
        {
            var directories = Walk(file, []);
            Assert.NotEmpty(directories);
            Assert.All(directories, d => Assert.DoesNotContain(d, f => f.Tag == 255));
        }
        Assert.All(files.Take(2).SelectMany(f => Walk(f, [])), d => Assert.Contains(d, f => f.Tag == 254));
    }
}
