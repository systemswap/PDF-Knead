using System.IO.Compression;
using System.Text;

namespace PdfKnead.Core.Pdf.Text.Entities;

/// <summary>The lists of the lexicon (KONZEPT.md, section 5.23). Each is asked on its own — a word has no type (docs/PATENTE.md, B7).</summary>
public enum LexiconList : byte
{
    /// <summary>Given names (Wikidata, CC0), as written („Max“).</summary>
    GivenNames = 1,

    /// <summary>Family names (Wikidata, CC0), as written.</summary>
    FamilyNames = 2,

    /// <summary>Common words written in small letters (Wikidata lexemes, CC0), in small letters — single words only (docs/PATENTE.md, B1).</summary>
    CommonLower = 3,

    /// <summary>The other common words (written with a capital only, as German nouns), in small letters — single words only.</summary>
    CommonOther = 4,

    /// <summary>Places with at least 1 000 inhabitants that are not among <see cref="BigPlaces"/> (GeoNames, CC BY 4.0): name and ASCII form.</summary>
    SmallPlaces = 5,

    /// <summary>Places with at least 15 000 inhabitants and their other names (GeoNames).</summary>
    BigPlaces = 6,

    /// <summary>Country names in many languages (GeoNames, Unicode CLDR).</summary>
    Countries = 7,

    /// <summary>Legal forms of companies (OpenSanctions rigour, MIT, and an own list).</summary>
    LegalForms = 8,

    /// <summary>Titles before a name (libpostal, MIT), capitalised.</summary>
    Titles = 9,

    /// <summary>What may follow a name: generation, degrees (libpostal and an own list).</summary>
    NameEndings = 10,

    /// <summary>Street words joined to a name in compounding languages (…straße, …weg; libpostal).</summary>
    StreetSuffixes = 11,

    /// <summary>Street words before a name (Rue, Via, ul.; libpostal, without the English ones).</summary>
    StreetTypesBefore = 12,

    /// <summary>Words for a post office box (Postfach, P.O. Box; libpostal).</summary>
    PostOffices = 13,

    /// <summary>Postal code formats as regular expressions (Google's address data, CC BY 4.0).</summary>
    PostcodeFormats = 14,

    /// <summary>The sources and their dates, one per line (for the info window and the notices).</summary>
    Sources = 15,
}

/// <summary>
/// The word lists of the recognition of persons, companies and addresses in one compact file (KONZEPT.md, section 5.23): each list sorted by
/// its UTF-8 bytes, in blocks of <see cref="BlockSize"/> entries that share their prefixes with the entry before; the whole file packed with
/// Brotli and built into the program. It is unpacked in memory on first use and searched there — nothing is written to the disk.
/// </summary>
/// <remarks>Format (version 1): „PKLX“, version byte, number of lists, then per list its id, the number of entries and the length of its
/// blob (both as 7-bit varints) and the blob. In a blob each block starts with a whole entry (length, bytes); every further entry gives the
/// length of the prefix it shares with the one before, the length of the rest and the rest.</remarks>
public sealed class Lexicon
{
    /// <summary>Entries per block (one whole entry, the rest by their shared prefix).</summary>
    public const int BlockSize = 16;

    /// <summary>The longest entry (a crafted file must not make the reader allocate much).</summary>
    public const int MaxEntryBytes = 1024;

    private static readonly byte[] Magic = "PKLX"u8.ToArray();
    private const byte Version = 1;

    private readonly Dictionary<LexiconList, Section> _sections = [];

    private sealed class Section(byte[] blob, int[] blocks, int count)
    {
        public readonly byte[] Blob = blob;

        /// <summary>Offset of every block in <see cref="Blob"/>.</summary>
        public readonly int[] Blocks = blocks;

        public readonly int Count = count;
    }

    private Lexicon()
    {
    }

    private static readonly Lazy<Lexicon?> BuiltIn = new(LoadBuiltIn, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The lexicon built into the program; null if the program was built without it.</summary>
    public static Lexicon? Default => BuiltIn.Value;

    private static Lexicon? LoadBuiltIn()
    {
        using var stream = typeof(Lexicon).Assembly.GetManifestResourceStream("Lexikon/lexikon.br");
        if (stream is null) return null;
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        brotli.CopyTo(memory);
        return Read(memory.ToArray());
    }

    /// <summary>The lists it holds.</summary>
    public IEnumerable<LexiconList> Lists => _sections.Keys;

    /// <summary>How many entries a list holds (0 if it is missing).</summary>
    public int Count(LexiconList list) => _sections.TryGetValue(list, out var s) ? s.Count : 0;

    /// <summary>Whether a list holds the word exactly (ordinal, as written).</summary>
    public bool Contains(LexiconList list, string word) => Contains(list, word.AsSpan());

    /// <summary>Whether a list holds the word exactly (ordinal, as written).</summary>
    public bool Contains(LexiconList list, ReadOnlySpan<char> word)
    {
        if (word.IsEmpty || !_sections.TryGetValue(list, out var section) || section.Count == 0) return false;
        var max = Encoding.UTF8.GetMaxByteCount(word.Length);
        if (max > MaxEntryBytes * 4) return false;
        Span<byte> key = max <= 512 ? stackalloc byte[max] : new byte[max];
        key = key[..Encoding.UTF8.GetBytes(word, key)];
        if (key.Length > MaxEntryBytes) return false;
        // the last block whose first entry is not greater than the key
        int lo = 0, hi = section.Blocks.Length - 1, block = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var head = Head(section, mid);
            var c = head.SequenceCompareTo(key);
            if (c == 0) return true;
            if (c < 0)
            {
                block = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        if (block < 0) return false;
        // the head is below the key: the rest of the block, in order, until an entry is not below it
        Span<byte> current = stackalloc byte[MaxEntryBytes];
        var at = section.Blocks[block];
        var end = block + 1 < section.Blocks.Length ? section.Blocks[block + 1] : section.Blob.Length;
        var length = ReadVarint(section.Blob, ref at);
        section.Blob.AsSpan(at, length).CopyTo(current);
        at += length;
        while (at < end)
        {
            var shared = ReadVarint(section.Blob, ref at);
            var rest = ReadVarint(section.Blob, ref at);
            section.Blob.AsSpan(at, rest).CopyTo(current[shared..]);
            at += rest;
            var c = current[..(shared + rest)].SequenceCompareTo(key);
            if (c >= 0) return c == 0;
        }
        return false;
    }

    /// <summary>All entries of a list, in their order (to build patterns from short lists such as legal forms or titles).</summary>
    public List<string> Entries(LexiconList list)
    {
        var result = new List<string>();
        if (!_sections.TryGetValue(list, out var section)) return result;
        Span<byte> current = stackalloc byte[MaxEntryBytes];
        var at = 0;
        for (var n = 0; n < section.Count; n++)
        {
            int length;
            if (n % BlockSize == 0)
            {
                length = ReadVarint(section.Blob, ref at);
                section.Blob.AsSpan(at, length).CopyTo(current);
                at += length;
            }
            else
            {
                var shared = ReadVarint(section.Blob, ref at);
                var rest = ReadVarint(section.Blob, ref at);
                section.Blob.AsSpan(at, rest).CopyTo(current[shared..]);
                at += rest;
                length = shared + rest;
            }
            result.Add(Encoding.UTF8.GetString(current[..length]));
        }
        return result;
    }

    /// <summary>The first entry of a block (stored whole).</summary>
    private static ReadOnlySpan<byte> Head(Section section, int block)
    {
        var at = section.Blocks[block];
        var length = ReadVarint(section.Blob, ref at);
        return section.Blob.AsSpan(at, length);
    }

    private static int ReadVarint(byte[] data, ref int at)
    {
        var value = 0;
        for (var shift = 0; shift < 32; shift += 7)
        {
            if (at >= data.Length) throw new InvalidDataException("lexicon: truncated");
            var b = data[at++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
        throw new InvalidDataException("lexicon: bad number");
    }

    /// <summary>
    /// Reads a lexicon (unpacked). Every block is checked while it is indexed — lengths within the blob and the entry limit, entries in order —
    /// so a lookup cannot run past the data.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a lexicon, another version or damaged.</exception>
    public static Lexicon Read(byte[] data)
    {
        if (data.Length < 6 || !data.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("lexicon: not a lexicon");
        if (data[4] != Version) throw new InvalidDataException("lexicon: other version");
        var lexicon = new Lexicon();
        var lists = data[5];
        var at = 6;
        for (var i = 0; i < lists; i++)
        {
            if (at >= data.Length) throw new InvalidDataException("lexicon: truncated");
            var id = (LexiconList)data[at++];
            var count = ReadVarint(data, ref at);
            var length = ReadVarint(data, ref at);
            if (count < 0 || length < 0 || length > data.Length - at) throw new InvalidDataException("lexicon: truncated");
            var blob = data.AsSpan(at, length).ToArray();
            at += length;
            lexicon._sections[id] = new Section(blob, Index(blob, count), count);
        }
        return lexicon;
    }

    /// <summary>The offsets of the blocks of a blob, checked entry by entry.</summary>
    private static int[] Index(byte[] blob, int count)
    {
        if (count > blob.Length) throw new InvalidDataException("lexicon: bad count");   // every entry takes at least one byte
        var blocks = new int[(count + BlockSize - 1) / BlockSize];
        var current = new byte[MaxEntryBytes];
        var before = new byte[MaxEntryBytes];
        var previous = 0;
        var at = 0;
        for (var n = 0; n < count; n++)
        {
            (current, before) = (before, current);
            before.AsSpan(0, previous).CopyTo(current);   // the shared prefix comes from the entry before
            int length;
            if (n % BlockSize == 0)
            {
                blocks[n / BlockSize] = at;
                length = ReadVarint(blob, ref at);
                if (length <= 0 || length > MaxEntryBytes || length > blob.Length - at) throw new InvalidDataException("lexicon: bad entry");
                blob.AsSpan(at, length).CopyTo(current);
                at += length;
            }
            else
            {
                var shared = ReadVarint(blob, ref at);
                var rest = ReadVarint(blob, ref at);
                length = shared + rest;
                if (shared < 0 || rest < 0 || shared > previous || length <= 0 || length > MaxEntryBytes || rest > blob.Length - at)
                    throw new InvalidDataException("lexicon: bad entry");
                blob.AsSpan(at, rest).CopyTo(current.AsSpan(shared));
                at += rest;
            }
            if (n > 0 && current.AsSpan(0, length).SequenceCompareTo(before.AsSpan(0, previous)) <= 0) throw new InvalidDataException("lexicon: not sorted");
            previous = length;
        }
        if (at != blob.Length) throw new InvalidDataException("lexicon: trailing data");
        return blocks;
    }

    /// <summary>Writes a lexicon (unpacked): each list sorted by its UTF-8 bytes, repeated entries and empty ones left out.</summary>
    /// <exception cref="ArgumentException">An entry is longer than <see cref="MaxEntryBytes"/>.</exception>
    public static byte[] Write(IReadOnlyDictionary<LexiconList, IEnumerable<string>> lists)
    {
        using var output = new MemoryStream();
        output.Write(Magic);
        output.WriteByte(Version);
        output.WriteByte(checked((byte)lists.Count));
        foreach (var (id, words) in lists.OrderBy(l => l.Key))
        {
            var sorted = words.Where(w => w.Length > 0).Select(w => Encoding.UTF8.GetBytes(w)).ToList();
            sorted.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
            var unique = new List<byte[]>(sorted.Count);
            foreach (var w in sorted)
            {
                if (w.Length > MaxEntryBytes) throw new ArgumentException($"lexicon: entry too long ({w.Length} bytes)");
                if (unique.Count == 0 || !unique[^1].AsSpan().SequenceEqual(w)) unique.Add(w);
            }
            using var blob = new MemoryStream();
            for (var n = 0; n < unique.Count; n++)
            {
                var w = unique[n];
                if (n % BlockSize == 0)
                {
                    WriteVarint(blob, w.Length);
                    blob.Write(w);
                    continue;
                }
                var shared = w.AsSpan().CommonPrefixLength(unique[n - 1]);
                WriteVarint(blob, shared);
                WriteVarint(blob, w.Length - shared);
                blob.Write(w, shared, w.Length - shared);
            }
            output.WriteByte((byte)id);
            WriteVarint(output, unique.Count);
            WriteVarint(output, (int)blob.Length);
            blob.WriteTo(output);
        }
        return output.ToArray();
    }

    /// <summary>Packs a lexicon as it is built into the program (Brotli, best compression).</summary>
    public static byte[] Pack(byte[] lexicon)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(lexicon);
        return output.ToArray();
    }

    private static void WriteVarint(Stream stream, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            stream.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        stream.WriteByte((byte)v);
    }
}
