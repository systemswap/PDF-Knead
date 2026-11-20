using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Printing;

/// <summary>A page to print: from its own file or from the flattened version (<see cref="SheetPdf.Flatten"/>).</summary>
/// <param name="Document">The document containing the page.</param>
/// <param name="Page">The page.</param>
public readonly record struct PrintPage(PdfDocument Document, PdfPage Page);

/// <summary>
/// The sheets to print as PDF: every printed page becomes a form XObject (vectors, images and fonts unchanged), and the sheets
/// compose them according to <see cref="PrintLayout"/> or <see cref="Booklet"/>. Used for CUPS and "save as PDF"; the preview and
/// Windows printing render the same sheets directly from the pages, because building a PDF for every option change would copy the
/// whole document. A test keeps both paths pixel-identical.
/// </summary>
public static class SheetPdf
{
    /// <summary>
    /// Pages as they are printed — fields, elements and annotations with the "print" flag fixed into the content. Without protection,
    /// in memory only. Only for pages that need it (<see cref="NeedsFlattening"/>); the others come directly from their file.
    /// Redactions are applied beforehand in the document („Schwärzen zuerst“); a mark that still arrives here is refused
    /// (<see cref="InDocumentRedaction.EnsureApplied"/>).
    /// </summary>
    /// <param name="main">The document the pages belong to.</param>
    /// <param name="pages">The pages to flatten.</param>
    /// <param name="codec">Image codec passed to the writer, or null.</param>
    /// <param name="cancel">Cancels writing.</param>
    /// <returns>A new in-memory document with the flattened pages; the caller disposes it.</returns>
    /// <exception cref="Pdf.PdfException">A page still carries a redaction mark.</exception>
    public static PdfDocument Flatten(PdfDocument main, IReadOnlyList<PageItem> pages, IImageCodec? codec = null, CancellationToken cancel = default)
    {
        InDocumentRedaction.EnsureApplied(pages);
        var buffer = new Pdf.IO.MemoryPdf();
        try
        {
            PdfWriter.Write(main, pages, buffer, new PdfSaveOptions
            {
                Flatten = true,
                ForPrint = true,
                KeepEncryption = false,
                ImageCodec = codec,
            }, cancel);
            return buffer.OpenDocument();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static readonly PdfName Annots = PdfName.Get("Annots");

    /// <summary>
    /// Whether the page must be flattened before printing: yes if something is placed on it (elements, fields, redactions) or it has
    /// annotations (when printing, these follow the Print flag, not the display). Otherwise it is printed as stored in the file.
    /// </summary>
    /// <param name="item">The page with its elements.</param>
    /// <returns>True if <see cref="Flatten"/> is needed.</returns>
    public static bool NeedsFlattening(PageItem item) =>
        item.Elements.Count > 0 || item.Page.Dictionary.Get(Annots) is PdfArray { Count: > 0 };

    /// <summary>
    /// Writes sheets as PDF. <see cref="SheetPlacement.Page"/> is the position in <paramref name="pages"/>; <paramref name="order"/>
    /// selects the sheets (−1 = blank page of the size of the preceding sheet, or of the first sheet if none precedes); null = all in
    /// order.
    /// </summary>
    /// <param name="pages">The pages referenced by the placements.</param>
    /// <param name="sheets">The sheets.</param>
    /// <param name="order">Sheet indices in output order, or null for all sheets.</param>
    /// <param name="output">Target stream.</param>
    /// <param name="cancel">Cancels writing.</param>
    public static void Write(IReadOnlyList<PrintPage> pages, IReadOnlyList<PrintSheet> sheets, IReadOnlyList<int>? order, Stream output,
        CancellationToken cancel = default) =>
        new Builder(output, cancel).Run(pages, sheets, order ?? [.. Enumerable.Range(0, sheets.Count)]);

    /// <summary>Writes sheets from the pages of <paramref name="flat"/>.</summary>
    /// <param name="flat">Document with the pages.</param>
    /// <param name="pageIndices">Maps <see cref="SheetPlacement.Page"/> to a zero-based page index in <paramref name="flat"/>.</param>
    /// <param name="sheets">The sheets, all written in order.</param>
    /// <param name="output">Target stream.</param>
    /// <param name="cancel">Cancels writing.</param>
    public static void Write(PdfDocument flat, IReadOnlyList<int> pageIndices, IReadOnlyList<PrintSheet> sheets, Stream output,
        CancellationToken cancel = default) =>
        Write([.. pageIndices.Select(i => new PrintPage(flat, flat.Pages[i]))], sheets, null, output, cancel);

    /// <summary>Like <see cref="Write(PdfDocument, IReadOnlyList{int}, IReadOnlyList{PrintSheet}, Stream, CancellationToken)"/>, as a document in memory.</summary>
    /// <param name="flat">Document with the pages.</param>
    /// <param name="pageIndices">Maps <see cref="SheetPlacement.Page"/> to a zero-based page index in <paramref name="flat"/>.</param>
    /// <param name="sheets">The sheets.</param>
    /// <param name="cancel">Cancels writing.</param>
    /// <returns>The sheet document; the caller disposes it.</returns>
    public static PdfDocument Build(PdfDocument flat, IReadOnlyList<int> pageIndices, IReadOnlyList<PrintSheet> sheets, CancellationToken cancel = default)
    {
        var buffer = new Pdf.IO.MemoryPdf();
        try
        {
            Write(flat, pageIndices, sheets, buffer, cancel);
            return buffer.OpenDocument();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Maps the page (crop box, rotation, /UserUnit) to its appearance with the origin at the lower left.</summary>
    internal static Matrix PageToVisual(PdfPage page)
    {
        var box = page.CropBox;
        double w = box.Width, h = box.Height;
        var rotate = page.Rotate switch
        {
            90 => new Matrix(0, -1, 1, 0, 0, w),
            180 => new Matrix(-1, 0, 0, -1, w, h),
            270 => new Matrix(0, 1, -1, 0, h, 0),
            _ => Matrix.Identity,
        };
        var unit = page.UserUnit is > 0 and < 1e4 ? page.UserUnit : 1;
        return Matrix.Translate(-box.Left, -box.Bottom) * rotate * Matrix.Scale(unit, unit);
    }

    private sealed class Builder(Stream output, CancellationToken cancel)
    {
        private static readonly PdfName Form = PdfName.Get("Form"), XObject = PdfName.Get("XObject"), BBox = PdfName.Get("BBox"),
            MatrixKey = PdfName.Get("Matrix"), Group = PdfName.Get("Group"), OCProperties = PdfName.Get("OCProperties"),
            FlateDecode = PdfName.Get("FlateDecode"), Annot = PdfName.Get("Annot");

        private readonly List<long> _offsets = [-1, -1];   // 1 = catalogue, 2 = page tree
        private readonly Dictionary<(PdfDocument, int), int> _map = [];
        private readonly Dictionary<PdfDocument, PdfSerializer.ReferenceMap> _mappers = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<(PdfDocument Doc, int Number)> _pending = new();
        private readonly ByteBuffer _buffer = new(1 << 12);
        private long _position;

        public void Run(IReadOnlyList<PrintPage> pages, IReadOnlyList<PrintSheet> sheets, IReadOnlyList<int> order)
        {
            Write("%PDF-1.7\n"u8);
            Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

            // One form XObject per page used (a page on several sheets: only once)
            var forms = new Dictionary<int, int>();
            var kids = new List<int>(order.Count);
            var last = sheets.Count > 0 ? sheets[order.FirstOrDefault(s => s >= 0 && s < sheets.Count, 0)] : null;
            foreach (var index in order)
            {
                cancel.ThrowIfCancellationRequested();
                var sheet = index >= 0 && index < sheets.Count ? sheets[index] : null;
                var (width, height) = sheet is not null ? (sheet.Width, sheet.Height) : last is not null ? (last.Width, last.Height) : (595.28, 841.89);
                last = sheet ?? last;
                var names = new StringBuilder();
                var content = new StringBuilder();
                foreach (var p in sheet?.Placements ?? [])
                {
                    if (p.Page < 0 || p.Page >= pages.Count) continue;
                    if (!forms.TryGetValue(p.Page, out var form)) forms[p.Page] = form = PageForm(pages[p.Page]);
                    names.Append("/P").Append(form).Append(' ').Append(form).Append(" 0 R");
                    content.Append("q ");
                    // Poster: only the tile
                    if (p.Clip is { } clip)
                        content.Append(Real(clip.Left)).Append(' ').Append(Real(clip.Bottom)).Append(' ').Append(Real(clip.Width)).Append(' ')
                            .Append(Real(clip.Height)).Append(" re W n ");
                    content.Append(Real(p.Scale)).Append(" 0 0 ").Append(Real(p.Scale)).Append(' ')
                        .Append(Real(p.X)).Append(' ').Append(Real(p.Y)).Append(" cm /P").Append(form).Append(" Do Q\n");
                    if (sheet!.Borders)
                        content.Append("q 0.4 G 0.5 w ").Append(Real(p.X)).Append(' ').Append(Real(p.Y)).Append(' ')
                            .Append(Real(p.Width * p.Scale)).Append(' ').Append(Real(p.Height * p.Scale)).Append(" re S Q\n");
                }
                var font = sheet?.Marks is { Count: > 0 } marks && AppendMarks(content, marks);
                var stream = NewObject();
                var data = Encoding.Latin1.GetBytes(content.ToString());
                Write($"{stream} 0 obj\n<</Length {data.Length}>>\nstream\n");
                Write(data);
                Write("\nendstream\nendobj\n");
                var page = NewObject();
                Write($"{page} 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {Real(width)} {Real(height)}]" +
                      $"/Resources<</XObject<<{names}>>{(font ? "/Font<</M<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>>>" : "")}>>" +
                      $"/Contents {stream} 0 R>>\nendobj\n");
                kids.Add(page);
            }

            // Copy what the forms need (fonts, images …) from the sources
            Drain();

            _offsets[1] = _position;
            Write($"2 0 obj\n<</Type/Pages/Count {kids.Count}/Kids[{string.Join(' ', kids.Select(k => $"{k} 0 R"))}]>>\nendobj\n");
            // Layers: without /OCProperties, hidden layers would count as visible and end up on paper
            var catalog = new PdfDictionary();
            catalog.Set(PdfName.Type, PdfName.Catalog);
            catalog.Set(PdfName.Pages, new OutputReference(2));
            var layers = pages.Select(p => p.Document).Distinct().FirstOrDefault(d => d.Catalog?.GetDictionary(OCProperties) is not null);
            if (layers is not null && Pdf.Content.OptionalContent.ForPrintConfig(layers) is { } config) catalog.Set(OCProperties, config);
            _offsets[0] = _position;
            WriteObject(1, catalog, layers);
            Drain();

            var xref = _position;
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(_offsets.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (var offset in _offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<</Size ").Append(_offsets.Count + 1).Append("/Root 1 0 R>>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            Write(sb.ToString());
            output.Flush();
        }

        /// <summary>Appends marks (poster) as content: hairlines, registration marks, text in Helvetica. Returns true if text is included (it needs the font).</summary>
        private static bool AppendMarks(StringBuilder content, IReadOnlyList<SheetMark> marks)
        {
            var text = false;
            content.Append("q 0 G 0 g ").Append(Real(MarkStroke)).Append(" w\n");
            foreach (var m in marks)
                switch (m.Kind)
                {
                    case SheetMarkKind.Line:
                        content.Append(Real(m.X)).Append(' ').Append(Real(m.Y)).Append(" m ").Append(Real(m.X2)).Append(' ').Append(Real(m.Y2)).Append(" l S\n");
                        break;
                    case SheetMarkKind.Cross:
                        var half = m.Size / 2;
                        content.Append(Real(m.X - half)).Append(' ').Append(Real(m.Y)).Append(" m ").Append(Real(m.X + half)).Append(' ').Append(Real(m.Y)).Append(" l ")
                            .Append(Real(m.X)).Append(' ').Append(Real(m.Y - half)).Append(" m ").Append(Real(m.X)).Append(' ').Append(Real(m.Y + half)).Append(" l S\n");
                        Circle(content, m.X, m.Y, half * 0.6);
                        break;
                    case SheetMarkKind.Text when m.Text is { Length: > 0 } t:
                        content.Append("BT /M ").Append(Real(m.Size)).Append(" Tf ").Append(Real(m.X)).Append(' ').Append(Real(m.Y)).Append(" Td (");
                        foreach (var c in t)
                        {
                            // WinAnsi: Latin-1 apart from a few characters; "·" and umlauts work as is, "–" and "×" are mapped, everything else becomes "?"
                            var b = c switch { '–' => (char)0x96, '×' => (char)0xD7, _ when c <= 0xFF => c, _ => '?' };
                            if (b is '(' or ')' or '\\') content.Append('\\');
                            content.Append(b);
                        }
                        content.Append(") Tj ET\n");
                        text = true;
                        break;
                }
            content.Append("Q\n");
            return text;
        }

        /// <summary>Circle made of four Bézier curves.</summary>
        private static void Circle(StringBuilder content, double x, double y, double r)
        {
            const double k = 0.5523;
            content.Append(Real(x + r)).Append(' ').Append(Real(y)).Append(" m ")
                .Append(Real(x + r)).Append(' ').Append(Real(y + k * r)).Append(' ').Append(Real(x + k * r)).Append(' ').Append(Real(y + r)).Append(' ').Append(Real(x)).Append(' ').Append(Real(y + r)).Append(" c ")
                .Append(Real(x - k * r)).Append(' ').Append(Real(y + r)).Append(' ').Append(Real(x - r)).Append(' ').Append(Real(y + k * r)).Append(' ').Append(Real(x - r)).Append(' ').Append(Real(y)).Append(" c ")
                .Append(Real(x - r)).Append(' ').Append(Real(y - k * r)).Append(' ').Append(Real(x - k * r)).Append(' ').Append(Real(y - r)).Append(' ').Append(Real(x)).Append(' ').Append(Real(y - r)).Append(" c ")
                .Append(Real(x + k * r)).Append(' ').Append(Real(y - r)).Append(' ').Append(Real(x + r)).Append(' ').Append(Real(y - k * r)).Append(' ').Append(Real(x + r)).Append(' ').Append(Real(y)).Append(" c S\n");
        }

        /// <summary>Line width of the marks in points (as in the preview).</summary>
        private const double MarkStroke = 0.4;

        private void Drain()
        {
            while (_pending.TryDequeue(out var next))
            {
                cancel.ThrowIfCancellationRequested();
                CopyObject(next.Doc, next.Number);
            }
        }

        /// <summary>
        /// Page as a form XObject: its resources, box and rotation. A single content stream is copied raw (including its filter), because
        /// decoding and recompressing it costs a lot (A0 plan: 209 MB, 200 ms); several streams are decoded and compressed one after
        /// another into one.
        /// </summary>
        private int PageForm(PrintPage source)
        {
            var (doc, page) = source;
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, XObject);
            dict.Set(PdfName.Subtype, Form);
            var box = page.CropBox;
            dict.Set(BBox, new PdfArray([new PdfReal(box.Left), new PdfReal(box.Bottom), new PdfReal(box.Right), new PdfReal(box.Top)]));
            var m = PageToVisual(page);
            dict.Set(MatrixKey, new PdfArray([new PdfReal(m.A), new PdfReal(m.B), new PdfReal(m.C), new PdfReal(m.D), new PdfReal(m.E), new PdfReal(m.F)]));
            if (page.Resources is { } resources) dict.Set(PdfName.Resources, resources);
            if (page.Dictionary.GetRaw(Group) is { } group) dict.Set(Group, group);
            var number = NewObject();

            if (page.Dictionary.Get(PdfName.Contents) is PdfStream single && RawCopyable(doc, single))
            {
                ReadOnlySpan<byte> raw = doc.IsStoredPlain(single) ? doc.StoredStreamData(single) : doc.GetRawStreamData(single);
                if (single.Dictionary.GetRaw(PdfName.Filter) is { } filter) dict.Set(PdfName.Filter, filter);
                if (single.Dictionary.GetRaw(PdfName.DecodeParms) is { } parms) dict.Set(PdfName.DecodeParms, parms);
                dict.Set(PdfName.Length, PdfInteger.Of(raw.Length));
                WriteStream(number, dict, raw, doc);
                return number;
            }

            using var data = new MemoryStream();
            using (var z = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
            {
                switch (page.Dictionary.Get(PdfName.Contents))
                {
                    case PdfStream stream:
                        using (var decoded = doc.DecodeStreamPooled(stream)) z.Write(decoded.Span);
                        break;
                    case PdfArray parts:
                        for (var i = 0; i < parts.Count; i++)
                            if (parts.Get(i) is PdfStream part)
                            {
                                cancel.ThrowIfCancellationRequested();
                                // Parts may end in the middle of an operator: line break between them (7.8.2)
                                using (var decoded = doc.DecodeStreamPooled(part)) z.Write(decoded.Span);
                                z.WriteByte((byte)'\n');
                            }
                        break;
                }
            }
            dict.Set(PdfName.Filter, FlateDecode);
            dict.Set(PdfName.Length, PdfInteger.Of(data.Length));
            WriteStream(number, dict, data.GetBuffer().AsSpan(0, (int)data.Length), doc);
            return number;
        }

        /// <summary>A raw copy is possible if there is no Crypt filter in the chain and the data lies in the file (no /F).</summary>
        private static bool RawCopyable(PdfDocument doc, PdfStream stream) =>
            stream.Dictionary.Get(PdfName.F) is null && !StreamFilters.Chain(stream.Dictionary).Any(f => f.Name == "Crypt");

        /// <summary>Writes an object of a source under its new number; streams with their compressed data.</summary>
        private void CopyObject(PdfDocument doc, int number)
        {
            var target = _map[(doc, number)];
            var obj = doc.GetObject(number);
            if (obj is PdfStream stream)
            {
                var dict = new PdfDictionary(stream.Dictionary.Count);
                foreach (var (key, value) in stream.Dictionary.Entries)
                    if (key != PdfName.Length) dict.Set(key, value);
                if (RawCopyable(doc, stream) && doc.IsStoredPlain(stream))
                {
                    // Directly from the memory-mapped file without a copy (otherwise each image would be copied in full into memory)
                    var span = doc.StoredStreamData(stream);
                    dict.Set(PdfName.Length, PdfInteger.Of(span.Length));
                    WriteStream(target, dict, span, doc);
                    return;
                }
                var raw = doc.GetRawStreamData(stream);
                if (!RawCopyable(doc, stream))
                {
                    // Crypt filter or external data: written decoded, without a filter chain
                    raw = doc.GetStreamData(stream);
                    dict.Remove(PdfName.Filter);
                    dict.Remove(PdfName.DecodeParms);
                }
                dict.Set(PdfName.Length, PdfInteger.Of(raw.Length));
                WriteStream(target, dict, raw, doc);
            }
            else
            {
                WriteObject(target, obj, doc);
            }
        }

        private PdfSerializer.ReferenceMap MapperFor(PdfDocument doc)
        {
            if (!_mappers.TryGetValue(doc, out var mapper)) _mappers[doc] = mapper = id => Map(doc, id);
            return mapper;
        }

        /// <summary>
        /// New number for a reference of the source (queued for copying the first time). Never for pages, page tree, catalogue and
        /// annotations — otherwise /Parent or /P would pull in the whole document; "null" is written there instead. Nor for the /Encrypt
        /// object.
        /// </summary>
        private int Map(PdfDocument doc, PdfObjectId id)
        {
            if (_map.TryGetValue((doc, id.Number), out var mapped)) return mapped;
            if (id.Number == doc.EncryptNumber) return _map[(doc, id.Number)] = 0;
            var obj = doc.Resolve(id);
            if (obj is PdfNull || obj is PdfDictionary d && (d.IsType(PdfName.Page) || d.IsType(PdfName.Pages) || d.IsType(PdfName.Catalog) || d.IsType(Annot)))
                return _map[(doc, id.Number)] = 0;
            var number = NewObject(reserveOnly: true);
            _map[(doc, id.Number)] = number;
            _pending.Enqueue((doc, id.Number));
            return number;
        }

        private void WriteObject(int number, PdfObject obj, PdfDocument? doc)
        {
            _offsets[number - 1] = _position;
            _buffer.Clear();
            _buffer.Ascii($"{number} 0 obj\n");
            PdfSerializer.Write(_buffer, obj, doc is null ? null : MapperFor(doc), null);
            _buffer.Ascii("\nendobj\n");
            Write(_buffer.Written);
        }

        private void WriteStream(int number, PdfDictionary dict, ReadOnlySpan<byte> data, PdfDocument doc)
        {
            _offsets[number - 1] = _position;
            _buffer.Clear();
            _buffer.Ascii($"{number} 0 obj\n");
            PdfSerializer.Write(_buffer, dict, MapperFor(doc), null);
            _buffer.Ascii("\nstream\n");
            Write(_buffer.Written);
            Write(data);
            Write("\nendstream\nendobj\n"u8);
        }

        private int NewObject(bool reserveOnly = false)
        {
            _offsets.Add(reserveOnly ? -1 : _position);
            return _offsets.Count;
        }

        private void Write(string text) => Write(Encoding.ASCII.GetBytes(text));

        private void Write(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            _position += bytes.Length;
        }

        private static string Real(double value) => PdfSerializer.FormatReal(Math.Round(value, 4));
    }
}
