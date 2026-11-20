using Microsoft.ML.OnnxRuntime;
using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Ocr;

namespace PdfKnead.Ai;

/// <summary>Settings of the field detector.</summary>
/// <param name="Threads">Threads of the network; 0 = as many as the computer has cores.</param>
/// <param name="MinScore">Fields the detector is less sure of are dropped (0 … 1).</param>
/// <param name="MaxOverlap">Of two fields overlapping more than this (intersection over union) the less certain one goes, whatever their kinds.</param>
/// <remarks>
/// The publisher of the converted model detects with 0.3 and 0.1. Measured on the forms of the public test files (docs/PRUEFUNGEN.md, section
/// 6q): at 0.3 the recognition finds 90.7 % of their fields, at 0.5 only 63.2 % with barely fewer wrong proposals.
/// </remarks>
public sealed record FfdetrOptions(int Threads = 0, double MinScore = 0.3, double MaxOverlap = 0.1);

/// <summary>
/// The field detector FFDetr (KONZEPT.md, section 5.9a): an RF-DETR network that finds text fields, boxes to tick and signature fields in the
/// image of a form page. The page is scaled to 1024 × 1024 (antialiased, the aspect ratio is not kept — the network was trained so), the 300
/// answers of the network are scored with the sigmoid of their class, weak ones dropped, overlapping ones thinned. Runs in ONNX Runtime on
/// the processor; nothing leaves the computer.
/// </summary>
/// <remarks>
/// Model: <c>docspring/ffdetr-onnx</c> on Hugging Face, Apache-2.0 (FFDetr and CommonForms by Joe Barrow, https://arxiv.org/abs/2509.16506);
/// input and output as described there: <c>input</c> [1, 3, 1024, 1024] with the ImageNet mean and spread, <c>dets</c> [1, 300, 4] as
/// centre and size relative to the page, <c>labels</c> [1, 300, 91] class logits of which the first three are used.
/// </remarks>
public sealed class FfdetrDetector : IFieldDetector
{
    /// <summary>The side of the square the network sees.</summary>
    public const int Side = 1024;

    /// <summary>The classes the network was trained on (the others of its 91 outputs are unused).</summary>
    private const int Classes = 3;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Spread = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly FfdetrOptions _options;

    static FfdetrDetector() => OnnxRuntimeSetup.Prepare();

    /// <summary>Loads the model from the folder „ocr“ (<see cref="OcrModels.FieldDetector"/>).</summary>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The detector.</returns>
    /// <exception cref="TextRecognitionException">The model is not there or cannot be loaded.</exception>
    public static FfdetrDetector Open(FfdetrOptions? options = null) =>
        OcrModels.FieldDetectorPath is { } path ? new FfdetrDetector(path, options) : throw new TextRecognitionException("The field detector FFDetr is not installed.");

    /// <summary>Loads the model from a file.</summary>
    /// <param name="path">The ONNX file.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <exception cref="TextRecognitionException">The model is missing, damaged or does not fit, or ONNX Runtime cannot be loaded.</exception>
    public FfdetrDetector(string path, FfdetrOptions? options = null)
    {
        _options = options ?? new FfdetrOptions();
        try
        {
            using var settings = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Math.Max(0, _options.Threads),
                // without the arena the memory of a run goes back after it (the detector runs once per page). A run needs about 1 GB at its
                // peak, almost all of it the attention of the network at 1024 × 1024; without prepacking or with less graph optimisation it is
                // the same (measured, docs/PRUEFUNGEN.md, section 6q)
                EnableCpuMemArena = false,
                EnableMemoryPattern = false,
            };
            _session = new InferenceSession(path, settings);
            if (!_session.InputNames.Contains("input") || !_session.OutputNames.Contains("dets") || !_session.OutputNames.Contains("labels"))
            {
                _session.Dispose();
                throw new TextRecognitionException($"{Path.GetFileName(path)} is not the field detector FFDetr (inputs and outputs differ).");
            }
        }
        catch (Exception e) when (e is OnnxRuntimeException or IOException or TypeInitializationException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new TextRecognitionException($"The field detector could not be loaded: {e.Message}", e);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<DetectedField> Detect(OcrImage image, CancellationToken cancel = default)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Rgba.Length < (long)image.Width * image.Height * 4) return [];
        var input = Input(image, cancel);
        using var run = new RunOptions();
        using var stop = cancel.Register(() => run.Terminate = true);
        using var value = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.AsMemory(), [1, 3, Side, Side]);
        IDisposableReadOnlyCollection<OrtValue> output;
        try
        {
            output = _session.Run(run, ["input"], [value], ["dets", "labels"]);
        }
        catch (OnnxRuntimeException) when (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancel);
        }
        using (output)
        {
            var boxes = output[0].GetTensorDataAsSpan<float>();
            var logits = output[1].GetTensorDataAsSpan<float>();
            var queries = (int)output[0].GetTensorTypeAndShape().Shape[1];
            var width = (int)output[1].GetTensorTypeAndShape().Shape[2];
            return Fields(boxes, logits, queries, width, image.Width, image.Height, _options);
        }
    }

    /// <summary>
    /// The answers of the network as fields: per query the class of the first three with the highest score (sigmoid of its logit), those
    /// above <see cref="FfdetrOptions.MinScore"/> scaled to the image, then of overlapping ones only the more certain (class-agnostic).
    /// </summary>
    /// <param name="boxes">[queries, 4]: centre x, centre y, width, height relative to the image.</param>
    /// <param name="logits">[queries, width]: class logits.</param>
    /// <param name="queries">Number of queries.</param>
    /// <param name="width">Number of classes per query in <paramref name="logits"/>.</param>
    /// <param name="imageWidth">Width of the image in pixels.</param>
    /// <param name="imageHeight">Height of the image in pixels.</param>
    /// <param name="options">Thresholds.</param>
    /// <returns>The fields, the most certain first.</returns>
    internal static List<DetectedField> Fields(ReadOnlySpan<float> boxes, ReadOnlySpan<float> logits, int queries, int width, int imageWidth, int imageHeight,
        FfdetrOptions options)
    {
        var found = new List<DetectedField>();
        queries = Math.Min(queries, Math.Min(boxes.Length / 4, width > 0 ? logits.Length / width : 0));
        for (var q = 0; q < queries; q++)
        {
            var best = -1;
            var score = 0.0;
            for (var c = 0; c < Math.Min(Classes, width); c++)
            {
                var s = 1 / (1 + Math.Exp(-logits[q * width + c]));
                if (s > score)
                {
                    score = s;
                    best = c;
                }
            }
            if (best < 0 || score < options.MinScore || double.IsNaN(score)) continue;
            double cx = boxes[q * 4], cy = boxes[q * 4 + 1], w = boxes[q * 4 + 2], h = boxes[q * 4 + 3];
            if (!double.IsFinite(cx + cy + w + h) || w <= 0 || h <= 0) continue;
            var left = Math.Clamp(cx - w / 2, 0, 1) * imageWidth;
            var right = Math.Clamp(cx + w / 2, 0, 1) * imageWidth;
            var top = Math.Clamp(cy - h / 2, 0, 1) * imageHeight;
            var bottom = Math.Clamp(cy + h / 2, 0, 1) * imageHeight;
            if (right - left < 1 || bottom - top < 1) continue;
            found.Add(new DetectedField(left, top, right, bottom, (DetectedFieldKind)best, score));
        }
        found.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<DetectedField>(found.Count);
        foreach (var f in found)
            if (!kept.Any(k => Overlap(k, f) > options.MaxOverlap)) kept.Add(f);
        return kept;
    }

    /// <summary>Intersection over union of two fields.</summary>
    private static double Overlap(DetectedField a, DetectedField b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return 0;
        var both = w * h;
        return both / ((a.Right - a.Left) * (a.Bottom - a.Top) + (b.Right - b.Left) * (b.Bottom - b.Top) - both);
    }

    /// <summary>
    /// The image as the network takes it: scaled to <see cref="Side"/> × <see cref="Side"/> with a triangle filter as wide as the scale
    /// (antialiased bilinear, as the network was trained with), RGB from 0 to 1 minus the ImageNet mean, divided by its spread, planar.
    /// </summary>
    /// <param name="image">The page image (RGBA).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>3 × Side × Side values.</returns>
    internal static float[] Input(OcrImage image, CancellationToken cancel = default)
    {
        int w = image.Width, h = image.Height;
        var (columns, columnWeights) = Kernel(w, Side);
        var (rows, rowWeights) = Kernel(h, Side);
        // across first: h rows of Side pixels, 3 channels each
        var across = new float[(long)h * Side * 3];
        var rgba = image.Rgba;
        for (var y = 0; y < h; y++)
        {
            if ((y & 127) == 0) cancel.ThrowIfCancellationRequested();
            var row = (long)y * w * 4;
            for (var x = 0; x < Side; x++)
            {
                var (first, count) = columns[x];
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < count; k++)
                {
                    var weight = columnWeights[x][k];
                    var p = row + (first + k) * 4;
                    r += weight * rgba[p];
                    g += weight * rgba[p + 1];
                    b += weight * rgba[p + 2];
                }
                var o = ((long)y * Side + x) * 3;
                across[o] = r;
                across[o + 1] = g;
                across[o + 2] = b;
            }
        }
        var input = new float[3 * Side * Side];
        for (var y = 0; y < Side; y++)
        {
            if ((y & 127) == 0) cancel.ThrowIfCancellationRequested();
            var (first, count) = rows[y];
            for (var x = 0; x < Side; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < count; k++)
                {
                    var weight = rowWeights[y][k];
                    var o = ((long)(first + k) * Side + x) * 3;
                    r += weight * across[o];
                    g += weight * across[o + 1];
                    b += weight * across[o + 2];
                }
                var at = y * Side + x;
                input[at] = (r / 255f - Mean[0]) / Spread[0];
                input[Side * Side + at] = (g / 255f - Mean[1]) / Spread[1];
                input[2 * Side * Side + at] = (b / 255f - Mean[2]) / Spread[2];
            }
        }
        return input;
    }

    /// <summary>
    /// The source pixels and weights of each target pixel when scaling <paramref name="from"/> pixels to <paramref name="to"/>: a triangle
    /// around the target's centre, as wide as one target pixel covers (at least one source pixel), the weights summing to 1.
    /// </summary>
    internal static ((int First, int Count)[] Spans, float[][] Weights) Kernel(int from, int to)
    {
        var scale = (double)from / to;
        var support = Math.Max(1.0, scale);
        var spans = new (int, int)[to];
        var weights = new float[to][];
        for (var i = 0; i < to; i++)
        {
            var centre = (i + 0.5) * scale;
            var first = Math.Max(0, (int)Math.Floor(centre - support));
            var last = Math.Min(from - 1, (int)Math.Ceiling(centre + support));
            var list = new float[Math.Max(1, last - first + 1)];
            double sum = 0;
            for (var j = first; j <= last; j++)
            {
                var weight = Math.Max(0, 1 - Math.Abs(j + 0.5 - centre) / support);
                list[j - first] = (float)weight;
                sum += weight;
            }
            if (sum <= 0)
            {
                first = Math.Clamp((int)centre, 0, from - 1);
                list = [1f];
                sum = 1;
            }
            for (var k = 0; k < list.Length; k++) list[k] = (float)(list[k] / sum);
            spans[i] = (first, list.Length);
            weights[i] = list;
        }
        return (spans, weights);
    }

    /// <inheritdoc/>
    public void Dispose() => _session.Dispose();
}
