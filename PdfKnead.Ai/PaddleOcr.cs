using System.Buffers;
using Microsoft.ML.OnnxRuntime;
using PdfKnead.Core.Ocr;

namespace PdfKnead.Ai;

/// <summary>Settings of the text recognition.</summary>
/// <param name="Threads">Threads per network; 0 = as many as the computer has cores.</param>
/// <param name="MaxSide">The longest side the detection network sees an image at (larger images are scaled down).</param>
/// <param name="MinConfidence">Lines the recognition is less sure of are dropped (0 … 1).</param>
/// <param name="BatchSize">Lines recognised together.</param>
/// <param name="Arena">Let ONNX Runtime keep the memory of its tensors between runs (a little faster, but it keeps the peak for as long as the
/// recognition is open).</param>
/// <remarks>
/// Measured on 14 scanned pages at 200 dpi (docs/PRUEFUNGEN.md, section 6o): finding the lines at 1600 pixels on the longest side reads as
/// many words correctly as at the full 2339 (96.6 %), in half the time and with 340 MB less memory; without the arena the peak is 30 % lower
/// for 8 % more time; batches of 6 lines are the fastest, several batches at once are not faster.
/// </remarks>
public sealed record PaddleOcrOptions(int Threads = 0, int MaxSide = 1600, double MinConfidence = 0.5, int BatchSize = 6, bool Arena = false);

/// <summary>
/// Text recognition with PP-OCRv6 (KONZEPT.md, section 5.9): the detection network marks where text lies (<see cref="TextBoxes"/>), the
/// recognition network reads each line (<see cref="CtcDecoder"/>). Both run in ONNX Runtime on the processor; nothing leaves the computer.
/// </summary>
public sealed class PaddleOcr : ITextRecognizer
{
    private readonly InferenceSession _detection;
    private readonly InferenceSession _recognition;
    private readonly PaddleDescription _detectionDescription;
    private readonly IReadOnlyList<string> _characters;
    private readonly PaddleOcrOptions _options;
    private CtcDecoder? _decoder;

    static PaddleOcr() => OnnxRuntimeSetup.Prepare();

    /// <summary>Loads the models of a size from the folder „ocr“ (<see cref="OcrModels"/>).</summary>
    /// <param name="size">The size.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The recognition.</returns>
    /// <exception cref="TextRecognitionException">The models of that size are not there or cannot be loaded.</exception>
    public static PaddleOcr Open(OcrModelSize size, PaddleOcrOptions? options = null)
    {
        var detection = OcrModels.FolderOf(size, recognition: false);
        var recognition = OcrModels.FolderOf(size, recognition: true);
        if (detection is null || recognition is null)
            throw new TextRecognitionException($"The models PP-OCRv6 {OcrModels.Key(size)} are not installed.");
        return new PaddleOcr(detection, recognition, options);
    }

    /// <summary>Loads the models from two folders, each with inference.onnx and inference.yml.</summary>
    /// <param name="detectionFolder">The detection model.</param>
    /// <param name="recognitionFolder">The recognition model.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <exception cref="TextRecognitionException">A model is missing, damaged or does not fit, or ONNX Runtime cannot be loaded.</exception>
    public PaddleOcr(string detectionFolder, string recognitionFolder, PaddleOcrOptions? options = null)
    {
        try
        {
            _options = options ?? new PaddleOcrOptions();
            (_detectionDescription, _characters, _detection, _recognition) = Load(detectionFolder, recognitionFolder);
        }
        catch (Exception e) when (e is OnnxRuntimeException or IOException or InvalidDataException or DllNotFoundException or TypeInitializationException
                                       or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new TextRecognitionException($"The text recognition could not be started: {e.Message}", e);
        }
    }

    private (PaddleDescription, IReadOnlyList<string>, InferenceSession, InferenceSession) Load(string detectionFolder, string recognitionFolder)
    {
        var description = PaddleDescription.Read(Path.Combine(detectionFolder, "inference.yml"));
        var characters = PaddleDescription.Read(Path.Combine(recognitionFolder, "inference.yml")).Characters;
        if (characters.Count == 0) throw new InvalidDataException("The recognition model has no characters.");
        var detection = Session(Path.Combine(detectionFolder, "inference.onnx"));
        try
        {
            return (description, characters, detection, Session(Path.Combine(recognitionFolder, "inference.onnx")));
        }
        catch
        {
            detection.Dispose();
            throw;
        }
    }

    private InferenceSession Session(string path)
    {
        using var settings = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Max(0, _options.Threads),
            EnableCpuMemArena = _options.Arena,
            EnableMemoryPattern = _options.Arena,
        };
        return new InferenceSession(path, settings);
    }

    /// <inheritdoc/>
    public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default) => Recognize(image, null, cancel);

    /// <inheritdoc/>
    public IReadOnlyList<OcrLine> Recognize(OcrImage image, Action<OcrProgress>? progress, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Rgba.Length < (long)image.Width * image.Height * 4) return [];
        var boxes = Detect(image, cancel);
        return Read(image, boxes, cancel, progress);
    }

    /// <summary>Where text lies: the lines as quadrilaterals in reading order, in the pixels of the image, top to bottom.</summary>
    internal List<OcrPoint[]> Detect(OcrImage image, CancellationToken cancel)
    {
        var (width, height) = OcrInput.DetectionSize(image.Width, image.Height, Math.Max(64, _options.MaxSide));
        var length = 3 * width * height;
        var input = ArrayPool<float>.Shared.Rent(length);
        var d = _detectionDescription;
        List<OcrPoint[]> boxes;
        try
        {
            OcrInput.Detection(image, width, height, input.AsSpan(0, length));
            cancel.ThrowIfCancellationRequested();
            using var output = Run(_detection, input.AsMemory(0, length), [1, 3, height, width], cancel, out var shape);
            int mapHeight = (int)shape[^2], mapWidth = (int)shape[^1];
            var map = output[0].GetTensorDataAsSpan<float>()[..(mapWidth * mapHeight)];
            boxes = TextBoxes.Find(map, mapWidth, mapHeight, d.Threshold, d.BoxThreshold, d.UnclipRatio, d.MaxCandidates, cancel);
            double sx = (double)image.Width / mapWidth, sy = (double)image.Height / mapHeight;
            foreach (var box in boxes)
                for (var i = 0; i < 4; i++)
                    box[i] = new OcrPoint(Math.Clamp(box[i].X * sx, 0, image.Width), Math.Clamp(box[i].Y * sy, 0, image.Height));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(input);
        }
        return InReadingOrder(boxes);
    }

    /// <summary>
    /// Sorts lines top to bottom; lines at about the same height (less than half the thinner one apart) left to right — columns beside each
    /// other otherwise come out in the order of their tops' pixel rows.
    /// </summary>
    internal static List<OcrPoint[]> InReadingOrder(List<OcrPoint[]> boxes)
    {
        var sorted = boxes.OrderBy(b => b[0].Y).ThenBy(b => b[0].X).ToList();
        for (var i = 0; i < sorted.Count - 1; i++)
            for (var j = i; j >= 0; j--)
            {
                var (a, b) = (sorted[j], sorted[j + 1]);
                var tolerance = Math.Min(OcrInput.Extent(a).Thickness, OcrInput.Extent(b).Thickness) / 2;
                if (Math.Abs(b[0].Y - a[0].Y) < tolerance && b[0].X < a[0].X) (sorted[j], sorted[j + 1]) = (b, a);
                else break;
            }
        return sorted;
    }

    /// <summary>Reads the lines.</summary>
    /// <remarks>The batches go by the width of the lines (the fastest way), not from top to bottom. <paramref name="progress"/> says how many
    /// are done; shown in reading order this is a steady picture of the progress, not of the very lines in the network.</remarks>
    internal List<OcrLine> Read(OcrImage image, List<OcrPoint[]> boxes, CancellationToken cancel, Action<OcrProgress>? progress = null)
    {
        // a line standing upright (much taller than wide) reads top to bottom: its corners are turned so that sampling lays it down
        var lines = boxes.Select(b =>
        {
            var (length, thickness) = OcrInput.Extent(b);
            return thickness >= 1.5 * length ? new[] { b[1], b[2], b[3], b[0] } : b;
        }).ToList();
        var results = new OcrLine?[lines.Count];
        var widths = lines.Select(OcrInput.LineWidth).ToArray();
        var order = Enumerable.Range(0, lines.Count).OrderBy(i => widths[i]).ToArray();
        var batchSize = Math.Max(1, _options.BatchSize);
        for (var start = 0; start < order.Length; start += batchSize)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = order.AsSpan(start, Math.Min(batchSize, order.Length - start)).ToArray();
            progress?.Invoke(new OcrProgress(boxes, start, batch.Length));
            var batchWidth = Math.Max(OcrInput.MinLineWidth, batch.Max(i => widths[i]));
            var length = batch.Length * 3 * OcrInput.LineHeight * batchWidth;
            var input = ArrayPool<float>.Shared.Rent(length);
            try
            {
                // the part of the batch beyond a line's own width stays 0 (padding)
                Array.Clear(input, 0, length);
                for (var k = 0; k < batch.Length; k++) OcrInput.Line(image, lines[batch[k]], widths[batch[k]], input.AsSpan(0, length), k, batchWidth);
                using var output = Run(_recognition, input.AsMemory(0, length), [batch.Length, 3, OcrInput.LineHeight, batchWidth], cancel, out var shape);
                Decode(output, shape, batch, lines, widths, batchWidth, results);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(input);
            }
        }
        return [.. results.Where(l => l is not null && l.Words.Count > 0 && l.Confidence >= _options.MinConfidence).Select(l => l!)];
    }

    /// <summary>Turns the output of one batch into its lines.</summary>
    private void Decode(IDisposableReadOnlyCollection<OrtValue> output, long[] shape, int[] batch, List<OcrPoint[]> lines, int[] widths, int batchWidth,
        OcrLine?[] results)
    {
        int steps = (int)shape[1], classes = (int)shape[2];
        var decoder = _decoder ??= new CtcDecoder(_characters, classes);
        if (decoder.Classes != classes) throw new InvalidDataException("The recognition model changed its number of classes.");
        var all = output[0].GetTensorDataAsSpan<float>();
        for (var k = 0; k < batch.Length; k++)
        {
            var characters = decoder.Decode(all.Slice(k * steps * classes, steps * classes), steps);
            var stepWidth = (double)batchWidth / steps;
            results[batch[k]] = Line(characters, lines[batch[k]], widths[batch[k]], stepWidth);
        }
    }

    /// <summary>
    /// A line from its characters: split into words at the spaces; a word reaches from halfway between the space before it and its first
    /// character to halfway between its last character and the space after it (at the ends of the line from a step before its first to a
    /// step after its last character).
    /// </summary>
    internal static OcrLine? Line(List<CtcCharacter> characters, OcrPoint[] corners, int lineWidth, double stepWidth)
    {
        if (characters.Count == 0) return null;
        var words = new List<OcrWord>();
        var confidence = characters.Average(c => c.Probability);
        var i = 0;
        while (i < characters.Count)
        {
            if (characters[i].Text == " ")
            {
                i++;
                continue;
            }
            var first = i;
            while (i < characters.Count && characters[i].Text != " ") i++;
            var last = i - 1;
            double from = first == 0 ? characters[first].Step - 1 : (characters[first - 1].Step + characters[first].Step) / 2.0 + 0.5;
            double to = i >= characters.Count ? characters[last].Step + 2 : (characters[last].Step + characters[i].Step) / 2.0 + 0.5;
            double u0 = Math.Clamp(from * stepWidth / lineWidth, 0, 1), u1 = Math.Clamp(to * stepWidth / lineWidth, u0, 1);
            var text = string.Concat(characters.Skip(first).Take(last - first + 1).Select(c => c.Text));
            words.Add(new OcrWord(text,
            [
                OcrInput.Bilinear(corners, u0, 0), OcrInput.Bilinear(corners, u1, 0), OcrInput.Bilinear(corners, u1, 1), OcrInput.Bilinear(corners, u0, 1),
            ]));
        }
        return new OcrLine(words, confidence);
    }

    /// <summary>Runs a network on one input; the shape of its first output comes back through <paramref name="shape"/>.</summary>
    private static IDisposableReadOnlyCollection<OrtValue> Run(InferenceSession session, Memory<float> input, long[] inputShape, CancellationToken cancel, out long[] shape)
    {
        using var run = new RunOptions();
        using var stop = cancel.Register(() => run.Terminate = true);
        using var value = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input, inputShape);
        IDisposableReadOnlyCollection<OrtValue> output;
        try
        {
            output = session.Run(run, [session.InputNames[0]], [value], [session.OutputNames[0]]);
        }
        catch (OnnxRuntimeException) when (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancel);
        }
        shape = output[0].GetTensorTypeAndShape().Shape;
        return output;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _detection.Dispose();
        _recognition.Dispose();
    }
}
