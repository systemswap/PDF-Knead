using System.Text;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>Reading the description and the state of an eSCL scanner (KONZEPT.md §5.25, S9b) — tolerant of devices, strict against hostile answers.</summary>
public sealed class EsclCapabilitiesTests
{
    private static EsclCapabilities Caps(string xml) => EsclCapabilities.Read(Encoding.UTF8.GetBytes(xml)) ?? throw new Xunit.Sdk.XunitException("nicht gelesen");

    [Fact]
    public void Sample_AllInputsAndValues()
    {
        var caps = Caps(EsclSamples.Capabilities);
        Assert.Equal("Beispiel Büro 1000", caps.MakeAndModel);
        Assert.Equal("11111111-2222-3333-4444-555555555555", caps.Uuid);
        Assert.Equal(50, caps.FeederCapacity);
        Assert.Contains("DetectPaperLoaded", caps.FeederOptions);
        Assert.Empty(caps.Unknown);
        Assert.Equal([EsclInput.Platen, EsclInput.Feeder, EsclInput.FeederDuplex], caps.Inputs.Keys.Order());
        var platen = caps.Inputs[EsclInput.Platen];
        Assert.Equal(2550, platen.MaxWidth);
        Assert.Equal(["application/pdf", "image/jpeg"], platen.Formats);
        var range = Assert.Single(Assert.Single(platen.Resolutions).Ranges);
        Assert.Equal(new EsclResolutionRange(75, 1200, 25), range);
        // the shared profile, resolved by name
        Assert.Equal([150, 300, 200], Assert.Single(caps.Inputs[EsclInput.Feeder].Resolutions).Values);
        Assert.Equal(3600, caps.Inputs[EsclInput.FeederDuplex].MaxHeight);
    }

    [Fact]
    public void ToScanCapabilities_SourcesResolutionsModesSize()
    {
        var caps = Caps(EsclSamples.Capabilities);
        var platen = caps.ToScanCapabilities(EsclInput.Platen);
        Assert.Equal([ScanSourceKind.Flatbed, ScanSourceKind.Feeder, ScanSourceKind.FeederDuplex], platen.Sources.Select(s => s.Kind));
        Assert.Equal("Platen", platen.Source?.Name);
        Assert.Equal([75, 100, 150, 200, 300, 400, 600, 1200], platen.Resolutions);   // the usual values on the 25-dpi grid
        // black and white comes as JPEG or PDF only: grey from the device, the threshold in the program
        Assert.Equal([ScanColorMode.Color, ScanColorMode.Gray], platen.ColorModes);
        Assert.Equal(215.9, platen.MaxWidthMm, 1);
        Assert.Equal(297.0, platen.MaxHeightMm, 1);
        var duplex = caps.ToScanCapabilities(EsclInput.FeederDuplex);
        Assert.Equal("Feeder" + ScanSource.DuplexMarker, duplex.Source?.Name);
        Assert.Equal([150, 200, 300], duplex.Resolutions);
        Assert.Equal(EsclInput.FeederDuplex, EsclCapabilities.InputOf(duplex.Source?.Name));
    }

    [Fact]
    public void BlackWhite_OnlyWithLosslessFormat()
    {
        var xml = EsclSamples.Capabilities.Replace("<pwg:DocumentFormat>application/pdf</pwg:DocumentFormat>",
            "<pwg:DocumentFormat>image/png</pwg:DocumentFormat>");
        Assert.Contains(ScanColorMode.BlackWhite, Caps(xml).ToScanCapabilities(EsclInput.Platen).ColorModes);
    }

    [Fact]
    public void MissingInput_FallsBackToFirst()
    {
        var xml = Body("<scan:Adf><scan:AdfSimplexInputCaps><scan:MaxWidth>2550</scan:MaxWidth></scan:AdfSimplexInputCaps></scan:Adf>");
        var sc = Caps(xml).ToScanCapabilities(EsclInput.Platen);
        Assert.Equal(ScanSourceKind.Feeder, sc.Source?.Kind);
        Assert.Empty(new EsclCapabilities(null, null, null, null, new Dictionary<EsclInput, EsclInputCaps>(), 0, [], []).ToScanCapabilities(null).Sources);
    }

    private static string Body(string inner, string root = "ScannerCapabilities") =>
        $"<scan:{root} xmlns:scan=\"{EsclSamples.Scan}\" xmlns:pwg=\"{EsclSamples.Pwg}\">{inner}</scan:{root}>";

    [Fact]
    public void Resolutions_PerColorMode()
    {
        var xml = Body("""
            <scan:Platen><scan:PlatenInputCaps><scan:SettingProfiles><scan:SettingProfile>
              <scan:ColorModes><scan:ColorMode>RGB24</scan:ColorMode><scan:ColorMode>Grayscale8</scan:ColorMode></scan:ColorModes>
              <scan:SupportedResolutions><scan:ColorMode>RGB24</scan:ColorMode><scan:DiscreteResolutions>
                <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution><scan:YResolution>300</scan:YResolution></scan:DiscreteResolution>
              </scan:DiscreteResolutions></scan:SupportedResolutions>
              <scan:SupportedResolutions><scan:ColorMode>Grayscale8</scan:ColorMode><scan:DiscreteResolutions>
                <scan:DiscreteResolution><scan:XResolution>600</scan:XResolution><scan:YResolution>600</scan:YResolution></scan:DiscreteResolution>
                <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution><scan:YResolution>600</scan:YResolution></scan:DiscreteResolution>
              </scan:DiscreteResolutions></scan:SupportedResolutions>
            </scan:SettingProfile></scan:SettingProfiles></scan:PlatenInputCaps></scan:Platen>
            """);
        var platen = Caps(xml).Inputs[EsclInput.Platen];
        Assert.Equal([300], Assert.Single(platen.ResolutionsFor("RGB24")).Values);
        Assert.Equal([600], Assert.Single(platen.ResolutionsFor("grayscale8")).Values);   // 300 × 600 does not count
        Assert.Equal([300, 600], Caps(xml).ToScanCapabilities(EsclInput.Platen).Resolutions);
        Assert.True(Assert.Single(platen.ResolutionsFor("Grayscale8")).Allows(600));
    }

    [Fact]
    public void Tolerant_PrefixesNamespacesCaseAndWrappers()
    {
        // other prefixes, the namespaces swapped, no namespace at all, other case, resolutions without their wrapper, profile directly in the input
        var xml = """
            <a:scannercapabilities xmlns:a="http://www.pwg.org/schemas/2010/12/sm" xmlns:b="urn:other">
              <b:makeandmodel> Gerät </b:makeandmodel>
              <Platen><PlatenInputCaps>
                <MaxWidth>2550.0</MaxWidth><MaxHeight> 3508 </MaxHeight>
                <SettingProfile>
                  <ColorModes><ColorMode>rgb24</ColorMode><ColorMode>RGB48</ColorMode><ColorMode>Infrared8</ColorMode></ColorModes>
                  <DiscreteResolutions><DiscreteResolution><XResolution>300</XResolution></DiscreteResolution></DiscreteResolutions>
                </SettingProfile>
              </PlatenInputCaps></Platen>
            </a:scannercapabilities>
            """;
        var caps = Caps(xml);
        Assert.Equal("Gerät", caps.MakeAndModel);
        var platen = caps.Inputs[EsclInput.Platen];
        Assert.Equal(2550, platen.MaxWidth);
        Assert.Equal(3508, platen.MaxHeight);
        Assert.Equal([300], Assert.Single(platen.Resolutions).Values);
        Assert.Equal([ScanColorMode.Color], caps.ToScanCapabilities(null).ColorModes);
        Assert.Contains(caps.Unknown, u => u.Contains("Infrared8"));
    }

    [Fact]
    public void UnresolvedProfile_NamedAndSkipped()
    {
        var xml = Body("""
            <scan:Platen><scan:PlatenInputCaps><scan:MaxWidth>2550</scan:MaxWidth>
              <scan:SettingProfiles><scan:SettingProfile ref="fehlt"/></scan:SettingProfiles>
            </scan:PlatenInputCaps></scan:Platen>
            """);
        var caps = Caps(xml);
        Assert.Contains(caps.Unknown, u => u.Contains("fehlt"));
        Assert.Empty(caps.Inputs[EsclInput.Platen].ColorModes);
    }

    [Fact]
    public void Hostile_RejectedOrBounded()
    {
        // not a description, not XML, too large, entities (declared in a document type that is skipped — so they stay undefined)
        Assert.Null(EsclCapabilities.Read(Encoding.UTF8.GetBytes(EsclSamples.StatusIdle)));
        Assert.Null(EsclCapabilities.Read("<scan:ScannerCapabilities"u8));
        Assert.Null(EsclCapabilities.Read([]));
        Assert.Null(EsclCapabilities.Read(Encoding.UTF8.GetBytes(Body(new string(' ', EsclCapabilities.MaxBytes)))));
        var laughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lol [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <ScannerCapabilities><MakeAndModel>&c;</MakeAndModel></ScannerCapabilities>
            """;
        Assert.Null(EsclCapabilities.Read(Encoding.UTF8.GetBytes(laughs)));
        var external = """
            <?xml version="1.0"?>
            <!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/hostname">]>
            <ScannerCapabilities><MakeAndModel>&e;</MakeAndModel></ScannerCapabilities>
            """;
        Assert.Null(EsclCapabilities.Read(Encoding.UTF8.GetBytes(external)));
        // a document type without entities, as some devices send it, is tolerated
        Assert.Equal("Gerät", EsclCapabilities.Read(Encoding.UTF8.GetBytes(
            "<!doctype ScannerCapabilities><ScannerCapabilities><MakeAndModel>Gerät</MakeAndModel></ScannerCapabilities>"))?.MakeAndModel);
        // absurd values are left out
        var absurd = Body("""
            <scan:Platen><scan:PlatenInputCaps><scan:MaxWidth>99999999999</scan:MaxWidth><scan:MaxHeight>-5</scan:MaxHeight>
              <scan:SettingProfiles><scan:SettingProfile><scan:SupportedResolutions><scan:ResolutionRange>
                <scan:XResolutionRange><scan:Min>1</scan:Min><scan:Max>200000</scan:Max><scan:Step>-3</scan:Step></scan:XResolutionRange>
              </scan:ResolutionRange></scan:SupportedResolutions></scan:SettingProfile></scan:SettingProfiles>
            </scan:PlatenInputCaps></scan:Platen>
            """);
        var sc = Caps(absurd).ToScanCapabilities(null);
        Assert.Equal(0, sc.MaxWidthMm);
        Assert.Equal(0, sc.MaxHeightMm);
        Assert.Equal(ResolutionChoices.Common, sc.Resolutions);
    }

    [Fact]
    public void DeepNesting_Refused()
    {
        // within the size limit, but nested far deeper than any device answer: refused before a tree is built
        static byte[] Nested(int depth) => Encoding.UTF8.GetBytes("<ScannerCapabilities><MakeAndModel>" + string.Concat(Enumerable.Repeat("<a>", depth))
            + "x" + string.Concat(Enumerable.Repeat("</a>", depth)) + "</MakeAndModel></ScannerCapabilities>");
        Assert.Null(EsclCapabilities.Read(Nested(100_000)));
        Assert.Null(EsclCapabilities.Read(Nested(EsclXml.MaxDepth)));
        Assert.Equal("x", EsclCapabilities.Read(Nested(EsclXml.MaxDepth - 3))?.MakeAndModel);
    }

    [Theory]
    [InlineData("Büro\u202Ecba\u202C 1000", "Bürocba 1000")]          // a name reversed on screen
    [InlineData("  Etage\n2\t\u0007Links  ", "Etage 2 Links")]
    [InlineData("\u2066Gerät\u2069\u200E", "Gerät")]
    public void Names_Cleaned(string raw, string shown)
    {
        Assert.Equal(shown, EsclXml.Clean(raw));
        Assert.Equal(128, EsclXml.Clean(new string('x', 1000)).Length);
        var caps = EsclCapabilities.Read(Encoding.UTF8.GetBytes($"<ScannerCapabilities><MakeAndModel>{System.Security.SecurityElement.Escape(raw)}</MakeAndModel></ScannerCapabilities>"));
        // a character XML does not allow makes the whole answer unreadable — that is right too
        Assert.Equal(raw.All(System.Xml.XmlConvert.IsXmlChar) ? shown : null, caps?.MakeAndModel);
        var device = EsclBackend.ToScanDevice(new EsclDevice("u", raw, [new EsclService(DnsMessage.Escape(raw) + "._uscan._tcp.local", false, "h", 80,
            [System.Net.IPAddress.Parse("192.168.1.2")], new Dictionary<string, string>())]));
        Assert.Equal(shown, device.Model);
    }

    [Fact]
    public void ManyElements_Bounded()
    {
        var many = string.Concat(Enumerable.Range(1, 5000).Select(i =>
            $"<scan:DiscreteResolution><scan:XResolution>{i}</scan:XResolution></scan:DiscreteResolution>"));
        var modes = string.Concat(Enumerable.Range(1, 500).Select(i => $"<scan:ColorMode>Mode{i}</scan:ColorMode>"));
        var xml = Body($"<scan:Platen><scan:PlatenInputCaps><scan:SettingProfiles><scan:SettingProfile><scan:ColorModes>{modes}</scan:ColorModes>"
            + $"<scan:SupportedResolutions><scan:DiscreteResolutions>{many}</scan:DiscreteResolutions></scan:SupportedResolutions>"
            + "</scan:SettingProfile></scan:SettingProfiles></scan:PlatenInputCaps></scan:Platen>");
        var platen = Caps(xml).Inputs[EsclInput.Platen];
        Assert.True(Assert.Single(platen.Resolutions).Values.Count <= 256);
        Assert.True(platen.ColorModes.Count <= 32);
    }

    [Fact]
    public void Status_StatesFeederJobs()
    {
        var idle = EsclStatus.Read(Encoding.UTF8.GetBytes(EsclSamples.StatusIdle))!;
        Assert.Equal(EsclScannerState.Idle, idle.State);
        Assert.Equal(EsclFeederState.Loaded, idle.Feeder);
        Assert.Empty(idle.Jobs);
        var busy = EsclStatus.Read(Encoding.UTF8.GetBytes(EsclSamples.StatusWithJobs))!;
        Assert.Equal(EsclScannerState.Processing, busy.State);
        Assert.Equal(EsclFeederState.Processing, busy.Feeder);
        Assert.Equal(2, busy.Jobs.Count);
        // the job found by its last path segment, with or without the resource path, or as a full address
        var job = busy.Job("/eSCL/ScanJobs/aaaa0000-0000-4000-8000-000000000001")!;
        Assert.Equal(2, job.ImagesCompleted);
        Assert.Equal(1, job.ImagesToTransfer);
        Assert.Equal(EsclJobState.Processing, job.State);
        Assert.Equal(["JobScanning"], job.Reasons);
        var old = busy.Job("http://192.168.1.20:8080/eSCL/ScanJobs/bbbb0000-0000-4000-8000-000000000002/")!;
        Assert.Equal(EsclJobState.Canceled, old.State);
        Assert.Null(busy.Job("/eSCL/ScanJobs/cccc"));
        Assert.Null(busy.Job(""));
    }

    [Theory]
    [InlineData("ScannerAdfEmpty", EsclFeederState.Empty)]
    [InlineData("ScannerAdfJam", EsclFeederState.Jam)]
    [InlineData("scanneradfhatchopen", EsclFeederState.HatchOpen)]
    [InlineData("ScannerAdfMispick", EsclFeederState.Mispick)]
    [InlineData("ScannerAdfMultipickDetected", EsclFeederState.MultipickDetected)]
    [InlineData("ScannerAdfDuplexPageTooLong", EsclFeederState.DuplexPageTooLong)]
    [InlineData("ScannerAdfInputTrayOverloaded", EsclFeederState.InputTrayOverloaded)]
    [InlineData("ScannerAdfSomethingNew", EsclFeederState.Unknown)]
    [InlineData("ScannerAdfLoaded,ScannerAdfEmpty", EsclFeederState.Unknown)]
    [InlineData("3", EsclFeederState.Unknown)]
    [InlineData("None", EsclFeederState.Unknown)]
    public void Status_FeederValues(string value, EsclFeederState expected)
    {
        var status = EsclStatus.Read(Encoding.UTF8.GetBytes(Body($"<pwg:State>Idle</pwg:State><scan:AdfState>{value}</scan:AdfState>", "ScannerStatus")))!;
        Assert.Equal(expected, status.Feeder);
        Assert.Equal(expected == EsclFeederState.Unknown, status.Unknown.Count > 0);
    }

    [Fact]
    public void Status_UnknownStatesNeverBlock()
    {
        var status = EsclStatus.Read(Encoding.UTF8.GetBytes(Body("<pwg:State>Sleeping</pwg:State>", "ScannerStatus")))!;
        Assert.Equal(EsclScannerState.Unknown, status.State);
        Assert.Equal(EsclFeederState.None, status.Feeder);
        Assert.Null(EsclStatus.Read(Encoding.UTF8.GetBytes(EsclSamples.Capabilities)));
        Assert.Null(EsclStatus.Read(Encoding.UTF8.GetBytes(Body(new string(' ', EsclStatus.MaxBytes), "ScannerStatus"))));
    }

    [Fact]
    public void Fuzz_NeverThrows()
    {
        // changed bytes, cut answers, repeated pieces: reading returns a result or null, never an exception
        var random = new Random(9502);
        var samples = new[] { EsclSamples.Capabilities, EsclSamples.StatusIdle, EsclSamples.StatusWithJobs }.Select(Encoding.UTF8.GetBytes).ToArray();
        for (var round = 0; round < 3000; round++)
        {
            var data = (byte[])samples[round % samples.Length].Clone();
            switch (random.Next(4))
            {
                case 0:
                    for (var k = random.Next(1, 8); k > 0; k--) data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    data = data[..random.Next(data.Length)];
                    break;
                case 2:
                    var at = random.Next(data.Length);
                    var len = random.Next(1, Math.Min(200, data.Length - at) + 1);
                    data = [.. data[..at], .. data[at..(at + len)], .. data[at..(at + len)], .. data[at..]];
                    break;
                default:
                    var text = Encoding.UTF8.GetString(data);
                    var digits = text.Select((c, i) => (c, i)).Where(p => char.IsAsciiDigit(p.c)).Select(p => p.i).ToList();
                    if (digits.Count > 0)
                    {
                        var i = digits[random.Next(digits.Count)];
                        text = text[..i] + random.Next(-1000000, int.MaxValue) + text[(i + 1)..];
                    }
                    data = Encoding.UTF8.GetBytes(text);
                    break;
            }
            if (EsclCapabilities.Read(data) is { } caps)
                foreach (var input in Enum.GetValues<EsclInput>()) _ = caps.ToScanCapabilities(input);
            _ = EsclStatus.Read(data)?.Job("/eSCL/ScanJobs/x");
        }
    }
}
