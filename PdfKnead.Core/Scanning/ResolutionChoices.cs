namespace PdfKnead.Core.Scanning;

/// <summary>Turns what a driver says about resolutions (a list of values or a range) into the choices shown to the user.</summary>
public static class ResolutionChoices
{
    /// <summary>The usual scanner resolutions; from a range, those inside it are offered.</summary>
    public static readonly IReadOnlyList<int> Common = [50, 75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400, 4800];

    /// <summary>The highest resolution offered at all: more yields pages of gigabytes without any gain for documents.</summary>
    public const int Max = 4800;

    /// <summary>The choices from a list of values the device names.</summary>
    /// <param name="values">The values (any order, duplicates allowed).</param>
    /// <returns>Positive values up to <see cref="Max"/>, ascending, without duplicates.</returns>
    public static IReadOnlyList<int> FromList(IEnumerable<int> values) =>
        [.. values.Where(v => v > 0 && v <= Max).Distinct().Order()];

    /// <summary>The choices from a range: the usual values inside it; if none is inside, the bounds.</summary>
    /// <param name="min">The lowest resolution.</param>
    /// <param name="max">The highest resolution.</param>
    /// <param name="step">The step between allowed values (0 or 1 = any).</param>
    /// <returns>The choices, ascending.</returns>
    public static IReadOnlyList<int> FromRange(int min, int max, int step)
    {
        if (max < min) (min, max) = (max, min);
        min = Math.Max(1, min);
        max = Math.Min(Max, max);
        if (max < min) return [];
        bool Allowed(int v) => v >= min && v <= max && (step <= 1 || (v - min) % step == 0);
        var values = Common.Where(Allowed).ToList();
        if (values.Count == 0) values.AddRange(min == max ? [min] : [min, max]);
        return values;
    }
}
