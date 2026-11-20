using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>The record types the scanner search uses (RFC 1035, RFC 2782, RFC 3596).</summary>
public enum DnsType : ushort
{
    /// <summary>IPv4 address.</summary>
    A = 1,
    /// <summary>Pointer from a service type to its instances (RFC 6763, 4.1).</summary>
    Ptr = 12,
    /// <summary>Key=value texts of a service instance (RFC 6763, 6).</summary>
    Txt = 16,
    /// <summary>IPv6 address.</summary>
    Aaaa = 28,
    /// <summary>Host and port of a service instance.</summary>
    Srv = 33,
}

/// <summary>One resource record of an answer, with the data of the types the search reads; other types keep only their header.</summary>
/// <param name="Name">The owner name, without the final dot.</param>
/// <param name="Type">The record type (any value; only the <see cref="DnsType"/> members carry data here).</param>
/// <param name="Ttl">The time to live in seconds (0 = the record is withdrawn).</param>
/// <param name="Target">PTR: the instance name; SRV: the host name.</param>
/// <param name="Port">SRV: the port.</param>
/// <param name="Address">A/AAAA: the address.</param>
/// <param name="Texts">TXT: the key=value strings in their order.</param>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl, string? Target = null, int Port = 0, IPAddress? Address = null,
    IReadOnlyList<string>? Texts = null);

/// <summary>
/// Builds questions and reads answers of multicast DNS (RFC 6762) in the DNS wire format (RFC 1035, 4.1). Answers come from devices in the
/// network and are read as untrusted input: every length is checked before use, names are limited to 255 bytes and labels to 63, a
/// compression pointer may only point backwards and at most <see cref="MaxPointers"/> times per name, labels of the reserved kinds are
/// refused, and a message yields at most <see cref="MaxRecords"/> records. A malformed message is dropped as a whole (null), never half read.
/// </summary>
public static class DnsMessage
{
    /// <summary>The largest message the search reads (RFC 6762, 17: multicast DNS messages may be up to 9000 bytes).</summary>
    public const int MaxMessage = 9000;

    /// <summary>The most records read from one message (all sections together).</summary>
    public const int MaxRecords = 256;

    /// <summary>The most compression pointers followed within one name.</summary>
    public const int MaxPointers = 32;

    private const int MaxName = 255, MaxLabel = 63;

    /// <summary>
    /// A query for the given names and types, as a client that is not a full responder asks (RFC 6762, 6.7): the message id is echoed by the
    /// answering devices, which then answer directly to the asking port.
    /// </summary>
    /// <param name="id">The message id.</param>
    /// <param name="questions">The names (without the final dot) and the record type asked for each.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentException">A name is not a valid DNS name.</exception>
    public static byte[] Query(ushort id, IEnumerable<(string Name, DnsType Type)> questions)
    {
        var list = questions.ToList();
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)list.Count);
        output.Write(header);
        Span<byte> tail = stackalloc byte[4];
        foreach (var (name, type) in list)
        {
            WriteName(output, name);
            BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);   // class IN
            output.Write(tail);
        }
        return output.ToArray();
    }

    private static void WriteName(Stream output, string name)
    {
        var total = 1;
        foreach (var label in Labels(name))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > MaxLabel) throw new ArgumentException($"DNS-Name ungültig: {name}", nameof(name));
            total += bytes.Length + 1;
            if (total > MaxName) throw new ArgumentException($"DNS-Name zu lang: {name}", nameof(name));
            output.WriteByte((byte)bytes.Length);
            output.Write(bytes);
        }
        output.WriteByte(0);
    }

    /// <summary>
    /// The labels of a name in the form <see cref="Read"/> gives: labels are separated by dots; a dot or backslash inside a label is written
    /// with a backslash before it (RFC 6763, 4.3 — instance names are free text and may contain dots).
    /// </summary>
    /// <param name="name">The name, without or with the final dot.</param>
    /// <returns>The labels.</returns>
    public static List<string> Labels(string name)
    {
        var labels = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '\\' && i + 1 < name.Length) current.Append(name[++i]);
            else if (name[i] == '.')
            {
                labels.Add(current.ToString());
                current.Clear();
            }
            else current.Append(name[i]);
        }
        if (current.Length > 0) labels.Add(current.ToString());
        return labels;
    }

    /// <summary>A label as it appears in a name: dots and backslashes inside it escaped.</summary>
    /// <param name="label">The label.</param>
    /// <returns>The escaped text.</returns>
    public static string Escape(string label) => label.Replace("\\", "\\\\").Replace(".", "\\.");

    /// <summary>
    /// Reads an answer: the records of the answer, authority and additional sections (questions are skipped). Messages that are questions
    /// (no answer bit), carry an error code, or are malformed in any way give null.
    /// </summary>
    /// <param name="message">The received bytes.</param>
    /// <returns>The id of the message and its records, or null.</returns>
    public static (ushort Id, List<DnsRecord> Records)? Read(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12 || message.Length > MaxMessage) return null;
        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) == 0) return null;   // a question, not an answer (own or other devices' queries are mirrored back)
        if ((flags & 0x000F) != 0) return null;   // an error code
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var records = BinaryPrimitives.ReadUInt16BigEndian(message[6..]) + BinaryPrimitives.ReadUInt16BigEndian(message[8..])
            + BinaryPrimitives.ReadUInt16BigEndian(message[10..]);
        if (questions > MaxRecords || records > MaxRecords) return null;
        var at = 12;
        for (var q = 0; q < questions; q++)
        {
            if (ReadName(message, ref at) is null || at + 4 > message.Length) return null;
            at += 4;
        }
        var result = new List<DnsRecord>(records);
        for (var r = 0; r < records; r++)
        {
            var name = ReadName(message, ref at);
            if (name is null || at + 10 > message.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[at..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(at + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[(at + 8)..]);
            at += 10;
            if (length > message.Length - at) return null;
            var start = at;
            var end = at + length;
            DnsRecord? record = (DnsType)type switch
            {
                DnsType.Ptr => ReadName(message, ref at) is { } target && at <= end ? new DnsRecord(name, type, ttl, target) : null,
                DnsType.Srv => ReadSrv(message, start, end, name, type, ttl),
                DnsType.A => length == 4 ? new DnsRecord(name, type, ttl, Address: new IPAddress(message.Slice(start, 4))) : null,
                DnsType.Aaaa => length == 16 ? new DnsRecord(name, type, ttl, Address: new IPAddress(message.Slice(start, 16))) : null,
                DnsType.Txt => ReadTxt(message[start..end], name, type, ttl),
                _ => new DnsRecord(name, type, ttl),
            };
            if (record is null) return null;
            result.Add(record);
            at = end;
        }
        return (id, result);
    }

    private static DnsRecord? ReadSrv(ReadOnlySpan<byte> message, int start, int end, string name, ushort type, uint ttl)
    {
        if (end - start < 7) return null;   // priority, weight, port and at least the root name
        var port = BinaryPrimitives.ReadUInt16BigEndian(message[(start + 4)..]);
        var at = start + 6;
        var target = ReadName(message, ref at);
        return target is not null && at <= end && port != 0 ? new DnsRecord(name, type, ttl, target, port) : null;
    }

    private static DnsRecord? ReadTxt(ReadOnlySpan<byte> data, string name, ushort type, uint ttl)
    {
        var texts = new List<string>();
        var at = 0;
        while (at < data.Length)
        {
            int length = data[at++];
            if (length > data.Length - at) return null;
            if (length > 0) texts.Add(Encoding.UTF8.GetString(data.Slice(at, length)));
            at += length;
        }
        return new DnsRecord(name, type, ttl, Texts: texts);
    }

    /// <summary>
    /// Reads a possibly compressed name at <paramref name="at"/> and moves <paramref name="at"/> behind it (behind the first pointer, if any).
    /// Pointers must point before the place they are read from, so a chain always ends; their number is limited all the same.
    /// </summary>
    private static string? ReadName(ReadOnlySpan<byte> message, ref int at)
    {
        var builder = new StringBuilder();
        var position = at;
        var resume = -1;
        var length = 1;
        var pointers = 0;
        while (true)
        {
            if (position >= message.Length) return null;
            int count = message[position];
            if (count == 0)
            {
                position++;
                break;
            }
            switch (count & 0xC0)
            {
                case 0xC0:
                    if (position + 1 >= message.Length || ++pointers > MaxPointers) return null;
                    var target = ((count & 0x3F) << 8) | message[position + 1];
                    if (target >= position) return null;   // only backwards: no loops, no jumps into the not yet read part
                    if (resume < 0) resume = position + 2;
                    position = target;
                    continue;
                case 0x00:
                    break;
                default:
                    return null;   // the reserved label kinds (RFC 6891, 5) are not used by the devices
            }
            if (count > MaxLabel || position + 1 + count > message.Length) return null;
            length += count + 1;
            if (length > MaxName) return null;
            if (builder.Length > 0) builder.Append('.');
            builder.Append(Escape(Encoding.UTF8.GetString(message.Slice(position + 1, count))));
            position += 1 + count;
        }
        at = resume >= 0 ? resume : position;
        return builder.ToString();
    }
}
