using System.Buffers.Binary;
using System.Net;
using System.Text;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>
/// The host names of scanners found by „Netz durchsuchen“ (docs/FEHLERGEDAECHTNIS.md, No. 926, design decision): NetBIOS node status after
/// RFC 1002, 4.2.17 and 4.2.18, the cleaning of names from the network, and the bounded lookup of several addresses. No request leaves the
/// test: the lookup gets a fixed table.
/// </summary>
public sealed class EsclHostNamesTests
{
    /// <summary>A node status answer: the name of the answer as a pointer, then the name table and six bytes of statistics.</summary>
    private static byte[] Answer(ushort id, params (string Name, byte Suffix, bool Group)[] names)
    {
        var data = new List<byte> { (byte)names.Length };
        foreach (var (name, suffix, group) in names)
        {
            data.AddRange(Encoding.ASCII.GetBytes(name.PadRight(15)[..15]));
            data.Add(suffix);
            data.Add(group ? (byte)0x84 : (byte)0x04);
            data.Add(0);
        }
        data.AddRange([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        var packet = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8400);   // answer, authoritative, no error
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);        // one answer
        packet.AddRange(header);
        packet.AddRange([0xC0, 0x0C]);                                      // the name: a pointer
        var fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, 0x0021);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Count);
        packet.AddRange(fixedPart);
        packet.AddRange(data);
        return [.. packet];
    }

    [Fact]
    public void NetBiosRequest_AsRfc1002Asks()
    {
        var request = EsclHostNames.NetBiosRequest(0x1234);
        Assert.Equal(50, request.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4)));
        Assert.Equal(32, request[12]);
        Assert.Equal("CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Encoding.ASCII.GetString(request, 13, 32));   // „*“ and 15 zero bytes, half bytes as letters
        Assert.Equal(0, request[45]);
        Assert.Equal(0x0021, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(46)));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(48)));
    }

    [Fact]
    public void NetBiosName_UniqueNameOfTheDevice_NeverTheWorkgroup()
    {
        // the workgroup comes first with the same suffix as the workstation: it must not win
        var answer = Answer(7, ("BEISPIELGRUPPE", 0x00, true), ("PRN-2OG-01", 0x00, false), ("PRN-2OG-01", 0x03, false));
        Assert.Equal("PRN-2OG-01", EsclHostNames.NetBiosName(answer, 7));
        // the file server name wins over the workstation name
        Assert.Equal("DRUCKER-FS", EsclHostNames.NetBiosName(Answer(7, ("DRUCKER-WS", 0x00, false), ("DRUCKER-FS", 0x20, false)), 7));
        // only group names, or no names: none
        Assert.Null(EsclHostNames.NetBiosName(Answer(7, ("BEISPIELGRUPPE", 0x00, true)), 7));
        Assert.Null(EsclHostNames.NetBiosName(Answer(7), 7));
    }

    [Fact]
    public void NetBiosName_ForeignOrDamagedAnswers_None()
    {
        var good = Answer(7, ("PRN-2OG-01", 0x00, false));
        Assert.Null(EsclHostNames.NetBiosName(good, 8));                                       // another request
        var question = (byte[])good.Clone();
        question[2] = 0x00;                                                                    // not an answer
        Assert.Null(EsclHostNames.NetBiosName(question, 7));
        var error = (byte[])good.Clone();
        error[3] = 0x03;                                                                       // an error code
        Assert.Null(EsclHostNames.NetBiosName(error, 7));
        // every cut, every claimed length and count larger than the data: no exception, at most the complete entries
        for (var cut = 0; cut < good.Length; cut++)
        {
            var name = EsclHostNames.NetBiosName(good.AsSpan(0, cut), 7);
            Assert.True(name is null or "PRN-2OG-01");
        }
        var lying = (byte[])good.Clone();
        lying[12 + 2 + 8] = 0xFF;
        lying[12 + 2 + 9] = 0xFF;                                                              // data length 65 535
        lying[12 + 2 + 10] = 200;                                                              // 200 names
        Assert.Equal("PRN-2OG-01", EsclHostNames.NetBiosName(lying, 7));
        // a name made of labels that never ends, and a reserved label type
        var endless = new byte[12 + 200];
        good.AsSpan(0, 12).CopyTo(endless);
        for (var i = 12; i < endless.Length; i += 2) endless[i] = 1;
        Assert.Null(EsclHostNames.NetBiosName(endless, 7));
        var reserved = (byte[])good.Clone();
        reserved[12] = 0x80;
        Assert.Null(EsclHostNames.NetBiosName(reserved, 7));
    }

    [Theory]
    [InlineData("PRN-2OG-01.beispiel.local.", "PRN-2OG-01.beispiel.local")]
    [InlineData("  drucker_1 ", "drucker_1")]
    [InlineData("drucker\u202Eevil", null)]          // direction characters never reach the window
    [InlineData("drucker\nzwei", null)]
    [InlineData("drücker", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Clean_OnlyPlainHostNames(string? name, string? expected) => Assert.Equal(expected, EsclHostNames.Clean(name));

    [Fact]
    public void Clean_TooLong_None() => Assert.Null(EsclHostNames.Clean(new string('a', 254)));

    /// <summary>The list shows the host name without its domain (design decision: shorter, easier to read); an address is no host name.</summary>
    [Theory]
    [InlineData("PRN-2OG-01.beispiel.local.", "PRN-2OG-01")]
    [InlineData("PRN-2OG-01", "PRN-2OG-01")]
    [InlineData("10.242.66.21", null)]
    [InlineData(".beispiel.local", null)]
    [InlineData(null, null)]
    public void Short_WithoutTheDomain(string? name, string? expected) => Assert.Equal(expected, EsclHostNames.Short(name));

    /// <summary>„Hostname – Gerätetyp“ in the list of scanners; the name profiles compare (<see cref="Core.Scanning.ScanDevice.BaseName"/>) stays.</summary>
    [Fact]
    public void DeviceName_HostBeforeTheType()
    {
        var device = new Core.Scanning.ScanDevice("escl-direkt:1", "", "Beispiel Büro 1000", "eSCL 10.242.66.21", Host: "PRN-2OG-01");
        Assert.Equal("PRN-2OG-01 – Beispiel Büro 1000", device.DisplayName);
        Assert.Equal("Beispiel Büro 1000", device.BaseName);
        Assert.Equal("PRN-2OG-01 – Beispiel Büro 1000 (nicht erreichbar)", (device with { Via = "nicht erreichbar" }).DisplayName);
        Assert.Equal("Beispiel Büro 1000", (device with { Host = null }).DisplayName);
        Assert.Equal("Beispiel Büro 1000", (device with { Host = "beispiel büro 1000" }).DisplayName);   // never the same name twice
    }

    [Fact]
    public async Task NamesOf_OwnNetworkOnly_Bounded_FailuresLeaveTheAddressAlone()
    {
        var asked = new List<IPAddress>();
        var running = 0;
        var most = 0;
        var gate = new Lock();
        async Task<string?> Resolve(IPAddress address, CancellationToken cancel)
        {
            lock (gate)
            {
                asked.Add(address);
                most = Math.Max(most, ++running);
            }
            await Task.Delay(20, cancel);
            lock (gate) running--;
            return address.GetAddressBytes()[3] switch
            {
                1 => "drucker-1.beispiel.local.",
                2 => throw new System.Net.Sockets.SocketException(),
                3 => "kaputt\u0000name",
                _ => null,
            };
        }
        var addresses = Enumerable.Range(1, 40).Select(i => IPAddress.Parse($"10.242.66.{i}")).Append(IPAddress.Parse("8.8.8.8")).ToList();
        var names = await EsclHostNames.NamesOfAsync(addresses, Resolve);
        Assert.DoesNotContain(IPAddress.Parse("8.8.8.8"), asked);                               // only the own network is asked
        Assert.Equal(40, asked.Count);
        Assert.True(most <= EsclHostNames.Parallel, $"{most} zugleich");
        Assert.Equal("drucker-1.beispiel.local", Assert.Single(names).Value);                  // a failure or an unclean name: no name
    }

    [Fact]
    public async Task NameOf_OutsideTheOwnNetwork_NotAsked() => Assert.Null(await EsclHostNames.NameOfAsync(IPAddress.Parse("8.8.8.8")));
}
