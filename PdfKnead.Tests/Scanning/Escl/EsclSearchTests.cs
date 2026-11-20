using System.Buffers.Binary;
using System.Net;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>
/// The search for eSCL scanners (KONZEPT.md §5.25, S9a) without a network: the own-network rule, the DNS reader against malformed and hostile
/// answers, the linking of answers to devices, the typed address and a fuzzing run over mutated answers. All names and addresses invented.
/// </summary>
public sealed class EsclSearchTests
{
    private const string Instance = "Muster Scanner 3000._uscan._tcp.local";
    private const string SecureInstance = "Muster Scanner 3000._uscans._tcp.local";
    private const string Uuid = "6f1d2c3b-4a59-4e6f-8a7b-9c0d1e2f3a4b";
    private static readonly IPAddress Sender = IPAddress.Parse("192.168.10.20");

    private static byte[] FullAnswer(string instance = Instance, string host = "muster.local", int port = 80, string address = "192.168.10.20",
        string service = EsclSearch.Plain) =>
        new DnsAnswer().Ptr(service, instance).Srv(instance, host, port)
            .Txt(instance, 120, "txtvers=1", "ty=Muster Scanner 3000", "rs=eSCL", "uuid=" + Uuid, "is=platen,adf", "duplex=F")
            .A(host, address).Build();

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.5", true)]
    [InlineData("169.254.12.7", true)]
    [InlineData("169.254.0.7", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void OwnNetwork_OnlyPrivateLinkLocalAndThisComputer(string address, bool local) =>
        Assert.Equal(local, EsclNetwork.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public async Task OwnNetwork_NamesOutsideRefused()
    {
        Assert.Equal([IPAddress.Parse("192.168.1.7")], await EsclNetwork.ResolveLocalAsync("192.168.1.7"));
        Assert.Equal([IPAddress.Parse("fe80::1")], await EsclNetwork.ResolveLocalAsync("[fe80::1]"));
        var outside = await Assert.ThrowsAnyAsync<ScanException>(() => EsclNetwork.ResolveLocalAsync("8.8.4.4"));
        Assert.Contains("nicht im eigenen Netzwerk", outside.Message);
        Assert.True((await EsclNetwork.ResolveLocalAsync("localhost")).All(IPAddress.IsLoopback));
    }

    [Fact]
    public void Query_WrittenAsNameAndTypePairs_DottedInstanceStaysOneLabel()
    {
        var query = DnsMessage.Query(0xBEEF, [("Scanner 2\\.OG._uscan._tcp.local", DnsType.Srv)]);
        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(query));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2)));   // a question
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(12, query[12]);   // „Scanner 2.OG“ as one label
        Assert.Equal((byte)'.', query[12 + 10]);
        Assert.Throws<ArgumentException>(() => DnsMessage.Query(1, [(new string('x', 64) + ".local", DnsType.A)]));
        Assert.Throws<ArgumentException>(() => DnsMessage.Query(1, [(string.Join('.', Enumerable.Repeat(new string('y', 60), 5)), DnsType.A)]));
    }

    [Fact]
    public void Read_RecordsOfAllTypes_WithEscapedDotsInLabels()
    {
        var read = DnsMessage.Read(new DnsAnswer().Ptr(EsclSearch.Plain, "Scanner 2\\.OG._uscan._tcp.local")
            .Srv("Scanner 2\\.OG._uscan._tcp.local", "s.local", 8080).Txt("Scanner 2\\.OG._uscan._tcp.local", 10, "a=1", "b")
            .A("s.local", "192.168.1.9").Aaaa("s.local", "fe80::9").Build());
        Assert.NotNull(read);
        var records = read.Value.Records;
        Assert.Equal("Scanner 2\\.OG._uscan._tcp.local", records[0].Target);
        Assert.Equal(("s.local", 8080), (records[1].Target, records[1].Port));
        Assert.Equal(["a=1", "b"], records[2].Texts!);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), records[3].Address);
        Assert.Equal(IPAddress.Parse("fe80::9"), records[4].Address);
    }

    [Fact]
    public void Read_MalformedAndHostileMessages_DroppedWhole()
    {
        var good = FullAnswer();
        Assert.NotNull(DnsMessage.Read(good));
        Assert.Null(DnsMessage.Read(good.AsSpan(0, 11)));                       // shorter than a header
        Assert.Null(DnsMessage.Read(new DnsAnswer().Ptr(EsclSearch.Plain, Instance).Build(flags: 0)));   // a question mirrored back
        Assert.Null(DnsMessage.Read(new DnsAnswer().Ptr(EsclSearch.Plain, Instance).Build(flags: 0x8403)));   // error code
        Assert.Null(DnsMessage.Read(good.AsSpan(0, good.Length - 3)));           // cut in the last record
        Assert.Null(DnsMessage.Read(new byte[DnsMessage.MaxMessage + 1]));       // larger than any multicast DNS message

        // a pointer to itself, a pointer forwards, a reserved label kind, a label longer than 63, a name longer than 255
        Assert.NotNull(DnsMessage.Read(Message([1, (byte)'a', 0])));                // the frame itself is valid
        Assert.Null(DnsMessage.Read(Message([0xC0, 12])));
        Assert.Null(DnsMessage.Read(Message([0xC0, 40, .. new byte[40]])));
        Assert.Null(DnsMessage.Read(Message([0x41, (byte)'a', 0])));
        Assert.Null(DnsMessage.Read(Message([64, .. Enumerable.Repeat((byte)'a', 64), 0])));
        Assert.Null(DnsMessage.Read(Message([.. Enumerable.Range(0, 5).SelectMany(_ => (byte[])[60, .. Enumerable.Repeat((byte)'b', 60)]), 0])));

        // a chain of backward pointers: each question's name points to the previous one — allowed up to the limit, refused beyond
        Assert.NotNull(DnsMessage.Read(PointerChain(DnsMessage.MaxPointers)));
        Assert.Null(DnsMessage.Read(PointerChain(DnsMessage.MaxPointers + 1)));

        // more records announced than allowed; a record whose length runs past the end; SRV with port 0; A with 5 bytes
        var many = FullAnswer();
        BinaryPrimitives.WriteUInt16BigEndian(many.AsSpan(6), DnsMessage.MaxRecords + 1);
        Assert.Null(DnsMessage.Read(many));
        Assert.Null(DnsMessage.Read(new DnsAnswer().Add("x.local", DnsType.Txt, 1, [200, 1, 2]).Build()));
        Assert.Null(DnsMessage.Read(new DnsAnswer().Srv(Instance, "h.local", 0).Build()));
        Assert.Null(DnsMessage.Read(new DnsAnswer().Add("h.local", DnsType.A, 1, [192, 168, 1, 1, 1]).Build()));
    }

    /// <summary>
    /// An answer whose questions form a pointer chain: the first names „x“, every further one points to the name of the one before, and the
    /// answer record's owner name points to the last — reading it follows <paramref name="length"/> pointers.
    /// </summary>
    private static byte[] PointerChain(int length)
    {
        var bytes = new List<byte>(new byte[12]) { 1, (byte)'x', 0, 0, 1, 0, 1 };
        var previous = 12;
        for (var i = 1; i < length; i++)
        {
            var at = bytes.Count;
            bytes.AddRange((byte[])[(byte)(0xC0 | previous >> 8), (byte)previous, 0, 1, 0, 1]);
            previous = at;
        }
        bytes.AddRange((byte[])[(byte)(0xC0 | previous >> 8), (byte)previous, 0, 99, 0, 1, 0, 0, 0, 120, 0, 0]);
        var message = bytes.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), 1);
        return message;
    }

    /// <summary>One answer record with <paramref name="name"/> as owner name (raw bytes, may be hostile), a type the search ignores, no data.</summary>
    private static byte[] Message(byte[] name)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);
        return [.. header, .. name, 0, 99, 0, 1, 0, 0, 0, 120, 0, 0];   // a type the search does not read, no data
    }

    [Fact]
    public void Search_LinksByName_SenderFirst_UuidJoinsTlsAndPlain()
    {
        var search = new EsclSearch();
        Assert.True(search.Add(FullAnswer(), Sender));
        // the same device announces its TLS service from another address of the same host
        Assert.True(search.Add(FullAnswer(SecureInstance, port: 443, address: "192.168.10.21", service: EsclSearch.Secure), IPAddress.Parse("192.168.10.21")));
        var device = Assert.Single(search.Devices());
        Assert.Equal(Uuid, device.Key);
        Assert.Equal("Muster Scanner 3000", device.Name);
        Assert.Equal([true, false], device.Services.Select(s => s.Tls));
        var plain = device.Services[1];
        Assert.Equal(("muster.local", 80, "eSCL"), (plain.Host, plain.Port, plain.Root));
        Assert.Equal(Sender, plain.Addresses[0]);                 // the address the answer came from comes first
        Assert.Contains(IPAddress.Parse("192.168.10.21"), plain.Addresses);   // the host's further address follows
        Assert.Equal("platen,adf", plain.Texts["is"]);
    }

    [Fact]
    public void Search_OutsideOwnNetworkIgnored_WithdrawnForgotten_OpenQuestionsAsked()
    {
        var search = new EsclSearch();
        Assert.False(search.Add(FullAnswer(), IPAddress.Parse("203.0.113.5")));   // an answer from outside
        Assert.Empty(search.Devices());

        // an instance without location, texts and addresses: those are asked for next
        search.Add(new DnsAnswer().Ptr(EsclSearch.Plain, Instance).Build(), Sender);
        Assert.Equal([(Instance, DnsType.Srv), (Instance, DnsType.Txt)], search.OpenQuestions());
        search.Add(new DnsAnswer().Srv(Instance, "muster.local", 80).Build(), Sender);
        Assert.Contains(("muster.local", DnsType.A), search.OpenQuestions());

        // a device naming an address outside: that address is never used; the sender stays
        search.Add(new DnsAnswer().A("muster.local", "8.8.8.8").Build(), Sender);
        Assert.Equal([Sender], Assert.Single(search.Devices()).Services[0].Addresses);

        // the device says goodbye (time to live 0)
        search.Add(new DnsAnswer().Ptr(EsclSearch.Plain, Instance, ttl: 0).Build(), Sender);
        Assert.Empty(search.Devices());
    }

    [Fact]
    public void Search_LinkLocalIpv6FromIpv4Sender_NoZoneNoCrash()
    {
        // a device answering over IPv4 names a link-local IPv6 address of its host: the address keeps no zone (an IPv4 sender has none),
        // and reading the zone of the IPv4 sender must not throw (Fehlergedächtnis 950)
        var search = new EsclSearch();
        Assert.True(search.Add(new DnsAnswer().Ptr(EsclSearch.Plain, Instance).Srv(Instance, "h.local", 80).Aaaa("h.local", "fe80::7").Build(), Sender));
        Assert.Equal([Sender, IPAddress.Parse("fe80::7")], Assert.Single(search.Devices()).Services[0].Addresses);

        // over IPv6 the zone of the receiving interface is given to the link-local address
        var v6 = new EsclSearch();
        var senderV6 = IPAddress.Parse("fe80::99%3");
        v6.Add(new DnsAnswer().Ptr(EsclSearch.Plain, Instance).Srv(Instance, "h.local", 80).Aaaa("h.local", "fe80::7").Build(), senderV6);
        Assert.Contains(v6.Devices()[0].Services[0].Addresses, a => a.ToString() == "fe80::7%3");
    }

    [Fact]
    public void Search_LimitsAndUnsafeTexts()
    {
        var search = new EsclSearch();
        for (var i = 0; i < EsclSearch.MaxInstances + 20; i++)
            search.Add(FullAnswer($"Gerät {i}._uscan._tcp.local", $"g{i}.local").Replace(Uuid, Guid.NewGuid().ToString()), Sender);
        Assert.Equal(EsclSearch.MaxInstances, search.Devices().Count);

        // a resource path that would lead elsewhere falls back to the default; an invalid UUID is no identity
        var other = new EsclSearch();
        other.Add(new DnsAnswer().Ptr(EsclSearch.Plain, Instance).Srv(Instance, "h.local", 80)
            .Txt(Instance, 120, "rs=../../admin", "uuid=kein-uuid", "ty=").A("h.local", "192.168.10.30").Build(), Sender);
        var service = Assert.Single(other.Devices()).Services[0];
        Assert.Equal("eSCL", service.Root);
        Assert.Null(service.Uuid);
        Assert.Equal("Muster Scanner 3000", service.Name);   // no „ty“: the first label of the instance
        Assert.Equal("ipp/scan", EsclSearch.SafeRoot("/ipp/scan/"));
        Assert.Null(EsclSearch.SafeRoot("a//b"));
        Assert.Equal(Uuid, EsclSearch.NormalizeUuid("urn:uuid:" + Uuid.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", null, null, null)]
    [InlineData("scanner.local:8080", "scanner.local", 8080, null, null)]
    [InlineData("[fe80::1]:443", "fe80::1", 443, null, null)]
    [InlineData("fd00::7", "fd00::7", null, null, null)]
    [InlineData("https://192.168.1.20/eSCL", "192.168.1.20", null, true, "eSCL")]
    [InlineData("http://drucker:9095", "drucker", 9095, false, null)]
    public void Address_TypedForms(string input, string host, int? port, bool? tls, string? root) =>
        Assert.Equal(new EsclAddress(host, port, tls, root), EsclAddress.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://192.168.1.2")]
    [InlineData("http://user:pw@192.168.1.2")]
    [InlineData("http://192.168.1.2/eSCL?x=1")]
    [InlineData("http://192.168.1.2/a%2F..%2Fb")]
    [InlineData("scanner.local:0")]
    [InlineData("scanner.local:70000")]
    [InlineData("scan ner")]
    [InlineData("[zz::1]")]
    public void Address_InvalidRefused(string input) => Assert.Null(EsclAddress.Parse(input));

    [Fact]
    public void Fuzz_MutatedAnswers_NeverThrow_StayWithinLimits()
    {
        var random = new Random(4711);
        var seeds = new[] { FullAnswer(), FullAnswer(SecureInstance, port: 443, service: EsclSearch.Secure),
            new DnsAnswer().Ptr(EsclSearch.Plain, "Scanner 2\\.OG._uscan._tcp.local").Aaaa("x.local", "fe80::5").Build() };
        var search = new EsclSearch();
        for (var round = 0; round < 20_000; round++)
        {
            var bytes = (byte[])seeds[round % seeds.Length].Clone();
            var changes = 1 + random.Next(8);
            for (var c = 0; c < changes; c++)
            {
                switch (random.Next(4))
                {
                    case 0: bytes[random.Next(bytes.Length)] = (byte)random.Next(256); break;
                    case 1: bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8)); break;
                    case 2: bytes = bytes[..Math.Max(1, random.Next(bytes.Length))]; break;
                    default: bytes = [.. bytes, .. Enumerable.Range(0, random.Next(16)).Select(_ => (byte)random.Next(256))]; break;
                }
            }
            DnsMessage.Read(bytes);
            search.Add(bytes, Sender);
        }
        var devices = search.Devices();
        Assert.True(devices.Sum(d => d.Services.Count) <= EsclSearch.MaxInstances);
        Assert.All(devices.SelectMany(d => d.Services), s => Assert.All(s.Addresses, a => Assert.True(EsclNetwork.IsLocal(a))));
    }
}

file static class Bytes
{
    /// <summary>Replaces the first occurrence of an ASCII text in a message (the UUID in the texts of a test answer).</summary>
    public static byte[] Replace(this byte[] message, string old, string replacement)
    {
        var o = System.Text.Encoding.ASCII.GetBytes(old);
        var r = System.Text.Encoding.ASCII.GetBytes(replacement);
        var at = message.AsSpan().IndexOf(o);
        return at < 0 || o.Length != r.Length ? message : [.. message[..at], .. r, .. message[(at + o.Length)..]];
    }
}
