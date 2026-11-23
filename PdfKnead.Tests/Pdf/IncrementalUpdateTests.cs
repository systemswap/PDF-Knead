using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Appending incremental updates (KONZEPT.md, section 5.17, step S1): the original bytes stay byte for byte, the appended objects
/// win on reading, the chain of sections stays readable, and a digital signature of the original stays intact.
/// </summary>
public sealed class IncrementalUpdateTests
{
    /// <summary>Appends an update; with PDFKNEAD_ABLAGE=&lt;folder&gt; the result is stored for a cross-check in other readers.</summary>
    private static byte[] Append(byte[] original, Action<PdfDocument, IncrementalUpdate> change,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var doc = PdfDocument.Open(original);
        var update = new IncrementalUpdate(doc);
        change(doc, update);
        using var output = new MemoryStream();
        var length = update.WriteTo(output);
        Assert.Equal(output.Length, length);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"angehaengt-{name}.pdf");
            for (var i = 2; File.Exists(file); i++) file = Path.Combine(folder, $"angehaengt-{name}-{i}.pdf");
            File.WriteAllBytes(file, output.ToArray());
        }
        return output.ToArray();
    }

    /// <summary>Replaces the /Title in the Info dictionary and adds an object referenced from it.</summary>
    private static void ChangeTitle(PdfDocument doc, IncrementalUpdate update, string title)
    {
        var infoRef = (PdfReference)doc.Trailer.GetRaw(PdfName.Get("Info"))!;
        var info = new PdfDictionary();
        foreach (var (key, value) in ((PdfDictionary)doc.Resolve(infoRef.Id)).Entries) info.Set(key, value);
        info.Set(PdfName.Get("Title"), new PdfString(PdfTextEncoding.Encode(title)));
        info.Set(PdfName.Get("Extra"), update.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Test"))), "Anhang"u8.ToArray())));
        update.Replace(infoRef.Id, info);
    }

    private static string? Title(PdfDocument doc) =>
        (doc.Trailer.GetDictionary(PdfName.Get("Info"))?.GetString(PdfName.Get("Title")))?.ToText();

    [Fact]
    public void ClassicTable_OriginalUnchanged_AppendedObjectsWin()
    {
        var original = TestPdf.Simple(pages: 2).Build();
        var updated = Append(original, (doc, update) => ChangeTitle(doc, update, "Neuer Titel"));

        Assert.Equal(original, updated.AsSpan(0, original.Length).ToArray());
        using var reopened = PdfDocument.Open(updated);
        Assert.False(reopened.WasRepaired);
        Assert.False(reopened.NewestXRefIsStream);
        Assert.Equal("Neuer Titel", Title(reopened));
        Assert.Equal(2, reopened.PageCount);
        var extra = (PdfStream)reopened.Trailer.GetDictionary(PdfName.Get("Info"))!.Get(PdfName.Get("Extra"));
        Assert.Equal("Anhang"u8.ToArray(), reopened.GetRawStreamData(extra));
        Assert.True(updated.Length > original.Length);
    }

    [Fact]
    public void XRefStream_UpdateUsesStreamToo()
    {
        using var source = PdfDocument.Open(TestPdf.Simple(pages: 2).Build());
        using var compact = new MemoryStream();
        PdfWriter.Write(source, compact, new PdfSaveOptions { Version = "1.7", ObjectStreams = true });
        var original = compact.ToArray();
        using (var check = PdfDocument.Open(original))
            Assert.True(check.NewestXRefIsStream);

        var updated = Append(original, (doc, update) => ChangeTitle(doc, update, "Mit Strom"));
        Assert.Equal(original, updated.AsSpan(0, original.Length).ToArray());
        using var reopened = PdfDocument.Open(updated);
        Assert.False(reopened.WasRepaired);
        Assert.True(reopened.NewestXRefIsStream);
        Assert.Equal("Mit Strom", Title(reopened));
        Assert.Equal(2, reopened.PageCount);
    }

    [Fact]
    public void TwoUpdates_ChainStaysReadable()
    {
        var first = Append(TestPdf.Simple(pages: 1).Build(), (doc, update) => ChangeTitle(doc, update, "Erste Fassung"));
        var second = Append(first, (doc, update) => ChangeTitle(doc, update, "Zweite Fassung"));
        Assert.Equal(first, second.AsSpan(0, first.Length).ToArray());
        using var reopened = PdfDocument.Open(second);
        Assert.False(reopened.WasRepaired);
        Assert.Equal("Zweite Fassung", Title(reopened));
        Assert.Equal(1, reopened.PageCount);
    }

    /// <summary>The point of it all: after appending, a signature over the original bytes still fits.</summary>
    [Fact]
    public void SignedOriginal_StaysIntactAfterAppend()
    {
        var original = SignedTestPdf.Build();
        var updated = Append(original, (doc, update) =>
        {
            var root = (PdfReference)doc.Trailer.GetRaw(PdfName.Root)!;
            var catalog = new PdfDictionary();
            foreach (var (key, value) in doc.Catalog!.Entries) catalog.Set(key, value);
            catalog.Set(PdfName.Get("Lang"), new PdfString("de-DE"u8.ToArray()));
            update.Replace(root.Id, catalog);
        });
        using var reopened = PdfDocument.Open(updated);
        Assert.Equal("de-DE", reopened.Catalog!.GetString(PdfName.Get("Lang"))!.ToText());
        var signature = Assert.Single(DigitalSignatures.Find(reopened));
        Assert.True(signature.Intact);
        Assert.True(signature.ByteRange[2] + signature.ByteRange[3] < updated.Length);   // signed part ends before the update
    }

    /// <summary>
    /// A trailer whose /Size is below the file's own object numbers (occurs in practice): new objects must come after the highest
    /// number, otherwise one of them would hide an existing object (a signature's placeholder went missing that way).
    /// </summary>
    /// <summary>The part after the original bytes, as text.</summary>
    private static string Added(byte[] original, byte[] updated) =>
        System.Text.Encoding.Latin1.GetString(updated, original.Length, updated.Length - original.Length);

    private static void OwnKey(PdfDictionary trailer) =>
        trailer.Set(PdfName.Get("PDFKneadTest"), new PdfString(PdfTextEncoding.Encode("Max Mustermann")));

    /// <summary>
    /// The trailer of an update carries every entry of the previous one on, except /Prev (ISO 32000-2, 7.5.6) — before only /Root, an indirect
    /// /Info and /ID, and a key of another program was gone after appending (docs/ABLAUFPLAN.md, 5.3 a; docs/FEHLERGEDAECHTNIS.md, No. 548).
    /// Looked for in the appended part: the trailer read back is merged with the older ones and would hold the key anyway.
    /// </summary>
    [Fact]
    public void Trailer_KeepsTheEntriesOfThePreviousOne()
    {
        var original = TestPdf.Simple(pages: 1).Build(patchTrailer: OwnKey);
        var updated = Append(original, (doc, update) => ChangeTitle(doc, update, "Neuer Titel"));
        var added = Added(original, updated);
        Assert.Contains("/PDFKneadTest", added);
        Assert.Contains("/Prev", added);
        using var reopened = PdfDocument.Open(updated);
        Assert.False(reopened.WasRepaired);
        Assert.Equal("Neuer Titel", Title(reopened));
        Assert.Equal("Max Mustermann", reopened.NewestTrailer!.GetString(PdfName.Get("PDFKneadTest"))!.ToText());
    }

    /// <summary>
    /// From a cross-reference stream only the trailer entries go on, not the keys of the stream (/W, /Index, /Filter …), which would describe
    /// the old stream (the new stream has its own, /Filter only /FlateDecode); no /XRefStm, no /AuthCode (a MAC is valid only for its revision,
    /// ISO/TS 32004, 6.5), no /DocChecksum.
    /// </summary>
    [Fact]
    public void Trailer_XRefStream_KeepsCustomKeys_WithoutStreamKeys()
    {
        var original = TestPdf.Simple(pages: 1).Build(XRefStyle.Stream, patchTrailer: trailer =>
        {
            OwnKey(trailer);
            trailer.Set(PdfName.Get("AuthCode"), TestPdf.Dict(("MACLocation", TestPdf.N("Standalone"))));
            trailer.Set(PdfName.Get("DocChecksum"), TestPdf.N("0123456789ABCDEF0123456789ABCDEF"));
        });
        var updated = Append(original, (doc, update) => ChangeTitle(doc, update, "Neuer Titel"));
        var added = Added(original, updated);
        Assert.Contains("/PDFKneadTest", added);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(added, @"/W\s*\["));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(added, @"/Index\s*\["));
        Assert.DoesNotContain("/XRefStm", added);
        Assert.DoesNotContain("/AuthCode", added);
        Assert.DoesNotContain("/DocChecksum", added);
        // only the new stream's own filter: Flate since 21.11.2026 (ISO 32000-2, 7.5.8.4; docs/FEHLERGEDAECHTNIS.md, No. 1001)
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(added, @"/Filter\s*/FlateDecode"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(added, @"/Filter\b"));
        using var reopened = PdfDocument.Open(updated);
        Assert.False(reopened.WasRepaired);
        Assert.Equal("Neuer Titel", Title(reopened));
        Assert.True(reopened.Trailer.GetInteger(PdfName.Size) >= reopened.HighestObjectNumber + 1);
    }

    /// <summary>A direct /Info (allowed since a correction of ISO 32000-2) stays in the trailer of the update — before it was left out.</summary>
    [Fact]
    public void Trailer_DirectInfo_Kept()
    {
        var original = TestPdf.Simple(pages: 1).Build(patchTrailer: trailer =>
            trailer.Set(PdfName.Info, TestPdf.Dict(("Title", new PdfString(PdfTextEncoding.Encode("Direkter Titel"))))));
        var updated = Append(original, (doc, update) => update.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Test"))), "Anhang"u8.ToArray())));
        Assert.Contains("/Info", Added(original, updated));
        using var reopened = PdfDocument.Open(updated);
        Assert.Equal("Direkter Titel", Title(reopened));
        Assert.IsType<PdfDictionary>(reopened.NewestTrailer!.GetRaw(PdfName.Info));
    }

    [Fact]
    public void SizeTooSmall_NewObjectsAfterTheHighestNumber()
    {
        var original = TestPdf.Simple(pages: 2).Build();
        var text = System.Text.Encoding.Latin1.GetString(original);
        var at = text.LastIndexOf("/Size ", StringComparison.Ordinal);
        var end = text.IndexOfAny([' ', '/', '>', '\n'], at + 6);
        var shrunk = System.Text.Encoding.Latin1.GetBytes(text[..(at + 6)] + "3".PadRight(end - at - 6) + text[end..]);
        using (var check = PdfDocument.Open(shrunk))
        {
            Assert.False(check.WasRepaired);
            Assert.True(check.HighestObjectNumber > 3);
        }
        var updated = Append(shrunk, (doc, update) => ChangeTitle(doc, update, "Neuer Titel"));
        using var reopened = PdfDocument.Open(updated);
        Assert.False(reopened.WasRepaired);
        // every object of the file is still there as it was (only the information dictionary was replaced on purpose)
        using (var before = PdfDocument.Open(shrunk))
        {
            var info = ((PdfReference)before.Trailer.GetRaw(PdfName.Info)!).Id.Number;
            for (var n = 1; n <= before.HighestObjectNumber; n++)
                if (n != info) Assert.Equal(before.GetObject(n).ToString(), reopened.GetObject(n).ToString());
        }
        Assert.Equal("Neuer Titel", Title(reopened));
        Assert.Equal(2, reopened.PageCount);
        Assert.All(reopened.Pages, p => Assert.NotNull(p.Dictionary.Get(PdfName.Contents) as PdfStream));
        var extra = (PdfStream)reopened.Trailer.GetDictionary(PdfName.Get("Info"))!.Get(PdfName.Get("Extra"));
        Assert.Equal("Anhang"u8.ToArray(), reopened.GetRawStreamData(extra));
    }

    [Fact]
    public void RepairedFile_CannotAppend()
    {
        var original = TestPdf.Simple(pages: 1).Build();
        var broken = original.ToArray();
        var text = System.Text.Encoding.Latin1.GetString(broken);
        var at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        broken[at + "startxref".Length + 1] = (byte)'9';   // the stated offset no longer points to the table
        using var doc = PdfDocument.Open(broken);
        Assert.True(doc.WasRepaired);
        Assert.False(IncrementalUpdate.CanAppend(doc));
        Assert.Throws<NotAppendableException>(() => new IncrementalUpdate(doc));
    }

    /// <summary>
    /// A classic file whose cross-reference entry for the content stream of the first page points to a wrong place: opening does not
    /// notice it, only reading that stream repairs the table.
    /// </summary>
    internal static byte[] LatentlyBroken()
    {
        var file = TestPdf.Simple(pages: 1).Build();
        int number;
        using (var doc = PdfDocument.Open(file))
            number = ((PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Get("Contents"))!).Id.Number;
        var text = System.Text.Encoding.Latin1.GetString(file);
        var table = text.LastIndexOf("xref\n0 ", StringComparison.Ordinal);
        var entries = text.IndexOf('\n', table + 5) + 1;
        "0000000009"u8.CopyTo(file.AsSpan(entries + 20 * number));   // inside the header, not an object
        return file;
    }

    /// <summary>
    /// The document repairs its cross-references while the update is built (fuzzing, S7): nothing may be written, because the update
    /// would continue the wrong table; the failure is the one that lets the caller write the file anew. Before, writing failed on a
    /// missing value.
    /// </summary>
    [Fact]
    public void RepairedWhileBuilding_NothingWritten()
    {
        using var doc = PdfDocument.Open(LatentlyBroken());
        Assert.True(IncrementalUpdate.CanAppend(doc));
        var update = new IncrementalUpdate(doc);
        update.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Test"))), "Anhang"u8.ToArray()));
        _ = doc.Pages[0].Dictionary.Get(PdfName.Get("Contents"));
        Assert.True(doc.WasRepaired);
        using var output = new MemoryStream();
        Assert.Throws<NotAppendableException>(() => update.WriteTo(output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void StreamFromFile_IsRejected()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var update = new IncrementalUpdate(doc);
        var page = doc.Pages[0];
        var contents = (PdfStream)page.Dictionary.Get(PdfName.Get("Contents"));
        Assert.Throws<ArgumentException>(() => update.Add(contents));
    }
}
