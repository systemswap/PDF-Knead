using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800 Table A.45, I.5.2 and I.5.3.1: the profile of the codestream PdfAJpeg2000.Wrap names in the compatibility list („J2P0“, „J2P1“)
/// and in the Reader Requirements box, checked against the restrictions of the table rather than taken from Rsiz, and UnkC 1 in the image
/// header, as the wrapper guesses the colour space from the number of components (docs/FEHLERGEDAECHTNIS.md, No. 1074).
/// </summary>
public sealed partial class PdfARepairTests
{
    /// <summary>The test codestream of <see cref="JpxRobustTests.Codestream"/> (Xsiz <paramref name="width"/>, Ysiz 1, one tile-part) with Rsiz.</summary>
    private static byte[] ProfileBase(int rsiz, uint width = 64, uint tile = 64, int components = 3, byte factor = 1, byte levels = 0)
    {
        var cs = JpxRobustTests.Codestream(0, width, tile, components, factor, levels, 1, 0);
        BinaryPrimitives.WriteUInt16BigEndian(cs.AsSpan(6), (ushort)rsiz);
        return cs;
    }

    /// <summary>Where COD begins (right behind SIZ) and where the first SOT begins (behind COD and QCD of the test codestream).</summary>
    private static (int Cod, int Sot) ProfilePlaces(byte[] cs)
    {
        var cod = 4 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(4));
        var qcd = cod + 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(cod + 2));
        return (cod, qcd + 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(qcd + 2)));
    }

    /// <summary>The codestream with COD replaced: Scod, levels, code-block exponents less 2, style, 5-3, the precinct bytes where given.</summary>
    private static byte[] WithCod(byte[] cs, byte levels, byte xcb = 4, byte ycb = 4, byte style = 0, params byte[] precincts)
    {
        var (cod, _) = ProfilePlaces(cs);
        var end = cod + 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(cod + 2));
        byte[] segment = [0xFF, 0x52, 0, (byte)(12 + precincts.Length), (byte)(precincts.Length > 0 ? 1 : 0), 0, 0, 1, 0, levels, xcb, ycb, style, 1, .. precincts];
        return [.. cs[..cod], .. segment, .. cs[end..]];
    }

    /// <summary>The codestream with <paramref name="segment"/> at the end of the main header.</summary>
    private static byte[] InMainHeader(byte[] cs, byte[] segment)
    {
        var (_, sot) = ProfilePlaces(cs);
        return [.. cs[..sot], .. segment, .. cs[sot..]];
    }

    /// <summary>The codestream with <paramref name="segment"/> in the header of its tile-part (Psot grows with it).</summary>
    private static byte[] InTilePartHeader(byte[] cs, byte[] segment)
    {
        var (_, sot) = ProfilePlaces(cs);
        var result = (byte[])[.. cs[..(sot + 12)], .. segment, .. cs[(sot + 12)..]];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(sot + 6)) + (uint)segment.Length);
        return result;
    }

    /// <summary>The codestream with its one tile-part repeated as the tile-parts <paramref name="parts"/> (Isot, TPsot; TNsot 0).</summary>
    private static byte[] WithTileParts(byte[] cs, params (int Tile, int Part)[] parts)
    {
        var (_, sot) = ProfilePlaces(cs);
        var chunk = cs[sot..^2];
        var result = new List<byte>(cs[..sot]);
        foreach (var (tile, part) in parts)
        {
            var copy = (byte[])chunk.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(4), (ushort)tile);
            (copy[10], copy[11]) = ((byte)part, 0);
            result.AddRange(copy);
        }
        result.AddRange([0xFF, 0xD9]);
        return [.. result];
    }

    /// <summary>
    /// T.800 Table A.45, restriction by restriction: the profile is the one Rsiz names (1: profile 0, 2: profile 1) only where the codestream
    /// keeps every restriction of the table for it — image and tile origin, tiles of 128 or one tile (profile 0), tiles of at most 1024 and
    /// square or one tile (profile 1), subsampling 1, 2 or 4 (profile 0), SPrgn at most 37, code-blocks of 32 × 32 or 64 × 64 with only the
    /// styles t, p, s (profile 0), at most 64 × 64 (profile 1), no PPM or PPT, COD, COC, QCD, QCC in the main header only and RSpoc, CSpoc 0 in
    /// the first progression of a POC (profile 0), the first tile-part of every tile before all others and in the order of the tiles (profile 0),
    /// the lowest resolution of at most 128 × 128 (both: for one tile (Xsiz − XOsiz) / 2^levels, for profile 1 per tile ⌊tx1/D⌋ − ⌊tx0/D⌋),
    /// one precinct in every resolution of at most 128 × 128 (profile 0). Rsiz 0 names no profile.
    /// </summary>
    [Theory]
    [InlineData("plain", 1, 0)]
    [InlineData("plain", 2, 1)]
    [InlineData("plain", 0, null)]
    [InlineData("plain", 3, null)]
    [InlineData("origin 1", 1, null)]
    [InlineData("origin 1", 2, 1)]
    [InlineData("tiles 128 of 256", 1, 0)]
    [InlineData("tiles 128 of 256", 2, 1)]
    [InlineData("tiles 64 of 256", 1, null)]
    [InlineData("tiles 64 of 256", 2, 1)]
    [InlineData("tiles 1024 of 4096", 2, 1)]
    [InlineData("tiles 2048 of 4096", 2, null)]
    [InlineData("tiles 128 x 64 of 256", 1, null)]
    [InlineData("tiles 128 x 64 of 256", 2, null)]
    [InlineData("size 2^31 - 1", 2, 1)]
    [InlineData("size 2^31", 2, null)]
    [InlineData("subsampling 2", 1, 0)]
    [InlineData("subsampling 3", 1, null)]
    [InlineData("subsampling 3", 2, 1)]
    [InlineData("blocks 32 x 32", 1, 0)]
    [InlineData("blocks 32 x 64", 1, null)]
    [InlineData("blocks 32 x 64", 2, 1)]
    [InlineData("blocks 16 x 16", 1, null)]
    [InlineData("blocks 16 x 16", 2, 1)]
    [InlineData("blocks 128 x 32", 2, null)]
    [InlineData("style t p s", 1, 0)]
    [InlineData("style bypass", 1, null)]
    [InlineData("style bypass", 2, 1)]
    [InlineData("style vertically causal", 1, null)]
    [InlineData("RGN 37", 1, 0)]
    [InlineData("RGN 38", 1, null)]
    [InlineData("RGN 38", 2, null)]
    [InlineData("RGN 38 in the tile-part", 2, null)]
    [InlineData("PPM", 1, null)]
    [InlineData("PPM", 2, 1)]
    [InlineData("PPT", 1, null)]
    [InlineData("PPT", 2, 1)]
    [InlineData("COD in the tile-part", 1, null)]
    [InlineData("COD in the tile-part", 2, 1)]
    [InlineData("QCD in the tile-part", 1, null)]
    [InlineData("POC first 0 0", 1, 0)]
    [InlineData("POC first resolution 1", 1, null)]
    [InlineData("POC first component 1", 1, null)]
    [InlineData("POC first resolution 1", 2, 1)]
    [InlineData("tile-parts 0 1", 1, 0)]
    [InlineData("tile-parts 1 0", 1, null)]
    [InlineData("tile-parts 1 0", 2, 1)]
    [InlineData("tile-parts 0 0b 1", 1, null)]
    [InlineData("tile-parts 0 1 0b", 1, 0)]
    [InlineData("tile-part missing", 1, null)]
    [InlineData("width 256 levels 0", 1, null)]
    [InlineData("width 256 levels 0", 2, null)]
    [InlineData("width 256 levels 1", 1, 0)]
    [InlineData("width 256 levels 1", 2, 1)]
    [InlineData("width 258 levels 1", 1, null)]
    [InlineData("width 258 levels 1", 2, null)]
    [InlineData("width 256 levels 0 in component 4", 1, 0)]
    [InlineData("width 256 levels 0 in component 3", 1, null)]
    [InlineData("width 1024 levels 3 in the tile-part", 2, 1)]
    [InlineData("width 1024 levels 2 in the tile-part", 2, null)]
    [InlineData("precincts 128", 1, 0)]
    [InlineData("precincts 64", 1, null)]
    [InlineData("precincts 64", 2, 1)]
    [InlineData("no EOC", 1, null)]
    public void Profile_OnlyWhereEveryRestrictionOfTableA45Holds(string change, int rsiz, int? expected)
    {
        byte[] Rgn(int shift) => [0xFF, 0x5E, 0, 5, 0, 0, (byte)shift];
        byte[] Poc(int resolution, int component) => [0xFF, 0x5F, 0, 9, (byte)resolution, (byte)component, 0, 1, 1, 3, 0];
        static byte[] Patched(byte[] cs, int at, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(at), value);
            return cs;
        }
        var cs = change switch
        {
            "origin 1" => JpxRobustTests.Codestream(1, 64, 64, 3, 1, 0, 1, 0),
            "tiles 128 of 256" or "tile-parts 0 1" => WithTileParts(ProfileBase(rsiz, 256, 128, levels: 1), (0, 0), (1, 0)),
            "tiles 64 of 256" => WithTileParts(ProfileBase(rsiz, 256, 64), (0, 0), (1, 0), (2, 0), (3, 0)),
            "tiles 1024 of 4096" => WithTileParts(ProfileBase(rsiz, 4096, 1024, levels: 3), (0, 0), (1, 0), (2, 0), (3, 0)),
            "tiles 2048 of 4096" => WithTileParts(ProfileBase(rsiz, 4096, 2048, levels: 5), (0, 0), (1, 0)),
            "tiles 128 x 64 of 256" => WithTileParts(Patched(ProfileBase(rsiz, 256, 128, levels: 1), 28, 64), (0, 0), (1, 0)),
            "size 2^31 - 1" => JpxRobustTests.Codestream(int.MaxValue - 64, int.MaxValue, int.MaxValue, 3, 1, 0, 1, 0),
            "size 2^31" => JpxRobustTests.Codestream((1u << 31) - 64, 1u << 31, 1u << 31, 3, 1, 0, 1, 0),
            "subsampling 2" => ProfileBase(rsiz, factor: 2),
            "subsampling 3" => ProfileBase(rsiz, factor: 3),
            "blocks 32 x 32" => WithCod(ProfileBase(rsiz), 0, 3, 3),
            "blocks 32 x 64" => WithCod(ProfileBase(rsiz), 0, 3, 4),
            "blocks 16 x 16" => WithCod(ProfileBase(rsiz), 0, 2, 2),
            "blocks 128 x 32" => WithCod(ProfileBase(rsiz), 0, 5, 3),
            "style t p s" => WithCod(ProfileBase(rsiz), 0, style: 0x34),
            "style bypass" => WithCod(ProfileBase(rsiz), 0, style: 0x01),
            "style vertically causal" => WithCod(ProfileBase(rsiz), 0, style: 0x08),
            "RGN 37" => InMainHeader(ProfileBase(rsiz), Rgn(37)),
            "RGN 38" => InMainHeader(ProfileBase(rsiz), Rgn(38)),
            "RGN 38 in the tile-part" => InTilePartHeader(ProfileBase(rsiz), Rgn(38)),
            "PPM" => InMainHeader(ProfileBase(rsiz), [0xFF, 0x60, 0, 3, 0]),
            "PPT" => InTilePartHeader(ProfileBase(rsiz), [0xFF, 0x61, 0, 3, 0]),
            "COD in the tile-part" => InTilePartHeader(ProfileBase(rsiz), [0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, 0, 4, 4, 0, 1]),
            "QCD in the tile-part" => InTilePartHeader(ProfileBase(rsiz), [0xFF, 0x5C, 0, 4, 0x40, 0x48]),
            "POC first 0 0" => InMainHeader(ProfileBase(rsiz), Poc(0, 0)),
            "POC first resolution 1" => InMainHeader(ProfileBase(rsiz), Poc(1, 0)),
            "POC first component 1" => InMainHeader(ProfileBase(rsiz), Poc(0, 1)),
            "tile-parts 1 0" => WithTileParts(ProfileBase(rsiz, 256, 128, levels: 1), (1, 0), (0, 0)),
            "tile-parts 0 0b 1" => WithTileParts(ProfileBase(rsiz, 256, 128, levels: 1), (0, 0), (0, 1), (1, 0)),
            "tile-parts 0 1 0b" => WithTileParts(ProfileBase(rsiz, 256, 128, levels: 1), (0, 0), (1, 0), (0, 1)),
            "tile-part missing" => ProfileBase(rsiz, 256, 128, levels: 1),
            "width 256 levels 0" => ProfileBase(rsiz, 256, 256),
            "width 256 levels 1" => ProfileBase(rsiz, 256, 256, levels: 1),
            "width 258 levels 1" => ProfileBase(rsiz, 258, 258, levels: 1),
            // five components with one level, component 4 (not counted) or 3 (counted) without
            "width 256 levels 0 in component 4" => InMainHeader(ProfileBase(rsiz, 256, 256, 5, levels: 1), [0xFF, 0x53, 0, 9, 4, 0, 0, 4, 4, 0, 1]),
            "width 256 levels 0 in component 3" => InMainHeader(ProfileBase(rsiz, 256, 256, 5, levels: 1), [0xFF, 0x53, 0, 9, 3, 0, 0, 4, 4, 0, 1]),
            "width 1024 levels 3 in the tile-part" => InTilePartHeader(ProfileBase(rsiz, 1024, 1024), [0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, 3, 4, 4, 0, 1]),
            "width 1024 levels 2 in the tile-part" => InTilePartHeader(ProfileBase(rsiz, 1024, 1024, levels: 3), [0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, 2, 4, 4, 0, 1]),
            // one level, so resolution 1 is 128 wide: precincts of 2^7 hold it in one, precincts of 2^6 in two
            "precincts 128" => WithCod(ProfileBase(rsiz, 128, 128), 1, precincts: [0x77, 0x77]),
            "precincts 64" => WithCod(ProfileBase(rsiz, 128, 128), 1, precincts: [0x66, 0x66]),
            "no EOC" => ProfileBase(rsiz)[..^2],
            _ => ProfileBase(rsiz),
        };
        BinaryPrimitives.WriteUInt16BigEndian(cs.AsSpan(6), (ushort)rsiz);
        Assert.Equal(expected, PdfAJpeg2000.Profile(cs));
    }

    /// <summary>
    /// T.800 I.5.2 (sentences on „J2P0“ and „J2P1“) and I.5.3.1 (UnkC), byte for byte: the file type box of a JP2 file names a codestream that
    /// keeps profile 0 or 1 after „jp2 “ — 24 bytes instead of 20 —, one with Rsiz 1 or 2 that does not keep it (here an origin of 1, beyond
    /// profile 0; blocks of 128 × 32, beyond profile 1) and one with Rsiz 0 are not named; the image header has UnkC 1 in every case (the
    /// colour space is guessed from the number of components: the wrapper has no other). Before the list held „jp2 “ alone and UnkC was 0.
    /// </summary>
    [Theory]
    [InlineData(1, false, "J2P0")]
    [InlineData(2, false, "J2P1")]
    [InlineData(1, true, "")]
    [InlineData(2, true, "")]
    [InlineData(0, false, "")]
    public void Wrap_ProfileKept_J2PxInTheCompatibilityList_UnkC1(int rsiz, bool broken, string named)
    {
        var cs = broken && rsiz == 1 ? JpxRobustTests.Codestream(1, 65, 65, 3, 1, 0, 1, 0) : ProfileBase(rsiz);
        if (broken && rsiz == 2) cs = WithCod(cs, 0, 5, 3);
        BinaryPrimitives.WriteUInt16BigEndian(cs.AsSpan(6), (ushort)rsiz);
        var file = PdfAJpeg2000.Wrap(cs)!;
        byte[] ftyp = [0, 0, 0, (byte)(20 + named.Length), .. "ftypjp2 \0\0\0\0jp2 "u8, .. System.Text.Encoding.ASCII.GetBytes(named)];
        Assert.Equal(ftyp, file[12..(12 + ftyp.Length)]);
        Assert.Equal("jp2h", System.Text.Encoding.ASCII.GetString(file, 16 + ftyp.Length, 4));
        Assert.Equal((byte[])[0, 0, 0, 22, .. "ihdr"u8, 0, 0, 0, 1, 0, 0, 0, 64, 0, 3, 7, 7, 1, 0], file[(20 + ftyp.Length)..(42 + ftyp.Length)]);
        Assert.Null(PdfAJpeg2000.Fault(file));
        Assert.Equal(Core.Pdf.Filters.Jpx.JpxDecoder.Decode(cs, 16_000_000).Samples, Core.Pdf.Filters.Jpx.JpxDecoder.Decode(file, 16_000_000).Samples);
    }

    /// <summary>
    /// T.801 Table M.14 names the profile 1 feature (4) by Table A.45 of T.800, and a JPX file needs no feature for a profile 0 codestream:
    /// for CMYK the Reader Requirements box takes the profile the codestream keeps, not the one Rsiz names — Rsiz 2 kept: 4; Rsiz 2 or 1 not
    /// kept (blocks of 128 × 32, beyond both profiles): 5, unrestricted Part 1; Rsiz 1 kept: CMYK alone. The image header has UnkC 1, and a
    /// JPX file lists only „jpx “ („J2P0“, „J2P1“ are defined for JP2 files). Before Rsiz alone decided.
    /// </summary>
    [Theory]
    [InlineData(2, false, 4)]
    [InlineData(2, true, 5)]
    [InlineData(1, true, 5)]
    [InlineData(1, false, null)]
    public void Wrap_Cmyk_ProfileFeatureOnlyWhereKept(int rsiz, bool broken, int? feature)
    {
        var cs = ProfileBase(rsiz, components: 4);
        if (broken) cs = WithCod(cs, 0, 5, 3);
        var file = PdfAJpeg2000.Wrap(cs)!;
        Assert.Equal((byte[])[0, 0, 0, 20, .. "ftypjpx \0\0\0\u0001jpx "u8], file[12..32]);
        var rreq = ReaderRequirementsOf(file);
        var expected = new Dictionary<int, int> { [55] = 0x80 };
        if (feature is { } f) expected[f] = 0x40;
        Assert.Equal(expected, rreq.Features);
        Assert.Equal(feature is null ? 0x80 : 0xC0, rreq.FullyUnderstand);
        Assert.Equal(1, WrappedBoxes.Header(file, "ihdr")[12]);
    }

    /// <summary>
    /// The conformance codestreams of ITU-T T.803 (2002) for profile 0 and 1 keep the profile their Rsiz names — tiles of 128 and of the whole
    /// image, origins, subsampling by 2 and 4, explicit precincts, POC, several tile-parts, 257 components —, those for the full standard name
    /// none; set to Rsiz 1, the profile 1 codestreams with origins, tiles of 37 or 3, or subsampling by 4 × 1 are no profile 0 codestreams. Local
    /// only (the data are not in Git); skipped without them.
    /// </summary>
    [Jpeg2000ConformanceFact]
    public void Profile_ConformanceCodestreamsOfT803()
    {
        var folder = Jpeg2000ConformanceFactAttribute.Folder()!;
        var files = Directory.GetFiles(Path.Combine(folder, "codestreams_profile0"), "*.j2k")
            .Concat(Directory.GetFiles(Path.Combine(folder, "codestreams_profile1"), "*.j2k")).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(23, files.Length);
        var named = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var rsiz = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
            int? expected = rsiz is 1 or 2 ? rsiz - 1 : null;
            if (PdfAJpeg2000.Profile(data) != expected) named.Add($"{Path.GetFileName(file)}: Rsiz {rsiz}, Profil {PdfAJpeg2000.Profile(data)?.ToString() ?? "keins"}");
            if (rsiz == 2 && Path.GetFileName(file) is "p1_01.j2k" or "p1_05.j2k" or "p1_06.j2k" or "p1_07.j2k")
            {
                data[7] = 1;
                if (PdfAJpeg2000.Profile(data) is not null) named.Add($"{Path.GetFileName(file)} als Profil 0 angenommen");
            }
        }
        Assert.True(named.Count == 0, string.Join("\n", named));
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the conformance data of ITU-T T.803 (Normen-ISO/grundlagen/pruefdaten/jpeg2000-t803,
/// kept locally only, not in Git) cannot be found from the source file.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class Jpeg2000ConformanceFactAttribute : FactAttribute
{
    /// <summary>Marks the test as skipped if the folder is missing.</summary>
    /// <param name="source">Path of the test's source file (filled in by the compiler).</param>
    public Jpeg2000ConformanceFactAttribute([CallerFilePath] string source = "")
    {
        if (Folder(source) is null) Skip = "Normen-ISO/grundlagen/pruefdaten/jpeg2000-t803 is missing: the conformance data are kept locally only, not in the repository.";
    }

    /// <summary>The folder J2KP4files of the unpacked archive of T.803; null if it cannot be found.</summary>
    /// <param name="source">Path of the calling source file (filled in by the compiler).</param>
    public static string? Folder([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "PdfKnead.sln"))) continue;
            var folder = Path.Combine(dir.FullName, "Normen-ISO", "grundlagen", "pruefdaten", "jpeg2000-t803", "entpackt", "J2KP4files");
            return Directory.Exists(folder) ? folder : null;
        }
        return null;
    }
}
