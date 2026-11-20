using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// The host name of a scanner found by „Netz durchsuchen“, shown behind its address so that the user knows which device it is (wish of the
/// user, docs/FEHLERGEDAECHTNIS.md, No. 926). First the name service of the system backwards (PTR — in a company network the name the IT gave
/// the device; the same way a typed scanner name is resolved, <see cref="EsclNetwork.ResolveLocalAsync"/>), then, without an answer, the
/// device itself over NetBIOS (node status request, RFC 1002, 4.2.17 and 4.2.18, the name encoded as in RFC 1001, 14.1) — that request goes
/// to the device's address only. Only addresses in the own network; the names are cleaned before they are shown.
/// </summary>
public static class EsclHostNames
{
    /// <summary>How many addresses are asked at a time: a small name server (a router) loses answers under a burst of many requests.</summary>
    public const int Parallel = 8;

    /// <summary>The time for the name service per address, its own repetitions included.</summary>
    public static readonly TimeSpan NameServiceBudget = TimeSpan.FromSeconds(4);

    /// <summary>How long one NetBIOS request waits; it is sent at most twice.</summary>
    public static readonly TimeSpan NetBiosWait = TimeSpan.FromSeconds(1);

    /// <summary>The UDP port of the NetBIOS name service.</summary>
    public const int NetBiosPort = 137;

    /// <summary>The names of several addresses, at most <see cref="Parallel"/> at a time; an address without a name is missing.</summary>
    /// <param name="addresses">The addresses.</param>
    /// <param name="resolve">One address to its name (tests put a fixed table here); <see cref="NameOfAsync"/> if null.</param>
    /// <param name="cancel">Stops asking; the names found so far are returned.</param>
    public static async Task<Dictionary<IPAddress, string>> NamesOfAsync(IEnumerable<IPAddress> addresses,
        Func<IPAddress, CancellationToken, Task<string?>>? resolve = null, CancellationToken cancel = default)
    {
        resolve ??= NameOfAsync;
        var names = new Dictionary<IPAddress, string>();
        var gate = new Lock();
        using var slots = new SemaphoreSlim(Parallel);
        await Task.WhenAll(addresses.Distinct().Where(EsclNetwork.IsLocal).Select(async address =>
        {
            try
            {
                await slots.WaitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (Clean(await resolve(address, cancel)) is { } name)
                    lock (gate) names[address] = name;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
            {
                // no name: the address is shown alone
            }
            finally
            {
                slots.Release();
            }
        }));
        return names;
    }

    /// <summary>The name of one address: the name service backwards, else NetBIOS at the device; null without an answer.</summary>
    public static async Task<string?> NameOfAsync(IPAddress address, CancellationToken cancel = default)
    {
        if (!EsclNetwork.IsLocal(address)) return null;
        return await ReverseAsync(address, cancel) ?? await NetBiosAsync(address, cancel);
    }

    private static async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancel)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(NameServiceBudget);
            var entry = await Dns.GetHostEntryAsync(address.ToString(), limit.Token);
            // without a PTR record some systems return the address itself as the name
            return IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            cancel.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<string?> NetBiosAsync(IPAddress address, CancellationToken cancel)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;   // NetBIOS over TCP/IP knows IPv4 only
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var target = new IPEndPoint(address, NetBiosPort);
        var buffer = new byte[1500];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var id = (ushort)Random.Shared.Next(1, 0xFFFF);
            try
            {
                await socket.SendToAsync(NetBiosRequest(id), SocketFlags.None, target, cancel);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(NetBiosWait);
                while (true)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), limit.Token);
                    // only the asked device counts, and only the answer to this request
                    if (received.RemoteEndPoint is IPEndPoint from && from.Address.Equals(address)
                        && NetBiosName(buffer.AsSpan(0, received.ReceivedBytes), id) is { } name)
                        return name;
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                // no answer in time: once more
            }
            catch (SocketException)
            {
                return null;   // e.g. the port is closed (ICMP)
            }
        }
        return null;
    }

    /// <summary>A NetBIOS node status request for the name „*“ (RFC 1002, 4.2.17): 50 bytes.</summary>
    /// <param name="id">The transaction number; the answer repeats it.</param>
    public static byte[] NetBiosRequest(ushort id)
    {
        var packet = new byte[12 + 34 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);           // one question, no flags
        packet[12] = 32;                                                       // the encoded name: 16 bytes as 32 letters
        for (var i = 0; i < 16; i++)
        {
            var b = i == 0 ? (byte)'*' : (byte)0;
            packet[13 + 2 * i] = (byte)('A' + (b >> 4));
            packet[14 + 2 * i] = (byte)('A' + (b & 0x0F));
        }
        // packet[45] = 0: end of the name; type NBSTAT (0x21), class IN (1)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(46), 0x0021);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(48), 0x0001);
        return packet;
    }

    /// <summary>
    /// The device's name from a node status answer (RFC 1002, 4.2.18): the first unique name of the file server (suffix 0x20), else of the
    /// workstation (0x00) — never a group name (the workgroup has the same suffix); null if the data is not the answer to this request. Every
    /// length is checked against the data; the data comes from the network.
    /// </summary>
    /// <param name="answer">The datagram.</param>
    /// <param name="id">The transaction number of the request.</param>
    public static string? NetBiosName(ReadOnlySpan<byte> answer, ushort id)
    {
        if (answer.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(answer) != id) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(answer[2..]);
        if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0 || BinaryPrimitives.ReadUInt16BigEndian(answer[6..]) == 0) return null;
        var at = 12;
        for (var labels = 0; ; labels++)                                       // the name of the answer: labels or one pointer
        {
            if (labels >= 64 || at >= answer.Length) return null;
            var length = answer[at];
            if (length == 0) { at++; break; }
            if ((length & 0xC0) == 0xC0) { at += 2; break; }
            if ((length & 0xC0) != 0) return null;
            at += 1 + length;
        }
        if (at + 10 > answer.Length) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(answer[at..]) != 0x0021 || BinaryPrimitives.ReadUInt16BigEndian(answer[(at + 2)..]) != 0x0001)
            return null;
        var dataLength = BinaryPrimitives.ReadUInt16BigEndian(answer[(at + 8)..]);
        var data = answer.Slice(at + 10, Math.Min(dataLength, answer.Length - at - 10));
        if (data.IsEmpty) return null;
        string? server = null, workstation = null;
        for (int i = 0, offset = 1; i < data[0] && offset + 18 <= data.Length; i++, offset += 18)
        {
            if ((data[offset + 16] & 0x80) != 0) continue;                     // a group name
            var name = Encoding.ASCII.GetString(data.Slice(offset, 15)).TrimEnd(' ', '\0').Trim();
            if (name.Length == 0) continue;
            if (data[offset + 15] == 0x20) server ??= name;
            else if (data[offset + 15] == 0x00) workstation ??= name;
        }
        return server ?? workstation;
    }

    /// <summary>The host name without its domain, as the list shows it (design decision: shorter and easier to read): „PRN-2OG-01“ from
    /// „PRN-2OG-01.example.local“; null for no name or an address.</summary>
    public static string? Short(string? name)
    {
        if (Clean(name) is not { } clean || IPAddress.TryParse(clean, out _)) return null;
        var first = clean.Split('.')[0];
        return first.Length > 0 ? first : null;
    }

    /// <summary>
    /// A name as it may be shown: letters, digits, „-“, „_“ and „.“, at most 253 characters, without a final dot; null otherwise. A name
    /// from the network never brings control or direction characters into the window.
    /// </summary>
    public static string? Clean(string? name)
    {
        var text = name?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(text) || text.Length > 253) return null;
        return text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? text : null;
    }
}
