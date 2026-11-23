using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>A JPEG that cannot be decoded (unsupported process, invalid header, too costly).</summary>
internal sealed class JpegException(string message) : PdfException(message);

/// <summary>A decoded JPEG: the components interleaved, 8 bits each, after the colour transform.</summary>
internal sealed class JpegImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>1, 3 or 4 (2 in images PDF does not allow but TN 5116 does).</summary>
    public required int Channels { get; init; }

    public required byte[] Samples { get; init; }

    /// <summary>The data was damaged or cut off: the image holds what could be decoded, the rest grey (zero coefficients).</summary>
    public bool Damaged { get; init; }

    /// <summary>Decoded smaller by this factor (1, 2, 4 or 8) to stay within the pixels asked for.</summary>
    public int Scale { get; init; } = 1;
}

/// <summary>What the frame header says (ITU-T T.81, B.2.2).</summary>
internal readonly record struct JpegFrameInfo(int Width, int Height, int Components, bool Progressive, bool Arithmetic, int Precision, int Marker);

/// <summary>Options of one decoding.</summary>
/// <param name="ColorTransform">/ColorTransform of the filter's /DecodeParms; null if absent (ISO 32000-2, 7.4.8, Table 13).</param>
/// <param name="PdfHeight">/Height of the image, for a header without the number of lines (0 if unknown).</param>
/// <param name="MaxPixels">The image is decoded smaller (by 2, 4 or 8) if it has more pixels; 0 for no limit.</param>
/// <param name="Budget">Memory the decoding may take in bytes; 0 for the budget of the machine for images.</param>
internal readonly record struct JpegOptions(int? ColorTransform = null, long PdfHeight = 0, long MaxPixels = long.MaxValue, long Budget = 0);

/// <summary>
/// The own JPEG decoder (ISO 32000-2, 7.4.8 with ITU-T T.81 = ISO/IEC 10918-1, T.871, Adobe TN 5116): 8-bit DCT images, sequential
/// (baseline and extended) and progressive, with Huffman or arithmetic coding, all sampling factors 1–4, restart markers, DNL, the
/// Adobe marker and /ColorTransform. Lossless and hierarchical processes and 12-bit samples are recognised and refused (PDF stores one
/// byte per component, 7.4.8). Decided on 12.11.2026: one decoder for display, redaction and cutting, so that a blacked-out
/// copy looks exactly like the original outside the bar.
/// </summary>
/// <remarks>
/// Security (images are often crafted): every size from the data is checked before memory is taken — dimensions against
/// <see cref="PdfImageDecoder.MaxPixels"/> and <see cref="PdfImageDecoder.MaxSide"/>, all buffers (planes, the coefficients of a
/// progressive image, the output) against the budget, the coefficients of a progressive image against the length of its data
/// (docs/FEHLERGEDAECHTNIS.md, No. 742), blocks times scans against <see cref="MaxBlockScans"/>; segment lengths against the data,
/// table numbers, sampling factors, component selectors, spectral bands and approximation bits against T.81; Huffman tables that
/// promise more codes than fit are refused. Damaged entropy-coded data ends the restart interval with zeros and is reported.
/// </remarks>
internal static class JpegDecoder
{
    /// <summary>
    /// Most 8 × 8 blocks times scans one image may take: 300 million — a valid progressive image of 100 megapixels needs about 25 million.
    /// (For the codec before it was 4 billion, No. 742; the fuzzer found crafted files repeating empty scans over large images that took
    /// seconds each.) Scans whose data is used up end at once (<see cref="JpegEntropy.Dead"/>), so only scans with data cost work.
    /// </summary>
    internal const long MaxBlockScans = 300_000_000;

    /// <summary>Coefficient memory above which a progressive JPEG must justify itself by the length of its data (No. 742).</summary>
    internal const long CostlyCoefficientBytes = 64L << 20;

    /// <summary>The number of lines at or above which the header holds a placeholder a scanner wrote (No. 457).</summary>
    private const int PlaceholderLines = 60_000;

    /// <summary>The frame header, or null if there is none before the first scan.</summary>
    public static JpegFrameInfo? ReadFrame(byte[] data)
    {
        var start = Start(data);
        if (start < 0) return null;
        for (var at = start + 2; at + 3 < data.Length;)
        {
            if (data[at] != 0xFF) return null;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length) return null;
            if (IsFrame(marker))
            {
                if (length < 8) return null;
                return new JpegFrameInfo(data[at + 7] << 8 | data[at + 8], data[at + 5] << 8 | data[at + 6], data[at + 9],
                    marker is 0xC2 or 0xC6 or 0xCA or 0xCE, marker >= 0xC9, data[at + 4], marker);
            }
            if (marker is 0xDA or 0xD9) return null;
            at += 2 + length;
        }
        return null;
    }

    /// <summary>SOF0 … SOF15 except DHT (C4), JPG (C8), DAC (CC).</summary>
    private static bool IsFrame(byte marker) => marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    /// <summary>The start marker: bytes before it are passed over (S-A A14), within the first kilobyte.</summary>
    private static int Start(byte[] data) => data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf([(byte)0xFF, (byte)0xD8]);

    /// <summary>Decodes a JPEG.</summary>
    /// <exception cref="JpegException">Not decodable: no JPEG, unsupported process, invalid header, beyond the limits.</exception>
    public static JpegImage Decode(byte[] data, JpegOptions options = default) => new State(data, options).Run();

    private sealed class Component
    {
        public int Id, H, V, Tq;
        public int Width, Height;               // samples of the component in the image (A.1.1)
        public int BlocksWide, BlocksHigh;      // blocks a scan of this component alone covers (A.2.2)
        public int PlaneBlocksWide, PlaneBlocksHigh;   // blocks of the interleaved grid (whole MCUs)
        public byte[] Plane = [];
        public int Stride;
        public short[]? Coefficients;           // progressive: quantised, natural order, 64 per block
        public int[]? Quant;                    // progressive: the table latched at the component's first scan
        public bool Seen;
    }

    private sealed class State(byte[] data, JpegOptions options)
    {
        private readonly int[]?[] _quant = new int[4][];
        private readonly JpegHuffmanTable?[] _dcTables = new JpegHuffmanTable[4], _acTables = new JpegHuffmanTable[4];
        private readonly byte[] _arithDc = [0, 0, 0, 0], _arithDcU = [1, 1, 1, 1], _arithAcK = [5, 5, 5, 5];
        private Component[] _components = [];
        private int _restartInterval, _hMax = 1, _vMax = 1, _mcusWide, _mcusHigh, _width, _height, _scale = 1, _blockSize = 8;
        private bool _progressive, _arithmetic, _baseline, _damaged, _frame;
        private int _adobeTransform = -1;
        private bool _jfif;
        private long _blockScans, _blocks;

        public JpegImage Run()
        {
            var start = Start(data);
            if (start < 0) throw new JpegException("kein JPEG");
            var at = start + 2;
            var end = data.Length;
            var scans = 0;
            while (at < end)
            {
                if (data[at] != 0xFF)
                {
                    // bytes where a marker should be: passed over to the next marker (common after damaged scans)
                    var next = JpegBits.NextMarker(data, at, end);
                    if (next < 0) break;
                    _damaged |= _frame;
                    at = next;
                    continue;
                }
                if (at + 1 >= end) break;
                var marker = data[at + 1];
                if (marker == 0xFF)
                {
                    at++;
                    continue;
                }
                if (marker == 0xD9) break;   // EOI
                if (marker is 0x01 or 0xD8 or >= 0xD0 and <= 0xD7)
                {
                    at += 2;
                    continue;
                }
                if (at + 3 >= end) break;
                var length = data[at + 2] << 8 | data[at + 3];
                if (length < 2 || at + 2 + length > end)
                {
                    if (!_frame) throw new JpegException("Segment über das Datenende");
                    _damaged = true;
                    break;
                }
                var segment = data.AsSpan(at + 4, length - 2);
                if (IsFrame(marker))
                {
                    if (_frame) throw new JpegException("zweiter Bildkopf");
                    Frame(marker, segment, at + 2 + length);
                    at += 2 + length;
                    continue;
                }
                switch (marker)
                {
                    case 0xC4:
                        Huffman(segment);
                        break;
                    case 0xCC:
                        Conditioning(segment);
                        break;
                    case 0xDB:
                        Quantization(segment);
                        break;
                    case 0xDD:
                        if (segment.Length < 2) throw new JpegException("DRI zu kurz");
                        if (segment.Length > 2) _damaged = true;   // Lr = 4 (Table B.7): a longer one read, reported (S-A A119)
                        _restartInterval = segment[0] << 8 | segment[1];
                        break;
                    case 0xEE when segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8):
                        _adobeTransform = segment[11];
                        break;
                    case 0xE0 when segment.Length >= 5 && segment[..5].SequenceEqual("JFIF\0"u8):
                        _jfif = true;
                        break;
                    case 0xDA:
                        if (!_frame) throw new JpegException("Scan vor dem Bildkopf");
                        at = Scan(segment, at + 2 + length);
                        scans++;
                        continue;
                }
                at += 2 + length;
            }
            if (!_frame) throw new JpegException("kein Bildkopf");
            if (scans == 0) throw new JpegException("kein Scan");
            if (_progressive) Reconstruct();
            var planes = _components.Select(c => new JpegColor.Plane(c.Plane, c.Stride, Ceil(c.Width, _scale), Ceil(c.Height, _scale), c.H, c.V)).ToArray();
            int outW = Ceil(_width, _scale), outH = Ceil(_height, _scale);
            var samples = JpegColor.Assemble(planes, _hMax, _vMax, outW, outH, Transform());
            return new JpegImage { Width = outW, Height = outH, Channels = _components.Length, Samples = samples, Damaged = _damaged, Scale = _scale };
        }

        private static int Ceil(int a, int b) => (int)(((long)a + b - 1) / b);

        /// <summary>ISO 32000-2, 7.4.8, Table 13: the Adobe marker decides, otherwise /ColorTransform, otherwise 1 for three components.</summary>
        private JpegTransform Transform()
        {
            var n = _components.Length;
            if (n is not (3 or 4)) return JpegTransform.None;
            int transform;
            if (_adobeTransform >= 0) transform = _adobeTransform;
            else if (options.ColorTransform is { } t) transform = t;
            else if (n == 3 && !_jfif && _components[0].Id == 'R' && _components[1].Id == 'G' && _components[2].Id == 'B') transform = 0;   // S-A A74
            else transform = n == 3 ? 1 : 0;
            if (transform == 0) return JpegTransform.None;
            return n == 3 ? JpegTransform.YCbCr : JpegTransform.Ycck;
        }

        private void Frame(byte marker, ReadOnlySpan<byte> s, int afterHeader)
        {
            if (s.Length < 6) throw new JpegException("Bildkopf zu kurz");
            var precision = s[0];
            if (precision != 8) throw new JpegException($"{precision} Bit je Abtastwert nicht unterstützt (PDF: ein Byte, 7.4.8)");
            // SOF0, 1, 2 (Huffman), 9, 10 (arithmetic); lossless (3, 11) and hierarchical (5–7, 13–15) are not DCT images of PDF
            if (marker is not (0xC0 or 0xC1 or 0xC2 or 0xC9 or 0xCA)) throw new JpegException($"JPEG-Verfahren SOF{marker - 0xC0} nicht unterstützt");
            _progressive = marker is 0xC2 or 0xCA;
            _baseline = marker == 0xC0;
            _arithmetic = marker >= 0xC9;
            int lines = s[1] << 8 | s[2], width = s[3] << 8 | s[4], n = s[5];
            if (n is < 1 or > 4) throw new JpegException($"{n} Komponenten");
            if (s.Length < 6 + 3 * n) throw new JpegException("Bildkopf zu kurz");
            // longer than Lf = 8 + 3 × Nf (Table B.2): read by its length, reported (S-A A119, FG 1011)
            if (s.Length > 6 + 3 * n) _damaged = true;
            if (width == 0) throw new JpegException("Breite 0");
            _components = new Component[n];
            for (var i = 0; i < n; i++)
            {
                int id = s[6 + 3 * i], h = s[7 + 3 * i] >> 4, v = s[7 + 3 * i] & 15, tq = s[8 + 3 * i];
                if (h is < 1 or > 4 || v is < 1 or > 4) throw new JpegException("Abtastfaktor außerhalb 1–4");
                if (tq > 3) throw new JpegException("Quantisierungstabelle außerhalb 0–3");
                for (var j = 0; j < i; j++)
                    if (_components[j].Id == id) throw new JpegException("Komponentenkennung doppelt");
                _components[i] = new Component { Id = id, H = h, V = v, Tq = tq };
            }
            _height = Lines(lines, afterHeader);
            _width = width;
            if ((long)_width * _height > PdfImageDecoder.MaxPixels) throw new JpegException("Bild zu groß");
            _hMax = _components.Max(c => c.H);
            _vMax = _components.Max(c => c.V);
            _mcusWide = Ceil(_width, 8 * _hMax);
            _mcusHigh = Ceil(_height, 8 * _vMax);
            _scale = 1;
            // 0 (the default of the options) means no limit
            var maxPixels = options.MaxPixels > 0 ? options.MaxPixels : long.MaxValue;
            while (_scale < 8 && (long)Ceil(_width, _scale) * Ceil(_height, _scale) > maxPixels) _scale *= 2;
            _blockSize = 8 / _scale;

            // sizes and memory, all before anything is taken
            long planeBytes = 0, coefficientBytes = 0;
            _blocks = 0;
            foreach (var c in _components)
            {
                c.Width = Ceil(_width * c.H, _hMax);
                c.Height = Ceil(_height * c.V, _vMax);
                c.BlocksWide = Ceil(c.Width, 8);
                c.BlocksHigh = Ceil(c.Height, 8);
                c.PlaneBlocksWide = n == 1 ? c.BlocksWide : _mcusWide * c.H;
                c.PlaneBlocksHigh = n == 1 ? c.BlocksHigh : _mcusHigh * c.V;
                long blocks = (long)c.PlaneBlocksWide * c.PlaneBlocksHigh;
                _blocks += (long)c.BlocksWide * c.BlocksHigh;
                planeBytes += blocks * _blockSize * _blockSize;
                if (_progressive) coefficientBytes += blocks * 128;
            }
            if (_progressive && coefficientBytes > CostlyCoefficientBytes && coefficientBytes > 2048L * data.Length)
                throw new JpegException("Koeffizienten weit über dem, was die Daten tragen können (präparierte Größe)");
            // blocks times scans, counted before anything is taken: an SOS marker cannot stand in entropy-coded data (FF there is followed by
            // 00 or a restart marker), so its occurrences bound the scans from above
            long scans = 0;
            for (var p = data.AsSpan(afterHeader).IndexOf([(byte)0xFF, (byte)0xDA]); p >= 0;)
            {
                scans++;
                var next = data.AsSpan(afterHeader + p + 2).IndexOf([(byte)0xFF, (byte)0xDA]);
                p = next < 0 ? -1 : p + 2 + next;
            }
            if (_blocks * Math.Max(1, scans) > MaxBlockScans) throw new JpegException("zu viele Scans für die Bildgröße");
            var outputBytes = (long)Ceil(_width, _scale) * Ceil(_height, _scale) * n;
            var budget = options.Budget > 0 ? options.Budget : PdfLimits.Budget(4L << 30);
            if (planeBytes + coefficientBytes + outputBytes > budget) throw new JpegException("JPEG über dem Speicherbudget");
            foreach (var c in _components)
            {
                c.Stride = c.PlaneBlocksWide * _blockSize;
                if ((long)c.Stride * c.PlaneBlocksHigh * _blockSize > Array.MaxLength) throw new JpegException("Komponente zu groß");
                c.Plane = new byte[c.Stride * c.PlaneBlocksHigh * _blockSize];
                // never decoded (a scan missing): grey, as zero coefficients give
                Array.Fill(c.Plane, (byte)128);
                if (_progressive)
                {
                    long count = (long)c.PlaneBlocksWide * c.PlaneBlocksHigh * 64;
                    if (count > Array.MaxLength) throw new JpegException("Komponente zu groß");
                    c.Coefficients = new short[count];
                }
            }
            _frame = true;
        }

        /// <summary>
        /// The number of lines: from the header, from a DNL segment after the first scan if the header has 0 (T.81, B.2.5), or the /Height of
        /// the PDF where the header has none (S-A A15) or a placeholder of a scanner larger than the PDF's (No. 457) and no DNL says otherwise.
        /// </summary>
        private int Lines(int header, int afterHeader)
        {
            var pdf = options.PdfHeight is > 0 and <= 0xFFFF ? (int)options.PdfHeight : 0;
            if (header != 0 && !(header >= PlaceholderLines && pdf > 0 && header > pdf)) return header;
            var dnl = Dnl(afterHeader);
            if (header == 0)
            {
                if (dnl > 0) return dnl;
                if (pdf > 0) return pdf;
                throw new JpegException("Höhe 0 ohne DNL-Segment");
            }
            return dnl < 0 || dnl == pdf ? pdf : header;
        }

        /// <summary>The number of lines of the DNL segment right after the first scan; −1 if there is none.</summary>
        private int Dnl(int from)
        {
            var at = from;
            // to the first scan
            while (at + 3 < data.Length && data[at] == 0xFF)
            {
                var m = data[at + 1];
                if (m == 0xFF)
                {
                    at++;
                    continue;
                }
                var length = data[at + 2] << 8 | data[at + 3];
                if (length < 2) return -1;
                at += 2 + length;
                if (m == 0xDA) break;
            }
            // past its entropy-coded data and restart markers
            for (var p = JpegBits.NextMarker(data, at, data.Length); p >= 0; p = JpegBits.NextMarker(data, p + 2, data.Length))
            {
                var m = data[p + 1];
                if (m is >= 0xD0 and <= 0xD7) continue;
                if (m == 0xDC && p + 5 < data.Length && (data[p + 2] << 8 | data[p + 3]) == 4) return data[p + 4] << 8 | data[p + 5];
                return -1;
            }
            return -1;
        }

        private void Quantization(ReadOnlySpan<byte> s)
        {
            for (var at = 0; at < s.Length;)
            {
                int pq = s[at] >> 4, tq = s[at] & 15;
                if (pq > 1 || tq > 3) throw new JpegException("DQT ungültig");
                var size = pq == 0 ? 64 : 128;
                if (at + 1 + size > s.Length) throw new JpegException("DQT zu kurz");
                var table = new int[64];
                for (var k = 0; k < 64; k++)
                {
                    var q = pq == 0 ? s[at + 1 + k] : s[at + 1 + 2 * k] << 8 | s[at + 2 + 2 * k];
                    // a step of 0 is not allowed (B.2.4.1); taken as 1 rather than losing the coefficient
                    table[JpegEntropy.ZigZag[k]] = Math.Max(1, q);
                }
                _quant[tq] = table;
                at += 1 + size;
            }
        }

        private void Huffman(ReadOnlySpan<byte> s)
        {
            Span<int> counts = stackalloc int[17];
            for (var at = 0; at < s.Length;)
            {
                if (at + 17 > s.Length) throw new JpegException("DHT zu kurz");
                int tc = s[at] >> 4, th = s[at] & 15;
                if (tc > 1 || th > 3) throw new JpegException("DHT ungültig");
                var total = 0;
                for (var l = 1; l <= 16; l++) total += counts[l] = s[at + l];
                if (total > 256 || at + 17 + total > s.Length) throw new JpegException("DHT zu lang");
                var table = new JpegHuffmanTable(counts, s.Slice(at + 17, total).ToArray());
                if (tc == 0) _dcTables[th] = table;
                else _acTables[th] = table;
                at += 17 + total;
            }
        }

        /// <summary>DAC (T.81, B.2.4.3): conditioning of arithmetic coding — L and U for DC, Kx for AC.</summary>
        private void Conditioning(ReadOnlySpan<byte> s)
        {
            for (var at = 0; at + 1 < s.Length; at += 2)
            {
                int tc = s[at] >> 4, tb = s[at] & 15, cs = s[at + 1];
                if (tc > 1 || tb > 3) throw new JpegException("DAC ungültig");
                if (tc == 0)
                {
                    int l = cs & 15, u = cs >> 4;
                    if (l > u) throw new JpegException("DAC ungültig");
                    _arithDc[tb] = (byte)l;
                    _arithDcU[tb] = (byte)u;
                }
                else
                {
                    if (cs is < 1 or > 63) throw new JpegException("DAC ungültig");
                    _arithAcK[tb] = (byte)cs;
                }
            }
        }

        /// <summary>One scan (B.2.3); returns the position of the marker after it.</summary>
        private unsafe int Scan(ReadOnlySpan<byte> s, int dataStart)
        {
            if (s.Length < 1) throw new JpegException("SOS zu kurz");
            int ns = s[0];
            if (ns is < 1 or > 4 || s.Length < 4 + 2 * ns) throw new JpegException("SOS ungültig");
            // longer than Ls = 6 + 2 × Ns (Table B.3): read by its length, reported (S-A A119, FG 1011)
            if (s.Length > 4 + 2 * ns) _damaged = true;
            var comps = new Component[ns];
            var dc = new JpegHuffmanTable?[ns];
            var ac = new JpegHuffmanTable?[ns];
            var dcIndex = new int[ns];
            var acIndex = new int[ns];
            for (var i = 0; i < ns; i++)
            {
                int id = s[1 + 2 * i], td = s[2 + 2 * i] >> 4, ta = s[2 + 2 * i] & 15;
                var c = _components.FirstOrDefault(x => x.Id == id) ?? throw new JpegException("Scan nennt eine unbekannte Komponente");
                if (Array.IndexOf(comps, c) >= 0) throw new JpegException("Komponente doppelt im Scan");
                if (td > 3 || ta > 3) throw new JpegException("Tabellennummer außerhalb 0–3");
                // a baseline frame has the destinations 0, 1 (Table B.3): others read, reported (S-A A120, FG 1011)
                if (_baseline && (td > 1 || ta > 1)) _damaged = true;
                comps[i] = c;
                // a Huffman table no DHT segment defined: the typical table of T.81, K.3 (Motion-JPEG; S-A A102)
                (dc[i], ac[i], dcIndex[i], acIndex[i]) = (_dcTables[td] ?? JpegHuffmanTable.Standard(false, td), _acTables[ta] ?? JpegHuffmanTable.Standard(true, ta), td, ta);
            }
            int ss = s[1 + 2 * ns], se = s[2 + 2 * ns], ah = s[3 + 2 * ns] >> 4, al = s[3 + 2 * ns] & 15;
            var end = data.Length;
            if (!_progressive)
            {
                // sequential: Ss, Se, Ah, Al have fixed values; others are passed over as common decoders do
                (ss, se, ah, al) = (0, 63, 0, 0);
            }
            else if (ss > se || se > 63 || ah > 13 || al > 13 || (ss == 0) != (se == 0) || (ss > 0 && ns != 1) || (ah != 0 && ah != al + 1))
            {
                // an impossible progression (G.1.1.1.1): the scan is passed over
                _damaged = true;
                return SkipScan(dataStart);
            }

            // tables the scan needs must be there (Huffman); for a sequential scan also the quantisation tables
            if (!_arithmetic)
                for (var i = 0; i < ns; i++)
                {
                    var needsDc = !_progressive || (ss == 0 && ah == 0);
                    var needsAc = !_progressive ? true : ss > 0;
                    if ((needsDc && dc[i] is null) || (needsAc && ac[i] is null))
                    {
                        _damaged = true;
                        return SkipScan(dataStart);
                    }
                }
            // sequential: each component in a single scan (T.81, 3.1.116) — a repeated one is passed over, the first stays (crafted files repeat
            // empty scans over large images to cost time)
            if (!_progressive && Array.Exists(comps, c => c.Seen))
            {
                _damaged = true;
                return SkipScan(dataStart);
            }
            foreach (var c in comps)
            {
                if (_quant[c.Tq] is null)
                {
                    _damaged = true;
                    return SkipScan(dataStart);
                }
                if (_progressive && !c.Seen) c.Quant = (int[])_quant[c.Tq]!.Clone();
                c.Seen = true;
            }

            // work: blocks times scans (No. 742)
            long scanBlocks = 0;
            foreach (var c in comps) scanBlocks += (long)c.BlocksWide * c.BlocksHigh;
            _blockScans += scanBlocks;
            if (_blockScans > MaxBlockScans)
            {
                _damaged = true;
                return SkipScan(dataStart);
            }

            JpegEntropy entropy;
            if (_arithmetic)
                entropy = new JpegArithmetic(data, dataStart, end, dcIndex, acIndex, _arithDc, _arithDcU, _arithAcK);
            else
                entropy = new JpegHuffman(new JpegBits(data, dataStart, end), dc, ac);
            entropy.Reset();

            Span<short> block = stackalloc short[64];
            var coef = new int[64];
            var single = ns == 1;
            int mcusWide = single ? comps[0].BlocksWide : _mcusWide, mcusHigh = single ? comps[0].BlocksHigh : _mcusHigh;
            var mcus = (long)mcusWide * mcusHigh;
            var interval = _restartInterval;
            var restarts = 0;
            for (long m = 0; m < mcus; m++)
            {
                if (interval > 0 && m > 0 && m % interval == 0)
                {
                    if (!entropy.Restart(restarts++ & 7))
                    {
                        _damaged = true;
                        if (!_progressive) Grey(comps, single, mcusWide, m, mcus);
                        break;
                    }
                }
                if (entropy.Dead)
                {
                    // nothing more until the next restart marker: the blocks up to it have zero coefficients — grey in a sequential scan,
                    // unchanged in a progressive one — set at once instead of decoding each (crafted files repeat empty scans over large images)
                    var to = interval > 0 ? Math.Min(mcus, (m / interval + 1) * interval) : mcus;
                    if (!_progressive) Grey(comps, single, mcusWide, m, to);
                    m = to - 1;
                    continue;
                }
                int mx = (int)(m % mcusWide), my = (int)(m / mcusWide);
                for (var i = 0; i < ns; i++)
                {
                    var c = comps[i];
                    int bw = single ? 1 : c.H, bh = single ? 1 : c.V;
                    for (var by = 0; by < bh; by++)
                        for (var bx = 0; bx < bw; bx++)
                        {
                            int x = mx * bw + bx, y = my * bh + by;
                            if (_progressive)
                            {
                                var target = c.Coefficients.AsSpan((int)(((long)y * c.PlaneBlocksWide + x) * 64), 64);
                                if (ss == 0)
                                {
                                    if (ah == 0) entropy.DcFirst(i, target, al);
                                    else entropy.DcRefine(target, al);
                                }
                                else if (ah == 0)
                                {
                                    entropy.AcFirst(i, target, ss, se, al);
                                }
                                else
                                {
                                    entropy.AcRefine(i, target, ss, se, al);
                                }
                                continue;
                            }
                            entropy.Sequential(i, block);
                            // blocks outside the component (an interleaved MCU at the right or bottom edge) are decoded and dropped
                            if (x >= c.PlaneBlocksWide || y >= c.PlaneBlocksHigh)
                            {
                                block.Clear();
                                continue;
                            }
                            if (_scale == 1)
                            {
                                // at full size: dequantised inside the transform, straight into the plane
                                fixed (short* b = block)
                                fixed (int* q = _quant[c.Tq]!)
                                fixed (byte* plane = c.Plane)
                                    JpegIdct.TransformQuantised(b, q, plane + y * 8 * c.Stride + x * 8, c.Stride);
                            }
                            else
                            {
                                var q = _quant[c.Tq]!;
                                for (var k = 0; k < 64; k++) coef[k] = block[k] * q[k];
                                Output(c, x, y, coef);
                            }
                            block.Clear();
                        }
                }
            }
            var next = entropy.End();
            _damaged |= entropy.Damaged;
            return next;
        }

        /// <summary>
        /// The MCUs <paramref name="from"/> … <paramref name="to"/> (exclusive) of a sequential scan with zero coefficients: their blocks grey
        /// (128, what the inverse DCT of a zero block gives) — whole rows of MCUs at once, the rest block by block.
        /// </summary>
        private void Grey(Component[] comps, bool single, int mcusWide, long from, long to)
        {
            var bs = _blockSize;
            foreach (var c in comps)
            {
                int bw = single ? 1 : c.H, bh = single ? 1 : c.V;
                var planeRows = c.PlaneBlocksHigh * bs;
                for (var m = from; m < to;)
                {
                    long row = m / mcusWide;
                    var col = (int)(m % mcusWide);
                    var rowEnd = (row + 1) * mcusWide;
                    if (col == 0 && rowEnd <= to)
                    {
                        var y0 = (int)Math.Min(planeRows, row * bh * bs);
                        var y1 = (int)Math.Min(planeRows, (row + 1) * bh * bs);
                        if (y1 > y0) c.Plane.AsSpan(y0 * c.Stride, (y1 - y0) * c.Stride).Fill(128);
                        m = rowEnd;
                        continue;
                    }
                    for (var by = 0; by < bh; by++)
                        for (var bx = 0; bx < bw; bx++)
                        {
                            long x = (long)col * bw + bx, y = row * bh + by;
                            if (x >= c.PlaneBlocksWide || y >= c.PlaneBlocksHigh) continue;
                            for (var r = 0; r < bs; r++) c.Plane.AsSpan((int)((y * bs + r) * c.Stride + x * bs), bs).Fill(128);
                        }
                    m++;
                }
            }
        }

        /// <summary>Passes over the entropy-coded data of a scan that cannot be decoded.</summary>
        private int SkipScan(int from)
        {
            for (var p = JpegBits.NextMarker(data, from, data.Length); p >= 0; p = JpegBits.NextMarker(data, p + 2, data.Length))
                if (data[p + 1] is < 0xD0 or > 0xD7) return p;
            return data.Length;
        }

        /// <summary>Progressive: all blocks transformed with the tables latched at each component's first scan.</summary>
        private void Reconstruct()
        {
            var coef = new int[64];
            foreach (var c in _components)
            {
                if (c.Coefficients is not { } all || c.Quant is not { } q) continue;   // a component no scan named stays grey
                for (var y = 0; y < c.PlaneBlocksHigh; y++)
                    for (var x = 0; x < c.PlaneBlocksWide; x++)
                    {
                        var block = all.AsSpan((int)(((long)y * c.PlaneBlocksWide + x) * 64), 64);
                        for (var k = 0; k < 64; k++) coef[k] = block[k] * q[k];
                        Output(c, x, y, coef);
                    }
                c.Coefficients = null;
            }
        }

        /// <summary>The block at (<paramref name="bx"/>, <paramref name="by"/>) into the component's plane, reduced by the scale.</summary>
        private void Output(Component c, int bx, int by, int[] coef)
        {
            var bs = _blockSize;
            var offset = by * bs * c.Stride + bx * bs;
            switch (_scale)
            {
                case 1:
                    JpegIdct.Transform(coef, c.Plane, offset, c.Stride);
                    return;
                case 8:
                    c.Plane[offset] = JpegIdct.DcOnly(coef[0]);
                    return;
            }
            // 1/2 and 1/4: the full block, averaged
            Span<byte> full = stackalloc byte[64];
            JpegIdct.Transform(coef, full, 0, 8);
            var f = _scale;
            for (var y = 0; y < bs; y++)
                for (var x = 0; x < bs; x++)
                {
                    var sum = 0;
                    for (var j = 0; j < f; j++)
                        for (var i = 0; i < f; i++) sum += full[(y * f + j) * 8 + x * f + i];
                    c.Plane[offset + y * c.Stride + x] = (byte)((sum + f * f / 2) / (f * f));
                }
        }
    }
}
