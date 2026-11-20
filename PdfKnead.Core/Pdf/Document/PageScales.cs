using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// One unit of a number format array (ISO 32000-2, 12.9, Table 268): label, conversion factor and how the fractional part is shown.
/// </summary>
/// <param name="Unit">The label (/U), e.g. "m" or "ft".</param>
/// <param name="Factor">The conversion factor (/C): for the first unit from what the referring entry measures, else from the previous unit.</param>
/// <param name="Fraction">/F of the last unit: 'D' decimal, 'F' fraction, 'R' round, 'T' truncate.</param>
/// <param name="Precision">/D: decimal precision (10, 100 …) or denominator of a fraction.</param>
/// <param name="Exact">/FD: keep low-order zeros, do not reduce fractions.</param>
/// <param name="Thousands">/RT: text between orders of thousands.</param>
/// <param name="Decimal">/RD: text at the decimal position.</param>
/// <param name="Before">/PS: text before the label.</param>
/// <param name="After">/SS: text after the label.</param>
/// <param name="Prefix">/O /P: the label before the value.</param>
public sealed record NumberFormat(string Unit, double Factor, char Fraction = 'D', int Precision = 100, bool Exact = false, string Thousands = ",",
    string Decimal = ".", string Before = " ", string After = " ", bool Prefix = false)
{
    /// <summary>A number format dictionary; null without /U or a usable /C.</summary>
    public static NumberFormat? Read(PdfDictionary? dict)
    {
        // a factor of 0, below 0 or not finite measures nothing sensible (a crafted /C 1e308 gave „In,fin,ity.NaN m“; No. 596)
        if (dict?.Get(PdfName.Get("U")) is not PdfString unit || dict.GetNumber(PdfName.Get("C")) is not { } factor || !(factor > 0) || !double.IsFinite(factor)
            || factor > 1e12 || factor < 1e-12) return null;
        var fraction = dict.GetName(PdfName.Get("F"))?.Value switch { "F" => 'F', "R" => 'R', "T" => 'T', _ => 'D' };
        var precision = (int)Math.Clamp(dict.GetNumber(PdfName.Get("D")) ?? (fraction == 'F' ? 16 : 100), 1, 1_000_000_000);
        string Text(string key, string fallback) => dict.Get(PdfName.Get(key)) is PdfString s ? s.ToText() : fallback;
        return new NumberFormat(unit.ToText(), factor, fraction, precision, dict.GetBoolean(PdfName.Get("FD")) ?? false,
            Text("RT", ","), Text("RD", "") is { Length: > 0 } rd ? rd : ".", Text("PS", " "), Text("SS", " "), dict.GetName(PdfName.Get("O"))?.Value == "P");
    }

    /// <summary>As a number format dictionary (Table 268); entries with their default value are left out (Ablaufplan 7.6 f).</summary>
    public PdfDictionary ToDictionary()
    {
        static PdfString Text(string s) => new(PdfTextEncoding.Encode(s));
        var d = new PdfDictionary();
        d.Set(PdfName.Type, PdfName.Get("NumberFormat"));
        d.Set(PdfName.Get("U"), Text(Unit));
        d.Set(PdfName.Get("C"), new PdfReal(Factor));
        if (Fraction != 'D') d.Set(PdfName.Get("F"), PdfName.Get(Fraction.ToString()));
        if (Fraction == 'D' && Precision != 100 || Fraction == 'F' && Precision != 16) d.Set(PdfName.Get("D"), PdfInteger.Of(Precision));
        if (Exact) d.Set(PdfName.Get("FD"), PdfBoolean.Of(true));
        if (Thousands != ",") d.Set(PdfName.Get("RT"), Text(Thousands));
        if (Decimal != ".") d.Set(PdfName.Get("RD"), Text(Decimal));
        if (Before != " ") d.Set(PdfName.Get("PS"), Text(Before));
        if (After != " ") d.Set(PdfName.Get("SS"), Text(After));
        if (Prefix) d.Set(PdfName.Get("O"), PdfName.Get("P"));
        return d;
    }
}

/// <summary>A number format array: units in descending order of granularity (12.9, Table 267/268), with the formatting of 12.9.2.</summary>
/// <param name="Units">The units, the largest first.</param>
public sealed record NumberFormats(IReadOnlyList<NumberFormat> Units)
{
    /// <summary>A number format array; null if it is missing or its first unit is unusable.</summary>
    public static NumberFormats? Read(PdfArray? array)
    {
        if (array is null || array.Count == 0) return null;
        var units = new List<NumberFormat>();
        for (var i = 0; i < Math.Min(array.Count, 8); i++)
        {
            if (NumberFormat.Read(array.Get(i) as PdfDictionary) is not { } unit) break;
            units.Add(unit);
        }
        return units.Count > 0 ? new NumberFormats(units) : null;
    }

    /// <summary>The factor of the first unit (/C), applied to what the referring entry measures.</summary>
    public double Factor => Units[0].Factor;

    /// <summary>As a number format array (Table 267).</summary>
    public PdfArray ToArray() => new(Units.Select(u => (PdfObject)u.ToDictionary()));

    /// <summary>
    /// A measured value as text (ISO 32000-2, 12.9.2): multiplied by the first factor; whole units with their label, the fractional part
    /// carried on to the next unit; the last one decimal, as a fraction, rounded or truncated. Negative values with a leading minus.
    /// </summary>
    /// <param name="value">The value in the units the referring entry measures (for /D: units of /X; for /T: degrees).</param>
    public string Format(double value)
    {
        if (!double.IsFinite(value)) return "";
        var negative = value < 0;
        var rest = Math.Abs(value) * Units[0].Factor;
        if (!double.IsFinite(rest)) return "";
        var text = new StringBuilder();
        if (Units.Count > 1)
        {
            // Several units: rounded first to what the last unit shows, then split from the top — so that rounding carries into the larger units
            // („2 ft 12 in“ was 2.9999 ft; No. 596). The size of each unit in units of the last one:
            var size = new double[Units.Count];
            size[^1] = 1;
            for (var i = Units.Count - 2; i >= 0; i--) size[i] = size[i + 1] * Units[i + 1].Factor;
            var inLast = RoundedLast(rest * size[0], Units[^1]);
            if (!double.IsFinite(inLast)) return "";
            for (var i = 0; i < Units.Count - 1; i++)
            {
                var whole = Math.Floor(inLast / size[i] + 1e-9);
                inLast = Math.Max(0, inLast - whole * size[i]);
                var nothingFollows = inLast < 1e-9 * size[i];
                // the whole part of a larger unit is shown only when it is not zero, or when nothing follows
                if (whole > 0 || nothingFollows) Append(Units[i], Group(whole, Units[i].Thousands));
                if (nothingFollows) break;
                if (i == Units.Count - 2) Append(Units[^1], Last(inLast, Units[^1]));
            }
        }
        else Append(Units[0], Last(rest, Units[0]));
        // the spacing PS/SS of a label is between value and label, not at the ends of the text: with the label in front (/O /P) the text began
        // with a space (FEHLERGEDAECHTNIS 583, found by the recalculation from the standard)
        var result = text.ToString().Trim();
        return negative && result.Length > 0 ? "-" + result : result;

        void Append(NumberFormat unit, string number)
        {
            var label = unit.Before + unit.Unit + unit.After;
            text.Append(unit.Prefix ? label + number : number + label);
        }
    }

    /// <summary>A value in units of the last unit, rounded as it will be shown (decimals, fraction, rounded or truncated).</summary>
    private static double RoundedLast(double value, NumberFormat unit) => unit.Fraction switch
    {
        'R' => Math.Round(value, MidpointRounding.AwayFromZero),
        'T' => Math.Floor(value + 1e-9),
        'F' => Math.Round(value * Math.Max(1, unit.Precision), MidpointRounding.AwayFromZero) / Math.Max(1, unit.Precision),
        _ => Math.Round(value * Math.Max(1, unit.Precision), MidpointRounding.AwayFromZero) / Math.Max(1, unit.Precision),
    };

    private static string Last(double value, NumberFormat unit)
    {
        switch (unit.Fraction)
        {
            case 'R':
                return Group(Math.Round(value, MidpointRounding.AwayFromZero), unit.Thousands);
            case 'T':
                return Group(Math.Floor(value + 1e-9), unit.Thousands);
            case 'F':
            {
                var denominator = unit.Precision;
                var whole = Math.Floor(value);
                var numerator = (long)Math.Round((value - whole) * denominator);
                if (numerator >= denominator)
                {
                    whole++;
                    numerator -= denominator;
                }
                if (numerator == 0) return Group(whole, unit.Thousands);
                long d = denominator;
                if (!unit.Exact)
                {
                    var g = Gcd(numerator, d);
                    numerator /= g;
                    d /= g;
                }
                return (whole > 0 ? Group(whole, unit.Thousands) + " " : "") + numerator.ToString(CultureInfo.InvariantCulture) + "/" + d.ToString(CultureInfo.InvariantCulture);
            }
            default:
            {
                var digits = Math.Max(0, (int)Math.Round(Math.Log10(unit.Precision)));
                var rounded = Math.Round(value, Math.Min(digits, 15), MidpointRounding.AwayFromZero);
                var whole = Math.Floor(rounded);
                var fraction = (rounded - whole).ToString("F" + digits, CultureInfo.InvariantCulture);
                var decimals = digits > 0 ? fraction[(fraction.IndexOf('.') + 1)..] : "";
                if (!unit.Exact) decimals = decimals.TrimEnd('0');
                return Group(whole, unit.Thousands) + (decimals.Length > 0 ? unit.Decimal + decimals : "");
            }
        }

        static long Gcd(long a, long b) => b == 0 ? Math.Max(a, 1) : Gcd(b, a % b);
    }

    private static string Group(double whole, string thousands)
    {
        var digits = whole.ToString("F0", CultureInfo.InvariantCulture);
        if (thousands.Length == 0 || digits.Length <= 3) return digits;
        var text = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) text.Append(thousands);
            text.Append(digits[i]);
        }
        return text.ToString();
    }
}

/// <summary>
/// A rectilinear measure dictionary (ISO 32000-2, 12.9, Tables 266 and 267): the scale of a viewport and the units of its measurements.
/// </summary>
/// <param name="Ratio">/R, the scale ratio as text (e.g. "1 cm = 1 m").</param>
/// <param name="X">Units along x (and y, without <paramref name="Y"/>); the first factor converts from default user space.</param>
/// <param name="Y">Units along y where they differ; null otherwise.</param>
/// <param name="Distance">/D: distances, from units of x.</param>
/// <param name="Area">/A: areas, from units of x squared.</param>
/// <param name="Angle">/T: angles, from degrees; null if not given.</param>
/// <param name="Origin">/O: origin of the measuring coordinate system in default user space; null for the first corner of the viewport.</param>
/// <param name="YToX">/CYX: units of y to units of x; null if not given.</param>
public sealed record RectilinearMeasure(string Ratio, NumberFormats X, NumberFormats? Y, NumberFormats Distance, NumberFormats Area, NumberFormats? Angle,
    (double X, double Y)? Origin, double? YToX)
{
    /// <summary>
    /// As a measure dictionary of subtype RL (Tables 266, 267) — for the /Measure of a dimension (Ablaufplan 7.6 f). /R is required: without a
    /// ratio of its own (the paper) „1:1“.
    /// </summary>
    public PdfDictionary ToDictionary()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Type, PdfName.Get("Measure"));
        d.Set(PdfName.Subtype, PdfName.Get("RL"));
        d.Set(PdfName.Get("R"), new PdfString(PdfTextEncoding.Encode(Ratio.Length > 0 ? Ratio : "1:1")));
        d.Set(PdfName.Get("X"), X.ToArray());
        if (Y is not null) d.Set(PdfName.Get("Y"), Y.ToArray());
        d.Set(PdfName.Get("D"), Distance.ToArray());
        d.Set(PdfName.Get("A"), Area.ToArray());
        if (Angle is not null) d.Set(PdfName.Get("T"), Angle.ToArray());
        if (Origin is { } o) d.Set(PdfName.Get("O"), new PdfArray([new PdfReal(o.X), new PdfReal(o.Y)]));
        if (YToX is { } cyx) d.Set(PdfName.Get("CYX"), new PdfReal(cyx));
        return d;
    }

    /// <summary>A measure dictionary of subtype RL (the default); null for GEO and other subtypes, or without the required entries.</summary>
    public static RectilinearMeasure? Read(PdfDictionary? dict)
    {
        if (dict is null || dict.GetName(PdfName.Subtype)?.Value is { } subtype && subtype != "RL") return null;
        if (NumberFormats.Read(dict.GetArray(PdfName.Get("X"))) is not { } x) return null;
        var distance = NumberFormats.Read(dict.GetArray(PdfName.Get("D"))) ?? new NumberFormats([x.Units[0] with { Factor = 1 }]);
        var area = NumberFormats.Read(dict.GetArray(PdfName.Get("A"))) ?? new NumberFormats([x.Units[0] with { Unit = x.Units[0].Unit + "²", Factor = 1 }]);
        var origin = dict.GetArray(PdfName.Get("O")) is { Count: 2 } o && o.GetNumber(0) is { } ox && o.GetNumber(1) is { } oy ? (ox, oy) : ((double, double)?)null;
        return new RectilinearMeasure(dict.Get(PdfName.Get("R")) is PdfString r ? r.ToText() : "", x, NumberFormats.Read(dict.GetArray(PdfName.Get("Y"))),
            distance, area, NumberFormats.Read(dict.GetArray(PdfName.Get("T"))), origin, dict.GetNumber(PdfName.Get("CYX")));
    }

    /// <summary>
    /// A change in default user space in units of x (12.9, Table 267): x by the first factor of /X, y by that of /Y and /CYX; null where /Y is
    /// given without /CYX (then distances and areas are not defined).
    /// </summary>
    public (double X, double Y)? ToUnits(double dx, double dy)
    {
        if (Y is null) return (dx * X.Factor, dy * X.Factor);
        return YToX is { } cyx && double.IsFinite(cyx) ? (dx * X.Factor, dy * Y.Factor * cyx) : null;
    }

    /// <summary>The length of a polyline in units of x (the sum of its pieces, 12.9.1); null if not defined.</summary>
    public double? Length(IReadOnlyList<(double X, double Y)> points)
    {
        double total = 0;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            if (ToUnits(points[i + 1].X - points[i].X, points[i + 1].Y - points[i].Y) is not { } d) return null;
            total += Math.Sqrt(d.X * d.X + d.Y * d.Y);
        }
        return total;
    }

    /// <summary>The area of a polygon (closed implicitly) in units of x squared; null if not defined.</summary>
    public double? Surface(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3) return 0;
        double twice = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            if (ToUnits(a.X - points[0].X, a.Y - points[0].Y) is not { } p || ToUnits(b.X - points[0].X, b.Y - points[0].Y) is not { } q) return null;
            twice += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(twice) / 2;
    }
}

/// <summary>A viewport of a page (ISO 32000-2, 12.9, Table 265): a region with its own scale.</summary>
/// <param name="Box">/BBox as given: the order of its corners sets the directions of the measuring axes.</param>
/// <param name="Name">/Name, for the user interface; empty without one.</param>
/// <param name="Measure">The rectilinear measure; null for another kind (GEO) or none.</param>
public sealed record Viewport(double[] Box, string Name, RectilinearMeasure? Measure)
{
    /// <summary>Whether the viewport covers a point of default user space.</summary>
    public bool Contains(double x, double y) =>
        x >= Math.Min(Box[0], Box[2]) && x <= Math.Max(Box[0], Box[2]) && y >= Math.Min(Box[1], Box[3]) && y <= Math.Max(Box[1], Box[3]);
}

/// <summary>The scales of a page (ISO 32000-2, 12.9): its viewports (/VP), and the one that applies at a point.</summary>
public static class PageScales
{
    /// <summary>Most viewports read per page (a crafted file with millions would be read each time a measurement starts).</summary>
    private const int MaxViewports = 1000;

    /// <summary>The viewports of a page in drawing order; empty without /VP. Viewports without a usable /BBox are left out.</summary>
    public static IReadOnlyList<Viewport> Of(PdfDictionary page)
    {
        if (page.GetArray(PdfName.Get("VP")) is not { } array) return [];
        var result = new List<Viewport>();
        for (var i = 0; i < Math.Min(array.Count, MaxViewports); i++)
        {
            if (array.Get(i) is not PdfDictionary dict || dict.GetArray(PdfName.Get("BBox")) is not { Count: 4 } box) continue;
            var corners = new double[4];
            var usable = true;
            for (var k = 0; k < 4; k++)
                if (box.GetNumber(k) is { } v && double.IsFinite(v)) corners[k] = v;
                else usable = false;
            if (!usable) continue;
            result.Add(new Viewport(corners, dict.Get(PdfName.Get("Name")) is PdfString name ? name.ToText() : "",
                RectilinearMeasure.Read(dict.GetDictionary(PdfName.Get("Measure")))));
        }
        return result;
    }

    /// <summary>Units the user can calibrate in, with millimetres per unit.</summary>
    public static readonly IReadOnlyList<(string Unit, double Millimetres)> Units = [("m", 1000), ("cm", 10), ("mm", 1)];

    /// <summary>
    /// A copy of a page dictionary with a scale for the whole page (ISO 32000-2, 12.9, Tables 265–268): a viewport over <paramref name="box"/>
    /// with a rectilinear measure — <paramref name="unitsPerPoint"/> units per point of default user space along x and y, distances and
    /// areas in the same unit (number formats with the defaults of the standard, two decimals). Appended to /VP, so that it applies on the
    /// whole page (the last viewport containing a point decides); a viewport over the same box written before is replaced (calibrating
    /// again does not pile them up). See docs/FEHLERGEDAECHTNIS.md, No. 582.
    /// </summary>
    /// <param name="page">The page dictionary (not changed).</param>
    /// <param name="box">The area of the page (crop box) in default user space.</param>
    /// <param name="unitsPerPoint">The scale: units per point.</param>
    /// <param name="unit">The unit, e.g. "m".</param>
    /// <param name="ratio">/R, the scale as text, e.g. "1:100".</param>
    /// <returns>The new page dictionary.</returns>
    public static PdfDictionary WithScale(PdfDictionary page, PdfRectangle box, double unitsPerPoint, string unit, string ratio)
    {
        if (!(unitsPerPoint > 0) || !double.IsFinite(unitsPerPoint)) throw new ArgumentOutOfRangeException(nameof(unitsPerPoint));
        static PdfString Text(string s) => new(PdfTextEncoding.Encode(s));
        static PdfDictionary Format(string label, double factor)
        {
            var d = new PdfDictionary();
            d.Set(PdfName.Type, PdfName.Get("NumberFormat"));
            d.Set(PdfName.Get("U"), Text(label));
            d.Set(PdfName.Get("C"), new PdfReal(factor));
            return d;
        }
        var measure = new PdfDictionary();
        measure.Set(PdfName.Type, PdfName.Get("Measure"));
        measure.Set(PdfName.Subtype, PdfName.Get("RL"));
        measure.Set(PdfName.Get("R"), Text(ratio));
        measure.Set(PdfName.Get("X"), new PdfArray([Format(unit, unitsPerPoint)]));
        measure.Set(PdfName.Get("D"), new PdfArray([Format(unit, 1)]));
        measure.Set(PdfName.Get("A"), new PdfArray([Format(unit + "²", 1)]));
        var corners = new[] { box.Left, box.Bottom, box.Right, box.Top };
        var viewport = new PdfDictionary();
        viewport.Set(PdfName.Type, PdfName.Get("Viewport"));
        viewport.Set(PdfName.Get("BBox"), new PdfArray([.. corners.Select(v => (PdfObject)new PdfReal(Math.Round(v, 4)))]));
        viewport.Set(PdfName.Get("Name"), Text(ratio));
        viewport.Set(PdfName.Get("Measure"), measure);

        var copy = new PdfDictionary(page.Count) { Resolver = page.Resolver };
        foreach (var (key, value) in page.Entries) copy.Set(key, value);
        var viewports = new PdfArray { Resolver = page.Resolver };
        if (page.GetArray(PdfName.Get("VP")) is { } existing)
            for (var i = 0; i < Math.Min(existing.Count, MaxViewports); i++)
            {
                var same = existing.Get(i) is PdfDictionary d && d.GetArray(PdfName.Get("BBox")) is { Count: 4 } b &&
                           Enumerable.Range(0, 4).All(k => b.GetNumber(k) is { } v && Math.Abs(v - corners[k]) < 0.01);
                if (!same) viewports.Add(existing[i]);
            }
        viewports.Add(viewport);
        copy.Set(PdfName.Get("VP"), viewports);
        return copy;
    }

    /// <summary>
    /// The viewport that applies at a point: the last in drawing order whose /BBox contains it (12.9.1); for a measurement over several points,
    /// that of the first point. Null if none contains it.
    /// </summary>
    public static Viewport? At(IReadOnlyList<Viewport> viewports, double x, double y)
    {
        for (var i = viewports.Count - 1; i >= 0; i--)
            if (viewports[i].Contains(x, y)) return viewports[i];
        return null;
    }
}
