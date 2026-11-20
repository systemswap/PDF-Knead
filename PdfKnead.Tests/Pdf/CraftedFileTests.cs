using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Security: crafted files from the review agents and from fuzzing. No process termination (stack overflow,
/// overflow, reading after closing), no hang, and after redaction nothing redacted remains in the file.
/// </summary>
// Alone, not in parallel: the time limits (hangs) apply to a run without foreign load; under the load of the print
// tests the form cascade once took 32 instead of 19 s
[Collection(nameof(LimitCollection))]
public sealed class CraftedFileTests
{
    private const string Font = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";

    /// <summary>
    /// One text operator with more characters than the work limit of a page (a string of 16.1 million characters, as a damaged file had 56
    /// million): redacting refuses the page at once — the string was a single operation, and redacting it took over ten seconds without a
    /// way to cancel (docs/FEHLERGEDAECHTNIS.md, No. 420).
    /// </summary>
    [Fact]
    public void StringBeyondTheWorkLimit_RedactingRefusesThePage()
    {
        var pdf = TestPdf.Simple(1);
        var text = new byte[16_100_000];
        Array.Fill(text, (byte)'A');
        pdf.Replace(4, TestPdf.FlateStream([.. "BT /F1 1 Tf 72 700 Td ("u8, .. text, .. ") Tj ET"u8]));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Throws<PdfLimitException>(() => Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(0, 0, 612, 792)]));
    }

    /// <summary>
    /// An appearance stream nested deeper than the parser allows, looked at by „Dokument bereinigen“: the check runs on several pages at
    /// once, and the threads wrapped the limit in an AggregateException — the caller, who handles the document's own errors, got an
    /// exception it does not know (found by fuzzing, docs/FEHLERGEDAECHTNIS.md, No. 935).
    /// </summary>
    [Fact]
    public void AppearanceNestedTooDeep_CleanupCheckReportsTheLimit()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Resources<<>>/Annots[4 0 R]>>",
            [4] = "<</Type/Annot/Subtype/Stamp/Rect[0 0 50 50]/AP<</N 5 0 R>>>>",
            [5] = new string('[', 300) + new string(']', 300),
        });
        var items = PageItem.From(doc);
        Assert.Throws<PdfLimitException>(() => DocumentCleanup.PagesWithInvisibleText(items));
        Assert.Throws<PdfLimitException>(() => DocumentCleanup.Inspect(doc, items));
    }

    /// <summary>Builds a PDF from objects given as text: string = object, (dictionary content, stream content) = stream.</summary>
    internal static byte[] Build(Dictionary<int, object> objects, string trailerExtra = "") => TestFiles.Build(objects, trailerExtra);

    private static PdfDocument Open(Dictionary<int, object> objects, string trailerExtra = "") =>
        PdfDocument.Open(TestFiles.Write(Build(objects, trailerExtra), "praepariert.pdf"));

    private static Dictionary<int, object> OnePage(string content, string resources = "<<>>") => new()
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = $"<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources {resources}>>",
        [7] = ("", content),
    };

    // ------------------------------------------------------------ Process termination

    [Fact]
    public void ImageMaskPointingToItself_DoesNotOverflowTheStack()
    {
        // Coloured mask with /SMask pointing to itself: without a guard DecodeCore → MaskAlpha → Alpha → DecodeCore … (stack overflow)
        var objects = OnePage("q 100 0 0 100 50 50 cm /Im1 Do Q", "<</XObject<</Im1 5 0 R>>>>");
        objects[5] = ("/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceRGB/BitsPerComponent 8/SMask 5 0 R", "\x10\x20\x30");
        using var doc = Open(objects);
        var image = Assert.Single(PageInterpreter.Build(doc, doc.Pages[0]).Images);
        Assert.NotNull(PdfImageDecoder.Decode(image, doc));
    }

    [Fact]
    public void HugeImageWidth_IsRefusedWithoutOverflow()
    {
        // /Width 2147483647: the row buffer width * 4 would overflow (OverflowException in the drawing thread = process termination)
        var objects = OnePage("q 100 0 0 100 50 50 cm /Im1 Do Q", "<</XObject<</Im1 5 0 R>>>>");
        objects[5] = ("/Type/XObject/Subtype/Image/Width 2147483647/Height 1/ColorSpace/DeviceRGB/BitsPerComponent 8", "\x10\x20\x30");
        using var doc = Open(objects);
        var image = Assert.Single(PageInterpreter.Build(doc, doc.Pages[0]).Images);
        Assert.Null(PdfImageDecoder.Decode(image, doc));
    }

    [Fact]
    public void LongLengthChain_DoesNotOverflowTheStack()
    {
        // /Length of a stream points to a stream whose /Length … (2 000 links): without a guard a stack overflow
        var objects = OnePage("BT ET");
        const int chain = 2000;
        for (var i = 0; i < chain; i++) objects[10 + i] = ($"/Length {11 + i} 0 R", "x");
        objects[10 + chain] = "1";
        using var doc = Open(objects);
        Assert.IsType<PdfStream>(doc.GetObject(10));
    }

    [Fact]
    public void ReadingAfterClose_IsAnObjectDisposedError()
    {
        var bytes = new byte[5 << 20];   // above 4 MB: memory-mapped
        var path = TestFiles.Write(bytes, "gross.bin");
        var source = Core.Pdf.IO.PdfSource.FromFile(path);
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Span(0, 10).ToArray());
        // Single bytes too (the lexer reads byte by byte): before, they read the released mapping — an access violation ends the
        // process. Also through a shorter source of the closed one
        Assert.Throws<ObjectDisposedException>(() => source[0]);
        Assert.Throws<ObjectDisposedException>(() => source.At(0));
        var open = Core.Pdf.IO.PdfSource.FromFile(path);
        var prefix = open.Prefix(100);
        open.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prefix.At(5));
        // Outside the data stays -1, a small source read completely stays readable
        Assert.Equal(-1, source.At(bytes.Length));
        var small = Core.Pdf.IO.PdfSource.FromBytes([1, 2, 3]);
        small.Dispose();
        Assert.Equal(2, small.At(1));
    }

    /// <summary>
    /// DPAPI fails when saving the device key (user profile not loaded, for example under a service account): before, the
    /// CryptographicException ended the start of the program. Now the new key applies for this session, and no file
    /// (also no half-written temporary one) is left. The collection runs alone, so no other test saves a key meanwhile.
    /// </summary>
    [Fact]
    public void DeviceKey_FailingDpapi_KeyForThisSession()
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-schluessel-").FullName;
        var file = Path.Combine(dir, "geraet.key");
        var before = Core.Platform.UserSecretFile.Protector;
        try
        {
            Core.Platform.UserSecretFile.Protector = (_, _) => throw new System.Security.Cryptography.CryptographicException("DPAPI nicht verfügbar");
            using var device = Core.Platform.DeviceIdentity.LoadOrCreate(file);
            var data = Encoding.UTF8.GetBytes("Herkunft");
            Assert.True(Core.Platform.DeviceIdentity.Verify(device.PublicKey, data, device.Sign(data)));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Core.Platform.UserSecretFile.Protector = before;
        }
    }

    [Fact]
    public void DeeplyNestedBookmarks_DoNotOverflowTheStack()
    {
        // 60 000 bookmarks, each the only child of the one before, the last with a script: before, the search for scripts that
        // could show a hidden annotation (HiddenContent) went down recursively without a limit — a stack overflow ends the process
        const int depth = 60_000;
        var objects = OnePage("BT ET");
        objects[1] = "<</Type/Catalog/Pages 2 0 R/Outlines 8 0 R>>";
        objects[4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<<>>/Annots[5 0 R]>>";
        objects[5] = "<</Type/Annot/Subtype/Square/Rect[10 10 50 50]/F 2/T(Versteckt)>>";
        objects[8] = "<</Type/Outlines/First 10 0 R/Last 10 0 R/Count 1>>";
        for (var i = 0; i < depth - 1; i++) objects[10 + i] = $"<</Title(K{i})/First {11 + i} 0 R/Last {11 + i} 0 R>>";
        objects[10 + depth - 1] = "<</Title(Unten)/A<</S/JavaScript/JS(this.getAnnot\\(0, \"Versteckt\"\\).hidden = false;)>>>>";
        using var doc = Open(objects);
        var scan = HiddenContent.Annotations(doc, doc.Pages[0], scriptsRemoved: false);
        Assert.Single(scan.Unseen.Concat(scan.Kept.Select(k => k.Annotation)));
        Assert.NotEmpty(PdfOutline.Read(doc));
        var path = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, "tiefe-lesezeichen.pdf");
        using (var file = File.Create(path))
            PdfWriter.Write(doc, PageItem.From(doc), file, new PdfSaveOptions());
        using var saved = PdfDocument.Open(path);
        Assert.Equal(1, saved.PageCount);
    }

    [Fact]
    public void NamesBeyondTheTableLimit_AreComparedByValue()
    {
        // Beyond 200 000 different names (a crafted file can bring that many), new names are separate objects. Before, small
        // dictionaries, /Type and the cleanup compared names by reference: entries were not found, and a /JavaScript key
        // could slip past "Bereinigen". Here the limit is reached at once (only in this test; the collection runs alone)
        var unique = "Pruefname" + Guid.NewGuid().ToString("N");
        var before = PdfName.MaxInterned;
        try
        {
            PdfName.MaxInterned = 0;
            var key = PdfName.Get(unique);
            var same = PdfName.Get(unique);
            Assert.False(ReferenceEquals(key, same));
            Assert.True(key == same);
            Assert.False(key != same);
            Assert.True(key.Equals(same));
            Assert.False(key == PdfName.Type);

            var objects = OnePage("BT ET");
            objects[9] = $"<</Type/{unique}/{unique} 42/{unique}x 7>>";
            using var doc = Open(objects);
            var dict = Assert.IsType<PdfDictionary>(doc.GetObject(9));
            Assert.True(dict.IsType(PdfName.Get(unique)));
            Assert.Equal(42, dict.GetInteger(PdfName.Get(unique)));
            Assert.Equal(7, dict.GetInteger(PdfName.Get(unique + "x")));
            Assert.True(dict.ContainsKey(same));
            // Shared names keep working as before (fast path by reference)
            Assert.Equal(1, doc.PageCount);
        }
        finally
        {
            PdfName.MaxInterned = before;
        }
    }

    // ------------------------------------------------------------ Hangs

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void NameTreeKidsPointingToThemselves_ReadsQuickly()
    {
        // Name tree whose node points to itself four times: without a guard 4^32 calls when reading the bookmarks (UI thread)
        var objects = OnePage("BT ET");
        objects[1] = "<</Type/Catalog/Pages 2 0 R/Outlines 8 0 R/Names<</Dests 9 0 R>>>>";
        objects[8] = "<</Type/Outlines/First 10 0 R/Last 10 0 R/Count 1>>";
        objects[9] = "<</Kids[9 0 R 9 0 R 9 0 R 9 0 R]>>";
        objects[10] = "<</Title(Kapitel)/Parent 8 0 R/Dest(fehlt)>>";
        using var doc = Open(objects);
        var watch = Stopwatch.StartNew();
        var outline = PdfOutline.Read(doc);
        Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.Null(Assert.Single(outline).PageIndex);
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void FormCascade_StopsAtTheOperationBudget()
    {
        // Every form XObject calls the next one 10 times without painting: 10^9 operators; without a budget endless (and not cancellable)
        var objects = OnePage("/F1 Do", "<</XObject<</F1 11 0 R>>>>");
        const int depth = 9;
        for (var level = 1; level <= depth; level++)
        {
            var calls = level < depth ? string.Concat(Enumerable.Repeat($"/F{level + 1} Do ", 10)) : "q Q q Q";
            var resources = level < depth ? $"/Resources<</XObject<</F{level + 1} {11 + level} 0 R>>>>" : "";
            objects[10 + level] = ($"/Type/XObject/Subtype/Form/BBox[0 0 10 10]{resources}", calls);
        }
        using var doc = Open(objects);
        var watch = Stopwatch.StartNew();
        PageInterpreter.Build(doc, doc.Pages[0]);
        // Limit against "endless": in a Debug build under load, merely reaching the budget takes 25 … 31 s
        Assert.True(watch.Elapsed.TotalSeconds < 60 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains(doc.Problems, p => p.Contains("aufwendig"));
    }

    [Fact]
    public void DeviceNWithManyComponents_IsRefused()
    {
        // 100 colour components: their buffers must not lie on the stack (stackalloc with a size taken from the file)
        var names = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"/C{i}"));
        var objects = OnePage("/CS0 cs 0.5 scn 10 10 100 100 re f", $"<</ColorSpace<</CS0[/DeviceN[{names}]/DeviceGray 6 0 R]>>>>");
        objects[6] = "<</FunctionType 2/Domain[0 1]/C0[0]/C1[1]/N 1>>";
        using var doc = Open(objects);
        Assert.True(PageInterpreter.Build(doc, doc.Pages[0]).Count > 0);
    }

    // ------------------------------------------------------------ Redaction

    private static string AllText(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder(Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        var size = (int)(doc.Trailer.GetInteger(PdfName.Size) ?? 0);
        for (var i = 1; i < size; i++)
            if (doc.GetObject(i) is PdfStream s)
                sb.Append(Encoding.Latin1.GetString(doc.GetStreamData(s)));
        return sb.ToString();
    }

    private static string SaveRedacted(PdfDocument doc, int page, params PdfRectangle[] areas)
    {
        var items = PageItem.From(doc);
        items[page] = items[page].WithElements([RedactionElement.FromPageAreas(areas)]);
        var path = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, "geschwaerzt.pdf");
        using (var file = File.Create(path))
            PdfWriter.Write(doc, items, file, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
        return path;
    }

    [Fact]
    public void Redaction_SharedResources_DropTheOriginals()
    {
        // Both pages share one /Resources object: page 2 must not carry along the unredacted form XObject and the image of page 1
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources 9 0 R>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 8 0 R/Resources 9 0 R>>",
            [6] = ("/Type/XObject/Subtype/Form/BBox[0 0 300 50]", "BT /F1 12 Tf 0 10 Td (GEHEIMFORM) Tj ET"),
            [7] = ("", "q 1 0 0 1 72 600 cm /Fm1 Do Q q 200 0 0 50 72 400 cm /Im1 Do Q"),
            [8] = ("", "BT /F1 12 Tf 72 700 Td (Seite zwei) Tj ET"),
            [9] = "<</Font<</F1 3 0 R>>/XObject<</Fm1 6 0 R/Im1 10 0 R>>>>",
            [10] = ("/Type/XObject/Subtype/Image/Width 4/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", "BILD"),
        };
        using var doc = Open(objects);
        var text = AllText(SaveRedacted(doc, 0, new PdfRectangle(60, 390, 400, 660)));
        Assert.DoesNotContain("GEHEIMFORM", text);
        Assert.DoesNotContain("BILD", text);
        Assert.Contains("Seite zwei", text);
    }

    [Fact]
    public void Redaction_CharactersCutFromTJ_LeaveOneRoundedGap()
    {
        // A gap per character and the kerning values in between would reveal the character count and widths ("Edact-Ray")
        var objects = OnePage("BT /F1 12 Tf 72 700 Td [(Max) -250 (M) 12 (u) -7 (s) 15 (t) 3 (e) (r) -40 (m) 22 (a) (n) (n)] TJ ET",
            "<</Font<</F1 3 0 R>>>>");
        objects[3] = Font;
        using var doc = Open(objects);
        var text = AllText(SaveRedacted(doc, 0, new PdfRectangle(96, 690, 300, 715)));
        var array = text[text.IndexOf("[(Max)", StringComparison.Ordinal)..];
        array = array[..(array.IndexOf(']') + 1)];
        // No number left: nothing follows „Max“ in the line, and since the line is set anew (docs/FEHLERGEDAECHTNIS.md, No. 236) neither the
        // spacing before the gap nor the gap itself is written (before: two numbers, the spacing and the single rounded gap)
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(array.Replace("(Max)", ""), @"-?[0-9.]+"));
    }

    [Fact]
    public void Redaction_DegenerateCharacters_AreRemoved()
    {
        // 0 Tf, 0 Tz and a matrix without area: these characters must not escape the coverage test and stay under the bar
        var objects = OnePage("q BT /F1 0 Tf 100 700 Td (NULLGROESSE) Tj ET Q q BT /F1 12 Tf 0 Tz 100 700 Td (NULLTZ) Tj ET Q " +
                              "q BT /F1 12 Tf 0 0 0 0 100 700 Tm (NULLMATRIX) Tj ET Q", "<</Font<</F1 3 0 R>>>>");
        objects[3] = Font;
        using var doc = Open(objects);
        var text = AllText(SaveRedacted(doc, 0, new PdfRectangle(90, 690, 300, 720)));
        Assert.DoesNotContain("NULLGROESSE", text);
        Assert.DoesNotContain("NULLTZ", text);
        Assert.DoesNotContain("NULLMATRIX", text);
    }

    [Fact]
    public void Redaction_PatternUnderTheBar_IsNotKept()
    {
        var objects = OnePage("/Pattern cs /P1 scn 72 600 200 30 re f", "<</Font<</F1 3 0 R>>/Pattern<</P1 6 0 R>>>>");
        objects[3] = Font;
        objects[6] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 200 30]/XStep 200/YStep 30/Resources<</Font<</F1 3 0 R>>>>",
            "BT /F1 12 Tf 0 10 Td (MUSTERGEHEIM) Tj ET");
        using var doc = Open(objects);
        Assert.DoesNotContain("MUSTERGEHEIM", AllText(SaveRedacted(doc, 0, new PdfRectangle(60, 590, 300, 640))));
    }

    [Fact]
    public void Redaction_NamedPropertiesLoseTheirActualText()
    {
        var objects = OnePage("/Span /MC0 BDC BT /F1 12 Tf 72 700 Td (Mustermann) Tj ET EMC",
            "<</Font<</F1 3 0 R>>/Properties<</MC0<</ActualText(BENANNTGEHEIM)>>>>>>");
        objects[3] = Font;
        using var doc = Open(objects);
        Assert.DoesNotContain("BENANNTGEHEIM", AllText(SaveRedacted(doc, 0, new PdfRectangle(60, 690, 300, 720))));
    }

    [Fact]
    public void Redaction_CompoundPath_KeepsTheVisibleParts()
    {
        // "Glyphs" as subpaths of one painting operator: neither keep all outlines in the file nor drop the whole line
        var path = new StringBuilder();
        for (var k = 0; k < 12; k++) path.Append($"{100 + k * 25} 700 m {115 + k * 25} 700 l {115 + k * 25} 715 l h ");
        path.Append("123.456 701 m 124.5 712 l 125 701 l h f");
        using var doc = Open(OnePage(path.ToString()));
        var text = AllText(SaveRedacted(doc, 0, new PdfRectangle(95, 695, 150, 720)));
        Assert.DoesNotContain("123.456", text);
        Assert.Contains("375 700 m", text);   // the last one, far to the right: stays
    }

    [Fact]
    public void Protection_PasswordOutsidePdfDocEncoding_UsesAes256()
    {
        // For RC4/AES-128 "Пароль" would become "??????", and any sequence of question marks would open the file
        using var doc = Open(OnePage("BT ET"));
        var path = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, "geschuetzt.pdf");
        PdfSaveResult result;
        using (var file = File.Create(path))
            result = PdfWriter.Write(doc, PageItem.From(doc), file,
                new PdfSaveOptions { Version = "1.4", Protection = new PdfProtection("Пароль", "Besitzer€Пароль", PdfPermissions.All) });
        Assert.True(result.VersionRaised);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(path, "??????"));
        using var opened = PdfDocument.Open(path, "Пароль");
        Assert.Equal(1, opened.PageCount);
    }

    /// <summary>
    /// Broken Brotli data: depending on the position, BrotliStream throws InvalidOperationException instead of InvalidDataException. This
    /// must abort neither display nor saving: as much as possible is decoded and reported as truncated.
    /// </summary>
    [Fact]
    public void BrokenBrotli_IsTruncatedNotThrown()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("BrotliDecode"));
        var random = new Random(9);
        for (var i = 0; i < 400; i++)
        {
            var data = new byte[random.Next(1, 300)];
            random.NextBytes(data);
            Core.Pdf.Filters.StreamFilters.Decode(data, dict);
        }
    }

    /// <summary>
    /// CVE-2026-111497: the Brotli decoder of the .NET runtime up to 10.0.13 writes outside its buffer on crafted data, and every opened
    /// file can bring /BrotliDecode streams. The tests run on the runtime of the installed SDK, which is also the one built into the
    /// AOT program (the build stops with an older one, Directory.Build.props).
    /// </summary>
    [Fact]
    public void Runtime_HasTheFixedBrotliDecoder()
    {
        Assert.True(Environment.Version >= new Version(10, 0, 14), $"Laufzeit {Environment.Version} hat die Brotli-Lücke CVE-2026-111497");
    }

    /// <summary>
    /// Valid Brotli streams with single bytes changed, cut off or with a piece repeated: unlike random bytes, they get past the header
    /// into the window and the back references, where the decoder used to write outside its buffer. Nothing throws, nothing ends the
    /// process.
    /// </summary>
    [Fact]
    public void MutatedBrotli_IsTruncatedNotThrown()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("BrotliDecode"));
        var random = new Random(11);
        var text = new byte[200_000];
        for (var i = 0; i < text.Length; i++) text[i] = (byte)("BT /F1 12 Tf 72 700 Td (Rechnung 4711) Tj ET\n"[i % 45] ^ (i % 997 == 0 ? random.Next(256) : 0));
        var packed = Core.Pdf.Filters.Brotli.Encode(text);
        for (var i = 0; i < 1500; i++)
        {
            var data = (byte[])packed.Clone();
            switch (i % 3)
            {
                case 0:
                    for (var k = random.Next(1, 4); k > 0; k--) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data = data[..random.Next(1, data.Length)];
                    break;
                default:
                    var at = random.Next(data.Length);
                    var piece = data[at..Math.Min(data.Length, at + random.Next(1, 64))];
                    data = [.. data[..at], .. piece, .. piece, .. data[at..]];
                    break;
            }
            Core.Pdf.Filters.StreamFilters.Decode(data, dict);
        }
    }
    /// <summary>
    /// A damaged trailer whose /Info points at an object that is no dictionary — here the integer that is the length of a stream: written
    /// anew, the stream keeps its own length (the reference was redirected to the new information dictionary; docs/FEHLERGEDAECHTNIS.md,
    /// No. 361), and the file is read as before.
    /// </summary>
    [Fact]
    public void InfoPointingAtANumber_NumberKeepsItsMeaning()
    {
        var file = Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>",
            [4] = ("/Extra 5 0 R", "0 0 1 rg 10 10 50 50 re f"),
            [5] = "1234",
        }, "/Info 5 0 R");
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(doc, Core.Pdf.Editing.PageItem.From(doc), output, new Core.Pdf.Writing.PdfSaveOptions { CompressStreams = false });
        using var saved = PdfDocument.Open(output.ToArray());
        var content = (PdfStream)saved.Pages[0].Dictionary.Get(PdfName.Contents)!;
        Assert.Equal(1234, content.Dictionary.Get(PdfName.Get("Extra"))?.AsNumber());
    }

    /// <summary>
    /// A CMap whose CID range starts below zero gives negative CIDs: looked up in the CIDToGIDMap they are outside it — glyph 0 instead of an
    /// index before the start of the map (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 404).
    /// </summary>
    [Fact]
    public void NegativeCid_FromADamagedCMap_DrawsNothingAndDoesNotThrow()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Resources<</Font<</F 5 0 R>>>>>>",
            [5] = "<</Type/Font/Subtype/Type0/BaseFont/Muster/Encoding 6 0 R/DescendantFonts[7 0 R]>>",
            [6] = ("/Type/CMap/CMapName/Verschoben/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>",
                "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                "1 begincidrange <00> <FF> -10 endcidrange endcmap CMapName currentdict /CMap defineresource pop end end"),
            [7] = "<</Type/Font/Subtype/CIDFontType2/BaseFont/Muster/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/DW 500" +
                  "/CIDToGIDMap 8 0 R/FontDescriptor<</Type/FontDescriptor/FontName/Muster/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800" +
                  "/Descent -200/CapHeight 700/StemV 80/FontFile2 9 0 R>>>>",
            [8] = ("", "\u0000\u0001\u0000\u0002\u0000\u0003"),
            [9] = ("", Encoding.Latin1.GetString(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"))),
        });
        var font = Core.Pdf.Fonts.PdfFont.Get((PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F")), doc);
        Assert.True(font.Cid(0x02, 1) < 0);   // CID -8
        // the display catches a failure while loading a glyph: what counts is that none happens (as the fuzzer watches for)
        var thread = Environment.CurrentManagedThreadId;
        var failures = 0;
        void Note(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception is IndexOutOfRangeException) failures++;
        }
        AppDomain.CurrentDomain.FirstChanceException += Note;
        try
        {
            Assert.Equal(0.5, font.Glyph(0x02, 1).Width, 6);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Note;
        }
        Assert.Equal(0, failures);
    }

    /// <summary>
    /// A CMap giving CIDs beyond a billion, one character shown beside one that is redacted: shortening the CIDToGIDMap afterwards keeps the
    /// shown CIDs that it holds — 2 · CID overflowed to a negative position, it was looked up before the start of the map, and the font was not
    /// cleaned (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 422).
    /// </summary>
    [Fact]
    public void CidBeyondABillionBesideARedactedCharacter_FontCleanedWithoutException()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Resources<</Font<</F 5 0 R>>>>/Contents 4 0 R>>",
            [4] = ("", "BT /F 10 Tf 20 100 Td <0240> Tj ET"),
            [5] = "<</Type/Font/Subtype/Type0/BaseFont/Muster/Encoding 6 0 R/DescendantFonts[7 0 R]>>",
            [6] = ("/Type/CMap/CMapName/Verschoben/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>",
                "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                "1 begincidrange <00> <FF> 1100000000 endcidrange endcmap CMapName currentdict /CMap defineresource pop end end"),
            [7] = "<</Type/Font/Subtype/CIDFontType2/BaseFont/Muster/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/DW 500" +
                  "/CIDToGIDMap 8 0 R/FontDescriptor<</Type/FontDescriptor/FontName/Muster/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800" +
                  "/Descent -200/CapHeight 700/StemV 80/FontFile2 9 0 R>>>>",
            [8] = ("", string.Concat(Enumerable.Range(0, 128).Select(i => $"\u0000{(char)(i % 90 + 3)}"))),
            [9] = ("", Encoding.Latin1.GetString(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"))),
        });
        var thread = Environment.CurrentManagedThreadId;
        var failures = 0;
        void Note(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception is IndexOutOfRangeException) failures++;
        }
        AppDomain.CurrentDomain.FirstChanceException += Note;
        try
        {
            RedactionTests.WithBars(doc, (0, new PdfRectangle(24.9, 96, 31, 112)));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Note;
        }
        Assert.Equal(0, failures);
    }

    /// <summary>
    /// A damaged catalogue whose /Pages points at the widths of a font (the pages are found anyway): written anew, the font keeps its widths —
    /// the array counted as an old node of the page tree, was left out of the file, and every character of the font stood on the same spot
    /// (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 423).
    /// </summary>
    [Fact]
    public void CataloguePagesPointingAtTheWidths_FontKeepsThem()
    {
        var widths = string.Join(' ', Enumerable.Range(32, 96).Select(_ => "600"));
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 3 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 300 200]>>",
            [3] = $"[{widths}]",
            [4] = "<</Type/Page/Parent 2 0 R/Resources<</Font<</F1 6 0 R>>>>/Contents 5 0 R>>",
            [5] = ("", "BT /F1 20 Tf 10 100 Td (Geheim Bleibt) Tj ET"),
            [6] = "<</Type/Font/Subtype/Type1/BaseFont/Courier/FirstChar 32/LastChar 127/Widths 3 0 R>>",
        });
        Assert.Equal(1, doc.PageCount);
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(8, 95, 90, 125))));
        var font = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal(96, font.GetArray(PdfName.Get("Widths"))?.Count);
    }

    /// <summary>
    /// Two composite fonts; a damaged file points the CIDToGIDMap of the first at the descendant of the second. A character of the second is
    /// redacted: its widths afterwards are its own — the object counted as shared by both, the second font got the CIDs and widths of the
    /// first, and its text moved (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 424).
    /// </summary>
    [Fact]
    public void PartReferredToInAnotherRole_NotShared()
    {
        const string descriptor = "/FontDescriptor<</Type/FontDescriptor/FontName/Muster/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800" +
                                  "/Descent -200/CapHeight 700/StemV 80/FontFile2 9 0 R>>";
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Resources<</Font<</F1 5 0 R/F2 6 0 R>>>>/Contents 4 0 R>>",
            [4] = ("", "BT /F1 10 Tf 20 150 Td <0030> Tj ET BT /F2 10 Tf 20 100 Td <003000310032> Tj ET"),
            [5] = "<</Type/Font/Subtype/Type0/BaseFont/Muster/Encoding/Identity-H/DescendantFonts[7 0 R]>>",
            [6] = "<</Type/Font/Subtype/Type0/BaseFont/Muster/Encoding/Identity-H/DescendantFonts[8 0 R]>>",
            [7] = "<</Type/Font/Subtype/CIDFontType2/BaseFont/Muster/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/W[48[700]]" +
                  "/CIDToGIDMap 8 0 R" + descriptor + ">>",
            [8] = "<</Type/Font/Subtype/CIDFontType2/BaseFont/Muster/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/W[48[500]49[500]50[500]]" +
                  descriptor + ">>",
            [9] = ("", Encoding.Latin1.GetString(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"))),
        });
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(29.5, 95, 36, 112))));
        var kid = (PdfDictionary)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F2"))!.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!;
        var w = kid.GetArray(PdfName.Get("W"))!;
        Assert.Equal(48, w.GetNumber(0));
        Assert.Equal(500, ((PdfArray)w.Get(1)!).GetNumber(0));
    }

    /// <summary>
    /// An image in the colour space /Pattern (which has no components and no decode ranges) is drawn without an exception: the missing decode
    /// range of its one sample is 0 … 1 (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 404).
    /// </summary>
    [Fact]
    public void ImageInThePatternColourSpace_DecodesWithoutException()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Resources<</XObject<</Im 4 0 R>>>>>>",
            [4] = ("/Type/XObject/Subtype/Image/Width 8/Height 2/ColorSpace/Pattern/BitsPerComponent 1", "\xF0\x0F"),
        });
        var image = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im"))!;
        var source = new Core.Pdf.Graphics.ImageSource(image, null, doc.Pages[0].Resources, false, 0);
        PdfKnead.Core.Pdf.Graphics.PdfImageDecoder.Decode(source, doc);
    }

    /// <summary>
    /// A JPEG 2000 image under a bar whose header claims more than the memory holds at full size (here a thousand components of 40 000 ×
    /// 40 000 pixels): blacking out does not try to decode it at full size — it goes the other way, as the display draws it. Before, the
    /// planes were allocated until the memory ran out (found by the redaction focus of the fuzzer with several workers;
    /// docs/FEHLERGEDAECHTNIS.md, No. 403).
    /// </summary>
    [Fact]
    public void JpxClaimingMoreThanTheMemory_IsNotDecodedForBlackingOut()
    {
        var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        void U16(int v) => b.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(uint v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        const int components = 1000;
        U16(38 + 3 * components);
        U16(0);
        U32(40_000); U32(40_000); U32(0); U32(0); U32(40_000); U32(40_000); U32(0); U32(0);
        U16(components);
        for (var c = 0; c < components; c++) b.AddRange([0x07, 1, 1]);
        b.AddRange([0xFF, 0x52, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x04, 0x04, 0x00, 0x01]);
        b.AddRange([0xFF, 0x5C, 0x00, 0x04, 0x40, 0x40]);
        b.AddRange([0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00]);
        U32(15);
        b.AddRange([0x00, 0x01, 0xFF, 0x93, 0x80, 0xFF, 0xD9]);
        var data = b.ToArray();
        Assert.True(Core.Pdf.Filters.Jpx.JpxDecoder.FullSizeBytes(data) > GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);
        var stream = new PdfStream(new PdfDictionary(), data);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(ImageBlanker.DecodeJpx(stream, data, 0));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20);
    }
}
