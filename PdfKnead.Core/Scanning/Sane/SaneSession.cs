using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace PdfKnead.Core.Scanning.Sane;

/// <summary>
/// An opened SANE device: its options (SANE Standard 1.06, 4.2.9 and 4.5 „Well-Known Options“) mapped onto source, resolution, colour
/// mode and area, and scanning with frames (4.3.8–4.3.10). Every call runs on the worker; only cancelling comes from another thread.
/// </summary>
internal sealed class SaneSession : IScanSession
{
    /// <summary>The most bytes one frame may have: a page of A4 at 1200 dpi in colour has about 420 MB; anything far beyond is an error.</summary>
    internal const long MaxFrameBytes = 1_600_000_000;

    private readonly SaneLibrary _sane;
    private readonly ScanWorker _worker;
    private nint _handle;
    private Dictionary<string, Option> _options = [];

    private SaneSession(SaneLibrary sane, ScanWorker worker, ScanDevice device, nint handle)
    {
        _sane = sane;
        _worker = worker;
        Device = device;
        _handle = handle;
        Capabilities = new ScanCapabilities([], null, [], [], 0, 0);
    }

    /// <inheritdoc/>
    public ScanDevice Device { get; }

    /// <inheritdoc/>
    public ScanCapabilities Capabilities { get; private set; }

    /// <summary>Opens the device and reads its options (on the worker).</summary>
    /// <param name="sane">The library.</param>
    /// <param name="worker">The worker of the library.</param>
    /// <param name="device">The device as listed (the session keeps it).</param>
    /// <param name="id">The way to open it by; null: its best way.</param>
    internal static SaneSession Open(SaneLibrary sane, ScanWorker worker, ScanDevice device, string? id = null)
    {
        var session = new SaneSession(sane, worker, device, sane.Open(id ?? device.Id));
        try
        {
            session.ReadOptions();
            session.Capabilities = session.ReadCapabilities();
            return session;
        }
        catch
        {
            sane.Close(session._handle);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default) => _worker.Run(() =>
    {
        cancel.ThrowIfCancellationRequested();
        SetSource(source);
        Capabilities = ReadCapabilities();
        return Capabilities;
    });

    /// <inheritdoc/>
    public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var pages = Channel.CreateUnbounded<ScannedImage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var handle = _handle;
        // sane_cancel may come from any thread: a read that waits for the scanner then returns CANCELLED (4.3.11)
        await using var stop = cancel.Register(() => { if (handle != 0) _sane.Cancel(handle); });
        var job = _worker.Run(() =>
        {
            try
            {
                Scan(settings, progress, pages.Writer, cancel);
                pages.Writer.Complete();
            }
            catch (Exception e)
            {
                pages.Writer.Complete(e);
            }
        });
        await foreach (var page in pages.Reader.ReadAllAsync(CancellationToken.None)) yield return page;
        await job;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_handle == 0) return;
        var handle = _handle;
        _handle = 0;
        await _worker.Run(() => _sane.Close(handle));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>An option as its descriptor describes it.</summary>
    private sealed record Option(int Index, string Name, int Type, int Unit, int Size, int Cap, int ConstraintType,
        IReadOnlyList<string> Strings, IReadOnlyList<int> Words, int Min, int Max, int Quant)
    {
        public bool Active => (Cap & SaneLibrary.CapInactive) == 0;

        public bool Settable => Active && (Cap & SaneLibrary.CapSoftSelect) != 0;

        /// <summary>A word of this option as a number (SANE_Fixed is 16.16).</summary>
        public double Number(int word) => Type == SaneLibrary.TypeFixed ? word / 65536.0 : word;
    }

    /// <summary>Reads all option descriptors (again after a change that reloads options, 4.3.7).</summary>
    private unsafe void ReadOptions()
    {
        var count = 0;
        var (status, _) = _sane.Control(_handle, 0, SaneLibrary.ActionGetValue, &count);
        SaneLibrary.Check(status);
        var options = new Dictionary<string, Option>(StringComparer.Ordinal);
        for (var i = 1; i < Math.Min(count, 1000); i++)
        {
            var d = _sane.Descriptor(_handle, i);
            if (d == null) continue;
            var name = SaneLibrary.Text(d->Name);
            if (name.Length == 0 || d->Type == SaneLibrary.TypeGroup || d->Type == SaneLibrary.TypeButton) continue;
            var strings = new List<string>();
            var words = new List<int>();
            int min = 0, max = 0, quant = 0;
            switch (d->ConstraintType)
            {
                case SaneLibrary.ConstraintStringList when d->Constraint != 0:
                    for (var p = (nint*)d->Constraint; *p != 0 && strings.Count < 256; p++) strings.Add(SaneLibrary.Text(*p));
                    break;
                case SaneLibrary.ConstraintWordList when d->Constraint != 0:
                    var list = (int*)d->Constraint;   // the first word is the length of the list
                    for (var k = 1; k <= Math.Min(list[0], 256); k++) words.Add(list[k]);
                    break;
                case SaneLibrary.ConstraintRange when d->Constraint != 0:
                    var range = (int*)d->Constraint;   // SANE_Range: min, max, quant
                    (min, max, quant) = (range[0], range[1], range[2]);
                    break;
            }
            options[name] = new Option(i, name, d->Type, d->Unit, d->Size, d->Cap, d->ConstraintType, strings, words, min, max, quant);
        }
        _options = options;
    }

    /// <summary>What the device offers with the current settings.</summary>
    private ScanCapabilities ReadCapabilities()
    {
        var sources = new List<ScanSource>();
        ScanSource? source = null;
        if (_options.TryGetValue("source", out var s) && s.Active && s.Type == SaneLibrary.TypeString)
        {
            sources.AddRange(s.Strings.Select(ScanSource.FromName));
            var current = GetString(s);
            source = sources.FirstOrDefault(x => x.Name == current) ?? (current.Length > 0 ? ScanSource.FromName(current) : null);
            // back ends that offer both sides not as a source but as an option of the feeder (adf-mode, adf_mode, duplex): a source of its own
            if (!sources.Any(x => x.Kind == ScanSourceKind.FeederDuplex) && sources.FirstOrDefault(x => x.Kind == ScanSourceKind.Feeder) is { } feeder
                && DuplexOption() is { } duplex)
            {
                var both = new ScanSource(feeder.Name + ScanSource.DuplexMarker, ScanSourceKind.FeederDuplex);
                sources.Insert(sources.IndexOf(feeder) + 1, both);
                if (source?.Name == feeder.Name && DuplexOn(duplex)) source = both;
            }
        }

        var resolutions = new List<int>();
        if (ResolutionOption() is { } r)
        {
            if (r.ConstraintType == SaneLibrary.ConstraintWordList)
                resolutions.AddRange(ResolutionChoices.FromList(r.Words.Select(w => (int)Math.Round(r.Number(w)))));
            else if (r.ConstraintType == SaneLibrary.ConstraintRange)
                resolutions.AddRange(ResolutionChoices.FromRange((int)Math.Round(r.Number(r.Min)), (int)Math.Round(r.Number(r.Max)),
                    (int)Math.Round(r.Number(r.Quant))));
        }

        var modes = new List<ScanColorMode>();
        if (_options.TryGetValue("mode", out var m) && m.Active)
            foreach (var mode in m.Strings.Select(ModeOf).OfType<ScanColorMode>())
                if (!modes.Contains(mode)) modes.Add(mode);
        modes.Sort();

        // feeders often take longer paper than the area: the larger of the area and the page size counts
        return new ScanCapabilities(sources, source, resolutions, modes, Math.Max(AreaMax("br-x"), AreaMax("page-width")),
            Math.Max(AreaMax("br-y"), AreaMax("page-height")));
    }

    private Option? ResolutionOption() =>
        _options.TryGetValue("resolution", out var r) && r.Active ? r
        : _options.TryGetValue("x-resolution", out var x) && x.Active ? x
        : null;

    /// <summary>The largest value of an area option in millimetres; 0 if unknown.</summary>
    private double AreaMax(string name) =>
        _options.TryGetValue(name, out var o) && o.Unit == SaneLibrary.UnitMm && o.ConstraintType == SaneLibrary.ConstraintRange ? o.Number(o.Max) : 0;

    /// <summary>
    /// The colour mode a back end's mode name stands for, null for others. SANE 4.5.2 names Color, Gray and Lineart; back ends use more and
    /// some translate the values, so German, French, Italian and Spanish words count too. Halftone, dithering and error diffusion are left
    /// out: they imitate grey with dots, and black and white means clean lines.
    /// </summary>
    internal static ScanColorMode? ModeOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("halftone") || n.Contains("dither") || n.Contains("diffusion") || n.Contains("raster")) return null;
        if (n.Contains("color") || n.Contains("colour") || n.Contains("24bit") || n.Contains("rgb") || n.Contains("farb") || n.Contains("couleur")
            || n.Contains("colore") || n.Contains("color")) return ScanColorMode.Color;
        if (n.Contains("gray") || n.Contains("grey") || n.Contains("grau") || n.Contains("gris") || n.Contains("grigi")) return ScanColorMode.Gray;
        if (n.Contains("lineart") || n.Contains("binary") || n.Contains("binär") || n.Contains("black") || n.Contains("mono") || n.Contains("schwarz")
            || n.Contains("strich") || n.Contains("noir") || n.Contains("bianco") || n.Contains("blanco")) return ScanColorMode.BlackWhite;
        return null;
    }

    /// <summary>An option that switches the feeder to both sides (adf-mode/adf_mode with a „Duplex“ value, or duplex as a switch or text), or null.</summary>
    private Option? DuplexOption()
    {
        foreach (var name in (string[])["adf-mode", "adf_mode"])
            if (_options.TryGetValue(name, out var o) && o.Settable && o.Type == SaneLibrary.TypeString && o.Strings.Any(v => v.Contains("duplex", StringComparison.OrdinalIgnoreCase)))
                return o;
        return _options.TryGetValue("duplex", out var d) && d.Settable && (d.Type == SaneLibrary.TypeBool
            || d.Type == SaneLibrary.TypeString && d.Strings.Any(v => v.Equals("both", StringComparison.OrdinalIgnoreCase) || v.Contains("duplex", StringComparison.OrdinalIgnoreCase)))
            ? d : null;
    }

    private bool DuplexOn(Option option) => option.Type == SaneLibrary.TypeBool ? GetWord(option) != 0
        : GetString(option) is var v && (v.Contains("duplex", StringComparison.OrdinalIgnoreCase) || v.Equals("both", StringComparison.OrdinalIgnoreCase));

    /// <summary>Switches both sides on or off; off is set explicitly when the front side alone is chosen, as back ends keep the last value.</summary>
    private void SetDuplex(Option option, bool on)
    {
        if (option.Type == SaneLibrary.TypeBool)
        {
            SetNumber(option, on ? 1 : 0);
            return;
        }
        var value = on
            ? option.Strings.FirstOrDefault(v => v.Equals("both", StringComparison.OrdinalIgnoreCase) || v.Contains("duplex", StringComparison.OrdinalIgnoreCase))
            : option.Strings.FirstOrDefault(v => v.Contains("simplex", StringComparison.OrdinalIgnoreCase) || v.Equals("front", StringComparison.OrdinalIgnoreCase)
                || v.Contains("single", StringComparison.OrdinalIgnoreCase));
        if (value is not null) SetString(option, value);
    }

    /// <summary>Sets a source by name; a source made from a duplex option sets the feeder and the option.</summary>
    private void SetSource(string name)
    {
        var duplex = name.EndsWith(ScanSource.DuplexMarker, StringComparison.Ordinal);
        var plain = duplex ? name[..^ScanSource.DuplexMarker.Length] : name;
        if (_options.TryGetValue("source", out var s) && s.Settable && s.Strings.Contains(plain)) SetString(s, plain);
        if (ScanSource.FromName(plain).Kind == ScanSourceKind.Feeder && DuplexOption() is { } option) SetDuplex(option, duplex);
    }

    private unsafe string GetString(Option option)
    {
        var buffer = new byte[Math.Clamp(option.Size, 1, 4096)];
        fixed (byte* p = buffer)
        {
            var (status, _) = _sane.Control(_handle, option.Index, SaneLibrary.ActionGetValue, p);
            if (status != SaneLibrary.StatusGood) return "";
        }
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private unsafe void SetString(Option option, string value)
    {
        var buffer = new byte[Math.Clamp(Math.Max(option.Size, Encoding.UTF8.GetByteCount(value) + 1), 1, 4096)];
        Encoding.UTF8.GetBytes(value, buffer);
        int info;
        fixed (byte* p = buffer)
        {
            var (status, i) = _sane.Control(_handle, option.Index, SaneLibrary.ActionSetValue, p);
            SaneLibrary.Check(status);
            info = i;
        }
        _ = info;
        // read again after every change, not only when the back end sets SANE_INFO_RELOAD_OPTIONS (4.3.7): reading the descriptors is cheap,
        // and the lists then never depend on a back end setting the flag correctly
        ReadOptions();
    }

    private unsafe void SetNumber(Option option, double value)
    {
        var word = option.Type == SaneLibrary.TypeFixed ? (int)Math.Round(value * 65536.0) : (int)Math.Round(value);
        if (option.ConstraintType == SaneLibrary.ConstraintRange) word = Math.Clamp(word, option.Min, option.Max);
        var (status, _) = _sane.Control(_handle, option.Index, SaneLibrary.ActionSetValue, &word);
        SaneLibrary.Check(status);
        ReadOptions();   // as in SetString
    }

    /// <summary>Further string options set after the settings (tests: error codes of the virtual test device).</summary>
    internal Dictionary<string, string>? TestOptions { get; set; }

    /// <summary>Applies the settings in the order the options depend on each other: source, mode, resolution, then the area.</summary>
    private void Apply(ScanSettings settings)
    {
        if (settings.Source is { } source) SetSource(source);
        if (_options.TryGetValue("mode", out var m) && m.Settable)
        {
            var name = m.Strings.FirstOrDefault(x => ModeOf(x) == settings.Mode && (x is "Color" or "Gray" or "Lineart"))
                ?? m.Strings.FirstOrDefault(x => ModeOf(x) == settings.Mode);
            if (name is not null) SetString(m, name);
        }
        var caps = ReadCapabilities();
        if (ResolutionOption() is { Settable: true } r)
        {
            SetNumber(r, caps.ClosestResolution(settings.Dpi));
            if (r.Name == "x-resolution" && _options.TryGetValue("y-resolution", out var y) && y.Settable) SetNumber(y, caps.ClosestResolution(settings.Dpi));
        }
        if (_options.TryGetValue("tl-x", out var tlx) && tlx.Settable) SetNumber(tlx, tlx.Number(tlx.Min));
        if (_options.TryGetValue("tl-y", out var tly) && tly.Settable) SetNumber(tly, tly.Number(tly.Min));
        if (_options.TryGetValue("br-x", out var brx) && brx.Settable && brx.Unit == SaneLibrary.UnitMm)
            SetNumber(brx, settings.WidthMm is { } w ? Math.Min(w, brx.Number(brx.Max)) : brx.Number(brx.Max));
        if (_options.TryGetValue("br-y", out var bry) && bry.Settable && bry.Unit == SaneLibrary.UnitMm)
            SetNumber(bry, settings.HeightMm is { } h ? Math.Min(h, bry.Number(bry.Max)) : bry.Number(bry.Max));
        // feeder back ends measure the paper by the page size, not by the area (otherwise they cut or report the wrong length)
        if (_options.TryGetValue("page-width", out var pw) && pw.Settable && pw.Unit == SaneLibrary.UnitMm)
            SetNumber(pw, settings.WidthMm is { } w2 ? Math.Min(w2, pw.Number(pw.Max)) : pw.Number(pw.Max));
        if (_options.TryGetValue("page-height", out var ph) && ph.Settable && ph.Unit == SaneLibrary.UnitMm)
            SetNumber(ph, settings.HeightMm is { } h2 ? Math.Min(h2, ph.Number(ph.Max)) : ph.Number(ph.Max));
        if (TestOptions is { } extra)
            foreach (var (name, value) in extra)
                if (_options.TryGetValue(name, out var o) && o.Settable && o.Type == SaneLibrary.TypeString) SetString(o, value);
        Capabilities = ReadCapabilities();
    }

    /// <summary>The scan job on the worker: one page from the glass, pages from the feeder until it reports no more paper.</summary>
    private void Scan(ScanSettings settings, IProgress<ScanProgress>? progress, ChannelWriter<ScannedImage> pages, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Apply(settings);
        var feeder = Capabilities.Source?.Kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex;
        var dpi = ResolutionOption() is { } r ? (int)Math.Round(r.Number(GetWord(r))) : settings.Dpi;
        try
        {
            for (var number = 1; ; number++)
            {
                cancel.ThrowIfCancellationRequested();
                var status = _sane.Start(_handle);
                if (status == SaneLibrary.StatusNoDocs && number > 1) break;   // the feeder is empty: the job is done
                SaneLibrary.Check(status);
                // a page without data, or „no documents“ while reading, also means the feeder is empty
                if (ReadPage(number, dpi, progress, cancel) is not { } page)
                {
                    if (number > 1) break;
                    throw feeder ? SaneLibrary.Error(SaneLibrary.StatusNoDocs) : new ScanException(Tr.T("Der Scanner hat keine Bilddaten geliefert."));
                }
                pages.TryWrite(page);
                if (!feeder) break;
            }
        }
        finally
        {
            // always after a job, also after the last frame (4.3.11)
            _sane.Cancel(_handle);
        }
    }

    private unsafe int GetWord(Option option)
    {
        var word = 0;
        var (status, _) = _sane.Control(_handle, option.Index, SaneLibrary.ActionGetValue, &word);
        return status == SaneLibrary.StatusGood ? word : 0;
    }

    /// <summary>Reads the frames of one page (one RGB or grey frame, or three single colour frames) and turns them into 8-bit samples.</summary>
    /// <returns>The page, or null if the first frame brought no data (the feeder is empty).</returns>
    private ScannedImage? ReadPage(int number, int dpi, IProgress<ScanProgress>? progress, CancellationToken cancel)
    {
        byte[]? red = null, green = null, blue = null;
        for (var frames = 0; frames < 3; frames++)
        {
            var p = _sane.GetParameters(_handle);
            var (data, lines) = ReadFrame(p, number, progress, cancel);
            if (lines == 0)
            {
                if (frames == 0) return null;
                throw new ScanException(Tr.T("Der Scanner liefert ein unvollständiges Farbbild."));
            }
            var frame = new Frame(p.Format, p.BytesPerLine, p.PixelsPerLine, lines, p.Depth, data);
            switch (p.Format)
            {
                case SaneLibrary.FrameGray:
                case SaneLibrary.FrameRgb:
                    return Convert(frame, dpi);
                case SaneLibrary.FrameRed: red = Plane(frame); break;
                case SaneLibrary.FrameGreen: green = Plane(frame); break;
                case SaneLibrary.FrameBlue: blue = Plane(frame); break;
                default: throw new ScanException(Tr.T("Der Scanner liefert ein unbekanntes Bildformat."));
            }
            if (p.LastFrame != 0) break;
            SaneLibrary.Check(_sane.Start(_handle));
            if (red is not null && green is not null && blue is not null) break;
        }
        if (red is null || green is null || blue is null || red.Length != green.Length || red.Length != blue.Length)
            throw new ScanException(Tr.T("Der Scanner liefert ein unvollständiges Farbbild."));
        var last = _sane.GetParameters(_handle);
        var width = last.PixelsPerLine;
        var height = red.Length / Math.Max(1, width);
        var rgb = new byte[(long)width * height * 3];
        for (var i = 0; i < width * height; i++) (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (red[i], green[i], blue[i]);
        return new ScannedImage(width, height, 3, dpi, rgb);
    }

    private sealed record Frame(int Format, int BytesPerLine, int Width, int Lines, int Depth, byte[] Data);

    /// <summary>Reads one frame until EOF; the number of lines may be unknown in advance (−1, e.g. a feeder that measures the paper).</summary>
    private (byte[] Data, int Lines) ReadFrame(SaneLibrary.Parameters p, int number, IProgress<ScanProgress>? progress, CancellationToken cancel)
    {
        if (!Plausible(p)) throw new ScanException(Tr.T("Der Scanner liefert ein unbekanntes Bildformat."));
        var expected = p.Lines > 0 ? (long)p.BytesPerLine * p.Lines : -1;
        if (expected > MaxFrameBytes) throw new ScanException(Tr.T("Die Seite wäre zu groß — bitte eine kleinere Auflösung oder Fläche wählen."));
        var data = new byte[expected > 0 ? expected : Math.Min((long)p.BytesPerLine * 4096, MaxFrameBytes)];
        long length = 0;
        var chunk = new byte[256 * 1024];
        while (true)
        {
            var (status, read) = _sane.Read(_handle, chunk);
            if (status == SaneLibrary.StatusEof) break;
            if (status == SaneLibrary.StatusNoDocs) break;   // some back ends report the empty feeder only while reading
            if (status == SaneLibrary.StatusCancelled || cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
            SaneLibrary.Check(status);
            if (read <= 0) continue;
            if (length + read > data.Length)
            {
                if (expected > 0) read = (int)Math.Max(0, data.Length - length);   // more than announced: the rest is not part of the image
                else
                {
                    var grown = Math.Min(Math.Max((long)data.Length * 2, length + read), MaxFrameBytes);
                    if (grown < length + read) throw new ScanException(Tr.T("Die Seite wäre zu groß — bitte eine kleinere Auflösung oder Fläche wählen."));
                    Array.Resize(ref data, (int)grown);
                }
            }
            chunk.AsSpan(0, read).CopyTo(data.AsSpan((int)length));
            length += read;
            progress?.Report(new ScanProgress(number, expected > 0 ? (double)length / expected : -1));
        }
        // the lines always from what arrived: feeders that measure the paper deliver fewer than announced; a frame without data is 0
        var lines = (int)(length / p.BytesPerLine);
        return (data, lines);
    }

    /// <summary>
    /// Whether the parameters of a frame make sense: a known depth and format, a width within reason, and rows long enough for their pixels —
    /// a driver announcing fewer bytes per line than its pixels need would otherwise make the conversion read past each row.
    /// </summary>
    internal static bool Plausible(SaneLibrary.Parameters p)
    {
        if (p.PixelsPerLine is <= 0 or > 1_000_000 || p.BytesPerLine <= 0 || p.Depth is not (1 or 8 or 16)) return false;
        if (p.Format is < SaneLibrary.FrameGray or > SaneLibrary.FrameBlue) return false;
        var channels = p.Format == SaneLibrary.FrameRgb ? 3 : 1;
        var needed = ((long)p.PixelsPerLine * channels * p.Depth + 7) / 8;
        return p.BytesPerLine >= needed && p.BytesPerLine <= needed + 4096;
    }

    /// <summary>One sample per pixel in 8 bits from a single-colour frame.</summary>
    private static byte[] Plane(Frame f)
    {
        var plane = new byte[(long)f.Width * f.Lines];
        for (var y = 0; y < f.Lines; y++)
            for (var x = 0; x < f.Width; x++)
                plane[(long)y * f.Width + x] = Sample(f, y, x, 1, 0);
        return plane;
    }

    /// <summary>A grey or RGB frame as 8-bit samples (16 bits in host byte order, 4.2.10; 1 bit: set bit is black).</summary>
    private static ScannedImage Convert(Frame f, int dpi)
    {
        var channels = f.Format == SaneLibrary.FrameRgb ? 3 : 1;
        var rowBytes = f.Width * channels;
        if (f.Depth == 8 && f.BytesPerLine == rowBytes)
        {
            // already packed 8-bit rows: keep the read buffer, at most trim it
            var pixels = f.Data;
            if (pixels.Length != (long)rowBytes * f.Lines) Array.Resize(ref pixels, rowBytes * f.Lines);
            return new ScannedImage(f.Width, f.Lines, channels, dpi, pixels);
        }
        var result = new byte[(long)rowBytes * f.Lines];
        for (var y = 0; y < f.Lines; y++)
            for (var x = 0; x < f.Width; x++)
                for (var c = 0; c < channels; c++)
                    result[(long)y * rowBytes + x * channels + c] = Sample(f, y, x, channels, c);
        return new ScannedImage(f.Width, f.Lines, channels, dpi, result, BlackWhite: f.Depth == 1 && channels == 1);
    }

    private static byte Sample(Frame f, int y, int x, int channels, int c)
    {
        var row = (long)y * f.BytesPerLine;
        switch (f.Depth)
        {
            case 8:
                return f.Data[row + x * channels + c];
            case 16:
                var at = row + (x * channels + c) * 2;
                return BitConverter.IsLittleEndian ? f.Data[at + 1] : f.Data[at];
            default:
                var bit = x * channels + c;
                var set = (f.Data[row + (bit >> 3)] & (0x80 >> (bit & 7))) != 0;
                return set ? (byte)0 : (byte)255;
        }
    }
}
