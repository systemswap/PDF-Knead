using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Pdf.Objects;

/// <summary>Number and generation of an indirect object ("12 0 obj").</summary>
/// <param name="Number">The object number.</param>
/// <param name="Generation">The generation number.</param>
public readonly record struct PdfObjectId(int Number, int Generation)
{
    /// <summary>Formats the identifier as "number generation".</summary>
    /// <returns>The formatted text, for example "12 0".</returns>
    public override string ToString() => $"{Number} {Generation}";
}

/// <summary>
/// Resolves references ("12 0 R"). The document sets itself as the resolver of the arrays and dictionaries it
/// reads, so that <see cref="PdfDictionary.Get"/> returns the target object directly.
/// </summary>
public interface IPdfResolver
{
    /// <summary>Returns the target object of a reference.</summary>
    /// <param name="id">The object identifier.</param>
    /// <returns>The object, or <see cref="PdfNull"/> if it is missing or unreadable.</returns>
    PdfObject Resolve(PdfObjectId id);
}

/// <summary>Base type of all PDF objects (ISO 32000-2, 7.3).</summary>
public abstract class PdfObject
{
    /// <summary>Returns a readable representation for debugging and the command line.</summary>
    /// <returns>The object in PDF-like syntax; streams show only their dictionary and data length.</returns>
    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb, 0);
        return sb.ToString();
    }

    internal abstract void Write(StringBuilder sb, int indent);
}

/// <summary>The null object; also returned for missing entries and unresolvable references.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>The only instance.</summary>
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    internal override void Write(StringBuilder sb, int indent) => sb.Append("null");
}

/// <summary>A boolean object; there are exactly two instances.</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>The instance for true.</summary>
    public static readonly PdfBoolean True = new(true);
    /// <summary>The instance for false.</summary>
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <summary>Returns the shared instance for a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see cref="True"/> or <see cref="False"/>.</returns>
    public static PdfBoolean Of(bool value) => value ? True : False;

    internal override void Write(StringBuilder sb, int indent) => sb.Append(Value ? "true" : "false");
}

/// <summary>An integer object.</summary>
public sealed class PdfInteger : PdfObject
{
    // Small numbers occur constantly (generation 0, lengths, indices), so they are not created anew each time
    private static readonly PdfInteger[] Small = Enumerable.Range(-1, 1026).Select(i => new PdfInteger(i)).ToArray();

    private PdfInteger(long value) => Value = value;

    /// <summary>The value.</summary>
    public long Value { get; }

    /// <summary>Returns an integer object for a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A shared instance for values from -1 to 1024, otherwise a new object.</returns>
    public static PdfInteger Of(long value) =>
        value is >= -1 and <= 1024 ? Small[value + 1] : new PdfInteger(value);

    internal override void Write(StringBuilder sb, int indent) => sb.Append(Value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A real number object.</summary>
public sealed class PdfReal : PdfObject
{
    /// <summary>Creates a real number object.</summary>
    /// <param name="value">The value.</param>
    public PdfReal(double value) => Value = value;

    /// <summary>The value.</summary>
    public double Value { get; }

    internal override void Write(StringBuilder sb, int indent) => sb.Append(Value.ToString("0.######", CultureInfo.InvariantCulture));
}

/// <summary>
/// A string of bytes (literal "(…)" or hexadecimal "&lt;…&gt;"). For text such as title, author or bookmarks, use
/// <see cref="ToText"/>, which decodes PDFDocEncoding, UTF-16BE or UTF-8.
/// </summary>
public sealed class PdfString : PdfObject
{
    /// <summary>Creates a string object.</summary>
    /// <param name="bytes">The bytes, after escape sequences have been resolved; the object keeps this array.</param>
    /// <param name="isHex">True if the string was written in hexadecimal form.</param>
    public PdfString(byte[] bytes, bool isHex = false)
    {
        Bytes = bytes;
        IsHex = isHex;
    }

    /// <summary>The bytes of the string.</summary>
    public byte[] Bytes { get; }

    /// <summary>True if the string was written in hexadecimal form.</summary>
    public bool IsHex { get; }

    /// <summary>Decodes the string as a text string (ISO 32000-2, 7.9.2.2).</summary>
    /// <returns>The decoded text.</returns>
    public string ToText() => PdfTextEncoding.Decode(Bytes);

    internal override void Write(StringBuilder sb, int indent)
    {
        if (IsHex || Bytes.Any(b => b < 0x20 && b is not (byte)'\n' and not (byte)'\r' and not (byte)'\t'))
        {
            sb.Append('<').Append(Convert.ToHexString(Bytes)).Append('>');
            return;
        }
        sb.Append('(');
        foreach (var b in Bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\': sb.Append('\\').Append((char)b); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                default: sb.Append((char)b); break;
            }
        }
        sb.Append(')');
    }
}

/// <summary>
/// A name ("/Type"). Equal names are almost always the same object (one table for all), so comparisons are cheap. The
/// value is the byte sequence after resolving #xx escapes, as Latin-1, so that every name is preserved exactly when written.
/// </summary>
/// <remarks>Compare names with <c>==</c>, <see cref="Equals(PdfName?)"/> or as dictionary keys, never with
/// <see cref="object.ReferenceEquals"/>: beyond the table limit equal names are different objects.</remarks>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    private static readonly ConcurrentDictionary<string, PdfName> Table = new(StringComparer.Ordinal);
    private static int _interned;   // approximate (Count of the table locks all segments)

    private PdfName(string value, bool shared)
    {
        Value = value;
        Shared = shared;
    }

    /// <summary>The name without the slash, with #xx escapes resolved, one character per byte (Latin-1).</summary>
    public string Value { get; }

    /// <summary>In the shared table: two shared names are equal exactly when they are the same object.</summary>
    internal bool Shared { get; }

    /// <summary>
    /// Maximum number of names in the shared table, which lives as long as the program. Without this limit, a crafted
    /// file with millions of different names would leave tens of MB behind each time it is opened. Beyond the limit,
    /// separate objects are created (comparisons then use the value, see <see cref="Equals(PdfName?)"/>); the well-known
    /// names are always in the table. Settable for tests only.
    /// </summary>
    internal static int MaxInterned { get; set; } = 200_000;

    /// <summary>Returns the name with the given value.</summary>
    /// <param name="value">The name without the slash, one character per byte.</param>
    /// <returns>The shared instance; a separate object once the table holds <c>MaxInterned</c> names and the
    /// value is not yet in it.</returns>
    /// <remarks>Thread-safe.</remarks>
    public static PdfName Get(string value)
    {
        if (Table.TryGetValue(value, out var name)) return name;
        if (Volatile.Read(ref _interned) >= MaxInterned) return new PdfName(value, shared: false);
        return Table.GetOrAdd(value, static v =>
        {
            Interlocked.Increment(ref _interned);
            return new PdfName(v, shared: true);
        });
    }

    private static readonly ConcurrentDictionary<string, PdfName>.AlternateLookup<ReadOnlySpan<char>> SpanLookup =
        Table.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Returns the name for bytes interpreted as Latin-1, without an intermediate string if the name already
    /// exists (the usual case).</summary>
    /// <param name="latin1">The name bytes without the slash, with #xx escapes resolved.</param>
    /// <returns>The name, as for <see cref="Get(string)"/>.</returns>
    public static PdfName Get(ReadOnlySpan<byte> latin1)
    {
        Span<char> chars = latin1.Length <= 256 ? stackalloc char[latin1.Length] : new char[latin1.Length];
        for (var i = 0; i < latin1.Length; i++) chars[i] = (char)latin1[i];
        return SpanLookup.TryGetValue(chars, out var name) ? name : Get(new string(chars));
    }

    /// <summary>Compares two names by value.</summary>
    /// <param name="other">The other name.</param>
    /// <returns>True if both have the same value.</returns>
    /// <remarks>Almost always the same object; only names beyond the table limit are different objects with the same value
    /// (the value is compared only then).</remarks>
    public bool Equals(PdfName? other) =>
        ReferenceEquals(this, other) || (other is not null && !(Shared && other.Shared) && string.Equals(Value, other.Value, StringComparison.Ordinal));

    /// <summary>Compares two names by value, like <see cref="Equals(PdfName?)"/>.</summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns>True if both are null or have the same value.</returns>
    public static bool operator ==(PdfName? a, PdfName? b) => ReferenceEquals(a, b) || (a is not null && a.Equals(b));

    /// <summary>Compares two names by value.</summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns>True if the values differ.</returns>
    public static bool operator !=(PdfName? a, PdfName? b) => !(a == b);

    /// <summary>Compares with another object by value.</summary>
    /// <param name="obj">The other object.</param>
    /// <returns>True if it is a <see cref="PdfName"/> with the same value.</returns>
    public override bool Equals(object? obj) => obj is PdfName other && Equals(other);

    /// <summary>Returns a hash code of the value.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    internal override void Write(StringBuilder sb, int indent)
    {
        sb.Append('/');
        foreach (var c in Value)
        {
            if (c is < '!' or > '~' or '#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%')
                sb.Append('#').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
    }

    // ------------------------------------------------------------ Common names (always in the shared table)
    public static readonly PdfName Type = Get("Type");
    public static readonly PdfName Subtype = Get("Subtype");
    public static readonly PdfName Root = Get("Root");
    public static readonly PdfName Info = Get("Info");
    public static readonly PdfName Encrypt = Get("Encrypt");
    public static readonly PdfName ID = Get("ID");
    public static readonly PdfName Size = Get("Size");
    public static readonly PdfName Prev = Get("Prev");
    public static readonly PdfName XRefStm = Get("XRefStm");
    public static readonly PdfName XRef = Get("XRef");
    public static readonly PdfName ObjStm = Get("ObjStm");
    public static readonly PdfName W = Get("W");
    public static readonly PdfName Index = Get("Index");
    public static readonly PdfName N = Get("N");
    public static readonly PdfName First = Get("First");
    public static readonly PdfName Length = Get("Length");
    public static readonly PdfName Filter = Get("Filter");
    public static readonly PdfName DecodeParms = Get("DecodeParms");
    public static readonly PdfName DP = Get("DP");
    public static readonly PdfName F = Get("F");
    public static readonly PdfName Catalog = Get("Catalog");
    public static readonly PdfName Pages = Get("Pages");
    public static readonly PdfName Page = Get("Page");
    public static readonly PdfName Kids = Get("Kids");
    public static readonly PdfName Count = Get("Count");
    public static readonly PdfName Parent = Get("Parent");
    public static readonly PdfName MediaBox = Get("MediaBox");
    public static readonly PdfName CropBox = Get("CropBox");
    public static readonly PdfName Rotate = Get("Rotate");
    public static readonly PdfName Resources = Get("Resources");
    public static readonly PdfName Contents = Get("Contents");
    public static readonly PdfName UserUnit = Get("UserUnit");
    public static readonly PdfName Version = Get("Version");
    public static readonly PdfName Metadata = Get("Metadata");
    public static readonly PdfName Outlines = Get("Outlines");
    public static readonly PdfName Crypt = Get("Crypt");
    public static readonly PdfName Name = Get("Name");
    public static readonly PdfName Identity = Get("Identity");
}

/// <summary>An array; <see cref="Get"/> resolves references, the indexer does not.</summary>
/// <remarks>Not thread-safe for modification.</remarks>
public sealed class PdfArray : PdfObject, IReadOnlyList<PdfObject>
{
    private readonly List<PdfObject> _items;

    /// <summary>Creates an array.</summary>
    /// <param name="items">The initial entries, copied; null for an empty array.</param>
    public PdfArray(IEnumerable<PdfObject>? items = null) => _items = items is null ? [] : [.. items];

    private PdfArray(List<PdfObject> items) => _items = items;

    /// <summary>Copy with exactly the right size (parser).</summary>
    internal static PdfArray CopyOf(List<PdfObject> source)
    {
        var items = new List<PdfObject>(source.Count);
        items.AddRange(source);
        return new PdfArray(items);
    }

    internal IPdfResolver? Resolver { get; set; }

    /// <summary>Number of entries.</summary>
    public int Count => _items.Count;

    /// <summary>Returns an entry without resolving it (it may be a reference).</summary>
    /// <param name="index">Zero-based index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the array.</exception>
    public PdfObject this[int index] => _items[index];

    /// <summary>Appends an entry.</summary>
    /// <param name="item">The entry.</param>
    public void Add(PdfObject item) => _items.Add(item);

    /// <summary>Returns an entry with references resolved.</summary>
    /// <param name="index">Zero-based index.</param>
    /// <returns>The entry, or <see cref="PdfNull"/> if the index is outside the array.</returns>
    public PdfObject Get(int index) => index < 0 || index >= _items.Count ? PdfNull.Instance : Resolve(_items[index]);

    /// <summary>Returns an entry as a number.</summary>
    /// <param name="index">Zero-based index.</param>
    /// <returns>The value of an integer or real entry, otherwise null.</returns>
    public double? GetNumber(int index) => Get(index).AsNumber();

    /// <summary>Enumerates the entries without resolving them.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private PdfObject Resolve(PdfObject value) =>
        value is PdfReference r && Resolver is { } resolver ? resolver.Resolve(r.Id) : value;

    internal override void Write(StringBuilder sb, int indent)
    {
        sb.Append('[');
        for (var i = 0; i < _items.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            _items[i].Write(sb, indent);
        }
        sb.Append(']');
    }
}

/// <summary>A dictionary; <see cref="Get"/> resolves references, <see cref="GetRaw"/> does not.</summary>
/// <remarks>Not thread-safe for modification. Entries keep their insertion order up to 12 entries.</remarks>
public sealed class PdfDictionary : PdfObject
{
    // Most PDF dictionaries have fewer than ten keys: for them, two small arrays searched by reference equality
    // (names are interned, so equal names are the same object; names created beyond PdfName's table limit are not
    // found this way) are faster than hashing and need a fraction of the memory of a Dictionary. A real Dictionary
    // is used only above SmallLimit entries.
    private const int SmallLimit = 12;

    private PdfName[] _keys;
    private PdfObject[] _values;
    private int _count;
    private Dictionary<PdfName, PdfObject>? _map;

    /// <summary>Creates an empty dictionary.</summary>
    public PdfDictionary()
    {
        _keys = [];
        _values = [];
    }

    /// <summary>Creates an empty dictionary with a suitable capacity (parser: the number of entries is already known).</summary>
    /// <param name="capacity">Expected number of entries; clamped to 0 … 12.</param>
    public PdfDictionary(int capacity)
    {
        capacity = Math.Clamp(capacity, 0, SmallLimit);
        _keys = capacity == 0 ? [] : new PdfName[capacity];
        _values = capacity == 0 ? [] : new PdfObject[capacity];
    }

    internal IPdfResolver? Resolver { get; set; }

    /// <summary>Number of entries.</summary>
    public int Count => _map?.Count ?? _count;

    /// <summary>The keys.</summary>
    public IEnumerable<PdfName> Keys => _map?.Keys ?? SmallKeys();

    /// <summary>
    /// The entries without resolving them. <c>foreach</c> over them allocates nothing (a struct instead of an
    /// iterator; an iterator allocates an object per loop, 6 MB alone when writing 65,000 objects).
    /// </summary>
    public EntryList Entries => new(this);

    private IEnumerable<PdfName> SmallKeys()
    {
        for (var i = 0; i < _count; i++) yield return _keys[i];
    }

    /// <summary>Entries of a <see cref="PdfDictionary"/> (see <see cref="Entries"/>).</summary>
    /// <param name="dict">The dictionary.</param>
    public readonly struct EntryList(PdfDictionary dict) : IEnumerable<KeyValuePair<PdfName, PdfObject>>
    {
        /// <summary>Returns an allocation-free enumerator.</summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator() => new(dict);

        IEnumerator<KeyValuePair<PdfName, PdfObject>> IEnumerable<KeyValuePair<PdfName, PdfObject>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Enumerator over the entries; the dictionary must not be modified during enumeration.</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<PdfName, PdfObject>>
    {
        private readonly PdfDictionary _dict;
        private readonly bool _isMap;
        private Dictionary<PdfName, PdfObject>.Enumerator _map;
        private int _index;

        internal Enumerator(PdfDictionary? dict)
        {
            // (a default EntryList has no dictionary — it enumerates nothing: docs/FEHLERGEDAECHTNIS.md, No. 522)
            _dict = dict!;
            _isMap = dict?._map is not null;
            _map = _isMap ? dict!._map!.GetEnumerator() : default;
            _index = -1;
        }

        /// <summary>The current entry, unresolved.</summary>
        public readonly KeyValuePair<PdfName, PdfObject> Current =>
            _isMap ? _map.Current : new(_dict._keys[_index], _dict._values[_index]);

        readonly object IEnumerator.Current => Current;

        /// <summary>Advances to the next entry.</summary>
        /// <returns>False after the last entry.</returns>
        public bool MoveNext() => _isMap ? _map.MoveNext() : _dict is not null && ++_index < _dict._count;

        /// <summary>Not supported.</summary>
        /// <exception cref="NotSupportedException">Always.</exception>
        public void Reset() => throw new NotSupportedException();

        /// <summary>Does nothing.</summary>
        public void Dispose()
        {
        }
    }

    private int IndexOf(PdfName key)
    {
        // Shared names are equal exactly when they are the same object; the value only counts for names beyond the table
        // limit (a crafted file with very many names), otherwise /JavaScript could slip past the cleanup, for example
        var keys = _keys;
        if (key.Shared)
        {
            for (var i = 0; i < _count; i++)
            {
                var k = keys[i];
                if (ReferenceEquals(k, key) || (!k.Shared && string.Equals(k.Value, key.Value, StringComparison.Ordinal))) return i;
            }
        }
        else
        {
            for (var i = 0; i < _count; i++)
                if (string.Equals(keys[i].Value, key.Value, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Checks whether the dictionary has an entry for a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True if the entry exists (even if its value is null or a reference that cannot be resolved).</returns>
    public bool ContainsKey(PdfName key) => _map?.ContainsKey(key) ?? IndexOf(key) >= 0;

    /// <summary>Sets an entry; an existing entry with the same key is replaced.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value (may be a reference).</param>
    public void Set(PdfName key, PdfObject value)
    {
        if (_map is not null)
        {
            _map[key] = value;
            return;
        }
        var i = IndexOf(key);
        if (i >= 0)
        {
            _values[i] = value;
            return;
        }
        if (_count == SmallLimit)
        {
            _map = new Dictionary<PdfName, PdfObject>(SmallLimit * 2);
            for (var k = 0; k < _count; k++) _map[_keys[k]] = _values[k];
            _map[key] = value;
            _keys = [];
            _values = [];
            _count = 0;
            return;
        }
        if (_count == _keys.Length)
        {
            var size = Math.Min(SmallLimit, Math.Max(4, _count * 2));
            Array.Resize(ref _keys, size);
            Array.Resize(ref _values, size);
        }
        _keys[_count] = key;
        _values[_count++] = value;
    }

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True if the entry existed.</returns>
    public bool Remove(PdfName key)
    {
        if (_map is not null) return _map.Remove(key);
        var i = IndexOf(key);
        if (i < 0) return false;
        Array.Copy(_keys, i + 1, _keys, i, _count - i - 1);
        Array.Copy(_values, i + 1, _values, i, _count - i - 1);
        _count--;
        _keys[_count] = null!;
        _values[_count] = null!;
        return true;
    }

    /// <summary>Returns a value without resolving it (it may be a reference).</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null if the key is missing.</returns>
    public PdfObject? GetRaw(PdfName key)
    {
        if (_map is not null) return _map.GetValueOrDefault(key);
        var i = IndexOf(key);
        return i >= 0 ? _values[i] : null;
    }

    /// <summary>Returns a value with references resolved.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or <see cref="PdfNull"/> if the key is missing. Without a resolver, a reference is returned
    /// unchanged.</returns>
    public PdfObject Get(PdfName key) =>
        GetRaw(key) is { } value
            ? value is PdfReference r && Resolver is { } resolver ? resolver.Resolve(r.Id) : value
            : PdfNull.Instance;

    /// <summary>Returns a value as a dictionary; for a stream, its dictionary.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The dictionary, or null if the value is neither a dictionary nor a stream.</returns>
    public PdfDictionary? GetDictionary(PdfName key) => Get(key) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    /// <summary>Returns a value as an array.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The array, or null if the value is not an array.</returns>
    public PdfArray? GetArray(PdfName key) => Get(key) as PdfArray;

    /// <summary>Returns a value as a stream.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The stream, or null if the value is not a stream.</returns>
    public PdfStream? GetStream(PdfName key) => Get(key) as PdfStream;

    /// <summary>Returns a value as a name.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The name, or null if the value is not a name.</returns>
    public PdfName? GetName(PdfName key) => Get(key) as PdfName;

    /// <summary>Returns a value as a string.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The string, or null if the value is not a string.</returns>
    public PdfString? GetString(PdfName key) => Get(key) as PdfString;

    /// <summary>Returns a value as a number.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value of an integer or real, otherwise null.</returns>
    public double? GetNumber(PdfName key) => Get(key).AsNumber();

    /// <summary>Returns a value as an integer; a finite real is rounded.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The integer, or null if the value is not a number or not finite.</returns>
    public long? GetInteger(PdfName key) => Get(key) switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => (long)Math.Round(r.Value),
        _ => null,
    };

    /// <summary>Returns a value as a boolean.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The boolean, or null if the value is not a boolean.</returns>
    public bool? GetBoolean(PdfName key) => (Get(key) as PdfBoolean)?.Value;

    /// <summary>Checks the /Type entry.</summary>
    /// <param name="type">The expected type name.</param>
    /// <returns>True if /Type is <paramref name="type"/>.</returns>
    public bool IsType(PdfName type) => GetName(PdfName.Type) == type;

    internal override void Write(StringBuilder sb, int indent)
    {
        sb.Append("<<");
        foreach (var (key, value) in Entries)
        {
            sb.Append('\n').Append(' ', indent + 2);
            key.Write(sb, indent + 2);
            sb.Append(' ');
            value.Write(sb, indent + 2);
        }
        sb.Append('\n').Append(' ', indent).Append(">>");
    }
}

/// <summary>
/// A stream: dictionary and data. The data is raw (still encoded, possibly encrypted) and lies either in the file
/// (<see cref="DataOffset"/>) or in memory (<see cref="RawData"/>, for example when newly created). Decoding:
/// <c>PdfDocument.GetStreamData</c>.
/// </summary>
public sealed class PdfStream : PdfObject
{
    /// <summary>Creates a stream whose data lies in the file.</summary>
    /// <param name="dictionary">The stream dictionary.</param>
    /// <param name="dataOffset">Position of the data in the file, in bytes.</param>
    /// <param name="dataLength">Length of the data in bytes.</param>
    /// <param name="id">The object the stream belongs to (for decryption); null if unknown.</param>
    public PdfStream(PdfDictionary dictionary, long dataOffset, long dataLength, PdfObjectId? id)
    {
        Dictionary = dictionary;
        DataOffset = dataOffset;
        DataLength = dataLength;
        Id = id;
    }

    /// <summary>Creates a stream whose data lies in memory; it is never decrypted.</summary>
    /// <param name="dictionary">The stream dictionary.</param>
    /// <param name="rawData">The encoded data; the stream keeps this array.</param>
    public PdfStream(PdfDictionary dictionary, byte[] rawData)
    {
        Dictionary = dictionary;
        RawData = rawData;
        DataOffset = -1;
        DataLength = rawData.Length;
    }

    /// <summary>The stream dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Position of the data in the file; -1 if it lies in memory.</summary>
    public long DataOffset { get; }

    /// <summary>Length of the raw data in bytes.</summary>
    public long DataLength { get; }

    /// <summary>Data in memory (still encoded); null if it lies in the file.</summary>
    public byte[]? RawData { get; }

    /// <summary>The object the stream belongs to, for decryption.</summary>
    public PdfObjectId? Id { get; }

    /// <summary>
    /// A form rebuilt for one invocation (redacting, removing hidden content, editing): the object number of the form of the file it stands
    /// for; null for any other stream. Kept in memory only — the writer lets the first copy take the original's place in the structure tree
    /// (an MCR's /Stm; docs/ABLAUFPLAN.md, 6.6 a).
    /// </summary>
    internal int? CopyOf { get; init; }

    /// <summary>
    /// A rebuilt form (<see cref="CopyOf"/>) from whose content characters were removed: the replacement texts of the structure elements of its
    /// marked content go on saving (they may hold the removed text). Kept in memory only (docs/ABLAUFPLAN.md, 6.6 a).
    /// </summary>
    internal bool LostText { get; init; }

    internal override void Write(StringBuilder sb, int indent)
    {
        Dictionary.Write(sb, indent);
        sb.Append(" stream (").Append(DataLength.ToString(CultureInfo.InvariantCulture)).Append(" Bytes)");
    }
}

/// <summary>A reference to an indirect object ("12 0 R").</summary>
public sealed class PdfReference : PdfObject
{
    /// <summary>Creates a reference.</summary>
    /// <param name="id">The identifier of the target object.</param>
    public PdfReference(PdfObjectId id) => Id = id;

    /// <summary>The identifier of the target object.</summary>
    public PdfObjectId Id { get; }

    internal override void Write(StringBuilder sb, int indent) => sb.Append(Id.Number).Append(' ').Append(Id.Generation).Append(" R");
}

/// <summary>Conversions for <see cref="PdfObject"/>.</summary>
public static class PdfObjectExtensions
{
    /// <summary>Returns the value of a number object.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The value of an integer or real, null for anything else.</returns>
    public static double? AsNumber(this PdfObject obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => null,
    };

    /// <summary>Returns the object as a dictionary; for a stream, its dictionary.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The dictionary, or null for anything else.</returns>
    public static PdfDictionary? AsDictionary(this PdfObject obj) => obj switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };
}
