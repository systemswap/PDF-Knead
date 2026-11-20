using System.Diagnostics.Tracing;

namespace PdfKnead.Cli;

/// <summary>
/// Runtime samples of what memory is allocated for ("leistung --allokationen"): the runtime reports the type of the object being
/// allocated roughly every 100 KB of allocations. Summed by phase (open, build, render — assigned via the timestamp, as the run is
/// sequential) and by type, this shows where allocations come from without a profiler.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private readonly List<(DateTime Time, string Type, long Bytes)> _samples = [];
    private readonly List<(DateTime Time, string Phase)> _phases = [];
    private bool _enabled;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);   // GC
    }

    public void Start() => _enabled = true;

    /// <summary>Assigns all allocations from now on to the phase <paramref name="name"/>.</summary>
    public void Phase(string name)
    {
        lock (_phases) _phases.Add((DateTime.UtcNow, name));
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_enabled || e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
            return;
        var names = e.PayloadNames!;
        var type = names.IndexOf("TypeName") is >= 0 and var t ? e.Payload[t] as string ?? "?" : "?";
        var amount = names.IndexOf("AllocationAmount64") is >= 0 and var a ? Convert.ToInt64(e.Payload[a])
            : names.IndexOf("AllocationAmount") is >= 0 and var b ? Convert.ToInt64(e.Payload[b]) : 0;
        lock (_samples) _samples.Add((e.TimeStamp.ToUniversalTime(), type, amount));
    }

    public void Print(int count = 15)
    {
        List<(DateTime Time, string Phase)> phases;
        lock (_phases) phases = [.. _phases];
        var times = phases.Select(p => p.Time).ToArray();
        var byPhase = new Dictionary<string, Dictionary<string, (long Bytes, int Samples)>>();
        lock (_samples)
        {
            foreach (var (time, type, bytes) in _samples)
            {
                var i = Array.BinarySearch(times, time);
                if (i < 0) i = ~i - 1;
                var phase = i >= 0 ? phases[i].Phase : "sonst";
                if (!byPhase.TryGetValue(phase, out var types)) byPhase[phase] = types = [];
                var (sum, samples) = types.GetValueOrDefault(type);
                types[type] = (sum + bytes, samples + 1);
            }
        }
        foreach (var (phase, types) in byPhase.OrderByDescending(p => p.Value.Values.Sum(v => v.Bytes)))
        {
            var total = types.Values.Sum(v => v.Bytes);
            Console.WriteLine($"Belegt beim {phase} (Stichproben je ~100 KB, zusammen {total >> 20} MB):");
            foreach (var (type, (bytes, samples)) in types.OrderByDescending(k => k.Value.Bytes).Take(count))
                Console.WriteLine($"  {bytes >> 20,7} MB {100.0 * bytes / Math.Max(1, total),5:0.0} %  {samples,7:N0}×  {type}");
        }
    }
}
