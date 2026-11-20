using System.Text;
using PdfKnead.Core.Scanning;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// Scan profiles (KONZEPT §5.25): saving one file per profile, reading back, export and import with their limits against prepared files,
/// merging imported profiles without replacing the user's own; finding blank pages; pairing fronts and backs.
/// </summary>
public sealed class ScanProfileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdfknead-scanprofile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static readonly ScanProfile Office = new("Büro A4", "airscan:e1:Muster", "Muster Scanner", "ADF", 200, ScanColorMode.Gray, "A4",
        ManualDuplex: true, SkipBlank: true, BlankLimit: 0.3);

    [Fact]
    public void Store_SaveLoadReplaceDelete()
    {
        var store = new ScanProfileStore(_folder);
        Assert.Empty(store.Load());   // the folder does not exist yet
        store.Save(Office);
        store.Save(Office with { Name = "Fotos", Mode = ScanColorMode.Color, Dpi = 600 });
        var loaded = store.Load();
        Assert.Equal(["Büro A4", "Fotos"], loaded.Select(p => p.Name));
        Assert.Equal(Office, loaded[0]);
        Assert.True(store.Exists("Büro A4"));
        Assert.False(store.Exists("büro a4"));   // names differing in case are different profiles

        // the same name again replaces it
        store.Save(Office with { Dpi = 300 });
        Assert.Equal(300, Assert.Single(store.Load(), p => p.Name == "Büro A4").Dpi);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);

        Assert.True(store.Delete("Fotos"));
        Assert.False(store.Delete("Fotos"));
        Assert.Equal(["Büro A4"], store.Load().Select(p => p.Name));
    }

    [Fact]
    public void Store_NamesWithPathCharacters_StayInTheFolder()
    {
        var store = new ScanProfileStore(_folder);
        foreach (var name in new[] { "../../böse", "a/b\\c:d*e?f", "CON", "Büro", "Buero", "BÜRO" }) store.Save(Office with { Name = name });
        Assert.Equal(6, store.Load().Count);
        Assert.All(Directory.GetFiles(_folder), f => Assert.Equal(Path.GetFullPath(_folder), Path.GetDirectoryName(f)));
    }

    [Fact]
    public void Store_BrokenAndForeignFiles_LeftOut()
    {
        var store = new ScanProfileStore(_folder);
        store.Save(Office);
        File.WriteAllText(Path.Combine(_folder, "kaputt.json"), "{ \"Name\": ");
        File.WriteAllText(Path.Combine(_folder, "ohne-name.json"), "{ \"Dpi\": 300 }");
        File.WriteAllBytes(Path.Combine(_folder, "riesig.json"), new byte[100_000]);
        Assert.Equal(["Büro A4"], store.Load().Select(p => p.Name));
    }

    [Fact]
    public void Normalized_ValuesInsideTheirLimits()
    {
        var odd = new ScanProfile("  Na\u0000me\u202E\u2028 mit Steuerzeichen  " + new string('x', 200), new string('d', 2000), null, "src\nX", -5,
            (ScanColorMode)99, "", BlankLimit: double.NaN).Normalized();
        Assert.StartsWith("Name mit Steuerzeichen", odd.Name);
        Assert.Equal(ScanProfile.MaxName, odd.Name.Length);
        Assert.Equal(512, odd.DeviceId!.Length);
        Assert.Equal("srcX", odd.Source);
        Assert.Equal(25, odd.Dpi);
        Assert.Equal(ScanColorMode.Color, odd.Mode);
        Assert.Equal("A4", odd.Format);
        Assert.Equal(BlankPages.DefaultLimit, odd.BlankLimit);
        Assert.Equal(19200, (Office with { Dpi = 1_000_000 }).Normalized().Dpi);
        Assert.Equal(BlankPages.MaxLimit, (Office with { BlankLimit = 50 }).Normalized().BlankLimit);
    }

    [Fact]
    public void ExportImport_RoundTrip()
    {
        using var file = new MemoryStream();
        ScanProfileStore.Export([Office, Office with { Name = "Zwei" }], file);
        var text = Encoding.UTF8.GetString(file.ToArray());
        Assert.Contains(ScanProfileStore.ExportFormat, text);
        Assert.Contains("\"Gray\"", text);   // readable names, not numbers
        file.Position = 0;
        var imported = ScanProfileStore.Import(file);
        Assert.Equal([Office, Office with { Name = "Zwei" }], imported);
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("{\"Format\":\"anderes\",\"Version\":1,\"Profiles\":[]}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Import_ForeignFile_Refused(string content) =>
        Assert.Throws<InvalidDataException>(() => ScanProfileStore.Import(new MemoryStream(Encoding.UTF8.GetBytes(content))));

    [Fact]
    public void Import_LimitsAgainstPreparedFiles()
    {
        // newer version: told so
        var newer = Assert.Throws<InvalidDataException>(() => Import($"{{\"Format\":\"{ScanProfileStore.ExportFormat}\",\"Version\":99,\"Profiles\":[]}}"));
        Assert.Contains(Tr.T("neueren Version"), newer.Message);
        // too many profiles
        var many = string.Join(",", Enumerable.Range(0, ScanProfileStore.MaxProfiles + 1).Select(i => $"{{\"Name\":\"P{i}\"}}"));
        Assert.Throws<InvalidDataException>(() => Import($"{{\"Format\":\"{ScanProfileStore.ExportFormat}\",\"Version\":1,\"Profiles\":[{many}]}}"));
        // too large (read no further than the limit)
        Assert.Throws<InvalidDataException>(() => ScanProfileStore.Import(new MemoryStream(new byte[ScanProfileStore.MaxFileBytes + 1])));
        // values out of range, unknown entries, profiles without a name, null entries
        var odd = Import($"{{\"Format\":\"{ScanProfileStore.ExportFormat}\",\"Version\":1,\"Unbekannt\":true,\"Profiles\":[" +
            "{\"Name\":\"A\",\"Dpi\":99999999,\"Mode\":\"Gray\",\"Zukunft\":[1]},{\"Name\":\"  \"},null,{\"Name\":\"B\",\"BlankLimit\":-3}]}");
        Assert.Equal(["A", "B"], odd.Select(p => p.Name));
        Assert.Equal(19200, odd[0].Dpi);
        Assert.Equal(BlankPages.MinLimit, odd[1].BlankLimit);
        // an unknown colour mode or a number where text belongs: not a profile file
        Assert.Throws<InvalidDataException>(() => Import($"{{\"Format\":\"{ScanProfileStore.ExportFormat}\",\"Version\":1,\"Profiles\":[{{\"Name\":\"A\",\"Mode\":\"Lila\"}}]}}"));
        Assert.Throws<InvalidDataException>(() => Import($"{{\"Format\":\"{ScanProfileStore.ExportFormat}\",\"Version\":1,\"Profiles\":[{{\"Name\":5}}]}}"));
    }

    /// <summary>Damaged export files (bytes flipped, cut, repeated): never anything but <see cref="InvalidDataException"/> or checked profiles.</summary>
    [Fact]
    public void Import_DamagedFiles_OnlyRefusedOrChecked()
    {
        using var file = new MemoryStream();
        ScanProfileStore.Export([Office, Office with { Name = "Zwei", Source = null, DeviceId = null }], file);
        var original = file.ToArray();
        var random = new Random(4711);
        for (var round = 0; round < 3000; round++)
        {
            var bytes = original.ToList();
            for (var change = random.Next(1, 6); change > 0; change--)
            {
                var at = random.Next(bytes.Count);
                switch (random.Next(4))
                {
                    case 0: bytes[at] = (byte)random.Next(256); break;
                    case 1: bytes.RemoveRange(at, Math.Min(random.Next(1, 20), bytes.Count - at)); break;
                    case 2: bytes.InsertRange(at, bytes.Skip(at).Take(random.Next(1, 40)).ToList()); break;
                    default: bytes.Insert(at, (byte)"{}[]\\\",:0e-9u"[random.Next(13)]); break;
                }
                if (bytes.Count == 0) break;
            }
            try
            {
                foreach (var profile in ScanProfileStore.Import(new MemoryStream([.. bytes])))
                {
                    Assert.Equal(profile, profile.Normalized());
                    Assert.InRange(profile.Name.Length, 1, ScanProfile.MaxName);
                }
            }
            catch (InvalidDataException)
            {
                // refused: fine
            }
        }
    }

    private static IReadOnlyList<ScanProfile> Import(string json) => ScanProfileStore.Import(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Merge_NeverReplaces_SameProfileLeftOut()
    {
        var existing = new[] { Office, Office with { Name = "Fotos" } };
        var merged = ScanProfileStore.Merge(existing,
        [
            Office,                                   // the same: left out
            Office with { Dpi = 600 },                // same name, other values: a free name
            Office with { Name = "Büro A4 (2)", Dpi = 75 },   // that name is then taken: counted on from its number
            Office with { Name = "Büro A4 (5)" },     // the same values as one there under a numbered name: left out
            Office with { Name = "Neu" },
        ]);
        Assert.Equal(["Büro A4 (2)", "Büro A4 (3)", "Neu"], merged.Select(p => p.Name));
        Assert.Equal(600, merged[0].Dpi);
        // importing the same file again after the first import brings nothing new
        Assert.Empty(ScanProfileStore.Merge([.. existing, .. merged], [Office with { Dpi = 600 }, Office with { Name = "Neu" }]));
        var longName = new string('n', ScanProfile.MaxName);
        Assert.Equal(ScanProfile.MaxName, ScanProfileStore.Merge([Office with { Name = longName }], [Office with { Name = longName, Dpi = 75 }])[0].Name.Length);
    }

    [Fact]
    public void FindDevice_ByIdThenByName()
    {
        ScanDevice[] devices =
        [
            new("escl:https://192.0.2.7:443", "eSCL", "Muster Scanner", "flatbed scanner", "escl"),
            new("airscan:e2:Muster", "eSCL", "Muster Scanner", "flatbed scanner", "airscan"),
        ];
        Assert.Same(devices[1], (Office with { DeviceId = "airscan:e2:Muster" }).FindDevice(devices));
        // the id has changed (another computer): the same name reached the same way
        Assert.Same(devices[1], Office.FindDevice(devices));
        Assert.Same(devices[0], (Office with { DeviceId = "escl:https://192.0.2.99:443" }).FindDevice(devices));
        Assert.Null((Office with { DeviceId = "x:1", DeviceName = "Anderer" }).FindDevice(devices));
    }

    [Fact]
    public void FindDevice_SavedWithAWayThatIsNowAFurtherOne()
    {
        // a profile from before the program chose the driver (or from a computer where another way was the best): the one entry of the device
        ScanDevice[] devices =
        [
            new("airscan:e0:Muster", "eSCL", "Muster Scanner", "flatbed scanner", null, "escl:https://192.0.2.7:443"),
            new("usb:1", "Muster", "Scan 1", "flatbed scanner"),
        ];
        Assert.Same(devices[0], (Office with { DeviceId = "escl:https://192.0.2.7:443", DeviceName = "Anderer Name" }).FindDevice(devices));
        Assert.Same(devices[0], (Office with { DeviceId = "airscan:e0:Muster" }).FindDevice(devices));
        // the address of the further way has changed: the back end of a further way and the name still find it
        Assert.Same(devices[0], (Office with { DeviceId = "escl:https://192.0.2.99:443" }).FindDevice(devices));
    }

    // ---------------------------------------------------------------- blank pages

    private static ScannedImage Page(byte paper, Action<byte[], int, int>? draw = null, int w = 850, int h = 1100)
    {
        var pixels = new byte[w * h];
        Array.Fill(pixels, paper);
        draw?.Invoke(pixels, w, h);
        return new ScannedImage(w, h, 1, 100, pixels);
    }

    private static void Box(byte[] p, int w, int x0, int y0, int x1, int y1, byte value)
    {
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                p[y * w + x] = value;
    }

    [Fact]
    public void Blank_WhiteGreyAndSpeckledPagesBlank_TextNot()
    {
        Assert.True(BlankPages.IsBlank(Page(250)));
        Assert.True(BlankPages.IsBlank(Page(170)));   // grey recycled paper, scanner set dark
        // a page number and some dust
        Assert.True(BlankPages.IsBlank(Page(245, (p, w, _) =>
        {
            Box(p, w, 420, 1020, 430, 1040, 20);
            for (var i = 0; i < 40; i++) p[(100 + i * 20) * w + 200 + i * 9] = 30;
        })));
        // shining through from the back: lighter than ink
        Assert.True(BlankPages.IsBlank(Page(245, (p, w, _) => Box(p, w, 100, 100, 750, 900, 200))));
        // dark edges of the glass and the lid
        Assert.True(BlankPages.IsBlank(Page(245, (p, w, h) =>
        {
            Box(p, w, 0, 0, w, 15, 0);
            Box(p, w, 0, 0, 12, h, 0);
        })));
        // three lines of text are content
        var text = Page(245, (p, w, _) =>
        {
            for (var line = 0; line < 3; line++)
                for (var c = 0; c < 60; c++) Box(p, w, 100 + c * 10, 200 + line * 30, 106 + c * 10, 214 + line * 30, 30);
        });
        Assert.False(BlankPages.IsBlank(text));
        Assert.InRange(BlankPages.Coverage(text), 0.5, 2);
        // a dark page is never blank
        Assert.False(BlankPages.IsBlank(Page(10)));
        Assert.Equal(100, BlankPages.Coverage(new ScannedImage(0, 0, 1, 100, [])));
    }

    [Fact]
    public void Blank_LimitDecides_ColourPages()
    {
        // a page with about half a percent of ink: blank only with a limit above that
        var page = Page(240, (p, w, _) => Box(p, w, 300, 400, 368, 470, 40));
        var coverage = BlankPages.Coverage(page);
        Assert.InRange(coverage, 0.4, 0.6);
        Assert.False(BlankPages.IsBlank(page));
        Assert.True(BlankPages.IsBlank(page, 1.0));

        // colour: a red stamp counts as ink by its brightness
        var (w, h) = (400, 500);
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)245);
        for (var y = 100; y < 160; y++)
            for (var x = 100; x < 300; x++)
                (rgb[(y * w + x) * 3], rgb[(y * w + x) * 3 + 1], rgb[(y * w + x) * 3 + 2]) = (200, 20, 20);
        Assert.False(BlankPages.IsBlank(new ScannedImage(w, h, 3, 50, rgb)));
    }

    // ---------------------------------------------------------------- both sides

    [Fact]
    public void Interleave_BacksInReverse()
    {
        Assert.Equal(["v1", "r1", "v2", "r2", "v3", "r3"], ManualDuplexOrder.Interleave(["v1", "v2", "v3"], ["r3", "r2", "r1"]));
        // stopped after two backs: they belong to the last two sheets
        Assert.Equal(["v1", "v2", "r2", "v3", "r3"], ManualDuplexOrder.Interleave(["v1", "v2", "v3"], ["r3", "r2"]));
        // more backs than fronts: the rest at the end
        Assert.Equal(["v1", "r1", "x"], ManualDuplexOrder.Interleave(["v1"], ["r1", "x"]));
        Assert.Equal(["r1"], ManualDuplexOrder.Interleave([], ["r1"]));
        Assert.Equal(["v1"], ManualDuplexOrder.Interleave(["v1"], Array.Empty<string>()));
    }

    [Fact]
    public void Interleave_BacksInTheOrderOfTheSheets()
    {
        // a feeder that lays the sheets face up: turned over, the first sheet's back comes first
        Assert.Equal(["v1", "r1", "v2", "r2", "v3", "r3"], ManualDuplexOrder.Interleave(["v1", "v2", "v3"], ["r1", "r2", "r3"], lastFirst: false));
        // stopped after two backs: they belong to the first two sheets
        Assert.Equal(["v1", "r1", "v2", "r2", "v3"], ManualDuplexOrder.Interleave(["v1", "v2", "v3"], ["r1", "r2"], lastFirst: false));
        // four fronts, three backs read: with the last first they belong to the last three sheets, otherwise to the first three
        Assert.Equal([-1, 2, 1, 0], Enumerable.Range(0, 4).Select(i => ManualDuplexOrder.BackOf(i, 4, 3)));
        Assert.Equal([0, 1, 2, -1], Enumerable.Range(0, 4).Select(i => ManualDuplexOrder.BackOf(i, 4, 3, lastFirst: false)));
    }

    [Fact]
    public void PositionCorrection_InTheProfile_OldProfilesWithout()
    {
        // „Lage-Korrektur“ of both sides by turning is kept in the profile; a profile written before it has neither (both off)
        var store = new ScanProfileStore(_folder);
        store.Save(Office with { Name = "Wenden", ManualDuplex = true, BacksTurned = true, BacksInFeedOrder = true });
        var read = Assert.Single(store.Load(), p => p.Name == "Wenden");
        Assert.True(read.BacksTurned && read.BacksInFeedOrder);
        File.WriteAllText(Path.Combine(_folder, "alt.json"), """{"Name":"Alt","Dpi":200,"ManualDuplex":true,"Version":1}""");
        var old = Assert.Single(store.Load(), p => p.Name == "Alt");
        Assert.False(old.BacksTurned || old.BacksInFeedOrder);
        Assert.True(old.ManualDuplex);
    }
}
