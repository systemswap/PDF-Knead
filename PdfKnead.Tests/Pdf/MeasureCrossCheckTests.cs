using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Messen“ against an independent recalculation (docs/FEHLERGEDAECHTNIS.md, No. 583, 584, 620): tools/messpruefung/nachrechnen.py computes
/// 1000 measurements — lengths, polylines, areas, angles, radii; scales with one or several units, fractions, separators, labels before or
/// after, separate y; the paper of pages with /UserUnit; the pieces and the angles at the points of polylines and areas; arcs through three
/// points and through more on circles known beforehand; polylines and areas with arcs built from known circles (Ablaufplan 7.6 j) — from
/// ISO 32000-2, 12.9 alone. PDF-Knead must give the same values and the same texts.
/// </summary>
public sealed class MeasureCrossCheckTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfArray Formats(JsonElement array) => new(array.EnumerateArray().Select(f =>
    {
        var dict = new PdfDictionary();
        foreach (var entry in f.EnumerateObject())
        {
            PdfObject value = entry.Name switch
            {
                "U" or "RT" or "RD" or "PS" or "SS" => S(entry.Value.GetString()!),
                "F" or "O" => PdfName.Get(entry.Value.GetString()!),
                "FD" => PdfBoolean.Of(entry.Value.GetBoolean()),
                "D" => PdfInteger.Of(entry.Value.GetInt32()),
                _ => new PdfReal(entry.Value.GetDouble()),
            };
            dict.Set(PdfName.Get(entry.Name), value);
        }
        return (PdfObject)dict;
    }));

    /// <summary>A viewport over everything with the measure of the case.</summary>
    private static IReadOnlyList<Viewport> Scales(JsonElement measure)
    {
        if (measure.ValueKind == JsonValueKind.Null) return [];
        var dict = TestPdf.Dict(("Type", TestPdf.N("Measure")), ("Subtype", TestPdf.N("RL")), ("R", S("Prüfung")));
        foreach (var key in new[] { "X", "Y", "D", "A", "T" })
            if (measure.TryGetProperty(key, out var formats)) dict.Set(PdfName.Get(key), Formats(formats));
        if (measure.TryGetProperty("CYX", out var cyx)) dict.Set(PdfName.Get("CYX"), new PdfReal(cyx.GetDouble()));
        return PageScales.Of(TestPdf.Dict(("VP", new PdfArray([TestPdf.Dict(("BBox", TestPdf.Box(-1, -1, 100000, 100000)), ("Measure", dict))]))));
    }

    [Fact]
    public void SameValuesAndTexts_AsTheRecalculationFromTheStandard()
    {
        using var cases = JsonDocument.Parse(MeasureCrossCheckCases.Json);
        var wrong = new List<string>();
        var number = 0;
        foreach (var c in cases.RootElement.EnumerateArray())
        {
            number++;
            var kind = Enum.Parse<MeasureKind>(c.GetProperty("kind").GetString()!);
            var points = c.GetProperty("points").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList();
            IReadOnlyList<int>? arcs = c.TryGetProperty("arcPoints", out var marked) ? [.. marked.EnumerateArray().Select(e => e.GetInt32())] : null;
            var measured = Measuring.Measure(kind, points, Scales(c.GetProperty("measure")), c.GetProperty("userUnit").GetDouble(), arcPoints: arcs);
            var value = c.GetProperty("value");
            var text = c.GetProperty("text").GetString();
            var sameValue = value.ValueKind == JsonValueKind.Null ? measured.Value is null
                : measured.Value is { } v && Math.Abs(v - value.GetDouble()) <= 1e-9 * Math.Max(1, Math.Abs(value.GetDouble()));
            if (!sameValue || measured.Text != text)
                wrong.Add($"#{number} {kind}: PDF-Knead {measured.Value} „{measured.Text}“, nachgerechnet {value} „{text}“");
            var segments = c.GetProperty("segments").EnumerateArray().Select(e => e.GetString()!).ToList();
            if (!segments.SequenceEqual(measured.Segments))
                wrong.Add($"#{number} {kind} Abschnitte: PDF-Knead [{string.Join(" | ", measured.Segments)}], nachgerechnet [{string.Join(" | ", segments)}]");
            var corners = c.GetProperty("corners").EnumerateArray().Select(e => e.GetString()!).ToList();
            if (!corners.SequenceEqual(measured.Corners))
                wrong.Add($"#{number} {kind} Winkel: PDF-Knead [{string.Join(" | ", measured.Corners)}], nachgerechnet [{string.Join(" | ", corners)}]");
            var arc = c.GetProperty("arc");
            if (arc.ValueKind == JsonValueKind.Null ? measured.Arc is not null
                : measured.Arc is not { } a || !Near(a.Length, arc.GetProperty("length").GetDouble()) || !Near(a.Sweep, arc.GetProperty("sweep").GetDouble())
                  || a.LengthText != arc.GetProperty("lengthText").GetString() || a.SweepText != arc.GetProperty("sweepText").GetString()
                  || a.DeviationText != arc.GetProperty("deviationText").GetString())
                wrong.Add($"#{number} {kind} Bogen: PDF-Knead {measured.Arc?.Length} „{measured.Arc?.LengthText}“ {measured.Arc?.Sweep} „{measured.Arc?.SweepText}“ "
                    + $"„{measured.Arc?.DeviationText}“, nachgerechnet {arc}");
        }
        Assert.Equal(1000, number);
        Assert.True(wrong.Count == 0, $"{wrong.Count} Abweichungen:\n" + string.Join("\n", wrong.Take(40)));
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(b));

    /// <summary>
    /// Dimensions saved by a reference program (public test files): its points, its scale (/Measure: 0.035278 cm per point) and the value
    /// it showed (/Contents). Measured again with PDF-Knead from the same points and scale. The newer files agree to the hundredth; in the two
    /// files of an older version the value shown does not follow from the file — recalculated by hand from its points and scale after 12.9,
    /// 2.534 cm and 1.443 cm² (shown: 2.49 cm, 1.46 cm²); PDF-Knead keeps to the file.
    /// </summary>
    [TestPdfsFact]
    public void DimensionsOfAReferenceProgram_SameValues()
    {
        var files = new (string Path, double Shown, double Ours)[]
        {
            (Path.Combine("mulobj", "new", "comments", "new_distance.pdf"), 3.50, 3.50),
            (Path.Combine("mulobj", "new", "comments", "new_perimeter.pdf"), 6.82, 6.82),
            (Path.Combine("mulobj", "2", "2_0_distance.pdf"), 2.49, 2.53),
            (Path.Combine("mulobj", "2", "2_2_area.pdf"), 1.46, 1.44),
        };
        foreach (var (path, shown, ours) in files)
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdfs.File("pdfium", Path.Combine("fx", path))));
            var annotations = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
            var dimension = Enumerable.Range(0, annotations.Count).Select(i => annotations.Get(i) as PdfDictionary)
                .Last(a => a?.GetDictionary(PdfName.Get("Measure")) is not null)!;
            var measure = RectilinearMeasure.Read(dimension.GetDictionary(PdfName.Get("Measure")))!;
            var numbers = (dimension.GetArray(PdfName.Get("L")) ?? dimension.GetArray(PdfName.Get("Vertices")))!;
            var points = Enumerable.Range(0, numbers.Count / 2).Select(i => (numbers.Get(2 * i).AsNumber()!.Value, numbers.Get(2 * i + 1).AsNumber()!.Value)).ToList();
            var value = dimension.GetName(PdfName.Subtype)!.Value == "Polygon" ? measure.Surface(points) : measure.Length(points);
            var contents = PdfTextEncoding.Decode(dimension.GetString(PdfName.Get("Contents"))!.Bytes);
            var inFile = double.Parse(Regex.Match(contents, @"(\d+\.\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.Equal(shown, inFile);
            Assert.Equal(ours, Math.Round(value!.Value, 2));
        }
    }
}
