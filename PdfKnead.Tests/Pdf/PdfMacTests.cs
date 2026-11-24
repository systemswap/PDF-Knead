using System.Security.Cryptography;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The integrity protection of encrypted files (ISO/TS 32004): the building blocks against published vectors, the token against an
/// independent computation, and the check of whole files — valid, changed, required but missing (docs/ABLAUFPLAN.md, 5.5;
/// docs/FEHLERGEDAECHTNIS.md, No. 556).
/// </summary>
public sealed class PdfMacTests
{
    /// <summary>RFC 3394, 4.6: a 256-bit key wrapped with a 256-bit key encryption key — and back; another key gives nothing.</summary>
    [Fact]
    public void KeyWrap_Rfc3394Vector()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F");
        var wrapped = Convert.FromHexString("28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21");
        Assert.Equal(wrapped, PdfMac.Wrap(kek, key));
        Assert.Equal(key, PdfMac.Unwrap(kek, wrapped));
        Assert.Null(PdfMac.Unwrap(new byte[32], wrapped));
    }

    // Computed independently (scratch script outside the repository, 6.11.2026): HKDF and AES key wrap with OpenSSL 3.5, HMAC and SHA-256
    // from the Python standard library, the DER written by hand. Inputs: file key 00..1F, /KDFSalt 20..3F, MAC key 40..5F, data digest
    // SHA-256 of „Max Mustermann“.
    private const string Kek = "40fc9ec3471341d3350f68e53c17cfc92b5d2d6573ed37c48b0a7caa6a42b2fd";
    private const string Expected =
        "30820144060b2a864886f70d0109100102a08201333082012f0201003146a344020100a00806062881fa040101300b060960864801650304012d042830f5a0ea25"
        + "a895415568b105838ad7f730d3df3f4386d2c699a4ec154241ad72c883b8536243af42300a06082a864886f70d0209a10b0609608648016503040201303306062881"
        + "fa040100a029042730250201000420dddfab9b5b8a360150547065daff114ff218b39c8b0986b761075977aeeca3c3a272301506092a864886f70d010903310806"
        + "062881fa040100302806092a864886f70d010934311b3019300b0609608648016503040201a20a06082a864886f70d0209302f06092a864886f70d01090431220420"
        + "976ed4fe822b685aaac0a078e87e119396632ad692298dee5d68a318208bad440420f7d8896d21debe656f225748e6ff5d5300f18058bef29e42ce76340d00ab4f93";

    private static byte[] Range(int from, int to) => [.. Enumerable.Range(from, to - from).Select(i => (byte)i)];

    /// <summary>pdfMacWrapKdf (6.4): salt and info in their places — the same key as HKDF of OpenSSL.</summary>
    [Fact]
    public void Kek_MatchesTheIndependentComputation() => Assert.Equal(Convert.FromHexString(Kek), PdfMac.Kek(Range(0, 32), Range(32, 64)));

    /// <summary>The whole token, byte for byte, as computed independently: structure, order, tags, wrap, HMAC — and the length of every token.</summary>
    [Fact]
    public void Token_KnownAnswer_MatchesTheIndependentComputation()
    {
        var token = PdfMac.Token(PdfMac.Kek(Range(0, 32), Range(32, 64)), Range(64, 96), SHA256.HashData("Max Mustermann"u8));
        Assert.Equal(Expected, Convert.ToHexStringLower(token));
        Assert.Equal(token.Length, PdfMac.TokenLength);
        for (var i = 0; i < 3; i++)
            Assert.Equal(PdfMac.TokenLength, PdfMac.Token(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)).Length);
    }

    private static byte[] MacFile(string user = "geheim", TestMac mac = TestMac.Real, bool required = true, Action<PdfDictionary>? patchTrailer = null) =>
        TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: user, owner: "chef", permissions: PdfPermissions.Print, version: "2.0",
            macRequired: required, mac: mac, patchTrailer: patchTrailer);

    private static MacCheck Check(byte[] file, string password = "geheim")
    {
        using var doc = PdfDocument.Open(file, password);
        return doc.Integrity;
    }

    [Fact]
    public void Mac_Valid_IsConfirmed()
    {
        using var doc = PdfDocument.Open(MacFile(), "geheim");
        Assert.Equal(MacState.Valid, doc.Integrity.State);
        Assert.False(doc.Integrity.EmptyUserPassword);
        Assert.Empty(doc.Problems);
    }

    /// <summary>One byte of a content stream changed, the length the same: violated, and said when the file is opened.</summary>
    [Fact]
    public void Mac_ByteChangedAfterwards_IsReportedViolated()
    {
        var file = MacFile();
        var text = System.Text.Encoding.Latin1.GetString(file);
        file[text.IndexOf("stream\n", text.IndexOf("\n4 0 obj", StringComparison.Ordinal), StringComparison.Ordinal) + 20] ^= 0x01;
        using var doc = PdfDocument.Open(file, "geheim");
        Assert.Equal(MacState.Invalid, doc.Integrity.State);
        Assert.Contains(doc.Problems, p => p.Contains("verletzt", StringComparison.Ordinal));
    }

    /// <summary>Bit 13 cleared in /P and /Perms, but no /AuthCode: not conforming, said — not „manipulated“. Before: no note.</summary>
    [Fact]
    public void Mac_RequiredButMissing_IsReported()
    {
        using var doc = PdfDocument.Open(MacFile(mac: TestMac.None), "geheim");
        Assert.Equal(MacState.MissingButRequired, doc.Integrity.State);
        Assert.Contains(doc.Problems, p => p.Contains("verlangt einen Integritätsschutz", StringComparison.Ordinal));
    }

    [Fact]
    public void Mac_NotRequired_NoNote()
    {
        using var doc = PdfDocument.Open(MacFile(mac: TestMac.None, required: false), "geheim");
        Assert.Equal(MacState.None, doc.Integrity.State);
        Assert.Empty(doc.Problems);
    }

    /// <summary>A MAC that is not required is still checked (and valid).</summary>
    [Fact]
    public void Mac_PresentWithoutRequirement_IsChecked() => Assert.Equal(MacState.Valid, Check(MacFile(required: false)).State);

    /// <summary>Without an open password anyone can derive the key: the check says the MAC protects nothing (6.4, note 2).</summary>
    [Fact]
    public void Mac_EmptyUserPassword_Notes()
    {
        var check = Check(MacFile(user: ""), "");
        Assert.Equal(MacState.Valid, check.State);
        Assert.True(check.EmptyUserPassword);
    }

    [Fact]
    public void Mac_MissingMacLocation_Invalid() =>
        Assert.Equal(MacState.Invalid, Check(MacFile(patchTrailer: t => t.GetDictionary(PdfName.Get("AuthCode"))!.Remove(PdfName.Get("MACLocation")))).State);

    /// <summary>Another key derivation than pdfMacWrapKdf: not checkable — never wrongly „violated“.</summary>
    [Fact]
    public void Mac_UnknownKdf_NotCheckable()
    {
        var file = MacFile();
        var text = System.Text.Encoding.Latin1.GetString(file);
        var at = text.IndexOf("06062881fa040101", StringComparison.Ordinal);
        Assert.True(at > 0);
        System.Text.Encoding.ASCII.GetBytes("06062881fa040105").CopyTo(file, at);
        Assert.Equal(MacState.NotCheckable, Check(file).State);
    }

    /// <summary>The gap must hold exactly the hexadecimal token: a space in it (allowed in ordinary hex strings) makes it invalid (Table 6).</summary>
    [Fact]
    public void Mac_GapNotStrict_Invalid()
    {
        var file = MacFile();
        var text = System.Text.Encoding.Latin1.GetString(file);
        var gap = text.IndexOf('<', text.LastIndexOf("/MAC", StringComparison.Ordinal)) + 1;
        file[gap] = (byte)' ';
        Assert.Equal(MacState.Invalid, Check(file).State);
    }

    // ---- 5.5 b: a fresh MAC when the encryption is kept (behind PdfSaveOptions.WriteMac until the outside check; No. 557)

    private static (byte[] Bytes, Core.Pdf.Writing.PdfSaveResult Result) Saved(byte[] file, Core.Pdf.Writing.PdfSaveOptions options, string password = "geheim")
    {
        using var doc = PdfDocument.Open(file, password);
        using var output = new MemoryStream();
        var result = Core.Pdf.Writing.PdfWriter.Write(doc, Core.Pdf.Editing.PageItem.From(doc), output, options);
        return (output.ToArray(), result);
    }

    private static readonly DateTimeOffset At = new(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(1));

    private static bool DeclaresMac(PdfDocument doc) =>
        doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))?.GetArray(PdfName.Get("ISO_")) is { } iso
        && Enumerable.Range(0, iso.Count).Any(i => (iso.Get(i) as PdfDictionary)?.GetInteger(PdfName.Get("ExtensionLevel")) == 32004);

    /// <summary>
    /// Saved with the encryption kept, as 2.0, both cross-reference forms: the copy has a fresh standalone MAC from 0 to the end of the file,
    /// still asks for one (bit 13 cleared, /KDFSalt), declares ISO/TS 32004, and nothing was dropped. Before (5.2): no MAC, requirement dropped.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MacFile_Saved_HasAFreshMacOverTheWholeFile(bool objectStreams)
    {
        var (bytes, result) = Saved(MacFile(), new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0", WriteMac = true, ObjectStreams = objectStreams });
        Assert.False(result.IntegrityProtectionDropped);
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(MacState.Valid, copy.Integrity.State);
        Assert.Empty(copy.Problems);
        Assert.Equal(0, copy.Security!.RawPermissions & 0x1000);
        Assert.Equal(32, copy.EncryptDictionary!.GetString(PdfName.Get("KDFSalt"))!.Bytes.Length);
        Assert.True(DeclaresMac(copy));
        var range = copy.NewestTrailer!.GetDictionary(PdfName.Get("AuthCode"))!.GetArray(PdfName.Get("ByteRange"))!;
        Assert.Equal(bytes.Length, (long)(range.GetNumber(2)! + range.GetNumber(3)!));
        Assert.Contains("Seite 2", string.Concat(Core.Pdf.Content.PageInterpreter.Build(copy, copy.Pages[1]).Text.Select(t => t.Text)));
    }

    /// <summary>One byte of the saved file changed afterwards: its MAC fails.</summary>
    [Fact]
    public void MacFile_Saved_OneByteChanged_MacFails()
    {
        var (bytes, _) = Saved(MacFile(), new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0", WriteMac = true, ObjectStreams = false });
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        bytes[text.IndexOf("stream", StringComparison.Ordinal) + 30] ^= 0x01;
        Assert.Equal(MacState.Invalid, Check(bytes).State);
    }

    /// <summary>As 1.7 no MAC can be written (its declaration is based on 2.0): the immediate solution of 5.2 — requirement dropped, said.</summary>
    [Fact]
    public void MacFile_SavedAsPdf17_FallsBackToTheImmediateSolution()
    {
        var (bytes, result) = Saved(MacFile(), new Core.Pdf.Writing.PdfSaveOptions { Version = "1.7", WriteMac = true });
        Assert.True(result.IntegrityProtectionDropped);
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(MacState.None, copy.Integrity.State);
        Assert.NotEqual(0, copy.Security!.RawPermissions & 0x1000);
    }

    /// <summary>
    /// AES-256-GCM (revision 7) exists only in PDF 2.0: asked for as 1.7, the copy is written as 2.0 all the same — and then it gets its fresh
    /// MAC like any 2.0 copy. The MAC was decided by the version asked for and dropped (docs/FEHLERGEDAECHTNIS.md, No. 878).
    /// </summary>
    [Fact]
    public void GcmMacFile_AskedFor17_WrittenAs20_KeepsAFreshMac()
    {
        var file = TestPdf.Simple().Build(cipher: PdfCipher.Aes256Gcm, user: "geheim", owner: "chef", permissions: PdfPermissions.Print, version: "2.0",
            macRequired: true, mac: TestMac.Real);
        Assert.Equal(MacState.Valid, Check(file).State);
        var (bytes, result) = Saved(file, new Core.Pdf.Writing.PdfSaveOptions { Version = "1.7", WriteMac = true });
        Assert.Equal("2.0", result.Version);
        Assert.False(result.IntegrityProtectionDropped);
        Assert.Equal(MacState.Valid, Check(bytes).State);
    }

    /// <summary>Every token has the same length: three saves with the same time are equally long (the trial run of compressing relies on it).</summary>
    [Fact]
    public void MacFile_Saved_LengthIndependentOfRandom()
    {
        var file = MacFile();
        var lengths = Enumerable.Range(0, 3).Select(_ => Saved(file, new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0", WriteMac = true, ModificationTime = At }).Bytes.Length);
        Assert.Single(lengths.Distinct());
    }

    /// <summary>With AES-GCM (revision 7, 5.4) the MAC is written as well.</summary>
    [Fact]
    public void MacFile_Gcm_R7_MacWritten()
    {
        var gcm = TestPdf.Simple().Build(cipher: PdfCipher.Aes256Gcm, user: "geheim", owner: "chef", version: "2.0", macRequired: true, mac: TestMac.Real);
        Assert.Equal(MacState.Valid, Check(gcm).State);
        var (bytes, _) = Saved(gcm, new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0", WriteMac = true });
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(PdfCipher.Aes256Gcm, copy.Security!.StreamCipher);
        Assert.Equal(MacState.Valid, copy.Integrity.State);
    }

    /// <summary>For the outside check (only with PDFKNEAD_ABLAGE): a source with a MAC and its saved copy, password „geheim“.</summary>
    [Fact]
    public void MacFiles_ForTheOutsideCheck()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var source = MacFile();
        File.WriteAllBytes(Path.Combine(folder, "mac-quelle.pdf"), source);
        File.WriteAllBytes(Path.Combine(folder, "mac-gespeichert.pdf"), Saved(source, new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0", WriteMac = true }).Bytes);
        // without an open password: a start file for the fuzzer, which opens files without one
        File.WriteAllBytes(Path.Combine(folder, "mac-ohne-passwort.pdf"), MacFile(user: ""));
    }
    /// <summary>
    /// Only the attachments encrypted (AES-256, /EFF with EFOpen), opened without their password: the file key is not there, so the MAC is
    /// „not checkable“ — before, an empty key was derived and the MAC reported as violated. A file asking for a MAC (bit 13 cleared) and
    /// carrying none is reported as such — before, the locked handler had bit 13 set (docs/FEHLERGEDAECHTNIS.md, No. 1099).
    /// </summary>
    [Theory]
    [InlineData(TestMac.Real, MacState.NotCheckable)]
    [InlineData(TestMac.None, MacState.MissingButRequired)]
    public void Mac_AttachmentsLocked_NotCheckableOrMissing(TestMac mac, MacState expected)
    {
        var file = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0", macRequired: true, mac: mac,
            attachmentsOnly: true, patchEncrypt: e => e.Set(PdfName.Get("CF"), TestPdf.Dict(("StdCF", TestPdf.Dict(("AuthEvent", TestPdf.N("EFOpen")),
                ("CFM", TestPdf.N("AESV3")), ("Length", PdfInteger.Of(32)))))));
        using var doc = PdfDocument.Open(file);
        Assert.True(doc.Security!.AttachmentsLocked);
        Assert.Equal(expected, doc.Integrity.State);
    }
}
