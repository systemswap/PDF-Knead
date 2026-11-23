using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        // ------------------------------------------------------------ Parts of objects as the standard asks of writers (No. 780, 781)
        // file specifications (ISO 32000-2, 7.11.3, Table 43) and Type 3 functions (7.10.4, Table 41), wherever they stand — objects of their
        // own and direct parts of others

        /// <summary>
        /// A simple font (Type 1, multiple master, TrueType) whose /Widths is no array of LastChar − FirstChar + 1 numbers, or whose
        /// /FontDescriptor is no font descriptor (test file bookmarkgetcolor: /Widths names a number tree, /FontDescriptor an outline item): written
        /// with the widths the display sets the codes with and the descriptor of its substitute, as ISO 32000-2, 9.6.2.1, Table 109 asks.
        /// Before, the entries went into the copy as they were, and a reader that read them set every character on top of the one before
        /// (docs/FEHLERGEDAECHTNIS.md, No. 886). An embedded font's own descriptor stays (its program is in it); missing entries stay missing.
        /// </summary>
        /// <param name="dict">A dictionary as read.</param>
        /// <param name="source">Where it comes from; null for objects built anew.</param>
        /// <returns>A copy with valid entries, or null when there is nothing to fix.</returns>
        private PdfDictionary? FontEntriesFixed(PdfDictionary dict, Source? source)
        {
            if (source is null || dict.GetName(PdfName.Type)?.Value != "Font" || dict.GetName(PdfName.Subtype)?.Value is not ("Type1" or "MMType1" or "TrueType"))
                return null;
            var widthsKey = PdfName.Get("Widths");
            // what the writer built itself (PDF/A embeds the fonts and writes their widths and descriptors anew) is right by construction
            var badWidths = dict.GetRaw(widthsKey) is not (null or OutputReference) && dict.Get(widthsKey) is not PdfNull && !NumbersEnough(dict);
            var descriptor = dict.Get(FontDescriptorKey);
            var badDescriptor = dict.GetRaw(FontDescriptorKey) is not (null or OutputReference) && descriptor is not PdfNull && !IsFontDescriptor(descriptor);
            if (!badWidths && !badDescriptor) return null;
            var copy = CloneWithResolver(dict);
            if (badWidths)
            {
                var shown = PdfFont.Get(dict, source.Doc);
                var widths = new PdfArray();
                for (var c = 0; c < 256; c++) widths.Add(Exact(shown.Glyph((uint)c, 1).Width * 1000));
                copy.Set(PdfName.Get("FirstChar"), PdfInteger.Of(0));
                copy.Set(PdfName.Get("LastChar"), PdfInteger.Of(255));
                copy.Set(widthsKey, widths);
            }
            if (badDescriptor) copy.Set(FontDescriptorKey, IndirectDescriptor(StandardFontEmbedding.SubstituteDescriptor(dict), dict));
            return copy;

            static bool NumbersEnough(PdfDictionary font) =>
                font.GetInteger(PdfName.Get("FirstChar")) is { } first && font.GetInteger(PdfName.Get("LastChar")) is { } last &&
                font.Get(PdfName.Get("Widths")) is PdfArray widths && widths.Count == last - first + 1 &&
                Enumerable.Range(0, widths.Count).All(i => widths.Get(i) is PdfInteger or PdfReal);

            static bool IsFontDescriptor(PdfObject value) =>
                value is PdfDictionary d && d.GetName(PdfName.Type)?.Value is "FontDescriptor" or null &&
                (d.ContainsKey(PdfName.Get("FontName")) || d.ContainsKey(PdfName.Get("Flags")));
        }

        /// <summary>The entries of a file specification dictionary (Table 43).</summary>
        private static readonly HashSet<string> FileSpecKeys =
            ["Type", "FS", "F", "UF", "DOS", "Mac", "Unix", "ID", "V", "EF", "RF", "Desc", "CI", "Thumb", "EP", "AFRelationship"];

        /// <summary>
        /// Keys whose dictionaries name a file with /F without being a file specification: the launch parameters of a launch action
        /// (ISO 32000-2, 12.6.4.6, Table 207) and a movie (13.4, Table 300).
        /// </summary>
        private static readonly HashSet<string> NotFileSpecHolders = ["Win", "Mac", "Unix", "Movie"];

        /// <summary>
        /// Keys under which the norm puts a file specification given directly: a file attachment's /FS (Table 187), the /F of an action that
        /// opens or names a file (GoToR, GoToE, Launch, ImportData, SubmitForm; 12.6.4), a media clip's /D (13.2.4.2), the items of /AF (14.13)
        /// and the values of a name tree (/Names; /EmbeddedFiles, 7.7.4). Elsewhere a dictionary without /Type is no file specification, even
        /// with only keys of Table 43 — a private dictionary of a program (under /PieceInfo, a key of its own) got /Type /Filespec and /UF added
        /// (docs/FEHLERGEDAECHTNIS.md, No. 897).
        /// </summary>
        private static readonly HashSet<string> FileSpecHolders = ["FS", "F", "D", "AF", "Names"];

        private static readonly PdfName UfKey = PdfName.Get("UF"), EfKey = PdfName.Get("EF"), RfKey = PdfName.Get("RF"), CiKey = PdfName.Get("CI"),
            DosKey = PdfName.Get("DOS"), MacKey = PdfName.Get("Mac"), UnixKey = PdfName.Get("Unix"), FilespecName = PdfName.Get("Filespec");

        /// <summary>Direct file specifications and collection items taken out of the objects that held them, written as objects of their own.</summary>
        private readonly Queue<(int Number, PdfDictionary Dict, Source? Source)> _liftedQueue = new();

        /// <summary>
        /// Whether a dictionary is a file specification: /Type /Filespec, or — without /Type — no entry outside Table 43 and a file named (/F,
        /// /UF, /DOS, /Mac, /Unix a string) or carried (/EF, /RF, /EP a dictionary). A thread names its first bead with /F, a dictionary: not a
        /// file specification.
        /// </summary>
        private static bool IsFileSpecification(PdfDictionary dict)
        {
            // a /Type naming nothing (a reference into the void) counts as absent (S-A A67)
            if (dict.Get(PdfName.Type) is not PdfNull and var type) return type is PdfName { Value: "Filespec" };
            var names = false;
            foreach (var (key, _) in dict.Entries)
            {
                if (!FileSpecKeys.Contains(key.Value)) return false;
                names |= key.Value switch
                {
                    "F" or "UF" or "DOS" or "Mac" or "Unix" => dict.Get(key) is PdfString,
                    "EF" or "RF" or "EP" => dict.Get(key) is PdfDictionary,
                    _ => false,
                };
            }
            return names;
        }

        /// <summary>
        /// A file specification as Table 43 asks of writers; null if nothing changes. /Type /Filespec (required with /EF, /RF or /EP,
        /// recommended always); /F where no platform name (/DOS, /Mac, /Unix) stands — taken from /UF —; /UF beside /F (PDF 1.7) where the bytes
        /// of /F read as text without doubt: 7-bit ASCII, or a text string with its byte order mark. Other bytes are a platform's file name in
        /// an unknown encoding (7.11.2.1: passed on without conversion); a /UF made from them could name another file — and readers take /UF
        /// before /F.
        /// </summary>
        private PdfDictionary? FileSpecFixed(PdfDictionary dict)
        {
            PdfDictionary? copy = null;
            PdfDictionary Copy() => copy ??= CloneWithResolver(dict);
            // an entry naming nothing (a reference into the void) counts as absent (S-A A67) — it is replaced
            bool Absent(PdfName key) => dict.Get(key) is PdfNull;
            if (Absent(PdfName.Type)) Copy().Set(PdfName.Type, FilespecName);
            var f = dict.Get(F) as PdfString;
            var uf = dict.Get(UfKey) as PdfString;
            if (Absent(F) && Absent(DosKey) && Absent(MacKey) && Absent(UnixKey) && uf is not null)
                Copy().Set(F, new PdfString(PdfTextEncoding.Encode(uf.ToText())));
            // no file named at all: /F is required where /DOS, /Mac and /Unix are absent (Table 43) — a neutral name, /UF follows from it below
            // (before, the specification went out without one; docs/FEHLERGEDAECHTNIS.md, No. 897)
            if (Absent(F) && Absent(DosKey) && Absent(MacKey) && Absent(UnixKey) && uf is null)
            {
                f = new PdfString("Anhang"u8.ToArray());
                Copy().Set(F, f);
            }
            if (Absent(UfKey) && f is not null && CompareVersions(_version, "1.7") >= 0 && UnambiguousText(f) is { } text)
                Copy().Set(UfKey, new PdfString(PdfTextEncoding.Encode(text)));
            return copy;
        }

        /// <summary>The text of a byte string where its bytes leave no doubt: 7-bit ASCII, or a byte order mark (UTF-16BE, UTF-8); else null.</summary>
        private static string? UnambiguousText(PdfString value)
        {
            var bytes = value.Bytes;
            if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return value.ToText();
            foreach (var b in bytes)
                if (b >= 0x80)
                    return null;
            return System.Text.Encoding.ASCII.GetString(bytes);
        }

        /// <summary>
        /// An object with its file specifications as Table 43 asks (<see cref="FileSpecFixed"/>) and its Type 3 functions as Table 41 asks
        /// (<see cref="StitchingFixed"/>), in its direct parts as well; a file specification with /EF or /RF and a collection item (/CI) given
        /// directly go into objects of their own (Table 43: indirectly referenced), written with the object's source. The object itself when
        /// nothing changes.
        /// </summary>
        private PdfObject PartsAsWritten(PdfObject obj, Source? source) => PartsWalk(obj, source, 0, top: true, holder: null);

        /// <summary>The direct parts of a stream's dictionary as <see cref="PartsAsWritten"/> writes them (the dictionary is a copy of the writer's).</summary>
        private void PartsWithin(PdfDictionary streamDictionary, Source? source)
        {
            // a function given as a stream (types 0 and 4) with its integer entries as read (No. 905), a form with its key into the parent tree
            // as read (No. 906)
            if (IntegersAsRead(streamDictionary) is { } integers)
                foreach (var (key, value) in integers)
                    if (value is null) streamDictionary.Remove(key);
                    else streamDictionary.Set(key, value);
            List<(PdfName Key, PdfObject Value)>? changed = null;
            foreach (var (key, item) in streamDictionary.Entries)
            {
                NoteOwnKey(key);
                if (item is PdfDictionary or PdfArray && PartsWalk(item, source, 1, top: false, holder: key.Value) is var walked && !ReferenceEquals(walked, item))
                    (changed ??= []).Add((key, walked));
            }
            if (changed is not null)
                foreach (var (key, value) in changed) streamDictionary.Set(key, value);
        }

        /// <summary>
        /// The prefix of PDF-Knead's own keys, a second-class name (ISO 32000-2, Annex E.2; docs/FEHLERGEDAECHTNIS.md, No. 599) — and the key
        /// of its developer extensions dictionary in /Extensions (No. 793).
        /// </summary>
        internal const string OwnPrefix = "PDFK";

        /// <summary>Whether a written dictionary holds a key of PDF-Knead's own (Annex E.2), seen by <see cref="NoteOwnKey"/>.</summary>
        private bool _ownKeysWritten;

        private void NoteOwnKey(PdfName key)
        {
            if (!_ownKeysWritten && key.Value.Length > OwnPrefix.Length && key.Value.StartsWith(OwnPrefix + "_", StringComparison.Ordinal))
                _ownKeysWritten = true;
        }

        /// <summary>
        /// Whether the prefix PDFK stands in the public list of developer prefixes (ISO 32000-2, Annex E.2). The keys of /Extensions must be
        /// registered prefixes (7.12.1 sentence 3, 7.12.2 sentence 7 — shall): until it is registered, no declaration is written, and one found
        /// is taken out (docs/FEHLERGEDAECHTNIS.md, No. 800; the user has the prefix registered, decision of 12.11.2026). Set to true once the
        /// list holds it — then sentence 17 of E.2 (should) is met as well.
        /// </summary>
        internal const bool OwnPrefixRegistered = false;

        /// <summary>
        /// ISO 32000-2, Annex E.2, sentence 17 (should; docs/FEHLERGEDAECHTNIS.md, No. 793, 800): a file that holds PDF-Knead's own keys declares
        /// them with a developer extensions dictionary under the prefix PDFK in /Extensions (7.12.3: base version 1.7, the first version
        /// with /Extensions, level 1) — from 1.7 only: below, the catalogue has no /Extensions (Table 29) — and only once the prefix is
        /// registered (<see cref="OwnPrefixRegistered"/>; 7.12.1, 7.12.2 ask registered prefixes). A declaration goes when the file holds no such
        /// key any more (cleaned up, elements deleted) or while the prefix is not registered. Called once every other object is written.
        /// </summary>
        private void DeclareOwnExtension(PdfDictionary catalog)
        {
            var own = OwnPrefixRegistered && (_ownKeysWritten || catalog.ContainsKey(Editing.ElementStore.CatalogKey)) && CompareVersions(_version, "1.7") >= 0;
            var current = catalog.GetRaw(ExtensionDeclarations.Key) as PdfDictionary;   // written directly (7.12.1)
            if (!own && current?.ContainsKey(PdfName.Get(OwnPrefix)) != true) return;
            var extensions = new PdfDictionary();
            if (current is not null)
                foreach (var (key, value) in current.Entries) extensions.Set(key, value);
            if (own) ExtensionDeclarations.Raise(extensions, OwnPrefix, "1.7", 1, _version);
            else extensions.Remove(PdfName.Get(OwnPrefix));
            if (ExtensionDeclarations.Any(extensions)) catalog.Set(ExtensionDeclarations.Key, extensions);
            else catalog.Remove(ExtensionDeclarations.Key);
        }

        /// <summary>
        /// Keys whose strings are byte strings (ISO 32000-2, 7.9.2.4: binary data, every byte any value) or arrays of them: an embedded file's
        /// /CheckSum (Table 45), the Web Capture identifier /ID of a page or content set, the font's /Panose (Table 122), the encryption
        /// dictionary's /O, /U, /OE, /UE, /Perms and the public-key /Recipients (7.6), the platform file names /DOS, /Mac, /Unix of a file
        /// specification (Table 43). They stay byte for byte, even where they happen to look like UTF-8 with its byte order mark
        /// (docs/FEHLERGEDAECHTNIS.md, No. 796, 802). So do the keys of a name tree (/Names, /Limits: bytes of any encoding, 7.9.6, sentence 24 —
        /// rewritten, the order and the match with /ID of structure elements would go) and the lookup string of an /Indexed colour space
        /// (8.6.6.3). The signature dictionary is left whole before.
        /// </summary>
        private static readonly HashSet<string> ByteStringKeys = new(StringComparer.Ordinal)
            { "CheckSum", "ID", "Panose", "O", "U", "OE", "UE", "Perms", "Recipients", "DOS", "Mac", "Unix", "Limits" };

        private PdfObject PartsWalk(PdfObject value, Source? source, int depth, bool top, string? holder)
        {
            if (depth > PdfLimits.MaxNesting) return value;
            switch (value)
            {
                // a byte string, or an array of them, is no text (No. 796)
                case PdfString or PdfArray when holder is not null && ByteStringKeys.Contains(holder):
                    return value;
                // below 2.0 a text string in UTF-8 (new in 2.0, 7.9.2.2) as the lower versions have it (No. 784)
                case PdfString text when !Pdf20 && Utf8TextAsBefore(text) is { } before:
                    return before;
                case PdfArray array:
                {
                    List<PdfObject>? items = null;
                    // the keys of a name tree's leaf (every other item of /Names) and the lookup string of [/Indexed base hival lookup] are bytes
                    var treeKeys = holder == "Names";
                    var indexed = array.Count == 4 && array.Get(0) is PdfName { Value: "Indexed" };
                    for (var i = 0; i < array.Count && i < 100_000; i++)
                    {
                        var item = array[i];
                        if (item is PdfString ? Pdf20 || treeKeys && i % 2 == 0 || indexed : item is not (PdfDictionary or PdfArray)) continue;
                        // the items of /AF and the values of a name tree are file specifications where they are dictionaries (No. 897)
                        var walked = PartsWalk(item, source, depth + 1, top: false, holder: holder == "AF" || treeKeys && i % 2 == 1 ? holder : null);
                        if (ReferenceEquals(walked, item)) continue;
                        if (items is null)
                        {
                            items = new List<PdfObject>(array.Count);
                            for (var k = 0; k < array.Count; k++) items.Add(array[k]);
                        }
                        items[i] = walked;
                    }
                    return items is null ? array : new PdfArray(items) { Resolver = array.Resolver };
                }
                case PdfDictionary dict:
                {
                    // a signature's dictionary stays byte for byte (its strings are bytes: /Contents, /ByteRange)
                    if (PdfSerializer.IsSignatureDictionary(dict)) return dict;
                    // indirect (holder unknown): judged by its entries; given directly: only where the norm puts a file specification, or with
                    // /Type /Filespec (No. 897)
                    var spec = (holder is null ? IsFileSpecification(dict)
                        : !NotFileSpecHolders.Contains(holder) && IsFileSpecification(dict) &&
                          (FileSpecHolders.Contains(holder) || dict.Get(PdfName.Type) is PdfName { Value: "Filespec" }));
                    // integer entries as read — a function's (No. 905), a key into the parent tree (No. 906); a stitching function then as Table 41
                    // asks
                    PdfDictionary? integers = null;
                    if (!spec && IntegersAsRead(dict) is { } asRead)
                    {
                        integers = CloneWithResolver(dict);
                        foreach (var (integerKey, integerValue) in asRead)
                            if (integerValue is null) integers.Remove(integerKey);
                            else integers.Set(integerKey, integerValue);
                    }
                    var basis = integers ?? dict;
                    var copy = spec ? FileSpecFixed(dict) : basis.GetRaw(FunctionTypeKey) is PdfInteger { Value: 3 } ? StitchingFixed(basis) ?? integers
                        : integers ?? GoToERenamed(dict, source) ?? FontEntriesFixed(dict, source);
                    List<(PdfName Key, PdfObject Value)>? changed = null;
                    foreach (var (key, item) in (copy ?? dict).Entries)
                    {
                        NoteOwnKey(key);
                        if (item is PdfString ? Pdf20 : item is not (PdfDictionary or PdfArray)) continue;
                        var walked = PartsWalk(item, source, depth + 1, top: false, holder: key.Value);
                        // a collection item of a file specification is indirectly referenced (Table 43)
                        if (spec && key == CiKey && walked is PdfDictionary item2) walked = Lifted(item2, source);
                        if (!ReferenceEquals(walked, item)) (changed ??= []).Add((key, walked));
                    }
                    if (changed is not null)
                    {
                        copy ??= CloneWithResolver(dict);
                        foreach (var (key, walked) in changed) copy.Set(key, walked);
                    }
                    var result = copy ?? dict;
                    // an object of type Template has no /B (ISO 32000-2, 7.7.3.3, Table 31): a page template of the source that carries one is
                    // written without it (docs/FEHLERGEDAECHTNIS.md, No. 1058)
                    if (result.GetRaw(PdfName.Type) is PdfName { Value: "Template" } && result.ContainsKey(BeadsKey))
                    {
                        if (ReferenceEquals(result, dict)) result = CloneWithResolver(dict);
                        result.Remove(BeadsKey);
                    }
                    // with an embedded file or related files the specification is indirectly referenced (Table 43)
                    return !top && spec && (Present(result, EfKey) || Present(result, RfKey)) ? Lifted(result, source) : result;
                }
                default:
                    return value;
            }
        }

        /// <summary>The beads of a page (ISO 32000-2, 7.7.3.3, Table 31), which an object of type Template does not have.</summary>
        private static readonly PdfName BeadsKey = PdfName.Get("B");

        private static readonly PdfName FunctionTypeKey = PdfName.Get("FunctionType"), FunctionsKey = PdfName.Get("Functions"),
            BoundsKey = PdfName.Get("Bounds"), EncodeKey = PdfName.Get("Encode"), DomainKey = PdfName.Get("Domain");

        private static readonly PdfName[] FunctionIntegerKeys = [FunctionTypeKey, PdfName.Get("BitsPerSample"), PdfName.Get("Order")];

        /// <summary>
        /// The integer entries of a function (ISO 32000-2, 7.10.1, Table 38: /FunctionType; Table 39: /BitsPerSample, /Order, /Size) given as reals,
        /// as the integers this program reads them as (S-A A69: rounded; /Size at least 1); null if none. Measured against the reference readers
        /// (17.11.2026): one rounds as this program does, two draw nothing — written as reals, the copy stayed empty there (docs/FEHLERGEDAECHTNIS.md,
        /// No. 905).
        /// </summary>
        private static readonly PdfName StructParentKeyName = PdfName.Get("StructParent"), StructParentsKeyName = PdfName.Get("StructParents");

        /// <summary>
        /// Integer entries given as reals, as this program reads them; null if none. A value null in the list: the entry goes.
        /// <list type="bullet">
        /// <item>a function's (<see cref="FunctionIntegers"/>, No. 905);</item>
        /// <item>a key into the parent tree, /StructParent of an annotation or an object, /StructParents of a page or a form (ISO 32000-2,
        /// 14.7.5.4; Table 31: integer): read as the keys of a number tree (<see cref="NumberTree.KeyOf"/>: an integral real is that integer), so
        /// written; a real that is no integer is no key and goes. Before, the program read such a key differently from place to place — the
        /// accessibility check rounded, the writer took only integers — and wrote it as it came (docs/FEHLERGEDAECHTNIS.md, No. 906).</item>
        /// </list>
        /// </summary>
        private static List<(PdfName Key, PdfObject? Value)>? IntegersAsRead(PdfDictionary dict)
        {
            List<(PdfName Key, PdfObject? Value)>? changed = null;
            if (FunctionIntegers(dict) is { } function) (changed ??= []).AddRange(function.Select(f => (f.Key, (PdfObject?)f.Value)));
            foreach (var key in new[] { StructParentKeyName, StructParentsKeyName })
                if (dict.Get(key) is PdfReal real) (changed ??= []).Add((key, NumberTree.KeyOf(real) is { } integer ? PdfInteger.Of(integer) : null));
            return changed;
        }

        private static List<(PdfName Key, PdfObject Value)>? FunctionIntegers(PdfDictionary dict)
        {
            if (!dict.ContainsKey(FunctionTypeKey)) return null;
            List<(PdfName Key, PdfObject Value)>? changed = null;
            foreach (var key in FunctionIntegerKeys)
                if (dict.Get(key) is PdfReal && dict.GetInteger(key) is { } value) (changed ??= []).Add((key, PdfInteger.Of(value)));
            if (dict.Get(PdfName.Size) is PdfArray size && size.Count <= 32 && size.Any(item => item is PdfReal) &&
                size.All(item => item.AsNumber() is { } v && double.IsFinite(v) && Math.Abs(v) < int.MaxValue))
                (changed ??= []).Add((PdfName.Size, new PdfArray(size.Select(item => (PdfObject)PdfInteger.Of(Math.Max(1, (long)Math.Round(item.AsNumber()!.Value)))))));
            return changed;
        }

        /// <summary>
        /// A Type 3 function as Table 41 asks, written as this program reads it (<see cref="Graphics.StitchingFunction"/>); null if nothing
        /// changes. /Bounds with k − 1 numbers: the one too many some generators write — the last at the end of the domain — left out
        /// (S-A A19); /Encode with 2 × k numbers: missing or short, [0 1] for each function, longer, its first 2 × k (S-A A89); an entry of
        /// either that is not a number as read, 0. A function this program cannot read (no functions, /Bounds of another length) stays as it is.
        /// </summary>
        private PdfDictionary? StitchingFixed(PdfDictionary dict)
        {
            if (dict.Get(FunctionsKey) is not PdfArray { Count: > 0 } functions) return null;
            var k = functions.Count;
            var bounds = Graphics.PdfFunction.Numbers(dict.Get(BoundsKey) as PdfArray) ?? [];
            var domain = Graphics.PdfFunction.Numbers(dict.Get(DomainKey) as PdfArray) ?? [];
            if (bounds.Length == k && domain.Length >= 2 && bounds[^1] >= domain[1]) bounds = bounds[..^1];
            if (bounds.Length != k - 1) return null;
            var encode = Graphics.PdfFunction.Numbers(dict.Get(EncodeKey) as PdfArray) ?? [];
            encode = encode.Length < 2 * k ? [.. Enumerable.Range(0, k).SelectMany(_ => new[] { 0.0, 1.0 })] : encode[..(2 * k)];
            int[] chosen = [.. Enumerable.Range(0, k)];
            if (k >= 2 && domain.Length >= 2 && domain[0] < domain[1])
            {
                var sorted = bounds.Zip(bounds.Skip(1)).All(p => p.First <= p.Second);
                var outside = bounds.Count(b => b < domain[0] || b > domain[1]);
                if (sorted && outside <= 1)
                    chosen = [.. Enumerable.Range(0, BoundsInTheDomain(bounds, encode, k, domain[0], domain[1], out bounds, out encode))];
                else chosen = AsRead(bounds, encode, k, domain[0], domain[1], out bounds, out encode);
            }
            PdfDictionary? copy = null;
            if (chosen.Length < k || chosen.Select((c, i) => c != i).Any(x => x))
                (copy ??= CloneWithResolver(dict)).Set(FunctionsKey, new PdfArray(chosen.Select(i => functions[i])));
            if (!AsWritten(dict.Get(BoundsKey), bounds)) (copy ??= CloneWithResolver(dict)).Set(BoundsKey, NumberArray(bounds));
            if (!AsWritten(dict.Get(EncodeKey), encode)) (copy ??= CloneWithResolver(dict)).Set(EncodeKey, NumberArray(encode));
            return copy;

            // 7.10.4, Table 41: the bounds lie within the domain — from 2.0 a first bound may equal Domain0 and a last one Domain1 (that function
            // only at that point, x' its first Encode value), 1.x asks Domain0 < Bounds0 and the last bound < Domain1. The function as this
            // program reads it (StitchingFunction) is kept: the bound moves to the edge (2.0) or a hair inside it (1.x: 1/100 000 of the domain,
            // the decision of 12.11.2026 to keep the version asked for — before, the file was raised to 2.0 and its other objects
            // stayed 1.x, No. 784, 801), the neighbour's Encode values follow its old mapping, the edge function is constant at its first Encode
            // value; a last bound beyond Domain1 belongs to a function never reached, which goes. Several bounds outside: AsRead.
            // Bounds out of order, or several outside the domain (7.10.4, Table 41: in increasing order, within the domain): the function as
            // StitchingFunction reads it — function j where the bounds before it all lie at or below x and its own above (from the largest
            // bound before it to its own, cut to the domain), the point Domain0 with the first function where the first bound lies at or
            // below Domain0 — written with bounds in order inside the domain, the functions never reached left out, each Encode pair mapping
            // its new interval as the old one did. Before, such functions went out as they came (docs/FEHLERGEDAECHTNIS.md, No. 898).
            int[] AsRead(double[] b, double[] e, int count, double d0, double d1, out double[] newBounds, out double[] newEncode)
            {
                var chosenFunctions = new List<int>();
                var starts = new List<double>();
                var mapped = new List<double>();
                var inside = Math.Max(1e-5 * (d1 - d0), 0);
                var point = b[0] <= d0;
                if (point)
                {
                    chosenFunctions.Add(0);
                    mapped.Add(e[0]);
                    mapped.Add(e[0]);
                }
                var largest = double.NegativeInfinity;
                for (var j = 0; j < count; j++)
                {
                    var lowOld = j == 0 ? d0 : b[j - 1];
                    var highOld = j == count - 1 ? d1 : b[j];
                    var low = j == 0 ? d0 : Math.Max(d0, largest);
                    var high = j == count - 1 ? d1 : Math.Min(d1, b[j]);
                    if (j < count - 1) largest = Math.Max(largest, b[j]);
                    if (high <= low) continue;
                    // after the point of Domain0: its function ends at Domain0 (2.0) or a hair inside it (1.x, as BoundsInTheDomain)
                    if (chosenFunctions.Count == 1 && point && low <= d0) low = Pdf20 ? d0 : d0 + Math.Min(inside, (high - d0) / 2);
                    if (chosenFunctions.Count > 0) starts.Add(low);
                    chosenFunctions.Add(j);
                    mapped.Add(Graphics.PdfFunction.Interpolate(low, lowOld, highOld, e[2 * j], e[2 * j + 1]));
                    mapped.Add(Graphics.PdfFunction.Interpolate(high, lowOld, highOld, e[2 * j], e[2 * j + 1]));
                }
                newBounds = [.. starts];
                newEncode = [.. mapped];
                return [.. chosenFunctions];
            }

            int BoundsInTheDomain(double[] b, double[] e, int count, double d0, double d1, out double[] newBounds, out double[] newEncode)
            {
                b = [.. b];
                e = [.. e];
                var inside = Math.Max(1e-5 * (d1 - d0), 0);
                if (b[0] < d0 || b[0] == d0 && !Pdf20)
                {
                    var high = count == 2 ? d1 : b[1];
                    if (high > d0)
                    {
                        var at = Pdf20 ? d0 : d0 + Math.Min(inside, (high - d0) / 2);
                        e[2] = Graphics.PdfFunction.Interpolate(at, b[0], high, e[2], e[3]);
                        e[1] = e[0];
                        b[0] = at;
                    }
                }
                var last = b.Length - 1;
                var low = last == 0 ? d0 : b[last - 1];
                if (b[last] > d1 && low < d1)
                {
                    // the last function is never reached: the one before it ends at Domain1, with the Encode value its mapping gave there
                    e[2 * last + 1] = Graphics.PdfFunction.Interpolate(d1, low, b[last], e[2 * last], e[2 * last + 1]);
                    newBounds = b[..last];
                    newEncode = e[..(2 * count - 2)];
                    return count - 1;
                }
                if (b[last] == d1 && !Pdf20 && low < d1)
                {
                    var at = d1 - Math.Min(inside, (d1 - low) / 2);
                    e[2 * last + 1] = Graphics.PdfFunction.Interpolate(at, low, d1, e[2 * last], e[2 * last + 1]);
                    e[2 * last + 3] = e[2 * last + 2];
                    b[last] = at;
                }
                newBounds = b;
                newEncode = e;
                return count;
            }

            // the array as stored already holds exactly these numbers
            static bool AsWritten(PdfObject stored, double[] numbers)
            {
                if (stored is not PdfArray array || array.Count != numbers.Length) return false;
                for (var i = 0; i < numbers.Length; i++)
                    if (array.Get(i) is not (PdfInteger or PdfReal) || array.GetNumber(i) != numbers[i])
                        return false;
                return true;
            }
        }

        private static PdfArray NumberArray(double[] numbers) =>
            new(numbers.Select(n => n == Math.Floor(n) && Math.Abs(n) < 1e15 ? (PdfObject)PdfInteger.Of((long)n) : new PdfReal(n)));

        /// <summary>
        /// A text string in UTF-8 (byte order mark EF BB BF, valid UTF-8 after it; ISO 32000-2, 7.9.2.2 — new in 2.0) as the versions before have
        /// text strings: PDFDocEncoding where every character has one, else UTF-16BE — the same text, which readers of 1.x would show as
        /// „ï»¿“ and mojibake otherwise. Null for any other string (bytes that are not valid UTF-8 stay as they are).
        /// </summary>
        private static PdfString? Utf8TextAsBefore(PdfString value)
        {
            var bytes = value.Bytes;
            if (!bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return null;
            try
            {
                return new PdfString(PdfTextEncoding.Encode(StrictUtf8.GetString(bytes, 3, bytes.Length - 3)));
            }
            catch (System.Text.DecoderFallbackException)
            {
                return null;
            }
        }

        private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>A direct dictionary as an object of its own, written with the source of the object it was in (references stay its own).</summary>
        private OutputReference Lifted(PdfDictionary dict, Source? source)
        {
            var number = NewNumber();
            _liftedQueue.Enqueue((number, dict, source));
            return new OutputReference(number);
        }
    }
}
