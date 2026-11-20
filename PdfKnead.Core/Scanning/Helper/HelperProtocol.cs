using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfKnead.Core.Scanning.Helper;

/// <summary>The kinds of messages between the program and its scan helper process.</summary>
internal enum HelperMessage : byte
{
    // program → helper
    FindDevices = 1,
    Open = 2,
    SelectSource = 3,
    Scan = 4,
    Cancel = 5,
    Close = 6,
    // helper → program
    Result = 10,
    Error = 11,
    Progress = 12,
    Page = 13,
    Done = 14,
}

/// <summary>One message: kind, the request it belongs to, a JSON part and an optional binary part (the samples of a page).</summary>
internal readonly record struct HelperFrame(HelperMessage Kind, int Request, byte[] Json, byte[] Data)
{
    /// <summary>The JSON part as a value.</summary>
    public T? Read<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => Json.Length == 0 ? default : JsonSerializer.Deserialize(Json, info);
}

/// <summary>What an error message carries.</summary>
/// <param name="Message">The message for the user (translated in the helper, which runs in the same language).</param>
/// <param name="Kind">The kind of error.</param>
/// <param name="Cancelled">Whether the request was cancelled (no error for the user).</param>
internal sealed record HelperError(string Message, ScanErrorKind Kind, bool Cancelled = false);

/// <summary>The answer to opening a device: the device as opened (a network back end may have given it a new id) and its capabilities.</summary>
internal sealed record HelperOpened(ScanDevice Device, ScanCapabilities Capabilities);

/// <summary>The header of a page; the samples follow as the binary part.</summary>
internal sealed record HelperPage(int Width, int Height, int Channels, int Dpi, bool BlackWhite);

/// <summary>Generated JSON code (Native AOT).</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<ScanDevice>))]
[JsonSerializable(typeof(ScanDevice))]
[JsonSerializable(typeof(ScanCapabilities))]
[JsonSerializable(typeof(ScanSettings))]
[JsonSerializable(typeof(ScanProgress))]
[JsonSerializable(typeof(HelperError))]
[JsonSerializable(typeof(HelperOpened))]
[JsonSerializable(typeof(HelperPage))]
[JsonSerializable(typeof(string))]
internal sealed partial class HelperJson : JsonSerializerContext;

/// <summary>
/// Reading and writing messages on a stream: kind (1 byte), request (4), length of the JSON part (4), JSON, length of the binary part (4),
/// binary — integers little-endian. Lengths are checked before anything is allocated.
/// </summary>
internal static class HelperWire
{
    /// <summary>The longest JSON part: device lists and capabilities are small.</summary>
    public const int MaxJson = 4 << 20;

    /// <summary>The longest binary part: one page (as the limit of a frame in the scanner interface).</summary>
    public const long MaxData = Sane.SaneSession.MaxFrameBytes;

    public static async Task WriteAsync(Stream stream, HelperFrame frame, CancellationToken cancel = default)
    {
        var head = new byte[9];
        head[0] = (byte)frame.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(1), frame.Request);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(5), frame.Json.Length);
        await stream.WriteAsync(head, cancel);
        await stream.WriteAsync(frame.Json, cancel);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Data.Length);
        await stream.WriteAsync(length, cancel);
        if (frame.Data.Length > 0) await stream.WriteAsync(frame.Data, cancel);
        await stream.FlushAsync(cancel);
    }

    /// <summary>Reads one message; null at the end of the stream (the other side has gone).</summary>
    /// <exception cref="InvalidDataException">A length beyond the limits (a broken or foreign stream).</exception>
    public static async Task<HelperFrame?> ReadAsync(Stream stream, CancellationToken cancel = default)
    {
        var head = new byte[9];
        if (!await Fill(stream, head, cancel)) return null;
        var kind = (HelperMessage)head[0];
        var request = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1));
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(5));
        if (jsonLength is < 0 or > MaxJson) throw new InvalidDataException("scan helper: JSON part too long");
        var json = new byte[jsonLength];
        if (!await Fill(stream, json, cancel)) return null;
        var length = new byte[4];
        if (!await Fill(stream, length, cancel)) return null;
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (dataLength < 0 || dataLength > MaxData) throw new InvalidDataException("scan helper: binary part too long");
        var data = dataLength == 0 ? [] : new byte[dataLength];
        if (!await Fill(stream, data, cancel)) return null;
        return new HelperFrame(kind, request, json, data);
    }

    private static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(at), cancel);
            if (read == 0) return false;
            at += read;
        }
        return true;
    }

    public static byte[] Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);
}
