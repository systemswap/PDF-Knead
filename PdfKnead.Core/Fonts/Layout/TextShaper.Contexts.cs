using System.Collections.Concurrent;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Parts of the shaper shared by GSUB and GPOS or added with docs/FEHLERGEDAECHTNIS.md, No. 1161: the context subtables of both tables
/// (formats 1–3 of SequenceContext and ChainedSequenceContext, OpenType 2.8), the components of ligatures that marks belong to, and caret
/// positions inside ligatures from GDEF.
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>
    /// Matches a contextual (GSUB 5, GPOS 7) or chained contextual (GSUB 6, GPOS 8) subtable at position <paramref name="i"/>: the three formats
    /// both tables share. On a match: the input positions (the glyphs the lookup skips passed over) and where the SequenceLookupRecords start
    /// and how many there are. Before, only GSUB used these formats and GPOS 7 and 8 were passed over (No. 1161).
    /// </summary>
    private bool MatchContextSubtable(TableData t, Work work, List<ShapedGlyph> run, Lookup lookup, int sub, int i, bool chained,
        out int[] positions, out int recordsAt, out int recordCount)
    {
        positions = [];
        recordsAt = recordCount = 0;
        var glyph = run[i].Glyph;
        var format = t.U16(sub);
        if (!chained)
        {
            switch (format)
            {
                case 1:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    // a NULL rule set or rule is none (Common Table Formats 2.8.2), a rule of no glyphs never matches (it holds the
                    // first one) — before, a NULL offset read the subtable itself as rules, and a count of 0 matched (No. 1144)
                    if (c < 0 || c >= t.U16(sub + 4) || t.U16(sub + 6 + c * 2) == 0) return false;
                    var set = sub + t.U16(sub + 6 + c * 2);
                    for (var r = 0; r < t.U16(set); r++)
                    {
                        if (t.U16(set + 2 + r * 2) == 0) continue;
                        var rule = set + t.U16(set + 2 + r * 2);
                        int count = t.U16(rule), subs = t.U16(rule + 2);
                        if (count == 0) continue;
                        var tt = t;
                        if (MatchContext(work, run, lookup, i, 0, None, count, (k, g) => g == tt.U16(rule + 4 + (k - 1) * 2), 0, None, out var pos))
                            return Found(pos, rule + 4 + (count - 1) * 2, subs, out positions, out recordsAt, out recordCount);
                    }
                    return false;
                }
                case 2:
                {
                    if (t.Coverage(sub + t.U16(sub + 2), glyph) < 0) return false;
                    var classDef = ClassDef(t, sub, sub + 4);
                    var cls = t.Class(classDef, glyph);
                    if (cls >= t.U16(sub + 6) || t.U16(sub + 8 + cls * 2) == 0) return false;
                    var set = sub + t.U16(sub + 8 + cls * 2);
                    for (var r = 0; r < t.U16(set); r++)
                    {
                        if (t.U16(set + 2 + r * 2) == 0) continue;
                        var rule = set + t.U16(set + 2 + r * 2);
                        int count = t.U16(rule), subs = t.U16(rule + 2);
                        if (count == 0) continue;
                        var tt = t;
                        if (MatchContext(work, run, lookup, i, 0, None, count, (k, g) => tt.Class(classDef, g) == tt.U16(rule + 4 + (k - 1) * 2), 0, None, out var pos))
                            return Found(pos, rule + 4 + (count - 1) * 2, subs, out positions, out recordsAt, out recordCount);
                    }
                    return false;
                }
                case 3:
                {
                    int count = t.U16(sub + 2), subs = t.U16(sub + 4);
                    var tt = t;
                    if (count == 0 || t.Coverage(sub + t.U16(sub + 6), glyph) < 0) return false;
                    if (!MatchContext(work, run, lookup, i, 0, None, count, (k, g) => tt.Coverage(sub + tt.U16(sub + 6 + k * 2), g) >= 0, 0, None, out var pos))
                        return false;
                    return Found(pos, sub + 6 + count * 2, subs, out positions, out recordsAt, out recordCount);
                }
                default:
                    return false;
            }
        }

        switch (format)
        {
            case 1:
            {
                var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                if (c < 0 || c >= t.U16(sub + 4) || t.U16(sub + 6 + c * 2) == 0) return false;
                var set = sub + t.U16(sub + 6 + c * 2);
                for (var r = 0; r < t.U16(set); r++)
                {
                    if (t.U16(set + 2 + r * 2) == 0) continue;
                    var rule = set + t.U16(set + 2 + r * 2);
                    var (bt, inp, la, records, subs) = ChainParts(t, rule);
                    if (t.U16(inp - 2) == 0) continue;
                    var tt = t;
                    if (MatchContext(work, run, lookup, i,
                            tt.U16(rule), (k, g) => g == tt.U16(bt + k * 2),
                            tt.U16(inp - 2), (k, g) => g == tt.U16(inp + (k - 1) * 2),
                            tt.U16(la - 2), (k, g) => g == tt.U16(la + k * 2), out var pos))
                        return Found(pos, records, subs, out positions, out recordsAt, out recordCount);
                }
                return false;
            }
            case 2:
            {
                if (t.Coverage(sub + t.U16(sub + 2), glyph) < 0) return false;
                int backDef = ClassDef(t, sub, sub + 4), inputDef = ClassDef(t, sub, sub + 6), aheadDef = ClassDef(t, sub, sub + 8);
                var cls = t.Class(inputDef, glyph);
                if (cls >= t.U16(sub + 10) || t.U16(sub + 12 + cls * 2) == 0) return false;
                var set = sub + t.U16(sub + 12 + cls * 2);
                for (var r = 0; r < t.U16(set); r++)
                {
                    if (t.U16(set + 2 + r * 2) == 0) continue;
                    var rule = set + t.U16(set + 2 + r * 2);
                    var (bt, inp, la, records, subs) = ChainParts(t, rule);
                    if (t.U16(inp - 2) == 0) continue;
                    var tt = t;
                    if (MatchContext(work, run, lookup, i,
                            tt.U16(rule), (k, g) => tt.Class(backDef, g) == tt.U16(bt + k * 2),
                            tt.U16(inp - 2), (k, g) => tt.Class(inputDef, g) == tt.U16(inp + (k - 1) * 2),
                            tt.U16(la - 2), (k, g) => tt.Class(aheadDef, g) == tt.U16(la + k * 2), out var pos))
                        return Found(pos, records, subs, out positions, out recordsAt, out recordCount);
                }
                return false;
            }
            case 3:
            {
                var btCount = t.U16(sub + 2);
                var inp = sub + 4 + btCount * 2;
                var inCount = t.U16(inp);
                var la = inp + 2 + inCount * 2;
                var laCount = t.U16(la);
                var records = la + 2 + laCount * 2;
                var tt = t;
                if (inCount == 0 || t.Coverage(sub + t.U16(inp + 2), glyph) < 0) return false;
                if (!MatchContext(work, run, lookup, i,
                        btCount, (k, g) => tt.Coverage(sub + tt.U16(sub + 4 + k * 2), g) >= 0,
                        inCount, (k, g) => tt.Coverage(sub + tt.U16(inp + 2 + k * 2), g) >= 0,
                        laCount, (k, g) => tt.Coverage(sub + tt.U16(la + 2 + k * 2), g) >= 0, out var pos))
                    return false;
                return Found(pos, records + 2, t.U16(records), out positions, out recordsAt, out recordCount);
            }
            default:
                return false;
        }
    }

    private static bool Found(int[] matched, int records, int count, out int[] positions, out int recordsAt, out int recordCount)
    {
        positions = matched;
        recordsAt = records;
        recordCount = count;
        return true;
    }

    /// <summary>A ClassDef from its offset at <paramref name="at"/>: NULL means every glyph is of class 0 (Common Table Formats 2.6) — before,
    /// the subtable itself was read as one (No. 1144). -1 for NULL (<see cref="TableData.Class"/> reads class 0 there).</summary>
    private static int ClassDef(TableData t, int sub, int at) => t.U16(at) == 0 ? -1 : sub + t.U16(at);

    /// <summary>Chained rule (formats 1/2): start of the backtrack, input (from the second glyph) and lookahead sequences, and the records.</summary>
    private static (int Backtrack, int Input, int Lookahead, int Records, int Count) ChainParts(TableData t, int rule)
    {
        var bt = rule + 2;
        var inCountAt = bt + t.U16(rule) * 2;
        var inp = inCountAt + 2;
        var laCountAt = inp + Math.Max(0, t.U16(inCountAt) - 1) * 2;
        var la = laCountAt + 2;
        var subsAt = la + t.U16(laCountAt) * 2;
        return (bt, inp, la, subsAt + 2, t.U16(subsAt));
    }

    private static bool None(int k, int g) => false;

    /// <summary>Whether the coverage whose offset (from <paramref name="sub"/>) stands at <paramref name="at"/> holds the glyph; a NULL offset holds none.</summary>
    private static bool Covers(TableData t, int sub, int at, int glyph) => t.U16(at) != 0 && t.Coverage(sub + t.U16(at), glyph) >= 0;

    // ------------------------------------------------------------ components of ligatures (OpenType 20.4.6)

    /// <summary>
    /// Before a ligature replaces the glyphs at <paramref name="positions"/>: notes for each glyph passed over between them (a mark the lookup
    /// skips) the component it belongs to — the one before it — and moves the marks after the ligature that belonged to its last component, when
    /// that was itself a ligature, onto the new numbering. A component that is a ligature brings its own components along. A ligature of a base
    /// and marks only, or of marks only, is none for this purpose: its marks keep their bases, as in common shaping engines. The component a
    /// mark belongs to depends on the text and the substitutions, and a shaper must track it through GSUB (OpenType 20.4.6); before, a mark on
    /// a ligature always took its last component (docs/FEHLERGEDAECHTNIS.md, No. 1161).
    /// </summary>
    /// <returns>The id of the new ligature and its number of components; id 0 when it is none.</returns>
    private (int Id, int Components) TrackComponents(List<ShapedGlyph> run, int[] positions, Work work)
    {
        var restMarks = true;
        for (var k = 1; k < positions.Length && restMarks; k++) restMarks = ClassOf(run[positions[k]]) == 3;
        if (restMarks && ClassOf(run[positions[0]]) is 1 or 3) return (0, 0);
        var id = work.NextLigatureId();
        int soFar = 0, lastId = 0, lastCount = 1;
        for (var k = 0; k < positions.Length; k++)
        {
            var component = run[positions[k]];
            lastId = component.LigatureId;
            lastCount = Math.Max(1, component.LigatureComponents);
            soFar += lastCount;
            var end = k + 1 < positions.Length ? positions[k + 1] : positions[k] + 1;
            for (var q = positions[k] + 1; q < end; q++)
            {
                var g = run[q];
                // a mark that belonged to a component of this component (a ligature itself) keeps that component, others take the last
                var own = g.LigatureComponent > 0 ? Math.Min(g.LigatureComponent, lastCount) : lastCount;
                g.LigatureId = id;
                g.LigatureComponent = soFar - lastCount + own;
                run[q] = g;
            }
        }
        if (lastId > 0)
            for (var q = positions[^1] + 1; q < run.Count && work.Spend(1); q++)
            {
                var g = run[q];
                if (g.LigatureId != lastId || g.LigatureComponent == 0) break;
                g.LigatureId = id;
                g.LigatureComponent = soFar - lastCount + Math.Min(g.LigatureComponent, lastCount);
                run[q] = g;
            }
        return (id, soFar);
    }

    // ------------------------------------------------------------ carets in ligatures (GDEF LigCaretList, OpenType 18.2.4)

    /// <summary>Largest ligature whose carets are read (components); a longer one is divided in equal shares.</summary>
    private const int MaxCaretComponents = 64;

    private readonly ConcurrentDictionary<(int Glyph, int Components), double[]?> _carets = new();

    /// <summary>
    /// The caret positions inside a ligature glyph from the LigCaretList of GDEF (OpenType 18.2.4), for a ligature of
    /// <paramref name="components"/> components: x in font units from the glyph origin, ascending and within its advance. Formats 1 and 3 give
    /// the coordinate (format 3 without its device or variation data: the default instance at no particular resolution), format 2 the
    /// unhinted position of the contour point. Null when the font has no carets for the glyph, another number than one fewer than the
    /// components, or a contour point that cannot be resolved — then the caller divides the ligature in equal shares, as a program without
    /// carets does (18.1). Before, the LigCaretList was not read (docs/FEHLERGEDAECHTNIS.md, No. 1161).
    /// </summary>
    /// <param name="glyph">The ligature glyph.</param>
    /// <param name="components">Its number of components (characters).</param>
    /// <returns>The carets (components − 1 of them), or null.</returns>
    public double[]? LigatureCarets(int glyph, int components)
    {
        if (_gdef is null || components < 2 || components > MaxCaretComponents) return null;
        if (_carets.Count > 4096) _carets.Clear();
        return _carets.GetOrAdd((glyph, components), key => ReadCarets(key.Glyph, key.Components - 1));
    }

    private double[]? ReadCarets(int glyph, int count)
    {
        if (_gdef!.LigatureCarets(glyph, count) is not { } carets) return null;
        var advance = Math.Max(0, _font.AdvanceWidth(glyph));
        var result = new double[count];
        for (var k = 0; k < count; k++)
        {
            var (format, value) = carets[k];
            double x = value;
            if (format == 2)
            {
                if (ContourPoint(glyph, value) is not { } point) return null;
                x = point.X;
            }
            result[k] = Math.Clamp(x, 0, advance);
        }
        Array.Sort(result);
        return result;
    }

    /// <summary>
    /// The unhinted position of contour point <paramref name="point"/> of a TrueType glyph: in a composite glyph the points are numbered through its
    /// components in order, each placed by its offset and transformation as when it is drawn. Null for a CFF font, a point beyond the glyph, a
    /// component placed by matching points, or data beyond the table or the work limit.
    /// </summary>
    private (double X, double Y)? ContourPoint(int glyph, int point)
    {
        if (_font.Cff is not null || point < 0) return null;
        var head = _font.TableData("head");
        if (head.Length < 54) return null;
        var budget = 200_000;
        return GlyphPoint(_font.TableData("glyf"), _font.TableData("loca"), head[51] != 0 || head[50] != 0, glyph, ref point, 0, ref budget);
    }

    private static int U16(ReadOnlySpan<byte> d, int at) => d[at] << 8 | d[at + 1];

    private static double F2Dot14(ReadOnlySpan<byte> d, int at) => (short)U16(d, at) / 16384.0;

    /// <summary>
    /// Finds point <paramref name="point"/> in a glyph; when the glyph has fewer points, returns null and lowers <paramref name="point"/> by their
    /// number. A failure (damaged data, matched points, the budget used up) sets <paramref name="budget"/> below 0.
    /// </summary>
    private static (double X, double Y)? GlyphPoint(ReadOnlySpan<byte> glyf, ReadOnlySpan<byte> loca, bool longLoca, int glyph, ref int point, int depth,
        ref int budget)
    {
        var entries = loca.Length / (longLoca ? 4 : 2);
        if (depth > 8 || --budget < 0 || glyph < 0 || glyph + 1 >= entries)
        {
            budget = -1;
            return null;
        }
        long start = longLoca ? (uint)(U16(loca, glyph * 4) << 16 | U16(loca, glyph * 4 + 2)) : 2L * U16(loca, glyph * 2);
        long end = longLoca ? (uint)(U16(loca, glyph * 4 + 4) << 16 | U16(loca, glyph * 4 + 6)) : 2L * U16(loca, glyph * 2 + 2);
        if (end <= start) return null;                       // no outline, no points
        if (end > glyf.Length || start + 10 > end)
        {
            budget = -1;
            return null;
        }
        var data = glyf[(int)start..(int)end];
        var contours = (short)U16(data, 0);
        if (contours >= 0) return SimplePoint(data, contours, ref point, ref budget);
        var at = 10;
        while (true)
        {
            if (at + 4 > data.Length) return Fail(ref budget);
            int flags = U16(data, at), component = U16(data, at + 2);
            at += 4;
            int dx, dy;
            if ((flags & 1) != 0)
            {
                if (at + 4 > data.Length) return Fail(ref budget);
                (dx, dy) = ((short)U16(data, at), (short)U16(data, at + 2));
                at += 4;
            }
            else
            {
                if (at + 2 > data.Length) return Fail(ref budget);
                (dx, dy) = ((sbyte)data[at], (sbyte)data[at + 1]);
                at += 2;
            }
            if ((flags & 2) == 0) return Fail(ref budget);   // placed by matching points
            double a = 1, b = 0, c = 0, d = 1;
            var size = (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
            if (at + size > data.Length) return Fail(ref budget);
            if (size == 2) a = d = F2Dot14(data, at);
            else if (size == 4) (a, d) = (F2Dot14(data, at), F2Dot14(data, at + 2));
            else if (size == 8) (a, b, c, d) = (F2Dot14(data, at), F2Dot14(data, at + 2), F2Dot14(data, at + 4), F2Dot14(data, at + 6));
            at += size;
            var found = GlyphPoint(glyf, loca, longLoca, component, ref point, depth + 1, ref budget);
            if (budget < 0) return null;
            if (found is { } p)
            {
                // as the outline is drawn (OpenTypeFont.CompositeGlyph): SCALED_COMPONENT_OFFSET transforms the offset too
                double ox = dx, oy = dy;
                if ((flags & 0x800) != 0) (ox, oy) = (a * dx + c * dy, b * dx + d * dy);
                return (a * p.X + c * p.Y + ox, b * p.X + d * p.Y + oy);
            }
            if ((flags & 0x20) == 0) return null;            // the last component: the point lies beyond this glyph
        }
    }

    private static (double X, double Y)? Fail(ref int budget)
    {
        budget = -1;
        return null;
    }

    /// <summary>Point <paramref name="point"/> of a simple glyph (coordinates summed from its deltas), or null with the point lowered by its number of points.</summary>
    private static (double X, double Y)? SimplePoint(ReadOnlySpan<byte> data, int contours, ref int point, ref int budget)
    {
        if (contours == 0) return null;
        var at = 10 + contours * 2;
        if (at + 2 > data.Length) return Fail(ref budget);
        var count = U16(data, at - 2) + 1;
        if (point >= count)
        {
            point -= count;
            return null;
        }
        if ((budget -= count) < 0) return Fail(ref budget);
        at += 2 + U16(data, at);
        var flags = new byte[count];
        for (var i = 0; i < count;)
        {
            if (at >= data.Length) return Fail(ref budget);
            var f = data[at++];
            flags[i++] = f;
            if ((f & 8) == 0) continue;
            if (at >= data.Length) return Fail(ref budget);
            for (var repeat = data[at++]; repeat > 0 && i < count; repeat--) flags[i++] = f;
        }
        int x = 0, y = 0, px = 0;
        for (var i = 0; i < count; i++)
        {
            var f = flags[i];
            if ((f & 2) != 0)
            {
                if (at >= data.Length) return Fail(ref budget);
                x += (f & 0x10) != 0 ? data[at] : -data[at];
                at++;
            }
            else if ((f & 0x10) == 0)
            {
                if (at + 2 > data.Length) return Fail(ref budget);
                x += (short)U16(data, at);
                at += 2;
            }
            if (i == point) px = x;
        }
        for (var i = 0; i <= point; i++)
        {
            var f = flags[i];
            if ((f & 4) != 0)
            {
                if (at >= data.Length) return Fail(ref budget);
                y += (f & 0x20) != 0 ? data[at] : -data[at];
                at++;
            }
            else if ((f & 0x20) == 0)
            {
                if (at + 2 > data.Length) return Fail(ref budget);
                y += (short)U16(data, at);
                at += 2;
            }
        }
        return (px, y);
    }
}
