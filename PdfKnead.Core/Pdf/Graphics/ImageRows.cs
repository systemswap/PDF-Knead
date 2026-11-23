using System.IO.Compression;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Reads the raw data of an image row by row without holding the whole decoded stream in
/// memory: unfiltered or Flate (also with a PNG or TIFF predictor). For large images (scans,
/// plans) and large masks, so that memory use stays at one row.
/// </summary>
internal sealed class ImageRows : IRowSource, IDisposable
{
    private readonly Stream? _stream;
    private readonly byte[]? _data;
    private long _offset;
    private readonly int _rowBytes;
    private readonly int _predictor;
    private readonly int _bpp;
    private readonly byte[] _previous;
    private readonly byte[] _filtered;

    private ImageRows(Stream? stream, byte[]? data, int rowBytes, int predictor, int bpp)
    {
        _stream = stream;
        _data = data;
        _rowBytes = rowBytes;
        _predictor = predictor;
        _bpp = bpp;
        _previous = new byte[predictor >= 10 ? rowBytes : 0];
        _filtered = new byte[predictor >= 10 ? rowBytes + 1 : 0];
    }

    /// <summary>The data ended too early or was damaged (the rest is filled with 0).</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Row reader for an image data stream; null if the filter chain is not suitable for it
    /// (the caller then decodes the whole stream).
    /// </summary>
    public static ImageRows? Open(PdfDocument doc, PdfStream stream, int rowBytes)
    {
        var chain = StreamFilters.Chain(stream.Dictionary);
        if (chain.Count == 0) return new ImageRows(null, doc.GetRawStreamData(stream), rowBytes, 1, 1);
        if (chain.Count != 1 || chain[0].Name != "FlateDecode") return null;

        var parms = chain[0].Parms;
        // the predictor and the zlib header by the same rules as the whole stream (docs/FEHLERGEDAECHTNIS.md, No. 937, 938)
        var (predictor, valid) = Predictor.Of(parms);
        var bpp = 1;
        if (predictor > 1)
        {
            // the values of Table 8 by the same rule as the whole stream (No. 1007): out of range, the whole stream decodes them
            if (Predictor.Parameters(parms) is not { Valid: true } p) return null;
            var (colors, bits, columns) = (p.Colors, p.Bits, p.Columns);
            // the predictor must match the image row, TIFF only with 8 bits; otherwise decode the whole stream instead
            if ((long)colors * bits * columns + 7 >> 3 != rowBytes) return null;
            if (predictor == 2 && bits != 8) return null;
            bpp = Math.Max(1, (colors * bits + 7) / 8);
            if (predictor == 2) bpp = colors;
        }

        var raw = doc.GetRawStreamData(stream);
        var skip = Flate.DataStart(raw, out var unusual);
        if (skip < 0) return new ImageRows(null, [], rowBytes, 1, 1) { Truncated = true };
        var deflate = new DeflateStream(new MemoryStream(raw, skip, raw.Length - skip, writable: false), CompressionMode.Decompress);
        return new ImageRows(deflate, null, rowBytes, predictor, bpp) { Truncated = !valid || unusual };
    }

    /// <summary>Reads the next row into the first row-length bytes of <paramref name="row"/>; false if no more data arrived.</summary>
    public bool Read(Span<byte> row)
    {
        row = row[.._rowBytes];
        if (_predictor >= 10)
        {
            var got = Fill(_filtered);
            if (got == 0) return false;
            _filtered.AsSpan(1, _rowBytes).CopyTo(row);
            if (!Predictor.PngRow(_filtered[0], row, _previous, _bpp)) Truncated = true;   // a tag Table 9 does not know (No. 1009)
            row.CopyTo(_previous);
            return true;
        }
        if (Fill(row) == 0) return false;
        if (_predictor == 2)
            for (var i = _bpp; i < row.Length; i++) row[i] += row[i - _bpp];
        return true;
    }

    /// <summary>Fills the buffer; missing bytes become 0. Returns the number of bytes actually read.</summary>
    private int Fill(Span<byte> buffer)
    {
        var got = 0;
        if (_data is not null)
        {
            var available = (int)Math.Clamp(_data.Length - _offset, 0, buffer.Length);
            _data.AsSpan((int)_offset, available).CopyTo(buffer);
            _offset += available;
            got = available;
        }
        else
        {
            try
            {
                while (got < buffer.Length)
                {
                    var read = _stream!.Read(buffer[got..]);
                    if (read == 0) break;
                    got += read;
                }
            }
            catch (InvalidDataException)
            {
                Truncated = true;
            }
        }
        if (got < buffer.Length)
        {
            if (got > 0) Truncated = true;
            buffer[got..].Clear();
        }
        return got;
    }

    public void Dispose() => _stream?.Dispose();
}
