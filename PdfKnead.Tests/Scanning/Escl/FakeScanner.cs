using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>A page the scanner in software hands out, with the quirks of its delivery.</summary>
/// <param name="Data">The bytes.</param>
/// <param name="ContentType">The media type named.</param>
/// <param name="BusyBefore">How often the device answers „wait“ before it hands the page out.</param>
/// <param name="CutOnce">Whether the first delivery breaks off in the middle.</param>
/// <param name="ContentLocation">The document address named, or null.</param>
/// <param name="Delay">How long the device takes before it answers.</param>
internal sealed record FakePage(byte[] Data, string ContentType = "image/jpeg", int BusyBefore = 0, bool CutOnce = false, string? ContentLocation = null,
    TimeSpan Delay = default);

/// <summary>How the scanner in software names a new job.</summary>
public enum FakeLocation { Relative, AbsoluteSameHost, OtherHost, Missing }

/// <summary>
/// Scan jobs on top of <see cref="FakeEsclDevice"/> (KONZEPT.md §5.25, S9, „Prüfen“): creates jobs, hands out <see cref="Pages"/>, answers the
/// state with the job's progress, records the requests and deletions. Switches for the quirks real devices show.
/// </summary>
internal sealed class FakeScanner : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _settings = new();
    private readonly Lock _lock = new();
    private string? _job;
    private int _next;
    private int _busy;
    private readonly HashSet<int> _cut = [];

    public FakeScanner(bool tls = false)
    {
        Device = new FakeEsclDevice(tls) { Handler = Handle };
    }

    public FakeEsclDevice Device { get; }

    public List<FakePage> Pages { get; } = [];

    /// <summary>The feeder state in the status, as the device names it (null: none).</summary>
    public string? Feeder { get; set; } = "ScannerAdfLoaded";

    /// <summary>The job state after the last page.</summary>
    public string EndState { get; set; } = "Completed";

    /// <summary>The job state reported while pages remain.</summary>
    public string ActiveState { get; set; } = "Processing";

    /// <summary>The reason given with the end state.</summary>
    public string EndReason { get; set; } = "JobCompletedSuccessfully";

    /// <summary>The answer to „next page“ when none is left (404, or 410 at some devices).</summary>
    public int EndStatus { get; set; } = 404;

    public FakeLocation Location { get; set; } = FakeLocation.Relative;

    /// <summary>Answers to job creation in turn before the normal 201 (e.g. 409, 503).</summary>
    public Queue<int> CreateAnswers { get; } = new();

    /// <summary>How often the device answers „wait“ after the last page while it closes the job (its state then still says active).</summary>
    public int ClosingAnswers { get; set; }

    private int _closing;

    /// <summary>Whether a set number of sheets in the request is honoured (only these pages, then the end).</summary>
    public bool HonorsPageCount { get; set; } = true;

    private int _limit = int.MaxValue;

    /// <summary>How the jobs ended: „deleted early“ while pages remained, or „completed“.</summary>
    public List<string> Endings { get; } = [];

    /// <summary>Whether job creation answers 503 while an earlier job was not deleted.</summary>
    public bool BusyUntilDeleted { get; set; }

    /// <summary>The request bodies of job creation, as received.</summary>
    public IReadOnlyList<string> Settings => [.. _settings];

    public int Deletes;

    public int Creates;

    public string Description { get => Device.Capabilities; set => Device.Capabilities = value; }

    private FakeAnswer? Handle(FakeRequest r)
    {
        lock (_lock)
        {
            if (r.Method == "POST" && r.Path == "/eSCL/ScanJobs")
            {
                Creates++;
                _settings.Enqueue(Encoding.UTF8.GetString(r.Body));
                if (CreateAnswers.Count > 0) return FakeAnswer.Empty(CreateAnswers.Dequeue(), ("Retry-After", "0"));
                if (BusyUntilDeleted && _job is not null) return FakeAnswer.Empty(503, ("Retry-After", "0"));
                _job = $"/eSCL/ScanJobs/job{Creates}";
                _next = 0;
                var count = System.Text.RegularExpressions.Regex.Match(Encoding.UTF8.GetString(r.Body), @"<scan:NumberOfPages>(\d+)</");
                _limit = HonorsPageCount && count.Success ? int.Parse(count.Groups[1].Value) : int.MaxValue;
                _busy = 0;
                _closing = 0;
                return Location switch
                {
                    FakeLocation.Relative => FakeAnswer.Empty(201, ("Location", _job)),
                    FakeLocation.AbsoluteSameHost => FakeAnswer.Empty(201, ("Location", $"http://127.0.0.1:{Device.Port}{_job}")),
                    FakeLocation.OtherHost => FakeAnswer.Empty(201, ("Location", $"http://192.168.1.99{_job}")),
                    _ => FakeAnswer.Empty(201),
                };
            }
            if (_job is not null && r.Method == "GET" && r.Path == _job + "/NextDocument")
            {
                if (_next >= Math.Min(Pages.Count, _limit))
                {
                    if (_closing < ClosingAnswers)
                    {
                        _closing++;
                        return FakeAnswer.Empty(503, ("Retry-After", "0"));
                    }
                    return FakeAnswer.Empty(EndStatus);
                }
                var page = Pages[_next];
                if (_busy < page.BusyBefore)
                {
                    _busy++;
                    return FakeAnswer.Empty(503, ("Retry-After", "0"));
                }
                if (page.CutOnce && _cut.Add(_next))
                    return new FakeAnswer(200, page.Data, page.ContentType, null, CutAfter: page.Data.Length / 2, Delay: page.Delay);
                _next++;
                _busy = 0;
                var headers = page.ContentLocation is { } cl ? new[] { ("Content-Location", cl) } : [];
                return new FakeAnswer(200, page.Data, page.ContentType, headers, Delay: page.Delay);
            }
            if (r.Method == "DELETE" && _job is not null && r.Path == _job)
            {
                Deletes++;
                Endings.Add(_next >= Math.Min(Pages.Count, _limit) ? "completed" : "deleted early");
                _job = null;
                return FakeAnswer.Empty(200);
            }
            if (r.Method == "GET" && r.Path == "/eSCL/ScannerStatus") return FakeAnswer.Xml(Status());
            return null;
        }
    }

    private string Status()
    {
        var job = _job is null ? "" : $"""
            <scan:Jobs><scan:JobInfo>
              <pwg:JobUri>{_job}</pwg:JobUri>
              <pwg:ImagesCompleted>{_next}</pwg:ImagesCompleted>
              <pwg:JobState>{(_next >= Math.Min(Pages.Count, _limit) && _closing >= ClosingAnswers ? EndState : ActiveState)}</pwg:JobState>
              <pwg:JobStateReasons><pwg:JobStateReason>{(_next >= Math.Min(Pages.Count, _limit) ? EndReason : "JobScanning")}</pwg:JobStateReason></pwg:JobStateReasons>
            </scan:JobInfo></scan:Jobs>
            """;
        var feeder = Feeder is null ? "" : $"<scan:AdfState>{Feeder}</scan:AdfState>";
        return $"""
            <scan:ScannerStatus xmlns:scan="{EsclSamples.Scan}" xmlns:pwg="{EsclSamples.Pwg}">
              <pwg:Version>2.63</pwg:Version><pwg:State>{(_job is null ? "Idle" : "Processing")}</pwg:State>{feeder}{job}
            </scan:ScannerStatus>
            """;
    }

    /// <summary>A page image: a gradient with a mark, so that size and colour can be checked.</summary>
    public static byte[] Image(int width, int height, bool color, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(width, height, color ? SKColorType.Rgba8888 : SKColorType.Gray8, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, color ? new SKColor((byte)(x * 255 / width), 80, (byte)(y * 255 / height)) : new SKColor((byte)(x * 255 / width), (byte)(x * 255 / width), (byte)(x * 255 / width)));
        using var data = bitmap.Encode(format, 92);
        return data.ToArray();
    }

    public ValueTask DisposeAsync() => Device.DisposeAsync();
}
