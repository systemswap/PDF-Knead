using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>The diagnosis protocol of the eSCL access (KONZEPT.md §5.25, S9a): bounded in entries and length, oldest out first, thread-safe.</summary>
public sealed class EsclDiagnosticsTests
{
    [Fact]
    public void Protocol_BoundedAndOrdered()
    {
        var log = new EsclDiagnostics();
        for (var i = 0; i < EsclDiagnostics.MaxEntries + 10; i++) log.Add("Suche", $"Eintrag {i}");
        Assert.Equal(EsclDiagnostics.MaxEntries, log.Entries.Count);
        Assert.EndsWith("Eintrag 10", log.Entries[0]);
        Assert.EndsWith($"Eintrag {EsclDiagnostics.MaxEntries + 9}", log.Entries[^1]);

        log.Add("Antwort", new string('x', EsclDiagnostics.MaxText + 500));
        Assert.Contains("500 Zeichen gekürzt", log.Entries[^1]);
        Assert.StartsWith("PDF-Knead — Diagnose", log.Text());
        log.Clear();
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Protocol_ManyWritersAtOnce()
    {
        var log = new EsclDiagnostics();
        Parallel.For(0, 8, w =>
        {
            for (var i = 0; i < 1000; i++) log.Add("Test", $"{w}/{i}");
        });
        Assert.Equal(EsclDiagnostics.MaxEntries, log.Entries.Count);
    }
}
