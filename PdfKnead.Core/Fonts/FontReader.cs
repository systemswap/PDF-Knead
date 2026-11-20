using System.Buffers.Binary;

namespace PdfKnead.Core.Fonts;

/// <summary>Thrown for a damaged or unsupported font file.</summary>
/// <param name="message">Description of the problem.</param>
public class FontFormatException(string message) : Exception(message);

/// <summary>
/// Reads big-endian values from font data. Checks every bound, because fonts in PDF files are often
/// damaged or deliberately malicious.
/// </summary>
internal struct FontReader(ArraySegment<byte> data, int offset = 0)
{
    // A segment: CFF tables are read directly inside the OpenType file, without a copy
    public readonly ArraySegment<byte> Data = data;
    public int Position = offset;

    public readonly int Length => Data.Count;

    public readonly bool CanRead(int count) => Position >= 0 && count >= 0 && Position <= Data.Count - count;

    public byte U8()
    {
        Need(1);
        return Data[Position++];
    }

    public sbyte I8() => (sbyte)U8();

    public ushort U16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public short I16() => (short)U16();

    public uint U24()
    {
        Need(3);
        var v = (uint)(Data[Position] << 16 | Data[Position + 1] << 8 | Data[Position + 2]);
        Position += 3;
        return v;
    }

    public uint U32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public int I32() => (int)U32();

    /// <summary>Reads a 16.16 fixed-point number.</summary>
    public double Fixed() => I32() / 65536.0;

    /// <summary>Reads an F2Dot14 number (TrueType scale factors).</summary>
    public float F2Dot14() => I16() / 16384f;

    /// <summary>Reads an offset of 1 to 4 bytes (CFF).</summary>
    public int Offset(int size) => size switch
    {
        1 => U8(),
        2 => U16(),
        3 => (int)U24(),
        4 => I32(),
        _ => throw new FontFormatException($"Offset-Größe {size}"),
    };

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Need(count);
        var span = Data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count)
    {
        Need(count);
        Position += count;
    }

    public readonly string Tag(int at) =>
        at >= 0 && at <= Data.Count - 4 ? System.Text.Encoding.ASCII.GetString(Data.AsSpan(at, 4)) : "";

    private readonly void Need(int count)
    {
        if (!CanRead(count)) throw new FontFormatException("Schriftdaten zu kurz");
    }
}
