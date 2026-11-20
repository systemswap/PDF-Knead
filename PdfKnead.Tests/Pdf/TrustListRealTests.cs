using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Xml;
using PdfKnead.Core.Pdf.Signatures.TrustLists;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The real EU trusted lists (docs/PRUEFUNGEN.md, „EU-Vertrauensliste“): only when PDFKNEAD_EU_LISTE names a folder with the list of lists
/// (<c>eu-lotl.xml</c>) and the national lists (<c>laender/&lt;Land&gt;.xml</c>) — the tests never go to the network. The own canonicalization
/// and signature check are compared with the XML signature code of the .NET libraries (in the test project only).
/// </summary>
public sealed class TrustListRealTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("PDFKNEAD_EU_LISTE") is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    /// <summary>The reference: the .NET XML signature code's verdict and its canonical form of the document without the signature.</summary>
    private static (bool Valid, byte[] Canonical, byte[] SignedInfo) Reference(byte[] data)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            document.Load(reader);
        var element = (XmlElement)document.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#")[0]!;
        var signed = new SignedXml(document);
        signed.LoadXml(element);
        var valid = signed.CheckSignature();

        var transform = new XmlDsigExcC14NTransform();
        var copy = (XmlDocument)document.Clone();
        var signatureCopy = copy.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#")[0]!;
        signatureCopy.ParentNode!.RemoveChild(signatureCopy);
        transform.LoadInput(copy);
        var canonical = ((MemoryStream)transform.GetOutput(typeof(Stream))).ToArray();

        var info = new XmlDsigExcC14NTransform();
        info.LoadInput(element.GetElementsByTagName("SignedInfo", "http://www.w3.org/2000/09/xmldsig#")[0]!.SelectNodes("descendant-or-self::node()|descendant-or-self::node()/@*")!);
        var signedInfo = ((MemoryStream)info.GetOutput(typeof(Stream))).ToArray();
        return (valid, canonical, signedInfo);
    }

    [Fact]
    public void RealLists_OwnCheckAgreesWithReference()
    {
        if (Folder is not { } folder) return;
        XmlSignatureMethods.Register();
        var lotlData = File.ReadAllBytes(Path.Combine(folder, "eu-lotl.xml"));
        var lotlDocument = XmlSignature.Load(lotlData);
        var lotl = TrustedList.Read(lotlDocument);
        Assert.Equal("EU", lotl.Territory);
        var self = lotl.Pointers.Single(p => p.Territory == "EU");
        XmlSignature.Verify(lotlDocument, self.Signers);
        Compare(lotlData, lotlDocument);

        var checkedLists = 0;
        var validLists = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "laender"), "*.xml"))
        {
            var territory = Path.GetFileNameWithoutExtension(file);
            var data = File.ReadAllBytes(file);
            if (data.Length == 0) continue;
            var document = XmlSignature.Load(data);
            var pointer = lotl.Pointers.FirstOrDefault(p => p.Territory == territory);
            Assert.NotNull(pointer);
            var (valid, _, _) = Reference(data);
            var ours = Record.Exception(() => XmlSignature.Verify(document, pointer.Signers));
            if (territory == "DE") Assert.Null(ours);   // signed with an id-RSASSA-PSS key: the reference code cannot check it, OpenSSL agrees with ours
            else Assert.True(valid == (ours is null), $"{territory}: Referenz {(valid ? "gültig" : "ungültig")}, eigene Prüfung {ours?.Message ?? "gültig"}");
            if (valid) validLists++;
            Compare(data, document);
            var list = TrustedList.Read(document);
            Assert.Equal(territory, list.Territory);
            Assert.NotEmpty(list.Services);
            checkedLists++;
        }
        Assert.True(checkedLists >= 25, $"nur {checkedLists} Listen geprüft");
        // every list the reference code can check agrees; the German one it cannot (a PSS-only key it does not hand out), ours must
        Assert.True(validLists >= checkedLists - 1, $"nur {validLists} von {checkedLists} Listen gültig");
    }

    /// <summary>The store loads the kept lists and checks them again with the pinned anchors; prints what it took and what it found.</summary>
    [Fact]
    public void RealLists_StoreLoadsWithPinnedAnchors()
    {
        if (Folder is not { } folder) return;
        var kept = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"eu-liste-{Guid.NewGuid():N}");
        Directory.CreateDirectory(kept);
        File.Copy(Path.Combine(folder, "eu-lotl.xml"), Path.Combine(kept, "eu-lotl.xml"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "laender"), "*.xml"))
            File.Copy(file, Path.Combine(kept, Path.GetFileName(file)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var set = TrustListStore.Load(kept);
        var took = watch.ElapsedMilliseconds;
        Directory.Delete(kept, recursive: true);
        var services = set.Lists.SelectMany(l => l.Services).ToList();
        Console.WriteLine($"EU-Listen: {set.Lists.Count} Länder, {took} ms, CA/QC {services.Count(s => s.Type == TrustedList.QualifiedCa)}, " +
                          $"QTST {services.Count(s => s.Type == TrustedList.QualifiedTimestamps)}, fehlend: {string.Join("; ", set.Failures.Select(f => f.Key + "=" + f.Value))}");
        Assert.True(set.Lists.Count >= 25);
        Assert.DoesNotContain("DE", set.Failures.Keys);
        Assert.Contains(services, s => s.Territory == "DE" && s.Type == TrustedList.QualifiedTimestamps);
    }

    /// <summary>
    /// The way the program loads (docs/PRUEFUNGEN.md, „EU-Vertrauensliste“): only when PDFKNEAD_EU_LISTE_LADEN names a folder — fetches the
    /// real lists from the network into it, with the pinned anchors.
    /// </summary>
    [Fact]
    public void RealDownload_WhenGiven()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_EU_LISTE_LADEN") is not { Length: > 0 } folder) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var set = TrustListStore.Download(new PdfKnead.Core.Platform.HttpTrustListSource(), folder);
        Console.WriteLine($"Geladen: {set.Lists.Count} Länder in {watch.Elapsed.TotalSeconds:F1} s, Liste Nr. {set.Lotl.Sequence} vom {set.Lotl.Issued:d}; " +
                          $"fehlend: {string.Join("; ", set.Failures.Select(f => f.Key + "=" + f.Value))}");
        Assert.True(set.Lists.Count >= 25);
    }

    private static void Compare(byte[] data, XmlDocument document)
    {
        var (_, canonical, signedInfo) = Reference(data);
        var signature = XmlSignature.Children(document.DocumentElement!, "Signature").Single();
        Assert.Equal(canonical, ExclusiveCanonicalizer.Canonicalize(document, signature));
        Assert.Equal(signedInfo, ExclusiveCanonicalizer.Canonicalize(XmlSignature.Children(signature, "SignedInfo").Single()));
    }
}
