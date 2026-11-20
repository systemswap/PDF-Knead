using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    /// <summary>
    /// Compression: thumbnails and private data of other applications are dropped, object streams are always used (from
    /// PDF 1.5; older versions are raised for this) — except for a PDF/A-1 file written as 1.4: PDF/A-1 knows no object streams, and raised
    /// to 1.5 the copy would lose its identification (phase 9.1, No. 708); it stays 1.4 and is packed without them, as when writing as PDF/A-1.
    /// </summary>
    private static PdfSaveOptions WithCompression(PdfSaveOptions options, Document.PdfDocument main) => options.Compression is null
        ? options
        : options with
        {
            Compression = options.Compression.Checked(),
            Cleanup = options.Cleanup | CleanupItems.Thumbnails | CleanupItems.PrivateData,
            ObjectStreams = true,
            CompressStreams = true,
            Version = CompareVersions(options.Version, "1.5") >= 0 ? options.Version
                : options.PdfA == PdfAConformance.None && PdfACheck.Declared(main)?.Part() == 1 ? options.Version
                : "1.5",
        };

    private sealed partial class Session
    {
        private static readonly PdfName BrotliDecode = PdfName.Get("BrotliDecode");

        /// <summary>Checksum (content) → new number: the same image or font only once in the file.</summary>
        private readonly Dictionary<string, int> _duplicates = new(StringComparer.Ordinal);

        /// <summary>Checksums per source object (null = cannot be merged); with the cache, kept across runs.</summary>
        private Dictionary<(Document.PdfDocument, int), string?> ContentKeys => field ??= Cache?.ContentKeys ?? [];

        /// <summary>Cache across several runs (only when compressing).</summary>
        private CompressionCache? Cache => _options.Compression is null ? null : _options.CompressionCache;

        /// <summary>Does the cache apply to these pages (resolutions, font usage, font subsets, images)?</summary>
        private bool CacheMatches => Cache is { } cache && (_cacheMatches ??= cache.Matches(_pages));
        private bool? _cacheMatches;

        /// <summary>May the result for this source object be cached? Not for replaced objects (they apply only to this run).</summary>
        private bool Cacheable(Source source, PdfStream stream, out int number)
        {
            number = stream.Id?.Number ?? -1;
            return Cache is not null && number > 0 && !source.Overrides.ContainsKey(number);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document.PdfDocument, object> DocIds = new();
        private static long _lastDocId;

        /// <summary>Identifier of a document that stays the same across all runs (a per-session hash would change with every run).</summary>
        private static long DocId(Document.PdfDocument doc) => (long)DocIds.GetValue(doc, _ => Interlocked.Increment(ref _lastDocId));

        /// <summary>How strongly streams are repacked: the strongest level when compressing.</summary>
        private CompressionLevel PackLevel => _options.Compression is null ? CompressionLevel.Optimal : CompressionLevel.SmallestSize;

        /// <summary>Does this object already exist (same content)? Then its number; otherwise null, plus the checksum to remember.</summary>
        private int? Duplicate(Source source, int old, out string? key)
        {
            key = null;
            if (_options.Compression is null) return null;
            key = ContentKey(source, old, 0, []);
            if (key is not null && ImageKeySuffix(source, old) is { } suffix) key += suffix;
            return key is not null && _duplicates.TryGetValue(key, out var same) ? same : null;
        }

        private void RememberContent(string? key, int number)
        {
            if (key is not null) _duplicates.TryAdd(key, number);
        }

        /// <summary>
        /// Checksum over the content of an object and everything it refers to. Only for objects without an identity of
        /// their own: streams (images, font programs, forms, colour profiles), arrays, and dictionaries of fonts, graphics
        /// states, patterns, shadings and functions. Never pages, annotations, fields or bookmarks: ones that look the same
        /// are still different objects.
        /// </summary>
        private string? ContentKey(Source source, int number, int depth, HashSet<int> visiting)
        {
            if (ContentKeys.TryGetValue((source.Doc, number), out var known)) return known;
            if (depth > 24 || !visiting.Add(number)) return null;
            string? key = null;
            try
            {
                var obj = source.Overrides.TryGetValue(number, out var replaced) ? replaced : source.Doc.GetObject(number);
                if (Mergeable(obj) && !(obj is PdfStream stream && CarriesStructure(source, stream)))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var budget = 20_000;
                    if (Feed(hash, obj, source, depth, visiting, ref budget, top: true)) key = Convert.ToHexString(hash.GetHashAndReset());
                }
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
            {
                key = null;
            }
            visiting.Remove(number);
            ContentKeys[(source.Doc, number)] = key;
            return key;
        }

        /// <summary>
        /// Whether a stream carries tagged content and so has an identity of its own (docs/FEHLERGEDAECHTNIS.md, No. 630): an XObject that is a
        /// content item itself or has its own key into the parent tree (/StructParent, /StructParents; ISO 32000-2, 14.7.5.3, 14.7.5.4), or —
        /// in a tagged file — a form whose content marks MCIDs: merged with a form that looks the same on another page it would be drawn twice
        /// (ISO 14289-1, 7.20) and give the content of one page to the elements of the other. Structure elements, MCR and OBJR are dictionaries
        /// of types that are never merged (<see cref="Mergeable"/>).
        /// </summary>
        private static bool CarriesStructure(Source source, PdfStream stream)
        {
            var dict = stream.Dictionary;
            if (dict.ContainsKey(StructParentKey) || dict.ContainsKey(StructParents)) return true;
            if (dict.GetName(PdfName.Subtype)?.Value != "Form" || source.Doc.Catalog?.ContainsKey(StructTreeRoot) != true) return false;
            try
            {
                return Accessibility.MarkedContentIds.Largest(source.Doc.GetStreamData(stream),
                    dict.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Properties"))) >= 0;
            }
            catch (PdfException)
            {
                return true;   // unreadable: kept on its own, as anything that cannot be told
            }
        }

        private static readonly HashSet<string> MergeableTypes =["Font", "FontDescriptor", "ExtGState", "Encoding", "Pattern", "XObject", "Halftone"];

        private static bool Mergeable(PdfObject obj) => obj switch
        {
            PdfStream s => !s.Dictionary.IsType(PdfName.Metadata),
            PdfArray => true,
            PdfDictionary d => d.GetName(PdfName.Type) is { } type ? MergeableTypes.Contains(type.Value)
                : d.ContainsKey(PdfName.Get("FunctionType")) || d.ContainsKey(PdfName.Get("ShadingType")),
            _ => false,
        };

        /// <summary>Adds an object to the checksum; false if it contains something that cannot be merged (it then stays separate).</summary>
        private bool Feed(IncrementalHash hash, PdfObject obj, Source source, int depth, HashSet<int> visiting, ref int budget, bool top = false)
        {
            if (--budget < 0) return false;
            switch (obj)
            {
                case PdfReference r:
                    var target = source.Doc.Resolve(r.Id);
                    if (!Mergeable(target))
                    {
                        // Reference to something with an identity of its own: counts as exactly this object
                        Add(hash, $"R{DocId(source.Doc)}:{r.Id.Number};");
                        return true;
                    }
                    var key = ContentKey(source, r.Id.Number, depth + 1, visiting);
                    if (key is null) return false;
                    Add(hash, "K" + key + ";");
                    return true;
                case PdfStream stream:
                    if (!top) return false;
                    Add(hash, "S");
                    if (!FeedDictionary(hash, stream.Dictionary, source, depth, visiting, ref budget, skipLength: true)) return false;
                    var raw = source.Doc.IsStoredPlain(stream) ? source.Doc.StoredStreamData(stream) : source.Doc.GetRawStreamData(stream);
                    Add(hash, $"D{raw.Length}:");
                    hash.AppendData(raw);
                    return true;
                case PdfDictionary dict:
                    return FeedDictionary(hash, dict, source, depth, visiting, ref budget, skipLength: false);
                case PdfArray array:
                    Add(hash, "[");
                    for (var i = 0; i < array.Count; i++)
                        if (!Feed(hash, array[i], source, depth, visiting, ref budget)) return false;
                    Add(hash, "]");
                    return true;
                case PdfName name:
                    Add(hash, "/" + name.Value + " ");
                    return true;
                case PdfString s:
                    Add(hash, $"({s.Bytes.Length}:");
                    hash.AppendData(s.Bytes);
                    return true;
                case PdfInteger i:
                    Add(hash, "i" + i.Value.ToString(CultureInfo.InvariantCulture) + " ");
                    return true;
                case PdfReal real:
                    Add(hash, "r" + real.Value.ToString("R", CultureInfo.InvariantCulture) + " ");
                    return true;
                case PdfBoolean b:
                    Add(hash, b.Value ? "T" : "F");
                    return true;
                case PdfNull:
                    Add(hash, "N");
                    return true;
                default:
                    return false;
            }
        }

        private bool FeedDictionary(IncrementalHash hash, PdfDictionary dict, Source source, int depth, HashSet<int> visiting, ref int budget, bool skipLength)
        {
            Add(hash, "<");
            foreach (var (key, value) in dict.Entries.OrderBy(e => e.Key.Value, StringComparer.Ordinal))
            {
                if (skipLength && key == PdfName.Length) continue;
                Add(hash, "/" + key.Value + "=");
                if (!Feed(hash, value, source, depth, visiting, ref budget)) return false;
            }
            Add(hash, ">");
            return true;
        }

        private static void Add(IncrementalHash hash, string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// Repacks an encoded stream when compressing: general filters (Flate, LZW, ASCII, RunLength, Brotli) are decoded and
        /// packed again at the strongest level (or with Brotli); an image format at the end (JPEG, JBIG2, CCITT, JPEG 2000)
        /// stays as it is. Only if the result is smaller. Null = stays.
        /// </summary>
        private (byte[] Data, PdfObject? Filter, PdfObject? Parms)? Repack(PdfStream stream, Source source, PdfDictionary original, ReadOnlySpan<byte> raw)
        {
            if (_options.Compression is not { } compression || original.IsType(PdfName.Metadata) || raw.Length < 64) return null;
            var chain = StreamFilters.Chain(original);
            if (chain.Count == 0 || chain.Any(f => f.Name == "Crypt")) return null;
            if (chain.Count == 1 && (chain[0].Name is "DCTDecode" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode")) return null;
            // With the cache: each stream only once (the strongest packing level is the most expensive part of a lossless run)
            if (!Cacheable(source, stream, out var number)) return Repacked(compression, original, raw);
            var key = (source.Doc, number, compression.PackKind);
            if (Cache!.Repacked.TryGetValue(key, out var known)) return known;
            var result = Repacked(compression, original, raw);
            if (Cache.Charge(result?.Data.Length ?? 0)) Cache.Repacked[key] = result;
            return result;
        }

        private (byte[] Data, PdfObject? Filter, PdfObject? Parms)? Repacked(CompressionSettings compression, PdfDictionary original, ReadOnlySpan<byte> raw)
        {
            StreamFilters.DecodeResult decoded;
            try
            {
                // Do not decode again an image that the image check has already decoded
                decoded = _decoded is { } known && ReferenceEquals(known.Dict, original) ? new StreamFilters.DecodeResult(known.Data)
                    : StreamFilters.Decode(raw.ToArray(), original);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                return null;
            }
            if (decoded.Truncated) return null;
            byte[] data;
            PdfObject? filter, parms;
            if (decoded.ImageFilter is { } image)
            {
                // Only remove the wrappers (for example ASCII85 around a JPEG)
                (data, filter, parms) = (decoded.Data, image, decoded.ImageParms);
            }
            else
            {
                // Already packed this way (image path, same bytes, no predictor)? Then not again
                data = _plainPacked is { } plain && plain.Brotli == compression.Brotli && ReferenceEquals(plain.Samples, decoded.Data) ? plain.Packed
                    : Pack(decoded.Data, compression.Brotli);
                (filter, parms) = (compression.Brotli ? BrotliDecode : FlateDecode, null);
            }
            return data.Length < raw.Length ? (data, filter, parms) : null;
        }

        /// <summary>Glyphs used per embedded TrueType file (checksum of the content, merged over all sources).</summary>
        private Dictionary<string, HashSet<int>>? _fontGlyphs;

        /// <summary>Font files that stay whole (form, graphics state, unreadable content).</summary>
        private HashSet<string> _wholeFonts = new(StringComparer.Ordinal);

        /// <summary>Before writing: determines which glyphs of the embedded TrueType fonts the pages use.</summary>
        private void PrepareFonts()
        {
            if (_options.Compression is not { SubsetFonts: true }) return;
            if (CacheMatches && Cache!.Fonts is { } known)
            {
                (_fontGlyphs, _wholeFonts) = known;
                return;
            }
            _fontGlyphs = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var group in _pages.GroupBy(p => p.Source, ReferenceEqualityComparer.Instance))
            {
                var doc = (Document.PdfDocument)group.Key!;
                var source = _sources[doc];
                var usage = new FontUsage(doc).Collect(group.Select(p => p.Page), _cancel);
                string Key(int file) => ContentKey(source, file, 0, []) ?? $"{DocId(source.Doc)}:{file}";
                foreach (var file in usage.Whole) _wholeFonts.Add(Key(file));
                foreach (var (file, glyphs) in usage.Glyphs)
                {
                    var key = Key(file);
                    if (usage.Failed) _wholeFonts.Add(key);
                    if (!_fontGlyphs.TryGetValue(key, out var set)) _fontGlyphs[key] = set = [];
                    set.UnionWith(glyphs);
                }
            }
            // Cache only once complete (a cancellation midway would leave half a font usage behind)
            if (CacheMatches) Cache!.Fonts = (_fontGlyphs, _wholeFonts);
        }

        /// <summary>Subsets an embedded TrueType font: only the outlines of unused glyphs become empty; null = stays.</summary>
        private (byte[] Data, PdfDictionary Dict)? SubsetFont(PdfStream stream, Source source, PdfDictionary dict)
        {
            if (_fontGlyphs is null || stream.Id is not { } id) return null;
            var key = ContentKey(source, id.Number, 0, []) ?? $"{DocId(source.Doc)}:{id.Number}";
            if (_wholeFonts.Contains(key) || !_fontGlyphs.TryGetValue(key, out var glyphs)) return null;
            // With the cache: subset and pack each font only once (the glyphs depend only on the pages)
            if (!CacheMatches || !Cacheable(source, stream, out var number)) return Subset(stream, source, dict, glyphs);
            var cacheKey = (source.Doc, number, _options.Compression!.PackKind);
            if (Cache!.Subsets.TryGetValue(cacheKey, out var cached)) return cached is { } c ? (c.Data, Copy(c.Dict)) : null;
            var result = Subset(stream, source, dict, glyphs);
            if (Cache.Charge(result?.Data.Length ?? 0)) Cache.Subsets[cacheKey] = result is { } r ? (r.Data, Copy(r.Dict)) : null;
            return result;
        }

        /// <summary>Shallow copy (the writer sets /Length in the dictionary it writes).</summary>
        private static PdfDictionary Copy(PdfDictionary dict)
        {
            var copy = new PdfDictionary(dict.Count);
            foreach (var (k, value) in dict.Entries) copy.Set(k, value);
            return copy;
        }

        private (byte[] Data, PdfDictionary Dict)? Subset(PdfStream stream, Source source, PdfDictionary dict, HashSet<int> glyphs)
        {
            try
            {
                var font = source.Doc.GetStreamData(stream);
                var subset = Core.Fonts.TrueTypeSubsetter.Subset(font, glyphs, keepAllTables: true);
                var packed = Pack(subset, _options.Compression!.Brotli);
                if (packed.Length >= (source.Doc.IsStoredPlain(stream) ? stream.DataLength : font.Length)) return null;
                var result = new PdfDictionary(dict.Count);
                foreach (var (k, value) in dict.Entries) result.Set(k, value);
                result.Remove(PdfName.DecodeParms);
                result.Set(PdfName.Filter, _options.Compression.Brotli ? BrotliDecode : FlateDecode);
                result.Set(PdfName.Get("Length1"), PdfInteger.Of(subset.Length));
                return (packed, result);
            }
            catch (Exception e) when (e is Core.Fonts.FontFormatException or PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Packs with the strongest Flate level or Brotli; with "Flate – strong", the own packer is tried as well and the smaller result wins.</summary>
        private byte[] Pack(ReadOnlySpan<byte> data, bool brotli)
        {
            if (brotli) return Brotli.Encode(data, _cancel);
            using var output = new MemoryStream();
            using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
            var net = output.ToArray();
            return _options.Compression is { StrongFlate: true } ? Stronger(data, net) : net;
        }

        /// <summary>
        /// "Flate – strong": used only if it is smaller and decodes back to exactly the same data (cross-check with the .NET
        /// decoder); otherwise the result of the strongest level stays.
        /// </summary>
        private byte[] Stronger(ReadOnlySpan<byte> data, byte[] net)
        {
            var strong = Filters.StrongDeflate.Encode(data, _cancel);
            if (strong.Length >= net.Length) return net;
            try
            {
                using var input = new ZLibStream(new MemoryStream(strong), CompressionMode.Decompress);
                var check = new byte[data.Length];
                input.ReadExactly(check);
                return input.ReadByte() < 0 && data.SequenceEqual(check) ? strong : net;
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                return net;
            }
        }
    }
}
