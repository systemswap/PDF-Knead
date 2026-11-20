using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// One scan job on an eSCL scanner (KONZEPT.md §5.25, S9, „Ablauf“): read the state (an empty feeder or a jam is reported before anything
/// starts), create the job, fetch the pages one by one, and delete the job at the end — exactly once, also after errors and cancellation,
/// because some devices stay busy otherwise. Every answer of the device is decided in one place per step; a device that asks to wait is
/// asked again after its wait, within limits; after „no more pages“ the state tells whether the job finished or was ended at the device.
/// A page the device hands out twice (same document address) is kept once. Pages handed out before an error stay with the caller.
/// </summary>
public static class EsclScanJob
{
    /// <summary>The longest wait a device may ask for at a time; longer waits are cut to this and asked again.</summary>
    public static TimeSpan MaxRetryWait { get; internal set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the state is read while the device keeps asking to wait (page counter, feeder, an ended job).</summary>
    public static TimeSpan StatusInterval { get; internal set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a device may answer „busy“ to a new job: after a cancelled job some devices take more than ten seconds before they take
    /// the next one (Fehlergedächtnis 952).
    /// </summary>
    public static TimeSpan BusyOnCreate { get; internal set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often „wait“ is accepted in a row for one page.</summary>
    public const int MaxBusyAnswers = 600;

    /// <summary>How often „no more pages“ is asked again while the state still calls the job active.</summary>
    public const int MaxEarlyEnds = 3;

    /// <summary>How often a broken connection is taken up again for one page while the state calls the job active.</summary>
    public const int MaxBrokenConnections = 3;

    private const int MaxSmallAnswer = 64 << 10;

    /// <summary>Scans.</summary>
    /// <param name="client">The connection to the device (with a confirmed certificate where it uses TLS).</param>
    /// <param name="caps">What the device offers.</param>
    /// <param name="settings">The user's settings.</param>
    /// <param name="progress">Receives the progress of the page being fetched.</param>
    /// <param name="log">Where the steps are noted, or null.</param>
    /// <param name="cancel">Stops the job; the job is deleted at the device, pages already handed out stay.</param>
    /// <param name="maxPages">
    /// The most pages, 0 for all. From the feeder, a device that can draw a set number of sheets is asked for exactly these and its job runs
    /// to its end at the device (deleting it earlier counts as a cancellation there, and the device stays busy for seconds,
    /// Fehlergedächtnis 952); should it deliver more anyway, the job is ended. Any other device stops after these pages, and the job
    /// is deleted (it may have drawn in a further sheet by then).
    /// </param>
    /// <returns>The pages as they arrive.</returns>
    /// <exception cref="ScanException">The device reported an error or could not be reached.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static async IAsyncEnumerable<ScannedImage> RunAsync(EsclClient client, EsclCapabilities caps, ScanSettings settings,
        IProgress<ScanProgress>? progress = null, EsclDiagnostics? log = null, [EnumeratorCancellation] CancellationToken cancel = default, int maxPages = 0)
    {
        // everything below runs beside the window's thread (ConfigureAwait(false)): a page of 1200 dpi takes seconds to receive and unpack
        var request = EsclScanRequest.Choose(caps, settings);
        if (maxPages > 0 && request.Input != EsclInput.Platen && caps.FeederOptions.Contains("SelectSinglePage", StringComparer.OrdinalIgnoreCase))
            request = request with { Pages = maxPages };
        // the job before may still be closing at the device (it was handed over after its last page): wait for it, and say so
        if (client.Closing is { IsCompleted: false } closing)
        {
            progress?.Report(new ScanProgress(1, -1, ScanPhase.Waiting));
            try
            {
                await closing.WaitAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                log?.Add("Auftrag", "Abschluss des vorigen Auftrags: " + e.Message);
            }
        }
        await CheckReadyAsync(client, caps, request.Input, log, cancel).ConfigureAwait(false);
        (var job, request) = await CreateAsync(client, caps, request, progress, log, cancel).ConfigureAwait(false);
        var handedOver = false;
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var exact = request.Pages > 0;
            for (var page = 1; maxPages <= 0 || page <= maxPages || exact; page++)
            {
                // from the glass there is one page, from the feeder no more than asked for: then the device only closes the job
                var finishing = page > 1 && request.Input == EsclInput.Platen || maxPages > 0 && page > maxPages;
                if (finishing)
                {
                    // the pages are all there: the window is free at once, the device closes the job in the background (it takes seconds —
                    // the next job waits for it, see above)
                    var last = page;
                    client.Closing = Task.Run(() => CloseAsync(client, job, request, last, seen, log));
                    handedOver = true;
                    yield break;
                }
                var image = await NextPageAsync(client, job, request, page, seen, progress, log, cancel).ConfigureAwait(false);
                if (image is null) yield break;
                if (maxPages > 0 && page > maxPages)
                {
                    log?.Add("Auftrag", $"der Scanner liefert mehr als {maxPages} Seite(n) — Auftrag wird beendet");
                    yield break;
                }
                yield return image;
            }
        }
        finally
        {
            if (!handedOver) await DeleteAsync(client, job, log).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After the last page: lets the device end the job (its „no more pages“, or the state „completed“), then deletes it — the job ends
    /// regularly at the device, deleting it earlier would count as a cancellation (Fehlergedächtnis 952). Never throws.
    /// </summary>
    private static async Task CloseAsync(EsclClient client, string job, EsclScanRequest request, int page, HashSet<string> seen, EsclDiagnostics? log)
    {
        try
        {
            using var limit = new CancellationTokenSource(client.Timeouts.PageWait);
            if (await NextPageAsync(client, job, request, page, seen, null, log, limit.Token, finishing: true).ConfigureAwait(false) is not null)
                log?.Add("Auftrag", "der Scanner liefert mehr Seiten als verlangt — Auftrag wird beendet");
        }
        catch (Exception e)
        {
            log?.Add("Auftrag", "Abschluss: " + e.Message);
        }
        try
        {
            await DeleteAsync(client, job, log).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // the connection was closed meanwhile (window closed): the device ends the job on its own
            log?.Add("Auftrag", "Löschen nach dem Abschluss: " + e.Message);
        }
    }

    private static async Task CheckReadyAsync(EsclClient client, EsclCapabilities caps, EsclInput input, EsclDiagnostics? log, CancellationToken cancel)
    {
        var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
        if (status is null || input == EsclInput.Platen) return;
        // an empty feeder counts only where the device says it detects paper; other feeder states always stop the job before it starts
        if (status.Feeder == EsclFeederState.Empty)
        {
            if (caps.FeederOptions.Contains("DetectPaperLoaded", StringComparer.OrdinalIgnoreCase)) throw NoPaper();
            log?.Add("Auftrag", "Einzug meldet „leer“, erkennt Papier aber nicht sicher — es wird trotzdem versucht");
            return;
        }
        if (FeederProblem(status.Feeder) is { } problem) throw problem;
    }

    private static async Task<(string Job, EsclScanRequest Request)> CreateAsync(EsclClient client, EsclCapabilities caps, EsclScanRequest request,
        IProgress<ScanProgress>? progress, EsclDiagnostics? log, CancellationToken cancel)
    {
        var busy = Stopwatch.StartNew();
        while (true)
        {
            log?.Add("Auftrag", $"anlegen: {request.Input}, {request.ColorMode}, {request.Dpi} dpi, {request.Format}, Fläche {request.Width} × {request.Height}"
                + (request.Pages > 0 ? $", {request.Pages} Blatt" : ""));
            var answer = await client.SendAsync(HttpMethod.Post, "ScanJobs", request.ToXml(), "text/xml", MaxSmallAnswer, client.Timeouts.Request, cancel).ConfigureAwait(false);
            switch (answer.Status)
            {
                case 200 or 201 or 202:
                    if (answer.Location is not { Length: > 0 } location)
                        throw new EsclProtocolException(Tr.T("Der Scanner hat den Auftrag angenommen, aber keine Adresse dafür genannt."));
                    var uri = client.Resolve(location) ?? throw new EsclProtocolException(Tr.T("Der Scanner nennt eine ungültige Adresse."));
                    var job = uri.AbsolutePath.TrimEnd('/');
                    log?.Add("Auftrag", $"angelegt: {job}");
                    return (job, request);
                case 409 when request.Fallback(caps) is { } other:
                    log?.Add("Auftrag", $"abgelehnt (409) — weiter mit {other.ColorMode}, {other.Format}");
                    request = other;
                    break;
                case 409 or 400:
                    throw new EsclProtocolException(Tr.T("Der Scanner lehnt die Einstellung ab."));
                case 503 when busy.Elapsed < BusyOnCreate:
                    progress?.Report(new ScanProgress(1, -1, ScanPhase.Waiting));
                    await Task.Delay(Wait(answer.RetryAfter, TimeSpan.FromSeconds(5)), cancel).ConfigureAwait(false);
                    break;
                case 503:
                    throw new ScanException(Tr.T("Der Scanner ist gerade belegt — vielleicht von einem anderen Programm."), ScanErrorKind.Busy);
                case 401 or 403:
                    throw Login();
                default:
                    throw new EsclProtocolException(Tr.T("Der Scanner meldet einen Fehler ({0}).", answer.Status));
            }
        }
    }

    private static async Task<ScannedImage?> NextPageAsync(EsclClient client, string job, EsclScanRequest request, int page, HashSet<string> seen,
        IProgress<ScanProgress>? progress, EsclDiagnostics? log, CancellationToken cancel, bool finishing = false)
    {
        var phase = finishing ? ScanPhase.Finishing : ScanPhase.Reading;
        var sinceData = Stopwatch.StartNew();
        var sinceStatus = Stopwatch.StartNew();
        int busy = 0, earlyEnds = 0, broken = 0;
        var fraction = progress is null || finishing ? null
            : new Inline(t => progress.Report(new ScanProgress(page, Share(t), ScanPhase.Transferring, t.Received)));
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(finishing ? page - 1 : page, -1, phase));
            EsclResponse answer;
            try
            {
                answer = await client.GetDocumentAsync(job + "/NextDocument", EsclImage.MaxBytes, fraction, cancel).ConfigureAwait(false);
            }
            catch (ScanException e) when (e is not EsclCertificateException && e.Kind == ScanErrorKind.Offline)
            {
                // a time limit or a broken connection: the state decides whether the job goes on
                var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
                var info = status?.Job(job);
                if (info?.State is EsclJobState.Pending or EsclJobState.Processing && ++broken <= MaxBrokenConnections && sinceData.Elapsed < client.Timeouts.PageWait)
                {
                    log?.Add("Auftrag", $"Seite {page}: Verbindung unterbrochen, der Auftrag läuft noch — neuer Versuch");
                    continue;
                }
                throw Ended(info, status) ?? e;
            }
            switch (answer.Status)
            {
                case 200 when answer.Body.Length == 0:
                case 404:
                {
                    var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
                    var info = status?.Job(job);
                    if (info?.State is EsclJobState.Pending or EsclJobState.Processing && ++earlyEnds <= MaxEarlyEnds)
                    {
                        log?.Add("Auftrag", $"Seite {page}: „keine Seite mehr“, der Auftrag läuft aber noch — neuer Versuch");
                        await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                        continue;
                    }
                    if (Ended(info, status) is { } ended) throw ended;
                    if (page == 1) throw NoPages(request.Input, status);
                    log?.Add("Auftrag", $"fertig nach {page - 1} Seite(n)");
                    return null;
                }
                case 200:
                    if (answer.ContentLocation is { Length: > 0 } document && !seen.Add(document))
                    {
                        log?.Add("Auftrag", $"Seite {page}: dieselbe Seite noch einmal ({document}) — übergangen");
                        continue;
                    }
                    progress?.Report(new ScanProgress(page, -1, ScanPhase.Processing, answer.Body.Length));
                    var body = answer;
                    var image = await Task.Run(() => EsclImage.Decode(body.Body, body.ContentType, request.Dpi, log), cancel).ConfigureAwait(false);
                    log?.Add("Auftrag", $"Seite {page}: {image.Width} × {image.Height}, {image.Channels} Kanal/Kanäle, {image.Dpi} dpi, {answer.Body.Length} Bytes");
                    return image;
                case 503 when finishing:
                {
                    // closing: the state says „completed“ before „no more pages“ comes (measured: a second earlier)
                    var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
                    if (status?.Job(job)?.State is EsclJobState.Completed or EsclJobState.Canceled or EsclJobState.Aborted
                        || (status is { State: EsclScannerState.Idle } && status.Job(job) is null))
                    {
                        log?.Add("Auftrag", "abgeschlossen (Zustand)");
                        return null;
                    }
                    if (sinceData.Elapsed >= client.Timeouts.PageWait) return null;
                    await Task.Delay(Wait(answer.RetryAfter, MaxRetryWait), cancel).ConfigureAwait(false);
                    continue;
                }
                case 503:
                    if (++busy > MaxBusyAnswers || sinceData.Elapsed >= client.Timeouts.PageWait)
                        throw new EsclProtocolException(Tr.T("Der Scanner antwortet nicht rechtzeitig."), ScanErrorKind.Offline);
                    if (sinceStatus.Elapsed >= StatusInterval)
                    {
                        sinceStatus.Restart();
                        var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
                        var problem = Ended(status?.Job(job), status) ?? (status is null ? null : FeederProblem(status.Feeder));
                        if (problem is not null) throw problem;
                    }
                    await Task.Delay(Wait(answer.RetryAfter, MaxRetryWait), cancel).ConfigureAwait(false);
                    continue;
                case 410:
                {
                    var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
                    if (Ended(status?.Job(job), status) is { } ended) throw ended;
                    if (page > 1 && status?.Job(job)?.State == EsclJobState.Completed) return null;
                    throw new EsclProtocolException(Tr.T("Der Auftrag ist am Scanner nicht mehr vorhanden."));
                }
                case 401 or 403:
                    throw Login();
                default:
                {
                    var status = await TryStatusAsync(client, log, cancel).ConfigureAwait(false);
                    throw Ended(status?.Job(job), status) ?? (status is null ? null : FeederProblem(status.Feeder))
                        ?? new EsclProtocolException(Tr.T("Der Scanner meldet einen Fehler ({0}).", answer.Status));
                }
            }
        }
    }

    private static async Task DeleteAsync(EsclClient client, string job, EsclDiagnostics? log)
    {
        // never cancelled by the user's cancellation: deleting is what cancelling means at the device
        try
        {
            var answer = await client.SendAsync(HttpMethod.Delete, job, null, null, MaxSmallAnswer, client.Timeouts.Request, CancellationToken.None).ConfigureAwait(false);
            log?.Add("Auftrag", $"gelöscht ({answer.Status})");
        }
        catch (ScanException e)
        {
            log?.Add("Auftrag", "löschen gescheitert: " + e.Message);
        }
    }

    private static async Task<EsclStatus?> TryStatusAsync(EsclClient client, EsclDiagnostics? log, CancellationToken cancel)
    {
        try
        {
            return await client.GetStatusAsync(cancel).ConfigureAwait(false);
        }
        catch (ScanException e) when (e is not EsclCertificateException)
        {
            log?.Add("Zustand", "nicht lesbar: " + e.Message);
            return null;
        }
    }

    /// <summary>The error for a job the device ended, or null if it did not end it.</summary>
    private static ScanException? Ended(EsclJobInfo? info, EsclStatus? status)
    {
        if (info?.State is not (EsclJobState.Canceled or EsclJobState.Aborted)) return null;
        if (status is not null && FeederProblem(status.Feeder) is { } problem) return problem;
        if (info.State == EsclJobState.Canceled) return new ScanException(Tr.T("Der Auftrag wurde am Scanner abgebrochen."));
        return new EsclProtocolException(Tr.T("Der Scanner hat den Auftrag abgebrochen ({0}).", info.Reasons.Count > 0 ? string.Join(", ", info.Reasons) : "Aborted"));
    }

    /// <summary>The error for a feeder state that needs the user, or null.</summary>
    public static ScanException? FeederProblem(EsclFeederState state) => state switch
    {
        EsclFeederState.Jam => new ScanException(Tr.T("Papierstau im Scanner."), ScanErrorKind.Jammed),
        EsclFeederState.HatchOpen => new ScanException(Tr.T("Eine Abdeckung des Scanners ist offen."), ScanErrorKind.CoverOpen),
        EsclFeederState.Mispick or EsclFeederState.MultipickDetected or EsclFeederState.DuplexPageTooShort or EsclFeederState.DuplexPageTooLong
            or EsclFeederState.InputTrayFailed or EsclFeederState.InputTrayOverloaded =>
            new ScanException(Tr.T("Mit dem Papier im Scanner stimmt etwas nicht — bitte nachsehen."), ScanErrorKind.Jammed),
        _ => null,
    };

    private static ScanException NoPages(EsclInput input, EsclStatus? status) =>
        input != EsclInput.Platen && status?.Feeder == EsclFeederState.Empty ? NoPaper()
        : (status is null ? null : FeederProblem(status.Feeder)) ?? new EsclProtocolException(Tr.T("Der Scanner hat keine Bilddaten geliefert."));

    private static ScanException NoPaper() => new(Tr.T("Im Einzug liegt kein Papier."), ScanErrorKind.NoPaper);

    private static EsclProtocolException Login() => new(Tr.T("Der Scanner verlangt eine Anmeldung — das unterstützt PDF-Knead noch nicht."));

    private static TimeSpan Wait(TimeSpan? asked, TimeSpan max) =>
        asked is { } a ? (a < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : a > max ? max : a) : TimeSpan.FromSeconds(1);

    // reports at once on the calling thread (Progress<T> would post to a synchronisation context)
    /// <summary>
    /// The share of the page: the rows that arrived against the height (the device streams while it scans, so this is how far the sheet is
    /// read), else the bytes against an announced length, else unknown.
    /// </summary>
    private static double Share(EsclTransfer t) =>
        t.Height > 0 ? Math.Min(1, t.Rows / (double)t.Height) : t.Length is > 0 and var l ? Math.Min(1, t.Received / (double)l) : -1;

    private sealed class Inline(Action<EsclTransfer> report) : IProgress<EsclTransfer>
    {
        public void Report(EsclTransfer value) => report(value);
    }
}
