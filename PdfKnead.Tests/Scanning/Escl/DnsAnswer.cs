using System.Buffers.Binary;
using System.Net;
using System.Text;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>Builds multicast DNS answers as devices send them — for the tests of the scanner search (names written in full, no compression).</summary>
internal sealed class DnsAnswer(ushort id = 0x1234)
{
    private readonly List<byte[]> _records = [];

    public DnsAnswer Ptr(string name, string target, uint ttl = 120) => Add(name, DnsType.Ptr, ttl, Name(target));

    public DnsAnswer Srv(string name, string host, int port, uint ttl = 120)
    {
        var data = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)port);
        return Add(name, DnsType.Srv, ttl, [.. data, .. Name(host)]);
    }

    public DnsAnswer Txt(string name, uint ttl = 120, params string[] texts) =>
        Add(name, DnsType.Txt, ttl, [.. texts.SelectMany(t => { var b = Encoding.UTF8.GetBytes(t); return (byte[])[(byte)b.Length, .. b]; })]);

    public DnsAnswer A(string name, string address, uint ttl = 120) => Add(name, DnsType.A, ttl, IPAddress.Parse(address).GetAddressBytes());

    public DnsAnswer Aaaa(string name, string address, uint ttl = 120) => Add(name, DnsType.Aaaa, ttl, IPAddress.Parse(address).GetAddressBytes());

    public DnsAnswer Add(string name, DnsType type, uint ttl, byte[] data)
    {
        var head = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(head, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), 0x8001);   // class IN with the cache-flush bit, as devices send it
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8), (ushort)data.Length);
        _records.Add([.. Name(name), .. head, .. data]);
        return this;
    }

    /// <summary>The message: answer bit set, all records in the answer section.</summary>
    public byte[] Build(ushort flags = 0x8400)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)_records.Count);
        return [.. header, .. _records.SelectMany(r => r)];
    }

    /// <summary>A name in wire form; labels as <see cref="DnsMessage.Labels"/> reads them (escaped dots stay inside a label).</summary>
    public static byte[] Name(string name) =>
        [.. DnsMessage.Labels(name).SelectMany(l => { var b = Encoding.UTF8.GetBytes(l); return (byte[])[(byte)b.Length, .. b]; }), 0];
}
