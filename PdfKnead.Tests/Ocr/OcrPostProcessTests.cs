using PdfKnead.Ai;
using PdfKnead.Core.Ocr;
using Xunit;

namespace PdfKnead.Tests.Ocr;

/// <summary>The steps around the networks of the text recognition, on made-up network outputs: boxes, characters, words, model description.</summary>
public class OcrPostProcessTests
{
    /// <summary>A probability map with filled areas (1 inside, 0 outside).</summary>
    private static float[] Map(int width, int height, params Func<double, double, bool>[] areas)
    {
        var map = new float[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (areas.Any(a => a(x, y))) map[y * width + x] = 0.9f;
        return map;
    }

    [Fact]
    public void Boxes_TwoLines_InReadingOrderWithTheirCornersWidened()
    {
        var map = Map(200, 100, (x, y) => x is >= 20 and <= 120 && y is >= 10 and <= 19, (x, y) => x is >= 30 and <= 180 && y is >= 50 and <= 61);
        var boxes = PaddleOcr.InReadingOrder(TextBoxes.Find(map, 200, 100, 0.3, 0.6, 1.5, 1000));
        Assert.Equal(2, boxes.Count);
        var first = boxes[0];
        // corners top left, top right, bottom right, bottom left; widened by area × 1.5 / perimeter on every side
        var d = 100.0 * 9 * 1.5 / (2 * (100 + 9));
        Assert.Equal(20 - d, first[0].X, 1);
        Assert.Equal(10 - d, first[0].Y, 1);
        Assert.Equal(120 + d, first[2].X, 1);
        Assert.Equal(19 + d, first[2].Y, 1);
        Assert.True(first[1].X > first[0].X && first[3].Y > first[0].Y);
        Assert.True(boxes[1][0].Y > first[2].Y);
    }

    [Fact]
    public void Boxes_TurnedLine_TurnedRectangle()
    {
        // a band at 30° around (100, 100): 120 long, 12 thick
        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
        var map = Map(200, 200, (x, y) =>
        {
            double u = (x - 100) * c + (y - 100) * s, v = -(x - 100) * s + (y - 100) * c;
            return Math.Abs(u) <= 60 && Math.Abs(v) <= 6;
        });
        var box = Assert.Single(TextBoxes.Find(map, 200, 200, 0.3, 0.6, 1.5, 1000));
        var (length, thickness) = OcrInput.Extent(box);
        Assert.InRange(length, 125, 145);
        Assert.InRange(thickness, 18, 30);
        var angle = Math.Atan2(box[1].Y - box[0].Y, box[1].X - box[0].X) * 180 / Math.PI;
        Assert.InRange(angle, 27, 33);
    }

    [Fact]
    public void Boxes_TooThinOrTooUnsure_Dropped()
    {
        var map = Map(100, 100, (x, y) => x is >= 10 and <= 90 && y == 10, (x, y) => x is >= 10 and <= 90 && y is >= 40 and <= 50);
        for (var i = 40 * 100; i <= 50 * 100 + 99; i++)
            if (map[i] > 0) map[i] = 0.5f;   // above the pixel threshold, below the box threshold
        Assert.Empty(TextBoxes.Find(map, 100, 100, 0.3, 0.6, 1.5, 1000));
    }

    [Fact]
    public void Boxes_ManyRegions_OnlyTheLargestAreLookedAt()
    {
        var map = Map(400, 400, (x, y) => x % 10 < 6 && y % 10 < 6);
        Assert.Equal(25, TextBoxes.Find(map, 400, 400, 0.3, 0.6, 1.5, 25).Count);
    }

    /// <summary>A network output: per step the class with probability 0.9, the others share the rest.</summary>
    private static float[] Output(int classes, params int[] steps)
    {
        var output = new float[steps.Length * classes];
        for (var t = 0; t < steps.Length; t++)
            for (var c = 0; c < classes; c++)
                output[t * classes + c] = c == steps[t] ? 0.9f : 0.1f / (classes - 1);
        return output;
    }

    [Fact]
    public void Ctc_RepeatsJoined_BlankSeparates_SpaceAsLastClass()
    {
        string[] characters = ["a", "b", "ü"];
        var decoder = new CtcDecoder(characters, 5);
        // a a _ a b b _ space ü
        var read = decoder.Decode(Output(5, 1, 1, 0, 1, 2, 2, 0, 4, 3), 9);
        Assert.Equal("aab ü", string.Concat(read.Select(c => c.Text)));
        Assert.Equal([0, 3, 4, 7, 8], read.Select(c => c.Step));
        Assert.Equal(0.9f, read[0].Probability, 3);
    }

    [Fact]
    public void Ctc_ClassesNotMatchingTheCharacters_Refused()
    {
        Assert.Throws<InvalidDataException>(() => new CtcDecoder(["a", "b"], 7));
    }

    [Fact]
    public void Line_WordsSplitAtSpaces_BoxesAlongTheLine()
    {
        OcrPoint[] corners = [new(100, 50), new(500, 50), new(500, 90), new(100, 90)];
        var characters = new List<CtcCharacter>
        {
            new("H", 2, 0.9f), new("i", 4, 0.9f), new(" ", 8, 0.9f), new("d", 12, 0.8f), new("u", 14, 0.8f),
        };
        // 40 steps over a line image of 400 pixels: 10 pixels per step, the line image as long as the box
        var line = PaddleOcr.Line(characters, corners, 400, 10)!;
        Assert.Equal("Hi du", line.Text);
        Assert.Equal(0.86, line.Confidence, 2);
        Assert.Equal(2, line.Words.Count);
        // "Hi": from a step before its first character to halfway to the space; "du": from halfway after the space to a step after its end
        Assert.Equal(100 + 10, line.Words[0].Corners[0].X, 3);
        Assert.Equal(100 + 65, line.Words[0].Corners[1].X, 3);
        Assert.Equal(100 + 105, line.Words[1].Corners[0].X, 3);
        Assert.Equal(100 + 160, line.Words[1].Corners[1].X, 3);
        Assert.Equal(90, line.Words[1].Corners[2].Y, 3);
    }

    [Fact]
    public void ReadingOrder_SameHeight_LeftBeforeRight()
    {
        OcrPoint[] Box(double x, double y) => [new(x, y), new(x + 50, y), new(x + 50, y + 20), new(x, y + 20)];
        var sorted = PaddleOcr.InReadingOrder([Box(300, 102), Box(10, 100), Box(10, 200)]);
        Assert.Equal([10, 300, 10], sorted.Select(b => b[0].X));
    }

    [Fact]
    public void Description_CharactersWithQuotesAndEscapes_ThresholdsRead()
    {
        string[] lines =
        [
            "PostProcess:", "  box_thresh: 0.45", "  max_candidates: 3000", "  thresh: 0.2", "  unclip_ratio: 1.4",
            "  character_dict:", "  - a", "  - ''''", "  - '\"'", "  - \"\\u00FC\"", "  - \"\\U0001F600\"", "  - \"\\UFFFFFFFF\"", "  - ' '", "Other: 1",
        ];
        var d = PaddleDescription.Parse(lines);
        Assert.Equal(["a", "'", "\"", "ü", "😀", "UFFFFFFFF", " "], d.Characters);
        Assert.Equal(0.2, d.Threshold);
        Assert.Equal(0.45, d.BoxThreshold);
        Assert.Equal(1.4, d.UnclipRatio);
        Assert.Equal(3000, d.MaxCandidates);
    }

    [Fact]
    public void DetectionSize_MultipleOf32_LongestSideLimited()
    {
        Assert.Equal((1696, 2208), OcrInput.DetectionSize(1700, 2200, 4000));
        Assert.Equal((2016, 4000), OcrInput.DetectionSize(5000, 9921, 4000));
        Assert.Equal((64, 640), OcrInput.DetectionSize(20, 200, 4000));
    }

    [Fact]
    public void LineImage_SampledAlongTheBox_ValuesBetweenMinusOneAndOne()
    {
        // white image with a black square at 40…60
        var rgba = new byte[100 * 100 * 4];
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
            {
                var v = (byte)(x is >= 40 and < 60 && y is >= 40 and < 60 ? 0 : 255);
                rgba.AsSpan((y * 100 + x) * 4, 4).Fill(v);
            }
        var image = new OcrImage(100, 100, rgba);
        OcrPoint[] corners = [new(40, 40), new(60, 40), new(60, 60), new(40, 60)];
        var width = OcrInput.LineWidth(corners);
        Assert.Equal(48, width);
        var batch = new float[3 * 48 * 320];
        OcrInput.Line(image, corners, width, batch.AsSpan(), 0, 320);
        Assert.Equal(-1f, batch[24 * 320 + 24], 2);
        Assert.Equal(0f, batch[24 * 320 + 100]);   // beyond the line: padding
    }
}
