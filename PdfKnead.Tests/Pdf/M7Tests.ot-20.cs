using System.Buffers.Binary;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-20 (OpenType glyf 19.1–19.4 and GPOS 20.1–20.3): simple and composite glyphs built by hand in a small TrueType font, and
/// GPOS tables built by hand in Liberation Sans. The builders here (prefix Ot20) are shared with the packages ot-21 and ot-22.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ builder: bytes with labels (shared with ot-21, ot-22)

    /// <summary>Big-endian bytes with labels; 16- and 32-bit offsets are filled in once every label is placed.</summary>
    internal sealed class Ot20Bytes
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int At, string Target, int From, bool Wide)> _offsets = [];
        private int _next;

        public int Here => _bytes.Count;

        public string NewLabel() => "#" + _next++;

        public Ot20Bytes U8(int v)
        {
            _bytes.Add((byte)v);
            return this;
        }

        public Ot20Bytes U16(int v)
        {
            _bytes.Add((byte)(v >> 8));
            _bytes.Add((byte)v);
            return this;
        }

        public Ot20Bytes U32(long v) => U16((int)(v >> 16)).U16((int)v);

        public Ot20Bytes Bytes(IEnumerable<byte> data)
        {
            _bytes.AddRange(data);
            return this;
        }

        public Ot20Bytes Tag(string tag)
        {
            foreach (var c in tag) _bytes.Add((byte)c);
            return this;
        }

        public Ot20Bytes Label(string name)
        {
            _labels[name] = Here;
            return this;
        }

        /// <summary>A 16-bit offset to <paramref name="target"/>, counted from the position <paramref name="from"/>.</summary>
        public Ot20Bytes Off16(string target, int from)
        {
            _offsets.Add((Here, target, from, false));
            return U16(0);
        }

        public Ot20Bytes Off32(string target, int from)
        {
            _offsets.Add((Here, target, from, true));
            return U32(0);
        }

        public int Position(string label) => _labels[label];

        public byte[] ToArray()
        {
            var bytes = _bytes.ToArray();
            foreach (var (at, target, from, wide) in _offsets)
            {
                var value = _labels[target] - from;
                Assert.InRange(value, 1, wide ? int.MaxValue : 0xFFFF);
                if (wide) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)value);
                else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), (ushort)value);
            }
            return bytes;
        }
    }

    private static byte[] Ot20Raw(Action<Ot20Bytes> write)
    {
        var b = new Ot20Bytes();
        write(b);
        return b.ToArray();
    }

    // ------------------------------------------------------------ builder: GPOS in Liberation Sans

    private static readonly OpenTypeFont Ot20Lib = OpenTypeFont.Parse(ShaperLimitTests.Liberation);

    private static int Ot20G(char c) => Ot20Lib.GlyphForUnicode(c);

    private static double Ot20Adv(char c) => Ot20Lib.AdvanceWidth(Ot20G(c));

    /// <summary>
    /// A GSUB or GPOS of one script „latn“ whose default language system names every feature, then the FeatureList and the LookupList; with
    /// <paramref name="featureVariations"/> a version 1.1 header and the FeatureVariations table at the end.
    /// </summary>
    internal static byte[] Ot20Layout((string Tag, int[] Lookups)[] features, Action<Ot20Bytes>[] lookups, Action<Ot20Bytes>? featureVariations = null)
    {
        var b = new Ot20Bytes();
        string scriptList = b.NewLabel(), featureList = b.NewLabel(), lookupList = b.NewLabel(), variations = b.NewLabel();
        b.U16(1).U16(featureVariations is null ? 0 : 1).Off16(scriptList, 0).Off16(featureList, 0).Off16(lookupList, 0);
        if (featureVariations is not null) b.Off32(variations, 0);
        b.Label(scriptList);
        var at = b.Here;
        var script = b.NewLabel();
        b.U16(1).Tag("latn").Off16(script, at);
        b.Label(script);
        b.U16(4).U16(0);                                                         // default language system right after
        b.U16(0).U16(0xFFFF).U16(features.Length);
        for (var i = 0; i < features.Length; i++) b.U16(i);
        b.Label(featureList);
        at = b.Here;
        var labels = features.Select(_ => b.NewLabel()).ToArray();
        b.U16(features.Length);
        for (var i = 0; i < features.Length; i++) b.Tag(features[i].Tag).Off16(labels[i], at);
        for (var i = 0; i < features.Length; i++)
        {
            b.Label(labels[i]).U16(0).U16(features[i].Lookups.Length);
            foreach (var l in features[i].Lookups) b.U16(l);
        }
        b.Label(lookupList);
        at = b.Here;
        var lookupLabels = lookups.Select(_ => b.NewLabel()).ToArray();
        b.U16(lookups.Length);
        foreach (var l in lookupLabels) b.Off16(l, at);
        for (var i = 0; i < lookups.Length; i++)
        {
            b.Label(lookupLabels[i]);
            lookups[i](b);
        }
        if (featureVariations is not null)
        {
            b.Label(variations);
            featureVariations(b);
        }
        return b.ToArray();
    }

    /// <summary>A GPOS whose feature „kern“ names lookup 0; the further lookups are reached only from it (or not at all).</summary>
    internal static byte[] Ot20Kern(params Action<Ot20Bytes>[] lookups) => Ot20Layout([("kern", [0])], lookups);

    /// <summary>A lookup table with its subtables written right after it.</summary>
    internal static Action<Ot20Bytes> Ot20Lookup(int type, int flag, params Action<Ot20Bytes>[] subtables) => b =>
    {
        var start = b.Here;
        var labels = subtables.Select(_ => b.NewLabel()).ToArray();
        b.U16(type).U16(flag).U16(subtables.Length);
        foreach (var l in labels) b.Off16(l, start);
        for (var i = 0; i < subtables.Length; i++)
        {
            b.Label(labels[i]);
            subtables[i](b);
        }
    };

    internal static Action<Ot20Bytes> Ot20Lookup(int type, params Action<Ot20Bytes>[] subtables) => Ot20Lookup(type, 0, subtables);

    internal static void Ot20Coverage(Ot20Bytes b, IEnumerable<int> glyphs)
    {
        var sorted = glyphs.Distinct().Order().ToArray();
        b.U16(1).U16(sorted.Length);
        foreach (var g in sorted) b.U16(g);
    }

    internal static void Ot20ClassDef(Ot20Bytes b, IReadOnlyDictionary<int, int> classes)
    {
        var sorted = classes.OrderBy(c => c.Key).ToArray();
        b.U16(2).U16(sorted.Length);
        foreach (var (glyph, cls) in sorted) b.U16(glyph).U16(glyph).U16(cls);
    }

    /// <summary>Single positioning format 1: one value record (the values in the order of the format's bits) for every glyph covered.</summary>
    internal static Action<Ot20Bytes> Ot20Single1(int[] glyphs, int valueFormat, params int[] values) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(valueFormat);
        foreach (var v in values) b.U16(v & 0xFFFF);
        b.Label(coverage);
        Ot20Coverage(b, glyphs);
    };

    /// <summary>Single positioning format 2: one value record per glyph in coverage order (glyphs sorted), valueCount as given or their number.</summary>
    internal static Action<Ot20Bytes> Ot20Single2(int[] glyphs, int valueFormat, int[][] records, int valueCount = -1) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(2).Off16(coverage, start).U16(valueFormat).U16(valueCount >= 0 ? valueCount : records.Length);
        foreach (var record in records)
            foreach (var v in record) b.U16(v & 0xFFFF);
        b.Label(coverage);
        Ot20Coverage(b, glyphs);
    };

    /// <summary>Pair positioning format 1: pair sets per first glyph, pair value records sorted by the second glyph.</summary>
    internal static Action<Ot20Bytes> Ot20Pair1(int valueFormat1, int valueFormat2, params (int First, int Second, int[] Value1, int[] Value2)[] pairs) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        var groups = pairs.GroupBy(p => p.First).OrderBy(g => g.Key).ToArray();
        var sets = groups.Select(_ => b.NewLabel()).ToArray();
        b.U16(1).Off16(coverage, start).U16(valueFormat1).U16(valueFormat2).U16(groups.Length);
        foreach (var s in sets) b.Off16(s, start);
        for (var i = 0; i < groups.Length; i++)
        {
            var members = groups[i].OrderBy(p => p.Second).ToArray();
            b.Label(sets[i]).U16(members.Length);
            foreach (var p in members)
            {
                b.U16(p.Second);
                foreach (var v in p.Value1) b.U16(v & 0xFFFF);
                foreach (var v in p.Value2) b.U16(v & 0xFFFF);
            }
        }
        b.Label(coverage);
        Ot20Coverage(b, groups.Select(g => g.Key));
    };

    /// <summary>
    /// Pair positioning format 2: coverage, both ClassDefs (the second NULL-free; <paramref name="classes2"/> null: the same offset as the first),
    /// and the class records from <paramref name="record"/> (class 1, class 2 → both value records).
    /// </summary>
    internal static Action<Ot20Bytes> Ot20Pair2(int[] coverage, int valueFormat1, int valueFormat2, IReadOnlyDictionary<int, int> classes1,
        IReadOnlyDictionary<int, int>? classes2, int class1Count, int class2Count, Func<int, int, (int[] Value1, int[] Value2)> record) => b =>
    {
        var start = b.Here;
        string cov = b.NewLabel(), def1 = b.NewLabel(), def2 = b.NewLabel();
        b.U16(2).Off16(cov, start).U16(valueFormat1).U16(valueFormat2).Off16(def1, start).Off16(classes2 is null ? def1 : def2, start)
            .U16(class1Count).U16(class2Count);
        for (var c1 = 0; c1 < class1Count; c1++)
            for (var c2 = 0; c2 < class2Count; c2++)
            {
                var (v1, v2) = record(c1, c2);
                foreach (var v in v1) b.U16(v & 0xFFFF);
                foreach (var v in v2) b.U16(v & 0xFFFF);
            }
        b.Label(cov);
        Ot20Coverage(b, coverage);
        b.Label(def1);
        Ot20ClassDef(b, classes1);
        if (classes2 is not null)
        {
            b.Label(def2);
            Ot20ClassDef(b, classes2);
        }
    };

    /// <summary>An anchor table of format 1.</summary>
    internal static byte[] Ot20Anchor(int x, int y) => Ot20Raw(b => b.U16(1).U16(x & 0xFFFF).U16(y & 0xFFFF));

    /// <summary>
    /// Writes an anchor (a self-contained table: device tables of format 3 follow it, their offsets counted from its start) or NULL, and
    /// returns nothing; the offset to it is written by the caller as a label.
    /// </summary>
    private static void Ot20WriteAnchors(Ot20Bytes b, List<(string Label, byte[] Anchor)> anchors)
    {
        foreach (var (label, anchor) in anchors) b.Label(label).Bytes(anchor);
    }

    /// <summary>
    /// Mark-to-base (type 4) or mark-to-mark (type 6) attachment format 1: marks (glyph, class, anchor) sorted by glyph, bases (or mark2
    /// glyphs) with one anchor per class (null: NULL offset); with <paramref name="markCount"/> fewer mark records than marks covered.
    /// </summary>
    internal static Action<Ot20Bytes> Ot20MarkAttach((int Glyph, int Class, byte[] Anchor)[] marks, (int Glyph, byte[]?[] Anchors)[] bases, int classCount,
        int markCount = -1) => b =>
    {
        var start = b.Here;
        string markCov = b.NewLabel(), baseCov = b.NewLabel(), markArray = b.NewLabel(), baseArray = b.NewLabel();
        var sortedMarks = marks.OrderBy(m => m.Glyph).ToArray();
        var sortedBases = bases.OrderBy(m => m.Glyph).ToArray();
        b.U16(1).Off16(markCov, start).Off16(baseCov, start).U16(classCount).Off16(markArray, start).Off16(baseArray, start);
        b.Label(markCov);
        Ot20Coverage(b, sortedMarks.Select(m => m.Glyph));
        b.Label(baseCov);
        Ot20Coverage(b, sortedBases.Select(m => m.Glyph));
        b.Label(markArray);
        var at = b.Here;
        var written = markCount >= 0 ? sortedMarks.Take(markCount).ToArray() : sortedMarks;
        var anchors = new List<(string, byte[])>();
        b.U16(written.Length);
        foreach (var m in written)
        {
            var label = b.NewLabel();
            b.U16(m.Class).Off16(label, at);
            anchors.Add((label, m.Anchor));
        }
        Ot20WriteAnchors(b, anchors);
        b.Label(baseArray);
        at = b.Here;
        anchors.Clear();
        b.U16(sortedBases.Length);
        foreach (var basis in sortedBases)
            for (var c = 0; c < classCount; c++)
                if (basis.Anchors[c] is { } anchor)
                {
                    var label = b.NewLabel();
                    b.Off16(label, at);
                    anchors.Add((label, anchor));
                }
                else b.U16(0);
        Ot20WriteAnchors(b, anchors);
    };

    /// <summary>Mark-to-ligature attachment format 1: marks as for <see cref="Ot20MarkAttach"/>, ligatures with one anchor per component and class.</summary>
    internal static Action<Ot20Bytes> Ot20MarkLigature((int Glyph, int Class, byte[] Anchor)[] marks, (int Glyph, byte[]?[][] Components)[] ligatures,
        int classCount) => b =>
    {
        var start = b.Here;
        string markCov = b.NewLabel(), ligCov = b.NewLabel(), markArray = b.NewLabel(), ligArray = b.NewLabel();
        var sortedMarks = marks.OrderBy(m => m.Glyph).ToArray();
        var sortedLigs = ligatures.OrderBy(l => l.Glyph).ToArray();
        b.U16(1).Off16(markCov, start).Off16(ligCov, start).U16(classCount).Off16(markArray, start).Off16(ligArray, start);
        b.Label(markCov);
        Ot20Coverage(b, sortedMarks.Select(m => m.Glyph));
        b.Label(ligCov);
        Ot20Coverage(b, sortedLigs.Select(l => l.Glyph));
        b.Label(markArray);
        var at = b.Here;
        var anchors = new List<(string, byte[])>();
        b.U16(sortedMarks.Length);
        foreach (var m in sortedMarks)
        {
            var label = b.NewLabel();
            b.U16(m.Class).Off16(label, at);
            anchors.Add((label, m.Anchor));
        }
        Ot20WriteAnchors(b, anchors);
        b.Label(ligArray);
        at = b.Here;
        var attachLabels = sortedLigs.Select(_ => b.NewLabel()).ToArray();
        b.U16(sortedLigs.Length);
        foreach (var l in attachLabels) b.Off16(l, at);
        for (var i = 0; i < sortedLigs.Length; i++)
        {
            b.Label(attachLabels[i]);
            var attach = b.Here;
            anchors.Clear();
            b.U16(sortedLigs[i].Components.Length);
            foreach (var component in sortedLigs[i].Components)
                for (var c = 0; c < classCount; c++)
                    if (component[c] is { } anchor)
                    {
                        var label = b.NewLabel();
                        b.Off16(label, attach);
                        anchors.Add((label, anchor));
                    }
                    else b.U16(0);
            Ot20WriteAnchors(b, anchors);
        }
    };

    /// <summary>An extension subtable (GPOS 9) pointing to <paramref name="inner"/> of the lookup type given (format 1 unless given).</summary>
    internal static Action<Ot20Bytes> Ot20Extension(int type, Action<Ot20Bytes> inner, int format = 1) => b =>
    {
        var start = b.Here;
        var label = b.NewLabel();
        b.U16(format).U16(type).Off32(label, start);
        b.Label(label);
        inner(b);
    };

    /// <summary>A GDEF of version 1.0 with glyph classes and, if given, mark attachment classes.</summary>
    internal static byte[] Ot20Gdef(IReadOnlyDictionary<int, int> classes, IReadOnlyDictionary<int, int>? markClasses = null)
    {
        var b = new Ot20Bytes();
        string glyphClasses = b.NewLabel(), attachClasses = b.NewLabel();
        b.U16(1).U16(0).Off16(glyphClasses, 0).U16(0).U16(0);
        if (markClasses is not null) b.Off16(attachClasses, 0);
        else b.U16(0);
        b.Label(glyphClasses);
        Ot20ClassDef(b, classes);
        if (markClasses is not null)
        {
            b.Label(attachClasses);
            Ot20ClassDef(b, markClasses);
        }
        return b.ToArray();
    }

    /// <summary>Liberation Sans with the GPOS, GDEF and GSUB given (null: none) and without the kern table; further tables replaced as given.</summary>
    internal static TextShaper Ot20Shaper(byte[]? gpos, byte[]? gdef = null, byte[]? gsub = null, IReadOnlyDictionary<string, byte[]?>? more = null)
    {
        var tables = new Dictionary<string, byte[]?> { ["GPOS"] = gpos, ["GDEF"] = gdef, ["GSUB"] = gsub, ["kern"] = null };
        foreach (var (tag, data) in more ?? new Dictionary<string, byte[]?>()) tables[tag] = data;
        return TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, tables)));
    }

    /// <summary>An fvar of one axis „wght“ (100 … 400 … 900), without named instances.</summary>
    internal static byte[] Ot20Fvar() => Ot20Raw(b =>
    {
        b.U16(1).U16(0).U16(16).U16(2).U16(1).U16(20).U16(0).U16(8);
        b.Tag("wght").U32(100L << 16).U32(400L << 16).U32(900L << 16).U16(0).U16(256);
    });

    /// <summary>A Device table (format 2, sizes 10–12, deltas +7) — a large correction for small sizes.</summary>
    internal static byte[] Ot20Device() => Ot20Raw(b => b.U16(10).U16(12).U16(2).U16(0x7770));

    /// <summary>A VariationIndex table (outer 0, inner 0, deltaFormat 0x8000).</summary>
    internal static byte[] Ot20VariationIndex() => Ot20Raw(b => b.U16(0).U16(0).U16(0x8000));

    // ------------------------------------------------------------ builder: a small TrueType font with glyphs written by hand

    /// <summary>A simple glyph: its contours of points (16-bit coordinates, on or off the curve), optional instructions and flag bits OR-ed into every flag.</summary>
    private static byte[] Ot20Simple((int X, int Y, bool On)[][] contours, byte[]? instructions = null, int firstFlagExtra = 0, int everyFlagExtra = 0,
        int contourCount = int.MinValue, (int, int, int, int)? box = null) => Ot20Raw(b =>
    {
        var points = contours.SelectMany(c => c).ToArray();
        var (x0, y0, x1, y1) = box ?? (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        b.U16(contourCount == int.MinValue ? contours.Length : contourCount).U16(x0 & 0xFFFF).U16(y0 & 0xFFFF).U16(x1 & 0xFFFF).U16(y1 & 0xFFFF);
        var end = -1;
        foreach (var c in contours)
        {
            end += c.Length;
            b.U16(end);
        }
        b.U16(instructions?.Length ?? 0).Bytes(instructions ?? []);
        for (var i = 0; i < points.Length; i++) b.U8((points[i].On ? 1 : 0) | everyFlagExtra | (i == 0 ? firstFlagExtra : 0));
        var prev = 0;
        foreach (var p in points)
        {
            b.U16((p.X - prev) & 0xFFFF);
            prev = p.X;
        }
        prev = 0;
        foreach (var p in points)
        {
            b.U16((p.Y - prev) & 0xFFFF);
            prev = p.Y;
        }
    });

    private static (int X, int Y, bool On)[] Ot20Rect(int x0, int y0, int x1, int y1) => [(x0, y0, true), (x1, y0, true), (x1, y1, true), (x0, y1, true)];

    /// <summary>The usual test component: one rectangle (0, 0)–(100, 50).</summary>
    private static readonly byte[] Ot20Box = Ot20Simple([Ot20Rect(0, 0, 100, 50)]);

    /// <summary>A component of a composite glyph: flags (without MORE_COMPONENTS and WE_HAVE_INSTRUCTIONS), glyph, both arguments, scale words (F2DOT14).</summary>
    internal readonly record struct Ot20Part(int Flags, int Glyph, int Arg1, int Arg2, params int[] Scale);

    /// <summary>A composite glyph of the parts given (numberOfContours −1 unless given; instructions after the last part if given).</summary>
    private static byte[] Ot20Composite(Ot20Part[] parts, int contours = -1, byte[]? instructions = null) => Ot20Raw(b =>
    {
        b.U16(contours & 0xFFFF).U16(0).U16(0).U16(0).U16(0);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            var flags = p.Flags | (i + 1 < parts.Length ? 0x20 : 0) | (i + 1 == parts.Length && instructions is not null ? 0x100 : 0);
            b.U16(flags).U16(p.Glyph);
            if ((flags & 1) != 0) b.U16(p.Arg1 & 0xFFFF).U16(p.Arg2 & 0xFFFF);
            else b.U8(p.Arg1).U8(p.Arg2);
            foreach (var s in p.Scale) b.U16(s & 0xFFFF);
        }
        if (instructions is not null) b.U16(instructions.Length).Bytes(instructions);
    });

    /// <summary>Offset words (ARG_1_AND_2_ARE_WORDS | ARGS_ARE_XY_VALUES).</summary>
    private const int Ot20Xy = 0x0003;

    /// <summary>
    /// A TrueType font of the glyphs given (glyph 0 first), with head (1000 units per em, long loca, the head flags given), hhea, hmtx
    /// (advance 600 or as given, side bearing 0 or as given), maxp 0.5, loca and glyf; the glyphs are stored in the order <paramref name="storage"/>
    /// (default: by glyph number), each padded to four bytes unless <paramref name="pad"/> is false; further tables as given.
    /// </summary>
    private static byte[] Ot20Font(IReadOnlyList<byte[]> glyphs, int[]? advances = null, IReadOnlyDictionary<string, byte[]>? more = null,
        int[]? storage = null, bool pad = true, int headFlags = 0)
    {
        var n = glyphs.Count;
        var glyf = new List<byte>();
        var starts = new int[n];
        foreach (var g in storage ?? [.. Enumerable.Range(0, n)])
        {
            starts[g] = glyf.Count;
            glyf.AddRange(glyphs[g]);
            while (pad && glyf.Count % 4 != 0) glyf.Add(0);
        }
        var loca = new byte[(n + 1) * 4];
        for (var g = 0; g < n; g++) BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)starts[g]);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(n * 4), (uint)glyf.Count);
        var head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(16), (ushort)headFlags);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)n);
        var hmtx = new byte[n * 4];
        for (var g = 0; g < n; g++) BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), (ushort)(advances?[g] ?? 600));
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)n);
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = [.. glyf], ["head"] = head, ["hhea"] = hhea, ["hmtx"] = hmtx, ["loca"] = loca, ["maxp"] = maxp,
        };
        foreach (var (tag, data) in more ?? new Dictionary<string, byte[]>()) tables[tag] = data;
        var font = new List<byte> { 0, 1, 0, 0, (byte)(tables.Count >> 8), (byte)tables.Count, 0, 0, 0, 0, 0, 0 };
        var at = 12 + 16 * tables.Count;
        var directory = new List<byte>();
        foreach (var (tag, data) in tables)
        {
            directory.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            directory.AddRange(new byte[4]);
            directory.AddRange([(byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at]);
            directory.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            at += (data.Length + 3) & ~3;
        }
        font.AddRange(directory);
        foreach (var (_, data) in tables)
        {
            font.AddRange(data);
            while (font.Count % 4 != 0) font.Add(0);
        }
        return [.. font];
    }

    /// <summary>The bytes of a table of a font file (single font).</summary>
    private static byte[] Ot20Table(byte[] font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = 12 + 16 * i;
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) != tag) continue;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12));
            return font.AsSpan(offset, length).ToArray();
        }
        return [];
    }

    /// <summary>The data of glyph <paramref name="glyph"/> in glyf (from loca; long or short offsets after head).</summary>
    private static byte[] Ot20GlyphBytes(byte[] font, int glyph)
    {
        var loca = Ot20Table(font, "loca");
        var glyf = Ot20Table(font, "glyf");
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(Ot20Table(font, "head").AsSpan(50)) != 0;
        int Offset(int g) => longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2;
        return glyf.AsSpan(Offset(glyph), Offset(glyph + 1) - Offset(glyph)).ToArray();
    }

    private static void Ot20SameOutline(GlyphOutline expected, GlyphOutline actual)
    {
        Assert.Equal(expected.Verbs, actual.Verbs);
        Assert.Equal(expected.Points, actual.Points);
    }

    /// <summary>The points of <paramref name="outline"/>, each moved by (dx, dy).</summary>
    private static float[] Ot20Moved(GlyphOutline outline, float dx, float dy) =>
        [.. outline.Points.Select((v, i) => v + (i % 2 == 0 ? dx : dy))];

    // ------------------------------------------------------------ OT 19.1

    /// <summary>
    /// OT 19.1 sentence 8: because of the loca format the glyph data stand in glyf in glyph order. A font whose glyf holds glyph 2 before
    /// glyph 1 before glyph 0 (descending loca) is subset: the loca of the subset rises with the glyph number, and every glyph reads as before.
    /// </summary>
    [Fact]
    public void Ot20_Subset_OfDescendingLoca_StoresGlyphsInGlyphOrder()
    {
        byte[][] glyphs = [Ot20Simple([Ot20Rect(0, 0, 500, 700)]), Ot20Simple([Ot20Rect(10, 20, 300, 400)]), Ot20Simple([Ot20Rect(50, 0, 250, 600), Ot20Rect(60, 10, 240, 590)])];
        var descending = Ot20Font(glyphs, storage: [2, 1, 0]);
        var original = OpenTypeFont.Parse(descending);
        var subset = TrueTypeSubsetter.Subset(descending, [1, 2]);
        var loca = Ot20Table(subset, "loca");
        var offsets = Enumerable.Range(0, loca.Length / 4).Select(i => BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(i * 4))).ToArray();
        Assert.Equal(offsets.Order(), offsets);
        var read = OpenTypeFont.Parse(subset);
        for (var g = 0; g < 3; g++) Ot20SameOutline(original.Outline(g), read.Outline(g));
        Assert.False(read.Outline(2).IsEmpty);
    }

    // ------------------------------------------------------------ OT 19.2

    /// <summary>
    /// OT 19.2 sentence 5 and OT 19.4 sentences 1–2: a negative numberOfContours means a composite glyph, −1 is only the recommended value. A
    /// composite with −2 and one component offset by (100, 0) reads like the same with −1 — the component's outline moved by 100 — and the
    /// subset keeps the component.
    /// </summary>
    [Fact]
    public void Ot20_CompositeWithMinusTwoContours_ReadLikeMinusOne()
    {
        var part = new Ot20Part(Ot20Xy, 1, 100, 0);
        var data = Ot20Font([Ot20Box, Ot20Box, Ot20Composite([part], contours: -2), Ot20Composite([part], contours: -1)]);
        var font = OpenTypeFont.Parse(data);
        Assert.Equal(Ot20Moved(font.Outline(1), 100, 0), font.Outline(2).Points);
        Ot20SameOutline(font.Outline(3), font.Outline(2));
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(data, [2]));
        Ot20SameOutline(font.Outline(1), subset.Outline(1));
        Ot20SameOutline(font.Outline(2), subset.Outline(2));
    }

    /// <summary>
    /// OT 19.2 sentences 6–13: xMin, yMin, xMax, yMax describe the glyph's rectangle, which need not be tight. A simple glyph whose header
    /// rectangle is (0, 0, 0, 0) and whose points run from (100, 0) to (600, 700): the outline's bounds come from the points; the subset keeps
    /// the eight bytes of the rectangle as they are.
    /// </summary>
    [Fact]
    public void Ot20_HeaderRectangle_NotUsedForTheOutline_KeptBySubset()
    {
        var glyph = Ot20Simple([Ot20Rect(100, 0, 600, 700)], box: (0, 0, 0, 0));
        var data = Ot20Font([Ot20Box, glyph]);
        Assert.Equal(new RectD(100, 0, 600, 700), OpenTypeFont.Parse(data).Outline(1).Bounds);
        Assert.Equal(glyph[2..10], Ot20GlyphBytes(TrueTypeSubsetter.Subset(data, [1]), 1)[2..10]);
    }

    /// <summary>
    /// OT 19.2 sentences 14–18: xMin equal to the left side bearing is recommended, and head flag bit 1 says all glyphs are so. Rebuilding Liberation
    /// Sans (the PDF/A copy) keeps the side bearing of every glyph it keeps and the head flags.
    /// </summary>
    [Fact]
    public void Ot20_Rebuild_KeepsSideBearingsAndHeadFlags()
    {
        var lib = ShaperLimitTests.Liberation;
        var kept = "AVafi".Select(Ot20G).ToArray();
        var rebuilt = TrueTypeSubsetter.Rebuild(lib, kept.ToDictionary(g => g, g => (int)Ot20Lib.AdvanceWidth(g)), [], null);
        static short Bearing(byte[] font, int glyph)
        {
            var hmtx = Ot20Table(font, "hmtx");
            int full = BinaryPrimitives.ReadUInt16BigEndian(Ot20Table(font, "hhea").AsSpan(34));
            var at = glyph < full ? glyph * 4 + 2 : full * 4 + (glyph - full) * 2;
            return BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(at));
        }
        foreach (var g in kept) Assert.Equal(Bearing(lib, g), Bearing(rebuilt, g));
        Assert.Equal(Ot20Table(lib, "head")[16..18], Ot20Table(rebuilt, "head")[16..18]);
    }

    /// <summary>
    /// OT 19.2 sentences 22–23 and OT 19.3 sentences 10–12: the phantom points of the rasterizer and the instructions (instructionLength,
    /// then the bytecode) belong to hinting. The same glyph once without instructions and once with four instruction bytes (bytecode that
    /// would move a phantom point) reads as the same outline.
    /// </summary>
    [Fact]
    public void Ot20_Instructions_DoNotChangeTheOutline()
    {
        (int, int, bool)[][] contour = [[(0, 0, true), (300, 0, true), (150, 400, false)]];
        // PUSHB[1] 5 (a phantom point), MDAP[0]: rounds and touches it
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Simple(contour), Ot20Simple(contour, instructions: [0xB0, 0x05, 0x2E, 0x00])]));
        Assert.False(font.Outline(1).IsEmpty);
        Ot20SameOutline(font.Outline(1), font.Outline(2));
    }

    /// <summary>
    /// A gvar of one axis: glyph 0 without variation data, every further glyph with one tuple (peak +1.0) moving all its points (the count given,
    /// phantom points included) by (dx, dy), the deltas packed as 16-bit words.
    /// </summary>
    private static byte[] Ot20Gvar(int[] points, int dx, int dy) => Ot20Raw(b =>
    {
        var data = points.Select(count => Ot20Raw(d =>
        {
            var serialized = Ot20Raw(s =>
            {
                s.U8(0);                                            // point numbers: all points
                for (var axis = 0; axis < 2; axis++)
                {
                    s.U8(0x40 | (count - 1));                       // DELTAS_ARE_WORDS, one run of all points
                    for (var p = 0; p < count; p++) s.U16((axis == 0 ? dx : dy) & 0xFFFF);
                }
            });
            d.U16(1).U16(10);                                       // one tuple, its serialized data after the 6-byte header
            d.U16(serialized.Length).U16(0x8000).U16(0x4000);       // variationDataSize, EMBEDDED_PEAK_TUPLE, peak +1.0
            d.Bytes(serialized);
        })).ToArray();
        var glyphs = points.Length + 1;
        var arrayAt = 20 + (glyphs + 1) * 2;
        b.U16(1).U16(0).U16(1).U16(0).U32(arrayAt).U16(glyphs).U16(0).U32(arrayAt);
        var offset = 0;
        b.U16(0);                                                   // glyph 0 from 0 to 0: no variation data
        foreach (var d in data)
        {
            b.U16(offset / 2);
            offset += d.Length;
        }
        b.U16(offset / 2);
        foreach (var d in data) b.Bytes(d);
    });

    /// <summary>
    /// OT 19.2 sentences 24–27, OT 19.3 sentence 62 and OT 19.4 sentences 71, 78–80: variable fonts (fvar, gvar) change points and component
    /// offsets per instance. PDF-Knead draws the default instance: a simple glyph and a composite glyph whose points gvar moves by (40, 30)
    /// read as in the same font without fvar and gvar, without an exception; the subset keeping all tables leaves gvar and the glyph data of
    /// the glyphs kept byte for byte as they were.
    /// </summary>
    [Fact]
    public void Ot20_VariableFont_DrawnAndSubsetInTheDefaultInstance()
    {
        byte[][] glyphs = [Ot20Box, Ot20Simple([Ot20Rect(0, 0, 300, 400)]), Ot20Composite([new Ot20Part(Ot20Xy, 1, 50, 0)])];
        var gvar = Ot20Gvar([4 + 4, 1 + 4], 40, 30);                // the rectangle, the one component, four phantom points each
        var plain = OpenTypeFont.Parse(Ot20Font(glyphs));
        var variableData = Ot20Font(glyphs, more: new Dictionary<string, byte[]> { ["fvar"] = Ot20Fvar(), ["gvar"] = gvar });
        var variable = OpenTypeFont.Parse(variableData);
        Ot20SameOutline(plain.Outline(1), variable.Outline(1));
        Ot20SameOutline(plain.Outline(2), variable.Outline(2));
        var subset = TrueTypeSubsetter.Subset(variableData, [1], keepAllTables: true);
        Assert.Equal(gvar, Ot20Table(subset, "gvar"));
        Assert.Equal(Ot20GlyphBytes(variableData, 1), Ot20GlyphBytes(subset, 1));
    }

    // ------------------------------------------------------------ OT 19.3

    /// <summary>
    /// OT 19.3 sentences 1–3: a glyph of numberOfContours 0 needs nothing after its header but may carry data, such as instructions. Contour
    /// count 0, instructionLength 3 and three bytes: the outline is empty, without an exception; the subset keeps the bytes.
    /// </summary>
    [Fact]
    public void Ot20_GlyphOfNoContours_MayCarryInstructions()
    {
        var glyph = Ot20Raw(b => b.U16(0).U16(0).U16(0).U16(0).U16(0).U16(3).U8(0xB0).U8(0x00).U8(0x2E));
        var data = Ot20Font([Ot20Box, glyph]);
        Assert.True(OpenTypeFont.Parse(data).Outline(1).IsEmpty);
        Assert.Equal(glyph, Ot20GlyphBytes(TrueTypeSubsetter.Subset(data, [1]), 1)[..glyph.Length]);
    }

    /// <summary>
    /// OT 19.3 sentences 4–6: the point numbers run from 0 across all contours; each contour begins one after the last point of the one before.
    /// A glyph with endPtsOfContours 2 and 5 draws two subpaths, the second beginning at point 3; no phantom points appear.
    /// </summary>
    [Fact]
    public void Ot20_TwoContours_SecondBeginsAfterTheLastPointOfTheFirst()
    {
        var glyph = Ot20Simple([[(0, 0, true), (100, 0, true), (50, 80, true)], [(200, 10, true), (300, 10, true), (250, 90, true)]]);
        var outline = OpenTypeFont.Parse(Ot20Font([Ot20Box, glyph])).Outline(1);
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Close,
            PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Close], outline.Verbs);
        Assert.Equal([0f, 0, 100, 0, 50, 80, 0, 0, 200, 10, 300, 10, 250, 90, 200, 10], outline.Points);
    }

    /// <summary>A simple glyph of one contour from raw parts: end point, flags, x and y bytes (header rectangle 0).</summary>
    private static byte[] Ot20RawGlyph(int lastPoint, byte[] flags, byte[] xs, byte[] ys) => Ot20Raw(b =>
        b.U16(1).U16(0).U16(0).U16(0).U16(0).U16(lastPoint).U16(0).Bytes(flags).Bytes(xs).Bytes(ys));

    /// <summary>
    /// OT 19.3 sentences 25–29: one flag, x and y per point logically, the point count from the last end point plus one, flags packed. A glyph
    /// with endPtsOfContours 3 and the flags 0x09, 0x03 (one flag on the curve, repeated three more times) and 16-bit x and y: four points,
    /// one contour of four lines at the places given.
    /// </summary>
    [Fact]
    public void Ot20_RepeatedFlag_GivesAllPointsOfTheCount()
    {
        static byte[] Words(params int[] v) => [.. v.SelectMany(x => new[] { (byte)(x >> 8), (byte)x })];
        var glyph = Ot20RawGlyph(3, [0x09, 0x03], Words(0, 200, 0, -200), Words(0, 0, 100, 0));
        var outline = OpenTypeFont.Parse(Ot20Font([Ot20Box, glyph])).Outline(1);
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Close], outline.Verbs);
        Assert.Equal([0f, 0, 200, 0, 200, 100, 0, 100, 0, 0], outline.Points);
    }

    /// <summary>
    /// OT 19.3 sentences 30–31 (the size of flags and coordinates follows from the flags), 35–38 and 45–48 (x: short with the sign in 0x10,
    /// the same, a signed word), 39–42 and 49–52 (y likewise with 0x20). Four points: x short +5, short −5, same, word −300 and y short +7,
    /// short −7, same, word +500 — the points (5, 7), (0, 0), (0, 0), (−300, 500). A second glyph stored right after it, without padding,
    /// still reads correctly: no byte was read too many or too few.
    /// </summary>
    [Fact]
    public void Ot20_ShortSameAndWordCoordinates_ReadByTheirFlags()
    {
        // flags: p0 on | x short | x positive | y short | y positive; p1 on | x short | y short (both negative);
        // p2 on | x same | y same; p3 on, both words
        var glyph = Ot20RawGlyph(3, [0x37, 0x07, 0x31, 0x01], [5, 5, 0xFE, 0xD4], [7, 7, 0x01, 0xF4]);
        var next = Ot20Simple([Ot20Rect(10, 20, 30, 40)]);
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, glyph, next], pad: false));
        Assert.Equal([5f, 7, 0, 0, 0, 0, -300, 500, 5, 7], font.Outline(1).Points);
        Assert.Equal(new RectD(10, 20, 30, 40), font.Outline(2).Bounds);
    }

    /// <summary>
    /// OT 19.3 sentences 32–34: flag 0x01 puts the point on the curve, otherwise off it. The contour (0, 0) on, (100, 100) off, (200, 100) off,
    /// (300, 0) on draws two quadratic pieces meeting at the implied midpoint (150, 100), stored as cubic curves.
    /// </summary>
    [Fact]
    public void Ot20_TwoPointsOffTheCurve_ImplyTheirMidpoint()
    {
        var outline = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Simple([[(0, 0, true), (100, 100, false), (200, 100, false), (300, 0, true)]])])).Outline(1);
        Assert.Equal([PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Line, PathVerb.Close], outline.Verbs);
        var p = outline.Points;
        Assert.Equal((150f, 100f), (p[6], p[7]));                                   // end of the first piece
        Assert.Equal(200f / 3, p[2], 3);                                            // its controls from (0, 0) via (100, 100)
        Assert.Equal(200f / 3, p[3], 3);
        Assert.Equal(150 - 100f / 3, p[4], 3);
        Assert.Equal(100f, p[5], 3);
        Assert.Equal((300f, 0f), (p[12], p[13]));                                   // end of the second piece
    }

    /// <summary>
    /// OT 19.3 sentences 43–44: with 0x08 the next byte (unsigned) tells how many more times the flag applies. Flag 0x09 with 255 repeats
    /// for 256 points: 256 points on the curve, and the byte after the repeat count is already the first x coordinate.
    /// </summary>
    [Fact]
    public void Ot20_Repeat255_Gives256Points_AndCoordinatesFollow()
    {
        // all x and y as words: x rises by 1 per point, y stays 0 except the first point at y 10
        var xs = Enumerable.Range(0, 256).SelectMany(i => new byte[] { 0, 1 }).ToArray();
        var ys = new byte[512];
        ys[1] = 10;
        var outline = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20RawGlyph(255, [0x09, 0xFF], xs, ys)])).Outline(1);
        Assert.Equal(257, outline.Verbs.Count(v => v is PathVerb.Line or PathVerb.Move));   // 256 points, closed by a line back to the first
        Assert.Equal((1f, 10f), (outline.Points[0], outline.Points[1]));
        Assert.Equal((256f, 10f), (outline.Points[510], outline.Points[511]));
    }

    /// <summary>
    /// OT 19.3 sentences 53–56 and 58–61 (and OT 19.4 sentences 58–61): OVERLAP_SIMPLE (0x40 in the first flag) and OVERLAP_COMPOUND (0x0400
    /// on the first component) are not needed. Two overlapping squares of the same direction, with and without the flag, and two overlapping
    /// components, with and without 0x0400, give the same outline; drawn on a page the overlap is filled (not left out as with even-odd).
    /// </summary>
    [Fact]
    public void Ot20_OverlapFlags_SameOutline_OverlapFilled()
    {
        var a = Ot20Rect(0, 0, 600, 600);
        var b = Ot20Rect(300, 300, 900, 900);
        var square = Ot20Simple([a]);
        byte[][] glyphs =
        [
            Ot20Box, Ot20Simple([a, b]), Ot20Simple([a, b], firstFlagExtra: 0x40), square,
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0400, 3, 0, 0), new Ot20Part(Ot20Xy, 3, 300, 300)]),
            Ot20Composite([new Ot20Part(Ot20Xy, 3, 0, 0), new Ot20Part(Ot20Xy, 3, 300, 300)]),
        ];
        var data = Ot20Font(glyphs);
        var font = OpenTypeFont.Parse(data);
        Ot20SameOutline(font.Outline(1), font.Outline(2));
        Ot20SameOutline(font.Outline(5), font.Outline(4));
        var (doc, list) = InterpreterTests.Page("BT /F 50 Tf 20 20 Td <0001> Tj 90 0 Td <0002> Tj -90 90 Td <0004> Tj 90 0 Td <0005> Tj ET",
            pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F", Ot20Type0(pdf, data))))));
        using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
        SKColor Pixel(double x, double y) => bitmap.GetPixel((int)x, bitmap.Height - 1 - (int)y);
        foreach (var (x, y) in new[] { (20.0, 20.0), (110.0, 20.0), (20.0, 110.0), (110.0, 110.0) })
        {
            Assert.True(Pixel(x + 22, y + 22).Red < 100, $"Überlappung bei {x}/{y} nicht gefüllt: {Pixel(x + 22, y + 22)}");
            Assert.True(Pixel(x + 5, y + 5).Red < 100, $"Fläche bei {x}/{y} nicht gefüllt");
            Assert.True(Pixel(x + 5, y + 40).Red > 200, $"außerhalb bei {x}/{y} gefüllt");
        }
    }

    /// <summary>A Type 0 font (Identity-H, CID = glyph) over the TrueType font given.</summary>
    private static PdfDictionary Ot20Type0(TestPdf pdf, byte[] font)
    {
        var file = pdf.Add(TestPdf.FlateStream(font));
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Probe")), ("Flags", PdfInteger.Of(4)),
            ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)),
            ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("ABCDEF+Probe")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())),
                ("Supplement", PdfInteger.Of(0)))),
            ("CIDToGIDMap", TestPdf.N("Identity")), ("DW", PdfInteger.Of(1000)), ("FontDescriptor", descriptor));
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Probe")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid])));
    }

    /// <summary>OT 19.3 sentence 57: bit 7 of the flags is reserved (0). Flags with bit 7 set give the same outline as without.</summary>
    [Fact]
    public void Ot20_ReservedFlagBit7_IsPassedOver()
    {
        (int, int, bool)[][] contour = [Ot20Rect(10, 0, 400, 300)];
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Simple(contour), Ot20Simple(contour, everyFlagExtra: 0x80)]));
        Ot20SameOutline(font.Outline(1), font.Outline(2));
    }

    // ------------------------------------------------------------ OT 19.4

    /// <summary>
    /// OT 19.4 sentences 6–7: composite glyphs may nest. Glyph 3 is glyph 2 moved by (0, 100), glyph 2 is glyph 1 moved by (50, 0): glyph 3
    /// draws glyph 1 moved by (50, 100), and the subset of glyph 3 keeps glyphs 1, 2 and 3.
    /// </summary>
    [Fact]
    public void Ot20_NestedComposite_OffsetsAdd_SubsetKeepsTheChain()
    {
        var data = Ot20Font([Ot20Simple([Ot20Rect(0, 0, 10, 10)]), Ot20Box, Ot20Composite([new Ot20Part(Ot20Xy, 1, 50, 0)]),
            Ot20Composite([new Ot20Part(Ot20Xy, 2, 0, 100)])]);
        var font = OpenTypeFont.Parse(data);
        Assert.Equal(Ot20Moved(font.Outline(1), 50, 100), font.Outline(3).Points);
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(data, [3]));
        foreach (var g in new[] { 1, 2, 3 }) Ot20SameOutline(font.Outline(g), subset.Outline(g));
    }

    /// <summary>
    /// OT 19.4 sentences 9–11: maxComponentDepth gives the nesting depth; no minimum depth must be supported. A chain of ten composite glyphs
    /// above a simple one: no exception; PDF-Knead draws components down to its own limit of depth 8 — the chain of eight levels above the
    /// simple glyph still draws it, the chains of nine and ten levels leave it out.
    /// </summary>
    [Fact]
    public void Ot20_DeeplyNestedComposite_DrawnDownToDepthEight()
    {
        var glyphs = new List<byte[]> { Ot20Box, Ot20Box };
        for (var g = 2; g <= 11; g++) glyphs.Add(Ot20Composite([new Ot20Part(Ot20Xy, g - 1, 0, 0)]));
        var font = OpenTypeFont.Parse(Ot20Font(glyphs));
        Assert.False(font.Outline(9).IsEmpty);         // glyph 1 at depth 8
        Assert.True(font.Outline(10).IsEmpty);         // depth 9
        Assert.True(font.Outline(11).IsEmpty);         // depth 10
    }

    /// <summary>
    /// OT 19.4 sentences 21–22, 74–77 (open question 17: alignment by points): without ARGS_ARE_XY_VALUES the arguments name a point of the
    /// glyph so far and a point of the component, which are to be laid onto each other. Today PDF-Knead does not align: the second component
    /// (its point 0 at (0, 0), to be laid onto point 2 of the first at (500, 0)) stays at the origin instead of moving by (500, 0).
    /// Test of the present behaviour until the question is decided.
    /// </summary>
    [Fact]
    public void Ot20_PointMatching_TodayTheComponentStaysAtTheOrigin()
    {
        var data = Ot20Font([Ot20Box, Ot20Simple([[(0, 0, true), (250, 80, true), (500, 0, true)]]), Ot20Box,
            Ot20Composite([new Ot20Part(Ot20Xy, 1, 0, 0), new Ot20Part(0x0000, 2, 2, 0)])]);
        var font = OpenTypeFont.Parse(data);
        var points = font.Outline(3).Points;
        var second = points.Skip(font.Outline(1).Points.Count).ToArray();
        Assert.Equal(font.Outline(2).Points, second);            // after the norm: moved by (500, 0)
    }

    /// <summary>
    /// OT 19.4 sentences 25–29, 55 and 95–96: WE_HAVE_INSTRUCTIONS (0x0100) puts the length and the instructions of the composite glyph after
    /// its last component; they work on the points after the components' own. A composite with four instruction bytes reads as the same composite
    /// without them, and the subset takes the glyph over with its instructions.
    /// </summary>
    [Fact]
    public void Ot20_CompositeInstructions_SameOutline_KeptBySubset()
    {
        Ot20Part[] parts = [new Ot20Part(Ot20Xy, 1, 30, 40)];
        var withInstructions = Ot20Composite(parts, instructions: [0xB0, 0x01, 0x2E, 0x00]);
        var data = Ot20Font([Ot20Box, Ot20Box, Ot20Composite(parts), withInstructions]);
        var font = OpenTypeFont.Parse(data);
        Assert.False(font.Outline(2).IsEmpty);
        Ot20SameOutline(font.Outline(2), font.Outline(3));
        Assert.Equal(withInstructions, Ot20GlyphBytes(TrueTypeSubsetter.Subset(data, [3]), 3)[..withInstructions.Length]);
    }

    /// <summary>
    /// OT 19.4 sentences 43–46: ARG_1_AND_2_ARE_WORDS (0x0001) gives 16-bit arguments, otherwise 8-bit. One composite with the word offset
    /// (300, −200), one with the byte offset (−5, 7), both over the same component: the outlines are moved by exactly these values, and the
    /// subset finds the second component behind each of them.
    /// </summary>
    [Fact]
    public void Ot20_WordAndByteArguments_ReadByFlag0001()
    {
        var data = Ot20Font([Ot20Box, Ot20Box, Ot20Simple([Ot20Rect(0, 0, 20, 20)]), Ot20Simple([Ot20Rect(0, 0, 30, 30)]),
            Ot20Composite([new Ot20Part(Ot20Xy, 1, 300, -200), new Ot20Part(Ot20Xy, 2, 0, 0)]),
            Ot20Composite([new Ot20Part(0x0002, 1, -5, 7), new Ot20Part(0x0002, 3, 0, 0)])]);
        var font = OpenTypeFont.Parse(data);
        var box = font.Outline(1);
        Assert.Equal(Ot20Moved(box, 300, -200), font.Outline(4).Points.Take(box.Points.Count));
        Assert.Equal(Ot20Moved(box, -5, 7), font.Outline(5).Points.Take(box.Points.Count));
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(data, [4, 5]));
        Assert.False(subset.Outline(2).IsEmpty);
        Assert.False(subset.Outline(3).IsEmpty);
    }

    /// <summary>
    /// OT 19.4 sentences 48–49 and 73: ROUND_XY_TO_GRID (0x0004) rounds the offset to the pixel grid, which belongs to rasterizing. A component
    /// with 0x0006 and the offset (13, 7) is moved by exactly (13, 7) in the outline.
    /// </summary>
    [Fact]
    public void Ot20_RoundXyToGrid_OffsetTakenExactly()
    {
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, Ot20Composite([new Ot20Part(0x0006, 1, 13, 7)])]));
        Assert.Equal(Ot20Moved(font.Outline(1), 13, 7), font.Outline(2).Points);
    }

    /// <summary>
    /// OT 19.4 sentences 50–51, 53–54 and 81–83: WE_HAVE_A_SCALE (0x0008, one F2DOT14), WE_HAVE_AN_X_AND_Y_SCALE (0x0040, two) and
    /// WE_HAVE_A_TWO_BY_TWO (0x0080, four). The component (0, 0)–(100, 50) scaled by 0.5 has the bounds (0, 0)–(50, 25); with x 1.5 and y 0.5
    /// (2.0 lies outside F2DOT14) (0, 0)–(150, 25); the subset finds the next component behind each form.
    /// </summary>
    [Fact]
    public void Ot20_ScaleForms_ReadAndSkippedByTheirFlags()
    {
        var data = Ot20Font([Ot20Box, Ot20Box, Ot20Simple([Ot20Rect(0, 0, 20, 20)]), Ot20Simple([Ot20Rect(0, 0, 30, 30)]),
            Ot20Simple([Ot20Rect(0, 0, 40, 40)]),
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0008, 1, 0, 0, 0x2000), new Ot20Part(Ot20Xy, 2, 0, 0)]),
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0040, 1, 0, 0, 0x6000, 0x2000), new Ot20Part(Ot20Xy, 3, 0, 0)]),
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0080, 1, 0, 0, 0x4000, 0, 0, 0x4000), new Ot20Part(Ot20Xy, 4, 0, 0)])]);
        var font = OpenTypeFont.Parse(data);
        static RectD First(GlyphOutline o)
        {
            var p = o.Points.Take(10).ToArray();
            return new RectD(p.Where((_, i) => i % 2 == 0).Min(), p.Where((_, i) => i % 2 == 1).Min(), p.Where((_, i) => i % 2 == 0).Max(), p.Where((_, i) => i % 2 == 1).Max());
        }
        Assert.Equal(new RectD(0, 0, 50, 25), First(font.Outline(5)));
        Assert.Equal(new RectD(0, 0, 150, 25), First(font.Outline(6)));
        // behind each form the next component is read at its place: glyphs 2, 3 and 4 follow unchanged
        for (var k = 0; k < 3; k++) Assert.Equal(font.Outline(2 + k).Points, font.Outline(5 + k).Points.Skip(10));
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(data, [5, 6, 7]));
        foreach (var g in new[] { 2, 3, 4 }) Assert.False(subset.Outline(g).IsEmpty);
    }

    /// <summary>
    /// OT 19.4 sentences 84–94: the 2×2 form maps x′ = xscale·x + scale10·y and y′ = scale01·x + yscale·y. The component (0, 0)–(100, 50) with
    /// xscale 0, scale01 1, scale10 −1, yscale 0 (a quarter turn) has the bounds (−50, 0)–(0, 100).
    /// </summary>
    [Fact]
    public void Ot20_TwoByTwo_TurnsTheComponent()
    {
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, Ot20Composite([new Ot20Part(Ot20Xy | 0x0080, 1, 0, 0, 0, 0x4000, 0xC000, 0)])]));
        Assert.Equal(new RectD(-50, 0, 0, 100), font.Outline(2).Bounds);
    }

    /// <summary>
    /// OT 19.4 sentences 56–57 and 97–101 (open question 17: USE_MY_METRICS): with 0x0200 the composite takes advance and side bearings of that
    /// component. Today PDF-Knead does not evaluate the bit: a composite whose component (advance 300) carries 0x0200 and whose own hmtx entry
    /// is 500 has the advance 500 (after the norm 300). Test of the present behaviour until the question is decided.
    /// </summary>
    [Fact]
    public void Ot20_UseMyMetrics_TodayTheAdvanceOfTheCompositeItself()
    {
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, Ot20Composite([new Ot20Part(Ot20Xy | 0x0200, 1, 0, 0)])], advances: [600, 300, 500]));
        Assert.Equal(500, font.AdvanceWidth(2));
    }

    /// <summary>
    /// OT 19.4 sentences 62–65, 72 and 102–106: SCALED_COMPONENT_OFFSET (0x0800) scales the offset with the component, UNSCALED_COMPONENT_OFFSET
    /// (0x1000) does not, and without either the unscaled one is recommended. A component scaled by 0.5 with the offset (200, 0): with 0x0800
    /// moved by 100, with 0x1000 and without either by 200.
    /// </summary>
    [Fact]
    public void Ot20_ComponentOffset_ScaledOnlyWith0800()
    {
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box,
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0008 | 0x0800, 1, 200, 0, 0x2000)]),
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0008 | 0x1000, 1, 200, 0, 0x2000)]),
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0008, 1, 200, 0, 0x2000)])]));
        Assert.Equal(100, font.Outline(2).Bounds.Left);
        Assert.Equal(200, font.Outline(3).Bounds.Left);
        Assert.Equal(200, font.Outline(4).Bounds.Left);
    }

    /// <summary>
    /// OT 19.4 sentence 107 (open question 17: both offset flags): with 0x0800 and 0x1000 together the flags are invalid and the rasterizer is
    /// to take its default (unscaled: offset 200). Today PDF-Knead scales (offset 100). Test of the present behaviour until the question is decided.
    /// </summary>
    [Fact]
    public void Ot20_BothOffsetFlags_TodayScaled()
    {
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, Ot20Composite([new Ot20Part(Ot20Xy | 0x0008 | 0x1800, 1, 200, 0, 0x2000)])]));
        Assert.Equal(100, font.Outline(2).Bounds.Left);          // after the norm: 200
    }

    /// <summary>OT 19.4 sentence 66: the component flag bits 4, 13, 14 and 15 are reserved (0). A component with bits 4 and 15 set reads as without.</summary>
    [Fact]
    public void Ot20_ReservedComponentFlags_ArePassedOver()
    {
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, Ot20Composite([new Ot20Part(Ot20Xy, 1, 20, 30)]),
            Ot20Composite([new Ot20Part(Ot20Xy | 0x0010 | 0x8000, 1, 20, 30)])]));
        Ot20SameOutline(font.Outline(2), font.Outline(3));
    }

    // ------------------------------------------------------------ OT 20.1

    /// <summary>
    /// OT 20.1 sentences 23–24: device tables correct positions per size and resolution; PDF-Knead places glyphs device-independently. A
    /// single positioning (format 2) with ValueFormat 0x0044 (XAdvance and the offset of its Device table, a correction of +7 at 10–12 ppem):
    /// A advances by exactly +50, and the record of the next glyph (B, +70) is read at its place.
    /// </summary>
    [Fact]
    public void Ot20_DeviceTableOfAnAdvance_IsPassedOver()
    {
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(1, b =>
        {
            var start = b.Here;
            string coverage = b.NewLabel(), device = b.NewLabel();
            b.U16(2).Off16(coverage, start).U16(0x0044).U16(2).U16(50).Off16(device, start).U16(70).Off16(device, start);
            b.Label(coverage);
            Ot20Coverage(b, [Ot20G('A'), Ot20G('B')]);
            b.Label(device).Bytes(Ot20Device());
        })));
        var shaped = shaper.Shape("AB");
        Assert.Equal(Ot20Adv('A') + 50, shaped[0].XAdvance);
        Assert.Equal(Ot20Adv('B') + 70, shaped[1].XAdvance);
    }

    /// <summary>
    /// OT 20.1 sentences 28–31: the meaning of the advance follows the writing direction — right to left, the next glyph stands to the left.
    /// An Arabic word typed as formatted text (<see cref="RichLayout"/>, set with Noto Sans Arabic; the appearance places the line in visual
    /// order) and saved: the glyph of the first character in logical order stands rightmost, and each following one ends where the one before
    /// it begins — further left by its own advance.
    /// </summary>
    [Fact]
    public void Ot20_RightToLeftWord_NextGlyphStandsLeftByItsAdvance()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
            var element = new RichTextElement
            {
                Rect = new PdfRectangle(72, 600, 400, 700),
                Content = new RichText("سلام", new RichStyle { Family = "Liberation Sans", Size = 20 }),
            };
            using var output = new MemoryStream();
            PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], output);
            using var saved = PdfDocument.Open(output.ToArray());
            var glyphs = PageInterpreter.Build(saved, saved.Pages[0]).Text.Where(t => t.Text.Length == 1 && "سلام".Contains(t.Text)).ToArray();
            var logical = "سلام".Select(c => Assert.Single(glyphs, g => g.Text == c.ToString())).ToArray();
            Assert.Equal(glyphs.Max(g => g.X0), logical[0].X0);
            for (var k = 1; k < logical.Length; k++)
            {
                Assert.True(logical[k].X0 < logical[k - 1].X0, $"Zeichen {k} steht nicht links von Zeichen {k - 1}");
                Assert.Equal(logical[k - 1].X0, logical[k].X1, 1);
            }
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// OT 20.1 sentence 50: mark-to-ligature attachment. Noto Sans Arabic, lam-alef with a fatha: the mark is attached to what lam and alef
    /// became (this font forms lam-alef from two joining glyphs; the fatha on the alef is attached by its anchor) — its base before it, no
    /// advance, raised.
    /// </summary>
    [Fact]
    public void Ot20_LamAlefWithFatha_MarkAttached()
    {
        var shaped = TextShaper.For(EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf")).Shape("لاَ");
        var fatha = Assert.Single(shaped, g => g.Text == "َ");
        Assert.True(fatha.AttachBack >= 1);
        Assert.Equal(0, fatha.XAdvance);
        Assert.NotEqual(0, fatha.YOffset);
    }

    /// <summary>
    /// OT 20.1 sentence 52: mark-to-mark attachment (a tone mark over a vowel mark). Liberation Sans with „a“, U+0302 and U+0301: the second
    /// mark is attached to the first (its base one glyph back) and stands above it.
    /// </summary>
    [Fact]
    public void Ot20_SecondMark_AttachedToTheFirst()
    {
        var shaped = TextShaper.For(Ot20Lib).Shape("ấ");
        Assert.Equal(1, shaped[1].AttachBack);
        Assert.Equal(1, shaped[2].AttachBack);
        Assert.True(shaped[2].YOffset > shaped[1].YOffset, $"{shaped[2].YOffset} über {shaped[1].YOffset}");
    }

    /// <summary>
    /// OT 20.1 sentences 58–60 and OT 20.4.10 sentences 1–4: the extension type (9) reaches subtables by 32-bit offsets and positions nothing
    /// itself. A GPOS whose pair lookup (A–V −80) is packed into an extension gives the same advances as the same lookup unpacked.
    /// </summary>
    [Fact]
    public void Ot20_PairLookupInAnExtension_SameAdvances()
    {
        var pair = Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []));
        var direct = Ot20Shaper(Ot20Kern(Ot20Lookup(2, pair))).Shape("AVA");
        var packed = Ot20Shaper(Ot20Kern(Ot20Lookup(9, Ot20Extension(2, pair)))).Shape("AVA");
        Assert.Equal(Ot20Adv('A') - 80, direct[0].XAdvance);
        Assert.Equal(direct.Select(g => g.XAdvance), packed.Select(g => g.XAdvance));
    }

    // ------------------------------------------------------------ OT 20.2

    /// <summary>A GDEF of version 1.3 (no classes) with an ItemVariationStore of one region (axis 0, peak +1.0) and one delta set of +50.</summary>
    private static byte[] Ot20GdefWithVariations() => Ot20Raw(b =>
    {
        string store = b.NewLabel(), regions = b.NewLabel(), data = b.NewLabel();
        b.U16(1).U16(3).U16(0).U16(0).U16(0).U16(0).U16(0).Off32(store, 0);
        b.Label(store);
        var at = b.Here;
        b.U16(1).Off32(regions, at).U16(1).Off32(data, at);
        b.Label(regions).U16(1).U16(1).U16(0).U16(0x4000).U16(0x4000);
        b.Label(data).U16(1).U16(0).U16(1).U16(0).U8(50).U8(0);
    });

    /// <summary>
    /// OT 20.2 sentences 4–9 and 14–22 and OT 20.4.11.1 sentences 22–25: in variable fonts the GPOS values belong to the default instance and are
    /// adjusted through VariationIndex tables into the ItemVariationStore of GDEF. A variable font (fvar, GDEF 1.3 with a store) whose pair value
    /// −80 carries a VariationIndex table (deltaFormat 0x8000): shaping gives exactly −80, the value of the default instance, without an exception.
    /// </summary>
    [Fact]
    public void Ot20_VariationIndexOfAPairValue_DefaultInstanceValue()
    {
        var gpos = Ot20Kern(Ot20Lookup(2, b =>
        {
            var start = b.Here;
            string coverage = b.NewLabel(), set = b.NewLabel(), variation = b.NewLabel();
            b.U16(1).Off16(coverage, start).U16(0x0044).U16(0).U16(1).Off16(set, start);
            b.Label(set);
            var setAt = b.Here;
            b.U16(1).U16(Ot20G('V')).U16(-80 & 0xFFFF).Off16(variation, setAt);
            b.Label(variation).Bytes(Ot20VariationIndex());
            b.Label(coverage);
            Ot20Coverage(b, [Ot20G('A')]);
        }));
        var shaped = Ot20Shaper(gpos, Ot20GdefWithVariations(), more: new Dictionary<string, byte[]?> { ["fvar"] = Ot20Fvar() }).Shape("AV");
        Assert.Equal(Ot20Adv('A') - 80, shaped[0].XAdvance);
    }

    /// <summary>
    /// OT 20.2 sentences 10–13 and OT 20.4.11.2 sentences 15–19, 21–26: an anchor of format 2 adds a contour point, which only hinting moves;
    /// the coordinates hold. Mark-to-base with the base anchor in format 2 (300, 700, contour point 0 of „a“, which lies elsewhere): the mark
    /// is placed at 300/700.
    /// </summary>
    [Fact]
    public void Ot20_AnchorFormat2_CoordinatesTaken()
    {
        var acute = Ot20G('́');
        var anchor2 = Ot20Raw(b => b.U16(2).U16(300).U16(700).U16(0));
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(4, Ot20MarkAttach([(acute, 0, Ot20Anchor(0, 0))], [(Ot20G('a'), [anchor2])], 1))),
            Ot20Gdef(new Dictionary<int, int> { [acute] = 3 }));
        var shaped = shaper.Shape("á");
        Assert.Equal(300 - Ot20Adv('a'), shaped[1].XOffset);
        Assert.Equal(700, shaped[1].YOffset);
        Assert.NotEqual((300f, 700f), (Ot20Lib.Outline(Ot20G('a')).Points[0], Ot20Lib.Outline(Ot20G('a')).Points[1]));
    }

    /// <summary>
    /// OT 20.2 sentence 23: programs without variations pass VariationIndex tables over. A single positioning (format 1) with ValueFormat
    /// 0x0044 (XAdvance +30 and a VariationIndex table): A advances by exactly +30.
    /// </summary>
    [Fact]
    public void Ot20_VariationIndexOfAnAdvance_IsPassedOver()
    {
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(1, b =>
        {
            var start = b.Here;
            string coverage = b.NewLabel(), variation = b.NewLabel();
            b.U16(1).Off16(coverage, start).U16(0x0044).U16(30).Off16(variation, start);
            b.Label(variation).Bytes(Ot20VariationIndex());
            b.Label(coverage);
            Ot20Coverage(b, [Ot20G('A')]);
        })));
        Assert.Equal(Ot20Adv('A') + 30, shaper.Shape("A")[0].XAdvance);
    }

    // ------------------------------------------------------------ OT 20.3

    /// <summary>
    /// A FeatureVariations table of one record: a condition on axis 0 from <paramref name="min"/> to <paramref name="max"/> (F2DOT14) and the
    /// substitution of feature 0 by a feature naming <paramref name="lookups"/>.
    /// </summary>
    internal static Action<Ot20Bytes> Ot20FeatureVariations(int min, int max, params int[] lookups) => b =>
    {
        var start = b.Here;
        string set = b.NewLabel(), substitution = b.NewLabel(), condition = b.NewLabel(), alternate = b.NewLabel();
        b.U16(1).U16(0).U32(1).Off32(set, start).Off32(substitution, start);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1).Off32(condition, setAt);
        b.Label(condition).U16(1).U16(0).U16(min & 0xFFFF).U16(max & 0xFFFF);
        b.Label(substitution);
        var subAt = b.Here;
        b.U16(1).U16(0).U16(1).U16(0).Off32(alternate, subAt);
        b.Label(alternate).U16(0).U16(lookups.Length);
        foreach (var l in lookups) b.U16(l);
    };

    /// <summary>
    /// OT 20.3 sentences 5–6, 14, 17: FeatureVariations can set other lookups for a feature under conditions on the axes, and where they are
    /// there, the conditions are to be evaluated. A variable font with GPOS 1.1 whose condition (axis 0 from −1 to +1) includes the default
    /// instance and puts lookup 1 (A +50) in place of lookup 0 (A +10) for „kern“: at the default instance the substitute applies — A +50.
    /// </summary>
    [Fact(Skip = "M7-Befund: FeatureVariations werden nicht ausgewertet, auch nicht, wenn die Bedingung die Standardinstanz einschließt")]
    public void Ot20_FeatureVariationsIncludingTheDefaultInstance_SubstituteApplies()
    {
        var gpos = Ot20Layout([("kern", [0])], [Ot20Lookup(1, Ot20Single1([Ot20G('A')], 4, 10)), Ot20Lookup(1, Ot20Single1([Ot20G('A')], 4, 50))],
            Ot20FeatureVariations(-0x4000, 0x4000, 1));
        var shaped = Ot20Shaper(gpos, more: new Dictionary<string, byte[]?> { ["fvar"] = Ot20Fvar() }).Shape("A");
        Assert.Equal(Ot20Adv('A') + 50, shaped[0].XAdvance);
    }

    /// <summary>
    /// OT 20.3 sentence 21 and OT 20.4.10 sentences 11–12: all subtables of a lookup have one type, and an extension does not name type 9. A
    /// lookup of type 9 with two extensions naming types 2 and 1 reads as type 2 with both subtables (each read as pair positioning), without
    /// an exception; an extension naming type 9 has no effect and does not loop.
    /// </summary>
    [Fact]
    public void Ot20_ExtensionsOfMixedTypesOrOfTypeNine_ReadWithoutHarm()
    {
        var pair = Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []));
        var mixed = Ot20Kern(Ot20Lookup(9, Ot20Extension(2, pair), Ot20Extension(1, Ot20Single1([Ot20G('A')], 4, 30))));
        var lookup = new LayoutTable(mixed, 9).Lookup(0);
        Assert.Equal(2, lookup.Type);
        Assert.Equal(2, lookup.Subtables.Length);
        Assert.Equal(Ot20Adv('A') - 80, Ot20Shaper(mixed).Shape("AV")[0].XAdvance);
        var nine = Ot20Kern(Ot20Lookup(9, Ot20Extension(9, Ot20Extension(2, pair))));
        Assert.Equal(Ot20Adv('A'), Ot20Shaper(nine).Shape("AV")[0].XAdvance);
    }

    /// <summary>
    /// OT 20.3 sentences 30–31 (open question 11: GPOS 7 and 8): types 7 and 8 are contextual and chained contextual positioning. A lookup of
    /// type 7 (format 3, coverages {T} {A} {V}) applying the pair A–V −80 only after T: today „TAV“ is set as without it (after the norm A
    /// advances 80 less), without an exception. Test of the present behaviour until the question is decided.
    /// </summary>
    [Fact]
    public void Ot20_ContextualPairAfterAGlyph_TodayWithoutEffect()
    {
        var (context, without, direct) = Ot22Contextual(7, Ot22Context3([[Ot20G('T')], [Ot20G('A')], [Ot20G('V')]], (1, 1)), "TAV", 1,
            Ot20Lookup(2, Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []))));
        Ot22SamePositions(without, context);
        Assert.Equal(Ot20Adv('A') - 80, direct[1].XAdvance);
    }

    /// <summary>
    /// OT 20.3 sentences 41–45: several operations on one glyph need a lookup each, applied in LookupList order on the result of the one before;
    /// adjustments add up, an attachment by anchors can replace an earlier one. Two single positionings (XAdvance +10, then +5) give +15; a
    /// mark first moved by 7 (single positioning) and then attached (mark-to-base) takes its offset from the anchors alone.
    /// </summary>
    [Fact]
    public void Ot20_LookupsInOrder_AdjustmentsAdd_AttachmentReplaces()
    {
        var a = Ot20G('A');
        var twice = Ot20Layout([("kern", [0, 1])], [Ot20Lookup(1, Ot20Single1([a], 4, 10)), Ot20Lookup(1, Ot20Single1([a], 4, 5))]);
        Assert.Equal(Ot20Adv('A') + 15, Ot20Shaper(twice).Shape("A")[0].XAdvance);
        var acute = Ot20G('́');
        var marked = Ot20Layout([("kern", [0, 1])], [Ot20Lookup(1, Ot20Single1([acute], 1, 7)),
            Ot20Lookup(4, Ot20MarkAttach([(acute, 0, Ot20Anchor(0, 0))], [(Ot20G('a'), [Ot20Anchor(500, 600)])], 1))]);
        var shaped = Ot20Shaper(marked, Ot20Gdef(new Dictionary<int, int> { [acute] = 3 })).Shape("á");
        Assert.Equal(500 - Ot20Adv('a'), shaped[1].XOffset);
        Assert.Equal(600, shaped[1].YOffset);
    }
}
