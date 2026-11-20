namespace PdfKnead.Core.Pdf.Filters.Jbig2;

/// <summary>
/// JBIG2Decode (ISO 32000-2, 7.4.7; ITU-T T.88): embedded segments (without file header), plus
/// the shared segments from /JBIG2Globals. Typical for scans from copiers and for PDF files
/// optimised for scans: text as symbol dictionary + text region, images as
/// generic or halftone region. Output like an unfiltered 1-bit image (0 = black).
/// </summary>
internal static class Jbig2Decoder
{
    /// <summary>
    /// Decodes the page and fits it to the image size (missing parts white). For damaged data the result is
    /// what was decoded up to that point, and <paramref name="truncated"/> is set.
    /// </summary>
    public static byte[] Decode(byte[] data, byte[]? globals, int width, int height, ref bool truncated)
    {
        var document = new Document((long)Math.Max(0, width) * Math.Max(0, height), (globals?.Length ?? 0) + (long)data.Length);
        try
        {
            if (globals is { Length: > 0 }) document.Run(globals);
            document.Run(data);
        }
        catch (Jbig2Exception)
        {
            truncated = true;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            // Nonsensical values that no check anticipated: treat them like damaged data
            truncated = true;
        }

        var rowBytes = (width + 7) >> 3;
        StreamFilters.CheckSize((long)rowBytes * height);
        var output = new byte[rowBytes * height];
        output.AsSpan().Fill(0xFF);
        if (document.Page is { } page)
        {
            var copy = Math.Min(rowBytes, page.Stride);
            for (var y = 0; y < Math.Min(height, page.Height); y++)
            {
                var src = page.Data.AsSpan(y * page.Stride, copy);
                var dst = output.AsSpan(y * rowBytes, copy);
                // JBIG2: 1 = black; PDF image data: 0 = black
                for (var i = 0; i < copy; i++) dst[i] = (byte)~src[i];
            }
        }
        else
        {
            truncated = true;
        }
        return output;
    }

    private sealed record Segment(uint Number, int Type, uint[] Referred, long Length)
    {
        public int Start;
        public int End;
        public int RowCount = -1;   // unknown length (generic region): row count from the end marker
    }

    private sealed record RegionInfo(int Width, int Height, long X, long Y, int Operator);

    private sealed class Document(long imagePixels, long dataLength)
    {
        private readonly Dictionary<uint, object> _results = [];
        private readonly Jbig2Budget _budget = new(imagePixels, dataLength);
        private bool _pageSeen;
        private bool _unknownHeight;
        private int _defaultPixel;
        private int _defaultOperator;
        private bool _operatorOverride;

        public Jbig2Bitmap? Page { get; private set; }

        public void Run(byte[] d)
        {
            var p = 0;
            var end = d.Length;
            var randomAccess = false;
            // Stand-alone JBIG2 file (not intended in PDF files, but it occurs): skip the header
            if (d.Length >= 9 && d[0] == 0x97 && d[1] == 0x4A && d[2] == 0x42 && d[3] == 0x32 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A)
            {
                var flags = d[8];
                randomAccess = (flags & 1) == 0;
                p = 9 + ((flags & 2) == 0 ? 4 : 0);
            }

            if (randomAccess)
            {
                // First all headers, then the data in the same order
                var segments = new List<Segment>();
                while (p < end)
                {
                    var s = ReadHeader(d, ref p, end);
                    if (s.Length == 0xFFFFFFFF) throw new Jbig2Exception("JBIG2: Länge fehlt");
                    segments.Add(s);
                    if (s.Type == 51) break;
                }
                foreach (var s in segments)
                {
                    s.Start = p;
                    s.End = (int)Math.Min(end, p + s.Length);
                    p = s.End;
                    if (Process(s, d)) return;
                }
                return;
            }

            while (p + 11 <= end)
            {
                var s = ReadHeader(d, ref p, end);
                s.Start = p;
                if (s.Length == 0xFFFFFFFF) FindGenericEnd(s, d, end);
                else s.End = (int)Math.Min(end, p + s.Length);
                p = s.RowCount >= 0 ? s.End + 6 : s.End;
                if (Process(s, d)) return;
            }
        }

        // ------------------------------------------------------------ Segment header (ITU-T T.88, 7.2)

        private static Segment ReadHeader(byte[] d, ref int p, int end)
        {
            var number = Jbig2Read.U32(d, p, end);
            var flags = Jbig2Read.U8(d, p + 4, end);
            p += 5;
            var count = Jbig2Read.U8(d, p, end) >> 5;
            if (count == 7)
            {
                count = Jbig2Read.Count(Jbig2Read.U32(d, p, end) & 0x1FFFFFFF, 1 << 16);
                p += 4 + (count + 8) / 8;   // the count, then one retention bit per referred segment
            }
            else
            {
                p++;
            }
            var size = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
            var referred = new uint[count];
            for (var i = 0; i < count; i++)
            {
                referred[i] = size switch
                {
                    1 => (uint)Jbig2Read.U8(d, p, end),
                    2 => (uint)Jbig2Read.U16(d, p, end),
                    _ => Jbig2Read.U32(d, p, end),
                };
                p += size;
            }
            p += (flags & 0x40) != 0 ? 4 : 1;   // page association
            var length = Jbig2Read.U32(d, p, end);
            p += 4;
            return new Segment(number, flags & 0x3F, referred, length);
        }

        /// <summary>
        /// Generic region without a length (ITU-T T.88, 7.2.7): the end is 0xFF 0xAC (arithmetic) or
        /// 0x00 0x00 (MMR), followed by the row count.
        /// </summary>
        private static void FindGenericEnd(Segment s, byte[] d, int end)
        {
            if (s.Type is not (36 or 38 or 39)) throw new Jbig2Exception("JBIG2: Länge fehlt");
            var info = ReadRegionInfo(d, s.Start, end);
            var flags = Jbig2Read.U8(d, s.Start + 17, end);
            var mmr = (flags & 1) != 0;
            RefuseExtendedTemplate(flags);
            var search = s.Start + 18 + (mmr ? 0 : ((flags >> 1) & 3) == 0 ? 8 : 2);
            for (var i = search; i + 6 <= end; i++)
            {
                if (mmr ? d[i] != 0 || d[i + 1] != 0 : d[i] != 0xFF || d[i + 1] != 0xAC) continue;
                var rows = Jbig2Read.U32(d, i + 2, end);
                if (mmr && info.Height >= 0 ? rows != info.Height : rows > 1 << 24) continue;
                s.End = i;
                s.RowCount = (int)rows;
                return;
            }
            throw new Jbig2Exception("JBIG2: Ende der Region fehlt");
        }

        /// <summary>
        /// The extended template of a generic region (T.88 Amd. 2, 7.4.6.2 bit 4: twelve AT pixels instead of four) is not implemented: read as the
        /// ordinary one, the AT pixels and the coded data would be taken from the wrong bytes and the image drawn wrongly without notice — a
        /// reader that knows it would show something else. Refused, the image counts as unreadable (docs/FEHLERGEDAECHTNIS.md, No. 730).
        /// </summary>
        private static void RefuseExtendedTemplate(int flags)
        {
            if ((flags & 1) == 0 && ((flags >> 1) & 3) == 0 && (flags & 0x10) != 0)
                throw new Jbig2Exception("JBIG2: erweitertes Muster (EXTTEMPLATE) wird nicht unterstützt");
        }

        private static RegionInfo ReadRegionInfo(byte[] d, int p, int end)
        {
            var width = Jbig2Read.Count(Jbig2Read.U32(d, p, end), 1 << 24);
            var h = Jbig2Read.U32(d, p + 4, end);
            var height = h == 0xFFFFFFFF ? -1 : Jbig2Read.Count(h, 1 << 24);
            var flags = Jbig2Read.U8(d, p + 16, end);
            // the colour extension (T.88 Amd. 3, 7.4.1.5 bit 3) adds data PDF does not allow (ISO 32000-2, 7.4.7): refused rather than read wrongly
            // (docs/FEHLERGEDAECHTNIS.md, No. 730)
            if ((flags & 0x08) != 0) throw new Jbig2Exception("JBIG2: Farberweiterung (in PDF nicht erlaubt)");
            return new RegionInfo(width, height, Jbig2Read.U32(d, p + 8, end), Jbig2Read.U32(d, p + 12, end), flags & 7);
        }

        // ------------------------------------------------------------ Segments (ITU-T T.88, 7.4)

        /// <summary>Processes one segment; true = end of page or file.</summary>
        private bool Process(Segment s, byte[] d)
        {
            int p = s.Start, end = s.End;
            switch (s.Type)
            {
                case 0:
                {
                    var input = new List<Jbig2Bitmap>();
                    Jbig2SymbolDictionary? last = null;
                    foreach (var r in Referred<Jbig2SymbolDictionary>(s))
                    {
                        input.AddRange(r.Symbols);
                        last = r;
                    }
                    _results[s.Number] = Jbig2Regions.SymbolDictionary(d, p, end, input, Tables(s), last, _budget);
                    break;
                }
                case 4 or 6 or 7:
                    Region(s, TextRegion(s, d, p, end, out var textInfo), textInfo);
                    break;
                case 16:
                    _results[s.Number] = Jbig2Regions.PatternDictionary(d, p, end, _budget);
                    break;
                case 20 or 22 or 23:
                {
                    var info = ReadRegionInfo(d, p, end);
                    var patterns = Referred<List<Jbig2Bitmap>>(s).FirstOrDefault() ?? throw new Jbig2Exception("JBIG2: Muster fehlen");
                    Region(s, Jbig2Regions.Halftone(d, p + 17, end, info.Width, Math.Max(0, info.Height), patterns, _budget), info);
                    break;
                }
                case 36 or 38 or 39:
                    Region(s, GenericRegion(s, d, p, end, out var genericInfo), genericInfo);
                    break;
                case 40 or 42 or 43:
                    Region(s, RefinementRegion(s, d, p, end, out var refineInfo), refineInfo);
                    break;
                case 48:
                    if (_pageSeen) return true;   // second page: a PDF image holds only the first
                    PageInformation(d, p, end);
                    break;
                case 49:
                    return true;
                case 50:
                    // End of stripe: with an unknown page height the page grows up to here
                    if (_unknownHeight) EnsureHeight(Jbig2Read.U32(d, p, end) + 1L);
                    break;
                case 51:
                    return true;
                case 53:
                    _results[s.Number] = Jbig2Huffman.FromSegment(d, p, end);
                    break;
            }
            return false;
        }

        private IEnumerable<T> Referred<T>(Segment s) where T : class
        {
            foreach (var number in s.Referred)
                if (_results.TryGetValue(number, out var result) && result is T t)
                    yield return t;
        }

        private Queue<Jbig2Huffman> Tables(Segment s) => new(Referred<Jbig2Huffman>(s));

        private void PageInformation(byte[] d, int p, int end)
        {
            var width = Jbig2Read.Count(Jbig2Read.U32(d, p, end), 1 << 24);
            var h = Jbig2Read.U32(d, p + 4, end);
            var flags = Jbig2Read.U8(d, p + 16, end);
            _defaultPixel = (flags >> 2) & 1;
            _defaultOperator = (flags >> 3) & 3;
            _operatorOverride = (flags & 0x40) != 0;
            _unknownHeight = h == 0xFFFFFFFF;
            _budget.Allocate(width, _unknownHeight ? 0 : Jbig2Read.Count(h, 1 << 24));
            Page = new Jbig2Bitmap(width, _unknownHeight ? 0 : Jbig2Read.Count(h, 1 << 24), _defaultPixel);
            _pageSeen = true;
        }

        private void EnsureHeight(long height)
        {
            if (Page is null || height <= Page.Height) return;
            if (height > 1 << 24) throw new Jbig2Exception("JBIG2: Seite zu hoch");
            _budget.Allocate(Page.Width, height);
            var grown = new Jbig2Bitmap(Page.Width, (int)height, _defaultPixel);
            Array.Copy(Page.Data, grown.Data, Page.Data.Length);
            Page = grown;
        }

        /// <summary>Keeps an intermediate result (types 4, 20, 36, 40) or composes the region onto the page.</summary>
        private void Region(Segment s, Jbig2Bitmap bitmap, RegionInfo info)
        {
            if (s.Type is 4 or 20 or 36 or 40)
            {
                _results[s.Number] = bitmap;
                return;
            }
            if (Page is null) throw new Jbig2Exception("JBIG2: Seiteninformation fehlt");
            if (_unknownHeight) EnsureHeight(info.Y + bitmap.Height);
            Page.Compose(bitmap, info.X, info.Y, _operatorOverride ? info.Operator : _defaultOperator);
        }

        private Jbig2Bitmap GenericRegion(Segment s, byte[] d, int p, int end, out RegionInfo info)
        {
            info = ReadRegionInfo(d, p, end);
            var flags = Jbig2Read.U8(d, p + 17, end);
            p += 18;
            var mmr = (flags & 1) != 0;
            var template = (flags >> 1) & 3;
            var tpgdon = (flags & 8) != 0;
            RefuseExtendedTemplate(flags);
            var at = new int[mmr ? 0 : template == 0 ? 8 : 2];
            for (var i = 0; i < at.Length; i++) at[i] = Jbig2Read.I8(d, p++, end);
            var height = info.Height;
            if (s.RowCount >= 0 && (height < 0 || s.RowCount < height)) height = s.RowCount;
            if (height < 0) throw new Jbig2Exception("JBIG2: Höhe fehlt");
            info = info with { Height = height };
            return mmr
                ? Jbig2Regions.Mmr(d, p, end, info.Width, height, out _, _budget)
                : Jbig2Regions.Generic(new MqDecoder(d, p, end), new byte[1 << 16], info.Width, height, template, tpgdon, null, at, _budget);
        }

        private Jbig2Bitmap RefinementRegion(Segment s, byte[] d, int p, int end, out RegionInfo info)
        {
            info = ReadRegionInfo(d, p, end);
            if (info.Height < 0) throw new Jbig2Exception("JBIG2: Höhe fehlt");
            var flags = Jbig2Read.U8(d, p + 17, end);
            p += 18;
            var template = flags & 1;
            var tpgron = (flags & 2) != 0;
            int[] at = [-1, -1, -1, -1];
            if (template == 0)
                for (var i = 0; i < 4; i++) at[i] = Jbig2Read.I8(d, p++, end);

            // Reference: the referred intermediate result, otherwise the section of the page (ITU-T T.88, 7.4.7.5)
            var reference = Referred<Jbig2Bitmap>(s).FirstOrDefault();
            if (reference is null)
            {
                if (Page is null) throw new Jbig2Exception("JBIG2: Seiteninformation fehlt");
                if (_unknownHeight) EnsureHeight(info.Y + info.Height);
                reference = Page.Crop((int)Math.Min(info.X, int.MaxValue), (int)Math.Min(info.Y, int.MaxValue), info.Width, info.Height);
            }
            return Jbig2Regions.Refine(new MqDecoder(d, p, end), new byte[1 << 13], info.Width, info.Height, template, reference, 0, 0, tpgron, at, _budget);
        }

        private Jbig2Bitmap TextRegion(Segment s, byte[] d, int p, int end, out RegionInfo info)
        {
            info = ReadRegionInfo(d, p, end);
            if (info.Height < 0) throw new Jbig2Exception("JBIG2: Höhe fehlt");
            var flags = Jbig2Read.U16(d, p + 17, end);
            p += 19;
            var huff = (flags & 1) != 0;
            var refine = (flags & 2) != 0;
            var dsOffset = (flags >> 10) & 0x1F;
            if (dsOffset >= 16) dsOffset -= 32;
            var text = new Jbig2TextParams
            {
                Huffman = huff,
                Refine = refine,
                Width = info.Width,
                Height = info.Height,
                LogStrips = (flags >> 2) & 3,
                RefCorner = (flags >> 4) & 3,
                Transposed = (flags & 0x40) != 0,
                Operator = (flags >> 7) & 3,
                DefaultPixel = (flags >> 9) & 1,
                DsOffset = dsOffset,
                RefineTemplate = (flags >> 15) & 1,
            };
            var huffFlags = 0;
            if (huff)
            {
                huffFlags = Jbig2Read.U16(d, p, end);
                p += 2;
            }
            if (refine && text.RefineTemplate == 0)
            {
                text.RefineAt = new int[4];
                for (var i = 0; i < 4; i++) text.RefineAt[i] = Jbig2Read.I8(d, p++, end);
            }
            text.Instances = Jbig2Read.Count(Jbig2Read.U32(d, p, end), 1 << 24);
            p += 4;

            var symbols = new List<Jbig2Bitmap>();
            foreach (var r in Referred<Jbig2SymbolDictionary>(s)) symbols.AddRange(r.Symbols);
            text.Symbols = symbols;
            text.SymbolCodeLength = Jbig2Read.CeilLog2(symbols.Count);

            var cx = new Jbig2Contexts();
            if (!huff) return Jbig2Regions.Text(text, new MqDecoder(d, p, end), cx, null, d, end, _budget);

            // Huffman tables (ITU-T T.88, 7.4.3.1.6): standard tables, or taken in order from the table segments
            var custom = Tables(s);
            Jbig2Huffman Pick(int selector, int[] standard) =>
                selector < standard.Length && standard[selector] > 0 ? Jbig2Huffman.Standard(standard[selector])
                : custom.Count > 0 ? custom.Dequeue() : throw new Jbig2Exception("JBIG2: Tabelle fehlt");
            text.Fs = Pick(huffFlags & 3, [6, 7, 0]);
            text.Ds = Pick((huffFlags >> 2) & 3, [8, 9, 10]);
            text.Dt = Pick((huffFlags >> 4) & 3, [11, 12, 13]);
            text.Rdw = Pick((huffFlags >> 6) & 3, [14, 15, 0]);
            text.Rdh = Pick((huffFlags >> 8) & 3, [14, 15, 0]);
            text.Rdx = Pick((huffFlags >> 10) & 3, [14, 15, 0]);
            text.Rdy = Pick((huffFlags >> 12) & 3, [14, 15, 0]);
            text.RSize = Pick((huffFlags >> 14) & 1, [1]);
            var hr = new Jbig2BitReader(d, p, end);
            text.SymbolTable = Jbig2Regions.SymbolIdTable(hr, symbols.Count);
            return Jbig2Regions.Text(text, null, cx, hr, d, end, _budget);
        }
    }
}
