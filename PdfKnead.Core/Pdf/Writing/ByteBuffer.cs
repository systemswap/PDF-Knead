using System.Buffers.Text;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>Growing byte buffer for writing PDF syntax, without a detour via strings.</summary>
internal sealed class ByteBuffer(int capacity = 4096)
{
    private byte[] _data = new byte[Math.Max(16, capacity)];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _data.AsSpan(0, Length);

    /// <summary>Last byte written; -1 if the buffer is empty.</summary>
    public int Last => Length > 0 ? _data[Length - 1] : -1;

    public void Clear() => Length = 0;

    private void Ensure(int extra)
    {
        if (Length + extra <= _data.Length) return;
        var size = Math.Max(_data.Length * 2, Length + extra);
        Array.Resize(ref _data, size);
    }

    public void Add(byte b)
    {
        Ensure(1);
        _data[Length++] = b;
    }

    public void Add(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_data.AsSpan(Length));
        Length += bytes.Length;
    }

    /// <summary>ASCII only (keywords, numbers as text); each character is truncated to one byte.</summary>
    public void Ascii(string text)
    {
        Ensure(text.Length);
        foreach (var c in text) _data[Length++] = (byte)c;
    }

    public void Int(long value)
    {
        Ensure(20);
        Utf8Formatter.TryFormat(value, _data.AsSpan(Length), out var written);
        Length += written;
    }

    /// <summary>
    /// Writes a real number in the shortest form that gives exactly the same value, directly into the buffer without a
    /// string. Exponents (which PDF does not know) and "-0" are handled as in <see cref="PdfSerializer.FormatReal"/>.
    /// </summary>
    public void Real(double value)
    {
        Ensure(32);
        var span = _data.AsSpan(Length);
        if (!double.IsFinite(value) || !value.TryFormat(span, out var written, "R", System.Globalization.CultureInfo.InvariantCulture) ||
            span[..written].IndexOfAny((byte)'E', (byte)'e') >= 0)
        {
            Ascii(PdfSerializer.FormatReal(value));   // rare: very small or large number
            return;
        }
        if (written == 2 && span[0] == '-' && span[1] == '0')
        {
            span[0] = (byte)'0';
            written = 1;
        }
        Length += written;
    }

    /// <summary>Number of fixed width with leading zeros (cross-reference table); higher digits that do not fit are dropped.</summary>
    public void Padded(long value, int digits)
    {
        Ensure(digits);
        for (var i = digits - 1; i >= 0; i--)
        {
            _data[Length + i] = (byte)('0' + value % 10);
            value /= 10;
        }
        Length += digits;
    }

    public void WriteTo(Stream output) => output.Write(_data, 0, Length);
}
